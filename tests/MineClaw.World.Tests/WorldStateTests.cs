using MineClaw.World;

namespace MineClaw.World.Tests;

public class WorldStateTests
{
    [Fact]
    public void ApplyChunkRegion_FullColumn_UsesPayloadIndexLayout()
    {
        var w = new WorldState(TestBlocks.Get);
        var ids = new byte[16 * 128 * 16];
        var meta = new byte[ids.Length];
        var bl = new byte[ids.Length];
        var sl = new byte[ids.Length];
        // (dx * sizeZ + dz) * sizeY + dy
        int i = (3 * 16 + 5) * 128 + 70;
        ids[i] = 4; meta[i] = 7; bl[i] = 9; sl[i] = 3;
        w.ApplyChunkRegion(32, 0, -16, 16, 128, 16, ids, meta, bl, sl);

        Assert.Equal(4, w.GetBlock(35, 70, -11));
        Assert.Equal(7, w.GetMeta(35, 70, -11));
        Assert.Equal(9, w.GetBlockLight(35, 70, -11));
        Assert.Equal(3, w.GetSkyLight(35, 70, -11));
        Assert.Equal(0, w.GetBlock(35, 71, -11));
        Assert.True(w.IsLoaded(40, -1));
        Assert.False(w.IsLoaded(48, -1));
    }

    [Fact]
    public void ApplyChunkRegion_PartialRegionSpanningColumns()
    {
        var w = new WorldState(TestBlocks.Get);
        int sx = 4, sy = 3, sz = 2;
        var ids = new byte[sx * sy * sz];
        for (int dx = 0; dx < sx; dx++)
        for (int dz = 0; dz < sz; dz++)
        for (int dy = 0; dy < sy; dy++)
            ids[(dx * sz + dz) * sy + dy] = (byte)(1 + dx * 10 + dz * 3 + dy);
        w.ApplyChunkRegion(14, 60, 0, sx, sy, sz, ids, null, null, null);

        Assert.Equal(2, w.LoadedColumnCount); // x 14..17 spans chunk 0 and 1
        Assert.Equal(1, w.GetBlock(14, 60, 0));
        Assert.Equal(1 + 10 + 3 + 2, w.GetBlock(15, 62, 1));
        Assert.Equal(1 + 30 + 0 + 1, w.GetBlock(17, 61, 0));
        Assert.Equal(0, w.GetBlock(17, 63, 0));
    }

    [Fact]
    public void SetBlock_RaisesEvent_AndUnloadedIsIgnored()
    {
        var w = TestWorld.Create();
        BlockChange? seen = null;
        w.BlockChanged += c => seen = c;
        Assert.True(w.SetBlock(1, 64, 1, 4, 0));
        Assert.Equal(new BlockChange(new BlockPos(1, 64, 1), 0, 0, 4, 0), seen);
        Assert.False(w.SetBlock(1000, 64, 1, 4, 0));
        Assert.Equal(0, w.GetBlock(1000, 64, 1));

        w.UnloadColumn(0, 0);
        Assert.False(w.IsLoaded(1, 1));
        Assert.Equal(0, w.GetBlock(1, 64, 1));
        w.LoadColumn(0, 0);
        Assert.False(w.IsLoaded(1, 1)); // pre-chunk only: no data yet
    }

    [Fact]
    public void VoidQueries()
    {
        var w = TestWorld.Create();
        w.Set(0, 64, 0, TestBlocks.Cobble);
        w.Set(5, 10, 5, TestBlocks.Stone);
        Assert.False(w.IsOverVoid(0, 65, 0));
        Assert.True(w.IsOverVoid(1, 65, 0));
        Assert.False(w.IsOverVoid(5, 65, 5));
        Assert.True(w.IsOverVoid(500, 65, 500)); // unloaded = unsafe
        Assert.Equal(64, w.HighestSolidBelow(0, 127, 0));
        Assert.Equal(-1, w.HighestSolidBelow(2, 127, 2));
        // Player overlapping the block's edge is not over the void.
        Assert.False(w.IsPlayerOverVoid(new Vec3d(1.2, 65, 0.5)));
        Assert.True(w.IsPlayerOverVoid(new Vec3d(1.5, 65, 0.5)));
        Assert.Equal(64, w.HighestBlock(0, 0));
        Assert.Single(w.ScanColumn(0, 0));
    }

    [Fact]
    public void FindBlocks_NearestFirst()
    {
        var w = TestWorld.Create();
        w.Set(10, 64, 0, TestBlocks.Chest);
        w.Set(3, 64, 0, TestBlocks.Chest);
        w.Set(-20, 64, 0, TestBlocks.Chest);
        var found = w.FindBlocks(new Vec3d(0.5, 64.5, 0.5), 16, TestBlocks.Chest);
        Assert.Equal(2, found.Count);
        Assert.Equal(new BlockPos(3, 64, 0), found[0].Pos);
        Assert.Equal(new BlockPos(10, 64, 0), w.FindBlocks(new Vec3d(0.5, 64.5, 0.5), 16, TestBlocks.Chest)[1].Pos);
        Assert.Equal(new BlockPos(3, 64, 0), w.FindNearestBlock(new Vec3d(0, 64, 0), 32, TestBlocks.Chest)!.Value.Pos);
    }

    [Fact]
    public void EntityTracker_SpawnMoveDestroyAndQueries()
    {
        var t = new EntityTracker { SelfId = 1 };
        t.SpawnPlayer(1, "me", new Vec3d(0, 64, 0), 0, 0);
        t.SpawnPlayer(2, "Steve", new Vec3d(5, 64, 0), 0, 0);
        t.SpawnMob(3, MobTypes.Zombie, new Vec3d(3, 64, 0), 0, 0);
        t.SpawnItem(4, new MineClaw.Data.ItemStack(4, 3, 0), new Vec3d(1, 64, 0));
        t.MoveRelative(3, 1, 0, 0);
        Assert.Equal(4, t.Get(3)!.Position.X, 6);
        Assert.Equal(4, t.NearestItem(new Vec3d(0, 64, 0))!.Id);
        Assert.Equal(3, t.NearestHostile(new Vec3d(0, 64, 0))!.Id);
        Assert.Equal(2, t.FindPlayer("steve")!.Id);
        Assert.Equal(3, t.All().Count); // self excluded
        Assert.True(t.Destroy(3));
        Assert.Null(t.NearestHostile(new Vec3d(0, 64, 0)));
        t.SetPlayerListEntry("Steve", true);
        t.SetPlayerListEntry("Alex", true);
        t.SetPlayerListEntry("Steve", false);
        Assert.Equal(["Alex"], t.OnlinePlayers);
    }
}
