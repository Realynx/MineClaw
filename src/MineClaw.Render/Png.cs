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
        // Palette pass: stop at 257 colours (RGB then).
        var index = new Dictionary<uint, byte>();
        var palette = new List<uint>(256);
        bool indexed = true;
        for (int i = 0; i < width * height; i++)
        {
            uint c = px[i] & 0xffffff;
            if (index.ContainsKey(c)) continue;
            if (palette.Count == 256) { indexed = false; break; }
            index[c] = (byte)palette.Count;
            palette.Add(c);
        }

        int bpp = indexed ? 1 : 3, stride = width * bpp + 1;
        var raw = new byte[stride * height];
        for (int y = 0; y < height; y++)
        {
            int o = y * stride;
            raw[o++] = 0; // filter: none
            for (int x = 0; x < width; x++)
            {
                uint c = px[y * width + x] & 0xffffff;
                if (indexed) raw[o++] = index[c];
                else { raw[o++] = (byte)(c >> 16); raw[o++] = (byte)(c >> 8); raw[o++] = (byte)c; }
            }
        }

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
        if (indexed)
        {
            var plte = new byte[palette.Count * 3];
            for (int i = 0; i < palette.Count; i++)
            {
                plte[3 * i] = (byte)(palette[i] >> 16);
                plte[3 * i + 1] = (byte)(palette[i] >> 8);
                plte[3 * i + 2] = (byte)palette[i];
            }
            Chunk(png, "PLTE", plte);
        }
        Chunk(png, "IDAT", idat);
        Chunk(png, "IEND", []);
        return png.ToArray();
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
        uint[] plte = [];
        byte[] trns = [];
        int o = 8;
        while (o + 8 <= png.Length)
        {
            int len = BinaryPrimitives.ReadInt32BigEndian(png[o..]);
            var type = System.Text.Encoding.ASCII.GetString(png.Slice(o + 4, 4));
            var data = png.Slice(o + 8, len);
            if (type == "IDAT") idat.Write(data);
            else if (type == "PLTE") { plte = new uint[len / 3]; for (int i = 0; i < plte.Length; i++) plte[i] = 0xff000000u | (uint)data[3 * i] << 16 | (uint)data[3 * i + 1] << 8 | data[3 * i + 2]; }
            else if (type == "tRNS") trns = data.ToArray();
            else if (type == "IEND") break;
            o += 12 + len;
        }
        int channels = ct switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => throw new NotSupportedException($"colour type {ct}") };
        if (depth != 8 && !(ct == 3 && depth is 1 or 2 or 4)) throw new NotSupportedException($"bit depth {depth}");
        int bitsPerPx = channels * depth, stride = (w * bitsPerPx + 7) / 8, bpp = Math.Max(1, bitsPerPx / 8);
        var raw = new byte[(stride + 1) * h];
        idat.Position = 0;
        using (var z = new ZLibStream(idat, CompressionMode.Decompress)) z.ReadExactly(raw);
        var cur = new byte[stride];
        var prev = new byte[stride];
        var outPx = new uint[w * h];
        for (int y = 0; y < h; y++)
        {
            int f = raw[y * (stride + 1)];
            var line = raw.AsSpan(y * (stride + 1) + 1, stride);
            for (int i = 0; i < stride; i++)
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
            for (int x = 0; x < w; x++)
            {
                uint p;
                switch (ct)
                {
                    case 0: { byte g = cur[x]; p = 0xff000000u | (uint)g << 16 | (uint)g << 8 | g; break; }
                    case 2: p = 0xff000000u | (uint)cur[3 * x] << 16 | (uint)cur[3 * x + 1] << 8 | cur[3 * x + 2]; break;
                    case 4: { byte g = cur[2 * x]; p = (uint)cur[2 * x + 1] << 24 | (uint)g << 16 | (uint)g << 8 | g; break; }
                    case 6: p = (uint)cur[4 * x + 3] << 24 | (uint)cur[4 * x] << 16 | (uint)cur[4 * x + 1] << 8 | cur[4 * x + 2]; break;
                    default:
                    {
                        int bit = x * depth, idx = (cur[bit >> 3] >> (8 - depth - (bit & 7))) & ((1 << depth) - 1);
                        p = idx < plte.Length ? plte[idx] : 0xff000000u;
                        if (idx < trns.Length) p = (p & 0xffffff) | (uint)trns[idx] << 24;
                        break;
                    }
                }
                outPx[y * w + x] = p;
            }
            (cur, prev) = (prev, cur);
        }
        return (w, h, outPx);
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
