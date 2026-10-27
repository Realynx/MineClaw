using MineClaw.World;

namespace MineClaw.World.Tests;

/// <summary>The remembered (stale) layer: planning may read it, live reads/IsLoaded/safety never do.</summary>
public class RememberedWorldTests
{
    private static ChunkSnapshot Floor(int id, int y = 63)
    {
        var ids = new byte[ChunkColumn.Volume];
        var meta = new byte[ChunkColumn.Volume];
        for (int x = 0; x < 16; x++) for (int z = 0; z < 16; z++) ids[ChunkColumn.Index(x, y, z)] = (byte)id;
        return new ChunkSnapshot(ids, meta);
    }

    [Fact]
    public void Remembered_columns_are_stale_and_only_visible_through_the_planning_view()
    {
        var w = new WorldState(TestBlocks.Get) { Dimension = -1 };
        var seen = DateTimeOffset.UtcNow - TimeSpan.FromHours(2);
        Assert.True(w.Remember(new RememberedColumn(0, 0, -1, seen, Floor(87))));
        Assert.False(w.Remember(new RememberedColumn(1, 0, 0, seen, Floor(87)))); // other dimension: never mixed in

        Assert.False(w.IsLoaded(5, 5));
        Assert.Equal(0, w.GetBlock(5, 63, 5));
        Assert.True(w.IsStale(5, 5));
        Assert.Equal(seen, w.RememberedAt(5, 5));
        Assert.True(w.PlanningView.IsLoaded(5, 5));
        Assert.Equal(87, w.PlanningView.GetBlock(5, 63, 5));

        // Live data replaces the memory; the planning view then reads live.
        var ids = new byte[16 * 128 * 16];
        ids[(5 * 16 + 5) * 128 + 63] = 89;
        w.ApplyChunkRegion(0, 0, 0, 16, 128, 16, ids, null, null, null);
        Assert.False(w.IsStale(5, 5));
        Assert.Null(w.RememberedAt(5, 5));
        Assert.Equal(0, w.RememberedCount);
        Assert.Equal(89, w.PlanningView.GetBlock(5, 63, 5));
        Assert.Equal(0, w.PlanningView.GetBlock(6, 63, 6));
        Assert.False(w.Remember(new RememberedColumn(0, 0, -1, seen, Floor(87)))); // live wins
    }

    [Fact]
    public void Dropped_columns_are_reported_with_their_dimension_and_clear_forgets_memory()
    {
        var w = new WorldState(TestBlocks.Get) { Dimension = -1 };
        var dropped = new List<ChunkColumn>();
        w.ColumnDropped += dropped.Add;
        w.ApplyChunkRegion(0, 0, 0, 16, 128, 16, new byte[16 * 128 * 16], null, null, null);
        w.Remember(new RememberedColumn(3, 3, -1, DateTimeOffset.UtcNow, Floor(87)));
        w.Clear();
        w.Dimension = 0;
        var c = Assert.Single(dropped);
        Assert.Equal(-1, c.Dimension);
        Assert.Equal(0, w.RememberedCount);
        w.LoadColumn(2, 2);
        w.UnloadColumn(2, 2); // no data: nothing to remember
        Assert.Single(dropped);
    }
}
