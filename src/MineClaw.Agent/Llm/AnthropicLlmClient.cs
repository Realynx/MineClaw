using System.Text.Json;
using Anthropic;
using Anthropic.Models.Messages;

namespace MineClaw.Agent.Llm;

/// <summary>
/// <see cref="ILlmClient"/> over the official Anthropic C# SDK (Messages API; non-streaming except interruptible calls — responses are
/// tiny). Applies: tier → model, small max_tokens, thinking off/minimal per model, effort, and up to three cache
/// breakpoints: after the static system prefix (tools render before system, so they're covered too), after the
/// identity/self block, and a moving 5m breakpoint on the last message so the next call reads all history from cache.
/// </summary>
public sealed class AnthropicLlmClient : ILlmClient
{
    private readonly IAnthropicClient _client;
    private readonly LlmOptions _options;

    public AnthropicLlmClient(LlmOptions options)
    {
        _options = options;
        var baseClient = string.IsNullOrEmpty(options.ApiKey) ? new AnthropicClient() : new AnthropicClient { ApiKey = options.ApiKey };
        _client = baseClient.WithOptions(o => o with
        {
            MaxRetries = options.MaxRetries,
            Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds),
        });
    }

    public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct)
    {
        string model = request.Model ?? _options.ModelFor(request.Tier);
        var parameters = BuildParams(request, model);
        if ((request.Interruptible || request.OnToolUse != null || request.OnTextDelta != null) && _options.StreamInterruptible)
            return await StreamAsync(parameters, model, ct, request.OnToolUse, request.OnTextDelta).ConfigureAwait(false);
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
    private async Task<LlmResponse> StreamAsync(MessageCreateParams parameters, string model, CancellationToken ct, Action<LlmToolUse>? onToolUse = null,
        Action<string>? onText = null)
    {
        LlmUsage? partial = null;
        long deltaChars = 0, reportedOut = 0;
        // Streaming execution: tool_use blocks by index, handed to the caller the moment each block stops.
        var open = new Dictionary<long, (string Id, string Name, System.Text.StringBuilder Json)>();
        // Streamed chat: open text blocks by index (their end is a line boundary for the caller).
        var openText = new HashSet<long>();

        async IAsyncEnumerable<RawMessageStreamEvent> Watch()
        {
            await foreach (RawMessageStreamEvent ev in _client.Messages.CreateStreaming(parameters, ct).WithCancellation(ct).ConfigureAwait(false))
            {
                if (ev.TryPickStart(out var start)) partial = MapUsage(start.Message.Usage);
                else if (ev.TryPickContentBlockStart(out var cbs))
                {
                    if (onToolUse != null && cbs.ContentBlock.TryPickToolUse(out var tu)) open[cbs.Index] = (tu.ID, tu.Name, new System.Text.StringBuilder());
                    else if (onText != null && cbs.ContentBlock.TryPickText(out _)) openText.Add(cbs.Index);
                }
                else if (ev.TryPickContentBlockDelta(out var cbd))
                {
                    if (cbd.Delta.TryPickText(out var t))
                    {
                        deltaChars += t.Text.Length;
                        if (onText != null) EmitText(onText, t.Text);
                    }
                    else if (cbd.Delta.TryPickInputJson(out var j))
                    {
                        deltaChars += j.PartialJson.Length;
                        if (open.TryGetValue(cbd.Index, out var tb)) tb.Json.Append(j.PartialJson);
                    }
                }
                else if (ev.TryPickContentBlockStop(out var cbe))
                {
                    if (onToolUse != null && open.Remove(cbe.Index, out var done)) EmitToolUse(onToolUse, done.Id, done.Name, done.Json.ToString());
                    else if (onText != null && openText.Remove(cbe.Index)) EmitText(onText, "\n");
                }
                else if (ev.TryPickDelta(out var md)) reportedOut = md.Usage.OutputTokens;
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
            var used = partial is { } p ? p with { OutputTokens = Math.Max(reportedOut, (deltaChars + 3) / 4) } : null;
            throw new LlmInterruptedException(used, model, ex);
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
        var system = new List<TextBlockParam>();
        bool firstCached = true;
        foreach (var s in request.System)
        {
            if (!s.Cache)
            {
                system.Add(new TextBlockParam { Text = s.Text });
                continue;
            }
            // Breakpoint 1 (static prefix) defaults to the configured TTL; later ones (identity/self) to 5m.
            var ttl = s.Ttl ?? (firstCached ? _options.StaticPrefixTtl : "5m");
            firstCached = false;
            system.Add(new TextBlockParam { Text = s.Text, CacheControl = CacheFor(ttl) });
        }

        var tools = new List<ToolUnion>();
        foreach (var t in request.Tools)
        {
            tools.Add(new Tool
            {
                Name = t.Name,
                Description = t.Description,
                InputSchema = ToInputSchema(t.InputSchema),
            });
        }

        var messages = new List<MessageParam>();
        for (int i = 0; i < request.Messages.Count; i++)
        {
            bool cacheLast = request.CacheHistory && i == request.Messages.Count - 1;
            messages.Add(ToParam(request.Messages[i], cacheLast));
        }

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

        if (!string.IsNullOrEmpty(effort) && ModelProfiles.SupportsEffort(model))
        {
            Effort e = effort.ToLowerInvariant() switch
            {
                "low" => Effort.Low,
                "high" => Effort.High,
                "max" => Effort.Max,
                _ => Effort.Medium,
            };
            p = p with { OutputConfig = new OutputConfig { Effort = e } };
        }
        return p;
    }

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
        {
            bool cache = cacheLast && i == m.Content.Count - 1;
            switch (m.Content[i])
            {
                case LlmText t:
                    blocks.Add(cache
                        ? new TextBlockParam { Text = t.Text, CacheControl = new CacheControlEphemeral() }
                        : new TextBlockParam { Text = t.Text });
                    break;
                case LlmToolUse u:
                    blocks.Add(new ToolUseBlockParam { ID = u.Id, Name = u.Name, Input = ToDict(u.Input) });
                    break;
                case LlmToolResult { Image: { } img } r:
                    // Text + PNG in one tool_result (vision). Only the newest turns carry it (compaction stubs old ones).
                    {
                        List<Block> content =
                        [
                            new TextBlockParam { Text = r.Content },
                            new ImageBlockParam { Source = new Base64ImageSource { Data = Convert.ToBase64String(img.Data), MediaType = MediaType.ImagePng } },
                        ];
                        blocks.Add(cache
                            ? new ToolResultBlockParam { ToolUseID = r.ToolUseId, Content = content, IsError = r.IsError, CacheControl = new CacheControlEphemeral() }
                            : new ToolResultBlockParam { ToolUseID = r.ToolUseId, Content = content, IsError = r.IsError });
                    }
                    break;
                case LlmToolResult r:
                    blocks.Add(cache
                        ? new ToolResultBlockParam { ToolUseID = r.ToolUseId, Content = r.Content, IsError = r.IsError, CacheControl = new CacheControlEphemeral() }
                        : new ToolResultBlockParam { ToolUseID = r.ToolUseId, Content = r.Content, IsError = r.IsError });
                    break;
                case LlmThinking th:
                    blocks.Add(new ThinkingBlockParam { Thinking = th.Thinking, Signature = th.Signature });
                    break;
                case LlmRedactedThinking rt:
                    blocks.Add(new RedactedThinkingBlockParam { Data = rt.Data });
                    break;
            }
        }
        return m.Role == LlmRole.User
            ? new UserMessageParam { Content = blocks }
            : new AssistantMessageParam { Content = blocks };
    }

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
