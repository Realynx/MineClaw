using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using MineClaw.ViewerBridge.Live;

namespace MineClaw.ViewerBridge.Tests;

public sealed class SocketTests
{
    private static ClientWebSocket SignedSocket(BridgeHost host, string target, string roles)
    {
        var socket = new ClientWebSocket();
        using var signed = host.Signed("GET", target, roles: roles);
        foreach (var h in signed.Headers) socket.Options.SetRequestHeader(h.Key, string.Join(",", h.Value));
        return socket;
    }

    [Fact]
    public async Task Control_socket_requires_story_and_relays_frames_with_the_verified_identity()
    {
        await using var host = await BridgeHost.StartAsync();
        const string target = "/v1/sup/bodies/102/control/socket";
        const string upstream = "/api/bodies/102/control/socket";
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Sup.Sockets[upstream] = async ctx =>
        {
            using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
            var bytes = new byte[4096];
            try
            {
                var frame = await socket.ReceiveAsync(bytes.AsMemory(), ctx.RequestAborted);
                using var json = JsonDocument.Parse(bytes.AsMemory(0, frame.Count));
                Assert.Equal("attach", json.RootElement.GetProperty("type").GetString());
                await socket.SendAsync(Encoding.UTF8.GetBytes("""{"type":"state","data":{"state":{"x":1}}}""").AsMemory(), WebSocketMessageType.Text, true, ctx.RequestAborted);
                await socket.ReceiveAsync(bytes.AsMemory(), ctx.RequestAborted);
            }
            finally { disconnected.TrySetResult(); }
        };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using (var denied = SignedSocket(host, target, "viewer"))
            await Assert.ThrowsAsync<WebSocketException>(() => denied.ConnectAsync(new Uri(host.BaseUrl.Replace("http:", "ws:") + target), timeout.Token));
        Assert.DoesNotContain(host.Sup.Seen, r => r.Target == upstream);
        using var socket = SignedSocket(host, target, "story,viewer");
        await socket.ConnectAsync(new Uri(host.BaseUrl.Replace("http:", "ws:") + target), timeout.Token);
        await socket.SendAsync(Encoding.UTF8.GetBytes("""{"type":"attach","session":"test-only"}""").AsMemory(), WebSocketMessageType.Text, true, timeout.Token);
        var buffer = new byte[4096];
        var got = await socket.ReceiveAsync(buffer.AsMemory(), timeout.Token);
        Assert.Contains("\"type\":\"state\"", Encoding.UTF8.GetString(buffer, 0, got.Count));
        var seen = Assert.Single(host.Sup.Seen, r => r.Target == upstream);
        Assert.Equal("fox", seen.User);
        Assert.Equal("story,viewer", seen.Roles);
        Assert.Empty(seen.Body);
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", timeout.Token);
        await disconnected.Task.WaitAsync(timeout.Token);
    }

    [Fact]
    public async Task Upstream_world_reconnect_resumes_both_cursors_without_restarting_http_pollers()
    {
        await using var host = await BridgeHost.StartAsync();
        int connections = 0;
        host.Sup.Sockets["/api/world/socket"] = async ctx =>
        {
            using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
            if (Interlocked.Increment(ref connections) == 1)
            {
                foreach (var frame in new[] {
                    """{"event":"entities","id":42,"data":{"rev":42,"full":true,"bots":[],"entities":[],"removed":[],"events":[],"chests":[]}}""",
                    """{"event":"blocks","id":7,"data":{"rev":7,"full":true,"more":false,"blocks":{},"columns":[]}}""" })
                    await socket.SendAsync(Encoding.UTF8.GetBytes(frame).AsMemory(), WebSocketMessageType.Text, true, ctx.RequestAborted);
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "reconnect", ctx.RequestAborted);
            }
            else
                try { await socket.ReceiveAsync(new byte[1024].AsMemory(), ctx.RequestAborted); }
                catch (Exception ex) when (ex is WebSocketException or OperationCanceledException) { }
        };
        var hub = host.Services.GetRequiredService<LiveHub>();
        var (client, _, _) = hub.Subscribe(null);
        try
        {
            for (int i = 0; i < 100 && Volatile.Read(ref connections) < 2; i++) await Task.Delay(25);
            Assert.Equal(2, Volatile.Read(ref connections));
            Assert.Contains(host.Sup.Seen, r => r.Target == "/api/world/socket?entities_since=42&blocks_since=7");
            Assert.DoesNotContain(host.Sup.Seen, r => r.Target.StartsWith("/api/world/entities", StringComparison.Ordinal));
        }
        finally { hub.Unsubscribe(client); }
    }

    [Fact]
    public async Task Viewer_socket_uses_shared_world_push_and_stops_subscription_after_disconnect()
    {
        await using var host = await BridgeHost.StartAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var socket = SignedSocket(host, "/v1/live/socket", "viewer");
        await socket.ConnectAsync(new Uri(host.BaseUrl.Replace("http:", "ws:") + "/v1/live/socket"), timeout.Token);
        var bytes = new byte[32 * 1024];
        bool entities = false;
        while (!entities)
        {
            var got = await socket.ReceiveAsync(bytes.AsMemory(), timeout.Token);
            using var frame = JsonDocument.Parse(bytes.AsMemory(0, got.Count));
            Assert.True(frame.RootElement.GetProperty("id").GetInt64() > 0);
            entities = frame.RootElement.GetProperty("event").GetString() == "entities";
            Assert.Equal(JsonValueKind.Object, frame.RootElement.GetProperty("data").ValueKind);
        }
        Assert.Single(host.Sup.Seen, r => r.Target.StartsWith("/api/world/socket?", StringComparison.Ordinal));
        Assert.DoesNotContain(host.Sup.Seen, r => r.Target.StartsWith("/api/world/entities", StringComparison.Ordinal) || r.Target.StartsWith("/api/world/blocks", StringComparison.Ordinal));
        var hub = host.Services.GetRequiredService<LiveHub>();
        Assert.Equal(1, hub.ClientCount);
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", timeout.Token);
        for (int i = 0; i < 20 && hub.ClientCount > 0; i++) await Task.Delay(25, timeout.Token);
        Assert.Equal(0, hub.ClientCount);
        Assert.False(hub.Polling);
    }
}
