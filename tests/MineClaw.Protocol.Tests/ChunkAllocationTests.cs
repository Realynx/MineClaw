using MineClaw.Protocol.Packets;
using MineClaw.Protocol.Release1122;
using Xunit.Abstractions;

namespace MineClaw.Protocol.Tests;

public sealed class ChunkAllocationTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(true, 0)]
    [InlineData(true, 0x211)]
    [InlineData(false, 0x8001)]
    [InlineData(false, 0xFFFF)]
    public void Compact_full_chunks_preserve_every_sent_cell_and_biomes(bool hasSky, int mask)
    {
        var sections = new int[]?[16];
        for (int s = 0; s < 16; s++)
            if ((mask & 1 << s) != 0) sections[s] = Enumerable.Range(0, 4096).Select(i => 35 << 4 | (i + s) % 16).ToArray();
        var body = ChunkData.EncodeBody(-3, 7, true, null, sections, hasSky, 6, 11, 4);
        var expanded = ChunkData.Decode(body, hasSky);
        var compact = ChunkData.Decode(body, hasSky, compact: true);
        Assert.True(compact.SectionsOnly);
        Assert.Equal(expanded.Biomes, compact.Biomes);
        Assert.Equal(sections.Count(s => s != null), compact.Regions.Count);
        var full = Assert.Single(expanded.Regions);
        foreach (var region in compact.Regions)
            for (int x = 0; x < 16; x++) for (int z = 0; z < 16; z++) for (int y = 0; y < 16; y++)
            {
                int a = region.Index(x, y, z), b = full.Index(x, region.Y + y, z);
                Assert.Equal(full.BlockIds[b], region.BlockIds[a]);
                Assert.Equal(full.Metadata[b], region.Metadata[a]);
                Assert.Equal(full.BlockLight[b], region.BlockLight[a]);
                Assert.Equal(full.SkyLight[b], region.SkyLight[a]);
            }
    }

    [Fact]
    public void Compact_decode_does_not_allocate_empty_upper_sections()
    {
        var sections = new int[]?[16];
        for (int s = 0; s < 5; s++) sections[s] = Enumerable.Repeat(1 << 4, 4096).ToArray();
        var body = ChunkData.EncodeBody(0, 0, true, null, sections, true);
        long Measure(bool compact)
        {
            ChunkData.Decode(body, true, compact);
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 32; i++) GC.KeepAlive(ChunkData.Decode(body, true, compact));
            return GC.GetAllocatedBytesForCurrentThread() - before;
        }
        long fullBytes = Measure(false), compactBytes = Measure(true);
        output.WriteLine($"32 five-section chunks: expanded {fullBytes:N0} bytes, compact {compactBytes:N0} bytes");
        Assert.True(compactBytes < fullBytes * 0.4, $"compact {compactBytes}, expanded {fullBytes}");
    }

    private static MemoryStream PacketStream(int threshold, int repeats, out byte[] ignoredPayload)
    {
        var stream = new MemoryStream();
        var framing = new Framing();
        if (threshold >= 0)
        {
            stream.Write(framing.Encode(WireWriter.ForPacket(Ids1122.SetCompression).WriteVarInt(threshold).Span));
            framing.Threshold = threshold;
        }
        stream.Write(framing.Encode(WireWriter.ForPacket(Ids1122.LoginSuccess).WriteString(Guid.Empty.ToString()).WriteString("test").Span));
        ignoredPayload = new byte[128 * 1024];
        new Random(42).NextBytes(ignoredPayload);
        ignoredPayload[0] = 0x7F; // An unsupported play packet: translated synchronously, without retained body data.
        var ignoredFrame = framing.Encode(ignoredPayload);
        var timeFrame = framing.Encode(WireWriter.ForPacket(Ids1122.TimeUpdate).WriteLong(42).WriteLong(6000).Span);
        stream.Write(ignoredFrame);
        stream.Write(timeFrame); // Warm buffers before measuring the remaining frames.
        for (int i = 0; i < repeats; i++) stream.Write(ignoredFrame);
        stream.Write(timeFrame);
        stream.Position = 0;
        return stream;
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(256)]
    public void Connection_reuses_packet_buffers_when_no_traffic_recorder_is_attached(int threshold)
    {
        using var stream = PacketStream(threshold, 32, out _);
        using var connection = new Release1122Connection(stream, ownsStream: false);
        Assert.IsType<TimeUpdatePacket>(connection.ReadPacket());
        long before = GC.GetAllocatedBytesForCurrentThread();
        var packet = connection.ReadPacket();
        long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.IsType<TimeUpdatePacket>(packet);
        output.WriteLine($"32 128KiB frames, compression {threshold}: allocated {bytes:N0} bytes");
        Assert.InRange(bytes, 0, 512 * 1024);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(256)]
    public void Recorded_raw_packets_keep_owned_bytes_after_receive_buffers_are_reused(int threshold)
    {
        using var stream = PacketStream(threshold, 8, out var expected);
        using var connection = new Release1122Connection(stream, ownsStream: false);
        var traffic = new List<PacketTraffic>();
        connection.TrafficLog = traffic.Add;
        Assert.IsType<TimeUpdatePacket>(connection.ReadPacket());
        Assert.IsType<TimeUpdatePacket>(connection.ReadPacket());
        Assert.Null(connection.ReadPacket());
        var ignored = traffic.Where(t => t.Packet is WirePacket1122 { WireId: 0x7F }).ToList();
        Assert.Equal(9, ignored.Count);
        Assert.All(ignored, t =>
        {
            Assert.Equal(expected, t.Raw.ToArray());
            Assert.Equal(expected[1..], ((WirePacket1122)t.Packet).Body);
        });
    }
}
