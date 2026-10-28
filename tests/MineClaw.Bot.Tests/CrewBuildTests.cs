using MineClaw.Agent.Rag;
using MineClaw.Bot.Blueprints;
using MineClaw.Bot.Building;
using MineClaw.Data;
using MineClaw.World;
using MineClaw.World.Claims;

namespace MineClaw.Bot.Tests;

/// <summary>
/// The slicer for a crew of 1..N bodies (Bot/Building/CrewSlicer) and the crew executor (Bot/Blueprints/CrewExecutor): N=1 is
/// today's plan; lanes are disjoint, cover every step once and balance; a simulated parallel run of three bodies honours every
/// cross-lane wait and never puts two bodies in one cell; the one gate comes first; a dead body's lane is re-balanced; lanes
/// resume from the persisted plan; hive mode starts one job per clone lane.
/// </summary>
public class CrewBuildTests
{
    /// <summary>An 8×3×5 cobble hall with a plank roof (the roof's middle hangs on its neighbours: waits across the lane cuts).</summary>
    internal const string Hall = """
        blueprint long hall
        size 8x3x5
        palette C=cobblestone P=planks
        layer 0-1
        CCCCCCCC
        C......C
        C......C
        C......C
        CCC.CCCC
        layer 2
        PPPPPPPP
        PPPPPPPP
        PPPPPPPP
        PPPPPPPP
        PPPPPPPP
        """;

    internal static readonly BlockPos HallAt = new(-4, 64, -2);

    /// <summary>A 12×3×6 ring of walls (96 blocks, no roof): enough steps to balance three lanes.</summary>

    private static VoxelPlan PlanOf(string dsl, BlockPos origin)
    {
        var (plan, issues) = BlueprintNormalizer.FromText(dsl, origin);
        Assert.NotNull(plan);
        Assert.DoesNotContain(issues, i => i.Level == IssueLevel.Error);
        return plan!;
    }

    private static readonly (int X, int Z)[] Starts = [(0, 5), (-7, 6), (7, 6), (-7, -6), (7, -6)];
    private static readonly string[] Names = ["maid_chan", "maid_chan_67", "maid_chan_69", "maid_chan_420", "maid_chan_1337"];

    /// <summary>n sim bodies on the r=8 island, each in its own world (the block changes mirrored), standing apart outside the build.</summary>
    internal static (SimCrewLink Link, List<SimBuildBody> Bodies) Crew(int n, SimCrewLink? link = null, List<WorldState>? worlds = null)
    {
        link ??= new SimCrewLink();
        var bodies = new List<SimBuildBody>();
        for (int i = 0; i < n; i++)
        {
            var w = worlds?[i] ?? BlueprintTests.Island(8);
            if (worlds == null) w.Player.ServerTeleport(new Vec3d(Starts[i].X + 0.5, 64, Starts[i].Z + 0.5), 65.62, 0, 0, true);
            bodies.Add(new SimBuildBody(w, null, Names[i], link));
        }
        return (link, bodies);
    }

    private static List<CrewMember> Members(IEnumerable<SimBuildBody> bodies) =>
        bodies.Select(b => new CrewMember(b.Name, b.Feet0)).ToList();

    private static CrewExecutor Fast(ConstructionExecutor single) => new(single)
    {
        Poll = TimeSpan.FromMilliseconds(2), ParkAfter = TimeSpan.FromMilliseconds(80), WaitLimit = TimeSpan.FromSeconds(20),
        RemotePoll = TimeSpan.FromMilliseconds(20),
    };

    /// <summary>A run that hangs fails the test instead of the suite.</summary>
    private static CancellationToken Soon() => new CancellationTokenSource(TimeSpan.FromSeconds(60)).Token;

    /// <summary>What every lane of every round was left with (failure messages).</summary>
    internal static string Why(CrewReport r) => r.Text("build") + " | " + string.Join(" | ", r.Rounds.Select((bp, k) => $"round {k}: " + string.Join("; ",
        bp.Lanes.Select(l => $"{l.Body} {l.State} {l.Steps.Count(i => bp.Steps[i].Finished)}/{l.Steps.Count} " +
            string.Join(",", l.Steps.Select(i => bp.Steps[i]).Where(s => !s.Finished).Select(s => s.Note ?? "-").Distinct().Take(3))))));

    private static string Sig(PlanStep s) =>
        $"{s.Index} {s.Action} {s.Technique} {s.Target} {s.Id}:{s.Meta} {s.Item}:{s.Damage} {s.Stand} {s.Face} {s.HiddenFace} {s.Yaw} {s.Column} [{string.Join(",", s.Deps)}] {s.Gate} {s.Cost:0.###}";

    // ------------------------------------------------------------------ slicing

    [Fact]
    public void One_body_gets_exactly_the_plan_it_got_before()
    {
        foreach (var (dsl, at) in new[] { (BlueprintTests.Hut, new BlockPos(-2, 64, -3)), (Hall, HallAt) })
        {
            var w = BlueprintTests.Island(8);
            var plan = PlanOf(dsl, at);
            var start = Pathfinder.StartCellFor(w.PlanningView, w.Player.Position)!.Value;
            var o = new BuildPlannerOptions { GateBlocks = 5 };
            var before = BuildPlanner.Plan(plan, w.PlanningView, start, null, o);
            var crew = CrewSlicer.Slice(plan, w.PlanningView, [new CrewMember("maid_chan", start)], null, o);
            Assert.Equal(before.Steps.Select(Sig), crew.Steps.Select(Sig));
            Assert.Equal(before.Infeasible.Count, crew.Infeasible.Count);
            Assert.Equal(before.Summary(), crew.Summary());
            var lane = Assert.Single(crew.Lanes);
            Assert.Equal(Enumerable.Range(0, crew.Steps.Count), lane.Steps);
            Assert.Empty(lane.Waits);
            Assert.Equal(crew.SoloTicks, crew.CrewTicks);
        }
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void Lanes_are_disjoint_cover_every_step_once_and_balance_within_15_percent(int n)
    {
        var (_, bodies) = Crew(n);
        var plan = PlanOf(Hall, HallAt);
        var bp = CrewSlicer.Slice(plan, bodies[0].World.PlanningView, Members(bodies), null, new BuildPlannerOptions { GateBlocks = 0 });
        Assert.Empty(bp.Infeasible);
        Assert.Equal(n, bp.Lanes.Count);
        var all = bp.Lanes.SelectMany(l => l.Steps).Concat(bp.Steps.Where(s => s.Lane == -1).Select(s => s.Index)).ToList();
        Assert.Equal(all.Count, all.Distinct().Count());                                   // disjoint
        Assert.Equal(Enumerable.Range(0, bp.Steps.Count), all.OrderBy(i => i));           // every step exactly once
        Assert.Equal(bp.Lanes.Select(l => l.Body).OrderBy(x => x), bodies.Select(b => b.Name).OrderBy(x => x));
        double mean = bp.Lanes.Average(l => l.Steps.Count);
        Assert.All(bp.Lanes, l => Assert.InRange(l.Steps.Count, mean * 0.85, mean * 1.15));
        foreach (var l in bp.Lanes)
        {
            Assert.Equal(l.Steps.OrderBy(i => i), l.Steps);                                 // plan order inside a lane
            Assert.All(l.Steps, i => Assert.Equal(l.Index, bp.Steps[i].Lane));
            foreach (var (step, waits) in l.Waits)
                Assert.All(waits, w => { Assert.True(w < step); Assert.NotEqual(l.Index, bp.Steps[w].Lane); });
        }
        // Regions: each lane holds a contiguous strip along the long axis (x here).
        var spans = bp.Lanes.Select(l => (Lo: l.Steps.Min(i => bp.Steps[i].Target.X), Hi: l.Steps.Max(i => bp.Steps[i].Target.X))).OrderBy(s => s.Lo).ToList();
        for (int k = 1; k < spans.Count; k++) Assert.True(spans[k].Lo >= spans[k - 1].Hi, $"strips overlap: {spans[k - 1]} / {spans[k]}");
        // The estimate shows the benefit: lanes in parallel beat one body.
        Assert.True(bp.CrewTicks < bp.SoloTicks * 0.8, $"crew {bp.CrewTicks:0} vs solo {bp.SoloTicks:0}");
        Assert.Contains($"with {n} bodies", bp.Summary());
    }

    // ------------------------------------------------------------------ the parallel run

    [Fact]
    public async Task Three_bodies_build_in_parallel_honouring_every_cross_lane_wait_and_never_sharing_a_cell()
    {
        var (link, bodies) = Crew(3);
        var plan = PlanOf(Hall, HallAt);
        using var db = AgentDb.Open(null);
        var store = new BlueprintStore(db);
        var crew = Fast(new ConstructionExecutor(new BlueprintOptions(), store));
        var report = await crew.RunAsync(1, plan, bodies, Soon());
        Assert.True(report.Complete, Why(report));
        var w = bodies[0].World;
        foreach (var b in plan.Blocks.Values) Assert.Equal(b.Id, w.GetBlock(b.Pos));
        Assert.All(bodies, b => Assert.NotEmpty(b.Placed));                                // all three built
        Assert.Empty(link.Violations);                                                    // never two bodies in one cell
        var round = report.Rounds[0];
        Assert.Equal(3, round.Lanes.Count);
        Assert.True(round.Lanes.Sum(l => l.Waits.Count) > 0, "the roof hangs across lanes: some waits");
        var first = link.FirstPlaced();
        int checkedWaits = 0;
        foreach (var lane in round.Lanes)
            foreach (var (i, waits) in lane.Waits)
            {
                var s = round.Steps[i];
                if (!s.IsPlacement || !first.TryGetValue(s.Target, out var at)) continue;
                foreach (var d in waits.Select(d => round.Steps[d]).Where(d => d.IsPlacement && first.ContainsKey(d.Target)))
                {
                    Assert.True(first[d.Target] < at, $"{s} placed before {d} it waits for");
                    checkedWaits++;
                }
            }
        Assert.True(checkedWaits > 0);
        // The bodies really worked at the same time (their placements interleave), and the plan was faster for it.
        var order = link.Events.Where(e => e.What.StartsWith("place ")).Select(e => e.Body).ToList();
        Assert.True(order.Zip(order.Skip(1)).Count(p => p.First != p.Second) >= 6, "placements interleave");
        Assert.True(round.CrewTicks < round.SoloTicks);
    }

    [Fact]
    public async Task The_gate_blocks_go_up_and_are_answered_before_any_lane_starts()
    {
        var (_, bodies) = Crew(3);
        var plan = PlanOf(Hall, HallAt);
        using var db = AgentDb.Open(null);
        var store = new BlueprintStore(db);
        var gates = new BuildGates();
        int othersAtAsk = -1, firstAtAsk = -1, asks = 0;
        var single = new ConstructionExecutor(new BlueprintOptions { GateBlocks = 4 }, store)
        {
            Gates = gates,
            Ask = q =>
            {
                asks++;
                othersAtAsk = bodies.Skip(1).Sum(b => b.Placed.Count);
                firstAtAsk = bodies[0].Placed.Count;
                _ = Task.Run(async () =>
                {
                    for (int i = 0; i < 2000 && !gates.IsWaiting(7); i++) await Task.Delay(2);
                    await Task.Delay(30);
                    Assert.Equal(0, bodies.Skip(1).Sum(b => b.Placed.Count)); // still waiting while the question is open
                    gates.Answer(7, new GateReply(GateVerdict.Approve, "PoofImaFox", "yes"));
                });
                return Task.CompletedTask;
            },
        };
        var report = await Fast(single).RunAsync(7, plan, bodies, Soon());
        Assert.True(report.Complete, Why(report));
        Assert.Equal(1, asks);                                    // one gate for the whole build
        Assert.Equal(0, othersAtAsk);                             // nobody else placed a block before it
        Assert.InRange(firstAtAsk, 1, 4);                         // only the gate blocks
        Assert.True(report.Gate!.GatePassed);
        Assert.All(bodies.Skip(1), b => Assert.NotEmpty(b.Placed)); // then every lane built
        Assert.Equal(GateState.Approved, new BuildPlanStore(db).Get(7, 0)!.Gate.State);
    }

    [Fact]
    public async Task A_dead_body_s_steps_are_re_balanced_to_the_others()
    {
        var (link, bodies) = Crew(3);
        var plan = PlanOf(Hall, HallAt);
        using var db = AgentDb.Open(null);
        var dead = bodies[2];
        dead.AfterPlace = n => { if (n == 3) dead.Available = false; }; // gone for good (no respawn expected)
        var report = await Fast(new ConstructionExecutor(new BlueprintOptions(), new BlueprintStore(db))).RunAsync(2, plan, bodies, Soon());
        Assert.True(report.Complete, Why(report));
        Assert.Equal(3, dead.Placed.Count);
        Assert.True(report.Rebalances >= 1, report.Text("hall"));
        Assert.Contains(report.Lanes, l => l.Body == dead.Name && l.State == "lost");
        var second = report.Rounds[1];
        Assert.Equal(2, second.Lanes.Count);
        Assert.DoesNotContain(second.Lanes, l => l.Body == dead.Name);
        foreach (var b in plan.Blocks.Values) Assert.Equal(b.Id, bodies[0].World.GetBlock(b.Pos));
        Assert.Empty(link.Violations);
    }

    [Fact]
    public async Task A_downed_body_with_a_quick_respawn_is_waited_for_when_that_is_faster()
    {
        var (_, bodies) = Crew(2);
        var plan = PlanOf(Hall, HallAt);
        using var db = AgentDb.Open(null);
        var down = bodies[1];
        down.AfterPlace = n =>
        {
            if (n != 2) return;
            down.Available = false;
            down.BackInSeconds = 0.05;
            _ = Task.Run(async () => { await Task.Delay(60); down.Available = true; down.BackInSeconds = null; });
        };
        var report = await Fast(new ConstructionExecutor(new BlueprintOptions(), new BlueprintStore(db))).RunAsync(3, plan, bodies, Soon());
        Assert.True(report.Complete, Why(report));
        Assert.Equal(0, report.Rebalances);
        Assert.True(down.Placed.Count > 2, "it came back and finished its lane");
    }

    [Fact]
    public async Task A_crew_build_resumes_each_lane_from_the_persisted_plan()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mineclaw-crew-{Guid.NewGuid():N}.db");
        try
        {
            var plan = PlanOf(Hall, HallAt);
            var (link1, bodies1) = Crew(2);
            string slice;
            HashSet<BlockPos> before;
            using (var db = AgentDb.Open(path))
            {
                using var cts = new CancellationTokenSource();
                int total = 0;
                foreach (var b in bodies1) b.AfterPlace = _ => { if (Interlocked.Increment(ref total) == 16) cts.Cancel(); };
                var r1 = await Fast(new ConstructionExecutor(new BlueprintOptions { CheckpointEvery = 2 }, new BlueprintStore(db))).RunAsync(4, plan, bodies1, cts.Token);
                Assert.Equal("cancelled", r1.Stopped);
                var store = new BuildPlanStore(db);
                var saved = store.Get(4, 0)!;
                store.MergeLanes(saved);
                Assert.Equal(2, saved.Lanes.Count);
                Assert.All(saved.Lanes, l => Assert.Contains(l.Steps, i => saved.Steps[i].Finished)); // both lanes got somewhere
                Assert.Contains("lane 67 ", saved.LaneProgress());
                foreach (var part in saved.LaneProgress().Split(", ").Select(t => t.Split(' ')[2].Split('/').Select(int.Parse).ToArray()))
                    Assert.InRange(part[0], 0, part[1]);                                // lane progress: design blocks, done ≤ total
                slice = saved.SliceId;
                before = link1.FirstPlaced().Keys.Where(plan.Blocks.ContainsKey).ToHashSet();
                Assert.InRange(before.Count, 10, plan.Blocks.Count - 1);
            }
            // The process restarts with the same crew: the same lanes carry on, nothing already standing is placed again.
            var (link2, bodies2) = Crew(2, null, bodies1.Select(b => b.World).ToList());
            using (var db = AgentDb.Open(path))
            {
                var r2 = await Fast(new ConstructionExecutor(new BlueprintOptions(), new BlueprintStore(db))).RunAsync(4, plan, bodies2, Soon());
                Assert.True(r2.Complete, r2.Text("hall"));
                Assert.Equal(slice, r2.Rounds[0].SliceId);
                var again = link2.FirstPlaced().Keys.Where(before.Contains).ToList();
                Assert.Empty(again);
                foreach (var b in plan.Blocks.Values) Assert.Equal(b.Id, bodies2[0].World.GetBlock(b.Pos));
                Assert.Equal("done", new BuildPlanStore(db).Get(4, 0)!.State);
            }
        }
        finally { try { File.Delete(path); } catch (IOException) { } }
    }

    [Fact]
    public async Task A_body_joining_mid_build_re_slices_the_rest_over_the_bigger_crew()
    {
        var (_, bodies) = Crew(3);
        var plan = PlanOf(Hall, HallAt);
        using var db = AgentDb.Open(null);
        int placed = 0;
        bool joined = false;
        foreach (var b in bodies.Take(2)) b.AfterPlace = _ => { if (Interlocked.Increment(ref placed) == 8) joined = true; };
        var report = await Fast(new ConstructionExecutor(new BlueprintOptions(), new BlueprintStore(db)))
            .RunAsync(5, plan, bodies.Take(2).ToList(), Soon(), live: () => joined ? bodies : bodies.Take(2).ToList());
        Assert.True(report.Complete, Why(report));
        Assert.True(report.Rounds.Count >= 2);
        Assert.Equal(3, report.Rounds[^1].Lanes.Count);
        Assert.NotEmpty(bodies[2].Placed);
    }

    // ------------------------------------------------------------------ hive mode

    /// <summary>A shared in-memory claim board (stands are leased per body, like the hive's).</summary>
    private sealed class Board
    {
        private readonly List<(string Owner, ClaimKey Key)> _leases = new();
        public IClaimBoard View(string owner) => new V(this, owner);

        private sealed class V(Board b, string owner) : IClaimBoard
        {
            public string Owner => owner;
            public bool IsFree(ClaimKey key) { lock (b._leases) return !b._leases.Any(l => l.Owner != owner && l.Key.ConflictsWith(key)); }
            public Task<ClaimResult> TryClaimAsync(ClaimKey key, TimeSpan? ttl = null, string? note = null, CancellationToken ct = default)
            {
                lock (b._leases)
                {
                    if (b._leases.FirstOrDefault(l => l.Owner != owner && l.Key.ConflictsWith(key)) is { Owner: { } holder })
                        return Task.FromResult(ClaimResult.Refused(holder, DateTimeOffset.MaxValue));
                    if (!b._leases.Contains((owner, key))) b._leases.Add((owner, key));
                    return Task.FromResult(ClaimResult.Granted(owner, DateTimeOffset.MaxValue));
                }
            }
            public Task ReleaseAsync(ClaimKey key, CancellationToken ct = default) { lock (b._leases) b._leases.Remove((owner, key)); return Task.CompletedTask; }
        }
    }

    /// <summary>The hive seam: each clone "process" is its own BlueprintService over the same db, running build{bp, lane}.</summary>
    private sealed class Jobs(Dictionary<string, (SimBuildBody Body, BlueprintService Service)> clones, AgentDb db) : IBuildJobs
    {
        public List<(string Body, long Id, int Lane, string LaneBody)> Started { get; } = new();
        public List<Task<(bool Ok, string Text)>> Runs { get; } = new();

        public IReadOnlyList<CrewMember> Candidates(IReadOnlyList<string>? names = null) =>
            clones.Where(kv => names == null || names.Any(n => kv.Key.EndsWith("_" + n) || kv.Key == n))
                .Select(kv => new CrewMember(kv.Key, kv.Value.Body.Feet0)).ToList();

        public string? StartLane(string body, long blueprintId, int lane)
        {
            var bp = new BuildPlanStore(db).Get(blueprintId, 0)!;
            lock (Started) Started.Add((body, blueprintId, lane, bp.Lanes[lane].Body));
            var svc = clones[body].Service;
            Runs.Add(Task.Run(() => svc.BuildLaneAsync(blueprintId.ToString(System.Globalization.CultureInfo.InvariantCulture), lane, Soon())));
            return null;
        }
    }

    [Fact]
    public async Task Hive_mode_starts_one_job_per_clone_lane_with_its_lane_id_and_the_build_completes()
    {
        var (link, bodies) = Crew(3);
        var board = new Board();
        foreach (var b in bodies) b.Claims = board.View(b.Name);
        using var db = AgentDb.Open(null);
        BlueprintService Service(SimBuildBody body)
        {
            var store = new BlueprintStore(db);
            var ex = new ConstructionExecutor(new BlueprintOptions(), store);
            return new BlueprintService(new BlueprintOptions(), store, new BlueprintPreview())
            {
                Site = () => BlueprintTests.Site(body.World), Crew = () => new SingleBodyCrew(body),
                ResolveSite = (at, _) => (new SitePick(new BlockPos(0, 63, 0), at ?? "here"), null), Trusted = () => ["PoofImaFox"],
                Executor = ex, Crews = Fast(ex),
            };
        }
        var main = Service(bodies[0]);
        var clones = bodies.Skip(1).ToDictionary(b => b.Name, b => (b, Service(b)));
        var jobs = new Jobs(clones, db);
        main.Jobs = jobs;
        var rec = main.Store.Insert(new BlueprintRecord
        {
            Name = "long hall", Description = "a long hall", Dsl = Hall, Status = DesignStatus.Accepted, Origin = HallAt, Site = HallAt.Down(),
            AcceptedBy = "PoofImaFox", Bot = "maid_chan",
        });
        rec.Origin = HallAt;
        main.Store.Update(rec);
        main.RequestCrew(rec.Id, new CrewRequest(["67", "69"]));
        var (ok, text) = await main.BuildAsync(rec.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), null, null, null, Soon());
        var lanes = await Task.WhenAll(jobs.Runs);
        Assert.True(ok, text);
        Assert.StartsWith("built #", text);
        Assert.Equal(2, jobs.Started.Count);                                         // one job per clone lane
        Assert.All(jobs.Started, j => Assert.Equal(j.Body, j.LaneBody));              // each with its own lane id
        Assert.Equal(new[] { "maid_chan_67", "maid_chan_69" }, jobs.Started.Select(j => j.Body).OrderBy(x => x));
        Assert.Equal(jobs.Started.Count, jobs.Started.Select(j => j.Lane).Distinct().Count());
        Assert.All(lanes, l => Assert.True(l.Ok, l.Text));
        Assert.All(bodies, b => Assert.NotEmpty(b.Placed));
        foreach (var b in PlanOf(Hall, HallAt).Blocks.Values) Assert.Equal(b.Id, bodies[0].World.GetBlock(b.Pos));
        Assert.Empty(link.Violations);
        Assert.Equal(DesignStatus.Built, main.Store.Get(rec.Id)!.Status);
    }

    // ------------------------------------------------------------------ workers and clones run a saved design

    [Fact]
    public async Task A_worker_told_to_continue_building_design_4_runs_the_plans_next_steps()
    {
        // Live 10-28: "Continue building design #4 'mob drop tower' (approved by Fox)" went to the worker, which had no tool for it.
        var (h, c) = TestBot.Create(new BotOptions { Blueprints = new BlueprintOptions { GateBlocks = 0 } });
        for (int x = -6; x <= 6; x++) for (int z = -6; z <= 6; z++) c.World.SetBlock(x, 64, z, Blocks.Stone, 0);
        c.World.Inventory.SetSlot(0, 36, new ItemStack(Blocks.Cobblestone, 64, 0));
        var s = h.Blueprints!.Service;
        var rec = s.Store.Insert(new BlueprintRecord
        {
            Name = "mob drop tower", Description = "a mob drop tower", Bot = "maid_chan", Status = DesignStatus.Building, AcceptedBy = "PoofImaFox",
            Dsl = "blueprint mob drop tower\nsize 3x2x1\npalette C=cobblestone\nlayer 0-1\nCCC",
        });
        rec.Origin = new BlockPos(2, 65, 2);
        s.Store.Update(rec);

        Assert.Contains("build", h.Runtime.Tools.Definitions(Agent.Llm.ModelTier.Worker).Select(d => d.Name)); // the worker has it
        Assert.DoesNotContain("build", h.Runtime.Tools.Definitions(Agent.Llm.ModelTier.Main).Select(d => d.Name)); // the brain uses design
        // The delegated task names the design: it carries the build{bp} line the worker runs.
        var (hint, refusal) = h.Blueprints.DesignTask($"Continue building design #{rec.Id} 'mob drop tower' (approved by Fox)");
        Assert.Null(refusal);
        Assert.Contains($"build{{bp:\"{rec.Id}\"}}", hint);
        // The worker's call runs the saved plan's next steps on its body.
        var r = await h.RunAs(Agent.Llm.ModelTier.Worker, "build", new { bp = rec.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        var places = c.CallList.Where(x => x.StartsWith("place ")).ToList();
        Assert.True(places.Count > 0, r.Text);
        Assert.Contains(places, p => p.StartsWith("place 2,65,2") || p.StartsWith("place 3,65,2") || p.StartsWith("place 4,65,2"));
        Assert.NotNull(s.Plans!.Get(rec.Id, 0));                                   // through the persisted plan

        // An unapproved design is refused up front: no worker gets a design task it can't execute.
        var draft = s.Store.Insert(new BlueprintRecord { Name = "glass dome", Description = "dome", Bot = "maid_chan", Dsl = BlueprintTests.Hut, Status = DesignStatus.Review });
        var (_, no) = h.Blueprints.DesignTask($"build design #{draft.Id}");
        Assert.Contains("not approved", no);
        var del = await h.RunAs(Agent.Llm.ModelTier.Main, "delegate", new { task = $"keep building design #{draft.Id}" });
        Assert.False(del.Ok);
        Assert.Contains("design build", del.Text);
        Assert.Equal((null, null), h.Blueprints.DesignTask("mine 32 cobblestone"));
    }

    [Fact]
    public async Task A_restart_mid_build_keeps_placed_within_total_in_design_blocks()
    {
        // Live 10-28: "the mob drop tower build (#4, 20/14 placed)": placed summed every placement ever, total was one session's steps.
        var path = Path.Combine(Path.GetTempPath(), $"mineclaw-tally-{Guid.NewGuid():N}.db");
        try
        {
            var w = BlueprintTests.Island(8);
            var plan = PlanOf(Hall, HallAt);
            int design = plan.Blocks.Values.Count(b => !b.UpperDoor);
            for (int run = 0; run < 3; run++)
                using (var db = AgentDb.Open(path))
                {
                    using var cts = new CancellationTokenSource();
                    var body = new SimBuildBody(w) { PlaceLimit = run < 2 ? 12 : int.MaxValue, Stop = cts };
                    var store = new BlueprintStore(db);
                    var r = await new ConstructionExecutor(new BlueprintOptions { CheckpointEvery = 3 }, store).RunAsync(4, plan, BuildPart.Whole(plan), body, cts.Token);
                    var rec = store.GetBuild(4, 0)!;
                    Assert.Equal(design, rec.Total);                                   // the same unit every session
                    Assert.InRange(rec.Placed, 0, rec.Total);
                    Assert.Equal(plan.Blocks.Values.Count(b => !b.UpperDoor && DesignChecks.SameState(b, w.GetBlock(b.Pos), w.GetMeta(b.Pos))), rec.Placed);
                    if (run == 2) { Assert.True(r.Complete, r.Text("hall")); Assert.Equal(rec.Total, rec.Placed); }
                }
        }
        finally { try { File.Delete(path); } catch (IOException) { } }
    }

    [Fact]
    public void The_design_build_op_reads_bodies_and_crew()
    {
        var crew = BlueprintWiring.CrewOf(System.Text.Json.JsonSerializer.SerializeToElement(new { op = "build", bodies = new[] { "67", "69" } }));
        Assert.Equal(["67", "69"], crew!.Bodies!);
        Assert.Equal(3, BlueprintWiring.CrewOf(System.Text.Json.JsonSerializer.SerializeToElement(new { crew = 3 }))!.Count);
        Assert.Equal(["67", "420"], BlueprintWiring.CrewOf(System.Text.Json.JsonSerializer.SerializeToElement(new { bodies = "67, 420" }))!.Bodies!);
        Assert.Null(BlueprintWiring.CrewOf(System.Text.Json.JsonSerializer.SerializeToElement(new { op = "build" })));
    }
}
