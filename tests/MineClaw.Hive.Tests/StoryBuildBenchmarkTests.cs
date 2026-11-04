using System.Text.Json.Nodes;
using MineClaw.Hive.Story;
using Xunit.Abstractions;

namespace MineClaw.Hive.Tests;

/// <summary>
/// Building throughput (docs/web/story-mode.md "Building fast"; live 11-03: "Building floors and placing blocks seems to go very
/// slowly", pawns' shifts were ~90% <c>wait</c>). A simulated colony on simulated time: 10 pawns with only Build on run the director's
/// jobs the way <c>story_pawn</c> does (walk at 3.3 blocks/s in a straight line, a chest session of 2.5 s with the chest held, a
/// placement of 0.6 s within reach, 0.5 s per job for asking and reporting, a hold's seconds), over a 16×16 planks floor and a 4×4×3 cobblestone wall ring, with the materials split over two chests.
/// It reports blocks per minute and the share of pawn time spent waiting.
/// </summary>
public sealed class StoryBuildBenchmarkTests(ITestOutputHelper output)
{
    private sealed class SimTime : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    internal sealed record SimResult(int Placed, int Total, double Seconds, double IdlePct, int Failures, int Busy, int Occupied, List<string> FailLines, int Jobs, int ChestSessions)
    {
        public double PerMinute => Placed / Math.Max(1e-9, Seconds / 60);
    }

    private sealed class Pawn
    {
        public string Name = "";
        public double X, Y = 64, Z;
        public double ReadyAt;
        public double Idle;
        public List<Stack> Inv = new();
        public int Count(int id, int meta) => Inv.Where(s => s.Id == id && s.Meta == meta).Sum(s => s.Count);
        public void Add(int id, int meta, int n) { if (n > 0) Inv.Add(new Stack(id, meta, n)); Merge(); }
        public bool Use(int id, int meta)
        {
            var i = Inv.FindIndex(s => s.Id == id && s.Meta == meta && s.Count > 0);
            if (i < 0) return false;
            Inv[i] = Inv[i] with { Count = Inv[i].Count - 1 };
            Merge();
            return true;
        }
        private void Merge() => Inv = Inv.Where(s => s.Count > 0).GroupBy(s => (s.Id, s.Meta)).Select(g => new Stack(g.Key.Id, g.Key.Meta, g.Sum(s => s.Count))).ToList();
    }

    private const double Walk = 3.3, Reach = 4.5, ChestSession = 2.5, PlaceTime = 0.6, JobOverhead = 0.5;

    /// <summary>Runs the colony until its builds are done (or the cap). <paramref name="chestWaits"/>: a withdraw at a chest another
    /// body holds waits its turn (up to 30 s: the client's 12 s wait plus the script's retries) instead of failing "busy".</summary>
    internal static SimResult Run(int pawns, Action<StoryColony, FakeStoryWorld, FakeStoryHost> setup, double capSeconds = 1800, bool chestWaits = true)
    {
        var time = new SimTime();
        var (c, h, w) = StoryRig.Make(pawns, time: time);
        c.DoneGrace = TimeSpan.Zero;
        var sim = new List<Pawn>();
        int i = 0;
        foreach (var name in c.Pawns.Keys)
        {
            var p = new Pawn { Name = name, X = 0.5 + i % 5, Z = 0.5 + i / 5 };
            i++;
            sim.Add(p);
            foreach (var t in WorkTypes.All) c.Pawns[name].Priorities[t] = t == WorkTypes.Build ? 1 : 0;
        }
        void Sync(Pawn p) => h.Lives[p.Name] = new PawnLive(true, 20, 20, p.X, p.Y, p.Z, 0, null, p.Inv.ToList(), true, 36 - p.Inv.Count);
        foreach (var p in sim) Sync(p);
        setup(c, w, h);
        var chestHeld = new Dictionary<P3, (string Who, double Until)>();
        double start = 0, now = 0, lastTick = -1;
        int failures = 0, busy = 0, occupied = 0, jobs = 0, sessions = 0;
        var failLines = new List<string>();
        var total = c.Builds.Sum(b => b.Ghosts.Count(g => g.Job));

        double Dist(Pawn p, P3 a) => Math.Sqrt(Math.Pow(a.X + 0.5 - p.X, 2) + Math.Pow(a.Y + 0.5 - (p.Y + 1.62), 2) + Math.Pow(a.Z + 0.5 - p.Z, 2));
        // Walks until within d of a (straight line, the pathfinder's best case).
        double WalkTo(Pawn p, P3 a, double d)
        {
            double dx = a.X + 0.5 - p.X, dz = a.Z + 0.5 - p.Z, len = Math.Sqrt(dx * dx + dz * dz);
            if (len <= d) return 0;
            double k = (len - d) / len;
            p.X += dx * k; p.Z += dz * k;
            return (len - d) / Walk;
        }
        static P3 Parse(string s) { var a = s.Split(',').Select(int.Parse).ToArray(); return new P3(a[0], a[1], a[2]); }
        int Id(string item) => w.Data.ItemByNameWithDamage(item) is { } it ? it.Item.Id : 0;
        int Meta(string item) => w.Data.ItemByNameWithDamage(item) is { } it ? Math.Max(0, it.Damage) : 0;

        while (now - start < capSeconds && c.Builds.Any(b => b.Status != "done"))
        {
            var p = sim.OrderBy(x => x.ReadyAt).First();
            now = Math.Max(now, p.ReadyAt);
            time.Now = new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.Zero).AddSeconds(now);
            if (now - lastTick >= 1) { lastTick = now; c.Tick(); }
            Sync(p);
            var j = c.Next(p.Name);
            if (j == null) { p.ReadyAt = now + 1; p.Idle += 1; continue; }
            string id = j["id"]!.GetValue<string>(), kind = j["kind"]!.GetValue<string>();
            double t = 0;
            string? fail = null;
            if (kind is not ("hold" or "idle" or "sleep")) { jobs++; t += JobOverhead; }
            if (kind is "hold" or "idle" or "sleep")
            {
                double s = j["s"]?.GetValue<int>() ?? 5;
                p.Idle += s;
                p.ReadyAt = now + s;
                c.Report(p.Name, id, "done", null, null);
                continue;
            }
            // fetch: walk to the chest, wait for it (or fail busy), one session takes what is there
            foreach (var f in j["fetch"]?.AsArray() ?? [])
            {
                var chest = Parse(f!["from"]!.GetValue<string>());
                var item = f["item"]!.GetValue<string>();
                int n = f["n"]!.GetValue<int>();
                t += WalkTo(p, chest, 3);
                if (chestHeld.TryGetValue(chest, out var held) && held.Until > now + t && held.Who != p.Name)
                {
                    if (!chestWaits || held.Until - (now + t) > 30) { fail = $"busy: {held.Who} is using chest {chest}"; busy++; break; }
                    p.Idle += held.Until - (now + t);
                    t = held.Until - now;
                }
                chestHeld[chest] = (p.Name, now + t + ChestSession);
                sessions++;
                t += ChestSession;
                var stock = h.Chests[chest];
                int idn = Id(item), mt = Meta(item);
                int k = stock.FindIndex(s => s.Id == idn && s.Meta == mt);
                int got = k < 0 ? 0 : Math.Min(n, stock[k].Count);
                if (got == 0) { fail = $"withdraw: no {item} in the chest"; break; }
                stock[k] = stock[k] with { Count = stock[k].Count - got };
                if (stock[k].Count == 0) stock.RemoveAt(k);
                p.Add(idn, mt, got);
                if (j["cells"] != null) { Sync(p); c.Report(p.Name, id, "progress", "fetched", null); }
            }
            if (fail == null && kind == "place")
            {
                var block = j["block"]!.GetValue<string>();
                int bid = Id(block), bm = Meta(block);
                var cells = j["cells"]?.AsArray().Select(x => Parse(x!.GetValue<string>())).ToList() ?? [Parse(j["at"]!.GetValue<string>())];
                bool batch = j["cells"] != null;
                if (j["stand"] is { } st) t += WalkTo(p, Parse(st.GetValue<string>()), 0); // the pawn script steps to its stand first
                foreach (var cell in cells)
                {
                    var (cur, _) = w.Block(cell.X, cell.Y, cell.Z);
                    string? why = null;
                    if (cur != 0 && !(batch && cur == bid)) { why = $"{cell} is occupied by {w.Data.Block(cur).Name}"; occupied++; }
                    else if (cur == bid && batch) { c.Report(p.Name, id, "progress", $"cell {cell} done", null); continue; }
                    else
                    {
                        if (Dist(p, cell) > Reach) t += WalkTo(p, cell, 2.5);
                        if (!p.Use(bid, bm)) why = $"no {block} in inventory";
                        else { t += PlaceTime; w.Set(cell.X, cell.Y, cell.Z, bid, bm); }
                    }
                    if (!batch) { fail = why; break; }
                    Sync(p);
                    c.Report(p.Name, id, "progress", why == null ? $"cell {cell} done" : $"cell {cell} failed: {why}", null);
                }
            }
            else if (fail == null && kind == "go" && j["at"] is { } at) t += WalkTo(p, Parse(at.GetValue<string>()), j["dist"]?.GetValue<int>() ?? 1);
            if (fail == null)
                foreach (var s in j["stash"]?.AsArray() ?? [])
                {
                    var chest = Parse(s!["to"]!.GetValue<string>());
                    t += WalkTo(p, chest, 3) + ChestSession;
                    foreach (var st in p.Inv) h.Chests[chest].Add(st);
                    p.Inv.Clear();
                }
            p.ReadyAt = now + Math.Max(0.05, t);
            Sync(p);
            if (fail != null) { failures++; if (failLines.Count < 12) failLines.Add($"{p.Name} {kind}: {fail}"); }
            c.Report(p.Name, id, fail == null ? "done" : "failed", fail ?? (j["cells"] != null ? "batch" : null), null);
        }
        double secs = Math.Max(1, now - start);
        int placed = c.Builds.Sum(b => b.Ghosts.Count(g => g.Job && g.State == "done"));
        double idlePct = 100 * sim.Sum(x => Math.Min(x.Idle, secs)) / (secs * sim.Count);
        return new SimResult(placed, total, secs, idlePct, failures, busy, occupied, failLines, jobs, sessions);
    }

    internal static void FloorAndWalls(StoryColony c, FakeStoryWorld w, FakeStoryHost h)
    {
        P3 a = new(4, 64, 8), b = new(8, 64, 4);
        w.Set(a.X, a.Y, a.Z, 54); w.Set(b.X, b.Y, b.Z, 54);
        h.Chests[a] = [new Stack(5, 0, 64), new Stack(5, 0, 64), new Stack(5, 0, 64), new Stack(4, 0, 18)];
        h.Chests[b] = [new Stack(5, 0, 64), new Stack(5, 0, 64), new Stack(4, 0, 24)];
        Assert.Null(c.StorageOp("set", [a, b], null, "normal", null).Error);
        Assert.Null(c.BuildsOp("add", new JsonArray(
            new JsonObject { ["kind"] = "floor", ["area"] = new JsonArray(10, 64, 10, 25, 64, 25), ["material"] = "planks" },
            new JsonObject { ["kind"] = "wall", ["area"] = new JsonArray(30, 64, 10, 33, 64, 13), ["material"] = "cobblestone", ["height"] = 3 }), null).Error);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Ten_pawns_lay_a_16x16_floor_and_a_wall_ring_from_two_chests(bool chestWaits)
    {
        var r = Run(10, FloorAndWalls, chestWaits: chestWaits);
        output.WriteLine($"chest waits {chestWaits}; 10 pawns, 16x16 floor + 4x4x3 walls: {r.Placed}/{r.Total} in {r.Seconds:0} s = {r.PerMinute:0.0} blocks/min, idle {r.IdlePct:0}%, " +
                         $"{r.Failures} failed jobs ({r.Busy} chest busy, {r.Occupied} occupied), {r.Jobs} jobs, {r.ChestSessions} chest sessions");
        foreach (var l in r.FailLines) output.WriteLine("  " + l);
        Assert.Equal(r.Total, r.Placed);
    }
}
