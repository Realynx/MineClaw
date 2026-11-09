using System.Globalization;
using System.Net.WebSockets;

namespace MineClaw.App.Supervisor;

public sealed partial class SupervisorWorldRoutes
{
    /// <summary>One subscribed bridge connection: merged entities at the delta cache's 10 Hz and block batches at 5 Hz.
    /// Resume cursors retain missed events; the existing full flag covers log gaps/restarts.</summary>
    internal async Task SocketAsync(WebSocket socket, SupervisorRequest request, CancellationToken shutdown)
    {
        long Cursor(string name) => long.TryParse(Q(request, name), NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : 0;
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
        var send = SendWorld(socket, Cursor("entities_since"), Cursor("blocks_since"), lifetime.Token);
        var read = ReadUntilClose(socket, lifetime.Token);
        try { await await Task.WhenAny(send, read).ConfigureAwait(false); }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException) { }
        finally
        {
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
    }

    private async Task SendWorld(WebSocket socket, long entities, long blocks, CancellationToken ct)
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
            if (frame.Status != 200) throw new IOException("World snapshot unavailable");
            await socket.SendAsync(frame.Body.AsMemory(), WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
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
