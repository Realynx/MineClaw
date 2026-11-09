using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MineClaw.ViewerBridge.Live;

public sealed partial class LiveHub
{
    /// <summary>One upstream world subscription shared by every viewer. No HTTP round trip or connection pool wait per motion frame.</summary>
    private async Task StreamWorld(CancellationToken ct)
    {
        var entities = new EntityCursor();
        var blocks = new BlocksCursor();
        var delay = TimeSpan.Zero;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var path = FormattableString.Invariant($"api/world/socket?entities_since={entities.Rev}&blocks_since={blocks.Rev ?? 0}");
                using var socket = await _sup.ConnectSocketAsync(path, new Dictionary<string, string>(), ct);
                var bytes = new byte[32 * 1024];
                using var message = new MemoryStream();
                while (!ct.IsCancellationRequested)
                {
                    var frame = await socket.ReceiveAsync(bytes.AsMemory(), ct);
                    if (frame.MessageType == WebSocketMessageType.Close) break;
                    if (frame.MessageType != WebSocketMessageType.Text || message.Length + frame.Count > 8 * 1024 * 1024)
                        throw new InvalidDataException("Invalid world socket frame");
                    message.Write(bytes, 0, frame.Count);
                    if (!frame.EndOfMessage) continue;
                    var node = JsonNode.Parse(message.GetBuffer().AsSpan(0, (int)message.Length));
                    message.SetLength(0);
                    if (node is not JsonObject envelope || envelope["data"] is not JsonObject data) continue;
                    lock (_lock)
                    {
                        // A last viewer can leave and a new one subscribe while this receive completes.
                        // The old connection must never publish behind the new subscription's full snapshot.
                        if (ct.IsCancellationRequested) return;
                        if ((string?)envelope["event"] == "entities") EntitiesPage(data, entities);
                        else if ((string?)envelope["event"] == "blocks") BlocksPage(data, blocks);
                    }
                    delay = TimeSpan.Zero;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) when (ex is WebSocketException or HttpRequestException or OperationCanceledException or IOException or JsonException)
            { _log.LogDebug("World socket disconnected: {Kind}", ex.GetType().Name); }
            delay = FastBackoff.Next(delay);
            try { await Task.Delay(delay, ct); } catch (OperationCanceledException) { return; }
        }
    }
}
