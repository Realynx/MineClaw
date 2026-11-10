using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.Json;
using MineClaw.App.Supervisor;
using MineClaw.World;
using Microsoft.Extensions.Logging;

namespace MineClaw.App.Tests;

public sealed class WorldSocketTests
{
    [Fact]
    public async Task World_subscription_pushes_motion_chest_state_and_resumable_blocks_then_stops_when_closed()
    {
        int port;
        using (var probe = new TcpListener(IPAddress.Loopback, 0)) { probe.Start(); port = ((IPEndPoint)probe.LocalEndpoint).Port; }
        var world = new FakeSupervisorWorld { OpenChests = [new(0, 1, 64, 2, 1)] };
        var log = new BlockDeltaLog();
        await using var server = new SupervisorServer(new SupervisorOptions { Bind = "127.0.0.1", Port = port, Token = "secret" }, new FakeSupervisorData())
        { World = new SupervisorWorldRoutes(world) { Blocks = log } };
        server.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        using (var refused = new ClientWebSocket())
            await Assert.ThrowsAsync<WebSocketException>(() => refused.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/api/world/socket"), timeout.Token));
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("X-Supervisor-Token", "secret");
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/api/world/socket?entities_since=0&blocks_since=0"), timeout.Token);
        int entities = 0;
        long blocks = -1;
        while (entities < 2 || blocks < 0)
        {
            using var json = await Read();
            var data = json.RootElement.GetProperty("data");
            if (json.RootElement.GetProperty("event").GetString() == "entities")
            { entities++; Assert.Single(data.GetProperty("chests").EnumerateArray()); }
            else blocks = data.GetProperty("rev").GetInt64();
        }
        world.OpenChests = [];
        log.OnBlock(new WorldState(), new BlockChange(new BlockPos(1, 64, 2), 0, 0, 4, 0));
        bool closedChest = false, newBlock = false;
        while (!closedChest || !newBlock)
        {
            using var json = await Read();
            var data = json.RootElement.GetProperty("data");
            if (json.RootElement.GetProperty("event").GetString() == "entities") closedChest = data.GetProperty("chests").GetArrayLength() == 0;
            else if (data.GetProperty("rev").GetInt64() > blocks)
            { Assert.Equal(4, data.GetProperty("blocks").GetProperty("0")[0][3].GetInt32()); newBlock = true; }
        }
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", timeout.Token);
        int calls = world.EntityCalls;
        await Task.Delay(250, timeout.Token);
        Assert.Equal(calls, world.EntityCalls);
        Assert.Contains("push world ", server.Perf.Summary());

        async Task<JsonDocument> Read()
        {
            var bytes = new byte[32 * 1024];
            var got = await socket.ReceiveAsync(bytes.AsMemory(), timeout.Token);
            Assert.Equal(WebSocketMessageType.Text, got.MessageType);
            Assert.True(got.EndOfMessage);
            return JsonDocument.Parse(bytes.AsMemory(0, got.Count));
        }
    }

    /// <summary>Completes on the first world-socket-ended line.</summary>
    private sealed class EndLog : ILogger
    {
        public readonly TaskCompletionSource<(LogLevel Level, string Text)> Ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var text = formatter(state, exception);
            if (text.Contains("world socket ended", StringComparison.Ordinal)) Ended.TrySetResult((logLevel, text));
        }
    }

    [Fact]
    public async Task A_snapshot_error_ends_the_socket_as_failed_with_its_reason_and_the_next_connection_works()
    {
        int port;
        using (var probe = new TcpListener(IPAddress.Loopback, 0)) { probe.Start(); port = ((IPEndPoint)probe.LocalEndpoint).Port; }
        var world = new FakeSupervisorWorld { ThrowEntitiesOnce = 1 };
        var log = new EndLog();
        await using var server = new SupervisorServer(new SupervisorOptions { Bind = "127.0.0.1", Port = port, Token = "secret" }, new FakeSupervisorData(), log)
        { World = new SupervisorWorldRoutes(world) { Blocks = new BlockDeltaLog() } };
        server.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        var uri = new Uri($"ws://127.0.0.1:{port}/api/world/socket?entities_since=0&blocks_since=0");
        var bytes = new byte[32 * 1024];

        using (var broken = new ClientWebSocket())
        {
            broken.Options.SetRequestHeader("X-Supervisor-Token", "secret");
            await broken.ConnectAsync(uri, timeout.Token);
            try
            {
                var got = await broken.ReceiveAsync(bytes.AsMemory(), timeout.Token);
                Assert.Equal(WebSocketMessageType.Close, got.MessageType); // no frame: the very first snapshot failed
            }
            catch (WebSocketException) { } // or the server tore the connection down without a close handshake
        }
        var (level, text) = await log.Ended.Task.WaitAsync(timeout.Token);
        Assert.Equal(LogLevel.Information, level);
        Assert.Contains("SnapshotFailed World snapshot unavailable (500)", text);
        Assert.Contains("entity merge broke", text);
        Assert.Contains("push world 0 samples, gaps>250ms 0, max 0 ms (GC 0 ms), GC-in-gaps 0 ms, queued-peak-at-send 0, longest 0 ms, gaps>150ms 0, opens 1, failed 1",
            server.Perf.Summary());

        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("X-Supervisor-Token", "secret");
        await socket.ConnectAsync(uri, timeout.Token);
        string? evt = null;
        while (evt != "entities")
        {
            var got = await socket.ReceiveAsync(bytes.AsMemory(), timeout.Token);
            Assert.Equal(WebSocketMessageType.Text, got.MessageType);
            using var json = JsonDocument.Parse(bytes.AsMemory(0, got.Count));
            evt = json.RootElement.GetProperty("event").GetString();
        }
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", timeout.Token);
        Assert.Contains(", opens 1, failed 0", server.Perf.Summary());
    }

    [Fact]
    public void World_socket_end_lines_are_at_most_one_per_ten_seconds()
    {
        long last = long.MinValue / 2;
        Assert.True(SupervisorServer.WorldSocketEndDue(ref last, 1_000));
        Assert.False(SupervisorServer.WorldSocketEndDue(ref last, 5_000));
        Assert.False(SupervisorServer.WorldSocketEndDue(ref last, 10_999));
        Assert.True(SupervisorServer.WorldSocketEndDue(ref last, 11_000));
        Assert.Equal(11_000, last);
    }
}
