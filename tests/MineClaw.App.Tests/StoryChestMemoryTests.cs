using MineClaw.Agent.Rag;
using MineClaw.App.Supervisor;
using MineClaw.Data;
using MineClaw.Data.Versioning;
using MineClaw.Hive.Story;
using MineClaw.World;

namespace MineClaw.App.Tests;

public sealed class StoryChestMemoryTests
{
    [Fact]
    public void Chest_memory_uses_only_the_actual_pair_and_its_newest_observation_in_the_same_dimension()
    {
        using var db = AgentDb.Open(null);
        using var rag = new RagIndex(db, backgroundEmbedding: false);
        var obs = new ObservationStore(rag);
        var world = new WorldState(inventory: InventoryModel.Beta181) { Dimension = 0 };
        world.ApplyChunkRegion(0, 0, 0, 16, world.WorldHeight, 16, new byte[16 * world.WorldHeight * 16], null, null, null);
        var first = new P3(2, 64, 2); var second = new P3(3, 64, 2); var separate = new P3(2, 64, 3);
        world.SetBlock(first.X, first.Y, first.Z, 54, 0);
        world.SetBlock(second.X, second.Y, second.Z, 54, 0);
        world.SetBlock(separate.X, separate.Y, separate.Z, 146, 0);
        var time = DateTimeOffset.Parse("2026-11-08T07:00:00Z");
        void Remember(P3 p, int item, int seconds, int dim = 0) => obs.Upsert(new Observation("chest", p.ToString(), "chest")
        { Contents = [new ItemCount(item, "item", 1)], ContentsAt = time.AddSeconds(seconds), Dimension = dim });
        Remember(first, 4, 0); Remember(second, 5, 1); Remember(separate, 265, 2); Remember(first, 264, 3, -1);
        var host = new StoryHost(null, () => null, () => world, Beta181GameData.Instance, () => null, observations: obs);
        Assert.Equal(5, Assert.Single(host.SeenChest(first)!.Value.Contents).Id);
        Assert.Equal(5, Assert.Single(host.KnownChest(second)!).Id);
        Assert.Equal(265, Assert.Single(host.KnownChest(separate)!).Id);
        // A newly placed unrelated adjacent chest with no observation cannot borrow the old chest's contents.
        var unknown = new P3(3, 64, 3);
        world.SetBlock(unknown.X, unknown.Y, unknown.Z, 54, 0); // an invalid three-chest layout never forms a pair
        Assert.Null(host.SeenChest(unknown));
        Assert.Null(host.KnownChest(unknown));
        Assert.Equal(4, Assert.Single(host.KnownChest(first)!).Id);
    }

    [Fact]
    public void Chest_looks_name_every_remembered_chest_of_the_dimension_with_when_it_was_read_in_one_query()
    {
        using var db = AgentDb.Open(null);
        using var rag = new RagIndex(db, backgroundEmbedding: false);
        var obs = new ObservationStore(rag);
        var world = new WorldState(inventory: InventoryModel.Beta181) { Dimension = 0 };
        var time = DateTimeOffset.Parse("2026-11-10T07:00:00Z");
        var a = new P3(2, 64, 2); var b = new P3(-3, 12, 40); var nether = new P3(5, 70, 5); var noContents = new P3(9, 64, 9);
        void Remember(P3 p, int seconds, int dim = 0) => obs.Upsert(new Observation("chest", p.ToString(), "chest")
        { Contents = [new ItemCount(4, "cobblestone", 1)], ContentsAt = time.AddSeconds(seconds), Dimension = dim });
        Remember(a, 0); Remember(b, 5); Remember(nether, 9, -1);
        obs.Upsert(new Observation("chest", noContents.ToString(), "chest") { Dimension = 0 }); // seen, never opened
        var host = new StoryHost(null, () => null, () => world, Beta181GameData.Instance, () => null, observations: obs);
        var looks = host.ChestLooks()!;
        Assert.Equal(2, looks.Count);
        Assert.Equal(time, looks[a]);
        Assert.Equal(time.AddSeconds(5), looks[b]);
        Assert.Null(new StoryHost(null, () => null, () => world, Beta181GameData.Instance, () => null).ChestLooks());
    }
}
