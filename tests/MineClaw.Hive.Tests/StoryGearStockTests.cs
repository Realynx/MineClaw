using System.Text.Json.Nodes;
using MineClaw.Hive.Story;

namespace MineClaw.Hive.Tests;

/// <summary>
/// Tools are colony stock (live 11-04, Fox: "one pawn holding 13 stone pickaxes"): a pawn keeps what its policy needs, the rest goes
/// back to storage where the other pawns take theirs; spare and auto tool targets count every pocket.
/// </summary>
public sealed class StoryGearStockTests
{
    private static readonly P3 Chest = new(0, 64, 9), Table = new(3, 64, -3);

    private static void Only(StoryColony c, int i, params string[] types)
    {
        var p = c.Pawns.Values.ElementAt(i);
        foreach (var t in WorkTypes.All) p.Priorities[t] = types.Contains(t) ? 1 : 0;
    }

    private static string Kind(JsonObject j) => j["kind"]!.GetValue<string>();
    private static void Done(StoryColony c, int pawn, JsonObject job) => Assert.True(c.Report(c.P(pawn), job["id"]!.GetValue<string>(), "done", null, null));

    private static void Store(StoryColony c, params Stack[] stock)
    {
        if (c.StorageAt(Chest) == null) Assert.Null(c.StorageOp("set", [Chest], null, null, null).Error);
        foreach (var s in stock) c.AddContents(c.StorageAt(Chest)!, s);
    }

    private static int Stored(StoryColony c, int id) => c.StorageAt(Chest)!.Contents.Where(s => s.Id == id).Sum(s => s.Count);

    private static JsonObject StoreOf(JsonObject job, string item) =>
        job["store"]!.AsArray().Select(x => x!.AsObject()).Single(x => x["item"]!.GetValue<string>() == item);

    [Fact]
    public void A_pawn_with_13_pickaxes_returns_12_and_the_other_miners_take_one_each()
    {
        var (c, h, w) = StoryRig.Make(3);
        for (int i = 0; i < 3; i++) Only(c, i, WorkTypes.Mine);
        var pick = w.Data.Item(274).Name;
        h.Lives[c.P(0)] = FakeStoryHost.Bare(0, 64, 0, new Stack(274, 0, 13));
        h.Lives[c.P(1)] = FakeStoryHost.Bare(1, 64, 0);
        h.Lives[c.P(2)] = FakeStoryHost.Bare(2, 64, 0);
        Store(c);
        var j = c.Next(0);
        Assert.Equal("equip", Kind(j));
        var st = StoreOf(j, pick);
        Assert.Equal(1, st["keep"]!.GetValue<int>()); // a miner keeps one
        Assert.Equal(Chest.ToString(), st["to"]!.GetValue<string>());
        Done(c, 0, j);
        Assert.Equal(12, Stored(c, 274));
        h.Lives[c.P(0)] = FakeStoryHost.Bare(0, 64, 0, new Stack(274, 0, 1));
        // The other miners fetch one each (claimed: never the same one twice).
        var j1 = c.Next(1);
        var j2 = c.Next(2);
        foreach (var x in new[] { j1, j2 })
        {
            Assert.Equal("equip", Kind(x));
            Assert.Equal(1, x["fetch"]!.AsArray().Count(f => f!["item"]!.GetValue<string>() == pick && f["n"]!.GetValue<int>() == 1));
        }
        Done(c, 1, j1);
        Done(c, 2, j2);
        Assert.Equal(10, Stored(c, 274));
    }

    [Fact]
    public void A_crafter_keeps_at_most_one_tool_its_policy_does_not_need_and_a_miner_keeps_one()
    {
        var (c, h, w) = StoryRig.Make(2);
        Only(c, 0, WorkTypes.Craft);
        Only(c, 1, WorkTypes.Craft, WorkTypes.Mine);
        h.Lives[c.P(0)] = FakeStoryHost.Bare(0, 64, 0, new Stack(274, 0, 5));
        h.Lives[c.P(1)] = FakeStoryHost.Bare(1, 64, 0, new Stack(274, 0, 5));
        Store(c);
        var pick = w.Data.Item(274).Name;
        Assert.Equal(1, StoreOf(c.Next(0), pick)["keep"]!.GetValue<int>()); // one stray may stay (nobody lacks one)
        Assert.Equal(1, StoreOf(c.Next(1), pick)["keep"]!.GetValue<int>());
    }

    [Fact]
    public void A_tool_bills_product_goes_to_storage_and_the_spare_target_counts_pockets()
    {
        var (c, h, w) = StoryRig.Make(2);
        Only(c, 0, WorkTypes.Craft);
        Only(c, 1, WorkTypes.Mine);
        w.Set(Table.X, Table.Y, Table.Z, 58);
        Assert.Null(c.WorkbenchOp("designate", Table).Error);
        h.Lives[c.P(0)] = FakeStoryHost.Bare(0, 64, 0);
        h.Lives[c.P(1)] = FakeStoryHost.Bare(1, 64, 0, new Stack(274, 0, 2)); // two stone pickaxes in a pocket
        Store(c, new Stack(4, 0, 9), new Stack(280, 0, 6));
        Assert.Null(c.SetGearOptions(true, new JsonObject { ["count"] = 2, ["kinds"] = new JsonArray("pickaxe") }));
        c.State();
        Assert.Empty(c.Benches.Single().Bills); // the pocketed ones count: no over-crafting
        h.Lives[c.P(1)] = FakeStoryHost.Bare(1, 64, 0);
        Assert.Null(c.SetGearOptions(null, null)); // plan again
        c.State();
        var bill = c.Benches.Single().Bills.Single();
        Assert.Equal(StoryColony.ToolsTag, bill.ForBuild);
        var job = c.Next(0);
        Assert.Equal("craft", Kind(job));
        Assert.Equal(Chest.ToString(), job["stash"]![0]!["to"]!.GetValue<string>()); // colony stock: into storage, not kept
    }

    [Fact]
    public void A_job_that_fetches_a_better_tool_returns_the_one_it_replaces()
    {
        var (c, h, w) = StoryRig.Make(1);
        Only(c, 0, WorkTypes.Mine);
        w.Set(3, 64, 3, 15); // iron ore: the wooden pickaxe won't do
        h.Lives[c.P()] = FakeStoryHost.Bare(0, 64, 0, new Stack(270, 0, 1));
        Store(c, new Stack(274, 0, 1));
        c.Designate("add", "mine", null, [new P3(3, 64, 3)], null);
        var j = c.Next(0);
        Assert.Equal("dig", Kind(j));
        var back = StoreOf(j, w.Data.Item(270).Name);
        Assert.Equal(0, back["keep"]!.GetValue<int>());
        Assert.Equal(Chest.ToString(), back["to"]!.GetValue<string>());
        w.Set(3, 64, 3, 0);
        Done(c, 0, j);
        Assert.Equal(1, Stored(c, 270));
        Assert.Equal(0, Stored(c, 274));
    }

    [Fact]
    public void A_hauler_keeps_one_weapon_and_one_stray_tool_and_gives_that_up_when_a_miner_has_none()
    {
        var (c, h, w) = StoryRig.Make(1);
        Only(c, 0, WorkTypes.Haul);
        h.Lives[c.P()] = FakeStoryHost.Bare(0, 64, 0, new Stack(267, 0, 3), new Stack(274, 0, 2));
        Store(c);
        var j = c.Next(0);
        Assert.Equal("equip", Kind(j));
        Assert.Equal(1, StoreOf(j, w.Data.Item(267).Name)["keep"]!.GetValue<int>());
        Assert.Equal(1, StoreOf(j, w.Data.Item(274).Name)["keep"]!.GetValue<int>());
        Done(c, 0, j);
        Assert.Equal(2, Stored(c, 267));
        Assert.Equal(1, Stored(c, 274));

        // A miner with no pickaxe and none in storage: the hauler's stray one goes at once.
        var (c2, h2, w2) = StoryRig.Make(2);
        Only(c2, 0, WorkTypes.Haul);
        Only(c2, 1, WorkTypes.Mine);
        h2.Lives[c2.P(0)] = FakeStoryHost.Bare(0, 64, 0, new Stack(274, 0, 1));
        h2.Lives[c2.P(1)] = FakeStoryHost.Bare(1, 64, 0);
        Store(c2);
        var give = c2.Next(0);
        Assert.Equal("equip", Kind(give));
        Assert.Equal(0, StoreOf(give, w2.Data.Item(274).Name)["keep"]!.GetValue<int>());
    }
}
