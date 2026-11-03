namespace MineClaw.World.Tests;

using static MineClaw.World.Tests.TestBlocks;

/// <summary>
/// Item 11 (live 10-27): working the wheat field must not trample it. mc-dev Entity.move calls Block.b(…, entity) on each step
/// of a non-sneaking walker and BlockSoil.b turns farmland to dirt 1 in 4: paths go around fields, and any step on farmland
/// (or a jump onto it) is sneaked.
/// </summary>
public class FarmlandPathTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private const int Farmland = 60;
    private static readonly PathOptions Strict = new() { AllowPartial = false, AllowParkour = false };

    [Fact]
    public void Path_goes_around_a_field_when_it_can()
    {
        var w = TestWorld.Create();
        w.Fill(0, 63, -2, 8, 63, 2, Cobble);
        w.Fill(3, 63, -2, 5, 63, 1, Farmland); // the field; z=2 stays a cobble path around it
        var r = Pathfinder.FindPath(w, new BlockPos(0, 64, 0), new GoalBlock(8, 64, 0), Strict);
        Assert.Equal(PathStatus.Found, r.Status);
        Assert.DoesNotContain(r.Steps, s => w.GetBlock(s.X, s.Y - 1, s.Z) == Farmland);
    }

    [Fact]
    public void Steps_onto_or_across_farmland_are_sneaked_and_never_plain_jumps()
    {
        var w = TestWorld.Create();
        w.Fill(0, 63, 0, 8, 63, 0, Cobble);
        w.Fill(3, 63, 0, 5, 63, 0, Farmland); // a 1-wide strip: the only way across is over the field
        w.Fill(7, 64, 0, 8, 64, 0, Cobble);   // a step up after it
        var r = Pathfinder.FindPath(w, new BlockPos(0, 64, 0), new GoalBlock(8, 65, 0), Strict);
        Assert.Equal(PathStatus.Found, r.Status);
        var onField = r.Steps.Where(s => w.GetBlock(s.X, s.Y - 1, s.Z) == Farmland).ToList();
        Assert.NotEmpty(onField);
        Assert.All(onField, s => Assert.True(s.Sneak, $"step {s.Pos} on farmland not sneaked"));
        // Leaving the field is sneaked too (the first step off it still treads the farmland).
        int last = r.Steps.ToList().FindLastIndex(s => w.GetBlock(s.X, s.Y - 1, s.Z) == Farmland);
        Assert.True(r.Steps[last + 1].Sneak, "step off the field not sneaked");
    }

    [Theory]
    [InlineData(60, false)]
    [InlineData(60, true)]
    [InlineData(208, false)]
    public void Partial_height_field_is_walkable_from_grass_to_every_tile(int surface, bool withCrops)
    {
        var w = Physics1122Tests.World();
        w.Fill(-2, 63, -3, 8, 63, 3, Grass);
        w.Fill(1, 63, -2, 6, 63, 2, surface);
        if (withCrops) w.Fill(1, 64, -2, 6, 64, 2, Wheat);
        for (int x = 1; x <= 6; x++)
        for (int z = -2; z <= 2; z++)
        {
            var goal = new GoalBlock(x, 63, z);
            // Edge sneaking must cost at least as much as ordinary field sneaking for the cost lower bound below.
            var r = Pathfinder.FindPath(w, new Vec3d(0.5, 64, 0.5), goal,
                Strict with { EdgeCostMultiplier = PathOptions.SneakFactor });
            Assert.Equal(PathStatus.Found, r.Status);
            Assert.Equal(new BlockPos(x, 63, z), r.Steps[^1].Pos);
            Assert.All(r.Steps, s => Assert.True(s.Kind is MoveKind.Walk or MoveKind.Diagonal));
            if (surface == Farmland)
                Assert.All(r.Steps.Where(s => OnField(w, s)), s =>
                {
                    Assert.True(s.Sneak);
                    double penalty = goal.Distance(s.X, s.Y, s.Z) <= PathOptions.FarmlandGoalRange
                        ? PathOptions.FarmlandPenalty : PathOptions.FarmlandWalkPenalty;
                    Assert.True(s.Cost >= PathOptions.WalkCost * PathOptions.SneakFactor + penalty);
                });
            Follow(w, new Vec3d(0.5, 64, 0.5), r);
        }
    }

    [Theory]
    [InlineData(60, false)]
    [InlineData(60, true)]
    [InlineData(208, false)]
    public void Partial_height_field_can_be_walked_off_onto_grass(int surface, bool withCrops)
    {
        var w = Physics1122Tests.World();
        w.Fill(-2, 63, -3, 8, 63, 3, Grass);
        w.Fill(1, 63, -2, 6, 63, 2, surface);
        if (withCrops) w.Fill(1, 64, -2, 6, 64, 2, Wheat);
        var start = new Vec3d(3.5, 63.9375, 0.5);
        var r = Pathfinder.FindPath(w, start, new GoalBlock(0, 64, 0), Strict);
        Assert.Equal(PathStatus.Found, r.Status);
        Assert.All(r.Steps, s => Assert.True(s.Kind is MoveKind.Walk or MoveKind.Diagonal));
        Assert.Equal(64, r.Steps[^1].Floor);
        Follow(w, start, r);
    }

    [Theory]
    [InlineData(8, -1, false)]
    [InlineData(8, 1, true)]
    [InlineData(9, -1, true)]
    [InlineData(9, 1, false)]
    public void A_body_in_a_one_wide_water_trench_can_exit_onto_either_field(int water, int side, bool withCrops)
    {
        var w = Physics1122Tests.World();
        w.Fill(-4, 62, -4, 4, 62, 4, Dirt);
        w.Fill(-4, 63, -4, 4, 63, 4, Farmland);
        w.Fill(0, 63, -4, 0, 63, 4, water);
        if (withCrops)
        {
            w.Fill(-4, 64, -4, -1, 64, 4, Wheat);
            w.Fill(1, 64, -4, 4, 64, 4, Wheat);
        }
        var start = new Vec3d(0.5, 63, 0.5);
        var r = Pathfinder.FindPath(w, start, new GoalBlock(side * 3, 63, 0), Strict);
        Assert.Equal(PathStatus.Found, r.Status);
        var exit = r.Steps.First(s => s.X != 0);
        Assert.Equal(MoveKind.Jump, exit.Kind);
        Assert.True(exit.Cost >= PathOptions.FarmlandJumpPenalty);
        Assert.Null(exit.Place);
        Follow(w, start, r);
    }

    [Theory]
    [InlineData(60, false, 1)]
    [InlineData(60, true, 1)]
    [InlineData(60, false, 2)]
    [InlineData(60, true, 2)]
    [InlineData(59, false, 1)]
    [InlineData(59, false, 2)]
    [InlineData(141, false, 2)]
    [InlineData(142, false, 2)]
    [InlineData(207, false, 2)]
    [InlineData(104, false, 2)]
    [InlineData(105, false, 2)]
    [InlineData(115, false, 2)]
    [InlineData(81, false, 2)]
    [InlineData(83, false, 2)]
    [InlineData(86, false, 2)]
    [InlineData(103, false, 2)]
    [InlineData(127, false, 2)]
    public void A_field_island_uses_the_rim_instead_of_bridging_above_soil_or_crops(int field, bool withCrops, int height)
    {
        var w = Physics1122Tests.World();
        w.Fill(1, 62, 0, 5, 62, 19, Dirt);
        w.Fill(1, 63, 0, 5, 63, 19, field);
        if (withCrops) w.Fill(1, 64, 1, 5, 64, 19, Wheat); // planted field with a bare row at the crossing
        int rim = 63 + height;
        w.Fill(0, 62, 0, 0, rim, 20, Cobble);
        w.Fill(6, 62, 0, 6, rim, 20, Cobble);
        w.Fill(0, 62, 20, 6, rim, 20, Cobble);
        var opts = Strict with { AllowPlace = true, ScaffoldBudget = 5, MaxDrop = 0 };
        var r = Pathfinder.FindPath(w, new BlockPos(0, rim + 1, 0), new GoalBlock(6, rim + 1, 0), opts);
        Assert.Equal(PathStatus.Found, r.Status);
        Assert.DoesNotContain(r.Steps, s => s.Place is { X: >= 1 and <= 5, Z: >= 0 and < 20 });
        Assert.Contains(r.Steps, s => s.Z == 20);
    }

    [Theory]
    [InlineData(60)]
    [InlineData(59)]
    public void Pillaring_cannot_add_a_second_block_above_farmland_or_a_crop(int field)
    {
        var w = Physics1122Tests.World();
        w.Set(0, 63, 0, field);
        w.Set(0, 64, 0, Cobble); // an existing scaffold does not permit another block over the field
        var opts = Strict with { AllowPlace = true, ScaffoldBudget = 1 };
        var r = Pathfinder.FindPath(w, new BlockPos(0, 65, 0), new GoalBlock(0, 66, 0), opts);
        Assert.Equal(PathStatus.NoPath, r.Status);
        Assert.DoesNotContain(r.Steps, s => s.Place is not null);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_lower_field_is_reached_by_dropping_onto_grass_then_walking_in(bool withCrops)
    {
        var w = Physics1122Tests.World();
        w.Fill(-4, 62, -4, 10, 62, 10, Stone);
        w.Fill(-4, 63, -4, 0, 63, 3, Stone);
        w.Fill(1, 62, 0, 6, 62, 0, Farmland);
        if (withCrops) w.Fill(1, 63, 0, 6, 63, 0, Wheat);
        var r = Pathfinder.FindPath(w, new BlockPos(0, 64, 0), new GoalBlock(4, 62, 0), Opts1122);
        Assert.Equal(PathStatus.Found, r.Status);
        Assert.Contains(r.Steps, s => s.Kind == MoveKind.Drop && !OnField(w, s));
        Assert.DoesNotContain(r.Steps, s => OnField(w, s) && s.Kind is MoveKind.Drop or MoveKind.Jump or MoveKind.JumpGap or MoveKind.SprintJump);
        Follow(w, new Vec3d(0.5, 64, 0.5), r);
    }

    [Fact]
    public void A_base_ringed_by_nine_plots_can_reach_an_outside_chest_and_return_with_normal_budgets()
    {
        var w = Physics1122Tests.World();
        w.Fill(-22, 62, -22, 22, 63, 22, Dirt);
        foreach (int x in new[] { -14, -4, 6 })
        foreach (int z in new[] { -14, -4, 6 })
        {
            w.Fill(x, 63, z, x + 8, 63, z + 8, Farmland);
            w.Fill(x, 64, z, x + 8, 64, z + 8, Wheat);
        }
        foreach (int channel in new[] { -5, 5 })
        {
            w.Fill(channel, 63, -14, channel, 64, 14, Air);
            w.Fill(channel, 63, -14, channel, 63, 14, Water);
            w.Fill(-14, 63, channel, 14, 64, channel, Air);
            w.Fill(-14, 63, channel, 14, 63, channel, Water);
            w.Fill(channel - 1, 63, 0, channel + 1, 63, 0, Cobble); // plot-edge blocks and a channel crossing
            w.Fill(channel - 1, 64, 0, channel + 1, 64, 0, Air);
        }
        w.Fill(-2, 63, -2, 2, 63, 2, Grass); // base, entirely surrounded by the plots
        w.Fill(-2, 64, -2, 2, 64, 2, Air);
        w.Set(17, 64, 0, Chest);
        var options = PathOptions.Default with { AllowPartial = false };
        foreach (var (start, goal) in new[]
        {
            (new Vec3d(0.5, 64, 0.5), new GoalBlock(16, 64, 0)), // beside the outside chest
            (new Vec3d(16.5, 64, 0.5), new GoalBlock(0, 64, 0)), // back inside
            (new Vec3d(0.5, 64, 0.5), new GoalBlock(10, 63, 10)), // work a plot
        })
        {
            var r = Pathfinder.FindPath(w, start, goal, options);
            output.WriteLine($"{start} -> {goal}: {r}");
            Assert.True(r.Status == PathStatus.Found, $"{start} -> {goal}: {r.Status}, {r.Reason}, {r.NodesExplored} nodes, {r.Elapsed}");
            Assert.True(r.NodesExplored < PathOptions.Default.MaxNodes);
            Assert.True(r.Elapsed <= PathOptions.Default.Timeout);
            Assert.DoesNotContain(r.Steps, s => OnField(w, s) && s.Kind is MoveKind.Drop or MoveKind.JumpGap or MoveKind.SprintJump);
            for (int i = 0; i < r.Steps.Count; i++)
            {
                var s = r.Steps[i];
                if (!OnField(w, s) || s.Kind != MoveKind.Jump) continue;
                // The only hop needed in this level layout is the explicitly permitted trench escape, still +100.
                var from = i == 0 ? Pathfinder.StartCellFor(w, start)!.Value : r.Steps[i - 1].Pos;
                Assert.True(BlockShapes.IsWater(w.GetBlock(from.X, from.Y, from.Z)));
                Assert.True(s.Cost >= PathOptions.FarmlandJumpPenalty);
            }
        }
    }

    private static void Follow(WorldState w, Vec3d start, PathResult path)
    {
        var s = TestWorld.Standing(start.X, start.Y, start.Z);
        var f = new PathFollower(path.Steps);
        for (int tick = 0; tick < 1200; tick++)
        {
            var o = f.Tick(w, s);
            Assert.True(o.Status != FollowerStatus.Failed, $"{o.Reason}: {s.Position}");
            Assert.NotEqual(FollowerStatus.NeedsAction, o.Status);
            if (o.Status == FollowerStatus.Finished) return;
            PlayerPhysics.Tick(w, s, o.Input);
        }
        Assert.Fail($"follower did not finish at {s.Position}");
    }

    // ---- 1.12.2 trampling is from FALLING (BlockFarmland.onFallenUpon: fall distance - 0.5 chance), so goto must not drop,
    // gap-jump or hop onto a field, bare or with crops on it, and prefers walking around fields. ----

    private const int Wheat = 59;
    private static readonly PathOptions Opts1122 = new() { AllowPartial = false };

    private static bool OnField(WorldState w, PathStep s) =>
        w.GetBlock(s.X, s.Y, s.Z) == Farmland || w.GetBlock(s.X, s.Y - 1, s.Z) == Farmland;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_drop_never_lands_on_farmland_bare_or_with_crops(bool withCrops)
    {
        var w = Physics1122Tests.World();
        w.Fill(-4, 62, -4, 10, 62, 10, Stone);          // low ground (feet 63)
        w.Fill(-4, 63, -4, 0, 63, 3, Stone);            // a platform one block up (feet 64) ...
        w.Fill(1, 62, 0, 6, 62, 0, Farmland);           // ... and a one-wide field at ground level below its edge (feet 62.94)
        if (withCrops) w.Fill(1, 63, 0, 6, 63, 0, Wheat);
        // Walls one block up along both sides and the far end (feet 64, like the platform): every way onto the field is a drop
        // from feet 64 onto farmland. Since walking between full blocks and farmland is legal, the low stone yard must not touch it.
        w.Fill(1, 63, -1, 7, 63, -1, Stone);
        w.Fill(1, 63, 1, 7, 63, 1, Stone);
        w.Fill(7, 63, 0, 7, 63, 0, Stone);
        // Sanity: the field itself is walkable once on it, so the drop is the only way in (and the old planner took it).
        var r = Pathfinder.FindPath(w, new BlockPos(0, 64, 0), new GoalBlock(4, 62, 0), Opts1122);
        Assert.NotEqual(PathStatus.Found, r.Status);
    }

    [Fact]
    public void A_gap_jump_never_lands_on_farmland()
    {
        var w = Physics1122Tests.World();
        w.Fill(-6, 61, -1, 0, 63, 1, Dirt);              // start platform, feet 64
        w.Fill(3, 60, -1, 7, 63, 1, Stone);              // landing platform across a 2-wide gap ...
        w.Fill(3, 63, -1, 7, 63, 1, Farmland);           // ... that is all field (feet 63.94): a stone landing past it would be a clean jump
        w.Fill(-8, 61, -4, 10, 61, 4, Stone);            // a yard below: a miss is a small fall, no way back up
        var r = Pathfinder.FindPath(w, new BlockPos(-3, 64, 0), new GoalBlock(6, 63, 0), Opts1122);
        Assert.NotEqual(PathStatus.Found, r.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_hop_up_onto_a_field_is_detoured_not_taken_even_when_the_detour_is_long(bool withCrops)
    {
        var w = Physics1122Tests.World();
        w.Fill(-4, 62, -12, 10, 62, 12, Stone);          // ground (feet 63)
        w.Fill(2, 63, -6, 2, 64, -1, Stone);             // a wall two blocks high, 13 wide ...
        w.Fill(2, 63, 1, 2, 64, 6, Stone);
        w.Set(2, 63, 0, Farmland);                       // ... with one low spot: a raised field strip (feet 63.94), hopped onto
        if (withCrops) w.Set(2, 64, 0, Wheat);
        var r = Pathfinder.FindPath(w, new BlockPos(0, 63, 0), new GoalBlock(4, 63, 0), Opts1122);
        Assert.Equal(PathStatus.Found, r.Status);
        Assert.DoesNotContain(r.Steps, s => OnField(w, s));
        Assert.Contains(r.Steps, s => Math.Abs(s.Z) >= 6); // round the wall's end (z = 7), not over the field
    }

    [Fact]
    public void A_long_field_is_walked_around_but_crossed_straight_when_the_target_is_on_it()
    {
        var w = TestWorld.Create();                       // b1.8.1 table: farmland is a full block, walking on it is a plain walk
        w.Fill(-4, 63, -20, 20, 63, 20, Cobble);
        w.Fill(2, 63, -14, 8, 63, 14, Farmland);          // 7 wide, 29 long: going round costs ~110 ticks, across ~100 sneaking
        var around = Pathfinder.FindPath(w, new BlockPos(0, 64, 0), new GoalBlock(10, 64, 0), Strict);
        Assert.Equal(PathStatus.Found, around.Status);
        Assert.DoesNotContain(around.Steps, s => w.GetBlock(s.X, s.Y - 1, s.Z) == Farmland);

        var onto = Pathfinder.FindPath(w, new BlockPos(0, 64, 0), new GoalBlock(6, 64, 0), Strict);   // planting in the middle
        Assert.Equal(PathStatus.Found, onto.Status);
        Assert.Equal(new BlockPos(6, 64, 0), onto.Steps[^1].Pos);
        Assert.Equal(6, onto.Steps.Count);                                                             // straight in, no detour
    }
}
