using MineClaw.Data;
using MineClaw.Data.Versioning;
using MineClaw.Hive.Story;
using MineClaw.World;
using MineClaw.World.Chunks;

namespace MineClaw.Hive.Tests;

/// <summary>
/// The allocation cuts of 11-04 (live: 164 MB/s allocated, 4.1 s of GC pause a minute) keep their meaning: the merged entity list is
/// reused only while nobody came or went, and filter entries parsed by hand read as the regex did.
/// </summary>
public sealed class StoryPerfTests
{
    [Fact]
    public void The_merged_entity_list_is_reused_until_an_entity_comes_goes_or_changes_stack()
    {
        var store = new SharedChunkStore(128);
        var main = new WorldState(chunkStore: store);
        var body = new WorldState(chunkStore: store);
        var w = new MergedStoryWorld(() => main, () => [body], Beta181GameData.Instance);
        body.Entities.SpawnItem(10, new ItemStack(4, 1, 0), new Vec3d(1.5, 64, 1.5));
        var a = w.Entities();
        Assert.Same(a, w.Entities()); // nothing changed: the same list, no walk over the trackers

        body.Entities.MoveRelative(10, 1, 0, 0); // a move alone keeps it (for up to EntityReuseMs)
        Assert.Same(a, w.Entities());

        body.Entities.SpawnItem(11, new ItemStack(3, 2, 0), new Vec3d(2.5, 64, 2.5));
        var b = w.Entities();
        Assert.NotSame(a, b);
        Assert.Contains(b, e => e.Id == 11);

        body.Entities.SetItem(11, new ItemStack(3, 5, 0)); // a stack merged into it
        Assert.Equal(5, w.Entities().Single(e => e.Id == 11).Count);

        body.Entities.Destroy(10); // picked up
        Assert.DoesNotContain(w.Entities(), e => e.Id == 10);
        main.Entities.SpawnItem(12, new ItemStack(1, 1, 0), new Vec3d(0.5, 64, 0.5)); // main's tracker counts too
        Assert.Contains(w.Entities(), e => e.Id == 12);
    }

    [Theory]
    [InlineData("4", 4, 0, true)]
    [InlineData("4", 4, 3, true)]
    [InlineData(" 4 ", 4, 0, true)]
    [InlineData("4:2", 4, 2, true)]
    [InlineData("4:2", 4, 1, false)]
    [InlineData("4:-1", 4, 9, true)]
    [InlineData("5", 4, 0, false)]
    [InlineData("4:", 4, 0, false)]
    [InlineData("4:x", 4, 0, false)]
    [InlineData("*", 4, 0, true)]
    [InlineData("food", 297, 0, true)]
    [InlineData("food", 4, 0, false)]
    public void Filter_entries_read_as_before(string entry, int id, int meta, bool covers) =>
        Assert.Equal(covers, StoryCategories.For(Beta181GameData.Instance).Covers(entry, id, meta));

    [Fact]
    public void The_category_tree_is_one_per_version() =>
        Assert.Same(StoryCategories.For(Beta181GameData.Instance), StoryCategories.For(Beta181GameData.Instance));
}
