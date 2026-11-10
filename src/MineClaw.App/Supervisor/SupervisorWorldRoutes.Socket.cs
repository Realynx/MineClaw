using System.Globalization;
using System.Net.WebSockets;

namespace MineClaw.App.Supervisor;

/// <summary>How a world socket ended. The server logs every end but the client's close and shutdown; a snapshot error and a busy
/// lane count as failed on the perf line.</summary>
internal enum WorldSocketEnd { ClientClosed, Shutdown, Dropped, SnapshotFailed, Busy }

/// <summary>A world socket's end and, unless the client closed it, why (the snapshot's error text or the exception's kind).</summary>
internal readonly record struct WorldSocketEnded(WorldSocketEnd End, string? Detail = null)
{
    public bool Failed => End is WorldSocketEnd.SnapshotFailed or WorldSocketEnd.Busy;
}

public sealed partial class SupervisorWorldRoutes
{
    /// <summary>A world frame's snapshot answered with an error; the message carries its status and text.</summary>
    private sealed class WorldSnapshotException(string message) : IOException(message);

    /// <summary>One subscribed bridge connection: merged entities on every 100 ms tick (the delta log rebuilds at most every
    /// 50 ms, so each tick carries fresh movement) and block batches at 5 Hz. Resume cursors retain missed events; the existing
    /// full flag covers log gaps/restarts. Returns how the socket ended.</summary>
    internal async Task<WorldSocketEnded> SocketAsync(WebSocket socket, SupervisorRequest request, CancellationToken shutdown, SupervisorPerf? perf = null)
    {
        long Cursor(string name) => long.TryParse(Q(request, name), NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : 0;
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
        var monitor = perf?.MonitorPublication("world");
        var send = SendWorld(socket, Cursor("entities_since"), Cursor("blocks_since"), lifetime.Token, monitor);
        var read = ReadUntilClose(socket, lifetime.Token);
        var ended = new WorldSocketEnded(WorldSocketEnd.ClientClosed);
        try
        {
            var first = await Task.WhenAny(send, read).ConfigureAwait(false);
            await first.ConfigureAwait(false);
            if (first == send) ended = new(WorldSocketEnd.Dropped, "publisher stopped");
        }
        catch (WorldSnapshotException ex) { ended = new(WorldSocketEnd.SnapshotFailed, ex.Message); }
        catch (RenderBusyException) { ended = new(WorldSocketEnd.Busy, "live lane busy"); }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { ended = new(WorldSocketEnd.Shutdown); }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException) { ended = new(WorldSocketEnd.Dropped, ex.GetType().Name); }
        finally
        {
            // Counted before the socket is torn down, so a client that sees the end can read the perf line at once.
            if (ended.Failed) monitor?.Failed();
            lifetime.Cancel();
            foreach (var task in new[] { send, read }) try { await task.ConfigureAwait(false); } catch { }
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                try
                {
                    using var close = new CancellationTokenSource(250);
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "World subscription ended", close.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is WebSocketException or OperationCanceledException) { }
        }
        return ended;
    }

    private async Task SendWorld(WebSocket socket, long entities, long blocks, CancellationToken ct, SupervisorPerf.PublicationMonitor? monitor)
    {
        using var tick = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
        long lastBlocks = long.MinValue / 2;
        do
        {
            var frame = await Work(_live, () =>
            {
                var delta = EntityDelta(entities);
                entities = delta.Rev;
                return Json(200, new { @event = "entities", id = delta.Rev, data = delta.Data });
            }, ct).ConfigureAwait(false);
            if (frame.Status != 200) throw new WorldSnapshotException(SnapshotError(frame));
            await socket.SendAsync(frame.Body.AsMemory(), WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
            monitor?.Sent();
            long now = Environment.TickCount64;
            if (now - lastBlocks < 200 || (Blocks ?? BlockDeltaLog.Process) is not { } log) continue;
            lastBlocks = now;
            for (int batch = 0; batch < 8; batch++)
            {
                var page = log.Since(blocks, null, MaxBlockEntries);
                blocks = page.Rev;
                var bytes = SupervisorJson.Bytes(new { @event = "blocks", id = page.Rev, data = new { at = _time.GetUtcNow().ToUnixTimeMilliseconds(),
                    rev = page.Rev, full = page.Full, more = page.More, blocks = page.Blocks, columns = page.Columns } });
                await socket.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
                if (!page.More) break;
            }
        }
        while (await tick.WaitForNextTickAsync(ct).ConfigureAwait(false));
    }

    /// <summary>"World snapshot unavailable" with the reply's status and error text (at most 200 characters).</summary>
    private static string SnapshotError(SupervisorReply reply)
    {
        var text = reply.Text;
        return $"World snapshot unavailable ({reply.Status.ToString(CultureInfo.InvariantCulture)}): {(text.Length <= 200 ? text : text[..200])}";
    }

    private static async Task ReadUntilClose(WebSocket socket, CancellationToken ct)
    {
        var bytes = new byte[1024];
        while (!ct.IsCancellationRequested)
        {
            var frame = await socket.ReceiveAsync(bytes.AsMemory(), ct).ConfigureAwait(false);
            if (frame.MessageType == WebSocketMessageType.Close || !frame.EndOfMessage) return;
        }
    }
}
