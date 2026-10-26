using MineClaw.Data;

namespace MineClaw.Data.Tests;

public class BlockTests
{
    [Fact]
    public void AllBlockIdsPresent()
    {
        Assert.Equal(110, Blocks.All.Count);
        for (int i = 0; i <= Blocks.MaxId; i++) Assert.True(Blocks.Exists(i), $"missing {i}");
        Assert.False(Blocks.Exists(110));
    }

    [Theory]
    [InlineData(Blocks.Stone, 1.5f)]
    [InlineData(Blocks.Cobblestone, 2f)]
    [InlineData(Blocks.Dirt, 0.5f)]
    [InlineData(Blocks.Grass, 0.6f)]
    [InlineData(Blocks.Obsidian, 10f)]
    [InlineData(Blocks.Leaves, 0.2f)]
    [InlineData(Blocks.Web, 4f)]
    [InlineData(Blocks.StoneBricks, 1.5f)]
    [InlineData(Blocks.IronBars, 5f)]
    [InlineData(Blocks.GlassPane, 0.3f)]
    [InlineData(Blocks.Melon, 1f)]
    [InlineData(Blocks.Vine, 0.2f)]
    [InlineData(Blocks.MonsterEgg, 0.75f)]
    [InlineData(Blocks.CraftingTable, 2.5f)]
    [InlineData(Blocks.Furnace, 3.5f)]
    [InlineData(Blocks.Bedrock, -1f)]
    [InlineData(Blocks.Piston, 0.5f)]
    [InlineData(Blocks.StoneBrickStairs, 1.5f)]
    public void Hardness(int id, float h) => Assert.Equal(h, Blocks.Get(id).Hardness);

    [Theory]
    [InlineData("cobblestone", 4)]
    [InlineData("Cobble", 4)]
    [InlineData("crafting table", 58)]
    [InlineData("workbench", 58)]
    [InlineData("Crafting_Table", 58)]
    [InlineData("minecraft:furnace", 61)]
    [InlineData("lit_furnace", 62)]
    [InlineData("water", 9)]
    [InlineData("flowing_water", 8)]
    [InlineData("sugar cane", 83)]
    [InlineData("stone bricks", 98)]
    [InlineData("iron bars", 101)]
    [InlineData("logs", 17)]
    public void NameLookup(string name, int id) => Assert.Equal(id, Blocks.ByName(name)?.Id);

    [Fact]
    public void Collision()
    {
        Assert.False(Blocks.Get(Blocks.Water).Solid);
        Assert.True(Blocks.Get(Blocks.Water).Liquid);
        Assert.Equal(0.5, Blocks.Get(Blocks.Slab).CollisionHeight);
        Assert.Equal(1.5, Blocks.Get(Blocks.Fence).CollisionHeight);
        Assert.True(Blocks.Get(Blocks.Ladder).Climbable);
        Assert.True(Blocks.Get(Blocks.Lava).Hazard);
        Assert.True(Blocks.Get(Blocks.Chest).Interactable);
        Assert.False(Blocks.Get(Blocks.TallGrass).Solid);
    }
}

public class ItemTests
{
    [Fact]
    public void ItemRangeMatchesB181()
    {
        for (int i = 256; i <= 368; i++) Assert.True(Items.Exists(i), $"missing {i}");
        Assert.False(Items.Exists(369));
        Assert.True(Items.Exists(Items.Record13));
        Assert.True(Items.Exists(Items.RecordCat));
        Assert.False(Items.Exists(2258));
    }

    [Theory]
    [InlineData(Items.WoodenPickaxe, 1, 59)]
    [InlineData(Items.StonePickaxe, 1, 131)]
    [InlineData(Items.IronPickaxe, 1, 250)]
    [InlineData(Items.DiamondPickaxe, 1, 1561)]
    [InlineData(Items.GoldenPickaxe, 1, 32)]
    [InlineData(Items.Shears, 1, 238)]
    [InlineData(Items.FlintAndSteel, 1, 64)]
    [InlineData(Items.Bow, 1, 0)]
    [InlineData(Items.LeatherHelmet, 1, 33)]
    [InlineData(Items.IronChestplate, 1, 192)]
    [InlineData(Items.DiamondBoots, 1, 312)]
    [InlineData(Items.GoldenHelmet, 1, 66)]
    [InlineData(Items.MushroomStew, 1, 0)]
    [InlineData(Items.Sign, 1, 0)]
    [InlineData(Items.Bucket, 1, 0)]
    [InlineData(Items.Egg, 16, 0)]
    [InlineData(Items.Snowball, 16, 0)]
    [InlineData(Items.Bread, 64, 0)]
    [InlineData(Items.Stick, 64, 0)]
    [InlineData(Blocks.Cobblestone, 64, 0)]
    public void StackAndDurability(int id, int stack, int dur)
    {
        Assert.Equal(stack, Items.Get(id).MaxStack);
        Assert.Equal(dur, Items.Get(id).MaxDamage);
    }

    [Fact]
    public void Lookups()
    {
        Assert.Equal(Items.Bed, Items.ByName("bed")?.Id);
        Assert.Equal(Items.SugarCane, Items.ByName("sugar cane")?.Id);
        Assert.Equal(Items.WoodenPickaxe, Items.ByName("Wooden Pickaxe")?.Id);
        Assert.Equal(Items.WoodenPickaxe, Items.ByName("wood_pickaxe")?.Id);
        Assert.Equal(Blocks.Cobblestone, Items.ByName("cobblestone")?.Id);
        Assert.Equal((Items.Coal, 1), Items.ByNameWithDamage("charcoal") is { } c ? (c.Item.Id, c.Damage) : default);
        Assert.Equal((Items.Dye, 15), Items.ByNameWithDamage("bone meal") is { } b ? (b.Item.Id, b.Damage) : default);
        Assert.Equal(Blocks.Torch, Items.ByName("torches")?.Id);
    }

    [Fact]
    public void DisplayNames()
    {
        Assert.Equal("Red Wool", Items.DisplayName(Blocks.Wool, 14));
        Assert.Equal("Bone Meal", Items.DisplayName(Items.Dye, 15));
        Assert.Equal("Charcoal", Items.DisplayName(Items.Coal, 1));
        Assert.Equal("Coal", Items.DisplayName(Items.Coal, 0));
        Assert.Equal("Birch Wood", Items.DisplayName(Blocks.Log, 2));
        Assert.Equal("Spruce Sapling", Items.DisplayName(Blocks.Sapling, 1));
        Assert.Equal("Cobblestone Slab", Items.DisplayName(Blocks.Slab, 3));
    }
}

public class RecipeTests
{
    [Fact]
    public void Count() => Assert.Equal(161, Recipes.All.Count);

    [Fact]
    public void Basics()
    {
        var stick = Assert.Single(Recipes.ForOutput(Items.Stick));
        Assert.Equal(4, stick.OutputCount);
        Assert.True(stick.FitsIn2x2);

        var furnace = Assert.Single(Recipes.ForOutput(Blocks.Furnace));
        Assert.True(furnace.RequiresCraftingTable);
        Assert.Equal(8, furnace.Totals.Single().Count);

        Assert.Equal(2, Recipes.ForOutput(Blocks.Torch).Count);
        Assert.Equal(6, Recipes.ForOutput(Blocks.Slab).Count);
        Assert.All(Recipes.ForOutput(Blocks.Slab), r => Assert.Equal(3, r.OutputCount));
        Assert.Equal(4, Recipes.ForOutput(Blocks.WoodenStairs).Single().OutputCount);
        Assert.Equal(2, Recipes.ForOutput(Blocks.Ladder).Single().OutputCount);
        Assert.Equal(1, Recipes.ForOutput(Items.Sign).Single().OutputCount);
        Assert.Single(Recipes.ForOutput(Blocks.Wool, 14));
        Assert.Equal(4, Recipes.ForOutput("planks").Single().OutputCount);
        Assert.Empty(Recipes.ForOutput(Items.PumpkinSeeds));
    }

    [Fact]
    public void LayoutAndMatch()
    {
        var pick = Recipes.ForOutput(Items.WoodenPickaxe).Single();
        var cells = pick.LayoutFor(3)!;
        Assert.Equal(Blocks.Planks, cells[0]!.Value.Id);
        Assert.Equal(Items.Stick, cells[4]!.Value.Id);
        Assert.Null(cells[3]);
        Assert.Null(pick.LayoutFor(2));

        // mirrored axe placed in bottom-right of a 3x3 grid
        var P = new ItemStack(Blocks.Planks, 1, 0); var S = new ItemStack(Items.Stick, 1, 0); var E = ItemStack.Empty;
        var grid = new[] { P, P, E, S, P, E, S, E, E };
        Assert.Equal(Items.WoodenAxe, Recipes.Match(grid, 3)?.OutputId);
        var torch = new[] { new ItemStack(Items.Coal, 1, 1), E, S, E };
        Assert.Equal(Blocks.Torch, Recipes.Match(torch, 2)?.OutputId);
    }
}

public class PlannerTests
{
    private static ItemStack St(int id, int n, int dmg = 0) => new((short)id, (byte)n, (short)dmg);

    [Fact]
    public void WoodenPickaxeFromLogs()
    {
        var plan = RecipePlanner.Plan(Items.WoodenPickaxe, 1, [St(Blocks.Log, 2)]);
        Assert.True(plan.Complete, plan.ToString());
        Assert.Equal(Items.WoodenPickaxe, plan.Steps[^1].OutputId);
        Assert.Equal(Blocks.Planks, plan.Steps[0].OutputId);
        Assert.Contains(plan.Steps, s => s.OutputId == Items.Stick);
    }

    [Fact]
    public void WoodenPickaxeWithoutTableNeedsThreeLogs()
    {
        var opts = new PlannerOptions { HasCraftingTable = false };
        var plan = RecipePlanner.Plan(Items.WoodenPickaxe, 1, [St(Blocks.Log, 3)], options: opts);
        Assert.True(plan.Complete, plan.ToString());
        Assert.Contains(plan.Steps, s => s.OutputId == Blocks.CraftingTable);

        var plan2 = RecipePlanner.Plan(Items.WoodenPickaxe, 1, [St(Blocks.Log, 2)], options: opts);
        Assert.False(plan2.Complete);
        Assert.Equal(Blocks.Log, plan2.Missing.Single().Id);
    }

    [Fact]
    public void FurnaceFromCobblestone()
    {
        var ok = RecipePlanner.Plan(Blocks.Furnace, 1, [St(Blocks.Cobblestone, 8)]);
        Assert.True(ok.Complete);
        Assert.Single(ok.Steps);
        Assert.True(ok.NeedsCraftingTable);

        var missing = RecipePlanner.Plan(Blocks.Furnace, 1, [St(Blocks.Cobblestone, 5)]);
        Assert.Equal(new MaterialNeed(Blocks.Cobblestone, -1, 3), missing.Missing.Single());
    }

    [Fact]
    public void AlreadyHave()
    {
        var plan = RecipePlanner.Plan(Blocks.Torch, 4, [St(Blocks.Torch, 10)]);
        Assert.True(plan.Complete);
        Assert.Empty(plan.Steps);
    }

    [Fact]
    public void SmeltingStepsAndCharcoalTorches()
    {
        var plan = RecipePlanner.Plan(Items.IronPickaxe, 1, [St(Blocks.IronOre, 3), St(Blocks.Log, 1)]);
        Assert.True(plan.Complete, plan.ToString());
        Assert.Equal(3, plan.ItemsToSmelt);

        var torches = RecipePlanner.Plan(Blocks.Torch, 4, [St(Blocks.Log, 2)]);
        Assert.True(torches.Complete, torches.ToString());
        Assert.Contains(torches.Steps, s => s.IsSmelt && s.OutputId == Items.Coal && s.OutputDamage == 1);

        var withCoal = RecipePlanner.Plan(Blocks.Torch, 4, [St(Items.Coal, 1), St(Items.Stick, 1)]);
        Assert.True(withCoal.Complete);
        Assert.Single(withCoal.Steps);
    }

    [Fact]
    public void StoneButtonNeedsSmeltedStone()
    {
        var plan = RecipePlanner.Plan(Blocks.StoneButton, 1, [St(Blocks.Cobblestone, 2)]);
        Assert.True(plan.Complete, plan.ToString());
        Assert.Equal(2, plan.ItemsToSmelt);
    }

    [Fact]
    public void ColouredWoolUsesWhiteWool()
    {
        var plan = RecipePlanner.Plan("red wool", 2, [St(Blocks.Wool, 2), St(Blocks.Rose, 1)])!;
        Assert.True(plan.Complete, plan.ToString());
        Assert.Equal(2, plan.Steps.Count); // rose -> 2 rose red, then 2x (dye + white wool)
    }

    [Fact]
    public void ByName()
    {
        var plan = RecipePlanner.Plan("stone pickaxe", 1, [St(Blocks.Cobblestone, 3), St(Items.Stick, 2)]);
        Assert.NotNull(plan);
        Assert.True(plan!.Complete);
    }
}

public class ToolTests
{
    [Theory]
    [InlineData(Blocks.Stone, -1, 150)]          // 7.5 s by hand
    [InlineData(Blocks.Stone, Items.WoodenPickaxe, 23)]
    [InlineData(Blocks.Stone, Items.StonePickaxe, 12)]
    [InlineData(Blocks.Stone, Items.IronPickaxe, 8)]
    [InlineData(Blocks.Stone, Items.DiamondPickaxe, 6)]
    [InlineData(Blocks.Stone, Items.GoldenPickaxe, 4)]
    [InlineData(Blocks.Cobblestone, Items.WoodenPickaxe, 30)]
    [InlineData(Blocks.Log, -1, 60)]
    [InlineData(Blocks.Log, Items.WoodenAxe, 30)]
    [InlineData(Blocks.Log, Items.StoneAxe, 15)]
    [InlineData(Blocks.CraftingTable, Items.DiamondAxe, 75)] // axe does not speed up crafting tables in beta
    [InlineData(Blocks.Obsidian, Items.DiamondPickaxe, 300)] // 15 s: obsidian is not on the pickaxe list
    [InlineData(Blocks.Obsidian, Items.IronPickaxe, 1000)]
    [InlineData(Blocks.Web, Items.IronSword, 8)]
    [InlineData(Blocks.Web, -1, 400)]
    [InlineData(Blocks.Leaves, -1, 6)]
    [InlineData(Blocks.Leaves, Items.Shears, 0)]
    [InlineData(Blocks.TallGrass, -1, 0)]
    public void DigTicks(int block, int held, int expected)
    {
        int t = Tools.DigTicks(block, held);
        Assert.InRange(t, expected, expected + 1); // float accumulation may add one tick at exact boundaries
    }

    [Fact]
    public void Penalties()
    {
        Assert.InRange(Tools.DigTicks(Blocks.Dirt, -1), 15, 16);
        Assert.InRange(Tools.DigTicks(Blocks.Dirt, -1, onGround: false), 75, 76);
        Assert.InRange(Tools.DigTicks(Blocks.Dirt, -1, onGround: false, inWater: true), 375, 376);
        Assert.Equal(int.MaxValue, Tools.DigTicks(Blocks.Bedrock, Items.DiamondPickaxe));
        Assert.Equal(int.MaxValue, Tools.DigTicks(Blocks.Water, -1));
    }

    [Fact]
    public void Harvest()
    {
        Assert.False(Tools.CanHarvest(Blocks.Stone, -1));
        Assert.True(Tools.CanHarvest(Blocks.Stone, Items.WoodenPickaxe));
        Assert.False(Tools.CanHarvest(Blocks.IronOre, Items.WoodenPickaxe));
        Assert.True(Tools.CanHarvest(Blocks.IronOre, Items.StonePickaxe));
        Assert.False(Tools.CanHarvest(Blocks.DiamondOre, Items.GoldenPickaxe));
        Assert.True(Tools.CanHarvest(Blocks.Obsidian, Items.DiamondPickaxe));
        Assert.False(Tools.CanHarvest(Blocks.Obsidian, Items.IronPickaxe));
        Assert.True(Tools.CanHarvest(Blocks.Dirt, -1));
        Assert.False(Tools.CanHarvest(Blocks.SnowBlock, -1));
        Assert.True(Tools.CanHarvest(Blocks.SnowBlock, Items.WoodenShovel));
        Assert.False(Tools.CanHarvest(Blocks.Glowstone, -1));
    }

    [Fact]
    public void HarvestTableConsistentWithBlockInfo()
    {
        foreach (var b in Blocks.All.Where(b => b.Id != Blocks.Bedrock))
            Assert.True(Tools.RequiresTool(b.Id) == b.MinHarvestLevel >= 0, $"{b.Name}");
    }

    [Fact]
    public void BestTool()
    {
        ItemStack[] inv = [new(Items.WoodenPickaxe, 1, 0), new(Items.StonePickaxe, 1, 5), new(Items.StoneAxe, 1, 0), new(Items.IronSword, 1, 0), new(Blocks.Dirt, 10, 0)];
        Assert.Equal(Items.StonePickaxe, (int?)Tools.BestToolFor(Blocks.Stone, inv)?.Id);
        Assert.Equal(Items.StoneAxe, (int?)Tools.BestToolFor(Blocks.Log, inv)?.Id);
        Assert.Null(Tools.BestToolFor(Blocks.Dirt, inv));
        Assert.Equal(Items.IronSword, (int?)Tools.BestToolFor(Blocks.Web, inv)?.Id);
        Assert.Equal(1, Tools.BestToolIndex(Blocks.Stone, inv));
    }
}

public class SmeltingTests
{
    [Fact]
    public void Table()
    {
        Assert.Equal(12, Smelting.All.Count);
        Assert.Equal(Blocks.Stone, Smelting.ResultFor(Blocks.Cobblestone)?.OutputId);
        Assert.Equal(Blocks.Glass, Smelting.ResultFor(Blocks.Sand)?.OutputId);
        var charcoal = Smelting.ResultFor(Blocks.Log)!;
        Assert.Equal((Items.Coal, 1), (charcoal.OutputId, charcoal.OutputDamage));
        Assert.Null(Smelting.ResultFor(Blocks.CoalOre));
        Assert.Single(Smelting.ForOutput(Items.Dye, 2));
    }

    [Fact]
    public void Fuel()
    {
        Assert.Equal(1600, Smelting.FuelTicks(Items.Coal));
        Assert.Equal(300, Smelting.FuelTicks(Blocks.Planks));
        Assert.Equal(300, Smelting.FuelTicks(Blocks.Log));
        Assert.Equal(100, Smelting.FuelTicks(Items.Stick));
        Assert.Equal(100, Smelting.FuelTicks(Blocks.Sapling));
        Assert.Equal(20000, Smelting.FuelTicks(Items.LavaBucket));
        Assert.Equal(0, Smelting.FuelTicks(Items.WoodenPickaxe));
        Assert.Equal(1, Smelting.FuelNeeded(8, Items.Coal));
        Assert.Equal(2, Smelting.FuelNeeded(9, Items.Coal));
        Assert.Equal(2, Smelting.FuelNeeded(3, Blocks.Planks));
        Assert.Equal(8.0, Smelting.ItemsPerFuel(Items.Coal));
    }
}

public class DropTests
{
    [Fact]
    public void Typical()
    {
        Assert.Empty(Drops.For(Blocks.Stone, 0, -1));
        Assert.Equal(Blocks.Cobblestone, Drops.For(Blocks.Stone, 0, Items.WoodenPickaxe).Single().ItemId);
        Assert.Equal(Blocks.Dirt, Drops.For(Blocks.Grass).Single().ItemId);
        Assert.Empty(Drops.For(Blocks.Glass));
        Assert.Empty(Drops.For(Blocks.Ice));
        var leaves = Drops.For(Blocks.Leaves, 2).Single();
        Assert.Equal((Blocks.Sapling, 2, 0.05f), (leaves.ItemId, leaves.Damage, leaves.Chance));
        Assert.Equal(Blocks.Leaves, Drops.For(Blocks.Leaves, 0, Items.Shears).Single().ItemId);
        Assert.Equal(Items.Seeds, Drops.For(Blocks.TallGrass, 1).Single().ItemId);
        Assert.Equal(Items.Coal, Drops.For(Blocks.CoalOre, 0, Items.WoodenPickaxe).Single().ItemId);
        Assert.Empty(Drops.For(Blocks.IronOre, 0, Items.WoodenPickaxe));
        Assert.Equal(Blocks.IronOre, Drops.For(Blocks.IronOre, 0, Items.StonePickaxe).Single().ItemId);
        Assert.Equal(2, Drops.For(Blocks.Gravel).Count);
        Assert.Equal(Items.String, Drops.For(Blocks.Web, 0, Items.WoodenSword).Single().ItemId);
        Assert.Equal(new Drop(Items.MelonSlice, 0, 3, 7), Drops.For(Blocks.Melon).Single());
        Assert.Contains(Blocks.Stone, Drops.SourcesOf(Blocks.Cobblestone));
    }
}
