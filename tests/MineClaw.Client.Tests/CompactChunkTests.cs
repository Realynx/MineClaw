using System.Reflection;
using MineClaw.Protocol.Packets;
using MineClaw.Protocol.Release1122;
using MineClaw.World;
using MineClaw.World.Chunks;
using Xunit.Abstractions;

namespace MineClaw.Client.Tests;

public sealed class CompactChunkTests(ITestOutputHelper output)
{
    private static readonly MethodInfo Receive = typeof(MinecraftClient).GetMethod("ReceiveChunkSections", BindingFlags.NonPublic | BindingFlags.Static)!;
    private static void Apply(WorldState world, ChunkSectionsMessage packet) => Receive.Invoke(null, [packet, world]);

    private static ChunkSectionsMessage Filled(int cx = 0)
    {
        var sections = Enumerable.Range(0, 16).Select(_ => (int[]?)Enumerable.Repeat(1 << 4, 4096).ToArray()).ToArray();
        return ChunkData.Decode(ChunkData.EncodeBody(cx, 0, true, null, sections, true), true, compact: true);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Chunk_acknowledgements_clear_only_the_shared_history_the_packet_replaces(bool full)
    {
        var store = new SharedChunkStore();
        using var a = new WorldState(height: 256, chunkStore: store);
        using var b = new WorldState(height: 256, chunkStore: store);
        for (int cx = 0; cx < 2; cx++) { var packet = Filled(cx); Apply(a, packet); Apply(b, packet); }
        var inside = new BlockPos(2, 10, 2);
        var upper = new BlockPos(2, 70, 2);
        var other = new BlockPos(18, 10, 2);
        foreach (var p in new[] { inside, upper, other }) a.SetBlock(p.X, p.Y, p.Z, 2, 0);
        var section = Enumerable.Repeat(4 << 4, 4096).ToArray();
        Apply(b, ChunkData.Decode(ChunkData.EncodeBody(0, 0, full, null, [section], true), true, compact: true));
        var seen = new List<BlockChange>();
        b.BlockChanged += seen.Add;
        foreach (var p in new[] { inside, upper, other }) b.SetBlock(p.X, p.Y, p.Z, 3, 0);
        Assert.Equal(new[] { 4, full ? 0 : 1, 1 }, seen.Select(c => c.OldId));
    }

    [Fact]
    public void A_short_full_column_update_keeps_shared_history_above_its_height()
    {
        var store = new SharedChunkStore();
        using var a = new WorldState(height: 256, chunkStore: store);
        using var b = new WorldState(height: 256, chunkStore: store);
        var packet = Filled(); Apply(a, packet); Apply(b, packet);
        a.SetBlock(2, 10, 2, 2, 0);
        a.SetBlock(2, 200, 2, 2, 0);
        b.ApplyChunkSections(0, 0, 0, 128, true, []);
        var seen = new List<BlockChange>();
        b.BlockChanged += seen.Add;
        b.SetBlock(2, 10, 2, 3, 0);
        b.SetBlock(2, 200, 2, 3, 0);
        Assert.Equal(new[] { 0, 1 }, seen.Select(c => c.OldId));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Chunk_acknowledgements_do_not_snapshot_unrelated_shared_history(bool full)
    {
        var store = new SharedChunkStore();
        using var a = new WorldState(height: 256, chunkStore: store);
        using var b = new WorldState(height: 256, chunkStore: store);
        for (int cx = 0; cx < 2; cx++) { var initial = Filled(cx); Apply(a, initial); Apply(b, initial); }
        // Keep 4096 unrelated entries nonempty throughout every update. These used to be copied once per x/z run.
        for (int x = 16; x < 32; x++) for (int z = 0; z < 16; z++) for (int y = 16; y < 32; y++) a.SetBlock(x, y, z, 2, 0);
        var source = Enumerable.Repeat((int[]?)Enumerable.Repeat(1 << 4, 4096).ToArray(), full ? 16 : 1).ToArray();
        var packet = ChunkData.Decode(ChunkData.EncodeBody(0, 0, full, null, source, true), true, compact: true);
        Apply(b, packet);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 2; i++) Apply(b, packet);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        output.WriteLine($"Two {(full ? "full" : "partial")} updates with 4096 unrelated Told entries: {allocated:N0} bytes");
        Assert.InRange(allocated, 0, 64 * 1024);
        var seen = new List<BlockChange>();
        b.BlockChanged += seen.Add;
        b.SetBlock(16, 16, 0, 3, 0);
        Assert.Equal(1, Assert.Single(seen).OldId); // The unrelated history is still intact.
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Compact_updates_match_expanded_chunks_and_clear_stale_omitted_sections(bool hasSky)
    {
        using var expected = new WorldState(height: 256);
        using var actual = new WorldState(height: 256);
        var expectedUpdates = new List<(BlockPos Min, BlockPos Size)>();
        var actualUpdates = new List<(BlockPos Min, BlockPos Size)>();
        expected.RegionUpdated += (min, size) => expectedUpdates.Add((min, size));
        actual.RegionUpdated += (min, size) => actualUpdates.Add((min, size));
        int loaded = 0;
        actual.ColumnLoaded += (_, _) => loaded++;
        var all = Enumerable.Range(0, 16).Select(_ => (int[]?)Enumerable.Repeat(35 << 4 | 3, 4096).ToArray()).ToArray();
        var sparse = new int[]?[16];
        sparse[3] = Enumerable.Repeat(1 << 4, 4096).ToArray();
        sparse[12] = Enumerable.Repeat(35 << 4 | 7, 4096).ToArray();
        foreach (var (sections, full) in new[] { (all, true), (sparse, true), (new int[]?[16], true), (all, true), (sparse, false) })
        {
            var body = ChunkData.EncodeBody(-2, 1, full, null, sections, hasSky, 4, 8, 3);
            Apply(expected, ChunkData.Decode(body, hasSky));
            Apply(actual, ChunkData.Decode(body, hasSky, compact: true));
            Assert.Equal(expectedUpdates, actualUpdates);
            Assert.Equal(1, loaded);
            for (int x = -32; x < -16; x++) for (int z = 16; z < 32; z++) for (int y = 0; y < 256; y++)
            {
                Assert.Equal(expected.GetBlock(x, y, z), actual.GetBlock(x, y, z));
                Assert.Equal(expected.GetMeta(x, y, z), actual.GetMeta(x, y, z));
                Assert.Equal(expected.GetBlockLight(x, y, z), actual.GetBlockLight(x, y, z));
                Assert.Equal(expected.GetSkyLight(x, y, z), actual.GetSkyLight(x, y, z));
            }
            Assert.True(actual.IsLoaded(-32, 16));
        }
        // A partial update must keep the previously sent sections absent from its mask.
        Assert.Equal(35, actual.GetBlock(-32, 5, 16));
        Assert.Equal(7, actual.GetMeta(-32, 200, 16));
    }
}
