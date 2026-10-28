namespace MineClaw.Data.Tests;

/// <summary>Item phrases → (id, meta) as b1.8.1 recipes (Recipes.cs, from the jar's CraftingManager) define them, and the
/// variant carried through recipe lookup, planning and the inventory count.</summary>
public class VariantTests
{
    private static ItemStack St(int id, int n, int dmg = 0) => new((short)id, (byte)n, (short)dmg);

    private static (int Id, int Damage) R(string name) =>
        Items.ByNameWithDamage(name) is { } r ? (r.Item.Id, r.Damage) : (-1, -1);

    [Theory]
    [InlineData("cobble slabs", Blocks.Slab, 3)]
    [InlineData("cobblestone slab", Blocks.Slab, 3)]
    [InlineData("cobblestone_slab", Blocks.Slab, 3)]
    [InlineData("20 cobble slabs", Blocks.Slab, 3)]
    [InlineData("sandstone slab", Blocks.Slab, 1)]
    [InlineData("wooden slabs", Blocks.Slab, 2)]
    [InlineData("wood slab", Blocks.Slab, 2)]
    [InlineData("plank slab", Blocks.Slab, 2)]
    [InlineData("stone slab", Blocks.Slab, 0)]
    [InlineData("smooth stone slab", Blocks.Slab, 0)]
    [InlineData("brick slab", Blocks.Slab, 4)]
    [InlineData("stone brick slab", Blocks.Slab, 5)]
    [InlineData("red wool", Blocks.Wool, 14)]
    [InlineData("white wool", Blocks.Wool, 0)]
    [InlineData("light gray wool", Blocks.Wool, 8)]
    [InlineData("birch log", Blocks.Log, 2)]
    [InlineData("spruce log", Blocks.Log, 1)]
    [InlineData("pine log", Blocks.Log, 1)]
    [InlineData("oak log", Blocks.Log, 0)]
    [InlineData("bone meal", Items.Dye, 15)]
    [InlineData("lapis", Items.Dye, 4)]
    [InlineData("lapis lazuli", Items.Dye, 4)]
    [InlineData("ink sac", Items.Dye, 0)]
    [InlineData("cocoa beans", Items.Dye, 3)]
    [InlineData("rose red", Items.Dye, 1)]
    [InlineData("dandelion yellow", Items.Dye, 11)]
    [InlineData("cactus green", Items.Dye, 2)]
    public void Phrases_resolve_to_their_variant(string phrase, int id, int meta) => Assert.Equal((id, meta), R(phrase));

    [Fact]
    public void Planks_have_one_type_in_b181()
    {
        // Any log → 4 planks (meta 0): the jar has a single plank recipe on a wildcard log.
        var planks = Recipes.ForOutput(Blocks.Planks);
        Assert.Single(planks);
        Assert.Equal(0, planks[0].OutputDamage);
        Assert.Equal(-1, planks[0].Ingredients[0].Damage);
        Assert.Equal(Blocks.Planks, R("birch planks").Id);
        Assert.Equal(Blocks.Planks, R("oak planks").Id);
    }

    [Theory]
    [InlineData(3, Blocks.Cobblestone)]
    [InlineData(0, Blocks.Stone)]
    [InlineData(1, Blocks.Sandstone)]
    [InlineData(2, Blocks.Planks)]
    [InlineData(4, Blocks.Bricks)]
    [InlineData(5, Blocks.StoneBricks)]
    public void Slab_recipes_match_the_jar(int meta, int input)
    {
        var r = Assert.Single(Recipes.ForOutput(Blocks.Slab, meta));
        Assert.Equal(3, r.OutputCount);
        Assert.All(r.Ingredients, i => Assert.Equal(input, i.Id));
        Assert.Equal(3, r.Ingredients.Count);
    }

    [Fact]
    public void Twenty_cobble_slabs_plan_from_cobblestone_without_smelting()
    {
        var (count, name) = Items.ParseAmount("20 cobble slabs");
        Assert.Equal(20, count);
        var plan = RecipePlanner.Plan(name, count!.Value, [St(Blocks.Cobblestone, 64)], new PlannerOptions { AllowSmelting = false });
        Assert.NotNull(plan);
        Assert.True(plan!.Complete, plan.ToString());
        Assert.Equal(3, plan.TargetDamage);
        var step = Assert.Single(plan.Steps);
        Assert.Equal(7, step.Times); // 21 cobble → 21 slabs
        Assert.Equal(3, step.OutputDamage);
        Assert.False(plan.NeedsFurnace);
    }

    [Fact]
    public void Stone_slab_needs_smooth_stone()
    {
        var plan = RecipePlanner.Plan("stone slab", 3, [St(Blocks.Cobblestone, 3)], new PlannerOptions { AllowSmelting = false })!;
        Assert.False(plan.Complete);
        Assert.Contains(plan.Missing, m => m.Id == Blocks.Stone);
        var smelt = RecipePlanner.Plan("stone slab", 3, [St(Blocks.Cobblestone, 3)])!;
        Assert.True(smelt.Complete && smelt.NeedsFurnace, smelt.ToString());
    }

    [Fact]
    public void Have_count_is_per_variant()
    {
        // Holding 5 stone slabs doesn't count as having cobble slabs.
        var plan = RecipePlanner.Plan("cobble slab", 3, [St(Blocks.Slab, 5, 0), St(Blocks.Cobblestone, 3)], new PlannerOptions { AllowSmelting = false })!;
        Assert.True(plan.Complete);
        Assert.Single(plan.Steps);
        var have = RecipePlanner.Plan("cobble slab", 3, [St(Blocks.Slab, 5, 3)])!;
        Assert.Empty(have.Steps); // already has them
    }

    [Fact]
    public void Red_wool_plans_rose_red_from_a_rose()
    {
        var plan = RecipePlanner.Plan("red wool", 1, [St(Blocks.Wool, 1, 0), St(Blocks.Rose, 1)])!;
        Assert.True(plan.Complete, plan.ToString());
        Assert.Equal(14, plan.Steps[^1].OutputDamage);
        Assert.Contains(plan.Steps, s => s.OutputId == Items.Dye && s.OutputDamage == 1);
    }

    [Fact]
    public void Bone_meal_and_lapis_are_dye_variants_with_their_own_sources()
    {
        Assert.Contains(Recipes.ForOutput("bone meal"), r => r.Ingredients[0].Id == Items.Bone);
        Assert.Equal((Items.Dye, 4), (Drops.Primary(Blocks.LapisOre, 0, Items.StonePickaxe)!.Value.ItemId, Drops.Primary(Blocks.LapisOre, 0, Items.StonePickaxe)!.Value.Damage));
    }

    [Theory]
    [InlineData("20 cobble slabs", 20, "cobble slabs")]
    [InlineData("torch x16", 16, "torch")]
    [InlineData("16x torch", 16, "torch")]
    [InlineData("a bucket", 1, "bucket")]
    [InlineData("cobble slabs", null, "cobble slabs")]
    public void Amounts_split_off(string text, int? count, string name)
    {
        var (n, rest) = Items.ParseAmount(text);
        Assert.Equal(count, n);
        Assert.Equal(name, rest);
    }

    [Theory]
    [InlineData(Blocks.Slab, 3, "cobblestone_slab")]
    [InlineData(Blocks.Slab, 0, "stone_slab")]
    [InlineData(Blocks.Slab, 1, "sandstone_slab")]
    [InlineData(Blocks.Slab, 2, "wooden_slab")]
    [InlineData(Blocks.Wool, 14, "red_wool")]
    [InlineData(Blocks.Log, 2, "birch_wood")]
    [InlineData(Blocks.Sapling, 1, "spruce_sapling")]
    [InlineData(Items.Dye, 15, "bone_meal")]
    public void Variant_names_round_trip(int id, int meta, string name)
    {
        Assert.Equal(name, Items.VariantName(id, meta));
        Assert.Equal((id, meta), R(name));
    }

    [Theory]
    [InlineData(Blocks.Slab, 3, "44:3, 3 cobblestone → 3")]
    [InlineData(Blocks.Slab, 0, "44:0, 3 stone (smelted cobblestone) → 3")]
    [InlineData(Blocks.Slab, 2, "44:2, 3 planks → 3")]
    [InlineData(Blocks.Wool, 14, "35:14, 1 rose red + 1 white wool → 1")]
    public void Variant_tags_name_the_kind_and_its_recipe_yield(int id, int meta, string tag) =>
        Assert.Equal(tag, Recipes.VariantTag(id, meta));
}

/// <summary>Pins the core-knowledge food line to the jar data: no block drops apples in b1.8.1 (leaves: saplings only).</summary>
public class FoodSourceTests
{
    [Fact]
    public void No_block_drops_apples_leaves_drop_only_saplings()
    {
        Assert.Empty(Drops.SourcesOf(Items.Apple));
        Assert.Empty(Drops.SourcesOf(Items.GoldenApple));
        for (int meta = 0; meta < 3; meta++)
            Assert.All(Drops.For(Blocks.Leaves, meta), d => Assert.Equal(Blocks.Sapling, d.ItemId));
    }

    [Fact]
    public void Cooked_foods_come_from_the_furnace()
    {
        foreach (var (raw, cooked) in new[] { (Items.Porkchop, Items.CookedPorkchop), (Items.RawBeef, Items.Steak), (Items.RawChicken, Items.CookedChicken), (Items.RawFish, Items.CookedFish) })
            Assert.Equal(cooked, Smelting.ResultFor(raw)?.OutputId);
        Assert.NotEmpty(Recipes.ForOutput(Items.Bread));
        Assert.NotEmpty(Recipes.ForOutput(Items.MushroomStew));
        Assert.NotEmpty(Recipes.ForOutput(Items.Cake));
        Assert.NotNull(Items.Food(Items.MelonSlice));
        Assert.NotEmpty(Drops.SourcesOf(Items.MelonSlice));
    }
}
