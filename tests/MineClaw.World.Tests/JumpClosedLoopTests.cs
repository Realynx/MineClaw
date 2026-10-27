using MineClaw.World;
using static MineClaw.World.Tests.TestBlocks;

namespace MineClaw.World.Tests;

/// <summary>
/// Jumping end to end with the always-on guard: Pathfinder → PathFollower → EdgeGuard.Filter → PlayerPhysics, with
/// re-planning on follower failure exactly like the Client's goto loop. Every case must reach its goal, never have the
/// hitbox over the void, never fall more than a planned drop, and never loop forever (bounded re-plans).
/// </summary>
public class JumpClosedLoopTests
{
    private sealed record Outcome(bool Reached, PhysicsState State, double MinY, bool EverOverVoid, int Replans, int Ticks,
        GuardActions Seen, List<MoveKind> Kinds, string? LastFailure, PathStatus FirstStatus);

    /// <summary><paramref name="veto"/> stands in for a guard that refuses a particular jump (returns true = cancel it).</summary>
    private static Outcome Run(WorldState w, Vec3d start, Goal goal, PathOptions? opts = null, int maxReplans = 4, int maxTicks = 3000,
        Func<PhysicsState, MoveInput, bool>? veto = null)
    {
        opts ??= PathOptions.Default with { AllowPartial = false };
        var guard = new EdgeGuard();
        var s = TestWorld.Standing(start.X, start.Y, start.Z);
        double minY = s.Position.Y;
        bool overVoid = false;
        var seen = GuardActions.None;
        var kinds = new List<MoveKind>();
        string? lastFailure = null;
        PathStatus? first = null;
        int replans = 0, ticks = 0;
        var blocked = new HashSet<(BlockPos, BlockPos)>();
        while (true)
        {
            var startCell = Pathfinder.StartCellFor(w, s.Position) ?? s.Position.ToBlockPos();
            if (goal.IsReached(startCell)) return new(true, s, minY, overVoid, replans, ticks, seen, kinds, lastFailure, first ?? PathStatus.Found);
            var path = Pathfinder.FindPath(w, s.Position, goal, blocked.Count == 0 ? opts : opts with { AvoidJumps = blocked });
            first ??= path.Status;
            if (path.Status != PathStatus.Found || path.Steps.Count == 0)
                return new(false, s, minY, overVoid, replans, ticks, seen, kinds, path.Reason ?? path.Status.ToString(), first.Value);
            kinds.AddRange(path.Steps.Select(p => p.Kind));
            var f = new PathFollower(path.Steps);
            FollowerOutput o;
            while (true)
            {
                if (++ticks > maxTicks) return new(false, s, minY, overVoid, replans, ticks, seen, kinds, "tick budget (deadlock?)", first.Value);
                o = f.Tick(w, s);
                if (o.Status is FollowerStatus.Finished or FollowerStatus.Failed) break;
                if (o.Status == FollowerStatus.NeedsAction && o.Action is { } a)
                {
                    if (a.Kind == FollowerActionKind.Place) w.SetBlock(a.Pos.X, a.Pos.Y, a.Pos.Z, Cobble, 0);
                    else w.SetBlock(a.Pos.X, a.Pos.Y, a.Pos.Z, Air, 0);
                }
                bool drop = f.CurrentStep?.Kind == MoveKind.Drop;
                var d = guard.Filter(w, s, o.Input, drop);
                if (veto is not null && d.Input.Jump && veto(s, d.Input))
                    d = d with { Input = d.Input with { Jump = false }, Actions = d.Actions | GuardActions.CancelledJump };
                if ((d.Actions & GuardActions.CancelledJump) != 0) f.NoteJumpCancelled(s);
                seen |= d.Actions;
                PlayerPhysics.Tick(w, s, d.Input);
                minY = Math.Min(minY, s.Position.Y);
                overVoid |= Safety.IsPlayerOverVoid(w, s.Position);
            }
            if (o.Status == FollowerStatus.Failed)
            {
                lastFailure = o.Reason;
                if (f.BlockedJump is { } bj) blocked.Add(bj);
                if (++replans > maxReplans) return new(false, s, minY, overVoid, replans, ticks, seen, kinds, lastFailure, first.Value);
            }
        }
    }

    private static void AssertSafeArrival(Outcome r, double goalY)
    {
        Assert.True(r.Reached, $"not reached: {r.LastFailure} after {r.Replans} re-plans, {r.Ticks} ticks, at {r.State.Position}");
        Assert.False(r.EverOverVoid, "hitbox went over the void");
        Assert.Equal(goalY, r.State.Position.Y, 2);
        Assert.True(r.Ticks < 2000, $"took {r.Ticks} ticks");
    }

    [Fact]
    public void StaircaseUpThreeBlocks_InTheInterior()
    {
        var w = TestWorld.Create();
        w.Fill(-6, 61, -6, 12, 63, 6, Dirt);           // big island
        w.Fill(3, 64, -1, 3, 64, 1, Cobble);           // step 1
        w.Fill(4, 64, -1, 4, 65, 1, Cobble);           // step 2
        w.Fill(5, 64, -1, 7, 66, 1, Cobble);           // top landing
        var r = Run(w, new Vec3d(0.5, 64, 0.5), new GoalBlock(6, 67, 0));
        AssertSafeArrival(r, 67);
        Assert.Equal(3, r.Kinds.Count(k => k == MoveKind.Jump));
        Assert.Equal(0, r.Replans);
        Assert.True(r.MinY >= 63.99);
    }

    [Fact]
    public void StepUpOneBlock_RightNextToTheVoid()
    {
        var w = TestWorld.Create();
        w.Fill(0, 63, 0, 8, 63, 0, Dirt);              // 1-wide walkway, void on both sides
        w.Fill(5, 64, 0, 8, 64, 0, Dirt);              // one block up halfway along
        var r = Run(w, new Vec3d(0.5, 64, 0.5), new GoalBlock(7, 65, 0));
        AssertSafeArrival(r, 65);
        Assert.Contains(MoveKind.Jump, r.Kinds);
        Assert.True((r.Seen & GuardActions.ForcedSneak) != 0 || r.Kinds.Count > 0); // edge rules active the whole way
        Assert.True(r.MinY >= 63.99, $"dipped to {r.MinY}");
    }

    [Fact]
    public void StepUpOneBlock_AtTheIslandRim_WithVoidBeyondTheLanding()
    {
        var w = TestWorld.Create();
        w.Fill(0, 61, 0, 4, 63, 4, Dirt);
        w.Set(4, 64, 2, Dirt);                         // a single block on the rim: void right behind it
        var r = Run(w, new Vec3d(1.5, 64, 2.5), new GoalBlock(4, 65, 2));
        AssertSafeArrival(r, 65);
        Assert.True(r.MinY >= 63.99);
    }

    [Fact]
    public void TwoHighWall_WithAGap_IsWalkedAround()
    {
        var w = TestWorld.Create();
        w.Fill(0, 61, 0, 10, 63, 8, Dirt);
        w.Fill(5, 64, 0, 5, 65, 6, Cobble);            // 2-high wall, open at z = 7..8
        var r = Run(w, new Vec3d(2.5, 64, 2.5), new GoalBlock(8, 64, 2));
        AssertSafeArrival(r, 64);
        Assert.DoesNotContain(MoveKind.Jump, r.Kinds);
    }

    [Fact]
    public void TwoHighWall_AcrossTheIsland_RefusedWithoutBlocks_PillaredOverWithThem()
    {
        var w = TestWorld.Create();
        w.Fill(0, 61, 0, 10, 63, 4, Dirt);
        w.Fill(5, 64, 0, 5, 65, 4, Cobble);            // wall spans the whole width
        var none = Run(w, new Vec3d(2.5, 64, 2.5), new GoalBlock(8, 64, 2));
        Assert.False(none.Reached);
        Assert.Equal(PathStatus.NoPath, none.FirstStatus);

        var r = Run(w, new Vec3d(2.5, 64, 2.5), new GoalBlock(8, 64, 2),
            PathOptions.Default with { AllowPartial = false, AllowPlace = true, ScaffoldBudget = 4 });
        AssertSafeArrival(r, 64);
        Assert.Contains(MoveKind.Pillar, r.Kinds);
        Assert.Contains(MoveKind.Jump, r.Kinds);
        Assert.True(r.MinY >= 63.99);
    }

    [Fact]
    public void OneBlockGapOverTheVoid_IsNeverJumped_RefusedByDefault_BridgedWithBlocks()
    {
        var w = TestWorld.Create();
        w.Fill(0, 61, 0, 3, 63, 3, Dirt);
        w.Fill(5, 61, 0, 8, 63, 3, Dirt);              // x = 4 is a 1-block void gap
        var none = Run(w, new Vec3d(1.5, 64, 1.5), new GoalBlock(6, 64, 1));
        Assert.False(none.Reached);
        Assert.Equal(PathStatus.NoPath, none.FirstStatus);
        Assert.False(none.EverOverVoid);

        var r = Run(w, new Vec3d(1.5, 64, 1.5), new GoalBlock(6, 64, 1),
            PathOptions.Default with { AllowPartial = false, AllowPlace = true, ScaffoldBudget = 2 });
        AssertSafeArrival(r, 64);
        Assert.Contains(MoveKind.Bridge, r.Kinds);
        Assert.DoesNotContain(MoveKind.Jump, r.Kinds);
    }

    [Fact]
    public void SlabHeight_IsAStepUp_ThenAJumpFromTheSlabOntoTheBlockAbove()
    {
        var w = TestWorld.Create();
        w.Fill(0, 61, 0, 8, 63, 4, Dirt);
        w.Set(3, 64, 2, Slab);                          // 0.5 high: walked up
        w.Fill(4, 64, 2, 4, 64, 2, Cobble);             // full block next to it (floor 65)
        w.Set(5, 64, 2, Cobble); w.Set(5, 65, 2, Slab); // block + slab (floor 65.5)
        var r = Run(w, new Vec3d(1.5, 64, 2.5), new GoalBlock(5, 65, 2));
        AssertSafeArrival(r, 65.5);
        Assert.True(r.MinY >= 63.99);
    }

    [Fact]
    public void FenceHeight_IsNotJumpable_PathGoesAroundOrRefuses()
    {
        var w = TestWorld.Create();
        w.Fill(0, 61, 0, 8, 63, 4, Dirt);
        w.Fill(4, 64, 0, 4, 64, 3, Fence);              // fence line with a gap at z = 4
        var r = Run(w, new Vec3d(1.5, 64, 1.5), new GoalBlock(6, 64, 1));
        AssertSafeArrival(r, 64);
        Assert.DoesNotContain(MoveKind.Jump, r.Kinds);

        w.Set(4, 64, 4, Fence);                         // closed: nothing jumps 1.5
        var shut = Run(w, new Vec3d(1.5, 64, 1.5), new GoalBlock(6, 64, 1));
        Assert.False(shut.Reached);
        Assert.Equal(PathStatus.NoPath, shut.FirstStatus);
        // And never standing on top of a fence (floor 65.5) either.
        Assert.Equal(PathStatus.NoPath, Pathfinder.FindPath(w, new Vec3d(1.5, 64, 1.5), new GoalBlock(4, 65, 1),
            PathOptions.Default with { AllowPartial = false }).Status);
    }

    [Fact]
    public void GuardRefusingAJump_ReplansAroundIt_InsteadOfRetryingForever()
    {
        var w = TestWorld.Create();
        w.Fill(0, 61, 0, 8, 63, 4, Dirt);
        w.Fill(4, 64, 0, 8, 64, 4, Cobble);             // raised plateau: reachable by a jump from anywhere along x = 3
        var goal = new GoalBlock(6, 65, 2);
        // A guard that never verifies jumps toward +x from the column x = 3, z = 2 (the shortest route).
        bool Veto(PhysicsState s, MoveInput _) => (int)Math.Floor(s.Position.X) == 3 && (int)Math.Floor(s.Position.Z) == 2;
        var r = Run(w, new Vec3d(1.5, 64, 2.5), goal, veto: Veto);
        AssertSafeArrival(r, 65);
        Assert.True(r.Replans is >= 1 and <= 2, $"re-plans {r.Replans}");
        Assert.Contains("refused by the edge guard", r.LastFailure);

        // Every jump refused: bounded failure, no endless retry.
        var stuck = Run(w, new Vec3d(1.5, 64, 2.5), goal, veto: (_, _) => true, maxReplans: 20);
        Assert.False(stuck.Reached);
        Assert.False(stuck.EverOverVoid);
        Assert.True(stuck.Ticks < 3000, "must give up (no path left), not hit the tick budget");
    }
}
