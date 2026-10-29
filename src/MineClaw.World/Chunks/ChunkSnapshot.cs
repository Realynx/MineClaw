using System.IO.Compression;
using System.Text;

namespace MineClaw.World;

/// <summary>
/// Compact, light-free copy of a chunk column's blocks (ids + metadata) for the remembered world: deflated ids followed by
/// metadata packed two nibbles per byte. A Skyblock column (mostly air) compresses to well under a kilobyte; a dense one to a
/// few KB. <see cref="Hash"/> is a 64-bit FNV-1a over ids and metadata (what "unchanged since last seen" compares).
/// </summary>
public sealed class ChunkSnapshot
{
    /// <summary>Cells of a b1.8.1 (128-high) column. A 1.12.2 column holds twice as many (<see cref="ChunkColumn"/>'s layout:
    /// the upper half follows the lower one, so the lower half reads the same either way).</summary>
    public const int Volume = ChunkColumn.Volume;

    public byte[] Ids { get; }
    public byte[] Meta { get; }
    public long Hash { get; }
    /// <summary>Blocks per column: 128 or 256.</summary>
    public int Height { get; }

    public ChunkSnapshot(byte[] ids, byte[] meta)
    {
        if (ids.Length is not (Volume or 2 * Volume) || meta.Length != ids.Length) throw new ArgumentException("snapshot arrays must hold one column");
        Ids = ids;
        Meta = meta;
        Height = ids.Length / (ChunkColumn.SizeX * ChunkColumn.SizeZ);
        Hash = ComputeHash(ids, meta);
    }

    /// <summary>Copies a live column (no lock needed: single bytes never tear, and a racing change only means a slightly
    /// newer copy).</summary>
    public static ChunkSnapshot Capture(ChunkColumn c) => new(c.CopyIds(), c.CopyMeta()); // the column stores metadata as nibbles already

    public static long ComputeHash(ReadOnlySpan<byte> ids, ReadOnlySpan<byte> meta)
    {
        const ulong prime = 1099511628211UL;
        ulong h = 14695981039346656037UL;
        for (int i = 0; i < ids.Length; i++) { h ^= ids[i]; h *= prime; }
        for (int i = 0; i < meta.Length; i++) { h ^= (byte)(meta[i] & 0x0F); h *= prime; }
        return unchecked((long)h);
    }

    public bool IsEmpty
    {
        get
        {
            foreach (var b in Ids) if (b != 0) return false;
            return true;
        }
    }

    /// <summary>Deflated bytes (ids, then metadata nibbles).</summary>
    public byte[] Encode()
    {
        int n = Ids.Length;
        var packed = new byte[n + n / 2];
        Ids.CopyTo(packed, 0);
        for (int i = 0; i < n; i += 2) packed[n + i / 2] = (byte)((Meta[i] & 0x0F) | ((Meta[i + 1] & 0x0F) << 4));
        using var ms = new MemoryStream();
        using (var z = new DeflateStream(ms, CompressionLevel.Fastest, leaveOpen: true)) z.Write(packed);
        return ms.ToArray();
    }

    /// <summary>Decodes <see cref="Encode"/>'s bytes; the column height (128 or 256) follows from the inflated length.</summary>
    public static ChunkSnapshot Decode(byte[] data)
    {
        const int small = Volume + Volume / 2, large = 2 * small;
        var packed = new byte[large];
        int read = 0;
        using (var z = new DeflateStream(new MemoryStream(data), CompressionMode.Decompress))
        {
            while (read < packed.Length)
            {
                int n = z.Read(packed, read, packed.Length - read);
                if (n <= 0) break;
                read += n;
            }
            if (read == large && z.ReadByte() >= 0) throw new InvalidDataException("chunk snapshot too long");
        }
        if (read is not (small or large)) throw new InvalidDataException("truncated chunk snapshot");
        int v = read == small ? Volume : 2 * Volume;
        var ids = new byte[v];
        var meta = new byte[v];
        Array.Copy(packed, ids, v);
        for (int i = 0; i < v; i += 2)
        {
            byte b = packed[v + i / 2];
            meta[i] = (byte)(b & 0x0F);
            meta[i + 1] = (byte)(b >> 4);
        }
        return new ChunkSnapshot(ids, meta);
    }

    /// <summary>
    /// Block-count summary per 16-high section: "3:4=40,1=12;4:64=2" (section:id=count, most common first, non-air only,
    /// at most 6 ids per section). Small enough to store beside the hash.
    /// </summary>
    public string Summary()
    {
        var sb = new StringBuilder();
        var counts = new int[256];
        for (int s = 0; s < Height / 16; s++)
        {
            Array.Clear(counts);
            for (int x = 0; x < 16; x++)
            for (int z = 0; z < 16; z++)
            {
                int b = ChunkColumn.Index(x, s * 16, z); // 16 y run: never crosses the 128 half boundary
                for (int y = 0; y < 16; y++) counts[Ids[b + y]]++;
            }
            var top = Enumerable.Range(1, 255).Where(i => counts[i] > 0).OrderByDescending(i => counts[i]).Take(6).ToList();
            if (top.Count == 0) continue;
            if (sb.Length > 0) sb.Append(';');
            sb.Append(s).Append(':').Append(string.Join(",", top.Select(i => $"{i}={counts[i]}")));
        }
        return sb.ToString();
    }
}

/// <summary>
/// A chunk column known from an earlier visit (the remembered world): blocks as last seen, never live. Decoded lazily from the
/// stored snapshot. Planning may read it; movement and safety never do.
/// </summary>
public sealed class RememberedColumn
{
    private readonly Lazy<ChunkSnapshot> _blocks;

    public RememberedColumn(int chunkX, int chunkZ, int dimension, DateTimeOffset lastSeen, Func<ChunkSnapshot> load)
    {
        ChunkX = chunkX;
        ChunkZ = chunkZ;
        Dimension = dimension;
        LastSeen = lastSeen;
        _blocks = new Lazy<ChunkSnapshot>(load, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public RememberedColumn(int chunkX, int chunkZ, int dimension, DateTimeOffset lastSeen, ChunkSnapshot blocks)
        : this(chunkX, chunkZ, dimension, lastSeen, () => blocks) { }

    public int ChunkX { get; }
    public int ChunkZ { get; }
    public int Dimension { get; }
    public DateTimeOffset LastSeen { get; }

    public ChunkSnapshot Blocks => _blocks.Value;

    public int GetBlock(int lx, int y, int lz) => (uint)y >= Blocks.Height ? 0 : Blocks.Ids[ChunkColumn.Index(lx, y, lz)];
    public int GetMeta(int lx, int y, int lz) => (uint)y >= Blocks.Height ? 0 : Blocks.Meta[ChunkColumn.Index(lx, y, lz)];
}
