using System.Text.Json.Nodes;
using MineClaw.Data.Versioning;
using MineClaw.Hive.Story;

namespace MineClaw.Hive.Tests;

public sealed class StoryRecipeToolItemTests
{
    [Theory]
    [InlineData("1.12.2", 5, "craft:planks", "oak_wood_planks")]
    [InlineData("1.12.2", 35, "craft:wool", "white_wool")]
    [InlineData("b1.8.1", 44, "craft:stone_slab", "stone_slab")]
    public void Exact_zero_metadata_recipes_keep_their_saved_id_and_send_the_variant_to_the_tool(string version, int id, string recipeId, string toolItem)
    {
        IGameData data = version == "1.12.2" ? Release1122GameData.Instance : Beta181GameData.Instance;
        var recipe = StoryRecipes.Get(data, recipeId);
        Assert.NotNull(recipe);
        Assert.Equal((id, 0), (recipe.Output.Id, recipe.Output.Meta));
        Assert.Equal(toolItem, recipe.ToolItem);
        Assert.Equal((id, 0), data.ItemByNameWithDamage(recipe.ToolItem) is { } item ? (item.Item.Id, item.Damage) : (-1, -1));
    }

    [Fact]
    public void A_colliding_stone_brick_alias_uses_an_exact_selector_without_renaming_the_saved_recipe()
    {
        var data = Release1122GameData.Instance;
        Assert.Equal(-1, data.ItemByNameWithDamage("stone_brick")!.Value.Damage);
        var recipe = StoryRecipes.Get(data, "craft:stonebrick")!;
        Assert.Equal((98, 0), (recipe.Output.Id, recipe.Output.Meta));
        Assert.Equal("98:0", recipe.ToolItem);
    }

    [Fact]
    public void A_planks_from_any_logs_recipe_still_allows_every_wood()
    {
        var data = Release1122GameData.Instance;
        var recipe = StoryRecipes.Get(data, "craft:planks_any")!;
        Assert.True(recipe.AnyOutput);
        Assert.Equal("planks", recipe.ToolItem);
        Assert.Equal((5, -1), data.ItemByNameWithDamage(recipe.ToolItem) is { } item ? (item.Item.Id, item.Damage) : (-1, -1));
    }

    [Fact]
    public void A_stone_brick_build_dispatches_the_exact_craft_instead_of_an_ambiguous_family()
    {
        var data = Release1122GameData.Instance;
        var (colony, _, world) = StoryRig.Make(pawns: 1, data: data);
        var pawn = colony.Pawns.Values.Single();
        foreach (var type in WorkTypes.All) pawn.Priorities[type] = type == WorkTypes.Craft ? 1 : 0;
        var chest = new P3(0, 64, 9);
        var table = new P3(3, 64, 0);
        colony.StorageOp("set", [chest], null, null, null);
        colony.AddContents(colony.StorageAt(chest)!, new Stack(1, 0, 4));
        world.Set(table.X, table.Y, table.Z, 58);
        Assert.Null(colony.WorkbenchOp("designate", table).Error);
        var added = colony.BuildsOp("add", new JsonArray(new JsonObject
        {
            ["kind"] = "block", ["blocks"] = new JsonArray(new JsonArray(0, 64, 5)), ["material"] = new JsonArray(98, 0),
        }), null);
        Assert.Null(added.Error);

        var job = colony.Next();
        Assert.Equal("craft", job["kind"]!.GetValue<string>());
        Assert.Equal("craft:stonebrick", job["recipe"]!.GetValue<string>());
        Assert.Equal("98:0", job["item"]!.GetValue<string>());
        Assert.Equal(4, job["n"]!.GetValue<int>());
        Assert.Equal(4, job["fetch"]![0]!["n"]!.GetValue<int>());
    }

    [Fact]
    public void Spruce_stairs_keep_their_spruce_plank_intermediate_when_birch_logs_are_more_plentiful()
    {
        var (colony, _, world) = StoryRig.Make(pawns: 1, data: Release1122GameData.Instance);
        var pawn = colony.Pawns.Values.Single();
        foreach (var type in WorkTypes.All) pawn.Priorities[type] = type == WorkTypes.Craft ? 1 : 0;
        var chest = new P3(0, 64, 9);
        var table = new P3(3, 64, 0);
        colony.StorageOp("set", [chest], null, null, null);
        colony.AddContents(colony.StorageAt(chest)!, new Stack(17, 1, 2));
        colony.AddContents(colony.StorageAt(chest)!, new Stack(17, 2, 64));
        world.Set(table.X, table.Y, table.Z, 58);
        Assert.Null(colony.WorkbenchOp("designate", table).Error);
        Assert.Null(colony.BuildsOp("add", new JsonArray(new JsonObject
        {
            ["kind"] = "block", ["blocks"] = new JsonArray(new JsonArray(0, 64, 5)), ["material"] = new JsonArray(134, 0),
        }), null).Error);

        var planks = colony.Next();
        Assert.Equal("craft", planks["kind"]!.GetValue<string>());
        Assert.Equal("craft:spruce_wood_planks", planks["recipe"]!.GetValue<string>());
        Assert.Equal("spruce_wood_planks", planks["item"]!.GetValue<string>());
        Assert.Equal("spruce_wood", planks["fetch"]![0]!["item"]!.GetValue<string>());
        Assert.Equal(2, planks["fetch"]![0]!["n"]!.GetValue<int>());
        Assert.True(colony.Report(colony.P(), planks["id"]!.GetValue<string>(), "done", null, null));

        var stairs = colony.Next();
        Assert.Equal("craft", stairs["kind"]!.GetValue<string>());
        Assert.Equal("spruce_stairs", stairs["item"]!.GetValue<string>());
        Assert.Equal("spruce_wood_planks", stairs["fetch"]![0]!["item"]!.GetValue<string>());
        Assert.Equal(6, stairs["fetch"]![0]!["n"]!.GetValue<int>());
        Assert.Equal(64, colony.StorageAt(chest)!.Contents.Where(s => s.Id == 17 && s.Meta == 2).Sum(s => s.Count));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Craft_and_fuel_fetches_name_selected_oak_exactly_when_the_same_chest_holds_protected_spruce(bool smelt)
    {
        var data = Release1122GameData.Instance;
        var (colony, host, world) = StoryRig.Make(pawns: 1, data: data);
        foreach (var type in WorkTypes.All) colony.Pawns.Values.Single().Priorities[type] = type == WorkTypes.Craft ? 1 : 0;
        var chest = new P3(0, 64, 9);
        colony.StorageOp("set", [chest], null, null, null);
        foreach (var stack in new[] { new Stack(17, 1, 30), new Stack(17, 0, 3), new Stack(12, 0, 4) })
            colony.AddContents(colony.StorageAt(chest)!, stack);
        foreach (var (at, id) in new[] { (new P3(3, 64, 0), 58), (new P3(6, 64, 0), 61) })
        {
            world.Set(at.X, at.Y, at.Z, id);
            Assert.Null(colony.WorkbenchOp("designate", at).Error);
        }
        void Build(int id, int count, int z)
        {
            Assert.Null(colony.BuildsOp("add", new JsonArray(new JsonObject
            {
                ["kind"] = "block", ["material"] = new JsonArray(id, 0),
                ["blocks"] = new JsonArray(Enumerable.Range(0, count).Select(i => (JsonNode)new JsonArray(i % 16, 64, z + i / 16)).ToArray()),
            }), null).Error);
        }
        Build(smelt ? 20 : 54, smelt ? 4 : 16, 40);
        Build(134, 80, 60); // all thirty spruce logs belong to these stairs
        if (smelt) host.Lives[colony.P()] = FakeStoryHost.Live(6, 64, 0);

        var job = colony.Next();
        Assert.Equal(smelt ? "smelt" : "craft", job["kind"]!.GetValue<string>());
        Assert.Equal(smelt ? "smelt:sand" : "craft:planks_any", job["recipe"]!.GetValue<string>());
        var wood = job["fetch"]!.AsArray().Where(f => data.ItemByName(f!["item"]!.GetValue<string>())?.Id == 17).ToList();
        Assert.NotEmpty(wood);
        Assert.All(wood, fetch =>
        {
            var item = data.ItemByNameWithDamage(fetch!["item"]!.GetValue<string>())!.Value;
            Assert.Equal((17, 0), (item.Item.Id, item.Damage));
        });
        Assert.All(colony.CurrentOf(colony.P())!.Takes.Where(t => t.Stack.Id == 17), take => Assert.Equal(0, take.Stack.Meta));
    }
}
