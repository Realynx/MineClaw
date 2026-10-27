using System.Text.Json;
using Anthropic;
using Anthropic.Models.Messages;

namespace MineClaw.Agent.Llm;

/// <summary>
/// <see cref="ILlmClient"/> over the official Anthropic C# SDK (Messages API, non-streaming — responses are
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
        Message response = await _client.Messages.Create(parameters, ct).ConfigureAwait(false);
        return MapResponse(response, model);
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

        var thinkingMode = _options.ThinkingFor(request.Tier);
        string effort = _options.EffortFor(request.Tier);

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

        var u = response.Usage;
        long cw1h = 0;
        try { cw1h = u.CacheCreation?.Ephemeral1hInputTokens ?? 0; } catch { /* field absent */ }
        var usage = new LlmUsage(u.InputTokens, u.OutputTokens, u.CacheReadInputTokens ?? 0, u.CacheCreationInputTokens ?? 0, cw1h);

        string stop = response.StopReason?.Raw() ?? StopReasons.EndTurn;
        return new LlmResponse(blocks, stop, usage, model);
    }
}
