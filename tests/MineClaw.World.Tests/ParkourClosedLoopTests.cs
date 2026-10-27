using MineClaw.World;
using static MineClaw.World.Tests.TestBlocks;

namespace MineClaw.World.Tests;

/// <summary>
/// Gap jumps end to end: Pathfinder → PathFollower → EdgeGuard (planned-parkour exemption) → PlayerPhysics, with
/// re-planning like the Client's goto (see <see cref="ParkourHarness"/>).
/// </summary>
public class ParkourClosedLoopTests
{
    private static readonly PathOptions Opts = PathOptions.Default with { AllowPartial = false };
    private static readonly PathOptions VoidOk = Opts with { AllowVoidParkour = true, HasClutchBlock = true };

    /// <summary>Start platform x = −6..0 × z −1..1 (floor 64), gap columns x = 1..gap, landing platform beyond with
    /// top at 64 + dy; below a shallow yard (a miss is a small fall) or the void.</summary>
    private static WorldState Gap(int gap, int dy = 0, bool overVoid = false, int startFrom = -6)
    {
        var w = TestWorld.Create();
        w.Fill(startFrom, 61, -1, 0, 63, 1, Dirt);
        w.Fill(gap + 1, 60, -1, gap + 5, 63 + dy, 1, Stone);
        // A yard 2 below the landing (no climbing out) under and around everything: a miss is a small fall.
        if (!overVoid) w.Fill(-8, Math.Min(61, 61 + dy), -4, gap + 8, Math.Min(61, 61 + dy), 4, Stone);
        return w;
    }

    private static void AssertSafeArrival(ParkourHarness.Outcome r, double goalY, MoveKind? kind = null)
    {
        Assert.True(r.Reached, r.ToString());
        Assert.False(r.Died, r.ToString());
        Assert.Equal(goalY, r.State.Position.Y, 2);
        Assert.True(r.MinY >= Math.Min(goalY, 64) - 0.01, $"dipped to {r.MinY}: {r}");
        if (kind is { } k) Assert.Contains(k, r.Kinds);
        Assert.True(r.Jumps.Count > 0, r.ToString());
        Assert.Equal(0, r.Clutches);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void FlatGap_OverGround_IsJumped(int gap)
    {
        var w = Gap(gap);
        var r = ParkourHarness.Run(w, new Vec3d(-3.5, 64, 0.5), new GoalBlock(gap + 3, 64, 0), Opts);
        AssertSafeArrival(r, 64);
        Assert.Equal(0, r.Replans);
        if (gap == 3) Assert.Contains(MoveKind.SprintJump, r.Kinds);
        Assert.True((r.Seen & GuardActions.ParkourExempt) != 0);
        Assert.Equal(GuardActions.None, r.Seen & GuardActions.CancelledJump);
    }

    [Fact]
    public void FlatGap_OfFour_IsRefused_NotTried()
    {
        var w = Gap(4);
        var r = ParkourHarness.Run(w, new Vec3d(-3.5, 64, 0.5), new GoalBlock(7, 64, 0), Opts);
        Assert.False(r.Reached);
        Assert.Equal(PathStatus.NoPath, r.FirstStatus);
        Assert.True(r.MinY >= 63.99);
    }

    [Fact]
    public void BridgeToRoof_WithARise()
    {
        // 1-wide bridge over a yard 3 below, house roof one block higher two columns away.
        var w = TestWorld.Create();
        w.Fill(-8, 59, -6, 12, 60, 6, Dirt);                  // yard (top 61): a miss is a 3-block fall
        w.Fill(-6, 63, 0, 0, 63, 0, Planks);                   // the bridge, floor 64
        w.Fill(-8, 61, 0, -7, 63, 0, Dirt);                    // bridge pier
        w.Fill(3, 61, -2, 7, 63, 2, Cobble);                   // house walls
        w.Fill(3, 64, -2, 7, 64, 2, Planks);                   // roof, floor 65
        var r = ParkourHarness.Run(w, new Vec3d(-4.5, 64, 0.5), new GoalBlock(5, 65, 0), Opts);
        AssertSafeArrival(r, 65, MoveKind.SprintJump);
    }

    [Fact]
    public void BridgeToRoof_LandingOnASlab()
    {
        var w = TestWorld.Create();
        w.Fill(-8, 59, -6, 12, 60, 6, Dirt);
        w.Fill(-6, 63, 0, 0, 63, 0, Planks);
        w.Fill(3, 61, -2, 7, 63, 2, Cobble);
        w.Fill(3, 64, -2, 3, 64, 2, Slab);                     // slab eave at the near edge: floor 64.5
        w.Fill(4, 64, -2, 7, 64, 2, Planks);                   // roof, floor 65
        var r = ParkourHarness.Run(w, new Vec3d(-4.5, 64, 0.5), new GoalBlock(5, 65, 0), Opts);
        AssertSafeArrival(r, 65);
        Assert.Contains(r.Jumps, j => Math.Abs(j.ToFloor - 64.5) < 1e-6 || Math.Abs(j.ToFloor - 65) < 1e-6);
    }

    [Fact]
    public void BridgeToRoof_LandingOnAStairTop()
    {
        var w = TestWorld.Create();
        w.Fill(-8, 59, -6, 12, 60, 6, Dirt);
        w.Fill(-6, 63, 0, 0, 63, 0, Planks);
        w.Fill(3, 61, -2, 7, 63, 2, Cobble);
        w.Fill(3, 64, -2, 3, 64, 2, BlockShapes.WoodStairs, 0); // stairs ascending toward +x: low half faces the bridge
        w.Fill(4, 64, -2, 7, 64, 2, Planks);
        var r = ParkourHarness.Run(w, new Vec3d(-4.5, 64, 0.5), new GoalBlock(5, 65, 0), Opts);
        AssertSafeArrival(r, 65);
    }

    [Fact]
    public void NotEnoughRunUp_IsRefused_ThenWithRoom_IsJumped()
    {
        // A 4-gap down one block: needs at least one cell of run-up (from the table: run 0 clears 3 robustly).
        var tight = Gap(4, dy: -1, startFrom: 0);
        tight.Fill(0, 61, -1, 0, 63, -1, Air);
        tight.Fill(0, 61, 1, 0, 63, 1, Air);                   // a 1x1 pillar: no run-up in any direction
        tight.Fill(5, 61, -1, 9, 62, -1, Air);
        tight.Fill(5, 61, 1, 9, 62, 1, Air);                   // a 1-wide landing strip: no off-axis shortcut
        var none = ParkourHarness.Run(tight, new Vec3d(0.5, 64, 0.5), new GoalBlock(7, 63, 0), Opts);
        Assert.False(none.Reached, none.ToString());
        Assert.Equal(PathStatus.NoPath, none.FirstStatus);
        Assert.True(none.MinY >= 63.99);

        var roomy = Gap(4, dy: -1, startFrom: -3);
        roomy.Fill(5, 61, -1, 9, 62, -1, Air);
        roomy.Fill(5, 61, 1, 9, 62, 1, Air);
        var r = ParkourHarness.Run(roomy, new Vec3d(-2.5, 64, 0.5), new GoalBlock(7, 63, 0), Opts);
        AssertSafeArrival(r, 63, MoveKind.SprintJump);
    }

    [Fact]
    public void WalkAround_IsPreferred_OverAJump()
    {
        var w = Gap(2);
        w.Fill(1, 61, 2, 2, 63, 3, Dirt);                      // a land bridge around the gap at z = 2..3
        w.Fill(-1, 61, 2, 0, 63, 3, Dirt);
        w.Fill(3, 61, 2, 4, 63, 3, Dirt);
        var r = ParkourHarness.Run(w, new Vec3d(-0.5, 64, 0.5), new GoalBlock(4, 64, 0), Opts);
        Assert.True(r.Reached, r.ToString());
        Assert.Empty(r.Jumps); // ~6 blocks around beats the jump's risk penalty
    }

    [Fact]
    public void TooHungryToSprint_LongGapRefused_ShortGapPlainJump()
    {
        var hungry = Opts with { CanSprint = false };
        var r3 = ParkourHarness.Run(Gap(3), new Vec3d(-3.5, 64, 0.5), new GoalBlock(6, 64, 0), hungry, canSprint: () => false);
        Assert.False(r3.Reached);
        Assert.Equal(PathStatus.NoPath, r3.FirstStatus);

        var r2 = ParkourHarness.Run(Gap(2), new Vec3d(-3.5, 64, 0.5), new GoalBlock(5, 64, 0), hungry, canSprint: () => false);
        AssertSafeArrival(r2, 64, MoveKind.JumpGap);
        Assert.DoesNotContain(MoveKind.SprintJump, r2.Kinds);
    }

    [Fact]
    public void HungerDuringTheRunUp_Aborts_AtTheEdge()
    {
        int calls = 0;
        bool Food() => ++calls < 4; // food drops to 6 a few ticks into the run
        var w = Gap(2, overVoid: true);
        var r = ParkourHarness.Run(w, new Vec3d(-3.5, 64, 0.5), new GoalBlock(5, 64, 0), VoidOk, canSprint: Food);
        Assert.False(r.Reached);
        Assert.Contains(r.Failures, f => f.Contains("hungry"));
        Assert.False(r.EverOverVoid, r.ToString());
        Assert.True(r.MinY >= 63.99);
    }

    [Fact]
    public void VoidGap_RefusedByDefault_AllowedWithFlagAndClutchBlock()
    {
        var w = Gap(2, overVoid: true);
        var start = new Vec3d(-3.5, 64, 0.5);
        var goal = new GoalBlock(5, 64, 0);
        var def = ParkourHarness.Run(w, start, goal, Opts);
        Assert.False(def.Reached);
        Assert.Equal(PathStatus.NoPath, def.FirstStatus);
        Assert.False(def.EverOverVoid);

        var noClutch = ParkourHarness.Run(w, start, goal, Opts with { AllowVoidParkour = true });
        Assert.Equal(PathStatus.NoPath, noClutch.FirstStatus);

        var flagOnly = ParkourHarness.Run(w, start, goal, Opts with { HasClutchBlock = true });
        Assert.Equal(PathStatus.NoPath, flagOnly.FirstStatus);

        var ok = ParkourHarness.Run(w, start, goal, VoidOk);
        AssertSafeArrival(ok, 64);
        Assert.All(ok.Jumps, j => Assert.True(j.OverVoid && j.Margin >= 0, $"margin {j.Margin}"));
    }

    [Fact]
    public void VoidGap_UsesTheStricterThreshold()
    {
        // Physically clearable 3-gap (+1 rise is 2 robust): over the void the strict set must refuse what ground allows.
        var ground = ParkourHarness.Run(Gap(2, dy: 1), new Vec3d(-3.5, 64, 0.5), new GoalBlock(4, 65, 0), Opts);
        AssertSafeArrival(ground, 65);
        foreach (var j in ground.Jumps) Assert.False(j.OverVoid);
        var plan = Parkour.Plan(Gap(4, dy: -1, overVoid: true), new BlockPos(-3, 64, 0), 64, new BlockPos(5, 63, 0), 63, true, true, 4.3, 3, out _);
        Assert.Null(plan); // robust over ground (table: 4) but not with the void set (3)
        Assert.NotNull(Parkour.Plan(Gap(4, dy: -1), new BlockPos(-3, 64, 0), 64, new BlockPos(5, 63, 0), 63, true, false, 4.3, 3, out _));
    }

    [Fact]
    public void KnockbackMidFlight_OverTheVoid_SteersOrClutches_NeverFalls()
    {
        foreach (var kb in new[] { new Vec3d(-0.45, 0.25, 0), new Vec3d(0, 0.3, 0.5), new Vec3d(0.0, 0.1, -0.35), new Vec3d(-0.2, 0.3, 0.2) })
        {
            var w = Gap(2, overVoid: true);
            bool hit = false;
            Vec3d? Hook(int t, PhysicsState s, PathFollower f)
            {
                if (hit || f.Commitment is not { } c || c.Tick != c.Plan.Schedule.JumpTick + 3) return null;
                hit = true;
                return kb;
            }
            var r = ParkourHarness.Run(w, new Vec3d(-3.5, 64, 0.5), new GoalBlock(5, 64, 0), VoidOk, onTick: Hook, clutch: true);
            Assert.True(hit, "knockback never applied");
            Assert.False(r.Died, $"{kb}: {r}");
            Assert.True(r.State.OnGround, $"{kb}: {r}");
            Assert.False(Safety.IsPlayerOverVoid(w, r.State.Position), $"{kb}: {r}");
            Assert.True((r.Seen & GuardActions.ParkourRejected) != 0, $"{kb}: guard never took over: {r}");
            Assert.True(r.Clutches > 0 || (r.Seen & (GuardActions.AirSteer | GuardActions.ClampedInput)) != 0 || r.Reached, $"{kb}: {r}");
        }
    }

    [Fact]
    public void WorldChangeDuringRunUp_AbortsAtTheEdge_AndReplans()
    {
        var w = Gap(2, overVoid: true);
        bool removed = false;
        Vec3d? Hook(int t, PhysicsState s, PathFollower f)
        {
            if (removed || f.Commitment is not { } c || c.Tick != 2) return null;
            removed = true;
            w.Fill(3, 60, -1, 4, 63, 1, Air); // the landing's near edge is mined away mid run-up
            return null;
        }
        var r = ParkourHarness.Run(w, new Vec3d(-3.5, 64, 0.5), new GoalBlock(5, 64, 0), VoidOk, onTick: Hook);
        Assert.True(removed);
        Assert.Contains(r.Failures, f => f.Contains("aborted"));
        Assert.False(r.EverOverVoid, r.ToString());
        Assert.True(r.MinY >= 63.99, r.ToString());
        Assert.True(r.State.Position.X < 1.0, $"stopped at the edge: {r.State.Position}");
        Assert.True(r.State.OnGround);
        // The re-plan sees the wider gap: a 4-gap over the void is refused.
        Assert.False(r.Reached);
    }

    [Fact]
    public void BlockAppearingInTheRunUp_Aborts_NoSneakClampBeforeIt()
    {
        var w = Gap(3);
        bool placed = false;
        Vec3d? Hook(int t, PhysicsState s, PathFollower f)
        {
            if (placed || f.Commitment is not { } c || c.Tick != 3) return null;
            placed = true;
            w.Set(0, 64, 0, Cobble); w.Set(0, 65, 0, Cobble); // a wall at the takeoff
            return null;
        }
        var r = ParkourHarness.Run(w, new Vec3d(-3.5, 64, 0.5), new GoalBlock(6, 64, 0), Opts, onTick: Hook);
        Assert.True(placed);
        Assert.Contains(r.Failures, f => f.Contains("aborted"));
        Assert.True(r.MinY >= 63.99);
    }

    [Fact]
    public void CeilingBump_LowCeilingRefused_HigherCeilingStillLands()
    {
        var low = Gap(3);
        low.Fill(-6, 66, -1, 6, 66, 1, Stone);             // 2 blocks of headroom: the jump can't rise
        var r = ParkourHarness.Run(low, new Vec3d(-3.5, 64, 0.5), new GoalBlock(6, 64, 0), Opts);
        Assert.False(r.Reached);
        Assert.Equal(PathStatus.NoPath, r.FirstStatus);

        var bump = Gap(3);
        bump.Fill(-6, 67, -1, 6, 67, 1, Stone);            // the head grazes the ceiling at the apex
        var ok = ParkourHarness.Run(bump, new Vec3d(-3.5, 64, 0.5), new GoalBlock(6, 64, 0), Opts);
        AssertSafeArrival(ok, 64);
        Assert.All(ok.Jumps, j => Assert.True(j.Nominal.Max(p => p.Y) < 65.25 - 0.01, "apex should be cut by the ceiling"));
    }

    [Fact]
    public void OffAxisJump_ToADiagonallyOffsetPlatform()
    {
        var w = TestWorld.Create();
        w.Fill(-6, 61, -1, 0, 63, 1, Dirt);
        w.Fill(-8, 60, -4, 10, 60, 8, Stone);              // yard 3 below
        w.Fill(4, 61, 2, 7, 63, 4, Stone);                 // landing offset by 2 in z
        var r = ParkourHarness.Run(w, new Vec3d(-3.5, 64, 0.5), new GoalBlock(5, 64, 3), Opts);
        AssertSafeArrival(r, 64);
    }

    [Fact]
    public void Cache_ReusesValidatedJumps_AndInvalidatesOnBlockChange()
    {
        var w = Gap(2);
        var cache = new ParkourCache();
        var o = Opts with { ParkourCache = cache };
        var a = Pathfinder.FindPath(w, new BlockPos(-3, 64, 0), new GoalBlock(5, 64, 0), o);
        Assert.True(a.Success);
        long misses = cache.Misses;
        var b = Pathfinder.FindPath(w, new BlockPos(-3, 64, 0), new GoalBlock(5, 64, 0), o);
        Assert.True(b.Success);
        Assert.Equal(misses, cache.Misses);
        Assert.True(cache.Hits > 0);
        w.Fill(3, 60, -1, 3, 63, 1, Air);                  // landing edge gone: entries around it must be re-validated
        Pathfinder.FindPath(w, new BlockPos(-3, 64, 0), new GoalBlock(5, 64, 0), o);
        Assert.True(cache.Misses > misses);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DensePillarField_PlansWithinTheBudget(bool yard)
    {
        // Worst case for neighbour generation: every node has gap jumps in many directions.
        var w = TestWorld.Create();
        var rng = new Random(5);
        if (yard) w.Fill(-30, 60, -30, 30, 60, 30, Stone);
        w.Fill(-2, 63, -2, 2, 63, 2, Dirt);
        for (int i = 0; i < 160; i++)
        {
            int x = rng.Next(-24, 25), z = rng.Next(-24, 25);
            w.Fill(x, 61, z, x, 63 + rng.Next(-1, 2), z, Stone);
        }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = Pathfinder.FindPath(w, new BlockPos(0, 64, 0), new GoalBlock(22, 64, 22),
            PathOptions.Default with { AllowVoidParkour = true, HasClutchBlock = true, Timeout = TimeSpan.FromSeconds(10) });
        Assert.NotEqual(PathStatus.InvalidStart, r.Status);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"{sw.Elapsed} {r}");
    }
}
