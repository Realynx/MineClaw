using System.IO.Compression;
using System.Runtime.InteropServices;

namespace MineClaw.World;

/// <summary>
/// A live column's light for the Live Hive viewer (the light nibbles a <see cref="ChunkSnapshot"/> leaves out), plus its biomes:
/// per 16³ section either one value every cell has (open sky above the terrain 0xF0, buried 0x00) or 4096 bytes
/// <c>sky &lt;&lt; 4 | block</c> in the section's local order (<c>lx &lt;&lt; 8 | lz &lt;&lt; 4 | (y &amp; 15)</c>), so a typical column costs a
/// few uniform bytes plus its two or three surface sections. <see cref="Hash"/> (64-bit, over sections and biomes) joins the
/// block hash in the viewer's delta key, so a light-only change (a torch lighting the next column, a cave opened) is resent too.
/// Wire form (<see cref="Encode"/>, deflated): <c>u8 version(=1) u8 sections u8 flags(1 = biomes)</c>, per section <c>u8 kind</c>
/// (0 uniform + <c>u8 value</c> | 1 full + 4096 bytes), then 256 biome bytes when flagged. Immutable.
/// </summary>
public sealed class LightSnapshot
{
    public const int SectionCells = ChunkColumn.SectionCells;
    private readonly byte[]?[] _full;
    private readonly byte[] _uniform;

    private LightSnapshot(byte[]?[] full, byte[] uniform, byte[]? biomes)
    {
        _full = full;
        _uniform = uniform;
        Biomes = biomes;
        Hash = ComputeHash();
    }

    /// <summary>Blocks per column covered (16 · sections).</summary>
    public int Height => _full.Length * 16;
    public int Sections => _full.Length;
    /// <summary>256 biome ids (index <c>lz · 16 + lx</c>) or null (b1.8.1).</summary>
    public byte[]? Biomes { get; }
    public long Hash { get; }
    /// <summary>Sections stored cell by cell (the rest are uniform).</summary>
    public int FullSections => _full.Count(s => s != null);

    private static int Local(int lx, int y, int lz) => (lx & 15) << 8 | (lz & 15) << 4 | (y & 15);

    /// <summary>Packed light (<c>sky &lt;&lt; 4 | block</c>) of a local cell; above the covered height open sky (0xF0).</summary>
    public int Packed(int lx, int y, int lz)
    {
        int s = y >> 4;
        if ((uint)s >= (uint)_full.Length) return 0xF0;
        return _full[s] is { } f ? f[Local(lx, y, lz)] : _uniform[s];
    }

    public int Sky(int lx, int y, int lz) => Packed(lx, y, lz) >> 4;
    public int Block(int lx, int y, int lz) => Packed(lx, y, lz) & 15;
    public int Biome(int lx, int lz) => Biomes is { } b ? b[(lz & 15) << 4 | (lx & 15)] : -1;

    /// <summary>True when section <paramref name="s"/> has the same light in both snapshots.</summary>
    public bool SameSection(LightSnapshot other, int s)
    {
        if (s >= Sections || s >= other.Sections) return Sections == other.Sections;
        var a = _full[s]; var b = other._full[s];
        if (a == null && b == null) return _uniform[s] == other._uniform[s];
        if (a == null || b == null) return false;
        return a.AsSpan().SequenceEqual(b);
    }

    /// <summary>Copies a live column's light (single-byte reads, no lock: a racing relight only means a slightly newer copy).</summary>
    public static LightSnapshot Capture(ChunkColumn c)
    {
        int n = c.SectionCount;
        var full = new byte[]?[n];
        var uniform = new byte[n];
        var buf = new byte[SectionCells];
        for (int s = 0; s < n; s++)
        {
            int u = c.PackSectionLight(s, buf);
            if (u >= 0) { uniform[s] = (byte)u; continue; }
            full[s] = buf;
            buf = new byte[SectionCells];
        }
        var biomes = c.Biomes is { Length: 256 } b ? (byte[])b.Clone() : null;
        return new LightSnapshot(full, uniform, biomes);
    }

    /// <summary>A snapshot from per-cell levels (tests, fixtures): <paramref name="sky"/>/<paramref name="block"/> of local (x, y, z).</summary>
    public static LightSnapshot From(int height, Func<int, int, int, int> sky, Func<int, int, int, int> block, byte[]? biomes = null)
    {
        int n = height >> 4;
        var full = new byte[]?[n];
        var uniform = new byte[n];
        for (int s = 0; s < n; s++)
        {
            var f = new byte[SectionCells];
            for (int x = 0; x < 16; x++)
                for (int z = 0; z < 16; z++)
                    for (int y = 0; y < 16; y++)
                        f[Local(x, y, z)] = (byte)((sky(x, s * 16 + y, z) & 15) << 4 | block(x, s * 16 + y, z) & 15);
            if (f.AsSpan().IndexOfAnyExcept(f[0]) < 0) uniform[s] = f[0];
            else full[s] = f;
        }
        return new LightSnapshot(full, uniform, biomes);
    }

    private const ulong Prime = 1099511628211UL;

    private long ComputeHash()
    {
        ulong h = 14695981039346656037UL ^ (ulong)_full.Length;
        for (int s = 0; s < _full.Length; s++)
            h = _full[s] is { } f ? Words(h, f) : (h ^ (0x100UL | _uniform[s])) * Prime;
        return Finish(Biomes is { } b ? Words(h, b) : h);
    }

    private static ulong Words(ulong h, ReadOnlySpan<byte> bytes)
    {
        foreach (ulong w in MemoryMarshal.Cast<byte, ulong>(bytes)) h = (h ^ w) * Prime;
        return h;
    }

    private static long Finish(ulong h) => unchecked((long)(h ^ h >> 29));

    /// <summary><see cref="Capture"/>(c).<see cref="Hash"/> without keeping the copy (the index hashes every live column each
    /// second: one reused section buffer, no allocation per column).</summary>
    public static long HashOf(ChunkColumn c)
    {
        Span<byte> buf = stackalloc byte[SectionCells];
        ulong h = 14695981039346656037UL ^ (ulong)c.SectionCount;
        for (int s = 0; s < c.SectionCount; s++)
        {
            int u = c.PackSectionLight(s, buf);
            h = u >= 0 ? (h ^ (0x100UL | (uint)u)) * Prime : Words(h, buf);
        }
        return Finish(c.Biomes is { Length: 256 } b ? Words(h, b) : h);
    }

    /// <summary>The viewer's delta key of a live column with light: the block hash and the light hash joined.</summary>
    public static long Combine(long blockHash, long lightHash) => unchecked(blockHash * -7046029254386353131L ^ lightHash);

    public byte[] Encode()
    {
        using var ms = new MemoryStream();
        using (var z = new DeflateStream(ms, CompressionLevel.Fastest, leaveOpen: true))
        {
            z.WriteByte(1);
            z.WriteByte((byte)_full.Length);
            z.WriteByte(Biomes != null ? (byte)1 : (byte)0);
            for (int s = 0; s < _full.Length; s++)
            {
                if (_full[s] is { } f) { z.WriteByte(1); z.Write(f); }
                else { z.WriteByte(0); z.WriteByte(_uniform[s]); }
            }
            if (Biomes != null) z.Write(Biomes);
        }
        return ms.ToArray();
    }

    /// <summary>Decodes <see cref="Encode"/>'s bytes; <see cref="InvalidDataException"/> on anything malformed.</summary>
    public static LightSnapshot Decode(byte[] data)
    {
        using var z = new DeflateStream(new MemoryStream(data), CompressionMode.Decompress);
        int Byte() => z.ReadByte() is var b and >= 0 ? b : throw new InvalidDataException("truncated light snapshot");
        void Fill(Span<byte> dst)
        {
            while (dst.Length > 0)
            {
                int n = z.Read(dst);
                if (n <= 0) throw new InvalidDataException("truncated light snapshot");
                dst = dst[n..];
            }
        }
        if (Byte() != 1) throw new InvalidDataException("light snapshot version unsupported");
        int sections = Byte(), flags = Byte();
        if (sections is not (8 or 16)) throw new InvalidDataException($"light snapshot: {sections} sections");
        var full = new byte[]?[sections];
        var uniform = new byte[sections];
        for (int s = 0; s < sections; s++)
        {
            int kind = Byte();
            if (kind == 0) uniform[s] = (byte)Byte();
            else if (kind == 1) { var f = new byte[SectionCells]; Fill(f); full[s] = f; }
            else throw new InvalidDataException("light snapshot: bad section kind");
        }
        byte[]? biomes = null;
        if ((flags & 1) != 0) { biomes = new byte[256]; Fill(biomes); }
        if (z.ReadByte() >= 0) throw new InvalidDataException("light snapshot too long");
        return new LightSnapshot(full, uniform, biomes);
    }
}
