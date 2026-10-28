using System.Text.Json;

namespace MineClaw.Agent.Llm;

/// <summary>Which model tier a call runs on. Model IDs come from <see cref="LlmOptions"/>.</summary>
public enum ModelTier
{
    /// <summary>Haiku-class "memory" tier: compaction, reflection, ask_memory retrieval, budget-fallback chat.</summary>
    Cheap,
    /// <summary>Opus-class "frontal lobe": planning, decisions and all conversation with players.</summary>
    Main,
    /// <summary>Sonnet-class worker/builder: runs delegated multi-step tasks in its own short-lived tool loop.</summary>
    Worker,
}

public enum LlmRole { User, Assistant }

/// <summary>
/// Provider-neutral conversation block. Kept minimal so the agent loop and tests don't depend on SDK types;
/// <see cref="AnthropicLlmClient"/> maps these to/from the official SDK.
/// </summary>
public abstract record LlmBlock;

public sealed record LlmText(string Text) : LlmBlock;

public sealed record LlmToolUse(string Id, string Name, JsonElement Input) : LlmBlock;

public sealed record LlmToolResult(string ToolUseId, string Content, bool IsError = false) : LlmBlock
{
    /// <summary>Optional image sent with the text (the <c>see</c> tool). Only the newest turns carry it: compaction swaps it for
    /// <see cref="LlmImage.Stub"/> so old images aren't re-sent forever.</summary>
    public LlmImage? Image { get; init; }

    /// <summary>The same result with its image replaced by the one-line text stub (no-op without an image).</summary>
    public LlmToolResult WithoutImage() => Image == null ? this : this with { Image = null, Content = Content + " " + Image.Stub };
}

/// <summary>An image for the model (PNG bytes). <see cref="Tokens"/> ≈ w·h/750, the API's own estimate.</summary>
public sealed record LlmImage(byte[] Data, int Width, int Height, string Stub, string MediaType = "image/png")
{
    public int Tokens => (Width * Height + 749) / 750;
}

/// <summary>An image in a user message (the perception tier: picture + question). The brain never gets these.</summary>
public sealed record LlmImageBlock(LlmImage Image) : LlmBlock;

/// <summary>Opaque thinking block; must be passed back unchanged within the turn that produced it.</summary>
public sealed record LlmThinking(string Thinking, string Signature) : LlmBlock;

public sealed record LlmRedactedThinking(string Data) : LlmBlock;

public sealed record LlmMessage(LlmRole Role, IReadOnlyList<LlmBlock> Content)
{
    public static LlmMessage User(string text) => new(LlmRole.User, [new LlmText(text)]);
    public static LlmMessage User(params LlmBlock[] blocks) => new(LlmRole.User, blocks);
    public static LlmMessage Assistant(string text) => new(LlmRole.Assistant, [new LlmText(text)]);
    public static LlmMessage Assistant(params LlmBlock[] blocks) => new(LlmRole.Assistant, blocks);

    public IEnumerable<LlmToolUse> ToolUses => Content.OfType<LlmToolUse>();
    public IEnumerable<LlmToolResult> ToolResults => Content.OfType<LlmToolResult>();
    public string Text => string.Concat(Content.OfType<LlmText>().Select(t => t.Text));
}

public sealed record LlmToolDefinition(string Name, string Description, JsonElement InputSchema);

/// <summary>
/// A system-prompt segment. Segments with <see cref="Cache"/> set get a cache breakpoint after them; <see cref="Ttl"/>
/// is "5m" or "1h" (null = client default). Longer TTLs must come before shorter ones in a request.
/// </summary>
public sealed record LlmSystemBlock(string Text, bool Cache = false, string? Ttl = null);

public sealed class LlmRequest
{
    public ModelTier Tier { get; init; }
    /// <summary>Overrides the tier's model id (normally left null).</summary>
    public string? Model { get; init; }
    public int? MaxTokens { get; init; }
    public IReadOnlyList<LlmSystemBlock> System { get; init; } = [];
    public IReadOnlyList<LlmToolDefinition> Tools { get; init; } = [];
    public IReadOnlyList<LlmMessage> Messages { get; init; } = [];
    /// <summary>Put a (short-TTL) cache breakpoint on the last message block so the next call in the loop reuses history.</summary>
    public bool CacheHistory { get; init; } = true;
    /// <summary>Short label for logs/usage ("chat", "task", "compact").</summary>
    public string Purpose { get; init; } = "";
    /// <summary>
    /// The caller may cancel this call mid-generation (the brain's first call of a turn, when a player adds a line): the client
    /// streams it so cancelling really aborts generation, and reports what was used so far via <see cref="LlmInterruptedException"/>.
    /// </summary>
    public bool Interruptible { get; init; }
    /// <summary>Named profile this call runs on (delegate/hive profile): picks the provider and tags telemetry. Null = the tier's.</summary>
    public string? Profile { get; init; }
    /// <summary>Thinking override for this call ("off" | "adaptive" | "omit"); null = the tier's setting.</summary>
    public string? Thinking { get; init; }
    /// <summary>output_config.effort override for this call ("low" | "medium" | "high"); null = the tier's setting.</summary>
    public string? Effort { get; init; }
    /// <summary>
    /// Streaming execution: invoked (on the client's stream thread) with each tool_use block as soon as it is complete,
    /// before the message ends, so the caller can start a movement or a read-only lookup early. Providers that don't
    /// stream simply never call it. Null = don't stream for this.
    /// </summary>
    public Action<LlmToolUse>? OnToolUse { get; init; }
    /// <summary>
    /// Streamed chat: invoked (on the client's stream thread) with each text delta as it arrives; the end of a text block is
    /// reported as a single "\n" (a line boundary). Providers that don't stream never call it: the caller then speaks the
    /// final message as before. Null = don't stream for this.
    /// </summary>
    public Action<string>? OnTextDelta { get; init; }
    /// <summary>
    /// Running-cost cap of this call, USD (authoring). The client streams the call and aborts it with
    /// <see cref="LlmCostCapException"/> once <see cref="CostOf"/>(usage so far) passes it. Null = no per-call cap.
    /// </summary>
    public decimal? MaxUsd { get; init; }
    /// <summary>Prices usage for <see cref="MaxUsd"/> (the budget's price table). Null = no running-cost check.</summary>
    public Func<LlmUsage, decimal>? CostOf { get; init; }
}

/// <summary>A streamed call aborted by its <see cref="LlmRequest.MaxUsd"/>: <see cref="PartialUsage"/> must still be booked.</summary>
public sealed class LlmCostCapException(LlmUsage partialUsage, string model, decimal usd, decimal cap)
    : Exception($"model call aborted at ${usd:0.00} (cap ${cap:0.00})")
{
    public LlmUsage PartialUsage { get; } = partialUsage;
    public string Model { get; } = model;
    public decimal Usd { get; } = usd;
    public decimal Cap { get; } = cap;
}

/// <summary>
/// A call cancelled mid-generation. <see cref="PartialUsage"/> is what the API had reported (input/cache from message_start,
/// output so far) or a best estimate; null when nothing was known.
/// </summary>
public sealed class LlmInterruptedException(LlmUsage? partialUsage, string model, Exception? inner = null)
    : OperationCanceledException("model call interrupted", inner)
{
    public LlmUsage? PartialUsage { get; } = partialUsage;
    public string Model { get; } = model;
}

public sealed record LlmUsage(long InputTokens, long OutputTokens, long CacheReadTokens, long CacheWriteTokens, long CacheWrite1hTokens = 0)
{
    public static LlmUsage Zero { get; } = new(0, 0, 0, 0);
    public long CacheWrite5mTokens => Math.Max(0, CacheWriteTokens - CacheWrite1hTokens);
    public LlmUsage Add(LlmUsage o) => new(InputTokens + o.InputTokens, OutputTokens + o.OutputTokens,
        CacheReadTokens + o.CacheReadTokens, CacheWriteTokens + o.CacheWriteTokens, CacheWrite1hTokens + o.CacheWrite1hTokens);
    public override string ToString() => $"in {InputTokens} out {OutputTokens} cr {CacheReadTokens} cw {CacheWriteTokens}";
}

public static class StopReasons
{
    public const string EndTurn = "end_turn";
    public const string ToolUse = "tool_use";
    public const string MaxTokens = "max_tokens";
    public const string Refusal = "refusal";
    public const string PauseTurn = "pause_turn";
}

public sealed record LlmResponse(IReadOnlyList<LlmBlock> Content, string StopReason, LlmUsage Usage, string Model)
{
    public IEnumerable<LlmToolUse> ToolUses => Content.OfType<LlmToolUse>();
    public string Text => string.Concat(Content.OfType<LlmText>().Select(t => t.Text));
    /// <summary>"anthropic" or "ollama": who answered.</summary>
    public string Provider { get; init; } = LlmProviders.Anthropic;
    /// <summary>A non-Claude model's reasoning text (Ollama's <c>message.thinking</c>, e.g. DeepSeek): for the Explorer's context view
    /// only. Never sent back to a model, never part of <see cref="Content"/> (Claude thinking stays as <see cref="LlmThinking"/> blocks).</summary>
    public string? Reasoning { get; init; }
}

/// <summary>The only thing the agent needs from a model provider. Tests use a scripted fake.</summary>
public interface ILlmClient
{
    Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct);
}
