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

public sealed record LlmToolResult(string ToolUseId, string Content, bool IsError = false) : LlmBlock;

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
}

/// <summary>The only thing the agent needs from a model provider. Tests use a scripted fake.</summary>
public interface ILlmClient
{
    Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct);
}
