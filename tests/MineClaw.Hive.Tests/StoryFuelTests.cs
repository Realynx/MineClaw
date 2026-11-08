using System.Text.Json.Nodes;
using MineClaw.Data.Versioning;
using MineClaw.Hive.Story;

namespace MineClaw.Hive.Tests;

/// <summary>
/// Furnace fuel (docs/web/story-mode.md "Fuel"): the fuel need and the reason a bill waits, the furnace's choice and a bill's override
/// (a coal-only furnace never loads planks), auto leaving reserved planks to the builds, the fuel the furnace still holds, the brewing
/// stand's blaze powder, the workbench fuel op.
/// </summary>
public class StoryFuelTests
{
    private static readonly P3 Furnace = new(6, 64, 0), Chest = new(6, 64, 2);

    private sealed class FakeClock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.Parse("2026-11-04T12:00:00Z");
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static (StoryColony C, FakeStoryHost H, FakeStoryWorld W) Rig(IGameData? data = null, params Stack[] stock)
    {
        var (c, h, w) = StoryRig.Make(pawns: 2, data: data ?? Release1122GameData.Instance, time: new FakeClock());
        foreach (var p in c.Pawns.Values) foreach (var t in WorkTypes.All) p.Priorities[t] = t is WorkTypes.Craft or WorkTypes.Cook ? 1 : 0;
        w.Set(Furnace.X, Furnace.Y, Furnace.Z, 61);
        c.StorageOp("set", [Chest], null, null, null);
        foreach (var s in stock) c.AddContents(c.StorageAt(Chest)!, s);
        Assert.Null(c.WorkbenchOp("designate", Furnace).Error);
        return (c, h, w);
    }

    private static string Bill(StoryColony c, JsonObject bill)
    {
        var r = c.BillsOp("add", new JsonObject { ["bench"] = Furnace.Json(), ["bill"] = bill });
        Assert.Null(r.Error);
        return r.Result!["id"]!.GetValue<string>();
    }

    private static List<(string Item, int N)> Fetched(JsonObject j) =>
        (j["fetch"] as JsonArray ?? []).Select(f => (f!["item"]!.GetValue<string>(), f["n"]!.GetValue<int>())).ToList();

    private static JsonObject BillState(StoryColony c, int i = 0) =>
        c.State()["workbenches"]!.AsArray().Single(b => b!["kind"]!.GetValue<string>() == "furnace")!["bills"]![i]!.AsObject();

    [Fact]
    public void A_smelt_with_no_fuel_waits_and_shows_its_fuel_need()
    {
        var (c, h, w) = Rig(null, new Stack(15, 0, 4)); // iron ore, no fuel
        Bill(c, new JsonObject { ["recipe"] = "smelt:iron_ore", ["target"] = 4 });
        Assert.NotEqual("smelt", c.Next()["kind"]!.GetValue<string>());
        var bill = c.Benches.Single().Bills.Single();
        Assert.Equal("needs fuel: no coal, charcoal, planks or logs within 32 blocks", bill.SuspendedReason);
        var need = BillState(c)["fuel_need"]!;
        Assert.True(need["short"]!.GetValue<bool>());
        Assert.Equal("auto", need["choice"]!.GetValue<string>());
        Assert.Equal(1, need["need"]!.GetValue<int>()); // 4 items: one coal
        Assert.Equal(0.125, need["per_item"]!.GetValue<double>());
        var recipe = StoryRecipes.Get(Release1122GameData.Instance, "smelt:iron_ore")!.Json(Release1122GameData.Instance);
        Assert.Contains(recipe["fuels"]!.AsArray(), f => f!["name"]!.GetValue<string>() == "coal" && f["items_per_unit"]!.GetValue<double>() == 8);
        Assert.Contains(recipe["fuels"]!.AsArray(), f => f!["name"]!.GetValue<string>() == "planks" && f["items_per_unit"]!.GetValue<double>() == 1.5);
    }

    [Fact]
    public void A_coal_only_furnace_never_loads_planks_and_a_bill_can_override_it()
    {
        var (c, h, w) = Rig(null, new Stack(15, 0, 4), new Stack(5, 0, 20), new Stack(17, 0, 8));
        Assert.Equal("coal", c.WorkbenchOp("fuel", Furnace, "coal").Result!["fuel"]!.GetValue<string>());
        var id = Bill(c, new JsonObject { ["recipe"] = "smelt:iron_ore", ["target"] = 4 });
        Assert.NotEqual("smelt", c.Next()["kind"]!.GetValue<string>());
        Assert.Equal("needs fuel: no coal within 32 blocks", c.Benches.Single().Bills.Single().SuspendedReason);
        Assert.Equal("coal", BillState(c)["fuel_need"]!["choice"]!.GetValue<string>());

        // The bill burns planks instead: the override clears the wait.
        Assert.Null(c.BillsOp("update", new JsonObject { ["id"] = id, ["fuel"] = "planks" }).Error);
        Assert.Null(c.Benches.Single().Bills.Single().SuspendedReason);
        c.Report(c.P(), c.CurrentOf(c.P())?.Id ?? "", "done", null, null);
        var j = c.Next();
        Assert.Equal("smelt", j["kind"]!.GetValue<string>());
        Assert.Equal("planks", j["fuel"]!.GetValue<string>());
        Assert.Contains(("oak_wood_planks", 3), Fetched(j)); // 4 items ÷ 1.5, fetch the selected wood variant
        Assert.Equal("planks", BillState(c)["fuel"]!.GetValue<string>());
    }

    [Fact]
    public void A_coal_furnace_with_coal_stored_brings_coal_only()
    {
        var (c, h, w) = Rig(null, new Stack(15, 0, 4), new Stack(5, 0, 20), new Stack(263, 0, 2));
        Assert.Null(c.WorkbenchOp("fuel", Furnace, "coal").Error);
        Bill(c, new JsonObject { ["recipe"] = "smelt:iron_ore", ["target"] = 4 });
        var k = c.Next();
        Assert.Equal("coal", k["fuel"]!.GetValue<string>());
        Assert.Equal([("iron_ore", 4), ("coal", 1)], Fetched(k));
    }

    [Fact]
    public void Auto_leaves_the_builds_reserved_planks_and_burns_logs()
    {
        var (c, h, w) = Rig(null, new Stack(15, 0, 2), new Stack(5, 0, 4), new Stack(17, 0, 6));
        var b = c.BuildsOp("add", new JsonArray(new JsonObject
        {
            ["kind"] = "block", ["blocks"] = new JsonArray(new JsonArray(0, 64, 9), new JsonArray(1, 64, 9), new JsonArray(2, 64, 9), new JsonArray(3, 64, 9)),
            ["material"] = "planks",
        }), null);
        Assert.Null(b.Error);
        c.EnsureMaterials();
        Bill(c, new JsonObject { ["recipe"] = "smelt:iron_ore", ["target"] = 2 });
        var j = c.Next();
        Assert.Equal("smelt", j["kind"]!.GetValue<string>());
        Assert.StartsWith("log", j["fuel"]!.GetValue<string>());
        Assert.DoesNotContain(Fetched(j), f => f.Item.Contains("plank"));
    }

    [Fact]
    public void What_the_furnace_still_holds_counts_and_a_load_tops_up_only_what_the_batch_needs()
    {
        var (c, h, w) = Rig(null, new Stack(15, 0, 8), new Stack(263, 0, 4));
        Bill(c, new JsonObject { ["recipe"] = "smelt:iron_ore", ["target"] = 2 });
        var j = c.Next();
        Assert.Equal(1, j["fuel_units"]!.GetValue<int>());
        Assert.Contains(("coal", 1), Fetched(j));
        c.Report(c.P(), j["id"]!.GetValue<string>(), "done", null, null);
        var bench = c.Benches.Single();
        Assert.Equal(6, c.FuelCreditNow(bench), 3); // one coal smelts 8: 6 left after 2 (while it burns: StoryFurnaceRefuelTests)
        Assert.Equal(6, c.State()["workbenches"]![0]!["fuel_credit"]!.GetValue<double>());

        // The next batch of 2 brings no fuel: the furnace still burns the coal.
        bench.LoadedBill = null; bench.ReadyAt = null;
        Bill(c, new JsonObject { ["recipe"] = "smelt:iron_ore", ["target"] = 2 });
        var k = c.Next(1);
        Assert.Equal("smelt", k["kind"]!.GetValue<string>());
        Assert.Equal(0, k["fuel_units"]!.GetValue<int>());
        Assert.DoesNotContain(Fetched(k), f => f.Item == "coal");
        Assert.Equal("coal", k["fuel"]!.GetValue<string>()); // still the only fuel it may load

        // Short of fuel at the furnace after all: it holds nothing the colony knew of.
        c.Report(c.P(1), k["id"]!.GetValue<string>(), "failed", "not enough fuel (0 coal)", null);
        Assert.Equal(0, c.FuelCreditNow(bench));
    }

    [Fact]
    public void The_fuel_op_takes_what_the_version_burns_and_only_at_a_furnace()
    {
        var (c, h, w) = Rig(Beta181GameData.Instance);
        Assert.StartsWith("fuel must be", c.WorkbenchOp("fuel", Furnace, "blaze_rod").Error); // b1.8.1 has no blaze rods
        Assert.Null(c.WorkbenchOp("fuel", Furnace, "lava_bucket").Error);
        var bench = c.State()["workbenches"]![0]!;
        Assert.Equal("lava_bucket", bench["fuel"]!.GetValue<string>());
        var choices = bench["fuel_choices"]!.AsArray().Select(x => x!.GetValue<string>()).ToList();
        Assert.Contains("coal_or_charcoal", choices);
        Assert.DoesNotContain("blaze_rod", choices);
        Assert.Null(c.WorkbenchOp("fuel", Furnace, "auto").Error);
        Assert.Null(c.Benches.Single().FuelSetting);

        var (m, _, mw) = Rig(Release1122GameData.Instance);
        Assert.Contains("blaze_rod", StoryFuels.ValidFor(Release1122GameData.Instance));
        mw.Set(3, 64, 0, 58);
        Assert.Null(m.WorkbenchOp("designate", new P3(3, 64, 0)).Error);
        Assert.StartsWith("only a furnace takes a fuel choice", m.WorkbenchOp("fuel", new P3(3, 64, 0), "coal").Error);
        var bad = m.BillsOp("add", new JsonObject { ["bench"] = new JsonArray(3, 64, 0), ["bill"] = new JsonObject { ["recipe"] = "craft:stick", ["fuel"] = "coal" } });
        Assert.Equal("fuel is for furnace bills (a brewing stand burns blaze powder)", bad.Error);
    }

    [Fact]
    public void A_brew_shows_its_blaze_powder()
    {
        var (c, h, w) = StoryRig.Make(pawns: 1, data: Release1122GameData.Instance);
        w.Set(3, 64, 0, 117);
        c.StorageOp("set", [new P3(3, 64, 2)], null, null, null);
        c.AddContents(c.StorageAt(new P3(3, 64, 2))!, new Stack(377, 0, 2));
        Assert.Null(c.BillsOp("add", new JsonObject { ["bench"] = new JsonArray(3, 64, 0), ["bill"] = new JsonObject { ["recipe"] = "brew:swiftness" } }).Error);
        var need = c.State()["workbenches"]![0]!["bills"]![0]!["fuel_need"]!;
        Assert.Equal("blaze_powder", need["choice"]!.GetValue<string>());
        Assert.Equal(1, need["need"]!.GetValue<int>());
        Assert.Equal(2, need["have"]!.GetValue<int>());
        Assert.False(need["short"]!.GetValue<bool>());
        Assert.Equal(20, StoryRecipes.Get(Release1122GameData.Instance, "brew:swiftness")!.Json()["fuels"]![0]!["items_per_unit"]!.GetValue<int>());
    }
}
