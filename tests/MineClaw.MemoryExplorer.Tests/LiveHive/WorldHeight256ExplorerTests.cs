using MineClaw.Data;
using MineClaw.MemoryExplorer.LiveHive;
using MineClaw.MemoryExplorer.LiveHive.Gpu;
using MineClaw.World;
using static MineClaw.MemoryExplorer.Tests.LiveHive.LiveHiveDecoderTests;

namespace MineClaw.MemoryExplorer.Tests.LiveHive;

/// <summary>The Explorer on a 1.12.2 (256-high) hive: voxels, light and GPU sections above y 127 (multi-version P3).</summary>
public class WorldHeight256ExplorerTests
{
    private static ChunkSnapshot Snap256(params (int X, int Y, int Z, byte Id)[] blocks)
    {
        var ids = new byte[2 * ChunkSnapshot.Volume];
        var meta = new byte[2 * ChunkSnapshot.Volume];
        foreach (var (x, y, z, id) in blocks) ids[ChunkColumn.Index(x, y, z)] = id;
        return new ChunkSnapshot(ids, meta);
    }

    [Fact]
    public void Voxels_read_blocks_and_sky_above_y_127()
    {
        var cache = new HiveWorldCache();
        Assert.Equal(128, cache.WorldHeight);
        cache.Apply(ColumnPayload.Encode(0, [Entry(0, 0, Snap256((3, 200, 4, (byte)Blocks.Stone), (3, 60, 4, (byte)Blocks.Dirt)))]));
        Assert.Equal(256, cache.WorldHeight);
        var v = new HiveVoxels(cache);
        v.Prepare(0, 8, 8, 1, DateTimeOffset.UtcNow);
        Assert.Equal(256, v.WorldHeight);
        Assert.Equal(Blocks.Stone, v.Block(3, 200, 4));
        Assert.Equal(15, v.SkyLight(3, 201, 4)); // the opaque top is y 200 now
        Assert.Equal(-1, v.SkyLight(3, 150, 4)); // under it
        Assert.Equal(0, v.Block(3, 256, 4));
    }

    [Fact]
    public void A_b181_column_in_a_256_high_cache_reads_air_above_its_top()
    {
        var cache = new HiveWorldCache();
        cache.Apply(ColumnPayload.Encode(0, [Entry(0, 0, Snap256((1, 200, 1, (byte)Blocks.Stone))), Entry(1, 0, Snap((1, 64, 1, (byte)Blocks.Stone)))]));
        var v = new HiveVoxels(cache);
        v.Prepare(0, 8, 8, 2, DateTimeOffset.UtcNow);
        Assert.Equal(0, v.Block(17, 200, 1));          // no index past the 128-high snapshot
        Assert.Equal(Blocks.Stone, v.Block(17, 64, 1));
    }

    [Fact]
    public void Sections_above_y_127_are_meshed_and_rebuilt()
    {
        var cache = new HiveWorldCache();
        var col = Snap256((5, 20, 5, (byte)Blocks.Stone), (5, 200, 5, (byte)Blocks.Stone));
        cache.Apply(ColumnPayload.Encode(0, [Entry(0, 0, col)]));
        var store = new SectionStore(cache) { Log = [] };
        while (store.Update(0, 0, 0, 1) > 0) { }
        Assert.Contains(new SectionKey(0, 1, 0), store.Log!);
        Assert.Contains(new SectionKey(0, 12, 0), store.Log!); // y 200 = section 12
        store.Log!.Clear();

        // A change at y 230 (section 14) rebuilds that section (plus the sky light it uncovers: the top moved 200 → 230).
        cache.Apply(ColumnPayload.Encode(0, [Entry(0, 0, Snap256((5, 20, 5, (byte)Blocks.Stone), (5, 200, 5, (byte)Blocks.Stone), (5, 230, 5, (byte)Blocks.Stone)))]));
        while (store.Update(0, 0, 0, 1) > 0) { }
        Assert.Contains(new SectionKey(0, 14, 0), store.Log!);
        Assert.DoesNotContain(store.Log!, k => k.Sy < 12);
    }

    [Fact]
    public void Diff_of_two_256_high_snapshots_marks_upper_sections()
    {
        var a = Snap256((0, 10, 0, 1));
        var b = Snap256((0, 10, 0, 1), (0, 128, 0, 1)); // the bottom layer of section 8 (the first of the upper half): also section 7
        var top = new byte[256];
        Assert.Equal((ushort)(1 << 8 | 1 << 7), SectionStore.Diff(a, b, top, top));
        Assert.True(SectionMesher.NonEmpty(b, 8));
        Assert.False(SectionMesher.NonEmpty(a, 8));
        Assert.False(SectionMesher.NonEmpty(Snap((0, 10, 0, 1)), 8)); // beyond a 128-high snapshot
    }
}
