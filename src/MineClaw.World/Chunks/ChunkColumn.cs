namespace MineClaw.World;

/// <summary>
/// One 16×H×16 chunk column: H = 128 (b1.8.1, <see cref="SizeY"/>) or 256 (1.12.2, <see cref="MaxHeight"/>; the height comes
/// from the game version through <see cref="WorldState"/>). Stores ids, metadata, block light and sky light unpacked (one byte
/// per block). Block ids are bytes: 1.12.2's block registry ends at 255 too (minecraft-data blocks.json).
/// <para>Layout: two stacked 128-high halves, <see cref="Index"/> = <c>(y &gt;&gt; 7) &lt;&lt; 15 | (x * 16 + z) * 128 + (y &amp; 127)</c>.
/// For y &lt; 128 that is exactly the b1.8.1 payload index <c>(x * 16 + z) * 128 + y</c>, so a 128-high column, its snapshots
/// and every consumer that indexes the lower half are unchanged; a 256-high column just has a second half above it
/// (multi-version P1). Contiguous y runs exist only inside a half (<see cref="Index"/> of y and y + 1 differ by 1 unless y is 127).</para>
/// Single-byte reads are lock-free (bytes never tear); writers serialize on <see cref="Sync"/>.
/// </summary>
public sealed class ChunkColumn
{
    /// <summary>The b1.8.1 column height, and the height of one half of the layout.</summary>
    public const int SizeX = 16, SizeY = 128, SizeZ = 16, Volume = SizeX * SizeY * SizeZ;
    /// <summary>The tallest supported column (1.12.2).</summary>
    public const int MaxHeight = 256;

    public int ChunkX { get; }
    public int ChunkZ { get; }
    /// <summary>Blocks per column: 128 (b1.8.1) or 256 (1.12.2).</summary>
    public int Height { get; }
    /// <summary>Cells in this column (16 · 16 · <see cref="Height"/>).</summary>
    public int CellCount => Ids.Length;

    internal readonly byte[] Ids;
    internal readonly byte[] Meta;
    internal readonly byte[] BlockLight;
    internal readonly byte[] SkyLight;
    /// <summary>mc-dev Chunk.heightMap (index <c>lz &lt;&lt; 4 | lx</c>): the lowest y from which every block up to the top has light
    /// opacity 0 ("can see the sky" = y ≥ height). Kept by <see cref="LightEngine"/>; 0..<see cref="Height"/>.</summary>
    internal readonly short[] HeightMap = new short[SizeX * SizeZ];

    /// <summary>Lock taken by writers. Readers do not need it for single-block reads.</summary>
    internal readonly object Sync = new();

    /// <summary>True once any block data has been received (0x33) — a column only pre-loaded via 0x32 is empty air.</summary>
    public bool HasData { get; internal set; }

    /// <summary>Dimension the column belongs to (stamped by <see cref="WorldState"/> when it is created).</summary>
    public int Dimension { get; internal set; }

    /// <summary>The 256 biome ids of a full chunk (index <c>lz · 16 + lx</c>, 1.12.2 <c>Chunk.blockBiomeArray</c>), or null: b1.8.1's
    /// map chunk carries none (its biomes come from the seed) and a partial update leaves them as they were.</summary>
    public byte[]? Biomes { get; internal set; }

    /// <summary>The biome id of a local column, or −1 when the column has no biome data (b1.8.1).</summary>
    public int GetBiome(int lx, int lz) => Biomes is { } b ? b[(lz & 15) << 4 | (lx & 15)] : -1;

    /// <summary>Raw block ids (read-only view, <see cref="Index"/> layout); used for snapshots.</summary>
    public ReadOnlySpan<byte> IdData => Ids;

    /// <summary>Raw metadata, one byte per block (read-only view, <see cref="Index"/> layout); used for snapshots.</summary>
    public ReadOnlySpan<byte> MetaData => Meta;

    public ChunkColumn(int chunkX, int chunkZ) : this(chunkX, chunkZ, SizeY) { }

    public ChunkColumn(int chunkX, int chunkZ, int height)
    {
        if (height is not (SizeY or MaxHeight)) throw new ArgumentOutOfRangeException(nameof(height), height, "column height is 128 or 256");
        ChunkX = chunkX;
        ChunkZ = chunkZ;
        Height = height;
        int cells = SizeX * SizeZ * height;
        Ids = new byte[cells];
        Meta = new byte[cells];
        BlockLight = new byte[cells];
        SkyLight = new byte[cells];
        Array.Fill(SkyLight, (byte)15);
    }

    /// <summary>Cell index of local (x, y, z), y in 0..255 (see the class remarks).</summary>
    public static int Index(int lx, int y, int lz) => (y >> 7) << 15 | (lx * SizeZ + lz) * SizeY + (y & 127);

    public int GetBlock(int lx, int y, int lz) => Ids[Index(lx, y, lz)];
    public int GetMeta(int lx, int y, int lz) => Meta[Index(lx, y, lz)];
    public int GetBlockLight(int lx, int y, int lz) => BlockLight[Index(lx, y, lz)];
    public int GetSkyLight(int lx, int y, int lz) => SkyLight[Index(lx, y, lz)];
    /// <summary>mc-dev Chunk.b(x, z): the height map value of a local column.</summary>
    public int GetHeight(int lx, int lz) => HeightMap[lz << 4 | lx];

    internal void Set(int lx, int y, int lz, int id, int meta)
    {
        int i = Index(lx, y, lz);
        Ids[i] = (byte)id;
        Meta[i] = (byte)(meta & 0x0F);
    }

    /// <summary>Highest y (0..Height-1) holding a non-air block in a local column, or -1.</summary>
    public int HighestNonAir(int lx, int lz)
    {
        for (int half = (Height >> 7) - 1; half >= 0; half--)
        {
            int b = half << 15 | (lx * SizeZ + lz) * SizeY;
            for (int y = SizeY - 1; y >= 0; y--)
                if (Ids[b + y] != 0) return half << 7 | y;
        }
        return -1;
    }
}
