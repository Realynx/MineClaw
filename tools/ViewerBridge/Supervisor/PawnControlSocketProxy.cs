using System.Net.WebSockets;
using MineClaw.ViewerBridge.Security;

namespace MineClaw.ViewerBridge.Supervisor;

/// <summary>The signed gateway identity is bound to the upgrade. Session capabilities remain inside socket frames.</summary>
public static class PawnControlSocketProxy
{
    public static async Task HandleAsync(HttpContext ctx, string body, SupervisorClient supervisor)
    {
        var identity = ctx.Identity();
        if (!identity.IsAdmin && !identity.Roles.Contains("story")) { await ApiErrors.Write(ctx, 403, "forbidden", "story role required"); return; }
        if (!ctx.WebSockets.IsWebSocketRequest) { await ApiErrors.Write(ctx, 400, "bad_request", "WebSocket upgrade required"); return; }
        if (body.Length is 0 or > 128 || body.Contains('/') || ctx.Request.Query.Count != 0)
        { await ApiErrors.Write(ctx, 400, "bad_request", "Invalid control socket path"); return; }
        ClientWebSocket upstream;
        try { upstream = await supervisor.ConnectSocketAsync($"api/bodies/{Uri.EscapeDataString(body)}/control/socket", SettingsProxy.IdentityHeaders(identity), ctx.RequestAborted); }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or HttpRequestException)
        { await ApiErrors.Write(ctx, 503, "upstream_unavailable", "Control socket is not available"); return; }
        using (upstream)
        using (var downstream = await ctx.WebSockets.AcceptWebSocketAsync())
        using (var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted))
        {
            var inputs = Relay(downstream, upstream, 4096, lifetime.Token);
            var state = Relay(upstream, downstream, 256 * 1024, lifetime.Token);
            try { await await Task.WhenAny(inputs, state); }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or InvalidDataException) { }
            finally
            {
                lifetime.Cancel();
                // Closing either leg immediately closes the other, allowing the supervisor to release this exact lease.
                foreach (var socket in new WebSocket[] { upstream, downstream })
                    if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                        try
                        {
                            using var close = new CancellationTokenSource(250);
                            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Connection closed", close.Token);
                        }
                        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException) { }
                foreach (var task in new[] { inputs, state }) try { await task; } catch { }
            }
        }
    }

    private static async Task Relay(WebSocket from, WebSocket to, int limit, CancellationToken ct)
    {
        var buffer = new byte[Math.Min(limit, 16 * 1024)];
        int count = 0;
        while (!ct.IsCancellationRequested)
        {
            var frame = await from.ReceiveAsync(buffer.AsMemory(), ct);
            if (frame.MessageType == WebSocketMessageType.Close) return;
            count += frame.Count;
            if (frame.MessageType != WebSocketMessageType.Text || count > limit) throw new InvalidDataException("Invalid socket frame");
            await to.SendAsync(buffer.AsMemory(0, frame.Count), frame.MessageType, frame.EndOfMessage, ct);
            if (frame.EndOfMessage) count = 0;
        }
    }
}
