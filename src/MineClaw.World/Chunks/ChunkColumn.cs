namespace MineClaw.World;

/// <summary>
/// One 16x128x16 chunk column. Stores ids, metadata, block light and sky light unpacked (one byte per block).
/// Index layout matches the b1.8.1 chunk payload: <c>(x * 16 + z) * 128 + y</c> with local x/z in 0..15.
/// Single-byte reads are lock-free (bytes never tear); writers serialize on <see cref="Sync"/>.
/// </summary>
public sealed class ChunkColumn
{
    public const int SizeX = 16, SizeY = 128, SizeZ = 16, Volume = SizeX * SizeY * SizeZ;

    public int ChunkX { get; }
    public int ChunkZ { get; }

    internal readonly byte[] Ids = new byte[Volume];
    internal readonly byte[] Meta = new byte[Volume];
    internal readonly byte[] BlockLight = new byte[Volume];
    internal readonly byte[] SkyLight = new byte[Volume];

    /// <summary>Lock taken by writers. Readers do not need it for single-block reads.</summary>
    internal readonly object Sync = new();

    /// <summary>True once any block data has been received (0x33) — a column only pre-loaded via 0x32 is empty air.</summary>
    public bool HasData { get; internal set; }

    public ChunkColumn(int chunkX, int chunkZ)
    {
        ChunkX = chunkX;
        ChunkZ = chunkZ;
        Array.Fill(SkyLight, (byte)15);
    }

    public static int Index(int lx, int y, int lz) => (lx * SizeZ + lz) * SizeY + y;

    public int GetBlock(int lx, int y, int lz) => Ids[Index(lx, y, lz)];
    public int GetMeta(int lx, int y, int lz) => Meta[Index(lx, y, lz)];
    public int GetBlockLight(int lx, int y, int lz) => BlockLight[Index(lx, y, lz)];
    public int GetSkyLight(int lx, int y, int lz) => SkyLight[Index(lx, y, lz)];

    internal void Set(int lx, int y, int lz, int id, int meta)
    {
        int i = Index(lx, y, lz);
        Ids[i] = (byte)id;
        Meta[i] = (byte)(meta & 0x0F);
    }

    /// <summary>Highest y (0..127) holding a non-air block in a local column, or -1.</summary>
    public int HighestNonAir(int lx, int lz)
    {
        int baseIdx = (lx * SizeZ + lz) * SizeY;
        for (int y = SizeY - 1; y >= 0; y--)
            if (Ids[baseIdx + y] != 0) return y;
        return -1;
    }
}
