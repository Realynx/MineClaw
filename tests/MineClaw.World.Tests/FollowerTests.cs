using MineClaw.World;
using static MineClaw.World.Tests.TestBlocks;

namespace MineClaw.World.Tests;

/// <summary>End-to-end: pathfinder → follower → physics, with the test acting as the Client for dig/place requests.</summary>
public class FollowerTests
{
    private static (PhysicsState State, double MinY, FollowerOutput Last, int Ticks, int Places) Simulate(
        WorldState w, Vec3d start, Goal goal, PathOptions opts, int maxTicks = 1200)
    {
        var path = Pathfinder.FindPath(w, start, goal, opts);
        Assert.Equal(PathStatus.Found, path.Status);
        var s = TestWorld.Standing(start.X, start.Y, start.Z);
        var f = new PathFollower(path.Steps);
        double minY = s.Position.Y;
        FollowerOutput o = default;
        int places = 0, t = 0;
        for (; t < maxTicks; t++)
        {
            o = f.Tick(w, s);
            if (o.Status is FollowerStatus.Finished or FollowerStatus.Failed) break;
            if (o.Status == FollowerStatus.NeedsAction && o.Action is { } a)
            {
                if (a.Kind == FollowerActionKind.Place)
                {
                    Assert.NotNull(Aiming.FindPlaceAim(w, s.EyePosition, a.Pos, 4.5, s.BoundingBox));
                    w.SetBlock(a.Pos.X, a.Pos.Y, a.Pos.Z, Cobble, 0);
                    places++;
                }
                else
                {
                    w.SetBlock(a.Pos.X, a.Pos.Y, a.Pos.Z, Air, 0);
                }
            }
            PlayerPhysics.Tick(w, s, o.Input);
            minY = Math.Min(minY, s.Position.Y);
        }
        return (s, minY, o, t, places);
    }

    [Fact]
    public void FollowsWalkJumpAndEdgePath()
    {
        var w = TestWorld.Create();
        w.Fill(0, 63, 0, 4, 63, 2, Cobble);
        w.Fill(5, 63, 1, 8, 63, 1, Cobble); // narrow walkway
        w.Fill(7, 64, 1, 8, 64, 1, Cobble); // step up at its end
        var r = Simulate(w, new Vec3d(0.5, 64, 0.5), new GoalBlock(8, 65, 1), PathOptions.Default with { AllowPartial = false });
        Assert.Equal(FollowerStatus.Finished, r.Last.Status);
        Assert.Equal(65, r.State.Position.Y, 6);
        Assert.Equal(8, (int)Math.Floor(r.State.Position.X));
        Assert.True(r.MinY >= 63.99, $"dipped to {r.MinY}");
    }

    [Fact]
    public void BridgesAcrossTheVoid()
    {
        var w = TestWorld.Create();
        w.Fill(0, 63, 0, 2, 63, 2, Cobble);
        w.Fill(6, 63, 0, 8, 63, 2, Cobble);
        var opts = PathOptions.Default with { AllowPartial = false, AllowPlace = true, ScaffoldBudget = 5 };
        var r = Simulate(w, new Vec3d(1.5, 64, 1.5), new GoalBlock(7, 64, 1), opts);
        Assert.Equal(FollowerStatus.Finished, r.Last.Status);
        Assert.Equal(3, r.Places);
        Assert.Equal(7, (int)Math.Floor(r.State.Position.X));
        Assert.True(r.MinY >= 63.99, $"dipped to {r.MinY}");
    }

    [Fact]
    public void PillarsUp()
    {
        var w = TestWorld.Create();
        w.Fill(0, 63, 0, 2, 63, 2, Cobble);
        var opts = PathOptions.Default with { AllowPartial = false, AllowPlace = true, ScaffoldBudget = 5 };
        var r = Simulate(w, new Vec3d(1.5, 64, 1.5), new GoalBlock(1, 67, 1), opts);
        Assert.Equal(FollowerStatus.Finished, r.Last.Status);
        Assert.Equal(3, r.Places);
        Assert.Equal(67, r.State.Position.Y, 6);
    }

    [Fact]
    public void DropsAndLandsOnTarget()
    {
        var w = TestWorld.Create();
        w.Fill(0, 66, 0, 2, 66, 2, Cobble);
        w.Fill(3, 63, 0, 5, 63, 2, Cobble);
        var r = Simulate(w, new Vec3d(1.5, 67, 1.5), new GoalBlock(4, 64, 1), PathOptions.Default with { AllowPartial = false });
        Assert.Equal(FollowerStatus.Finished, r.Last.Status);
        Assert.Equal(64, r.State.Position.Y, 6);
        Assert.True(r.MinY >= 63.99);
    }

    [Fact]
    public void MinesThroughWall()
    {
        var w = TestWorld.Create();
        w.Fill(0, 63, 0, 6, 63, 2, Cobble);
        w.Fill(3, 64, 0, 3, 65, 2, Dirt);
        var r = Simulate(w, new Vec3d(1.5, 64, 1.5), new GoalBlock(5, 64, 1), PathOptions.Default with { AllowPartial = false, AllowBreak = true });
        Assert.Equal(FollowerStatus.Finished, r.Last.Status);
        Assert.Equal(5, (int)Math.Floor(r.State.Position.X));
    }

    [Fact]
    public void ReportsFallingIntoVoid()
    {
        var w = TestWorld.Create();
        w.Fill(0, 63, 0, 4, 63, 0, Cobble);
        var path = Pathfinder.FindPath(w, new BlockPos(0, 64, 0), new GoalBlock(4, 64, 0));
        var f = new PathFollower(path.Steps);
        var s = new PhysicsState();
        s.Teleport(new Vec3d(0.5, 70, 5.5), 0, 0); // knocked off: mid-air over the void
        s.Velocity = new Vec3d(0, -0.5, 0);
        var o = f.Tick(w, s);
        Assert.Equal(FollowerStatus.Failed, o.Status);
    }
}
