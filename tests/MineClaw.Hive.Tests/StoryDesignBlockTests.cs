using System.Text.Json.Nodes;
using MineClaw.Data.Versioning;
using MineClaw.Hive.Story;

namespace MineClaw.Hive.Tests;

/// <summary>
/// A placed design's blocks in the Story build queue (any block of the version now, not only the old palette): each cell gets the
/// item that places exactly it (red wool, not wool; a spruce door's item), second halves come with their first, and blocks pawns
/// can't place yet are kept as unsupported ghosts with an alert instead of a wrong block or a build stuck forever.
/// </summary>
public sealed class StoryDesignBlockTests
{
    private static List<GhostState> Block(IGameData d, int id, int meta)
    {
        var (g, _, err) = StoryConstruction.Expand(d, new JsonObject
        {
            ["kind"] = "block", ["blocks"] = new JsonArray(new JsonArray(4, 64, 4)), ["material"] = new JsonArray(id, meta),
        });
        Assert.Null(err);
        return g;
    }

    [Fact]
    public void A_design_cell_is_placed_with_the_item_for_exactly_its_state()
    {
        var m = Release1122GameData.Instance;
        Assert.Equal("red_wool", Block(m, 35, 14).Single().Item);
        Assert.Equal("spruce_wood_planks", Block(m, 5, 1).Single().Item);
        Assert.Equal("spruce_door", Block(m, 193, 1).Single().Item);
        Assert.Equal("redstone", Block(m, 55, 0).Single().Item);
        Assert.Equal("oak_stairs", Block(m, 53, 3).Single().Item);  // a facing meta is no item variant
        Assert.Equal("cobblestone", Block(Beta181GameData.Instance, 4, 0).Single().Item);
        // A second half rides with its first (the door's item places both): never a job of its own.
        Assert.False(Block(m, 193, 9).Single().Job);
        Assert.False(Block(m, 26, 8).Single().Job);
        Assert.True(Block(m, 26, 0).Single().Job);
    }

    [Fact]
    public void Blocks_pawns_cant_place_yet_are_unsupported_ghosts_and_never_block_the_build()
    {
        var m = Release1122GameData.Instance;
        foreach (var (id, meta, why) in new[] { (59, 0, "planted"), (8, 3, "bucket"), (63, 0, "text"), (176, 0, "block-entity") })
        {
            var g = Block(m, id, meta).Single();
            Assert.Equal("unsupported", g.State);
            Assert.False(g.Job);
            Assert.Contains(why, g.Reason);
        }
        // A design's water source is poured from a bucket (docs/web/story-mode.md "Water"); its flowing water stays a ghost (above).
        var water = Block(m, 9, 0).Single();
        Assert.Equal(("todo", true, "water_bucket"), (water.State, water.Job, water.Item));
        // Queued in a colony: an alert names them; the build finishes around them.
        var (c, _, _) = StoryRig.Make(data: m);
        var r = c.BuildsOp("add", new JsonArray(new JsonObject
        {
            ["kind"] = "block", ["blocks"] = new JsonArray(new JsonArray(4, 64, 4), new JsonArray(5, 64, 4)), ["material"] = new JsonArray(59, 0),
        }), null);
        Assert.Null(r.Error);
        Assert.Contains(c.Alerts, a => a.Text.Contains("can't place yet") && a.Text.Contains("crops are planted"));
    }
}
