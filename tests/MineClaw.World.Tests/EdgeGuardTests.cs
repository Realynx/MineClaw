using MineClaw.World;
using static MineClaw.World.Tests.TestBlocks;

namespace MineClaw.World.Tests;

/// <summary>Void-safety: <see cref="EdgeGuard"/> filtering every tick of input against synthetic Skyblock islands.</summary>
public class EdgeGuardTests
{
    private const float East = -90f, West = 90f, South = 0f, North = 180f;

    /// <summary>Square island x,z ∈ [0, size-1], top at y = 64 (blocks at 63), void everywhere else.</summary>
    private static WorldState Island(int size = 5)
    {
        var w = TestWorld.Create();
        w.Fill(0, 61, 0, size - 1, 63, size - 1, Dirt);
        return w;
    }

    private sealed record Run(PhysicsState State, double MinY, bool EverOverVoid, int Clutches, GuardActions Seen);

    /// <summary>Guard → physics loop. Unrecoverable ticks get a clutch block exactly like the Client does.</summary>
    private static Run Drive(WorldState w, EdgeGuard g, PhysicsState s, Func<int, PhysicsState, MoveInput> input, int ticks,
        bool allowDrop = false, bool clutch = true, Action<int, PhysicsState>? before = null)
    {
        double minY = s.Position.Y;
        bool overVoid = false;
        int clutches = 0;
        var seen = GuardActions.None;
        for (int t = 0; t < ticks; t++)
        {
            before?.Invoke(t, s);
            var d = g.Filter(w, s, input(t, s), allowDrop);
            seen |= d.Actions;
            PlayerPhysics.Tick(w, s, d.Input);
            if (d.Unrecoverable && clutch && g.PlanClutch(w, s, 4.25, Cobble) is { } plan)
            {
                w.SetBlock(plan.Target.X, plan.Target.Y, plan.Target.Z, Cobble, 0);
                g.NoteClutchPlaced(plan.Target);
                clutches++;
            }
            minY = Math.Min(minY, s.Position.Y);
            overVoid |= Safety.IsPlayerOverVoid(w, s.Position);
        }
        return new Run(s, minY, overVoid, clutches, seen);
    }

    private static Func<int, PhysicsState, MoveInput> Hold(MoveInput i) => (_, _) => i;

    [Fact]
    public void Standing_in_flowing_water_on_the_ground_is_not_an_unrecoverable_fall()
    {
        // The current keeps horizontal speed above the brake's 0.02 threshold even on solid ground.
        // Remaining grounded in flowing water must count as a viable outcome.
        var w = TestWorld.Create();
        w.Fill(-8, 50, -8, 8, 55, 8, Stone);
        for (int x = -7; x <= 7; x++)
        for (int z = -2; z <= 2; z++)
            w.SetBlock(x, 56, z, Water, Math.Clamp((x + 8) / 2, 1, 7)); // shallow, flowing east
        var s = TestWorld.Standing(-2.5, 56, 0.5);
        var g = new EdgeGuard();
        var run = Drive(w, g, s, Hold(new MoveInput(Yaw: East)), 40, clutch: false);
        Assert.Equal(GuardActions.None, run.Seen & GuardActions.Unrecoverable);
        Assert.True(run.State.Position.X > -2.5, "the current still carries me (physics unchanged)");
        Assert.True(run.MinY >= 56 - 1e-6);
    }

    [Fact]
    public void Flowing_water_toward_the_void_edge_is_still_braked_against()
    {
        // The same current on a skyblock ledge: the guard must still keep me on the island (the water rule only accepts a
        // horizon that stays on ground, in water, never over the void).
        var w = TestWorld.Create();
        w.Fill(0, 61, 0, 4, 63, 4, Dirt);
        for (int x = 0; x <= 4; x++)
        for (int z = 0; z <= 4; z++)
            w.SetBlock(x, 64, z, Water, Math.Clamp(x + 1, 1, 7)); // flowing east, off the island at x = 5
        var s = TestWorld.Standing(3.5, 64, 2.5);
        var run = Drive(w, new EdgeGuard(), s, Hold(new MoveInput(Yaw: East)), 120);
        Assert.False(run.EverOverVoid, $"pushed over the void, ended at {run.State.Position}");
    }

    [Theory]
    [InlineData(East)]
    [InlineData(West)]
    [InlineData(North)]
    [InlineData(South)]
    [InlineData(-45f)]
    [InlineData(135f)]
    public void WalkingOrSprintingAtTheEdge_StopsAtTheEdge(float yaw)
    {
        foreach (var input in new[]
                 {
                     new MoveInput(Forward: 1, Yaw: yaw),
                     new MoveInput(Forward: 1, Sprint: true, Yaw: yaw),
                     new MoveInput(Forward: 1, Sprint: true, Jump: true, Yaw: yaw),
                     new MoveInput(Strafe: 1, Yaw: yaw + 90),
                 })
        {
            var w = Island();
            var g = new EdgeGuard();
            var r = Drive(w, g, TestWorld.Standing(2.5, 64, 2.5), Hold(input), 200);
            Assert.False(r.EverOverVoid, $"{input}: went over the void");
            Assert.True(r.State.OnGround, $"{input}: not on ground");
            Assert.Equal(64, r.State.Position.Y, 6);
            Assert.True(r.Seen.HasFlag(GuardActions.ForcedSneak));
        }
    }

    [Fact]
    public void WithoutTheGuard_SameInputFalls()
    {
        var w = Island();
        var s = TestWorld.Standing(2.5, 64, 2.5);
        TestWorld.Run(w, s, new MoveInput(Forward: 1, Sprint: true, Yaw: East), 80);
        Assert.True(s.Position.Y < 50);
    }

    [Fact]
    public void ThinBridgeAndCorners_WalkingOffSidewaysIsBlocked()
    {
        // 1-wide L: x 0..6 at z=0, then z 0..6 at x=6.
        var w = TestWorld.Create();
        w.Fill(0, 63, 0, 6, 63, 0, Cobble);
        w.Fill(6, 63, 0, 6, 63, 6, Cobble);
        foreach (float yaw in new[] { East, West, North, South, -45f, 45f, 135f, -135f, -100f, -10f })
        {
            var g = new EdgeGuard();
            var s = TestWorld.Standing(3.5, 64, 0.5);
            var r = Drive(w, g, s, Hold(new MoveInput(Forward: 1, Sprint: true, Yaw: yaw)), 150);
            Assert.False(r.EverOverVoid, $"yaw {yaw}");
            Assert.Equal(64, r.State.Position.Y, 6);
            // Then turn the outer corner of the L and keep pushing outward.
            var r2 = Drive(w, g, TestWorld.Standing(6.5, 64, 0.5), Hold(new MoveInput(Forward: 1, Strafe: 1, Yaw: yaw)), 150);
            Assert.False(r2.EverOverVoid, $"corner yaw {yaw}");
            Assert.Equal(64, r2.State.Position.Y, 6);
        }
    }

    [Fact]
    public void JumpTowardTheVoid_IsCancelled_ButJumpOntoABlockIsAllowed()
    {
        var w = Island();
        var g = new EdgeGuard();
        // Hanging over the east rim as far as sneaking allows: any forward jump from here lands in the void.
        var s = TestWorld.Standing(5.25, 64, 2.5);
        var d = g.Filter(w, s, new MoveInput(Forward: 1, Sprint: true, Jump: true, Yaw: East));
        Assert.False(d.Input.Jump);
        Assert.True(d.Actions.HasFlag(GuardActions.CancelledJump));

        w.Set(3, 64, 2, Cobble); // a step up, landing verified solid
        var s2 = TestWorld.Standing(2.5, 64, 2.5);
        var r = Drive(w, g, s2, (_, st) => st.Position.X < 3.4 ? new MoveInput(Forward: 1, Jump: true, Yaw: East) : MoveInput.Idle(East, 0), 40);
        Assert.Equal(65, r.State.Position.Y, 6);
        Assert.False(r.EverOverVoid);
    }

    [Fact]
    public void JumpAcrossAGap_LandingNotVerified_IsCancelled()
    {
        var w = TestWorld.Create();
        w.Fill(0, 63, 0, 2, 63, 2, Cobble);
        w.Fill(5, 63, 0, 7, 63, 2, Cobble); // 2-wide void gap: a sprint-jump might make it, but don't gamble
        var g = new EdgeGuard();
        var r = Drive(w, g, TestWorld.Standing(0.5, 64, 1.5), Hold(new MoveInput(Forward: 1, Sprint: true, Jump: true, Yaw: East)), 120);
        Assert.False(r.EverOverVoid);
        Assert.True(r.State.Position.X < 3.4);
        Assert.Equal(64, r.State.Position.Y, 6);
    }

    /// <summary>Vanilla EntityLiving.knockBack: motion halved, then 0.4 away from the attacker horizontally and +0.4 up
    /// (applied to the local player via 0x1C, which replaces the motion).</summary>
    [Theory]
    [InlineData(0.15, false)]
    [InlineData(0.25, false)]
    [InlineData(0.4, true)]
    [InlineData(0.6, true)]
    [InlineData(0.8, true)]
    public void KnockbackTowardTheVoid_SurvivesOrClutches(double strength, bool expectClutch)
    {
        var w = Island();
        var g = new EdgeGuard();
        var s = TestWorld.Standing(4.5, 64, 2.5);
        // Settle a few idle ticks first so the guard knows where support is (and is sneaking).
        var r = Drive(w, g, s, (_, st) => MoveInput.Idle(st.Yaw, st.Pitch), 80, before: (t, st) =>
        {
            if (t == 5) st.Velocity = new Vec3d(strength, 0.4, 0);
        });
        Assert.True(r.State.OnGround, $"knockback {strength}: still airborne at {r.State.Position}");
        Assert.False(Safety.IsPlayerOverVoid(w, r.State.Position), $"knockback {strength}: over void");
        Assert.True(r.State.Position.Y >= 63.99, $"knockback {strength}: fell to {r.State.Position.Y}");
        if (expectClutch) Assert.True(r.Clutches > 0, "expected a clutch placement");
        else Assert.Equal(0, r.Clutches);
    }

    [Fact]
    public void WeakKnockback_RecoveredByAirControlAlone()
    {
        var w = Island();
        var g = new EdgeGuard();
        var s = TestWorld.Standing(4.5, 64, 2.5);
        var r = Drive(w, g, s, (_, st) => MoveInput.Idle(st.Yaw, st.Pitch), 60, clutch: false, before: (t, st) =>
        {
            if (t == 3) st.Velocity = new Vec3d(0.25, 0.4, 0);
        });
        Assert.True(r.Seen.HasFlag(GuardActions.AirSteer));
        Assert.True(r.State.OnGround);
        Assert.Equal(64, r.State.Position.Y, 6);
    }

    [Fact]
    public void KnockbackWhileStandingOnABridge_Clutches()
    {
        var w = TestWorld.Create();
        w.Fill(0, 63, 0, 8, 63, 0, Cobble); // 1-wide bridge, knocked sideways
        var g = new EdgeGuard();
        var s = TestWorld.Standing(4.5, 64, 0.5);
        var r = Drive(w, g, s, (_, st) => MoveInput.Idle(st.Yaw, st.Pitch), 80, before: (t, st) =>
        {
            if (t == 4) st.Velocity = new Vec3d(0.1, 0.4, 0.5);
        });
        Assert.True(r.State.OnGround);
        Assert.True(r.State.Position.Y >= 63.99, $"fell to {r.State.Position.Y}");
        Assert.False(Safety.IsPlayerOverVoid(w, r.State.Position));
    }

    [Fact]
    public void ClutchPlan_PlacesAgainstTheEdgeBlockWhenKnockedOut()
    {
        var w = Island();
        var g = new EdgeGuard();
        var s = TestWorld.Standing(4.5, 64, 2.5);
        g.Filter(w, s, MoveInput.Idle(0, 0)); // records support
        s.Velocity = new Vec3d(0.6, 0.4, 0);
        s.OnGround = false;
        PlayerPhysics.Tick(w, s, MoveInput.Idle(0, 0));
        var plan = g.PlanClutch(w, s, 4.25, Cobble);
        Assert.NotNull(plan);
        Assert.Equal(63, plan.Value.Target.Y);
        Assert.True(plan.Value.Target.X >= 5);
        Assert.False(s.BoundingBox.Intersects(AABB.Block(plan.Value.Target.X, plan.Value.Target.Y, plan.Value.Target.Z)));
    }

    [Fact]
    public void PlannedDrop_ToASafeLedge_IsAllowedUnsneaked_ButNotOverTheVoid()
    {
        var w = Island();
        w.Fill(5, 61, 0, 6, 61, 4, Dirt); // lower ledge 2 below the east rim, then void
        var g = new EdgeGuard();
        var r = Drive(w, g, TestWorld.Standing(3.5, 64, 2.5), (_, st) =>
            new MoveInput(Forward: st.Position.X < 5.5 ? 1 : 0, Yaw: East), 120, allowDrop: true);
        Assert.Equal(62, r.State.Position.Y, 6);
        Assert.False(r.EverOverVoid);
        Assert.True(r.State.Position.X < 7.3);
    }

    [Fact]
    public void LavaContact_IsRefused()
    {
        var w = Island(7);
        w.Set(5, 64, 3, StillLava);
        var g = new EdgeGuard();
        var r = Drive(w, g, TestWorld.Standing(2.5, 64, 3.5), Hold(new MoveInput(Forward: 1, Sprint: true, Yaw: East)), 100);
        Assert.False(r.State.InLava);
        Assert.False(Safety.TouchesHazard(w, r.State.BoundingBox));
        Assert.True(r.State.Position.X < 4.7);
    }

    [Fact]
    public void BridgingOutOverTheVoid_SneaksAndNeverFalls()
    {
        var w = TestWorld.Create();
        w.Fill(0, 63, 0, 2, 63, 2, Cobble);
        var opts = PathOptions.Default with { AllowPlace = true, ScaffoldBudget = 10, AllowPartial = false };
        var path = Pathfinder.FindPath(w, new Vec3d(1.5, 64, 1.5), new GoalBlock(7, 64, 1), opts);
        Assert.Equal(PathStatus.Found, path.Status);
        var f = new PathFollower(path.Steps);
        var g = new EdgeGuard();
        var s = TestWorld.Standing(1.5, 64, 1.5);
        int places = 0;
        bool sneakAtPlace = true;
        for (int t = 0; t < 2000; t++)
        {
            var o = f.Tick(w, s);
            if (o.Status is FollowerStatus.Finished or FollowerStatus.Failed) break;
            if (o.Status == FollowerStatus.NeedsAction && o.Action is { Kind: FollowerActionKind.Place } a)
            {
                sneakAtPlace &= o.Input.Sneak;
                w.SetBlock(a.Pos.X, a.Pos.Y, a.Pos.Z, Cobble, 0);
                places++;
            }
            var d = g.Filter(w, s, o.Input, f.CurrentStep?.Kind == MoveKind.Drop);
            PlayerPhysics.Tick(w, s, d.Input);
            Assert.False(Safety.IsPlayerOverVoid(w, s.Position), $"over void at tick {t}: {s.Position}");
            Assert.True(s.Position.Y > 63.99);
        }
        Assert.True(f.IsFinished);
        Assert.True(places >= 4);
        Assert.True(sneakAtPlace);
        Assert.Equal(7, (int)Math.Floor(s.Position.X));
    }

    [Fact]
    public void RemovalChecks_FindTheLastBlockAboveTheVoid()
    {
        var w = TestWorld.Create();
        w.Set(0, 63, 0, Cobble);
        w.Set(0, 60, 0, Cobble);
        var feet = new Vec3d(0.5, 64, 0.5);
        Assert.False(Safety.RemovalLeavesOverVoid(w, feet, new BlockPos(0, 63, 0))); // 60 still below
        w.Set(0, 60, 0, Air);
        Assert.True(Safety.RemovalLeavesOverVoid(w, feet, new BlockPos(0, 63, 0)));
        Assert.Equal([new BlockPos(0, 63, 0)], Safety.SupportBlocks(w, feet));
    }

    [Fact]
    public void EdgeDistanceAndInterior()
    {
        var w = Island(7);
        Assert.Equal(1, Safety.EdgeDistance(w, 0, 64, 3));
        Assert.Equal(1, Safety.EdgeDistance(w, 6, 64, 6));
        Assert.Equal(2, Safety.EdgeDistance(w, 1, 64, 3));
        Assert.Equal(4, Safety.EdgeDistance(w, 3, 64, 3));
        var g = new EdgeGuard();
        var cell = g.FindInterior(w, TestWorld.Standing(6.5, 64, 3.5));
        Assert.NotNull(cell);
        Assert.True(Safety.EdgeDistance(w, cell.Value.X, 64, cell.Value.Z) >= 2);
    }

    [Fact]
    public void AwayFromEdgeGoal_RejectsRimCells()
    {
        var w = Island(7);
        var goal = GoalFiltered.AwayFromEdge(w, new GoalNear(6, 64, 3, 2));
        Assert.False(goal.IsReached(6, 64, 3));
        Assert.True(goal.IsReached(5, 64, 3));
        var path = Pathfinder.FindPath(w, new Vec3d(1.5, 64, 3.5), goal);
        Assert.Equal(PathStatus.Found, path.Status);
        var end = path.Steps[^1];
        Assert.True(Safety.EdgeDistance(w, end.X, end.Y, end.Z) >= 2);
        Assert.True(new BlockPos(end.X, end.Y, end.Z).DistanceSquaredTo(new BlockPos(6, 64, 3)) <= 4);
    }

    /// <summary>10k ticks of random inputs on a small island with a 1-wide L bridge, a safe lower ledge and lava.</summary>
    [Theory]
    [InlineData(12345)]
    [InlineData(777)]
    [InlineData(2024)]
    public void Fuzz_RandomInputs_NeverOverTheVoid(int seed)
    {
        var w = TestWorld.Create();
        w.Fill(0, 61, 0, 4, 63, 4, Dirt);       // 5x5 island
        w.Fill(5, 63, 2, 9, 63, 2, Cobble);     // 1-wide bridge east
        w.Fill(9, 63, 3, 9, 63, 6, Cobble);     // ... turning south (outer + inner corners)
        w.Fill(-3, 61, 0, -1, 61, 1, Dirt);     // lower ledge west (2-block drop, safe), void beyond
        w.Set(2, 64, 2, Cobble);                // a bump to jump on
        w.Set(0, 64, 4, StillLava);             // hazard on the island corner
        var g = new EdgeGuard();
        var s = TestWorld.Standing(2.5, 65, 0.5);
        var rng = new Random(seed);
        var input = new MoveInput();
        bool allowDrop = false;
        int overVoid = 0, hazard = 0;
        for (int t = 0; t < 10_000; t++)
        {
            if (t % rng.Next(1, 25) == 0)
            {
                input = new MoveInput(rng.Next(-1, 2), rng.Next(-1, 2), rng.NextDouble() < 0.3, rng.NextDouble() < 0.2,
                    rng.NextDouble() < 0.4, (float)(rng.NextDouble() * 360 - 180), 0);
                allowDrop = rng.NextDouble() < 0.15;
            }
            var d = g.Filter(w, s, input, allowDrop);
            PlayerPhysics.Tick(w, s, d.Input);
            if (Safety.IsPlayerOverVoid(w, s.Position)) overVoid++;
            if (s.InLava || Safety.TouchesHazard(w, s.BoundingBox)) hazard++;
            Assert.True(s.Position.Y > 61.9, $"tick {t}: fell to {s.Position}");
        }
        Assert.Equal(0, overVoid);
        Assert.Equal(0, hazard);
        Assert.Equal(0, g.Stats.UnrecoverableTicks);
        // The run really lived at the edges: the guard had to act, often.
        Assert.True(g.Stats.ForcedSneakTicks > 500, g.Stats.ToString());
        Assert.True(g.Stats.InputsClamped + g.Stats.JumpsCancelled > 50, g.Stats.ToString());
    }
}
