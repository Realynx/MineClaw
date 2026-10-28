using MineClaw.Client.Combat;
using MineClaw.Client.Survival;

namespace MineClaw.Client.Versioning;

/// <summary>
/// Melee rules of one version (docs/design/multi-version.md, "Seam: combat"). b1.8.1 = <see cref="CombatRules"/>: spam
/// clicks, 10-tick invulnerability, sword blocking, crits while falling. 1.12.2 adds the 1.9 attack cooldown (damage scales
/// with the charge; attack speed per item), sweeping, shields instead of sword blocking, and different weapon damage.
/// <para>P0: defined and registered; <c>CombatEngine</c> still calls the statics. P4 routes it through the profile.</para>
/// </summary>
public interface ICombatRules
{
    /// <summary>Damage of a fully charged hit with the item.</summary>
    int WeaponDamage(int itemId);
    /// <summary>Ticks between full-strength swings (0 = no cooldown, b1.8.1). 1.12.2: 20 / attack speed.</summary>
    double AttackCooldownTicks(int itemId);
    /// <summary>Ticks a hit entity ignores new full hits.</summary>
    int InvulnerabilityTicks { get; }
    /// <summary>Server-side reach check between positions.</summary>
    double ServerReach { get; }
    /// <summary>Right-click with a sword blocks (b1.8.1) vs a shield in the off hand (1.9+).</summary>
    bool SwordBlocks { get; }
    bool IsCritical(double motionY, bool onGround, bool onLadder, bool inWater);
}

/// <summary>
/// Hunger and natural regeneration (docs/design/multi-version.md, "Seam: hunger"). b1.8.1 = <see cref="HungerRules"/>:
/// +1 hp per 80 ticks at food ≥ 18, free. 1.12.2: saturation-fuelled fast regen (every 10 ticks while food is 20 and
/// saturation &gt; 0), slow regen at ≥ 18 every 80 ticks costing 6 exhaustion per hp, new food values.
/// <para>P0: defined and registered; the survival reflex still calls the statics (P4).</para>
/// </summary>
public interface IHungerRules
{
    int MaxFood { get; }
    int RegenFood { get; }
    int SprintFood { get; }
    int EatTicks { get; }
    int RegenIntervalTicks { get; }
    /// <summary>Healing costs exhaustion (1.6+); false in b1.8.1.</summary>
    bool RegenCostsExhaustion { get; }
    int StarvationFloor(int difficulty);
    float SaturationGain(int hunger, float modifier);
}

/// <summary>b1.8.1 combat: wraps <see cref="CombatRules"/>, unchanged.</summary>
public sealed class Beta181CombatRules : ICombatRules
{
    public static readonly Beta181CombatRules Instance = new();
    public int WeaponDamage(int itemId) => CombatRules.WeaponDamage(itemId);
    public double AttackCooldownTicks(int itemId) => 0;
    public int InvulnerabilityTicks => CombatRules.InvulnerabilityTicks;
    public double ServerReach => CombatRules.ServerReach;
    public bool SwordBlocks => true;
    public bool IsCritical(double motionY, bool onGround, bool onLadder, bool inWater) => CombatRules.IsCritical(motionY, onGround, onLadder, inWater);
}

/// <summary>b1.8.1 hunger: wraps <see cref="HungerRules"/>, unchanged.</summary>
public sealed class Beta181HungerRules : IHungerRules
{
    public static readonly Beta181HungerRules Instance = new();
    public int MaxFood => HungerRules.MaxFood;
    public int RegenFood => HungerRules.RegenFood;
    public int SprintFood => HungerRules.SprintFood;
    public int EatTicks => HungerRules.EatTicks;
    public int RegenIntervalTicks => HungerRules.RegenIntervalTicks;
    public bool RegenCostsExhaustion => false;
    public int StarvationFloor(int difficulty) => HungerRules.StarvationFloor(difficulty);
    public float SaturationGain(int hunger, float modifier) => HungerRules.SaturationGain(hunger, modifier);
}
