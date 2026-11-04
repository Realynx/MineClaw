using System.Globalization;
using MineClaw.World;

namespace MineClaw.App.Supervisor;

/// <summary>A known chunk column in the hive's merged world memory (no blocks): the viewer's delta key.</summary>
public sealed record WorldColumnInfo(int Cx, int Cz, long Hash, long Seen, string? By, bool Live);

/// <summary>A hive bot for the live viewer: pose (yaw/pitch when known), vitals, job, held item, claims; an in-process body also
/// what it wears and holds (<paramref name="Equipment"/>, the entity wire's slots, glint included), from its own inventory.</summary>
public sealed record WorldBot(string Name, string Short, bool Main, int Dim, double X, double Y, double Z, float? Yaw, float? Pitch,
    double? PoseAge, int Hp, int Food, string? Job, string? Brain, string? Held, IReadOnlyList<string> Claims, bool Live, double AgeSeconds,
    string? Kind = null, string? Owner = null, bool? Online = null, WorldEquipment? Equipment = null);

/// <summary>Anything else to draw (players, mobs, items, and objects: paintings, item frames, armour stands, arrows, boats,
/// minecarts, falling blocks, primed TNT, xp orbs, ender crystals, fireworks) the hive knows of, merged across every body. Items
/// carry the stack (<paramref name="Item"/> id, <paramref name="Damage"/>, <paramref name="Count"/>; an item frame's or firework's
/// item too); velocity is blocks/tick, <paramref name="Age"/> seconds since the body first saw it, <paramref name="By"/> the body
/// whose tracker it came from. The looks (all left out when unknown or default, so deltas stay small): <paramref name="Equipment"/>,
/// <paramref name="HeadYaw"/>, <paramref name="Baby"/>, <paramref name="Look"/> (the compact per-kind string, <see cref="EntityLooks.Look"/>),
/// <paramref name="Vehicle"/> (the ridden entity's id), <paramref name="Facing"/> (paintings, item frames), <paramref name="Rotation"/>
/// (an item frame's item, 0..7), <paramref name="Block"/> (a falling block), <paramref name="Fuse"/> (primed TNT ticks),
/// <paramref name="Pose"/> (an armour stand), <paramref name="InGround"/> (an arrow that stopped), <paramref name="Glint"/> (a dropped or
/// framed item that glints), <paramref name="Carried"/> (an enderman's block), <paramref name="Shoulders"/> (a player's parrots),
/// <paramref name="Arrows"/> (arrows stuck in a living entity).</summary>
public sealed record WorldEntity(string Kind, string? Name, int Dim, double X, double Y, double Z, int? Id = null, int? Type = null,
    int? Item = null, int? Damage = null, int? Count = null, double? Vx = null, double? Vy = null, double? Vz = null, double? Age = null,
    float? Yaw = null, string? By = null, int? Flags = null, int? Variant = null, WorldEquipment? Equipment = null, float? HeadYaw = null,
    bool? Baby = null, string? Look = null, int? Vehicle = null, string? Facing = null, int? Rotation = null, WireItem? Block = null,
    int? Fuse = null, WorldPose? Pose = null, bool? InGround = null, bool? Glint = null, WireItem? Carried = null, WorldShoulders? Shoulders = null,
    int? Arrows = null);

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
    /// <summary>The index for a viewer that syncs light: live hashes include the light.</summary>
    IReadOnlyList<WorldColumnInfo> Index(int dim, int cx0, int cz0, int cx1, int cz1, int max, bool light) => Index(dim, cx0, cz0, cx1, cz1, max);
    /// <summary>The columns with each live column's light (<see cref="LightSnapshot"/>, payload version 2).</summary>
    IReadOnlyList<ColumnPayloadEntry> Columns(int dim, IReadOnlyList<(int Cx, int Cz)> cols, bool light) => Columns(dim, cols);
    /// <summary>The game version the hive plays ("b1.8.1", "1.12.2"), null = unknown: the viewer's block models follow it.</summary>
    string? Version => null;
    /// <summary>The world time (ticks) in <paramref name="dim"/>, null = unknown: the viewer's day/night light follows it.</summary>
    long? WorldTime(int dim) => null;
    /// <summary>The server stopped the daylight cycle in <paramref name="dim"/> (the viewer then holds the time), null = unknown.</summary>
    bool? WorldTimeFrozen(int dim) => null;
    /// <summary>Rain and thunder strengths (0..1) in <paramref name="dim"/>, null = unknown: the viewer's sky, light and rain follow them.</summary>
    (float Rain, float Thunder)? WorldWeather(int dim) => null;
    IReadOnlyList<WorldBot> Bots();
    IReadOnlyList<WorldEntity> Entities(int? dim);
    /// <summary>The last few seconds of one-shot entity events (swings, hurts, deaths, crits, lightning) of every body, as each saw
    /// them (the delta log dedupes the copies).</summary>
    IReadOnlyList<WorldEntityEvent> EntityEvents() => [];
    /// <summary>The block entities of the columns (<c>/api/world/blockentities</c>); an unknown column comes back empty.</summary>
    IReadOnlyList<WorldBlockEntityColumn> BlockEntities(int dim, IReadOnlyList<(int Cx, int Cz)> cols) => [];
    /// <summary>What the hive knows of the container with a block at <paramref name="pos"/> (<c>/api/world/containers</c>,
    /// <see cref="ContainerKnowledge"/>); null = a live world shows a block there that is no container.</summary>
    ContainerView? Container(int dim, BlockPos pos) =>
        new([pos.X, pos.Y, pos.Z], dim, "chest", 27, [], null, null, "unknown", Version, true, [new[] { pos.X, pos.Y, pos.Z }]);
    /// <summary>The known containers with a block in the box (inclusive), newest look first, at most <paramref name="limit"/>.</summary>
    (IReadOnlyList<ContainerSummary> Items, bool Capped) Containers(int dim, BlockPos min, BlockPos max, int limit) => ([], false);
    WorldOverlays Overlays(int dim);
}

/// <summary>Per-route caps for the world routes (requests per second, burst).</summary>
public sealed record WorldRouteLimits(double IndexPerSec = 8, double IndexBurst = 16, double ChunksPerSec = 25, double ChunksBurst = 50,
    double EntitiesPerSec = 12, double EntitiesBurst = 24, double OverlaysPerSec = 1, double OverlaysBurst = 4, double BlocksPerSec = 10,
    double BlocksBurst = 20, double ContainersPerSec = 10, double ContainersBurst = 20)
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
/// <item><c>/api/world/index?dim&amp;x&amp;z&amp;r[&amp;light=1]</c> (block centre, radius 16..256) → JSON <c>{dim, at, columns:[{cx,cz,hash,seen,by,live}],
/// version, time}</c> (the game version and the world time; <c>light=1</c>: live hashes include the light)</item>
/// <item><c>/api/world/chunks?dim&amp;cols=cx,cz;cx,cz…[&amp;light=1]</c> (≤ 64) → <see cref="ColumnPayload"/> binary (<c>light=1</c>: version 2,
/// live columns with their <see cref="LightSnapshot"/>)</item>
/// <item><c>/api/world/entities?dim</c> → JSON <c>{at, rev, bots:[…], entities:[…]}</c> (bots of every dimension, entities of <c>dim</c> or all)</item>
/// <item><c>/api/world/entities?since=REV</c> → the delta stream: <c>{at, rev, full, bots, entities:[changed since REV], removed:[[dim,id]…],
/// events:[{id, kind, at, dim, x?, y?, z?}] (one-shot: each once per client)}</c>
/// (every dimension; <c>full</c> = the client was too far behind and gets everything)</item>
/// <item><c>/api/world/overlays?dim</c> → JSON claims, places, points (chests, gens, farms…), plan ghost blocks, dangers</item>
/// <item><c>/api/world/blockentities?dim&amp;cols=cx,cz;…(≤ 64)</c> → <c>{dim, columns:[{cx, cz, hash, entities:[{x,y,z,kind,…}]}]}</c>
/// (signs, beds, banners, skulls, flower pots, chests, spawners…; <c>hash</c> changes with any of them)</item>
/// <item><c>/api/world/containers?dim&amp;x&amp;y&amp;z</c> → one container's contents (<see cref="ContainerView"/>: live, the last look, the
/// world memory or Story's ledger; <c>source: "unknown"</c> when nobody looked; 404 when the block holds nothing), and
/// <c>/api/world/containers?dim&amp;x0&amp;y0&amp;z0&amp;x1&amp;y1&amp;z1[&amp;limit]</c> (≤ <see cref="MaxContainerBox"/> wide) → <c>{dim, containers:[summary], capped}</c>
/// for map badges (<see cref="ContainerSummary"/>, at most <see cref="MaxContainers"/>)</item>
/// <item><c>/api/world/blocks?since=REV[&amp;dim=]</c> → the live block feed (<see cref="BlockDeltaLog"/>): <c>{rev, full, more,
/// blocks:{"dim":[[x,y,z,id,meta]…]}, columns:[{rev,at,dim,cx,cz,kind}]}</c>, at most <see cref="MaxBlockEntries"/> entries
/// (<c>more</c>: ask again from <c>rev</c>); <c>full</c> = too far behind, refetch the columns whose index hash changed</item>
/// </list>
/// </summary>
public sealed class SupervisorWorldRoutes
{
    public const string Prefix = "/api/world/";
    public const int MaxColumnsPerRequest = 64;
    public const int MaxIndexRadius = 256;
    public const int MaxBlockEntries = 4000;
    public const int MaxContainerBox = 512;
    public const int MaxContainers = 500;

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
            ["blocks"] = new(l.BlocksPerSec, l.BlocksBurst, _time),
            ["blockentities"] = new(l.ChunksPerSec, l.ChunksBurst, _time),
            ["containers"] = new(l.ContainersPerSec, l.ContainersBurst, _time),
        };
    }

    /// <summary>The live block feed (<c>/api/world/blocks</c>); null = <see cref="BlockDeltaLog.Process"/>, none = 404.</summary>
    public BlockDeltaLog? Blocks { get; init; }

    /// <summary>Index/entity/overlay replies are shared for this long (several viewers or tiles = one read).</summary>
    public TimeSpan IndexCache { get; init; } = TimeSpan.FromMilliseconds(400);
    public TimeSpan EntitiesCache { get; init; } = TimeSpan.FromMilliseconds(100);
    public TimeSpan OverlaysCache { get; init; } = TimeSpan.FromSeconds(3);
    /// <summary>Box (map badge) replies are shared for this long; one container's reply is never cached (the open panel polls it).</summary>
    public TimeSpan ContainersCache { get; init; } = TimeSpan.FromSeconds(1);

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
                        bool light = Q(r, "light") is "1" or "true";
                        var key = $"index:{dim}:{cx0}:{cz0}:{cx1}:{cz1}:{light}";
                        return await _cache.GetAsync(key, IndexCache, () => Work(() =>
                        {
                            var cols = _world.Index(dim, cx0, cz0, cx1, cz1, (cx1 - cx0 + 1) * (cz1 - cz0 + 1), light);
                            var wx = _world.WorldWeather(dim);
                            return Json(200, new { dim, at = _time.GetUtcNow().ToUnixTimeMilliseconds(), columns = cols, version = _world.Version, time = _world.WorldTime(dim),
                                time_frozen = _world.WorldTimeFrozen(dim), rain = wx?.Rain, thunder = wx?.Thunder });
                        }, ct)).ConfigureAwait(false);
                    }
                case "chunks":
                    {
                        if (ParseCols(Q(r, "cols")) is not { } cols) return Json(400, new { error = $"chunks needs cols=cx,cz;cx,cz (1..{MaxColumnsPerRequest})" });
                        bool light = Q(r, "light") is "1" or "true";
                        return await Work(() => new SupervisorReply(200, ColumnPayload.ContentType, ColumnPayload.Encode(dim, _world.Columns(dim, cols, light))), ct)
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
                                var d = _entityLog.Since(since, () => _world.Entities(null), _world.EntityEvents);
                                return Json(200, new { at = _time.GetUtcNow().ToUnixTimeMilliseconds(), rev = d.Rev, full = d.Full, bots = _world.Bots(), entities = d.Entities, removed = d.Removed,
                                    events = d.Events });
                            }, ct).ConfigureAwait(false);
                        }
                        return await _cache.GetAsync($"entities:{edim}", EntitiesCache, () => Work(() =>
                            Json(200, new { at = _time.GetUtcNow().ToUnixTimeMilliseconds(), bots = _world.Bots(), entities = _world.Entities(edim) }), ct))
                            .ConfigureAwait(false);
                    }
                case "blocks":
                    {
                        if ((Blocks ?? BlockDeltaLog.Process) is not { } log) return Json(404, new { error = "no block feed in this process" });
                        long since = long.TryParse(Q(r, "since"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var sv) ? sv : 0;
                        int? bdim = Q(r, "dim") is { Length: > 0 } ? dim : null;
                        var page = log.Since(since, bdim, MaxBlockEntries); // a ring read under its lock: no pool hop
                        return Json(200, new { at = _time.GetUtcNow().ToUnixTimeMilliseconds(), rev = page.Rev, full = page.Full, more = page.More,
                            blocks = page.Blocks, columns = page.Columns });
                    }
                case "blockentities":
                    {
                        if (ParseCols(Q(r, "cols")) is not { } bcols) return Json(400, new { error = $"blockentities needs cols=cx,cz;cx,cz (1..{MaxColumnsPerRequest})" });
                        return await Work(() => Json(200, new { dim, columns = _world.BlockEntities(dim, bcols) }), ct).ConfigureAwait(false);
                    }
                case "containers":
                    {
                        if (Q(r, "x0") is { Length: > 0 })
                        {
                            if (I(Q(r, "x0")) is not { } x0 || I(Q(r, "z0")) is not { } z0 || I(Q(r, "x1")) is not { } x1 || I(Q(r, "z1")) is not { } z1)
                                return Json(400, new { error = "containers box needs x0, z0, x1, z1 (y0, y1 optional)" });
                            int y0 = I(Q(r, "y0")) ?? 0, y1 = I(Q(r, "y1")) ?? 319;
                            var min = new BlockPos(Math.Min(x0, x1), Math.Min(y0, y1), Math.Min(z0, z1));
                            var max = new BlockPos(Math.Max(x0, x1), Math.Max(y0, y1), Math.Max(z0, z1));
                            if (max.X - min.X > MaxContainerBox || max.Z - min.Z > MaxContainerBox)
                                return Json(400, new { error = $"containers box is at most {MaxContainerBox} blocks wide" });
                            int limit = Math.Clamp(I(Q(r, "limit")) ?? 200, 1, MaxContainers);
                            return await _cache.GetAsync($"containers:{dim}:{min}:{max}:{limit}", ContainersCache, () => Work(() =>
                            {
                                var (items, capped) = _world.Containers(dim, min, max, limit);
                                return Json(200, new { dim, containers = items, capped });
                            }, ct)).ConfigureAwait(false);
                        }
                        if (I(Q(r, "x")) is not { } cx || I(Q(r, "y")) is not { } cy || I(Q(r, "z")) is not { } cz)
                            return Json(400, new { error = "containers needs x, y, z (or a box x0..z1)" });
                        return await Work(() => _world.Container(dim, new BlockPos(cx, cy, cz)) is { } view
                            ? Json(200, view)
                            : Json(404, new { error = $"no container at {cx},{cy},{cz}" }), ct).ConfigureAwait(false);
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
