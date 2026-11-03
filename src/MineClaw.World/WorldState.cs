using System.Collections.Concurrent;
using MineClaw.Data;
using MineClaw.World.Chunks;

namespace MineClaw.World;

/// <summary>A single block change (for <see cref="WorldState.BlockChanged"/>). Old values are as this world last saw them
/// (see <see cref="WorldState.BlockChanged"/> for a column shared through a <see cref="SharedChunkStore"/>).</summary>
public readonly record struct BlockChange(BlockPos Pos, int OldId, int OldMeta, int NewId, int NewMeta);

/// <summary>A block found by a world query.</summary>
public readonly record struct FoundBlock(BlockPos Pos, int Id, int Meta, double DistanceSquared);

/// <summary>
/// The client's view of the world: loaded chunk columns plus world-level state (time, spawn, dimension),
/// and the entity tracker, own player state and inventory model.
/// <para>Threading: the network thread mutates (ApplyChunkRegion/SetBlock/LoadColumn...), tick and agent threads read.
/// Single-block reads are lock-free; writers lock per column. Events fire synchronously on the mutating thread,
/// after the change is visible — handlers must be quick and must not block.</para>
/// <para>Shared columns: built with a <see cref="SharedChunkStore"/>, the world keeps only its own set of loaded keys (its view:
/// <see cref="GetColumn"/>, <see cref="IsLoaded"/>, <see cref="Columns"/>, <see cref="LoadedColumnCount"/> and the column events
/// answer for what THIS connection loaded) and takes the column objects from the store, so in-process bodies near each other
/// hold one copy of each column. Every body's connection sends the same server data; applying it twice is idempotent. Light
/// updates of all worlds on one store serialize on one lock (a change is relit once, by the world that wrote it first). A body
/// whose connection lags may briefly write older data (a chunk, a block change) over what another body already applied; its
/// own later packets put it right again.</para>
/// </summary>
public sealed class WorldState : IBlockAccess, IDisposable
{
    /// <summary>The b1.8.1 world height (128). Consumers outside the world model still clip to it; on a 256-high (1.12.2) world
    /// they see the lower half only until multi-version P3/P6 move them to <see cref="WorldHeight"/>. The world model itself
    /// (storage, block reads/writes, chunk regions, light) uses <see cref="WorldHeight"/>.</summary>
    public const int Height = ChunkColumn.SizeY;

    private readonly ConcurrentDictionary<long, ChunkColumn> _columns = new();
    private readonly Func<int, BlockInfo> _lookup;
    private readonly SharedChunkStore? _store;
    /// <summary>Store mode: serializes this view's acquire/release against its own load/unload.</summary>
    private readonly object _viewLock = new();
    /// <summary>Store mode: cells of shared columns another world changed since this one last saw them → what this world last
    /// saw there (<c>id &lt;&lt; 4 | meta</c>), so its own copy of the change still reports the right old block. Written under
    /// the column's <see cref="ChunkColumn.Sync"/>.</summary>
    internal readonly ConcurrentDictionary<BlockPos, int> Told = new();

    /// <param name="blockLookup">Block property source; defaults to <see cref="Blocks.Get"/>. Tests inject their own table.</param>
    /// <param name="height">Blocks per column: 128 (b1.8.1, the default) or 256 (1.12.2: the profile's
    /// <c>GameVersion.WorldHeight</c>).</param>
    /// <param name="inventory">The inventory model (b1.8.1 by default; 1.12.2 adds the off hand and its click rules).</param>
    /// <param name="physics">The version's movement and collision rules (b1.8.1 by default): what <see cref="IBlockAccess.Physics"/>
    /// answers, so shapes, simulators and the pathfinder follow the version.</param>
    /// <param name="chunkStore">The process's shared column store (in-process bodies), or null (the default): columns private to
    /// this world, as before. The store's height must match <paramref name="height"/>.</param>
    public WorldState(Func<int, BlockInfo>? blockLookup = null, int height = ChunkColumn.SizeY, InventoryModel? inventory = null,
        Versioning.IPhysicsRules? physics = null, SharedChunkStore? chunkStore = null)
    {
        Physics = physics ?? Versioning.Beta181PhysicsRules.Instance;
        if (height is not (ChunkColumn.SizeY or ChunkColumn.MaxHeight)) throw new ArgumentOutOfRangeException(nameof(height), height, "world height is 128 or 256");
        _lookup = blockLookup ?? Blocks.Get;
        WorldHeight = height;
        chunkStore?.Bind(height);
        _store = chunkStore;
        Light = new LightEngine(this);
        Inventory = new InventoryManager(model: inventory);
        ColumnUnloaded += (cx, cz) => BlockEntities.DropColumn(cx, cz);
    }

    /// <summary>Blocks per column of this world (128 b1.8.1, 256 1.12.2).</summary>
    public int WorldHeight { get; }

    /// <summary>The shared column store this world takes its columns from, or null (private columns).</summary>
    public SharedChunkStore? ChunkStore => _store;

    /// <summary>The version's movement and collision rules (see the constructor).</summary>
    public Versioning.IPhysicsRules Physics { get; }

    /// <summary>The b1.8.1 light engine: the server's light arrays kept up to date on every block change (mc-dev).</summary>
    public LightEngine Light { get; }

    public EntityTracker Entities { get; } = new();
    /// <summary>The block entities this connection described (the viewer's signs, beds, banners, skulls, pots, spawners…).</summary>
    public BlockEntityStore BlockEntities { get; } = new();
    public PlayerState Player { get; } = new();
    public InventoryManager Inventory { get; }
    /// <summary>Cells the bot itself just dug/placed (attribution: the bot's own edits are never a player's).</summary>
    public SelfEditLog SelfEdits { get; } = new();
    /// <summary>Explosions just reported (the blocks they took are the world's doing, not a player's: design drift).</summary>
    public BlastLog Blasts { get; } = new();
    /// <summary>Blocks hive bots placed themselves and that still stand (protection rule 1: always minable by the bot).</summary>
    public PlacedBlockLog Placed { get; } = new();
    /// <summary>Recent chop_tree runs (felled/left logs): a remnant they left is the bot's own unfinished tree. In-process
    /// bodies share main's.</summary>
    public ChopLog Chops { get; set; } = new();
    /// <summary>Cells dug designs say must stay air (stair shafts, mine rooms, corridors): never bridged, pillared, boxed or sealed
    /// into. In-process bodies share main's.</summary>
    public KeepAirLog KeepAir { get; set; } = new();
    /// <summary>Learned trigger → effect links (buttons, levers, plates → doors, pistons, hidden walls), per dimension.</summary>
    public MechanismGraph Mechanisms { get; } = new();

    // ---- world-level state ----
    private long _time;
    /// <summary>World time in ticks (0x04 Time Update). 0 = sunrise, 6000 noon, 12000 sunset, 18000 midnight.</summary>
    public long Time { get => Interlocked.Read(ref _time); set => Interlocked.Exchange(ref _time, value); }
    public long TimeOfDay => ((Time % 24000) + 24000) % 24000;
    public bool IsNight => TimeOfDay is >= 13000 and < 23000;

    private BlockPos _spawn;
    /// <summary>The world spawn point (0x06). Hostile mobs never spawn within 24 blocks of it (SpawnerCreature).</summary>
    public BlockPos Spawn { get => _spawn; set { _spawn = value; HasSpawn = true; } }
    /// <summary>True once <see cref="Spawn"/> was set (the server sent 0x06).</summary>
    public bool HasSpawn { get; private set; }
    /// <summary>Rain strength 0..1 (0x46 begin/end rain; 1.12.2 fades it with Change Game State 7); darkens the sky a little (World.k).</summary>
    public float Rain { get; set; }
    /// <summary>Thunder strength 0..1 (1.12.2 Change Game State 8; b1.8.1 has no packet: a lightning bolt while it rains sets 1).
    /// Counts only while it rains (World.getThunderStrength × rain): a storm takes 3 more off the noon sky light.</summary>
    public float Thunder { get; set; }
    /// <summary>0 overworld, -1 nether.</summary>
    public int Dimension { get; set; }
    public long Seed { get; set; }
    public int MaxPlayers { get; set; }

    // ---- events ----
    /// <summary>
    /// A block of a loaded column changed (<see cref="SetBlock"/>: a block change packet or local prediction). Never raised
    /// when nothing changed from this world's point of view (old = new).
    /// <para>Shared columns (<see cref="SharedChunkStore"/>): raised on each world when ITS connection applies the change, with
    /// the old block as that world last saw it. When another body already wrote the same change into the shared column, this
    /// world's copy finds the new block in place but still raises old → new (the world remembers what it last saw at cells
    /// other worlds changed), so indexers and the placement ledger miss nothing; a later identical copy raises nothing. A body
    /// never gets another body's change raised on its own handlers: <see cref="GetBlock(int,int,int)"/> shows it at once, the
    /// event waits for its own server's packet (another body's local prediction its server never confirms raises nothing).</para>
    /// </summary>
    public event Action<BlockChange>? BlockChanged;
    /// <summary>Raised after a bulk region update, with the region's min corner and size.</summary>
    public event Action<BlockPos, BlockPos>? RegionUpdated;
    public event Action<int, int>? ColumnLoaded;
    public event Action<int, int>? ColumnUnloaded;

    public BlockInfo Info(int id) => _lookup(id);

    private static long Key(int cx, int cz) => ((long)cx << 32) | (uint)cz;

    public ChunkColumn? GetColumn(int chunkX, int chunkZ) => _columns.GetValueOrDefault(Key(chunkX, chunkZ));

    public IReadOnlyCollection<ChunkColumn> Columns => _columns.Values.ToArray();

    public int LoadedColumnCount => _columns.Count;

    private ChunkColumn? ColumnAt(int x, int z) => _columns.GetValueOrDefault(Key(x >> 4, z >> 4));

    internal ChunkColumn? ColumnAtRaw(int x, int z) => _columns.GetValueOrDefault(Key(x >> 4, z >> 4));

    private ChunkColumn GetOrCreate(int cx, int cz, out bool created)
    {
        long key = Key(cx, cz);
        if (_store is null)
        {
            bool c = false;
            var col = _columns.GetOrAdd(key, _ => { c = true; return new ChunkColumn(cx, cz, WorldHeight) { Dimension = Dimension }; });
            created = c;
            return col;
        }
        created = false;
        if (_columns.TryGetValue(key, out var have)) return have;
        lock (_viewLock)
        {
            if (_columns.TryGetValue(key, out have)) return have;
            var col = _store.Acquire(this, Dimension, cx, cz);
            _columns[key] = col;
            created = true;
            return col;
        }
    }

    /// <summary>Removes a column from this view (and, in store mode, gives it back to the store).</summary>
    private bool TryRemoveColumn(long key, out ChunkColumn gone)
    {
        if (_store is null) return _columns.TryRemove(key, out gone!);
        lock (_viewLock)
        {
            if (!_columns.TryRemove(key, out gone!)) return false;
            _store.Release(this, gone);
            return true;
        }
    }

    // ---- remembered world (stale, from an earlier visit; never live) ----

    private readonly ConcurrentDictionary<long, RememberedColumn> _remembered = new();

    /// <summary>Raised after a live column was removed (unload, dimension change), with the removed column (its data is
    /// final): the remembered-world store snapshots it.</summary>
    public event Action<ChunkColumn>? ColumnDropped;

    /// <summary>
    /// Adds a remembered column of the current dimension. Ignored when a live column with data is there (live always wins)
    /// or the column belongs to another dimension. Remembered data is for planning/answers only: <see cref="GetBlock(int,int,int)"/>,
    /// <see cref="IsLoaded"/> and everything movement/safety use stay live-only.
    /// </summary>
    public bool Remember(RememberedColumn column)
    {
        if (column.Dimension != Dimension) return false;
        if (GetColumn(column.ChunkX, column.ChunkZ) is { HasData: true }) return false;
        _remembered[Key(column.ChunkX, column.ChunkZ)] = column;
        return true;
    }

    public RememberedColumn? GetRemembered(int chunkX, int chunkZ) => _remembered.GetValueOrDefault(Key(chunkX, chunkZ));

    public int RememberedCount => _remembered.Count;

    /// <summary>True when block column (x, z) is only known from memory (no live data yet).</summary>
    public bool IsStale(int x, int z) => !IsLoaded(x, z) && _remembered.ContainsKey(Key(x >> 4, z >> 4));

    /// <summary>When the remembered column holding (x, z) was last seen live, or null (live or unknown).</summary>
    public DateTimeOffset? RememberedAt(int x, int z) => IsLoaded(x, z) ? null : _remembered.GetValueOrDefault(Key(x >> 4, z >> 4))?.LastSeen;

    /// <summary>Drops every remembered column (dimension change; the store reloads the new dimension's).</summary>
    public void ForgetRemembered() => _remembered.Clear();

    private RememberedView? _planning;

    /// <summary>
    /// Live blocks where loaded, remembered blocks elsewhere — for route/size ESTIMATES before live chunks arrive (after a
    /// portal, answering about the other dimension). Never use it for stepping, jumping or EdgeGuard decisions.
    /// </summary>
    public IBlockAccess PlanningView => _planning ??= new RememberedView(this);

    /// <summary>Live here, else live in another body's view of the same <see cref="ChunkStore"/> (the crew's combined sight:
    /// live 10-29 a hive brain's own body saw 1 tree where its 20 bodies stood among dozens), else remembered.</summary>
    private sealed class RememberedView(WorldState w) : IBlockAccess
    {
        private ChunkColumn? Crew(int x, int z) => w._store?.Peek(w.Dimension, x >> 4, z >> 4);

        public int GetBlock(int x, int y, int z)
        {
            if (w.IsLoaded(x, z)) return w.GetBlock(x, y, z);
            if ((uint)y < w.WorldHeight && Crew(x, z) is { } c) return c.GetBlock(x & 15, y, z & 15);
            return w._remembered.TryGetValue(Key(x >> 4, z >> 4), out var r) ? r.GetBlock(x & 15, y, z & 15) : 0;
        }

        public int GetMeta(int x, int y, int z)
        {
            if (w.IsLoaded(x, z)) return w.GetMeta(x, y, z);
            if ((uint)y < w.WorldHeight && Crew(x, z) is { } c) return c.GetMeta(x & 15, y, z & 15);
            return w._remembered.TryGetValue(Key(x >> 4, z >> 4), out var r) ? r.GetMeta(x & 15, y, z & 15) : 0;
        }

        public bool IsLoaded(int x, int z) => w.IsLoaded(x, z) || Crew(x, z) != null || w._remembered.ContainsKey(Key(x >> 4, z >> 4));

        public BlockInfo Info(int id) => w.Info(id);

        public int WorldHeight => w.WorldHeight;

        public Versioning.IPhysicsRules Physics => w.Physics;
    }

    // ---- column lifecycle (0x32 Pre-Chunk) ----

    /// <summary>0x32 with mode=true: allocate an empty column (data follows in 0x33).</summary>
    public void LoadColumn(int chunkX, int chunkZ)
    {
        GetOrCreate(chunkX, chunkZ, out bool created);
        if (created) ColumnLoaded?.Invoke(chunkX, chunkZ);
    }

    /// <summary>0x32 with mode=false: forget a column.</summary>
    public void UnloadColumn(int chunkX, int chunkZ)
    {
        if (TryRemoveColumn(Key(chunkX, chunkZ), out var gone))
        {
            ColumnUnloaded?.Invoke(chunkX, chunkZ);
            if (gone.HasData) ColumnDropped?.Invoke(gone);
        }
    }

    /// <summary>Drops every column (respawn / dimension change), remembered ones included.</summary>
    public void Clear()
    {
        foreach (var k in _columns.Keys.ToArray())
            if (TryRemoveColumn(k, out var c))
            {
                ColumnUnloaded?.Invoke(c.ChunkX, c.ChunkZ);
                if (c.HasData) ColumnDropped?.Invoke(c);
            }
        _remembered.Clear();
    }

    /// <summary>With a <see cref="ChunkStore"/>: gives every column of this view back to the store, silently (no unload events);
    /// the view is empty afterwards. Without one there is nothing to release (the columns stay readable).</summary>
    public void Dispose()
    {
        if (_store is null) return;
        foreach (var k in _columns.Keys.ToArray()) TryRemoveColumn(k, out _);
    }

    /// <summary>True when the column containing block (x, z) exists and has received block data.</summary>
    public bool IsLoaded(int x, int z) => ColumnAt(x, z) is { HasData: true };

    // ---- block access ----

    public int GetBlock(int x, int y, int z)
    {
        if ((uint)y >= WorldHeight) return 0;
        var c = ColumnAt(x, z);
        return c is null ? 0 : c.GetBlock(x & 15, y, z & 15);
    }

    public int GetMeta(int x, int y, int z)
    {
        if ((uint)y >= WorldHeight) return 0;
        var c = ColumnAt(x, z);
        return c is null ? 0 : c.GetMeta(x & 15, y, z & 15);
    }

    public int GetBlockLight(int x, int y, int z)
    {
        if ((uint)y >= WorldHeight) return 0;
        var c = ColumnAt(x, z);
        return c is null ? 0 : c.GetBlockLight(x & 15, y, z & 15);
    }

    public int GetSkyLight(int x, int y, int z)
    {
        if ((uint)y >= WorldHeight) return 15;
        var c = ColumnAt(x, z);
        return c is null ? 15 : c.GetSkyLight(x & 15, y, z & 15);
    }

    /// <summary>Raw sky and block light of a cell (live columns; unloaded = 0, 0).</summary>
    public LightLevel LightAt(BlockPos p) => new(Light.Get(true, p.X, p.Y, p.Z), Light.Get(false, p.X, p.Y, p.Z));

    /// <summary>World.k (skylightSubtracted) at <paramref name="timeOfDay"/> with the current rain: 0 by day, 11 at night.</summary>
    public int SkylightSubtracted(long? timeOfDay = null) => LightEngine.SkylightSubtracted(timeOfDay ?? TimeOfDay, Rain, Thunder);

    /// <summary>getLightLevel at <paramref name="timeOfDay"/> (default now): max(sky − World.k, block), the slab/stairs/farmland
    /// neighbour rule included. What the mob spawn check reads.</summary>
    public int EffectiveLight(BlockPos p, long? timeOfDay = null) => Light.LightLevel(p.X, p.Y, p.Z, SkylightSubtracted(timeOfDay));

    /// <summary>
    /// True when a hostile mob may spawn with its feet in <paramref name="p"/> (mc-dev SpawnerCreature + EntityMonster +
    /// EntityLiving, see <see cref="SpawnRules"/>). <paramref name="timeOfDay"/> null = after dark (World.k 11, the worst case
    /// torches must beat); pass <see cref="TimeOfDay"/> for "right now".
    /// </summary>
    public bool CanHostileSpawn(BlockPos p, long? timeOfDay = null) =>
        SpawnRules.Check(this, p, timeOfDay is { } t ? SkylightSubtracted(t) : LightEngine.NightSubtracted) == SpawnBlock.Spawnable;

    /// <summary>The biome id at block column (x, z) (1.12.2 chunk data), or −1: not loaded, or a version whose chunks carry no
    /// biomes (b1.8.1).</summary>
    public int GetBiome(int x, int z) => ColumnAt(x, z) is { HasData: true } c ? c.GetBiome(x & 15, z & 15) : -1;

    /// <summary>Stores a full chunk's 256 biome ids (index <c>z · 16 + x</c>); a column that doesn't exist yet is created.</summary>
    public void SetBiomes(int chunkX, int chunkZ, byte[] biomes)
    {
        if (biomes.Length < 256) return;
        var col = GetOrCreate(chunkX, chunkZ, out _);
        col.Biomes = biomes.AsSpan(0, 256).ToArray();
    }

    public int GetBlock(BlockPos p) => GetBlock(p.X, p.Y, p.Z);
    public int GetMeta(BlockPos p) => GetMeta(p.X, p.Y, p.Z);
    public BlockInfo GetInfo(BlockPos p) => _lookup(GetBlock(p));

    /// <summary>Sets one block (0x35 Block Change / 0x34 Multi Block Change entries, or local prediction).
    /// Ignored when y is out of range or the column isn't loaded. Raises <see cref="BlockChanged"/> (shared columns: see there).</summary>
    public bool SetBlock(int x, int y, int z, int id, int meta)
    {
        if ((uint)y >= WorldHeight) return false;
        var c = ColumnAt(x, z);
        if (c is null) return false;
        meta &= 0x0F;
        int oldId, oldMeta, seenId, seenMeta;
        lock (c.Sync)
        {
            int lx = x & 15, lz = z & 15;
            oldId = c.GetBlock(lx, y, lz);
            oldMeta = c.GetMeta(lx, y, lz);
            c.Set(lx, y, lz, id, meta);
            seenId = oldId;
            seenMeta = oldMeta;
            if (c.SharedHolders is { } holders)
            {
                var p = new BlockPos(x, y, z);
                if (!Told.IsEmpty && Told.TryRemove(p, out int t)) { seenId = t >> 4; seenMeta = t & 15; }
                int now = (byte)id << 4 | meta;
                if (now != (oldId << 4 | oldMeta))
                    foreach (var h in holders)
                    {
                        if (ReferenceEquals(h, this)) continue;
                        // Keep what the other world saw first; forget it once the cell is back to that (a reverted prediction).
                        if (h.Told.TryGetValue(p, out int theirs)) { if (theirs == now) h.Told.TryRemove(p, out _); }
                        else h.Told[p] = oldId << 4 | oldMeta;
                    }
            }
        }
        if (oldId != (byte)id) Light.OnBlockChanged(x, y, z, oldId, id);
        if (seenId != (byte)id || seenMeta != meta)
            BlockChanged?.Invoke(new BlockChange(new BlockPos(x, y, z), seenId, seenMeta, id, meta));
        return true;
    }

    /// <summary>Store mode: forgets what this world was told about other worlds' changes in one x/z column of cells (it just got
    /// their data). Callers hold the chunk column's <see cref="ChunkColumn.Sync"/>.</summary>
    private void ForgetTold(int x, int z, int y0, int y1)
    {
        foreach (var p in Told.Keys)
            if (p.X == x && p.Z == z && p.Y >= y0 && p.Y <= y1) Told.TryRemove(p, out _);
    }

    /// <summary>Store mode: forgets the <see cref="Told"/> entries of one chunk column (it left this view). Callers hold its
    /// <see cref="ChunkColumn.Sync"/>.</summary>
    internal void ForgetTold(int chunkX, int chunkZ)
    {
        if (Told.IsEmpty) return;
        foreach (var p in Told.Keys)
            if (p.X >> 4 == chunkX && p.Z >> 4 == chunkZ) Told.TryRemove(p, out _);
    }

    /// <summary>
    /// Applies an unpacked 0x33 Map Chunk region. Arrays are one byte per block (nibbles already unpacked),
    /// indexed <c>(dx * sizeZ + dz) * sizeY + dy</c>. Regions may be partial or span several columns; missing
    /// columns are created. <paramref name="meta"/>/<paramref name="blockLight"/>/<paramref name="skyLight"/> may be null.
    /// </summary>
    public void ApplyChunkRegion(int originX, int originY, int originZ, int sizeX, int sizeY, int sizeZ,
        byte[] ids, byte[]? meta, byte[]? blockLight, byte[]? skyLight)
    {
        int volume = sizeX * sizeY * sizeZ;
        if (sizeX <= 0 || sizeY <= 0 || sizeZ <= 0) return;
        if (ids.Length < volume) throw new ArgumentException($"ids has {ids.Length} entries, region needs {volume}");

        var newColumns = new List<(int, int)>();
        // Iterate column by column so each chunk column is locked once per (dx,dz) run.
        for (int dx = 0; dx < sizeX; dx++)
        {
            int x = originX + dx;
            for (int dz = 0; dz < sizeZ; dz++)
            {
                int z = originZ + dz;
                var col = GetOrCreate(x >> 4, z >> 4, out bool created);
                if (created) newColumns.Add((x >> 4, z >> 4));
                int src = (dx * sizeZ + dz) * sizeY;
                // The part of the y run inside the world.
                int d0 = Math.Max(0, -originY), d1 = Math.Min(sizeY, WorldHeight - originY);
                lock (col.Sync)
                {
                    if (d1 > d0)
                    {
                        int s0 = src + d0, n = d1 - d0;
                        col.WriteRun(x & 15, z & 15, originY + d0, ids.AsSpan(s0, n), Run(meta, s0, n), Run(blockLight, s0, n), Run(skyLight, s0, n));
                        if (!Told.IsEmpty) ForgetTold(x, z, originY + d0, originY + d1 - 1);
                    }
                    col.HasData = true;
                }
            }
        }
        // Live data replaces what was remembered of these columns.
        if (!_remembered.IsEmpty)
            for (int cx = originX >> 4; cx <= (originX + sizeX - 1) >> 4; cx++)
            for (int cz = originZ >> 4; cz <= (originZ + sizeZ - 1) >> 4; cz++)
                _remembered.TryRemove(Key(cx, cz), out _);
        Light.OnRegion(originX, originZ, sizeX, sizeZ, hasSky: skyLight != null, hasBlock: blockLight != null);
        foreach (var (cx, cz) in newColumns) ColumnLoaded?.Invoke(cx, cz);
        RegionUpdated?.Invoke(new BlockPos(originX, originY, originZ), new BlockPos(sizeX, sizeY, sizeZ));
    }

    /// <summary>The part of <paramref name="a"/> from <paramref name="start"/>, at most <paramref name="n"/> long (shorter or empty
    /// when the array is: the cells past its end take the layer's default).</summary>
    private static ReadOnlySpan<byte> Run(byte[]? a, int start, int n) =>
        a is null || start >= a.Length ? default : a.AsSpan(start, Math.Min(n, a.Length - start));

    // ---- queries ----

    /// <summary>Finds blocks matching <paramref name="match"/> within a sphere, nearest first.</summary>
    public List<FoundBlock> FindBlocks(Vec3d center, double radius, Func<int, int, bool> match, int maxResults = 64) =>
        FindBlocks(center, radius, match, null, maxResults);

    /// <summary>Like <see cref="FindBlocks(Vec3d, double, Func{int, int, bool}, int)"/>, but only cells that also pass
    /// <paramref name="where"/> (e.g. exposed to air) count toward <paramref name="maxResults"/>: in solid ground the nearest
    /// matches are all buried.</summary>
    public List<FoundBlock> FindBlocks(Vec3d center, double radius, Func<int, int, bool> match, Func<BlockPos, bool>? where, int maxResults = 64)
    {
        var results = new List<FoundBlock>();
        int r = (int)Math.Ceiling(radius);
        double r2 = radius * radius;
        int cx = (int)Math.Floor(center.X), cy = (int)Math.Floor(center.Y), cz = (int)Math.Floor(center.Z);
        int y0 = Math.Max(0, cy - r), y1 = Math.Min(WorldHeight - 1, cy + r);
        for (int x = cx - r; x <= cx + r; x++)
        for (int z = cz - r; z <= cz + r; z++)
        {
            var col = ColumnAt(x, z);
            if (col is null) continue;
            double hx = x + 0.5 - center.X, hz = z + 0.5 - center.Z;
            double h2 = hx * hx + hz * hz;
            if (h2 > r2) continue;
            int lx = x & 15, lz = z & 15;
            for (int y = y0; y <= y1; y++)
            {
                int id = col.GetBlock(lx, y, lz);
                if (id == 0 && !match(0, 0)) continue;
                double hy = y + 0.5 - center.Y;
                double d2 = h2 + hy * hy;
                if (d2 > r2) continue;
                int m = col.GetMeta(lx, y, lz);
                if (match(id, m) && (where is null || where(new BlockPos(x, y, z)))) results.Add(new FoundBlock(new BlockPos(x, y, z), id, m, d2));
            }
        }
        results.Sort((a, b) => a.DistanceSquared.CompareTo(b.DistanceSquared));
        if (results.Count > maxResults) results.RemoveRange(maxResults, results.Count - maxResults);
        return results;
    }

    public List<FoundBlock> FindBlocks(Vec3d center, double radius, int blockId, int maxResults = 64) =>
        FindBlocks(center, radius, (id, _) => id == blockId, maxResults);

    public FoundBlock? FindNearestBlock(Vec3d center, double radius, params int[] blockIds)
    {
        var set = blockIds.ToHashSet();
        var r = FindBlocks(center, radius, (id, _) => set.Contains(id), 1);
        return r.Count > 0 ? r[0] : null;
    }

    /// <summary>Highest y ≤ <paramref name="fromY"/> whose block has a collision box, or -1 if none (void).</summary>
    public int HighestSolidBelow(int x, int fromY, int z)
    {
        for (int y = Math.Min(fromY, WorldHeight - 1); y >= 0; y--)
            if (BlockShapes.HasCollision(this, x, y, z)) return y;
        return -1;
    }

    /// <summary>Top-most non-air block of a column, or -1.</summary>
    public int HighestBlock(int x, int z)
    {
        var col = ColumnAt(x, z);
        return col?.HighestNonAir(x & 15, z & 15) ?? -1;
    }

    /// <summary>All non-air blocks in a column, bottom to top.</summary>
    public List<(int Y, int Id, int Meta)> ScanColumn(int x, int z)
    {
        var list = new List<(int, int, int)>();
        var col = ColumnAt(x, z);
        if (col is null) return list;
        for (int y = 0; y < WorldHeight; y++)
        {
            int id = col.GetBlock(x & 15, y, z & 15);
            if (id != 0) list.Add((y, id, col.GetMeta(x & 15, y, z & 15)));
        }
        return list;
    }

    /// <summary>
    /// True when nothing with a collision box exists at or below y in column (x, z) — stepping there means falling
    /// into the void. Unloaded columns count as void (unknown = unsafe).
    /// </summary>
    public bool IsOverVoid(int x, int y, int z) => Safety.IsOverVoid(this, x, y, z);

    /// <summary>True when every column under the player's hitbox at <paramref name="feet"/> is void below.</summary>
    public bool IsPlayerOverVoid(Vec3d feet) => Safety.IsPlayerOverVoid(this, feet);
}
