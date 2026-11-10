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
    /// <summary>Players whose surroundings the bot guards (engages mobs near them; config: Minecraft:GuardPlayers). Empty = nobody.</summary>
    public IReadOnlyList<string> GuardPlayers { get; init; } = [];
    public bool GuardEnabled { get; init; } = true;
    /// <summary>A hostile within this distance of a guarded player is engaged.</summary>
    public double GuardRadius { get; init; } = 6;
    /// <summary>Never engage anything further than this from the bot.</summary>
    public double MaxEngageDistance { get; init; } = 16;
    /// <summary>A reflex (or guard) fight that landed no hit on its target in this many ticks gives up ("couldn't reach"):
    /// live 10-27/28 a skeleton on a ledge, a creeper across a gap and a spider by kiwi each held the body as "busy" for
    /// 20 s at a time, over and over, with 0 hits.</summary>
    public int ZeroHitGiveUpTicks { get; init; } = 160;
    /// <summary>A skeleton is engaged on sight only this close (and only with a safe first step toward it); further away it
    /// is left alone unless it gets close, and when it is hurting the bot the bot takes cover instead.</summary>
    public double SkeletonEngageRange { get; init; } = 6;
    /// <summary>Take cover from a ranged mob the bot can't reach once health is at or below this.</summary>
    public int CoverHealth { get; init; } = 16;
    /// <summary>Longest a "take cover" move holds the body (ticks).</summary>
    public int CoverMaxTicks { get; init; } = 80;
    /// <summary>Walks keep this far (horizontal) from any creeper: b1.8.1 EntityCreeper ignites within 3 blocks.</summary>
    public double CreeperAvoidRadius { get; init; } = 3.5;
    /// <summary>A job asked for during a reflex fight waits this long for it to end before answering "busy" (0 = never waits).</summary>
    public TimeSpan ReflexWaitForJobs { get; init; } = TimeSpan.FromSeconds(10);

    // ---- mechanics
    /// <summary>Eye-to-hitbox reach for melee. Vanilla b1.8 client targets entities only within 3.0 (EntityRenderer.getMouseOver).</summary>
    public double Reach { get; init; } = 3.0;
    /// <summary>Ticks between attacks on the same target. Vanilla invulnerability: heartsLife 20, full damage only once
    /// heartsLife ≤ 10 (EntityLiving.attackEntityFrom) → 10 ticks; +1 for server/client tick phase.</summary>
    public int AttackIntervalTicks { get; init; } = 11;
    /// <summary>1.9+ (a version with an attack cooldown): the charge a swing waits for (<c>getCooledAttackStrength</c>); above 0.9 the
    /// hit can crit, sweep and sprint-knock back. Ignored on b1.8.1.</summary>
    public float MinCharge { get; init; } = 0.9f;
    /// <summary>1.9+: at or below this health (and above <see cref="DisengageHealth"/>, where the fight turns into a retreat) a mob
    /// within <see cref="Reach"/> is hit at any charge: an uncharged hit still knocks it back.</summary>
    public int EmergencySwingHealth { get; init; } = 8;
    /// <summary>Jump-and-fall critical hits (b1.8 EntityPlayer.attackTargetEntityWithCurrentItem) against mobs.</summary>
    public bool Crits { get; init; } = true;
    /// <summary>Right-click block with a sword between swings (b1.8: damage → (1 + dmg) / 2).</summary>
    public bool Block { get; init; } = true;
    public CreeperTactic Creeper { get; init; } = CreeperTactic.HitAndRun;
    /// <summary>Health at or below which a mob fight is abandoned (retreat inward, eat).</summary>
    public int DisengageHealth { get; init; } = 6;
    /// <summary>Eat after a fight when food is at or below this (b1.8 regenerates only at food ≥ 18).</summary>
    public int EatBelowFood { get; init; } = 17;
    /// <summary>How far behind the bot (away from the threat) knockback is assumed to carry it. b1.8 knockback is
    /// 0.4 b/t horizontal + 0.4 up (EntityLiving.java:449), which slides ≈ 2.1 blocks (<see cref="CombatRules.KnockbackTravel"/>).</summary>
    public double KnockbackDepth { get; init; } = 3.5;
    /// <summary>Same for a hit from a sprinting player: b1.8 adds 1.0 b/t horizontal + 0.1 up (EntityHuman.java:631-636),
    /// ≈ 7.9 blocks of travel. Used in spars while the opponent's sprint flag (metadata 0, bit 3) is set.</summary>
    public double SprintKnockbackDepth { get; init; } = 9;
    /// <summary>Give up a fight whose target hasn't been hit or come closer for this long.</summary>
    public int StaleFightTicks { get; init; } = 400;
    /// <summary>Item ids never wielded (besides the client's reserved items).</summary>
    public IReadOnlySet<int> NeverWield { get; init; } = new HashSet<int>();

    // ---- sparring
    /// <summary>Players who may be sparred (config: Minecraft:SparPlayers). Empty = nobody.</summary>
    public IReadOnlyList<string> SparPlayers { get; init; } = [];
    /// <summary>The opponent must have asked ("spar", "duel", "fight me", "pvp", "1v1") in chat within this window.</summary>
    public TimeSpan SparConsentWindow { get; init; } = TimeSpan.FromMinutes(2);
    /// <summary>Arena (adaptive, from the island as it is — see <see cref="SparZone"/>): every zone cell is at least this
    /// far from the nearest void/deadly column (rim cell = 1). A spar stops when either party gets within
    /// MinEdgeDistance − 1 of an edge.</summary>
    public int MinEdgeDistance { get; init; } = 3;
    /// <summary>The zone must be a connected area of at least this many standable cells.</summary>
    public int ArenaMinCells { get; init; } = 12;
    /// <summary>No lava/fire/cactus within this many blocks of a zone cell.</summary>
    public int ArenaHazardRadius { get; init; } = 2;
    /// <summary>Neighbouring zone cells may differ by this many blocks (1 = a jump; uneven terrain is fine).</summary>
    public int ArenaMaxStep { get; init; } = 1;
    /// <summary>How far around the players the zone is searched for.</summary>
    public int ArenaSearchRadius { get; init; } = 16;
    /// <summary>Being further than this outside the zone ends the spar.</summary>
    public double ArenaLeaveSlack { get; init; } = 2;
    /// <summary>Never swing while the opponent is this close to an edge (or the knockback would carry them toward one).</summary>
    public int SparNoSwingEdge { get; init; } = 2;
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
