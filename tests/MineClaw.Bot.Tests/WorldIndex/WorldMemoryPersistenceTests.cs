using MineClaw.Bot.WorldIndex;
using MineClaw.World;
using MineClaw.World.Versioning;

namespace MineClaw.Bot.Tests.WorldIndex;

public sealed class WorldMemoryPersistenceTests
{
    [Fact]
    public void Pooled_persistence_preserves_dirty_unloaded_and_revisited_columns()
    {
        var clock = new ManualClock(DateTimeOffset.Parse("2026-11-08T00:00:00Z"));
        var world = new WorldState(height: 256, physics: Release1122PhysicsRules.Instance);
        var store = new InMemoryWorldMemoryStore();
        using var memory = new WorldMemory(world, store, time: clock).Attach();
        world.LoadColumn(0, 0);
        world.ApplyChunkRegion(0, 0, 0, 16, 256, 16, new byte[65536], null, null, null);
        memory.Pump();
        Assert.Equal(1, store.ChunkWrites);
        var first = store.GetChunk(0, 0, 0)!;
        Assert.True(ChunkSnapshot.Decode(first.Data).IsEmpty);

        world.SetBlock(3, 250, 5, 35, 7);
        clock.Advance(TimeSpan.FromSeconds(61));
        memory.Pump();
        Assert.Equal(2, store.ChunkWrites);
        var changed = store.GetChunk(0, 0, 0)!;
        var changedSnapshot = ChunkSnapshot.Decode(changed.Data);
        Assert.Equal(35, changedSnapshot.Ids[ChunkColumn.Index(3, 250, 5)]);
        Assert.Equal(7, changedSnapshot.Meta[ChunkColumn.Index(3, 250, 5)]);
        Assert.True(ChunkSnapshot.Decode(first.Data).IsEmpty);

        world.SetBlock(3, 250, 5, 35, 2);
        world.UnloadColumn(0, 0);
        memory.Pump();
        Assert.Equal(3, store.ChunkWrites);
        var unloaded = store.GetChunk(0, 0, 0)!;
        var data = ChunkSnapshot.Decode(unloaded.Data);
        Assert.Equal(2, data.Meta[ChunkColumn.Index(3, 250, 5)]);

        clock.Advance(TimeSpan.FromSeconds(1));
        world.LoadColumn(0, 0);
        var ids = new byte[65536];
        var meta = new byte[65536];
        for (int x = 0; x < 16; x++)
        for (int z = 0; z < 16; z++)
        for (int y = 0; y < 256; y++)
        {
            // Region packets use full-height y runs; snapshots retain the two legacy128-high halves.
            int destination = (x * 16 + z) * 256 + y, source = ChunkColumn.Index(x, y, z);
            ids[destination] = data.Ids[source];
            meta[destination] = data.Meta[source];
        }
        world.ApplyChunkRegion(0, 0, 0, 16, 256, 16, ids, meta, null, null);
        long before = GC.GetAllocatedBytesForCurrentThread();
        memory.Pump();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(BaselineState.Unchanged, memory.State(0, 0, 0));
        Assert.Equal(3, store.ChunkWrites);
        Assert.Equal(clock.GetUtcNow(), store.GetChunk(0, 0, 0)!.LastSeen);
        Assert.True(allocated < 50_000, $"An unchanged revisit allocated {allocated:N0} bytes; it should compare the column hash without copying it.");
    }
}
