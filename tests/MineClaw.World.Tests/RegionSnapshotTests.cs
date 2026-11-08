using MineClaw.Data;
using MineClaw.World.Versioning;

namespace MineClaw.World.Tests;

public sealed class RegionSnapshotTests
{
    private sealed class Source : IBlockAccess
    {
        public int Id = 1, Meta = 7;
        public bool Loaded = true;
        public int WorldHeight => 256;
        public IPhysicsRules Physics => Release1122PhysicsRules.Instance;
        public BlockInfo Info(int id) => TestBlocks.Get(id);
        public int GetBlock(int x, int y, int z) => Id;
        public int GetMeta(int x, int y, int z) => Meta;
        public bool IsLoaded(int x, int z) => Loaded;
    }

    [Fact]
    public void Reused_storage_clears_unloaded_columns_and_air_metadata()
    {
        var source = new Source();
        for (int i = 0; i < 8; i++)
        {
            using (var filled = new RegionSnapshot(source, -1, 1, 60, 66, -1, 1))
            {
                Assert.Equal(1, filled.GetBlock(0, 64, 0));
                Assert.Equal(7, filled.GetMeta(0, 64, 0));
            }
            source.Loaded = false;
            using (var unloaded = new RegionSnapshot(source, -1, 1, 60, 66, -1, 1))
            {
                Assert.False(unloaded.IsLoaded(0, 0));
                Assert.Equal(0, unloaded.GetBlock(0, 64, 0));
                Assert.Equal(0, unloaded.GetMeta(0, 64, 0));
            }
            source.Loaded = true;
            source.Id = 0;
            using (var air = new RegionSnapshot(source, -1, 1, 60, 66, -1, 1))
            {
                Assert.True(air.IsLoaded(0, 0));
                Assert.Equal(0, air.GetBlock(0, 64, 0));
                Assert.Equal(0, air.GetMeta(0, 64, 0));
            }
            source.Id = 1;
        }
    }

    [Fact]
    public void Active_snapshots_do_not_share_storage_and_outside_reads_use_the_source()
    {
        var source = new Source();
        using var first = new RegionSnapshot(source, -1, 1, -2, 258, -1, 1);
        source.Id = 2;
        source.Meta = 3;
        using (var second = new RegionSnapshot(source, -1, 1, 0, 255, -1, 1))
        {
            Assert.Equal(2, second.GetBlock(0, 64, 0));
            Assert.Equal(3, second.GetMeta(0, 64, 0));
            Assert.Equal(1, first.GetBlock(0, 64, 0));
            Assert.Equal(7, first.GetMeta(0, 64, 0));
        }
        Assert.Equal(1, first.GetBlock(0, 64, 0));
        Assert.Equal(2, first.GetBlock(2, 64, 0));
        Assert.Equal(3, first.GetMeta(0, 256, 0));
        Assert.Same(source.Physics, first.Physics);
        Assert.Equal(256, first.WorldHeight);
    }

    [Fact]
    public void Concurrent_snapshots_keep_each_world_separate()
    {
        Parallel.For(0, 32, i =>
        {
            var source = new Source { Id = i + 1, Meta = i };
            for (int j = 0; j < 8; j++)
            {
                using var snapshot = new RegionSnapshot(source, -2, 2, 62, 68, -2, 2);
                Assert.Equal(i + 1, snapshot.GetBlock(0, 64, 0));
                Assert.Equal(i, snapshot.GetMeta(0, 64, 0));
            }
        });
    }
}
