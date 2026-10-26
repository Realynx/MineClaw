namespace MineClaw.Protocol;

/// <summary>
/// A decoded 0x33 map-chunk region: an axis-aligned box of blocks with origin (X, Y, Z) in world block coordinates
/// and one byte per block in each array (nibbles already expanded). Index with
/// <c>Index(dx, dy, dz) = (dx * SizeZ + dz) * SizeY + dy</c>, where 0 ≤ dx &lt; SizeX etc.
/// For a full column X = cx·16, Y = 0, Z = cz·16 and the size is 16×128×16, so the index equals vanilla's
/// <c>x &lt;&lt; 11 | z &lt;&lt; 7 | y</c>.
/// </summary>
public sealed class ChunkRegion
{
    public int X { get; }
    public int Y { get; }
    public int Z { get; }
    public int SizeX { get; }
    public int SizeY { get; }
    public int SizeZ { get; }

    public byte[] BlockIds { get; }
    public byte[] Metadata { get; }
    public byte[] BlockLight { get; }
    public byte[] SkyLight { get; }

    public ChunkRegion(int x, int y, int z, int sizeX, int sizeY, int sizeZ)
    {
        if (sizeX < 0 || sizeY < 0 || sizeZ < 0) throw new ArgumentOutOfRangeException(nameof(sizeX), "sizes must be non-negative");
        X = x; Y = y; Z = z;
        SizeX = sizeX; SizeY = sizeY; SizeZ = sizeZ;
        int volume = sizeX * sizeY * sizeZ;
        BlockIds = new byte[volume];
        Metadata = new byte[volume];
        BlockLight = new byte[volume];
        SkyLight = new byte[volume];
    }

    public int Volume => SizeX * SizeY * SizeZ;

    /// <summary>True for a whole 16×128×16 column aligned to a chunk.</summary>
    public bool IsFullChunk => SizeX == 16 && SizeY == 128 && SizeZ == 16 && Y == 0 && (X & 15) == 0 && (Z & 15) == 0;

    public int Index(int dx, int dy, int dz) => (dx * SizeZ + dz) * SizeY + dy;

    public bool Contains(int worldX, int worldY, int worldZ) =>
        worldX >= X && worldX < X + SizeX && worldY >= Y && worldY < Y + SizeY && worldZ >= Z && worldZ < Z + SizeZ;

    public byte GetBlockId(int worldX, int worldY, int worldZ) => BlockIds[Index(worldX - X, worldY - Y, worldZ - Z)];
    public byte GetMetadata(int worldX, int worldY, int worldZ) => Metadata[Index(worldX - X, worldY - Y, worldZ - Z)];

    public override string ToString() => $"ChunkRegion({X},{Y},{Z} size {SizeX}x{SizeY}x{SizeZ})";
}

/// <summary>One block change in world coordinates (from 0x34 or 0x35).</summary>
public readonly record struct BlockChangeRecord(int X, int Y, int Z, byte BlockId, byte Metadata);
