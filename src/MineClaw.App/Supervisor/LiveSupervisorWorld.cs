using MineClaw.Agent;
using MineClaw.Bot;
using MineClaw.Bot.GameTools;
using MineClaw.Bot.Perception.Vision;
using MineClaw.Bot.WorldIndex;
using MineClaw.Data;
using MineClaw.World;
using MineClaw.World.Claims;
using Microsoft.Extensions.Logging;

namespace MineClaw.App.Supervisor;

/// <summary>A body whose client runs in this process (main, in-process clones): its live world and job.</summary>
public sealed record LiveBody(string Name, string Short, bool IsMain, WorldState World, Func<string?>? Job = null);

/// <summary>
/// The hive's merged world memory as columns for the Live Hive viewer: live columns of every in-process body (main first), then
/// every hive bot's remembered <c>world_chunks</c> rows. Live hashes are computed straight off the column arrays (no copy, no lock:
/// a racing write only means the next index differs) and cached ~1 s; a live column is copied and encoded only when asked for,
/// remembered ones ship their stored blob untouched. Thread-safe.
/// </summary>
public sealed class WorldColumnSource(Func<IReadOnlyList<LiveBody>> bodies, Func<IWorldMemoryStore?> store, TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Dictionary<(int, int, int), (DateTimeOffset At, long Hash, string By)> _liveHash = new();
    private readonly Dictionary<(int, int, int, long), byte[]> _encoded = new();
    private readonly object _lock = new();

    public TimeSpan LiveHashTtl { get; init; } = TimeSpan.FromSeconds(1);

    private (ChunkColumn Col, string By)? Live(IReadOnlyList<LiveBody> bs, int dim, int cx, int cz)
    {
        foreach (var b in bs)
        {
            var w = b.World;
            if (w.Dimension != dim) continue;
            if (w.GetColumn(cx, cz) is { HasData: true } c && c.Dimension == dim) return (c, b.Name);
        }
        return null;
    }

    private long LiveHash(int dim, ChunkColumn c, string by, DateTimeOffset now)
    {
        var k = (dim, c.ChunkX, c.ChunkZ);
        lock (_lock)
            if (_liveHash.TryGetValue(k, out var e) && now - e.At < LiveHashTtl && e.By == by) return e.Hash;
        long h = ChunkSnapshot.ComputeHash(c.IdData, c.MetaData);
        lock (_lock)
        {
            if (_liveHash.Count > 8192) _liveHash.Clear();
            _liveHash[k] = (now, h, by);
        }
        return h;
    }

    private IReadOnlyList<LiveBody> Bodies()
    {
        try { return bodies(); } catch (Exception) { return []; }
    }

    public IReadOnlyList<WorldColumnInfo> Index(int dim, int cx0, int cz0, int cx1, int cz1, int max)
    {
        var now = _time.GetUtcNow();
        var map = new Dictionary<(int, int), WorldColumnInfo>();
        try
        {
            if (store() is { } s)
                foreach (var r in s.ChunkIndex(dim, cx0, cz0, cx1, cz1, max))
                    map[(r.X, r.Z)] = new WorldColumnInfo(r.X, r.Z, r.Hash, r.LastSeen.ToUnixTimeMilliseconds(), r.SeenBy, false);
        }
        catch (Exception) { /* memory unavailable: live only */ }
        var bs = Bodies();
        if (bs.Any(b => b.World.Dimension == dim))
            for (int cx = cx0; cx <= cx1; cx++)
                for (int cz = cz0; cz <= cz1; cz++)
                    if (Live(bs, dim, cx, cz) is { } l)
                        map[(cx, cz)] = new WorldColumnInfo(cx, cz, LiveHash(dim, l.Col, l.By, now), now.ToUnixTimeMilliseconds(), l.By, true);
        return map.Values.Take(Math.Max(0, max)).ToList();
    }

    public IReadOnlyList<ColumnPayloadEntry> Columns(int dim, IReadOnlyList<(int Cx, int Cz)> cols)
    {
        var now = _time.GetUtcNow();
        var bs = Bodies();
        var s = store();
        var list = new List<ColumnPayloadEntry>(cols.Count);
        foreach (var (cx, cz) in cols)
        {
            if (Live(bs, dim, cx, cz) is { } l)
            {
                var snap = ChunkSnapshot.Capture(l.Col);
                var key = (dim, cx, cz, snap.Hash);
                byte[]? data;
                lock (_lock) _encoded.TryGetValue(key, out data);
                if (data == null)
                {
                    data = snap.Encode();
                    lock (_lock)
                    {
                        if (_encoded.Count > 512) _encoded.Clear();
                        _encoded[key] = data;
                    }
                }
                list.Add(new ColumnPayloadEntry(cx, cz, snap.Hash, now.ToUnixTimeMilliseconds(), l.By, true, data));
                continue;
            }
            ChunkRow? row = null;
            try { row = s?.GetChunk(dim, cx, cz); } catch (Exception) { }
            list.Add(row != null
                ? new ColumnPayloadEntry(cx, cz, row.Hash, row.LastSeen.ToUnixTimeMilliseconds(), row.SeenBy, false, row.Data)
                : new ColumnPayloadEntry(cx, cz, 0, 0, null, false, []));
        }
        return list;
    }
}

/// <summary>
/// The Live Hive's data from the main process: <see cref="WorldColumnSource"/> for blocks, the hub's hive bots merged with the
/// in-process bodies (exact live poses) and out-of-process clones (pose over the existing "send me your view" IPC, refreshed in
/// the background at most once per <see cref="SupervisorOptions.PoseCacheMs"/>: a request never waits on it), and overlays from
/// the claim board, memory, the world index and the build planner. Reads only.
/// </summary>
public sealed class LiveSupervisorWorld : ISupervisorWorld
{
    private readonly VisionHub _hub;
    private readonly Func<WorldState> _world;
    private readonly string _main;
    private readonly Func<IReadOnlyList<LiveBody>> _bodies;
    private readonly AgentRuntime? _rt;
    private readonly Func<MineClaw.Bot.Blueprints.BlueprintService?> _blueprints;
    private readonly SupervisorOptions _o;
    private readonly ILogger? _log;
    private readonly TimeProvider _time;
    private readonly WorldColumnSource _columns;
    private readonly Dictionary<string, (DateTimeOffset Asked, HiveEyePose? Pose, bool Busy)> _poses = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public LiveSupervisorWorld(VisionHub hub, Func<WorldState> world, string main, Func<IReadOnlyList<LiveBody>> bodies, AgentRuntime? rt,
        Func<MineClaw.Bot.Blueprints.BlueprintService?> blueprints, SupervisorOptions o, ILogger? log = null, TimeProvider? time = null)
    {
        _hub = hub;
        _world = world;
        _main = main;
        _bodies = bodies;
        _rt = rt;
        _blueprints = blueprints;
        _o = o;
        _log = log;
        _time = time ?? TimeProvider.System;
        _columns = new WorldColumnSource(Bodies, () => hub.WorldStore, _time);
    }

    /// <summary>The routes for the host: main + every in-process body (<see cref="BotHost.Bodies"/>), the shared memory.</summary>
    public static SupervisorWorldRoutes Routes(VisionHub hub, BotHost bot, Func<WorldState> world, string main, SupervisorOptions o, ILogger? log)
    {
        IReadOnlyList<LiveBody> Bodies()
        {
            var list = new List<LiveBody>();
            if (bot.Bodies is { } reg)
                foreach (var b in reg.All)
                {
                    WorldState? w = null;
                    try { w = b.Adapter.World; } catch (Exception) { }
                    if (w == null || b.IsMain) continue;
                    var acts = b.Activities;
                    list.Add(new LiveBody(b.Name, b.Short, false, w, () => acts.Describe()));
                }
            list.Insert(0, new LiveBody(main, "main", true, world(), () => bot.Runtime.Activities.Describe()));
            return list;
        }
        return new SupervisorWorldRoutes(new LiveSupervisorWorld(hub, world, main, Bodies, bot.Runtime, () => bot.Blueprints?.Service, o, log));
    }

    private IReadOnlyList<LiveBody> Bodies()
    {
        try { return _bodies(); }
        catch (Exception ex) { _log?.LogDebug(ex, "supervisor world: bodies unavailable"); return [new LiveBody(_main, "main", true, _world())]; }
    }

    public IReadOnlyList<WorldColumnInfo> Index(int dim, int cx0, int cz0, int cx1, int cz1, int max) => _columns.Index(dim, cx0, cz0, cx1, cz1, max);

    public IReadOnlyList<ColumnPayloadEntry> Columns(int dim, IReadOnlyList<(int Cx, int Cz)> cols) => _columns.Columns(dim, cols);

    private IReadOnlyList<HiveLease> Leases()
    {
        try { return _hub.Hive?.Leases() ?? []; }
        catch (Exception ex) { _log?.LogDebug(ex, "supervisor world: leases unavailable"); return []; }
    }

    private static string? HeldOf(WorldState w)
    {
        try
        {
            var st = w.Inventory.HeldItem(w.Player.HeldSlot);
            if (st.IsEmpty) return null;
            var name = Items.Get(st.Id).Name.Replace('_', ' ');
            return st.Count > 1 ? $"{name} x{st.Count}" : name;
        }
        catch (Exception) { return null; }
    }

    /// <summary>An out-of-process clone's last eye pose; a stale one is refreshed in the background (never awaited here).</summary>
    private HiveEyePose? Pose(string clone, DateTimeOffset now)
    {
        if (_hub.Hive is not { } hive) return null;
        lock (_lock)
        {
            _poses.TryGetValue(clone, out var p);
            if (!p.Busy && now - p.Asked >= TimeSpan.FromMilliseconds(Math.Max(500, _o.PoseCacheMs)))
            {
                _poses[clone] = (now, p.Pose, true);
                _ = RefreshAsync(hive, clone);
            }
            return p.Pose;
        }
    }

    private async Task RefreshAsync(IHiveVision hive, string clone)
    {
        HiveEyePose? pose = null;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            pose = await hive.ViewAsync(clone, cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) { _log?.LogDebug(ex, "supervisor world: pose of {Clone} failed", clone); }
        lock (_lock)
        {
            var old = _poses.GetValueOrDefault(clone);
            _poses[clone] = (old.Asked, pose ?? old.Pose, false);
        }
    }

    public IReadOnlyList<WorldBot> Bots()
    {
        var now = _time.GetUtcNow();
        var leases = Leases();
        IReadOnlyList<string> Claims(string name) =>
            leases.Where(l => l.Owner.Equals(name, StringComparison.OrdinalIgnoreCase)).Select(l => HiveVision.Lease(l.Key).Text).ToList();
        var live = Bodies().ToDictionary(b => b.Name, StringComparer.OrdinalIgnoreCase);
        var list = new List<WorldBot>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        List<HiveBotState> hive;
        try { hive = _hub.HiveBots(); } catch (Exception ex) { _log?.LogDebug(ex, "supervisor world: hive bots unavailable"); hive = []; }
        foreach (var hb in hive)
        {
            if (!seen.Add(hb.Name)) continue;
            if (live.TryGetValue(hb.Name, out var lb) || (hb.IsMain && live.TryGetValue(_main, out lb))) list.Add(LiveBot(lb, hb, Claims(lb.Name)));
            else
            {
                var p = Pose(hb.Name, now);
                bool usePose = p != null && p.Dim == hb.Dim && Math.Abs(p.X - hb.X) + Math.Abs(p.Z - hb.Z) < 24;
                list.Add(new WorldBot(hb.Name, hb.Short, hb.IsMain, hb.Dim, R(usePose ? p!.X : hb.X), R(usePose ? p!.Y : hb.Y), R(usePose ? p!.Z : hb.Z),
                    p?.Yaw, p?.Pitch, p != null ? Math.Round(Math.Max(0, (now - p.At).TotalSeconds), 1) : null, hb.Hp, hb.Food, hb.Job, hb.Brain, null,
                    Claims(hb.Name), false, Math.Round(Math.Max(0, (now - hb.At).TotalSeconds), 1)));
            }
        }
        foreach (var lb in live.Values)
            if (seen.Add(lb.Name)) list.Add(LiveBot(lb, null, Claims(lb.Name)));
        return list;
    }

    private static double R(double v) => Math.Round(v, 2);

    private WorldBot LiveBot(LiveBody b, HiveBotState? hb, IReadOnlyList<string> claims)
    {
        var w = b.World;
        var me = w.Player;
        string? job = hb?.Job;
        if (job == null && b.Job != null) try { job = b.Job(); } catch (Exception) { }
        var pos = me.Position;
        return new WorldBot(b.Name, b.IsMain ? "main" : b.Short, b.IsMain, w.Dimension, R(pos.X), R(pos.Y), R(pos.Z), me.Yaw, me.Pitch, 0,
            me.Health, me.Food, job, hb?.Brain, HeldOf(w), claims, true, 0);
    }

    public IReadOnlyList<WorldEntity> Entities(int? dim)
    {
        var bots = Bots();
        var list = new List<WorldEntity>();
        var keys = new HashSet<(string, string?, int, int, int)>();
        var botNames = new HashSet<string>(bots.Select(b => b.Name), StringComparer.OrdinalIgnoreCase);
        foreach (var b in bots)
        {
            if (dim is { } d && b.Dim != d) continue;
            IReadOnlyList<MineClaw.Render.VisionEntity> near;
            try { near = _hub.EntitiesNear(b.Dim, new BlockPos((int)Math.Floor(b.X), (int)Math.Floor(b.Y), (int)Math.Floor(b.Z)), 48); }
            catch (Exception ex) { _log?.LogDebug(ex, "supervisor world: entities unavailable"); continue; }
            foreach (var e in near)
            {
                if (e.Kind is "self" or "hive" || (e.Name != null && botNames.Contains(e.Name))) continue;
                if (!keys.Add((e.Kind, e.Name, b.Dim, (int)Math.Floor(e.X), (int)Math.Floor(e.Z)))) continue;
                list.Add(new WorldEntity(e.Kind, e.Name, b.Dim, R(e.X), R(e.Y), R(e.Z)));
                if (list.Count >= 256) return list;
            }
        }
        return list;
    }

    private static int[] P(BlockPos p) => [p.X, p.Y, p.Z];
    private static int[] P(double x, double y, double z) => [(int)Math.Floor(x), (int)Math.Floor(y), (int)Math.Floor(z)];

    private static readonly string[] PointKinds = [IndexKinds.Chest, IndexKinds.Furnace, IndexKinds.CraftingTable, IndexKinds.Generator, IndexKinds.Farm,
        IndexKinds.Portal, IndexKinds.Bed, IndexKinds.Spawner, IndexKinds.Island, IndexKinds.Dispenser];

    public WorldOverlays Overlays(int dim)
    {
        var now = _time.GetUtcNow();
        var notes = new List<string>();
        var claims = new List<WorldMark>();
        foreach (var l in Leases())
        {
            var (text, k) = HiveVision.Lease(l.Key);
            if (k is not { } key || key.Kind == ClaimKind.Task || key.Dim != dim) continue;
            claims.Add(new WorldMark(key.Kind.ToString().ToLowerInvariant(), text, P(key.Min), P(key.Max), l.Owner));
        }
        var places = new List<WorldMark>();
        var points = new List<WorldMark>();
        var dangers = new List<WorldMark>();
        if (_rt is { } rt)
        {
            try
            {
                var (wps, sts) = rt.Memory.Read(d => (d.Waypoints.ToList(), d.Structures.ToList()));
                foreach (var s in sts.Where(s => s.Dimension == dim))
                {
                    if (s.Min is not { } mn) continue;
                    var mx = s.Max ?? mn;
                    places.Add(new WorldMark("structure:" + s.Kind, s.Name, [mn.X, mn.Y, mn.Z], [mx.X, mx.Y, mx.Z]));
                    foreach (var kb in s.KeyBlocks.Take(12)) points.Add(new WorldMark("key", $"{s.Name}: {kb.Label}", [kb.X, kb.Y, kb.Z], [kb.X, kb.Y, kb.Z]));
                }
                foreach (var wp in wps.Where(w => w.Dimension == dim))
                    places.Add(new WorldMark("waypoint", wp.Name, [wp.X, wp.Y, wp.Z], [wp.X, wp.Y, wp.Z]));
            }
            catch (Exception ex) { notes.Add("memory unavailable"); _log?.LogDebug(ex, "supervisor world: memory read failed"); }
            try
            {
                foreach (var kind in PointKinds)
                    foreach (var row in rt.Observations.OfKind(kind, dim, kind == IndexKinds.Chest ? 400 : 100))
                        if (row.Bounds is { } b)
                            points.Add(new WorldMark(kind, Trunc(row.Text, 120), P(b.MinX, b.MinY, b.MinZ), P(b.MaxX, b.MaxY, b.MaxZ)));
                foreach (var row in rt.Observations.OfKind(IndexKinds.Hazard, dim, 100))
                    if (row.Bounds is { } b) dangers.Add(new WorldMark("hazard", Trunc(row.Text, 120), P(b.MinX, b.MinY, b.MinZ), P(b.MaxX, b.MaxY, b.MaxZ)));
            }
            catch (Exception ex) { notes.Add("world index unavailable"); _log?.LogDebug(ex, "supervisor world: observations failed"); }
        }
        else notes.Add("no runtime: places and points unavailable");
        foreach (var b in Bots().Where(b => b.Dim == dim))
        {
            if (b.Hp is > 0 and <= 6) dangers.Add(new WorldMark("vital", $"{b.Short} hp {b.Hp}", P(b.X, b.Y, b.Z), P(b.X, b.Y, b.Z), b.Name));
            if (b.Food is >= 0 and <= 4 && b.Hp > 0) dangers.Add(new WorldMark("vital", $"{b.Short} food {b.Food}", P(b.X, b.Y, b.Z), P(b.X, b.Y, b.Z), b.Name));
        }
        var plans = new List<WorldPlan>();
        try
        {
            if (_blueprints() is { Plans: { } store } svc)
            {
                int budget = 6000;
                foreach (var rec in svc.Store.List(12).Where(r => r.Status is "accepted" or "building" or "review" or "draft"))
                    foreach (var part in store.ForBlueprint(rec.Id).Where(p => p.Dim == dim))
                    {
                        var blocks = part.Steps.Where(s => s.Action == MineClaw.Bot.Building.StepAction.Place).Take(budget)
                            .Select(s => new[] { s.Target.X, s.Target.Y, s.Target.Z, s.Id, s.Meta, s.Finished ? 1 : 0 }).ToList();
                        budget -= blocks.Count;
                        plans.Add(new WorldPlan(rec.Id, rec.Name, rec.Status, part.Part, blocks));
                        if (budget <= 0) break;
                    }
            }
        }
        catch (Exception ex) { notes.Add("build plans unavailable"); _log?.LogDebug(ex, "supervisor world: plans failed"); }
        return new WorldOverlays(now, dim, claims, places, points, plans, dangers, notes);
    }

    private static string Trunc(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";
}
