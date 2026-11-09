using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.Json;
using MineClaw.App.Supervisor;

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
}
