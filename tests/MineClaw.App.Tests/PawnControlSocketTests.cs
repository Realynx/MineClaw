using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.Json;
using MineClaw.App.Supervisor;
using MineClaw.Client.Tests;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.App.Tests;

public sealed partial class PawnControlTests
{
    [Fact]
    public async Task Replacing_a_socket_fences_old_input_and_disconnect_without_renewing_the_lease()
    {
        var clock = new Clock();
        var body = new Body { HoldAction = true };
        using var sessions = new PawnControlSessions(_ => body, clock, startTimer: false);
        var lease = sessions.Acquire("101", "alice", "tab");
        var old = sessions.Attach("101", "alice", lease.Session);
        sessions.SocketInput(old, new(0, 1, 0, 0, 0));
        var held = sessions.ActionAsync("101", "alice", lease.Session, new(1, "use"));
        await body.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));
        clock.Now += TimeSpan.FromMilliseconds(100);
        var current = sessions.Attach("101", "alice", lease.Session);
        Assert.True(old.Closed.IsCancellationRequested);
        Assert.False((await held.WaitAsync(TimeSpan.FromSeconds(1))).Ok);
        Assert.Equal(0, body.Inputs.Last().Forward);
        Assert.Equal(lease.ExpiresAt, sessions.SocketState(current).ExpiresAt);
        Assert.Throws<PawnControlException>(() => sessions.SocketInput(old, new(1, 1, 0, 0, 0)));
        sessions.Detach(old);
        Assert.Equal(0, body.Ends);
        sessions.SocketInput(current, new(2, 1, 0, 0, 0));
        sessions.Detach(current);
        Assert.Equal(1, body.Ends);
        Assert.Throws<PawnControlException>(() => sessions.Input("101", "alice", lease.Session, new(3, 1, 0, 0, 0)));
    }

    [Fact]
    public void Control_socket_upgrade_requires_identity_and_never_accepts_capabilities_in_urls()
    {
        using var sessions = new PawnControlSessions(_ => new Body(), startTimer: false);
        using var routes = new SupervisorPawnControlRoutes(new SupervisorOptions { Token = "secret", Viewer = { Enabled = true } }, sessions);
        var request = new SupervisorRequest("GET", "/api/bodies/101/control/socket", new Dictionary<string, string>(), "secret",
            Headers: new Dictionary<string, string> { ["X-MCX-User"] = "alice", ["X-MCX-Roles"] = "story" });
        Assert.Null(routes.SocketAuthorization(request));
        Assert.Equal(403, routes.SocketAuthorization(request with { Headers = null })!.Status);
        Assert.Equal(403, routes.SocketAuthorization(request with { TokenHeader = "wrong" })!.Status);
        Assert.Equal(400, routes.SocketAuthorization(request with { Query = new Dictionary<string, string> { ["session"] = "do-not-log" } })!.Status);
    }

    [Fact]
    public async Task Persistent_socket_pushes_without_input_and_receives_input_while_action_is_pending_then_releases_on_close()
    {
        var body = new Body { HoldAction = true };
        using var sessions = new PawnControlSessions(_ => body, startTimer: false);
        var lease = sessions.Acquire("101", "alice", "tab");
        int port;
        using (var probe = new TcpListener(IPAddress.Loopback, 0)) { probe.Start(); port = ((IPEndPoint)probe.LocalEndpoint).Port; }
        var options = new SupervisorOptions { Bind = "127.0.0.1", Port = port, Token = "secret", Viewer = { Enabled = true } };
        using var routes = new SupervisorPawnControlRoutes(options, sessions);
        await using var server = new SupervisorServer(options, new FakeSupervisorData()) { PawnControl = routes };
        server.Start();
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("X-Supervisor-Token", "secret");
        socket.Options.SetRequestHeader("X-MCX-User", "alice");
        socket.Options.SetRequestHeader("X-MCX-Roles", "story");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/api/bodies/101/control/socket"), timeout.Token);
        await Send(new { type = "attach", session = lease.Session });
        var times = new HashSet<long>();
        while (times.Count < 3)
        {
            using var json = await Receive();
            Assert.Equal("state", json.RootElement.GetProperty("type").GetString());
            times.Add(json.RootElement.GetProperty("sampled_at_ms").GetInt64());
        }
        await Send(new { type = "action", request_id = "held", seq = 1, kind = "use" });
        await Send(new { type = "input", seq = 1, forward = 1, strafe = 0, yaw = 90, pitch = 0 });
        await Send(new { type = "action", request_id = "up", seq = 2, kind = "release" });
        bool released = false;
        while (!released)
        {
            using var json = await Receive();
            released = json.RootElement.GetProperty("type").GetString() == "action" && json.RootElement.GetProperty("request_id").GetString() == "up";
        }
        Assert.Contains(body.Inputs, i => i.Seq == 1 && i.Forward == 1);
        Assert.Equal(1, body.Actions);
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "leaving", timeout.Token);
        Assert.Equal(1, body.Ends);
        Assert.Contains("push control ", server.Perf.Summary());

        Task Send(object frame) => socket.SendAsync(SupervisorJson.Bytes(frame).AsMemory(), WebSocketMessageType.Text, true, timeout.Token).AsTask();
        async Task<JsonDocument> Receive()
        {
            var bytes = new byte[16 * 1024];
            var got = await socket.ReceiveAsync(bytes.AsMemory(), timeout.Token);
            Assert.True(got.EndOfMessage);
            Assert.Equal(WebSocketMessageType.Text, got.MessageType);
            return JsonDocument.Parse(bytes.AsMemory(0, got.Count));
        }
    }

    [Fact]
    public async Task State_frames_carry_tick_ack_and_dig()
    {
        // The frozen wire: one state frame with every field, the same file the browser's contract test reads.
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "MineClaw.sln"))) root = root.Parent;
        Assert.NotNull(root);
        using var frozen = JsonDocument.Parse(File.ReadAllText(Path.Combine(root.FullName, "web", "packages", "contracts", "test", "fixtures", "pawn-control-frame.json")));
        var wire = frozen.RootElement.GetProperty("data");
        static string[] Names(JsonElement o) => o.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray();

        using var game = new FakeServer();
        game.Blocks[(8, 64, 10)] = Blocks.Stone;
        game.BreakOnFinish = false; // the test plays the game server's answer
        var (client, live) = await LivePawnAsync(game);
        await using var disposeClient = client;
        Assert.True(SpinWait.SpinUntil(() => client.World.Player.OnGround, TimeSpan.FromSeconds(3)));
        using var sessions = new PawnControlSessions(_ => live, startTimer: false);
        var lease = sessions.Acquire("101", "alice", "tab");
        Assert.Equal(wire.GetProperty("reach").GetDouble(), lease.Reach);
        int port;
        using (var probe = new TcpListener(IPAddress.Loopback, 0)) { probe.Start(); port = ((IPEndPoint)probe.LocalEndpoint).Port; }
        var options = new SupervisorOptions { Bind = "127.0.0.1", Port = port, Token = "secret", Viewer = { Enabled = true } };
        using var routes = new SupervisorPawnControlRoutes(options, sessions);
        await using var server = new SupervisorServer(options, new FakeSupervisorData()) { PawnControl = routes };
        server.Start();
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("X-Supervisor-Token", "secret");
        socket.Options.SetRequestHeader("X-MCX-User", "alice");
        socket.Options.SetRequestHeader("X-MCX-Roles", "story");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/api/bodies/101/control/socket"), timeout.Token);
        await Send(new { type = "attach", session = lease.Session });

        var stone = new BlockPos(8, 64, 10);
        var (yaw, pitch) = PlayerPhysics.YawPitchTo(client.World.Player.EyePosition, stone.Center);
        long seq = 0;
        var ticking = await Frame(data => data.GetProperty("state").TryGetProperty("ack_seq", out var ack) && ack.GetInt64() >= 1);
        var state = ticking.GetProperty("state");
        // Everything the frozen frame names, except what there is nothing to say about yet.
        Assert.Equal(Names(wire.GetProperty("state")).Except(["dig", "broke"]), Names(state));
        Assert.InRange(state.GetProperty("ack_seq").GetInt64(), 1, seq);
        Assert.True(state.GetProperty("ack_wait_ms").GetDouble() >= 0);
        Assert.InRange(state.GetProperty("tick").GetInt64(), 1, client.TickCount + 1);
        Assert.Equal((0d, 0d), (state.GetProperty("vx").GetDouble(), state.GetProperty("vz").GetDouble()));
        Assert.InRange(state.GetProperty("vy").GetDouble(), -0.0785, -0.0783); // a standing body's fall speed, in blocks per tick
        Assert.Equal((0L, false), (state.GetProperty("impulses").GetInt64(), state.GetProperty("using_item").GetBoolean()));
        Assert.Equal("""{"speed":-1,"slowness":-1,"jump_boost":-1}""", state.GetProperty("effects").GetRawText());
        Assert.Equal("""{"x":8,"y":64,"z":10,"face":2}""", state.GetProperty("aim").GetRawText());

        await Send(new { type = "action", request_id = "dig", seq = 1, kind = "attack" });
        var progress = new List<double>();
        await Frame(data =>
        {
            if (!data.GetProperty("state").TryGetProperty("dig", out var dig)) return false;
            Assert.Equal(Names(wire.GetProperty("state").GetProperty("dig")), Names(dig));
            Assert.Equal((8, 64, 10, 2, 1, 0), (dig.GetProperty("x").GetInt32(), dig.GetProperty("y").GetInt32(), dig.GetProperty("z").GetInt32(),
                dig.GetProperty("face").GetInt32(), dig.GetProperty("id").GetInt32(), dig.GetProperty("meta").GetInt32()));
            progress.Add(dig.GetProperty("progress").GetDouble());
            return progress[^1] == 1; // the finish packet is out; the dig stays, complete, until the game server answers
        });
        Assert.All(progress, p => Assert.InRange(p, 0, 1));
        Assert.Equal(progress.Order(), progress);
        Assert.Contains(progress, p => p is > 0 and < 1);

        game.SetBlock(8, 64, 10, 0);
        var broken = await Frame(data => data.TryGetProperty("action", out var action) && !action.GetProperty("pending").GetBoolean()
            && data.GetProperty("state").TryGetProperty("aim", out var aim) && aim.ValueKind == JsonValueKind.Null);
        // Nothing is under the crosshair now: an explicit null, where a missing member would mean "not reported".
        Assert.Equal("""{"seq":1,"pending":false,"ok":true,"message":"dug block","code":"dug"}""", broken.GetProperty("action").GetRawText());
        Assert.Equal(Names(wire.GetProperty("action")), Names(broken.GetProperty("action")));
        Assert.False(broken.GetProperty("state").TryGetProperty("dig", out _));
        Assert.Equal("""{"count":1,"x":8,"y":64,"z":10,"id":1,"meta":0}""", broken.GetProperty("state").GetProperty("broke").GetRawText());
        Assert.Equal(Names(wire.GetProperty("state").GetProperty("broke")), Names(broken.GetProperty("state").GetProperty("broke")));
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "leaving", timeout.Token);

        Task Send(object frame) => socket.SendAsync(SupervisorJson.Bytes(frame).AsMemory(), WebSocketMessageType.Text, true, timeout.Token).AsTask();
        // The next state frame that satisfies the test. Each one is answered with an input frame, as the browser's heartbeat does,
        // and may only use names the frozen frame has.
        async Task<JsonElement> Frame(Func<JsonElement, bool> wanted)
        {
            var bytes = new byte[16 * 1024];
            while (true)
            {
                var got = await socket.ReceiveAsync(bytes.AsMemory(), timeout.Token);
                Assert.True(got.EndOfMessage);
                using var json = JsonDocument.Parse(bytes.AsMemory(0, got.Count));
                if (json.RootElement.GetProperty("type").GetString() != "state") continue;
                Assert.Equal(Names(frozen.RootElement), Names(json.RootElement)); // type, sampled_at_ms, data
                var data = json.RootElement.GetProperty("data");
                Assert.Subset(Names(wire).ToHashSet(), Names(data).ToHashSet());
                Assert.Subset(Names(wire.GetProperty("state")).ToHashSet(), Names(data.GetProperty("state")).ToHashSet());
                await Send(new { type = "input", seq = ++seq, forward = 0, strafe = 0, yaw, pitch });
                if (wanted(data)) return data.Clone();
            }
        }
    }

    /// <summary>A browser's control socket on a supervisor in this process.</summary>
    private sealed class ControlSocket : IAsyncDisposable
    {
        private readonly SupervisorServer _server;
        private readonly CancellationTokenSource _timeout = new(TimeSpan.FromSeconds(10));
        public readonly ClientWebSocket Socket = new();
        public readonly SupervisorPawnControlRoutes Routes;

        private ControlSocket(SupervisorServer server, SupervisorPawnControlRoutes routes) => (_server, Routes) = (server, routes);

        public static async Task<ControlSocket> OpenAsync(PawnControlSessions sessions, string session, TimeSpan? stateFallback = null)
        {
            int port;
            using (var probe = new TcpListener(IPAddress.Loopback, 0)) { probe.Start(); port = ((IPEndPoint)probe.LocalEndpoint).Port; }
            var options = new SupervisorOptions { Bind = "127.0.0.1", Port = port, Token = "secret", Viewer = { Enabled = true } };
            var routes = new SupervisorPawnControlRoutes(options, sessions);
            if (stateFallback is { } fallback) routes.StateFallback = fallback;
            var server = new SupervisorServer(options, new FakeSupervisorData()) { PawnControl = routes };
            server.Start();
            var control = new ControlSocket(server, routes);
            control.Socket.Options.SetRequestHeader("X-Supervisor-Token", "secret");
            control.Socket.Options.SetRequestHeader("X-MCX-User", "alice");
            control.Socket.Options.SetRequestHeader("X-MCX-Roles", "story");
            await control.Socket.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/api/bodies/101/control/socket"), control._timeout.Token);
            await control.Socket.SendAsync(SupervisorJson.Bytes(new { type = "attach", session }).AsMemory(), WebSocketMessageType.Text, true,
                control._timeout.Token);
            return control;
        }

        /// <summary>The next frame, or null when the server closed the socket.</summary>
        public async Task<JsonDocument?> ReceiveAsync()
        {
            var bytes = new byte[16 * 1024];
            var got = await Socket.ReceiveAsync(bytes.AsMemory(), _timeout.Token);
            if (got.MessageType == WebSocketMessageType.Close) return null;
            Assert.True(got.EndOfMessage);
            return JsonDocument.Parse(bytes.AsMemory(0, got.Count));
        }

        /// <summary>The tick of the next state frame, and when it was sampled.</summary>
        public async Task<(long Tick, long At)> StateAsync()
        {
            using var json = await ReceiveAsync();
            Assert.NotNull(json);
            Assert.Equal("state", json.RootElement.GetProperty("type").GetString());
            return (json.RootElement.GetProperty("data").GetProperty("state").GetProperty("tick").GetInt64(),
                json.RootElement.GetProperty("sampled_at_ms").GetInt64());
        }

        public async ValueTask DisposeAsync()
        {
            Socket.Abort();
            Socket.Dispose();
            await _server.DisposeAsync();
            Routes.Dispose();
            _timeout.Dispose();
        }
    }

    [Fact]
    public async Task State_is_published_once_per_tick_and_still_flows_when_ticks_stop()
    {
        long tick = 0;
        var pulse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var body = new Body { Tick = () => Interlocked.Read(ref tick) };
        // The body's next tick: waits for the test to advance the clock, with no limit of its own.
        body.NextState = async (after, _, ct) =>
        {
            while (true)
            {
                var next = Volatile.Read(ref pulse).Task;
                if (Interlocked.Read(ref tick) > after) return;
                await next.WaitAsync(ct);
            }
        };
        void Advance(long ticks)
        {
            Interlocked.Add(ref tick, ticks);
            Interlocked.Exchange(ref pulse, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
        }
        using var sessions = new PawnControlSessions(_ => body, startTimer: false);
        var lease = sessions.Acquire("101", "alice", "tab");
        // No frame without a tick while the ticks run.
        await using var control = await ControlSocket.OpenAsync(sessions, lease.Session, stateFallback: TimeSpan.FromSeconds(30));

        var frames = new List<(long Tick, long At)> { await control.StateAsync() };
        for (int i = 0; i < 4; i++)
        {
            Advance(1);
            frames.Add(await control.StateAsync());
        }
        Advance(3); // a catch-up burst: one frame for the three ticks
        frames.Add(await control.StateAsync());
        Advance(1);
        frames.Add(await control.StateAsync());
        // Frames arrive in order, so a repeated frame for any tick would show here.
        Assert.Equal(new long[] { 0, 1, 2, 3, 4, 7, 8 }, frames.Select(f => f.Tick));
        for (int i = 1; i < frames.Count; i++) Assert.True(frames[i].At - frames[i - 1].At >= 19, $"frame {i} came {frames[i].At - frames[i - 1].At} ms after the one before");

        // The ticks stop (dead, riding, frozen): the body never answers, and the state still flows at the fallback.
        control.Routes.StateFallback = TimeSpan.FromMilliseconds(100);
        Advance(1);
        Assert.Equal(9, (await control.StateAsync()).Tick);
        for (int i = 0; i < 3; i++) Assert.Equal(9, (await control.StateAsync()).Tick);
    }

    [Fact]
    public async Task The_closing_frame_names_why_the_server_ended_the_takeover()
    {
        var body = new Body();
        using var sessions = new PawnControlSessions(_ => body, startTimer: false);
        var lease = sessions.Acquire("101", "alice", "tab");
        await using var control = await ControlSocket.OpenAsync(sessions, lease.Session);
        using (var first = await control.ReceiveAsync()) Assert.Equal("state", first!.RootElement.GetProperty("type").GetString());
        body.Online = false;
        body.Dead = true;
        sessions.Sweep();
        while (true)
        {
            using var frame = await control.ReceiveAsync();
            Assert.NotNull(frame);
            if (frame.RootElement.GetProperty("type").GetString() == "state") continue;
            Assert.Equal("""{"type":"error","reason":"died","message":"Your pawn died. Take control again once it has respawned."}""", frame.RootElement.GetRawText());
            break;
        }
        // Then a clean close: one closing frame, nothing after it, answered as a browser does.
        Assert.Null(await control.ReceiveAsync());
        await control.Socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "ended", CancellationToken.None);
        Assert.Equal(1, body.Ends);
    }

    [Fact]
    public void Takeover_endings_are_named_on_the_socket_only_when_the_server_ends_them()
    {
        var clock = new Clock();
        var body = new Body();
        using var sessions = new PawnControlSessions(_ => body, clock, startTimer: false);
        PawnControlSessions.SocketAttachment Attached(string tab) => sessions.Attach("101", "alice", sessions.Acquire("101", "alice", tab).Session);

        var first = Attached("tab");
        var second = sessions.Attach("101", "alice", first.Session);
        Assert.Equal("replaced", first.EndReason);
        clock.Now += TimeSpan.FromMilliseconds(PawnControlSessions.LeaseTimeoutMs);
        sessions.Sweep();
        Assert.Equal(("expired", true), (second.EndReason, second.Closed.IsCancellationRequested));

        var gone = Attached("tab-2");
        body.Online = false;
        sessions.Sweep();
        Assert.Equal("offline", gone.EndReason);

        body.Online = true;
        var dead = Attached("tab-3");
        (body.Online, body.Dead) = (false, true);
        sessions.Sweep();
        Assert.Equal("died", dead.EndReason);

        (body.Online, body.Dead) = (true, false);
        var released = Attached("tab-4");
        sessions.Release("101", "alice", released.Session);
        Assert.Equal((null, true), (released.EndReason, released.Closed.IsCancellationRequested));
    }

    [Fact]
    public void State_frames_carry_the_inventory_only_when_it_changed()
    {
        var body = new Body { InventoryVersion = 5 };
        using var sessions = new PawnControlSessions(_ => body, startTimer: false);
        var lease = sessions.Acquire("101", "alice", "tab");
        var socket = sessions.Attach("101", "alice", lease.Session);
        Assert.NotNull(sessions.SocketState(socket).Inventory); // the socket's first frame
        Assert.Null(sessions.SocketState(socket).Inventory);
        body.InventoryVersion = 6;
        Assert.NotNull(sessions.SocketState(socket).Inventory);
        Assert.Null(sessions.SocketState(socket).Inventory);
        var next = sessions.Attach("101", "alice", lease.Session);
        Assert.NotNull(sessions.SocketState(next).Inventory);
        body.InventoryVersion = null; // a body that does not count its changes never sends one
        Assert.Null(sessions.SocketState(next).Inventory);
    }

    [Fact]
    public async Task Every_inventory_names_the_version_it_was_read_at()
    {
        // The browser keeps the newer of a click's reply and a state frame by this label: a frame of the click's own tick can carry
        // a newer inventory than the reply (a second slot update in the same tick), and that version is never sent again.
        var body = new Body { InventoryVersion = 5 };
        using var sessions = new PawnControlSessions(_ => body, startTimer: false);
        var lease = sessions.Acquire("101", "alice", "tab");
        Assert.Equal(5, lease.Inventory?.Version);
        var socket = sessions.Attach("101", "alice", lease.Session);
        Assert.Equal(5, sessions.SocketState(socket).Inventory?.Version);
        body.InventoryVersion = 6;
        var click = await sessions.ActionAsync("101", "alice", lease.Session, new(1, "inventory_click", Slot: 36, WindowId: 0));
        Assert.Equal(6, click.Inventory?.Version);
        Assert.Equal(6, (await sessions.ActionAsync("101", "alice", lease.Session, new(2, "inventory"))).Inventory?.Version);
        Assert.Equal(6, sessions.SocketState(socket).Inventory?.Version);
        body.InventoryVersion = null;
        Assert.Null((await sessions.ActionAsync("101", "alice", lease.Session, new(3, "inventory"))).Inventory?.Version);
    }
}
