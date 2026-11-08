using MineClaw.Data;
using MineClaw.Data.Versioning;
using MineClaw.World;
using static MineClaw.World.Tests.TestBlocks;

namespace MineClaw.World.Tests;

public sealed class CarefulDropTests
{
    private static WorldState Stair(bool modern, int dx = 0, int dz = 1, int fall = 1, bool floor = true)
    {
        var w = GoldenWorlds.Empty(modern);
        if (floor) w.Fill(-4, 54, -4, 4, 54, 4, Stone);
        w.Set(0, 64 + fall - 1, 0, Stone);
        w.Set(dx, 63, dz, Cobble);
        return w;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_repaired_step_without_a_runout_is_a_careful_single_block_drop(bool modern)
    {
        using var data = GoldenWorlds.Data(modern);
        var w = Stair(modern);
        var path = Pathfinder.FindPath(w, new BlockPos(0, 65, 0), new GoalBlock(0, 64, 1), new PathOptions { AllowPartial = false });
        Assert.Equal(PathStatus.Found, path.Status);
        var step = Assert.Single(path.Steps);
        Assert.Equal(MoveKind.Drop, step.Kind);
        Assert.True(step.Gentle);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Void_lava_ice_and_taller_drops_still_require_a_safe_runout(bool modern)
    {
        using var data = GoldenWorlds.Data(modern);
        foreach (var danger in new[] { "void", "lava", "ice", "tall" })
        {
            int fall = danger == "tall" ? 2 : 1;
            var w = Stair(modern, fall: fall, floor: danger != "void");
            if (danger == "lava") w.Set(0, 55, 2, Lava);
            if (danger == "ice") w.Set(0, 63, 1, Ice);
            var path = Pathfinder.FindPath(w, new BlockPos(0, 64 + fall, 0), new GoalBlock(0, 64, 1), new PathOptions { AllowPartial = false });
            Assert.True(path.Status == PathStatus.NoPath, $"{danger}: {path.Status}");
        }
    }

    public static IEnumerable<object[]> Approaches()
    {
        foreach (bool modern in new[] { false, true })
            foreach (var (dx, dz) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                foreach (double speed in new[] { 0.0, 0.3, 0.5 })
                    yield return [modern, dx, dz, speed];
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_boosted_run_slows_before_the_careful_descent(bool modern)
    {
        using var data = GoldenWorlds.Data(modern);
        var w = Stair(modern);
        w.Fill(-4, 54, -8, 4, 54, -4, Stone);
        w.Fill(0, 64, -6, 0, 64, 0, Stone);
        var state = TestWorld.Standing(0.5, 65, -4.5);
        var path = Pathfinder.FindPath(w, state.Position, new GoalBlock(0, 64, 1), new PathOptions { AllowPartial = false });
        Assert.Equal(PathStatus.Found, path.Status);
        var follower = new PathFollower(path.Steps, new PathFollowerOptions { AllowSprint = true, Boosted = () => true });
        var guard = new EdgeGuard();
        bool boosted = false, stoppedBeforeDrop = false;
        FollowerOutput output = default;
        for (int tick = 0; tick < 250; tick++)
        {
            output = follower.Tick(w, state);
            double room = follower.BoostRoom(state);
            if (follower.CurrentStep is { Kind: MoveKind.Drop } && state.OnGround && state.Position.Y > 64.5)
            {
                Assert.Equal(0, room);
                stoppedBeforeDrop |= state.Velocity.HorizontalLength < 0.025;
            }
            var decision = guard.Filter(w, state, output.Input, follower.CurrentStep?.Kind == MoveKind.Drop);
            var before = state.Position;
            bool grounded = state.OnGround;
            PlayerPhysics.Tick(w, state, decision.Input);
            if (SpeedBoost.Plan(w, state, before, grounded, 3, room) is { } boost)
            {
                SpeedBoost.Apply(w, state, boost);
                boosted = true;
            }
            Assert.True(state.Position.Y >= 64 - 1e-6);
            Assert.False(decision.Unrecoverable);
            Assert.Equal(0, state.FallDamage);
            if (output.Status is FollowerStatus.Finished or FollowerStatus.Failed) break;
        }
        Assert.True(boosted);
        Assert.True(stoppedBeforeDrop);
        Assert.Equal(FollowerStatus.Finished, output.Status);
        Assert.True(state.OnGround);
        Assert.True(state.Velocity.HorizontalLength < 0.025);
    }

    [Theory]
    [MemberData(nameof(Approaches))]
    public void The_follower_brakes_before_the_edge_and_settles_on_the_repaired_step(bool modern, int dx, int dz, double incoming)
    {
        using var data = GoldenWorlds.Data(modern);
        var w = Stair(modern, dx, dz);
        var state = TestWorld.Standing(0.5 + dz * 0.12, 65, 0.5 + dx * 0.12);
        state.Velocity = new Vec3d(dx * incoming + dz * incoming / 2, 0, dz * incoming + dx * incoming / 2);
        var path = Pathfinder.FindPath(w, state.Position, new GoalBlock(dx, 64, dz), new PathOptions { AllowPartial = false });
        Assert.Equal(PathStatus.Found, path.Status);
        var follower = new PathFollower(path.Steps, new PathFollowerOptions { AllowSprint = true, Boosted = () => true });
        var guard = new EdgeGuard();
        FollowerOutput output = default;
        bool wasAirborne = false;
        for (int tick = 0; tick < 200; tick++)
        {
            output = follower.Tick(w, state);
            double room = follower.BoostRoom(state);
            Assert.Equal(0, room);
            var decision = guard.Filter(w, state, output.Input, follower.CurrentStep?.Kind == MoveKind.Drop);
            var before = state.Position;
            bool grounded = state.OnGround;
            PlayerPhysics.Tick(w, state, decision.Input);
            if (SpeedBoost.Plan(w, state, before, grounded, 3, room) is { } boost) SpeedBoost.Apply(w, state, boost);
            wasAirborne |= !state.OnGround;
            Assert.False(decision.Unrecoverable);
            Assert.True(state.Position.Y >= 64 - 1e-6, $"fell below landing at tick {tick}: {state.Position}");
            Assert.Equal(0, state.FallDamage);
            if (output.Status is FollowerStatus.Finished or FollowerStatus.Failed) break;
        }
        Assert.True(output.Status == FollowerStatus.Finished, $"{output.Status}: {output.Reason}; pos={state.Position}, velocity={state.Velocity}");
        Assert.True(wasAirborne);
        Assert.True(state.OnGround);
        Assert.InRange(state.Position.X, dx + 0.3, dx + 0.7);
        Assert.InRange(state.Position.Z, dz + 0.3, dz + 0.7);
        Assert.True(state.Velocity.HorizontalLength < 0.025);
        for (int tick = 0; tick < 20; tick++) PlayerPhysics.Tick(w, state, MoveInput.Idle(state.Yaw, state.Pitch));
        Assert.Equal(64, state.Position.Y, 6);
    }
}
