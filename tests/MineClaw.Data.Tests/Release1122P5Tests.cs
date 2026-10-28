using System.IO.Compression;
using System.Text.Json;
using MineClaw.Data.Versioning;

namespace MineClaw.Data.Tests;

/// <summary>
/// Multi-version P5: the 1.12.2 recipes (minecraft-data = the jar's recipe JSON, variant keys as wildcards), FurnaceRecipes and
/// getItemBurnTime, the drop overrides, ItemFood values and tool tiers, and the static facades over <see cref="GameData.Current"/>.
/// Expected values are read off the decompiled 1.12.2 client (FurnaceRecipes, TileEntityFurnace, Block*.getItemDropped /
/// quantityDropped / damageDropped, Item.registerItems).
/// </summary>
public class Release1122P5Tests
{
    private static readonly Release1122GameData D = Release1122GameData.Instance;

    private static int Id(string name) => D.IdOf(name) ?? throw new ArgumentException(name);
    private static ItemStack S(string name, int dmg = 0) => new((short)Id(name), 1, (short)dmg);
    private static readonly ItemStack E = ItemStack.Empty;

    private static Recipe? Match(int size, params ItemStack[] grid) => D.MatchRecipe(grid, size);

    // ------------------------------------------------------------------ recipes

    [Fact]
    public void Crafting_table_from_any_planks_even_mixed()
    {
        for (int wood = 0; wood < 6; wood++)
        {
            var p = S("planks", wood);
            var r = Match(2, p, p, p, p);
            Assert.NotNull(r);
            Assert.Equal(Blocks.CraftingTable, r!.OutputId);
        }
        Assert.Equal(Blocks.CraftingTable, Match(2, S("planks", 0), S("planks", 2), S("planks", 4), S("planks", 5))!.OutputId);
        var table = Assert.Single(D.RecipesFor(Blocks.CraftingTable));
        Assert.True(table.FitsIn2x2);
        Assert.Equal(new Ingredient(Blocks.Planks, -1), table.Ingredients[0]); // the jar's planks 0..5 list = any planks
        // In the table's 3x3 grid, anywhere.
        Assert.Equal(Blocks.CraftingTable, Match(3, E, E, E, E, S("planks", 1), S("planks", 1), E, S("planks", 3), S("planks", 3))!.OutputId);
    }

    [Fact]
    public void Planks_keep_the_logs_wood_type()
    {
        // P2's mispredict: a birch log made oak planks in the b1.8.1 table, then the server resynced.
        var birch = Match(2, S("log", 2), E, E, E)!;
        Assert.Equal((Blocks.Planks, 2, 4), (birch.OutputId, birch.OutputDamage, birch.OutputCount));
        var acacia = Match(2, E, E, E, S("log2", 0))!;
        Assert.Equal((Blocks.Planks, 4), (acacia.OutputId, acacia.OutputDamage));
        var darkOak = Match(2, S("log2", 1), E, E, E)!;
        Assert.Equal(5, darkOak.OutputDamage);
        Assert.Equal(6, D.RecipesFor(Blocks.Planks).Count);
        Assert.Equal(0, D.RecipesFor(Blocks.Planks)[0].OutputDamage); // oak first: a planner's tie picks oak
        Assert.Equal(Items.Stick, Match(2, S("planks", 2), E, S("planks", 5), E)!.OutputId); // sticks from mixed planks
    }

    [Fact]
    public void Stone_pickaxe_needs_the_table_and_cobblestone()
    {
        var c = S("cobblestone");
        var st = S("stick");
        var r = Match(3, c, c, c, E, st, E, E, st, E);
        Assert.Equal(Items.StonePickaxe, r!.OutputId);
        Assert.True(r.RequiresCraftingTable);
        Assert.False(Assert.Single(D.RecipesFor(Items.StonePickaxe)).FitsIn2x2);
        Assert.Null(Match(3, c, c, S("stone"), E, st, E, E, st, E)); // 1.12.2: cobblestone only
        Assert.Null(Match(2, c, c, st, E));
    }

    [Fact]
    public void Bed_takes_one_wool_colour_and_any_planks()
    {
        var w = S("wool", 0);
        var white = Match(3, w, w, w, S("planks", 0), S("planks", 3), S("planks", 5), E, E, E);
        Assert.Equal((Id("bed"), 0), (white!.OutputId, white.OutputDamage));
        var r = S("wool", 14);
        var red = Match(3, E, E, E, r, r, r, S("planks", 1), S("planks", 1), S("planks", 2));
        Assert.Equal((Id("bed"), 14), (red!.OutputId, red.OutputDamage));
        Assert.Null(Match(3, w, r, w, S("planks", 0), S("planks", 0), S("planks", 0), E, E, E)); // mixed wool isn't a bed
        var beds = D.RecipesFor(Id("bed"));
        Assert.Equal(16, beds.Count(b => b.Shaped));
        Assert.Equal(15, beds.Count(b => !b.Shaped)); // dye a white bed
        Assert.True(beds.First(b => b.Shaped).RequiresCraftingTable);
    }

    [Fact]
    public void Furnace_and_torch_and_fire_charge()
    {
        var c = S("cobblestone");
        var f = Match(3, c, c, c, c, E, c, c, c, c)!;
        Assert.Equal(Blocks.Furnace, f.OutputId);
        Assert.Equal(8, f.Totals.Single().Count);
        // Torches from coal or charcoal (the jar's coal 0/1 list).
        Assert.Equal((Blocks.Torch, 4), (Match(2, S("coal", 1), E, S("stick"), E)!.OutputId, Match(2, S("coal", 0), E, S("stick"), E)!.OutputCount));
        Assert.Equal(Id("fire_charge"), Match(3, S("gunpowder"), S("blaze_powder"), S("coal", 1), E, E, E, E, E, E)!.OutputId);
    }

    [Fact]
    public void Unpacks_are_reverses_of_storage_recipes()
    {
        Assert.True(D.IsUnpack(D.RecipesFor(Items.IronIngot).Single(r => r.OutputCount == 9)));
        Assert.False(D.IsUnpack(D.RecipesFor(Items.IronIngot).Single(r => r.OutputCount == 1))); // 9 nuggets → 1 ingot
        Assert.True(D.IsUnpack(Assert.Single(D.RecipesFor(Id("iron_nugget")))));
        Assert.True(D.IsUnpack(D.RecipesFor(Id("slime_ball")).Single()));
        Assert.False(D.IsUnpack(D.RecipesFor(Blocks.Planks)[0]));
        Assert.Equal(432, D.AllRecipes.Count);
    }

    // ------------------------------------------------------------------ smelting and fuel

    [Fact]
    public void Smelting_follows_FurnaceRecipes()
    {
        Assert.Equal(Items.IronIngot, D.SmeltingFor(Blocks.IronOre)!.OutputId);
        Assert.Equal(0.7f, D.SmeltingFor(Blocks.IronOre)!.Experience);
        Assert.Equal((Items.Coal, 1), (D.SmeltingFor(Id("log2"), 1)!.OutputId, D.SmeltingFor(Id("log2"), 1)!.OutputDamage)); // charcoal
        Assert.Equal((Id("stonebrick"), 2), (D.SmeltingFor(Id("stonebrick"), 0)!.OutputId, D.SmeltingFor(Id("stonebrick"), 0)!.OutputDamage));
        Assert.Null(D.SmeltingFor(Id("stonebrick"), 1)); // only plain stone bricks crack
        Assert.Equal((Id("sponge"), 0), (D.SmeltingFor(Id("sponge"), 1)!.OutputId, D.SmeltingFor(Id("sponge"), 1)!.OutputDamage));
        Assert.Null(D.SmeltingFor(Id("sponge"), 0));
        Assert.Equal(1, D.SmeltingFor(Id("fish"), 1)!.OutputDamage); // salmon
        Assert.Null(D.SmeltingFor(Id("fish"), 3)); // pufferfish can't cook
        Assert.Equal(Id("iron_nugget"), D.SmeltingFor(Items.IronPickaxe)!.OutputId);
        Assert.Equal(Id("cooked_mutton"), D.SmeltingFor(Id("mutton"))!.OutputId);
        Assert.Equal(Id("hardened_clay"), D.SmeltingFor(Id("clay"))!.OutputId);
        Assert.Null(D.SmeltingFor(Blocks.Planks));
        Assert.Equal(200, D.CookTicks);
    }

    [Theory]
    [InlineData("coal", 1600)] [InlineData("coal_block", 16000)] [InlineData("lava_bucket", 20000)] [InlineData("blaze_rod", 2400)]
    [InlineData("planks", 300)] [InlineData("log2", 300)] [InlineData("crafting_table", 300)] [InlineData("chest", 300)]
    [InlineData("oak_stairs", 300)] [InlineData("fence_gate", 300)] [InlineData("banner", 300)] [InlineData("wooden_slab", 150)]
    [InlineData("wool", 100)] [InlineData("carpet", 67)] [InlineData("ladder", 300)] [InlineData("wooden_button", 100)]
    [InlineData("wooden_pickaxe", 200)] [InlineData("wooden_sword", 200)] [InlineData("wooden_hoe", 200)] [InlineData("stick", 100)]
    [InlineData("bow", 300)] [InlineData("fishing_rod", 300)] [InlineData("sign", 200)] [InlineData("sapling", 100)] [InlineData("bowl", 100)]
    [InlineData("birch_door", 200)] [InlineData("wooden_door", 200)] [InlineData("iron_door", 0)] [InlineData("acacia_boat", 400)]
    [InlineData("boat", 400)] [InlineData("iron_ingot", 0)] [InlineData("stone_pickaxe", 0)] [InlineData("cobblestone", 0)]
    public void Fuel_burn_times_follow_getItemBurnTime(string item, int ticks) => Assert.Equal(ticks, D.FuelTicks(Id(item)));

    [Fact]
    public void Fuel_agrees_with_the_knowledge_packs_smelting_facts()
    {
        var path = Path.Combine(RepoRoot(), "shards", "sources", "1.12.2", "smelting.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        int n = 0;
        foreach (var f in doc.RootElement.GetProperty("fuels").EnumerateArray())
        {
            if (f.GetProperty("item").ValueKind != JsonValueKind.String) continue;
            Assert.Equal(f.GetProperty("ticks").GetInt32(), D.FuelTicks(Id(f.GetProperty("item").GetString()!)));
            n++;
        }
        Assert.True(n > 10);
        Assert.Equal(doc.RootElement.GetProperty("smelting").GetArrayLength(), D.AllSmelting.Count);
    }

    // ------------------------------------------------------------------ drops

    [Fact]
    public void Drops_follow_the_block_overrides()
    {
        int pick = Items.IronPickaxe, stonePick = Items.StonePickaxe;
        Assert.Equal(Blocks.Cobblestone, Assert.Single(D.DropsFor(Blocks.Stone, 0, pick)).ItemId);
        Assert.Equal((Blocks.Stone, 1), Pair(Assert.Single(D.DropsFor(Blocks.Stone, 1, pick)))); // granite drops granite
        Assert.Empty(D.DropsFor(Blocks.Stone, 0, -1)); // rock needs a pickaxe
        Assert.Equal((Blocks.Dirt, 0), Pair(Assert.Single(D.DropsFor(Blocks.Grass))));
        Assert.Equal((Blocks.Dirt, 0), Pair(Assert.Single(D.DropsFor(Blocks.Dirt, 2)))); // podzol
        Assert.Equal(Items.Diamond, Assert.Single(D.DropsFor(Blocks.DiamondOre, 0, pick)).ItemId);
        Assert.Empty(D.DropsFor(Blocks.DiamondOre, 0, stonePick));
        Assert.Equal((Items.Dye, 4, 4, 8), Quad(Assert.Single(D.DropsFor(Blocks.LapisOre, 0, stonePick))));
        Assert.Equal((Items.Book, 0, 3, 3), Quad(Assert.Single(D.DropsFor(Blocks.Bookshelf))));
        var leaves = D.DropsFor(Blocks.Leaves, 0);
        Assert.Contains(leaves, d => d.ItemId == Blocks.Sapling && d.Damage == 0 && Math.Abs(d.Chance - 0.05f) < 1e-6);
        Assert.Contains(leaves, d => d.ItemId == Items.Apple && Math.Abs(d.Chance - 0.005f) < 1e-6);
        Assert.Equal((Blocks.Sapling, 5), Pair(D.DropsFor(Id("leaves2"), 1)[0])); // dark oak sapling
        Assert.Equal((Blocks.Leaves, 2), Pair(Assert.Single(D.DropsFor(Blocks.Leaves, 2 | 4, Items.Shears))));
        Assert.Equal((Id("log2"), 1), Pair(Assert.Single(D.DropsFor(Id("log2"), 1 | 8))));
        Assert.Equal((Blocks.Slab, 3, 2, 2), Quad(Assert.Single(D.DropsFor(Blocks.DoubleSlab, 3, pick))));
        Assert.Equal(Id("oak_stairs"), Assert.Single(D.DropsFor(Id("oak_stairs"), 2)).ItemId); // 1.12.2 stairs drop themselves
        const int spruceDoorBlock = 193; // the item is 427
        Assert.Equal(Id("spruce_door"), Assert.Single(D.DropsFor(spruceDoorBlock, 1)).ItemId);
        Assert.Empty(D.DropsFor(spruceDoorBlock, 8)); // the upper half
        Assert.Equal(Items.WoodenDoor, Assert.Single(D.DropsFor(Blocks.WoodenDoor, 3)).ItemId);
        Assert.Equal(Id("reeds"), Assert.Single(D.DropsFor(Blocks.SugarCane)).ItemId);
        Assert.Equal((Id("snowball"), 0, 4, 4), Quad(Assert.Single(D.DropsFor(Blocks.SnowLayer, 2, Items.WoodenShovel))));
        Assert.Empty(D.DropsFor(Blocks.SnowLayer, 2, -1));
        Assert.Empty(D.DropsFor(Blocks.Glass));
        Assert.Equal(Id("brown_mushroom"), Assert.Single(D.DropsFor(Blocks.BrownMushroomBlock)).ItemId); // minecraft-data says red
        var wheat = D.DropsFor(Blocks.Crops, 7);
        Assert.Contains(wheat, d => d.ItemId == Items.Wheat && d.Min == 1);
        Assert.Contains(wheat, d => d.ItemId == Items.Seeds && d.Max == 3);
        Assert.Equal(Items.Seeds, Assert.Single(D.DropsFor(Blocks.Crops, 3)).ItemId);
        Assert.Equal((Items.String, 0), Pair(Assert.Single(D.DropsFor(Blocks.Web, 0, Items.IronSword))));
        Assert.Empty(D.DropsFor(Blocks.Web, 0, -1));
    }

    private static (int, int) Pair(Drop d) => (d.ItemId, d.Damage);
    private static (int, int, int, int) Quad(Drop d) => (d.ItemId, d.Damage, d.Min, d.Max);

    // ------------------------------------------------------------------ food and tools

    [Fact]
    public void Food_values_are_ItemFoods()
    {
        Assert.Equal((8, 0.8f), D.Food(Id("cooked_beef"))!.Value);
        Assert.Equal((6, 0.6f), D.Food(Items.MushroomStew)!.Value); // ItemSoup(6); b1.8.1 had 8
        Assert.Equal((4, 1.2f), D.Food(Items.GoldenApple)!.Value);
        Assert.Equal((6, 0.8f), D.Food(Id("cooked_fish"), 1)!.Value); // salmon
        Assert.Equal((5, 0.6f), D.Food(Id("cooked_fish"), 0)!.Value);
        Assert.Equal((2, 0.3f), D.Food(Id("melon"))!.Value);
        Assert.Equal((1, 0.6f), D.Food(Id("beetroot"))!.Value);
        Assert.Null(D.Food(Id("melon_block")));
        Assert.Null(D.Food(Items.Bone));
        Assert.Equal(1f, D.FoodPoisonChance(Id("fish"), 3));
        Assert.Equal(0f, D.FoodPoisonChance(Id("fish"), 0));
        Assert.Equal(0.3f, D.FoodPoisonChance(Id("chicken")));
        Assert.Equal(0.6f, D.FoodPoisonChance(Id("poisonous_potato")));
    }

    [Fact]
    public void Tool_tiers_gate_harvesting()
    {
        Assert.True(D.CanHarvest(Blocks.Obsidian, Items.DiamondPickaxe));
        Assert.False(D.CanHarvest(Blocks.Obsidian, Items.IronPickaxe));
        Assert.True(D.CanHarvest(Blocks.IronOre, Items.StonePickaxe));
        Assert.False(D.CanHarvest(Blocks.IronOre, Items.WoodenPickaxe));
        Assert.False(D.CanHarvest(Blocks.IronOre, Items.GoldenPickaxe)); // gold harvests like wood
        Assert.True(D.CanHarvest(Blocks.DiamondOre, Items.IronPickaxe));
        Assert.True(D.CanHarvest(Blocks.Log, -1));
        Assert.Equal((ToolType.Pickaxe, 4), (D.Item(Items.GoldenPickaxe).Tool, D.Item(Items.GoldenPickaxe).ToolTier));
        Assert.Equal(0, Tools.HarvestLevel(D.Item(Items.GoldenPickaxe).ToolTier));
        Assert.Equal(3, D.Block(Blocks.Obsidian).MinHarvestLevel);
        Assert.Equal(2, D.Block(Blocks.GoldOre).MinHarvestLevel);
        Assert.Equal(1, D.Block(Blocks.IronOre).MinHarvestLevel);
    }

    // ------------------------------------------------------------------ facades

    [Fact]
    public void Static_tables_follow_the_current_version()
    {
        Assert.False(Items.Exists(162));
        Assert.Single(Recipes.ForOutput(Blocks.Planks)); // b1.8.1: any log → oak planks
        using (GameData.Override(D))
        {
            Assert.Same(D, GameData.Current);
            Assert.Equal("log2", Items.Get(162).Name);
            Assert.Equal(6, Recipes.ForOutput(Blocks.Planks).Count);
            Assert.Equal((Blocks.Planks, 2), (Recipes.Match([S("log", 2), E, E, E], 2)!.OutputId, Recipes.Match([S("log", 2), E, E, E], 2)!.OutputDamage));
            Assert.Equal(300, Smelting.FuelTicks(Blocks.CraftingTable));
            Assert.Equal(Items.Coal, Smelting.ResultFor(162)!.OutputId);
            Assert.Equal(3, Drops.For(Blocks.Bookshelf).Single().Min);
            Assert.Contains(Blocks.Stone, Drops.SourcesOf(Blocks.Cobblestone));
            Assert.Equal((6, 0.6f), Items.Food(Items.MushroomStew)!.Value);
            Assert.Equal("Birch Wood Planks", Items.DisplayName(Blocks.Planks, 2));
            Assert.Equal((Blocks.Planks, 2), Items.ByNameWithDamage(Items.VariantName(Blocks.Planks, 2)) is { } v ? (v.Item.Id, v.Damage) : default);
            Assert.False(Tools.CanHarvest(Blocks.IronOre, Items.GoldenPickaxe));
            // The scope flows into tasks and threads.
            Assert.Equal("log2", Task.Run(() => Items.Get(162).Name).Result);
        }
        Assert.Same(Beta181GameData.Instance, GameData.Current);
        Assert.Equal((8, 0.6f), Items.Food(Items.MushroomStew)!.Value);
    }

    [Fact]
    public void The_planner_crafts_an_iron_pickaxe_tree_on_1122()
    {
        using var _ = GameData.Override(D);
        // Birch logs, iron ore, coal: planks (birch) → sticks; iron ore smelted ×3; the table recipe.
        var inv = new[] { new ItemStack((short)Blocks.Log, 2, 2), new ItemStack((short)Blocks.IronOre, 3, 0), new ItemStack((short)Items.Coal, 1, 0) };
        var plan = RecipePlanner.Plan(Items.IronPickaxe, 1, inv);
        Assert.True(plan.Complete, plan.ToString());
        Assert.Contains(plan.Steps, s => s.IsSmelt && s.Smelt!.OutputId == Items.IronIngot && s.Times == 3);
        Assert.Contains(plan.Steps, s => s.Recipe is { OutputId: Blocks.Planks, OutputDamage: 2 });
        Assert.Contains(plan.Used, u => u.Id == Blocks.Log && u.Damage == 2);
        Assert.True(plan.NeedsCraftingTable);
    }

    // ------------------------------------------------------------------ the client jar (skipped without it)

    /// <summary>Every recipe JSON in the installed 1.12.2 jar, laid out with its first and last alternatives, matches a loaded
    /// recipe with the jar's result (skipped when the jar isn't installed, e.g. CI).</summary>
    [Fact]
    public void Jar_recipes_match_the_loaded_table()
    {
        var jar = JarPath();
        if (jar is null) return;
        using var zip = ZipFile.OpenRead(jar);
        int n = 0;
        foreach (var e in zip.Entries.Where(e => e.FullName.StartsWith("assets/minecraft/recipes/", StringComparison.Ordinal) && e.Name.EndsWith(".json")))
        {
            using var s = e.Open();
            using var doc = JsonDocument.Parse(s);
            var root = doc.RootElement;
            var res = root.GetProperty("result");
            var want = (Id(res.GetProperty("item").GetString()![10..]), res.TryGetProperty("data", out var rd) ? rd.GetInt32() : 0,
                res.TryGetProperty("count", out var rc) ? rc.GetInt32() : 1);
            foreach (bool last in new[] { false, true })
            {
                ItemStack Pick(JsonElement k)
                {
                    var alt = k.ValueKind == JsonValueKind.Array ? (last ? k[k.GetArrayLength() - 1] : k[0]) : k;
                    return new((short)Id(alt.GetProperty("item").GetString()![10..]), 1, (short)(alt.TryGetProperty("data", out var d) ? d.GetInt32() : 0));
                }
                var grid = Enumerable.Repeat(ItemStack.Empty, 9).ToArray();
                if (root.GetProperty("type").GetString() == "crafting_shaped")
                {
                    var rows = root.GetProperty("pattern").EnumerateArray().Select(r => r.GetString()!).ToArray();
                    for (int y = 0; y < rows.Length; y++)
                        for (int x = 0; x < rows[y].Length; x++)
                            if (rows[y][x] != ' ') grid[y * 3 + x] = Pick(root.GetProperty("key").GetProperty(rows[y][x].ToString()));
                }
                else
                {
                    int i = 0;
                    foreach (var ing in root.GetProperty("ingredients").EnumerateArray()) grid[i++] = Pick(ing);
                }
                var got = D.MatchRecipe(grid, 3);
                Assert.True(got is not null, $"{e.Name}: no recipe matched");
                Assert.Equal(want, (got!.OutputId, got.OutputDamage, got.OutputCount));
            }
            n++;
        }
        Assert.Equal(432, n);
    }

    internal static string? JarPath()
    {
        var env = Environment.GetEnvironmentVariable("MINECLAW_1122_JAR");
        if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var p = Path.Combine(appData, "PrismLauncher", "libraries", "com", "mojang", "minecraft", "1.12.2", "minecraft-1.12.2-client.jar");
        return File.Exists(p) ? p : null;
    }

    internal static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "MineClaw.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
