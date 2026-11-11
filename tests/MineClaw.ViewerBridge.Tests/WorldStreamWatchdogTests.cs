using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using MineClaw.ViewerBridge.Live;

namespace MineClaw.ViewerBridge.Tests;

/// <summary>
/// Live 11-10: the bridge's world stream went from 600 frames a minute to none for two hours while a viewer stayed connected
/// ("0 frames, longest gap 0 ms, reconnects 0" every minute) and the viewers fell back to REST polls: pawns teleported. The pump
/// was fire-and-forget and caught only network and JSON errors, so an OutOfMemoryException (the bridge was at its heap limit) or a
/// frame its cursors could not read ended it for good, and a socket that stayed open but silent was never noticed. These fake
/// supervisor sockets stop sending, or send what makes the pump throw; the stream must come back by itself.
/// </summary>
public sealed class WorldStreamWatchdogTests
{
    private static byte[] Entities(long rev) => Encoding.UTF8.GetBytes(
        $$$"""{"event":"entities","id":{{{rev}}},"data":{"rev":{{{rev}}},"full":false,"bots":[],"entities":[{"id":{{{rev}}},"pos":[1,64,2]}],"removed":[],"events":[],"chests":[]}}""");

    /// <summary>Sends <paramref name="first"/> on the connection, then holds it open and silent until the bridge drops it.</summary>
    private static async Task SendThenHold(HttpContext ctx, Func<WebSocket, Task> first)
    {
        using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
        try
        {
            await first(socket);
            await socket.ReceiveAsync(new byte[1024].AsMemory(), ctx.RequestAborted);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException) { }
    }

    private static async Task<LiveHub.Frame> WaitForEntities(LiveHub.Client client, long rev, TimeSpan within)
    {
        using var timeout = new CancellationTokenSource(within);
        while (true)
        {
            var frame = await client.Reader.ReadAsync(timeout.Token);
            if (frame.Event == "entities" && frame.Text.Contains($"\"rev\":{rev},", StringComparison.Ordinal)) return frame;
        }
    }

    [Fact]
    public async Task A_world_socket_that_goes_silent_is_dropped_and_the_stream_resumes_from_its_cursor()
    {
        await using var host = await BridgeHost.StartAsync();
        int connections = 0;
        var targets = new ConcurrentQueue<string>();
        host.Sup.Sockets["/api/world/socket"] = ctx =>
        {
            int n = Interlocked.Increment(ref connections);
            targets.Enqueue(ctx.Request.QueryString.Value ?? "");
            // One frame, then a stalled publisher: the socket stays open and nothing more comes.
            return SendThenHold(ctx, s => s.SendAsync(Entities(n == 1 ? 41 : 42).AsMemory(), WebSocketMessageType.Text, true, ctx.RequestAborted).AsTask());
        };
        var hub = host.Services.GetRequiredService<LiveHub>();
        hub.WorldSilenceTimeout = TimeSpan.FromMilliseconds(500);
        hub.WorldWatchEvery = TimeSpan.FromMilliseconds(100);
        var (client, _, _) = hub.Subscribe(null);
        try
        {
            await WaitForEntities(client, 41, TimeSpan.FromSeconds(5));
            await WaitForEntities(client, 42, TimeSpan.FromSeconds(5));
            Assert.True(hub.WorldStream.Stalls >= 1, "the silent connection was dropped by the watchdog");
            Assert.True(hub.WorldStream.Reconnects >= 1);
            Assert.Contains(targets, t => t.Contains("entities_since=41", StringComparison.Ordinal)); // resumed from its cursor
            Assert.Contains(", stalls ", hub.WorldStream.Take(0));
        }
        finally { hub.Unsubscribe(client); }
    }

    [Theory]
    [InlineData("out-of-memory")] // what the heap limit threw at the pump in production
    [InlineData("binary-frame")] // InvalidDataException: thrown by the pump itself, yet outside its own filter
    [InlineData("unreadable-revision")] // InvalidOperationException from the cursor's GetValue<long>
    public async Task A_world_message_that_throws_reconnects_instead_of_ending_the_stream(string fault)
    {
        await using var host = await BridgeHost.StartAsync();
        int connections = 0;
        host.Sup.Sockets["/api/world/socket"] = ctx =>
        {
            int n = Interlocked.Increment(ref connections);
            if (n > 1) return SendThenHold(ctx, s => s.SendAsync(Entities(42).AsMemory(), WebSocketMessageType.Text, true, ctx.RequestAborted).AsTask());
            return SendThenHold(ctx, s => fault switch
            {
                "binary-frame" => s.SendAsync(new byte[] { 1, 2, 3 }.AsMemory(), WebSocketMessageType.Binary, true, ctx.RequestAborted).AsTask(),
                "unreadable-revision" => s.SendAsync(Encoding.UTF8.GetBytes(
                    """{"event":"entities","id":1,"data":{"rev":"one","full":false,"bots":[],"entities":[{"id":1}],"removed":[],"events":[]}}""").AsMemory(),
                    WebSocketMessageType.Text, true, ctx.RequestAborted).AsTask(),
                _ => s.SendAsync(Entities(41).AsMemory(), WebSocketMessageType.Text, true, ctx.RequestAborted).AsTask(),
            });
        };
        var hub = host.Services.GetRequiredService<LiveHub>();
        int thrown = 0;
        if (fault == "out-of-memory")
            hub.WorldMessageHook = _ => { if (Interlocked.Exchange(ref thrown, 1) == 0) throw new OutOfMemoryException("test: the heap limit"); };
        // The default silence timeout (5 s) is longer than this test waits: the recovery comes from the failure, not from silence.
        Assert.True(hub.WorldSilenceTimeout >= TimeSpan.FromSeconds(5));
        var (client, _, _) = hub.Subscribe(null);
        try
        {
            await WaitForEntities(client, 42, TimeSpan.FromSeconds(4));
            Assert.Equal(0, hub.WorldStream.Stalls);
            Assert.True(hub.WorldStream.Reconnects >= 1);
            Assert.Equal(0, hub.WorldStream.PumpRestarts);
            // And it keeps flowing on the new connection's cursor afterwards (the stats saw the motion frame).
            Assert.True(hub.WorldStream.Frames >= 1);
        }
        finally { hub.Unsubscribe(client); }
    }

    [Fact]
    public async Task The_watchdog_does_not_drop_a_stream_that_keeps_sending()
    {
        await using var host = await BridgeHost.StartAsync(); // the default fake supervisor sends every 100 ms
        var hub = host.Services.GetRequiredService<LiveHub>();
        hub.WorldSilenceTimeout = TimeSpan.FromMilliseconds(1500);
        hub.WorldWatchEvery = TimeSpan.FromMilliseconds(100);
        var (client, _, _) = hub.Subscribe(null);
        try
        {
            await Task.Delay(2000);
            Assert.Equal(0, hub.WorldStream.Stalls);
            Assert.Equal(0, hub.WorldStream.Reconnects);
            Assert.True(hub.WorldStream.Frames >= 5);
        }
        finally { hub.Unsubscribe(client); }
    }
}
