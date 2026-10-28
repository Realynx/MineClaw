using System.Text.Json;
using MineClaw.Agent.Llm;
using MineClaw.Agent.Tools;

namespace MineClaw.Agent.Tests;

/// <summary>
/// Live 10-27 23:13: "Mine cobblestone at the cobble gen ...": failed: out of time (5 min); 1 actions — a time-boxed gen
/// slice that did mine. A slice that runs out of its minutes with gains is a finished slice: "ok: time slice done".
/// </summary>
public class WorkerTimeSliceTests
{
    /// <summary>Real clock, but timers run 1000x faster (a 1-minute worker budget elapses in 60 ms).</summary>
    private sealed class FastTimers : TimeProvider
    {
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            System.CreateTimer(callback, state, Scale(dueTime), Scale(period));

        private static TimeSpan Scale(TimeSpan t) => t == Timeout.InfiniteTimeSpan ? t : TimeSpan.FromTicks(Math.Max(1, t.Ticks / 1000));
    }

    private static (AgentRuntime Rt, FakeLlm Llm) Bot(string? gains)
    {
        var (rt, llm, _) = TestRuntime.Create(time: new FastTimers());
        // The gen slice runs until the worker's minutes are up (the gen loop never ends on its own for a big count).
        rt.Tools.Register("harvest_generator", "Gen.", ToolSchema.Create().Str("at").Int("count").Build(),
            async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return ToolResult.Success("never"); }, longRunning: true);
        rt.ApplyBrainSplit();
        rt.Worker!.GainsTracker = () => () => gains;
        llm.Fallback = r => llm.Resp(r, StopReasons.ToolUse,
            new LlmToolUse("w1", "harvest_generator", JsonSerializer.SerializeToElement(new { at = "6,64,8", count = 300 })));
        return (rt, llm);
    }

    [Fact]
    public async Task A_slice_that_runs_out_of_time_with_a_haul_is_done_and_says_what_and_where()
    {
        var (rt, _) = Bot("32 cobblestone; stored 30 cobblestone in chest 14,64,-5");
        var o = await rt.Worker!.RunAsync("Mine cobblestone at the cobble gen for 5 min", null, 5, default);
        Assert.True(o.Ok, o.Summary);
        Assert.Equal("time", o.StopReason);
        Assert.Equal("ok: time slice done (5 min): 32 cobblestone; stored 30 cobblestone in chest 14,64,-5", o.Summary);
    }

    [Fact]
    public async Task A_slice_with_nothing_gained_is_still_out_of_time()
    {
        var (rt, _) = Bot(null);
        var o = await rt.Worker!.RunAsync("Mine cobblestone at the cobble gen for 5 min", null, 5, default);
        Assert.False(o.Ok);
        Assert.StartsWith("failed: out of time (5 min)", o.Summary);
    }
}
