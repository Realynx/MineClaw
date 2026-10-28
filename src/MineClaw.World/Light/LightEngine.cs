using System.Diagnostics;

namespace MineClaw.World;

/// <summary>Raw light of one cell: sky light and block light, each 0..15 (the 0x33 nibbles, kept up to date by
/// <see cref="LightEngine"/>).</summary>
public readonly record struct LightLevel(int Sky, int Block)
{
    public override string ToString() => $"sky {Sky}, block {Block}";
}

/// <summary>
/// The b1.8.1 light engine of a <see cref="WorldState"/>, a line-by-line port of mc-dev (Bukkit mc-dev 218a2a71):
/// <list type="bullet">
/// <item>Columns arrive with the server's light (0x33 carries the block and sky light nibbles): used as they are; only the
/// height map (Chunk.heightMap) is derived. A region without light arrays (fixtures, tests) is lit like
/// <c>Chunk.initLighting</c> (sky straight down, minus each block's opacity) plus each emitter's spread.</item>
/// <item>Every block change runs what the vanilla client and the server run for it: <c>Chunk.a(x,y,z,id,meta)</c>'s height
/// map update (<c>Chunk.g</c>: the column's sky light rewritten, World.g, Chunk.d on the 4 neighbour columns and itself) and
/// then <c>World.p</c> = <c>World.b(SKY)</c> + <c>World.b(BLOCK)</c> at the cell: the 32768-entry queue BFS (darken what the
/// old value lit within Manhattan 17, then re-brighten), each step losing <c>max(1, opacity)</c>.</item>
/// </list>
/// mc-dev's guards are kept over our loaded columns: World.b runs only when every column within 17 blocks is loaded
/// (<c>areChunksLoaded(x, y, z, 17)</c>; the server's own view edge behaves the same), Chunk.d within 16; reads of unloaded
/// columns return 0 like World.a(EnumSkyBlock) does. Without them a lone test column under a full floor cascades for minutes.
/// Incremental only: a torch is ≈ 3k cell visits (≪ 1 ms), never a per-tick recompute. Thread-safe: updates serialize on one
/// lock (network thread + local prediction); reads are lock-free byte reads.
/// </summary>
public sealed class LightEngine
{
    private readonly WorldState _w;
    private readonly int[] _h = new int[32768]; // mc-dev World.H
    private readonly object _sync = new();
    private long _updates, _visits, _ticks;

    private readonly int _height;

    internal LightEngine(WorldState world)
    {
        _w = world;
        _height = world.WorldHeight;
    }

    // A tiny direct-mapped column cache for the BFS (used only while _sync is held; reset at every entry point, so a column
    // replaced by a reload is never read through a stale reference across updates).
    private readonly ChunkColumn?[] _cache = new ChunkColumn?[16];
    private readonly long[] _cacheKey = new long[16];

    private void ResetCache() => Array.Clear(_cache);

    private ChunkColumn? Col(int x, int z)
    {
        int cx = x >> 4, cz = z >> 4;
        long key = ((long)cx << 32) | (uint)cz;
        int slot = (cx & 3) << 2 | (cz & 3);
        var c = _cache[slot];
        if (c != null && _cacheKey[slot] == key) return c;
        c = _w.ColumnAtRaw(x, z);
        if (c is not { HasData: true }) return null;
        _cache[slot] = c;
        _cacheKey[slot] = key;
        return c;
    }

    /// <summary><see cref="Get"/> through the cache (callers hold <see cref="_sync"/>).</summary>
    private int GetC(bool sky, int x, int y, int z)
    {
        if (y < 0) y = 0;
        if (y >= _height) y = _height - 1;
        var c = Col(x, z);
        if (c is null) return 0;
        int i = ChunkColumn.Index(x & 15, y, z & 15);
        return sky ? c.SkyLight[i] : c.BlockLight[i];
    }

    /// <summary>World.a(x0, y0, z0, x1, y1, z1) = areChunksLoaded: every column of the box has data and the box touches 0..height-1.</summary>
    private bool Loaded(int x0, int y0, int z0, int x1, int y1, int z1)
    {
        if (y1 < 0 || y0 >= _height) return false;
        for (int cx = x0 >> 4; cx <= x1 >> 4; cx++)
        for (int cz = z0 >> 4; cz <= z1 >> 4; cz++)
            if (Col(cx << 4, cz << 4) is null) return false;
        return true;
    }

    private bool Loaded(int x, int y, int z, int r) => Loaded(x - r, y - r, z - r, x + r, y + r, z + r);

    private int HeightC(int x, int z) => Col(x, z) is { } c ? c.HeightMap[(z & 15) << 4 | (x & 15)] : 0;

    /// <summary>Off = block changes leave the light arrays alone (the old behaviour). On by default.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>World.b calls run so far (cost telemetry: tests pin it).</summary>
    public long Updates => Interlocked.Read(ref _updates);
    /// <summary>Queue entries processed so far (cells looked at).</summary>
    public long Visits => Interlocked.Read(ref _visits);
    /// <summary>Total time spent in light updates.</summary>
    public TimeSpan Time => TimeSpan.FromTicks((long)(Interlocked.Read(ref _ticks) * (TimeSpan.TicksPerSecond / (double)Stopwatch.Frequency)));

    // ------------------------------------------------------------------ reads

    /// <summary>World.a(EnumSkyBlock, x, y, z): y clamped to 0..height-1, 0 for an unloaded column.</summary>
    public int Get(bool sky, int x, int y, int z)
    {
        if (y < 0) y = 0;
        if (y >= _height) y = _height - 1;
        var c = _w.ColumnAtRaw(x, z);
        if (c is null || !c.HasData) return 0;
        int i = ChunkColumn.Index(x & 15, y, z & 15);
        return sky ? c.SkyLight[i] : c.BlockLight[i];
    }

    /// <summary>Chunk.c(x, y, z): y ≥ the column's height map (an unloaded column: false, like EmptyChunk).</summary>
    public bool CanSeeSky(int x, int y, int z)
    {
        var c = _w.ColumnAtRaw(x, z);
        return c is { HasData: true } && y >= c.HeightMap[(z & 15) << 4 | (x & 15)];
    }

    /// <summary>The column's height map value (Chunk.b), or 0 when unloaded.</summary>
    public int Height(int x, int z)
    {
        var c = _w.ColumnAtRaw(x, z);
        return c is { HasData: true } ? c.HeightMap[(z & 15) << 4 | (x & 15)] : 0;
    }

    /// <summary>
    /// World.a(x, y, z, true) = getLightLevel with <paramref name="skylightSubtracted"/> as World.k: max(sky − k, block),
    /// except that a slab (44), farmland (60) or wooden/cobble stairs (53/67) cell reads the brightest of the cell above and its
    /// 4 side neighbours (mc-dev's exact id list: brick and stone-brick stairs are not in it).
    /// </summary>
    public int LightLevel(int x, int y, int z, int skylightSubtracted, bool neighbours = true)
    {
        if (neighbours)
        {
            int id = (uint)y < _height ? _w.GetBlock(x, y, z) : 0;
            if (id is 44 or 60 or 67 or 53)
            {
                int best = LightLevel(x, y + 1, z, skylightSubtracted, false);
                best = Math.Max(best, LightLevel(x + 1, y, z, skylightSubtracted, false));
                best = Math.Max(best, LightLevel(x - 1, y, z, skylightSubtracted, false));
                best = Math.Max(best, LightLevel(x, y, z + 1, skylightSubtracted, false));
                return Math.Max(best, LightLevel(x, y, z - 1, skylightSubtracted, false));
            }
        }
        if (y < 0) return 0;
        if (y >= _height) y = _height - 1;
        var c = _w.ColumnAtRaw(x, z);
        if (c is null || !c.HasData) return 0;
        int i = ChunkColumn.Index(x & 15, y, z & 15);
        int v = c.SkyLight[i] - skylightSubtracted, b = c.BlockLight[i];
        return b > v ? b : v;
    }

    // ------------------------------------------------------------------ sky darkening (World.k)

    private static readonly float[] Sin = BuildSin();

    private static float[] BuildSin()
    {
        var a = new float[65536];
        for (int i = 0; i < a.Length; i++) a[i] = (float)Math.Sin(i * 3.141592653589793D * 2.0D / 65536.0D);
        return a;
    }

    /// <summary>MathHelper.cos (the 65536-entry table).</summary>
    private static float Cos(float f) => Sin[(int)(f * 10430.378F + 16384.0F) & 0xffff];

    /// <summary>WorldProvider.a(time, f): the celestial angle.</summary>
    public static float CelestialAngle(long time, float partial = 1.0F)
    {
        int j = (int)(time % 24000L);
        float f1 = (j + partial) / 24000.0F - 0.25F;
        if (f1 < 0.0F) ++f1;
        if (f1 > 1.0F) --f1;
        float f2 = f1;
        f1 = 1.0F - (float)((Math.Cos(f1 * 3.141592653589793D) + 1.0D) / 2.0D);
        return f2 + (f1 - f2) / 3.0F;
    }

    /// <summary>
    /// World.a(1.0F) = World.k (skylightSubtracted) at <paramref name="timeOfDay"/>: 0 by day, 11 at night, less with
    /// <paramref name="rain"/>/<paramref name="thunder"/> strength 0..1 (each × 5/16 of the remaining brightness). During a
    /// thunderstorm EntityMonster spawn checks use 10 instead (World.t()).
    /// </summary>
    public static int SkylightSubtracted(long timeOfDay, float rain = 0f, float thunder = 0f)
    {
        float f1 = CelestialAngle(((timeOfDay % 24000) + 24000) % 24000);
        float f2 = 1.0F - (Cos(f1 * 3.1415927F * 2.0F) * 2.0F + 0.5F);
        if (f2 < 0.0F) f2 = 0.0F;
        if (f2 > 1.0F) f2 = 1.0F;
        f2 = 1.0F - f2;
        f2 = (float)(f2 * (1.0D - rain * 5.0F / 16.0D));
        f2 = (float)(f2 * (1.0D - thunder * rain * 5.0F / 16.0D));
        f2 = 1.0F - f2;
        return (int)(f2 * 11.0F);
    }

    /// <summary>The darkest World.k of a day (midnight, clear sky): 11. Spawn checks "after dark" use it.</summary>
    public const int NightSubtracted = 11;

    // ------------------------------------------------------------------ column data

    /// <summary>A region of live data arrived (0x33): height maps of its columns; with no light arrays, initLighting + emitters.</summary>
    internal void OnRegion(int originX, int originZ, int sizeX, int sizeZ, bool hasSky, bool hasBlock, bool spread = false)
    {
        if (!Enabled) return;
        long t0 = Stopwatch.GetTimestamp();
        lock (_sync)
        {
            ResetCache();
            var emitters = new List<BlockPos>();
            for (int x = originX; x < originX + sizeX; x++)
            for (int z = originZ; z < originZ + sizeZ; z++)
            {
                var c = _w.ColumnAtRaw(x, z);
                if (c is null) continue;
                int lx = x & 15, lz = z & 15;
                RecomputeHeight(c, lx, lz);
                if (!hasSky) InitSky(c, lx, lz);
                if (!hasBlock)
                {
                    for (int y = 0; y < _height; y++)
                    {
                        int i = ChunkColumn.Index(lx, y, lz);
                        c.BlockLight[i] = 0;
                        if (LightTables.Emission(c.Ids[i]) > 0) emitters.Add(new BlockPos(x, y, z));
                    }
                }
            }
            // Chunk.i(): every column initLighting marked gets its sky spread sideways (overhangs, the underside of an island).
            if (!hasSky && spread)
                for (int x = originX; x < originX + sizeX; x++)
                for (int z = originZ; z < originZ + sizeZ; z++)
                {
                    if (Col(x, z) is null || !Loaded((x >> 4) * 16 + 8, 64, (z >> 4) * 16 + 8, 16)) continue; // Chunk.i's guard
                    int l = HeightC(x, z);
                    int k1 = Math.Min(Math.Min(HeightC(x - 1, z), HeightC(x + 1, z)), Math.Min(HeightC(x, z - 1), HeightC(x, z + 1)));
                    ColumnF(x, z, k1);
                    ColumnF(x - 1, z, l);
                    ColumnF(x + 1, z, l);
                    ColumnF(x, z - 1, l);
                    ColumnF(x, z + 1, l);
                }
            foreach (var e in emitters) Update(false, e.X, e.Y, e.Z);
        }
        Interlocked.Add(ref _ticks, Stopwatch.GetTimestamp() - t0);
    }

    /// <summary>Chunk.f(x, z, y): relight the sky between a column's height and <paramref name="k"/>.</summary>
    private void ColumnF(int x, int z, int k)
    {
        int l = HeightC(x, z);
        if (l > k) ChunkD(x, z, k, l + 1);
        else if (l < k) ChunkD(x, z, l, k + 1);
    }

    /// <summary>The height map loop of Chunk.initLighting for one local column.</summary>
    private static void RecomputeHeight(ChunkColumn c, int lx, int lz)
    {
        int l = c.Height - 1;
        while (l > 0 && LightTables.Opacity(c.Ids[ChunkColumn.Index(lx, l - 1, lz)]) == 0) l--;
        c.HeightMap[lz << 4 | lx] = (short)l;
    }

    /// <summary>Chunk.initLighting's sky pass for one local column (no light array came with it).</summary>
    private static void InitSky(ChunkColumn c, int lx, int lz)
    {
        for (int half = 0; half < c.Height >> 7; half++) Array.Clear(c.SkyLight, half << 15 | (lx * 16 + lz) * ChunkColumn.SizeY, ChunkColumn.SizeY);
        int k1 = 15, l1 = c.Height - 1;
        do
        {
            int i = ChunkColumn.Index(lx, l1, lz);
            k1 -= LightTables.Opacity(c.Ids[i]);
            if (k1 > 0) c.SkyLight[i] = (byte)k1;
            --l1;
        } while (l1 > 0 && k1 > 0);
    }

    /// <summary>Re-lights a loaded column from its blocks alone, the full way (initLighting, then Chunk.i's sideways sky spread:
    /// overhangs, an island's underside, then every emitter). A region that arrives without light gets only the cheap
    /// initLighting pass + emitters.</summary>
    public void Relight(int chunkX, int chunkZ)
    {
        if (_w.GetColumn(chunkX, chunkZ) is not { HasData: true }) return;
        OnRegion(chunkX * 16, chunkZ * 16, 16, 16, hasSky: false, hasBlock: false, spread: true);
    }

    // ------------------------------------------------------------------ block change (Chunk.a + World.p)

    /// <summary>A block changed from <paramref name="oldId"/> (already written): Chunk.a's height map update, then World.p.</summary>
    internal void OnBlockChanged(int x, int y, int z, int oldId, int newId)
    {
        if (!Enabled) return;
        var c = _w.ColumnAtRaw(x, z);
        if (c is null || !c.HasData || (uint)y >= _height) return;
        long t0 = Stopwatch.GetTimestamp();
        lock (_sync)
        {
            ResetCache();
            int lx = x & 15, lz = z & 15;
            if (_w.Dimension != -1) // !worldProvider.e (the nether has no sky)
            {
                int k1 = c.HeightMap[lz << 4 | lx];
                if (LightTables.Opacity(newId) != 0)
                {
                    if (y >= k1) HeightUpdate(c, lx, y + 1, lz);
                }
                else if (y == k1 - 1) HeightUpdate(c, lx, y, lz);
            }
            Update(true, x, y, z);
            Update(false, x, y, z);
        }
        Interlocked.Add(ref _ticks, Stopwatch.GetTimestamp() - t0);
    }

    /// <summary>Chunk.g(x, y, z) (local x/z).</summary>
    private void HeightUpdate(ChunkColumn c, int lx, int j, int lz)
    {
        int l = c.HeightMap[lz << 4 | lx];
        int i1 = l;
        if (j > l) i1 = j;
        while (i1 > 0 && LightTables.Opacity(c.Ids[ChunkColumn.Index(lx, i1 - 1, lz)]) == 0) --i1;
        if (i1 == l) return;
        // mc-dev passes the LOCAL x/z to World.g here (this.world.g(i, k, i1, l)): it relights world column (lx, lz). Kept.
        WorldG(lx, lz, i1, l);
        c.HeightMap[lz << 4 | lx] = (short)i1;
        int wx = c.ChunkX * 16 + lx, wz = c.ChunkZ * 16 + lz;
        if (i1 < l) for (int y = i1; y < l; y++) c.SkyLight[ChunkColumn.Index(lx, y, lz)] = 15;
        else for (int y = l; y < i1; y++) c.SkyLight[ChunkColumn.Index(lx, y, lz)] = 0;
        int j2 = 15;
        while (i1 > 0 && j2 > 0)
        {
            --i1;
            int k2 = LightTables.Opacity(c.Ids[ChunkColumn.Index(lx, i1, lz)]);
            if (k2 == 0) k2 = 1;
            j2 -= k2;
            if (j2 < 0) j2 = 0;
            c.SkyLight[ChunkColumn.Index(lx, i1, lz)] = (byte)j2;
        }
        int b0 = c.HeightMap[lz << 4 | lx];
        int l2 = l, i3 = b0;
        if (b0 < l) { l2 = b0; i3 = l; }
        ChunkD(wx - 1, wz, l2, i3);
        ChunkD(wx + 1, wz, l2, i3);
        ChunkD(wx, wz - 1, l2, i3);
        ChunkD(wx, wz + 1, l2, i3);
        ChunkD(wx, wz, l2, i3);
    }

    /// <summary>World.g(x, z, y0, y1): sky relight of y0..y1 (inclusive) in one world column.</summary>
    private void WorldG(int x, int z, int k, int l)
    {
        if (k > l) (k, l) = (l, k);
        for (int y = k; y <= l; y++) Update(true, x, y, z);
    }

    /// <summary>Chunk.d(x, z, y0, y1): sky relight of y0..y1−1 in one world column.</summary>
    private void ChunkD(int x, int z, int k, int l)
    {
        if (l <= k || !Loaded(x, 64, z, 16)) return; // Chunk.d: areChunksLoaded(x, 64, z, 16)
        for (int y = k; y < l; y++) Update(true, x, y, z);
    }

    // ------------------------------------------------------------------ World.b(EnumSkyBlock, x, y, z)

    private void Set(bool sky, int x, int y, int z, int v)
    {
        if ((uint)y >= _height) return;
        var c = Col(x, z);
        if (c is null) return;
        int i = ChunkColumn.Index(x & 15, y, z & 15);
        if (sky) c.SkyLight[i] = (byte)v;
        else c.BlockLight[i] = (byte)v;
    }

    private int TypeId(int x, int y, int z) =>
        (uint)y < _height && Col(x, z) is { } c ? c.Ids[ChunkColumn.Index(x & 15, y, z & 15)] : 0;

    /// <summary>World.d: sky light a cell should have (15 under open sky, else the brightest neighbour minus opacity).</summary>
    private int SkyValue(int x, int y, int z, int opacity)
    {
        if (Col(x, z) is { } col && y >= col.HeightMap[(z & 15) << 4 | (x & 15)]) return 15;
        if (opacity == 0) opacity = 1;
        int best = 0;
        for (int n = 0; n < 6; n++)
        {
            int s = n % 2 * 2 - 1;
            int v = GetC(true, x + n / 2 % 3 / 2 * s, y + (n / 2 + 1) % 3 / 2 * s, z + (n / 2 + 2) % 3 / 2 * s) - opacity;
            if (v > best) best = v;
        }
        return best;
    }

    /// <summary>World.e: block light a cell should have (its emission, or the brightest neighbour minus opacity).</summary>
    private int BlockValue(int x, int y, int z, int id, int opacity)
    {
        int best = LightTables.Emission(id);
        int v;
        if ((v = GetC(false, x - 1, y, z) - opacity) > best) best = v;
        if ((v = GetC(false, x + 1, y, z) - opacity) > best) best = v;
        if ((v = GetC(false, x, y - 1, z) - opacity) > best) best = v;
        if ((v = GetC(false, x, y + 1, z) - opacity) > best) best = v;
        if ((v = GetC(false, x, y, z - 1) - opacity) > best) best = v;
        if ((v = GetC(false, x, y, z + 1) - opacity) > best) best = v;
        return best;
    }

    private int Value(bool sky, int x, int y, int z, int id, int opacity) =>
        sky ? SkyValue(x, y, z, opacity) : BlockValue(x, y, z, id, opacity);

    private static int Pack(int x, int y, int z, int i, int j, int k) => x - i + 32 + (y - j + 32 << 6) + (z - k + 32 << 12);

    /// <summary>World.b(EnumSkyBlock, x, y, z), the queue BFS. Callers hold <see cref="_sync"/>.</summary>
    private void Update(bool sky, int i, int j, int k)
    {
        if (!Loaded(i, j, k, 17)) return; // mc-dev areChunksLoaded(i, j, k, 17)
        Interlocked.Increment(ref _updates);
        var h = _h;
        int l = 0, i1 = 0;
        int j1 = GetC(sky, i, j, k);
        int k1 = TypeId(i, j, k);
        int l1 = LightTables.Opacity(k1);
        if (l1 == 0) l1 = 1;
        int i2 = Value(sky, i, j, k, k1, l1);
        int visits = 0;

        if (i2 > j1)
            h[i1++] = 133152;
        else if (i2 < j1)
        {
            h[i1++] = 133152 + (j1 << 18);
            while (l < i1)
            {
                int e = h[l++];
                int x = (e & 63) - 32 + i, y = (e >> 6 & 63) - 32 + j, z = (e >> 12 & 63) - 32 + k;
                int level = e >> 18 & 15;
                visits++;
                if (GetC(sky, x, y, z) != level) continue;
                Set(sky, x, y, z, 0);
                --level;
                if (level <= 0) continue;
                if (Math.Abs(x - i) + Math.Abs(y - j) + Math.Abs(z - k) >= 17) continue;
                for (int n = 0; n < 6; n++)
                {
                    int s = n % 2 * 2 - 1;
                    int nx = x + n / 2 % 3 / 2 * s, ny = y + (n / 2 + 1) % 3 / 2 * s, nz = z + (n / 2 + 2) % 3 / 2 * s;
                    if (GetC(sky, nx, ny, nz) == level && i1 < h.Length)
                        h[i1++] = Pack(nx, ny, nz, i, j, k) + (level << 18);
                }
            }
            l = 0;
        }

        while (l < i1)
        {
            int e = h[l++];
            int x = (e & 63) - 32 + i, y = (e >> 6 & 63) - 32 + j, z = (e >> 12 & 63) - 32 + k;
            visits++;
            int cur = GetC(sky, x, y, z);
            int id = TypeId(x, y, z);
            int op = LightTables.Opacity(id);
            if (op == 0) op = 1;
            int want = Value(sky, x, y, z, id, op);
            if (want == cur) continue;
            Set(sky, x, y, z, want);
            if (want <= cur) continue;
            if (Math.Abs(x - i) + Math.Abs(y - j) + Math.Abs(z - k) >= 17 || i1 >= h.Length - 6) continue;
            if (GetC(sky, x - 1, y, z) < want) h[i1++] = Pack(x - 1, y, z, i, j, k);
            if (GetC(sky, x + 1, y, z) < want) h[i1++] = Pack(x + 1, y, z, i, j, k);
            if (GetC(sky, x, y - 1, z) < want) h[i1++] = Pack(x, y - 1, z, i, j, k);
            if (GetC(sky, x, y + 1, z) < want) h[i1++] = Pack(x, y + 1, z, i, j, k);
            if (GetC(sky, x, y, z - 1) < want) h[i1++] = Pack(x, y, z - 1, i, j, k);
            if (GetC(sky, x, y, z + 1) < want) h[i1++] = Pack(x, y, z + 1, i, j, k);
        }
        Interlocked.Add(ref _visits, visits);
    }
}
