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
    }

    public EntityTracker Entities { get; } = new();
    public PlayerState Player { get; } = new();
    public InventoryManager Inventory { get; } = new();

    // ---- world-level state ----
    private long _time;
    /// <summary>World time in ticks (0x04 Time Update). 0 = sunrise, 6000 noon, 12000 sunset, 18000 midnight.</summary>
    public long Time { get => Interlocked.Read(ref _time); set => Interlocked.Exchange(ref _time, value); }
    public long TimeOfDay => ((Time % 24000) + 24000) % 24000;
    public bool IsNight => TimeOfDay is >= 13000 and < 23000;

    public BlockPos Spawn { get; set; }
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

    private ChunkColumn GetOrCreate(int cx, int cz, out bool created)
    {
        bool c = false;
        var col = _columns.GetOrAdd(Key(cx, cz), _ => { c = true; return new ChunkColumn(cx, cz); });
        created = c;
        return col;
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
        if (_columns.TryRemove(Key(chunkX, chunkZ), out _)) ColumnUnloaded?.Invoke(chunkX, chunkZ);
    }

    /// <summary>Drops every column (respawn / dimension change).</summary>
    public void Clear()
    {
        foreach (var k in _columns.Keys.ToArray())
            if (_columns.TryRemove(k, out var c)) ColumnUnloaded?.Invoke(c.ChunkX, c.ChunkZ);
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
