using MineClaw.Agent.Rag;
using MineClaw.Bot.Blueprints;
using MineClaw.Bot.Building;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Bot.Tests;

/// <summary>
/// Live 10-28 16:15–16:20 (alice): main finished design #4 (the drop tower, 748/748) up on its platform, a deploy restart woke it
/// there at night, seven zombies came, "disengaging inward" / "retreating inward to eat" found nowhere to go ("there was no path
/// from up there back to the main island") and it died at -26,71,14. The cause: "a way down" meant back to the level the
/// current slicing started on. A resume, a deviation re-plan or a crew's finale sliced from up on the platform made the platform
/// that level, so the climb's scaffold (a stair, or a pillar the slicer no longer knew as its own) "kept a way down" to where
/// the body already stood and came down from above. Now the plan keeps the first slicing's ground (<see cref="BuildPlan.Home"/>)
/// and every run that ends up there with no walk home comes down first (its own pillar, else a stair: <see cref="ExitRoute"/>).
/// </summary>
public class TowerTopExitTests(Xunit.Abstractions.ITestOutputHelper log)
{
    private static SimBuildBody Body(WorldState w) => new(w, new() { [(Blocks.Cobblestone, 0)] = 1024, [(Blocks.Slab, 3)] = 300 });

    [Fact]
    public async Task A_tower_top_finish_resumed_from_the_platform_ends_with_the_body_back_on_the_island()
    {
        var (w, design, store, ex, _, db) = DropTowerReplayTests.Live1455();
        using var _db = db;
        var body = Body(w);
        // The run stops up on the platform (a restart mid-build), 200 placements in.
        using var cts = new CancellationTokenSource();
        body.PlaceLimit = 200;
        body.Stop = cts;
        var first = await ex.RunAsync(4, design, BuildPart.Whole(design), body, cts.Token);
        Assert.Equal("cancelled", first.Stopped);
        Assert.True(body.Feet0.Y >= 76, $"stopped at {body.Feet0}");
        // The saved plan as a crew/older run left it: the climb is a stair (not a pillar the slicer digs from on top), and the plan
        // has no recorded ground (sliced before Home existed).
        var saved = ex.Plans!.Get(4, 0)!;
        Assert.NotEmpty(saved.ScaffoldUp);
        foreach (var s in saved.Steps.Where(s => s.Technique == Technique.Pillar)) s.Technique = Technique.Stair;
        saved.Home = null;
        ex.Plans.Save(saved);

        // The restart's resume runs from the platform and finishes the tower.
        body.PlaceLimit = int.MaxValue;
        body.Stop = null;
        var report = await ex.RunAsync(4, design, BuildPart.Whole(design), body, CancellationToken.None);
        var text = report.Text("whole build");
        log.WriteLine(text);
        log.WriteLine(string.Join("\n", body.Log.TakeLast(25)));
        Assert.All(design.Blocks.Values, b => Assert.True(DesignChecks.SameState(b, w.GetBlock(b.Pos), w.GetMeta(b.Pos)), $"{b.Pos} not built"));
        // Not on the tower top with no way down: back on Fox's floor, or a recorded exit that the walk home paths.
        var feet = body.Feet0;
        var home = report.Plan!.Home!.Value;
        Assert.Equal(64, home.Y); // the design's ground, not the platform the resume started on
        Assert.True(WayHome.AtHome(feet, home) || WayHome.WalkDown(w.PlanningView, feet, home) != null, $"stranded at {feet}: {text}");
        Assert.DoesNotContain("stranded", text);
        Assert.Empty(body.Unguarded);
    }

    [Fact]
    public async Task A_resume_from_the_platform_takes_the_stair_down_from_below_never_from_above()
    {
        // A finished 3×3 platform at y70 on a post (the design, all placed), the climb's 1-wide scaffold stair still up east of it,
        // the body on the platform: the run that resumes there only has the stair to take down. The plan was saved without a
        // recorded ground (the live drop tower's plans), so before the fix the platform was "the ground": the top tread came
        // down from the platform first and the rest was out of reach, the body left up there with no way down.
        var w = BlueprintTests.Island(8);
        var (design, _) = BlueprintNormalizer.FromText("size 3x7x3\npalette C=cobblestone\nlayer 0-5\n...\n.C.\n...\nlayer 6\nCCC\nCCC\nCCC", new BlockPos(-1, 64, -1));
        foreach (var b in design!.Blocks.Values) w.SetBlock(b.Pos.X, b.Pos.Y, b.Pos.Z, b.Id, b.Meta);
        var stair = Enumerable.Range(0, 6).Select(k => new BlockPos(2 + k, 69 - k, 0)).ToList();
        foreach (var t in stair) { w.SetBlock(t.X, t.Y, t.Z, Blocks.Cobblestone, 0); w.Placed.Note(t, 0, Blocks.Cobblestone, "maid_chan", DateTimeOffset.UtcNow); }
        w.Player.ServerTeleport(new Vec3d(0.5, 71, 0.5), 72.62, 0, 0, true);
        using var db = AgentDb.Open(null);
        var ex = new ConstructionExecutor(new BlueprintOptions(), new BlueprintStore(db));
        var body = Body(w);
        var prev = ex.PlanFor(7, design, BuildPart.Whole(design), body, save: false);
        prev.ScaffoldUp = stair.ToList();
        prev.Home = null;
        prev.Gate.State = GateState.Approved;
        foreach (var s in prev.Steps) s.Status = StepStatus.Verified;
        ex.Plans!.Save(prev);

        var report = await ex.RunAsync(7, design, BuildPart.Whole(design), body, CancellationToken.None);
        var text = report.Text("platform");
        log.WriteLine(text);
        log.WriteLine(string.Join("\n", body.Log));
        Assert.Equal(64, report.Plan!.Home!.Value.Y);
        Assert.True(WayHome.AtHome(body.Feet0, report.Plan.Home.Value), $"left at {body.Feet0}: {text}");
        Assert.DoesNotContain("stranded", text);
        Assert.Empty(body.Unguarded);
    }

    [Fact]
    public async Task Stranded_on_a_platform_with_no_scaffold_left_it_places_a_stair_down_and_walks_home()
    {
        // A 6×6 island at y63, a 3×3 platform on a 1×1 stone post 8 up beside it, nothing else: the body on the platform, no pillar
        // of its own, no walk down (an 8-block drop).
        var w = BlueprintTests.Island(3);
        for (int y = 64; y <= 70; y++) w.SetBlock(0, y, 0, Blocks.Stone, 0);
        for (int x = -1; x <= 1; x++) for (int z = -1; z <= 1; z++) w.SetBlock(x, 71, z, Blocks.Cobblestone, 0);
        w.Player.ServerTeleport(new Vec3d(0.5, 72, 0.5), 73.62, 0, 0, true);
        var feet = Pathfinder.StartCellFor(w.PlanningView, w.Player.Position)!.Value;
        var home = new BlockPos(0, 64, 0);
        Assert.True(WayHome.Stranded(w.PlanningView, feet, home));

        var trace = new List<string>();
        var plan = ExitRoute.Plan(w.PlanningView, feet, home, _ => false, _ => true, blocks: 64, trace: trace);
        log.WriteLine(string.Join("\n", trace));
        Assert.NotNull(plan);
        log.WriteLine(string.Join("\n", plan!.Moves));
        Assert.InRange(plan.Placed, 7, 16);
        Assert.Equal(0, plan.Dug);

        // Run through the executor's exit on a sim body: every placement legal from where it stands, never over the void.
        var body = new SimBuildBody(w);
        var bp = new BuildPlan { Home = home };
        var (voxel, _) = BlueprintNormalizer.FromText("size 1x1x1\npalette C=cobblestone\nlayer 0\nC", new BlockPos(0, 71, 0));
        var line = await ConstructionExecutor.LeaveSafelyAsync(body, bp, voxel!, null, CancellationToken.None);
        log.WriteLine(line);
        Assert.StartsWith("came down off the build: placed ", line);
        Assert.True(WayHome.AtHome(body.Feet0, home), $"at {body.Feet0}");
        Assert.Equal(plan.Placed, bp.Exit.Count);
        Assert.Empty(body.Unguarded);
        Assert.All(bp.Exit, p => Assert.False(Safety.IsOverVoid(w, p.X, p.Y - 1, p.Z), $"tread {p} over the void"));
    }

    [Fact]
    public async Task On_my_own_pillar_it_digs_down_from_on_top_never_over_the_void()
    {
        var w = BlueprintTests.Island(3);
        for (int y = 64; y <= 68; y++) w.SetBlock(1, y, 1, Blocks.Cobblestone, 0);
        w.Player.ServerTeleport(new Vec3d(1.5, 69, 1.5), 70.62, 0, 0, true);
        var body = new SimBuildBody(w);
        var pillar = Enumerable.Range(64, 5).Select(y => new BlockPos(1, y, 1)).ToList();
        var bp = new BuildPlan { Home = new BlockPos(0, 64, 0), ScaffoldUp = pillar.ToList() };
        var (voxel, _) = BlueprintNormalizer.FromText("size 1x1x1\npalette C=cobblestone\nlayer 0\nC", new BlockPos(3, 64, 3));
        var line = await ConstructionExecutor.LeaveSafelyAsync(body, bp, voxel!, null, CancellationToken.None);
        log.WriteLine(line);
        // Dug down to a 1-block step onto the island (the last one is a walk off), never a fall.
        Assert.StartsWith("came down off the build: dug ", line);
        Assert.True(WayHome.AtHome(body.Feet0, bp.Home!.Value), $"at {body.Feet0}");
        Assert.DoesNotContain(body.Log, l => l.StartsWith("won't dig"));
        Assert.Empty(body.Unguarded);
        Assert.Equal(0, bp.Exit.Count);
    }

    [Fact]
    public void A_plan_keeps_the_ground_of_its_first_slicing_across_resumes()
    {
        var (plan, _) = BlueprintNormalizer.FromText("size 1x3x1\npalette C=cobblestone\nlayer 0-2\nC", new BlockPos(5, 64, 5));
        // Fresh from the ground: that start; fresh from up on the build: the design's bottom layer; a resume: the first one's.
        Assert.Equal(new BlockPos(0, 64, 0), BuildPlanner.HomeFor(plan!, new BlockPos(0, 64, 0), null));
        Assert.Equal(new BlockPos(5, 64, 5), BuildPlanner.HomeFor(plan!, new BlockPos(5, 67, 5), null));
        Assert.Equal(new BlockPos(1, 64, 1), BuildPlanner.HomeFor(plan!, new BlockPos(5, 67, 5), new BuildPlan { Home = new BlockPos(1, 64, 1) }));
        var resumed = BuildPlan.Resume(new BuildPlan { Home = new BlockPos(1, 64, 1), Exit = [new BlockPos(2, 64, 2)] }, 1)!;
        Assert.Equal(new BlockPos(1, 64, 1), resumed.Home);
        Assert.Equal([new BlockPos(2, 64, 2)], resumed.Exit);
    }
}
