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

    /// <summary>
    /// No world message for this long on an open connection while a client is connected: the connection is dropped and replaced.
    /// The supervisor sends an entities frame every 100 ms whether or not anything moved, so silence means a stalled or half-open
    /// socket (live 11-10: one stayed silent for two hours and the viewers fell back to REST polls).
    /// </summary>
    internal TimeSpan WorldSilenceTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>How often the world watchdog checks for silence and for a pump that stopped.</summary>
    internal TimeSpan WorldWatchEvery { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Tests: runs on every world message (its event name) before it is applied; may throw.</summary>
    internal Action<string?>? WorldMessageHook { get; set; }

    /// <summary>The upstream world stream's cadence as this bridge receives it (the supervisor's perf line only sees its own side).</summary>
    internal WorldStreamStats WorldStream { get; } = new();

    /// <summary>
    /// One subscription's world connection as the watchdog sees it: when the last message arrived and the token source of the
    /// connection in progress (the watchdog cancels it to drop a silent socket). One per subscription, so a pump that is still
    /// winding down after the last client left never shares state with the next subscription's.
    /// </summary>
    internal sealed class WorldLink
    {
        private readonly object _lock = new();
        private CancellationTokenSource? _connection;
        private long _lastAt;
        private bool _silent;

        /// <summary>A connection attempt begins: silence is measured from now.</summary>
        public void Arm(CancellationTokenSource connection, long now)
        {
            lock (_lock)
            {
                _connection = connection;
                _silent = false;
                Volatile.Write(ref _lastAt, now);
            }
        }

        /// <summary>A message arrived (allocation-free: one volatile write per message).</summary>
        public void Heard(long now) => Volatile.Write(ref _lastAt, now);

        /// <summary>The attempt ended: nothing for the watchdog to drop until the next one is armed.</summary>
        public void Disarm(CancellationTokenSource connection)
        {
            lock (_lock)
            {
                if (ReferenceEquals(_connection, connection)) _connection = null;
                _silent = false;
            }
        }

        /// <summary>The seconds of silence when the connection in progress has been quiet longer than <paramref name="limit"/> (and is
        /// then cancelled), else null.</summary>
        public double? DropIfSilent(long now, TimeSpan limit)
        {
            lock (_lock)
            {
                if (_connection == null || _silent) return null;
                var quiet = Stopwatch.GetElapsedTime(Volatile.Read(ref _lastAt), now);
                if (quiet < limit) return null;
                _silent = true;
                _connection.Cancel();
                return quiet.TotalSeconds;
            }
        }
    }

    /// <summary>
    /// Runs the world pump for one subscription and watches it: a connection silent for <see cref="WorldSilenceTimeout"/> is dropped
    /// (the pump reconnects from its cursors), and a pump that stopped while clients are still connected is logged and started
    /// again. Before 11-10 the pump was fire-and-forget: an exception outside its filter (an OutOfMemoryException while the bridge
    /// was at its heap limit, a malformed frame) ended it silently, and nothing restarted it until every client left.
    /// </summary>
    private async Task SuperviseWorld(CancellationToken ct)
    {
        var link = new WorldLink();
        WorldStream.Start(GC.GetTotalPauseDuration().TotalMilliseconds);
        _ = ReportWorldStream(ct);
        var pump = StreamWorld(link, ct);
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(WorldWatchEvery, ct); }
            catch (OperationCanceledException) { break; }
            try
            {
                if (pump.IsCompleted && !ct.IsCancellationRequested)
                {
                    _log.LogError(pump.Exception?.GetBaseException(), "World stream pump stopped ({Status}); restarting it", pump.Status);
                    WorldStream.PumpRestart();
                    pump = StreamWorld(link, ct);
                    continue;
                }
                if (link.DropIfSilent(Stopwatch.GetTimestamp(), WorldSilenceTimeout) is { } quiet)
                {
                    WorldStream.Stall();
                    _log.LogWarning("World socket silent for {Seconds:0.0} s while {Clients} client(s) are connected; reconnecting", quiet, ClientCount);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogWarning(ex, "World stream watchdog check failed"); }
        }
        try { await pump; } catch { } // already logged; the subscription is over
    }

    /// <summary>One upstream world subscription shared by every viewer. No HTTP round trip or connection pool wait per motion frame.</summary>
    private async Task StreamWorld(WorldLink link, CancellationToken ct)
    {
        var entities = new EntityCursor();
        var blocks = new BlocksCursor();
        var delay = TimeSpan.Zero;
        var bytes = new byte[32 * 1024];
        using var message = new MemoryStream();
        while (!ct.IsCancellationRequested)
        {
            bool connected = false, unexpected = false;
            using var connection = CancellationTokenSource.CreateLinkedTokenSource(ct);
            link.Arm(connection, Stopwatch.GetTimestamp());
            try
            {
                var path = FormattableString.Invariant($"api/world/socket?entities_since={entities.Rev}&blocks_since={blocks.Rev ?? 0}");
                using var socket = await _sup.ConnectSocketAsync(path, new Dictionary<string, string>(), connection.Token);
                connected = true;
                message.SetLength(0);
                if (!await PumpSocket(socket, link, entities, blocks, bytes, message, connection.Token, ct)) return;
                delay = TimeSpan.Zero;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception) when (connection.IsCancellationRequested) { } // the watchdog dropped a silent connection (logged there)
            catch (Exception ex) when (ex is WebSocketException or HttpRequestException or OperationCanceledException or IOException or JsonException)
            {
                // A connection that was up and dropped is worth a line; a supervisor that is down fails every connect, so those stay Debug.
                if (connected) _log.LogInformation("World socket disconnected: {Kind}: {Reason}", ex.GetType().Name, ex.Message);
                else _log.LogDebug("World socket disconnected: {Kind}", ex.GetType().Name);
            }
            catch (Exception ex)
            {
                // Anything else (OutOfMemoryException at the heap limit, a frame the cursors cannot read) used to end the pump for good.
                unexpected = true;
                _log.LogWarning(ex, "World stream failed ({Kind}); reconnecting", ex.GetType().Name);
            }
            finally { link.Disarm(connection); }
            if (ct.IsCancellationRequested) return;
            WorldStream.Reconnect();
            delay = (unexpected ? SlowBackoff : FastBackoff).Next(delay);
            try { await Task.Delay(delay, ct); } catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>Reads one connection until the supervisor closes it (true) or the subscription ends (false). Throws on a dropped or
    /// cancelled connection.</summary>
    private async Task<bool> PumpSocket(WebSocket socket, WorldLink link, EntityCursor entities, BlocksCursor blocks, byte[] bytes, MemoryStream message,
        CancellationToken connection, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var frame = await socket.ReceiveAsync(bytes.AsMemory(), connection);
            if (frame.MessageType == WebSocketMessageType.Close)
            {
                _log.LogInformation("World socket closed by the supervisor: {Status} {Reason}", socket.CloseStatus, socket.CloseStatusDescription);
                return true;
            }
            if (frame.MessageType != WebSocketMessageType.Text || message.Length + frame.Count > 8 * 1024 * 1024)
                throw new InvalidDataException("Invalid world socket frame");
            message.Write(bytes, 0, frame.Count);
            if (!frame.EndOfMessage) continue;
            long now = Stopwatch.GetTimestamp();
            link.Heard(now);
            var node = JsonNode.Parse(message.GetBuffer().AsSpan(0, (int)message.Length));
            message.SetLength(0);
            if (node is not JsonObject envelope || envelope["data"] is not JsonObject data) continue;
            var evt = (string?)envelope["event"];
            WorldMessageHook?.Invoke(evt);
            bool motion = evt == "entities";
            if (motion) WorldStream.Frame(now);
            lock (_lock)
            {
                // A last viewer can leave and a new one subscribe while this receive completes.
                // The old connection must never publish behind the new subscription's full snapshot.
                if (ct.IsCancellationRequested) return false;
                if (motion) EntitiesPage(data, entities);
                else if (evt == "blocks") BlocksPage(data, blocks);
            }
        }
        return false;
    }

    /// <summary>One <c>world stream:</c> line a minute until the last client leaves.</summary>
    private async Task ReportWorldStream(CancellationToken ct)
    {
        using var every = new PeriodicTimer(WorldStreamReportEvery);
        try
        {
            while (await every.WaitForNextTickAsync(ct))
                _log.LogInformation("world stream: {Stats}", WorldStream.Take(GC.GetTotalPauseDuration().TotalMilliseconds, Stopwatch.GetTimestamp()));
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>
    /// Counters of the upstream world stream: entity (motion) frames received, the longest interval between two of them
    /// (across a reconnect too: that is what viewers see), reconnect attempts, and the process's GC pause time, per report.
    /// Silent connections the watchdog dropped (<c>stalls</c>) and pump restarts are appended when there were any.
    /// Scalars only. Thread-safe.
    /// </summary>
    internal sealed class WorldStreamStats
    {
        private readonly object _lock = new();
        private long _frames, _reconnects, _stalls, _restarts, _lastAt, _startAt;
        private double _longestMs, _gcMs;

        public long Frames { get { lock (_lock) return _frames; } }
        public long Reconnects { get { lock (_lock) return _reconnects; } }
        public long Stalls { get { lock (_lock) return _stalls; } }
        public long PumpRestarts { get { lock (_lock) return _restarts; } }
        public double LongestGapMs { get { lock (_lock) return _longestMs; } }

        /// <summary>A new subscription (a client connected after none were): the idle time before it is no gap.</summary>
        public void Start(double totalGcMs)
        {
            lock (_lock)
            {
                _frames = _reconnects = _stalls = _restarts = _lastAt = 0;
                _startAt = Stopwatch.GetTimestamp();
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

        /// <summary>The watchdog dropped a silent connection.</summary>
        public void Stall()
        {
            lock (_lock) _stalls++;
        }

        /// <summary>The watchdog found the pump stopped and started it again.</summary>
        public void PumpRestart()
        {
            lock (_lock) _restarts++;
        }

        /// <summary>The report since the last one, and starts over (the interval in progress carries on).</summary>
        public string Take(double totalGcMs) => Take(totalGcMs, null);

        /// <summary>As <see cref="Take(double)"/>, counting the interval still open at <paramref name="now"/> into the longest gap, so a
        /// stream that went silent reports how long it has been silent (it used to say "0 frames, longest gap 0 ms").</summary>
        public string Take(double totalGcMs, long? now)
        {
            lock (_lock)
            {
                double longest = _longestMs;
                if (now is { } n && (_lastAt != 0 ? _lastAt : _startAt) is var since and not 0)
                    longest = Math.Max(longest, Stopwatch.GetElapsedTime(since, n).TotalMilliseconds);
                var line = string.Create(CultureInfo.InvariantCulture,
                    $"{_frames} frames, longest gap {longest:0} ms, reconnects {_reconnects}, gc pause {Math.Max(0, totalGcMs - _gcMs):0} ms");
                if (_stalls > 0) line += string.Create(CultureInfo.InvariantCulture, $", stalls {_stalls}");
                if (_restarts > 0) line += string.Create(CultureInfo.InvariantCulture, $", pump restarts {_restarts}");
                _frames = _reconnects = _stalls = _restarts = 0;
                _longestMs = 0;
                _gcMs = totalGcMs;
                return line;
            }
        }
    }
}
