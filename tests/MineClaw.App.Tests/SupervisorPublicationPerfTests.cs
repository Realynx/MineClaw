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
}
