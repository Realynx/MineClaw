using System.Diagnostics;
using MineClaw.App.Supervisor;

namespace MineClaw.App.Tests;

public sealed class SupervisorPublicationPerfTests
{
    private static long At(int milliseconds) => Stopwatch.Frequency * milliseconds / 1000;

    [Fact]
    public void Publication_gaps_measure_their_own_gc_delta_and_sampled_queue_peak()
    {
        var perf = new SupervisorPerf();
        var control = perf.MonitorPublication("control");
        control.Sample(At(0), 100, 2); // prior process GC is only a baseline
        control.Sample(At(50), 110, 17);
        control.Sample(At(300), 120, 0); // exactly 250ms is not a gap
        control.Sample(At(900), 140, 1);

        Assert.Contains("push control 4 samples, gaps>250ms 1, max 600 ms (GC 20 ms), GC-in-gaps 20 ms, queued-peak-at-send 17", perf.Summary());
    }

    [Fact]
    public void Summary_rotates_counters_without_resetting_a_connections_interval()
    {
        var perf = new SupervisorPerf();
        var world = perf.MonitorPublication("world");
        world.Sample(At(0), 100, 0);
        world.Sample(At(300), 380, 4);
        Assert.Contains("push world 2 samples, gaps>250ms 1, max 300 ms (GC 280 ms)", perf.Summary());
        Assert.DoesNotContain("push world", perf.Summary());

        world.Sample(At(1000), 385, 9);
        Assert.Contains("push world 1 samples, gaps>250ms 1, max 700 ms (GC 5 ms), GC-in-gaps 5 ms, queued-peak-at-send 9", perf.Summary());
    }

    [Fact]
    public void Connections_have_independent_baselines_and_streams_have_separate_counters()
    {
        var perf = new SupervisorPerf();
        var world = perf.MonitorPublication("world");
        var first = perf.MonitorPublication("control");
        var second = perf.MonitorPublication("control");
        world.Sample(At(0), 100, 1);
        first.Sample(At(0), 100, 2);
        second.Sample(At(1000), 120, 3); // a new socket does not inherit another socket's gap
        world.Sample(At(100), 105, 0);
        first.Sample(At(600), 115, 1);

        var line = perf.Summary();
        Assert.Contains("push control 3 samples, gaps>250ms 1, max 600 ms (GC 15 ms), GC-in-gaps 15 ms, queued-peak-at-send 3", line);
        Assert.Contains("push world 2 samples, gaps>250ms 0, max 0 ms (GC 0 ms), GC-in-gaps 0 ms, queued-peak-at-send 1", line);
    }

    [Fact]
    public void Story_routes_and_the_container_read_are_printed_even_when_twelve_routes_cost_more()
    {
        var perf = new SupervisorPerf();
        // Thirteen routes that cost far more than any colony command: only the costliest twelve of them are printed.
        for (int i = 0; i < 13; i++) perf.Record($"/api/costly/{(char)('a' + i)}", 1000 - i);
        perf.Record("/api/story/colonies/story-1/designations", 3);
        perf.Record("/api/story/colonies/story-1/storage", 4);
        perf.Record("/api/story/colonies/story-1", 5);
        perf.Record("/api/story", 6);
        perf.Record("/api/world/containers", 2);

        var line = perf.Summary();
        Assert.Contains("; /api/costly/a 1x p50 1000 p95 1000 max 1000 ms", line);
        Assert.Contains("; /api/costly/l 1x", line);
        Assert.DoesNotContain("/api/costly/m", line); // the thirteenth: neither among the twelve nor one that always prints
        Assert.Contains("; /api/story/colonies/*/designations 1x p50 3 p95 3 max 3 ms", line);
        Assert.Contains("; /api/story/colonies/*/storage 1x p50 4 p95 4 max 4 ms", line);
        Assert.Contains("; /api/story/colonies/* 1x p50 5 p95 5 max 5 ms", line);
        Assert.Contains("; /api/story 1x p50 6 p95 6 max 6 ms", line);
        Assert.Contains("; /api/world/containers 1x p50 2 p95 2 max 2 ms", line);
    }

    [Fact]
    public void A_story_route_among_the_twelve_costliest_is_printed_once()
    {
        var perf = new SupervisorPerf();
        perf.Record("/api/story/colonies/story-1", 900);
        perf.Record("/api/world/index", 10);
        var line = perf.Summary();
        Assert.Equal(line.IndexOf("; /api/story/colonies/* 1x", StringComparison.Ordinal), line.LastIndexOf("; /api/story/colonies/* 1x", StringComparison.Ordinal));
        Assert.Contains("; /api/story/colonies/* 1x p50 900 p95 900 max 900 ms; /api/world/index 1x", line); // still by cost
    }

    [Fact]
    public void The_story_clause_says_how_long_routes_waited_for_the_colony_lock_and_what_held_it()
    {
        // The figures are process-wide and other tests pick, tick and take them meanwhile, so this pins the clause, not its numbers
        // (tests/MineClaw.Hive.Tests/StoryCommandLockTests.cs counts them).
        var line = new SupervisorPerf().Summary();
        Assert.Matches(@"story \d+ picks \d+ ms, \d+ ticks \d+ ms, longest pick \d+ ms tick \d+ ms, lock-wait \d+x \d+ ms max \d+ ms "
            + @"\(busy: \d+ commands \d+ reads\), state-build \d+x \d+ ms max \d+ ms, save \d+x \d+ ms max \d+ ms, seen-chest \d+x \d+ ms max \d+ ms", line);
    }
}
