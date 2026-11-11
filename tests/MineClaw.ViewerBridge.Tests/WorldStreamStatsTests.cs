using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using MineClaw.ViewerBridge.Live;

namespace MineClaw.ViewerBridge.Tests;

/// <summary>The bridge's own view of the world stream's cadence: the supervisor's perf line cannot see a relay stall or a reconnect.</summary>
public sealed class WorldStreamStatsTests
{
    // An hour after the stopwatch's zero, so no reading is the "no frame yet" 0.
    private static long At(int milliseconds) => Stopwatch.Frequency * 3600 + Stopwatch.Frequency * milliseconds / 1000;

    [Fact]
    public void Frames_the_longest_gap_and_reconnects_are_reported_and_start_over_each_minute()
    {
        var stats = new LiveHub.WorldStreamStats();
        stats.Start(100);
        stats.Frame(At(0));
        stats.Frame(At(100));
        stats.Frame(At(430)); // a 330 ms hole
        stats.Reconnect();
        stats.Frame(At(530));

        Assert.Equal("4 frames, longest gap 330 ms, reconnects 1, gc pause 25 ms", stats.Take(125));
        stats.Frame(At(640)); // the interval in progress carries over into the next report
        Assert.Equal("1 frames, longest gap 110 ms, reconnects 0, gc pause 0 ms", stats.Take(125));
    }

    [Fact]
    public void A_silent_stream_reports_the_gap_still_open_and_the_stalls_the_watchdog_dropped()
    {
        // Live 11-10 the line read "0 frames, longest gap 0 ms, reconnects 0" for two hours: the open interval was never counted.
        var stats = new LiveHub.WorldStreamStats();
        stats.Start(0);
        stats.Frame(At(0));
        stats.Stall();
        stats.Reconnect();
        Assert.Equal("1 frames, longest gap 60000 ms, reconnects 1, gc pause 0 ms, stalls 1", stats.Take(0, At(60_000)));
        Assert.Equal("0 frames, longest gap 120000 ms, reconnects 0, gc pause 0 ms", stats.Take(0, At(120_000)));
        stats.PumpRestart();
        Assert.EndsWith(", pump restarts 1", stats.Take(0, At(121_000)));
    }

    [Fact]
    public void A_new_subscription_does_not_count_the_idle_time_before_it_as_a_gap()
    {
        var stats = new LiveHub.WorldStreamStats();
        stats.Start(0);
        stats.Frame(At(0));
        stats.Start(0); // every client left, then one came back a minute later
        stats.Frame(At(60_000));
        stats.Frame(At(60_100));
        Assert.Equal((2L, 0L), (stats.Frames, stats.Reconnects));
        Assert.Equal(100, stats.LongestGapMs, 3);
    }

    [Fact]
    public async Task An_upstream_drop_is_counted_as_a_reconnect_after_the_frames_before_it()
    {
        await using var host = await BridgeHost.StartAsync();
        int connections = 0;
        var reconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Sup.Sockets["/api/world/socket"] = async ctx =>
        {
            using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
            if (Interlocked.Increment(ref connections) == 1)
            {
                await socket.SendAsync(Encoding.UTF8.GetBytes(
                    """{"event":"entities","id":42,"data":{"rev":42,"full":true,"bots":[],"entities":[],"removed":[],"events":[],"chests":[]}}""").AsMemory(),
                    WebSocketMessageType.Text, true, ctx.RequestAborted);
                await socket.SendAsync(Encoding.UTF8.GetBytes(
                    """{"event":"blocks","id":7,"data":{"rev":7,"full":true,"more":false,"blocks":{},"columns":[]}}""").AsMemory(),
                    WebSocketMessageType.Text, true, ctx.RequestAborted);
                // The supervisor ends the socket (a snapshot error or a busy lane does this); a close after the frames keeps their order.
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "World subscription ended", ctx.RequestAborted);
                return;
            }
            reconnected.TrySetResult();
            try { await socket.ReceiveAsync(new byte[1024].AsMemory(), ctx.RequestAborted); }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException) { }
        };
        var hub = host.Services.GetRequiredService<LiveHub>();
        var (client, _, _) = hub.Subscribe(null);
        try
        {
            await reconnected.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal((1L, 1L), (hub.WorldStream.Frames, hub.WorldStream.Reconnects)); // motion frames only; blocks are not counted
            Assert.StartsWith("1 frames, longest gap 0 ms, reconnects 1, gc pause ", hub.WorldStream.Take(GC.GetTotalPauseDuration().TotalMilliseconds));
        }
        finally { hub.Unsubscribe(client); }
    }
}
