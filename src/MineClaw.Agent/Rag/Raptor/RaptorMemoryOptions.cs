namespace MineClaw.Agent.Rag.Raptor;

/// <summary>How the RAPTOR tree contributes to retrieval.</summary>
public enum RaptorMode
{
    /// <summary>Collapsed tree (paper default; every node ranked by cosine). Best recall at any size.</summary>
    Collapsed,
    /// <summary>Top-down tree traversal (top-k per layer). Faster on huge trees, but recall drops as the tree grows.</summary>
    Traversal,
    /// <summary>Both lists fused (collapsed + traversal).</summary>
    Hybrid,
}

/// <summary>
/// RAPTOR integration settings ("Agent:Rag:Raptor"). The tree is built over episodes/activity rollups, knowledge
/// chunks, structures, notes/player facts and the seed game-data corpus; its ranked list is fused with the flat
/// FTS / spatial / vector lists by reciprocal-rank fusion. Summaries run on the cheap (Haiku) tier under a daily budget.
/// </summary>
public sealed class RaptorMemoryOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>"collapsed" (default) | "traversal" | "hybrid".</summary>
    public string Mode { get; set; } = "collapsed";

    // ---------------- retrieval ----------------

    /// <summary>Collapsed: node-count cap of the fused candidate list.</summary>
    public int CandidateTopK { get; set; } = 24;
    /// <summary>Collapsed: token budget of the candidate list (paper default for answer context is 2000).</summary>
    public int CandidateMaxTokens { get; set; } = 4000;
    /// <summary>Traversal: nodes kept per layer (paper top_k = 5).</summary>
    public int TraversalTopK { get; set; } = 5;
    /// <summary>Include summary nodes in hits (false = leaves only; summaries still steer traversal).</summary>
    public bool IncludeSummaries { get; set; } = true;

    // ---------------- what goes into the tree ----------------

    /// <summary>Doc kinds that become tree leaves. Everything else stays in the flat/structured index only.</summary>
    public string[] TreeKinds { get; set; } = ["episode", "knowledge", "structure", "note", "player", SeedCorpus.Kind];
    /// <summary>Doc sources never put in the tree (test chat, personality-change log).</summary>
    public string[] ExcludeSources { get; set; } = ["test", "self"];
    /// <summary>Doc ref prefixes never put in the tree.</summary>
    public string[] ExcludeRefPrefixes { get; set; } = ["eptest:", "self:"];

    // ---------------- scheduling (background; never on the game loop) ----------------

    /// <summary>Run the background maintenance loop (incremental insert, refresh, rebuild).</summary>
    public bool Maintenance { get; set; } = true;
    /// <summary>How often the loop syncs new/changed docs into the tree.</summary>
    public int PollSeconds { get; set; } = 30;
    /// <summary>Docs inserted per sync step.</summary>
    public int InsertBatch { get; set; } = 64;
    /// <summary>RefreshDirty after this many leaves were inserted since the last refresh…</summary>
    public int RefreshEveryLeaves { get; set; } = 25;
    /// <summary>…or this many minutes after the last refresh while anything is dirty.</summary>
    public int RefreshEveryMinutes { get; set; } = 60;
    /// <summary>Cheap-tier summary spend per local day across refresh and rebuild jobs.</summary>
    public decimal DailySummaryBudgetUsd { get; set; } = 0.10m;
    /// <summary>Cap per refresh job.</summary>
    public decimal RefreshMaxUsd { get; set; } = 0.01m;
    /// <summary>Cap per rebuild job (clusters over the cap keep extractive placeholders until a later refresh).</summary>
    public decimal RebuildMaxUsd { get; set; } = 0.08m;
    /// <summary>Full rebuilds only run in this local-hour window [start, end) …</summary>
    public int OffPeakStartHour { get; set; } = 3;
    public int OffPeakEndHour { get; set; } = 7;
    /// <summary>… unless the tree has fewer leaves than this (small rebuilds are cheap and fast).</summary>
    public int RebuildAnytimeBelowLeaves { get; set; } = 300;
    /// <summary>Upper bound on clustering threads during maintenance (0 = half the cores).</summary>
    public int MaxThreads { get; set; }

    public RaptorMode ParsedMode => Mode?.Trim().ToLowerInvariant() switch
    {
        "traversal" or "tree" => RaptorMode.Traversal,
        "hybrid" or "both" => RaptorMode.Hybrid,
        _ => RaptorMode.Collapsed,
    };
}
