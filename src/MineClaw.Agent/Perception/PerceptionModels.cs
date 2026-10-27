using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MineClaw.Agent.Llm;
using MineClaw.Agent.Tools;

namespace MineClaw.Agent.Perception;

/// <summary>
/// Claude as the perception brain (the default): the existing <see cref="ILlmClient"/> (same SDK client, same key) with a
/// model override, an image block and the strict-JSON prompt. Thinking off, low effort, small max_tokens.
/// </summary>
public sealed class AnthropicPerceptionModel(ILlmClient llm, PerceptionOptions options) : IPerceptionModel
{
    public string Provider => PerceptionProviders.Anthropic;
    public string Model => options.Model.Trim();

    public async Task<PerceptionResult> AskAsync(PerceptionQuery q, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var req = new LlmRequest
        {
            Tier = ModelTier.Cheap, Model = Model, MaxTokens = Math.Clamp(options.MaxTokens, 64, 2000), Purpose = "perception",
            CacheHistory = false, Thinking = string.IsNullOrWhiteSpace(options.Thinking) ? "off" : options.Thinking,
            Effort = options.Effort ?? "",
            System = [new LlmSystemBlock(PerceptionPrompt.System, Cache: true)],
            Messages = [LlmMessage.User(new LlmImageBlock(new LlmImage(q.Png, q.Width, q.Height, "[sheet]")), new LlmText(PerceptionPrompt.UserText(q)))],
        };
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, options.TimeoutSeconds)));
        try
        {
            var resp = await llm.CompleteAsync(req, cts.Token).ConfigureAwait(false);
            return PerceptionPrompt.Result(resp.Text, resp.Usage, resp.Model, Provider, sw.ElapsedMilliseconds);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            return new PerceptionResult([], null, "", LlmUsage.Zero, Model, Provider, sw.ElapsedMilliseconds, ex.GetType().Name + ": " + TextUtil.OneLine(ex.Message));
        }
    }
}

/// <summary>
/// Azure OpenAI (e.g. a GPT-6 Astra deployment) or any OpenAI-compatible endpoint: chat completions with the picture as an
/// <c>image_url</c> data URI and <c>response_format: json_object</c>. Azure: <c>api-key</c> header, deployment URL with
/// api-version (or the v1 API when no deployment is set); openai: bearer key, <c>{endpoint}/v1/chat/completions</c>.
/// </summary>
public sealed class OpenAIChatPerceptionModel(PerceptionOptions options, HttpClient http, bool azure) : IPerceptionModel
{
    public string Provider => azure ? PerceptionProviders.AzureOpenAI : PerceptionProviders.OpenAI;
    public string Model => string.IsNullOrWhiteSpace(options.Model) ? options.Deployment.Trim() : options.Model.Trim();

    public Uri Url()
    {
        var b = options.Endpoint.Trim().TrimEnd('/');
        if (!azure) return new Uri(b.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? b + "/chat/completions" : b + "/v1/chat/completions");
        return string.IsNullOrWhiteSpace(options.Deployment)
            ? new Uri(b + "/openai/v1/chat/completions")
            : new Uri($"{b}/openai/deployments/{Uri.EscapeDataString(options.Deployment.Trim())}/chat/completions?api-version={Uri.EscapeDataString(options.ApiVersion.Trim())}");
    }

    public JsonObject Body(PerceptionQuery q)
    {
        var body = new JsonObject
        {
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = PerceptionPrompt.System },
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = new JsonArray
                    {
                        new JsonObject { ["type"] = "text", ["text"] = PerceptionPrompt.UserText(q) },
                        new JsonObject
                        {
                            ["type"] = "image_url",
                            ["image_url"] = new JsonObject { ["url"] = "data:image/png;base64," + Convert.ToBase64String(q.Png), ["detail"] = "high" },
                        },
                    },
                },
            },
            ["max_completion_tokens"] = Math.Clamp(options.MaxTokens, 64, 4000),
            ["response_format"] = new JsonObject { ["type"] = "json_object" },
        };
        if (!azure || string.IsNullOrWhiteSpace(options.Deployment)) body["model"] = Model;
        return body;
    }

    public async Task<PerceptionResult> AskAsync(PerceptionQuery q, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, Url()) { Content = new StringContent(Body(q).ToJsonString(), Encoding.UTF8, "application/json") };
            if (!string.IsNullOrWhiteSpace(options.ApiKey))
            {
                if (azure) req.Headers.Add("api-key", options.ApiKey.Trim());
                else req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey.Trim());
            }
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, options.TimeoutSeconds)));
            using var resp = await http.SendAsync(req, cts.Token).ConfigureAwait(false);
            var text = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return Fail($"HTTP {(int)resp.StatusCode}: {TextUtil.Truncate(TextUtil.OneLine(text), 160)}", sw);
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            string content = root.TryGetProperty("choices", out var ch) && ch.ValueKind == JsonValueKind.Array && ch.GetArrayLength() > 0
                && ch[0].TryGetProperty("message", out var m) && m.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String
                ? c.GetString() ?? "" : "";
            long inTok = 0, outTok = 0;
            if (root.TryGetProperty("usage", out var u))
            {
                if (u.TryGetProperty("prompt_tokens", out var pt) && pt.ValueKind == JsonValueKind.Number) inTok = pt.GetInt64();
                if (u.TryGetProperty("completion_tokens", out var ctk) && ctk.ValueKind == JsonValueKind.Number) outTok = ctk.GetInt64();
            }
            string model = root.TryGetProperty("model", out var mo) && mo.ValueKind == JsonValueKind.String && !azure ? mo.GetString() ?? Model : Model;
            return PerceptionPrompt.Result(content, new LlmUsage(inTok, outTok, 0, 0), model, Provider, sw.ElapsedMilliseconds);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            return Fail(ex.GetType().Name + ": " + TextUtil.OneLine(ex.Message), sw);
        }
    }

    private PerceptionResult Fail(string why, Stopwatch sw) => new([], null, "", LlmUsage.Zero, Model, Provider, sw.ElapsedMilliseconds, why);
}

/// <summary>Ollama (a local or proxied vision model): <c>/api/chat</c> with <c>images</c> and <c>format: json</c>.</summary>
public sealed class OllamaPerceptionModel(PerceptionOptions options, string endpoint, string? bearer, HttpClient http) : IPerceptionModel
{
    public string Provider => PerceptionProviders.Ollama;
    public string Model => options.Model.Trim();

    public async Task<PerceptionResult> AskAsync(PerceptionQuery q, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var body = new JsonObject
            {
                ["model"] = Model, ["stream"] = false, ["format"] = "json",
                ["messages"] = new JsonArray
                {
                    new JsonObject { ["role"] = "system", ["content"] = PerceptionPrompt.System },
                    new JsonObject { ["role"] = "user", ["content"] = PerceptionPrompt.UserText(q), ["images"] = new JsonArray { Convert.ToBase64String(q.Png) } },
                },
                ["options"] = new JsonObject { ["num_predict"] = Math.Clamp(options.MaxTokens, 64, 4000) },
            };
            using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(endpoint.Trim().TrimEnd('/') + "/api/chat"))
            { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
            if (!string.IsNullOrWhiteSpace(bearer)) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer.Trim());
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, options.TimeoutSeconds)));
            using var resp = await http.SendAsync(req, cts.Token).ConfigureAwait(false);
            var text = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return new([], null, "", LlmUsage.Zero, Model, Provider, sw.ElapsedMilliseconds, $"HTTP {(int)resp.StatusCode}: {TextUtil.Truncate(TextUtil.OneLine(text), 160)}");
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            string content = root.TryGetProperty("message", out var m) && m.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "";
            long inTok = root.TryGetProperty("prompt_eval_count", out var pe) && pe.ValueKind == JsonValueKind.Number ? pe.GetInt64() : 0;
            long outTok = root.TryGetProperty("eval_count", out var ev) && ev.ValueKind == JsonValueKind.Number ? ev.GetInt64() : 0;
            return PerceptionPrompt.Result(content, new LlmUsage(inTok, outTok, 0, 0), Model, Provider, sw.ElapsedMilliseconds);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            return new([], null, "", LlmUsage.Zero, Model, Provider, sw.ElapsedMilliseconds, ex.GetType().Name + ": " + TextUtil.OneLine(ex.Message));
        }
    }
}

/// <summary>Picks the perception model from config alone. Null model + a reason when the mode is direct or config is missing.</summary>
public static class PerceptionModelFactory
{
    public static (IPerceptionModel? Model, string? Problem) Create(LlmOptions llm, ILlmClient shared, HttpClient? http = null)
    {
        var o = llm.Perception;
        if (o.Direct) return (null, "mode direct");
        var provider = PerceptionProviders.Normalize(o.Provider);
        if (string.IsNullOrWhiteSpace(o.Model) && !(provider == PerceptionProviders.AzureOpenAI && !string.IsNullOrWhiteSpace(o.Deployment)))
            return (null, "Agent:Llm:Perception:Model is empty");
        switch (provider)
        {
            case PerceptionProviders.Anthropic:
                if (!string.IsNullOrWhiteSpace(o.ApiKey))
                    return (new AnthropicPerceptionModel(new AnthropicLlmClient(new LlmOptions { ApiKey = o.ApiKey.Trim(), TimeoutSeconds = o.TimeoutSeconds }), o), null);
                // Claude to Claude: the shared client and key (no key = nothing to call; see stays on the picture path).
                if (!llm.ProviderConfigured(LlmProviders.Anthropic)) return (null, "no Anthropic API key");
                return (new AnthropicPerceptionModel(shared, o), null);
            case PerceptionProviders.AzureOpenAI:
            case PerceptionProviders.OpenAI:
                if (string.IsNullOrWhiteSpace(o.Endpoint)) return (null, $"Agent:Llm:Perception:Endpoint is empty ({provider})");
                if (!Uri.TryCreate(o.Endpoint.Trim(), UriKind.Absolute, out _)) return (null, $"Agent:Llm:Perception:Endpoint '{o.Endpoint}' is not an absolute URL");
                if (string.IsNullOrWhiteSpace(o.ApiKey)) return (null, $"Agent:Llm:Perception:ApiKey is empty ({provider})");
                return (new OpenAIChatPerceptionModel(o, http ?? new HttpClient(), provider == PerceptionProviders.AzureOpenAI), null);
            case PerceptionProviders.Ollama:
                var ep = string.IsNullOrWhiteSpace(o.Endpoint) ? llm.Ollama.Endpoint : o.Endpoint;
                if (string.IsNullOrWhiteSpace(ep)) return (null, "Agent:Llm:Perception:Endpoint (or Agent:Llm:Ollama:Endpoint) is empty");
                return (new OllamaPerceptionModel(o, ep, string.IsNullOrWhiteSpace(o.ApiKey) ? llm.Ollama.ApiKey : o.ApiKey, http ?? new HttpClient()), null);
            default:
                return (null, $"Agent:Llm:Perception:Provider '{o.Provider}' is not a provider (anthropic|azure-openai|openai|ollama)");
        }
    }
}
