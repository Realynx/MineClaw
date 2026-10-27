using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MineClaw.Agent.Llm;

/// <summary>Marker for a tool call whose arguments weren't valid JSON even after one repair attempt.</summary>
public static class LlmToolArgs
{
    /// <summary>The tool use's input is <c>{"__invalid_arguments": "&lt;raw&gt;"}</c>; the tool registry answers it with an error
    /// result (never runs the tool), so the tool_use/tool_result pairing stays intact and the model can retry.</summary>
    public const string InvalidKey = "__invalid_arguments";

    public static bool IsInvalid(JsonElement input, out string raw)
    {
        raw = "";
        if (input.ValueKind != JsonValueKind.Object || !input.TryGetProperty(InvalidKey, out var v)) return false;
        raw = v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.GetRawText();
        return true;
    }
}

/// <summary>
/// <see cref="ILlmClient"/> over a local Ollama server (plain HttpClient, <c>POST {Endpoint}/api/chat</c>). The server proxies
/// cloud models itself, so this one endpoint serves both. Mapping:
/// <list type="bullet">
/// <item>system blocks → one leading system message, in the same order (an identical prefix lets Ollama reuse its KV cache while
/// the model stays loaded — there is no explicit prompt caching, so cache usage is always 0);</item>
/// <item>tool_use → assistant <c>tool_calls</c>; tool_result → <c>role:"tool"</c> messages (with <c>tool_name</c>) in call order,
/// before any user text of the same turn; returned tool calls get generated ids;</item>
/// <item>images (the <c>see</c> tool) → base64 <c>images</c> on a user message, only for vision models (else the text stub);</item>
/// <item>thinking blocks are never sent or returned (they're provider-specific); <c>think</c> is sent only when /api/show says
/// the model can think;</item>
/// <item>usage = prompt_eval_count / eval_count (prompt_eval_count counts only the tokens Ollama had to evaluate).</item>
/// </list>
/// Interruptible calls stream (NDJSON) so a chat-debounce restart really stops generation; cancelling throws
/// <see cref="LlmInterruptedException"/> like the Anthropic client.
/// </summary>
public sealed class OllamaLlmClient : ILlmClient, IDisposable
{
    private readonly LlmOptions _options;
    private readonly OllamaOptions _o;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    public OllamaLlmClient(LlmOptions options, HttpClient? http = null)
    {
        _options = options;
        _o = options.Ollama;
        _ownsHttp = http == null;
        _http = http ?? CreateHttp(_o);
        Catalog = new OllamaCatalog(_o, _http);
    }

    /// <summary>Long-lived pooled client; the per-call timeout is applied by the caller (streams can outlive HttpClient.Timeout).</summary>
    public static HttpClient CreateHttp(OllamaOptions o) => new(new SocketsHttpHandler
    {
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(10),
        ConnectTimeout = TimeSpan.FromSeconds(10),
    }) { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>/api/tags and /api/show (capabilities, cached per model).</summary>
    public OllamaCatalog Catalog { get; }

    public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct)
    {
        string model = request.Model ?? _options.ModelFor(request.Tier);
        var info = await Catalog.ShowAsync(model, ct).ConfigureAwait(false);
        bool stream = request.Interruptible && _options.StreamInterruptible || request.OnToolUse != null;
        var body = BuildBody(request, model, info, stream);
        var json = body.ToJsonString();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(_o.EffectiveTimeoutSeconds));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        var progress = new StreamProgress { InputEstimate = json.Length / 4 };
        try
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    return await SendOnceAsync(json, model, stream, progress, request.OnToolUse, linked.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (Retryable(ex) && attempt < _o.MaxRetries && !linked.IsCancellationRequested && progress.Chars == 0)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(500 * Math.Pow(2, attempt)), linked.Token).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex) when (ct.IsCancellationRequested && ex is not LlmInterruptedException)
        {
            if (!request.Interruptible) throw new OperationCanceledException("ollama call cancelled", ex, ct);
            var used = new LlmUsage(progress.InputEstimate, (progress.Chars + 3) / 4, 0, 0);
            throw new LlmInterruptedException(used, model, ex);
        }
        catch (Exception ex) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested && ex is OperationCanceledException)
        {
            throw new TimeoutException($"ollama call to {model} timed out after {_o.EffectiveTimeoutSeconds}s", ex);
        }
    }

    /// <summary>Connection failures and 5xx (a model still loading, a cloud hiccup behind the proxy); never 4xx.</summary>
    private static bool Retryable(Exception ex) =>
        ex is HttpRequestException h && (h.StatusCode == null || (int)h.StatusCode >= 500) || ex is IOException;

    private sealed class StreamProgress
    {
        public long InputEstimate;
        public long Chars;
    }

    private async Task<LlmResponse> SendOnceAsync(string json, string model, bool stream, StreamProgress progress, Action<LlmToolUse>? onToolUse,
        CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(_o.BaseUri, "api/chat"))
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        Authorize(req, _o);
        using var resp = await _http.SendAsync(req, stream ? HttpCompletionOption.ResponseHeadersRead : HttpCompletionOption.ResponseContentRead, ct)
            .ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new OllamaException(resp.StatusCode, $"ollama HTTP {(int)resp.StatusCode} ({model}): {ErrorText(text)}");
        }
        var acc = new Accumulator { OnToolUse = stream ? onToolUse : null };
        if (!stream)
        {
            var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(text);
            acc.Add(doc.RootElement);
            return acc.ToResponse(model);
        }
        await using var s = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(s, Encoding.UTF8);
        while (true)
        {
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line == null) break;
            if (line.Length == 0) continue;
            using var doc = JsonDocument.Parse(line);
            if (doc.RootElement.TryGetProperty("error", out var err)) throw new OllamaException(HttpStatusCode.InternalServerError, "ollama stream error: " + err);
            progress.Chars += acc.Add(doc.RootElement);
            if (acc.Done) break;
        }
        return acc.ToResponse(model);
    }

    internal static void Authorize(HttpRequestMessage req, OllamaOptions o)
    {
        if (!string.IsNullOrWhiteSpace(o.ApiKey)) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", o.ApiKey.Trim());
    }

    private static string ErrorText(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String) return e.GetString()!;
        }
        catch (JsonException) { }
        return Tools.TextUtil.Truncate(Tools.TextUtil.OneLine(body), 200);
    }

    // ------------------------------------------------------------------ request mapping

    /// <summary>The /api/chat body for <paramref name="request"/> (internal for tests).</summary>
    internal JsonObject BuildBody(LlmRequest request, string model, OllamaModelInfo? info, bool stream)
    {
        bool vision = info?.SupportsVision == true || _o.VisionModels.Any(v => OllamaCatalog.SameFamily(v, model));
        var messages = new JsonArray();
        var system = string.Join("\n\n", request.System.Select(s => s.Text).Where(t => !string.IsNullOrEmpty(t)));
        if (system.Length > 0) messages.Add(new JsonObject { ["role"] = "system", ["content"] = system });

        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var m in request.Messages)
            foreach (var u in m.Content.OfType<LlmToolUse>()) names[u.Id] = u.Name;

        foreach (var m in request.Messages)
        {
            if (m.Role == LlmRole.Assistant) messages.Add(AssistantMessage(m));
            else foreach (var node in UserMessages(m, names, vision)) messages.Add(node);
        }

        var body = new JsonObject
        {
            ["model"] = model,
            ["messages"] = messages,
            ["stream"] = stream,
        };
        if (request.Tools.Count > 0)
        {
            var tools = new JsonArray();
            foreach (var t in request.Tools)
                tools.Add(new JsonObject
                {
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = t.Name,
                        ["description"] = t.Description,
                        ["parameters"] = t.InputSchema.ValueKind == JsonValueKind.Object ? JsonNode.Parse(t.InputSchema.GetRawText()) : new JsonObject { ["type"] = "object" },
                    },
                });
            body["tools"] = tools;
        }
        var options = new JsonObject { ["num_predict"] = request.MaxTokens ?? _options.MaxTokensFor(request.Tier) };
        if (_o.DefaultNumCtx > 0) options["num_ctx"] = _o.DefaultNumCtx;
        if (_o.Temperature is { } temp) options["temperature"] = temp;
        body["options"] = options;
        if (!string.IsNullOrWhiteSpace(_o.KeepAlive)) body["keep_alive"] = _o.KeepAlive.Trim();
        // think only where the model can (sending it to a non-thinking model is a 400); unknown capabilities = omit.
        if (info?.SupportsThinking == true)
        {
            var mode = (request.Thinking ?? _options.ThinkingFor(request.Tier)).ToLowerInvariant();
            if (mode == "adaptive") body["think"] = true;
            else if (mode != "omit") body["think"] = false;
        }
        return body;
    }

    private static JsonObject AssistantMessage(LlmMessage m)
    {
        var o = new JsonObject { ["role"] = "assistant", ["content"] = string.Concat(m.Content.OfType<LlmText>().Select(t => t.Text)) };
        var uses = m.Content.OfType<LlmToolUse>().ToList();
        if (uses.Count > 0)
        {
            var calls = new JsonArray();
            foreach (var u in uses)
            {
                JsonNode args = u.Input.ValueKind == JsonValueKind.Object && !LlmToolArgs.IsInvalid(u.Input, out _)
                    ? JsonNode.Parse(u.Input.GetRawText())! : new JsonObject();
                calls.Add(new JsonObject { ["function"] = new JsonObject { ["name"] = u.Name, ["arguments"] = args } });
            }
            o["tool_calls"] = calls;
        }
        return o;
    }

    private static IEnumerable<JsonObject> UserMessages(LlmMessage m, Dictionary<string, string> names, bool vision)
    {
        var images = new JsonArray();
        // Tool results first and in order: Ollama pairs role:"tool" messages with the preceding assistant tool_calls.
        foreach (var r in m.Content.OfType<LlmToolResult>())
        {
            var content = r.IsError && !r.Content.StartsWith("error", StringComparison.OrdinalIgnoreCase) ? "error: " + r.Content : r.Content;
            if (r.Image is { } img)
            {
                if (vision) images.Add(Convert.ToBase64String(img.Data));
                else content = content + " " + img.Stub;
            }
            var t = new JsonObject { ["role"] = "tool", ["content"] = content };
            if (names.TryGetValue(r.ToolUseId, out var name)) t["tool_name"] = name;
            yield return t;
        }
        var text = string.Join("\n", m.Content.OfType<LlmText>().Select(t => t.Text).Where(t => t.Length > 0));
        if (text.Length == 0 && images.Count == 0) yield break;
        var u = new JsonObject { ["role"] = "user", ["content"] = text.Length > 0 ? text : "(image from the tool result above)" };
        if (images.Count > 0) u["images"] = images;
        yield return u;
    }

    // ------------------------------------------------------------------ response parsing (one object or NDJSON chunks)

    internal sealed class Accumulator
    {
        private readonly StringBuilder _text = new();
        private readonly List<LlmToolUse> _calls = new();
        private string? _doneReason;
        private long _in, _out;

        public bool Done { get; private set; }

        /// <summary>Streaming execution: each tool call as soon as its chunk arrives (Ollama sends calls whole).</summary>
        public Action<LlmToolUse>? OnToolUse { get; init; }

        /// <summary>Adds one response object or stream chunk; returns the characters of new content (for partial usage).</summary>
        public long Add(JsonElement chunk)
        {
            long chars = 0;
            if (chunk.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.Object)
            {
                if (msg.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String && c.GetString() is { Length: > 0 } s)
                {
                    _text.Append(s);
                    chars += s.Length;
                }
                if (msg.TryGetProperty("tool_calls", out var tc) && tc.ValueKind == JsonValueKind.Array)
                    foreach (var call in tc.EnumerateArray())
                    {
                        var use = ParseCall(call, _calls.Count);
                        if (use == null) continue;
                        _calls.Add(use);
                        chars += use.Input.GetRawText().Length;
                        if (!LlmToolArgs.IsInvalid(use.Input, out _))
                            try { OnToolUse?.Invoke(use); } catch (Exception) { /* an early start never breaks the call */ }
                    }
            }
            if (chunk.TryGetProperty("done", out var d) && d.ValueKind == JsonValueKind.True)
            {
                Done = true;
                if (chunk.TryGetProperty("done_reason", out var dr) && dr.ValueKind == JsonValueKind.String) _doneReason = dr.GetString();
            }
            if (chunk.TryGetProperty("prompt_eval_count", out var pe) && pe.TryGetInt64(out var pin)) _in = pin;
            if (chunk.TryGetProperty("eval_count", out var ev) && ev.TryGetInt64(out var pout)) _out = pout;
            return chars;
        }

        public LlmResponse ToResponse(string model)
        {
            var blocks = new List<LlmBlock>();
            var text = _text.ToString();
            if (text.Trim().Length > 0) blocks.Add(new LlmText(text));
            blocks.AddRange(_calls);
            string stop = _calls.Count > 0 ? StopReasons.ToolUse : _doneReason == "length" ? StopReasons.MaxTokens : StopReasons.EndTurn;
            return new LlmResponse(blocks, stop, new LlmUsage(_in, _out, 0, 0), model) { Provider = LlmProviders.Ollama };
        }
    }

    private static LlmToolUse? ParseCall(JsonElement call, int index)
    {
        var fn = call.TryGetProperty("function", out var f) ? f : call;
        if (!fn.TryGetProperty("name", out var n) || n.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(n.GetString())) return null;
        string id = call.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.String && i.GetString() is { Length: > 0 } given
            ? given : $"call_{Guid.NewGuid():N}"[..17] + index;
        var args = fn.TryGetProperty("arguments", out var a) ? a : default;
        return new LlmToolUse(id, n.GetString()!.Trim(), ParseArguments(args));
    }

    /// <summary>
    /// Tool-call arguments as a JSON object: objects as-is, strings parsed, and one repair attempt for malformed text
    /// (code fences, single quotes, trailing commas, missing closing braces). Still broken → the <see cref="LlmToolArgs"/> marker.
    /// </summary>
    internal static JsonElement ParseArguments(JsonElement args)
    {
        switch (args.ValueKind)
        {
            case JsonValueKind.Object: return args.Clone();
            case JsonValueKind.Undefined or JsonValueKind.Null: return JsonSerializer.SerializeToElement(new { });
            case JsonValueKind.String:
                var raw = args.GetString() ?? "";
                if (raw.Trim().Length == 0) return JsonSerializer.SerializeToElement(new { });
                if (TryObject(raw, out var obj) || TryObject(Repair(raw), out obj)) return obj;
                return Invalid(raw);
            default:
                return Invalid(args.GetRawText());
        }
    }

    private static JsonElement Invalid(string raw) =>
        JsonSerializer.SerializeToElement(new Dictionary<string, string> { [LlmToolArgs.InvalidKey] = raw.Length > 300 ? raw[..300] : raw });

    private static bool TryObject(string s, out JsonElement obj)
    {
        obj = default;
        try
        {
            using var doc = JsonDocument.Parse(s, new JsonDocumentOptions { AllowTrailingCommas = true });
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;
            obj = doc.RootElement.Clone();
            return true;
        }
        catch (JsonException) { return false; }
    }

    private static readonly Regex Fence = new(@"^```[a-zA-Z]*\s*|\s*```$", RegexOptions.Compiled);
    private static readonly Regex TrailingComma = new(@",\s*([}\]])", RegexOptions.Compiled);

    /// <summary>One best-effort repair pass (internal for tests).</summary>
    internal static string Repair(string raw)
    {
        var s = Fence.Replace(raw.Trim(), "").Trim();
        int start = s.IndexOf('{');
        if (start < 0) return s;
        s = s[start..];
        int end = s.LastIndexOf('}');
        if (end >= 0 && Balance(s[..(end + 1)]) == 0) s = s[..(end + 1)];
        if (!s.Contains('"') && s.Contains('\'')) s = s.Replace('\'', '"');
        s = TrailingComma.Replace(s, "$1");
        // Close what a cut-off generation left open (strings first, then brackets in reverse order).
        var stack = new Stack<char>();
        bool inStr = false, esc = false;
        foreach (var ch in s)
        {
            if (inStr)
            {
                if (esc) esc = false;
                else if (ch == '\\') esc = true;
                else if (ch == '"') inStr = false;
                continue;
            }
            if (ch == '"') inStr = true;
            else if (ch is '{' or '[') stack.Push(ch == '{' ? '}' : ']');
            else if (ch is '}' or ']' && stack.Count > 0) stack.Pop();
        }
        var sb = new StringBuilder(s);
        if (inStr) sb.Append('"');
        while (stack.Count > 0) sb.Append(stack.Pop());
        return TrailingComma.Replace(sb.ToString(), "$1");
    }

    private static int Balance(string s)
    {
        int depth = 0;
        bool inStr = false, esc = false;
        foreach (var ch in s)
        {
            if (inStr) { if (esc) esc = false; else if (ch == '\\') esc = true; else if (ch == '"') inStr = false; continue; }
            if (ch == '"') inStr = true;
            else if (ch is '{' or '[') depth++;
            else if (ch is '}' or ']') depth--;
        }
        return depth;
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}

/// <summary>An Ollama HTTP error (status + the server's "error" text).</summary>
public sealed class OllamaException(HttpStatusCode status, string message) : HttpRequestException(message, null, status);

/// <summary>What /api/show says about a model (null capabilities = an older server that doesn't report them).</summary>
public sealed record OllamaModelInfo(string Model, IReadOnlyList<string>? Capabilities)
{
    private bool Has(string c) => Capabilities?.Contains(c, StringComparer.OrdinalIgnoreCase) == true;
    /// <summary>Unknown capabilities count as "tools" (older servers): only an explicit list without it refuses.</summary>
    public bool SupportsTools => Capabilities == null || Has("tools");
    public bool SupportsVision => Has("vision");
    public bool SupportsThinking => Has("thinking");
}

/// <summary>Model listing and capabilities of the Ollama server (<c>GET /api/tags</c>, <c>POST /api/show</c>).</summary>
public sealed class OllamaCatalog(OllamaOptions o, HttpClient http)
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Task<OllamaModelInfo?>> _show = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>"llama3.1" and "llama3.1:latest" are the same model.</summary>
    public static bool SameModel(string a, string b) => string.Equals(Norm(a), Norm(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>A config entry without a tag ("qwen2.5vl") covers every tag of that model; with a tag it must match exactly.</summary>
    public static bool SameFamily(string entry, string model) =>
        entry.Contains(':') ? SameModel(entry, model) : string.Equals(entry.Trim(), model.Split(':')[0].Trim(), StringComparison.OrdinalIgnoreCase);

    private static string Norm(string m)
    {
        m = m.Trim();
        return m.Contains(':') ? m : m + ":latest";
    }

    public async Task<IReadOnlyList<string>> TagsAsync(CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, new Uri(o.BaseUri, "api/tags"));
        OllamaLlmClient.Authorize(req, o);
        using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
        var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) throw new OllamaException(resp.StatusCode, $"ollama /api/tags HTTP {(int)resp.StatusCode}: {Tools.TextUtil.Truncate(text, 160)}");
        using var doc = JsonDocument.Parse(text);
        var list = new List<string>();
        if (doc.RootElement.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Array)
            foreach (var m in models.EnumerateArray())
            {
                if (m.TryGetProperty("name", out var n) && n.GetString() is { Length: > 0 } name) list.Add(name);
                else if (m.TryGetProperty("model", out var mm) && mm.GetString() is { Length: > 0 } model) list.Add(model);
            }
        return list;
    }

    /// <summary>Capabilities of one model, cached; null when the server can't tell (failure, unknown model).</summary>
    public Task<OllamaModelInfo?> ShowAsync(string model, CancellationToken ct) =>
        _show.GetOrAdd(model, m => FetchShowAsync(m, CancellationToken.None)).WaitAsync(ct);

    private async Task<OllamaModelInfo?> FetchShowAsync(string model, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(o.BaseUri, "api/show"))
            {
                Content = new StringContent(JsonSerializer.Serialize(new { model }), Encoding.UTF8, "application/json"),
            };
            OllamaLlmClient.Authorize(req, o);
            using var resp = await http.SendAsync(req, cts.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false));
            List<string>? caps = null;
            if (doc.RootElement.TryGetProperty("capabilities", out var c) && c.ValueKind == JsonValueKind.Array)
                caps = c.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList();
            return new OllamaModelInfo(model, caps);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException or IOException)
        {
            _show.TryRemove(model, out _); // not cached: try again next call
            return null;
        }
    }

    /// <summary>
    /// Startup check: every wanted model is on the server, and those used by tool-using tiers support tools.
    /// Transient = the server is unreachable (worth a restart), as opposed to a missing or tool-less model.
    /// </summary>
    public async Task<(bool Ok, string Message, bool Transient)> CheckAsync(IEnumerable<(string Model, string Use, bool NeedsTools)> wanted, CancellationToken ct)
    {
        IReadOnlyList<string> tags;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(20));
            tags = await TagsAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OllamaException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return (false, $"the Ollama endpoint refused us ({(int)ex.StatusCode}). Check Agent:Llm:Ollama:ApiKey.", false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            return (false, $"can't reach Ollama at {o.Endpoint}: {ex.GetType().Name}: {ex.Message}", true);
        }
        var found = new List<string>();
        foreach (var (model, use, needsTools) in wanted.DistinctBy(w => (w.Model.ToLowerInvariant(), w.Use)))
        {
            if (!tags.Any(t => SameModel(t, model)))
                return (false, $"model '{model}' ({use}) isn't on the Ollama server (have: {string.Join(", ", tags.Take(12))}). Pull it or fix the model name.", false);
            var info = await ShowAsync(model, ct).ConfigureAwait(false);
            if (needsTools && info is { SupportsTools: false })
                return (false, $"model '{model}' can't call tools (capabilities: {string.Join(", ", info.Capabilities!)}), so it can't run the {use}. Pick a tool-capable model.", false);
            found.Add(model + (info?.Capabilities is { } c ? $" [{string.Join(",", c)}]" : ""));
        }
        return (true, string.Join(", ", found.Distinct()), false);
    }
}
