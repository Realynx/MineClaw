namespace MineClaw.Agent.Self;

/// <summary>
/// Evolving personality ("Agent:Self"). <see cref="AgentOptions.Persona"/> stays the fixed anchor; this is a small,
/// bounded, slowly-changing layer on top, rendered into the uncached identity block (never the cached prefix).
/// </summary>
public sealed class SelfOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>Run a cheap-tier reflection pass after this many episodes...</summary>
    public int ReflectEveryEpisodes { get; set; } = 30;
    /// <summary>...or after this many hours (whichever comes first, given at least <see cref="MinEpisodesForReflection"/> new episodes).</summary>
    public double ReflectEveryHours { get; set; } = 24;
    public int MinEpisodesForReflection { get; set; } = 5;
    public int MaxChangesPerPass { get; set; } = 3;
    /// <summary>Rate limit for the model's own reflect_self calls.</summary>
    public int MaxToolChangesPerHour { get; set; } = 6;

    /// <summary>Strength lost per day without reinforcement (multiplicative; milestones decay at half rate).</summary>
    public double DecayPerDay { get; set; } = 0.03;
    /// <summary>Entries that decay below this are dropped.</summary>
    public double MinStrength { get; set; } = 0.1;
    /// <summary>Re-observing an entry: s += (1 - s) × rate.</summary>
    public double ReinforceRate { get; set; } = 0.35;
    public double InitialStrength { get; set; } = 0.5;

    public int MaxEntryChars { get; set; } = 60;
    /// <summary>Hard cap on the rendered "Who you've become" section (~120–180 tokens).</summary>
    public int MaxRenderChars { get; set; } = 650;

    public int MaxTraits { get; set; } = 5;
    public int MaxLikes { get; set; } = 4;
    public int MaxDislikes { get; set; } = 3;
    public int MaxOpinions { get; set; } = 3;
    public int MaxJokes { get; set; } = 3;
    public int MaxNicknames { get; set; } = 3;
    public int MaxRelationships { get; set; } = 6;
    public int MaxMilestones { get; set; } = 3;

    /// <summary>Players allowed to reset / undo / roll back the personality (via personality_admin).</summary>
    public string[] TrustedPlayers { get; set; } = [];
    /// <summary>An admin request must be backed by chat from that trusted player within this many minutes.</summary>
    public int AdminRequestMinutes { get; set; } = 3;
    /// <summary>Extra words that make an entry invalid (added to the built-in abuse filter).</summary>
    public string[] BlockedWords { get; set; } = [];
}
