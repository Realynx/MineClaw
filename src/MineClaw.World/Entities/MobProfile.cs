namespace MineClaw.World;

/// <summary>How the combat engine fights a mob type.</summary>
public enum MobTactic
{
    Melee,
    /// <summary>Ranged (skeleton, stray, witch, blaze…): close in along safe ground or break line of sight; never chase over edges.</summary>
    Ranged,
    /// <summary>Creeper: watch the fuse.</summary>
    Creeper,
    /// <summary>Enderman: never look at it; fight only when provoked.</summary>
    Enderman,
    /// <summary>Not fought (ghast, giant, squid, shulker, bosses, anything unknown...).</summary>
    Ignore,
}

/// <summary>Static facts about a mob type in one version (the EntityXxx constructors / attributes): hitbox, max health, the
/// melee damage at normal difficulty, the tactic and whether it attacks unprovoked.</summary>
public sealed record MobProfile(int Type, double Width, double Height, int MaxHealth, int MeleeDamage, MobTactic Tactic, bool Hostile)
{
    /// <summary>Registry name ("husk"); null = <see cref="MobTypes.Name"/>.</summary>
    public string? Name { get; init; }
}

/// <summary>A potion effect on an entity (Entity Effect): id (<see cref="PotionEffects"/>), amplifier (0 = level I), duration in ticks
/// when it was received (32767 = "max", shown as **:**) and when.</summary>
public readonly record struct StatusEffect(int Id, int Amplifier, int DurationTicks, DateTime Since)
{
    /// <summary>Ticks left at <paramref name="now"/> (50 ms ticks; a "max" effect never runs out).</summary>
    public int RemainingTicks(DateTime now) =>
        DurationTicks >= short.MaxValue ? int.MaxValue : Math.Max(0, DurationTicks - (int)((now - Since).TotalMilliseconds / 50));

    public bool Active(DateTime now) => RemainingTicks(now) > 0;
}

/// <summary>Potion effect ids (1.9+ <c>MobEffects</c> / minecraft-data effects.json; the b1.8.1 ids that existed are the same).</summary>
public static class PotionEffects
{
    public const int Speed = 1, Slowness = 2, Haste = 3, MiningFatigue = 4, Strength = 5, InstantHealth = 6, InstantDamage = 7,
        JumpBoost = 8, Nausea = 9, Regeneration = 10, Resistance = 11, FireResistance = 12, WaterBreathing = 13, Invisibility = 14,
        Blindness = 15, NightVision = 16, Hunger = 17, Weakness = 18, Poison = 19, Wither = 20, HealthBoost = 21, Absorption = 22,
        Saturation = 23, Glowing = 24, Levitation = 25, Luck = 26, BadLuck = 27;

    public static string Name(int id) => (uint)id < (uint)Names.Length && Names[id] is { } name ? name : $"effect {id}";

    /// <summary>Effect names by id (1 speed .. 27 bad luck).</summary>
    private static readonly string?[] Names =
    [
        null, "speed", "slowness", "haste", "mining fatigue", "strength", "instant health", "instant damage", "jump boost", "nausea",
        "regeneration", "resistance", "fire resistance", "water breathing", "invisibility", "blindness", "night vision", "hunger",
        "weakness", "poison", "wither", "health boost", "absorption", "saturation", "glowing", "levitation", "luck", "bad luck",
    ];
}
