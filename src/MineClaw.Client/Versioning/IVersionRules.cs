using MineClaw.Client.Combat;
using MineClaw.Client.Survival;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Client.Versioning;

/// <summary>The attacker's state at the moment of a hit, as the version's crit and sweep rules see it
/// (1.12.2 <c>EntityPlayer.attackTargetEntityWithCurrentItem</c>).</summary>
/// <param name="Strength">The attack charge 0..1 (<see cref="ICombatRules.AttackStrength"/>); 1 on b1.8.1.</param>
/// <param name="WalkedThisTick">Horizontal distance moved this tick (the sweep needs less than the AI move speed).</param>
public readonly record struct AttackContext(float Strength, double MotionY, double FallDistance, bool OnGround, bool OnLadder, bool InWater,
    bool Sprinting, int HeldItem, bool Blind = false, bool Riding = false, bool TargetLiving = true, double WalkedThisTick = 0,
    double AiMoveSpeed = 0.1);

/// <summary>
/// Melee rules of one version (docs/design/multi-version.md, "Seam: combat"). b1.8.1 = <see cref="CombatRules"/>: spam
/// clicks, 10-tick invulnerability, sword blocking, crits while falling. 1.12.2 (<see cref="Release1122CombatRules"/>) adds the
/// 1.9 attack cooldown (damage scales with the charge; attack speed per item), sweeping, shields in the off hand instead of
/// sword blocking, the attribute weapon damage, armor toughness and the attack-before-swing packet order. The defaults are
/// b1.8.1's, so a b1.8.1 caller sees exactly the old behaviour.
/// </summary>
public interface ICombatRules
{
    /// <summary>Damage of a fully charged hit with the item (rounded down on 1.12.2: see <see cref="AttackDamage"/>).</summary>
    int WeaponDamage(int itemId);
    /// <summary>Exact damage of a fully charged hit (1.12.2: the attack-damage attribute, base 1 + the item's modifier).</summary>
    double AttackDamage(int itemId) => WeaponDamage(itemId);
    /// <summary>Ticks between full-strength swings (0 = no cooldown, b1.8.1). 1.12.2: 20 / attack speed.</summary>
    double AttackCooldownTicks(int itemId);
    /// <summary>The charge of a hit <paramref name="ticksSinceSwing"/> ticks after the last swing / item change (1.12.2
    /// <c>getCooledAttackStrength(0.5F)</c>); 1 when there is no cooldown.</summary>
    float AttackStrength(int ticksSinceSwing, int itemId) => 1f;
    /// <summary>Damage of a hit at <paramref name="strength"/> (1.12.2: × (0.2 + 0.8·s²)), before crits.</summary>
    double ChargedDamage(int itemId, float strength) => AttackDamage(itemId);
    /// <summary>Ticks a hit entity ignores new full hits.</summary>
    int InvulnerabilityTicks { get; }
    /// <summary>Server-side reach check between positions.</summary>
    double ServerReach { get; }
    /// <summary>The server's reach when the target can't be seen (1.12.2 <c>processUseEntity</c>: 3 blocks).</summary>
    double ServerReachUnseen => ServerReach;
    /// <summary>Right-click with a sword blocks (b1.8.1) vs a shield in the off hand (1.9+).</summary>
    bool SwordBlocks { get; }
    bool IsCritical(double motionY, bool onGround, bool onLadder, bool inWater);
    /// <summary>Whether the hit is a critical one (1.12.2 also needs a charge &gt; 0.9 and no sprint).</summary>
    bool IsCriticalHit(in AttackContext a) => IsCritical(a.MotionY, a.OnGround, a.OnLadder, a.InWater);
    /// <summary>Damage of a crit with base damage <paramref name="damage"/> (b1.8.1 d·3/2+1; 1.12.2 ×1.5).</summary>
    double CritDamage(double damage) => CombatRules.MaxCritDamage((int)damage);
    /// <summary>Whether the hit sweeps the entities around the target (1.12.2: a sword, charged, on the ground, slow, no crit or
    /// sprint knockback). Never on b1.8.1.</summary>
    bool IsSweep(in AttackContext a) => false;
    /// <summary>The box around the target a sweep hits (1.12.2: the target's box grown by 1, 0.25, 1, within 3 of the attacker).</summary>
    AABB SweepBox(AABB target) => target;
    /// <summary>The vanilla client's packet order for a melee hit: b1.8.1 swings then attacks; 1.12.2 attacks then swings (the
    /// server's <c>swingArm</c> resets the attack cooldown, so a swing first would make every hit an uncharged one).</summary>
    bool SwingBeforeAttack => true;
    /// <summary>The block the body can raise with these hands: b1.8.1 a sword in the main hand; 1.12.2 a shield in either hand
    /// (the off hand first, as <c>processRightClick</c> tries the main hand, whose sword has no use).</summary>
    bool CanBlock(ItemStack main, ItemStack offhand) => SwordBlocks && CombatRules.IsSword(main.Id);
    /// <summary>The hand the block is raised with (0 main, 1 off hand).</summary>
    int BlockHand(ItemStack main, ItemStack offhand) => 0;
    /// <summary>Ticks a raised block needs before it stops a hit (1.12.2 <c>isActiveItemStackBlocking</c>: 5).</summary>
    int BlockRaiseTicks => 0;
    /// <summary>Damage that gets through a block (b1.8.1 (1 + d) &gt;&gt; 1; 1.12.2: a shield facing the hit stops all of it).</summary>
    double BlockedDamage(double damage) => CombatRules.BlockedDamage((int)Math.Ceiling(damage));
    /// <summary>A hit from an attacker holding <paramref name="attackerHeld"/> disables a raised shield (1.12.2: an axe).</summary>
    bool DisablesShield(int attackerHeld) => false;
    /// <summary>Ticks a disabled shield stays down (1.12.2 <c>disableShield</c>: 100).</summary>
    int ShieldDisableTicks => 0;
    /// <summary>The item saves its holder from a lethal hit (1.12.2 totem of undying in either hand).</summary>
    bool SavesFromDeath(int itemId) => false;
    /// <summary>Monster damage at a difficulty (b1.8.1 easy d/3+1; 1.12.2 easy min(d/2+1, d); both: hard ×1.5).</summary>
    double MonsterDamageToPlayer(double damage, int difficulty) => CombatRules.MonsterDamageToPlayer((int)damage, difficulty);
    /// <summary>Armor reduction (b1.8.1 points scaled by durability; 1.12.2 <c>CombatRules.getDamageAfterAbsorb</c> with toughness).</summary>
    double DamageAfterArmor(double damage, IEnumerable<ItemStack> armor) => CombatRules.DamageAfterArmor(damage, CombatRules.ArmorValue(armor));
}

/// <summary>
/// Hunger and natural regeneration (docs/design/multi-version.md, "Seam: hunger"). b1.8.1 = <see cref="HungerRules"/>:
/// +1 hp per 80 ticks at food ≥ 18, free. 1.12.2 (<see cref="Release1122HungerRules"/>): saturation-fuelled fast regen (every 10
/// ticks while food is 20 and saturation &gt; 0), slow regen at ≥ 18 every 80 ticks costing 6 exhaustion per hp, the 1.11
/// exhaustion values.
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
    /// <summary>Food level of the fast saturation regeneration (1.12.2: 20, every 10 ticks); 0 = none (b1.8.1).</summary>
    int FastRegenFood => 0;
    /// <summary>Exhaustion of one action (b1.8.1 values; 1.12.2 the 1.11 cuts).</summary>
    float Exhaustion(ExhaustionCause cause) => cause switch
    {
        ExhaustionCause.Jump => 0.2f, ExhaustionCause.SprintJump => 0.8f, ExhaustionCause.Attack => 0.3f,
        ExhaustionCause.Damage => 0.3f, ExhaustionCause.BlockBroken => 0.025f, ExhaustionCause.SprintMeter => 0.1f,
        ExhaustionCause.SwimMeter => 0.015f, ExhaustionCause.WalkMeter => 0.01f, _ => 0f,
    };
    /// <summary>Hit points regained per minute at (food, saturation) with no work done (the regen rate the survival reflex counts on).</summary>
    double RegenPerMinute(int food, float saturation) => food >= RegenFood ? 1200.0 / RegenIntervalTicks : 0;
    /// <summary>Eating now would switch on a faster regeneration (1.12.2: topping food up to 20 with saturation when hurt).</summary>
    bool WantsTopUp(int health, int food, float saturation) => false;
}

/// <summary>What spends food (<see cref="IHungerRules.Exhaustion"/>).</summary>
public enum ExhaustionCause { Jump, SprintJump, Attack, Damage, BlockBroken, SprintMeter, SwimMeter, WalkMeter }

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
