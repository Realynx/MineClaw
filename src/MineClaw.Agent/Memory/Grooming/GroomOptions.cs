namespace MineClaw.Agent.Memory.Grooming;

/// <summary>Memory-corpus settings ("Agent:Memory").</summary>
public sealed class MemoryCorpusOptions
{
    /// <summary>The grooming chain every doc write goes through ("Agent:Memory:Groom").</summary>
    public GroomOptions Groom { get; set; } = new();
}

/// <summary>
/// The memory groomer ("Agent:Memory:Groom"): a deterministic pre-processing chain between every writer and the docs table
/// (activity episodes, structure notes, chat/episode summaries, remember_note, ledger notes, shard imports, log ingestion).
/// <see cref="Sources"/> names the stages per doc source (in order); a source not listed uses <see cref="DefaultStages"/>.
/// Stage names: normalize, denoise, redact, phrase, merge, coalesce (activity episodes), size, rewrite (LLM, off by default).
/// </summary>
public sealed class GroomOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>Stages for a source not in <see cref="Sources"/> (seed data, knowledge files, raptor summaries: untouched).</summary>
    public string[] DefaultStages { get; set; } = [];

    /// <summary>Source → stages (in order). Replaces the built-in entry for that source when set in config.</summary>
    public Dictionary<string, string[]> Sources { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["activity"] = ["normalize", "redact", "coalesce", "size"],
        ["episode"] = ["normalize", "denoise", "redact"],
        ["journal"] = ["normalize", "denoise", "redact"],
        // Raw logs: de-noise first, while the level (DBG/TRC) is still on the line.
        ["log"] = ["denoise", "normalize", "redact", "phrase", "merge", "size", "rewrite"],
        ["paste"] = ["denoise", "normalize", "redact", "phrase", "merge", "size", "rewrite"],
        ["memory"] = ["normalize", "redact"],
        ["structure"] = ["normalize", "redact"],
        ["claim"] = ["normalize", "redact"],
        ["belief"] = ["normalize", "redact"],
        ["rule"] = ["normalize", "redact"],
        ["self"] = ["normalize", "redact"],
        ["selfnote"] = ["normalize", "redact"],
        ["hive"] = ["normalize", "denoise", "redact"],
        ["admin"] = ["normalize", "redact"],
        ["routine"] = ["redact"],
    };

    /// <summary>Same-fact merge window (the merge stage): a repeat inside it extends the existing doc.</summary>
    public int MergeWindowMinutes { get; set; } = 120;

    /// <summary>Docs above this many estimated tokens are split (the size stage); 0 = <c>Agent:Rag:ChunkMaxTokens</c>.</summary>
    public int ChunkMaxTokens { get; set; }

    /// <summary>Extra de-noise patterns (regex, case-insensitive): matching lines are dropped.</summary>
    public string[] DropPatterns { get; set; } = [];

    /// <summary>Spelling aliases for players/bots (any case) → the canonical name ("clone 67" → "maid_chan_67").</summary>
    public Dictionary<string, string> Names { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public ActivityEpisodeOptions Activity { get; set; } = new();
    public GroomRewriteOptions Rewrite { get; set; } = new();

    /// <summary>The stages configured for <paramref name="source"/>.</summary>
    public IReadOnlyList<string> StagesFor(string? source) =>
        source != null && Sources.TryGetValue(source, out var s) ? s : DefaultStages;
}

/// <summary>Activity coalescing ("Agent:Memory:Groom:Activity").</summary>
public sealed class ActivityEpisodeOptions
{
    /// <summary>Routine work (gen mining, chopping, farming rounds) gets at most one episode per actor+place per this window.</summary>
    public int RoutineWindowMinutes { get; set; } = 120;
    /// <summary>Crew bots join one crew episode when their sessions start within this long of its last activity.</summary>
    public int CrewOverlapMinutes { get; set; } = 15;
    /// <summary>Placing at least this many blocks in one session is a build (a novel event, never folded into routine).</summary>
    public int BuildMinBlocks { get; set; } = 8;
    /// <summary>Fold crew bots' episodes about the same job into one crew episode.</summary>
    public bool CrewMerge { get; set; } = true;
    /// <summary>Run the one-off compaction of per-minute activity episodes at bot startup (idempotent; the user decides).</summary>
    public bool CompactOnStartup { get; set; }
}

/// <summary>The optional LLM rewrite stage ("Agent:Memory:Groom:Rewrite"): off by default, memory tier only, budget-capped.</summary>
public sealed class GroomRewriteOptions
{
    public bool Enabled { get; set; }
    /// <summary>Only free-text sources (pasted logs) are rewritten.</summary>
    public string[] Sources { get; set; } = ["log", "paste"];
    /// <summary>Hard cap per process day for rewrite calls.</summary>
    public decimal DailyMaxUsd { get; set; } = 0.25m;
    public int MaxOutputTokens { get; set; } = 300;
}
