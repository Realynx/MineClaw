using System.Text.Json.Nodes;
using MineClaw.Data.Versioning;
using MineClaw.Hive.Story;

namespace MineClaw.Hive.Tests;

/// <summary>
/// The director asks the pawn client's own rules before it offers a cell (docs/web/story-mode.md "Builds reconcile with the world").
/// Fox: "there's some blocked blocks on the storehouse build". A wall torch, a ladder or a top slab was offered as soon as any neighbour
/// stood (the floor), the client found no face that gives its meta and the cell failed "out of reach even from a pillar" until it was
/// blocked; a dead bush, a vine or a two-tall plant in a cell was placed into, and the client refused "is occupied by" forever.
/// </summary>
public sealed class StoryPlaceReadinessTests
{
    private static readonly P3 Cell = new(4, 64, 12);

    public static TheoryData<string> Versions => new() { "1.12.2", "b1.8.1" };

    private static IGameData Data(string v) => v == "b1.8.1" ? Beta181GameData.Instance : Release1122GameData.Instance;

    /// <summary>One pawn that only builds, carrying <paramref name="inv"/>, beside the site on a stone floor (y 63).</summary>
    private static (StoryColony C, FakeStoryHost H, FakeStoryWorld W) Rig(string v, params Stack[] inv)
    {
        var (c, h, w) = StoryRig.Make(1, Data(v));
        foreach (var p in c.Pawns.Values) foreach (var t in WorkTypes.All) p.Priorities[t] = t == WorkTypes.Build ? 1 : 0;
        h.Lives[c.P()] = FakeStoryHost.Bare(0, 64, 4, inv);
        return (c, h, w);
    }

    private static BuildState Add(StoryColony c, int id, int meta, params P3[] cells)
    {
        var item = new JsonObject
        {
            ["kind"] = "block", ["blocks"] = new JsonArray(cells.Select(p => (JsonNode)new JsonArray(p.X, p.Y, p.Z)).ToArray()),
            ["material"] = new JsonArray(id, meta),
        };
        Assert.Null(c.BuildsOp("add", new JsonArray(item), null).Error);
        return c.Builds.Last();
    }

    private static string Id(JsonObject j) => j["id"]!.GetValue<string>();

    private static List<string> Strs(JsonNode? a) => a?.AsArray().Select(x => x!.GetValue<string>()).ToList() ?? [];

    /// <summary>The pawn's next job; anything but a place job is ended at once so the next pick is fresh.</summary>
    private static JsonObject Pick(StoryColony c)
    {
        var j = c.Next();
        if (j["kind"]?.GetValue<string>() != "place" && j["id"] is not null) c.Report(c.P(), Id(j), "done", null, null);
        return j;
    }

    private static bool PlaceAt(JsonObject j, P3 at) => j["kind"]?.GetValue<string>() == "place" && j["at"]?.GetValue<string>() == at.ToString();

    private static JsonArray GhostOf(StoryColony c, P3 at) =>
        ((JsonArray)((JsonObject)c.State()["ghosts"]!)["blocks"]!).Select(x => (JsonArray)x!)
        .Single(g => g[0]!.GetValue<int>() == at.X && g[1]!.GetValue<int>() == at.Y && g[2]!.GetValue<int>() == at.Z);

    [Theory, MemberData(nameof(Versions))]
    public void A_wall_torch_is_not_offered_before_its_wall_stands(string v)
    {
        var (c, _, _) = Rig(v, new Stack(50, 0, 16));
        var torch = Add(c, 50, 1, Cell).Ghosts.Single(); // on the west wall: 3,64,12, still air; the stone floor is under it
        for (int i = 0; i < 3; i++)
        {
            Assert.False(PlaceAt(Pick(c), Cell));
            Assert.Equal("todo", torch.State);
            Assert.Null(torch.Reason);
        }
        Assert.False(PlaceAt(c.Next(), Cell));
        c.Tick();
        Assert.Contains("1 blocks have nothing to place against yet", c.IdleReasonOf(c.P()));
        Assert.DoesNotContain(c.Alerts, a => a.Text.Contains("blocked"));
    }

    [Theory, MemberData(nameof(Versions))]
    public void A_wall_torch_is_not_offered_before_its_wall_stands_and_is_offered_once_it_does(string v)
    {
        var (c, _, w) = Rig(v, new Stack(50, 0, 16), new Stack(5, 0, 16));
        var wallAt = Cell.Add(-1, 0, 0);
        var torch = Add(c, 50, 1, Cell).Ghosts.Single(); // first in the queue
        Add(c, 5, 0, wallAt);
        var wall = c.Next();
        Assert.True(PlaceAt(wall, wallAt)); // the torch's build has nothing ready: the wall's build goes first
        Assert.Equal("todo", torch.State);
        w.Set(wallAt.X, wallAt.Y, wallAt.Z, 5);
        c.Report(c.P(), Id(wall), "done", "batch: 1/1 placed", null);
        var j = c.Next();
        Assert.True(PlaceAt(j, Cell));
        Assert.Equal([Cell.ToString()], Strs(j["cells"]));
    }

    [Theory, MemberData(nameof(Versions))]
    public void A_floor_torch_needs_only_the_block_under_it(string v)
    {
        var (c, _, _) = Rig(v, new Stack(50, 0, 16));
        Add(c, 50, 5, Cell); // on the stone floor, nothing beside it
        Assert.True(PlaceAt(Pick(c), Cell));

        var (c2, _, w2) = Rig(v, new Stack(50, 0, 16));
        var up = Cell.Add(0, 1, 0);
        w2.Set(up.X - 1, up.Y, up.Z, 1); // a wall beside it, air under it: a floor torch has nothing to stand on
        w2.Set(up.X - 1, up.Y - 1, up.Z, 1);
        var floating = Add(c2, 50, 5, up).Ghosts.Single();
        var j = Pick(c2);
        Assert.False(PlaceAt(j, up));
        Assert.Null(j["scaffold"]);
        Assert.Equal("todo", floating.State);
    }

    [Theory, MemberData(nameof(Versions))]
    public void A_chest_needs_any_one_neighbour(string v)
    {
        var (c, _, w) = Rig(v, new Stack(54, 0, 4));
        var at = Cell.Add(0, 2, 0); // air under it; only a stone block beside it
        w.Set(at.X + 1, at.Y, at.Z, 1);
        Add(c, 54, 2, at);
        var j = Pick(c);
        Assert.True(PlaceAt(j, at));
        Assert.Null(j["scaffold"]);
    }

    [Theory]
    [InlineData(175, 2)] // a two-tall plant (large fern)
    [InlineData(106, 1)] // a vine
    [InlineData(32, 0)]  // a dead bush
    public void A_cell_holding_a_two_tall_plant_a_vine_or_a_dead_bush_is_cleared_before_its_block_goes_in(int plant, int meta)
    {
        var (c, _, w) = Rig("1.12.2", new Stack(5, 0, 16));
        w.Set(Cell.X, Cell.Y, Cell.Z, plant, meta);
        if (plant == 175) w.Set(Cell.X, Cell.Y + 1, Cell.Z, 175, 8);
        var g = Add(c, 5, 0, Cell).Ghosts.Single();
        c.Reconcile(w);
        var shown = GhostOf(c, Cell);
        Assert.Equal("clear", shown[6]!.GetValue<string>());
        Assert.Contains("dug out first", shown[8]!.GetValue<string>());
        Assert.Equal("clear", g.Holds);
        var j = Pick(c);
        Assert.True(PlaceAt(j, Cell));
        Assert.Equal([Cell.ToString()], Strs(j["clear"]));
        Assert.True(StoryTools.For(w.Data, plant).Hand); // dug by hand: no tool planned for it
    }

    [Theory, MemberData(nameof(Versions))]
    public void A_wall_torch_without_a_wall_gets_no_scaffold(string v)
    {
        var (c, _, _) = Rig(v, new Stack(50, 0, 16));
        var at = Cell.Add(0, 1, 0); // nothing to click at all; the air under it could hold a scaffold block
        var torch = Add(c, 50, 1, at).Ghosts.Single();
        for (int i = 0; i < 3; i++)
        {
            var j = Pick(c);
            Assert.False(PlaceAt(j, at));
            Assert.Null(j["scaffold"]);
        }
        Assert.Equal("todo", torch.State);
        Assert.False(PlaceAt(c.Next(), at));
        c.Tick();
        Assert.Contains("nothing to place against yet", c.IdleReasonOf(c.P()));
    }

    [Fact]
    public void A_turned_blocks_scaffold_is_on_a_face_it_can_be_placed_against()
    {
        var (c, _, w) = Rig("1.12.2", new Stack(53, 0, 8));
        var at = Cell.Add(0, 1, 0); // upside-down stairs: a side high or the underside, never the top face of a block under it
        w.Set(at.X + 1, at.Y - 1, at.Z, 1); // so the spot east of it has something to stand on
        Add(c, 53, 4, at);
        var j = Pick(c);
        Assert.True(PlaceAt(j, at));
        Assert.Equal(at.Add(1, 0, 0).ToString(), j["scaffold"]?[at.ToString()]?.GetValue<string>());
    }
}
