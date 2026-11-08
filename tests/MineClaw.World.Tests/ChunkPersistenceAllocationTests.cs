using System.IO.Compression;
using Xunit.Abstractions;

namespace MineClaw.World.Tests;

public sealed class ChunkPersistenceAllocationTests(ITestOutputHelper output)
{
    private static ChunkColumn Column(int height)
    {
        var column = new ChunkColumn(0, 0, height);
        for (int x = 0; x < 16; x++)
        for (int z = 0; z < 16; z++) column.Set(x, 63, z, 1, (x + z) & 15);
        column.Set(3, height - 2, 5, 35, 7);
        return column;
    }

    private static byte[] OriginalEncoding(ChunkSnapshot snapshot)
    {
        int n = snapshot.Ids.Length;
        var packed = new byte[n + n / 2];
        snapshot.Ids.CopyTo(packed, 0);
        for (int i = 0; i < n; i += 2)
            packed[n + i / 2] = (byte)((snapshot.Meta[i] & 15) | ((snapshot.Meta[i + 1] & 15) << 4));
        using var stream = new MemoryStream();
        using (var deflate = new DeflateStream(stream, CompressionLevel.Fastest, leaveOpen: true)) deflate.Write(packed);
        return stream.ToArray();
    }

    [Theory]
    [InlineData(128)]
    [InlineData(256)]
    public void Captured_payload_preserves_exact_bytes_hash_summary_and_owned_result(int height)
    {
        var column = Column(height);
        var original = ChunkSnapshot.Capture(column);
        var encoded = ChunkSnapshot.CaptureEncoded(column);
        Assert.Equal(original.Hash, encoded.Hash);
        Assert.Equal(original.Summary(), encoded.Summary);
        Assert.Equal(OriginalEncoding(original), encoded.Data);
        Assert.Equal(original.Encode(), encoded.Data);

        // Reusing every scratch buffer for empty and other-height columns cannot change the retained result.
        for (int i = 0; i < 8; i++)
        {
            var empty = ChunkSnapshot.CaptureEncoded(new ChunkColumn(1, 1, i % 2 == 0 ? 128 : 256));
            Assert.True(ChunkSnapshot.Decode(empty.Data).IsEmpty);
        }
        var restored = ChunkSnapshot.Decode(encoded.Data);
        Assert.Equal(height, restored.Height);
        Assert.Equal(original.Ids, restored.Ids);
        Assert.Equal(original.Meta, restored.Meta);
    }

    [Theory]
    [InlineData(128)]
    [InlineData(256)]
    public void Persistence_does_not_allocate_full_column_or_large_packing_buffers(int height)
    {
        var column = Column(height);
        static void Old(ChunkColumn c)
        {
            var snapshot = ChunkSnapshot.Capture(c);
            _ = snapshot.Summary();
            _ = OriginalEncoding(snapshot);
        }
        Old(column);
        _ = ChunkSnapshot.CaptureEncoded(column);
        const int iterations = 16;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < iterations; i++) Old(column);
        long oldBytes = (GC.GetAllocatedBytesForCurrentThread() - before) / iterations;
        before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < iterations; i++) _ = ChunkSnapshot.CaptureEncoded(column);
        long newBytes = (GC.GetAllocatedBytesForCurrentThread() - before) / iterations;
        output.WriteLine($"{height}-high persistence: {oldBytes:N0} -> {newBytes:N0} bytes per column");
        Assert.True(newBytes < oldBytes / 4, $"Persistence allocated {newBytes:N0} versus {oldBytes:N0} bytes before scratch reuse.");
    }

    [Fact]
    public void Scratch_copy_clears_unallocated_sections_and_preserves_destination_tail()
    {
        var column = Column(256);
        var ids = Enumerable.Repeat((byte)255, column.CellCount + 17).ToArray();
        var meta = ids.ToArray();
        column.CopyIds(ids);
        column.CopyMeta(meta);
        Assert.Equal(column.CopyIds(), ids[..column.CellCount]);
        Assert.Equal(column.CopyMeta(), meta[..column.CellCount]);
        Assert.All(ids[column.CellCount..], b => Assert.Equal(255, b));
        Assert.All(meta[column.CellCount..], b => Assert.Equal(255, b));
    }

    [Fact]
    public void Concurrent_capture_and_decode_keep_payloads_independent()
    {
        Parallel.For(0, 16, i =>
        {
            var column = Column(i % 2 == 0 ? 128 : 256);
            column.Set(0, 70, 0, 35, i);
            for (int j = 0; j < 4; j++)
            {
                var result = ChunkSnapshot.Decode(ChunkSnapshot.CaptureEncoded(column).Data);
                Assert.Equal(column.Height, result.Height);
                Assert.Equal(i, result.Meta[ChunkColumn.Index(0, 70, 0)]);
                Assert.Equal(35, result.Ids[ChunkColumn.Index(0, 70, 0)]);
            }
        });
    }
}
