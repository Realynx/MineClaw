using System.Collections.Concurrent;
using System.Diagnostics;
using MineClaw.Agent.Rag;
using MineClaw.World;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MineClaw.Bot.WorldIndex;

/// <summary>
/// Builds the bot's map memory from the client's copy of the world, for zero LLM tokens: containers/stations (with
/// contents when opened), signs as labels, cobblestone generators (confirmed by watching a dug cell refill), farms,
/// trees, islands, player hangouts/activity and unknown player builds — all written as source=auto observations.
/// <para>Threading: world/inventory event handlers only enqueue (O(1) on the network thread); all work runs in
/// <see cref="Pump"/> passes on a background loop (or called directly by tests). Column scans are budgeted per pass, writes
/// are coalesced per (kind, key) and committed in one <c>UpsertMany</c> transaction per pass.</para>
/// <para>Never overwrites taught/llm rows (the store only annotates them) and never calls the LLM.</para>
/// </summary>
public sealed partial class WorldIndexer : IDisposable
{
    private readonly WorldState _world;
    private readonly IObservationSink _sink;
    private readonly IWorldIndexFeed _feed;
    private readonly WorldIndexOptions _o;
    private readonly TimeProvider _time;
    private readonly ILogger _log;
    private readonly string _selfName;
    private readonly object _pumpLock = new();

    // ---- inbound (any thread) ----
    private readonly ConcurrentQueue<(BlockChange Change, DateTimeOffset At)> _changes = new();
    private readonly object _columnsLock = new();
    private readonly HashSet<long> _dirtyColumns = new();
    private readonly ConcurrentQueue<ContainerEvent> _containerEvents = new();
    private int _inventoryVersion;

    // ---- worker state ----
    private readonly Dictionary<string, IndexEntry> _entries = new(StringComparer.Ordinal);
    private readonly HashSet<long> _scannedColumns = new();
    private readonly List<ObservationRow> _pendingClusterRows = new();
    private readonly List<(string Kind, string Key)> _removals = new();
    private bool _protectDirty = true;
    private volatile Dictionary<BlockPos, string> _protected = new();

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private readonly List<Action> _unsubscribe = new();

    /// <param name="dimensions">Which dimension the bot is in (default: polls <see cref="WorldState.Dimension"/>).</param>
    public WorldIndexer(WorldState world, IObservationSink sink, IWorldIndexFeed? feed = null, WorldIndexOptions? options = null,
        string? selfName = null, TimeProvider? time = null, ILogger? log = null, IDimensionSource? dimensions = null)
    {
        _world = world;
        _dims = dimensions ?? new WorldDimensionSource(world);
        _dim = _dims.Dimension;
        _sink = sink;
        _feed = feed ?? new ManualIndexFeed();
        _o = options ?? new WorldIndexOptions();
        _time = time ?? TimeProvider.System;
        _log = log ?? NullLogger.Instance;
        _selfName = selfName ?? "";
        _farms = BlockClass.FarmTypes.ToDictionary(t => t, _ => new ClusterIndex(2));
    }

    public WorldIndexOptions Options => _o;
    public IndexerStats Stats { get; } = new();

    /// <summary>Raised (on the indexer thread) when a new cluster of unusual player-placed blocks matches nothing known.</summary>
    public event Action<UnknownStructure>? UnknownStructureFound;

    /// <summary>Optional gate for <see cref="UnknownStructureFound"/> (structure, dimension): false = known scenery, stay quiet.</summary>
    public Func<UnknownStructure, int, bool>? ShouldAnnounce { get; set; }

    /// <summary>Optional: true when a box is already known from memory (a taught structure), so it isn't "unknown".</summary>
    public Func<BlockPos, BlockPos, bool>? IsKnownArea { get; set; }

    // ------------------------------------------------------------------ lifecycle

    /// <summary>Subscribes to world/inventory events and queues every loaded column for a scan.</summary>
    public WorldIndexer Attach()
    {
        if (_unsubscribe.Count > 0) return this;
        Action<BlockChange> onChange = c => _changes.Enqueue((c, _time.GetUtcNow()));
        Action<BlockPos, BlockPos> onRegion = OnRegion;
        Action<int, int> onUnload = (cx, cz) => { lock (_columnsLock) _dirtyColumns.Remove(ColKey(cx, cz)); };
        Action<Window> onOpen = OnWindowOpened;
        Action<Window> onClose = OnWindowClosed;
        Action onInv = () => Interlocked.Increment(ref _inventoryVersion);
        Action<int> onItems = OnWindowItems;
        _world.BlockChanged += onChange;
        _world.RegionUpdated += onRegion;
        _world.ColumnUnloaded += onUnload;
        _world.Inventory.WindowOpened += onOpen;
        _world.Inventory.WindowClosed += onClose;
        _world.Inventory.Changed += onInv;
        _world.Inventory.WindowItemsSet += onItems;
        Action<int, int> onDim = OnDimensionEvent;
        _dims.DimensionChanged += onDim;
        _unsubscribe.Add(() =>
        {
            _dims.DimensionChanged -= onDim;
            _world.BlockChanged -= onChange;
            _world.RegionUpdated -= onRegion;
            _world.ColumnUnloaded -= onUnload;
            _world.Inventory.WindowOpened -= onOpen;
            _world.Inventory.WindowClosed -= onClose;
            _world.Inventory.Changed -= onInv;
            _world.Inventory.WindowItemsSet -= onItems;
        });
        foreach (var c in _world.Columns)
            if (c.HasData) MarkColumn(c.ChunkX, c.ChunkZ);
        return this;
    }

    /// <summary>Starts the background pass loop.</summary>
    public void Start()
    {
        if (_loop != null || !_o.Enabled) return;
        Attach();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _loop = Task.Run(async () =>
        {
            var interval = TimeSpan.FromMilliseconds(Math.Max(50, _o.PassIntervalMs));
            while (!ct.IsCancellationRequested)
            {
                // A closed container is written at once (shared chest memory for every hive bot), not on the next tick.
                var wake = Volatile.Read(ref _wake);
                try { await Task.WhenAny(Task.Delay(interval, _time, ct), wake.Task).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                if (ct.IsCancellationRequested) break;
                if (wake.Task.IsCompleted) Interlocked.CompareExchange(ref _wake, NewWake(), wake);
                try { Pump(); }
                catch (Exception ex) { PassFailed.Report(ex, _time.GetUtcNow(), _log); }
            }
        }, ct);
    }

    /// <summary>Live 10-27 (L7): a failing pass logged a full stack trace every second. Same failure = one stack, then a count per minute.</summary>
    internal FailureThrottle PassFailed { get; } = new("world indexer pass failed");

    public void Stop()
    {
        _cts?.Cancel();
        try { _loop?.Wait(TimeSpan.FromSeconds(3)); } catch { /* cancelled */ }
        _loop = null;
        _cts?.Dispose();
        _cts = null;
    }

    public void Dispose()
    {
        Stop();
        foreach (var u in _unsubscribe) u();
        _unsubscribe.Clear();
    }

    // ------------------------------------------------------------------ inbound handlers (network thread: enqueue only)

    private static long ColKey(int cx, int cz) => ((long)cx << 32) | (uint)cz;
    private static (int X, int Z) ColOf(long k) => ((int)(k >> 32), (int)(uint)k);

    private void MarkColumn(int cx, int cz) { lock (_columnsLock) _dirtyColumns.Add(ColKey(cx, cz)); }

    private void OnRegion(BlockPos min, BlockPos size)
    {
        int x0 = min.X >> 4, x1 = (min.X + size.X - 1) >> 4, z0 = min.Z >> 4, z1 = (min.Z + size.Z - 1) >> 4;
        lock (_columnsLock)
            for (int cx = x0; cx <= x1; cx++)
            for (int cz = z0; cz <= z1; cz++)
                _dirtyColumns.Add(ColKey(cx, cz));
    }

    private void OnWindowOpened(Window w)
    {
        if (w.Type is not (WindowType.Chest or WindowType.Furnace or WindowType.Dispenser)) return;
        var p = _world.Player.Snapshot;
        _containerEvents.Enqueue(new ContainerEvent(true, w, p.Position + new Vec3d(0, PlayerPhysics.EyeHeight, 0), p.Yaw, p.Pitch,
            _feed.LastOpenedContainer, Volatile.Read(ref _inventoryVersion), null));
    }

    private void OnWindowClosed(Window w)
    {
        if (w.Type is not (WindowType.Chest or WindowType.Furnace or WindowType.Dispenser)) return;
        // Copy now: the window object outlives the close but its player half is shared with window 0.
        var snap = w.ContainerContents().Select(t => t.Item).ToArray();
        _initialContents.TryGetValue(w, out var first);
        _initialContents.Remove(w);
        _containerEvents.Enqueue(new ContainerEvent(false, w, default, 0, 0, null, Volatile.Read(ref _inventoryVersion), snap)
            { Burning = w.IsBurning, Initial = first });
        Volatile.Read(ref _wake).TrySetResult();
    }

    /// <summary>Packet thread: a container's first full contents (0x68) after it opened, before any click of ours changes them.
    /// What the chest REALLY held, so "gone since memory" never counts what the bot itself is about to take.</summary>
    private void OnWindowItems(int windowId)
    {
        if (_world.Inventory.OpenWindow is not { } w || w.Id != windowId) return;
        if (w.Type is not (WindowType.Chest or WindowType.Furnace or WindowType.Dispenser)) return;
        MineClaw.Data.ItemStack[] snap;
        lock (_world.Inventory.SyncRoot) snap = w.ContainerContents().Select(t => t.Item).ToArray();
        _initialContents.TryAdd(w, snap);
    }

    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<Window, MineClaw.Data.ItemStack[]> _initialContents = new();
    private TaskCompletionSource _wake = NewWake();
    private static TaskCompletionSource NewWake() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Raised on the indexer thread when an open shows that items memory listed in a container are gone.</summary>
    public event Action<ContainerVanished>? ContentsVanished;

    private readonly ConcurrentDictionary<BlockPos, ContainerVanished> _lastVanished = new();

    /// <summary>The newest "gone since memory" finding of any container within <paramref name="maxAge"/>.</summary>
    public ContainerVanished? RecentVanished(TimeSpan maxAge)
    {
        var now = _time.GetUtcNow();
        return _lastVanished.Values.Where(v => now - v.At <= maxAge).OrderByDescending(v => v.At).FirstOrDefault();
    }

    /// <summary>The latest "gone since memory" finding for the container at (or one block beside, a double chest) <paramref name="pos"/>.</summary>
    public ContainerVanished? LastVanished(BlockPos pos, TimeSpan? maxAge = null)
    {
        var now = _time.GetUtcNow();
        foreach (var v in _lastVanished.Values)
            if ((v.Pos == pos || (Math.Abs(v.Pos.X - pos.X) + Math.Abs(v.Pos.Z - pos.Z) == 1 && v.Pos.Y == pos.Y))
                && now - v.At <= (maxAge ?? TimeSpan.FromMinutes(2)))
                return v;
        return null;
    }

    private sealed record ContainerEvent(bool Open, Window Window, Vec3d Eye, float Yaw, float Pitch, BlockPos? FeedPos, int InvVersion,
        MineClaw.Data.ItemStack[]? Snapshot)
    {
        public bool Burning { get; init; }
        /// <summary>The window's first full contents (before our clicks), when they arrived.</summary>
        public MineClaw.Data.ItemStack[]? Initial { get; init; }
    }

    // ------------------------------------------------------------------ protection

    /// <summary>
    /// True for blocks memory says must not be broken: containers/stations, generators' lava/water and the walls
    /// holding them, farmland/irrigation/soil, sign posts, unknown player builds. Lock-free (pathfinder inner loop).
    /// </summary>
    public bool IsProtected(BlockPos p, int blockId) => _protected.ContainsKey(p);

    /// <summary>Which indexed thing protects <paramref name="p"/> (its kind: cobble_generator, farm, tree, unknown_structure,
    /// portal, sign, chest...), or null. For refusal texts and telemetry (protection policy: all of these are SOFT).</summary>
    public string? ProtectionKind(BlockPos p) => _protected.GetValueOrDefault(p);

    /// <summary>Protection kind of a placed floor/wall/path cell (not an observation kind).</summary>
    public const string FloorKind = "floor";

    public int ProtectedCount => _protected.Count;

    private void RebuildProtection()
    {
        if (!_protectDirty) return;
        var set = new Dictionary<BlockPos, string>();
        // Specific things first (a generator wall inside an unknown build reads as the generator).
        foreach (var e in _entries.Values.OrderBy(e => e.Kind == IndexKinds.UnknownStructure ? 1 : 0))
            foreach (var p in e.Protect) set.TryAdd(p, e.Kind);
        // Placed floors/walls/paths (cobble, planks, slabs...): soft-protected from the bot's own choices.
        foreach (var p in FloorCells()) set.TryAdd(p, FloorKind);
        // A generator dig cell is never a wall: a dual generator's second cell sits next to the first cell's lava/water.
        foreach (var g in _gens.AllCells()) set.Remove(g);
        _protected = set;
        _confirmedDig = _regens.Where(kv => kv.Value > 0).Select(kv => kv.Key).ToHashSet();
        _protectDirty = false;
    }

    // ------------------------------------------------------------------ queries (any thread; snapshots)

    /// <summary>Unknown structures not yet asked about (the brain asks players, then calls <see cref="MarkAsked"/>).</summary>
    public IReadOnlyList<UnknownStructure> PendingUnknownStructures
    {
        get { lock (_unknownLock) return _pendingUnknown.Values.ToList(); }
    }

    public void MarkAsked(string key)
    {
        lock (_unknownLock) _pendingUnknown.Remove(key);
    }

    private readonly object _unknownLock = new();
    private readonly Dictionary<string, UnknownStructure> _pendingUnknown = new(StringComparer.Ordinal);

    /// <summary>Kinds and counts currently tracked (diagnostics).</summary>
    public Dictionary<string, int> Census()
    {
        lock (_pumpLock) return _entries.Values.GroupBy(e => e.Kind).ToDictionary(g => g.Key, g => g.Count());
    }

    /// <summary>Rendered text of a tracked thing (tests/diagnostics).</summary>
    public string? TextOf(string kind, string key)
    {
        lock (_pumpLock) return _entries.TryGetValue(kind + "|" + key, out var e) ? e.WrittenText ?? Render(e).Text : null;
    }

    public IReadOnlyList<(string Kind, string Key, string Text)> Snapshot()
    {
        lock (_pumpLock) return _entries.Values.Select(e => (e.Kind, e.Key, e.WrittenText ?? Render(e).Text)).ToList();
    }

    // ------------------------------------------------------------------ the pass

    /// <summary>True while work is queued (tests loop <see cref="Pump"/> until idle).</summary>
    public bool HasPendingWork
    {
        get
        {
            lock (_columnsLock) if (_dirtyColumns.Count > 0) return true;
            return !_changes.IsEmpty || !_containerEvents.IsEmpty;
        }
    }

    /// <summary>Runs passes until nothing is queued (bounded).</summary>
    public void PumpAll(int maxPasses = 200)
    {
        for (int i = 0; i < maxPasses; i++)
        {
            Pump();
            if (!HasPendingWork) break;
        }
    }

    /// <summary>One indexing pass: block changes, budgeted column scans, containers, signs, players, clusters, writes.</summary>
    public void Pump()
    {
        lock (_pumpLock)
        {
            var now = _time.GetUtcNow();
            Stats.Passes++;
            CheckDimension(now);
            bool active = _dim is WorldDim.Overworld or WorldDim.Nether;
            int n = 0;
            while (n < 50_000 && _changes.TryDequeue(out var ch))
            {
                n++;
                if (active) HandleChange(ch.Change, ch.At);
            }
            Stats.BlockChanges += n;
            if (active)
            {
                TrackPortals(now);
                ScanDirtyColumns(now);
                HandleContainers(now);
                PollSigns(now);
                SamplePlayers(now);
                FlushActivity(now);
                ReclusterAll(now);
                if (InNether) ApplyHazards();
                AttachLabels();
                ReconcilePending();
            }
            Write(now);
            RebuildProtection();
        }
    }

    // ------------------------------------------------------------------ entries

    private IndexEntry GetOrCreate(string kind, string key, out bool created)
    {
        var id = kind + "|" + key;
        created = !_entries.TryGetValue(id, out var e);
        if (created)
        {
            e = new IndexEntry(kind, key);
            _entries[id] = e;
        }
        return e!;
    }

    private IndexEntry? Find(string kind, string key) => _entries.GetValueOrDefault(kind + "|" + key);

    private void RemoveEntry(IndexEntry e)
    {
        if (!_entries.Remove(e.Id)) return;
        e.Removed = true;
        if (!IndexKinds.HistoryKinds.Contains(e.Kind)) _removals.Add((e.Kind, e.Key));
        if (e.Protect.Count > 0) _protectDirty = true;
        GridRemove(e);
        MarkNear(e);
        if (e.Kind == IndexKinds.UnknownStructure) lock (_unknownLock) _pendingUnknown.Remove(e.Key);
    }

    private void SetProtect(IndexEntry e, HashSet<BlockPos> protect)
    {
        if (e.Protect.SetEquals(protect)) return;
        e.Protect = protect;
        _protectDirty = true;
    }

    // ------------------------------------------------------------------ writes

    private void Write(DateTimeOffset now)
    {
        foreach (var (kind, key) in _removals)
        {
            try { if (_sink.Remove(kind, key, _dim)) Stats.Removes++; }
            catch (Exception ex) { _log.LogDebug(ex, "remove {Kind} {Key} failed", kind, key); }
        }
        _removals.Clear();

        var batch = new List<Observation>();
        var written = new List<(IndexEntry E, string Text, string Fields)>();
        var refresh = TimeSpan.FromMinutes(Math.Max(1, _o.RefreshMinutes));
        foreach (var e in _entries.Values)
        {
            if (!e.Dirty && !e.Seen && !e.ContentsDirty) continue;
            if (!e.Force && e.WrittenAt != default && now - e.WrittenAt < e.MinWriteInterval) continue;
            if (!e.Force && !CanRender(e)) { e.Dirty = false; continue; }
            var (text, fields) = Render(e);
            var bounds = (e.Min, e.Max);
            bool changed = text != e.WrittenText || fields != e.WrittenFields || e.WrittenBounds != bounds || e.ContentsDirty;
            bool touch = e.Seen && now - e.WrittenAt >= refresh;
            e.Dirty = false;
            e.Seen = false;
            if (!changed && !touch) continue;
            batch.Add(new Observation(e.Kind, e.Key, text)
            {
                Bounds = e.Bounds,
                FieldsJson = fields,
                Source = ObservationSource.Auto,
                Confidence = Confidence(e),
                Contents = e.ContentsDirty ? e.Contents : null,
                ContentsAt = e.ContentsDirty ? e.ContentsAt : null,
                ContentsBy = e.ContentsDirty && _selfName.Length > 0 ? _selfName : null,
                Dimension = _dim,
            });
            written.Add((e, text, fields));
        }
        if (batch.Count == 0) return;
        for (int i = 0; i < batch.Count; i += Math.Max(1, _o.MaxBatch))
        {
            var slice = batch.GetRange(i, Math.Min(Math.Max(1, _o.MaxBatch), batch.Count - i));
            try
            {
                _sink.UpsertMany(slice);
                Stats.Upserts += slice.Count;
                Stats.Transactions++;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "world index write failed ({Count} observations)", slice.Count);
                // Leave them dirty: retried next pass.
                foreach (var o in slice) if (Find(o.Kind, o.Key) is { } e) e.Dirty = true;
                return;
            }
        }
        foreach (var (e, text, fields) in written)
        {
            e.WrittenText = text;
            e.WrittenFields = fields;
            e.WrittenBounds = (e.Min, e.Max);
            e.WrittenAt = now;
            e.ContentsDirty = false;
            e.Force = false;
            if (e.Kind == IndexKinds.UnknownStructure && e.Extra.ContainsKey("new"))
            {
                e.Extra.Remove("new");
                var u = new UnknownStructure(e.Key, e.Min, e.Max, text) { Blocks = e.Extra.GetValueOrDefault("blocks") as Dictionary<string, int> };
                lock (_unknownLock) _pendingUnknown[e.Key] = u;
                try
                {
                    // The scenery ledger (persisted) decides: already announced/asked before, or unchanged since last seen = silent.
                    if (ShouldAnnounce?.Invoke(u, _dim) ?? true) UnknownStructureFound?.Invoke(u);
                }
                catch (Exception ex) { _log.LogDebug(ex, "UnknownStructureFound handler failed"); }
            }
        }
    }

    /// <summary>World-derived text needs the area loaded; container snapshots and player facts don't.</summary>
    private bool CanRender(IndexEntry e) =>
        e.Kind is IndexKinds.Hangout or IndexKinds.Activity or IndexKinds.Island or IndexKinds.Hazard || _world.IsLoaded(e.Anchor.X, e.Anchor.Z);

    private static double Confidence(IndexEntry e) => e.Kind switch
    {
        IndexKinds.Generator => e.Extra.GetValueOrDefault("confirmed") is true ? 0.95 : 0.6,
        IndexKinds.UnknownStructure => 0.5,
        IndexKinds.Hangout or IndexKinds.Activity or IndexKinds.Hazard => 0.7,
        _ => 0.9,
    };

    private static double ElapsedMs(long start) => (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
}
