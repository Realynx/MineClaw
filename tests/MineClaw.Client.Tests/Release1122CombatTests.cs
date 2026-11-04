using MineClaw.Client.Combat;
using MineClaw.Client.Survival;
using MineClaw.Client.Versioning;
using MineClaw.Data;
using MineClaw.Data.Versioning;
using MineClaw.Protocol.Packets;
using MineClaw.World;
using MineClaw.World.Versioning;

namespace MineClaw.Client.Tests;

/// <summary>
/// Multi-version P4: the 1.9 combat (cooldown curve, damage from attributes, crit and sweep conditions, shields and the axe,
/// knockback, armor) and 1.12.2 hunger, against values computed by hand from the decompiled source (float arithmetic as Java's).
/// </summary>
public class Release1122CombatRulesTests
{
    private static readonly Release1122CombatRules R = Release1122CombatRules.Instance;

    [Fact]
    public void Cooldown_curve_matches_getCooledAttackStrength()
    {
        // Hand: speed 4.0 → period (float)(1/4·20) = 5; strength = clamp((t + 0.5) / 5).
        Assert.Equal(5f, Release1122CombatRules.CooldownPeriod(0));
        Assert.Equal(0.1f, R.AttackStrength(0, 0));
        Assert.Equal(0.9f, R.AttackStrength(4, 0));
        Assert.Equal(1f, R.AttackStrength(5, 0));
        // Diamond sword: 4.0 + (−2.4000000953674316) = 1.5999999046325684 → period (float)12.500000745 = 12.500001f.
        float sword = (float)(1.0 / (4.0 - 2.4000000953674316) * 20.0);
        Assert.Equal(sword, Release1122CombatRules.CooldownPeriod(Items.DiamondSword));
        Assert.Equal(12.500001f, sword);
        Assert.Equal(0.5f / sword, R.AttackStrength(0, Items.DiamondSword));
        Assert.Equal(11.5f / sword, R.AttackStrength(11, Items.DiamondSword)); // 0.92: the first tick over 0.9
        Assert.Equal(12.5f / sword, R.AttackStrength(12, Items.DiamondSword)); // 0.99999994: (float)12.500001 is just over 12.5
        Assert.Equal(1f, R.AttackStrength(13, Items.DiamondSword));
        Assert.Equal(11, R.TicksToCharge(Items.DiamondSword, 0.9f));
        // Wooden axe: 4.0 + (double)−3.2F = 0.79999995 → period 25.000002f; 22.5/25.000002 = 0.8999999 < 0.9 → 23 ticks.
        float axe = (float)(1.0 / (4.0 + (double)-3.2f) * 20.0);
        Assert.Equal(axe, Release1122CombatRules.CooldownPeriod(Items.WoodenAxe));
        Assert.Equal(23, R.TicksToCharge(Items.WoodenAxe, 0.9f));
        // Diamond axe 1.0 (period 20), iron pickaxe 4 + (double)−2.8F, stone shovel 1.0, diamond hoe 3 + 1 − 4 = 0 → 4.0.
        Assert.Equal(20f, Release1122CombatRules.CooldownPeriod(Items.DiamondAxe));
        Assert.Equal((float)(1.0 / (4.0 + (double)-2.8f) * 20.0), Release1122CombatRules.CooldownPeriod(Items.IronPickaxe));
        Assert.Equal(20f, Release1122CombatRules.CooldownPeriod(Items.StoneShovel));
        Assert.Equal(5f, Release1122CombatRules.CooldownPeriod(Items.DiamondHoe));
        Assert.Equal(20.0 / 1.6, R.AttackCooldownTicks(Items.IronSword), 4);
        Assert.Equal(0, Beta181CombatRules.Instance.AttackCooldownTicks(Items.IronSword)); // b1.8.1: no cooldown
    }

    [Fact]
    public void Damage_from_attributes_and_the_charge()
    {
        // 1 (player base) + the item's modifier: sword 3 + material, axe its own table, pickaxe 1 + m, shovel 1.5 + m, hoe 0.
        Assert.Equal(1, R.AttackDamage(0));
        Assert.Equal(1, R.AttackDamage(Items.Stick));
        Assert.Equal(4, R.AttackDamage(Items.WoodenSword));
        Assert.Equal(4, R.AttackDamage(Items.GoldenSword));
        Assert.Equal(5, R.AttackDamage(Items.StoneSword));
        Assert.Equal(6, R.AttackDamage(Items.IronSword));
        Assert.Equal(7, R.AttackDamage(Items.DiamondSword));
        Assert.Equal(7, R.AttackDamage(Items.WoodenAxe));
        Assert.Equal(9, R.AttackDamage(Items.StoneAxe));
        Assert.Equal(9, R.AttackDamage(Items.DiamondAxe));
        Assert.Equal(7, R.AttackDamage(Items.GoldenAxe));
        Assert.Equal(3, R.AttackDamage(Items.StonePickaxe));
        Assert.Equal(4.5, R.AttackDamage(Items.IronShovel));
        Assert.Equal(1, R.AttackDamage(Items.DiamondHoe));
        // f × (0.2 + 0.8·s²): half charged = 0.4×; uncharged (s 0.04) ≈ 0.2×.
        Assert.Equal(7f * (0.2f + 0.5f * 0.5f * 0.8f), (float)R.ChargedDamage(Items.DiamondSword, 0.5f));
        Assert.Equal(7f, (float)R.ChargedDamage(Items.DiamondSword, 1f));
        Assert.Equal(10.5, R.CritDamage(7));
        Assert.Equal(16, ((ICombatRules)Beta181CombatRules.Instance).CritDamage(10)); // b1.8.1: d·3/2 + 1
    }

    private static AttackContext Falling(float s = 1f) => new(s, -0.2, 0.4, OnGround: false, OnLadder: false, InWater: false, Sprinting: false, Items.DiamondSword);
    private static AttackContext Standing(float s = 1f, int held = Items.DiamondSword) => new(s, 0, 0, OnGround: true, OnLadder: false, InWater: false, Sprinting: false, held);

    [Fact]
    public void Crit_needs_a_charge_over_09_a_fall_and_no_sprint()
    {
        Assert.True(R.IsCriticalHit(Falling()));
        Assert.True(R.IsCriticalHit(Falling(0.92f)));
        Assert.False(R.IsCriticalHit(Falling(0.9f)));                       // strictly greater than 0.9
        Assert.False(R.IsCriticalHit(Falling() with { Sprinting = true }));
        Assert.False(R.IsCriticalHit(Falling() with { FallDistance = 0 }));
        Assert.False(R.IsCriticalHit(Falling() with { OnLadder = true }));
        Assert.False(R.IsCriticalHit(Falling() with { InWater = true }));
        Assert.False(R.IsCriticalHit(Falling() with { Blind = true }));
        Assert.False(R.IsCriticalHit(Falling() with { Riding = true }));
        Assert.False(R.IsCriticalHit(Standing()));
        // b1.8.1: any falling hit crits, charge or not.
        Assert.True(((ICombatRules)Beta181CombatRules.Instance).IsCriticalHit(Falling(0.1f)));
    }

    [Fact]
    public void Sweep_needs_a_sword_a_charge_the_ground_and_a_slow_step()
    {
        Assert.True(R.IsSweep(Standing()));
        Assert.False(R.IsSweep(Standing(held: Items.DiamondAxe)));
        Assert.False(R.IsSweep(Standing(0.85f)));
        Assert.False(R.IsSweep(Standing() with { Sprinting = true }));      // the sprint knockback hit instead
        Assert.False(R.IsSweep(Standing() with { WalkedThisTick = 0.13 }));  // walking at full speed: 0.216 × 0.6 > 0.1
        Assert.False(R.IsSweep(Falling()));                                  // a crit
        Assert.False(((ICombatRules)Beta181CombatRules.Instance).IsSweep(Standing()));
        var box = R.SweepBox(new AABB(0, 64, 0, 0.6, 65.95, 0.6));
        Assert.Equal((-1, 63.75, -1, 1.6, 66.2, 1.6), (box.MinX, box.MinY, box.MinZ, box.MaxX, box.MaxY, box.MaxZ));
    }

    [Fact]
    public void Shields_block_from_either_hand_after_5_ticks_and_an_axe_disables_them()
    {
        var sword = new ItemStack((short)Items.DiamondSword, 1, 0);
        var shield = new ItemStack(Release1122CombatRules.ShieldId, 1, 0);
        Assert.True(R.CanBlock(sword, shield));
        Assert.Equal(1, R.BlockHand(sword, shield));
        Assert.True(R.CanBlock(shield, ItemStack.Empty));
        Assert.Equal(0, R.BlockHand(shield, ItemStack.Empty));
        Assert.False(R.CanBlock(sword, ItemStack.Empty));                    // 1.9+: a sword doesn't block
        Assert.Equal(5, R.BlockRaiseTicks);
        Assert.Equal(0, R.BlockedDamage(13));
        Assert.True(R.DisablesShield(Items.IronAxe));
        Assert.True(R.DisablesShield(Items.WoodenAxe));
        Assert.False(R.DisablesShield(Items.IronSword));
        Assert.Equal(100, R.ShieldDisableTicks);
        Assert.True(R.SavesFromDeath(Release1122CombatRules.TotemId));
        // b1.8.1 unchanged: the sword blocks, halving.
        ICombatRules b = Beta181CombatRules.Instance;
        Assert.True(b.CanBlock(sword, ItemStack.Empty));
        Assert.Equal(4, b.BlockedDamage(7));
        Assert.False(b.DisablesShield(Items.IronAxe));
    }

    [Fact]
    public void Knockback_armor_and_difficulty()
    {
        // knockBack(attacker, 0.4F, …): motion halved, 0.4 away; vertical only on the ground (+0.4, capped at 0.4).
        var v = Release1122CombatRules.Knockback(Vec3d.Zero, new Vec3d(0, 64, 0), new Vec3d(-1, 64, 0), victimOnGround: true);
        Assert.Equal(0.4000000059604645, v.X, 7);
        Assert.Equal(0.4000000059604645, v.Y, 7);
        var air = Release1122CombatRules.Knockback(new Vec3d(0, -0.2, 0), new Vec3d(0, 64, 0), new Vec3d(0, 64, -1), victimOnGround: false);
        Assert.Equal(-0.2, air.Y, 7);
        Assert.Equal(0.4000000059604645, air.Z, 7);
        // getDamageAfterAbsorb: full diamond (20 armor, toughness 8): f = 4, armor' = clamp(20 − 2.5, 4, 20) → 10 × 0.3 = 3.
        var diamond = new[] { Items.DiamondHelmet, Items.DiamondChestplate, Items.DiamondLeggings, Items.DiamondBoots }.Select(i => new ItemStack((short)i, 1, 0)).ToList();
        Assert.Equal(3.0, R.DamageAfterArmor(10, diamond), 5);
        var iron = new[] { Items.IronHelmet, Items.IronChestplate, Items.IronLeggings, Items.IronBoots }.Select(i => new ItemStack((short)i, 1, 0)).ToList();
        Assert.Equal(6.0, R.DamageAfterArmor(10, iron), 5);                  // 15 armor: clamp(15 − 5, 3, 20) = 10
        Assert.Equal((1, 3, 2, 1), (Release1122CombatRules.ArmorOf(Items.LeatherHelmet).Points, Release1122CombatRules.ArmorOf(Items.LeatherChestplate).Points,
            Release1122CombatRules.ArmorOf(Items.LeatherLeggings).Points, Release1122CombatRules.ArmorOf(Items.LeatherBoots).Points));
        // EntityPlayer.attackEntityFrom: easy min(d/2 + 1, d), normal d, hard ×1.5.
        Assert.Equal(2.5, R.MonsterDamageToPlayer(3, 1));
        Assert.Equal(1, R.MonsterDamageToPlayer(1, 1));
        Assert.Equal(3, R.MonsterDamageToPlayer(3, 2));
        Assert.Equal(4.5, R.MonsterDamageToPlayer(3, 3));
        Assert.Equal(0, R.MonsterDamageToPlayer(3, 0));
    }

    [Fact]
    public void Mob_profiles_from_the_source_and_a_safe_default()
    {
        var rules = Release1122EntityRules.Instance;
        Assert.Equal((0.6, 1.95, 20, 3), (rules.Mob(MobTypes.Husk).Width, rules.Mob(MobTypes.Husk).Height, rules.Mob(MobTypes.Husk).MaxHealth, rules.Mob(MobTypes.Husk).MeleeDamage));
        Assert.Equal((40, 7), (rules.Mob(MobTypes.Enderman).MaxHealth, rules.Mob(MobTypes.Enderman).MeleeDamage));
        Assert.Equal((16, 1.4, 0.9), (rules.Mob(MobTypes.Spider).MaxHealth, rules.Mob(MobTypes.Spider).Width, rules.Mob(MobTypes.Spider).Height));
        Assert.Equal(43, rules.Mob(MobTypes.Creeper).MeleeDamage);            // (1 + 1)/2 · 7 · 6 + 1
        Assert.Equal(1.7, rules.Mob(MobTypes.Creeper).Height);
        Assert.Equal(MobTactic.Ranged, rules.Mob(MobTypes.Stray).Tactic);
        Assert.Equal(MobTactic.Ranged, rules.Mob(MobTypes.Witch).Tactic);
        Assert.Equal(13, rules.Mob(MobTypes.Vindicator).MeleeDamage);          // 5 + the iron axe's 8
        Assert.Equal(MobTactic.Ignore, rules.Mob(MobTypes.Shulker).Tactic);    // the End only
        var unknown = rules.Mob(250);
        Assert.Equal((MobTactic.Ignore, false), (unknown.Tactic, unknown.Hostile));
        // Stamped on spawn; babies are half-size; b1.8.1 entities keep the old tables.
        var t = new EntityTracker { Rules = rules };
        var husk = t.SpawnMob(1, MobTypes.Husk, new Vec3d(0, 64, 0), 0, 0, new Dictionary<int, object> { [EntityMeta.Baby] = (sbyte)1 });
        Assert.Equal("husk", husk.DisplayName);
        Assert.True(CombatRules.IsThreat(husk));
        var box = CombatRules.Hitbox(husk);
        Assert.Equal((0.3, 0.975), (Math.Round(box.MaxX - box.MinX, 3), Math.Round(box.MaxY - box.MinY, 3)));
        var zombieB = new EntityTracker().SpawnMob(2, MobTypes.Zombie, new Vec3d(0, 64, 0), 0, 0);
        Assert.Null(zombieB.Profile);
        Assert.Equal(1.8, CombatRules.Hitbox(zombieB).MaxY - 64, 6);
        // A screaming enderman is a threat; a quiet one isn't.
        var ender = t.SpawnMob(3, MobTypes.Enderman, Vec3d.Zero, 0, 0);
        Assert.True(CombatRules.IsThreat(ender));                              // hostile profile (the scan still waits for a provocation)
        Assert.True(CombatRules.IsThreat(ender with { Metadata = new Dictionary<int, object> { [EntityMeta.Screaming] = (sbyte)1 } }));
        Assert.Equal(5, CombatRules.MeleeDamageOf(t.SpawnMob(4, MobTypes.MagmaCube, Vec3d.Zero, 0, 0, new Dictionary<int, object> { [16] = 3 }))); // size + 2
    }
}

public class Release1122HungerTests
{
    [Fact]
    public void Fast_regen_at_20_with_saturation_heals_sat_over_6_every_10_ticks()
    {
        var f = new FoodStats1122 { Food = 20, Saturation = 5, Health = 10 };
        for (int i = 0; i < 9; i++) f.Tick();
        Assert.Equal(10f, f.Health);
        f.Tick();
        Assert.Equal(10f + 5f / 6f, f.Health, 5);
        Assert.Equal(5f, f.Exhaustion);
        f.Tick();                                                             // exhaustion 5 > 4: one saturation
        Assert.Equal((4f, 1f), (f.Saturation, f.Exhaustion));
        // Saturation 6+ heals a whole hp per 10 ticks.
        var g = new FoodStats1122 { Food = 20, Saturation = 12, Health = 10 };
        for (int i = 0; i < 10; i++) g.Tick();
        Assert.Equal(11f, g.Health, 5);
    }

    [Fact]
    public void Slow_regen_at_18_costs_6_exhaustion_then_food()
    {
        var f = new FoodStats1122 { Food = 18, Saturation = 0, Health = 10 };
        for (int i = 0; i < 79; i++) f.Tick();
        Assert.Equal(10f, f.Health);
        f.Tick();
        Assert.Equal((11f, 6f), (f.Health, f.Exhaustion));
        f.Tick();                                                             // no saturation: the food pays
        Assert.Equal((17, 2f), (f.Food, f.Exhaustion));
        for (int i = 0; i < 200; i++) f.Tick();
        Assert.Equal(11f, f.Health);                                          // food 17: no regeneration
        // Full health: no regen and no cost.
        var full = new FoodStats1122 { Food = 20, Saturation = 5, Health = 20 };
        for (int i = 0; i < 100; i++) full.Tick();
        Assert.Equal((20f, 0f), (full.Health, full.Exhaustion));
    }

    [Theory]
    [InlineData(1, 10f)]
    [InlineData(2, 1f)]
    [InlineData(3, 0f)]
    public void Starvation_floor_by_difficulty(int difficulty, float floor)
    {
        var f = new FoodStats1122 { Food = 0, Saturation = 0, Health = 12, Difficulty = difficulty };
        for (int i = 0; i < 80 * 20; i++) f.Tick();
        Assert.Equal(floor, Math.Max(0, f.Health));
        Assert.Equal((int)floor, Release1122HungerRules.Instance.StarvationFloor(difficulty));
    }

    [Fact]
    public void Exhaustion_values_and_the_regen_facts()
    {
        IHungerRules r = Release1122HungerRules.Instance, b = Beta181HungerRules.Instance;
        Assert.Equal((0.05f, 0.2f, 0.1f, 0.1f, 0.005f, 0.1f, 0.01f, 0f),
            (r.Exhaustion(ExhaustionCause.Jump), r.Exhaustion(ExhaustionCause.SprintJump), r.Exhaustion(ExhaustionCause.Attack),
             r.Exhaustion(ExhaustionCause.Damage), r.Exhaustion(ExhaustionCause.BlockBroken), r.Exhaustion(ExhaustionCause.SprintMeter),
             r.Exhaustion(ExhaustionCause.SwimMeter), r.Exhaustion(ExhaustionCause.WalkMeter)));
        Assert.Equal((0.2f, 0.8f, 0.3f), (b.Exhaustion(ExhaustionCause.Jump), b.Exhaustion(ExhaustionCause.SprintJump), b.Exhaustion(ExhaustionCause.Attack)));
        Assert.Equal((20, 0), (r.FastRegenFood, b.FastRegenFood));
        Assert.Equal(120, r.RegenPerMinute(20, 6));
        Assert.Equal(15, r.RegenPerMinute(18, 0));
        Assert.Equal(15, b.RegenPerMinute(20, 6));
        Assert.True(r.WantsTopUp(12, 19, 0));
        Assert.False(r.WantsTopUp(16, 19, 0));
        Assert.False(b.WantsTopUp(12, 19, 0));
        // The survival reflex eats a top-up meal on 1.12.2 only (bread: 5 hunger, waste 4 at food 19 → only when hurt ≤ 14).
        var slots = Enumerable.Repeat(ItemStack.Empty, 45).ToArray();
        slots[36] = new ItemStack((short)Items.Apple, 3, 0);
        var o = new SurvivalOptions();
        Assert.NotNull(SurvivalReflex.EatNow(slots, new HashSet<int>(), 12, 19, o, r, 0));
        Assert.Null(SurvivalReflex.EatNow(slots, new HashSet<int>(), 12, 19, o, b, 0));
        Assert.Null(SurvivalReflex.EatNow(slots, new HashSet<int>(), 12, 19, o));
    }

    [Fact]
    public void Poison_and_wither_are_survival_facts()
    {
        var now = DateTime.UtcNow;
        var fx = new Dictionary<int, StatusEffect>
        {
            [PotionEffects.Poison] = new(PotionEffects.Poison, 0, 140, now),
            [PotionEffects.FireResistance] = new(PotionEffects.FireResistance, 0, 3600, now),
        };
        Assert.Equal("poisoned 7s (stops at 1 hp), fire resistant 180s", SurvivalReflex.EffectFacts(fx));
        var o = new SurvivalOptions();
        Assert.Contains("poisoned", SurvivalReflex.GateReason("fight", o.GateMinHealth + 2, 20, true, o, effects: fx));
        Assert.Null(SurvivalReflex.GateReason("fight", 20, 20, true, o, effects: fx));
        fx[PotionEffects.Wither] = new(PotionEffects.Wither, 1, 200, now);
        Assert.Contains("withering", SurvivalReflex.GateReason("fight", o.GateMinHealth + 5, 20, true, o, effects: fx));
        Assert.Null(SurvivalReflex.GateReason("mine 3 blocks", 8, 20, true, o, effects: fx)); // only fights are gated
        Assert.EndsWith("withering 10s (can kill), fire resistant 180s", SurvivalReflex.HeaderText(14, 18, Enumerable.Repeat(ItemStack.Empty, 45).ToArray(), new HashSet<int>(), null, fx));
        Assert.Equal("", SurvivalReflex.EffectFacts(null));
    }
}

/// <summary>The combat engine on the 1.12.2 rules: it waits for the charge, swings early only in an emergency, never sweeps into a
/// bystander, and raises the off-hand shield (not against an axe).</summary>
public class Release1122CombatEngineTests
{
    private static CombatRig Rig(int held, ItemStack offhand = default, CombatOptions? o = null)
    {
        var w = new WorldState(height: 256, inventory: InventoryModel.Release1122);
        w.Entities.Rules = Release1122EntityRules.Instance;
        var rig = new CombatRig(o, world: w);
        rig.E.Rules = Release1122CombatRules.Instance;
        w.Inventory.SetSlot(0, 36, new ItemStack((short)held, 1, 0));
        if (!offhand.IsEmpty) w.Inventory.SetSlot(0, InventoryModel.OffhandSlot, offhand);
        return rig;
    }

    private static List<long> SwingTicks(CombatRig rig, int ticks)
    {
        var at = new List<long>();
        for (int i = 0; i < ticks; i++)
            if (rig.Step().AttackId >= 0) at.Add(rig.Tick - 1);
        return at;
    }

    [Fact]
    public void Waits_for_the_charge_of_the_held_weapon()
    {
        var rig = Rig(Items.WoodenAxe);
        rig.Mob(1, MobTypes.Zombie, 1.9, 0.5);
        var swings = SwingTicks(rig, 120);
        Assert.True(swings.Count >= 3, $"swings at {string.Join(",", swings)}");
        Assert.All(swings.Zip(swings.Skip(1), (a, b) => b - a), gap => Assert.True(gap >= 23, $"gap {gap}: a wooden axe charges in 23 ticks"));
        var charges = rig.Json("swing").Select(j => j.GetProperty("charge").GetDouble()).ToList();
        Assert.All(charges.Skip(1), c => Assert.True(c >= 0.9, $"charge {c}"));
        // b1.8.1: the same fight clicks every 11 ticks (one full hit per invulnerability window).
        var old = new CombatRig();
        old.W.Inventory.SetSlot(0, 36, new ItemStack((short)Items.WoodenAxe, 1, 0));
        old.Mob(1, MobTypes.Zombie, 1.9, 0.5);
        var oldSwings = SwingTicks(old, 120);
        Assert.Contains(oldSwings.Zip(oldSwings.Skip(1), (a, b) => b - a), g => g < 15);
    }

    [Fact]
    public void Ticks_that_ran_ahead_of_real_time_do_not_count_toward_the_charge()
    {
        // The tick loop catches up in bursts (Windows sleeps in ~15.6 ms steps), but the server's ticksSinceLastSwing follows
        // real time: 120 of our ticks in a burst are not 120 server ticks (the normal-world scenario's swings on our count landed at 0.75).
        var rig = Rig(Items.WoodenPickaxe);
        rig.E.TickInterval = TimeSpan.FromSeconds(10);
        rig.Mob(1, MobTypes.Zombie, 1.9, 0.5);
        var swings = SwingTicks(rig, 120);
        Assert.Single(swings); // the charged first one; the next waits for 15 server ticks of real time
        // Without the tick length (ticks only) the same burst swings every 15 ticks.
        var ticksOnly = Rig(Items.WoodenPickaxe);
        ticksOnly.Mob(1, MobTypes.Zombie, 1.9, 0.5);
        Assert.True(SwingTicks(ticksOnly, 120).Count >= 3);
    }

    [Fact]
    public void An_emergency_swings_at_any_charge()
    {
        var rig = Rig(Items.WoodenAxe);
        rig.W.Player.SetHealth(7, 20, 0); // above the disengage line (6), at or below the emergency line (8)
        rig.Mob(1, MobTypes.Zombie, 1.9, 0.5);
        var swings = SwingTicks(rig, 60);
        Assert.Contains(swings.Zip(swings.Skip(1), (a, b) => b - a), g => g < 20);
    }

    [Fact]
    public void An_own_arm_swing_or_a_weapon_switch_resets_the_charge()
    {
        var rig = Rig(Items.DiamondSword);
        rig.Mob(1, MobTypes.Zombie, 1.9, 0.5);
        Assert.Single(SwingTicks(rig, 1));                                    // charged from the start
        for (int i = 0; i < 12; i++) rig.Step();
        rig.E.NoteOwnSwing(rig.Tick);                                          // a dig swing the server saw
        var after = SwingTicks(rig, 30);
        Assert.True(after[0] - (rig.Tick - 30) >= 10, "the dig swing reset the charge");
    }

    [Fact]
    public void Never_sweeps_into_a_player_next_to_the_target()
    {
        var rig = Rig(Items.DiamondSword);
        rig.Mob(1, MobTypes.Zombie, 1.9, 0.5);
        rig.Player(2, "PoofImaFox", 2.4, 1.2);
        int swings = 0;
        bool jumped = false;
        for (int i = 0; i < 40; i++)
        {
            var s = rig.Step();
            if (s.AttackId >= 0) swings++;
            jumped |= s.Input.Jump;
        }
        Assert.Equal(0, swings);                                              // on the ground a charged sword hit would sweep
        Assert.True(jumped, "it jumps for a crit instead (a crit never sweeps)");
        Assert.Contains(rig.Json("hold_fire"), j => j.GetProperty("why").GetString()!.Contains("PoofImaFox"));
        // Airborne and falling: a crit, no sweep: it swings.
        rig.P.OnGround = false;
        rig.P.Velocity = new Vec3d(0, -0.2, 0);
        rig.P.FallDistance = 0.5;
        Assert.Contains(Enumerable.Range(0, 5).Select(_ => rig.Step()), s => s.AttackId == 1);
        Assert.True(rig.Json("swing").Last().GetProperty("crit").GetBoolean());
    }

    [Fact]
    public void Raises_the_offhand_shield_while_charging_but_not_against_an_axe()
    {
        var shield = new ItemStack(Release1122CombatRules.ShieldId, 1, 0);
        var rig = Rig(Items.DiamondSword, shield);
        rig.Mob(1, MobTypes.Zombie, 1.9, 0.5);
        var steps = Enumerable.Range(0, 12).Select(_ => rig.Step()).ToList();
        Assert.True(steps[0].AttackId == 1 && !steps[0].Block);                // the swing lowers the shield
        Assert.Contains(steps.Skip(1), s => s.Block);                         // then it goes up while the sword charges
        // A vindicator with an iron axe would disable it: no block.
        var rig2 = Rig(Items.DiamondSword, shield);
        rig2.Mob(1, MobTypes.Vindicator, 1.9, 0.5);
        rig2.W.Entities.SetEquipment(1, 0, new ItemStack((short)Items.IronAxe, 1, 0));
        Assert.DoesNotContain(Enumerable.Range(0, 12).Select(_ => rig2.Step()), s => s.Block);
        // A disabled shield (its cooldown) stays down too; no shield = no block (a 1.9 sword doesn't block).
        var rig3 = Rig(Items.DiamondSword, shield);
        rig3.E.ShieldDisabled = () => true;
        rig3.Mob(1, MobTypes.Zombie, 1.9, 0.5);
        Assert.DoesNotContain(Enumerable.Range(0, 12).Select(_ => rig3.Step()), s => s.Block);
        var rig4 = Rig(Items.DiamondSword);
        rig4.Mob(1, MobTypes.Zombie, 1.9, 0.5);
        Assert.DoesNotContain(Enumerable.Range(0, 12).Select(_ => rig4.Step()), s => s.Block);
    }
}
