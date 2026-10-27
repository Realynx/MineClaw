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

    // ---------------- growth v2: signals, aggregates, rendering ----------------

    /// <summary>Record deterministic personality signals from every finished (non-test) episode.</summary>
    public bool Signals { get; set; } = true;
    /// <summary>Players whose treatment counts fully (TrustedPlayers always do). Others weigh <see cref="NonRegularWeight"/>.</summary>
    public string[] RegularPlayers { get; set; } = [];
    /// <summary>Anyone who interacted in this many distinct episodes also becomes a regular.</summary>
    public int RegularAfterEpisodes { get; set; } = 25;
    public double NonRegularWeight { get; set; } = 0.35;
    /// <summary>Other bots (hive clones, the main bot) barely move the personality: no praise loops between clones.</summary>
    public double BotWeight { get; set; } = 0.15;

    /// <summary>Mood (cheer, confidence, energy, affection) relaxes toward neutral with this half-life.</summary>
    public double MoodHalfLifeHours { get; set; } = 3;
    /// <summary>Relationship affinity/playfulness half-life.</summary>
    public double RelationshipHalfLifeDays { get; set; } = 14;
    /// <summary>Trust in a player's claims moves slowest.</summary>
    public double TrustHalfLifeDays { get; set; } = 30;
    public double PreferenceHalfLifeDays { get; set; } = 21;
    /// <summary>Lowest affinity a relationship can reach ("wary"): bad treatment never turns the bot hostile.</summary>
    public double AffinityFloor { get; set; } = -0.5;
    public double TrustFloor { get; set; } = -0.5;

    /// <summary>Distinct episodes a phrase must recur in (from one player) before it is an inside-joke / nickname candidate.</summary>
    public int InsideJokeEpisodes { get; set; } = 3;
    /// <summary>An entry renders as a settled trait only after this many observations (add + reinforcements); before that "~".</summary>
    public int StrongTraitObservations { get; set; } = 3;
    /// <summary>Reflection adds (trait/like/dislike/opinion/joke) must cite signals from at least this many distinct episodes.</summary>
    public int MinEvidenceEpisodes { get; set; } = 2;
    /// <summary>...and this much summed signal weight (a regular = 1, a stranger = NonRegularWeight).</summary>
    public double MinEvidenceWeight { get; set; } = 1.0;

    /// <summary>Uncached per-turn "[self]" block cap (≈180 tokens at 3.5 chars/token).</summary>
    public int MaxBlockChars { get; set; } = 630;
    /// <summary>Cap on the clone's own shard part of the block (≈60–80 tokens).</summary>
    public int MaxShardChars { get; set; } = 280;
    public int MaxPeopleLines { get; set; } = 3;
    /// <summary>Players seen in signals within this many minutes count as present (plus the turn's speakers).</summary>
    public int PresentMinutes { get; set; } = 20;

    /// <summary>Build the RAPTOR "who I am" self subtree during reflection (needs embeddings).</summary>
    public bool SelfTree { get; set; } = true;
    /// <summary>Cheap-tier summaries per self-tree build (batched with reflection; nothing per turn).</summary>
    public int SelfTreeMaxSummaries { get; set; } = 6;
    public decimal SelfTreeMaxUsd { get; set; } = 0.01m;

    /// <summary>Hive: a trait is promoted to the shared profile only when at least this many members show it independently.</summary>
    public int PromotionMinMembers { get; set; } = 2;
    public double PromotionMinStrength { get; set; } = 0.55;
    public int PromotionMinObservations { get; set; } = 2;
    public double PromoteEveryHours { get; set; } = 6;
}
