namespace MineClaw.Bot.Blueprints;

/// <summary>"Bot:Blueprints": the design loop (build from a description) and the construction executor.</summary>
public sealed class BlueprintOptions
{
    public bool Enabled { get; set; } = true;
    /// <summary>Author versions per design (v1 + revisions) before the best one is kept.</summary>
    public int MaxIterations { get; set; } = 3;
    /// <summary>Hard spend cap per design (author + perception), USD.</summary>
    public decimal MaxUsdPerDesign { get; set; } = 3.00m;
    /// <summary>Output budget per author call. Opus 5.5 can't turn thinking off (ModelProfiles.OffStyle = omit), so the budget
    /// must cover its thinking AND the blueprint (live 10-28: at 1600 every call stopped on max_tokens with no text at all).
    /// Default = Agent:Llm:AuthorMaxTokens (100K, streamed); never above it (0 = that value).</summary>
    public int AuthorMaxTokens { get; set; } = Agent.Llm.LlmOptions.DefaultAuthorMaxTokens;
    /// <summary>Effort for the author's calls: low keeps Opus's (unavoidable) thinking short.</summary>
    public string AuthorEffort { get; set; } = "low";
    /// <summary>Thinking override for the author's calls ("off" | "adaptive" | "omit"; empty = the main tier's).</summary>
    public string AuthorThinking { get; set; } = "off";
    /// <summary>Ask the perception tier for a visual critique of every passing version.</summary>
    public bool Critique { get; set; } = true;
    /// <summary>A design awaiting review answers "build it"/change requests in chat for this long.</summary>
    public int ReviewMinutes { get; set; } = 30;
    /// <summary>Scaffold path length limit (blocks) to reach an unsupported cell.</summary>
    public int MaxScaffold { get; set; } = 6;
    /// <summary>Progress checkpoint every N placements (resume after a restart).</summary>
    public int CheckpointEvery { get; set; } = 8;
    /// <summary>Resume an interrupted build on the next login.</summary>
    /// <summary>
    /// True: a build that was running before a restart starts again by itself. False (default): the brain gets one notice and
    /// resumes it (design build{id}, no second go needed) when its own learned rules allow, e.g. Fox's "only build in daytime".
    /// </summary>
    public bool AutoResume { get; set; }
    /// <summary>Split a build across the idle bodies by default only above this many blocks (a named crew always splits).</summary>
    public int SplitAboveBlocks { get; set; } = 48;
    /// <summary>Claim lease of a build part on the hive claim board (renewed while building).</summary>
    public int ClaimSeconds { get; set; } = 120;
    /// <summary>Confirmation gate: after this many real placements the build pauses and asks once (0 = off). No default
    /// answer: without a trusted yes it stays paused.</summary>
    public int GateBlocks { get; set; } = 5;
    /// <summary>How long a running build waits at the gate before pausing (it stays paused; the answer restarts it).</summary>
    public int GateMinutes { get; set; } = 10;
    /// <summary>Click faces the bot can't see (b1.8.1 NetServerHandler checks distance only, no raytrace).</summary>
    public bool HiddenFaces { get; set; } = true;
    /// <summary>Re-plans per run after deviations before stopping to ask.</summary>
    public int MaxReplans { get; set; } = 4;
}
