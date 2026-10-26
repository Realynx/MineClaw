using System.IO.Compression;
using MineClaw.Protocol.Packets;

namespace MineClaw.Protocol.Tests;

public class ChunkTests
{
    private static byte[] Zlib(byte[] raw)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Fastest, leaveOpen: true)) z.Write(raw);
        return ms.ToArray();
    }

    [Fact]
    public void FullChunk_HandBuiltPayload_DecodesWithVanillaIndexing()
    {
        // Vanilla layout: ids[32768] (index x<<11|z<<7|y), meta[16384], blockLight[16384], skyLight[16384].
        var raw = new byte[81920];
        int idx = (1 << 11) | (3 << 7) | 2;             // (1,2,3) — even index -> low nibble
        raw[idx] = 7;                                    // bedrock
        raw[32768 + (idx >> 1)] = 0x05;                  // meta 5 at (1,2,3)
        int idx2 = (1 << 11) | (3 << 7) | 3;             // (1,3,3) — odd index -> high nibble
        raw[32768 + 16384 + (idx2 >> 1)] = 0x90;         // block light 9 at (1,3,3)
        raw[32768 + 32768 + 0] = 0x0F;                   // sky light 15 at (0,0,0)
        raw[(15 << 11) | (15 << 7) | 127] = 20;          // glass at the far corner

        var packet = new MapChunkPacket { X = 32, Y = 0, Z = -16, SizeX = 16, SizeY = 128, SizeZ = 16, CompressedData = Zlib(raw) };
        var wire = packet.ToBytes(PacketDirection.ClientBound);
        var read = Assert.IsType<MapChunkPacket>(TestUtil.ReadExact(wire, PacketDirection.ClientBound));
        Assert.Equal(16, read.SizeX);
        Assert.Equal(128, read.SizeY);

        var region = ChunkDataDecoder.Decode(read);
        Assert.True(region.IsFullChunk);
        Assert.Equal((32, 0, -16), (region.X, region.Y, region.Z));
        Assert.Equal(7, region.BlockIds[region.Index(1, 2, 3)]);
        Assert.Equal(idx, region.Index(1, 2, 3));
        Assert.Equal(7, region.GetBlockId(33, 2, -13));
        Assert.Equal(5, region.GetMetadata(33, 2, -13));
        Assert.Equal(0, region.Metadata[region.Index(1, 3, 3)]);
        Assert.Equal(9, region.BlockLight[region.Index(1, 3, 3)]);
        Assert.Equal(0, region.BlockLight[region.Index(1, 2, 3)]);
        Assert.Equal(15, region.SkyLight[region.Index(0, 0, 0)]);
        Assert.Equal(0, region.SkyLight[region.Index(0, 1, 0)]);
        Assert.Equal(20, region.GetBlockId(47, 127, -1));
        Assert.Equal(1, region.BlockIds.Count(b => b == 7));
    }

    [Fact]
    public void PartialRegion_HandBuiltPayload()
    {
        // Region x=21..22, y=10..13, z=7 (inside chunk (1,0)), as vanilla PlayerInstance sends after >=10 changes.
        var raw = new byte[]
        {
            1, 2, 3, 4, 5, 6, 7, 8,          // ids: column x=21 (y10..13), column x=22
            0x21, 0x43, 0x65, 0x87,          // meta: 1,2,3,4 | 5,6,7,8
            0xA9, 0xCB, 0xED, 0x0F,          // block light: 9,10,11,12 | 13,14,15,0
            0xFF, 0xFF, 0x00, 0x00,          // sky light: 15x4 | 0x4
        };
        var region = ChunkDataDecoder.Decode(21, 10, 7, 2, 4, 1, Zlib(raw));
        Assert.Equal((21, 10, 7, 2, 4, 1), (region.X, region.Y, region.Z, region.SizeX, region.SizeY, region.SizeZ));
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, region.BlockIds);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, region.Metadata);
        Assert.Equal(new byte[] { 9, 10, 11, 12, 13, 14, 15, 0 }, region.BlockLight);
        Assert.Equal(new byte[] { 15, 15, 15, 15, 0, 0, 0, 0 }, region.SkyLight);
        Assert.Equal(7, region.GetBlockId(22, 12, 7));
        Assert.Equal(raw, ChunkDataDecoder.Inflate(ChunkDataDecoder.Encode(region).CompressedData, 20));
    }

    [Fact]
    public void MultiChunkRegion_EncodeDecode_RoundTrip()
    {
        var rng = new Random(1234);
        var src = new ChunkRegion(14, 4, -2, 4, 8, 4); // spans chunks x 0..1, z -1..0
        rng.NextBytes(src.BlockIds);
        for (int i = 0; i < src.Volume; i++)
        {
            src.Metadata[i] = (byte)rng.Next(16);
            src.BlockLight[i] = (byte)rng.Next(16);
            src.SkyLight[i] = (byte)rng.Next(16);
        }
        var packet = ChunkDataDecoder.Encode(src);
        var back = ChunkDataDecoder.Decode(packet);
        Assert.Equal(src.BlockIds, back.BlockIds);
        Assert.Equal(src.Metadata, back.Metadata);
        Assert.Equal(src.BlockLight, back.BlockLight);
        Assert.Equal(src.SkyLight, back.SkyLight);
    }

    [Fact]
    public void YRangeAboveWorldTop_IsClampedLikeVanilla()
    {
        // PlayerInstance can send y=120, height 10; vanilla only serialises y 120..127.
        var raw = new byte[1 * 10 * 1 * 5 / 2];
        for (int i = 0; i < 8; i++) raw[i] = (byte)(i + 1);
        var region = ChunkDataDecoder.Decode(0, 120, 0, 1, 10, 1, Zlib(raw));
        Assert.Equal(120, region.Y);
        Assert.Equal(8, region.SizeY);
        Assert.Equal(8, region.BlockIds[region.Index(0, 7, 0)]);
    }

    [Fact]
    public void CorruptCompressedData_Throws()
    {
        Assert.Throws<ProtocolException>(() => ChunkDataDecoder.Decode(0, 0, 0, 16, 128, 16, [1, 2, 3, 4, 5]));
    }
}
