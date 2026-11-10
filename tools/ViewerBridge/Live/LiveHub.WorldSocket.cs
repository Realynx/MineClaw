using System.Diagnostics;
using System.Globalization;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MineClaw.ViewerBridge.Live;

public sealed partial class LiveHub
{
    /// <summary>How often the world stream's counters are logged while a client is connected.</summary>
    internal static readonly TimeSpan WorldStreamReportEvery = TimeSpan.FromMinutes(1);

    /// <summary>The upstream world stream's cadence as this bridge receives it (the supervisor's perf line only sees its own side).</summary>
    internal WorldStreamStats WorldStream { get; } = new();

    /// <summary>One upstream world subscription shared by every viewer. No HTTP round trip or connection pool wait per motion frame.</summary>
    private async Task StreamWorld(CancellationToken ct)
    {
        var entities = new EntityCursor();
        var blocks = new BlocksCursor();
        var delay = TimeSpan.Zero;
        WorldStream.Start(GC.GetTotalPauseDuration().TotalMilliseconds);
        _ = ReportWorldStream(ct);
        while (!ct.IsCancellationRequested)
        {
            bool connected = false;
            try
            {
                var path = FormattableString.Invariant($"api/world/socket?entities_since={entities.Rev}&blocks_since={blocks.Rev ?? 0}");
                using var socket = await _sup.ConnectSocketAsync(path, new Dictionary<string, string>(), ct);
                connected = true;
                var bytes = new byte[32 * 1024];
                using var message = new MemoryStream();
                while (!ct.IsCancellationRequested)
                {
                    var frame = await socket.ReceiveAsync(bytes.AsMemory(), ct);
                    if (frame.MessageType == WebSocketMessageType.Close)
                    {
                        _log.LogInformation("World socket closed by the supervisor: {Status} {Reason}", socket.CloseStatus, socket.CloseStatusDescription);
                        break;
                    }
                    if (frame.MessageType != WebSocketMessageType.Text || message.Length + frame.Count > 8 * 1024 * 1024)
                        throw new InvalidDataException("Invalid world socket frame");
                    message.Write(bytes, 0, frame.Count);
                    if (!frame.EndOfMessage) continue;
                    var node = JsonNode.Parse(message.GetBuffer().AsSpan(0, (int)message.Length));
                    message.SetLength(0);
                    if (node is not JsonObject envelope || envelope["data"] is not JsonObject data) continue;
                    bool motion = (string?)envelope["event"] == "entities";
                    if (motion) WorldStream.Frame(Stopwatch.GetTimestamp());
                    lock (_lock)
                    {
                        // A last viewer can leave and a new one subscribe while this receive completes.
                        // The old connection must never publish behind the new subscription's full snapshot.
                        if (ct.IsCancellationRequested) return;
                        if (motion) EntitiesPage(data, entities);
                        else if ((string?)envelope["event"] == "blocks") BlocksPage(data, blocks);
                    }
                    delay = TimeSpan.Zero;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) when (ex is WebSocketException or HttpRequestException or OperationCanceledException or IOException or JsonException)
            {
                // A connection that was up and dropped is worth a line; a supervisor that is down fails every connect, so those stay Debug.
                if (connected) _log.LogInformation("World socket disconnected: {Kind}: {Reason}", ex.GetType().Name, ex.Message);
                else _log.LogDebug("World socket disconnected: {Kind}", ex.GetType().Name);
            }
            WorldStream.Reconnect();
            delay = FastBackoff.Next(delay);
            try { await Task.Delay(delay, ct); } catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>One <c>world stream:</c> line a minute until the last client leaves.</summary>
    private async Task ReportWorldStream(CancellationToken ct)
    {
        using var every = new PeriodicTimer(WorldStreamReportEvery);
        try
        {
            while (await every.WaitForNextTickAsync(ct))
                _log.LogInformation("world stream: {Stats}", WorldStream.Take(GC.GetTotalPauseDuration().TotalMilliseconds));
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>
    /// Counters of the upstream world stream: entity (motion) frames received, the longest interval between two of them
    /// (across a reconnect too: that is what viewers see), reconnect attempts, and the process's GC pause time, per report.
    /// Scalars only. Thread-safe.
    /// </summary>
    internal sealed class WorldStreamStats
    {
        private readonly object _lock = new();
        private long _frames, _reconnects, _lastAt;
        private double _longestMs, _gcMs;

        public long Frames { get { lock (_lock) return _frames; } }
        public long Reconnects { get { lock (_lock) return _reconnects; } }
        public double LongestGapMs { get { lock (_lock) return _longestMs; } }

        /// <summary>A new subscription (a client connected after none were): the idle time before it is no gap.</summary>
        public void Start(double totalGcMs)
        {
            lock (_lock)
            {
                _frames = _reconnects = _lastAt = 0;
                _longestMs = 0;
                _gcMs = totalGcMs;
            }
        }

        /// <summary>An entity frame arrived at <paramref name="at"/> (<see cref="Stopwatch"/> ticks).</summary>
        public void Frame(long at)
        {
            lock (_lock)
            {
                if (_lastAt != 0) _longestMs = Math.Max(_longestMs, Stopwatch.GetElapsedTime(_lastAt, at).TotalMilliseconds);
                _lastAt = at;
                _frames++;
            }
        }

        public void Reconnect()
        {
            lock (_lock) _reconnects++;
        }

        /// <summary>The report since the last one, and starts over (the interval in progress carries on).</summary>
        public string Take(double totalGcMs)
        {
            lock (_lock)
            {
                var line = string.Create(CultureInfo.InvariantCulture,
                    $"{_frames} frames, longest gap {_longestMs:0} ms, reconnects {_reconnects}, gc pause {Math.Max(0, totalGcMs - _gcMs):0} ms");
                _frames = _reconnects = 0;
                _longestMs = 0;
                _gcMs = totalGcMs;
                return line;
            }
        }
    }
}
