using MineClaw.Client.Combat;
using MineClaw.Client.Survival;
using MineClaw.World;

namespace MineClaw.Client.Tests;

/// <summary>Regression tests for the mc-dev (b1.8) mechanics audit — docs/mechanics-audit.md.</summary>
public class MechanicsAuditTests
{
    [Fact]
    public void Crits_are_deterministic_and_need_a_fall()
    {
        Assert.Equal(7, CombatRules.MaxCritDamage(4));  // 4 * 3 / 2 + 1
        Assert.Equal(8, CombatRules.MaxCritDamage(5));
        Assert.Equal(2, CombatRules.MaxCritDamage(1));  // fist
        Assert.True(CombatRules.IsCritical(-0.1, onGround: false, onLadder: false, inWater: false));
        Assert.False(CombatRules.IsCritical(0.1, false, false, false));   // still rising
        Assert.False(CombatRules.IsCritical(-0.1, false, true, false));   // ladder
        Assert.False(CombatRules.IsCritical(-0.1, false, false, true));   // water
    }

    [Fact]
    public void Mob_profiles_match_mcdev()
    {
        Assert.Equal((20, 5), (CombatRules.Profile(MobTypes.Enderman).MaxHealth, CombatRules.Profile(MobTypes.Enderman).MeleeDamage));
        Assert.Equal(20, CombatRules.Profile(MobTypes.Spider).MaxHealth);
        Assert.Equal(20, CombatRules.Profile(MobTypes.CaveSpider).MaxHealth);
        Assert.Equal(10, CombatRules.Profile(MobTypes.Sheep).MaxHealth);
        Assert.Equal(0.7, CombatRules.Profile(MobTypes.Chicken).Height);
        Assert.Equal(5, CombatRules.Profile(MobTypes.Zombie).MeleeDamage);
        Assert.Equal(5, CombatRules.Profile(MobTypes.PigZombie).MeleeDamage);
        Assert.Equal(0, CombatRules.Profile(MobTypes.Silverfish).MeleeDamage);
        var bigSlime = new Entity(1, EntityKind.Mob, Vec3d.Zero, TypeId: MobTypes.Slime, Metadata: new Dictionary<int, object> { [16] = (sbyte)4 });
        var tinySlime = bigSlime with { Metadata = new Dictionary<int, object> { [16] = (sbyte)1 } };
        Assert.Equal(4, CombatRules.MeleeDamageOf(bigSlime));
        Assert.Equal(0, CombatRules.MeleeDamageOf(tinySlime));
    }

    [Fact]
    public void Monster_damage_scales_with_difficulty()
    {
        Assert.Equal(0, CombatRules.MonsterDamageToPlayer(5, 0));
        Assert.Equal(2, CombatRules.MonsterDamageToPlayer(5, 1)); // 5 / 3 + 1
        Assert.Equal(5, CombatRules.MonsterDamageToPlayer(5, 2));
        Assert.Equal(7, CombatRules.MonsterDamageToPlayer(5, 3)); // 5 * 3 / 2
        Assert.Equal(1, CombatRules.MonsterDamageToPlayer(0, 1)); // silverfish only hurts on easy
    }

    [Fact]
    public void Knockback_is_04_and_a_sprint_hit_adds_10()
    {
        var victim = new Vec3d(0, 64, 0);
        var attacker = new Vec3d(2, 64, 0); // east of the victim, facing west (yaw 90)
        var v = CombatRules.KnockbackVelocity(Vec3d.Zero, victim, attacker, sprintHit: false, attackerYaw: 90);
        Assert.Equal(-0.4, v.X, 9);
        Assert.Equal(0.4, v.Y, 9);
        Assert.Equal(0, v.Z, 9);
        var halved = CombatRules.KnockbackVelocity(new Vec3d(0.2, 0.6, 0), victim, attacker, false, 90);
        Assert.Equal(-0.3, halved.X, 9);   // existing motion halved first
        Assert.Equal(0.4, halved.Y, 9);    // capped
        var sprint = CombatRules.KnockbackVelocity(Vec3d.Zero, victim, attacker, sprintHit: true, attackerYaw: 90);
        Assert.Equal(-1.4, sprint.X, 6);
        Assert.Equal(0.5, sprint.Y, 9);

        double baseTravel = CombatRules.KnockbackTravel(0.4, 0.4);
        double sprintTravel = CombatRules.KnockbackTravel(1.4, 0.5);
        var o = new CombatOptions();
        Assert.InRange(baseTravel, 1.8, 2.4);
        Assert.True(o.KnockbackDepth > baseTravel);
        Assert.InRange(sprintTravel, 7.0, 8.5);
        Assert.True(o.SprintKnockbackDepth > sprintTravel);
    }

    [Fact]
    public void Creeper_explosion_reaches_six_blocks_and_hits_for_49()
    {
        Assert.Equal((49, 1.0), CombatRules.ExplosionImpact(3, 0));
        Assert.Equal(19, CombatRules.ExplosionImpact(3, 3).Damage);   // at the ignite range: still 19
        Assert.Equal((0, 0.0), CombatRules.ExplosionImpact(3, 6.01));
        Assert.True(CombatRules.CreeperCancelRange + 0.5 > 6);         // retreat target is outside the blast radius
    }

    [Fact]
    public void Starvation_floor_follows_FoodMetaData()
    {
        Assert.Equal(10, HungerRules.StarvationFloor(0)); // health > 10 still starves (food can't drop on peaceful)
        Assert.Equal(10, HungerRules.StarvationFloor(1));
        Assert.Equal(1, HungerRules.StarvationFloor(2));
        Assert.Equal(0, HungerRules.StarvationFloor(3));
    }

    [Fact]
    public void Sprint_flag_is_metadata_bit_3()
    {
        var e = new Entity(1, EntityKind.Player, Vec3d.Zero, Metadata: new Dictionary<int, object> { [0] = (sbyte)8 });
        Assert.True(e.IsSprinting);
        Assert.False(e.IsCrouched);
    }
}
