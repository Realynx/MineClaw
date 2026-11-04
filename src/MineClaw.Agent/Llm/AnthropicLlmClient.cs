using System.Text.Json;
using Anthropic;
using Anthropic.Models.Messages;

namespace MineClaw.Agent.Llm;

/// <summary>
/// <see cref="ILlmClient"/> over the official Anthropic C# SDK (Messages API; non-streaming except interruptible calls — responses are
/// tiny — and large/cost-capped authoring calls: max_tokens above <see cref="LlmOptions.StreamAboveTokens"/> streams, aggregated
/// into the same message, usage and stop_reason). Applies: tier → model, small max_tokens, thinking off/minimal per model, effort, and up to three cache
/// breakpoints: after the static system prefix (tools render before system, so they're covered too), after the
/// identity/self block, and a moving 5m breakpoint on the last message so the next call reads all history from cache.
/// </summary>
public sealed class AnthropicLlmClient : ILlmClient
{
    private readonly IAnthropicClient _client;
    private readonly LlmOptions _options;

    /// <summary>Same client with a longer whole-call timeout, for large streamed calls (authoring).</summary>
    private readonly IAnthropicClient _longClient;

    public AnthropicLlmClient(LlmOptions options) : this(options, null) { }

    /// <summary><paramref name="http"/>: tests pass a fake handler's client (no network).</summary>
    internal AnthropicLlmClient(LlmOptions options, HttpClient? http)
    {
        _options = options;
        IAnthropicClient baseClient = string.IsNullOrEmpty(options.ApiKey) ? new AnthropicClient() : new AnthropicClient { ApiKey = options.ApiKey };
        if (!string.IsNullOrWhiteSpace(options.BaseUrl)) baseClient = baseClient.WithOptions(o => o with { BaseUrl = options.BaseUrl.Trim() });
        if (http != null) baseClient = baseClient.WithOptions(o => o with { HttpClient = http });
        _client = baseClient.WithOptions(o => o with
        {
            MaxRetries = options.MaxRetries,
            Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds),
        });
        _longClient = _client.WithOptions(o => o with { Timeout = TimeSpan.FromMinutes(Math.Max(1, options.LongCallTimeoutMinutes)) });
    }

    /// <summary>A plain call this large streams (no HTTP timeout on a long generation); a per-call USD cap needs the stream too.</summary>
    internal bool StreamsLarge(LlmRequest request, long maxTokens) =>
        maxTokens > _options.StreamAboveTokens || (request.MaxUsd != null && request.CostOf != null);

    public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct)
    {
        string model = request.Model ?? _options.ModelFor(request.Tier);
        var parameters = BuildParams(request, model);
        if ((request.Interruptible || request.OnToolUse != null || request.OnTextDelta != null) && _options.StreamInterruptible)
            return await StreamAsync(parameters, model, ct, request.OnToolUse, request.OnTextDelta, request).ConfigureAwait(false);
        if (StreamsLarge(request, parameters.MaxTokens))
            return await StreamAsync(parameters, model, ct, null, null, request, longCall: parameters.MaxTokens > _options.StreamAboveTokens).ConfigureAwait(false);
        try
        {
            Message response = await _client.Messages.Create(parameters, ct).ConfigureAwait(false);
            return MapResponse(response, model);
        }
        catch (Exception ex) when (ct.IsCancellationRequested && request.Interruptible && ex is not LlmInterruptedException)
        {
            throw new LlmInterruptedException(null, model, ex);
        }
    }

    /// <summary>
    /// Streamed call, aggregated into the same <see cref="Message"/> a plain call returns. Cancelling <paramref name="ct"/>
    /// drops the stream (the API stops generating) and throws <see cref="LlmInterruptedException"/> with the usage seen so far:
    /// input/cache tokens from message_start, output from the last message_delta or ~4 chars per token of the deltas.
    /// </summary>
    /// <remarks>With <see cref="LlmRequest.MaxUsd"/> + <see cref="LlmRequest.CostOf"/> the running cost (message_start input/cache +
    /// output so far, thinking deltas included) is checked on every delta; past the cap the stream is dropped (the API stops
    /// generating) and <see cref="LlmCostCapException"/> carries the usage to book.</remarks>
    private async Task<LlmResponse> StreamAsync(MessageCreateParams parameters, string model, CancellationToken ct, Action<LlmToolUse>? onToolUse = null,
        Action<string>? onText = null, LlmRequest? request = null, bool longCall = false)
    {
        var watch = new StreamWatch(model, request?.MaxUsd, request?.CostOf, onToolUse, onText);
        var client = longCall ? _longClient : _client;

        async IAsyncEnumerable<RawMessageStreamEvent> Watch()
        {
            await foreach (RawMessageStreamEvent ev in client.Messages.CreateStreaming(parameters, ct).WithCancellation(ct).ConfigureAwait(false))
            {
                watch.Observe(ev);
                yield return ev;
            }
        }

        try
        {
            Message message = await Watch().Aggregate().ConfigureAwait(false);
            return MapResponse(message, model);
        }
        // A torn-down stream can surface as an IO/HTTP error rather than OperationCanceledException.
        catch (Exception ex) when (ct.IsCancellationRequested && ex is not LlmInterruptedException)
        {
            throw new LlmInterruptedException(watch.InterruptedUsage(), model, ex);
        }
    }

    /// <summary>
    /// What a stream has shown so far: usage (message_start input/cache, the last message_delta output, ~4 chars per token of the
    /// deltas), the cost cap check, and the streamed tool_use / text blocks handed to the caller's hooks as each one ends.
    /// </summary>
    private sealed class StreamWatch(string model, decimal? cap, Func<LlmUsage, decimal>? costOf, Action<LlmToolUse>? onToolUse, Action<string>? onText)
    {
        private LlmUsage? _partial;
        private long _deltaChars, _reportedOut, _thinkingChars;
        // Streaming execution: tool_use blocks by index, handed to the caller the moment each block stops.
        private readonly Dictionary<long, (string Id, string Name, System.Text.StringBuilder Json)> _open = new();
        // Streamed chat: open text blocks by index (their end is a line boundary for the caller).
        private readonly HashSet<long> _openText = new();

        private LlmUsage SoFar() => (_partial ?? LlmUsage.Zero) with { OutputTokens = Math.Max(_reportedOut, (_deltaChars + _thinkingChars + 3) / 4) };

        /// <summary>The usage to book for an interrupted stream (null before message_start).</summary>
        public LlmUsage? InterruptedUsage() =>
            _partial is { } p ? p with { OutputTokens = Math.Max(_reportedOut, (_deltaChars + 3) / 4) } : null;

        private void CheckCap()
        {
            if (cap is not { } c || costOf == null) return;
            var used = SoFar();
            decimal usd;
            try { usd = costOf(used); } catch (Exception) { return; }
            if (usd > c) throw new LlmCostCapException(used, model, usd, c);
        }

        public void Observe(RawMessageStreamEvent ev)
        {
            if (ev.TryPickStart(out var start)) { _partial = MapUsage(start.Message.Usage); CheckCap(); }
            else if (ev.TryPickContentBlockStart(out var cbs)) OnBlockStart(cbs);
            else if (ev.TryPickContentBlockDelta(out var cbd)) OnDelta(cbd);
            else if (ev.TryPickContentBlockStop(out var cbe)) OnBlockStop(cbe);
            else if (ev.TryPickDelta(out var md)) { _reportedOut = md.Usage.OutputTokens; CheckCap(); }
        }

        private void OnBlockStart(RawContentBlockStartEvent cbs)
        {
            if (onToolUse != null && cbs.ContentBlock.TryPickToolUse(out var tu)) _open[cbs.Index] = (tu.ID, tu.Name, new System.Text.StringBuilder());
            else if (onText != null && cbs.ContentBlock.TryPickText(out _)) _openText.Add(cbs.Index);
        }

        private void OnDelta(RawContentBlockDeltaEvent cbd)
        {
            if (cbd.Delta.TryPickText(out var t))
            {
                _deltaChars += t.Text.Length;
                if (onText != null) EmitText(onText, t.Text);
            }
            else if (cbd.Delta.TryPickInputJson(out var j))
            {
                _deltaChars += j.PartialJson.Length;
                if (_open.TryGetValue(cbd.Index, out var tb)) tb.Json.Append(j.PartialJson);
            }
            else if (cbd.Delta.TryPickThinking(out var th)) _thinkingChars += th.Thinking.Length;
            CheckCap();
        }

        private void OnBlockStop(RawContentBlockStopEvent cbe)
        {
            if (onToolUse != null && _open.Remove(cbe.Index, out var done)) EmitToolUse(onToolUse, done.Id, done.Name, done.Json.ToString());
            else if (onText != null && _openText.Remove(cbe.Index)) EmitText(onText, "\n");
        }
    }

    /// <summary>A completed streamed tool_use → the caller's early-execution hook (unparseable input: skipped, the final message runs it).</summary>
    private static void EmitToolUse(Action<LlmToolUse> onToolUse, string id, string name, string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return;
            onToolUse(new LlmToolUse(id, name, doc.RootElement.Clone()));
        }
        catch (JsonException) { }
        catch (Exception) { /* the hook never breaks the stream */ }
    }

    private static void EmitText(Action<string> onText, string delta)
    {
        try { if (delta.Length > 0) onText(delta); }
        catch (Exception) { /* the hook never breaks the stream */ }
    }

    private static LlmUsage MapUsage(Usage u)
    {
        long cw1h = 0;
        try { cw1h = u.CacheCreation?.Ephemeral1hInputTokens ?? 0; } catch { /* field absent */ }
        return new LlmUsage(u.InputTokens, u.OutputTokens, u.CacheReadInputTokens ?? 0, u.CacheCreationInputTokens ?? 0, cw1h);
    }

    internal MessageCreateParams BuildParams(LlmRequest request, string model)
    {
        var system = SystemParams(request.System);
        var tools = ToolParams(request.Tools);
        var messages = new List<MessageParam>();
        for (int i = 0; i < request.Messages.Count; i++)
            messages.Add(ToParam(request.Messages[i], request.CacheHistory && i == request.Messages.Count - 1));

        var thinkingMode = request.Thinking ?? _options.ThinkingFor(request.Tier);
        string effort = request.Effort ?? _options.EffortFor(request.Tier);

        var p = new MessageCreateParams
        {
            Model = model,
            MaxTokens = request.MaxTokens ?? _options.MaxTokensFor(request.Tier),
            Messages = messages,
        };
        if (system.Count > 0) p = p with { System = system };
        if (tools.Count > 0) p = p with { Tools = tools };

        var thinking = ThinkingParam(model, thinkingMode);
        if (thinking != null) p = p with { Thinking = thinking };
        return WithEffort(p, effort, model);
    }

    /// <summary>System blocks with their cache breakpoints, longer TTLs first.</summary>
    private List<TextBlockParam> SystemParams(IReadOnlyList<LlmSystemBlock> blocks)
    {
        var system = new List<TextBlockParam>();
        bool firstCached = true, short5m = false;
        foreach (var s in blocks)
        {
            if (!s.Cache)
            {
                system.Add(new TextBlockParam { Text = s.Text });
                continue;
            }
            // An explicit block TTL wins (the brain: its prefix TTL); breakpoint 1 without one uses the legacy default, later ones 5m.
            var ttl = LlmOptions.NormalizeTtl(s.Ttl) ?? (firstCached ? DefaultPrefixTtl() : "5m");
            // The API wants longer TTLs first: once a 5m breakpoint went out, no later one may be 1h.
            if (short5m) ttl = "5m";
            short5m = ttl == "5m";
            firstCached = false;
            system.Add(new TextBlockParam { Text = s.Text, CacheControl = CacheFor(ttl) });
        }
        return system;
    }

    private static List<ToolUnion> ToolParams(IReadOnlyList<LlmToolDefinition> defs)
    {
        var tools = new List<ToolUnion>();
        foreach (var t in defs)
        {
            tools.Add(new Tool
            {
                Name = t.Name,
                Description = t.Description,
                InputSchema = ToInputSchema(t.InputSchema),
            });
        }
        return tools;
    }

    private static MessageCreateParams WithEffort(MessageCreateParams p, string effort, string model)
    {
        if (string.IsNullOrEmpty(effort) || !ModelProfiles.SupportsEffort(model)) return p;
        Effort e = effort.ToLowerInvariant() switch
        {
            "low" => Effort.Low,
            "high" => Effort.High,
            "max" => Effort.Max,
            _ => Effort.Medium,
        };
        return p with { OutputConfig = new OutputConfig { Effort = e } };
    }

    /// <summary>
    /// Breakpoint 1 without an explicit TTL (perception, the blueprint author, evals: small prompts of their own): the legacy
    /// static override when it is a plain TTL, else 5m. The brain sends its prefix TTL explicitly (<see cref="LlmOptions.PrefixCacheTtl"/>).
    /// </summary>
    private string DefaultPrefixTtl() => LlmOptions.NormalizeTtl(_options.StaticPrefixTtl) ?? "5m";

    /// <summary>"1h" = <c>ttl: "1h"</c>; anything else = the default 5m ephemeral breakpoint (history always uses this).</summary>
    private static CacheControlEphemeral CacheFor(string? ttl) =>
        ttl == "1h" ? new CacheControlEphemeral { Ttl = Ttl.Ttl1h } : new CacheControlEphemeral();

    private static ThinkingConfigParam? ThinkingParam(string model, string mode)
    {
        switch (mode.ToLowerInvariant())
        {
            case "adaptive":
                return new ThinkingConfigAdaptive();
            case "omit":
                return null;
            default: // "off"
                return ModelProfiles.OffStyle(model) switch
                {
                    ModelProfiles.ThinkingOffStyle.Disabled => new ThinkingConfigDisabled(),
                    ModelProfiles.ThinkingOffStyle.BetweenTools => new ThinkingConfigBetweenTools(),
                    _ => null,
                };
        }
    }

    private static InputSchema ToInputSchema(JsonElement schema)
    {
        var props = new Dictionary<string, JsonElement>();
        var required = new List<string>();
        if (schema.ValueKind == JsonValueKind.Object)
        {
            if (schema.TryGetProperty("properties", out var pr) && pr.ValueKind == JsonValueKind.Object)
                foreach (var prop in pr.EnumerateObject()) props[prop.Name] = prop.Value.Clone();
            if (schema.TryGetProperty("required", out var rq) && rq.ValueKind == JsonValueKind.Array)
                foreach (var r in rq.EnumerateArray()) if (r.GetString() is { } s) required.Add(s);
        }
        return required.Count > 0
            ? new InputSchema { Properties = props, Required = required }
            : new InputSchema { Properties = props };
    }

    private static MessageParam ToParam(LlmMessage m, bool cacheLast)
    {
        var blocks = new List<ContentBlockParam>();
        for (int i = 0; i < m.Content.Count; i++)
            if (ToBlockParam(m.Content[i], cacheLast && i == m.Content.Count - 1) is { } block) blocks.Add(block);
        return m.Role == LlmRole.User
            ? new UserMessageParam { Content = blocks }
            : new AssistantMessageParam { Content = blocks };
    }

    /// <summary>One block as the API takes it (<paramref name="cache"/>: the moving history breakpoint); null for a kind it doesn't send.</summary>
    private static ContentBlockParam? ToBlockParam(LlmBlock b, bool cache)
    {
        switch (b)
        {
            case LlmText t:
                return TextParam(t, cache);
            case LlmToolUse u:
                return new ToolUseBlockParam { ID = u.Id, Name = u.Name, Input = ToDict(u.Input) };
            case LlmToolResult { Image: { } img } r:
                return ImageToolResult(r, img, cache);
            case LlmToolResult r:
                return TextToolResult(r, cache);
            case LlmImageBlock ib:
                return ImageParam(ib.Image);
            case LlmThinking th:
                return new ThinkingBlockParam { Thinking = th.Thinking, Signature = th.Signature };
            case LlmRedactedThinking rt:
                return new RedactedThinkingBlockParam { Data = rt.Data };
            default:
                return null;
        }
    }

    /// <summary>Text + PNG in one tool_result (vision). Only the newest turns carry it (compaction stubs old ones).</summary>
    private static ToolResultBlockParam ImageToolResult(LlmToolResult r, LlmImage img, bool cache)
    {
        List<Block> content =
        [
            new TextBlockParam { Text = r.Content },
            new ImageBlockParam { Source = new Base64ImageSource { Data = Convert.ToBase64String(img.Data), MediaType = MediaType.ImagePng } },
        ];
        return cache
            ? new ToolResultBlockParam { ToolUseID = r.ToolUseId, Content = content, IsError = r.IsError, CacheControl = new CacheControlEphemeral() }
            : new ToolResultBlockParam { ToolUseID = r.ToolUseId, Content = content, IsError = r.IsError };
    }

    private static TextBlockParam TextParam(LlmText t, bool cache) => cache
        ? new TextBlockParam { Text = t.Text, CacheControl = new CacheControlEphemeral() }
        : new TextBlockParam { Text = t.Text };

    private static ToolResultBlockParam TextToolResult(LlmToolResult r, bool cache) => cache
        ? new ToolResultBlockParam { ToolUseID = r.ToolUseId, Content = r.Content, IsError = r.IsError, CacheControl = new CacheControlEphemeral() }
        : new ToolResultBlockParam { ToolUseID = r.ToolUseId, Content = r.Content, IsError = r.IsError };

    private static ImageBlockParam ImageParam(LlmImage image) => new()
    {
        Source = new Base64ImageSource
        {
            Data = Convert.ToBase64String(image.Data),
            MediaType = image.MediaType == "image/jpeg" ? MediaType.ImageJpeg : MediaType.ImagePng,
        },
    };

    private static Dictionary<string, JsonElement> ToDict(JsonElement input)
    {
        var d = new Dictionary<string, JsonElement>();
        if (input.ValueKind == JsonValueKind.Object)
            foreach (var p in input.EnumerateObject()) d[p.Name] = p.Value.Clone();
        return d;
    }

    private static LlmResponse MapResponse(Message response, string model)
    {
        var blocks = new List<LlmBlock>();
        foreach (ContentBlock block in response.Content)
        {
            if (block.TryPickText(out TextBlock? text)) blocks.Add(new LlmText(text.Text));
            else if (block.TryPickToolUse(out ToolUseBlock? tu))
                blocks.Add(new LlmToolUse(tu.ID, tu.Name, JsonSerializer.SerializeToElement(tu.Input)));
            else if (block.TryPickThinking(out ThinkingBlock? th)) blocks.Add(new LlmThinking(th.Thinking, th.Signature));
            else if (block.TryPickRedactedThinking(out RedactedThinkingBlock? rt)) blocks.Add(new LlmRedactedThinking(rt.Data));
        }

        var usage = MapUsage(response.Usage);

        string stop = response.StopReason?.Raw() ?? StopReasons.EndTurn;
        return new LlmResponse(blocks, stop, usage, model);
    }
}
