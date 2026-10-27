using MineClaw.World;
using static MineClaw.World.Tests.TestBlocks;

namespace MineClaw.World.Tests;

public class PathfinderTests
{
    /// <summary>Floor of cobble at y=63 (feet at 64) over the void.</summary>
    private static void Island(WorldState w, int x0, int z0, int x1, int z1, int y = 63) =>
        w.Fill(x0, y, z0, x1, y, z1, Cobble);

    private static readonly PathOptions Strict = new() { AllowPartial = false };

    [Fact]
    public void WalksAcrossIsland_WithDiagonals()
    {
        var w = TestWorld.Create();
        Island(w, 0, 0, 6, 6);
        var r = Pathfinder.FindPath(w, new BlockPos(1, 64, 1), new GoalBlock(5, 64, 5), Strict);
        Assert.Equal(PathStatus.Found, r.Status);
        Assert.Equal(4, r.Steps.Count);
        Assert.All(r.Steps, s => Assert.Equal(MoveKind.Diagonal, s.Kind));
        Assert.Equal(new BlockPos(5, 64, 5), r.Steps[^1].Pos);
    }

    [Fact]
    public void RefusesToCrossTheVoid()
    {
        var w = TestWorld.Create();
        Island(w, 0, 0, 2, 2);
        Island(w, 5, 0, 7, 2);
        var r = Pathfinder.FindPath(w, new BlockPos(1, 64, 1), new GoalBlock(6, 64, 1), Strict);
        Assert.Equal(PathStatus.NoPath, r.Status);
        var partial = Pathfinder.FindPath(w, new BlockPos(1, 64, 1), new GoalBlock(6, 64, 1));
        Assert.Equal(PathStatus.Partial, partial.Status);
        Assert.All(partial.Steps, s => Assert.InRange(s.X, 0, 2)); // closest point stays on the island
    }

    [Fact]
    public void BridgesWhenAllowed_WithinScaffoldBudget()
    {
        var w = TestWorld.Create();
        Island(w, 0, 0, 2, 2);
        Island(w, 5, 0, 7, 2);
        var opts = Strict with { AllowPlace = true, ScaffoldBudget = 2 };
        var r = Pathfinder.FindPath(w, new BlockPos(1, 64, 1), new GoalBlock(6, 64, 1), opts);
        Assert.Equal(PathStatus.Found, r.Status);
        var bridges = r.Steps.Where(s => s.Kind == MoveKind.Bridge).ToList();
        Assert.Equal(2, bridges.Count);
        Assert.Equal(new BlockPos(3, 63, 1), bridges[0].Place);
        Assert.Equal(new BlockPos(4, 63, 1), bridges[1].Place);
        Assert.All(bridges, b => Assert.True(b.Sneak));
        Assert.Equal(2, r.ScaffoldUsed);

        var tooFew = Pathfinder.FindPath(w, new BlockPos(1, 64, 1), new GoalBlock(6, 64, 1), opts with { ScaffoldBudget = 1 });
        Assert.NotEqual(PathStatus.Found, tooFew.Status);
    }

    [Fact]
    public void JumpsUpOneBlock()
    {
        var w = TestWorld.Create();
        Island(w, 0, 0, 5, 2);
        w.Fill(3, 64, 0, 5, 64, 2, Dirt);
        var r = Pathfinder.FindPath(w, new BlockPos(0, 64, 1), new GoalBlock(4, 65, 1), Strict);
        Assert.Equal(PathStatus.Found, r.Status);
        Assert.Contains(r.Steps, s => s.Kind == MoveKind.Jump && s.Y == 65);
        // Two blocks up is not jumpable.
        w.Fill(3, 65, 0, 5, 65, 2, Dirt);
        Assert.Equal(PathStatus.NoPath, Pathfinder.FindPath(w, new BlockPos(0, 64, 1), new GoalBlock(4, 66, 1), Strict).Status);
    }

    [Fact]
    public void StepsOntoSlabsWithoutJumping()
    {
        var w = TestWorld.Create();
        Island(w, 0, 0, 5, 2);
        w.Set(2, 64, 1, Slab);
        w.Fill(3, 64, 0, 5, 64, 2, Dirt);
        var r = Pathfinder.FindPath(w, new BlockPos(0, 64, 1), new GoalBlock(4, 65, 1), Strict with { AllowDiagonal = false });
        Assert.Equal(PathStatus.Found, r.Status);
        var slabStep = r.Steps.Single(s => s.X == 2 && s.Z == 1);
        Assert.Equal(64.5, slabStep.Floor);
        Assert.Equal(MoveKind.Walk, slabStep.Kind);
    }

    [Fact]
    public void DropsDownWithinMaxDrop()
    {
        var w = TestWorld.Create();
        Island(w, 0, 0, 2, 2, y: 66);
        Island(w, 3, 0, 8, 2, y: 63);
        var r = Pathfinder.FindPath(w, new BlockPos(1, 67, 1), new GoalBlock(6, 64, 1), Strict);
        Assert.Equal(PathStatus.Found, r.Status);
        var drop = r.Steps.Single(s => s.Kind == MoveKind.Drop);
        Assert.Equal(64, drop.Y);

        // 5-block drop exceeds MaxDrop = 3.
        var w2 = TestWorld.Create();
        Island(w2, 0, 0, 2, 2, y: 68);
        Island(w2, 3, 0, 8, 2, y: 63);
        Assert.Equal(PathStatus.NoPath, Pathfinder.FindPath(w2, new BlockPos(1, 69, 1), new GoalBlock(6, 64, 1), Strict).Status);
        Assert.Equal(PathStatus.Found,
            Pathfinder.FindPath(w2, new BlockPos(1, 69, 1), new GoalBlock(6, 64, 1), Strict with { MaxDrop = 5 }).Status);
    }

    [Fact]
    public void RefusesDropWhereOvershootFallsIntoVoid()
    {
        var w = TestWorld.Create();
        Island(w, 0, 0, 2, 2, y: 65);
        w.Set(3, 63, 1, Cobble); // lone landing block with void beyond
        var r = Pathfinder.FindPath(w, new BlockPos(1, 66, 1), new GoalBlock(3, 64, 1), Strict);
        Assert.Equal(PathStatus.NoPath, r.Status);
        w.Set(4, 63, 1, Cobble); // now there's a runout
        Assert.Equal(PathStatus.Found, Pathfinder.FindPath(w, new BlockPos(1, 66, 1), new GoalBlock(3, 64, 1), Strict).Status);
    }

    [Fact]
    public void NarrowBridge_StepsAreSneaky()
    {
        var w = TestWorld.Create();
        Island(w, 0, 0, 2, 2);
        Island(w, 3, 1, 6, 1); // 1-wide walkway
        Island(w, 7, 0, 9, 2);
        var r = Pathfinder.FindPath(w, new BlockPos(1, 64, 1), new GoalBlock(8, 64, 1), Strict);
        Assert.Equal(PathStatus.Found, r.Status);
        Assert.All(r.Steps.Where(s => s.X is >= 3 and <= 6), s => Assert.True(s.Sneak));
        Assert.All(r.Steps, s => Assert.Equal(1, s.Z));
    }

    [Fact]
    public void NoDiagonalCornerCuttingOverVoid()
    {
        var w = TestWorld.Create();
        w.Set(0, 63, 0, Cobble);
        w.Set(1, 63, 0, Cobble);
        w.Set(1, 63, 1, Cobble);
        var r = Pathfinder.FindPath(w, new BlockPos(0, 64, 0), new GoalBlock(1, 64, 1), Strict);
        Assert.Equal(PathStatus.Found, r.Status);
        Assert.Equal(2, r.Steps.Count);
        Assert.Equal(new BlockPos(1, 64, 0), r.Steps[0].Pos);
    }

    [Fact]
    public void AvoidsLavaAndFire()
    {
        var w = TestWorld.Create();
        Island(w, 0, 0, 6, 2);
        w.Set(3, 63, 1, StillLava); // lava hole in the middle row
        w.Set(3, 64, 0, Fire);
        var r = Pathfinder.FindPath(w, new BlockPos(0, 64, 1), new GoalBlock(6, 64, 1), Strict);
        Assert.Equal(PathStatus.Found, r.Status);
        Assert.DoesNotContain(r.Steps, s => s.X == 3 && s.Z is 1 or 0);
    }

    [Fact]
    public void PillarsUpWhenAllowed()
    {
        var w = TestWorld.Create();
        w.Set(0, 63, 0, Cobble);
        var opts = Strict with { AllowPlace = true, ScaffoldBudget = 3 };
        var r = Pathfinder.FindPath(w, new BlockPos(0, 64, 0), new GoalYLevel(66), opts);
        Assert.Equal(PathStatus.Found, r.Status);
        Assert.Equal(2, r.Steps.Count);
        Assert.All(r.Steps, s => Assert.Equal(MoveKind.Pillar, s.Kind));
        Assert.Equal(new BlockPos(0, 64, 0), r.Steps[0].Place);
        Assert.Equal(new BlockPos(0, 65, 0), r.Steps[1].Place);
    }

    [Fact]
    public void BreaksThroughWallWhenAllowed()
    {
        var w = TestWorld.Create();
        Island(w, 0, 0, 6, 2);
        w.Fill(3, 64, 0, 3, 66, 2, Dirt);
        Assert.Equal(PathStatus.NoPath, Pathfinder.FindPath(w, new BlockPos(1, 64, 1), new GoalBlock(5, 64, 1), Strict).Status);
        var r = Pathfinder.FindPath(w, new BlockPos(1, 64, 1), new GoalBlock(5, 64, 1), Strict with { AllowBreak = true });
        Assert.Equal(PathStatus.Found, r.Status);
        var b = r.Steps.Single(s => s.Break.Count > 0);
        Assert.Equal(2, b.Break.Count);
        Assert.Equal(65, b.Break[0].Y); // head block first

        // Never breaks unbreakable blocks or chests.
        w.Fill(3, 64, 0, 3, 66, 2, Bedrock);
        Assert.Equal(PathStatus.NoPath,
            Pathfinder.FindPath(w, new BlockPos(1, 64, 1), new GoalBlock(5, 64, 1), Strict with { AllowBreak = true }).Status);
    }

    [Fact]
    public void ClimbsLadder()
    {
        var w = TestWorld.Create();
        Island(w, 0, 0, 3, 3);
        w.Fill(0, 64, 2, 3, 67, 3, Stone);     // cliff, top floor at y=67 (feet 68)
        w.Fill(1, 64, 1, 1, 67, 1, Ladder, 2); // ladder on its face (attached to +Z)
        var r = Pathfinder.FindPath(w, new BlockPos(1, 64, 0), new GoalBlock(1, 68, 3), Strict);
        Assert.Equal(PathStatus.Found, r.Status);
        Assert.Contains(r.Steps, s => s.Kind == MoveKind.ClimbUp);
    }

    [Fact]
    public void GoalWithinReach_StopsNearTarget()
    {
        var w = TestWorld.Create();
        Island(w, 0, 0, 10, 2);
        var r = Pathfinder.FindPath(w, new BlockPos(0, 64, 1), new GoalWithinReach(new BlockPos(10, 64, 1), 3.5), Strict);
        Assert.Equal(PathStatus.Found, r.Status);
        Assert.InRange(r.Steps[^1].X, 7, 9);
    }

    [Fact]
    public void GoalUseBlock_needs_line_of_sight_not_just_distance()
    {
        // Chest at 5,64,5 behind a planks wall along z=4 (x 0..10, 2 high) with a gap at x=9: the cell right outside
        // (5,64,3) is in reach but blind; the search must go round through the gap to the chest's side.
        var w = TestWorld.Create();
        Island(w, 0, 0, 10, 10);
        for (int x = 0; x <= 10; x++)
        for (int y = 64; y <= 65; y++)
            if (x != 9) w.SetBlock(x, y, 4, TestBlocks.Planks, 0);
        w.SetBlock(5, 64, 5, TestBlocks.Chest, 0);
        var chest = new BlockPos(5, 64, 5);
        Assert.True(new GoalWithinReach(chest, 3.9).IsReached(5, 64, 3)); // the old, distance-only goal
        var goal = new GoalUseBlock(w, chest, 3.9);
        Assert.False(goal.IsReached(5, 64, 3));
        Assert.True(goal.IsReached(5, 64, 6));
        var r = Pathfinder.FindPath(w, new BlockPos(5, 64, 1), goal, Strict);
        Assert.Equal(PathStatus.Found, r.Status);
        Assert.True(r.Steps[^1].Z >= 5, $"ended at {r.Steps[^1]}");
    }

    [Fact]
    public void InvalidStartInMidAir()
    {
        var w = TestWorld.Create();
        Island(w, 0, 0, 2, 2);
        Assert.Equal(PathStatus.InvalidStart, Pathfinder.FindPath(w, new BlockPos(5, 64, 5), new GoalBlock(1, 64, 1)).Status);
        // A player sneaking at the edge (center over air) still gets a valid start cell.
        Assert.Equal(new BlockPos(2, 64, 1), Pathfinder.StartCellFor(w, new Vec3d(3.2, 64, 1.5)));
    }
}

public class PathfinderBudgetTests
{
    [Fact]
    public void LargeIsland_UnreachableGoal_EndsWithinBudget()
    {
        var w = TestWorld.Create();
        w.Fill(-30, 63, -30, 30, 63, 30, TestBlocks.Cobble);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = Pathfinder.FindPath(w, new BlockPos(0, 64, 0), new GoalBlock(200, 64, 0),
            new PathOptions { MaxNodes = 20_000, Timeout = TimeSpan.FromSeconds(5) });
        Assert.Equal(PathStatus.Partial, r.Status);
        Assert.Equal(30, r.Steps[^1].X); // got as close as the island allows
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), sw.Elapsed.ToString());
    }

    [Fact]
    public void LongBridge_FoundQuickly()
    {
        var w = TestWorld.Create();
        w.Fill(-2, 63, -2, 2, 63, 2, TestBlocks.Cobble);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = Pathfinder.FindPath(w, new BlockPos(0, 64, 0), new GoalBlock(20, 64, 0),
            new PathOptions { AllowPlace = true, ScaffoldBudget = 64, AllowPartial = false, Timeout = TimeSpan.FromSeconds(10) });
        Assert.Equal(PathStatus.Found, r.Status);
        Assert.Equal(18, r.ScaffoldUsed);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), $"{sw.Elapsed} {r}");
    }

    [Fact]
    public void Cancellation_Stops()
    {
        var w = TestWorld.Create();
        w.Fill(-30, 63, -30, 30, 63, 30, TestBlocks.Cobble);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var r = Pathfinder.FindPath(w, new BlockPos(0, 64, 0), new GoalBlock(200, 64, 0), new PathOptions(), cts.Token);
        Assert.True(r.NodesExplored <= 256);
    }
}
