using System.Globalization;
using MineClaw.World;

namespace MineClaw.App.Supervisor;

/// <summary>A known chunk column in the hive's merged world memory (no blocks): the viewer's delta key.</summary>
public sealed record WorldColumnInfo(int Cx, int Cz, long Hash, long Seen, string? By, bool Live);

/// <summary>A hive bot for the live viewer: pose (yaw/pitch when known), vitals, job, held item, claims.</summary>
public sealed record WorldBot(string Name, string Short, bool Main, int Dim, double X, double Y, double Z, float? Yaw, float? Pitch,
    double? PoseAge, int Hp, int Food, string? Job, string? Brain, string? Held, IReadOnlyList<string> Claims, bool Live, double AgeSeconds);

/// <summary>Anything else to draw (players, mobs, items) the hive knows of, merged across every body. Items carry the stack
/// (<paramref name="Item"/> id, <paramref name="Damage"/>, <paramref name="Count"/>); velocity is blocks/tick, <paramref name="Age"/>
/// seconds since the body first saw it, <paramref name="By"/> the body whose tracker it came from.</summary>
public sealed record WorldEntity(string Kind, string? Name, int Dim, double X, double Y, double Z, int? Id = null, int? Type = null,
    int? Item = null, int? Damage = null, int? Count = null, double? Vx = null, double? Vy = null, double? Vz = null, double? Age = null,
    float? Yaw = null, string? By = null, int? Flags = null, int? Variant = null);

/// <summary>A labelled box or point (claims, structures, waypoints, chests, gens, dangers). Min/Max are [x, y, z].</summary>
public sealed record WorldMark(string Kind, string Text, int[] Min, int[] Max, string? Owner = null);

/// <summary>A build plan's remaining placements as ghost blocks: [x, y, z, id, meta, done(0/1)] each.</summary>
public sealed record WorldPlan(long Id, string Name, string Status, int Part, IReadOnlyList<int[]> Blocks);

public sealed record WorldOverlays(DateTimeOffset At, int Dim, IReadOnlyList<WorldMark> Claims, IReadOnlyList<WorldMark> Places,
    IReadOnlyList<WorldMark> Points, IReadOnlyList<WorldPlan> Plans, IReadOnlyList<WorldMark> Dangers, IReadOnlyList<string> Notes);

/// <summary>
/// What the Live Hive viewer reads: the merged world memory as columns (main's and every in-process body's live columns, plus
/// every hive bot's remembered <c>world_chunks</c>), the bots with poses, and the overlay data. Read-only; implementations
/// snapshot briefly and never block the game tick.
/// </summary>
public interface ISupervisorWorld
{
    /// <summary>Known columns of <paramref name="dim"/> in the chunk rectangle, at most <paramref name="max"/>.</summary>
    IReadOnlyList<WorldColumnInfo> Index(int dim, int cx0, int cz0, int cx1, int cz1, int max);
    /// <summary>The columns' blocks (<see cref="ChunkSnapshot.Encode"/>); an unknown column comes back with empty data.</summary>
    IReadOnlyList<ColumnPayloadEntry> Columns(int dim, IReadOnlyList<(int Cx, int Cz)> cols);
    IReadOnlyList<WorldBot> Bots();
    IReadOnlyList<WorldEntity> Entities(int? dim);
    WorldOverlays Overlays(int dim);
}

/// <summary>Per-route caps for the world routes (requests per second, burst).</summary>
public sealed record WorldRouteLimits(double IndexPerSec = 8, double IndexBurst = 16, double ChunksPerSec = 25, double ChunksBurst = 50,
    double EntitiesPerSec = 12, double EntitiesBurst = 24, double OverlaysPerSec = 1, double OverlaysBurst = 4)
{
    public static WorldRouteLimits Default { get; } = new();
}

/// <summary>A token bucket: <see cref="TryTake"/> fails with the wait until the next token when empty.</summary>
public sealed class TokenBucket(double perSecond, double burst, TimeProvider time)
{
    private readonly object _lock = new();
    private double _tokens = Math.Max(1, burst);
    private DateTimeOffset _at = time.GetUtcNow();

    public bool TryTake(out TimeSpan retryAfter)
    {
        lock (_lock)
        {
            var now = time.GetUtcNow();
            double rate = Math.Max(0.01, perSecond);
            _tokens = Math.Min(Math.Max(1, burst), _tokens + Math.Max(0, (now - _at).TotalSeconds) * rate);
            _at = now;
            if (_tokens >= 1) { _tokens -= 1; retryAfter = TimeSpan.Zero; return true; }
            retryAfter = TimeSpan.FromSeconds((1 - _tokens) / rate);
            return false;
        }
    }
}

/// <summary>
/// The Live Hive's read-only world routes (GET, token-gated by the server like everything else, rate-capped per route with
/// 429 + Retry-After, cached briefly, at most two in flight):
/// <list type="bullet">
/// <item><c>/api/world/index?dim&amp;x&amp;z&amp;r</c> (block centre, radius 16..256) → JSON <c>{dim, at, columns:[{cx,cz,hash,seen,by,live}]}</c></item>
/// <item><c>/api/world/chunks?dim&amp;cols=cx,cz;cx,cz…</c> (≤ 64) → <see cref="ColumnPayload"/> binary</item>
/// <item><c>/api/world/entities?dim</c> → JSON <c>{at, rev, bots:[…], entities:[…]}</c> (bots of every dimension, entities of <c>dim</c> or all)</item>
/// <item><c>/api/world/entities?since=REV</c> → the delta stream: <c>{at, rev, full, bots, entities:[changed since REV], removed:[[dim,id]…]}</c>
/// (every dimension; <c>full</c> = the client was too far behind and gets everything)</item>
/// <item><c>/api/world/overlays?dim</c> → JSON claims, places, points (chests, gens, farms…), plan ghost blocks, dangers</item>
/// </list>
/// </summary>
public sealed class SupervisorWorldRoutes
{
    public const string Prefix = "/api/world/";
    public const int MaxColumnsPerRequest = 64;
    public const int MaxIndexRadius = 256;

    private readonly ISupervisorWorld _world;
    private readonly TimeProvider _time;
    private readonly Dictionary<string, TokenBucket> _buckets;
    private readonly RenderCache<SupervisorReply> _cache;
    private readonly SemaphoreSlim _work = new(2);
    private readonly EntityDeltaLog _entityLog;

    public SupervisorWorldRoutes(ISupervisorWorld world, WorldRouteLimits? limits = null, TimeProvider? time = null)
    {
        _world = world;
        _time = time ?? TimeProvider.System;
        var l = limits ?? WorldRouteLimits.Default;
        _cache = new RenderCache<SupervisorReply>(_time, 48);
        _entityLog = new EntityDeltaLog(_time) { MinInterval = TimeSpan.FromMilliseconds(100) };
        _buckets = new Dictionary<string, TokenBucket>(StringComparer.Ordinal)
        {
            ["index"] = new(l.IndexPerSec, l.IndexBurst, _time),
            ["chunks"] = new(l.ChunksPerSec, l.ChunksBurst, _time),
            ["entities"] = new(l.EntitiesPerSec, l.EntitiesBurst, _time),
            ["overlays"] = new(l.OverlaysPerSec, l.OverlaysBurst, _time),
        };
    }

    /// <summary>Index/entity/overlay replies are shared for this long (several viewers or tiles = one read).</summary>
    public TimeSpan IndexCache { get; init; } = TimeSpan.FromMilliseconds(400);
    public TimeSpan EntitiesCache { get; init; } = TimeSpan.FromMilliseconds(100);
    public TimeSpan OverlaysCache { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>Handles a <c>/api/world/*</c> path (already authorized, GET/HEAD); null = not one of these routes.</summary>
    public async Task<SupervisorReply?> TryHandleAsync(SupervisorRequest r, string path, CancellationToken ct = default)
    {
        if (!path.StartsWith(Prefix, StringComparison.Ordinal)) return null;
        var route = path[Prefix.Length..];
        if (!_buckets.TryGetValue(route, out var bucket)) return Json(404, new { error = "not found" });
        if (!bucket.TryTake(out var wait))
            return Json(429, new { error = "rate limited", retry_after_ms = (int)Math.Ceiling(wait.TotalMilliseconds) },
                new Dictionary<string, string> { ["Retry-After"] = Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds)).ToString(CultureInfo.InvariantCulture) });
        int dim = Dim(Q(r, "dim"));
        try
        {
            switch (route)
            {
                case "index":
                    {
                        if (I(Q(r, "x")) is not { } x || I(Q(r, "z")) is not { } z) return Json(400, new { error = "index needs x, z (block coords) and r" });
                        int rad = Math.Clamp(I(Q(r, "r")) ?? 96, 16, MaxIndexRadius);
                        int cx0 = (x - rad) >> 4, cx1 = (x + rad) >> 4, cz0 = (z - rad) >> 4, cz1 = (z + rad) >> 4;
                        var key = $"index:{dim}:{cx0}:{cz0}:{cx1}:{cz1}";
                        return await _cache.GetAsync(key, IndexCache, () => Work(() =>
                        {
                            var cols = _world.Index(dim, cx0, cz0, cx1, cz1, (cx1 - cx0 + 1) * (cz1 - cz0 + 1));
                            return Json(200, new { dim, at = _time.GetUtcNow().ToUnixTimeMilliseconds(), columns = cols });
                        }, ct)).ConfigureAwait(false);
                    }
                case "chunks":
                    {
                        if (ParseCols(Q(r, "cols")) is not { } cols) return Json(400, new { error = $"chunks needs cols=cx,cz;cx,cz (1..{MaxColumnsPerRequest})" });
                        return await Work(() => new SupervisorReply(200, ColumnPayload.ContentType, ColumnPayload.Encode(dim, _world.Columns(dim, cols))), ct)
                            .ConfigureAwait(false);
                    }
                case "entities":
                    {
                        int? edim = Q(r, "dim") is { Length: > 0 } ? dim : null;
                        if (Q(r, "since") is { Length: > 0 } sinceText)
                        {
                            long since = long.TryParse(sinceText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sv) ? sv : 0;
                            return await Work(() =>
                            {
                                var d = _entityLog.Since(since, () => _world.Entities(null));
                                return Json(200, new { at = _time.GetUtcNow().ToUnixTimeMilliseconds(), rev = d.Rev, full = d.Full, bots = _world.Bots(), entities = d.Entities, removed = d.Removed });
                            }, ct).ConfigureAwait(false);
                        }
                        return await _cache.GetAsync($"entities:{edim}", EntitiesCache, () => Work(() =>
                            Json(200, new { at = _time.GetUtcNow().ToUnixTimeMilliseconds(), bots = _world.Bots(), entities = _world.Entities(edim) }), ct))
                            .ConfigureAwait(false);
                    }
                case "overlays":
                    return await _cache.GetAsync($"overlays:{dim}", OverlaysCache, () => Work(() => Json(200, _world.Overlays(dim)), ct)).ConfigureAwait(false);
            }
            return Json(404, new { error = "not found" });
        }
        catch (RenderBusyException) { return Json(503, new { error = "busy, retry" }, new Dictionary<string, string> { ["Retry-After"] = "1" }); }
        catch (OperationCanceledException) { return Json(503, new { error = "cancelled" }); }
    }

    /// <summary>Runs the read on the pool (never the caller's or the game's thread), at most two at once; busy = 503.</summary>
    private async Task<SupervisorReply> Work(Func<SupervisorReply> f, CancellationToken ct)
    {
        if (!await _work.WaitAsync(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false)) throw new RenderBusyException();
        try { return await Task.Run(f, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not (OperationCanceledException or RenderBusyException)) { return Json(500, new { error = ex.Message }); }
        finally { _work.Release(); }
    }

    internal static List<(int, int)>? ParseCols(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var list = new List<(int, int)>();
        foreach (var part in s.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var xz = part.Split(',');
            if (xz.Length != 2 || I(xz[0]) is not { } cx || I(xz[1]) is not { } cz) return null;
            if (!list.Contains((cx, cz))) list.Add((cx, cz));
            if (list.Count > MaxColumnsPerRequest) return null;
        }
        return list.Count == 0 ? null : list;
    }

    private static int Dim(string? s) => I(s) is -1 ? -1 : I(s) is 1 ? 1 : 0;

    private static int? I(string? s) =>
        int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v is > -30_000_000 and < 30_000_000 ? v
        : double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d) && Math.Abs(d) < 30_000_000 ? (int)Math.Floor(d) : null;

    private static string? Q(SupervisorRequest r, string k) => r.Query.TryGetValue(k, out var v) ? v : null;

    private static SupervisorReply Json(int status, object value, IReadOnlyDictionary<string, string>? headers = null) =>
        new(status, "application/json; charset=utf-8", SupervisorJson.Bytes(value), headers);
}
