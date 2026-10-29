namespace MineClaw.World;

/// <summary>
/// One 16×H×16 chunk column: H = 128 (b1.8.1, <see cref="SizeY"/>) or 256 (1.12.2, <see cref="MaxHeight"/>; the height comes
/// from the game version through <see cref="WorldState"/>). Stores ids, metadata, block light and sky light unpacked (one byte
/// per block). Block ids are bytes: 1.12.2's block registry ends at 255 too (minecraft-data blocks.json).
/// <para>Storage: each layer is H/16 lazily allocated 16³ sections (4 KiB each). A section is allocated on the first write of a
/// value other than the layer's default (ids, metadata and block light 0, sky light 15); reading an unallocated section answers
/// the default. So the air above the terrain costs nothing, nor does block light where nothing glows, and a typical 1.12.2
/// column holds a few sections instead of 256 KiB. A section written back to all-default stays allocated.</para>
/// <para><see cref="Index"/> (the flat layout the snapshots and <see cref="IdData"/> use) is two stacked 128-high halves:
/// <c>(y &gt;&gt; 7) &lt;&lt; 15 | (x * 16 + z) * 128 + (y &amp; 127)</c>. For y &lt; 128 that is exactly the b1.8.1 payload index
/// <c>(x * 16 + z) * 128 + y</c>, so a 128-high column, its snapshots and every consumer that indexes the lower half are
/// unchanged (multi-version P1).</para>
/// Single-byte reads are lock-free (bytes never tear; a section is published fully initialised); writers serialize on
/// <see cref="Sync"/> (the light engine writes light under its own lock: section allocation is a compare-exchange, so racing
/// first writes never lose one another).
/// </summary>
public sealed class ChunkColumn
{
    /// <summary>The b1.8.1 column height, and the height of one half of the layout.</summary>
    public const int SizeX = 16, SizeY = 128, SizeZ = 16, Volume = SizeX * SizeY * SizeZ;
    /// <summary>The tallest supported column (1.12.2).</summary>
    public const int MaxHeight = 256;
    /// <summary>Cells of one 16³ section (and bytes of one allocated section of one layer).</summary>
    public const int SectionCells = 16 * 16 * 16;

    private const byte SkyDefault = 15;

    public int ChunkX { get; }
    public int ChunkZ { get; }
    /// <summary>Blocks per column: 128 (b1.8.1) or 256 (1.12.2).</summary>
    public int Height { get; }
    /// <summary>Cells in this column (16 · 16 · <see cref="Height"/>).</summary>
    public int CellCount => SizeX * SizeZ * Height;
    /// <summary>16³ sections per layer (8 or 16).</summary>
    public int SectionCount => Height >> 4;

    // Layers: section s holds y 16s..16s+15, cell (lx, y, lz) at Local = lx << 8 | lz << 4 | (y & 15) (a 16-cell y run per x/z).
    private readonly byte[]?[] _ids, _meta, _blockLight, _skyLight;

    /// <summary>mc-dev Chunk.heightMap (index <c>lz &lt;&lt; 4 | lx</c>): the lowest y from which every block up to the top has light
    /// opacity 0 ("can see the sky" = y ≥ height). Kept by <see cref="LightEngine"/>; 0..<see cref="Height"/>.</summary>
    internal readonly short[] HeightMap = new short[SizeX * SizeZ];

    /// <summary>Lock taken by writers. Readers do not need it for single-block reads.</summary>
    internal readonly object Sync = new();

    /// <summary>The views sharing this column when it lives in a <see cref="Chunks.SharedChunkStore"/> (replaced under
    /// <see cref="Sync"/>); null for a column private to one <see cref="WorldState"/>.</summary>
    internal WorldState[]? SharedHolders;

    /// <summary>True once any block data has been received (0x33) — a column only pre-loaded via 0x32 is empty air.</summary>
    public bool HasData { get; internal set; }

    /// <summary>Dimension the column belongs to (stamped by <see cref="WorldState"/> when it is created).</summary>
    public int Dimension { get; internal set; }

    /// <summary>The 256 biome ids of a full chunk (index <c>lz · 16 + lx</c>, 1.12.2 <c>Chunk.blockBiomeArray</c>), or null: b1.8.1's
    /// map chunk carries none (its biomes come from the seed) and a partial update leaves them as they were.</summary>
    public byte[]? Biomes { get; internal set; }

    /// <summary>The biome id of a local column, or −1 when the column has no biome data (b1.8.1).</summary>
    public int GetBiome(int lx, int lz) => Biomes is { } b ? b[(lz & 15) << 4 | (lx & 15)] : -1;

    /// <summary>Raw block ids in the flat <see cref="Index"/> layout. Allocates a fresh <see cref="CellCount"/>-byte copy per call
    /// (the column is stored in sections): read <see cref="GetBlock"/> for single cells.</summary>
    public ReadOnlySpan<byte> IdData => CopyIds();

    /// <summary>Raw metadata, one byte per block, in the flat <see cref="Index"/> layout. Allocates a copy per call, like
    /// <see cref="IdData"/>.</summary>
    public ReadOnlySpan<byte> MetaData => CopyMeta();

    public ChunkColumn(int chunkX, int chunkZ) : this(chunkX, chunkZ, SizeY) { }

    public ChunkColumn(int chunkX, int chunkZ, int height)
    {
        if (height is not (SizeY or MaxHeight)) throw new ArgumentOutOfRangeException(nameof(height), height, "column height is 128 or 256");
        ChunkX = chunkX;
        ChunkZ = chunkZ;
        Height = height;
        int sections = height >> 4;
        _ids = new byte[]?[sections];
        _meta = new byte[]?[sections];
        _blockLight = new byte[]?[sections];
        _skyLight = new byte[]?[sections];
    }

    /// <summary>Cell index of local (x, y, z), y in 0..255, in the flat layout (see the class remarks).</summary>
    public static int Index(int lx, int y, int lz) => (y >> 7) << 15 | (lx * SizeZ + lz) * SizeY + (y & 127);

    private static int Local(int lx, int y, int lz) => lx << 8 | lz << 4 | (y & 15);

    public int GetBlock(int lx, int y, int lz) => _ids[y >> 4] is { } s ? s[Local(lx, y, lz)] : 0;
    public int GetMeta(int lx, int y, int lz) => _meta[y >> 4] is { } s ? s[Local(lx, y, lz)] : 0;
    public int GetBlockLight(int lx, int y, int lz) => _blockLight[y >> 4] is { } s ? s[Local(lx, y, lz)] : 0;
    public int GetSkyLight(int lx, int y, int lz) => _skyLight[y >> 4] is { } s ? s[Local(lx, y, lz)] : SkyDefault;
    /// <summary>mc-dev Chunk.b(x, z): the height map value of a local column.</summary>
    public int GetHeight(int lx, int lz) => HeightMap[lz << 4 | lx];

    internal void Set(int lx, int y, int lz, int id, int meta)
    {
        int sec = y >> 4, i = Local(lx, y, lz);
        Put(_ids, sec, i, (byte)id, 0);
        Put(_meta, sec, i, (byte)(meta & 0x0F), 0);
    }

    internal void SetBlockLight(int lx, int y, int lz, int v) => Put(_blockLight, y >> 4, Local(lx, y, lz), (byte)v, 0);
    internal void SetSkyLight(int lx, int y, int lz, int v) => Put(_skyLight, y >> 4, Local(lx, y, lz), (byte)v, SkyDefault);

    private static void Put(byte[]?[] layer, int sec, int i, byte v, byte def)
    {
        var s = layer[sec];
        if (s is null)
        {
            if (v == def) return; // the unallocated section already reads as the default
            s = Allocate(layer, sec, def);
        }
        s[i] = v;
    }

    private static byte[] Allocate(byte[]?[] layer, int sec, byte def)
    {
        var s = new byte[SectionCells];
        if (def != 0) s.AsSpan().Fill(def);
        return Interlocked.CompareExchange(ref layer[sec], s, null) ?? s;
    }

    /// <summary>Writes a y run of one local x/z column: cell <c>y0 + k</c> gets ids[k], metadata/block light from
    /// <paramref name="meta"/>/<paramref name="blockLight"/> (low nibble; past their end 0) and sky light from
    /// <paramref name="skyLight"/> (low nibble; past its end 15). y0 .. y0 + ids.Length − 1 must lie inside the column.
    /// Callers hold <see cref="Sync"/>.</summary>
    internal void WriteRun(int lx, int lz, int y0, ReadOnlySpan<byte> ids, ReadOnlySpan<byte> meta, ReadOnlySpan<byte> blockLight,
        ReadOnlySpan<byte> skyLight)
    {
        PutRun(_ids, lx, lz, y0, ids, ids.Length, 0, 0xFF);
        PutRun(_meta, lx, lz, y0, meta, ids.Length, 0, 0x0F);
        PutRun(_blockLight, lx, lz, y0, blockLight, ids.Length, 0, 0x0F);
        PutRun(_skyLight, lx, lz, y0, skyLight, ids.Length, SkyDefault, 0x0F);
    }

    private static void PutRun(byte[]?[] layer, int lx, int lz, int y0, ReadOnlySpan<byte> src, int count, byte def, int mask)
    {
        for (int k = 0; k < count;)
        {
            int y = y0 + k, sec = y >> 4, n = Math.Min(16 - (y & 15), count - k);
            int at = Local(lx, y, lz);
            var s = layer[sec];
            for (int j = 0; j < n; j++, k++)
            {
                byte v = k < src.Length ? (byte)(src[k] & mask) : def;
                if (s is null)
                {
                    if (v == def) continue;
                    s = Allocate(layer, sec, def);
                }
                s[at + j] = v;
            }
        }
    }

    /// <summary>Highest y (0..Height-1) holding a non-air block in a local column, or -1.</summary>
    public int HighestNonAir(int lx, int lz)
    {
        int b = lx << 8 | lz << 4;
        for (int sec = _ids.Length - 1; sec >= 0; sec--)
        {
            if (_ids[sec] is not { } s) continue;
            for (int y = 15; y >= 0; y--)
                if (s[b + y] != 0) return sec << 4 | y;
        }
        return -1;
    }

    /// <summary>True when the section holding y has no block ids allocated (all air): scans may skip its 16 cells.</summary>
    internal bool IsAirSection(int y) => _ids[y >> 4] is null;

    /// <summary>A copy of the block ids in the flat <see cref="Index"/> layout.</summary>
    public byte[] CopyIds() => Flatten(_ids, 0);

    /// <summary>A copy of the metadata (low nibbles) in the flat <see cref="Index"/> layout.</summary>
    public byte[] CopyMeta() => Flatten(_meta, 0);

    /// <summary>
    /// The light of 16³ section <paramref name="section"/> packed one byte per cell, <c>sky &lt;&lt; 4 | block</c>, in the section's
    /// local order (<c>lx &lt;&lt; 8 | lz &lt;&lt; 4 | (y &amp; 15)</c>) into <paramref name="dst"/> (≥ <see cref="SectionCells"/>), for the
    /// Live Hive light sync (<see cref="LightSnapshot"/>). Returns the value every cell has when the section is uniform (open sky
    /// above the terrain: 0xF0; buried: 0x00) — <paramref name="dst"/> is then not written — or −1 when it was filled.
    /// </summary>
    public int PackSectionLight(int section, Span<byte> dst)
    {
        var sky = _skyLight[section];
        var blk = _blockLight[section];
        if (sky is null && blk is null) return SkyDefault << 4;
        dst = dst[..SectionCells];
        if (sky is null) for (int i = 0; i < SectionCells; i++) dst[i] = (byte)(SkyDefault << 4 | blk![i] & 15);
        else if (blk is null) for (int i = 0; i < SectionCells; i++) dst[i] = (byte)((sky[i] & 15) << 4);
        else for (int i = 0; i < SectionCells; i++) dst[i] = (byte)((sky[i] & 15) << 4 | blk[i] & 15);
        byte first = dst[0];
        return dst.IndexOfAnyExcept(first) < 0 ? first : -1;
    }

    private byte[] Flatten(byte[]?[] layer, byte def)
    {
        var r = new byte[CellCount];
        if (def != 0) r.AsSpan().Fill(def);
        for (int sec = 0; sec < layer.Length; sec++)
        {
            if (layer[sec] is not { } s) continue;
            int dst = (sec >> 3) << 15 | (sec & 7) << 4;
            for (int xz = 0; xz < 256; xz++) s.AsSpan(xz << 4, 16).CopyTo(r.AsSpan(dst + xz * SizeY, 16));
        }
        return r;
    }

    /// <summary><see cref="ChunkSnapshot.ComputeHash"/> of <see cref="IdData"/> and <see cref="MetaData"/> without copying them
    /// (the same 64-bit FNV-1a, walked in the flat <see cref="Index"/> order).</summary>
    public long ContentHash()
    {
        const ulong prime = 1099511628211UL;
        ulong h = 14695981039346656037UL;
        foreach (var layer in (ReadOnlySpan<byte[]?[]>)[_ids, _meta])
            for (int half = 0; half < Height >> 7; half++)
            for (int xz = 0; xz < 256; xz++)
            for (int sec = half << 3; sec < (half + 1) << 3; sec++)
            {
                if (layer[sec] is not { } s) { for (int k = 0; k < 16; k++) h *= prime; continue; } // 16 zero bytes: h ^= 0 is a no-op
                for (int k = xz << 4, e = k + 16; k < e; k++) { h ^= s[k]; h *= prime; }
            }
        return unchecked((long)h);
    }

    /// <summary>Allocated 16³ sections over the four layers.</summary>
    public int AllocatedSections
    {
        get
        {
            int n = 0;
            for (int s = 0; s < _ids.Length; s++)
                n += (_ids[s] is null ? 0 : 1) + (_meta[s] is null ? 0 : 1) + (_blockLight[s] is null ? 0 : 1) + (_skyLight[s] is null ? 0 : 1);
            return n;
        }
    }

    /// <summary>Approximate managed bytes this column holds: its allocated sections, the height map, biomes and the fixed
    /// object overhead (section tables, lock, the object itself).</summary>
    public long ApproximateBytes =>
        (long)AllocatedSections * (SectionCells + 24) + 4 * (24 + 8L * SectionCount) + 24 + 2 * HeightMap.Length + (Biomes is null ? 0 : 280) + 120;
}
