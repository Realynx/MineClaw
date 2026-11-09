using System.Net.WebSockets;
using System.Text.Json;

namespace MineClaw.App.Supervisor;

public sealed partial class SupervisorPawnControlRoutes
{
    public static bool IsSocketPath(string path) => IsControlPath(path) && path.EndsWith("/control/socket", StringComparison.Ordinal);

    internal SupervisorReply? SocketAuthorization(SupervisorRequest request)
    {
        if (!SupervisorStoryRoutes.Writes(options) || !TokenOk(request.TokenHeader, options.Token))
            return Reply(403, new { error = "Story control permission required." });
        var owner = Header(request, "X-MCX-User");
        var roles = (Header(request, "X-MCX-Roles") ?? "").Split([',', ' '], StringSplitOptions.RemoveEmptyEntries);
        if (string.IsNullOrWhiteSpace(owner) || owner.Length > 128 || !roles.Any(r => r is "story" or "admin"))
            return Reply(403, new { error = "Authenticated Story permission required." });
        var parts = request.Path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (request.Method != "GET" || parts.Length != 5 || parts[3] != "control" || parts[4] != "socket"
            || Uri.UnescapeDataString(parts[2]) is not { Length: > 0 and <= 128 } body || body.Contains('/'))
            return Reply(400, new { error = "Invalid control socket path." });
        // The capability belongs in the first encrypted frame, never URLs/access logs.
        if (request.Query.Count != 0) return Reply(400, new { error = "Control sockets do not accept query parameters." });
        return null;
    }

    internal async Task SocketAsync(WebSocket socket, SupervisorRequest request, CancellationToken shutdown, SupervisorPerf? perf = null)
    {
        PawnControlSessions.SocketAttachment? attachment = null;
        using var send = new SemaphoreSlim(1);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
        CancellationTokenSource? bound = null;
        Task? receive = null, publish = null;
        try
        {
            using var firstTimeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
            firstTimeout.CancelAfter(TimeSpan.FromSeconds(3));
            using var first = await ReadSocketJson(socket, firstTimeout.Token).ConfigureAwait(false);
            if (first == null) return;
            if (Text(first.RootElement, "type") != "attach") throw new PawnControlException(400, "Attach the control session first.");
            var body = Uri.UnescapeDataString(request.Path.Split('/', StringSplitOptions.RemoveEmptyEntries)[2]);
            attachment = sessions.Attach(body, Header(request, "X-MCX-User")!, Text(first.RootElement, "session"));
            bound = CancellationTokenSource.CreateLinkedTokenSource(stop.Token, attachment.Closed.Token);
            // State delivery and receiving input run independently. An acknowledgement cannot serialize movement.
            receive = ReceiveInputs(socket, send, attachment, bound.Token);
            publish = PublishState(socket, send, attachment, bound.Token, perf?.MonitorPublication("control"));
            await await Task.WhenAny(receive, publish).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is PawnControlException or JsonException or ArgumentException)
        {
            try { await SendSocketJson(socket, send, new { type = "error", message = ex is PawnControlException ? ex.Message : "Invalid control frame." }, stop.Token).ConfigureAwait(false); }
            catch (Exception e) when (e is WebSocketException or OperationCanceledException or ObjectDisposedException) { }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException) { }
        finally
        {
            if (attachment != null) sessions.Detach(attachment);
            bound?.Cancel();
            stop.Cancel();
            foreach (var task in new[] { receive, publish })
                if (task != null) try { await task.ConfigureAwait(false); } catch { }
            bound?.Dispose();
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                try
                {
                    using var closeTimeout = new CancellationTokenSource(250);
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Control ended", closeTimeout.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException) { }
        }
    }

    private async Task ReceiveInputs(WebSocket socket, SemaphoreSlim send, PawnControlSessions.SocketAttachment attachment, CancellationToken ct)
    {
        var pending = new List<Task>();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                using var frame = await ReadSocketJson(socket, ct).ConfigureAwait(false);
                if (frame == null) return;
                var json = frame.RootElement;
                RequireSequence(json);
                switch (Text(json, "type"))
                {
                    case "input":
                        var input = json.Deserialize<PawnControlInput>(Json) ?? throw new PawnControlException(400, "Invalid input.");
                        ValidateInput(input);
                        sessions.SocketInput(attachment, input);
                        break;
                    case "action":
                        var action = json.Deserialize<PawnControlAction>(Json) ?? throw new PawnControlException(400, "Invalid action.");
                        ValidateAction(action);
                        var requestId = Text(json, "request_id");
                        pending.RemoveAll(t => t.IsCompleted);
                        if (pending.Count >= 16) throw new PawnControlException(429, "Too many pending control actions.");
                        // Register synchronously in received order; only sending its result is asynchronous.
                        var result = sessions.SocketActionAsync(attachment, action, ct);
                        pending.Add(SendAction(socket, send, requestId, result, ct));
                        break;
                    default: throw new PawnControlException(400, "Unknown control frame.");
                }
            }
        }
        finally
        {
            // These sends observe ct; the outer lifetime cancels before awaiting this reader on close.
            foreach (var done in pending.Where(t => t.IsCompleted)) try { await done.ConfigureAwait(false); } catch { }
        }
    }

    private static async Task SendAction(WebSocket socket, SemaphoreSlim send, string requestId, Task<PawnControlActionReply> pending, CancellationToken ct)
    {
        try
        {
            var data = await pending.ConfigureAwait(false);
            await SendSocketJson(socket, send, new { type = "action", request_id = requestId, data }, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException) { }
        catch (PawnControlException ex)
        {
            try { await SendSocketJson(socket, send, new { type = "error", request_id = requestId, message = ex.Message }, ct).ConfigureAwait(false); }
            catch (Exception e) when (e is WebSocketException or OperationCanceledException or ObjectDisposedException) { }
        }
    }

    private async Task PublishState(WebSocket socket, SemaphoreSlim send, PawnControlSessions.SocketAttachment attachment, CancellationToken ct,
        SupervisorPerf.PublicationMonitor? monitor)
    {
        using var tick = new PeriodicTimer(TimeSpan.FromMilliseconds(50));
        do
        {
            var data = sessions.SocketState(attachment);
            await SendSocketJson(socket, send, new { type = "state", sampled_at_ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), data }, ct).ConfigureAwait(false);
            monitor?.Sent();
        }
        while (await tick.WaitForNextTickAsync(ct).ConfigureAwait(false));
    }

    private static async Task SendSocketJson(WebSocket socket, SemaphoreSlim send, object value, CancellationToken ct)
    {
        var bytes = SupervisorJson.Bytes(value);
        await send.WaitAsync(ct).ConfigureAwait(false);
        try { await socket.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, true, ct).ConfigureAwait(false); }
        finally { send.Release(); }
    }

    private static async Task<JsonDocument?> ReadSocketJson(WebSocket socket, CancellationToken ct)
    {
        var bytes = new byte[4096];
        int count = 0;
        while (true)
        {
            var got = await socket.ReceiveAsync(bytes.AsMemory(count), ct).ConfigureAwait(false);
            if (got.MessageType == WebSocketMessageType.Close) return null;
            if (got.MessageType != WebSocketMessageType.Text) throw new PawnControlException(400, "Control frames must be JSON text.");
            count += got.Count;
            if (got.EndOfMessage) return JsonDocument.Parse(bytes.AsMemory(0, count));
            if (count >= bytes.Length) throw new PawnControlException(413, "Control frame is too large.");
        }
    }
}
