using System.Collections.Concurrent;
using MineClaw.Data;

namespace MineClaw.World;

/// <summary>A single block change (for <see cref="WorldState.BlockChanged"/>).</summary>
public readonly record struct BlockChange(BlockPos Pos, int OldId, int OldMeta, int NewId, int NewMeta);

/// <summary>A block found by a world query.</summary>
public readonly record struct FoundBlock(BlockPos Pos, int Id, int Meta, double DistanceSquared);

/// <summary>
/// The client's view of the world: loaded chunk columns plus world-level state (time, spawn, dimension),
/// and the entity tracker, own player state and inventory model.
/// <para>Threading: the network thread mutates (ApplyChunkRegion/SetBlock/LoadColumn...), tick and agent threads read.
/// Single-block reads are lock-free; writers lock per column. Events fire synchronously on the mutating thread,
/// after the change is visible — handlers must be quick and must not block.</para>
/// </summary>
public sealed class WorldState : IBlockAccess
{
    public const int Height = ChunkColumn.SizeY;

    private readonly ConcurrentDictionary<long, ChunkColumn> _columns = new();
    private readonly Func<int, BlockInfo> _lookup;

    /// <param name="blockLookup">Block property source; defaults to <see cref="Blocks.Get"/>. Tests inject their own table.</param>
    public WorldState(Func<int, BlockInfo>? blockLookup = null)
    {
        _lookup = blockLookup ?? Blocks.Get;
        Light = new LightEngine(this);
    }

    /// <summary>The b1.8.1 light engine: the server's light arrays kept up to date on every block change (mc-dev).</summary>
    public LightEngine Light { get; }

    public EntityTracker Entities { get; } = new();
    public PlayerState Player { get; } = new();
    public InventoryManager Inventory { get; } = new();
    /// <summary>Cells the bot itself just dug/placed (attribution: the bot's own edits are never a player's).</summary>
    public SelfEditLog SelfEdits { get; } = new();
    /// <summary>Blocks hive bots placed themselves and that still stand (protection rule 1: always minable by the bot).</summary>
    public PlacedBlockLog Placed { get; } = new();
    /// <summary>Recent chop_tree runs (felled/left logs): a remnant they left is the bot's own unfinished tree. In-process
    /// bodies share main's.</summary>
    public ChopLog Chops { get; set; } = new();
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
    /// <summary>Rain strength 0..1 (0x46 begin/end rain); darkens the sky a little (World.k).</summary>
    public float Rain { get; set; }
    /// <summary>0 overworld, -1 nether.</summary>
    public int Dimension { get; set; }
    public long Seed { get; set; }
    public int MaxPlayers { get; set; }

    // ---- events ----
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
        bool c = false;
        var col = _columns.GetOrAdd(Key(cx, cz), _ => { c = true; return new ChunkColumn(cx, cz) { Dimension = Dimension }; });
        created = c;
        return col;
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

    private sealed class RememberedView(WorldState w) : IBlockAccess
    {
        public int GetBlock(int x, int y, int z)
        {
            if (w.IsLoaded(x, z)) return w.GetBlock(x, y, z);
            return w._remembered.TryGetValue(Key(x >> 4, z >> 4), out var r) ? r.GetBlock(x & 15, y, z & 15) : 0;
        }

        public int GetMeta(int x, int y, int z)
        {
            if (w.IsLoaded(x, z)) return w.GetMeta(x, y, z);
            return w._remembered.TryGetValue(Key(x >> 4, z >> 4), out var r) ? r.GetMeta(x & 15, y, z & 15) : 0;
        }

        public bool IsLoaded(int x, int z) => w.IsLoaded(x, z) || w._remembered.ContainsKey(Key(x >> 4, z >> 4));

        public BlockInfo Info(int id) => w.Info(id);
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
        if (_columns.TryRemove(Key(chunkX, chunkZ), out var gone))
        {
            ColumnUnloaded?.Invoke(chunkX, chunkZ);
            if (gone.HasData) ColumnDropped?.Invoke(gone);
        }
    }

    /// <summary>Drops every column (respawn / dimension change), remembered ones included.</summary>
    public void Clear()
    {
        foreach (var k in _columns.Keys.ToArray())
            if (_columns.TryRemove(k, out var c))
            {
                ColumnUnloaded?.Invoke(c.ChunkX, c.ChunkZ);
                if (c.HasData) ColumnDropped?.Invoke(c);
            }
        _remembered.Clear();
    }

    /// <summary>True when the column containing block (x, z) exists and has received block data.</summary>
    public bool IsLoaded(int x, int z) => ColumnAt(x, z) is { HasData: true };

    // ---- block access ----

    public int GetBlock(int x, int y, int z)
    {
        if ((uint)y >= Height) return 0;
        var c = ColumnAt(x, z);
        return c is null ? 0 : c.Ids[ChunkColumn.Index(x & 15, y, z & 15)];
    }

    public int GetMeta(int x, int y, int z)
    {
        if ((uint)y >= Height) return 0;
        var c = ColumnAt(x, z);
        return c is null ? 0 : c.Meta[ChunkColumn.Index(x & 15, y, z & 15)];
    }

    public int GetBlockLight(int x, int y, int z)
    {
        if ((uint)y >= Height) return 0;
        var c = ColumnAt(x, z);
        return c is null ? 0 : c.BlockLight[ChunkColumn.Index(x & 15, y, z & 15)];
    }

    public int GetSkyLight(int x, int y, int z)
    {
        if ((uint)y >= Height) return 15;
        var c = ColumnAt(x, z);
        return c is null ? 15 : c.SkyLight[ChunkColumn.Index(x & 15, y, z & 15)];
    }

    /// <summary>Raw sky and block light of a cell (live columns; unloaded = 0, 0).</summary>
    public LightLevel LightAt(BlockPos p) => new(Light.Get(true, p.X, p.Y, p.Z), Light.Get(false, p.X, p.Y, p.Z));

    /// <summary>World.k (skylightSubtracted) at <paramref name="timeOfDay"/> with the current rain: 0 by day, 11 at night.</summary>
    public int SkylightSubtracted(long? timeOfDay = null) => LightEngine.SkylightSubtracted(timeOfDay ?? TimeOfDay, Rain);

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

    public int GetBlock(BlockPos p) => GetBlock(p.X, p.Y, p.Z);
    public int GetMeta(BlockPos p) => GetMeta(p.X, p.Y, p.Z);
    public BlockInfo GetInfo(BlockPos p) => _lookup(GetBlock(p));

    /// <summary>Sets one block (0x35 Block Change / 0x34 Multi Block Change entries, or local prediction).
    /// Ignored when y is out of range or the column isn't loaded. Raises <see cref="BlockChanged"/>.</summary>
    public bool SetBlock(int x, int y, int z, int id, int meta)
    {
        if ((uint)y >= Height) return false;
        var c = ColumnAt(x, z);
        if (c is null) return false;
        int oldId, oldMeta;
        lock (c.Sync)
        {
            int i = ChunkColumn.Index(x & 15, y, z & 15);
            oldId = c.Ids[i];
            oldMeta = c.Meta[i];
            c.Ids[i] = (byte)id;
            c.Meta[i] = (byte)(meta & 0x0F);
        }
        if (oldId != id) Light.OnBlockChanged(x, y, z, oldId, id);
        if (oldId != id || oldMeta != (meta & 0x0F))
            BlockChanged?.Invoke(new BlockChange(new BlockPos(x, y, z), oldId, oldMeta, id, meta & 0x0F));
        return true;
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
                lock (col.Sync)
                {
                    for (int dy = 0; dy < sizeY; dy++)
                    {
                        int y = originY + dy;
                        if ((uint)y >= Height) continue;
                        int i = ChunkColumn.Index(x & 15, y, z & 15);
                        int s = src + dy;
                        col.Ids[i] = ids[s];
                        col.Meta[i] = meta is null || s >= meta.Length ? (byte)0 : (byte)(meta[s] & 0x0F);
                        col.BlockLight[i] = blockLight is null || s >= blockLight.Length ? (byte)0 : (byte)(blockLight[s] & 0x0F);
                        col.SkyLight[i] = skyLight is null || s >= skyLight.Length ? (byte)15 : (byte)(skyLight[s] & 0x0F);
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

    // ---- queries ----

    /// <summary>Finds blocks matching <paramref name="match"/> within a sphere, nearest first.</summary>
    public List<FoundBlock> FindBlocks(Vec3d center, double radius, Func<int, int, bool> match, int maxResults = 64)
    {
        var results = new List<FoundBlock>();
        int r = (int)Math.Ceiling(radius);
        double r2 = radius * radius;
        int cx = (int)Math.Floor(center.X), cy = (int)Math.Floor(center.Y), cz = (int)Math.Floor(center.Z);
        int y0 = Math.Max(0, cy - r), y1 = Math.Min(Height - 1, cy + r);
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
                int i = ChunkColumn.Index(lx, y, lz);
                int id = col.Ids[i];
                if (id == 0 && !match(0, 0)) continue;
                double hy = y + 0.5 - center.Y;
                double d2 = h2 + hy * hy;
                if (d2 > r2) continue;
                int m = col.Meta[i];
                if (match(id, m)) results.Add(new FoundBlock(new BlockPos(x, y, z), id, m, d2));
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
        for (int y = Math.Min(fromY, Height - 1); y >= 0; y--)
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
        for (int y = 0; y < Height; y++)
        {
            int i = ChunkColumn.Index(x & 15, y, z & 15);
            if (col.Ids[i] != 0) list.Add((y, col.Ids[i], col.Meta[i]));
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
