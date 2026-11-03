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
