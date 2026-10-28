namespace MineClaw.Data;

/// <summary>
/// One possible drop: between <see cref="Min"/> and <see cref="Max"/> of the item, rolled with probability <see cref="Chance"/>.
/// For multi-roll drops (crop seeds) Min/Max give the range and Chance is 1.
/// </summary>
public readonly record struct Drop(int ItemId, int Damage, int Min, int Max, float Chance = 1f)
{
    /// <summary>Expected number of items.</summary>
    public double Expected => (Min + Max) / 2.0 * Chance;
    public override string ToString() =>
        $"{(Min == Max ? Min.ToString() : $"{Min}-{Max}")} {Items.DisplayName(ItemId, Damage)}{(Chance < 1f ? $" ({Chance:P0})" : "")}";
}

/// <summary>Block drops in Beta 1.8.1 (idDropped / quantityDropped / damageDropped / harvestBlock overrides in the jar).</summary>
public static class Drops
{
    /// <summary>
    /// Typical drops when a player breaks the block holding <paramref name="heldItemId"/> (-1/0 = hand).
    /// Empty when nothing drops — including when the held item cannot harvest it (e.g. stone by hand).
    /// </summary>
    public static IReadOnlyList<Drop> For(int blockId, int meta = 0, int heldItemId = -1) =>
        Versioning.GameData.IsBeta(out var d) ? ForB(blockId, meta, heldItemId) : d.DropsFor(blockId, meta, heldItemId);

    internal static IReadOnlyList<Drop> ForB(int blockId, int meta = 0, int heldItemId = -1)
    {
        if (!Tools.CanHarvestB(blockId, heldItemId)) return [];
        bool shears = heldItemId == Items.Shears;
        Drop One(int id, int dmg = 0) => new(id, dmg, 1, 1);
        Drop[] Self(int dmg = 0) => [One(blockId, dmg)];

        switch (blockId)
        {
            case Blocks.Air: case Blocks.Bedrock: case Blocks.FlowingWater: case Blocks.Water:
            case Blocks.FlowingLava: case Blocks.Lava: case Blocks.PistonHead: case Blocks.PistonMoving:
            case Blocks.Fire: case Blocks.Portal: case Blocks.Glass: case Blocks.Bookshelf: case Blocks.MobSpawner:
            case Blocks.DeadBush: case Blocks.Cake: case Blocks.Ice: case Blocks.MonsterEgg: case Blocks.LockedChest:
                return []; // ice turns into a water source if the block below is solid or liquid; silverfish stone spawns a silverfish

            case Blocks.Stone: return [One(Blocks.Cobblestone)];
            case Blocks.Grass: case Blocks.Farmland: return [One(Blocks.Dirt)];
            case Blocks.Gravel: return [new(Blocks.Gravel, 0, 1, 1, 0.9f), new(Items.Flint, 0, 1, 1, 0.1f)];
            case Blocks.CoalOre: return [One(Items.Coal)];
            case Blocks.DiamondOre: return [One(Items.Diamond)];
            case Blocks.LapisOre: return [new(Items.Dye, 4, 4, 8)];
            case Blocks.RedstoneOre: case Blocks.LitRedstoneOre: return [new(Items.Redstone, 0, 4, 5)];
            case Blocks.Glowstone: return [new(Items.GlowstoneDust, 0, 2, 4)];
            case Blocks.Sapling: case Blocks.Log: return Self(meta & 3);
            case Blocks.Leaves:
                return shears ? [One(Blocks.Leaves, meta & 3)] : [new(Blocks.Sapling, meta & 3, 1, 1, 1f / 20f)];
            case Blocks.TallGrass:
                return shears ? [One(Blocks.TallGrass, meta)] : [new(Items.Seeds, 0, 1, 1, 1f / 8f)];
            case Blocks.Vine: return shears ? [One(Blocks.Vine)] : [];
            case Blocks.Web: return [One(Items.String)];
            case Blocks.Wool: return Self(meta);
            case Blocks.DoubleSlab: return [new(Blocks.Slab, meta, 2, 2)];
            case Blocks.Slab: return Self(meta);
            case Blocks.StoneBricks: return Self(meta);
            case Blocks.Bed: return (meta & 8) == 0 ? [One(Items.Bed)] : [];   // foot half drops the bed
            case Blocks.WoodenStairs: return [One(Blocks.Planks)];               // stairs drop their model block's drop
            case Blocks.CobblestoneStairs: return [One(Blocks.Cobblestone)];
            case Blocks.BrickStairs: return [One(Blocks.Bricks)];
            case Blocks.StoneBrickStairs: return [One(Blocks.StoneBricks)];
            case Blocks.RedstoneWire: return [One(Items.Redstone)];
            case Blocks.Crops:
                return meta >= 7
                    ? [One(Items.Wheat), new(Items.Seeds, 0, 0, 3)]
                    : [new(Items.Seeds, 0, 0, 3)];
            case Blocks.Furnace: case Blocks.LitFurnace: return [One(Blocks.Furnace)];
            case Blocks.SignPost: case Blocks.WallSign: return [One(Items.Sign)];
            case Blocks.WoodenDoor: return (meta & 8) == 0 ? [One(Items.WoodenDoor)] : [];
            case Blocks.IronDoor: return (meta & 8) == 0 ? [One(Items.IronDoor)] : [];
            case Blocks.RedstoneTorchOff: case Blocks.RedstoneTorch: return [One(Blocks.RedstoneTorch)];
            case Blocks.SnowLayer: return [One(Items.Snowball)];
            case Blocks.SnowBlock: return [new(Items.Snowball, 0, 4, 4)];
            case Blocks.Clay: return [new(Items.ClayBall, 0, 4, 4)];
            case Blocks.SugarCane: return [One(Items.SugarCane)];
            case Blocks.RepeaterOff: case Blocks.RepeaterOn: return [One(Items.Repeater)];
            case Blocks.BrownMushroomBlock: return [new(Blocks.BrownMushroom, 0, 0, 2)];
            case Blocks.RedMushroomBlock: return [new(Blocks.RedMushroom, 0, 0, 2)];
            case Blocks.Melon: return [new(Items.MelonSlice, 0, 3, 7)];
            case Blocks.PumpkinStem: return [new(Items.PumpkinSeeds, 0, 0, 3)];
            case Blocks.MelonStem: return [new(Items.MelonSeeds, 0, 0, 3)];
            default:
                return Blocks.Exists(blockId) ? Self() : [];
        }
    }

    /// <summary>The main item you get from the block with the given tool, or null (e.g. stone → cobblestone).</summary>
    public static Drop? Primary(int blockId, int meta = 0, int heldItemId = -1)
    {
        var d = For(blockId, meta, heldItemId);
        return d.Count == 0 ? null : d.MaxBy(x => x.Expected);
    }

    /// <summary>Block ids whose drop is the given item (e.g. cobblestone ← stone, cobblestone). Useful for "collect X".</summary>
    public static IReadOnlyList<int> SourcesOf(int itemId, int damage = -1)
    {
        var data = Versioning.GameData.Current;
        return data.BlockIds.Where(b => data.DropsFor(b, Math.Max(damage, 0), BestHarvester(data, b))
                .Any(d => d.ItemId == itemId && (damage < 0 || d.Damage == damage)))
            .ToArray();
    }

    private static int BestHarvester(Versioning.IGameData data, int blockId) =>
        data.CanHarvest(blockId, -1) ? -1
        : blockId == Blocks.Web ? Items.Shears
        : blockId is Blocks.SnowLayer or Blocks.SnowBlock ? Items.DiamondShovel
        : Items.DiamondPickaxe;
}
