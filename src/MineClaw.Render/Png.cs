using System.Buffers.Binary;
using System.IO.Compression;

namespace MineClaw.Render;

/// <summary>
/// Minimal managed PNG encoder: 8-bit indexed (PLTE) when the image has at most 256 colours — pixel art almost always does —
/// else 8-bit RGB; filter 0 on every row; zlib deflate from the BCL (<see cref="ZLibStream"/>, no extra packages or native
/// libraries beyond the runtime itself). Pixels are 0xRRGGBB, row-major.
/// </summary>
public static class Png
{
    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];

    public static byte[] Encode(ReadOnlySpan<uint> px, int width, int height)
    {
        if (width <= 0 || height <= 0 || px.Length < width * height) throw new ArgumentException("bad image size");
        var (index, palette) = Palette(px, width * height);
        bool indexed = index != null;
        var raw = Scanlines(px, width, height, index);

        byte[] idat;
        using (var ms = new MemoryStream())
        {
            using (var z = new ZLibStream(ms, CompressionLevel.SmallestSize, leaveOpen: true)) z.Write(raw);
            idat = ms.ToArray();
        }

        using var png = new MemoryStream(idat.Length + 64 + palette.Count * 3);
        png.Write(Signature);
        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr[4..], height);
        ihdr[8] = 8;                     // bit depth
        ihdr[9] = (byte)(indexed ? 3 : 2); // colour type: palette / truecolour
        ihdr[10] = ihdr[11] = ihdr[12] = 0; // deflate, adaptive filtering (all rows 0), no interlace
        Chunk(png, "IHDR", ihdr);
        if (indexed) Chunk(png, "PLTE", PlteBytes(palette));
        Chunk(png, "IDAT", idat);
        Chunk(png, "IEND", []);
        return png.ToArray();
    }

    /// <summary>The palette pass: the colour → index map and the palette, or a null map past 256 colours (RGB then; the palette
    /// stops at 256).</summary>
    private static (Dictionary<uint, byte>? Index, List<uint> Palette) Palette(ReadOnlySpan<uint> px, int n)
    {
        var index = new Dictionary<uint, byte>();
        var palette = new List<uint>(256);
        for (int i = 0; i < n; i++)
        {
            uint c = px[i] & 0xffffff;
            if (index.ContainsKey(c)) continue;
            if (palette.Count == 256) return (null, palette);
            index[c] = (byte)palette.Count;
            palette.Add(c);
        }
        return (index, palette);
    }

    /// <summary>The raw image data: each row a filter byte (0) then palette indices or RGB triples.</summary>
    private static byte[] Scanlines(ReadOnlySpan<uint> px, int width, int height, Dictionary<uint, byte>? index)
    {
        int bpp = index != null ? 1 : 3, stride = width * bpp + 1;
        var raw = new byte[stride * height];
        for (int y = 0; y < height; y++)
        {
            int o = y * stride;
            raw[o++] = 0; // filter: none
            for (int x = 0; x < width; x++)
            {
                uint c = px[y * width + x] & 0xffffff;
                if (index != null) raw[o++] = index[c];
                else { raw[o++] = (byte)(c >> 16); raw[o++] = (byte)(c >> 8); raw[o++] = (byte)c; }
            }
        }
        return raw;
    }

    private static byte[] PlteBytes(List<uint> palette)
    {
        var plte = new byte[palette.Count * 3];
        for (int i = 0; i < palette.Count; i++)
        {
            plte[3 * i] = (byte)(palette[i] >> 16);
            plte[3 * i + 1] = (byte)(palette[i] >> 8);
            plte[3 * i + 2] = (byte)palette[i];
        }
        return plte;
    }
    private static void Chunk(Stream s, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> head = stackalloc byte[8];
        BinaryPrimitives.WriteInt32BigEndian(head, data.Length);
        for (int i = 0; i < 4; i++) head[4 + i] = (byte)type[i];
        s.Write(head);
        s.Write(data);
        uint crc = Crc32.Update(Crc32.Update(0xffffffff, head[4..]), data) ^ 0xffffffff;
        Span<byte> tail = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(tail, crc);
        s.Write(tail);
    }

    /// <summary>(width, height, colour type) from a PNG header, or null when it isn't one.</summary>
    public static (int Width, int Height, int ColourType)? Header(ReadOnlySpan<byte> png)
    {
        if (png.Length < 33 || !png[..8].SequenceEqual(Signature)) return null;
        return (BinaryPrimitives.ReadInt32BigEndian(png[16..]), BinaryPrimitives.ReadInt32BigEndian(png[20..]), png[25]);
    }

    /// <summary>
    /// Minimal decoder for the atlases (terrain.png, items.png) and tests: 8-bit greyscale / RGB / palette / grey+alpha / RGBA
    /// (palette also at 1/2/4 bits, with tRNS), filters 0-4, no interlace. Returns 0xAARRGGBB pixels.
    /// </summary>
    public static (int Width, int Height, uint[] Argb) Decode(ReadOnlySpan<byte> png)
    {
        if (Header(png) is not var (w, h, ct)) throw new InvalidDataException("not a PNG");
        int depth = png[24];
        if (png[28] != 0) throw new NotSupportedException("interlaced PNG");
        using var idat = new MemoryStream();
        var (plte, trns) = ReadChunks(png, idat);
        int channels = Channels(ct);
        CheckDepth(ct, depth);
        int bitsPerPx = channels * depth, stride = (w * bitsPerPx + 7) / 8, bpp = Math.Max(1, bitsPerPx / 8);
        var raw = new byte[(stride + 1) * h];
        idat.Position = 0;
        using (var z = new ZLibStream(idat, CompressionMode.Decompress)) z.ReadExactly(raw);
        var cur = new byte[stride];
        var prev = new byte[stride];
        var outPx = new uint[w * h];
        var fmt = new Format(ct, depth, plte, trns);
        for (int y = 0; y < h; y++)
        {
            Unfilter(raw[y * (stride + 1)], raw.AsSpan(y * (stride + 1) + 1, stride), cur, prev, bpp);
            for (int x = 0; x < w; x++) outPx[y * w + x] = PixelAt(fmt, cur, x);
            (cur, prev) = (prev, cur);
        }
        return (w, h, outPx);
    }

    private static int Channels(int ct) => ct switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => throw new NotSupportedException($"colour type {ct}") };

    private static void CheckDepth(int ct, int depth)
    {
        if (depth != 8 && !(ct == 3 && depth is 1 or 2 or 4)) throw new NotSupportedException($"bit depth {depth}");
    }

    /// <summary>Collects the IDAT data into <paramref name="idat"/>; returns the palette (opaque ARGB) and tRNS alphas.</summary>
    private static (uint[] Plte, byte[] Trns) ReadChunks(ReadOnlySpan<byte> png, MemoryStream idat)
    {
        uint[] plte = [];
        byte[] trns = [];
        int o = 8;
        while (o + 8 <= png.Length)
        {
            int len = BinaryPrimitives.ReadInt32BigEndian(png[o..]);
            var type = System.Text.Encoding.ASCII.GetString(png.Slice(o + 4, 4));
            var data = png.Slice(o + 8, len);
            if (type == "IDAT") idat.Write(data);
            else if (type == "PLTE") plte = PaletteOf(data);
            else if (type == "tRNS") trns = data.ToArray();
            else if (type == "IEND") break;
            o += 12 + len;
        }
        return (plte, trns);
    }

    private static uint[] PaletteOf(ReadOnlySpan<byte> data)
    {
        var plte = new uint[data.Length / 3];
        for (int i = 0; i < plte.Length; i++) plte[i] = 0xff000000u | (uint)data[3 * i] << 16 | (uint)data[3 * i + 1] << 8 | data[3 * i + 2];
        return plte;
    }

    /// <summary>One scanline through its filter (0 none, 1 sub, 2 up, 3 average, 4 Paeth) into <paramref name="cur"/>.</summary>
    private static void Unfilter(int f, ReadOnlySpan<byte> line, byte[] cur, byte[] prev, int bpp)
    {
        for (int i = 0; i < line.Length; i++)
        {
            int a = i >= bpp ? cur[i - bpp] : 0, b = prev[i], c = i >= bpp ? prev[i - bpp] : 0;
            int v = line[i];
            v += f switch
            {
                1 => a,
                2 => b,
                3 => (a + b) >> 1,
                4 => Paeth(a, b, c),
                _ => 0,
            };
            cur[i] = (byte)v;
        }
    }

    /// <summary>The image's colour type, bit depth, palette and palette alphas.</summary>
    private sealed record Format(int ColourType, int Depth, uint[] Plte, byte[] Trns);

    /// <summary>Pixel <paramref name="x"/> of an unfiltered scanline as 0xAARRGGBB.</summary>
    private static uint PixelAt(Format fmt, byte[] cur, int x)
    {
        switch (fmt.ColourType)
        {
            case 0: { byte g = cur[x]; return 0xff000000u | (uint)g << 16 | (uint)g << 8 | g; }
            case 2: return 0xff000000u | (uint)cur[3 * x] << 16 | (uint)cur[3 * x + 1] << 8 | cur[3 * x + 2];
            case 4: { byte g = cur[2 * x]; return (uint)cur[2 * x + 1] << 24 | (uint)g << 16 | (uint)g << 8 | g; }
            case 6: return (uint)cur[4 * x + 3] << 24 | (uint)cur[4 * x] << 16 | (uint)cur[4 * x + 1] << 8 | cur[4 * x + 2];
            default:
            {
                int depth = fmt.Depth, bit = x * depth, idx = (cur[bit >> 3] >> (8 - depth - (bit & 7))) & ((1 << depth) - 1);
                uint p = idx < fmt.Plte.Length ? fmt.Plte[idx] : 0xff000000u;
                if (idx < fmt.Trns.Length) p = (p & 0xffffff) | (uint)fmt.Trns[idx] << 24;
                return p;
            }
        }
    }
    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    internal static class Crc32
    {
        private static readonly uint[] Table = Make();

        private static uint[] Make()
        {
            var t = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xedb88320u ^ (c >> 1) : c >> 1;
                t[n] = c;
            }
            return t;
        }

        public static uint Update(uint crc, ReadOnlySpan<byte> data)
        {
            foreach (var b in data) crc = Table[(crc ^ b) & 0xff] ^ (crc >> 8);
            return crc;
        }
    }
}
