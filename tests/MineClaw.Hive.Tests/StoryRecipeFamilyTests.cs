using System.Text.Json.Nodes;
using MineClaw.Data.Versioning;
using MineClaw.Hive.Story;

namespace MineClaw.Hive.Tests;

/// <summary>
/// Any-variant bills and stock planning (docs/web/story-mode.md "Any-variant recipes"). Fox: "we really shouldn't care which type of wood
/// we turn into planks"; live 11-04: "craft failed: no oak wood planks in the chest" with birch there, "stone_pickaxe until 10 done 11",
/// pawns resting from stock errors.
/// </summary>
public sealed class StoryRecipeFamilyTests
{
    private static readonly IGameData Modern = Release1122GameData.Instance;
    private static readonly P3 Chest = new(0, 64, 9), Table = new(3, 64, 0), Furnace = new(6, 64, 0);

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private readonly Clock _clock = new();

    private (StoryColony C, FakeStoryHost H, FakeStoryWorld W) Rig(IGameData? data = null, params Stack[] stock)
    {
        var (c, h, w) = StoryRig.Make(pawns: 1, data: data ?? Modern, time: _clock);
        foreach (var t in WorkTypes.All) c.Pawns.Values.First().Priorities[t] = t is WorkTypes.Craft or WorkTypes.Cook ? 1 : 0;
        h.Lives[c.P()] = FakeStoryHost.Bare(0, 64, 0);
        Assert.Null(c.StorageOp("set", [Chest], null, null, null).Error);
        foreach (var s in stock) c.AddContents(c.StorageAt(Chest)!, s);
        w.Set(Table.X, Table.Y, Table.Z, 58);
        Assert.Null(c.WorkbenchOp("designate", Table).Error);
        return (c, h, w);
    }

    private static string Bill(StoryColony c, string recipe, int target = 1, string mode = "count", P3? bench = null)
    {
        var r = c.BillsOp("add", new JsonObject
        {
            ["bench"] = (bench ?? Table).Json(), ["bill"] = new JsonObject { ["recipe"] = recipe, ["mode"] = mode, ["target"] = target },
        });
        Assert.True(r.Error == null, r.Error);
        return r.Result!["id"]!.GetValue<string>();
    }

    private static string Kind(JsonObject j) => j["kind"]!.GetValue<string>();

    private static List<(string Item, int N)> Fetched(JsonObject j) =>
        j["fetch"]!.AsArray().Select(f => (f!["item"]!.GetValue<string>(), f["n"]!.GetValue<int>())).ToList();

    /// <summary>The item name a job uses (fetches take by name: any variant of it).</summary>
    private static string Name(int id, int meta = 0) => Modern.Item(id).Name;

    [Fact]
    public void The_catalogue_has_any_variant_bills_first_and_labels_vanilla_wildcards()
    {
        var table = StoryRecipes.For(Modern, "crafting_table");
        var planks = table.Single(r => r.Id == "craft:planks_any");
        Assert.True(planks.Any && planks.AnyOutput);
        Assert.Equal("logs", planks.Family);
        Assert.Contains(planks.Inputs[0].Options, o => o.Id == 17);
        Assert.Contains(planks.Inputs[0].Options, o => o.Id == 162);
        Assert.True(table.ToList().FindIndex(r => r.Id == "craft:planks_any") < table.ToList().FindIndex(r => r.Output.Id == 5 && !r.Any));
        Assert.Contains("(from any planks)", table.Single(r => r.Id == "craft:stick").Label);
        Assert.Contains("(from any coal)", table.Single(r => r.Id == "craft:torch").Label);
        var furnace = StoryRecipes.For(Modern, "furnace");
        Assert.Contains(furnace, r => r.Id == "smelt:charcoal_any" && r.Inputs[0].Options.Select(o => o.Id).Order().SequenceEqual([17, 162]));
        Assert.Contains("(from any sand)", furnace.Single(r => r.Id == "smelt:sand").Label);
        Assert.True(StoryRecipes.Get(Modern, "craft:planks_any")!.Json()["any"]!.GetValue<bool>());
        // b1.8.1: one plank type, nothing to merge; the wildcard labels still read right.
        var old = StoryRecipes.For(Beta181GameData.Instance, "crafting_table");
        Assert.Contains(old, r => r.Id == "craft:stick");
    }

    [Fact]
    public void Sticks_take_birch_a_pickaxe_takes_mixed_planks_a_chest_takes_jungle_and_stairs_keep_their_wood()
    {
        var (c, _, _) = Rig(null, new Stack(5, 2, 4));
        Bill(c, "craft:stick", 4);
        Assert.Equal([(Name(5, 2), 2)], Fetched(c.Next()));

        var (c2, _, _) = Rig(null, new Stack(5, 1, 2), new Stack(5, 0, 1), new Stack(280, 0, 2));
        Bill(c2, "craft:" + Name(270));
        var pick = c2.Next();
        Assert.Equal("craft", Kind(pick));
        Assert.Equal(3, Fetched(pick).Where(f => f.Item != Name(280)).Sum(f => f.N)); // two spruce and one oak make one pickaxe

        var (c3, _, _) = Rig(null, new Stack(5, 3, 8));
        Bill(c3, "craft:" + Name(54));
        Assert.Equal([(Name(5, 3), 8)], Fetched(c3.Next()));

        var (c4, _, _) = Rig(null, new Stack(5, 2, 8)); // birch only
        Bill(c4, "craft:" + Name(53)); // oak stairs
        Assert.NotEqual("craft", Kind(c4.Next()));
        Assert.StartsWith("no oak", c4.Benches.Single().Bills.Single().SuspendedReason);
    }

    [Fact]
    public void Planks_from_any_logs_take_mixed_logs_and_until_counts_every_plank()
    {
        var (c, _, _) = Rig(null, new Stack(17, 0, 1), new Stack(17, 2, 1), new Stack(162, 0, 1));
        Bill(c, "craft:planks_any", 12);
        var j = c.Next();
        Assert.Equal("craft", Kind(j));
        Assert.Equal(3, Fetched(j).Sum(f => f.N)); // oak, birch and acacia: three crafts
        Assert.Equal(Modern.Item(5).Name, j["item"]!.GetValue<string>());

        var (c2, _, _) = Rig(null, new Stack(5, 0, 4), new Stack(5, 2, 4), new Stack(17, 0, 4));
        Bill(c2, "craft:planks_any", 8, "until");
        Assert.NotEqual("craft", Kind(c2.Next())); // 4 oak + 4 birch planks are the 8 asked for
    }

    [Fact]
    public void Charcoal_from_any_log_glass_from_red_sand_and_torches_from_charcoal()
    {
        var (c, _, w) = Rig(null, new Stack(162, 0, 4), new Stack(263, 0, 4));
        w.Set(Furnace.X, Furnace.Y, Furnace.Z, 61);
        Assert.Null(c.WorkbenchOp("designate", Furnace).Error);
        Bill(c, "smelt:charcoal_any", 4, bench: Furnace);
        var smelt = c.Next();
        Assert.Equal("smelt", Kind(smelt));
        Assert.Equal(Name(162), smelt["item"]!.GetValue<string>()); // the acacia log it fetched

        var (c2, _, w2) = Rig(null, new Stack(12, 1, 4), new Stack(263, 0, 4));
        w2.Set(Furnace.X, Furnace.Y, Furnace.Z, 61);
        Assert.Null(c2.WorkbenchOp("designate", Furnace).Error);
        Bill(c2, "smelt:sand", 4, bench: Furnace);
        Assert.Contains(Fetched(c2.Next()), f => f.Item == Name(12, 1));

        var (c3, _, _) = Rig(null, new Stack(263, 1, 2), new Stack(280, 0, 2));
        Bill(c3, "craft:torch", 4);
        Assert.Contains(Fetched(c3.Next()), f => f.Item == Name(263, 1));
    }

    [Fact]
    public void A_tool_chain_from_birch_logs_crafts_planks_from_any_logs()
    {
        var (c, _, _) = Rig(null, new Stack(17, 2, 8));
        Assert.Null(c.SetGearOptions(true, new JsonObject { ["count"] = 1, ["kinds"] = new JsonArray("pickaxe") }));
        c.State();
        var recipes = c.Benches.Single().Bills.Select(b => b.Recipe).ToList();
        Assert.Contains("craft:planks_any", recipes);
        Assert.Contains("craft:" + Name(270), recipes);
    }

    [Fact]
    public void A_bill_short_of_its_raw_material_suspends_with_why_rests_nobody_and_resumes_when_stock_appears()
    {
        var (c, _, _) = Rig(null, new Stack(3, 0, 10));
        var id = Bill(c, "craft:stick", 4);
        var j = c.Next();
        Assert.NotEqual("craft", Kind(j));
        var bill = c.Benches.Single().Bills.Single();
        Assert.Contains("chop trees", bill.SuspendedReason);
        c.Report(c.P(), j["id"]!.GetValue<string>(), "done", null, null);
        Assert.DoesNotContain("resting", c.Next()["label"]!.GetValue<string>());

        // A record that lied: planks on record, none in the chest. The fetch fails, the record is corrected, nobody rests.
        var (c2, _, _) = Rig(null, new Stack(5, 0, 4));
        Bill(c2, "craft:stick", 4);
        var craft = c2.Next();
        Assert.Equal("craft", Kind(craft));
        c2.Report(c2.P(), craft["id"]!.GetValue<string>(), "failed", $"no oak wood planks in the chest at {Chest} (it had: dirt x3)", null);
        var b2 = c2.Benches.Single().Bills.Single();
        Assert.NotEqual("craft", Kind(c2.Next()));
        Assert.StartsWith("no planks", b2.SuspendedReason);
        // Planks come in: the bill goes on by itself (the 30 s retry), and crafts.
        c2.AddContents(c2.StorageAt(Chest)!, new Stack(5, 4, 4));
        _clock.Now += TimeSpan.FromSeconds(31);
        c2.Tick();
        Assert.Null(b2.SuspendedReason);
        for (int i = 0; i < 4 && b2.Done < 4; i++)
        {
            if (c2.CurrentOf(c2.P()) is not { } cur) { c2.Next(); continue; }
            c2.Report(c2.P(), cur.Id, "done", null, null);
        }
        Assert.Equal(4, b2.Done); // crafted once the planks were there
        Assert.Equal(4, c2.StorageAt(Chest)!.Contents.Where(s => s.Id == 280).Sum(s => s.Count));
    }

    [Fact]
    public void An_until_bill_counts_used_tools_in_pockets()
    {
        var (c, h, _) = Rig(null, new Stack(274, 0, 1), new Stack(4, 0, 6), new Stack(280, 0, 4));
        h.Lives[c.P()] = FakeStoryHost.Bare(0, 64, 0, new Stack(274, 60, 1)); // a used stone pickaxe in the crafter's pocket
        Bill(c, "craft:" + Name(274), 2, "until");
        Assert.NotEqual("craft", Kind(c.Next())); // 1 stored + 1 used in a pocket = the 2 asked for
    }

    [Fact]
    public void Many_bills_on_one_bench_with_an_idle_crafter_raise_one_alert()
    {
        var (c, _, _) = Rig(null, new Stack(5, 0, 64));
        foreach (var r in new[] { "craft:stick", "craft:" + Name(58), "craft:" + Name(54), "craft:" + Name(270) }) Bill(c, r, 4);
        c.State();
        Assert.Contains(c.Alerts, a => a.Text.StartsWith("4 bills waiting on 1 crafting table"));
        int n = c.Alerts.Count;
        c.State();
        Assert.Equal(n, c.Alerts.Count); // once, not every poll
    }
}
