using MineClaw.App.Supervisor;

namespace MineClaw.App.Tests;

/// <summary>
/// The world socket asks the entity log for a delta every 100 ms tick, but two ticks are 100 ms apart only on average. With the
/// log's rebuild limit equal to the tick, a tick that came a little early found the log fresh and carried no movement, and the
/// next carried twice as much (mobs and players stepped at 5 Hz). Half the tick keeps every tick fresh.
/// </summary>
public class EntityDeltaCadenceTests
{
    // Socket ticks with ordinary scheduling noise: 99.9, 100.2, 99.9 and 99.9 ms apart.
    private static readonly double[] TickMs = [0, 99.9, 200.1, 300.0, 399.9];

    private static int Rebuilds(TimeSpan minInterval)
    {
        var clock = new ManualClock(DateTimeOffset.UnixEpoch.AddDays(1));
        var start = clock.Now;
        var log = new EntityDeltaLog(clock) { MinInterval = minInterval };
        int builds = 0;
        long rev = 0;
        foreach (var ms in TickMs)
        {
            clock.Now = start.AddTicks((long)Math.Round(ms * TimeSpan.TicksPerMillisecond));
            rev = log.Since(rev, () => { builds++; return []; }).Rev;
        }
        return builds;
    }

    [Fact]
    public void A_rebuild_limit_equal_to_the_tick_skips_early_ticks()
    {
        Assert.Equal(3, Rebuilds(TimeSpan.FromMilliseconds(100)));
    }

    [Fact]
    public void The_routes_rebuild_limit_refreshes_the_log_on_every_tick()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(50), SupervisorWorldRoutes.EntityRebuildInterval);
        Assert.Equal(TickMs.Length, Rebuilds(SupervisorWorldRoutes.EntityRebuildInterval));
    }
}
