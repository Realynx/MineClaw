using MineClaw.Data;
using MineClaw.World.Release1122;
using MineClaw.World.Versioning;

namespace MineClaw.World.Tests;

/// <summary>Multi-version P4 in the world model: the tracker's version stamps and new facts, and potion effects in the 1.12.2 tick.</summary>
public class Entities1122Tests
{
    [Fact]
    public void Tracker_stamps_the_versions_facts_and_keeps_new_entity_facts()
    {
        var t = new EntityTracker { Rules = Release1122EntityRules.Instance };
        var w = t.SpawnMob(1, MobTypes.Witch, new Vec3d(0, 64, 0), 0, 0);
        Assert.Equal(("witch", 26, true), (w.DisplayName, w.Profile!.MaxHealth, w.IsHostileMob));
        Assert.Equal("item_frame", t.SpawnObject(2, 71, Vec3d.Zero).DisplayName);   // 1.12.2's 71 (b1.8.1: falling gravel)
        Assert.Equal("falling_gravel", new EntityTracker().SpawnObject(2, 71, Vec3d.Zero).DisplayName);
        Assert.False(new EntityTracker().SpawnMob(3, 250, Vec3d.Zero, 0, 0).IsHostileMob);
        Assert.False(t.SpawnMob(3, 250, Vec3d.Zero, 0, 0).IsHostileMob);        // unknown 1.12.2 type: the safe default

        // Off hand (slot 5), head yaw, effects with expiry, attributes, metadata facts.
        Assert.True(t.SetEquipment(1, 5, new ItemStack(442, 1, 0)));
        Assert.Equal(442, t.Get(1)!.Offhand.Id);
        Assert.True(t.SetHeadYaw(1, 45f));
        Assert.Equal(45f, t.Get(1)!.HeadYaw);
        var long_ago = DateTime.UtcNow.AddSeconds(-10);
        t.SetEffect(1, new StatusEffect(PotionEffects.Speed, 1, 600, DateTime.UtcNow));
        t.SetEffect(1, new StatusEffect(PotionEffects.Poison, 0, 100, long_ago));     // 5 s, received 10 s ago: over
        Assert.Equal(1, t.Get(1)!.EffectAmplifier(PotionEffects.Speed));
        Assert.Equal(-1, t.Get(1)!.EffectAmplifier(PotionEffects.Poison));
        t.RemoveEffect(1, PotionEffects.Speed);
        Assert.Equal(-1, t.Get(1)!.EffectAmplifier(PotionEffects.Speed));
        t.SetAttributes(1, [new("generic.movementSpeed", 0.25)]);
        Assert.Equal(0.25, t.Get(1)!.Attributes!["generic.movementSpeed"]);
        t.SetMetadata(1, new Dictionary<int, object> { [EntityMeta.Health] = 13.5f, [EntityMeta.Flags] = (sbyte)EntityMeta.FlagUsingItem });
        Assert.Equal(13.5f, t.Get(1)!.Health);
        Assert.True(t.Get(1)!.IsUsingItem);
        Assert.Equal(int.MaxValue, new StatusEffect(PotionEffects.NightVision, 0, short.MaxValue, long_ago).RemainingTicks(DateTime.UtcNow));
    }

    [Fact]
    public void An_arrow_that_stops_or_loses_its_critical_flag_is_in_the_ground()
    {
        var t = new EntityTracker { Rules = Release1122EntityRules.Instance };
        var a = t.SpawnObject(5, 60, new Vec3d(0, 66, 0), new Vec3d(1, 0, 0));
        Assert.True(a.IsArrow);
        t.SetMetadata(5, new Dictionary<int, object> { [EntityMeta.ArrowCritical] = (sbyte)1 });
        t.MoveRelative(5, 1.5, -0.2, 0);
        Assert.False(t.Get(5)!.InGround);
        t.SetMetadata(5, new Dictionary<int, object> { [EntityMeta.ArrowCritical] = (sbyte)0 }); // onHit on a block
        Assert.True(t.Get(5)!.InGround);
        Assert.Equal(default, t.Get(5)!.Velocity);
        // A non-critical arrow: a teleport to where it already is.
        t.SpawnObject(6, 60, new Vec3d(2, 66, 2), new Vec3d(0, -1, 0));
        t.Teleport(6, new Vec3d(2, 65, 2), 0, 0);
        Assert.False(t.Get(6)!.InGround);
        t.Teleport(6, new Vec3d(2, 65, 2), 0, 0);
        Assert.True(t.Get(6)!.InGround);
        Assert.False(t.SpawnObject(7, 71, Vec3d.Zero).IsArrow);                  // an item frame
    }

    [Fact]
    public void Player_effects_clear_on_respawn()
    {
        var p = new PlayerState();
        p.SetEffect(new StatusEffect(PotionEffects.Haste, 1, 600, DateTime.UtcNow));
        Assert.True(p.HasEffect(PotionEffects.Haste));
        p.Respawned(0);
        Assert.False(p.HasEffect(PotionEffects.Haste));
    }

    private static (WorldState W, PhysicsState S) Floor()
    {
        var w = Physics1122Tests.World();
        w.Fill(-20, 63, -20, 20, 63, 20, 1);
        var s = new PhysicsState();
        s.Teleport(new Vec3d(-15.5, 64, 0.5), -90f, 0);
        s.OnGround = true;
        return (w, s);
    }

    [Theory]
    [InlineData(-1, -1, 1.0)]
    [InlineData(1, -1, 1.4)]   // speed II: × (1 + 0.2·2)
    [InlineData(-1, 0, 0.85)]  // slowness I: × (1 − 0.15)
    public void Speed_and_slowness_scale_the_walk(int speed, int slowness, double factor)
    {
        var (w, s) = Floor();
        s.SpeedAmplifier = speed;
        s.SlownessAmplifier = slowness;
        var input = new MoveInput(Forward: 1, Yaw: -90f);
        for (int i = 0; i < 60; i++) PlayerPhysics.Tick(w, s, input);
        double x0 = s.Position.X;
        PlayerPhysics.Tick(w, s, input);
        // Terminal ground step a/(1 − f) with a = 0.98 · speed · 0.16277136/f³ (f = 0.6·0.91): linear in the attribute.
        Assert.Equal(0.215859 * factor, s.Position.X - x0, 3);
        Assert.Equal((float)(Physics1122.WalkSpeedBase * (1 + 0.20000000298023224 * 2)), Physics1122.LandSpeed(false, 1, -1));
    }

    [Fact]
    public void Jump_boost_adds_a_tenth_per_level()
    {
        var (w, s) = Floor();
        s.JumpBoostAmplifier = 1; // jump boost II: 0.42 + 0.2
        PlayerPhysics.Tick(w, s, new MoveInput(Jump: true, Yaw: -90f));
        double y1 = s.Position.Y - 64;
        Assert.Equal(0.42f + 2 * 0.1f, y1, 5);                         // the first tick moves the full jump motion
        double apex = 0;
        for (int i = 0; i < 20; i++) { PlayerPhysics.Tick(w, s, new MoveInput(Yaw: -90f)); apex = Math.Max(apex, s.Position.Y - 64); }
        Assert.True(apex > 1.9, $"apex {apex}");                       // jump boost II clears two blocks
    }
}
