using System.Collections.Concurrent;
using MineClaw.Data;
using MineClaw.World;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MineClaw.Bot.WorldIndex;

/// <summary>Remembered-world settings (bind from "Bot:WorldIndex:Remembered").</summary>
public sealed class RememberedWorldOptions
{
    public bool Enabled { get; set; } = true;
    /// <summary>Changed live columns are written back at most this often (and on unload / shutdown).</summary>
    public int FlushSeconds { get; set; } = 60;
    /// <summary>Total stored size cap; least-recently-seen far-away columns are evicted first.</summary>
    public long MaxBytes { get; set; } = 64L * 1024 * 1024;
    /// <summary>Columns of the current dimension within this many chunks of the bot are never evicted.</summary>
    public int KeepRadiusChunks { get; set; } = 8;
    /// <summary>Remembered columns loaded into the world on arrival (nearest first; decoded lazily).</summary>
    public int MaxRememberedColumns { get; set; } = 2048;
    /// <summary>The arrival burst is over when no new column arrived for this long (then the "since last time" line goes out).</summary>
    public double SettleSeconds { get; set; } = 6;
    /// <summary>Hard end of the arrival window.</summary>
    public double MaxArrivalSeconds { get; set; } = 40;
    public int MaxLineChars { get; set; } = 220;
    public int PassIntervalMs { get; set; } = 1000;
}

/// <summary>What one live column looked like compared with the stored baseline, this visit.</summary>
public enum BaselineState { Unknown, New, Unchanged, Changed }

/// <summary>
/// The remembered world: a compressed, light-free snapshot of every chunk column the bot has seen, per dimension, in the
/// shared db (hash + per-section block summary + last_seen; size-capped with LRU eviction). On arrival (join, respawn,
/// portal) the stored columns of that dimension are loaded into <see cref="WorldState"/> as STALE (planning/answers only),
/// and every live column that arrives is compared with its stored baseline: unchanged = silent; real differences since
/// last seen are summed into ONE line ("since last time: 3 wooden doors appeared on the west side"). Columns are written
/// back on unload, on a timer while they change, and on shutdown — batched, on this component's own pass, never on the
/// network thread. Chunk loads are never block changes: the activity tracker only sees 0x35/0x34 updates.
/// </summary>
public sealed class WorldMemory : IDisposable
{
    private readonly WorldState _world;
    private readonly IWorldMemoryStore _store;
    private readonly RememberedWorldOptions _o;
    private readonly TimeProvider _time;
    private readonly ILogger _log;
    private readonly object _pumpLock = new();

    private readonly ConcurrentQueue<long> _regions = new();
    private readonly ConcurrentDictionary<long, byte> _dirty = new();
    private readonly ConcurrentQueue<ChunkColumn> _dropped = new();
    private readonly List<Action> _unsubscribe = new();

    private bool _started;
    private int _dim;
    private readonly Dictionary<long, BaselineState> _state = new();
    /// <summary>The stored baseline of each column that arrived CHANGED this visit, kept before it is overwritten with the
    /// live column (block-level "was this already here?" for announcements; decoded lazily).</summary>
    private readonly Dictionary<long, ChunkRow> _baseline = new();
    private readonly Dictionary<long, ChunkSnapshot> _baselineDecoded = new();
    private bool _arrivalOpen;
    private bool _arrivedAny;
    private DateTimeOffset _arrivalStart, _arrivalLast, _lastFlush;
    private readonly Dictionary<string, Change> _diff = new(StringComparer.Ordinal);
    private CancellationTokenSource? _cts;
    private Task? _loop;

    private sealed class Change
    {
        public int Added, Removed;
        public double SumX, SumZ;
        public int N;
    }

    public WorldMemory(WorldState world, IWorldMemoryStore store, RememberedWorldOptions? options = null, TimeProvider? time = null, ILogger? log = null)
    {
        _world = world;
        _store = store;
        _o = options ?? new RememberedWorldOptions();
        _time = time ?? TimeProvider.System;
        _log = log ?? NullLogger.Instance;
        _lastFlush = _time.GetUtcNow();
    }

    public IWorldMemoryStore Store => _store;
    public RememberedWorldOptions Options => _o;

    /// <summary>One concise line of real changes since the bot last saw this dimension (raised on the memory pass thread).</summary>
    public event Action<string>? SinceLastTime;

    /// <summary>Stats for tests/diagnostics.</summary>
    public int ColumnsWritten { get; private set; }
    public int ColumnsUnchanged { get; private set; }
    public int ColumnsChanged { get; private set; }
    public int Evicted { get; private set; }

    // ------------------------------------------------------------------ lifecycle

    public WorldMemory Attach()
    {
        if (_unsubscribe.Count > 0) return this;
        Action<BlockPos, BlockPos> onRegion = (min, size) =>
        {
            for (int cx = min.X >> 4; cx <= (min.X + size.X - 1) >> 4; cx++)
            for (int cz = min.Z >> 4; cz <= (min.Z + size.Z - 1) >> 4; cz++)
                _regions.Enqueue(Key(cx, cz));
        };
        Action<BlockChange> onChange = c => _dirty.TryAdd(Key(c.Pos.X >> 4, c.Pos.Z >> 4), 0);
        Action<ChunkColumn> onDrop = c => _dropped.Enqueue(c);
        _world.RegionUpdated += onRegion;
        _world.BlockChanged += onChange;
        _world.ColumnDropped += onDrop;
        _unsubscribe.Add(() =>
        {
            _world.RegionUpdated -= onRegion;
            _world.BlockChanged -= onChange;
            _world.ColumnDropped -= onDrop;
        });
        foreach (var c in _world.Columns) if (c.HasData) _regions.Enqueue(Key(c.ChunkX, c.ChunkZ));
        return this;
    }

    public void Start()
    {
        if (_loop != null || !_o.Enabled) return;
        Attach();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _loop = Task.Run(async () =>
        {
            var interval = TimeSpan.FromMilliseconds(Math.Max(100, _o.PassIntervalMs));
            while (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(interval, _time, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                try { Pump(); }
                catch (Exception ex) { _log.LogWarning(ex, "world memory pass failed"); }
            }
        }, ct);
    }

    public void Dispose()
    {
        _cts?.Cancel();
        try { _loop?.Wait(TimeSpan.FromSeconds(3)); } catch { /* cancelled */ }
        _loop = null;
        try { Flush(all: true); } catch (Exception ex) { _log.LogDebug(ex, "world memory final flush failed"); }
        foreach (var u in _unsubscribe) u();
        _unsubscribe.Clear();
        _cts?.Dispose();
        _cts = null;
    }

    private static long Key(int cx, int cz) => ((long)cx << 32) | (uint)cz;
    private static (int X, int Z) Col(long k) => ((int)(k >> 32), (int)(uint)k);

    /// <summary>
    /// Wires the indexer's "new unknown build" announcements through the scenery ledger: things already announced/asked
    /// (matched fuzzily) or present and unchanged in the baseline stay quiet; only new or materially changed ones speak.
    /// </summary>
    public static void GateAnnouncements(WorldIndexer indexer, WorldMemory memory, SceneryLedger ledger)
    {
        indexer.ShouldAnnounce = (u, dim) =>
        {
            memory.Pump(); // baseline states of the columns that just arrived
            var blocks = u.Blocks ?? new Dictionary<string, int>();
            // Live 10-27 16:33 / 21:20: long-standing builds (gen housing, a cobble+torch build by the cactus farm) were asked
            // about as "new" right after restarts: the classifier flagged them for the first time, the ledger had no row, and
            // their columns' hashes differed (the gen and the cactus farm change every visit). Old = its blocks were already
            // in the remembered world; ours = hive bots placed them. Neither is ever news.
            bool old = memory.UnchangedSinceLastSeen(dim, u.Min, u.Max) || memory.BaselineShare(dim, u.Min, u.Max, blocks) >= OldShare;
            bool ours = memory.SelfPlacedShare(dim, u.Min, u.Max, blocks) >= OursShare;
            return ledger.Observe(dim, IndexKinds.UnknownStructure, u.Min, u.Max, blocks, old || ours) != SceneryLedger.Verdict.Known;
        };
    }

    /// <summary>Share of a build's blocks that already stood there in the remembered world for it to count as old scenery.</summary>
    public const double OldShare = 0.6;
    /// <summary>Share of a build's blocks that hive bots placed for it to count as the bots' own.</summary>
    public const double OursShare = 0.5;

    // ------------------------------------------------------------------ queries

    /// <summary>How live column (cx, cz) of <paramref name="dim"/> compared with the stored baseline this visit.</summary>
    public BaselineState State(int dim, int cx, int cz)
    {
        lock (_pumpLock) return dim == _dim ? _state.GetValueOrDefault(Key(cx, cz)) : BaselineState.Unknown;
    }

    /// <summary>True when every column under the box was in the baseline and arrived unchanged (the thing was there, as
    /// is, last time).</summary>
    public bool UnchangedSinceLastSeen(int dim, BlockPos min, BlockPos max)
    {
        lock (_pumpLock)
        {
            if (dim != _dim) return false;
            for (int cx = min.X >> 4; cx <= max.X >> 4; cx++)
            for (int cz = min.Z >> 4; cz <= max.Z >> 4; cz++)
            {
                var s = _state.GetValueOrDefault(Key(cx, cz));
                if (s != BaselineState.Unchanged) return false;
            }
            return true;
        }
    }

    /// <summary>
    /// Block-level baseline check: the share (0..1) of the build's block counts (<paramref name="blocks"/>: name -> count) that
    /// the remembered world already held inside the box before this visit. Columns that arrived unchanged count from the live
    /// world (identical), changed ones from the kept pre-visit baseline; new/unknown columns contribute nothing.
    /// </summary>
    public double BaselineShare(int dim, BlockPos min, BlockPos max, IReadOnlyDictionary<string, int> blocks)
    {
        int total = blocks.Values.Sum();
        if (total <= 0) return 0;
        lock (_pumpLock)
        {
            if (dim != _dim) return 0;
            var have = new Dictionary<string, int>(StringComparer.Ordinal);
            int y0 = Math.Max(0, min.Y), y1 = Math.Min(ChunkColumn.SizeY - 1, max.Y);
            for (int cx = min.X >> 4; cx <= max.X >> 4; cx++)
            for (int cz = min.Z >> 4; cz <= max.Z >> 4; cz++)
            {
                long k = Key(cx, cz);
                var st = _state.GetValueOrDefault(k);
                ChunkSnapshot? before = null;
                if (st == BaselineState.Changed)
                {
                    if (!_baselineDecoded.TryGetValue(k, out before))
                    {
                        if (!_baseline.TryGetValue(k, out var row)) continue;
                        try { before = ChunkSnapshot.Decode(row.Data); }
                        catch (Exception ex) { _log.LogDebug(ex, "stored column {X},{Z} unreadable", cx, cz); continue; }
                        _baselineDecoded[k] = before;
                    }
                }
                else if (st != BaselineState.Unchanged) continue;
                int xa = Math.Max(min.X, cx * 16), xb = Math.Min(max.X, cx * 16 + 15);
                int za = Math.Max(min.Z, cz * 16), zb = Math.Min(max.Z, cz * 16 + 15);
                for (int x = xa; x <= xb; x++)
                for (int z = za; z <= zb; z++)
                for (int y = y0; y <= y1; y++)
                {
                    int id = before != null ? before.Ids[ChunkColumn.Index(x & 15, y, z & 15)] : _world.GetBlock(x, y, z);
                    if (id == 0 || !Blocks.Exists(id)) continue;
                    var name = Blocks.Get(id).Name;
                    if (blocks.ContainsKey(name)) have[name] = have.GetValueOrDefault(name) + 1;
                }
            }
            int overlap = blocks.Sum(kv => Math.Min(kv.Value, have.GetValueOrDefault(kv.Key)));
            return (double)overlap / total;
        }
    }

    /// <summary>Share (0..1) of the build's blocks in the box that hive bots placed themselves (<see cref="WorldState.Placed"/>).</summary>
    public double SelfPlacedShare(int dim, BlockPos min, BlockPos max, IReadOnlyDictionary<string, int> blocks)
    {
        if (blocks.Count == 0 || _world.Placed.Count == 0) return 0;
        int n = 0, placed = 0;
        int y0 = Math.Max(0, min.Y), y1 = Math.Min(ChunkColumn.SizeY - 1, max.Y);
        for (int x = min.X; x <= max.X; x++)
        for (int z = min.Z; z <= max.Z; z++)
        for (int y = y0; y <= y1; y++)
        {
            int id = _world.GetBlock(x, y, z);
            if (id == 0 || !Blocks.Exists(id) || !blocks.ContainsKey(Blocks.Get(id).Name)) continue;
            n++;
            if (_world.Placed.IsPlaced(new BlockPos(x, y, z), dim, id)) placed++;
        }
        return n == 0 ? 0 : (double)placed / n;
    }

    // ------------------------------------------------------------------ the pass

    public void Pump()
    {
        if (!_o.Enabled) return;
        lock (_pumpLock)
        {
            var now = _time.GetUtcNow();
            var puts = new List<ChunkRow>();
            var touches = new List<(int, int)>();

            // Columns that left the world (unload / dimension change): store their final state. Before an arrival, so the
            // old dimension is written under its own id.
            while (_dropped.TryDequeue(out var gone))
            {
                puts.Add(RowOf(ChunkSnapshot.Capture(gone), gone.Dimension, gone.ChunkX, gone.ChunkZ, now));
                if (gone.Dimension == _dim)
                {
                    _state.Remove(Key(gone.ChunkX, gone.ChunkZ));
                    _baseline.Remove(Key(gone.ChunkX, gone.ChunkZ));
                    _baselineDecoded.Remove(Key(gone.ChunkX, gone.ChunkZ));
                }
                _dirty.TryRemove(Key(gone.ChunkX, gone.ChunkZ), out _);
            }

            if (!_started || _world.Dimension != _dim) Arrive(_world.Dimension, now);

            var seen = new HashSet<long>();
            while (_regions.TryDequeue(out var k))
            {
                if (!seen.Add(k)) continue;
                var (cx, cz) = Col(k);
                var col = _world.GetColumn(cx, cz);
                if (col is not { HasData: true } || col.Dimension != _dim) continue;
                if (_state.ContainsKey(k)) { _dirty.TryAdd(k, 0); continue; } // a later bulk update: a change, flushed on the timer
                var snap = ChunkSnapshot.Capture(col);
                var stored = _store.GetChunk(_dim, cx, cz);
                if (stored == null) { _state[k] = BaselineState.New; puts.Add(RowOf(snap, _dim, cx, cz, now)); }
                else if (stored.Hash == snap.Hash) { _state[k] = BaselineState.Unchanged; touches.Add((cx, cz)); ColumnsUnchanged++; }
                else
                {
                    _state[k] = BaselineState.Changed;
                    _baseline[k] = stored;
                    ColumnsChanged++;
                    if (_arrivalOpen) Diff(stored, snap, cx, cz);
                    puts.Add(RowOf(snap, _dim, cx, cz, now));
                }
                _arrivedAny = true;
                _arrivalLast = now;
            }

            if (_arrivalOpen && _arrivedAny &&
                (now - _arrivalLast >= TimeSpan.FromSeconds(_o.SettleSeconds) || now - _arrivalStart >= TimeSpan.FromSeconds(_o.MaxArrivalSeconds)))
            {
                _arrivalOpen = false;
                if (Describe() is { } line)
                {
                    try { SinceLastTime?.Invoke(line); }
                    catch (Exception ex) { _log.LogDebug(ex, "SinceLastTime handler failed"); }
                }
                _diff.Clear();
            }

            if (now - _lastFlush >= TimeSpan.FromSeconds(Math.Max(1, _o.FlushSeconds))) puts.AddRange(DirtyRows(now));

            Write(puts, touches, now);
        }
    }

    /// <summary>Writes every changed (or, with <paramref name="all"/>, every) live column now.</summary>
    public void Flush(bool all = false)
    {
        lock (_pumpLock)
        {
            var now = _time.GetUtcNow();
            if (all) foreach (var c in _world.Columns) if (c.HasData && c.Dimension == _dim) _dirty.TryAdd(Key(c.ChunkX, c.ChunkZ), 0);
            var puts = new List<ChunkRow>();
            while (_dropped.TryDequeue(out var gone)) puts.Add(RowOf(ChunkSnapshot.Capture(gone), gone.Dimension, gone.ChunkX, gone.ChunkZ, now));
            puts.AddRange(DirtyRows(now));
            Write(puts, [], now);
        }
    }

    /// <summary>
    /// "Send me your view" (hive IPC): writes every changed live column now and stamps every live column as seen now (by this
    /// bot), so main renders this bot's surroundings from the shared db with fresh data and an honest age. Returns the columns.
    /// </summary>
    public int FlushForView()
    {
        Flush();
        var now = _time.GetUtcNow();
        var live = _world.Columns.Where(c => c.HasData && c.Dimension == _dim).Select(c => (c.ChunkX, c.ChunkZ)).ToList();
        try { _store.TouchChunks(_dim, live, now); }
        catch (Exception ex) { _log.LogDebug(ex, "world memory: touch for view failed"); }
        return live.Count;
    }

    private List<ChunkRow> DirtyRows(DateTimeOffset now)
    {
        _lastFlush = now;
        var rows = new List<ChunkRow>();
        foreach (var k in _dirty.Keys.ToList())
        {
            _dirty.TryRemove(k, out _);
            var (cx, cz) = Col(k);
            if (_world.GetColumn(cx, cz) is { HasData: true } col && col.Dimension == _dim)
                rows.Add(RowOf(ChunkSnapshot.Capture(col), _dim, cx, cz, now));
        }
        return rows;
    }

    private void Write(List<ChunkRow> puts, List<(int, int)> touches, DateTimeOffset now)
    {
        if (puts.Count == 0 && touches.Count == 0) return;
        try
        {
            _store.PutChunks(puts);
            _store.TouchChunks(_dim, touches, now);
            ColumnsWritten += puts.Count;
            if (puts.Count > 0)
            {
                var me = _world.Player.Position;
                Evicted += _store.Evict(_o.MaxBytes, _dim, (int)Math.Floor(me.X) >> 4, (int)Math.Floor(me.Z) >> 4, _o.KeepRadiusChunks);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "world memory write failed ({Count} columns)", puts.Count);
            foreach (var r in puts) if (r.Dim == _dim) _dirty.TryAdd(Key(r.X, r.Z), 0); // retried on the next flush
        }
    }

    private static ChunkRow RowOf(ChunkSnapshot s, int dim, int cx, int cz, DateTimeOffset now) =>
        new(dim, cx, cz, s.Hash, s.Summary(), s.Encode(), now);

    /// <summary>A new visit of <paramref name="dim"/>: load what is remembered of it (stale) and open the arrival window.</summary>
    private void Arrive(int dim, DateTimeOffset now)
    {
        _started = true;
        _dim = dim;
        _state.Clear();
        _baseline.Clear();
        _baselineDecoded.Clear();
        _diff.Clear();
        _arrivalOpen = true;
        _arrivedAny = false;
        _arrivalStart = _arrivalLast = now;
        try
        {
            var me = _world.Player.Position;
            int n = 0;
            foreach (var r in _store.Chunks(dim, (int)Math.Floor(me.X) >> 4, (int)Math.Floor(me.Z) >> 4, Math.Max(0, _o.MaxRememberedColumns)))
            {
                var data = r.Data;
                if (_world.Remember(new RememberedColumn(r.X, r.Z, r.Dim, r.LastSeen, () => ChunkSnapshot.Decode(data)))) n++;
            }
            if (n > 0) _log.LogDebug("world memory: {Count} remembered column(s) of dimension {Dim} loaded (stale)", n, dim);
        }
        catch (Exception ex) { _log.LogWarning(ex, "world memory: loading remembered columns failed"); }
    }

    // ------------------------------------------------------------------ diffs

    /// <summary>Blocks whose appearing/vanishing is the world ticking, not news: fluids, fire, leaves (decay), grass/snow cover.</summary>
    private static bool Noise(int id) => id is 0 || Blocks.IsWater(id) || Blocks.IsLava(id) || id is Blocks.Fire or Blocks.Leaves or 31 or 32 or 78 or 79;

    /// <summary>Ids that count as the same thing (lit/unlit furnace, grass/dirt/farmland, redstone ore glow).</summary>
    private static int Canon(int id) => id switch
    {
        Blocks.LitFurnace => Blocks.Furnace,
        Blocks.Grass or Blocks.Farmland => Blocks.Dirt,
        74 => 73,
        76 => 75, // redstone torch on/off
        94 => 93, // repeater on/off
        _ => id,
    };

    /// <summary>Two-block things counted once (the lower door half, the bed's foot).</summary>
    private static bool SecondHalf(int id, int meta) => id is Blocks.WoodenDoor or 71 or 26 && (meta & 8) != 0;

    private void Diff(ChunkRow stored, ChunkSnapshot now, int cx, int cz)
    {
        ChunkSnapshot before;
        try { before = ChunkSnapshot.Decode(stored.Data); }
        catch (Exception ex) { _log.LogDebug(ex, "stored column {X},{Z} unreadable", cx, cz); return; }
        for (int lx = 0; lx < 16; lx++)
        for (int lz = 0; lz < 16; lz++)
        {
            int b = ChunkColumn.Index(lx, 0, lz);
            for (int y = 0; y < ChunkColumn.SizeY; y++)
            {
                int i = b + y;
                int was = before.Ids[i], isId = now.Ids[i];
                if (was == isId) continue;
                int cw = Canon(was), ci = Canon(isId);
                if (cw == ci) continue;
                double x = cx * 16 + lx + 0.5, z = cz * 16 + lz + 0.5;
                if (!Noise(was) && !SecondHalf(was, before.Meta[i])) Note(was, removed: true, x, z);
                if (!Noise(isId) && !SecondHalf(isId, now.Meta[i])) Note(isId, removed: false, x, z);
            }
        }
    }

    private void Note(int id, bool removed, double x, double z)
    {
        var name = Blocks.Get(Canon(id)).Name;
        if (!_diff.TryGetValue(name, out var c)) _diff[name] = c = new Change();
        if (removed) c.Removed++; else c.Added++;
        c.SumX += x; c.SumZ += z; c.N++;
    }

    /// <summary>"since last time: 3 wooden doors appeared on the west side; 12 cobblestone gone near spawn".</summary>
    private string? Describe()
    {
        var parts = new List<(int Weight, string Text)>();
        var spawn = _world.Spawn;
        foreach (var (name, c) in _diff)
        {
            int net = c.Added - c.Removed;
            if (net == 0) continue; // moved around in place (e.g. a door re-hung): not news
            double mx = c.SumX / c.N - (spawn.X + 0.5), mz = c.SumZ / c.N - (spawn.Z + 0.5);
            string where = Math.Sqrt(mx * mx + mz * mz) < 6 ? "near spawn" : $"on the {Side(mx, mz)} side";
            string label = name.Replace('_', ' ');
            int n = Math.Abs(net);
            parts.Add((n, net > 0 ? $"{n} {Plural(label, n)} appeared {where}" : $"{n} {Plural(label, n)} gone {where}"));
        }
        if (parts.Count == 0) return null;
        var text = "since last time: " + string.Join("; ", parts.OrderByDescending(p => p.Weight).Take(4).Select(p => p.Text));
        if (parts.Count > 4) text += $"; +{parts.Count - 4} more changes";
        return text.Length <= _o.MaxLineChars ? text : text[..(_o.MaxLineChars - 3)] + "...";
    }

    private static string Side(double dx, double dz)
    {
        // Minecraft: -z is north, +x is east.
        double a = Math.Atan2(dx, -dz) * 180 / Math.PI;
        if (a < 0) a += 360;
        string[] names = ["north", "north-east", "east", "south-east", "south", "south-west", "west", "north-west"];
        return names[(int)Math.Round(a / 45) % 8];
    }

    private static string Plural(string label, int n) =>
        n == 1 || label.EndsWith('s') || label is "cobblestone" or "stone" or "dirt" or "sand" or "gravel" or "glass" or "wool" or "obsidian" or "netherrack" or "glowstone" or "soul sand"
            ? label : label + "s";
}
