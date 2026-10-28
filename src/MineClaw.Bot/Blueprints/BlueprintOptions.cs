namespace MineClaw.Bot.Blueprints;

/// <summary>"Bot:Blueprints": the design loop (build from a description) and the construction executor.</summary>
public sealed class BlueprintOptions
{
    public bool Enabled { get; set; } = true;
    /// <summary>Author versions per design (v1 + revisions) before the best one is kept.</summary>
    public int MaxIterations { get; set; } = 3;
    /// <summary>Hard spend cap per design (author + perception), USD.</summary>
    public decimal MaxUsdPerDesign { get; set; } = 0.80m;
    public int AuthorMaxTokens { get; set; } = 1600;
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
    public bool AutoResume { get; set; } = true;
    /// <summary>Split a build across bodies only above this many blocks.</summary>
    public int SplitAboveBlocks { get; set; } = 120;
    /// <summary>Claim lease of a build part on the hive claim board (renewed while building).</summary>
    public int ClaimSeconds { get; set; } = 120;
}
