using MineClaw.World.Versioning;

namespace MineClaw.World.Tests;

/// <summary>
/// The pathfinder, follower and EdgeGuard on a 1.12.2 world (multi-version P3): planned against the 1.12.2 shapes (step 0.6,
/// thin fences and walls 1.5 high, vines, the new doors) and followed closed-loop through the 1.12.2 tick.
/// </summary>
public class Pathing1122Tests
{
    private const int Stone = 1, Cobble = 4, Bed = 26, Ladder = 65, Fence = 85, Vine = 106, Wall = 139, SpruceDoor = 193;
    private const float East = -90f, West = 90f, South = 0f, North = 180f;

    private static (PhysicsState State, double MinY, FollowerStatus Status) Follow(WorldState w, Vec3d start, PathResult path, int maxTicks = 1500)
    {
        var s = new PhysicsState();
        s.Teleport(start, 0, 0);
        s.OnGround = true;
        var f = new PathFollower(path.Steps);
        double minY = s.Position.Y;
        var status = FollowerStatus.Moving;
        for (int t = 0; t < maxTicks; t++)
        {
            var o = f.Tick(w, s);
            status = o.Status;
            if (o.Status is FollowerStatus.Finished or FollowerStatus.Failed) break;
            PlayerPhysics.Tick(w, s, o.Input);
            minY = Math.Min(minY, s.Position.Y);
        }
        return (s, minY, status);
    }

    [Fact]
    public void Bed_Height_Ledge_Is_A_Walk_With_Step_0_6()
    {
        var w = Physics1122Tests.World();
        w.Fill(0, 63, 0, 8, 63, 2, Stone);
        w.Fill(4, 64, 0, 8, 64, 2, Bed);              // 0.5625 high: beyond b1.8.1's 0.5 step
        var r = Pathfinder.FindPath(w, new BlockPos(1, 64, 1), new GoalBlock(7, 64, 1), new PathOptions { AllowPartial = false });
        Assert.Equal(PathStatus.Found, r.Status);
        Assert.DoesNotContain(r.Steps, s => s.Kind == MoveKind.Jump);
        var run = Follow(w, new Vec3d(1.5, 64, 1.5), r);
        Assert.Equal(FollowerStatus.Finished, run.Status);
        Assert.Equal(64.5625, run.State.Position.Y, 4);
    }

    [Theory]
    [InlineData(Fence)]
    [InlineData(Wall)]
    public void Fence_And_Wall_Rows_Are_Walked_Around_Not_Jumped(int id)
    {
        var w = Physics1122Tests.World();
        w.Fill(0, 63, -4, 10, 63, 6, Stone);
        w.Fill(5, 64, -4, 5, 64, 5, id);              // a row with a gap at z = 6
        var r = Pathfinder.FindPath(w, new BlockPos(2, 64, 0), new GoalBlock(8, 64, 0), new PathOptions { AllowPartial = false });
        Assert.Equal(PathStatus.Found, r.Status);
        Assert.Contains(r.Steps, s => s.Pos.Z == 6);
        var run = Follow(w, new Vec3d(2.5, 64, 0.5), r, 3000);
        Assert.Equal(FollowerStatus.Finished, run.Status);
        Assert.True(run.State.Position.X > 7.5);
    }

    [Fact]
    public void Climbs_A_Vine_Wall()
    {
        var w = Physics1122Tests.World();
        w.Fill(-3, 63, -3, 3, 63, 3, Stone);
        w.Fill(1, 64, -3, 3, 68, 3, Stone);            // a 5-high cliff east of x = 1
        w.Fill(0, 64, 0, 0, 68, 0, Vine, 8);           // vines on its west face (attached east)
        var r = Pathfinder.FindPath(w, new BlockPos(-2, 64, 0), new GoalBlock(2, 69, 0), new PathOptions { AllowPartial = false });
        Assert.Equal(PathStatus.Found, r.Status);
        Assert.Contains(r.Steps, s => s.Kind == MoveKind.ClimbUp);
        var run = Follow(w, new Vec3d(-1.5, 64, 0.5), r, 3000);
        Assert.Equal(FollowerStatus.Finished, run.Status);
        Assert.Equal(69, run.State.Position.Y, 3);
    }

    [Fact]
    public void Paths_Above_Y_128()
    {
        var w = Physics1122Tests.World();
        w.Fill(0, 199, 0, 9, 199, 2, Stone);
        w.Fill(5, 200, 0, 5, 200, 2, Cobble);           // a step to jump
        var r = Pathfinder.FindPath(w, new BlockPos(1, 200, 1), new GoalBlock(8, 200, 1), new PathOptions { AllowPartial = false });
        Assert.Equal(PathStatus.Found, r.Status);
        Assert.Contains(r.Steps, s => s.Kind == MoveKind.Jump && s.Pos.Y == 201);
        var run = Follow(w, new Vec3d(1.5, 200, 1.5), r);
        Assert.Equal(FollowerStatus.Finished, run.Status);
        Assert.True(run.MinY >= 200 - 1e-6);
    }

    [Fact]
    public void Spruce_Door_Is_Opened_Not_Walked_Through()
    {
        var w = Physics1122Tests.World();
        w.Fill(0, 63, -3, 8, 63, 3, Stone);
        w.Fill(4, 64, -3, 4, 66, 3, Stone);
        w.Set(4, 64, 0, SpruceDoor, 0);                // closed, facing east
        w.Set(4, 65, 0, SpruceDoor, 8);
        Assert.True(Openables.IsDoor(SpruceDoor) && Openables.OpensByHand(SpruceDoor));
        var shut = Pathfinder.FindPath(w, new BlockPos(1, 64, 0), new GoalBlock(7, 64, 0), new PathOptions { AllowPartial = false });
        Assert.NotEqual(PathStatus.Found, shut.Status);
        var r = Pathfinder.FindPath(w, new BlockPos(1, 64, 0), new GoalBlock(7, 64, 0), new PathOptions { AllowPartial = false, AllowOpenDoors = true });
        Assert.Equal(PathStatus.Found, r.Status);
        Assert.Contains(r.Steps, s => s.Kind == MoveKind.OpenDoor);
        Assert.False(Openables.LooksOpen(w, new BlockPos(4, 65, 0)));
        w.Set(4, 64, 0, SpruceDoor, 4);                // open
        Assert.True(Openables.LooksOpen(w, new BlockPos(4, 65, 0)));
    }

    [Theory]
    [InlineData(East)]
    [InlineData(West)]
    [InlineData(North)]
    [InlineData(South)]
    [InlineData(-45f)]
    public void EdgeGuard_Holds_At_The_Void_With_The_1122_Sneak_Clamp(float yaw)
    {
        foreach (var input in new[]
                 {
                     new MoveInput(Forward: 1, Yaw: yaw),
                     new MoveInput(Forward: 1, Sprint: true, Yaw: yaw),
                     new MoveInput(Forward: 1, Sprint: true, Jump: true, Yaw: yaw),
                 })
        {
            var w = Physics1122Tests.World();
            w.Fill(0, 61, 0, 4, 63, 4, Stone);
            var g = new EdgeGuard();
            var s = new PhysicsState();
            s.Teleport(new Vec3d(2.5, 64, 2.5), yaw, 0);
            s.OnGround = true;
            double minY = 64;
            for (int t = 0; t < 200; t++)
            {
                var d = g.Filter(w, s, input, false);
                PlayerPhysics.Tick(w, s, d.Input);
                minY = Math.Min(minY, s.Position.Y);
            }
            Assert.True(minY > 63.9, $"{input}: fell to {minY}");
            Assert.False(Safety.IsPlayerOverVoid(w, s.Position));
        }
    }

    [Fact]
    public void Fall_Damage_Rule_Per_Version()
    {
        Assert.Equal(7, Release1122PhysicsRules.Instance.FallDamage(10, Stone, false));
        Assert.Equal(0, Release1122PhysicsRules.Instance.FallDamage(10, 165, false)); // slime
        Assert.Equal(2, Release1122PhysicsRules.Instance.FallDamage(10, 170, false)); // hay
        Assert.Equal(7, Beta181PhysicsRules.Instance.FallDamage(10, Stone, false));
        Assert.Equal(0, Beta181PhysicsRules.Instance.FallDamage(3, Stone, false));
    }
}
