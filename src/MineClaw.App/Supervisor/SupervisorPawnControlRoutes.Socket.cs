using System.Diagnostics;
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
        bool told = false;
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
            // The reader does not end with the lease: cancelling a pending receive aborts the socket, and the closing frame that
            // names the reason must still go out.
            receive = ReceiveInputs(socket, send, attachment, stop.Token);
            publish = PublishState(socket, send, attachment, bound.Token, perf?.MonitorPublication("control"));
            await await Task.WhenAny(receive, publish).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is PawnControlException or JsonException or ArgumentException)
        {
            told = true;
            try
            {
                await SendSocketJson(socket, send, attachment?.EndReason is { } reason ? EndFrame(reason)
                    : new { type = "error", message = ex is PawnControlException ? ex.Message : "Invalid control frame." }, stop.Token).ConfigureAwait(false);
            }
            catch (Exception e) when (e is WebSocketException or OperationCanceledException or ObjectDisposedException) { }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException) { }
        finally
        {
            if (attachment != null) sessions.Detach(attachment);
            bound?.Cancel();
            if (publish != null) try { await publish.ConfigureAwait(false); } catch { }
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                try
                {
                    using var closeTimeout = new CancellationTokenSource(250);
                    // The close itself says nothing, so the browser learns from this frame why its takeover ended.
                    if (!told && attachment?.EndReason is { } reason)
                        await SendSocketJson(socket, send, EndFrame(reason), closeTimeout.Token).ConfigureAwait(false);
                    await send.WaitAsync(closeTimeout.Token).ConfigureAwait(false);
                    try { await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Control ended", closeTimeout.Token).ConfigureAwait(false); }
                    finally { send.Release(); }
                }
                catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException) { }
            // The browser answers the close at once, which ends the reader. Cancelling a pending read instead resets the connection,
            // and a reset can discard the closing frame before the browser has read it.
            if (receive != null && socket.State == WebSocketState.CloseSent)
                try { await receive.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); } catch { }
            stop.Cancel();
            if (receive != null) try { await receive.ConfigureAwait(false); } catch { }
            bound?.Dispose();
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

    /// <summary>How long a state frame waits for the pawn's next tick before it goes out anyway (dead, riding, frozen), so the
    /// browser's watchdog never starves. Settable for tests.</summary>
    internal TimeSpan StateFallback { get; set; } = TimeSpan.FromMilliseconds(100);
    /// <summary>The least time between two state frames: a burst of catch-up ticks becomes a few frames, not one per tick.</summary>
    internal static readonly TimeSpan StateSpacing = TimeSpan.FromMilliseconds(20);

    /// <summary>One state frame per game tick, sent as soon as the tick has its sample: no free-running timer whose phase against
    /// the tick differs per session.</summary>
    private async Task PublishState(WebSocket socket, SemaphoreSlim send, PawnControlSessions.SocketAttachment attachment, CancellationToken ct,
        SupervisorPerf.PublicationMonitor? monitor)
    {
        long lastTick = -1;
        while (true)
        {
            var data = sessions.SocketState(attachment);
            await SendSocketJson(socket, send, new { type = "state", sampled_at_ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), data }, ct).ConfigureAwait(false);
            var sent = Stopwatch.GetTimestamp();
            monitor?.Sent();
            if (data.State.Tick is { } tick) lastTick = tick;
            await sessions.NextStateAsync(attachment, lastTick, StateFallback, ct).ConfigureAwait(false);
            var early = StateSpacing - Stopwatch.GetElapsedTime(sent);
            if (early > TimeSpan.Zero) await Task.Delay(early, ct).ConfigureAwait(false);
        }
    }

    private static object EndFrame(string reason) => new { type = "error", reason, message = PawnControlSessions.EndMessage(reason) };

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
