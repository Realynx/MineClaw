using System.Text.Json;
using System.Text.Json.Serialization;

namespace MineClaw.Agent.Telemetry;

/// <summary>Request-section keys of <see cref="PromptUsageRecord.Sections"/> (stable: they are the JSONL schema).</summary>
public static class PromptSection
{
    /// <summary>Tool definitions (render before the system prompt; share breakpoint 1).</summary>
    public const string Tools = "tools";
    /// <summary>Static instructions (system block 1 minus the knowledge part).</summary>
    public const string System = "system";
    /// <summary>Core world knowledge inside the static block.</summary>
    public const string Knowledge = "knowledge";
    /// <summary>Identity block: system block 2's first paragraph (name, aliases, login; breakpoint 2).</summary>
    public const string Identity = "identity";
    /// <summary>
    /// Grown personality: the per-turn <c>[self]</c> block in the newest user message (personality v2), or the legacy
    /// self-profile rendered after the identity paragraph in system block 2 (v1, Growth off).
    /// </summary>
    public const string Self = "self";
    /// <summary>Committed history: journal + past episodes.</summary>
    public const string History = "history";
    /// <summary>Per-turn <c>[memory]</c> auto-retrieval block.</summary>
    public const string Memory = "memory";
    /// <summary>Per-turn <c>[since last turn]</c> digest.</summary>
    public const string Digest = "digest";
    /// <summary>State header + hints + events (for non-brain calls: the task / prompt message).</summary>
    public const string Events = "events";
    /// <summary>This turn's tool traffic: tool calls, tool results, mid-turn [new events], earlier rounds' text.</summary>
    public const string TurnTools = "turn_tools";

    public static readonly string[] All = [Tools, System, Knowledge, Identity, Self, History, Memory, Digest, Events, TurnTools];
}

/// <summary>Normalised call purposes (<see cref="PromptUsageRecord.Purpose"/>).</summary>
public static class PromptPurpose
{
    public const string ChatTurn = "chat_turn";
    public const string DelegateRound = "delegate_round";
    public const string AskMemory = "ask_memory";
    public const string Compaction = "compaction";
    public const string Reflection = "reflection";
    public const string RaptorSummary = "raptor_summary";

    /// <summary>Maps an <c>LlmRequest.Purpose</c> label to its normalised name (unknown labels pass through).</summary>
    private static readonly Dictionary<string, string> PurposeNames = new(StringComparer.Ordinal)
    {
        ["chat"] = ChatTurn, ["task"] = ChatTurn, [""] = ChatTurn, ["worker"] = DelegateRound, ["ask_memory"] = AskMemory,
        ["compact"] = Compaction, ["reflect"] = Reflection, ["raptor"] = RaptorSummary,
    };

    public static string Normalize(string? purpose)
    {
        var p = (purpose ?? "").ToLowerInvariant();
        return PurposeNames.TryGetValue(p, out var name) ? name : p;
    }
}

/// <summary>
/// One LLM call as seen by <see cref="TelemetryLlmClient"/>: actual API usage + cost, a per-section breakdown of the
/// request (estimator, calibrated so the sections sum to the real prompt tokens), context fill and timing.
/// One JSON object per line in <c>usage-YYYY-MM-DD.jsonl</c> (snake_case keys).
/// </summary>
public sealed record PromptUsageRecord
{
    public DateTimeOffset Ts { get; init; }
    /// <summary>Login name of the bot that made the call (main bot or hive clone).</summary>
    public string Bot { get; init; } = "";
    /// <summary>The body the call worked for (a worker run bound to a body, a call inside a BodyScope); null = brain-level
    /// (the brain's own turns, memory, compaction). Omitted from the JSONL line when null.</summary>
    public string? Body { get; init; }
    /// <summary>opus / worker / memory / other (tier of the model that answered).</summary>
    public string Tier { get; init; } = "";
    public string Model { get; init; } = "";
    /// <summary>anthropic / ollama (who served the call).</summary>
    public string? Provider { get; init; }
    /// <summary>Model profile the call ran on (opus / sonnet / haiku / local...; delegate or clone choice), null = none matched.</summary>
    public string? Profile { get; init; }
    /// <summary>chat_turn / delegate_round / ask_memory / compaction / reflection / raptor_summary.</summary>
    public string Purpose { get; init; } = "";
    /// <summary>0-based model call within the turn (brain) or task (worker / ask_memory).</summary>
    public int Round { get; init; }

    public long InputTokens { get; init; }
    public long OutputTokens { get; init; }
    public long CacheReadTokens { get; init; }
    public long CacheWriteTokens { get; init; }
    /// <summary>The part of <see cref="CacheWriteTokens"/> written with a 1h TTL (billed 2x base input; the rest 1.25x).</summary>
    public long CacheWrite1hTokens { get; init; }
    /// <summary>input + cache_read + cache_write: everything the request put in front of the model.</summary>
    public long PromptTokens { get; init; }
    public decimal CostUsd { get; init; }
    /// <summary>cache_read / prompt tokens.</summary>
    public double CacheHit { get; init; }

    /// <summary>Calibrated tokens per <see cref="PromptSection"/>; sums to <see cref="PromptTokens"/> (all keys present).</summary>
    public Dictionary<string, long> Sections { get; init; } = new();
    /// <summary>The raw estimator total before calibration (actual / estimate = calibration factor).</summary>
    public int EstimatedTokens { get; init; }

    /// <summary>Prompt tokens + max output vs the hard context ceiling (Agent:Context:MaxTokens).</summary>
    public int ContextLimit { get; init; }
    public double ContextPct { get; init; }
    public int MaxOutputTokens { get; init; }
    /// <summary>History compactions (incl. hard-cap folds) since the previous record.</summary>
    public int Compactions { get; init; }

    public long LatencyMs { get; init; }
    public string StopReason { get; init; } = "";
    public string[] ToolCalls { get; init; } = [];
    /// <summary>Set when the call threw (usage fields are then zero).</summary>
    public string? Error { get; init; }

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    public string ToJsonLine() => JsonSerializer.Serialize(this, Json);

    public static PromptUsageRecord? FromJsonLine(string line)
    {
        try { return JsonSerializer.Deserialize<PromptUsageRecord>(line, Json); }
        catch (JsonException) { return null; }
    }
}
