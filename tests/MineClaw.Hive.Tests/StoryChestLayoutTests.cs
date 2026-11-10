using System.Text.Json.Nodes;
using MineClaw.Data.Versioning;
using MineClaw.Hive.Story;

namespace MineClaw.Hive.Tests;

/// <summary>
/// A design's chests and the game's chest rule (docs/web/story-mode.md "Chests in a design"; Fox 11-09: "Even now, there's some blocked
/// blocks on the storehouse build"). Vanilla never places a chest beside a double chest of its kind, nor between two: a row of six
/// chests handed to pawns whole came back "server undid my placement (protected area?)" for every third one, three times each, and stood
/// blocked with a reason that pointed at protection. Now such cells are blocked from the start with the rule's reason, the answer and
/// one alert say so, no pawn is sent to them, and they open again once the chest beside them is gone.
/// </summary>
public sealed class StoryChestLayoutTests
{
    private const int Z = 12;

    /// <summary>One pawn that only builds, carrying chests (and trapped chests on 1.12.2).</summary>
    private static (StoryColony C, FakeStoryHost H, FakeStoryWorld W) Rig(IGameData data)
    {
        var (c, h, w) = StoryRig.Make(1, data, new ManualTime());
        foreach (var t in WorkTypes.All) c.Pawns[c.P()].Priorities[t] = t == WorkTypes.Build ? 1 : 0;
        h.Lives[c.P()] = FakeStoryHost.Live(0, 64, 4, inv: [new Stack(54, 0, 16), new Stack(146, 0, 16)]);
        return (c, h, w);
    }

    private static IGameData Data(string version) => version == "b1.8.1" ? Beta181GameData.Instance : Release1122GameData.Instance;

    private static JsonObject Chests(int id, params int[][] cells) => new()
    {
        ["kind"] = "block", ["blocks"] = new JsonArray(cells.Select(p => (JsonNode)new JsonArray(p[0], p[1], p[2])).ToArray()),
        ["material"] = new JsonArray(id, 3),
    };

    private static int[][] Row(int from, int to, int z = Z) => Enumerable.Range(from, to - from + 1).Select(x => new[] { x, 64, z }).ToArray();

    private static (JsonObject Answer, List<BuildState> Builds) Add(StoryColony c, params JsonObject[] items)
    {
        int before = c.Builds.Count;
        var r = c.BuildsOp("add", new JsonArray(items.Select(i => (JsonNode)i).ToArray()), null);
        Assert.Null(r.Error);
        return ((JsonObject)r.Result!, c.Builds.Skip(before).ToList());
    }

    private static List<string> Warnings(JsonObject answer) => answer["warnings"]?.AsArray().Select(x => x!.GetValue<string>()).ToList() ?? [];

    private static GhostState Cell(BuildState b, int x, int z = Z) => b.Ghosts.Single(g => g.At == new P3(x, 64, z));

    private static string Id(JsonObject j) => j["id"]!.GetValue<string>();

    private static List<string> Cells(JsonObject j) => j["cells"]?.AsArray().Select(x => x!.GetValue<string>()).ToList() ?? [];

    /// <summary>Every place job the pawn is given until there is none, built as asked (the world shows each chest); the cells offered.</summary>
    private static List<string> BuildAll(StoryColony c, FakeStoryWorld w)
    {
        var offered = new List<string>();
        for (int i = 0; i < 12; i++)
        {
            var j = c.Next();
            if (j["kind"]?.GetValue<string>() != "place") break;
            var cells = Cells(j);
            offered.AddRange(cells);
            foreach (var cell in cells)
            {
                var p = cell.Split(',').Select(int.Parse).ToArray();
                var g = c.Builds.SelectMany(b => b.Ghosts).First(x => x.At == new P3(p[0], p[1], p[2]));
                w.Set(p[0], p[1], p[2], g.Id, g.Meta);
            }
            Assert.True(c.Report(c.P(), Id(j), "done", $"batch: {cells.Count}/{cells.Count} placed", null));
        }
        return offered;
    }

    [Theory]
    [InlineData("b1.8.1")]
    [InlineData("1.12.2")]
    public void A_row_of_six_chests_queues_four_and_marks_the_two_the_game_refuses(string version)
    {
        var (c, _, w) = Rig(Data(version));
        var (answer, builds) = Add(c, Chests(54, Row(0, 5)));
        var b = builds.Single();
        string hint = version == "b1.8.1" ? "(leave a gap)" : "(use a trapped chest or leave a gap)";
        Assert.Equal("blocked", Cell(b, 2).State);
        Assert.Equal($"vanilla can't place a chest here: it would touch the double chest at 1,64,{Z} {hint}", Cell(b, 2).Reason);
        Assert.Equal("blocked", Cell(b, 5).State);
        Assert.Equal($"vanilla can't place a chest here: it would touch the double chest at 4,64,{Z} {hint}", Cell(b, 5).Reason);
        Assert.All(new[] { 0, 1, 3, 4 }, x => Assert.Equal("todo", Cell(b, x).State));
        Assert.Equal([$"build {b.Id}: 2 chests left blocked at 2,64,{Z}; 5,64,{Z}: {Cell(b, 2).Reason}"], Warnings(answer));
        Assert.Single(c.Alerts, a => a.Text.Contains(StoryConstruction.ChestRule));
        c.EnsureMaterials();
        Assert.Equal(4, b.Materials.Single().Need);
        // No pawn is sent to them, through every pass and pick.
        var offered = BuildAll(c, w);
        Assert.Equal(4, offered.Count);
        Assert.DoesNotContain($"2,64,{Z}", offered);
        Assert.DoesNotContain($"5,64,{Z}", offered);
        c.Reconcile(w);
        Assert.Equal("blocked", Cell(b, 2).State);
        Assert.Equal("blocked", Cell(b, 5).State);
        Assert.Single(c.Alerts, a => a.Text.Contains(StoryConstruction.ChestRule));
    }

    [Fact]
    public void Pairs_with_a_gap_and_chest_trapped_chest_alternation_are_all_offered()
    {
        var (c, _, w) = Rig(Release1122GameData.Instance);
        // Two pairs with a gap; and pairs of alternating kinds touching end to end (a trapped chest never pairs with a chest).
        var (answer, builds) = Add(c, Chests(54, [0, 64, Z], [1, 64, Z], [3, 64, Z], [4, 64, Z]),
            Chests(54, [0, 64, 14], [1, 64, 14], [4, 64, 14], [5, 64, 14]), Chests(146, [2, 64, 14], [3, 64, 14]));
        Assert.Empty(Warnings(answer));
        Assert.All(builds.SelectMany(b => b.Ghosts), g => Assert.Equal("todo", g.State));
        var offered = BuildAll(c, w);
        Assert.Equal(10, offered.Distinct().Count());
        Assert.All(builds.SelectMany(b => b.Ghosts), g => Assert.Equal("done", g.State));
    }

    [Fact]
    public void A_design_chest_beside_an_existing_double_chest_is_marked_and_names_it()
    {
        var (c, _, w) = Rig(Release1122GameData.Instance);
        w.Set(0, 64, Z, 54, 2);
        w.Set(1, 64, Z, 54, 2);
        w.Set(7, 64, Z, 146, 2); // another kind beside the second cell: no clash
        var (answer, builds) = Add(c, Chests(54, [2, 64, Z], [6, 64, Z]));
        var b = builds.Single();
        Assert.Equal("blocked", Cell(b, 2).State);
        Assert.Equal($"vanilla can't place a chest here: it would touch the double chest at 1,64,{Z} (use a trapped chest or leave a gap)", Cell(b, 2).Reason);
        Assert.Equal("todo", Cell(b, 6).State);
        Assert.Contains($"double chest at 1,64,{Z}", Assert.Single(Warnings(answer)));
        Assert.Equal([$"6,64,{Z}"], BuildAll(c, w));
    }

    [Fact]
    public void A_client_chest_refusal_blocks_the_cell_at_once_with_one_alert()
    {
        var (c, _, w) = Rig(Release1122GameData.Instance);
        var (_, builds) = Add(c, Chests(54, [2, 64, Z], [2, 64, 16]));
        var b = builds.Single();
        // Chests the queue could not know of: placed by hand afterwards, a double chest beside each cell.
        foreach (var z in new[] { Z, 16 })
        {
            w.Set(0, 64, z, 54, 2);
            w.Set(1, 64, z, 54, 2);
        }
        for (int i = 0; i < 4 && b.Ghosts.Any(g => g.State == "todo"); i++)
        {
            var j = c.Next();
            Assert.Equal("place", j["kind"]!.GetValue<string>());
            foreach (var at in Cells(j))
                c.Report(c.P(), Id(j), "progress", $"cell {at} failed: can't place a chest at {at}: it would touch the double chest at 1,64,{at.Split(',')[2]}", null);
            c.Report(c.P(), Id(j), "done", $"batch: 0/{Cells(j).Count} placed", null);
        }
        foreach (var z in new[] { Z, 16 })
        {
            var g = Cell(b, 2, z);
            Assert.Equal("blocked", g.State); // one trip, not three
            Assert.Equal($"vanilla can't place a chest here: it would touch the double chest at 1,64,{z} (use a trapped chest or leave a gap)", g.Reason);
        }
        Assert.Single(c.Alerts, a => a.Text.Contains(StoryConstruction.ChestRule));
        Assert.Empty(BuildAll(c, w));
    }

    [Fact]
    public void A_cell_blocked_by_a_server_refusal_is_relabelled_when_the_chest_rule_explains_it()
    {
        var (c, _, w) = Rig(Release1122GameData.Instance);
        var (_, builds) = Add(c, Chests(54, [2, 64, Z], [2, 64, 20]));
        var b = builds.Single();
        // As the storehouse's save holds them: refused by the server three times before the colony knew the rule.
        foreach (var z in new[] { Z, 20 })
        {
            Cell(b, 2, z).State = "blocked";
            Cell(b, 2, z).Reason = $"server undid my placement at 2,64,{z} (protected area?)";
        }
        w.Set(0, 64, Z, 54, 2);
        w.Set(1, 64, Z, 54, 2);
        c.Reconcile(w);
        Assert.Equal("blocked", Cell(b, 2).State);
        Assert.Equal($"vanilla can't place a chest here: it would touch the double chest at 1,64,{Z} (use a trapped chest or leave a gap)", Cell(b, 2).Reason);
        // No chest beside the other one: the rule doesn't explain it, and its reason stays as it was.
        Assert.Equal("server undid my placement at 2,64,20 (protected area?)", Cell(b, 2, 20).Reason);
        // The chest rule's cell is none of the blocked-cell tries (the other cell is one: it was blocked before this process).
        var offered = BuildAll(c, w);
        Assert.Contains("2,64,20", offered);
        Assert.DoesNotContain($"2,64,{Z}", offered);
        Assert.Equal("blocked", Cell(b, 2).State);
    }

    [Fact]
    public void A_chest_rule_cell_reopens_when_the_chest_beside_it_is_gone()
    {
        var (c, _, w) = Rig(Release1122GameData.Instance);
        w.Set(0, 64, Z, 54, 2);
        w.Set(1, 64, Z, 54, 2);
        var (_, builds) = Add(c, Chests(54, [2, 64, Z]));
        var g = builds.Single().Ghosts.Single();
        Assert.Equal("blocked", g.State);
        c.Reconcile(w);
        Assert.Equal("blocked", g.State);
        w.Set(1, 64, Z, 0); // the owner took the chest beside it away
        c.Reconcile(w);
        Assert.Equal("todo", g.State);
        Assert.Null(g.Reason);
        Assert.Equal([$"2,64,{Z}"], BuildAll(c, w));
    }

    private static List<string> Reasons(JsonObject retry) => retry["reasons"]!.AsArray().Select(x => x!["reason"]!.GetValue<string>()).ToList();

    [Theory]
    [InlineData("b1.8.1")]
    [InlineData("1.12.2")]
    public void Retry_leaves_a_chest_rule_cell_blocked_and_says_why(string version)
    {
        var (c, _, w) = Rig(Data(version));
        var (_, builds) = Add(c, Chests(54, Row(0, 5)));
        var b = builds.Single();
        string why2 = Cell(b, 2).Reason!, why5 = Cell(b, 5).Reason!;
        Assert.Equal(0, c.FailedCells()); // the game's rule, not a failure: "Retry failed cells (N)" has nothing to count
        // Asked for by cell: nothing opens, and the answer says why.
        var one = (JsonObject)c.RetryBuilds("retry", [b.Id], [new P3(2, 64, Z)]).Result!;
        Assert.Equal(0, one["reopened"]!.GetValue<int>());
        Assert.Equal([why2], Reasons(one));
        Assert.Equal("blocked", Cell(b, 2).State);
        Assert.Equal(why2, Cell(b, 2).Reason);
        var offered = BuildAll(c, w);
        Assert.Equal(4, offered.Count);
        Assert.DoesNotContain($"2,64,{Z}", offered);
        Assert.DoesNotContain($"5,64,{Z}", offered);
        // All the build has left is the rule's: a retry of the whole build says so and sends no pawn.
        var all = (JsonObject)c.RetryBuilds("retry", [b.Id], null).Result!;
        Assert.Equal(0, all["reopened"]!.GetValue<int>());
        Assert.Equal([why2], Reasons(all));
        Assert.Equal((why2, why5), (Cell(b, 2).Reason, Cell(b, 5).Reason));
        Assert.All(new[] { 2, 5 }, x => Assert.Equal("blocked", Cell(b, x).State));
        Assert.Empty(BuildAll(c, w));
    }

    [Fact]
    public void Retry_opens_a_chest_rule_cell_the_rule_allows_now()
    {
        var (c, _, w) = Rig(Release1122GameData.Instance);
        w.Set(0, 64, Z, 54, 2);
        w.Set(1, 64, Z, 54, 2);
        var (_, builds) = Add(c, Chests(54, [2, 64, Z]));
        var g = builds.Single().Ghosts.Single();
        w.Set(1, 64, Z, 0); // the owner took the chest beside it away; no pass has run since
        var r = (JsonObject)c.RetryBuilds("retry", [builds[0].Id], null).Result!;
        Assert.Equal(1, r["reopened"]!.GetValue<int>());
        Assert.Empty(Reasons(r));
        Assert.Equal("todo", g.State);
        Assert.Null(g.Reason);
        Assert.Equal([$"2,64,{Z}"], BuildAll(c, w));
    }

    [Fact]
    public void A_design_with_a_block_on_a_chest_says_so()
    {
        var (c, _, w) = Rig(Release1122GameData.Instance);
        w.Set(3, 65, Z, 1); // the world's stone on one
        w.Set(6, 65, Z, 20); // glass doesn't shut a lid
        var planks = new JsonObject { ["kind"] = "block", ["blocks"] = new JsonArray(new JsonArray(0, 65, Z)), ["material"] = new JsonArray(5, 0) };
        var (answer, builds) = Add(c, Chests(54, [0, 64, Z], [3, 64, Z], [6, 64, Z]), planks);
        Assert.Contains($"2 chests of this design sit under a solid block and will not open (0,64,{Z}; 3,64,{Z})", Warnings(answer));
        Assert.All(builds.SelectMany(b => b.Ghosts), x => Assert.Equal("todo", x.State)); // nothing blocked
    }
}
