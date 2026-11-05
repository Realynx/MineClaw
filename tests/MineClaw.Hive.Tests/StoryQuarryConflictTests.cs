using System.Text.Json.Nodes;
using MineClaw.Data.Versioning;
using MineClaw.Hive.Story;

namespace MineClaw.Hive.Tests;

/// <summary>
/// A cell is never both "to dig" and "to place" (docs/web/story-mode.md "Quarries win over builds"). Live 11-05 story-1, Fox: "The
/// pawns are broken at the quarry. They keep placing and deleting blocks on the top level." A dirt floor build (b11401, 432 blocks at
/// y 63) covered quarry z17860's top layer: the quarry dug its cells, the reconcile reopened them, builders placed the dirt back, and
/// the quarry called everyone up out of the pit to dig them again; a pawn 17 blocks down was handed rim placements.
/// </summary>
public sealed class StoryQuarryConflictTests
{
    private const int X0 = 10, Z0 = 10, BedrockTop = 2;
    private static readonly P3 Chest = new(0, 64, 9);

    public static TheoryData<string> Versions => new() { "1.12.2", "b1.8.1" };

    private static IGameData Data(string v) => v == "b1.8.1" ? Beta181GameData.Instance : Release1122GameData.Instance;

    private static (StoryColony C, FakeStoryHost H, FakeStoryWorld W) Rig(string v, int pawns = 1)
    {
        var (c, h, w) = StoryRig.Make(pawns, Data(v));
        c.DoneGrace = TimeSpan.Zero;
        for (int x = X0 - 4; x < X0 + 10; x++)
            for (int z = Z0 - 4; z < Z0 + 10; z++)
            {
                for (int y = 0; y <= BedrockTop; y++) w.Set(x, y, z, 7);
                w.Set(x, 63, z, 2); // grass on top
            }
        w.Set(Chest.X, Chest.Y, Chest.Z, 54, 3);
        c.StorageOp("set", [Chest], null, null, null);
        c.AddContents(c.StorageAt(Chest)!, new Stack(3, 0, 64));
        c.AddContents(c.StorageAt(Chest)!, new Stack(4, 0, 64));
        return (c, h, w);
    }

    private static string AddQuarry(StoryColony c, int size)
    {
        var (res, err) = c.ZoneOp("add", "mine", Area.Of(X0, 63, Z0, X0 + size - 1, 63, Z0 + size - 1));
        Assert.True(err == null, err);
        return res!["id"]!.GetValue<string>();
    }

    private static (BuildState B, JsonObject Result) AddFloor(StoryColony c, int x1, int z1, int x2, int z2, int y = 63, int id = 3)
    {
        var item = new JsonObject { ["kind"] = "floor", ["area"] = new JsonArray(x1, y, z1, x2, y, z2), ["material"] = new JsonArray(id, 0) };
        var (res, err) = c.BuildsOp("add", new JsonArray(item), null);
        Assert.True(err == null, err);
        return (c.Builds.Last(), res!.AsObject());
    }

    private static P3 At(JsonObject j) => P3.Parse(JsonNode.Parse("[" + j["at"]!.GetValue<string>() + "]"))!.Value;

    private static IEnumerable<P3> Cells(JsonObject j) =>
        j["cells"] is JsonArray a ? a.Select(n => P3.Parse(JsonNode.Parse("[" + n!.GetValue<string>() + "]"))!.Value) : [At(j)];

    [Theory, MemberData(nameof(Versions))]
    public void A_dirt_floor_over_a_quarrys_top_layer_never_churns_the_quarry_digs_it_once_and_the_overlap_is_said_once(string v)
    {
        var (c, h, w) = Rig(v);
        foreach (var t in WorkTypes.All) c.Pawns.Values.First().Priorities[t] = t is WorkTypes.Mine or WorkTypes.Build ? 1 : 0;
        h.Lives[c.P()] = FakeStoryHost.Live(X0 - 3, 64, Z0, inv: new Stack(4, 0, 64));
        var qid = AddQuarry(c, 4);
        // The floor covers the quarry and a ring around it (live: 432 blocks, the quarry's rim inside).
        var (floor, added) = AddFloor(c, X0 - 2, Z0 - 2, X0 + 5, Z0 + 5);
        Assert.Contains(added["warnings"]!.AsArray(), x => x!.GetValue<string>().Contains($"lie in quarry {qid}"));
        bool InQuarry(P3 p) => p.X >= X0 && p.X < X0 + 4 && p.Z >= Z0 && p.Z < Z0 + 4 && p.Y <= 63;
        var ring = StoryQuarry.Ring(c.Zones.Single().Cells)!;
        var steps = Enumerable.Range(0, 63 - BedrockTop).Select(k => StoryQuarry.Step(ring, 63, k)).ToHashSet();
        var dug = new Dictionary<P3, int>();
        int quarryPlaces = 0;
        for (int i = 0; i < 400; i++)
        {
            c.Reconcile(w); // the 5-second pass, every job (live: it reopened the dug cells and they were placed again)
            var j = c.Next();
            var kind = j["kind"]!.GetValue<string>();
            if (kind is not ("dig" or "place")) break;
            if (kind == "dig")
            {
                var at = At(j);
                Assert.DoesNotContain(at, steps); // the quarry never digs its own steps
                dug[at] = dug.GetValueOrDefault(at) + 1;
                w.Set(at.X, at.Y, at.Z, 0);
            }
            else
                foreach (var p in Cells(j))
                {
                    if (InQuarry(p)) quarryPlaces++;
                    w.Set(p.X, p.Y, p.Z, 3);
                }
            c.Report(c.P(), j["id"]!.GetValue<string>(), "done", null, null);
            if (c.Zones.Single().QuarryLayer >= 4) break;
        }
        Assert.Equal(0, quarryPlaces); // nothing placed in the quarry: no place/dig loop
        Assert.All(dug, d => Assert.True(d.Value == 1, $"{d.Key} dug {d.Value} times"));
        Assert.True(c.Zones.Single().QuarryLayer >= 4, $"the quarry stalled at layer {c.Zones.Single().QuarryLayer}");
        // The top layer: every cell dug but the step, which stands.
        var step0 = StoryQuarry.Step(ring, 63, 0);
        Assert.NotEqual(0, w.Block(step0.X, step0.Y, step0.Z).Id);
        for (int x = X0; x < X0 + 4; x++)
            for (int z = Z0; z < Z0 + 4; z++)
                if (new P3(x, 63, z) != step0) Assert.Equal(0, w.Block(x, 63, z).Id);
        // The floor's cells in the quarry are blocked as the conflict (shown with the reason), the rest is built; said once.
        Assert.All(floor.Ghosts.Where(g => InQuarry(g.At)), g =>
        {
            Assert.Equal("blocked", g.State);
            Assert.StartsWith("conflicts with quarry", g.Reason);
        });
        Assert.All(floor.Ghosts.Where(g => !InQuarry(g.At)), g => Assert.Equal("done", g.State));
        Assert.Single(c.Alerts, a => a.Text.Contains("lie in quarry"));
        Assert.DoesNotContain(c.Alerts, a => a.Text.Contains("no longer match the design"));
        Assert.Equal("blocked", floor.Status);
    }

    [Theory, MemberData(nameof(Versions))]
    public void A_finished_floor_a_quarry_is_painted_over_is_said_and_never_reopened_and_erasing_the_quarry_frees_it(string v)
    {
        var (c, _, w) = Rig(v);
        var (floor, _) = AddFloor(c, X0, Z0, X0 + 3, Z0 + 3);
        c.Reconcile(w);
        Assert.Equal("done", floor.Status); // grass is the dirt floor (drift)
        var (res, err) = c.ZoneOp("add", "mine", Area.Of(X0, 63, Z0, X0 + 3, 63, Z0 + 3));
        Assert.Null(err);
        Assert.Contains(res!["warnings"]!.AsArray(), x => x!.GetValue<string>().Contains($"overlaps build {floor.Id}"));
        Assert.Single(c.Alerts, a => a.Text.Contains("lie in quarry"));
        // The quarry digs two of them: the reconcile never reopens them.
        w.Set(X0 + 1, 63, Z0 + 1, 0);
        w.Set(X0 + 2, 63, Z0 + 1, 0);
        for (int i = 0; i < 3; i++) c.Reconcile(w);
        Assert.All(floor.Ghosts, g => Assert.Equal("blocked", g.State));
        Assert.DoesNotContain(c.Alerts, a => a.Text.Contains("no longer match"));
        Assert.Single(c.Alerts, a => a.Text.Contains("lie in quarry"));
        // The quarry erased: the cells are the build's again.
        Assert.Null(c.ZoneOp("remove", "any", Area.Of(X0, 63, Z0, X0 + 3, 63, Z0 + 3)).Error);
        c.Reconcile(w);
        Assert.Equal("todo", floor.Ghosts.Single(g => g.At == new P3(X0 + 1, 63, Z0 + 1)).State);
        Assert.DoesNotContain(floor.Ghosts, g => g.Reason?.StartsWith("conflicts") == true);
    }

    [Theory, MemberData(nameof(Versions))]
    public void A_pawn_at_the_pit_bottom_is_not_given_placements_on_the_rim_a_builder_up_top_is(string v)
    {
        var (c, h, w) = Rig(v, pawns: 2);
        foreach (var p in c.Pawns.Values) foreach (var t in WorkTypes.All) p.Priorities[t] = t == WorkTypes.Build ? 1 : 0;
        AddQuarry(c, 6);
        for (int x = X0 + 1; x < X0 + 5; x++)
            for (int z = Z0 + 1; z < Z0 + 5; z++)
                for (int y = 47; y <= 63; y++) w.Set(x, y, z, 0); // the pit, its floor at y 46
        // A low wall on the rim, just outside the quarry (live: placements on the rim handed to a pawn at y 46).
        var wall = new JsonObject
        {
            ["kind"] = "block", ["blocks"] = new JsonArray(new JsonArray(X0 - 1, 64, Z0 + 2), new JsonArray(X0 - 1, 64, Z0 + 3)),
            ["material"] = new JsonArray(4, 0),
        };
        Assert.Null(c.BuildsOp("add", new JsonArray(wall), null).Error);
        h.Lives[c.P(0)] = FakeStoryHost.Live(X0 + 2, 47, Z0 + 2, inv: new Stack(4, 0, 16)); // down in the pit, carrying cobblestone
        h.Lives[c.P(1)] = FakeStoryHost.Live(X0 - 6, 64, Z0, inv: new Stack(4, 0, 16)); // up top, idle
        var down = c.Next(0);
        Assert.NotEqual("place", down["kind"]?.GetValue<string>());
        var up = c.Next(1);
        Assert.Equal("place", up["kind"]!.GetValue<string>());
        Assert.Equal(64, At(up).Y);
    }

    [Fact]
    public void A_lone_builder_in_the_pit_still_gets_the_rim_it_walks_up_the_stair()
    {
        var (c, h, w) = Rig("1.12.2");
        foreach (var t in WorkTypes.All) c.Pawns.Values.First().Priorities[t] = t == WorkTypes.Build ? 1 : 0;
        AddQuarry(c, 6);
        for (int x = X0 + 1; x < X0 + 5; x++)
            for (int z = Z0 + 1; z < Z0 + 5; z++)
                for (int y = 47; y <= 63; y++) w.Set(x, y, z, 0);
        var wall = new JsonObject { ["kind"] = "block", ["blocks"] = new JsonArray(new JsonArray(X0 - 1, 64, Z0 + 2)), ["material"] = new JsonArray(4, 0) };
        Assert.Null(c.BuildsOp("add", new JsonArray(wall), null).Error);
        h.Lives[c.P()] = FakeStoryHost.Live(X0 + 2, 47, Z0 + 2, inv: new Stack(4, 0, 16));
        Assert.Equal("place", c.Next()["kind"]!.GetValue<string>());
    }
}
