using MineClaw.Agent.Rag;
using MineClaw.Bot.Blueprints;
using MineClaw.Bot.Bodies;
using MineClaw.Bot.Building;
using MineClaw.Client;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Bot.Tests;

/// <summary>
/// A massive build (a stepped sand pyramid of base 63, 32 layers,
/// 43,680 blocks, by the crew in survival). The slicer has to plan it in reasonable time, the crew has to build it side by side,
/// the materials have to keep coming, and nothing may need the brain per block.
/// </summary>
public class MassiveBuildTests
{
    internal static string Pyramid(int b, string palette = "S=sand", string extra = "") =>
        $"blueprint pyramid\nsize {b}x{(b + 1) / 2}x{b}\npalette {palette}\npyramid S 0,0,0 {b}{extra}";

    internal static VoxelPlan PlanOf(string dsl, BlockPos origin)
    {
        var (plan, issues) = BlueprintNormalizer.FromText(dsl, origin, worldHeight: 128);
        Assert.NotNull(plan);
        Assert.DoesNotContain(issues, i => i.Level == IssueLevel.Error);
        return plan!;
    }

    /// <summary>A flat stone island of radius <paramref name="r"/> at y 63 in a world loaded wide enough for it.</summary>
    internal static WorldState Field(int r)
    {
        var w = new WorldState();
        int half = ((r + 8) / 16 + 1) * 16;
        w.ApplyChunkRegion(-half, 0, -half, 2 * half, 128, 2 * half, new byte[2 * half * 128 * 2 * half], null, null, null);
        for (int x = -r; x <= r; x++) for (int z = -r; z <= r; z++) w.SetBlock(x, 63, z, Blocks.Stone, 0);
        w.Player.ServerTeleport(new Vec3d(0.5, 64, r + 0.5 - 1), 65.62, 0, 0, true);
        return w;
    }

    private static string Sig(PlanStep s) =>
        $"{s.Index} {s.Action} {s.Technique} {s.Target} {s.Id}:{s.Meta} {s.Stand} {s.Face} {s.HiddenFace} {s.Yaw} {s.Column} [{string.Join(",", s.Deps)}] {s.Gate} {s.Cost:0.###}";

    // ------------------------------------------------------------------ 1. planning time: the flood after each placement

    public static TheoryData<string, string> Designs => new()
    {
        { "pyramid 9, dirt core", Pyramid(9, "S=sand D=dirt", " core:D") },
        { "hut", BlueprintTests.Hut },
        { "hall", CrewBuildTests.Hall },
        { "hollow tower (pillar climb)", ClimbTests.HollowTower },
        { "mushroom (platform at height)", "size 11x13x3\npalette C=cobblestone\npillar C 1,0,1 12\nfloor C 0,12,0 2,12,2\nfloor C 3,12,1 10,12,1" },
    };

    [Theory]
    [MemberData(nameof(Designs))]
    public void A_partial_flood_after_each_placement_plans_exactly_what_the_whole_flood_plans(string name, string dsl)
    {
        // FloodLocal stops the walk flood early in the main placement loop; each pick is only taken from it when no cell beyond it
        // could be cheaper, otherwise the whole flood decides. Tiny local floods (6, 40 cells) force both paths on every design.
        var w = Field(14);
        var plan = PlanOf(dsl, new BlockPos(-3, 64, -3));
        var start = Pathfinder.StartCellFor(w.PlanningView, w.Player.Position)!.Value;
        var whole = BuildPlanner.Plan(plan, w.PlanningView, start, null, new BuildPlannerOptions { GateBlocks = 5, FloodLocal = 0 });
        Assert.True(whole.Steps.Count >= plan.Blocks.Count / 2, name);
        foreach (var local in new[] { 6, 40 })
        {
            var part = BuildPlanner.Plan(plan, w.PlanningView, start, null, new BuildPlannerOptions { GateBlocks = 5, FloodLocal = local });
            Assert.Equal(whole.Steps.Select(Sig), part.Steps.Select(Sig));
            Assert.Equal(whole.Infeasible.Select(i => i.Pos + i.Why), part.Infeasible.Select(i => i.Pos + i.Why));
        }
    }

    // ------------------------------------------------------------------ 2. the crew side by side

    private static readonly string[] Names = ["maid_chan", "maid_chan_67", "maid_chan_69", "maid_chan_420", "maid_chan_1337", "maid_chan_7", "maid_chan_8", "maid_chan_9"];

    /// <summary>n sim bodies around a base-<paramref name="b"/> footprint (each in its own mirrored world), restocking from one chest.</summary>
    internal static (SimCrewLink Link, List<SimBuildBody> Bodies, SimChest Chest) Crew(int n, int b, int sand, int bag = 64 * 36)
    {
        var link = new SimCrewLink();
        var chest = new SimChest(new() { [(Blocks.Sand, 0)] = sand }, bag);
        int e = b / 2 + 2;
        var at = new (int X, int Z)[] { (0, e), (-e, 0), (e, 0), (0, -e), (-e, e), (e, e), (-e, -e), (e, -e) };
        var bodies = new List<SimBuildBody>();
        for (int i = 0; i < n; i++)
        {
            var w = Field(b / 2 + 6);
            w.Player.ServerTeleport(new Vec3d(at[i].X + 0.5, 64, at[i].Z + 0.5), 65.62, 0, 0, true);
            bodies.Add(new SimBuildBody(w, new(), Names[i], link) { Chest = chest });
        }
        return (link, bodies, chest);
    }

    private static BlockPos Corner(int b) => new(-b / 2, 64, -b / 2);

    /// <summary>In the global event order, the share of windows of 4 placements per body in which at least <paramref name="k"/>
    /// different bodies placed: 1.0 = everyone always at work at once, low = turns.</summary>
    private static double SideBySide(SimCrewLink link, int n, int k)
    {
        var ev = link.Events.Where(e => e.What.StartsWith("place ")).Select(e => e.Body).ToList();
        int win = 4 * n, windows = ev.Count / win;
        return windows == 0 ? 0 : Enumerable.Range(0, windows).Count(w => ev.Skip(w * win).Take(win).Distinct().Count() >= k) / (double)windows;
    }

    [Fact]
    public async Task Eight_bodies_build_a_base_21_sand_pyramid_side_by_side_with_no_waits_no_re_balance_and_nothing_left_for_the_finale()
    {
        // The one-plan slicer planned the pyramid from the first body's feet and cut it into lanes: stands wandered into the
        // neighbours' strips, 1,254 steps waited on another lane, lanes timed out or got stuck ("face 13.4 away from my eyes":
        // a stand the body could not get to yet), and after 2 re-balances the build was mostly turns. Strips planned each on
        // its own share nothing, so all eight build at once.
        var (link, bodies, chest) = Crew(8, 21, sand: 1771);
        var plan = PlanOf(Pyramid(21), Corner(21));
        using var db = AgentDb.Open(null);
        var report = await CrewBuildTests.Fast(new ConstructionExecutor(new BlueprintOptions(), new BlueprintStore(db)))
            .RunAsync(1, plan, bodies, new CancellationTokenSource(TimeSpan.FromSeconds(120)).Token);
        Assert.True(report.Complete, CrewBuildTests.Why(report));
        foreach (var blk in plan.Blocks.Values) Assert.Equal(Blocks.Sand, bodies[0].World.GetBlock(blk.Pos));
        Assert.Equal(0, report.Rebalances);
        Assert.Equal(8, report.Rounds[0].Lanes.Count);
        Assert.All(report.Rounds[0].Lanes, l => Assert.Empty(l.Waits));                // nobody waits on anybody
        // Every lane stands only in its own strip of columns (an edge strip also beyond the footprint's edge).
        var r0 = report.Rounds[0];
        foreach (var l in r0.Lanes)
        {
            var steps = l.Steps.Select(i => r0.Steps[i]).ToList();
            int lo = steps.Min(st => st.Target.X), hi = steps.Max(st => st.Target.X);
            Assert.All(steps.Where(st => st.Stand != default), st => Assert.True(st.Stand.X >= lo && st.Stand.X <= hi
                || lo == plan.Min.X && st.Stand.X < lo || hi == plan.Max.X && st.Stand.X > hi, $"lane {l.Body} ({lo}..{hi}) stands at {st.Stand}"));
        }
        Assert.Equal(0, report.Finale!.Placed);                                          // the lanes placed every block
        Assert.All(bodies, b => Assert.True(b.Placed.Count >= 1771 / 8 / 2, $"{b.Name} placed {b.Placed.Count}"));
        Assert.True(SideBySide(link, 8, 7) >= 0.6, $"at least 7 of 8 bodies placing in {SideBySide(link, 8, 7):P0} of the windows");
        Assert.Empty(link.Violations);                                                   // never two bodies in one cell
        Assert.Empty(bodies.SelectMany(b => b.Falls));                                   // no grain over air
        Assert.Equal(0, chest.Stock[(Blocks.Sand, 0)]);
    }

    [Fact]
    public void A_strip_that_cant_stand_on_its_own_leaves_the_crew_to_the_one_plan_slicer()
    {
        // The mushroom's arm hangs off its platform in the next strip: planned alone, the arm's strip has no support within its
        // scaffold limit. Strips are only taken when every strip plans clean, so the crew gets the one-plan slicing instead.
        var w = Field(14);
        const string mushroom = "size 11x13x3\npalette C=cobblestone\npillar C 1,0,1 12\nfloor C 0,12,0 2,12,2\nfloor C 3,12,1 10,12,1";
        var plan = PlanOf(mushroom, new BlockPos(-5, 64, -1));
        var crew = new List<CrewMember> { new("maid_chan", new BlockPos(-6, 64, 3)), new("maid_chan_67", new BlockPos(6, 64, 3)) };
        var strict = new BuildPlannerOptions { GateBlocks = 0, StripsAbove = 1 };
        Assert.Null(CrewSlicer.Strips(plan, w.PlanningView, crew, BuildPart.Whole(plan), strict, null));
        var sliced = CrewSlicer.Slice(plan, w.PlanningView, crew, null, strict);
        var onePlan = CrewSlicer.Slice(plan, w.PlanningView, crew, null, strict with { StripsAbove = 0 });
        Assert.Equal(onePlan.Steps.Count, sliced.Steps.Count);
        Assert.Equal(onePlan.Infeasible.Count, sliced.Infeasible.Count);
    }

    // ------------------------------------------------------------------ 3. materials at scale

    [Fact]
    public async Task Three_bodies_restock_a_base_17_pyramid_in_one_trip_each_side_by_side_at_the_chest()
    {
        // 969 sand over 3 lanes is ~323 each: a fetch was capped at 256 (64 * 4) and only one body was let at the chests at a time,
        // so each lane went twice and the others queued behind it. Now one fetch takes up to 16 stacks and three go at once.
        var (_, bodies, chest) = Crew(3, 17, sand: 969);
        foreach (var b in bodies) b.EnsureMs = 40;
        var plan = PlanOf(Pyramid(17), Corner(17));
        using var db = AgentDb.Open(null);
        var report = await CrewBuildTests.Fast(new ConstructionExecutor(new BlueprintOptions(), new BlueprintStore(db)))
            .RunAsync(1, plan, bodies, new CancellationTokenSource(TimeSpan.FromSeconds(90)).Token);
        Assert.True(report.Complete, CrewBuildTests.Why(report));
        Assert.All(report.Rounds[0].Lanes, l => Assert.True(l.Steps.Count > 256, $"lane {l.Body} has {l.Steps.Count} (more than one old fetch)"));
        Assert.Equal(3, chest.Withdrawals);                    // one trip per lane
        Assert.Equal(3, chest.PeakAt);                         // all three at the chest at once
        Assert.Equal(0, chest.Stock[(Blocks.Sand, 0)]);
    }

    [Fact]
    public async Task A_body_dropped_from_the_crew_puts_its_sand_back_so_the_others_can_finish()
    {
        // The chest holds exactly the pyramid's sand. One body fetches its lane's share and then can't place anything (stuck): its
        // lane is re-sliced over the others, which would run dry ("missing sand") while its share sat in its bag.
        var (_, bodies, chest) = Crew(3, 15, sand: 680);
        bodies[2].Jammed = true;
        var plan = PlanOf(Pyramid(15), Corner(15));
        using var db = AgentDb.Open(null);
        var report = await CrewBuildTests.Fast(new ConstructionExecutor(new BlueprintOptions(), new BlueprintStore(db)))
            .RunAsync(1, plan, bodies, new CancellationTokenSource(TimeSpan.FromSeconds(90)).Token);
        Assert.True(report.Complete, CrewBuildTests.Why(report));
        Assert.Contains(report.Lanes, l => l.Body == bodies[2].Name && l.State == "stuck");
        Assert.True(report.Rebalances >= 1);
        Assert.Empty(bodies[2].Placed);
        Assert.Equal(0, bodies[2].Count(Blocks.Sand, 0));                                // its bag gave it all back
        Assert.True(report.Returned > 0, report.Text("pyramid"));
        Assert.Contains("put back in storage", report.Text("pyramid"));
        foreach (var blk in plan.Blocks.Values) Assert.Equal(Blocks.Sand, bodies[0].World.GetBlock(blk.Pos));
        Assert.Equal(0, chest.Stock[(Blocks.Sand, 0)] + bodies.Sum(b => b.Count(Blocks.Sand, 0)));
    }

    // ------------------------------------------------------------------ 1b. sections: band by band

    [Fact]
    public void A_massive_design_is_cut_into_bottom_up_bands_and_the_base_63_pyramid_fits_a_design()
    {
        // Base 63: 32 layers, 43,680 sand; past 32 a side and 6,000 blocks before. Bands of at most 6,000: a layer bigger than that
        // would be a band by itself; the pyramid's 3,969-block bottom layer is one band, the top 14 layers share one.
        var (plan, issues) = BlueprintNormalizer.FromText(Pyramid(63), new BlockPos(-31, 64, -31), worldHeight: 256);
        Assert.NotNull(plan);
        Assert.DoesNotContain(issues, i => i.Level == IssueLevel.Error);
        Assert.Equal(43680, plan!.Blocks.Count);
        Assert.Equal("63×63×32", plan.Blueprint.SizeText);
        var bands = plan.Bands(6000);
        Assert.Equal(10, bands.Count);
        Assert.Equal((64, 64), bands[0]);
        Assert.Equal((64 + 18, 64 + 31), bands[^1]);
        Assert.All(bands.Zip(bands.Skip(1)), p => Assert.Equal(p.First.Hi + 1, p.Second.Lo)); // contiguous, bottom-up
        var sizes = bands.Select(b => plan.Band(b.Lo, b.Hi).Blocks.Count).ToList();
        Assert.Equal(43680, sizes.Sum());
        Assert.All(sizes, n => Assert.InRange(n, 1, 6000));
        Assert.Single(plan.Bands(50_000));
        Assert.Same(plan, plan.Band(64, 95));
    }

    [Fact]
    public async Task A_design_past_the_section_size_is_built_band_by_band_and_a_stopped_build_resumes_at_its_first_unfinished_band()
    {
        // Base 11 (286 sand; layers 121, 81, 49, 25, 9, 1) in bands of at most 100: [121] [81] [49 25 9 1]. Each band is a plan of its
        // own: no slicing ever holds more than one band, and a restart carries on from the first band not standing.
        var plan = PlanOf(Pyramid(11), Corner(11));
        Assert.Equal([(64, 64), (65, 65), (66, 69)], plan.Bands(100));
        var o = new BlueprintOptions { SectionBlocks = 100, CheckpointEvery = 1 };
        var (_, bodies, _) = Crew(2, 11, sand: 286);
        int total = 0;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        foreach (var b in bodies) b.AfterPlace = _ => { if (Interlocked.Increment(ref total) == 150) stop.Cancel(); }; // band 2 half done
        using var db = AgentDb.Open(null);
        var first = await CrewBuildTests.Fast(new ConstructionExecutor(o, new BlueprintStore(db))).RunAsync(1, plan, bodies, stop.Token);
        Assert.Equal("cancelled", first.Stopped);
        Assert.Equal((2, 3), first.Section);
        Assert.All(first.Rounds, r => Assert.Single(r.Steps.Where(st => st.Action == StepAction.Place).Select(st => st.Target.Y >= 66 ? 66 : st.Target.Y).Distinct()));
        foreach (var b in bodies) b.AfterPlace = null;

        var again = await CrewBuildTests.Fast(new ConstructionExecutor(o, new BlueprintStore(db))).RunAsync(1, plan, bodies,
            new CancellationTokenSource(TimeSpan.FromSeconds(60)).Token);
        Assert.True(again.Complete, CrewBuildTests.Why(again));
        Assert.Equal((3, 3), again.Section);
        // The second run started on band 2 (band 1 stands): band 1 was not sliced again.
        Assert.Equal(65, again.Rounds[0].BandLo);
        Assert.DoesNotContain(again.Rounds, r => r.BandLo == 64);
        Assert.All(again.Rounds.Concat(first.Rounds), r => Assert.True(r.Placements <= 121, $"a plan of {r.Placements} placements"));
        foreach (var blk in plan.Blocks.Values) Assert.Equal(Blocks.Sand, bodies[0].World.GetBlock(blk.Pos));
        Assert.Contains("(3 sections)", again.Text("pyramid"));
    }

    // ------------------------------------------------------------------ the design service: one body, the hive, the summary

    private static BlueprintService Service(SimBuildBody body, AgentDb db, BlueprintOptions o, IBuildCrew? crew = null)
    {
        var store = new BlueprintStore(db);
        var ex = new ConstructionExecutor(o, store);
        return new BlueprintService(o, store, new BlueprintPreview())
        {
            Site = () => BlueprintTests.Site(body.World), Crew = () => crew ?? new SingleBodyCrew(body),
            ResolveSite = (at, _) => (new SitePick(new BlockPos(0, 63, 0), at ?? "here"), null), Trusted = () => ["PoofImaFox"],
            Executor = ex, Crews = CrewBuildTests.Fast(ex),
        };
    }

    private static BlueprintRecord Accepted(BlueprintService s, string dsl, BlockPos at)
    {
        var rec = s.Store.Insert(new BlueprintRecord
        {
            Name = "pyramid", Description = "a pyramid", Dsl = dsl, Status = DesignStatus.Accepted, Origin = at, Site = at.Down(), AcceptedBy = "PoofImaFox", Bot = "maid_chan",
        });
        rec.Origin = at;
        s.Store.Update(rec);
        return rec;
    }

    [Fact]
    public async Task One_body_builds_a_sectioned_design_band_by_band_and_its_summary_is_counted_not_sliced()
    {
        var o = new BlueprintOptions { SectionBlocks = 100 };
        var (_, bodies, _) = Crew(1, 11, sand: 286);
        using var db = AgentDb.Open(null);
        var s = Service(bodies[0], db, o);
        var rec = Accepted(s, Pyramid(11), Corner(11));
        // Planning a 43,680-block design up front took minutes on the chat thread ("build it" waits for it): counted instead.
        var summary = s.PlanSummary(rec)!;
        Assert.StartsWith("286 placements in 3 sections", summary);
        Assert.Contains("needs: 286 sand", summary);
        Assert.Null(s.Plans!.Get(rec.Id, 0));                                          // nothing sliced yet
        var (ok, text) = await s.BuildAsync(rec.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), null, null, null, new CancellationTokenSource(TimeSpan.FromSeconds(60)).Token);
        Assert.True(ok, text);
        Assert.StartsWith("built #", text);
        Assert.Contains("(3 sections)", text);
        Assert.Equal(DesignStatus.Built, s.Store.Get(rec.Id)!.Status);
        var last = s.Plans.Get(rec.Id, 0)!;
        Assert.True(last.Placements <= 84, $"the saved plan is the last band's, {last.Placements} placements");
    }

    [Fact]
    public async Task Hive_clones_run_their_lanes_of_every_band_of_a_sectioned_design()
    {
        // Main slices one band at a time and saves that band's plan; a clone's lane job (build{bp, lane}) parses the whole design,
        // so it has to rebuild the same band from the saved plan, or every lane after the first band would be refused as stale.
        var o = new BlueprintOptions { SectionBlocks = 40 };
        var (link, bodies) = CrewBuildTests.Crew(3);
        var board = new CrewBuildTests.Board();
        foreach (var b in bodies) b.Claims = board.View(b.Name);
        using var db = AgentDb.Open(null);
        var main = Service(bodies[0], db, o);
        var clones = bodies.Skip(1).ToDictionary(b => b.Name, b => (b, Service(b, db, o)));
        var jobs = new CrewBuildTests.Jobs(clones, db);
        main.Jobs = jobs;
        var rec = Accepted(main, CrewBuildTests.Hall, CrewBuildTests.HallAt);
        main.RequestCrew(rec.Id, new CrewRequest(["67", "69"]));
        var (ok, text) = await main.BuildAsync(rec.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), null, null, null, CrewBuildTests.Soon());
        var lanes = await Task.WhenAll(jobs.Runs);
        Assert.True(ok, text);
        Assert.Contains("(3 sections)", text);
        Assert.Equal(6, jobs.Started.Count);                                             // two clone lanes in each of three bands
        Assert.All(lanes, l => Assert.True(l.Ok, l.Text));
        Assert.All(bodies.Skip(1), b => Assert.True(b.Placed.Count(p => p.Y == CrewBuildTests.HallAt.Y + 2) > 0, $"{b.Name} built in the top band"));
        Assert.Empty(link.Violations);
        Assert.Equal(DesignStatus.Built, main.Store.Get(rec.Id)!.Status);
    }

    // ------------------------------------------------------------------ 4. the brain: no wake while the crew works

    [Fact]
    public async Task While_the_crew_builds_the_coordinating_bodys_stall_signature_keeps_changing_though_it_stands_still()
    {
        // Main runs the build job; once its own strip is done it parks while the others finish the band (and stands still while
        // a band is sliced). The stall watchdog compared only its position, bag and action: 45 s of that was a "stalled: build
        // idle" wake of the brain, a paid turn, per band of a healthy build. The crew's progress is now part of the signature.
        var (_, bodies, _) = Crew(3, 15, sand: 680);
        var plan = PlanOf(Pyramid(15), Corner(15));
        var main = new FakeGameClient { Username = bodies[0].Name };
        var still = BodyFactory.StallSignature(main, bodies[0].Name);
        Assert.Equal(still, BodyFactory.StallSignature(main, bodies[0].Name));   // standing still, nothing building: no change
        long before = BuildPulse.Of(bodies[0].Name);
        bodies[0].Jammed = true;                                                   // main itself places nothing at all
        using var db = AgentDb.Open(null);
        var report = await CrewBuildTests.Fast(new ConstructionExecutor(new BlueprintOptions(), new BlueprintStore(db)))
            .RunAsync(1, plan, bodies, new CancellationTokenSource(TimeSpan.FromSeconds(90)).Token);
        Assert.True(report.Complete, CrewBuildTests.Why(report));
        Assert.Empty(bodies[0].Placed);
        Assert.True(BuildPulse.Of(bodies[0].Name) - before >= bodies[1].Placed.Count + bodies[2].Placed.Count, "every lane step beats main's pulse");
        Assert.NotEqual(still, BodyFactory.StallSignature(main, bodies[0].Name));
    }

    [Fact]
    public void Slicing_a_band_beats_the_pulse_while_it_plans()
    {
        var w = Field(14);
        var plan = PlanOf(Pyramid(11), Corner(11));
        var interval = BuildPulse.Interval;
        BuildPulse.Interval = TimeSpan.FromMilliseconds(5);
        try
        {
            const string name = "pulse_planner";
            long before = BuildPulse.Of(name);
            var body = new SimBuildBody(w, new() { [(Blocks.Sand, 0)] = 300 }, name);
            using var db = AgentDb.Open(null);
            new ConstructionExecutor(new BlueprintOptions(), new BlueprintStore(db)).PlanFor(1, plan, BuildPart.Whole(plan), body);
            Assert.True(BuildPulse.Of(name) - before >= 2, $"{BuildPulse.Of(name) - before} beats while slicing 286 blocks");
        }
        finally { BuildPulse.Interval = interval; }
    }
}
