using System.Diagnostics;
using System.Text.Json.Nodes;
using MineClaw.Hive.Story;
using Xunit.Abstractions;

namespace MineClaw.Hive.Tests;

/// <summary>
/// The director's cost per tick with a busy colony (live 11-03: the bot ran at ~70% CPU with 10 pawns): 10 pawns, 500 mine
/// designations, a forbidden zone and some forbidden drops. One "tick" here is what the director does in about a second of play:
/// every pawn picks (and finishes) a job, the watchdog tick runs and the browser's state delta is built.
/// </summary>
public sealed class StoryPickerBenchmarkTests(ITestOutputHelper output)
{
    private static (StoryColony C, FakeStoryHost H, FakeStoryWorld W) Rig()
    {
        var (c, h, w) = StoryRig.Make(10);
        int i = 0;
        foreach (var p in c.Pawns.Keys.ToList()) h.Lives[p] = FakeStoryHost.Live(i * 2 % 20, 64, i++ % 5);
        foreach (var p in c.Pawns.Values)
            foreach (var t in WorkTypes.All) p.Priorities[t] = t == WorkTypes.Mine ? 1 : 0;
        // 20 x 5 x 5 = 500 stone cells (the fake world is stone at y <= 63), and something to make "forbidden" checks real.
        Assert.Equal(500, c.Designate("add", "mine", Area.Of(0, 59, 0, 19, 63, 4), null, "all").Result!["count"]!.GetValue<int>());
        c.ZoneOp("add", "forbidden", Area.Of(40, 60, 40, 45, 70, 45));
        for (int e = 0; e < 20; e++) w.Ents.Add(new StoryEntity(1000 + e, "item", "cobblestone", 50 + e, 64, 50, ItemId: 4, Count: 1));
        c.Designate("add", "forbid", Area.Of(49, 60, 49, 80, 70, 51), null, null);
        return (c, h, w);
    }

    [Fact]
    public void Picking_with_10_pawns_and_500_designations_is_cheap()
    {
        var (c, h, w) = Rig();
        // Warm up (JIT).
        foreach (var p in c.Pawns.Values) c.Candidate(WorkTypes.Mine, p, h.Live(p.Name));

        // 1) The pick itself with all 500 open: every pawn asks for the nearest mine job.
        const int rounds = 50;
        long ab = GC.GetAllocatedBytesForCurrentThread();
        var sw = Stopwatch.StartNew();
        for (int r = 0; r < rounds; r++)
            foreach (var p in c.Pawns.Values) Assert.NotNull(c.Candidate(WorkTypes.Mine, p, h.Live(p.Name)));
        double pickRound = sw.Elapsed.TotalMilliseconds / rounds;
        output.WriteLine($"a mine candidate search: {(GC.GetAllocatedBytesForCurrentThread() - ab) / (rounds * 10) / 1024.0:0.0} KB");

        // 2) A whole dig-out: each round every pawn takes its job and finishes it, then the tick and a state delta (the browser polls).
        long since = c.Rev;
        int ticks = 0, done = 0;
        sw.Restart();
        while (c.Designations.Any(d => d.Kind == "mine") && ticks < 400)
        {
            foreach (var name in c.Pawns.Keys)
            {
                var j = c.Next(name);
                if (j == null || j["kind"]?.GetValue<string>() != "dig") { if (j != null) c.Report(name, j["id"]!.GetValue<string>(), "done", null, null); continue; }
                var xyz = j["at"]!.GetValue<string>().Split(",").Select(int.Parse).ToArray(); var at = new P3(xyz[0], xyz[1], xyz[2]);
                w.Set(at.X, at.Y, at.Z, 0);
                c.Report(name, j["id"]!.GetValue<string>(), "done", null, null);
                done++;
            }
            c.Tick();
            var s = c.State(since);
            since = s["rev"]!.GetValue<long>();
            ticks++;
        }
        double perTick = sw.Elapsed.TotalMilliseconds / Math.Max(1, ticks);
        output.WriteLine($"pick round (10 pawns, 500 open): {pickRound:0.000} ms; dig-out: {ticks} ticks, {done} digs, {perTick:0.000} ms per tick");
        Assert.Equal(500, done);
        // Generous bounds (CI machines vary): the point is no O(n²) per pick.
        Assert.True(pickRound < 25, $"a pick round took {pickRound:0.0} ms");
        Assert.True(perTick < 25, $"a tick took {perTick:0.0} ms");
    }

    /// <summary>The real host builds a pawn's pockets (inventory and gear, slot by slot) when they changed and a fresh
    /// <see cref="PawnLive"/> per call (<see cref="PawnLiveCache"/>): this one does too.</summary>
    private sealed class LiveLikeHost(FakeStoryHost inner) : IStoryHost
    {
        public long LiveCalls;
        private readonly Dictionary<string, (PawnLive From, PawnLive Built)> _built = new();
        public IStoryWorld? World => inner.World;
        public (IReadOnlyList<string> Accounts, string? Why) Spawn(string colony, int n, IReadOnlyList<string>? prefer) => inner.Spawn(colony, n, prefer);
        public int Release(string colony) => inner.Release(colony);
        public PawnLive Live(string account)
        {
            LiveCalls++;
            var l = inner.Live(account);
            if (_built.TryGetValue(account, out var b) && ReferenceEquals(b.From, l)) return b.Built with { };
            var inv = l.Inventory.Select(s => s with { }).ToList();
            var gear = new List<GearSlot>();
            for (int slot = 5; slot <= 44; slot++) if (slot < 9 + inv.Count) gear.Add(new GearSlot(slot, 274, 0, 1, new Dictionary<string, int>()));
            var built = l with { Inventory = inv, Gear = gear };
            _built[account] = (l, built);
            return built with { };
        }
        public string? StartPawn(string account, string colony) => inner.StartPawn(account, colony);
        public void Interrupt(string account, string why) => inner.Interrupt(account, why);
    }

    /// <summary>
    /// Per pick and per tick: time, allocations (GC.GetAllocatedBytesForCurrentThread) and pawn live-state reads, with a host that builds
    /// the live state like the real one (live 11-04: 53 ms a pick, 164 MB/s allocated, 4.1 s of GC pause a minute).
    /// </summary>
    [Fact]
    public void A_busy_colony_allocates_little_per_pick_and_tick()
    {
        var (c0, h, w) = Rig();
        var live = new LiveLikeHost(h);
        var c = new StoryColony(c0.Meta, live) { DoneGrace = TimeSpan.Zero };
        foreach (var p in c0.Pawns.Values) c.Pawns[p.Name] = p;
        foreach (var d in c0.Designations) c.Designations.Add(d);
        foreach (var z in c0.Zones) c.Zones.Add(z);
        c.Joined(c.Pawns.Keys);
        Assert.Null(c.StorageOp("set", [new P3(0, 64, 9)], null, null, null).Error);
        c.AddContents(c.StorageAt(new P3(0, 64, 9))!, new Stack(274, 0, 4));
        w.Set(3, 64, -3, 58);
        Assert.Null(c.WorkbenchOp("designate", new P3(3, 64, -3)).Error);
        foreach (var r in new[] { "craft:stick", "craft:planks_any", "craft:wood_pickaxe", "craft:stone_pickaxe" })
            c.BillsOp("add", new JsonObject { ["bench"] = new P3(3, 64, -3).Json(), ["bill"] = new JsonObject { ["recipe"] = r, ["mode"] = "until", ["target"] = 8 } });
        c.State();
        for (int i = 0; i < 3; i++) foreach (var n in c.Pawns.Keys) { var j = c.Next(n); if (j != null) c.Report(n, j["id"]!.GetValue<string>(), "done", null, null); }

        const int rounds = 30;
        long since = c.Rev, calls0 = live.LiveCalls, b0 = GC.GetAllocatedBytesForCurrentThread();
        var sw = Stopwatch.StartNew();
        int picks = 0;
        long pickBytes = 0, tickBytes = 0, stateBytes = 0;
        for (int r = 0; r < rounds; r++)
        {
            foreach (var n in c.Pawns.Keys)
            {
                long a0 = GC.GetAllocatedBytesForCurrentThread();
                var j = c.Next(n);
                pickBytes += GC.GetAllocatedBytesForCurrentThread() - a0;
                picks++;
                if (j != null) c.Report(n, j["id"]!.GetValue<string>(), "failed", "can't reach dirt at 1,63,1", null);
            }
            long t0 = GC.GetAllocatedBytesForCurrentThread();
            c.Tick();
            long s0 = GC.GetAllocatedBytesForCurrentThread();
            tickBytes += s0 - t0;
            since = c.State(since)["rev"]!.GetValue<long>();
            stateBytes += GC.GetAllocatedBytesForCurrentThread() - s0;
        }
        double ms = sw.Elapsed.TotalMilliseconds;
        long bytes = GC.GetAllocatedBytesForCurrentThread() - b0, calls = live.LiveCalls - calls0;
        output.WriteLine($"{picks} picks + {rounds} ticks/states: {ms / picks:0.00} ms and {bytes / picks / 1024.0:0.0} KB a pick (tick and state included), "
            + $"{(double)calls / picks:0.0} live reads a pick; a pick {pickBytes / picks / 1024.0:0.0} KB, a tick {tickBytes / rounds / 1024.0:0.0} KB, "
            + $"a state delta {stateBytes / rounds / 1024.0:0.0} KB");
        Assert.True(bytes / picks < 2 * 1024 * 1024, $"{bytes / picks / 1024} KB a pick");
    }
}
