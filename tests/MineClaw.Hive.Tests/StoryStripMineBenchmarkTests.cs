using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MineClaw.Agent.Rag;
using MineClaw.Hive.Story;
using Xunit.Abstractions;

namespace MineClaw.Hive.Tests;

/// <summary>
/// The director with a huge strip mine queued (live 11-10, Fox: "I just queued a very large strip mine, and it's causing a whole lot
/// of lag"; 182 picks a minute took 153 s, the longest 7 s, ticks up to 8 s, 200 MB/s allocated): the colony of
/// <see cref="StoryStripMineFixture"/>, 47k open build cells. It measures what a simulated second of play costs (every pawn picks
/// and reports, the watchdog tick, the browser's state delta), then each phase on its own: the build search per build, a mine
/// build searched with its cells not known yet, the ore veins, each step of the tick, the full state and the delta, the save. A golden digest of
/// which job each pawn gets over a dozen seconds pins the behaviour while the hot paths are rewritten.
/// </summary>
public sealed class StoryStripMineBenchmarkTests(ITestOutputHelper output)
{
    private const BindingFlags Members = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;

    /// <summary>A director member by name and arguments, optional ones left at their defaults (the phases measured on their own are
    /// private).</summary>
    private static object? Call(object o, string name, params object?[] args)
    {
        var m = o.GetType().GetMethods(Members).Single(x => x.Name == name && x.GetParameters() is var ps && ps.Length >= args.Length
            && ps.Skip(args.Length).All(p => p.IsOptional));
        return m.Invoke(o, args.Concat(m.GetParameters().Skip(args.Length).Select(p => p.DefaultValue)).ToArray());
    }

    private static long Alloc() => GC.GetAllocatedBytesForCurrentThread();

    /// <summary>The world whose block reads the meters count (the test running now).</summary>
    private static FakeStoryWorld? _counted;

    /// <summary>Time, allocations and block reads of one kind of work, summed.</summary>
    private sealed class Meter
    {
        public double Ms;
        public long Bytes, Reads;
        public int N;
        public double Max;

        public T Run<T>(Func<T> f)
        {
            long a0 = Alloc(), b0 = _counted?.BlockReads ?? 0;
            long t0 = Stopwatch.GetTimestamp();
            var r = f();
            double ms = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            Bytes += Alloc() - a0;
            Reads += (_counted?.BlockReads ?? 0) - b0;
            Ms += ms;
            Max = Math.Max(Max, ms);
            N++;
            return r;
        }

        public void Run(Action f) => Run(() => { f(); return 0; });

        public string Line(string what) =>
            $"{what,-36} {N,5}x {Ms,9:0.0} ms  avg {Ms / Math.Max(1, N),8:0.000} ms  max {Max,8:0.000} ms  {Bytes / 1024.0 / Math.Max(1, N),9:0.0} KB  {Reads / Math.Max(1, N),7} reads each";
    }

    /// <summary>What one simulated second costs, summed over the seconds run.</summary>
    private sealed class Second
    {
        public readonly Meter Pick = new(), Report = new(), Tick = new(), Delta = new();
        public long DeltaChars;
        public int Jobs, Digs;
    }

    /// <summary>
    /// One simulated second: every pawn asks for its job; a third of them finish it (a dig really happens; every other one of those
    /// fails), the rest report progress; then the watchdog tick and the browser's delta since the last one. Each job is noted in
    /// <paramref name="log"/> (idle legs as "idle": where they go is random).
    /// </summary>
    private static long Round(StoryStripMineFixture.Rig r, int s, long since, Second? m = null, List<string>? log = null)
    {
        r.Clock.Now += TimeSpan.FromSeconds(1);
        var c = r.C;
        for (int i = 0; i < r.Names.Count; i++)
        {
            var n = r.Names[i];
            var j = m != null ? m.Pick.Run(() => c.Next(n)) : c.Next(n);
            if (j == null) continue;
            var cur = c.CurrentOf(n);
            log?.Add(cur == null || cur.Source is "none" or "wander" ? $"{s} {n} idle" : $"{s} {n} {j["type"]} {j["kind"]} {j["at"]} {cur.Source} {j["label"]}");
            if (m != null) m.Jobs++;
            var id = j["id"]!.GetValue<string>();
            void Report()
            {
                if ((s + i) % 3 != 0) { c.Report(n, id, "progress", null, 0.5); return; }
                bool fail = (s + i) % 6 == 0;
                if (!fail && j["kind"]?.GetValue<string>() == "dig" && j["at"] is { } at)
                {
                    var xyz = at.GetValue<string>().Split(',').Select(int.Parse).ToArray();
                    r.W.Set(xyz[0], xyz[1], xyz[2], 0);
                    if (m != null) m.Digs++;
                }
                c.Report(n, id, fail ? "failed" : "done", fail ? "bench" : null, null);
            }
            if (m != null) m.Report.Run(Report); else Report();
        }
        if (m != null) m.Tick.Run(c.Tick); else c.Tick();
        var st = m != null ? m.Delta.Run(() => c.State(since)) : c.State(since);
        if (m != null) m.DeltaChars += st.ToJsonString().Length;
        return st["rev"]!.GetValue<long>();
    }

    [Fact]
    public void The_fixture_has_the_live_colonys_shape()
    {
        var r = StoryStripMineFixture.Make();
        var c = r.C;
        int open = StoryStripMineFixture.OpenCells(c);
        var mines = c.Builds.Where(b => b.Kind == "mine").ToList();
        output.WriteLine($"{c.Pawns.Count} pawns, {c.Builds.Count} builds ({mines.Count} mine), {c.Builds.Sum(b => b.Ghosts.Count)} cells, {open} open, " +
                         $"{c.Storages.Count} storages, {c.Benches.Count} benches, {c.Zones.Count} zones ({string.Join(" ", c.Zones.Select(z => z.Kind + "/" + z.Mode))}), " +
                         $"{c.Designations.Count} designations ({string.Join(" ", c.Designations.Select(d => d.Kind + "/" + d.Status))}); " +
                         $"mine builds {string.Join(" ", mines.Select(b => b.Ghosts.Count))}");
        Assert.Equal(30, c.Pawns.Count);
        Assert.Equal(16, c.Pawns.Values.Count(p => p.Role == "generalist"));
        Assert.Equal(13, mines.Count);
        Assert.All(mines.Take(12), b => Assert.InRange(b.Ghosts.Count, 3_700, 4_100));
        Assert.InRange(open, 47_000, 48_500);
        Assert.Equal(374, c.Storages.Count);
        Assert.Equal(30, c.Benches.Count);
        Assert.Equal(4, c.Zones.Count);
        Assert.Equal(3, c.Designations.Count);
        var tower = c.Builds.Single(b => b.Material == "stonebrick");
        Assert.Equal(1367, tower.Ghosts.Count);
        Assert.Equal(1, tower.Ghosts.Count(g => g.State != "done"));
        Assert.Equal(142, c.Builds.Single(b => b.Material == "leaves").Ghosts.Count);
        // No two builds share a cell (as live).
        Assert.Equal(c.Builds.Sum(b => b.Ghosts.Count), c.Builds.SelectMany(b => b.Ghosts).Select(g => g.At).Distinct().Count());
    }

    /// <summary>
    /// The baseline: what a second of play costs at this size and where the time goes. Numbers only (machines vary): the lane's later
    /// stages compare against them (artifacts/claude-work/lanes2/lag-diagnosis.md).
    /// </summary>
    /// <param name="viewChunks">8: the mine's far end is out of view (an unloaded cell reads as air: open to dig); 16: all of it is in
    /// view (buried but for the shaft's frontier).</param>
    [Theory]
    [InlineData(8)]
    [InlineData(16)]
    public void Strip_mine_colony_costs_per_phase(int viewChunks)
    {
        using var db = AgentDb.Open(null);
        var r = StoryStripMineFixture.Make(viewChunks: viewChunks, store: new StoryStore(db)); // the tick saves, as live
        output.WriteLine($"view: {viewChunks} chunks around home ({r.W.Unloaded.Count} of the mine's columns out of view)");
        _counted = r.W;
        var c = r.C;
        long since = c.State()["rev"]!.GetValue<long>();
        for (int s = 0; s < 2; s++) since = Round(r, s, since); // warm-up (JIT, caches)
        const int seconds = 10;
        var sec = new Second();
        var seq = typeof(StoryColony).GetField("_materialsSeq", BindingFlags.NonPublic | BindingFlags.Instance)!;
        long passes0 = (long)seq.GetValue(c)!, searches0 = c.Searches;
        var saves0 = StoryPerf.Saves.Take();
        var all = Stopwatch.StartNew();
        for (int s = 2; s < 2 + seconds; s++) since = Round(r, s, since, sec);
        all.Stop();
        int picks = sec.Pick.N;
        var saves = StoryPerf.Saves.Take();
        output.WriteLine($"== a simulated second ({r.Names.Count} pawns, {StoryStripMineFixture.OpenCells(c)} open cells), {seconds} seconds (Debug build):");
        output.WriteLine($"materials passes {((long)seq.GetValue(c)! - passes0) / (double)seconds:0.0} a second; work searches {(c.Searches - searches0) / (double)seconds:0.0} a second; " +
                         $"saves (in the tick) {saves.Count}, {saves.Total.TotalMilliseconds:0} ms in all, the longest {saves.Longest.TotalMilliseconds:0} ms");
        output.WriteLine($"total {all.Elapsed.TotalMilliseconds / seconds:0.0} ms a second = picks {sec.Pick.Ms / seconds:0.0} + reports {sec.Report.Ms / seconds:0.0} + " +
                         $"tick {sec.Tick.Ms / seconds:0.0} + state delta {sec.Delta.Ms / seconds:0.0}; {sec.Jobs} jobs, {sec.Digs} digs");
        output.WriteLine(sec.Pick.Line("pick (Next)"));
        output.WriteLine(sec.Report.Line("report"));
        output.WriteLine(sec.Tick.Line("tick"));
        output.WriteLine(sec.Delta.Line("state delta"));
        output.WriteLine($"state delta size: {sec.DeltaChars / 1024.0 / seconds:0.0} KB a second");

        var (pick, buildSearch, mineSearch) = PickInOrder(r);
        var (materials, bills) = PickPhases(r);
        TickPhases(r);
        StateAndSave(r);
        _counted = null;
        Assert.True(picks > 0);
        // Ceilings on what doesn't depend on the machine (block reads, bytes allocated). Before the lane's S2 (main 70c76aeb): the build
        // search read 20k (view 8) and 284k (view 16) blocks, a whole pick 11k and 152k, and a materials pass allocated 14 MB.
        Assert.InRange(buildSearch.Reads / Math.Max(1, buildSearch.N), 0, 3_000);
        Assert.InRange(mineSearch.Reads / Math.Max(1, mineSearch.N), 0, 3_000); // the mine designs' digs (mining work since 11-10)
        Assert.InRange(pick.Reads / Math.Max(1, pick.N), 0, 3_000);
        Assert.InRange(sec.Pick.Reads / Math.Max(1, sec.Pick.N), 0, 5_000);
        // The whole pick's allocations (a stand-in for its time, which machines vary): 544 KB in view 16 at S1, 261 KB after the lane.
        Assert.InRange(pick.Bytes / Math.Max(1, pick.N), 0, 384L << 10);
        Assert.InRange(materials.Bytes / Math.Max(1, materials.N), 0, 2L << 20);
        // The bill search grouped the stock of every chest for every fuel and input it checked: 2.8 MB a call before 11-10's completion.
        Assert.InRange(bills.Bytes / Math.Max(1, bills.N), 0, 1_536L << 10);
    }

    /// <summary>
    /// Each pawn's pick as <c>Pick</c> runs it, step by step: the gate (offline, drafted, gear), the errands (orders, needs, deliveries,
    /// gear policy), then the work search in the pawn's work order until a type has a job (which type found it, and what each type's
    /// search cost on the way). The materials pass is not due here (nothing changed): its cost is apart.
    /// </summary>
    private (Meter Pick, Meter BuildSearch, Meter MineSearch) PickInOrder(StoryStripMineFixture.Rig r)
    {
        var c = r.C;
        Meter pick = new(), gate = new(), errand = new(), immediate = new();
        var byType = new Dictionary<string, Meter>(StringComparer.Ordinal);
        var found = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var name in r.Names)
        {
            var p = c.Pawns[name];
            var live = r.H.Live(name);
            pick.Run(() => Call(c, "Pick", p, live));
            gate.Run(() => Call(c, "GateJob", p, live));
            errand.Run(() => Call(c, "ErrandJob", p, live));
            if (immediate.Run(() => Call(c, "ImmediateCraftJob", p, live)) != null) { found["immediate craft"] = found.GetValueOrDefault("immediate craft") + 1; continue; }
            foreach (var t in c.WorkOrder(p))
            {
                if (!byType.TryGetValue(t, out var m)) byType[t] = m = new Meter();
                if (m.Run(() => c.Candidate(t, p, live)) is { } job) { found[t + " (" + job.Source + ")"] = found.GetValueOrDefault(t + " (" + job.Source + ")") + 1; break; }
            }
        }
        output.WriteLine($"== a pick in its own order (each of the {r.Names.Count} pawns once; jobs found: {string.Join(", ", found.Select(kv => $"{kv.Key} {kv.Value}"))}):");
        output.WriteLine(pick.Line("whole pick (Pick)"));
        output.WriteLine(gate.Line("  gate (offline, gear, drafted)"));
        output.WriteLine(errand.Line("  errands (needs, deliveries)"));
        output.WriteLine(immediate.Line("  immediate craft"));
        foreach (var (t, m) in byType.OrderByDescending(kv => kv.Value.Ms)) output.WriteLine(m.Line("  work search: " + t));
        return (pick, byType[WorkTypes.Build], byType.GetValueOrDefault(WorkTypes.Mine) ?? new Meter());
    }

    /// <summary>A pick taken apart: each work type's search, the build search build by build (a mine build also with its cells not
    /// known yet), the bill search, and the materials pass (returned) and its steps.</summary>
    private (Meter Materials, Meter Bills) PickPhases(StoryStripMineFixture.Rig r)
    {
        var c = r.C;
        var w = r.W;
        var byType = new Dictionary<string, Meter>(StringComparer.Ordinal);
        Meter mineBuild = new(), minePlaces = new(), otherBuild = new(), mineCold = new(), veinBuild = new(), veinMine = new(), materials = new();
        Meter workOrder = new(), builders = new();
        foreach (var name in r.Names)
        {
            var p = c.Pawns[name];
            var live = r.H.Live(name);
            var order = workOrder.Run(() => c.WorkOrder(p));
            foreach (var t in order)
            {
                if (!byType.TryGetValue(t, out var m)) byType[t] = m = new Meter();
                m.Run(() => c.Candidate(t, p, live));
            }
            veinBuild.Run(() => Call(c, "VeinJob", p, live, WorkTypes.Build));
            veinMine.Run(() => Call(c, "VeinJob", p, live, WorkTypes.Mine));
            int n = builders.Run(() => (int)Call(c, "BuilderCount")!);
            foreach (var b in c.Builds.Where(b => b.Status is not ("paused" or "done")).OrderBy(b => b.Order))
            {
                // A mine design's digs are the Mine search's; its blocks (torches) the Build search's.
                if (b.Kind == "mine") mineBuild.Run(() => Call(c, "MineJobFor", w, p, live, b, n, live.Block, int.MaxValue, true));
                (b.Kind == "mine" ? minePlaces : otherBuild).Run(() => Call(c, "BuildJobFor", w, p, live, b, n, live.Block, int.MaxValue));
            }
        }
        // A mine build searched with nothing known of its cells (after a load, or each search before 11-10): every queued cell read.
        var mineIndex = (System.Collections.IDictionary)typeof(StoryColony).GetField("_mineCells", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(c)!;
        {
            var p = c.Pawns[r.Names[0]];
            var live = r.H.Live(r.Names[0]);
            foreach (var b in c.Builds.Where(b => b.Kind == "mine" && b.Status is not ("paused" or "done")))
            {
                mineIndex.Clear();
                mineCold.Run(() => Call(c, "MineJobFor", w, p, live, b, 1, live.Block, int.MaxValue, true));
            }
        }
        // Every buried cell's verdict due again at once: each mine search wakes a few of them per build and reads them anew.
        Meter recheck = new();
        r.Clock.Now += StoryColony.MineRecheck;
        foreach (var name in r.Names.Where(n => c.Pawns[n].Role == "generalist"))
            recheck.Run(() => c.Candidate(WorkTypes.Mine, c.Pawns[name], r.H.Live(name)));
        Meter immediate = new(), bills = new(), supply = new(), pool = new(), benches = new();
        foreach (var name in r.Names.Where(n => c.Pawns[n].Priorities[WorkTypes.Craft] > 0))
        {
            var p = c.Pawns[name];
            var live = r.H.Live(name);
            immediate.Run(() => Call(c, "ImmediateCraftJob", p, live));
            bills.Run(() => Call(c, "BillJob", p, live, WorkTypes.Craft));
            supply.Run(() => Call(c, "SupplyJob", p, live));
        }
        // The bill search's loop: every priority (0..5) over every bench, nearest first.
        var benchJob = new Dictionary<string, Meter>(StringComparer.Ordinal);
        {
            var p = c.Pawns[r.Names[0]];
            var live = r.H.Live(r.Names[0]);
            for (int priority = 0; priority <= 5; priority++)
                foreach (var bench in c.Benches)
                {
                    var k = $"{bench.Kind}, {(bench.Bills.Count + bench.Tasks.Count > 0 ? "with bills" : "no bills")}";
                    if (!benchJob.TryGetValue(k, out var m)) benchJob[k] = m = new Meter();
                    m.Run(() => Call(c, "BenchJob", w, p, live, WorkTypes.Craft, bench, null, priority));
                }
        }
        // The materials pass taken apart: its tool crafts, the wood it protects, each build's share, the auto bills.
        Meter tools = new(), wood = new(), finish = new(), autoBills = new();
        var passBuild = new Dictionary<string, Meter>(StringComparer.Ordinal);
        {
            var passType = typeof(StoryColony).GetNestedType("MaterialsPass", BindingFlags.NonPublic)!;
            var pass = Activator.CreateInstance(passType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, [c, w, Call(c, "Pool", w)], null)!;
            object? F(string f) => passType.GetField(f)!.GetValue(pass);
            tools.Run(() => passType.GetField("Pool")!.SetValue(pass, Call(c, "ToolCrafts", w, F("Pool"), F("Reserved"), F("Wants"), F("HasTable"))));
            wood.Run(() => Call(pass, "ProtectWood"));
            foreach (var b in c.Builds.OrderBy(b => b.Order))
            {
                var k = b.Status is "done" or "paused" ? "done" : b.Kind == "mine" ? "mine" : "other open";
                if (!passBuild.TryGetValue(k, out var m)) passBuild[k] = m = new Meter();
                m.Run(() => Call(pass, "Build", b));
            }
            finish.Run(() => Call(pass, "FinishWood"));
            autoBills.Run(() => Call(c, "ReconcileAutoBills", F("Wants"), StoryStripMineFixture.Home));
        }
        // Inside the auto bills: whether each furnace has fuel to hand (asked for every furnace on every pass).
        Meter fuel = new();
        foreach (var bench in c.Benches.Where(b => b.Kind == "furnace")) fuel.Run(() => Call(c, "FurnaceCanFuel", bench));
        for (int i = 0; i < 3; i++)
        {
            materials.Run(c.RefreshMaterials);
            pool.Run(() => Call(c, "Pool", w));
            benches.Run(() => Call(c, "ReconcileWorkbenches"));
        }
        output.WriteLine($"== a pick taken apart (each of the {r.Names.Count} pawns once):");
        output.WriteLine(workOrder.Line("work order"));
        foreach (var (t, m) in byType.OrderByDescending(kv => kv.Value.Ms)) output.WriteLine(m.Line("candidate " + t));
        output.WriteLine(builders.Line("builder count"));
        output.WriteLine(mineBuild.Line("mine job: a mine build's digs"));
        output.WriteLine(minePlaces.Line("build job: a mine build's blocks"));
        output.WriteLine(otherBuild.Line("build job: another open build"));
        output.WriteLine(mineCold.Line("  mine: its cells not known yet"));
        output.WriteLine(recheck.Line("mine search, buried cells due again"));
        output.WriteLine(veinBuild.Line("ore veins (build)"));
        output.WriteLine(veinMine.Line("ore veins (mine)"));
        output.WriteLine(immediate.Line("craft: immediate craft job"));
        output.WriteLine(bills.Line("craft: bill job"));
        foreach (var (k, m) in benchJob) output.WriteLine(m.Line("  bench job: " + k));
        output.WriteLine(supply.Line("craft: supply job (builds' crafts)"));
        output.WriteLine(materials.Line("materials pass (RefreshMaterials)"));
        output.WriteLine(pool.Line("  materials: stock pool"));
        output.WriteLine(tools.Line("  materials: tool crafts"));
        output.WriteLine(wood.Line("  materials: protect wood"));
        foreach (var (k, m) in passBuild) output.WriteLine(m.Line("  materials: builds, " + k));
        output.WriteLine(finish.Line("  materials: finish wood"));
        output.WriteLine(autoBills.Line("  materials: auto bills"));
        output.WriteLine(fuel.Line("    auto bills: a furnace's fuel check"));
        output.WriteLine(benches.Line("  materials: reconcile workbenches"));
        return (materials, bills);
    }

    /// <summary>The tick's steps one by one, as Tick runs them (a second apart, the 5 s reconcile forced).</summary>
    private void TickPhases(StoryStripMineFixture.Rig r)
    {
        var c = r.C;
        string[] steps = ["ReadyToGo", "RenewHeldLeases", "ExpireLeases", "ReleaseOffline", "ReconcileStorageSigns", "OreTick", "ScanBenches", "GuardTick",
            "WakeWanderers", "WakeNow", "HaulTick", "PruneEntityMarks", "PruneGoneHarvestMarks", "TempTick", "AuditIdle", "PublishLedger"];
        var meters = steps.ToDictionary(s => s, _ => new Meter());
        var reconcile = new Meter();
        for (int round = 0; round < 3; round++)
        {
            r.Clock.Now += TimeSpan.FromSeconds(1.5);
            foreach (var s in steps) meters[s].Run(() => Call(c, s));
            reconcile.Run(() => c.Reconcile(r.W));
        }
        output.WriteLine("== the tick's steps (3 times each):");
        foreach (var (s, m) in meters.Where(kv => kv.Value.Ms > 0.05).OrderByDescending(kv => kv.Value.Ms)) output.WriteLine(m.Line(s));
        output.WriteLine(reconcile.Line("Reconcile (every 5 s)"));
    }

    /// <summary>The full state (the browser's first load), a delta after one pawn's dig, and a save to the store.</summary>
    private void StateAndSave(StoryStripMineFixture.Rig r)
    {
        var c = r.C;
        Meter full = new(), delta = new(), quiet = new(), save = new(), serial = new();
        long chars = 0;
        for (int i = 0; i < 3; i++) chars = full.Run(() => c.State()).ToJsonString().Length;
        long since = c.Rev;
        var name = r.Names[0];
        long dchars = 0;
        for (int i = 0; i < 3; i++)
        {
            r.Clock.Now += TimeSpan.FromSeconds(1);
            if (c.Next(name) is { } j) c.Report(name, j["id"]!.GetValue<string>(), "done", null, null);
            dchars = delta.Run(() => c.State(since)).ToJsonString().Length;
            since = c.Rev;
            quiet.Run(() => c.State(since)); // nothing changed since: what the 2 Hz poll costs on a quiet colony
        }
        using var db = AgentDb.Open(null);
        var store = new StoryStore(db);
        for (int i = 0; i < 3; i++) save.Run(() => store.Save(c));
        // A tick's save after one more dig: the rows that changed, taken under the lock (plan) and written after it.
        Meter plan = new(), write = new();
        long rows = 0, rowChars = 0;
        for (int i = 0; i < 3; i++)
        {
            r.Clock.Now += TimeSpan.FromSeconds(1);
            if (c.Next(name) is { } j && j["kind"]?.GetValue<string>() == "dig" && j["at"] is { } at)
            {
                var xyz = at.GetValue<string>().Split(',').Select(int.Parse).ToArray();
                r.W.Set(xyz[0], xyz[1], xyz[2], 0);
                c.Report(name, j["id"]!.GetValue<string>(), "done", null, null);
            }
            var p = plan.Run(() => store.Plan(c, full: false));
            rows += p.ObjectRows;
            rowChars += p.ObjectChars;
            write.Run(() => store.Write(p));
        }
        long bytes = 0, biggest = 0;
        serial.Run(() =>
        {
            foreach (var b in c.Builds)
            {
                long n = JsonSerializer.Serialize(b, StoryJson.Options).Length;
                bytes += n;
                biggest = Math.Max(biggest, n);
            }
        });
        output.WriteLine("== state and save:");
        output.WriteLine(full.Line("state, full") + $"  {chars / 1024.0:0} KB");
        output.WriteLine(delta.Line("state, delta after a dig") + $"  {dchars / 1024.0:0.0} KB");
        output.WriteLine(quiet.Line("state, delta with nothing new"));
        output.WriteLine(save.Line("save (whole colony)"));
        output.WriteLine(plan.Line("save after a dig: rows (under lock)") + $"  {rows / 3.0:0.0} object rows, {rowChars / 3.0 / 1024:0} KB each save");
        output.WriteLine(write.Line("save after a dig: db write"));
        output.WriteLine(serial.Line("  builds serialized") + $"  {bytes / 1024.0:0} KB, the biggest {biggest / 1024.0:0} KB");
    }

    /// <summary>
    /// Which job each pawn gets, second by second for a dozen seconds (digs really happen, a third report each second), as one
    /// digest: the hot paths' rewrites must not change it. A change that means to change the jobs updates the digest and says why.
    /// </summary>
    [Fact]
    public void Which_job_each_pawn_gets_is_pinned()
    {
        var r = StoryStripMineFixture.Make();
        long since = r.C.State()["rev"]!.GetValue<long>();
        var log = new List<string>();
        for (int s = 0; s < 12; s++) since = Round(r, s, since, log: log);
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", log))))[..16];
        foreach (var l in log.Take(60)) output.WriteLine(l);
        output.WriteLine($"{log.Count} jobs, {log.Count(l => l.EndsWith(" idle", StringComparison.Ordinal))} idle; digest {digest}");
        Assert.Equal(Golden, digest);
    }

    /// <summary>The digest of <see cref="Which_job_each_pawn_gets_is_pinned"/>. A49B4DFA82E54C1E at main 70c76aeb and through the lane's
    /// speed-ups; changed on purpose when a mine design's digs became mining work (11-10): the miners take the design's digs instead of
    /// quarrying, and those jobs' type is <c>mine</c>, not <c>build</c> (B23A5B484BFC7344). Changed again when the fixture moved to
    /// made-up coordinates (the same shape, 1600 east and 800 south): the jobs' positions, and the ores placed by position, moved.</summary>
    private const string Golden = "3211521576AB2F41";
}
