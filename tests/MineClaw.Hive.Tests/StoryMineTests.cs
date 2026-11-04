using System.Text.Json.Nodes;
using MineClaw.Hive.Story;

namespace MineClaw.Hive.Tests;

/// <summary>A placed mine design (docs/web/api.md "Mine designs"): one `mine` item keeps its absolute y, digs in reachable order
/// (top-down from the entry, a cell once a neighbour is open), puts its blocks in as their cells open, never cuts bedrock or goes
/// below y 1, and tells each ghost apart as dig or place with the material expected there.</summary>
public class StoryMineTests
{
    private static void BuildOnly(StoryColony c, int i)
    {
        var p = c.Pawns.Values.ElementAt(i);
        foreach (var t in WorkTypes.All) p.Priorities[t] = t == WorkTypes.Build ? 1 : 0;
    }

    private static JsonArray Cells(params (int X, int Y, int Z)[] ps) => new(ps.Select(p => (JsonNode)new JsonArray(p.X, p.Y, p.Z)).ToArray());

    [Fact]
    public void A_mine_digs_in_reachable_order_from_its_entry_and_keeps_its_y()
    {
        var (c, h, w) = StoryRig.Make(pawns: 1);
        BuildOnly(c, 0);
        h.Lives[c.P(0)] = FakeStoryHost.Live(0, 64, 3, inv: new Stack(50, 0, 64));
        // The fixture's ground is stone up to y 63: a shaft at 5,·,5 from the surface down to y 58, then a tunnel east at y 58, and a
        // torch in the tunnel's far cell.
        var dig = Cells((7, 58, 5), (6, 58, 5), (5, 58, 5), (5, 59, 5), (5, 60, 5), (5, 61, 5), (5, 62, 5), (5, 63, 5));
        var r = c.BuildsOp("add", new JsonArray(new JsonObject
        {
            ["kind"] = "mine", ["blocks"] = dig, ["place"] = new JsonArray(new JsonArray(8, 58, 5, 50, 5)), ["name"] = "test mine", ["group"] = "m1",
        }), null);
        Assert.Null(r.Error);
        // Every ghost says dig or place; a dig carries the block expected there (stone, from the world view).
        var ghosts = c.State()["ghosts"]!["blocks"]!.AsArray();
        Assert.Equal(9, ghosts.Count);
        Assert.All(ghosts, g => Assert.Equal(8, g!.AsArray().Count));
        Assert.Equal(8, ghosts.Count(g => g![7]!.GetValue<string>() == "dig" && g[3]!.GetValue<int>() == 1));
        Assert.Single(ghosts, g => g![7]!.GetValue<string>() == "place" && g[3]!.GetValue<int>() == 50 && g[1]!.GetValue<int>() == 58);
        var order = new List<string>();
        for (int i = 0; i < 10; i++)
        {
            var j = c.Next(0);
            var kind = j["kind"]?.GetValue<string>();
            if (kind is not ("dig" or "place")) break;
            var at = j["at"]!.GetValue<string>();
            order.Add($"{kind} {at}");
            var p = at.Split(',').Select(int.Parse).ToArray();
            w.Set(p[0], p[1], p[2], kind == "dig" ? 0 : 50, kind == "dig" ? 0 : 5);
            c.Report(c.P(0), j["id"]!.GetValue<string>(), "done", null, null);
        }
        Assert.Equal(new[]
        {
            "dig 5,63,5", "dig 5,62,5", "dig 5,61,5", "dig 5,60,5", "dig 5,59,5", "dig 5,58,5", "dig 6,58,5", "dig 7,58,5", "place 8,58,5",
        }, order);
    }

    [Fact]
    public void A_mine_never_cuts_bedrock_or_goes_below_y_1()
    {
        var (c, _, w) = StoryRig.Make(pawns: 1);
        var low = c.BuildsOp("add", new JsonArray(new JsonObject { ["kind"] = "mine", ["blocks"] = Cells((5, 0, 5)) }), null);
        Assert.Contains("below y 1", low.Error);
        w.Set(5, 3, 5, 7);
        var bedrock = c.BuildsOp("add", new JsonArray(new JsonObject { ["kind"] = "mine", ["blocks"] = Cells((5, 4, 5), (5, 3, 5)) }), null);
        Assert.Contains("bedrock at 5,3,5", bedrock.Error);
        Assert.Empty(c.Builds);
        // Already open cells (a cave) need nothing.
        w.Set(9, 40, 9, 0);
        var cave = c.BuildsOp("add", new JsonArray(new JsonObject { ["kind"] = "mine", ["blocks"] = Cells((9, 40, 9)) }), null);
        Assert.Contains("nothing to dig", cave.Error);
        // A big mine's last item may carry only the blocks that go in.
        var placeOnly = c.BuildsOp("add", new JsonArray(new JsonObject { ["kind"] = "mine", ["place"] = new JsonArray(new JsonArray(9, 40, 9, 50, 5)) }), null);
        Assert.Null(placeOnly.Error);
    }
}
