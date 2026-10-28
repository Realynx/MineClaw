using MineClaw.Agent.Rag;
using MineClaw.Bot.Blueprints;
using MineClaw.Bot.Building;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Bot.Tests;

/// <summary>
/// The build slicer (Bot/Building): ordering on small shapes, every step legal when it runs (a solid clicked face within reach
/// of the planned stand), stands chosen through the real pathfinder (doors), infeasibility reported before building, building
/// down with a water column (and the underside place for 1–2 below), resume after an interruption, deviation re-plans, the
/// confirmation gate (correction, timeout, restart), undo, partition.
/// </summary>
public class BuildPlannerTests
{
    private static VoxelPlan PlanOf(string dsl, BlockPos origin)
    {
        var (plan, issues) = BlueprintNormalizer.FromText(dsl, origin);
        Assert.NotNull(plan);
        Assert.DoesNotContain(issues, i => i.Level == IssueLevel.Error);
        return plan!;
    }

    private static BlockPos Start(WorldState w) => Pathfinder.StartCellFor(w.PlanningView, w.Player.Position)!.Value;

    private static BuildPlan Slice(VoxelPlan plan, WorldState w, BuildPlannerOptions? o = null) =>
        BuildPlanner.Plan(plan, w.PlanningView, Start(w), null, o ?? new BuildPlannerOptions { GateBlocks = 0 });

    /// <summary>Re-runs the plan on a copy of the world and checks every step is legal at its turn.</summary>
    private static void Replay(BuildPlan bp, Func<WorldState> fresh)
    {
        var w = fresh();
        foreach (var s in bp.Steps)
        {
            switch (s.Action)
            {
                case StepAction.Place or StepAction.Scaffold:
                    {
                        Assert.True(BuildBlocks.Supports(w.GetBlock(s.Against)), $"{s}: nothing solid to click at {s.Against}");
                        var eye = s.Column >= 0 ? new Vec3d(s.Stand.X + 0.5, s.Stand.Y + PlayerPhysics.EyeHeight, s.Stand.Z + 0.5)
                            : GoalUseBlock.EyeAt(w, s.Stand.X, s.Stand.Y, s.Stand.Z);
                        var n = s.Face!.Value.Normal();
                        var point = new Vec3d(s.Target.X + 0.5 - n.X * 0.5, s.Target.Y + 0.5 - n.Y * 0.5, s.Target.Z + 0.5 - n.Z * 0.5);
                        Assert.True(eye.DistanceTo(point) <= 4.5 + 1e-6, $"{s}: face {eye.DistanceTo(point):0.00} from the eyes");
                        Assert.False(AABB.ForPlayer(eye with { Y = eye.Y - PlayerPhysics.EyeHeight }).Intersects(AABB.Block(s.Target.X, s.Target.Y, s.Target.Z)), $"{s}: stands in it");
                        w.SetBlock(s.Target.X, s.Target.Y, s.Target.Z, s.Action == StepAction.Scaffold ? Blocks.Cobblestone : s.Id, s.Meta);
                        break;
                    }
                case StepAction.Clear or StepAction.RemoveScaffold:
                    w.SetBlock(s.Target.X, s.Target.Y, s.Target.Z, 0, 0);
                    break;
            }
        }
    }

    // ------------------------------------------------------------------ slicing and ordering

    [Fact]
    public void Hut_slices_bottom_up_with_every_step_legal_and_the_door_faced_right()
    {
        var w = BlueprintTests.Island();
        var plan = PlanOf(BlueprintTests.Hut, new BlockPos(-2, 64, -3));
        var bp = Slice(plan, w);
        Assert.Empty(bp.Infeasible);
        Assert.Equal(plan.Blocks.Values.Count(b => !b.UpperDoor), bp.Placements);
        var ys = bp.Steps.Where(s => s.Action == StepAction.Place).Select(s => s.Target.Y).ToList();
        Assert.Equal(ys.OrderBy(y => y), ys); // support first: layer by layer
        Replay(bp, () => BlueprintTests.Island());
        var door = bp.Steps.Single(s => s.Id == Blocks.WoodenDoor);
        Assert.Equal(BlockFace.Top, door.Face);
        Assert.Equal(BuildPlanner.DirYaw(BuildBlocks.DoorPlacerFacing(door.Meta)), door.Yaw);
        Assert.Contains("placements", bp.Summary());
    }

    [Fact]
    public async Task Floating_platform_gets_scaffold_that_comes_down_top_first()
    {
        var w = BlueprintTests.Island(4);
        var plan = PlanOf("size 3x3x3\npalette C=cobblestone\nlayer 2\nCCC\nCCC\nCCC", new BlockPos(-1, 64, -1));
        var bp = Slice(plan, w);
        Assert.Empty(bp.Infeasible);
        Assert.True(bp.Scaffolds > 0);
        Replay(bp, () => BlueprintTests.Island(4));
        var body = new SimBuildBody(w);
        var report = await new ConstructionExecutor(new BlueprintOptions()).RunAsync(1, plan, BuildPart.Whole(plan), body, CancellationToken.None);
        Assert.True(report.Complete, report.Text("platform"));
        Assert.Equal(report.Scaffolded, report.ScaffoldRemoved);
        foreach (var b in plan.Blocks.Values) Assert.Equal(Blocks.Cobblestone, w.GetBlock(b.Pos));
        Assert.Equal(0, w.GetBlock(0, 64, 0) | w.GetBlock(0, 65, 0)); // the scaffold pillar is gone
    }

    [Fact]
    public async Task Staircase_steps_face_up_the_way_they_were_drawn()
    {
        var w = BlueprintTests.Island();
        var plan = PlanOf("size 1x4x4\npalette S=cobblestone_stairs:north C=cobblestone\nlayer 0\n.\n.\n.\nS\nlayer 1\n.\n.\nS\nC\nlayer 2\n.\nS\nC\nC\nlayer 3\nS\nC\nC\nC",
            new BlockPos(0, 64, -2));
        var bp = Slice(plan, w);
        Assert.Empty(bp.Infeasible);
        Assert.All(bp.Steps.Where(s => s.Id == Blocks.CobblestoneStairs), s => Assert.Equal(BuildPlanner.DirYaw(Dir.North), s.Yaw));
        var body = new SimBuildBody(w) { Inv = { [(Blocks.CobblestoneStairs, 0)] = 8 } };
        var report = await new ConstructionExecutor(new BlueprintOptions()).RunAsync(1, plan, BuildPart.Whole(plan), body, CancellationToken.None);
        Assert.True(report.Complete, report.Text("stairs"));
        Assert.Empty(report.Deviations);
        foreach (var b in plan.Blocks.Values.Where(b => b.Id == Blocks.CobblestoneStairs))
            Assert.Equal(Dir.North, BuildBlocks.StairsAscend(w.GetMeta(b.Pos))); // the meta the server derives from the yaw
    }

    [Fact]
    public async Task Overhang_bridges_out_from_the_edge_without_scaffold()
    {
        var w = BlueprintTests.Island();
        var plan = PlanOf("size 4x1x1\npalette C=cobblestone\nlayer 0\nCCCC", new BlockPos(7, 63, 0)); // floor level, east of the edge (x 6)
        var bp = Slice(plan, w);
        Assert.Empty(bp.Infeasible);
        Assert.Equal(0, bp.Scaffolds);
        Assert.Contains(bp.Steps, s => s.Technique is Technique.Edge or Technique.Hidden); // the edge block's outer face
        Replay(bp, () => BlueprintTests.Island());
        var body = new SimBuildBody(w);
        var report = await new ConstructionExecutor(new BlueprintOptions()).RunAsync(1, plan, BuildPart.Whole(plan), body, CancellationToken.None);
        Assert.True(report.Complete, report.Text("overhang"));
    }

    // ------------------------------------------------------------------ infeasible, doors

    [Fact]
    public async Task Infeasible_blocks_are_reported_before_anything_is_built()
    {
        var w = BlueprintTests.Island(3);
        var plan = PlanOf("size 3x1x3\npalette C=cobblestone\nlayer 0\nCCC\nCCC\nCCC", new BlockPos(1, 50, -1)); // 13 below, no water bucket
        var bp = Slice(plan, w);
        Assert.NotEmpty(bp.Infeasible);
        Assert.Contains(bp.Infeasible, i => i.Why.Contains("water", StringComparison.OrdinalIgnoreCase) || i.Why.Contains("scaffold", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("can't place", bp.Summary());
        var body = new SimBuildBody(w);
        var report = await new ConstructionExecutor(new BlueprintOptions()).RunAsync(1, plan, BuildPart.Whole(plan), body, CancellationToken.None);
        Assert.NotEmpty(report.Unreachable);
        Assert.False(report.Complete);
    }

    [Fact]
    public void A_block_inside_a_closed_room_is_placed_from_inside_through_the_door()
    {
        var w = BlueprintTests.Island();
        for (int x = -2; x <= 2; x++) for (int z = -2; z <= 2; z++) for (int y = 64; y <= 66; y++)
            if (Math.Abs(x) == 2 || Math.Abs(z) == 2) w.SetBlock(x, y, z, Blocks.Cobblestone, 0);
        w.SetBlock(0, 64, 2, Blocks.WoodenDoor, 3);
        w.SetBlock(0, 65, 2, Blocks.WoodenDoor, 3 | 8);
        for (int x = -2; x <= 2; x++) for (int z = -2; z <= 2; z++) w.SetBlock(x, 67, z, Blocks.Planks, 0); // a roof: no stand above
        var plan = PlanOf("size 1x1x1\npalette T=crafting_table\nlayer 0\nT", new BlockPos(0, 64, -1));
        var bp = Slice(plan, w, new BuildPlannerOptions { GateBlocks = 0, AllowHidden = false });
        Assert.Empty(bp.Infeasible);
        var step = bp.Steps.Single(s => s.Action == StepAction.Place);
        Assert.True(Math.Abs(step.Stand.X) <= 1 && step.Stand.Z is >= -1 and <= 2, step.ToString()); // inside, or in the doorway

        var path = Pathfinder.FindPath(w, Start(w), new GoalBlock(step.Stand), PathOptions.Default with { AllowOpenDoors = true, AllowPartial = false });
        Assert.True(path.Success, path.ToString());
        Assert.Contains(path.Steps, s => s.Kind == MoveKind.OpenDoor);
    }

    // ------------------------------------------------------------------ building down

    private static Dictionary<(int, int), int> WithWater() =>
        new() { [(Blocks.Cobblestone, 0)] = 256, [(Items.WaterBucket, 0)] = 1 };

    [Fact]
    public async Task A_platform_six_below_the_floor_is_built_from_a_water_column_and_the_water_comes_back()
    {
        var w = BlueprintTests.Island(3);
        var plan = PlanOf("size 3x1x3\npalette C=cobblestone\nlayer 0\nCCC\nCCC\nCCC", new BlockPos(1, 57, -1)); // floor top y 63
        var bp = Slice(plan, w, new BuildPlannerOptions { GateBlocks = 0, WaterAvailable = true, MaxScaffold = 12 });
        Assert.Empty(bp.Infeasible);
        Assert.Single(bp.Columns); // one descent placed to cover the whole platform
        Assert.Contains(bp.Steps, s => s.Action == StepAction.PourWater);
        Assert.Contains(bp.Steps, s => s.Action == StepAction.PickupWater);
        Assert.Contains(bp.Steps, s => s.Action == StepAction.Place && s.Technique == Technique.Water);
        Assert.Contains(bp.Steps, s => s.Technique == Technique.Hidden); // the first chain blocks hang under the floor
        Assert.Contains("water descent", bp.Summary());

        var body = new SimBuildBody(w, WithWater());
        var report = await new ConstructionExecutor(new BlueprintOptions()).RunAsync(1, plan, BuildPart.Whole(plan), body, CancellationToken.None);
        Assert.True(report.Complete, report.Text("platform") + " | " + string.Join(" / ", body.Log.TakeLast(6)));
        foreach (var b in plan.Blocks.Values) Assert.Equal(Blocks.Cobblestone, w.GetBlock(b.Pos));
        Assert.Contains(body.Log, l => l.StartsWith("pour"));
        Assert.Contains(body.Log, l => l.StartsWith("swim to"));
        Assert.Contains(body.Log, l => l.StartsWith("pickup"));
        Assert.Contains(body.Log, l => l.StartsWith("place") && l.EndsWith("swimming"));
        Assert.Equal(1, body.Count(Items.WaterBucket, 0)); // the water was picked back up
        foreach (var col in report.Plan!.Columns)
            for (int y = 0; y <= 63; y++) Assert.False(BlockShapes.IsWater(w.GetBlock(col.Source.X, y, col.Source.Z)), $"water left at y {y}");
        Assert.False(body.InWater);
        Assert.Equal(report.Scaffolded, report.ScaffoldRemoved); // the hanging chain came down too
    }

    [Fact]
    public void One_or_two_below_the_floor_is_an_underside_place_without_water()
    {
        var w = BlueprintTests.Island(3);
        var plan = PlanOf("size 1x2x1\npalette C=cobblestone\nlayer 0\nC\nlayer 1\nC", new BlockPos(3, 61, 0)); // under the edge block
        var bp = Slice(plan, w);
        Assert.Empty(bp.Infeasible);
        Assert.Empty(bp.Columns);
        Assert.All(bp.Steps.Where(s => s.Action == StepAction.Place), s => Assert.True(s.Technique is Technique.Hidden or Technique.Edge, s.ToString()));
        Assert.Contains(bp.Steps, s => s.Face == BlockFace.Bottom); // clicked the underside
        Replay(bp, () => BlueprintTests.Island(3));
    }

    // ------------------------------------------------------------------ resume, deviation

    [Fact]
    public async Task A_killed_build_resumes_from_the_db_and_finishes()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mineclaw-bplan-{Guid.NewGuid():N}.db");
        try
        {
            var w = BlueprintTests.Island();
            var plan = PlanOf(BlueprintTests.Hut, new BlockPos(-2, 64, -3));
            using (var db = AgentDb.Open(path))
            {
                using var cts = new CancellationTokenSource();
                var body = new SimBuildBody(w) { PlaceLimit = 15, Stop = cts };
                var r1 = await new ConstructionExecutor(new BlueprintOptions { CheckpointEvery = 3 }, new BlueprintStore(db)).RunAsync(9, plan, BuildPart.Whole(plan), body, cts.Token);
                Assert.Equal("cancelled", r1.Stopped);
                var saved = new BuildPlanStore(db).Get(9, 0)!;
                Assert.True(saved.Steps.Count(s => s.Finished) >= 12);
                Assert.Equal("paused", saved.State);
            }
            using (var db = AgentDb.Open(path))
            {
                var body = new SimBuildBody(w);
                var r2 = await new ConstructionExecutor(new BlueprintOptions(), new BlueprintStore(db)).RunAsync(9, plan, BuildPart.Whole(plan), body, CancellationToken.None);
                Assert.True(r2.Complete, r2.Text("hut"));
                Assert.True(r2.Placed <= plan.Blocks.Count - 15, $"re-placed {r2.Placed}");
                var done = new BuildPlanStore(db).Get(9, 0)!;
                Assert.Equal("done", done.State);
                Assert.True(done.Replans >= 1); // the resumed plan, re-verified and re-planned
            }
        }
        finally { try { File.Delete(path); } catch (IOException) { } }
    }

    [Fact]
    public async Task Someone_building_in_a_planned_cell_is_a_deviation_and_the_rest_is_replanned()
    {
        var w = BlueprintTests.Island();
        var plan = PlanOf(BlueprintTests.Hut, new BlockPos(-2, 64, -3));
        var top = plan.Blocks.Values.Where(b => b.Pos.Y == plan.Origin.Y + 2 && b.Id == Blocks.Cobblestone).First().Pos;
        var body = new SimBuildBody(w) { AfterPlace = n => { if (n == 3) w.SetBlock(top.X, top.Y, top.Z, Blocks.Dirt, 0); } };
        var report = await new ConstructionExecutor(new BlueprintOptions()).RunAsync(1, plan, BuildPart.Whole(plan), body, CancellationToken.None);
        Assert.Contains(report.Deviations, d => d.Contains("someone built there"));
        Assert.DoesNotContain(body.Log, l => l == $"dig {top.X},{top.Y},{top.Z}");
        Assert.Equal(Blocks.Dirt, w.GetBlock(top)); // never dug: it's not ours
        Assert.Equal(new[] { top }, report.Missing);
        Assert.All(plan.Blocks.Values.Where(b => b.Pos != top && !b.UpperDoor), b => Assert.Equal(b.Id, w.GetBlock(b.Pos)));
    }

    [Fact]
    public async Task A_placed_block_that_went_missing_is_put_back_on_the_next_run()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mineclaw-bplan-{Guid.NewGuid():N}.db");
        try
        {
            var w = BlueprintTests.Island();
            var plan = PlanOf("size 3x1x1\npalette C=cobblestone\nlayer 0\nCCC", new BlockPos(0, 64, 0));
            using var db = AgentDb.Open(path);
            var ex = new ConstructionExecutor(new BlueprintOptions(), new BlueprintStore(db));
            Assert.True((await ex.RunAsync(3, plan, BuildPart.Whole(plan), new SimBuildBody(w), CancellationToken.None)).Complete);
            w.SetBlock(1, 64, 0, 0, 0);
            var r2 = await ex.RunAsync(3, plan, BuildPart.Whole(plan), new SimBuildBody(w), CancellationToken.None);
            Assert.True(r2.Complete, r2.Text("row"));
            Assert.Equal(1, r2.Placed);
        }
        finally { try { File.Delete(path); } catch (IOException) { } }
    }

    // ------------------------------------------------------------------ the confirmation gate

    private const string Bridge = "size 6x1x1\npalette C=cobblestone\nlayer 0\nCCCCCC"; // a line along +x from the island centre

    private static ConstructionExecutor Gated(BuildGates gates, List<string> asked, Func<int, GateReply?> answer, BlueprintStore? store = null, double minutes = 1) =>
        new(new BlueprintOptions { GateMinutes = (int)Math.Max(0, minutes) == 0 ? 0 : (int)minutes }, store)
        {
            Gates = gates,
            Ask = q =>
            {
                asked.Add(q);
                int n = asked.Count;
                if (answer(n) is { } reply)
                    _ = Task.Run(async () =>
                    {
                        for (int i = 0; i < 400 && !gates.IsWaiting(5); i++) await Task.Delay(5);
                        gates.Answer(5, reply);
                    });
                return Task.CompletedTask;
            },
        };

    [Fact]
    public async Task Gate_question_is_stable_across_a_restart_when_a_step_reverifies_differently()
    {
        // Live 04:07: after a restart the same paused gate said "First 3 blocks ... runs east" instead of "4 ... runs west".
        var w = BlueprintTests.Island();
        var plan = PlanOf(Bridge, new BlockPos(0, 64, 0));
        var gates = new BuildGates();
        var asked = new List<string>();
        BuildPlan? held = null;
        var ex = new ConstructionExecutor(new BlueprintOptions { GateMinutes = 1 })
        {
            Gates = gates,
            Ask = q => { asked.Add(q); return Task.CompletedTask; },
            GateQuestion = (bp, v) => { held = bp; return BuildGate.Question(bp, v, "bridge", _ => null, null, null); },
        };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var run = ex.RunAsync(7, plan, BuildPart.Whole(plan), new SimBuildBody(w), cts.Token);
        for (int i = 0; i < 400 && !gates.IsWaiting(7); i++) await Task.Delay(5);
        Assert.NotNull(held);
        var first = BuildGate.Question(held!, plan, "bridge", _ => null, null, null);
        // A restart re-verifies one gate step as unfinished (e.g. its chunk wasn't loaded yet).
        held!.Steps.First(s => s.Gate && s.Finished).Status = StepStatus.Pending;
        Assert.Equal(first, BuildGate.Question(held, plan, "bridge", _ => null, null, null));
        cts.Cancel();
        try { await run; } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task Gate_question_never_describes_the_build_relative_to_itself()
    {
        // Live 03:51: "the mob drop tower starts by a hangout, runs west, away from mob drop tower".
        var w = BlueprintTests.Island();
        var plan = PlanOf(Bridge, new BlockPos(0, 64, 0));
        var gates = new BuildGates();
        var asked = new List<string>();
        var ex = new ConstructionExecutor(new BlueprintOptions { GateMinutes = 1 })
        {
            Gates = gates,
            Ask = q =>
            {
                asked.Add(q);
                _ = Task.Run(async () =>
                {
                    for (int i = 0; i < 400 && !gates.IsWaiting(6); i++) await Task.Delay(5);
                    gates.Answer(6, new GateReply(GateVerdict.Approve, "PoofImaFox", "keep going"));
                });
                return Task.CompletedTask;
            },
            GateQuestion = (bp, v) => BuildGate.Question(bp, v, "mob drop tower",
                p => p.X < 3 ? "at mob_drop_tower" : "by the hangout",
                p => p.X < 3 ? new NamedPlace("mob drop tower", new BlockPos(1, 64, 0)) : new NamedPlace("hangout", new BlockPos(20, 64, 0)), null),
        };
        var report = await ex.RunAsync(6, plan, BuildPart.Whole(plan), new SimBuildBody(w), CancellationToken.None);
        Assert.True(report.Complete, report.Text("tower"));
        Assert.Single(asked);
        Assert.DoesNotContain("from mob drop tower", asked[0]);
        Assert.DoesNotContain("mob_drop_tower", asked[0]);
        Assert.Contains("the mob drop tower starts here", asked[0]);
        Assert.Contains("toward hangout", asked[0]);
    }

    [Fact]
    public async Task Wrong_way_at_the_gate_takes_the_gate_blocks_down_turns_the_plan_and_asks_again()
    {
        var w = BlueprintTests.Island();
        var plan = PlanOf(Bridge, new BlockPos(0, 64, 0));
        var gates = new BuildGates();
        var asked = new List<string>();
        var ex = Gated(gates, asked, n => n == 1
            ? BuildGate.Parse("wrong way", "PoofImaFox", new BlockPos(0, 64, 0), 'x')
            : new GateReply(GateVerdict.Approve, "PoofImaFox", "keep going"));
        var body = new SimBuildBody(w);
        var report = await ex.RunAsync(5, plan, BuildPart.Whole(plan), body, CancellationToken.None);
        Assert.True(report.Complete, report.Text("bridge"));
        Assert.Equal(2, asked.Count);                       // gated, corrected, gated again
        Assert.Contains("Keep going?", asked[0]);
        Assert.Contains("runs east", asked[0]);
        Assert.Contains("runs west", asked[1]);
        for (int x = 1; x <= 5; x++) Assert.Equal(0, w.GetBlock(x, 64, 0));           // the first gate blocks came down
        for (int x = -5; x <= 0; x++) Assert.Equal(Blocks.Cobblestone, w.GetBlock(x, 64, 0)); // built the other way
        Assert.Equal(2, report.Plan!.Transform.Quarters);
    }

    // A 3×3 platform 13 up on a column with a 1-wide arm out along +x: every arm cell is placed standing on the last one, both
    // sides of that stand a 13-block drop.
    private const string Mushroom = "size 11x13x3\npalette C=cobblestone\npillar C 1,0,1 12\nfloor C 0,12,0 2,12,2\nfloor C 3,12,1 10,12,1";

    [Fact]
    public async Task A_platform_over_solid_ground_grows_from_sneak_edge_stands_but_over_the_void_it_cant_be_climbed()
    {
        // Live 10-28 (design #4): the body up on the platform's placed middle, 13 above the island floor. Its rim drops onto solid
        // ground: stands there are allowed (sneak-at-edge: the client's EdgeGuard; never a jump), so the platform grows outward
        // ring by ring from its own placed cells, each cell resting on the last.
        var w = BlueprintTests.Island(8);
        var plan = PlanOf(Mushroom, new BlockPos(-1, 64, -1));
        for (int y = 64; y <= 75; y++) w.SetBlock(0, y, 0, Blocks.Cobblestone, 0);
        for (int x = -1; x <= 1; x++) for (int z = -1; z <= 1; z++) w.SetBlock(x, 76, z, Blocks.Cobblestone, 0);
        w.Player.ServerTeleport(new Vec3d(0.5, 77, 0.5), 78.62, 0, 0, true);
        var body = new SimBuildBody(w);
        var report = await new ConstructionExecutor(new BlueprintOptions()).RunAsync(1, plan, BuildPart.Whole(plan), body, CancellationToken.None);
        Assert.True(report.Complete, report.Text("platform"));
        Assert.Empty(body.Unguarded);
        var bp = report.Plan!;
        // Never stranded up there (live 10-28 16:20): no pillar of its own to dig, so it placed a stair down to the island.
        Assert.StartsWith("came down off the build: placed ", report.Exit);
        Assert.True(WayHome.AtHome(body.Feet0, bp.Home!.Value), $"left at {body.Feet0}");
        Assert.All(plan.Blocks.Values, b => Assert.Equal(b.Id, w.GetBlock(b.Pos))); // never digs the design to get down
        var placed = bp.Steps.Where(s => s.Action == StepAction.Place && !(s.Target.X is >= -1 and <= 1 && s.Target.Z is >= -1 and <= 1)).ToList();
        Assert.Equal(8, placed.Count);
        Assert.All(placed, s => Assert.Equal(77, s.Stand.Y)); // stood on the platform's own placed cells
        var edge = placed.Where(s => s.Technique == Technique.Edge).ToList();
        Assert.NotEmpty(edge); // at the rim, sneaking
        Assert.DoesNotContain(bp.Steps, s => s.Technique is Technique.Pillar or Technique.Stair);
        var done = BlueprintTests.Island(8);
        foreach (var b in plan.Blocks.Values) done.SetBlock(b.Pos.X, b.Pos.Y, b.Pos.Z, b.Id, b.Meta);
        Assert.All(edge, s => Assert.Equal(0, Safety.VoidNeighbourCount(done, s.Stand.X, s.Stand.Y, s.Stand.Z))); // a solid drop, never the void

        // Over the void (a 2-block island: me and the column's foot): every pillar or stair spot is beside the void, so nothing
        // climbs to it (the void-edge rules unchanged).
        var v = BlueprintTests.Island(0);
        v.SetBlock(1, 63, 0, Blocks.Stone, 0);
        v.Player.ServerTeleport(new Vec3d(0.5, 64, 0.5), 65.62, 0, 0, true);
        var vplan = PlanOf(Mushroom, new BlockPos(0, 64, -1)); // the column on the island's second block, beside me
        var vb = BuildPlanner.Plan(vplan, v.PlanningView, Pathfinder.StartCellFor(v.PlanningView, v.Player.Position)!.Value, null, new BuildPlannerOptions { GateBlocks = 0 });
        Assert.True(vb.Infeasible.Any(i => i.Why.Contains("beside the void")), string.Join(" | ", vb.Steps.Select(s => $"{s.Action} {s.Technique} {s.Target} @ {s.Stand}")) + " || " + string.Join(" | ", vb.Infeasible.Select(i => i.Pos + " " + i.Why)));
        Assert.DoesNotContain(vb.Steps, s => s.Technique is Technique.Pillar or Technique.Stair);
    }

    [Fact]
    public async Task A_no_at_the_gate_names_the_exact_blocks_and_undo_takes_down_only_those()
    {
        // Live 10-28: Robin said "no" at design #9's gate; a worker told to "remove the 5 blocks just placed" couldn't find them.
        var w = BlueprintTests.Island();
        // Someone else's blocks right beside the build: never touched.
        w.SetBlock(1, 64, 1, Blocks.Planks, 0);
        w.SetBlock(3, 64, -1, Blocks.Dirt, 0);
        var before = new Dictionary<BlockPos, int>();
        for (int x = -7; x <= 7; x++) for (int y = 60; y <= 68; y++) for (int z = -7; z <= 7; z++) before[new BlockPos(x, y, z)] = w.GetBlock(x, y, z);
        var plan = PlanOf(Bridge, new BlockPos(0, 64, 0));
        var gates = new BuildGates();
        var asked = new List<string>();
        var ex = Gated(gates, asked, _ => BuildGate.Parse("no", "kiwi", new BlockPos(0, 64, 0), 'x'));
        var body = new SimBuildBody(w);
        int cobble0 = body.Count(Blocks.Cobblestone, 0);
        var report = await ex.RunAsync(5, plan, BuildPart.Whole(plan), body, CancellationToken.None);
        Assert.True(report.AtGate);
        var bp = report.Plan!;
        var gateBlocks = bp.Gate.Blocks.ToList();
        Assert.InRange(gateBlocks.Count, 4, 5);
        Assert.Equal(body.Placed.ToHashSet(), gateBlocks.ToHashSet());
        // The "no" names the exact cells and the undo.
        Assert.Contains("kiwi said \"no\"", report.Stopped);
        Assert.Contains("gate blocks at " + string.Join(" ", gateBlocks.Select(p => $"{p.X},{p.Y},{p.Z}")), report.Stopped);
        Assert.EndsWith("undo with design undo{id=5}", report.Stopped);

        // A player swaps one of ours for their own block meanwhile: it is theirs now.
        var swapped = gateBlocks[^1];
        w.SetBlock(swapped.X, swapped.Y, swapped.Z, Blocks.Stone, 0);
        int n = await ConstructionExecutor.UndoPlacedAsync(body, bp, w, CancellationToken.None);
        Assert.Equal(gateBlocks.Count - 1, n);
        foreach (var (p, id) in before)
        {
            if (p == swapped) Assert.Equal(Blocks.Stone, w.GetBlock(p));
            else Assert.Equal(id, w.GetBlock(p)); // exactly the placed blocks came down, nothing else
        }
        Assert.Empty(bp.Placed);
        Assert.Empty(bp.Gate.Blocks);
        Assert.Equal(cobble0, body.Count(Blocks.Cobblestone, 0) + 1); // refunded, all but the swapped one
        Assert.Equal(0, await ConstructionExecutor.UndoPlacedAsync(body, bp, w, CancellationToken.None)); // nothing left of ours
    }

    [Fact]
    public async Task No_answer_at_the_gate_stays_paused_with_no_default()
    {
        var w = BlueprintTests.Island();
        var plan = PlanOf(Bridge, new BlockPos(0, 64, 0));
        var asked = new List<string>();
        var ex = new ConstructionExecutor(new BlueprintOptions { GateMinutes = 0 }) { Gates = new BuildGates(), Ask = q => { asked.Add(q); return Task.CompletedTask; } };
        var body = new SimBuildBody(w);
        var report = await ex.RunAsync(5, plan, BuildPart.Whole(plan), body, CancellationToken.None);
        Assert.True(report.AtGate);
        Assert.StartsWith("paused at the confirmation gate", report.Stopped);
        Assert.Single(asked);
        Assert.InRange(body.Placed.Count, 4, 5);             // only the gate blocks
        Assert.Equal(GateState.Waiting, report.Plan!.Gate.State);
    }

    [Fact]
    public async Task A_restart_while_waiting_at_the_gate_asks_again_instead_of_building()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mineclaw-bplan-{Guid.NewGuid():N}.db");
        try
        {
            var w = BlueprintTests.Island();
            var plan = PlanOf(Bridge, new BlockPos(0, 64, 0));
            using var db = AgentDb.Open(path);
            var asked = new List<string>();
            var r1 = await new ConstructionExecutor(new BlueprintOptions { GateMinutes = 0 }, new BlueprintStore(db))
                { Gates = new BuildGates(), Ask = q => { asked.Add(q); return Task.CompletedTask; } }
                .RunAsync(5, plan, BuildPart.Whole(plan), new SimBuildBody(w), CancellationToken.None);
            Assert.True(r1.AtGate);
            Assert.Equal(GateState.Waiting, new BuildPlanStore(db).Get(5, 0)!.Gate.State);
            // The process restarts: the build comes back, asks again before any new block, then carries on with the yes.
            var gates = new BuildGates();
            var asked2 = new List<string>();
            var body = new SimBuildBody(w);
            int placedBeforeAsk = -1;
            var ex = Gated(gates, asked2, _ => { placedBeforeAsk = body.Placed.Count; return new GateReply(GateVerdict.Approve, "PoofImaFox", "yes"); }, new BlueprintStore(db));
            var r2 = await ex.RunAsync(5, plan, BuildPart.Whole(plan), body, CancellationToken.None);
            Assert.Equal(0, placedBeforeAsk);
            Assert.Single(asked2);
            Assert.True(r2.Complete, r2.Text("bridge"));
            Assert.Equal(GateState.Approved, new BuildPlanStore(db).Get(5, 0)!.Gate.State);
        }
        finally { try { File.Delete(path); } catch (IOException) { } }
    }

    [Theory]
    [InlineData("yes", GateVerdict.Approve)]
    [InlineData("keep going", GateVerdict.Approve)]
    [InlineData("looks good, carry on", GateVerdict.Approve)]
    [InlineData("wrong way", GateVerdict.Correct)]
    [InlineData("turn it left", GateVerdict.Correct)]
    [InlineData("flip it", GateVerdict.Correct)]
    [InlineData("move it 2 east", GateVerdict.Correct)]
    [InlineData("undo", GateVerdict.Undo)]
    [InlineData("no wait", GateVerdict.Hold)]
    public void Gate_answers_are_read_without_a_default(string text, GateVerdict verdict)
    {
        var r = BuildGate.Parse(text, "Fox", default, 'x');
        Assert.Equal(verdict, r?.Verdict);
        Assert.Null(BuildGate.Parse("what are you building?", "Fox", default, 'x'));
        if (text == "move it 2 east") Assert.Equal(2, r!.Correction!.Dx);
        if (text == "turn it left") Assert.Equal(3, r!.Correction!.Quarters);
        if (text == "flip it") Assert.True(r!.Correction!.Mirror);
    }

    [Fact]
    public async Task Undo_takes_down_everything_the_build_placed_and_nothing_else()
    {
        var w = BlueprintTests.Island();
        var plan = PlanOf(BlueprintTests.Hut, new BlockPos(-2, 64, -3));
        var body = new SimBuildBody(w);
        var report = await new ConstructionExecutor(new BlueprintOptions()).RunAsync(1, plan, BuildPart.Whole(plan), body, CancellationToken.None);
        Assert.True(report.Complete);
        int n = await ConstructionExecutor.UndoPlacedAsync(body, report.Plan!, w, CancellationToken.None);
        Assert.Equal(plan.Blocks.Count, n);
        foreach (var b in plan.Blocks.Values) Assert.Equal(0, w.GetBlock(b.Pos));
        for (int x = -6; x <= 6; x++) Assert.Equal(Blocks.Stone, w.GetBlock(x, 63, 0)); // the island floor stays
    }

    [Fact]
    public void Partition_gives_disjoint_step_sets_with_waits_across_parts()
    {
        var w = BlueprintTests.Island(8);
        var plan = PlanOf("size 12x2x3\npalette C=cobblestone\nwalls C 0,0,0 11,1,2", new BlockPos(-6, 64, -1));
        var bp = Slice(plan, w);
        var parts = bp.Partition(plan, 2);
        Assert.Equal(2, parts.Count);
        Assert.Equal(bp.Steps.Count, parts.Sum(p => p.Steps.Count));
        Assert.Empty(parts[0].Steps.Select(s => s.Index).Intersect(parts[1].Steps.Select(s => s.Index)));
        foreach (var p in parts)
            foreach (var (step, deps) in p.Waits)
                Assert.All(deps, d => Assert.DoesNotContain(p.Steps, s => s.Index == d)); // a wait is always on the other part
    }
}
