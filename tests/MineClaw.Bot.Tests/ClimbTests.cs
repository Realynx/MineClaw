using MineClaw.Bot.Blueprints;
using MineClaw.Bot.Building;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Bot.Tests;

/// <summary>
/// Climbing in the build slicer (Bot/Building/BuildPlanner.Climb): a build taller than reach goes up on the design's own blocks,
/// a pillar jumped up under the body (void-free on every side) or a scaffold stair, and the scaffold comes down top-first with
/// the body back on the ground; every step legal for the strict sim body; the void guard never leaves the body on a 1×1 over
/// the void; where every climb spot is beside the void, "above my reach" says why.
/// </summary>
public class ClimbTests(Xunit.Abstractions.ITestOutputHelper log)
{
    private static VoxelPlan PlanOf(string dsl, BlockPos origin)
    {
        var (plan, issues) = BlueprintNormalizer.FromText(dsl, origin);
        Assert.NotNull(plan);
        Assert.DoesNotContain(issues, i => i.Level == IssueLevel.Error);
        return plan!;
    }

    internal const string HollowTower = """
        blueprint hollow tower
        size 3x12x3
        palette C=cobblestone
        walls C 0,0,0 2,11,2
        """;

    /// <summary>
    /// Re-runs the plan on a fresh world and checks every step at its turn: a placement clicks a solid face within 4.5 of the
    /// stand's eyes and never stands in its cell; a pillar step stands in the very cell it fills, on a full block, the void off
    /// every side; a pillar removal stands on the block it digs and drops one block onto a full block.
    /// </summary>
    internal static void Replay(BuildPlan bp, WorldState w)
    {
        foreach (var s in bp.Steps)
        {
            switch (s.Action)
            {
                case StepAction.Scaffold when s.Technique == Technique.Pillar:
                    Assert.Equal(s.Target, s.Stand);
                    Assert.True(BuildBlocks.NormalCube(w.GetBlock(s.Target.Down())), $"{s}: nothing to pillar on");
                    Assert.Equal(0, w.GetBlock(s.Target));
                    Assert.True(Safety.VoidNeighbourCount(w, s.Target.X, s.Target.Y, s.Target.Z) == 0, $"{s}: jumps beside the void");
                    Assert.True(Safety.VoidNeighbourCount(w, s.Target.X, s.Target.Y + 1, s.Target.Z) == 0, $"{s}: lands beside the void");
                    w.SetBlock(s.Target.X, s.Target.Y, s.Target.Z, Blocks.Cobblestone, 0);
                    break;
                case StepAction.Place or StepAction.Scaffold:
                    {
                        Assert.True(BuildBlocks.Supports(w.GetBlock(s.Against)), $"{s}: nothing solid to click at {s.Against}");
                        var eye = GoalUseBlock.EyeAt(w, s.Stand.X, s.Stand.Y, s.Stand.Z);
                        var n = s.Face!.Value.Normal();
                        var point = new Vec3d(s.Target.X + 0.5 - n.X * 0.5, s.Target.Y + 0.5 - n.Y * 0.5, s.Target.Z + 0.5 - n.Z * 0.5);
                        Assert.True(eye.DistanceTo(point) <= 4.5 + 1e-6, $"{s}: face {eye.DistanceTo(point):0.00} from the eyes");
                        Assert.False(AABB.ForPlayer(eye with { Y = eye.Y - PlayerPhysics.EyeHeight }).Intersects(AABB.Block(s.Target.X, s.Target.Y, s.Target.Z)), $"{s}: stands in it");
                        w.SetBlock(s.Target.X, s.Target.Y, s.Target.Z, s.Action == StepAction.Scaffold ? Blocks.Cobblestone : s.Id, s.Meta);
                        break;
                    }
                case StepAction.RemoveScaffold when s.Technique == Technique.Pillar:
                    Assert.Equal(s.Target.Up(), s.Stand);
                    Assert.True(BuildBlocks.NormalCube(w.GetBlock(s.Target.Down())), $"{s}: no footing one below");
                    w.SetBlock(s.Target.X, s.Target.Y, s.Target.Z, 0, 0);
                    break;
                case StepAction.Clear or StepAction.RemoveScaffold:
                    if (s.Note == null)
                    {
                        var eye = GoalUseBlock.EyeAt(w, s.Stand.X, s.Stand.Y, s.Stand.Z);
                        Assert.True(Aiming.FindDigAim(w, eye, s.Target, 4.5) != null, $"{s}: can't dig it from there");
                    }
                    w.SetBlock(s.Target.X, s.Target.Y, s.Target.Z, 0, 0);
                    break;
            }
        }
    }

    [Fact]
    public async Task A_3x3x12_hollow_tower_on_a_floor_goes_up_complete_and_every_scaffold_block_comes_down()
    {
        var w = BlueprintTests.Island(4);
        var plan = PlanOf(HollowTower, new BlockPos(-1, 64, -1));
        Assert.Equal(96, plan.Blocks.Count);
        var start = Pathfinder.StartCellFor(w.PlanningView, w.Player.Position)!.Value;
        var bp = BuildPlanner.Plan(plan, w.PlanningView, start, null, new BuildPlannerOptions { GateBlocks = 0 });
        log.WriteLine(bp.Summary());
        Assert.Empty(bp.Infeasible);
        Assert.Equal(96, bp.Placements);
        // From the floor (feet y64) the top 5 layers are out of reach: a pillar beside the tower (the interior is sealed), every
        // block of it taken back down from on top, top first.
        var up = bp.Steps.Where(s => s.Action == StepAction.Scaffold).ToList();
        var down = bp.Steps.Where(s => s.Action == StepAction.RemoveScaffold).ToList();
        Assert.NotEmpty(up);
        Assert.All(up, s => Assert.Equal(Technique.Pillar, s.Technique));
        Assert.Equal(up.Select(s => s.Target).OrderBy(p => p.Y), down.Select(s => s.Target).OrderBy(p => p.Y));
        Assert.Equal(down.Select(s => s.Target.Y).OrderByDescending(y => y), down.Select(s => s.Target.Y));
        Assert.True(down.Min(s => s.Index) > bp.Steps.Where(s => s.Action == StepAction.Place).Max(s => s.Index), "scaffold down after its last dependant");
        Assert.All(up, s => Assert.False(plan.Blocks.ContainsKey(s.Target)));
        Assert.Contains("to climb: ", bp.Summary());
        Assert.Contains($"{up.Count} scaffold block", bp.Summary());
        Replay(bp, BlueprintTests.Island(4));

        // Built by the strict sim body: every step from its stand (reach, the clicked face, the pillar cell), nothing left.
        var world = BlueprintTests.Island(4);
        var body = new SimBuildBody(world);
        var report = await new ConstructionExecutor(new BlueprintOptions()).RunAsync(11, plan, BuildPart.Whole(plan), body, CancellationToken.None);
        log.WriteLine(report.Text("tower"));
        Assert.True(report.Complete, report.Text("tower"));
        Assert.Equal(0, report.Failed);
        Assert.Empty(report.Deviations);
        Assert.Equal(96, report.Placed);
        Assert.True(report.Scaffolded > 0);
        Assert.Equal(report.Scaffolded, report.ScaffoldRemoved);
        Assert.Empty(report.Plan!.ScaffoldUp);
        foreach (var b in plan.Blocks.Values) Assert.Equal(Blocks.Cobblestone, world.GetBlock(b.Pos));
        foreach (var s in report.Plan.Steps.Where(s => s.Action == StepAction.Scaffold)) Assert.Equal(0, world.GetBlock(s.Target));
        Assert.Equal(report.Scaffolded, body.Pillared);
        Assert.Empty(body.Unguarded);
        Assert.Equal(64, body.Feet0.Y); // back on the floor
        Assert.True(body.Trail.Max(c => c.Y) >= 70, "it climbed");
    }

    [Fact]
    public void The_design_s_own_blocks_are_climbed_before_any_scaffold()
    {
        // A solid stepped pyramid: each layer's top is a stand one up from the last; nothing extra is needed to reach the peak.
        var w = BlueprintTests.Island(6);
        var dsl = "blueprint steps\nsize 9x8x1\npalette C=cobblestone\n"
                  + string.Join("\n", Enumerable.Range(0, 8).Select(y => $"box C {y},{y},0 8,{y},0"));
        var plan = PlanOf(dsl, new BlockPos(-4, 64, 0));
        var bp = BuildPlanner.Plan(plan, w.PlanningView, Pathfinder.StartCellFor(w.PlanningView, w.Player.Position)!.Value, null,
            new BuildPlannerOptions { GateBlocks = 0 });
        Assert.Empty(bp.Infeasible);
        Assert.DoesNotContain(bp.Steps, s => s.Action is StepAction.Scaffold or StepAction.RemoveScaffold);
        Assert.Contains(bp.Steps, s => s.Action == StepAction.Place && s.Stand.Y >= 68); // stood on its own steps
        Replay(bp, BlueprintTests.Island(6));
    }

    [Fact]
    public async Task The_void_guard_never_leaves_the_body_on_a_1x1_over_the_void()
    {
        // A 1-wide walkway over the void and a wall 9 high across its middle: every pillar or stair spot has the void beside it.
        var w = new WorldState();
        w.ApplyChunkRegion(-16, 0, -16, 32, 128, 32, new byte[32 * 128 * 32], null, null, null);
        for (int x = -6; x <= 6; x++) w.SetBlock(x, 63, 0, Blocks.Stone, 0);
        w.Player.ServerTeleport(new Vec3d(5.5, 64, 0.5), 65.62, 0, 0, true);
        var plan = PlanOf("blueprint wall\nsize 3x9x1\npalette C=cobblestone\nbox C 0,0,0 2,8,0", new BlockPos(-1, 64, 0));
        var bp = BuildPlanner.Plan(plan, w.PlanningView, Pathfinder.StartCellFor(w.PlanningView, w.Player.Position)!.Value, null,
            new BuildPlannerOptions { GateBlocks = 0 });
        log.WriteLine(bp.Summary());
        Assert.DoesNotContain(bp.Steps, s => s.Technique is Technique.Pillar or Technique.Stair);
        Assert.Contains(bp.Infeasible, i => i.Why.StartsWith(BuildSkips.AboveReach + " (I can't climb: every pillar or stair spot near it is beside the void)"));
        Assert.Contains("no stand: above my reach (I can't climb)", BuildSkips.OfPlan(bp));

        var body = new SimBuildBody(w);
        var report = await new ConstructionExecutor(new BlueprintOptions()).RunAsync(12, plan, BuildPart.Whole(plan), body, CancellationToken.None);
        log.WriteLine(report.Text("wall"));
        Assert.Equal(0, report.Failed);
        Assert.True(report.Placed > 0);
        Assert.Contains("above my reach (I can't climb)", report.Skips);
        Assert.Empty(body.Unguarded);
        Assert.Equal(0, body.Pillared);

        // The same wall on a floor wide enough: a pillar goes up where all eight sides are floor, and the body never stands on a
        // 1×1 with the void beside it.
        var wide = BlueprintTests.Island(3);
        wide.Player.ServerTeleport(new Vec3d(0.5, 64, 2.5), 65.62, 0, 0, true);
        var wideBody = new SimBuildBody(wide);
        var r2 = await new ConstructionExecutor(new BlueprintOptions()).RunAsync(13, plan, BuildPart.Whole(plan), wideBody, CancellationToken.None);
        log.WriteLine(r2.Text("wide"));
        Assert.True(r2.Complete, r2.Text("wide"));
        Assert.True(wideBody.Pillared > 0);
        Assert.Empty(wideBody.Unguarded);
        foreach (var s in r2.Plan!.Steps.Where(s => s.Technique == Technique.Pillar && s.Action == StepAction.Scaffold))
            Assert.InRange(Math.Max(Math.Abs(s.Target.X), Math.Abs(s.Target.Z)), 0, 2); // inside the floor's rim
    }
}
