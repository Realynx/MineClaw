namespace MineClaw.Client.Survival;

/// <summary>
/// Survival reflex configuration (hunger + health), all C#, no LLM in the loop. Defaults follow the b1.8.1 rules in
/// <see cref="HungerRules"/>.
/// </summary>
public sealed record SurvivalOptions
{
    /// <summary>Master switch for the reflex (auto-eat, low-health retreat, out-of-food events, task gating).</summary>
    public bool Enabled { get; init; } = true;
    public bool AutoEat { get; init; } = true;

    // ---- eating
    /// <summary>Eat when food is at or below this.</summary>
    public int EatAtFood { get; init; } = 14;
    /// <summary>Also eat when hurt and food is below the regeneration line (b1.8: regen only at food ≥ 18).</summary>
    public bool EatToRegenerate { get; init; } = true;
    /// <summary>At or below this food level rotten flesh / raw chicken may be eaten (if nothing else is available).</summary>
    public int StarvingFood { get; init; } = 6;
    /// <summary>Hunger points over the deficit tolerated when eating only to unlock regeneration (food 15..17).</summary>
    public int MaxWaste { get; init; } = 3;
    /// <summary>Golden apples are eaten only at or below <see cref="CriticalHealth"/> (they are precious).</summary>
    public bool SaveGoldenApples { get; init; } = true;

    // ---- health
    /// <summary>Low health: cancel risky actions, retreat inward, eat; one High danger per episode.</summary>
    public int LowHealth { get; init; } = 8;
    /// <summary>Critical health: cancel any action (except eating); a critical danger.</summary>
    public int CriticalHealth { get; init; } = 4;
    /// <summary>A health alert re-arms only after health rises this far above its threshold.</summary>
    public int Hysteresis { get; init; } = 2;
    /// <summary>Retreat inward at low health when closer than this to a void edge (EdgeDistance semantics, rim = 1).</summary>
    public int RetreatEdgeDistance { get; init; } = 3;

    // ---- task gating (refuse to START risky actions)
    /// <summary>Bridging, fights, spars and long mining trips need at least this much health.</summary>
    public int GateMinHealth { get; init; } = 10;
    /// <summary>...and at least this much food (eats first when it has food). 6 and below: no sprinting.</summary>
    public int GateMinFood { get; init; } = 7;
    /// <summary>A mine/generator task of at least this many blocks counts as a long mining trip.</summary>
    public int LongMiningCount { get; init; } = 16;

    /// <summary>Ticks between reflex evaluations (cheap, but no need to do it every tick).</summary>
    public int CheckIntervalTicks { get; init; } = 10;
}
