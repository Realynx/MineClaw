using MineClaw.World;
using static MineClaw.World.Tests.TestBlocks;

namespace MineClaw.World.Tests;

/// <summary>
/// A Story quarry's spiral stair (docs/web/story-mode.md "Quarries"), walked end to end: Pathfinder → PathFollower →
/// EdgeGuard.Filter → PlayerPhysics. Live 11-04: "They crouch-walk when the stairs get high on the outside wall, so it takes too
/// long to get down." A pit with a floor is no fatal edge: the stair is walked normally (no sneak steps, no forced sneak), and the
/// guard still never lets the body fall into the pit. The void stays an edge.
/// </summary>
public class QuarryStairWalkTests
{
    private const int X1 = 3, Z1 = 3, Top = 63, Depth = 14;

    /// <summary>The clockwise edge ring of the rectangle x X1..x2, z Z1..z2 from its north-west corner (StoryQuarry.Ring).</summary>
    private static List<(int X, int Z)> Ring(int x2, int z2)
    {
        var ring = new List<(int, int)>();
        for (int x = X1; x <= x2; x++) ring.Add((x, Z1));
        for (int z = Z1 + 1; z <= z2; z++) ring.Add((x2, z));
        for (int x = x2 - 1; x >= X1; x--) ring.Add((x, z2));
        for (int z = z2 - 1; z > Z1; z--) ring.Add((X1, z));
        return ring;
    }

    /// <summary>Solid ground y 30..63 around; the quarry x X1..x2, z Z1..z2 dug from y 63 down <see cref="Depth"/> layers, its steps left.</summary>
    private static WorldState Quarry(int size)
    {
        int x2 = X1 + size - 1, z2 = Z1 + size - 1;
        var w = TestWorld.Create();
        w.Fill(-4, 30, -4, 14, Top, 14, Stone);
        var ring = Ring(x2, z2);
        for (int k = 0; k < Depth; k++)
        {
            var step = ring[k % ring.Count];
            for (int x = X1; x <= x2; x++)
                for (int z = Z1; z <= z2; z++)
                    if ((x, z) != step) w.SetBlock(x, Top - k, z, Air, 0);
        }
        return w;
    }

    private sealed record Walk(bool Reached, GuardActions Seen, bool SneakSteps, double MinY, int Ticks, string? Failure);

    private static Walk Go(WorldState w, Vec3d start, Goal goal)
    {
        var guard = new EdgeGuard();
        var s = TestWorld.Standing(start.X, start.Y, start.Z);
        var seen = GuardActions.None;
        bool sneakSteps = false;
        double minY = s.Position.Y;
        int ticks = 0;
        for (int replan = 0; replan < 4; replan++)
        {
            if (goal.IsReached(Pathfinder.StartCellFor(w, s.Position) ?? s.Position.ToBlockPos())) return new(true, seen, sneakSteps, minY, ticks, null);
            var path = Pathfinder.FindPath(w, s.Position, goal, PathOptions.Default with { AllowPartial = false });
            if (path.Status != PathStatus.Found) return new(false, seen, sneakSteps, minY, ticks, path.Reason ?? path.Status.ToString());
            sneakSteps |= path.Steps.Any(p => p.Sneak);
            var f = new PathFollower(path.Steps);
            FollowerOutput o;
            do
            {
                o = f.Tick(w, s);
                var d = guard.Filter(w, s, o.Input, f.CurrentStep?.Kind == MoveKind.Drop);
                seen |= d.Actions;
                PlayerPhysics.Tick(w, s, d.Input);
                minY = Math.Min(minY, s.Position.Y);
            } while (++ticks < 4000 && o.Status is not (FollowerStatus.Finished or FollowerStatus.Failed));
        }
        return new(goal.IsReached(s.Position.ToBlockPos()), seen, sneakSteps, minY, ticks, "re-plans");
    }

    [Theory]
    [InlineData(4)]
    [InlineData(2)]
    public void The_stair_is_walked_down_and_up_without_sneaking_and_never_falls(int size)
    {
        var w = Quarry(size);
        int bottom = Top - Depth + 1; // feet on the floor under the last dug layer
        var floor = Enumerable.Range(X1, size).SelectMany(x => Enumerable.Range(Z1, size).Select(z => (x, z)))
            .First(c => !BlockShapes.HasCollision(w, c.x, bottom, c.z) && !BlockShapes.HasCollision(w, c.x, bottom + 1, c.z) && BlockShapes.HasCollision(w, c.x, bottom - 1, c.z));
        var down = Go(w, new Vec3d(0.5, Top + 1, 0.5), new GoalBlock(floor.x, bottom, floor.z));
        Assert.True(down.Reached, $"down: {down.Failure}");
        Assert.False(down.SneakSteps, "a path step beside the pit is flagged sneak");
        Assert.Equal(GuardActions.None, down.Seen & GuardActions.ForcedSneak);
        var up = Go(w, new Vec3d(floor.x + 0.5, bottom, floor.z + 0.5), new GoalBlock(0, Top + 1, 0));
        Assert.True(up.Reached, $"up: {up.Failure}");
        Assert.False(up.SneakSteps);
        Assert.Equal(GuardActions.None, up.Seen & GuardActions.ForcedSneak);
        Assert.True(up.MinY >= bottom - 1e-6, "fell below the floor");
    }

    [Fact]
    public void The_void_is_still_an_edge_to_sneak_at()
    {
        var w = TestWorld.Create();
        w.Fill(0, 63, 0, 8, 63, 0, Dirt); // a 1-wide walkway over the void
        var r = Go(w, new Vec3d(0.5, 64, 0.5), new GoalBlock(8, 64, 0));
        Assert.True(r.Reached, r.Failure);
        Assert.True(r.SneakSteps || (r.Seen & GuardActions.ForcedSneak) != 0);
        Assert.True(Safety.IsFatalColumn(w, 0, 64, 1));
        var pit = Quarry(4);
        Assert.False(Safety.IsFatalColumn(pit, 4, 64, 4)); // a pit with a floor
    }
}
