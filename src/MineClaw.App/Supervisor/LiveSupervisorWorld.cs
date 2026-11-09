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
/// a racing write only means the next index differs) and kept while the column's <see cref="ChunkColumn.Version"/> stands still; a
/// live column is copied and encoded only when asked for and changed since (else its last payload ships again), remembered ones
/// ship their stored blob untouched. Thread-safe.
/// </summary>
public sealed class WorldColumnSource(Func<IReadOnlyList<LiveBody>> bodies, Func<IWorldMemoryStore?> store, TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Dictionary<(int, int, int, bool), (ChunkColumn Col, long Version, DateTimeOffset At, long Hash)> _liveHash = new();
    private readonly Dictionary<(int, int, int, bool), (ChunkColumn Col, long Version, long Hash, byte[] Data, byte[]? Light)> _payload = new();
    private readonly object _lock = new();
    private long _hashComputations;
    internal long HashComputations => Interlocked.Read(ref _hashComputations);
    private long _mergedColumnVisits;
    internal long MergedColumnVisits => Interlocked.Read(ref _mergedColumnVisits);

    /// <summary>Hashes remain valid until the column's reference or version changes. A finite TTL may be supplied for diagnostics;
    /// expiring quiet columns periodically otherwise rescans hundreds of unchanged arrays whenever the pawns spread out.</summary>
    public TimeSpan LiveHashTtl { get; init; } = Timeout.InfiniteTimeSpan;

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

    /// <summary>The viewer's delta key of a live column: the block hash, joined with the light hash when the viewer syncs light
    /// (<see cref="LightSnapshot.Combine"/>; the same key <see cref="Columns(int, IReadOnlyList{ValueTuple{int, int}}, bool)"/> ships).</summary>
    private long LiveHash(int dim, ChunkColumn c, DateTimeOffset now, bool light)
    {
        var k = (dim, c.ChunkX, c.ChunkZ, light);
        long v = c.Version; // read before hashing: a write during the walk moves it, so the next ask hashes again
        lock (_lock)
            if (_liveHash.TryGetValue(k, out var e) && ReferenceEquals(e.Col, c) && e.Version == v
                && (LiveHashTtl == Timeout.InfiniteTimeSpan || now - e.At < LiveHashTtl)) return e.Hash;
        Interlocked.Increment(ref _hashComputations);
        long h = c.ContentHash(); // = ChunkSnapshot.ComputeHash(c.IdData, c.MetaData), without the two flat copies
        if (light) h = LightSnapshot.Combine(h, LightSnapshot.HashOf(c));
        lock (_lock)
        {
            if (_liveHash.Count > 8192) _liveHash.Clear();
            _liveHash[k] = (c, v, now, h);
        }
        return h;
    }

    private IReadOnlyList<LiveBody> Bodies()
    {
        try { return bodies(); } catch (Exception) { return []; }
    }

    public IReadOnlyList<WorldColumnInfo> Index(int dim, int cx0, int cz0, int cx1, int cz1, int max) => Index(dim, cx0, cz0, cx1, cz1, max, false);

    /// <summary><paramref name="light"/>: live columns' hashes include their light (the viewer fetches them with light).</summary>
    public IReadOnlyList<WorldColumnInfo> Index(int dim, int cx0, int cz0, int cx1, int cz1, int max, bool light)
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
        var bs = Bodies().Where(b => b.World.Dimension == dim && b.World.LoadedColumnCount > 0).ToArray();
        // A small view usually belongs to the first body and needs one dictionary lookup per cell. A wide view spanning many
        // bodies used to search every body again for every cell (including unloaded cells); merge their loaded columns once,
        // but only when that scans fewer columns than direct lookups. An unloaded main must not turn a tiny view into a hive scan.
        long cells = ((long)cx1 - cx0 + 1) * ((long)cz1 - cz0 + 1);
        var live = bs.Length > 1 && cells > bs[0].World.LoadedColumnCount
            && bs.Sum(b => (long)b.World.LoadedColumnCount) < cells * bs.Length
            ? IndexLives(bs, dim, cx0, cz0, cx1, cz1) : null;
        if (bs.Length > 0)
            for (int cx = cx0; cx <= cx1; cx++)
                for (int cz = cz0; cz <= cz1; cz++)
                    if ((live != null ? live.GetValueOrDefault((cx, cz)) : Live(bs, dim, cx, cz)) is { } l)
                        map[(cx, cz)] = new WorldColumnInfo(cx, cz, LiveHash(dim, l.Col, now, light), now.ToUnixTimeMilliseconds(), l.By, true);
        return map.Values.Take(Math.Max(0, max)).ToList();
    }

    private Dictionary<(int, int), (ChunkColumn Col, string By)?> IndexLives(IReadOnlyList<LiveBody> bodies, int dim,
        int cx0, int cz0, int cx1, int cz1)
    {
        var map = new Dictionary<(int, int), (ChunkColumn Col, string By)?>();
        long visited = 0;
        foreach (var body in bodies)
            foreach (var column in body.World.Columns)
            {
                visited++;
                if (column.HasData && column.Dimension == dim && column.ChunkX >= cx0 && column.ChunkX <= cx1
                    && column.ChunkZ >= cz0 && column.ChunkZ <= cz1)
                    map.TryAdd((column.ChunkX, column.ChunkZ), (column, body.Name)); // preserve the existing main-first precedence
            }
        Interlocked.Add(ref _mergedColumnVisits, visited);
        return map;
    }

    public IReadOnlyList<ColumnPayloadEntry> Columns(int dim, IReadOnlyList<(int Cx, int Cz)> cols) => Columns(dim, cols, false);

    /// <summary><paramref name="light"/>: live columns carry their <see cref="LightSnapshot"/> (and the joined hash).</summary>
    public IReadOnlyList<ColumnPayloadEntry> Columns(int dim, IReadOnlyList<(int Cx, int Cz)> cols, bool light)
    {
        var now = _time.GetUtcNow();
        var bs = Bodies();
        var s = store();
        var list = new List<ColumnPayloadEntry>(cols.Count);
        foreach (var (cx, cz) in cols)
        {
            if (Live(bs, dim, cx, cz) is { } l)
            {
                // An unchanged column (same version) ships the payload encoded the last time: no 64 KB copy, hash or deflate.
                var key = (dim, cx, cz, light);
                long v = l.Col.Version;
                (ChunkColumn Col, long Version, long Hash, byte[] Data, byte[]? Light) p;
                bool hit;
                lock (_lock) hit = _payload.TryGetValue(key, out p) && ReferenceEquals(p.Col, l.Col) && p.Version == v;
                if (!hit)
                {
                    var snap = ChunkSnapshot.Capture(l.Col);
                    var data = snap.Encode();
                    if (light)
                    {
                        var ls = LightSnapshot.Capture(l.Col);
                        p = (l.Col, v, LightSnapshot.Combine(snap.Hash, ls.Hash), data, ls.Encode());
                    }
                    else p = (l.Col, v, snap.Hash, data, null);
                    lock (_lock)
                    {
                        if (_payload.Count > 1024) _payload.Clear();
                        _payload[key] = p;
                    }
                }
                list.Add(new ColumnPayloadEntry(cx, cz, p.Hash, now.ToUnixTimeMilliseconds(), l.By, true, p.Data, p.Light));
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
    private readonly ContainerKnowledge _containers;
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
        _containers = new ContainerKnowledge(ContainerSightings.Process, () => Bodies().Select(b => b.World).ToList(), () => _rt?.Observations,
            () => Version, _time);
    }

    /// <summary>Every body this process controls (a hive's subhive bodies too: their snapshot positions); null = the hub's hive
    /// bots and the in-process bodies only, as before.</summary>
    public HiveBodyList? AllBodies { get; init; }

    /// <summary>The routes for the host: main + every in-process body (<see cref="BotHost.Bodies"/>), the shared memory.</summary>
    public static SupervisorWorldRoutes Routes(VisionHub hub, BotHost bot, Func<WorldState> world, string main, SupervisorOptions o, ILogger? log,
        HiveBodyList? all = null)
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
        return new SupervisorWorldRoutes(new LiveSupervisorWorld(hub, world, main, Bodies, bot.Runtime, () => bot.Blueprints?.Service, o, log) { AllBodies = all });
    }

    private IReadOnlyList<LiveBody> Bodies()
    {
        try { return _bodies(); }
        catch (Exception ex) { _log?.LogDebug(ex, "supervisor world: bodies unavailable"); return [new LiveBody(_main, "main", true, _world())]; }
    }

    public IReadOnlyList<WorldColumnInfo> Index(int dim, int cx0, int cz0, int cx1, int cz1, int max) => _columns.Index(dim, cx0, cz0, cx1, cz1, max);

    public IReadOnlyList<ColumnPayloadEntry> Columns(int dim, IReadOnlyList<(int Cx, int Cz)> cols) => _columns.Columns(dim, cols);

    public IReadOnlyList<WorldColumnInfo> Index(int dim, int cx0, int cz0, int cx1, int cz1, int max, bool light) => _columns.Index(dim, cx0, cz0, cx1, cz1, max, light);

    public IReadOnlyList<ColumnPayloadEntry> Columns(int dim, IReadOnlyList<(int Cx, int Cz)> cols, bool light) => _columns.Columns(dim, cols, light);

    /// <summary>The process's game version (one per process): the viewer picks its block models by it.</summary>
    public string? Version => MineClaw.Data.Versioning.GameData.Current.VersionId;

    /// <summary>The world time (ticks) of the first in-process body (main first) in <paramref name="dim"/>.</summary>
    public long? WorldTime(int dim)
    {
        foreach (var b in Bodies())
            if (b.World.Dimension == dim) return b.World.Time;
        return null;
    }

    /// <summary>Whether the server stopped the daylight cycle (as the first in-process body in <paramref name="dim"/> heard it).</summary>
    public bool? WorldTimeFrozen(int dim)
    {
        foreach (var b in Bodies())
            if (b.World.Dimension == dim) return b.World.TimeFrozen;
        return null;
    }

    /// <summary>The rain and thunder of the first in-process body in <paramref name="dim"/> (the server's weather; live 10-29 the
    /// viewer never showed it).</summary>
    public (float Rain, float Thunder)? WorldWeather(int dim)
    {
        foreach (var b in Bodies())
            if (b.World.Dimension == dim) return (b.World.Rain, b.World.Thunder);
        return null;
    }

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
        var live = Bodies().ToDictionary(b => b.Name, StringComparer.OrdinalIgnoreCase);
        var list = new List<WorldBot>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        List<HiveBotState> hive;
        try { hive = _hub.HiveBots(); } catch (Exception ex) { _log?.LogDebug(ex, "supervisor world: hive bots unavailable"); hive = []; }
        foreach (var hb in hive)
        {
            if (!seen.Add(hb.Name)) continue;
            if (live.TryGetValue(hb.Name, out var lb) || (hb.IsMain && live.TryGetValue(_main, out lb))) list.Add(LiveBot(lb, hb, ClaimsOf(leases, lb.Name)));
            else list.Add(HubBot(hb, now, ClaimsOf(leases, hb.Name)));
        }
        foreach (var lb in live.Values)
            if (seen.Add(lb.Name)) list.Add(LiveBot(lb, null, ClaimsOf(leases, lb.Name)));
        if (AllBodies is { } all) AddBodyRows(all, now, leases, list, seen);
        return list;
    }

    private static IReadOnlyList<string> ClaimsOf(IReadOnlyList<HiveLease> leases, string name) =>
        leases.Where(l => l.Owner.Equals(name, StringComparison.OrdinalIgnoreCase)).Select(l => HiveVision.Lease(l.Key).Text).ToList();

    /// <summary>A hive bot this process doesn't drive: where the hub last saw it, or its eye pose when that is fresh and close by.</summary>
    private WorldBot HubBot(HiveBotState hb, DateTimeOffset now, IReadOnlyList<string> claims)
    {
        var p = Pose(hb.Name, now);
        bool usePose = p != null && p.Dim == hb.Dim && Math.Abs(p.X - hb.X) + Math.Abs(p.Z - hb.Z) < 24;
        return new WorldBot(hb.Name, hb.Short, hb.IsMain, hb.Dim, R(usePose ? p!.X : hb.X), R(usePose ? p!.Y : hb.Y), R(usePose ? p!.Z : hb.Z),
            p?.Yaw, p?.Pitch, p != null ? Math.Round(Math.Max(0, (now - p.At).TotalSeconds), 1) : null, hb.Hp, hb.Food, hb.Job, hb.Brain, null,
            claims, false, Math.Round(Math.Max(0, (now - hb.At).TotalSeconds), 1));
    }

    /// <summary>Each bot's kind, owner and online state from the body list; then a hive's subhive bodies (their hive streams their
    /// positions with its snapshot) and bodies whose position is known but that the hub doesn't list; a body with no position yet
    /// isn't drawn.</summary>
    private void AddBodyRows(HiveBodyList all, DateTimeOffset now, IReadOnlyList<HiveLease> leases, List<WorldBot> list, HashSet<string> seen)
    {
        IReadOnlyList<BodyRow> rows = [];
        try { rows = all.Rows(); } catch (Exception ex) { _log?.LogDebug(ex, "supervisor world: body list unavailable"); }
        var byName = rows.GroupBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < list.Count; i++)
            if (byName.TryGetValue(list[i].Name, out var r)) list[i] = list[i] with { Kind = r.Kind, Owner = r.Owner, Online = r.Online };
            else if (list[i].Main) list[i] = list[i] with { Kind = "main", Online = true };
        foreach (var r in rows)
            if (r.HasPos && seen.Add(r.Name))
                list.Add(new WorldBot(r.Name, r.Short, r.IsMain, r.Dim, R(r.X), R(r.Y), R(r.Z), null, null, null, r.Hp, r.Food, r.Job, r.Brain, null,
                    ClaimsOf(leases, r.Name), false, Math.Round(Math.Max(0, (now - r.At).TotalSeconds), 1), r.Kind, r.Owner, r.Online));
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
            me.Health, me.Food, job, hb?.Brain, HeldOf(w), claims, true, 0) { Equipment = EquipmentOf(w) };
    }

    /// <summary>What a live body wears and holds (player window: 5 helmet .. 8 boots, the held hotbar slot, 45 the off hand).</summary>
    internal static WorldEquipment? EquipmentOf(WorldState w)
    {
        try
        {
            var inv = w.Inventory;
            WireItem? S(ItemStack st) => st.IsEmpty ? null : new WireItem(st.Id, st.Damage, st.Tag?.DisplayColor, EntityLooks.Glint(st));
            var eq = new WorldEquipment(S(inv.HeldItem(w.Player.HeldSlot)), S(inv.Offhand), S(inv.GetPlayerSlot(5)), S(inv.GetPlayerSlot(6)),
                S(inv.GetPlayerSlot(7)), S(inv.GetPlayerSlot(8)));
            return eq == new WorldEquipment() ? null : eq;
        }
        catch (Exception) { return null; }
    }

    /// <summary>Every entity any in-process body tracks (players, mobs, drops), merged by server entity id
    /// (<see cref="HiveEntityMerge"/>), optionally of one dimension. Out-of-process clones only contribute their bot pose.</summary>
    public IReadOnlyList<WorldEntity> Entities(int? dim)
    {
        var bodies = new List<BodyEntities>();
        foreach (var b in Bodies())
        {
            try
            {
                int bd = b.World.Dimension;
                if (dim is { } d && bd != d) continue;
                bodies.Add(new BodyEntities(b.Name, bd, b.World.Entities.All()));
            }
            catch (Exception ex) { _log?.LogDebug(ex, "supervisor world: entities of {Body} unavailable", b.Name); }
        }
        var botNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try { foreach (var b in Bots()) botNames.Add(b.Name); } catch (Exception) { }
        foreach (var b in Bodies()) botNames.Add(b.Name);
        return HiveEntityMerge.Merge(bodies, botNames, _time.GetUtcNow().UtcDateTime);
    }

    /// <summary>Every in-process body's one-shot entity events of the last few seconds (the delta log dedupes the copies).</summary>
    public IReadOnlyList<WorldEntityEvent> EntityEvents()
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var list = new List<WorldEntityEvent>();
        foreach (var b in Bodies())
        {
            try
            {
                list.AddRange(EntityLooks.EventsOf(b.World, now));
            }
            catch (Exception ex) { _log?.LogDebug(ex, "supervisor world: events of {Body} unavailable", b.Name); }
        }
        return list;
    }

    /// <summary>The columns' block entities, merged over every in-process body in <paramref name="dim"/>.</summary>
    /// <summary>A container's contents: live, the last look of any body, the world memory or Story's ledger (<see cref="ContainerKnowledge"/>).</summary>
    public ContainerView? Container(int dim, BlockPos pos) => _containers.GetForViewer(dim, pos);

    public (IReadOnlyList<ContainerSummary> Items, bool Capped) Containers(int dim, BlockPos min, BlockPos max, int limit) => _containers.InBox(dim, min, max, limit);

    public IReadOnlyList<WorldBlockEntityColumn> BlockEntities(int dim, IReadOnlyList<(int Cx, int Cz)> cols)
    {
        var worlds = Bodies().Select(b => b.World).Where(w => w.Dimension == dim).ToList();
        return cols.Select(c => BlockEntityColumns.Build(c.Cx, c.Cz, worlds)).ToList();
    }

    private static int[] P(BlockPos p) => [p.X, p.Y, p.Z];
    private static int[] P(double x, double y, double z) => [(int)Math.Floor(x), (int)Math.Floor(y), (int)Math.Floor(z)];

    private static readonly string[] PointKinds = [IndexKinds.Chest, IndexKinds.Furnace, IndexKinds.CraftingTable, IndexKinds.Generator, IndexKinds.Farm,
        IndexKinds.Portal, IndexKinds.Bed, IndexKinds.Spawner, IndexKinds.Island, IndexKinds.Dispenser];

    public WorldOverlays Overlays(int dim)
    {
        var now = _time.GetUtcNow();
        var notes = new List<string>();
        var claims = ClaimMarks(dim);
        var places = new List<WorldMark>();
        var points = new List<WorldMark>();
        var dangers = new List<WorldMark>();
        if (_rt is { } rt)
        {
            AddRemembered(rt, dim, places, points, notes);
            AddIndexed(rt, dim, points, dangers, notes);
        }
        else notes.Add("no runtime: places and points unavailable");
        AddVitals(dim, dangers);
        var plans = Plans(dim, notes);
        return new WorldOverlays(now, dim, claims, places, points, plans, dangers, notes);
    }

    /// <summary>The hive's area claims in this dimension (task claims have no area).</summary>
    private List<WorldMark> ClaimMarks(int dim)
    {
        var claims = new List<WorldMark>();
        foreach (var l in Leases())
        {
            var (text, k) = HiveVision.Lease(l.Key);
            if (k is not { } key || key.Kind == ClaimKind.Task || key.Dim != dim) continue;
            claims.Add(new WorldMark(key.Kind.ToString().ToLowerInvariant(), text, P(key.Min), P(key.Max), l.Owner));
        }
        return claims;
    }

    /// <summary>The memory's structures (with up to 12 key blocks each) and waypoints.</summary>
    private void AddRemembered(AgentRuntime rt, int dim, List<WorldMark> places, List<WorldMark> points, List<string> notes)
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
    }

    /// <summary>The world index's points of interest (chests up to 400, the rest up to 100 each) and hazards.</summary>
    private void AddIndexed(AgentRuntime rt, int dim, List<WorldMark> points, List<WorldMark> dangers, List<string> notes)
    {
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

    /// <summary>Bots in this dimension low on health (≤ 6) or food (≤ 4, alive).</summary>
    private void AddVitals(int dim, List<WorldMark> dangers)
    {
        foreach (var b in Bots().Where(b => b.Dim == dim))
        {
            if (b.Hp is > 0 and <= 6) dangers.Add(new WorldMark("vital", $"{b.Short} hp {b.Hp}", P(b.X, b.Y, b.Z), P(b.X, b.Y, b.Z), b.Name));
            if (b.Food is >= 0 and <= 4 && b.Hp > 0) dangers.Add(new WorldMark("vital", $"{b.Short} food {b.Food}", P(b.X, b.Y, b.Z), P(b.X, b.Y, b.Z), b.Name));
        }
    }

    /// <summary>The live build plans' blocks in this dimension (the 12 newest blueprints in play, 6000 placements at most).</summary>
    private List<WorldPlan> Plans(int dim, List<string> notes)
    {
        var plans = new List<WorldPlan>();
        try
        {
            if (_blueprints() is not { Plans: { } store } svc) return plans;
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
        catch (Exception ex) { notes.Add("build plans unavailable"); _log?.LogDebug(ex, "supervisor world: plans failed"); }
        return plans;
    }

    private static string Trunc(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";
}
