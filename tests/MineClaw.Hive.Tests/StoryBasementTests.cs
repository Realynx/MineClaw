using System.Text.Json.Nodes;
using MineClaw.Hive.Story;

namespace MineClaw.Hive.Tests;

/// <summary>A design with a basement placed through the build queue: the dig item goes first and top-down, the design's blocks
/// (one group) wait for it; a plain removal and builds outside a group behave as before.</summary>
public class StoryBasementTests
{
    private static void Only(StoryColony c, int i, params string[] types)
    {
        var p = c.Pawns.Values.ElementAt(i);
        foreach (var t in WorkTypes.All) p.Priorities[t] = types.Contains(t) ? 1 : 0;
    }

    private static JsonArray Cells(params (int X, int Y, int Z)[] ps) => new(ps.Select(p => (JsonNode)new JsonArray(p.X, p.Y, p.Z)).ToArray());

    [Fact]
    public void A_placed_basement_is_dug_top_down_before_its_blocks_go_in()
    {
        var (c, h, w) = StoryRig.Make(pawns: 2);
        for (int i = 0; i < 2; i++)
        {
            Only(c, i, WorkTypes.Build);
            h.Lives[c.P(i)] = FakeStoryHost.Live(0, 64, 3, inv: new Stack(4, 0, 64));
        }
        // The fixture's ground is stone up to y 63: a 1×2 shaft at x 5 (y 62..63) and a floor block under it (y 61).
        var r = c.BuildsOp("add", new JsonArray(
            new JsonObject { ["kind"] = "remove", ["blocks"] = Cells((5, 62, 5), (5, 63, 5)), ["group"] = "g1" },
            new JsonObject { ["kind"] = "block", ["blocks"] = Cells((5, 61, 5), (5, 64, 6)), ["material"] = "cobblestone", ["group"] = "g1" }), null);
        Assert.Null(r.Error);
        var first = c.Next(0);
        Assert.Equal("dig", first["kind"]!.GetValue<string>());
        Assert.Equal("5,63,5", first["at"]!.GetValue<string>()); // top-down: the cell under it is buried until this one is gone
        // The design's blocks wait for the dig (the other pawn gets nothing from that group).
        var second = c.Next(1);
        Assert.NotEqual("place", second["kind"]?.GetValue<string>());
        w.Set(5, 63, 5, 0);
        c.Report(c.P(0), first["id"]!.GetValue<string>(), "done", null, null);
        var next = c.Next(0);
        Assert.Equal(("dig", "5,62,5"), (next["kind"]!.GetValue<string>(), next["at"]!.GetValue<string>()));
        w.Set(5, 62, 5, 0);
        c.Report(c.P(0), next["id"]!.GetValue<string>(), "done", null, null);
        // Dug out: now the floor goes in (bottom-up as ever).
        var place = c.Next(0);
        Assert.Equal(("place", "5,61,5"), (place["kind"]!.GetValue<string>(), place["at"]!.GetValue<string>()));
    }

    /// <summary>The top dig of a shaft blocked (a cell the pawns can't clear): its level counted as unfinished, so the cell under it
    /// was never offered ("the level below is still being built") and the design's blocks waited for it with "its ground is dug out
    /// first", for good.</summary>
    [Fact]
    public void A_blocked_dig_cell_does_not_hold_the_levels_below_it()
    {
        var (c, h, w) = StoryRig.Make(pawns: 2);
        for (int i = 0; i < 2; i++)
        {
            Only(c, i, WorkTypes.Build);
            h.Lives[c.P(i)] = FakeStoryHost.Live(0, 64, 3, inv: new Stack(4, 0, 64));
        }
        Assert.Null(c.BuildsOp("add", new JsonArray(
            new JsonObject { ["kind"] = "remove", ["blocks"] = Cells((5, 62, 5), (5, 63, 5)), ["group"] = "g1" },
            new JsonObject { ["kind"] = "block", ["blocks"] = Cells((5, 64, 6)), ["material"] = "cobblestone", ["group"] = "g1" }), null).Error);
        var dig = c.Builds.First(b => b.Kind == "remove");
        var top = dig.Ghosts.Single(g => g.At.Y == 63);
        top.State = "blocked";
        top.Reason = "can't clear the cell: the block at 5,63,5 is protected";
        var first = c.Next(0);
        Assert.Equal(("dig", "5,62,5"), (first["kind"]!.GetValue<string>(), first["at"]!.GetValue<string>()));
        // While that dig is out with a pawn the design's block still waits for its ground.
        Assert.NotEqual("place", c.Next(1)["kind"]?.GetValue<string>());
        w.Set(5, 62, 5, 0);
        c.Report(c.P(0), first["id"]!.GetValue<string>(), "done", null, null);
        // Only the blocked cell is left of the dig: it holds nothing back.
        var place = c.Next(0);
        Assert.Equal(("place", "5,64,6"), (place["kind"]!.GetValue<string>(), place["at"]!.GetValue<string>()));
        Assert.Equal("blocked", top.State);
    }

    /// <summary>Owner decision 11-10: water in a design's empty space is said, everything else is built, and the cells under it stay
    /// undug (a pit under standing water floods). Before, the water's blocked cell pinned the dig build's level and the design's
    /// blocks waited for digs no pawn was ever given.</summary>
    [Theory]
    [InlineData("b1.8.1")]
    [InlineData("1.12.2")]
    public void Water_in_a_designs_empty_space_holds_only_the_digs_under_it_and_the_placements_go_on(string v)
    {
        var (c, h, w) = StoryRig.Make(pawns: 1, data: v == "b1.8.1" ? MineClaw.Data.Versioning.Beta181GameData.Instance : MineClaw.Data.Versioning.Release1122GameData.Instance);
        Only(c, 0, WorkTypes.Build);
        h.Lives[c.P()] = FakeStoryHost.Live(0, 64, 3, inv: new Stack(4, 0, 64));
        w.Set(4, 64, 12, 9); // still water in the design's empty space; stone under it and beside the design's block
        var design = new JsonObject { ["kind"] = "block", ["blocks"] = Cells((5, 64, 12)), ["material"] = "cobblestone" };
        design["replace_area"] = new JsonArray(4, 62, 12, 5, 64, 12);
        Assert.Null(c.BuildsOp("add", new JsonArray(design), null).Error);
        var air = c.Builds.Single(b => b.Kind == "remove");
        var water = air.Ghosts.Single(g => g.At == new P3(4, 64, 12));
        Assert.Equal(("blocked", "cannot clear"), (water.State, water.Reason![..12]));

        var place = c.Next();
        Assert.Equal(("place", "5,64,12"), (place["kind"]!.GetValue<string>(), place["at"]!.GetValue<string>()));
        w.Set(5, 64, 12, 4);
        c.Report(c.P(), place["id"]!.GetValue<string>(), "done", null, null);
        // The digs under the water's level are never handed out (StoryBlockedWorkTests: the note names the water).
        var idle = c.Next();
        Assert.NotEqual("dig", idle["kind"]?.GetValue<string>());
        Assert.All(air.Ghosts.Where(g => g.At.Y < 64), g => Assert.Equal("todo", g.State));
        Assert.Equal("blocked", water.State);
    }

    [Fact]
    public void Builds_without_a_group_do_not_wait_for_a_dig()
    {
        var (c, h, _) = StoryRig.Make(pawns: 2);
        for (int i = 0; i < 2; i++)
        {
            Only(c, i, WorkTypes.Build);
            h.Lives[c.P(i)] = FakeStoryHost.Live(0, 64, 3, inv: new Stack(4, 0, 64));
        }
        c.BuildsOp("add", new JsonArray(
            new JsonObject { ["kind"] = "remove", ["blocks"] = Cells((5, 63, 5)) },
            new JsonObject { ["kind"] = "block", ["blocks"] = Cells((8, 64, 8)), ["material"] = "cobblestone" }), null);
        Assert.Equal("dig", c.Next(0)["kind"]!.GetValue<string>());
        var j = c.Next(1);
        Assert.Equal(("place", "8,64,8"), (j["kind"]!.GetValue<string>(), j["at"]!.GetValue<string>()));
    }
}
