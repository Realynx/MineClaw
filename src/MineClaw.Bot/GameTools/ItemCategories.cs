using MineClaw.Data;

namespace MineClaw.Bot.GameTools;

/// <summary>Coarse item categories for chest organizing (order = sort order inside a chest).</summary>
public static class ItemCategories
{
    public static readonly string[] Order = ["tools", "ores", "wood", "blocks", "farm", "food", "mob", "misc"];

    private static readonly HashSet<int> Ores =
    [
        Items.Coal, Items.Diamond, Items.IronIngot, Items.GoldIngot, Items.Redstone, Items.GlowstoneDust, Items.Flint, Items.ClayBall, Items.Brick,
        Blocks.CoalOre, Blocks.IronOre, Blocks.GoldOre, Blocks.DiamondOre, Blocks.LapisOre, Blocks.RedstoneOre,
        Blocks.IronBlock, Blocks.GoldBlock, Blocks.DiamondBlock, Blocks.LapisBlock, Blocks.Obsidian,
    ];

    private static readonly HashSet<int> Wood = [Blocks.Log, Blocks.Planks, Blocks.Sapling, Blocks.Leaves, Items.Stick, Items.Bowl];

    private static readonly HashSet<int> Farm =
    [
        Items.Seeds, Items.Wheat, Items.SugarCane, Items.PumpkinSeeds, Items.MelonSeeds, Items.Egg, Items.Sugar,
        Blocks.Pumpkin, Blocks.Melon, Blocks.Cactus, Blocks.RedMushroom, Blocks.BrownMushroom, Blocks.SugarCane,
    ];

    private static readonly HashSet<int> Mob =
        [Items.String, Items.Feather, Items.Gunpowder, Items.Leather, Items.Bone, Items.Slimeball, Items.RottenFlesh, Items.EnderPearl, Items.Arrow];

    private static readonly HashSet<int> ToolLike =
        [Items.Bow, Items.FishingRod, Items.Shears, Items.FlintAndSteel, Items.Bucket, Items.WaterBucket, Items.LavaBucket, Items.MilkBucket, Items.Compass, Items.Clock];

    public static string Of(int id, int damage = 0)
    {
        if (id == Items.Dye) return DyeCategory(damage);
        if (Mob.Contains(id)) return "mob";
        if (IsToolLike(id)) return "tools";
        if (Ores.Contains(id)) return "ores";
        if (Wood.Contains(id)) return "wood";
        if (Farm.Contains(id)) return "farm";
        if (Items.Food(id) != null) return "food";
        if (id < 256) return "blocks";
        return "misc";
    }

    /// <summary>Lapis is ore, bone meal is farm, other dyes misc.</summary>
    private static string DyeCategory(int damage) => damage == Items.LapisLazuli ? "ores" : damage == Items.BoneMeal ? "farm" : "misc";

    /// <summary>Tools, armour and tool-like items (bow, rod, shears, buckets, compass, clock).</summary>
    private static bool IsToolLike(int id) =>
        ToolLike.Contains(id) || (id >= Items.LeatherHelmet && id <= Items.GoldenBoots) || (Items.Exists(id) && Items.IsTool(id));

    public static int Rank(string category) => Array.IndexOf(Order, category) is var i and >= 0 ? i : Order.Length;
}
