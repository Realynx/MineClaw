using System.Text.Json.Nodes;
using MineClaw.Data.Versioning;
using MineClaw.Hive.Story;

namespace MineClaw.Hive.Tests;

public sealed class StoryConstructionWoodTests
{
    private static readonly P3 Chest = new(0, 64, 9), Table = new(3, 64, 0), Furnace = new(6, 64, 0);

    private static (StoryColony C, FakeStoryHost H) Rig(params Stack[] stock)
    {
        var (c, h, w) = StoryRig.Make(pawns: 1, data: Release1122GameData.Instance);
        foreach (var type in WorkTypes.All) c.Pawns.Values.Single().Priorities[type] = type == WorkTypes.Craft ? 1 : 0;
        c.StorageOp("set", [Chest], null, null, null);
        foreach (var stack in stock) c.AddContents(c.StorageAt(Chest)!, stack);
        w.Set(Table.X, Table.Y, Table.Z, 58);
        w.Set(Furnace.X, Furnace.Y, Furnace.Z, 61);
        Assert.Null(c.WorkbenchOp("designate", Table).Error);
        Assert.Null(c.WorkbenchOp("designate", Furnace).Error);
        return (c, h);
    }

    private static BuildState Build(StoryColony c, int id, int meta, int count, int z)
    {
        var cells = new JsonArray(Enumerable.Range(0, count).Select(i => (JsonNode)new JsonArray(i % 16, 64, z + i / 16)).ToArray());
        var added = c.BuildsOp("add", new JsonArray(new JsonObject
        {
            ["kind"] = "block", ["blocks"] = cells, ["material"] = new JsonArray(id, meta),
        }), null);
        Assert.Null(added.Error);
        var bid = added.Result!["ids"]![0]!.GetValue<string>();
        return c.Builds.Single(b => b.Id == bid);
    }

    [Fact]
    public void Earlier_stone_bricks_and_oversized_generic_chests_leave_spruce_floor_and_stair_ingredients()
    {
        var (c, _) = Rig(new Stack(4, 0, 64), new Stack(17, 1, 20), new Stack(17, 2, 8));
        Build(c, 98, 0, 64, 40);
        var floor = Build(c, 5, 1, 16, 60);
        Build(c, 54, 0, 64, 80);
        var stairs = Build(c, 134, 0, 24, 100);
        c.EnsureMaterials();
        Assert.Equal(16, floor.Materials.Single().Crafting);
        Assert.Equal(24, stairs.Materials.Single().Crafting);
        Assert.Equal(0, stairs.Materials.Single().Short);
        Assert.Contains(c.Benches.Single(b => b.Pos == Table).Tasks, b => b.ForBuild == stairs.Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Generic_auto_planks_fetch_birch_and_leave_the_stairs_spruce_in_chests_or_pockets(bool carried)
    {
        var (c, h) = Rig(new Stack(17, 2, 3));
        if (carried) h.Lives[c.P()] = h.Live(c.P()) with { Inventory = [new Stack(17, 1, 30)] };
        else c.AddContents(c.StorageAt(Chest)!, new Stack(17, 1, 30));
        var chests = Build(c, 54, 0, 16, 40);
        var stairs = Build(c, 134, 0, 80, 60);
        c.EnsureMaterials();
        Assert.Equal(80, stairs.Materials.Single().Crafting);
        var job = c.Next();
        Assert.Equal("craft", job["kind"]!.GetValue<string>());
        if (carried)
        {
            // The primitive can choose any carried log for a wildcard recipe; this pawn does its exact stairs instead.
            Assert.Equal("craft:spruce_wood_planks", job["recipe"]!.GetValue<string>());
            return;
        }
        Assert.Equal("craft:planks_any", job["recipe"]!.GetValue<string>());
        var fetched = job["fetch"]!.AsArray();
        Assert.NotEmpty(fetched);
        Assert.All(fetched, f => Assert.Equal("birch_wood", f!["item"]!.GetValue<string>()));
        Assert.Contains(c.Benches.Single(b => b.Pos == Table).Tasks, b => b.ForBuild == chests.Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Auto_fuel_never_burns_exact_construction_holds_but_named_logs_still_override(bool carried)
    {
        var (c, h) = Rig();
        if (carried) h.Lives[c.P()] = h.Live(c.P()) with { Inventory = [new Stack(17, 1, 3)] };
        else c.AddContents(c.StorageAt(Chest)!, new Stack(17, 1, 3));
        Build(c, 134, 0, 8, 40);
        c.EnsureMaterials();
        var furnace = c.Benches.Single(b => b.Pos == Furnace);
        var bill = new BillState { ForBuild = "other", Recipe = "smelt:iron_ore" };
        Assert.NotNull(c.PickFuel(furnace, bill, 2, h.Live(c.P())).Missing);
        bill.Fuel = "logs";
        Assert.Null(c.PickFuel(furnace, bill, 2, h.Live(c.P())).Missing);
    }

    [Fact]
    public void Auto_fuel_does_not_fetch_birch_when_the_id_only_loader_could_burn_carried_spruce()
    {
        var (c, h) = Rig(new Stack(17, 2, 8));
        h.Lives[c.P()] = h.Live(c.P()) with { Inventory = [new Stack(17, 1, 3)] };
        Build(c, 134, 0, 8, 40);
        c.EnsureMaterials();
        var furnace = c.Benches.Single(b => b.Pos == Furnace);
        var bill = new BillState { ForBuild = "other", Recipe = "smelt:iron_ore" };
        Assert.NotNull(c.PickFuel(furnace, bill, 2, h.Live(c.P())).Missing);
        c.AddContents(c.StorageAt(Chest)!, new Stack(263, 0, 1));
        Assert.Equal(263, c.PickFuel(furnace, bill, 2, h.Live(c.P())).Fuel!.Item.Id);
    }

    [Fact]
    public void Exact_construction_holds_keep_queue_order_when_the_same_variant_is_insufficient()
    {
        var (c, _) = Rig(new Stack(17, 1, 3));
        var floor = Build(c, 5, 1, 12, 40);
        var stairs = Build(c, 134, 0, 8, 60);
        c.EnsureMaterials();
        Assert.Equal(12, floor.Materials.Single().Crafting);
        Assert.Equal(0, stairs.Materials.Single().Crafting);
    }

    [Theory]
    [InlineData("auto", 0, 16)]
    [InlineData("logs", 8, 0)]
    public void Named_fuel_can_use_hidden_stock_without_making_phantom_stair_ingredients(string fuel, int glassCount, int stairCount)
    {
        var (c, _) = Rig(new Stack(12, 0, 8), new Stack(17, 1, 6));
        Assert.Null(c.WorkbenchOp("fuel", Furnace, fuel).Error);
        var glass = Build(c, 20, 0, 8, 40);
        var stairs = Build(c, 134, 0, 16, 60);
        c.EnsureMaterials();
        Assert.Equal(glassCount, glass.Materials.Single().Crafting);
        Assert.Equal(stairCount, stairs.Materials.Single().Crafting);
    }
}
