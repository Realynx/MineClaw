using System.IO.Compression;
using MineClaw.Protocol.Packets;

namespace MineClaw.Protocol;

/// <summary>
/// Decodes 0x33 map-chunk payloads and 0x34 multi-block-change records.
/// <para>
/// 0x33 payload (after zlib inflate, SizeX·SizeY·SizeZ·5/2 bytes), as produced by vanilla
/// <c>World.getMultiChunkData</c>: for each chunk column touched by the region (chunk X outer, chunk Z inner) the
/// chunk contributes four consecutive sections — block ids (1 byte/block), metadata, block light, sky light (each
/// 4 bits/block). Within a section, data runs x-major, then z, then y (vanilla index <c>x&lt;&lt;11 | z&lt;&lt;7 | y</c>
/// restricted to the region), i.e. for each (x, z) a run of the region's Y range. In nibble sections the even
/// index is the low nibble. Vanilla clamps the Y range to 0..128 when building the payload; the decoded region
/// reports that clamped range. Vanilla servers only send regions inside a single chunk (full columns, or partial
/// updates after ≥10 changes), but multi-chunk regions are handled exactly as the server would build them.
/// </para>
/// </summary>
public static class ChunkDataDecoder
{
    public const int WorldHeight = 128;

    /// <summary>Inflates and unpacks a 0x33 packet.</summary>
    public static ChunkRegion Decode(MapChunkPacket packet) =>
        Decode(packet.X, packet.Y, packet.Z, packet.SizeX, packet.SizeY, packet.SizeZ, packet.CompressedData);

    public static ChunkRegion Decode(int x, int y, int z, int sizeX, int sizeY, int sizeZ, byte[] compressed)
    {
        var data = Inflate(compressed, sizeX * sizeY * sizeZ * 5 / 2);
        return Unpack(x, y, z, sizeX, sizeY, sizeZ, data);
    }

    /// <summary>Unpacks already-inflated payload bytes.</summary>
    public static ChunkRegion Unpack(int x, int y, int z, int sizeX, int sizeY, int sizeZ, byte[] data)
    {
        int y0 = Math.Max(y, 0);
        int y1 = Math.Min(y + sizeY, WorldHeight);
        int height = Math.Max(y1 - y0, 0);
        var region = new ChunkRegion(x, y0, z, sizeX, height, sizeZ);
        if (height == 0 || sizeX == 0 || sizeZ == 0) return region;

        int offset = 0;
        foreach (var (cx, lx0, lx1, cz, lz0, lz1) in Columns(x, z, sizeX, sizeZ))
        {
            // Block ids: one byte per block, run of `height` per (x,z).
            for (int lx = lx0; lx < lx1; lx++)
            for (int lz = lz0; lz < lz1; lz++)
            {
                Need(data, offset, height);
                int dst = region.Index(cx * 16 + lx - x, 0, cz * 16 + lz - z);
                Buffer.BlockCopy(data, offset, region.BlockIds, dst, height);
                offset += height;
            }
            offset = UnpackNibbles(data, offset, region.Metadata, region, cx, lx0, lx1, cz, lz0, lz1, y0, height);
            offset = UnpackNibbles(data, offset, region.BlockLight, region, cx, lx0, lx1, cz, lz0, lz1, y0, height);
            offset = UnpackNibbles(data, offset, region.SkyLight, region, cx, lx0, lx1, cz, lz0, lz1, y0, height);
        }
        return region;
    }

    private static int UnpackNibbles(byte[] data, int offset, byte[] target, ChunkRegion region,
        int cx, int lx0, int lx1, int cz, int lz0, int lz1, int y0, int height)
    {
        int bytes = height / 2;
        int firstY = y0 & ~1; // nibble pairs start on an even y (vanilla copies from index (..|y0) >> 1)
        for (int lx = lx0; lx < lx1; lx++)
        for (int lz = lz0; lz < lz1; lz++)
        {
            Need(data, offset, bytes);
            int dx = cx * 16 + lx - region.X;
            int dz = cz * 16 + lz - region.Z;
            int col = region.Index(dx, 0, dz);
            for (int k = 0; k < bytes; k++)
            {
                byte b = data[offset + k];
                int yLow = firstY + 2 * k, yHigh = yLow + 1;
                if (yLow >= y0 && yLow < y0 + height) target[col + (yLow - y0)] = (byte)(b & 0x0F);
                if (yHigh >= y0 && yHigh < y0 + height) target[col + (yHigh - y0)] = (byte)(b >> 4);
            }
            offset += bytes;
        }
        return offset;
    }

    /// <summary>
    /// Packs a region into a 0x33 packet exactly as the vanilla server would (inverse of <see cref="Decode(MapChunkPacket)"/>).
    /// The region's Y range must lie within 0..128.
    /// </summary>
    public static MapChunkPacket Encode(ChunkRegion region)
    {
        CheckEncodable(region);
        var data = new byte[region.SizeX * region.SizeY * region.SizeZ * 5 / 2];
        int offset = 0;
        foreach (var column in Columns(region.X, region.Z, region.SizeX, region.SizeZ))
        {
            offset = PackIds(region, column, data, offset);
            offset = PackNibbles(region, region.Metadata, column, data, offset);
            offset = PackNibbles(region, region.BlockLight, column, data, offset);
            offset = PackNibbles(region, region.SkyLight, column, data, offset);
        }
        return new MapChunkPacket
        {
            X = region.X, Y = (short)region.Y, Z = region.Z,
            SizeX = region.SizeX, SizeY = region.SizeY, SizeZ = region.SizeZ,
            CompressedData = Compress(data),
        };
    }

    private static void CheckEncodable(ChunkRegion region)
    {
        if (region.Y < 0 || region.Y + region.SizeY > WorldHeight)
            throw new ArgumentException("Region Y range must lie within 0..128", nameof(region));
        if (region.SizeX is < 1 or > 256 || region.SizeY is < 1 or > 256 || region.SizeZ is < 1 or > 256)
            throw new ArgumentException("Region sizes must be 1..256", nameof(region));
    }

    /// <summary>One column's block ids: a run of the region's height per (x, z).</summary>
    private static int PackIds(ChunkRegion region, (int Cx, int Lx0, int Lx1, int Cz, int Lz0, int Lz1) col, byte[] data, int offset)
    {
        int height = region.SizeY;
        for (int lx = col.Lx0; lx < col.Lx1; lx++)
        for (int lz = col.Lz0; lz < col.Lz1; lz++)
        {
            int src = region.Index(col.Cx * 16 + lx - region.X, 0, col.Cz * 16 + lz - region.Z);
            Buffer.BlockCopy(region.BlockIds, src, data, offset, height);
            offset += height;
        }
        return offset;
    }

    /// <summary>One column's nibble array (metadata or light): pairs start on an even y, cells outside the region are 0.</summary>
    private static int PackNibbles(ChunkRegion region, byte[] arr, (int Cx, int Lx0, int Lx1, int Cz, int Lz0, int Lz1) col, byte[] data, int offset)
    {
        int height = region.SizeY, y0 = region.Y, firstY = y0 & ~1;
        for (int lx = col.Lx0; lx < col.Lx1; lx++)
        for (int lz = col.Lz0; lz < col.Lz1; lz++)
        {
            int at = region.Index(col.Cx * 16 + lx - region.X, 0, col.Cz * 16 + lz - region.Z);
            for (int k = 0; k < height / 2; k++)
            {
                int yLow = firstY + 2 * k, yHigh = yLow + 1;
                int lo = yLow >= y0 && yLow < y0 + height ? arr[at + yLow - y0] & 0x0F : 0;
                int hi = yHigh >= y0 && yHigh < y0 + height ? arr[at + yHigh - y0] & 0x0F : 0;
                data[offset + k] = (byte)(lo | (hi << 4));
            }
            offset += height / 2;
        }
        return offset;
    }

    /// <summary>Chunk columns touched by the region and the local x/z ranges within each (vanilla order).</summary>
    private static IEnumerable<(int cx, int lx0, int lx1, int cz, int lz0, int lz1)> Columns(int x, int z, int sizeX, int sizeZ)
    {
        int cx0 = x >> 4, cx1 = (x + sizeX - 1) >> 4;
        int cz0 = z >> 4, cz1 = (z + sizeZ - 1) >> 4;
        for (int cx = cx0; cx <= cx1; cx++)
        {
            int lx0 = Math.Max(x - cx * 16, 0), lx1 = Math.Min(x + sizeX - cx * 16, 16);
            for (int cz = cz0; cz <= cz1; cz++)
            {
                int lz0 = Math.Max(z - cz * 16, 0), lz1 = Math.Min(z + sizeZ - cz * 16, 16);
                yield return (cx, lx0, lx1, cz, lz0, lz1);
            }
        }
    }

    private static void Need(byte[] data, int offset, int count)
    {
        if (offset + count > data.Length)
            throw new ProtocolException($"Map chunk payload too short: need {offset + count} bytes, have {data.Length}");
    }

    /// <summary>
    /// zlib-inflates into a buffer of exactly <paramref name="expectedLength"/> bytes. Like vanilla's
    /// <c>Inflater.inflate(buf)</c>, a short stream leaves the remainder zero and surplus output is ignored.
    /// </summary>
    public static byte[] Inflate(byte[] compressed, int expectedLength)
    {
        var output = new byte[expectedLength];
        try
        {
            using var z = new ZLibStream(new MemoryStream(compressed, writable: false), CompressionMode.Decompress);
            int total = 0;
            while (total < expectedLength)
            {
                int n = z.Read(output, total, expectedLength - total);
                if (n == 0) break;
                total += n;
            }
        }
        catch (InvalidDataException ex)
        {
            throw new ProtocolException("Bad compressed map chunk data: " + ex.Message, ex);
        }
        return output;
    }

    /// <summary>zlib-deflates (with zlib header, as java.util.zip.Deflater does by default).</summary>
    public static byte[] Compress(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            z.Write(data, 0, data.Length);
        return ms.ToArray();
    }

    /// <summary>Converts 0x34 records to world coordinates: x = cx·16 + (c &gt;&gt; 12 &amp; 15), z = cz·16 + (c &gt;&gt; 8 &amp; 15), y = c &amp; 255.</summary>
    public static IReadOnlyList<BlockChangeRecord> DecodeMultiBlockChange(MultiBlockChangePacket packet)
    {
        int n = packet.Coordinates.Length;
        if (packet.BlockIds.Length != n || packet.Metadata.Length != n)
            throw new ProtocolException("MultiBlockChange arrays have mismatched lengths");
        var list = new BlockChangeRecord[n];
        for (int i = 0; i < n; i++)
        {
            int c = (ushort)packet.Coordinates[i];
            list[i] = new BlockChangeRecord(
                packet.ChunkX * 16 + ((c >> 12) & 15),
                c & 255,
                packet.ChunkZ * 16 + ((c >> 8) & 15),
                packet.BlockIds[i],
                packet.Metadata[i]);
        }
        return list;
    }

    /// <summary>Builds a 0x34 coordinate short from local x (0..15), y (0..255), local z (0..15).</summary>
    public static short EncodeMultiBlockCoordinate(int localX, int y, int localZ) =>
        unchecked((short)(((localX & 15) << 12) | ((localZ & 15) << 8) | (y & 255)));
}
