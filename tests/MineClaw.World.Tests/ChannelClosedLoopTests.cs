using MineClaw.World;
using Xunit.Abstractions;
using static MineClaw.World.Tests.TestBlocks;

namespace MineClaw.World.Tests;

/// <summary>
/// Fox 11-04: "The pawns are very bad at navigating one-drop-level holes. I have channels for water to flow, one level down, like a
/// hole. They fall into it when they're walking, and they can't walk out because they have to jump once to get out." Story water
/// digs 1-deep trenches and pours still sources into them. End to end (Pathfinder → PathFollower → EdgeGuard → PlayerPhysics, re-plan
/// on failure like the Client's goto) on both versions' physics.
/// </summary>
public class ChannelClosedLoopTests(ITestOutputHelper output)
{
    public static readonly TheoryData<bool> Versions = new() { false, true };

    private static WorldState NewWorld(bool v1122) => v1122 ? Physics1122Tests.World() : TestWorld.Create();

    /// <summary>A dirt field (feet 64) with a 1-wide still-water channel at x 5 and a 1-wide dry trench at x 10, both along z.</summary>
    private static WorldState Field(bool v1122, int width = 1, int waterId = StillWater, int waterMeta = 0)
    {
        var w = NewWorld(v1122);
        w.Fill(-6, 60, -8, 20, 63, 8, Dirt);
        w.Fill(5, 63, -8, 5 + width - 1, 63, 8, waterId, waterMeta); // channel (floor 62, water in 63)
        w.Fill(10, 63, -8, 10 + width - 1, 63, 8, Air);               // dry trench
        return w;
    }

    private sealed record Outcome(bool Reached, PhysicsState State, int Replans, int Ticks, List<MoveKind> Kinds, string? LastFailure,
        double MinY, List<string> Log);

    private static Outcome Run(WorldState w, Vec3d start, Goal goal, PathOptions? opts = null, int maxReplans = 4, int maxTicks = 2000,
        bool startInWater = false, Action<int, PhysicsState>? disturb = null)
    {
        opts ??= PathOptions.Default with { AllowPartial = false };
        var guard = new EdgeGuard();
        var s = TestWorld.Standing(start.X, start.Y, start.Z);
        if (startInWater) s.OnGround = true;
        var kinds = new List<MoveKind>();
        var log = new List<string>();
        string? lastFailure = null;
        int replans = 0, ticks = 0;
        double minY = s.Position.Y;
        var blocked = new HashSet<(BlockPos, BlockPos)>();
        while (true)
        {
            var startCell = Pathfinder.StartCellFor(w, s.Position) ?? s.Position.ToBlockPos();
            if (goal.IsReached(startCell)) return new(true, s, replans, ticks, kinds, lastFailure, minY, log);
            var path = Pathfinder.FindPath(w, s.Position, goal, blocked.Count == 0 ? opts : opts with { AvoidJumps = blocked });
            log.Add($"plan from {s.Position}: {path} [{string.Join(" ", path.Steps)}]");
            if (path.Status != PathStatus.Found || path.Steps.Count == 0)
                return new(false, s, replans, ticks, kinds, path.Reason ?? path.Status.ToString(), minY, log);
            kinds.AddRange(path.Steps.Select(p => p.Kind));
            var f = new PathFollower(path.Steps);
            FollowerOutput o;
            while (true)
            {
                if (++ticks > maxTicks) return new(false, s, replans, ticks, kinds, "tick budget", minY, log);
                disturb?.Invoke(ticks, s);
                o = f.Tick(w, s);
                if (o.Status is FollowerStatus.Finished or FollowerStatus.Failed) break;
                bool drop = f.CurrentStep?.Kind == MoveKind.Drop;
                var d = guard.Filter(w, s, o.Input, drop);
                if ((d.Actions & GuardActions.CancelledJump) != 0) f.NoteJumpCancelled(s);
                PlayerPhysics.Tick(w, s, d.Input);
                minY = Math.Min(minY, s.Position.Y);
            }
            if (o.Status == FollowerStatus.Failed)
            {
                lastFailure = o.Reason;
                log.Add($"failed at {s.Position} on {f.CurrentStep}: {o.Reason}");
                if (f.BlockedJump is { } bj) blocked.Add(bj);
                if (++replans > maxReplans) return new(false, s, replans, ticks, kinds, lastFailure, minY, log);
            }
        }
    }

    private void Dump(Outcome r)
    {
        foreach (var l in r.Log) output.WriteLine(l);
        output.WriteLine($"reached={r.Reached} ticks={r.Ticks} replans={r.Replans} at {r.State.Position} last={r.LastFailure}");
    }

    private void AssertArrived(Outcome r, int maxTicks)
    {
        Dump(r);
        Assert.True(r.Reached, $"not reached: {r.LastFailure} after {r.Replans} re-plans, {r.Ticks} ticks, at {r.State.Position}");
        Assert.Equal(0, r.Replans);
        Assert.True(r.Ticks <= maxTicks, $"took {r.Ticks} ticks");
    }

    [Theory, MemberData(nameof(Versions))]
    public void Climbs_out_of_a_dry_one_deep_trench(bool v1122)
    {
        var w = Field(v1122);
        var r = Run(w, new Vec3d(10.5, 63, 0.5), new GoalBlock(13, 64, 0));
        AssertArrived(r, 120);
        Assert.Equal(MoveKind.Jump, r.Kinds[0]);
    }

    [Theory, MemberData(nameof(Versions))]
    public void Climbs_out_of_a_water_filled_one_deep_channel(bool v1122)
    {
        var w = Field(v1122);
        var r = Run(w, new Vec3d(5.5, 63, 0.5), new GoalBlock(8, 64, 0), startInWater: true);
        AssertArrived(r, 160);
    }

    [Theory, MemberData(nameof(Versions))]
    public void Climbs_out_of_a_flowing_water_channel(bool v1122)
    {
        var w = Field(v1122, waterId: Water, waterMeta: 3);
        var r = Run(w, new Vec3d(5.5, 63, 0.5), new GoalBlock(2, 64, 0), startInWater: true);
        AssertArrived(r, 160);
    }

    [Theory, MemberData(nameof(Versions))]
    public void Climbs_out_of_a_two_wide_water_channel(bool v1122)
    {
        var w = Field(v1122, width: 2);
        var r = Run(w, new Vec3d(5.5, 63, 0.5), new GoalBlock(9, 64, 0), startInWater: true);
        AssertArrived(r, 200);
    }

    [Theory, MemberData(nameof(Versions))]
    public void Crosses_the_field_over_a_water_channel_and_a_dry_trench(bool v1122)
    {
        var w = Field(v1122);
        var r = Run(w, new Vec3d(0.5, 64, 0.5), new GoalBlock(15, 64, 0));
        AssertArrived(r, 300);
        Assert.True(r.State.Position.Y >= 63.99);
    }

    [Theory, MemberData(nameof(Versions))]
    public void Crosses_two_wide_channels(bool v1122)
    {
        var w = Field(v1122, width: 2);
        var r = Run(w, new Vec3d(0.5, 64, 0.5), new GoalBlock(15, 64, 0));
        AssertArrived(r, 400);
    }

    [Theory, MemberData(nameof(Versions))]
    public void Walks_alongside_a_channel_without_falling_in(bool v1122)
    {
        var w = Field(v1122);
        // Along the bank of the water channel (x 4) and of the dry trench (x 9), end to end.
        foreach (int bank in new[] { 4, 6, 9, 11 })
        {
            var r = Run(w, new Vec3d(bank + 0.5, 64, -6.5), new GoalBlock(bank, 64, 6));
            AssertArrived(r, 200);
            Assert.True(r.MinY >= 63.99, $"bank {bank}: dipped to {r.MinY}");
            Assert.DoesNotContain(MoveKind.Drop, r.Kinds);
            Assert.DoesNotContain(MoveKind.Swim, r.Kinds);
        }
    }

    [Theory, MemberData(nameof(Versions))]
    public void Walks_round_the_end_of_a_short_channel_or_jumps_it_without_falling_in(bool v1122)
    {
        var w = NewWorld(v1122);
        w.Fill(-6, 60, -8, 20, 63, 8, Dirt);
        w.Fill(5, 63, -8, 5, 63, -4, StillWater); // a channel ending at z -4: the walk turns round its end or jumps it
        var r = Run(w, new Vec3d(3.5, 64, -6.5), new GoalBlock(7, 64, -6));
        AssertArrived(r, 300);
        Assert.True(r.MinY >= 63.99, $"dipped to {r.MinY}");
        Assert.DoesNotContain(MoveKind.Swim, r.Kinds);
    }

    [Theory, MemberData(nameof(Versions))]
    public void A_one_wide_channel_or_trench_is_jumped_across_not_waded(bool v1122)
    {
        var w = Field(v1122);
        var r = Run(w, new Vec3d(0.5, 64, 0.5), new GoalBlock(15, 64, 0));
        AssertArrived(r, 400);
        Assert.True(r.MinY >= 63.99, $"dipped to {r.MinY}");
        Assert.Equal(2, r.Kinds.Count(k => k is MoveKind.JumpGap or MoveKind.SprintJump));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Knocked_into_a_ditch_beside_the_path_it_jumps_straight_back_out(bool v1122, bool water)
    {
        var w = Field(v1122);
        int ditch = water ? 5 : 10;
        bool pushed = false;
        // Walking north along the bank west of the ditch, something shoves the body east into it half way.
        var r = Run(w, new Vec3d(ditch - 0.5, 64, -6.5), new GoalBlock(ditch - 1, 64, 6), disturb: (t, s) =>
        {
            if (pushed || s.Position.Z < -1) return;
            pushed = true;
            s.Teleport(new Vec3d(ditch + 0.5, 63, s.Position.Z), s.Yaw, s.Pitch);
            s.OnGround = true;
        });
        Dump(r);
        Assert.True(pushed);
        Assert.True(r.Reached, $"not reached: {r.LastFailure} at {r.State.Position}");
        Assert.True(r.Ticks <= 200, $"took {r.Ticks} ticks");
        Assert.DoesNotContain(r.Log, l => l.Contains("stuck"));
    }

    [Theory, MemberData(nameof(Versions))]
    public void A_field_cut_by_channels_plans_well_inside_the_budget(bool v1122)
    {
        // Every node within 3 cells of a channel used to simulate the whole gap-jump fan (~16 jumps): 1.8-3.6 s for this search,
        // past the client's 2 s plan budget (timeouts, partial paths, "only got partway", then story.escape and a teleport home).
        var w = NewWorld(v1122);
        w.Fill(-6, 60, -8, 28, 63, 8, Dirt);
        foreach (int x in new[] { 3, 8, 13, 18, 23 }) w.Fill(x, 63, -8, x, 63, 8, x % 2 == 0 ? Air : StillWater);
        var opts = PathOptions.Default with { AllowPartial = false };
        Pathfinder.FindPath(w, new BlockPos(0, 64, 0), new GoalBlock(26, 64, 5), opts); // warm-up (JIT)
        var r = Pathfinder.FindPath(w, new BlockPos(0, 64, -6), new GoalBlock(26, 64, 5), opts);
        output.WriteLine(r.ToString());
        Assert.Equal(PathStatus.Found, r.Status);
        Assert.True(r.Elapsed < TimeSpan.FromMilliseconds(600), $"{r}");
    }
}
