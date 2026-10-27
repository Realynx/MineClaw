using MineClaw.World;
using B = MineClaw.Data.Blocks;

namespace MineClaw.World.Tests;

/// <summary>
/// Doors end to end with the real b1.8.1 block table: Pathfinder (AllowOpenDoors) → PathFollower → EdgeGuard →
/// PlayerPhysics, with a tiny "server" that flips the open bit on a right-click (both door halves) unless the block is
/// locked, and the Client's re-plan rule: an openable that didn't open is vetoed (CanOpen) for the next plan.
/// </summary>
public class DoorClosedLoopTests
{
    private sealed record Outcome(bool Reached, PhysicsState State, int Clicks, int Replans, List<MoveKind> Kinds, string? LastFailure,
        HashSet<BlockPos> Locked);

    private static WorldState NewWorld()
    {
        var w = new WorldState();
        int size = 64;
        w.ApplyChunkRegion(-32, 0, -32, size, 128, size, new byte[size * 128 * size], null, null, null);
        return w;
    }

    /// <summary>Right-click as the b1.8.1 server does it (BlockDoor/BlockTrapDoor/BlockFenceGate.blockActivated).</summary>
    private static void Click(WorldState w, BlockPos p, ISet<BlockPos> locked)
    {
        var s = Openables.StateBlock(w, p);
        if (locked.Contains(s)) return;
        int id = w.GetBlock(s.X, s.Y, s.Z), m = w.GetMeta(s.X, s.Y, s.Z);
        if (!Openables.OpensByHand(id)) return;
        w.SetBlock(s.X, s.Y, s.Z, id, m ^ Openables.OpenBit);
        if (Openables.IsDoor(id) && w.GetBlock(s.X, s.Y + 1, s.Z) == id)
            w.SetBlock(s.X, s.Y + 1, s.Z, id, (m ^ Openables.OpenBit) | Openables.UpperBit);
    }

    private static Outcome Run(WorldState w, Vec3d start, Goal goal, ISet<BlockPos>? lockedBlocks = null, int maxReplans = 4, int maxTicks = 4000)
    {
        lockedBlocks ??= new HashSet<BlockPos>();
        var vetoed = new HashSet<BlockPos>();
        var guard = new EdgeGuard();
        var s = TestWorld.Standing(start.X, start.Y, start.Z);
        var kinds = new List<MoveKind>();
        int clicks = 0, replans = 0, ticks = 0;
        string? lastFailure = null;
        while (true)
        {
            var cell = Pathfinder.StartCellFor(w, s.Position) ?? s.Position.ToBlockPos();
            if (goal.IsReached(cell)) return new(true, s, clicks, replans, kinds, lastFailure, vetoed);
            var path = Pathfinder.FindPath(w, s.Position, goal, PathOptions.Default with
            {
                AllowOpenDoors = true, AllowPartial = false, CanOpen = (p, _) => !vetoed.Contains(p),
            });
            if (path.Status != PathStatus.Found || path.Steps.Count == 0)
                return new(false, s, clicks, replans, kinds, path.Reason ?? path.Status.ToString(), vetoed);
            kinds.AddRange(path.Steps.Select(p => p.Kind));
            var f = new PathFollower(path.Steps);
            FollowerOutput o;
            while (true)
            {
                if (++ticks > maxTicks) return new(false, s, clicks, replans, kinds, "tick budget", vetoed);
                o = f.Tick(w, s);
                if (o.Status is FollowerStatus.Finished or FollowerStatus.Failed) break;
                if (o.Status == FollowerStatus.NeedsAction && o.Action is { Kind: FollowerActionKind.Activate } a)
                {
                    clicks++;
                    Click(w, a.Pos, lockedBlocks);
                }
                var d = guard.Filter(w, s, o.Input, f.CurrentStep?.Kind == MoveKind.Drop);
                PlayerPhysics.Tick(w, s, d.Input);
            }
            if (o.Status == FollowerStatus.Failed)
            {
                lastFailure = o.Reason;
                if (f.CurrentStep is { Opens.Count: > 0 } st)
                    foreach (var tg in st.Opens.Where(t => !t.Done(w))) vetoed.Add(tg.Pos);
                if (++replans > maxReplans) return new(false, s, clicks, replans, kinds, lastFailure, vetoed);
            }
            else if (f.IsFinished && !goal.IsReached(Pathfinder.StartCellFor(w, s.Position) ?? s.Position.ToBlockPos()))
                return new(false, s, clicks, replans, kinds, "finished off goal", vetoed);
        }
    }

    /// <summary>Stone floor y=63 (x,z -6..6), a 3-high stone wall at x=0 with one gap at z=0.</summary>
    private static WorldState Room()
    {
        var w = NewWorld();
        w.Fill(-6, 63, -6, 6, 63, 6, B.Stone);
        w.Fill(0, 64, -6, 0, 66, 6, B.Stone);
        w.Fill(0, 64, 0, 0, 65, 0, 0);
        return w;
    }

    private static readonly Goal Across = new GoalBlock(3, 64, 0);
    private static readonly Vec3d Start = new(-2.5, 64, 0.5);

    [Fact]
    public void Walks_through_a_closed_wooden_door_and_closes_it_behind()
    {
        var w = Room();
        w.Set(0, 64, 0, B.WoodenDoor, 0);
        w.Set(0, 65, 0, B.WoodenDoor, 8);
        var r = Run(w, Start, Across);
        Assert.True(r.Reached, r.LastFailure);
        Assert.Contains(MoveKind.OpenDoor, r.Kinds);
        Assert.Equal(2, r.Clicks); // open, then restore
        Assert.False(Openables.IsOpenAt(w, new BlockPos(0, 64, 0)), "door put back closed");
        Assert.False(Openables.IsOpen(w.GetMeta(0, 65, 0)), "upper half follows");
        Assert.Equal(0, r.Replans);
    }

    [Fact]
    public void Walks_through_a_fence_gate()
    {
        var w = Room();
        w.Fill(0, 64, -6, 0, 66, 6, 0);
        w.Fill(0, 64, -6, 0, 64, 6, B.Fence);
        w.Set(0, 64, 0, B.FenceGate, 0);
        var r = Run(w, Start, Across);
        Assert.True(r.Reached, r.LastFailure);
        Assert.Contains(MoveKind.OpenGate, r.Kinds);
        Assert.False(Openables.IsOpenAt(w, new BlockPos(0, 64, 0)), "gate closed behind");
    }

    [Fact]
    public void Climbs_a_ladder_through_a_trapdoor_hatch()
    {
        var w = NewWorld();
        w.Fill(-4, 63, -4, 4, 63, 4, B.Stone);         // ground floor
        w.Fill(-4, 66, -4, 4, 66, 4, B.Stone);         // ceiling = upper floor (stand at y=67)
        w.Fill(0, 64, 1, 0, 67, 1, B.Stone);           // wall the ladder hangs on (z+1), up to the hatch hinge
        w.Fill(0, 64, 0, 0, 66, 0, B.Ladder, 2);       // ladder facing north up through the hole
        w.Set(0, 67, -1, B.Stone);                     // hinge block on the far side
        w.Set(0, 67, 0, B.Trapdoor, 0);                 // hinged on the ladder wall (z+1): the open panel blocks the head
        var goal = new GoalBlock(-1, 67, 0);
        Assert.False(Pathfinder.FindPath(w, new Vec3d(0.5, 64, -1.5), goal, PathOptions.Default with { AllowPartial = false, AllowOpenDoors = true }).Success,
            "a hatch hinged on the ladder's wall can't be climbed through");
        w.Set(0, 67, 0, B.Trapdoor, 1);                 // closed hatch over the hole, hinged on z-1
        Assert.False(Pathfinder.FindPath(w, new Vec3d(0.5, 64, -1.5), goal, PathOptions.Default with { AllowPartial = false }).Success,
            "a closed hatch is a ceiling unless opening is allowed");
        var r = Run(w, new Vec3d(0.5, 64, -1.5), goal);
        Assert.True(r.Reached, $"{r.LastFailure} at {r.State.Position} kinds {string.Join(",", r.Kinds)}");
        Assert.True(r.Clicks >= 1);
        Assert.Contains(MoveKind.ClimbUp, r.Kinds);
    }

    [Fact]
    public void Locked_door_is_vetoed_and_the_replan_takes_the_other_way()
    {
        var w = Room();
        w.Set(0, 64, 0, B.WoodenDoor, 0);
        w.Set(0, 65, 0, B.WoodenDoor, 8);
        w.Fill(0, 64, 5, 0, 65, 5, 0);                 // a second, open doorway further along the wall
        var locked = new HashSet<BlockPos> { new(0, 64, 0) };
        var r = Run(w, Start, Across, locked);
        Assert.True(r.Reached, r.LastFailure);
        Assert.Contains(new BlockPos(0, 64, 0), r.Locked);
        Assert.True(r.Replans >= 1, "the follower gave up on the door and the goto re-planned");
        Assert.Contains("locked", r.LastFailure);
        Assert.False(Openables.IsOpenAt(w, new BlockPos(0, 64, 0)));
    }

    [Fact]
    public void Locked_door_without_another_way_fails_without_looping()
    {
        var w = Room();
        w.Set(0, 64, 0, B.WoodenDoor, 0);
        w.Set(0, 65, 0, B.WoodenDoor, 8);
        var r = Run(w, Start, Across, new HashSet<BlockPos> { new(0, 64, 0) });
        Assert.False(r.Reached);
        Assert.Equal(1, r.Replans);
        Assert.True(r.State.Position.X < 0, "never walked into the closed door");
    }
}
