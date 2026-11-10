using System.Diagnostics;
using MineClaw.Data;

namespace MineClaw.World;

/// <summary>Raw light of one cell: sky light and block light, each 0..15 (the 0x33 nibbles, kept up to date by
/// <see cref="LightEngine"/>).</summary>
public readonly record struct LightLevel(int Sky, int Block)
{
    public override string ToString() => $"sky {Sky}, block {Block}";
}

/// <summary>
/// Keeps the sky and block light of a <see cref="WorldState"/>'s loaded columns the way the b1.8.1 game does, so the values
/// match what the server computes (with the version's light tables, so 1.12.2 worlds use their own opacities and emissions):
/// <list type="bullet">
/// <item>Columns that arrive with the server's light (0x33 carries the block and sky light nibbles) keep it as it is; only
/// each column's surface height is worked out. A region without light arrays (fixtures, tests) is lit from its blocks: sky
/// light straight down from the top, losing each block's opacity, then every light-emitting block's spread.</item>
/// <item>A block change first moves the column's surface if it has to (rewriting that column's sky light and re-lighting the
/// changed span in the column and its 4 neighbours), then re-lights the changed cell for sky and for block light.</item>
/// <item>Re-lighting a cell is a breadth-first flood over a fixed queue of packed cells. If the cell is now darker, a
/// darkening pass first clears everything its old value lit (cells exactly one level dimmer at each step); then a
/// brightening pass gives every queued cell the light it should have (15 sky at or above the surface; else its emission or
/// the brightest neighbour minus the cell's opacity, at least 1 per step), and each cell that got brighter queues its darker
/// neighbours. Both passes stop spreading 17 blocks (Manhattan) from the changed cell.</item>
/// </list>
/// The game's guards are kept over our loaded columns: a cell is re-lit only when every column within 17 blocks is loaded (the
/// server's own view edge behaves the same), a column's sky span only within 16; reads of unloaded columns return 0. Without
/// them a lone test column under a full floor cascades for minutes.
/// Incremental only: a torch is ≈ 3k cell visits (≪ 1 ms), never a per-tick recompute. Thread-safe: updates serialize on one
/// lock (network thread + local prediction); reads are lock-free byte reads.
/// </summary>
public sealed class LightEngine
{
    private const int MaxLight = 15;
    /// <summary>How far (Manhattan) a re-light spreads from its cell, and how far around it columns must be loaded.</summary>
    private const int UpdateReach = 17;
    /// <summary>How far around a column the columns must be loaded before its sky span is re-lit.</summary>
    private const int ColumnRelightReach = 16;
    /// <summary>The y the column guard checks the loaded columns at (only x/z matter to it).</summary>
    private const int ColumnGuardY = 64;

    // A queue entry packs a cell's offset from the update's origin cell (6 bits per axis, biased by 32 so it is never
    // negative) and, in the darkening pass, the light level it was queued at.
    private const int QueueCapacity = 32768;
    /// <summary>Room the brightening pass keeps free before it queues one cell's 6 neighbours.</summary>
    private const int NeighbourRoom = 6;
    private const int OffsetBias = 32, OffsetMask = 63, YShift = 6, ZShift = 12, LevelShift = 18, LevelMask = 15;
    private const int OriginEntry = OffsetBias + (OffsetBias << YShift) + (OffsetBias << ZShift);

    private readonly WorldState _world;
    /// <summary>Update lock: this world's own, or the <see cref="Chunks.SharedChunkStore"/>'s when its columns are shared (every
    /// world on one store updates the same light arrays).</summary>
    private readonly object _sync;
    private readonly int _worldHeight;
    private readonly byte[] _opacityById = new byte[256], _emissionById = new byte[256];
    private long _updateCount, _visitCount, _elapsedTicks;

    // The flood's state, used only while _sync is held: the reusable queue, its end, and the cell the update started from.
    private readonly int[] _queue = new int[QueueCapacity];
    private int _queueEnd, _originX, _originY, _originZ;

    internal LightEngine(WorldState world)
    {
        _world = world;
        _sync = world.ChunkStore?.LightSync ?? new object();
        _worldHeight = world.WorldHeight;
        // The version's light tables (b1.8.1 or 1.12.2), captured once.
        for (int id = 0; id < 256; id++)
        {
            _opacityById[id] = (byte)world.Physics.LightOpacity(id);
            _emissionById[id] = (byte)world.Physics.LightEmission(id);
        }
    }

    private int Opacity(int id) => (uint)id < 256 ? _opacityById[id] : 255;
    private int Emission(int id) => (uint)id < 256 ? _emissionById[id] : 0;

    /// <summary>The light a cell loses per step through a block: its opacity, but at least 1.</summary>
    private int StepCost(int id) => Math.Max(1, Opacity(id));

    private static int HeightIndex(int localX, int localZ) => localZ << 4 | localX;

    // ------------------------------------------------------------------ column cache

    // A tiny direct-mapped column cache for the flood (used only while _sync is held; cleared at every entry point, so a
    // column replaced by a reload is never read through a stale reference across updates).
    private readonly ChunkColumn?[] _cachedColumns = new ChunkColumn?[16];
    private readonly long[] _cachedKeys = new long[16];

    private void ClearColumnCache() => Array.Clear(_cachedColumns);

    /// <summary>The loaded column holding world x/z, or null when it has no data.</summary>
    private ChunkColumn? CachedColumn(int x, int z)
    {
        int chunkX = x >> 4, chunkZ = z >> 4;
        long key = ((long)chunkX << 32) | (uint)chunkZ;
        int slot = (chunkX & 3) << 2 | (chunkZ & 3);
        var column = _cachedColumns[slot];
        if (column != null && _cachedKeys[slot] == key) return column;
        column = _world.ColumnAtRaw(x, z);
        if (column is not { HasData: true }) return null;
        _cachedColumns[slot] = column;
        _cachedKeys[slot] = key;
        return column;
    }

    /// <summary><see cref="Get"/> through the cache (callers hold <see cref="_sync"/>): y clamped into the world, 0 when
    /// unloaded.</summary>
    private int ReadLight(bool sky, int x, int y, int z)
    {
        y = Math.Clamp(y, 0, _worldHeight - 1);
        var column = CachedColumn(x, z);
        if (column is null) return 0;
        return sky ? column.GetSkyLight(x & 15, y, z & 15) : column.GetBlockLight(x & 15, y, z & 15);
    }

    /// <summary>Writes one cell's light; out-of-range y and unloaded columns are ignored.</summary>
    private void WriteLight(bool sky, int x, int y, int z, int level)
    {
        if ((uint)y >= _worldHeight) return;
        var column = CachedColumn(x, z);
        if (column is null) return;
        if (sky) column.SetSkyLight(x & 15, y, z & 15, level);
        else column.SetBlockLight(x & 15, y, z & 15, level);
    }

    private int BlockIdAt(int x, int y, int z) =>
        (uint)y < _worldHeight && CachedColumn(x, z) is { } column ? column.GetBlock(x & 15, y, z & 15) : 0;

    private int SurfaceAt(int x, int z) => CachedColumn(x, z) is { } column ? column.HeightMap[HeightIndex(x & 15, z & 15)] : 0;

    /// <summary>Every column the box touches has data, and the box overlaps the world's y range.</summary>
    private bool ColumnsLoaded(int minX, int minY, int minZ, int maxX, int maxY, int maxZ)
    {
        if (maxY < 0 || minY >= _worldHeight) return false;
        for (int chunkX = minX >> 4; chunkX <= maxX >> 4; chunkX++)
        for (int chunkZ = minZ >> 4; chunkZ <= maxZ >> 4; chunkZ++)
            if (CachedColumn(chunkX << 4, chunkZ << 4) is null) return false;
        return true;
    }

    private bool ColumnsLoadedAround(int x, int y, int z, int reach) =>
        ColumnsLoaded(x - reach, y - reach, z - reach, x + reach, y + reach, z + reach);

    /// <summary>Off = block changes leave the light arrays alone (the old behaviour). On by default.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Cell re-lights run so far (cost telemetry: tests pin it).</summary>
    public long Updates => Interlocked.Read(ref _updateCount);
    /// <summary>Queue entries processed so far (cells looked at).</summary>
    public long Visits => Interlocked.Read(ref _visitCount);
    /// <summary>Total time spent in light updates.</summary>
    public TimeSpan Time => TimeSpan.FromTicks((long)(Interlocked.Read(ref _elapsedTicks) * (TimeSpan.TicksPerSecond / (double)Stopwatch.Frequency)));

    // ------------------------------------------------------------------ reads

    /// <summary>A cell's sky or block light: y clamped into the world, 0 for an unloaded column.</summary>
    public int Get(bool sky, int x, int y, int z)
    {
        y = Math.Clamp(y, 0, _worldHeight - 1);
        var column = _world.ColumnAtRaw(x, z);
        if (column is null || !column.HasData) return 0;
        return sky ? column.GetSkyLight(x & 15, y, z & 15) : column.GetBlockLight(x & 15, y, z & 15);
    }

    /// <summary>The cell is at or above its column's surface (an unloaded column: false).</summary>
    public bool CanSeeSky(int x, int y, int z)
    {
        var column = _world.ColumnAtRaw(x, z);
        return column is { HasData: true } && y >= column.HeightMap[HeightIndex(x & 15, z & 15)];
    }

    /// <summary>The column's surface height (the lowest y that sees the sky), or 0 when unloaded.</summary>
    public int Height(int x, int z)
    {
        var column = _world.ColumnAtRaw(x, z);
        return column is { HasData: true } ? column.HeightMap[HeightIndex(x & 15, z & 15)] : 0;
    }

    /// <summary>
    /// The light level a mob or the player sees at a cell, with <paramref name="skylightSubtracted"/> taken off the sky light
    /// for the time of day: max(sky − subtracted, block). A slab (44), farmland (60) or wooden/cobble stairs (53/67) cell reads
    /// the brightest of the cell above and its 4 side neighbours instead (exactly those ids in b1.8.1: brick and stone-brick
    /// stairs are not among them).
    /// </summary>
    public int LightLevel(int x, int y, int z, int skylightSubtracted, bool neighbours = true)
    {
        if (neighbours && ReadsNeighbourLight(x, y, z)) return BrightestAround(x, y, z, skylightSubtracted);
        return OwnLightLevel(x, y, z, skylightSubtracted);
    }

    /// <summary>A slab, farmland or wooden/cobble stairs cell.</summary>
    private bool ReadsNeighbourLight(int x, int y, int z)
    {
        int id = (uint)y < _worldHeight ? _world.GetBlock(x, y, z) : 0;
        return id is Blocks.Slab or Blocks.Farmland or Blocks.CobblestoneStairs or Blocks.WoodenStairs;
    }

    /// <summary>The brightest of the cell above and the 4 side neighbours.</summary>
    private int BrightestAround(int x, int y, int z, int skylightSubtracted)
    {
        int brightest = OwnLightLevel(x, y + 1, z, skylightSubtracted);
        brightest = Math.Max(brightest, OwnLightLevel(x + 1, y, z, skylightSubtracted));
        brightest = Math.Max(brightest, OwnLightLevel(x - 1, y, z, skylightSubtracted));
        brightest = Math.Max(brightest, OwnLightLevel(x, y, z + 1, skylightSubtracted));
        return Math.Max(brightest, OwnLightLevel(x, y, z - 1, skylightSubtracted));
    }

    /// <summary>max(sky − subtracted, block) of the cell itself (0 below the world, the top cell above it).</summary>
    private int OwnLightLevel(int x, int y, int z, int skylightSubtracted)
    {
        if (y < 0) return 0;
        if (y >= _worldHeight) y = _worldHeight - 1;
        var column = _world.ColumnAtRaw(x, z);
        if (column is null || !column.HasData) return 0;
        int dimmedSky = column.GetSkyLight(x & 15, y, z & 15) - skylightSubtracted;
        return Math.Max(dimmedSky, column.GetBlockLight(x & 15, y, z & 15));
    }

    // ------------------------------------------------------------------ sky darkening over the day

    /// <summary>One full sine period sampled at 65536 points: the game's fast cosine reads it.</summary>
    private static readonly float[] SineTable = BuildSineTable();

    private static float[] BuildSineTable()
    {
        var table = new float[65536];
        for (int i = 0; i < table.Length; i++) table[i] = (float)Math.Sin(i * Math.PI * 2.0D / 65536.0D);
        return table;
    }

    /// <summary>The table cosine: 10430.378 table steps per radian, a quarter turn (16384 steps) ahead of the sine.</summary>
    private static float TableCos(float radians) => SineTable[(int)(radians * 10430.378F + 16384.0F) & 0xffff];

    /// <summary>The sun's angle as a fraction of a turn (0 = noon, 0.5 = midnight), eased so days run a little longer than
    /// nights.</summary>
    public static float CelestialAngle(long time, float partial = 1.0F)
    {
        int tickOfDay = (int)(time % 24000L);
        float linear = (tickOfDay + partial) / 24000.0F - 0.25F;
        if (linear < 0.0F) ++linear;
        if (linear > 1.0F) --linear;
        float eased = 1.0F - (float)((Math.Cos(linear * Math.PI) + 1.0D) / 2.0D);
        return linear + (eased - linear) / 3.0F;
    }

    /// <summary>
    /// How much sky light is taken off at <paramref name="timeOfDay"/>: 0 by day, 11 at night, less with
    /// <paramref name="rain"/>/<paramref name="thunder"/> strength 0..1 (each takes 5/16 of the remaining brightness).
    /// During a thunderstorm hostile spawn checks use 10 instead.
    /// </summary>
    public static int SkylightSubtracted(long timeOfDay, float rain = 0f, float thunder = 0f)
    {
        float angle = CelestialAngle(((timeOfDay % 24000) + 24000) % 24000);
        float darkness = Math.Clamp(1.0F - (TableCos(angle * 3.1415927F * 2.0F) * 2.0F + 0.5F), 0.0F, 1.0F);
        float brightness = 1.0F - darkness;
        brightness = (float)(brightness * (1.0D - rain * 5.0F / 16.0D));
        brightness = (float)(brightness * (1.0D - thunder * rain * 5.0F / 16.0D));
        return (int)((1.0F - brightness) * 11.0F);
    }

    /// <summary>The most sky light a day takes off (midnight, clear sky): 11. Spawn checks "after dark" use it.</summary>
    public const int NightSubtracted = 11;

    // ------------------------------------------------------------------ column data

    /// <summary>A region of live data arrived (0x33): its columns' surfaces; with no light arrays, the initial light and the
    /// emitters' spread.</summary>
    internal void OnRegion(int originX, int originZ, int sizeX, int sizeZ, bool hasSky, bool hasBlock, bool spread = false)
    {
        if (!Enabled) return;
        long started = Stopwatch.GetTimestamp();
        lock (_sync)
        {
            ClearColumnCache();
            var emitters = new List<BlockPos>();
            for (int x = originX; x < originX + sizeX; x++)
            for (int z = originZ; z < originZ + sizeZ; z++)
            {
                var column = _world.ColumnAtRaw(x, z);
                if (column is null) continue;
                LightNewColumn(column, x, z, hasSky, hasBlock, emitters);
            }
            if (!hasSky && spread) SpreadSkySideways(originX, originZ, sizeX, sizeZ);
            foreach (var emitter in emitters) RelightCell(false, emitter.X, emitter.Y, emitter.Z);
        }
        Interlocked.Add(ref _elapsedTicks, Stopwatch.GetTimestamp() - started);
    }

    /// <summary>One new column: its surface, its sky light when none came, and with no block light array its block light
    /// cleared and its emitters collected.</summary>
    private void LightNewColumn(ChunkColumn column, int x, int z, bool hasSky, bool hasBlock, List<BlockPos> emitters)
    {
        int localX = x & 15, localZ = z & 15;
        FindSurface(column, localX, localZ);
        if (!hasSky) LightSkyFromAbove(column, localX, localZ);
        if (!hasBlock) ClearBlockLight(column, x, z, emitters);
    }

    private void ClearBlockLight(ChunkColumn column, int x, int z, List<BlockPos> emitters)
    {
        int localX = x & 15, localZ = z & 15;
        for (int y = 0; y < _worldHeight; y++)
        {
            column.SetBlockLight(localX, y, localZ, 0);
            if (Emission(column.GetBlock(localX, y, localZ)) > 0) emitters.Add(new BlockPos(x, y, z));
        }
    }

    /// <summary>Sky light leaking sideways: each column's sky is re-lit between its surface and its lowest neighbour's, and
    /// each neighbour's up to this column's surface (overhangs, the underside of an island).</summary>
    private void SpreadSkySideways(int originX, int originZ, int sizeX, int sizeZ)
    {
        for (int x = originX; x < originX + sizeX; x++)
        for (int z = originZ; z < originZ + sizeZ; z++)
        {
            if (CachedColumn(x, z) is null || !ColumnsLoadedAround((x >> 4) * 16 + 8, ColumnGuardY, (z >> 4) * 16 + 8, ColumnRelightReach)) continue;
            int surface = SurfaceAt(x, z);
            int lowestNeighbour = Math.Min(Math.Min(SurfaceAt(x - 1, z), SurfaceAt(x + 1, z)), Math.Min(SurfaceAt(x, z - 1), SurfaceAt(x, z + 1)));
            RelightSkyBetweenSurfaceAnd(x, z, lowestNeighbour);
            RelightSkyBetweenSurfaceAnd(x - 1, z, surface);
            RelightSkyBetweenSurfaceAnd(x + 1, z, surface);
            RelightSkyBetweenSurfaceAnd(x, z - 1, surface);
            RelightSkyBetweenSurfaceAnd(x, z + 1, surface);
        }
    }

    /// <summary>Re-lights the sky of one column from its surface to <paramref name="otherY"/> (both ends included).</summary>
    private void RelightSkyBetweenSurfaceAnd(int x, int z, int otherY)
    {
        int surface = SurfaceAt(x, z);
        if (surface > otherY) RelightSkySpan(x, z, otherY, surface + 1);
        else if (surface < otherY) RelightSkySpan(x, z, surface, otherY + 1);
    }

    /// <summary>A column's surface from scratch: down from the top cell to just above the highest block that has opacity.</summary>
    private void FindSurface(ChunkColumn column, int localX, int localZ)
    {
        int surface = column.Height - 1;
        while (surface > 0 && Opacity(column.GetBlock(localX, surface - 1, localZ)) == 0) surface--;
        column.HeightMap[HeightIndex(localX, localZ)] = (short)surface;
    }

    /// <summary>A column's first sky light (no light array came with it): from the top down, 15 minus the opacities passed so far
    /// while that stays above 0, else 0 (y 0 is never lit). Written cell by cell in one pass, so the open air above the ground
    /// stays at the unallocated default.</summary>
    private void LightSkyFromAbove(ChunkColumn column, int localX, int localZ)
    {
        int remaining = MaxLight;
        for (int y = column.Height - 1; y >= 0; y--)
        {
            int level = 0;
            if (y > 0 && remaining > 0)
            {
                remaining -= Opacity(column.GetBlock(localX, y, localZ));
                if (remaining > 0) level = remaining;
            }
            column.SetSkyLight(localX, y, localZ, level);
        }
    }

    /// <summary>Re-lights a loaded column from its blocks alone, the full way (first light, then the sideways sky spread:
    /// overhangs, an island's underside, then every emitter). A region that arrives without light gets only the cheap first
    /// light and its emitters.</summary>
    public void Relight(int chunkX, int chunkZ)
    {
        if (_world.GetColumn(chunkX, chunkZ) is not { HasData: true }) return;
        OnRegion(chunkX * 16, chunkZ * 16, 16, 16, hasSky: false, hasBlock: false, spread: true);
    }

    // ------------------------------------------------------------------ block change

    /// <summary>A block changed from <paramref name="oldId"/> (already written): the column's surface first, then the cell
    /// re-lit for sky and block light.</summary>
    internal void OnBlockChanged(int x, int y, int z, int oldId, int newId)
    {
        if (!Enabled) return;
        var column = _world.ColumnAtRaw(x, z);
        if (column is null || !column.HasData || (uint)y >= _worldHeight) return;
        long started = Stopwatch.GetTimestamp();
        lock (_sync)
        {
            ClearColumnCache();
            if (_world.Dimension != -1) UpdateSurface(column, x & 15, y, z & 15, newId); // the nether has no sky
            RelightCell(true, x, y, z);
            RelightCell(false, x, y, z);
        }
        Interlocked.Add(ref _elapsedTicks, Stopwatch.GetTimestamp() - started);
    }

    /// <summary>A block with opacity placed at or above the surface lifts it over the block; a clear block at the cell just under
    /// the surface lets it sink.</summary>
    private void UpdateSurface(ChunkColumn column, int localX, int y, int localZ, int newId)
    {
        int surface = column.HeightMap[HeightIndex(localX, localZ)];
        if (Opacity(newId) != 0)
        {
            if (y >= surface) MoveSurface(column, localX, y + 1, localZ);
        }
        else if (y == surface - 1) MoveSurface(column, localX, y, localZ);
    }

    /// <summary>Settles the column's surface from <paramref name="startY"/> (or the old surface when that is higher) down past
    /// clear blocks, then rewrites the sky light it moved over and re-lights the span around it.</summary>
    private void MoveSurface(ChunkColumn column, int localX, int startY, int localZ)
    {
        int index = HeightIndex(localX, localZ);
        int oldSurface = column.HeightMap[index];
        int newSurface = Math.Max(startY, oldSurface);
        while (newSurface > 0 && Opacity(column.GetBlock(localX, newSurface - 1, localZ)) == 0) --newSurface;
        if (newSurface == oldSurface) return;
        // The game re-lights this span in the world column at the chunk-local x/z, not in the changed column; kept so the light
        // stays the same as the server's.
        RelightSkySpanInclusive(localX, localZ, newSurface, oldSurface);
        column.HeightMap[index] = (short)newSurface;
        int worldX = column.ChunkX * 16 + localX, worldZ = column.ChunkZ * 16 + localZ;
        RewriteSkyBetween(column, localX, localZ, newSurface, oldSurface);
        FadeSkyBelow(column, localX, localZ, newSurface);
        RelightSpanAround(worldX, worldZ, oldSurface, column.HeightMap[index]);
    }

    /// <summary>Between the old and the new surface: full sky where the column opened up, none where it is now covered.</summary>
    private static void RewriteSkyBetween(ChunkColumn column, int localX, int localZ, int newSurface, int oldSurface)
    {
        if (newSurface < oldSurface) for (int y = newSurface; y < oldSurface; y++) column.SetSkyLight(localX, y, localZ, MaxLight);
        else for (int y = oldSurface; y < newSurface; y++) column.SetSkyLight(localX, y, localZ, 0);
    }

    /// <summary>Under the new surface: 15 minus each block's step cost going down, until it reaches 0.</summary>
    private void FadeSkyBelow(ChunkColumn column, int localX, int localZ, int surface)
    {
        int level = MaxLight;
        for (int y = surface - 1; y >= 0 && level > 0; y--)
        {
            level = Math.Max(0, level - StepCost(column.GetBlock(localX, y, localZ)));
            column.SetSkyLight(localX, y, localZ, level);
        }
    }

    /// <summary>The span between the old and the new surface re-lit in the 4 neighbour columns and the column itself.</summary>
    private void RelightSpanAround(int x, int z, int oldSurface, int newSurface)
    {
        int bottom = Math.Min(oldSurface, newSurface), top = Math.Max(oldSurface, newSurface);
        RelightSkySpan(x - 1, z, bottom, top);
        RelightSkySpan(x + 1, z, bottom, top);
        RelightSkySpan(x, z - 1, bottom, top);
        RelightSkySpan(x, z + 1, bottom, top);
        RelightSkySpan(x, z, bottom, top);
    }

    /// <summary>Re-lights the sky of one world column from <paramref name="fromY"/> to <paramref name="toY"/>, both included, in
    /// either order.</summary>
    private void RelightSkySpanInclusive(int x, int z, int fromY, int toY)
    {
        if (fromY > toY) (fromY, toY) = (toY, fromY);
        for (int y = fromY; y <= toY; y++) RelightCell(true, x, y, z);
    }

    /// <summary>Re-lights the sky of one world column from <paramref name="bottom"/> up to just under <paramref name="top"/>,
    /// when the columns around it are loaded.</summary>
    private void RelightSkySpan(int x, int z, int bottom, int top)
    {
        if (top <= bottom || !ColumnsLoadedAround(x, ColumnGuardY, z, ColumnRelightReach)) return;
        for (int y = bottom; y < top; y++) RelightCell(true, x, y, z);
    }

    // ------------------------------------------------------------------ re-lighting one cell (the flood)

    /// <summary>The light a cell should have from its surroundings alone: 15 sky at or above its column's surface; otherwise the
    /// brightest neighbour minus the cell's step cost, but never less than its own emission (block light) or 0 (sky light).</summary>
    private int TargetLight(bool sky, int x, int y, int z)
    {
        int id = BlockIdAt(x, y, z);
        if (sky && CachedColumn(x, z) is { } column && y >= column.HeightMap[HeightIndex(x & 15, z & 15)]) return MaxLight;
        int floor = sky ? 0 : Emission(id);
        return Math.Max(floor, BrightestNeighbour(sky, x, y, z) - StepCost(id));
    }

    private int BrightestNeighbour(bool sky, int x, int y, int z)
    {
        int brightest = ReadLight(sky, x - 1, y, z);
        brightest = Math.Max(brightest, ReadLight(sky, x + 1, y, z));
        brightest = Math.Max(brightest, ReadLight(sky, x, y - 1, z));
        brightest = Math.Max(brightest, ReadLight(sky, x, y + 1, z));
        brightest = Math.Max(brightest, ReadLight(sky, x, y, z - 1));
        return Math.Max(brightest, ReadLight(sky, x, y, z + 1));
    }

    /// <summary>Brings one cell's sky or block light (and whatever it lights) up to date after a change there. Callers hold
    /// <see cref="_sync"/>.</summary>
    private void RelightCell(bool sky, int x, int y, int z)
    {
        if (!ColumnsLoadedAround(x, y, z, UpdateReach)) return;
        Interlocked.Increment(ref _updateCount);
        _originX = x;
        _originY = y;
        _originZ = z;
        _queueEnd = 0;
        int current = ReadLight(sky, x, y, z), target = TargetLight(sky, x, y, z);
        int visits = 0;
        if (target > current) _queue[_queueEnd++] = OriginEntry;
        else if (target < current)
        {
            _queue[_queueEnd++] = OriginEntry + (current << LevelShift);
            visits += DarkenPass(sky);
        }
        visits += BrightenPass(sky);
        Interlocked.Add(ref _visitCount, visits);
    }

    /// <summary>The darkening pass: a queued cell still at the level it was queued with goes to 0 and queues its neighbours that
    /// are exactly one level dimmer (they took their light from it), within reach of the origin. Every entry stays in the
    /// queue, so the brightening pass then re-lights all of them from the light that is left around. Returns the entries
    /// visited.</summary>
    private int DarkenPass(bool sky)
    {
        int read = 0;
        while (read < _queueEnd)
        {
            int entry = _queue[read++];
            int x = EntryX(entry), y = EntryY(entry), z = EntryZ(entry);
            int level = entry >> LevelShift & LevelMask;
            if (ReadLight(sky, x, y, z) != level) continue;
            WriteLight(sky, x, y, z, 0);
            int dimmer = level - 1;
            if (dimmer <= 0 || OutOfReach(x, y, z)) continue;
            QueueIfAtLevel(sky, x, y, z - 1, dimmer);
            QueueIfAtLevel(sky, x, y, z + 1, dimmer);
            QueueIfAtLevel(sky, x, y - 1, z, dimmer);
            QueueIfAtLevel(sky, x, y + 1, z, dimmer);
            QueueIfAtLevel(sky, x - 1, y, z, dimmer);
            QueueIfAtLevel(sky, x + 1, y, z, dimmer);
        }
        return read;
    }

    private void QueueIfAtLevel(bool sky, int x, int y, int z, int level)
    {
        if (ReadLight(sky, x, y, z) == level && _queueEnd < QueueCapacity)
            _queue[_queueEnd++] = PackOffset(x, y, z) + (level << LevelShift);
    }

    /// <summary>The brightening pass: every queued cell takes the light it should have; one that got brighter queues its darker
    /// neighbours, within reach of the origin and while the queue has room for all six. Returns the entries visited.</summary>
    private int BrightenPass(bool sky)
    {
        int read = 0;
        while (read < _queueEnd)
        {
            int entry = _queue[read++];
            int x = EntryX(entry), y = EntryY(entry), z = EntryZ(entry);
            int current = ReadLight(sky, x, y, z), target = TargetLight(sky, x, y, z);
            if (target == current) continue;
            WriteLight(sky, x, y, z, target);
            if (target < current || OutOfReach(x, y, z) || _queueEnd >= QueueCapacity - NeighbourRoom) continue;
            QueueIfDarker(sky, x - 1, y, z, target);
            QueueIfDarker(sky, x + 1, y, z, target);
            QueueIfDarker(sky, x, y - 1, z, target);
            QueueIfDarker(sky, x, y + 1, z, target);
            QueueIfDarker(sky, x, y, z - 1, target);
            QueueIfDarker(sky, x, y, z + 1, target);
        }
        return read;
    }

    private void QueueIfDarker(bool sky, int x, int y, int z, int level)
    {
        if (ReadLight(sky, x, y, z) < level) _queue[_queueEnd++] = PackOffset(x, y, z);
    }

    private int PackOffset(int x, int y, int z) =>
        x - _originX + OffsetBias + (y - _originY + OffsetBias << YShift) + (z - _originZ + OffsetBias << ZShift);

    private int EntryX(int entry) => (entry & OffsetMask) - OffsetBias + _originX;
    private int EntryY(int entry) => (entry >> YShift & OffsetMask) - OffsetBias + _originY;
    private int EntryZ(int entry) => (entry >> ZShift & OffsetMask) - OffsetBias + _originZ;

    /// <summary>At or past the flood's reach (Manhattan) from the origin cell: it is updated but spreads no further.</summary>
    private bool OutOfReach(int x, int y, int z) =>
        Math.Abs(x - _originX) + Math.Abs(y - _originY) + Math.Abs(z - _originZ) >= UpdateReach;
}
