using System.Diagnostics;
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
        var sw = Stopwatch.StartNew();
        for (int r = 0; r < rounds; r++)
            foreach (var p in c.Pawns.Values) Assert.NotNull(c.Candidate(WorkTypes.Mine, p, h.Live(p.Name)));
        double pickRound = sw.Elapsed.TotalMilliseconds / rounds;

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
}
