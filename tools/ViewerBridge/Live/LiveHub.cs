using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using MineClaw.ViewerBridge.Supervisor;

namespace MineClaw.ViewerBridge.Live;

/// <summary>
/// <c>GET /v1/live/stream</c>: the bridge polls the supervisor on loopback only while at least one client is connected
/// (status 1 Hz, events 2 Hz, viewer/feed 2 Hz, viewer/now 2 Hz per online bot, entity deltas 5 Hz, hive/tree every 3 s,
/// viewer/memory-activity 1 Hz, world/blocks 5 Hz) and emits the typed events of api.md §5 (<c>bots</c>, <c>event</c>, <c>feed</c>, <c>now</c>,
/// <c>entities</c>, <c>hive</c>, <c>memoryActivity</c>, <c>blocks</c>) as <c>event/id/data</c> frames with increasing ids. The last 256 frames
/// are kept for <c>Last-Event-ID</c> resume; a fresh client first gets the latest state frames (bots, hive, one now per bot).
/// Delta topics start from the supervisor's current cursor (clients fetch the REST route once, then apply newer frames).
/// </summary>
public sealed class LiveHub : IDisposable
{
    public const int RingSize = 256;
    public static readonly TimeSpan Heartbeat = TimeSpan.FromSeconds(15);

    private readonly SupervisorClient _sup;
    private readonly MemoryActivityStore _memory;
    private readonly ILogger<LiveHub> _log;
    private readonly object _lock = new();
    private readonly List<Client> _clients = [];
    private readonly LinkedList<Frame> _ring = new();
    private readonly Dictionary<string, Frame> _state = new(StringComparer.Ordinal);
    // Seeded from the clock so ids keep increasing across bridge restarts (a stale Last-Event-ID then simply gets fresh state).
    private long _nextId = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private CancellationTokenSource? _polling;
    private IReadOnlyList<string> _onlineBots = [];

    public LiveHub(SupervisorClient sup, MemoryActivityStore memory, ILogger<LiveHub> log)
    {
        _sup = sup;
        _memory = memory;
        _log = log;
    }

    public sealed record Frame(long Id, string Event, string Text);

    public sealed class Client
    {
        internal Channel<Frame> Channel { get; } = System.Threading.Channels.Channel.CreateBounded<Frame>(new BoundedChannelOptions(1024)
        { SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite });
        public ChannelReader<Frame> Reader => Channel.Reader;
        public bool Lagging { get; internal set; }
    }

    /// <summary>Clients connected right now (the pollers run only while this is above zero).</summary>
    public int ClientCount { get { lock (_lock) return _clients.Count; } }
    public bool Polling { get { lock (_lock) return _polling != null; } }

    /// <summary>Replay frames (after <paramref name="lastEventId"/>, or the current state for a fresh client) and the live subscription.</summary>
    public (Client Client, IReadOnlyList<Frame> Replay, long Sent) Subscribe(long? lastEventId)
    {
        var c = new Client();
        List<Frame> replay;
        long sent = 0;
        lock (_lock)
        {
            _clients.Add(c);
            if (lastEventId is { } last && last <= _nextId && (_ring.First is { } first ? first.Value.Id <= last + 1 : last == _nextId))
            {
                replay = _ring.Where(f => f.Id > last).ToList();
                sent = last;
            }
            else replay = _state.Values.OrderBy(f => f.Id).ToList();
            if (_polling == null)
            {
                _polling = new CancellationTokenSource();
                StartPollers(_polling.Token);
            }
        }
        return (c, replay, sent);
    }

    public void Unsubscribe(Client c)
    {
        lock (_lock)
        {
            _clients.Remove(c);
            if (_clients.Count == 0 && _polling != null)
            {
                _polling.Cancel();
                _polling.Dispose();
                _polling = null;
            }
        }
    }

    /// <summary>Appends a frame and fans it out. <paramref name="stateKey"/>: kept as the latest state for fresh clients.</summary>
    public Frame Publish(string evt, JsonNode payload, string? stateKey = null)
    {
        lock (_lock)
        {
            var f = new Frame(++_nextId, evt, Format(evt, _nextId, payload.ToJsonString()));
            _ring.AddLast(f);
            while (_ring.Count > RingSize) _ring.RemoveFirst();
            if (stateKey != null) _state[stateKey] = f;
            foreach (var c in _clients)
                if (!c.Channel.Writer.TryWrite(f) && !c.Lagging)
                {
                    c.Lagging = true;
                    c.Channel.Writer.TryComplete();
                }
            return f;
        }
    }

    public static string Format(string evt, long id, string json) =>
        $"event: {evt}\nid: {id.ToString(CultureInfo.InvariantCulture)}\ndata: {json}\n\n";

    // ------------------------------------------------------------------ the SSE response

    public async Task StreamAsync(HttpContext ctx)
    {
        long? last = long.TryParse(ctx.Request.Headers["Last-Event-ID"].ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var l) ? l : null;
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "text/event-stream; charset=utf-8";
        ctx.Response.Headers.CacheControl = "no-store";
        ctx.Response.Headers["X-Accel-Buffering"] = "no";
        var (client, replay, sent) = Subscribe(last);
        var ct = ctx.RequestAborted;
        try
        {
            await WriteAsync(ctx, "retry: 3000\n\n", ct);
            foreach (var f in replay) { await WriteAsync(ctx, f.Text, ct); sent = Math.Max(sent, f.Id); }
            await ctx.Response.Body.FlushAsync(ct);
            while (!ct.IsCancellationRequested)
            {
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
                wait.CancelAfter(Heartbeat);
                bool any;
                try { any = await client.Reader.WaitToReadAsync(wait.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    await WriteAsync(ctx, ": ping\n\n", ct);
                    await ctx.Response.Body.FlushAsync(ct);
                    continue;
                }
                if (!any) break; // lagging: Nest reconnects with Last-Event-ID
                while (client.Reader.TryRead(out var f))
                    if (f.Id > sent) { await WriteAsync(ctx, f.Text, ct); sent = f.Id; }
                await ctx.Response.Body.FlushAsync(ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        finally { Unsubscribe(client); }
    }

    private static Task WriteAsync(HttpContext ctx, string text, CancellationToken ct) => ctx.Response.Body.WriteAsync(Encoding.UTF8.GetBytes(text), ct).AsTask();

    // ------------------------------------------------------------------ pollers

    private void StartPollers(CancellationToken ct)
    {
        _log.LogInformation("live stream: a client connected, polling the supervisor");
        _ = Loop("status", TimeSpan.FromSeconds(1), PollStatus(), ct);
        _ = Loop("events", TimeSpan.FromMilliseconds(500), PollCursor("api/events", "event", "events"), ct);
        _ = Loop("feed", TimeSpan.FromMilliseconds(500), PollCursor("api/viewer/feed", "feed", "lines"), ct);
        _ = Loop("now", TimeSpan.FromMilliseconds(500), PollNow(), ct);
        _ = Loop("entities", TimeSpan.FromMilliseconds(200), PollEntities(), ct);
        _ = Loop("hive", TimeSpan.FromSeconds(3), PollChanged("api/hive/tree", "hive", "hive", TimeSpan.FromSeconds(3)), ct);
        _ = Loop("memoryActivity", TimeSpan.FromSeconds(1), PollMemory(), ct);
        _ = Loop("blocks", TimeSpan.FromMilliseconds(200), PollBlocks(), ct);
    }

    private async Task Loop(string name, TimeSpan every, Func<CancellationToken, Task> poll, CancellationToken ct)
    {
        var backoff = TimeSpan.Zero;
        while (!ct.IsCancellationRequested)
        {
            var started = DateTimeOffset.UtcNow;
            try
            {
                await poll(ct);
                backoff = TimeSpan.Zero;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                backoff = backoff == TimeSpan.Zero ? TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(Math.Min(15, backoff.TotalSeconds * 2));
                _log.LogDebug("live poll {Name} failed: {Message}", name, ex.Message);
            }
            var wait = every + backoff - (DateTimeOffset.UtcNow - started);
            try { if (wait > TimeSpan.Zero) await Task.Delay(wait, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task<JsonNode?> GetJson(string relative, TimeSpan timeout, CancellationToken ct)
    {
        var (status, body, _, _) = await _sup.GetAsync(relative, timeout, ct);
        if (status != 200) throw new SupervisorUnavailableException($"{relative.Split('?')[0]} answered {status}");
        return JsonNode.Parse(body);
    }

    /// <summary>A comparison key without the fields that change on every read (timestamps, ages).</summary>
    public static string Normalized(JsonNode? node, params string[] volatileKeys)
    {
        var copy = node?.DeepClone();
        var keys = new HashSet<string>(volatileKeys.Length > 0 ? volatileKeys : ["at"], StringComparer.Ordinal);
        Strip(copy, keys);
        return copy?.ToJsonString() ?? "null";
    }

    private static void Strip(JsonNode? n, HashSet<string> keys)
    {
        switch (n)
        {
            case JsonObject o:
                foreach (var k in o.Select(p => p.Key).Where(keys.Contains).ToList()) o.Remove(k);
                foreach (var (_, v) in o) Strip(v, keys);
                break;
            case JsonArray a:
                foreach (var v in a) Strip(v, keys);
                break;
        }
    }

    private Func<CancellationToken, Task> PollStatus()
    {
        string? prev = null;
        return async ct =>
        {
            var node = await GetJson("api/status", TimeSpan.FromSeconds(4), ct);
            if (node is not JsonObject o) return;
            _onlineBots = (o["bots"] as JsonArray ?? []).OfType<JsonObject>()
                .Where(b => (string?)b["status"] == "online").Select(b => (string?)b["name"]).OfType<string>().ToList();
            var key = Normalized(o, "at", "age_seconds", "updated_at");
            if (key == prev) return;
            prev = key;
            Publish("bots", o, "bots");
        };
    }

    private Func<CancellationToken, Task> PollChanged(string relative, string evt, string stateKey, TimeSpan timeout)
    {
        string? prev = null;
        return async ct =>
        {
            var node = await GetJson(relative, timeout, ct);
            if (node == null) return;
            var key = Normalized(node);
            if (key == prev) return;
            prev = key;
            Publish(evt, node, stateKey);
        };
    }

    /// <summary>`?since=` delta routes (events, feed): the first call only takes the cursor; then non-empty pages are emitted.</summary>
    private Func<CancellationToken, Task> PollCursor(string relative, string evt, string listKey)
    {
        long? cursor = null;
        return async ct =>
        {
            var node = await GetJson($"{relative}?since={(cursor ?? 0).ToString(CultureInfo.InvariantCulture)}", TimeSpan.FromSeconds(4), ct);
            if (node is not JsonObject o) return;
            long last = o["last"]?.GetValue<long>() ?? cursor ?? 0;
            bool first = cursor == null;
            if (cursor is { } c && last < c) { cursor = last; return; } // the bot restarted
            if (first)
            {
                // Page forward to the current end without emitting history.
                cursor = last;
                return;
            }
            cursor = last;
            if (o[listKey] is JsonArray { Count: > 0 }) Publish(evt, o);
        };
    }

    private Func<CancellationToken, Task> PollNow()
    {
        var prev = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        return async ct =>
        {
            foreach (var bot in _onlineBots.ToList())
            {
                JsonNode? node;
                try { node = await GetJson($"api/viewer/now?bot={Uri.EscapeDataString(bot)}", TimeSpan.FromSeconds(3), ct); }
                catch (SupervisorUnavailableException) { continue; }
                if (node is not JsonObject o) continue;
                SupervisorProxy.FixNow(o);
                var key = Normalized(o);
                if (prev.TryGetValue(bot, out var p) && p == key) continue;
                prev[bot] = key;
                Publish("now", o, "now:" + bot.ToLowerInvariant());
            }
        };
    }

    private Func<CancellationToken, Task> PollEntities()
    {
        long rev = 0;
        string? prevBots = null;
        return async ct =>
        {
            var node = await GetJson($"api/world/entities?since={rev.ToString(CultureInfo.InvariantCulture)}", TimeSpan.FromSeconds(3), ct);
            if (node is not JsonObject o) return;
            long newRev = o["rev"]?.GetValue<long>() ?? rev;
            bool full = o["full"]?.GetValue<bool>() == true;
            bool changed = full || o["entities"] is JsonArray { Count: > 0 } || o["removed"] is JsonArray { Count: > 0 };
            var bots = Normalized(o["bots"], "age_seconds", "pose_age", "age");
            changed |= bots != prevBots;
            prevBots = bots;
            rev = newRev;
            if (changed) Publish("entities", o);
        };
    }

    /// <summary>
    /// Per-block world deltas (<c>/api/world/blocks?since=rev</c>): the first call only takes the cursor; then every non-empty page is
    /// emitted in rev order (<c>more</c> = fetch the next page right away, ≤ 8 per tick). A <c>full: true</c> page is emitted too:
    /// the client was too far behind and falls back to the hash-diff refetch of /world/index + /world/chunks.
    /// A bot without the route (404) is skipped quietly (the loop backs off).
    /// </summary>
    private Func<CancellationToken, Task> PollBlocks()
    {
        long? rev = null;
        return async ct =>
        {
            for (int page = 0; page < 8; page++)
            {
                var node = await GetJson($"api/world/blocks?since={(rev ?? 0).ToString(CultureInfo.InvariantCulture)}", TimeSpan.FromSeconds(3), ct);
                if (node is not JsonObject o) return;
                long newRev = o["rev"]?.GetValue<long>() ?? rev ?? 0;
                bool first = rev == null;
                bool full = o["full"]?.GetValue<bool>() == true, more = o["more"]?.GetValue<bool>() == true;
                if (rev is { } r && newRev < r) { rev = newRev; return; } // the bot restarted: start over from its numbering
                rev = newRev;
                if (first) { if (more) continue; return; }
                bool any = o["blocks"] is JsonObject per && per.Any(kv => kv.Value is JsonArray { Count: > 0 });
                if (full || any || o["columns"] is JsonArray { Count: > 0 }) Publish("blocks", o);
                if (!more) return;
            }
        };
    }

    private Func<CancellationToken, Task> PollMemory()
    {
        string? prevCtx = null;
        return async ct =>
        {
            var r = await _memory.RefreshAsync(ct);
            var ctxKey = r.InContextJson.ToJsonString();
            bool ctxChanged = ctxKey != prevCtx;
            prevCtx = ctxKey;
            if (r.Initial || (r.New.Count == 0 && !ctxChanged)) return;
            Publish("memoryActivity", new JsonObject { ["events"] = r.NewJson, ["in_context"] = r.InContextJson, ["last"] = r.Last });
        };
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _polling?.Cancel();
            _polling?.Dispose();
            _polling = null;
        }
    }
}
