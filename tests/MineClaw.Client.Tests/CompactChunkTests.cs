using System.Reflection;
using MineClaw.Protocol.Packets;
using MineClaw.Protocol.Release1122;
using MineClaw.World;

namespace MineClaw.Client.Tests;

public sealed class CompactChunkTests
{
    private static readonly MethodInfo Receive = typeof(MinecraftClient).GetMethod("ReceiveChunkSections", BindingFlags.NonPublic | BindingFlags.Static)!;
    private static void Apply(WorldState world, ChunkSectionsMessage packet) => Receive.Invoke(null, [packet, world]);

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
