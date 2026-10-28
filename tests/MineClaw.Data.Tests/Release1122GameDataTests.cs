using MineClaw.Data.Versioning;

namespace MineClaw.Data.Tests;

/// <summary>
/// Multi-version P2: the 1.12.2 names and ids (minecraft-data pc/1.12.2) and the 1.12.2 dig rule. Expected dig ticks are
/// hand-computed from the decompiled source in float arithmetic, as the client accumulates them
/// (<c>PlayerControllerMP.onPlayerDamageBlock</c>): per tick <c>getDigSpeed / hardness / (canHarvest ? 30 : 100)</c> with
/// <c>EntityPlayer.getDigSpeed</c> = the stack's <c>getDestroySpeed</c>, + efficiency² + 1 when &gt; 1, × (1 + 0.2 × haste
/// level), / 5 in water, / 5 off the ground.
/// </summary>
public class Release1122GameDataTests
{
    private static readonly Release1122GameData D = Release1122GameData.Instance;

    private static ItemStack Held(int id) => new((short)id, 1, 0);

    [Fact]
    public void Names_and_ids_come_from_minecraft_data()
    {
        Assert.Equal("1.12.2", D.VersionId);
        Assert.True(D.HalvesFollowCursor);
        Assert.False(((IGameData)Beta181GameData.Instance).HalvesFollowCursor);
        Assert.Equal("crafting_table", D.Block(Blocks.Workbench).Name);
        Assert.True(D.BlockExists(251)); // concrete: past b1.8.1's range
        Assert.Equal("concrete", D.Block(251).Name);
        Assert.Equal("stained_glass", D.Block(95).Name); // 95 was b1.8.1's locked chest
        Assert.Equal("Shield", D.Item(442).DisplayName);
        Assert.Equal(1, D.Item(442).MaxStack);
        Assert.Equal(336, D.Item(442).MaxDamage);
        Assert.Equal(16, D.Item(Items.Sign).MaxStack); // b1.8.1: 1
        Assert.Equal(16, D.Item(Items.Bucket).MaxStack);
        Assert.Equal(ToolType.Pickaxe, D.Item(Items.DiamondPickaxe).Tool);
        Assert.Equal(3, D.Item(Items.DiamondPickaxe).ToolTier);

        // Variants by name, with their damage.
        Assert.Equal((5, 2), Pick(D.ItemByNameWithDamage("birch wood planks")));
        Assert.Equal((263, 1), Pick(D.ItemByNameWithDamage("charcoal")));
        Assert.Equal((Blocks.Workbench, -1), Pick(D.ItemByNameWithDamage("crafting_table")));
        Assert.Equal((Blocks.Workbench, -1), Pick(D.ItemByNameWithDamage("workbench"))); // the b1.8.1 alias still resolves
        Assert.Equal((Items.WoodenPickaxe, -1), Pick(D.ItemByNameWithDamage("wooden pickaxes")));
        Assert.Equal(251, D.BlockByName("concrete")!.Id);
        Assert.Equal("Birch Wood Planks", D.DisplayName(5, 2));
        Assert.Equal("Charcoal", D.DisplayName(263, 1));
        Assert.Contains("1561", D.DisplayName(Items.DiamondPickaxe, 10)); // durability shown
    }

    private static (int, int) Pick((ItemInfo Item, int Damage)? r) => r is { } x ? (x.Item.Id, x.Damage) : (-1, -99);

    [Theory]
    // block, held, onGround, inWater, haste level, efficiency, expected ticks
    [InlineData(Blocks.Stone, -1, true, false, 0, 0, 151)]             // hardness 1.5, rock needs a pickaxe: 1/1.5/100
    [InlineData(Blocks.Stone, Items.WoodenPickaxe, true, false, 0, 0, 23)] // 2/1.5/30
    [InlineData(Blocks.Stone, Items.DiamondPickaxe, true, false, 0, 5, 2)] // (8 + 5² + 1)/1.5/30
    [InlineData(Blocks.Stone, Items.WoodenPickaxe, true, false, 2, 0, 17)] // haste II: 2 × 1.4
    [InlineData(Blocks.Log, -1, true, false, 0, 0, 61)]                 // 1/2/30
    [InlineData(Blocks.Log, Items.StoneAxe, true, false, 0, 0, 15)]     // 4/2/30
    [InlineData(Blocks.IronOre, Items.WoodenPickaxe, true, false, 0, 0, 151)] // can't harvest, but 1.12.2 keeps the tool speed: 2/3/100
    [InlineData(Blocks.Dirt, -1, false, true, 0, 0, 376)]               // 1 / 5 (water) / 5 (air) / 0.5 / 30
    [InlineData(Blocks.Leaves, Items.Shears, true, false, 0, 0, 0)]     // 15/0.2/30 ≥ 1: on the first hit
    [InlineData(Blocks.Leaves, Items.IronSword, true, false, 0, 0, 4)]  // leaves material: 1.5
    [InlineData(Blocks.Leaves, -1, true, false, 0, 0, 6)]
    [InlineData(Blocks.Obsidian, Items.DiamondPickaxe, true, false, 0, 0, 188)] // 1.12.2 hardness 50
    public void Dig_ticks_match_the_source(int block, int held, bool onGround, bool inWater, int haste, int efficiency, int expected)
    {
        var stack = held < 0 ? ItemStack.Empty : Held(held);
        if (efficiency > 0) stack = stack with { Tag = new ItemTag([10, 0, 0, 0], new Dictionary<int, int> { [Release1122Dig.EfficiencyEnchantment] = efficiency }) };
        Assert.Equal(expected, D.DigTicks(new DigInput(block, 0, stack, onGround, inWater, Haste: haste)));
    }

    [Fact]
    public void Harvest_rules_and_the_b181_difference()
    {
        Assert.False(D.CanHarvest(Blocks.IronOre, Items.WoodenPickaxe));
        Assert.True(D.CanHarvest(Blocks.IronOre, Items.StonePickaxe));
        Assert.False(D.CanHarvest(Blocks.Obsidian, Items.IronPickaxe));
        Assert.True(D.CanHarvest(Blocks.Log, -1));
        Assert.True(D.CanHarvest(Blocks.Web, Items.Shears));
        // b1.8.1 ignores the tool when it can't harvest (1/hardness/100): iron ore with a wooden pickaxe takes twice as long.
        Assert.Equal(301, Tools.DigTicks(Blocks.IronOre, Items.WoodenPickaxe));
        Assert.Equal(151, D.DigTicks(Blocks.IronOre, Items.WoodenPickaxe));
        // The b1.8.1 profile's extended entry point is its old rule (the extras are ignored).
        Assert.Equal(Tools.DigTicks(Blocks.Stone, Items.WoodenPickaxe), ((IGameData)Beta181GameData.Instance).DigTicks(new DigInput(Blocks.Stone, 0, Held(Items.WoodenPickaxe), Haste: 2)));
        Assert.Equal(int.MaxValue, D.DigTicks(Blocks.Bedrock, Items.DiamondPickaxe));
        Assert.Equal(int.MaxValue, D.DigTicks(Blocks.Water, -1));
    }
}
