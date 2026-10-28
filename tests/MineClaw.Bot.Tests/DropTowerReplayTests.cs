using System.Text.Json;
using System.Text.RegularExpressions;
using MineClaw.Agent.Rag;
using MineClaw.Bot.Blueprints;
using MineClaw.Bot.Building;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Bot.Tests;

/// <summary>
/// Live 10-28: design #4 "mob drop tower" (16×16×16 at -33,64,7 over Fox's 8×8 floor at y63) answered every build with "placed 0,
/// 689 missing" and no reason; the brain resumed it every morning and a worker spent $0.05 guessing ("not reach, void or path").
/// The cause: the tower is taller than the bot can reach from the floor (feet y64, eyes 65.6, reach 4.5: the y70 layer at best)
/// and the slicer has no way up (no pillar/stair technique), so the shaft walls from y71 are out of reach and everything above
/// (the shaft, the 16×16 platform, its walls, the roof) rests on them. The replay runs the executor on the remembered world
/// (tests/fixtures/drop-tower: world_chunks around the site, the design's DSL, the persisted plan) with a sim body. With
/// climbing (pillars inside the shaft, stairs) the whole tower goes up; with climbing off the old reasons are still named.
/// </summary>
public class DropTowerReplayTests(Xunit.Abstractions.ITestOutputHelper log)
{
    internal static string Fixture(string file)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            var p = Path.Combine(d.FullName, "tests", "fixtures", "drop-tower", file);
            if (File.Exists(p)) return p;
        }
        // Built outside the repo (another artifacts path): next to this source file.
        var src = Path.Combine(Path.GetDirectoryName(SourceFile())!, "..", "fixtures", "drop-tower", file);
        if (File.Exists(src)) return src;
        throw new FileNotFoundException("tests/fixtures/drop-tower/" + file);
    }

    private static string SourceFile([System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;

    internal static readonly BlockPos Origin = new(-33, 64, 7);

    /// <summary>The remembered blocks (x -44..-6, z -4..33) in a loaded region; the bot on Fox's floor.</summary>
    internal static WorldState Site(string file = "site.txt")
    {
        var w = new WorldState();
        w.ApplyChunkRegion(-48, 0, -16, 48, 128, 64, new byte[48 * 128 * 64], null, null, null);
        foreach (var line in File.ReadLines(Fixture(file)))
        {
            if (line.Length == 0 || line[0] == '#') continue;
            var f = line.Split(' ').Select(int.Parse).ToArray();
            w.SetBlock(f[0], f[1], f[2], f[3], f[4]);
        }
        w.Player.ServerTeleport(new Vec3d(-21.5, 64, 19.5), 65.62, 0, 0, true);
        return w;
    }

    internal static VoxelPlan Design()
    {
        var (plan, issues) = BlueprintNormalizer.FromText(File.ReadAllText(Fixture("design.dsl")), Origin);
        Assert.NotNull(plan);
        Assert.DoesNotContain(issues, i => i.Level == IssueLevel.Error);
        return plan!;
    }

    /// <summary>The build_plans row as it was (pre-format plan: 20 steps done, 39-entry ledger with repeats, gate approved).</summary>
    internal static BuildPlan Persisted() => JsonSerializer.Deserialize<BuildPlan>(File.ReadAllText(Fixture("plan.json")))!;

    private static SimBuildBody Body(WorldState w) => new(w, new() { [(Blocks.Cobblestone, 0)] = 1024, [(Blocks.Slab, 3)] = 300 });

    private static int Count(string line, string label) =>
        Regex.Match(line, $@"(\d+) {Regex.Escape(label)}") is { Success: true } m ? int.Parse(m.Groups[1].Value) : 0;

    [Fact]
    public async Task Replay_design_4_climbs_and_builds_everything_above_the_floor()
    {
        var w = Site();
        var design = Design();
        Assert.Equal(748, design.Blocks.Count);
        using var db = AgentDb.Open(null);
        var store = new BlueprintStore(db);
        var progress = new List<string>();
        var ex = new ConstructionExecutor(new BlueprintOptions(), store) { Progress = progress.Add };
        var saved = Persisted();
        Assert.Equal(0, saved.Format);
        ex.Plans!.Save(saved);
        var body = Body(w);

        var report = await ex.RunAsync(4, design, BuildPart.Whole(design), body, CancellationToken.None);
        var text = report.Text("whole build");
        log.WriteLine(text);
        log.WriteLine(report.Plan!.Summary());

        // The stale pre-format plan was planned again, its approved gate and its ledger kept.
        Assert.Contains(progress, p => p.Contains("older format") && p.Contains("approved"));
        var bp = report.Plan!;
        Assert.Equal(BuildPlan.CurrentFormat, bp.Format);
        Assert.Equal(GateState.Approved, bp.Gate.State);
        Assert.Equal("PoofimaFox", bp.Gate.ApprovedBy);
        Assert.Superset(saved.Placed.ToHashSet(), bp.Placed.ToHashSet());
        Assert.Equal(bp.Placed.Count, bp.Placed.Distinct().Count());

        // Every step was legal for the sim body (reach, the clicked face, standing in the cell it pillars): nothing failed.
        Assert.Equal(0, report.Failed);
        Assert.Empty(report.Deviations);
        Assert.Equal(Blocks.Cobblestone, w.GetBlock(-27, 70, 16)); // reached from the floor (feet y64, bottom face 4.49 away)

        // It climbed: the shaft walls above y70 (out of reach from the floor), the 16×16 platform bridged out over the void from
        // the shaft, its walls and the slab roof: all 689 blocks still missing, none left (nothing hangs past the scaffold limit).
        Assert.True(report.Complete, text);
        Assert.Null(report.Skips);
        Assert.Empty(bp.Infeasible);
        Assert.Equal(689, report.Placed);
        Assert.All(design.Blocks.Values, b => Assert.True(DesignChecks.SameState(b, w.GetBlock(b.Pos), w.GetMeta(b.Pos)), $"{b.Pos} not built"));
        // The climb: pillars jumped up inside the 2×2 shaft (void-free on every side) and dug back down top-first; 13 scaffold
        // blocks counted in the plan, all taken down, the body back on Fox's floor.
        Assert.Equal(13, report.Scaffolded);
        Assert.Equal(13, report.ScaffoldRemoved);
        Assert.Empty(bp.ScaffoldUp);
        Assert.True(body.Pillared >= 10, $"pillared {body.Pillared}");
        var pillar = bp.Steps.Where(s => s.Action == StepAction.Scaffold && s.Technique == Technique.Pillar).ToList();
        Assert.All(pillar, s => Assert.Equal(s.Target, s.Stand));
        Assert.All(pillar, s => Assert.InRange(s.Target.X, -26, -25));
        Assert.All(pillar, s => Assert.InRange(s.Target.Z, 14, 15));
        Assert.All(bp.Steps.Where(s => s.Action == StepAction.Scaffold), s => Assert.Equal(0, w.GetBlock(s.Target)));
        Assert.Contains(bp.Steps, s => s.Action == StepAction.RemoveScaffold && s.Technique == Technique.Pillar && s.Stand == s.Target.Up());
        Assert.Empty(body.Unguarded);
        Assert.Equal(64, body.Feet0.Y);
        Assert.Contains("13 scaffold (13 to climb: ", bp.Summary());
        Assert.Contains(" pillar", bp.Summary());

        var rec = store.GetBuild(4, 0)!;
        Assert.Equal("done", rec.State);
        Assert.Equal(rec.Total, rec.Placed);
    }

    [Fact]
    public void Live_1455_plan_only()
    {
        var trace = Environment.GetEnvironmentVariable("MC_TRACE");
        void T(string s) { log.WriteLine(s); if (trace != null) File.AppendAllText(trace, s + Environment.NewLine); }
        var w = Site("site-1455.txt");
        var design = Design();
        using var db = AgentDb.Open(null);
        var store = new BlueprintStore(db);
        var ex = new ConstructionExecutor(new BlueprintOptions(), store);
        var saved = JsonSerializer.Deserialize<BuildPlan>(File.ReadAllText(Fixture("plan-1455.json")))!;
        ex.Plans!.Save(saved);
        if (Environment.GetEnvironmentVariable("MC_AT") is { } at)
        {
            var c = at.Split(',').Select(double.Parse).ToArray();
            w.Player.ServerTeleport(new Vec3d(c[0], c[1], c[2]), c[1] + 1.62, 0, 0, true);
        }
        var body = Body(w);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        T("start " + body.Feet0);
        var first = ex.PlanFor(4, design, BuildPart.Whole(design), body, save: false);
        T($"planned in {sw.ElapsedMilliseconds} ms: " + first.Summary());
        T("skips: " + BuildSkips.OfPlan(first));
        foreach (var s in first.Steps.Where(s => !s.Finished && (Environment.GetEnvironmentVariable("MC_ALL") != null || s.Target.X <= -32 || s.Action == StepAction.Clear || s.Stand.Y >= 77)).Take(120)) T($"{s.Index} {s.Action} {s.Technique} {s.Target} @ {s.Stand} {s.Note}");
        if (Environment.GetEnvironmentVariable("MC_RUN") == null) return;
        var progress = new List<string>();
        var ex2 = new ConstructionExecutor(new BlueprintOptions(), store) { Progress = p => { progress.Add(p); T("progress: " + p); } };
        var report = ex2.RunAsync(4, design, BuildPart.Whole(design), body, CancellationToken.None).GetAwaiter().GetResult();
        T($"ran in {sw.ElapsedMilliseconds} ms: " + report.Text("whole build"));
        T(report.Plan!.Summary());
        foreach (var l in body.Log.TakeLast(20)) T("body: " + l);
        foreach (var u in body.Unguarded) T("unguarded: " + u);
    }

    [Fact]
    public async Task With_climbing_off_design_4_still_names_every_skip_reason()
    {
        var w = Site();
        var design = Design();
        using var db = AgentDb.Open(null);
        var store = new BlueprintStore(db);
        var ex = new ConstructionExecutor(new BlueprintOptions { Climb = false }, store);
        ex.Plans!.Save(Persisted());

        var report = await ex.RunAsync(4, design, BuildPart.Whole(design), Body(w), CancellationToken.None);
        var text = report.Text("whole build");
        log.WriteLine(text);
        var bp = report.Plan!;
        Assert.Equal(0, report.Failed);
        Assert.Empty(report.Deviations);
        Assert.Equal(Blocks.Cobblestone, w.GetBlock(-27, 70, 16));

        // The reasons, grouped: the shaft walls above reach (the root), everything else waiting on them, nothing protected.
        Assert.NotNull(report.Skips);
        var skips = report.Skips!;
        int above = Count(skips, "no stand: above my reach (I can't climb)");
        int waiting = Count(skips, "waiting on supports");
        Assert.InRange(above, 10, 16);
        Assert.Equal(report.Missing.Count, above + waiting);
        Assert.Contains("0 protected", skips);
        Assert.DoesNotContain("no support: over the void", skips);
        Assert.Matches(@"above my reach \(I can't climb\) e\.g\. -2[4-7],7[01],1[3-6]", skips);
        Assert.Contains(" — " + skips, text);
        Assert.StartsWith($"whole build: placed {report.Placed}, {report.Missing.Count} missing — ", text);
        Assert.All(bp.Infeasible.Where(i => i.Why.StartsWith(BuildSkips.Waiting)), i => Assert.Contains("rests on ", i.Why));
        Assert.Contains(bp.Infeasible, i => i.Why.StartsWith(BuildSkips.AboveReach + " (I can't climb: climbing is off)"));
        Assert.DoesNotContain(bp.Steps, s => s.Technique is Technique.Pillar or Technique.Stair);

        // The same line rides in the build record (the design status) and the plan's own summary.
        var rec = store.GetBuild(4, 0)!;
        Assert.Contains(skips, rec.Note);
        var (n, planLine) = BuildSkips.PlanSkips(ex.Plans.Get(4, 0)!);
        Assert.Equal(report.Missing.Count, n);
        Assert.Contains("above my reach", planLine);
        Assert.Contains("can't place", bp.Summary());
        Assert.Contains("above my reach", bp.Summary());
    }

    [Fact]
    public async Task Design_4_hanging_over_the_void_suggests_the_exact_floor_under_its_footprint()
    {
        // Live 10-28 14:40 "expand the floor so you can build it": the same tower 16 south (its north edge touching Fox's floor's
        // south edge), nothing under it to climb from: it all hangs over the void. The build line names the floor
        // that would carry it: the design's own footprint one below its bottom, the cells still empty there. A suggestion only.
        var w = Site();
        var (plan, issues) = BlueprintNormalizer.FromText(File.ReadAllText(Fixture("design.dsl")), Origin.Offset(0, 0, 16));
        Assert.NotNull(plan);
        Assert.DoesNotContain(issues, i => i.Level == IssueLevel.Error);
        var report = await new ConstructionExecutor(new BlueprintOptions()).RunAsync(4, plan!, BuildPart.Whole(plan!), Body(w), CancellationToken.None);
        var text = report.Text("whole build");
        log.WriteLine(text);
        const string floor = "lay a floor: expand_island area=-33,23,-18,38 y=63 (256 cells, 256 cobble)";
        // It bridges the legs' bottoms out from the floor, then can't climb: every pillar or stair spot is beside the void, and
        // everything else rests on those.
        Assert.False(report.Complete);
        Assert.Contains("beside the void", string.Join("|", report.Plan!.Infeasible.Select(i => i.Why)));
        Assert.Contains("waiting on supports", report.Skips);
        Assert.EndsWith("; " + floor, report.Skips);
        Assert.Contains(floor, text);
        var bp = report.Plan!;
        Assert.Equal(floor, bp.Foundation);
        Assert.Contains(floor, bp.Summary());
        Assert.EndsWith(floor, BuildSkips.PlanSkips(bp).Line);
        Assert.Equal(0, w.GetBlock(-33, 63, 23)); // suggested, never laid by the build itself
    }

    [Fact]
    public void The_floor_hint_carries_design_4s_exact_footprint_and_only_when_it_mostly_hangs()
    {
        // The real footprint from the plan (origin -33,64,7, 16×16, as designed), not one read off the preview image.
        var w = Site();
        var design = Design();
        var fp = BuildSkips.Footprint(design.Blocks.Keys)!.Value;
        Assert.Equal("footprint x -33..-18, z 7..22, y 64..79", fp.Text);
        int empty = 0;
        for (int x = -33; x <= -18; x++) for (int z = 7; z <= 22; z++) if (!BuildBlocks.Supports(w.GetBlock(x, 63, z))) empty++;
        Assert.InRange(empty, 150, 192); // Fox's 8×8 floor and the bridge are already there
        var hanging = design.Blocks.Keys.Take(10).Select(p => (p, (string?)"no support within 6 scaffold blocks (over the void)")).ToList();
        var waiting = design.Blocks.Keys.Skip(10).Take(30).Select(p => (p, (string?)$"waiting on supports; rests on {hanging[0].p.X},{hanging[0].p.Y},{hanging[0].p.Z}, which can't be placed")).ToList();
        Assert.Equal($"lay a floor: expand_island area=-33,7,-18,22 y=63 ({empty} cells, {empty} cobble)",
            BuildSkips.FloorHint([.. hanging, .. waiting], fp, w));
        // Mostly something else (above my reach): no floor suggested.
        var above = design.Blocks.Keys.Skip(40).Take(60).Select(p => (p, (string?)(BuildSkips.AboveReach + " (I can't climb: climbing is off)"))).ToList();
        Assert.Null(BuildSkips.FloorHint([.. hanging, .. above], fp, w));
    }

    [Fact]
    public void Design_show_and_list_name_the_footprint_in_world_coordinates()
    {
        var w = Site();
        var store = new BlueprintStore(AgentDb.Open(null));
        var s = new BlueprintService(new BlueprintOptions(), store, new BlueprintPreview())
        {
            Site = () => new SiteContext { World = w.PlanningView, GeneratorKnown = true, Inventory = (_, _) => 0, Stored = (_, _) => 0 },
            Crew = () => new SingleBodyCrew(new SimBuildBody(w)),
            ResolveSite = (_, _) => (new SitePick(new BlockPos(-25, 63, 15), "here"), null),
        };
        var rec = store.Insert(new BlueprintRecord
        {
            Name = "mob drop tower", Description = "drop tower", Bot = "maid_chan", Status = DesignStatus.Building,
            Site = new BlockPos(-25, 63, 15), Origin = Origin, Dsl = File.ReadAllText(Fixture("design.dsl")),
        });
        Assert.Contains("16x16x16, footprint x -33..-18, z 7..22, y 64..79", s.Describe(rec).Replace('×', 'x'));
        Assert.Equal($"#{rec.Id} mob drop tower building (footprint x -33..-18, z 7..22, y 64..79)", s.ListLine(rec));
        var f = Assert.Single(s.Footprints());
        Assert.Equal(new MineClaw.Skills.DesignFootprint($"design #{rec.Id}", -33, 7, -18, 22, 64, 79), f);
    }

    [Fact]
    public async Task A_block_sealed_out_over_the_void_reports_no_stand()
    {
        // A 5×5 island; a lone stone block far out over the void, the design's one block against its far side: nothing to walk
        // on near it, more than a scaffold walkway away.
        var w = BlueprintTests.Island(2);
        w.SetBlock(13, 63, 0, Blocks.Stone, 0);
        var (plan, issues) = BlueprintNormalizer.FromText("size 1x1x1\npalette C=cobblestone\nlayer 0\nC", new BlockPos(14, 63, 0));
        Assert.NotNull(plan);
        var ex = new ConstructionExecutor(new BlueprintOptions());
        var report = await ex.RunAsync(9, plan!, BuildPart.Whole(plan!), new SimBuildBody(w), CancellationToken.None);
        var text = report.Text("whole build");
        log.WriteLine(text);
        var inf = Assert.Single(report.Plan!.Infeasible);
        Assert.StartsWith("no stand", inf.Why);
        Assert.Equal("whole build: placed 0, 1 missing — 1 no stand: I can't walk near it from here e.g. 14,63,0; 0 protected", text);

        // In reach of the island's stands but scaffold off: still a "no stand" reason, never a silent count.
        var near = BlueprintTests.Island(2);
        near.SetBlock(6, 63, 0, Blocks.Stone, 0);
        var (p2, _) = BlueprintNormalizer.FromText("size 1x1x1\npalette C=cobblestone\nlayer 0\nC", new BlockPos(7, 63, 0));
        var bp = BuildPlanner.Plan(p2!, near.PlanningView, Pathfinder.StartCellFor(near.PlanningView, near.Player.Position)!.Value, null,
            new BuildPlannerOptions { GateBlocks = 0, MaxScaffold = 0 });
        Assert.Equal("1 no stand in reach e.g. 7,63,0; 0 protected", BuildSkips.OfPlan(bp));
    }

    [Fact]
    public async Task A_stale_plan_is_planned_again_keeping_its_placed_blocks_and_approved_gate()
    {
        var w = BlueprintTests.Island();
        var (plan, _) = BlueprintNormalizer.FromText("size 4x1x1\npalette C=cobblestone\nlayer 0\nCCCC", new BlockPos(0, 64, 0));
        using var db = AgentDb.Open(null);
        var store = new BlueprintStore(db);
        var asked = new List<string>();
        var ex = new ConstructionExecutor(new BlueprintOptions { GateBlocks = 2, GateMinutes = 0 }, store)
        {
            Gates = new BuildGates(), Ask = q => { asked.Add(q); return Task.CompletedTask; },
        };
        // Saved by an older slicer (format 0) for another version of the design: steps claim done, nothing stands. One block of
        // ours from that run is in the ledger (undo must still find it).
        var mine = new BlockPos(5, 64, 5);
        w.SetBlock(mine.X, mine.Y, mine.Z, Blocks.Cobblestone, 0);
        var stale = BuildPlanner.Plan(plan!, w.PlanningView, Pathfinder.StartCellFor(w.PlanningView, w.Player.Position)!.Value, null,
            new BuildPlannerOptions { GateBlocks = 0 });
        stale.BlueprintId = 8;
        stale.Version = 12345;
        stale.Format = 0;
        foreach (var s in stale.Steps) s.Status = StepStatus.Verified;
        stale.Gate = new GateInfo { State = GateState.Approved, ApprovedBy = "PoofimaFox" };
        stale.Placed = [mine, mine];
        ex.Plans!.Save(stale);
        Assert.True(stale.IsStale(plan!.Hash()));

        var report = await ex.RunAsync(8, plan!, BuildPart.Whole(plan!), new SimBuildBody(w), CancellationToken.None);
        Assert.True(report.Complete, report.Text("row"));
        Assert.Equal(4, report.Placed);
        Assert.Empty(asked); // approved before: never asks again
        var bp = ex.Plans.Get(8, 0)!;
        Assert.False(bp.IsStale(plan.Hash()));
        Assert.Equal(GateState.Approved, bp.Gate.State);
        Assert.Contains(mine, bp.Placed);
        Assert.Equal(bp.Placed.Count, bp.Placed.Distinct().Count());
        Assert.True(bp.Replans >= 1);
        Assert.All(plan.Blocks.Values, b => Assert.Equal(Blocks.Cobblestone, w.GetBlock(b.Pos)));
        Assert.Null(report.Skips);

        // A current plan is used as it is.
        Assert.Same(bp, BuildPlan.Resume(bp, plan.Hash()));
        Assert.Null(BuildPlan.Resume(null, plan.Hash()));
    }
}
