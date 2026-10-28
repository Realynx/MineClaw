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
    /// <summary>Fights and spars need at least this much health (nothing else is hp-gated, see <see cref="EdgeMinHealth"/>).</summary>
    public int GateMinHealth { get; init; } = 10;
    /// <summary>Void-edge work (bridging, island expansion) is refused at or below this hp; mining/digging/placing never are.</summary>
    public int EdgeMinHealth { get; init; } = 4;
    /// <summary>...and at least this much food (eats first when it has food). 6 and below: no sprinting.</summary>
    public int GateMinFood { get; init; } = 7;
    /// <summary>A mine/generator task of at least this many blocks counts as a long mining trip.</summary>
    public int LongMiningCount { get; init; } = 16;

    // ---- the no-path danger reflex (box in) and spawn safety
    /// <summary>Box the body in (<see cref="Shelter"/>) when retreat has no way home and health is low or mobs outnumber it.</summary>
    public bool ShelterEnabled { get; init; } = true;
    /// <summary>Hostiles within 8 that count as outnumbered (box in even at good health when there's no way home).</summary>
    public int ShelterThreats { get; init; } = 2;
    /// <summary>Wait in the box until hp is at least this and the mobs are gone (or it's day).</summary>
    public int ShelterUntilHealth { get; init; } = 16;
    /// <summary>Longest wait in the box (minutes) before opening it anyway.</summary>
    public double ShelterMaxMinutes { get; init; } = 10;
    /// <summary>At login/respawn, an exposed body (night or mobs near, and not at home level or no walk home) makes itself safe
    /// first: walks home, else boxes in.</summary>
    public bool SecureOnSpawn { get; init; } = true;

    /// <summary>Ticks between reflex evaluations (cheap, but no need to do it every tick).</summary>
    public int CheckIntervalTicks { get; init; } = 10;
}
