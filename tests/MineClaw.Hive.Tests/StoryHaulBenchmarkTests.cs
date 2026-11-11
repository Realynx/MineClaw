using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using MineClaw.Hive.Story;
using Xunit.Abstractions;

namespace MineClaw.Hive.Tests;

/// <summary>
/// The haul search in the live colony's shape (<see cref="StoryHaulFixture"/>; live 11-10: work:haul 56-60 s a minute over 64-71
/// calls, about 0.9 s each and up to 4.5 s, under the colony's one lock). Each pawn's haul search as its pick runs it (its unreachable
/// chests left out), then taken apart: the drops (each drop's reach, its guards, its chest), the re-sort plan, the chest upkeep, the
/// chest memory, a delivery of its pockets. Before 11-10's fix every drop and every stored stack walked and ordered all 374 chests,
/// and the re-sort plan was made again for nearly every pawn: 44 ms and 19 MB a search with 600 drops, 54 ms and 25 MB a re-sort plan
/// (Debug). artifacts/claude-work/lanes2/haul-diagnosis.md has the numbers. The ceilings below are on allocations and block reads
/// (machines vary in speed, not in those). This reaches only a part of the live 0.9 s: the fake host's chest-look list and entity
/// reads are free here, and the live drop count is not known. The perf line's "haul parts", "chest-looks" and "haul scans" (StoryPerf)
/// say where the live time goes and how many drops a live search sees; calibrate the fixture's drops to that.
/// </summary>
[Collection("story-perf")] // takes the process-wide perf figures (StoryPerf.TakeInside): one such class at a time
public sealed class StoryHaulBenchmarkTests(ITestOutputHelper output)
{
    private const BindingFlags Members = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;

    private static object? Call(object o, string name, params object?[] args)
    {
        var m = o.GetType().GetMethods(Members).Single(x => x.Name == name && x.GetParameters() is var ps && ps.Length >= args.Length
            && ps.Skip(args.Length).All(p => p.IsOptional));
        return m.Invoke(o, args.Concat(m.GetParameters().Skip(args.Length).Select(p => p.DefaultValue)).ToArray());
    }

    private static T Del<T>(StoryColony c, string name) where T : Delegate =>
        (T)Delegate.CreateDelegate(typeof(T), c, typeof(StoryColony).GetMethod(name, Members)!);

    private static void SetField(object o, string name, object? v) => o.GetType().GetField(name, Members)!.SetValue(o, v);

    private static long Alloc() => GC.GetAllocatedBytesForCurrentThread();

    /// <summary>The world whose block reads the meters count.</summary>
    [ThreadStatic] private static FakeStoryWorld? _counted;

    private sealed class Meter
    {
        public double Ms, Max;
        public long Bytes, Reads;
        public int N;

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

        public double Avg => Ms / Math.Max(1, N);
        public long AvgBytes => Bytes / Math.Max(1, N);
        public long AvgReads => Reads / Math.Max(1, N);

        public string Line(string what) =>
            $"{what,-40} {N,5}x {Ms,9:0.0} ms  avg {Avg,8:0.000} ms  max {Max,8:0.000} ms  {AvgBytes / 1024.0,9:0.0} KB  {AvgReads,6} reads";
    }

    /// <summary>As a pick runs a search: this pawn's unreachable chests are left out, the world's entities read once, and (since the
    /// fix) the pick's memory of which chests take what is on.</summary>
    private static T AsPick<T>(StoryColony c, string pawn, Func<T> f)
    {
        SetField(c, "_pickPawn", pawn);
        SetField(c, "_pickEnts", null);
        bool scoped = typeof(StoryColony).GetMethod("BeginChestChoice", Members) != null;
        if (scoped) Call(c, "BeginChestChoice");
        try { return f(); }
        finally
        {
            if (scoped) Call(c, "EndChestChoice");
            SetField(c, "_pickPawn", null);
            SetField(c, "_pickEnts", null);
        }
    }

    /// <param name="drops">Drops on the ground (a third of them up top, in the haul area).</param>
    /// <param name="filled">Every n-th chest holds the mine's output: 8 is the live snapshot's storage (about 260 stacks), 2 a strip
    /// mine's output piling up (about 1,000).</param>
    [Theory]
    [InlineData(600, 8)]
    [InlineData(2400, 2)]
    public void Haul_search_costs_per_pawn(int drops, int filled)
    {
        var r = StoryHaulFixture.Make(drops: drops, filled: filled);
        var c = r.C;
        _counted = r.W;
        output.WriteLine($"{c.Pawns.Count} pawns, {c.Storages.Count} storages ({c.Storages.Count(s => s.DoubleWith != null)} double), " +
                         $"{r.W.Ents.Count} drops, {c.Storages.Sum(s => s.Contents.Count)} stored stacks, " +
                         $"{c.Storages.Count(s => s.FreeSlots == 0)} chests full or missing");
        foreach (var n in r.Names) AsPick(c, n, () => c.Candidate(WorkTypes.Haul, c.Pawns[n], r.H.Live(n))); // warm-up (JIT)
        Meter haul = new(), dropsM = new(), rebalance = new(), resorts = new(), inspect = new(), refresh = new(), deliver = new(), pick = new();
        for (int round = 0; round < 3; round++)
        {
            r.Clock.Now += TimeSpan.FromSeconds(2);
            foreach (var n in r.Names)
            {
                var p = c.Pawns[n];
                var live = r.H.Live(n);
                haul.Run(() => AsPick(c, n, () => c.Candidate(WorkTypes.Haul, p, live)));
                dropsM.Run(() => AsPick(c, n, () => c.HaulJob(p, live)));
                rebalance.Run(() => AsPick(c, n, () => c.RebalanceJob(p, live)));
                SetField(c, "_resort", null);
                resorts.Run(() => AsPick(c, n, () => Call(c, "Resorts")));
                inspect.Run(() => AsPick(c, n, () => Call(c, "HaulInspection", p, live)));
                refresh.Run(() => AsPick(c, n, () => c.RefreshSeen(c.Storages)));
                deliver.Run(() => AsPick(c, n, () => Call(c, "Deliver", p, live, true)));
                pick.Run(() => Call(c, "Pick", p, live));
            }
        }
        var (reach, guard, dest, reached) = DropSearchApart(r);
        output.WriteLine($"drops in reach of a pawn: {reached:0.0} of {r.W.Ents.Count}");
        output.WriteLine(reach.Line("  each drop's reach (HaulReach)"));
        output.WriteLine(guard.Line("  their guard checks (DropGuarded)"));
        output.WriteLine(dest.Line("  their chests (HaulDest)"));
        output.WriteLine(haul.Line("haul candidate (HaulingWork)"));
        output.WriteLine(dropsM.Line("  drops (HaulJob)"));
        output.WriteLine(rebalance.Line("  re-sort trip (RebalanceJob)"));
        output.WriteLine(resorts.Line("    re-sort plan, made anew (Resorts)"));
        output.WriteLine(inspect.Line("  chest upkeep (HaulInspection)"));
        output.WriteLine(refresh.Line("  chest memory (RefreshSeen)"));
        output.WriteLine(deliver.Line("delivery of its pockets (Deliver)"));
        output.WriteLine(pick.Line("whole pick (Pick)"));
        output.WriteLine(StoryPerf.TakeInside());
        _counted = null;
        // Ceilings (Debug): before the fix a haul search allocated 19 MB (600 drops) and 70 MB (2,400), a re-sort plan 25 and 92 MB.
        Assert.InRange(haul.AvgBytes, 0, 512L << 10);
        Assert.InRange(dropsM.AvgBytes, 0, 512L << 10);
        Assert.InRange(resorts.AvgBytes, 0, 512L << 10);
        Assert.InRange(rebalance.AvgBytes, 0, 512L << 10);
        Assert.InRange(inspect.AvgBytes, 0, 256L << 10);
        Assert.InRange(deliver.AvgBytes, 0, 512L << 10);
        Assert.InRange(pick.AvgBytes, 0, 768L << 10);
        // Block reads: each drop up top reads the column over it (is it under ground?), each chest its own block (still a chest?).
        Assert.InRange(haul.AvgReads, 0, 20 * drops + 2 * c.Storages.Count);
    }

    /// <summary>One drop search per pawn taken apart (each drop's reach, its guards, its chest), through delegates (no boxing).</summary>
    private static (Meter Reach, Meter Guard, Meter Dest, double Reached) DropSearchApart(StoryStripMineFixture.Rig r)
    {
        var c = r.C;
        var haulReach = Del<Func<StoryEntity, PawnState, P3, bool>>(c, "HaulReach");
        var guarded = Del<Func<StoryEntity, PawnState, IReadOnlyList<StoryEntity>, bool>>(c, "DropGuarded");
        var haulDest = Del<Func<int, int, P3, StorageState?, (StorageState? Chest, P3? Dump, bool Fallback)?>>(c, "HaulDest");
        Meter reach = new(), guard = new(), dest = new();
        int reached = 0;
        foreach (var n in r.Names)
        {
            var p = c.Pawns[n];
            var from = r.H.Live(n).Block;
            AsPick(c, n, () =>
            {
                var ents = r.W.Entities();
                var items = ents.Where(e => e.Kind == "item").ToList();
                var ok = reach.Run(() =>
                {
                    var list = new List<StoryEntity>();
                    foreach (var e in items) if (haulReach(e, p, from)) list.Add(e);
                    return list;
                });
                reached += ok.Count;
                guard.Run(() => { int k = 0; foreach (var e in ok) if (guarded(e, p, ents)) k++; return k; });
                dest.Run(() => { int k = 0; foreach (var e in ok) if (haulDest(e.ItemId, e.ItemMeta, e.Block, null) != null) k++; return k; });
                return 0;
            });
        }
        return (reach, guard, dest, reached / (double)r.Names.Count);
    }

    /// <summary>
    /// Which job each pawn of the haul colony gets, second by second for a dozen seconds, with where each trip takes things (its chests):
    /// one digest, the same before and after the fix (the chests' answers are the same, only worked out once). A collect that ends
    /// done takes its drops out of the world; a third of the jobs report each second, every other one of those fails.
    /// </summary>
    [Fact]
    public void Which_job_each_pawn_gets_in_the_haul_colony_is_pinned()
    {
        var r = StoryHaulFixture.Make();
        var c = r.C;
        long since = c.State()["rev"]!.GetValue<long>();
        var log = new List<string>();
        for (int s = 0; s < 12; s++)
        {
            r.Clock.Now += TimeSpan.FromSeconds(1);
            for (int i = 0; i < r.Names.Count; i++)
            {
                var n = r.Names[i];
                var j = c.Next(n);
                if (j == null) continue;
                var cur = c.CurrentOf(n);
                log.Add(cur == null || cur.Source is "none" or "wander" ? $"{s} {n} idle"
                    : $"{s} {n} {j["type"]} {j["kind"]} {j["at"]} {cur.Source} {j["label"]} -> {Chests(j)}");
                Report(r, c, n, j, cur, s + i);
            }
            c.Tick();
            since = c.State(since)["rev"]!.GetValue<long>();
        }
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", log))))[..16];
        foreach (var l in log.Where(l => !l.EndsWith(" idle", StringComparison.Ordinal)).Take(80)) output.WriteLine(l);
        output.WriteLine($"{log.Count} jobs, {log.Count(l => l.EndsWith(" idle", StringComparison.Ordinal))} idle, " +
                         $"{log.Count(l => l.Contains(" haul ", StringComparison.Ordinal))} haul; digest {digest}");
        Assert.Equal(Golden, digest);
    }

    /// <summary>Where a job takes things from and to: its fetches' chests, then its stash's and store's chests (sorted).</summary>
    private static string Chests(JsonObject j)
    {
        var from = (j["fetch"] as JsonArray ?? []).Select(f => f?["from"]?.GetValue<string>()).Where(x => x != null).Distinct().Order();
        var to = (j["stash"] as JsonArray ?? []).Concat(j["store"] as JsonArray ?? []).Select(f => f?["to"]?.GetValue<string>())
            .Where(x => x != null).Distinct().Order();
        return $"from [{string.Join(" ", from)}] to [{string.Join(" ", to)}]";
    }

    private static void Report(StoryStripMineFixture.Rig r, StoryColony c, string n, JsonObject j, ActiveJob? cur, int k)
    {
        var id = j["id"]!.GetValue<string>();
        if (k % 3 != 0) { c.Report(n, id, "progress", null, 0.5); return; }
        bool fail = k % 6 == 0;
        if (!fail && cur is { Kind: "collect" })
        {
            var gone = new HashSet<string>(cur.MoreLeases) { cur.Lease ?? "" };
            r.W.Ents.RemoveAll(e => gone.Contains("e:" + e.Id));
        }
        if (!fail && j["kind"]?.GetValue<string>() == "dig" && j["at"] is { } at)
        {
            var xyz = at.GetValue<string>().Split(',').Select(int.Parse).ToArray();
            r.W.Set(xyz[0], xyz[1], xyz[2], 0);
        }
        c.Report(n, id, fail ? "failed" : "done", fail ? "bench" : null, null);
    }

    /// <summary>The digest of <see cref="Which_job_each_pawn_gets_in_the_haul_colony_is_pinned"/>, made with main dc9a17a1's code (before
    /// the fix) and unchanged by it.</summary>
    private const string Golden = "FE89234C567D17D0";
}
