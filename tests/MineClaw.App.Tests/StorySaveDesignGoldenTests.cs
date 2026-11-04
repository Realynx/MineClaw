using MineClaw.Agent.Rag;
using MineClaw.App.Supervisor;
using MineClaw.Bot.Blueprints;
using MineClaw.Hive.Story;
using MineClaw.World;

namespace MineClaw.App.Tests;

/// <summary>
/// Pins <c>StoryWiring.SaveDesign</c> (WP07): a directive's build as an accepted design — the DSL (palette order, layers, the last
/// ghost on a cell wins, removals left out), the record's fields, and every "not a design" case.
/// </summary>
public class StorySaveDesignGoldenTests
{
    private static GhostState G(int x, int y, int z, int id, int meta = 0, bool remove = false) =>
        new() { At = new P3(x, y, z), Id = id, Meta = meta, Remove = remove };

    [Fact]
    public void Saves_the_dsl_and_record()
    {
        using var db = AgentDb.Open(null);
        var store = new BlueprintStore(db);
        var ghosts = new[] { G(10, 64, 20, 1), G(11, 64, 20, 5, 2), G(10, 65, 21, 1), G(10, 64, 20, 4), G(12, 66, 22, 3, remove: true) };
        var id = StoryWiring.SaveDesign(store, "Hut\nname", ghosts, "Fox", -1);
        Assert.NotNull(id);
        var rec = store.Get(id!.Value)!;
        string N(int block) => MineClaw.Data.Blocks.Get(block).Name;
        Assert.Equal("blueprint Hut name\nsize 2x2x2\norigin 10,64,20\n" +
                     $"palette A={N(1)} B={N(5)}:2 C={N(4)}\n" +
                     "layer 0\nCB\n__\nlayer 1\n__\nA_\n", rec.Dsl);
        Assert.Equal("Hut\nname", rec.Name);
        Assert.Equal("from the Story Mode map for Fox", rec.Description);
        Assert.Equal("Fox", rec.Requester);
        Assert.Equal(DesignStatus.Accepted, rec.Status);
        Assert.Equal(-1, rec.Dim);
        Assert.Equal(new BlockPos(11, 63, 21), rec.Site);
        Assert.Equal(new BlockPos(10, 64, 20), rec.Origin);
        Assert.Equal("where it was drawn", rec.Anchor);
        Assert.Equal("Fox", rec.AcceptedBy);
        Assert.Equal(1, rec.Rev);
        Assert.Equal("Hut\nname 2×2×2, 4 blocks (story map)", rec.Summary);
    }

    [Fact]
    public void Not_a_design_is_null()
    {
        using var db = AgentDb.Open(null);
        var store = new BlueprintStore(db);
        Assert.Null(StoryWiring.SaveDesign(store, "x", [], "Fox", 0));
        Assert.Null(StoryWiring.SaveDesign(store, "x", [G(0, 0, 0, 1, remove: true)], "Fox", 0));
        Assert.Null(StoryWiring.SaveDesign(store, "x", [G(0, 0, 0, 1), G(BlueprintParser.MaxSide, 0, 0, 1)], "Fox", 0));
        Assert.Null(StoryWiring.SaveDesign(store, "x", [G(0, 0, 0, 1), G(0, 0, BlueprintParser.MaxSide, 1)], "Fox", 0));
        Assert.Null(StoryWiring.SaveDesign(store, "x", [G(0, 0, 0, 1), G(0, BlueprintParser.MaxHeight, 0, 1)], "Fox", 0));
        Assert.NotNull(StoryWiring.SaveDesign(store, "x", [G(0, 0, 0, 1), G(BlueprintParser.MaxSide - 1, BlueprintParser.MaxHeight - 1, 0, 1)], "Fox", 0));
        // 62 palette symbols: a 63rd distinct block is too many.
        var many = Enumerable.Range(0, 63).Select(i => G(i % 8, 0, i / 8, 1, i)).ToArray();
        Assert.Null(StoryWiring.SaveDesign(store, "x", many, "Fox", 0));
        Assert.NotNull(StoryWiring.SaveDesign(store, "x", many[..62], "Fox", 0));
    }
}
