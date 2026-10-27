namespace MineClaw.Client.Combat;

/// <summary>How to deal with creepers (their explosion also destroys island blocks).</summary>
public enum CreeperTactic
{
    /// <summary>Hit once (knockback pushes it away), back off past the fuse range, re-engage after it resets.</summary>
    HitAndRun,
    /// <summary>Never melee: keep retreating inward and let the players deal with it.</summary>
    Avoid,
}

/// <summary>What to hold while sparring.</summary>
public enum SparWeapon
{
    /// <summary>Empty hand or any non-weapon item: 1 damage (vanilla Item.getDamageVsEntity).</summary>
    Hand,
    /// <summary>The weakest real weapon in the inventory.</summary>
    Weakest,
    /// <summary>The best weapon (not gentle; opt-in).</summary>
    Best,
}

/// <summary>Combat engine configuration (C# only; the LLM sees fight start/end events).</summary>
public sealed record CombatOptions
{
    // ---- reflexes
    /// <summary>Fight hostile mobs that come at the bot.</summary>
    public bool DefendSelf { get; init; } = true;
    /// <summary>Hostiles within this distance of the bot (and closing in, or provoked) start a fight.</summary>
    public double DefendRadius { get; init; } = 5;
    /// <summary>Players whose surroundings the bot guards (engages mobs near them).</summary>
    public IReadOnlyList<string> GuardPlayers { get; init; } = ["poofimafox", "kiwi"];
    public bool GuardEnabled { get; init; } = true;
    /// <summary>A hostile within this distance of a guarded player is engaged.</summary>
    public double GuardRadius { get; init; } = 6;
    /// <summary>Never engage anything further than this from the bot.</summary>
    public double MaxEngageDistance { get; init; } = 16;

    // ---- mechanics
    /// <summary>Eye-to-hitbox reach for melee. Vanilla b1.8 client targets entities only within 3.0 (EntityRenderer.getMouseOver).</summary>
    public double Reach { get; init; } = 3.0;
    /// <summary>Ticks between attacks on the same target. Vanilla invulnerability: heartsLife 20, full damage only once
    /// heartsLife ≤ 10 (EntityLiving.attackEntityFrom) → 10 ticks; +1 for server/client tick phase.</summary>
    public int AttackIntervalTicks { get; init; } = 11;
    /// <summary>Jump-and-fall critical hits (b1.8 EntityPlayer.attackTargetEntityWithCurrentItem) against mobs.</summary>
    public bool Crits { get; init; } = true;
    /// <summary>Right-click block with a sword between swings (b1.8: damage → (1 + dmg) / 2).</summary>
    public bool Block { get; init; } = true;
    public CreeperTactic Creeper { get; init; } = CreeperTactic.HitAndRun;
    /// <summary>Health at or below which a mob fight is abandoned (retreat inward, eat).</summary>
    public int DisengageHealth { get; init; } = 6;
    /// <summary>Eat after a fight when food is at or below this (b1.8 regenerates only at food ≥ 18).</summary>
    public int EatBelowFood { get; init; } = 17;
    /// <summary>How far behind the bot (away from the threat) knockback is assumed to carry it. Vanilla knockback
    /// sets ~0.4 b/t horizontal + 0.4 up; with air drag 0.91 that is ≈2.7 blocks.</summary>
    public double KnockbackDepth { get; init; } = 3.5;
    /// <summary>Give up a fight whose target hasn't been hit or come closer for this long.</summary>
    public int StaleFightTicks { get; init; } = 400;
    /// <summary>Item ids never wielded (besides the client's reserved items).</summary>
    public IReadOnlySet<int> NeverWield { get; init; } = new HashSet<int>();

    // ---- sparring
    /// <summary>Players who may be sparred (both regulars by default).</summary>
    public IReadOnlyList<string> SparPlayers { get; init; } = ["poofimafox", "kiwi"];
    /// <summary>The opponent must have asked ("spar", "duel", "fight me", "pvp", "1v1") in chat within this window.</summary>
    public TimeSpan SparConsentWindow { get; init; } = TimeSpan.FromMinutes(2);
    /// <summary>Arena: every cell within this Chebyshev radius of the center is flat, solid and standable.</summary>
    public int ArenaRadius { get; init; } = 2;
    /// <summary>No void/deadly drop within ArenaRadius + this of the arena center.</summary>
    public int ArenaEdgeMargin { get; init; } = 3;
    /// <summary>Leaving the arena by more than this ends the spar.</summary>
    public double ArenaLeaveSlack { get; init; } = 2;
    /// <summary>Bot stops at or below this health (exact, from 0x08).</summary>
    public int SparSelfStopHealth { get; init; } = 8;
    /// <summary>Stop when the opponent's estimated health is at or below this.</summary>
    public double SparOpponentStopHealth { get; init; } = 10;
    /// <summary>Stop after this many confirmed hits landed in total.</summary>
    public int SparMaxHits { get; init; } = 8;
    /// <summary>Extra fraction added to every estimated hit (conservative).</summary>
    public double SparDamageMargin { get; init; } = 0.25;
    /// <summary>Fraction of the opponent's armor value that is credited (armor wears down; be conservative).</summary>
    public double SparArmorTrust { get; init; } = 0.5;
    /// <summary>Damage assumed when the opponent is hurt by something other than the bot (fall, cactus...).</summary>
    public double SparOtherDamageAssumed { get; init; } = 4;
    /// <summary>A previous spar's estimate is reused (no regeneration assumed) if it ended within this window.</summary>
    public TimeSpan SparMemory { get; init; } = TimeSpan.FromMinutes(20);
    public SparWeapon SparWeapon { get; init; } = SparWeapon.Hand;
    /// <summary>Confirmed hits by either side that end a round (when rounds are requested).</summary>
    public int SparRoundHits { get; init; } = 3;
    /// <summary>Hard time limit of a spar.</summary>
    public TimeSpan SparMaxDuration { get; init; } = TimeSpan.FromMinutes(3);
    /// <summary>Any of these words from anyone in chat ends a spar immediately.</summary>
    public IReadOnlyList<string> StopWords { get; init; } = ["stop", "yield", "uncle", "gg"];

    // ---- telemetry
    /// <summary>Directory for logs/combat-yyyyMMdd.jsonl (null = no file).</summary>
    public string? TelemetryDirectory { get; init; } = "logs";
}
