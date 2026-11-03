using MineClaw.World;

namespace MineClaw.App.Supervisor;

/// <summary>
/// The live block feed for the browser world viewer (<c>/api/world/blocks?since=REV</c>): it downloads each column once, then
/// patches single blocks from here. A ring (<see cref="Capacity"/>) of rev-numbered entries fed by every world in the process
/// (<see cref="Attach"/>: main, in-process bodies, in-process hives):
/// <list type="bullet">
/// <item>a block entry per <see cref="WorldState.BlockChanged"/> (block change packets, multi-block changes, local predictions);
/// the same change seen by several bodies sharing a column (same dim, cell and new id+meta within <see cref="DedupeWindow"/>)
/// is one entry;</item>
/// <item>a column entry when a column's data arrives (<c>loaded</c>: a chunk load or a b1.8.1 partial region update, never one
/// entry per block) or the last attached world holding it lets it go (<c>unloaded</c>).</item>
/// </list>
/// It costs nothing while nobody reads: nothing is logged until the first read and after <see cref="IdleAfter"/> without one,
/// and a client whose rev predates such a gap (or the ring's oldest entry) gets <c>full</c> and refetches by index hash.
/// Thread-safe; entries are structs in a preallocated ring (no allocation per change).
/// </summary>
public sealed class BlockDeltaLog
{
    public const string Loaded = "loaded", Unloaded = "unloaded";

    private struct Entry
    {
        public long Rev, AtMs;
        public int Dim, X, Y, Z, Id, Meta;
        public byte Kind; // 0 block, 1 loaded, 2 unloaded
    }

    /// <summary>One page for a client at <c>since</c>. <see cref="Rev"/>: pass it back as the next <c>since</c>.</summary>
    /// <param name="Blocks">Per dimension, <c>[x, y, z, id, meta]</c> in rev order (a later entry for a cell wins).</param>
    /// <param name="Full">The client is too far behind (or new): lists are empty, refetch the columns whose index hash changed.</param>
    /// <param name="More">The page was capped: ask again from <see cref="Rev"/> at once.</param>
    public sealed record Page(long Rev, bool Full, bool More, IReadOnlyDictionary<string, List<int[]>> Blocks, IReadOnlyList<ColumnEvent> Columns);

    public sealed record ColumnEvent(long Rev, long At, int Dim, int Cx, int Cz, string Kind);

    private readonly Entry[] _ring;
    private readonly TimeProvider _time;
    private readonly object _lock = new();
    private readonly Dictionary<long, (int State, long AtMs)> _recentBlocks = new();
    private readonly Dictionary<(int Dim, int Cx, int Cz, byte Kind), long> _recentColumns = new();
    private readonly Dictionary<(int Dim, int Cx, int Cz), long> _changedAt = new();
    private readonly List<WeakReference<WorldState>> _worlds = new();
    private int _head, _count;
    private long _rev, _gap; // _gap: a rev before which this log may have missed changes (idle)
    private long _lastReadMs = long.MinValue / 4; // never read: idle (the first read opens a gap, so its rev is never 0)

    public BlockDeltaLog(int capacity = 20_000, TimeProvider? time = null)
    {
        _ring = new Entry[Math.Max(16, capacity)];
        _time = time ?? TimeProvider.System;
    }

    /// <summary>The process's log (null = no supervisor world routes: attached worlds' handlers return at once).</summary>
    public static BlockDeltaLog? Process { get; set; }

    public int Capacity => _ring.Length;
    /// <summary>Identical changes from several bodies within this window are one entry.</summary>
    public TimeSpan DedupeWindow { get; init; } = TimeSpan.FromSeconds(2);
    /// <summary>No read for this long = stop logging (the next reader starts with <c>full</c>).</summary>
    public TimeSpan IdleAfter { get; init; } = TimeSpan.FromMinutes(2);
    public long Revision { get { lock (_lock) return _rev; } }

    private long NowMs => _time.GetUtcNow().ToUnixTimeMilliseconds();

    // ------------------------------------------------------------------ feeding

    /// <summary>
    /// Feeds the process's log (<see cref="Process"/>, read when each event fires) from one world. Called once per world the
    /// process creates; costs one null check per event while the supervisor is off.
    /// </summary>
    public static void Attach(WorldState world)
    {
        world.BlockChanged += c => Process?.OnBlock(world, c);
        world.ColumnLoaded += (cx, cz) => Process?.OnColumnLoaded(world, cx, cz);
        world.ColumnUnloaded += (cx, cz) => Process?.OnColumnUnloaded(world, cx, cz);
        world.RegionUpdated += (min, size) => Process?.OnRegion(world, min, size);
        Track(world);
    }

    private static readonly List<WeakReference<WorldState>> AllWorlds = new();

    private static void Track(WorldState world)
    {
        lock (AllWorlds)
        {
            AllWorlds.RemoveAll(r => !r.TryGetTarget(out _));
            AllWorlds.Add(new WeakReference<WorldState>(world));
        }
    }

    // Another attached world (main's, a body's, a hive's) still holds the column: its unload in one view isn't the hive's.
    private bool HeldElsewhere(WorldState self, int dim, int cx, int cz)
    {
        lock (AllWorlds)
            foreach (var r in AllWorlds)
                if (r.TryGetTarget(out var w) && !ReferenceEquals(w, self) && w.Dimension == dim && w.GetColumn(cx, cz) is { HasData: true })
                    return true;
        lock (_lock)
            foreach (var r in _worlds)
                if (r.TryGetTarget(out var w) && !ReferenceEquals(w, self) && w.Dimension == dim && w.GetColumn(cx, cz) is { HasData: true })
                    return true;
        return false;
    }

    /// <summary>Feeds THIS log from a world (tests, or a host with its own log; production worlds go through <see cref="Attach"/>).</summary>
    public void Watch(WorldState world)
    {
        world.BlockChanged += c => OnBlock(world, c);
        world.ColumnLoaded += (cx, cz) => OnColumnLoaded(world, cx, cz);
        world.ColumnUnloaded += (cx, cz) => OnColumnUnloaded(world, cx, cz);
        world.RegionUpdated += (min, size) => OnRegion(world, min, size);
        lock (_lock) _worlds.Add(new WeakReference<WorldState>(world));
    }

    private bool Active(long now) => now - _lastReadMs <= (long)IdleAfter.TotalMilliseconds;

    public void OnBlock(WorldState world, BlockChange c)
    {
        long now = NowMs;
        if (!Active(now)) return;
        int dim = world.Dimension;
        var p = c.Pos;
        long key = CellKey(dim, p.X, p.Y, p.Z);
        int state = c.NewId << 4 | (c.NewMeta & 15);
        long window = (long)DedupeWindow.TotalMilliseconds;
        lock (_lock)
        {
            if (_recentBlocks.TryGetValue(key, out var seen) && seen.State == state && now - seen.AtMs <= window) return;
            if (_recentBlocks.Count > 8192) Prune(_recentBlocks, now, window);
            _recentBlocks[key] = (state, now);
            if (_changedAt.Count > 16384) _changedAt.Clear();
            _changedAt[(dim, p.X >> 4, p.Z >> 4)] = now;
            Push(new Entry { AtMs = now, Dim = dim, X = p.X, Y = p.Y, Z = p.Z, Id = c.NewId, Meta = c.NewMeta & 15, Kind = 0 });
        }
    }

    public void OnColumnLoaded(WorldState world, int cx, int cz)
    {
        // b1.8.1 allocates a column empty (pre-chunk) before its data: the entry waits for the data (OnRegion).
        if (!Active(NowMs) || world.GetColumn(cx, cz) is not { HasData: true }) return;
        Column(world.Dimension, cx, cz, 1);
    }

    public void OnColumnUnloaded(WorldState world, int cx, int cz)
    {
        if (!Active(NowMs)) return;
        int dim = world.Dimension;
        if (HeldElsewhere(world, dim, cx, cz)) return;
        Column(dim, cx, cz, 2);
    }

    /// <summary>Chunk data (a load, or a b1.8.1 partial update of a loaded column): one <c>loaded</c> entry per column, no block entries.</summary>
    public void OnRegion(WorldState world, BlockPos min, BlockPos size)
    {
        if (!Active(NowMs) || size.X <= 0 || size.Z <= 0) return;
        int dim = world.Dimension;
        for (int cx = min.X >> 4; cx <= (min.X + size.X - 1) >> 4; cx++)
        for (int cz = min.Z >> 4; cz <= (min.Z + size.Z - 1) >> 4; cz++)
            Column(dim, cx, cz, 1);
    }

    private void Column(int dim, int cx, int cz, byte kind)
    {
        long now = NowMs;
        if (!Active(now)) return;
        long window = (long)DedupeWindow.TotalMilliseconds;
        lock (_lock)
        {
            var key = (dim, cx, cz, kind);
            if (_recentColumns.TryGetValue(key, out var at) && now - at <= window) return;
            if (_recentColumns.Count > 8192)
                foreach (var k in _recentColumns.Where(kv => now - kv.Value > window).Select(kv => kv.Key).ToList()) _recentColumns.Remove(k);
            _recentColumns[key] = now;
            _recentColumns.Remove((dim, cx, cz, kind == 1 ? (byte)2 : (byte)1)); // a reload right after an unload is news again
            if (_changedAt.Count > 16384) _changedAt.Clear();
            _changedAt[(dim, cx, cz)] = now;
            Push(new Entry { AtMs = now, Dim = dim, X = cx, Z = cz, Kind = kind });
        }
    }

    // caller holds _lock
    private void Push(Entry e)
    {
        e.Rev = ++_rev;
        _ring[(_head + _count) % _ring.Length] = e;
        if (_count < _ring.Length) _count++;
        else _head = (_head + 1) % _ring.Length;
    }

    private static void Prune(Dictionary<long, (int State, long AtMs)> d, long now, long window)
    {
        foreach (var k in d.Where(kv => now - kv.Value.AtMs > window).Select(kv => kv.Key).ToList()) d.Remove(k);
        if (d.Count > 8192) d.Clear();
    }

    private static long CellKey(int dim, int x, int y, int z) =>
        ((long)(x & 0x3FFFFFF) << 38) | ((long)(z & 0x3FFFFFF) << 12) | ((long)(y & 0xFF) << 4) | (long)((dim + 1) & 3);

    /// <summary>
    /// True when the column changed (a logged block or column entry) after <paramref name="at"/>: the index's cached live hash is
    /// stale and is recomputed (the hash-diff backstop never lags a delta).
    /// </summary>
    public bool ChangedSince(int dim, int cx, int cz, DateTimeOffset at)
    {
        lock (_lock) return _changedAt.TryGetValue((dim, cx, cz), out var t) && t >= at.ToUnixTimeMilliseconds();
    }

    // ------------------------------------------------------------------ reading

    /// <summary>Entries after <paramref name="since"/> (optionally one dimension), at most <paramref name="max"/>.</summary>
    public Page Since(long since, int? dim, int max)
    {
        long now = NowMs;
        lock (_lock)
        {
            if (!Active(now))
            {
                // Waking from idle: anything before this rev may have been missed.
                _gap = ++_rev;
                _recentBlocks.Clear();
                _recentColumns.Clear();
            }
            _lastReadMs = now;
            long oldest = _count > 0 ? _ring[_head].Rev : _rev + 1;
            if (since <= 0 || since > _rev || since < _gap || since < oldest - 1)
                return new Page(_rev, true, false, new Dictionary<string, List<int[]>>(), []);
            var blocks = new Dictionary<string, List<int[]>>();
            var columns = new List<ColumnEvent>();
            long last = since;
            int n = 0;
            bool more = false;
            // The ring is in rev order (a gap rev has no entry): binary search the first entry after since.
            int lo = 0, hi = _count;
            while (lo < hi)
            {
                int mid = (lo + hi) >> 1;
                if (_ring[(_head + mid) % _ring.Length].Rev <= since) lo = mid + 1; else hi = mid;
            }
            int start = lo;
            for (int i = start; i < _count; i++)
            {
                ref var e = ref _ring[(_head + i) % _ring.Length];
                if (dim is { } d && e.Dim != d) { last = e.Rev; continue; }
                if (n >= max) { more = true; break; }
                if (e.Kind == 0)
                {
                    var key = e.Dim.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    if (!blocks.TryGetValue(key, out var list)) blocks[key] = list = new List<int[]>();
                    list.Add([e.X, e.Y, e.Z, e.Id, e.Meta]);
                }
                else columns.Add(new ColumnEvent(e.Rev, e.AtMs, e.Dim, e.X, e.Z, e.Kind == 1 ? Loaded : Unloaded));
                last = e.Rev;
                n++;
            }
            return new Page(more ? last : _rev, false, more, blocks, columns);
        }
    }
}
