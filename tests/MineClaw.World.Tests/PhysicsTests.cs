using MineClaw.World;
using static MineClaw.World.Tests.TestBlocks;

namespace MineClaw.World.Tests;

public class PhysicsTests
{
    private const float East = -90f; // yaw that walks toward +X

    private static WorldState Floor()
    {
        var w = TestWorld.Create();
        w.Fill(-10, 63, -10, 10, 63, 10, Stone);
        return w;
    }

    [Fact]
    public void Standing_StaysPut()
    {
        var w = Floor();
        var s = TestWorld.Standing(0.5, 64, 0.5);
        TestWorld.Run(w, s, MoveInput.Idle(0, 0), 40);
        Assert.Equal(64, s.Position.Y, 9);
        Assert.True(s.OnGround);
        Assert.Equal(0.5, s.Position.X, 9);
    }

    [Fact]
    public void Fall_LandsOnFloor_AndTracksFallDistance()
    {
        var w = Floor();
        var s = new PhysicsState();
        s.Teleport(new Vec3d(0.5, 74, 0.5), 0, 0);
        double maxFall = 0;
        int ticks = 0;
        while (!s.OnGround && ticks++ < 200)
        {
            PlayerPhysics.Tick(w, s, MoveInput.Idle(0, 0));
            maxFall = Math.Max(maxFall, s.FallDistance);
        }
        Assert.True(s.OnGround);
        Assert.Equal(64, s.Position.Y, 9);
        Assert.InRange(maxFall, 9.0, 10.0001);
        Assert.Equal(0, s.FallDistance);
        Assert.InRange(ticks, 16, 19); // ~17-18 ticks for 10 blocks
    }

    [Fact]
    public void Jump_ReachesVanillaApex_AndLands()
    {
        var w = Floor();
        var s = TestWorld.Standing(0.5, 64, 0.5);
        PlayerPhysics.Tick(w, s, new MoveInput(Jump: true));
        double apex = s.Position.Y;
        for (int i = 0; i < 30; i++)
        {
            PlayerPhysics.Tick(w, s, MoveInput.Idle(0, 0));
            apex = Math.Max(apex, s.Position.Y);
        }
        Assert.InRange(apex - 64, 1.24, 1.26); // vanilla: 1.2522
        Assert.True(s.OnGround);
        Assert.Equal(64, s.Position.Y, 9);
    }

    [Fact]
    public void Walk_And_Sprint_TerminalSpeeds()
    {
        var w = Floor();
        var s = TestWorld.Standing(-8.5, 64, 0.5);
        TestWorld.Run(w, s, new MoveInput(Forward: 1, Yaw: East), 20);
        double x0 = s.Position.X;
        PlayerPhysics.Tick(w, s, new MoveInput(Forward: 1, Yaw: East));
        double walk = s.Position.X - x0;
        Assert.InRange(walk, 0.213, 0.218); // 4.317 m/s

        var s2 = TestWorld.Standing(-8.5, 64, 0.5);
        TestWorld.Run(w, s2, new MoveInput(Forward: 1, Sprint: true, Yaw: East), 20);
        x0 = s2.Position.X;
        PlayerPhysics.Tick(w, s2, new MoveInput(Forward: 1, Sprint: true, Yaw: East));
        Assert.True(s2.Sprinting);
        Assert.InRange(s2.Position.X - x0, 0.277, 0.284); // 5.612 m/s
        Assert.Equal(0.5, s2.Position.Z, 6);
    }

    [Fact]
    public void Walking_StepsUpSlab_ButNotFullBlock()
    {
        var w = Floor();
        w.Fill(2, 64, 0, 9, 64, 0, Slab);
        var s = TestWorld.Standing(0.5, 64, 0.5);
        TestWorld.Run(w, s, new MoveInput(Forward: 1, Yaw: East), 20);
        Assert.True(s.Position.X > 2.5);
        Assert.Equal(64.5, s.Position.Y, 6);

        var w2 = Floor();
        w2.Set(2, 64, 0, Stone);
        var s2 = TestWorld.Standing(0.5, 64, 0.5);
        TestWorld.Run(w2, s2, new MoveInput(Forward: 1, Yaw: East), 20);
        Assert.Equal(64, s2.Position.Y, 6);
        Assert.Equal(1.7, s2.Position.X, 6); // stopped flush against the block (2 - 0.3)
        Assert.True(s2.CollidedHorizontally);
    }

    [Fact]
    public void JumpingWhileWalking_ClimbsOneBlock()
    {
        var w = Floor();
        w.Fill(2, 64, -1, 4, 64, 1, Stone);
        var s = TestWorld.Standing(0.5, 64, 0.5);
        for (int i = 0; i < 30; i++)
            PlayerPhysics.Tick(w, s, new MoveInput(Forward: 1, Jump: true, Yaw: East));
        TestWorld.Run(w, s, MoveInput.Idle(East, 0), 20);
        Assert.Equal(65, s.Position.Y, 6);
        Assert.True(s.Position.X > 2);
    }

    [Fact]
    public void Sneaking_DoesNotWalkOffEdge()
    {
        // 1x1 pillar over the void.
        var w = TestWorld.Create();
        w.Set(0, 63, 0, Cobble);
        var s = TestWorld.Standing(0.5, 64, 0.5);
        TestWorld.Run(w, s, new MoveInput(Forward: 1, Sneak: true, Yaw: East), 100);
        Assert.Equal(64, s.Position.Y, 9);
        Assert.True(s.OnGround);
        Assert.InRange(s.Position.X, 1.0, 1.3); // hangs over the edge but stays supported
        // Diagonal too.
        TestWorld.Run(w, s, new MoveInput(Forward: 1, Sneak: true, Yaw: -45), 100);
        Assert.Equal(64, s.Position.Y, 9);
        Assert.False(w.IsPlayerOverVoid(s.Position));

        // Without sneaking you fall.
        var s2 = TestWorld.Standing(0.5, 64, 0.5);
        TestWorld.Run(w, s2, new MoveInput(Forward: 1, Yaw: East), 40);
        Assert.True(s2.Position.Y < 60);
    }

    [Fact]
    public void Sneaking_StillAllowsDropsWithinOneBlockBelow_IsBlocked()
    {
        // Edge protection also blocks walking off a 1-block ledge (nothing within 1 block below the hitbox).
        var w = Floor();
        w.Fill(-10, 64, -10, 1, 64, 10, Stone); // raised platform x ≤ 1, ledge at x = 2
        var s = TestWorld.Standing(0.5, 65, 0.5);
        TestWorld.Run(w, s, new MoveInput(Forward: 1, Sneak: true, Yaw: East), 60);
        Assert.Equal(65, s.Position.Y, 9);
    }

    [Fact]
    public void Water_SinksSlowly_AndJumpFloats()
    {
        var w = Floor();
        w.Fill(-3, 64, -3, 3, 70, 3, StillWater);
        var s = new PhysicsState();
        s.Teleport(new Vec3d(0.5, 68, 0.5), 0, 0);
        TestWorld.Run(w, s, MoveInput.Idle(0, 0), 5);
        Assert.True(s.InWater);
        double y0 = s.Position.Y;
        TestWorld.Run(w, s, MoveInput.Idle(0, 0), 10);
        Assert.InRange(y0 - s.Position.Y, 0.2, 1.5); // terminal ≈ 0.1/tick
        double y1 = s.Position.Y;
        TestWorld.Run(w, s, new MoveInput(Jump: true), 20);
        Assert.True(s.Position.Y > y1);
    }

    [Fact]
    public void Ladder_ClimbsWhenPushingIntoIt()
    {
        var w = Floor();
        w.Fill(0, 64, 1, 0, 70, 1, Stone);     // wall at z = 1
        w.Fill(0, 64, 0, 0, 69, 0, Ladder, 2); // ladder meta 2: attached to the block at +Z
        var s = TestWorld.Standing(0.5, 64, 0.5);
        TestWorld.Run(w, s, new MoveInput(Forward: 1, Yaw: 0), 30); // yaw 0 = +Z, into the wall
        Assert.True(s.Position.Y > 67, $"y={s.Position.Y}");
        // Letting go: descends at most 0.15/tick.
        double y = s.Position.Y;
        PlayerPhysics.Tick(w, s, MoveInput.Idle(0, 0));
        PlayerPhysics.Tick(w, s, MoveInput.Idle(0, 0));
        Assert.True(y - s.Position.Y <= 0.31);
        // Sneaking holds position.
        TestWorld.Run(w, s, new MoveInput(Sneak: true), 5);
        y = s.Position.Y;
        TestWorld.Run(w, s, new MoveInput(Sneak: true), 10);
        Assert.Equal(y, s.Position.Y, 6);
    }

    [Fact]
    public void Unloaded_FreezesPhysics()
    {
        var w = TestWorld.Create();
        var s = new PhysicsState();
        s.Teleport(new Vec3d(1000.5, 80, 0.5), 0, 0);
        PlayerPhysics.Tick(w, s, MoveInput.Idle(0, 0));
        Assert.True(s.Frozen);
        Assert.Equal(80, s.Position.Y);
    }

    [Fact]
    public void Fence_IsOneAndAHalfHigh()
    {
        var w = Floor();
        w.Set(0, 64, 0, Fence);
        var s = new PhysicsState();
        s.Teleport(new Vec3d(0.5, 67, 0.5), 0, 0);
        TestWorld.Run(w, s, MoveInput.Idle(0, 0), 40);
        Assert.Equal(65.5, s.Position.Y, 6);
    }
}
