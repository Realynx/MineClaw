namespace MineClaw.Agent.Admin;

/// <summary>The memory kinds the admin tool edits (each maps to one store; see <see cref="MemoryEditSchema"/>).</summary>
public static class MemoryKinds
{
    /// <summary>notes (id = note id) via MemoryStore.</summary>
    public const string Fact = "fact";
    /// <summary>waypoints (id = name) via MemoryStore.</summary>
    public const string Place = "place";
    /// <summary>structures (id = name) via MemoryStore.</summary>
    public const string Structure = "structure";
    /// <summary>player claims (id = claim id) via BeliefStore.</summary>
    public const string Claim = "claim";
    /// <summary>self-profile entries (id = entry id) via SelfProfile (source admin).</summary>
    public const string Trait = "trait";
    /// <summary>knowledge corrections (id = correction id) via KnowledgeCorrections.</summary>
    public const string Lesson = "lesson";
    /// <summary>world observations (id = observation id or doc ref "obs:...") via ObservationStore.</summary>
    public const string Observation = "observation";

    public static IReadOnlyList<string> All { get; } = [Fact, Place, Structure, Claim, Trait, Lesson, Observation];

    /// <summary>Canonical kind name, or null when unknown (case-insensitive; plurals accepted).</summary>
    public static string? Normalize(string? kind)
    {
        if (string.IsNullOrWhiteSpace(kind)) return null;
        var k = kind.Trim().ToLowerInvariant();
        if (k.EndsWith('s') && All.Contains(k[..^1])) k = k[..^1];
        return All.Contains(k) ? k : null;
    }

    /// <summary>
    /// RAG doc kinds / ref prefixes a memory kind's docs carry (RAPTOR "rebuild a kind"). An unknown name is taken as a raw doc
    /// kind (e.g. "episode", "knowledge"). Trait has no tree docs.
    /// </summary>
    public static (IReadOnlyList<string> DocKinds, string? RefPrefix) DocKindsOf(string kind) => Normalize(kind) switch
    {
        Fact => (["note", "player"], "note:"),
        Place => (["waypoint"], "wp:"),
        Structure => (["structure"], "st:"),
        Claim => (["claim"], "claim:"),
        Trait => ([], "self:"),
        Lesson => (["knowledge"], "corr:"),
        Observation => (["world"], "obs:"),
        _ => ([kind.Trim()], null),
    };
}

/// <summary>One field of the generic edit form.</summary>
/// <param name="Type">text | multiline | int | number | enum | bool</param>
/// <param name="Required">Required when adding (an update leaves absent fields as they are).</param>
public sealed record EditField(string Name, string Label, string Type, bool Required = false, IReadOnlyList<string>? Options = null, string? Help = null);

/// <summary>
/// An edit of one memory record. <paramref name="Fields"/>: a field absent or null keeps its value; "" clears an optional
/// field. Ops: add | update | delete (delete needs only <paramref name="Id"/>).
/// </summary>
public sealed record MemoryEdit(string Op, string Kind, string? Id, IReadOnlyDictionary<string, string?> Fields, string Who, string? Reason = null);

/// <param name="Before">Compact JSON of the record before the edit (null for add).</param>
/// <param name="After">Compact JSON after (null for delete or failure).</param>
/// <param name="Reembedded">True when changed text got a new vector in this call.</param>
public sealed record MemoryEditResult(bool Ok, string Message, string? Id = null, string? Before = null, string? After = null, bool Reembedded = false);

/// <param name="Target">Where the edit ran: "local:{db path}" or "live:{bot}".</param>
/// <param name="Op">add | update | delete | rebuild | backup</param>
public sealed record AuditEntry(DateTimeOffset At, string Who, string Target, string Op, string Kind, string? Id, string? Before, string? After,
    string? Reason, bool Ok, string Message);

/// <param name="Scope">node (rebuild one summary node's subtree) | kind (re-insert one memory/doc kind's leaves and refresh)</param>
/// <param name="MaxUsd">Hard cap for the whole job (summaries + embeddings); the job is refused when the estimate exceeds it.</param>
public sealed record RebuildRequest(string Scope, int? Node = null, string? Kind = null, decimal MaxUsd = 0.50m, string Who = "admin");

/// <param name="Summaries">Summaries the job will (re)write.</param>
/// <param name="Model">The cheap-tier summary model used for the price.</param>
public sealed record RebuildEstimate(int Summaries, long InputTokens, long OutputTokens, long EmbeddingTokens, decimal Usd, string Model, string Note);

public sealed record RebuildProgress(string Stage, int Done, int Total, string? Message = null);

/// <summary>One summary node's text before/after a rebuild (Old null = new node, New null = removed).</summary>
public sealed record SummaryDiff(int Index, int Layer, string? Old, string? New);

public sealed record RebuildResult(bool Ok, string Message, IReadOnlyList<SummaryDiff> Diffs, decimal SpentUsd, TimeSpan Elapsed);
