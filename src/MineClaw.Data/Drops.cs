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
        $"{(Min == Max ? Min.ToString() : $"{Min}-{Max}")} {Items.DisplayName(ItemId, Damage)}{(Chance < 1f ? $" ({Percent.Of(Chance)})" : "")}";
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
        var c = new BetaBreak(blockId, meta, heldItemId == Items.Shears);
        if (BetaRules.TryGetValue(blockId, out var rule)) return rule(c);
        return Blocks.ExistsB(blockId) ? c.Self() : [];
    }

    /// <summary>One b1.8.1 break: the block, its meta and whether the held item is shears.</summary>
    private readonly record struct BetaBreak(int BlockId, int Meta, bool Shears)
    {
        public static Drop One(int id, int dmg = 0) => new(id, dmg, 1, 1);
        public Drop[] Self(int dmg = 0) => [One(BlockId, dmg)];
    }

    /// <summary>The b1.8.1 overrides by block id (a block without one drops itself).</summary>
    private sealed class BetaRuleTable : Dictionary<int, Func<BetaBreak, IReadOnlyList<Drop>>>
    {
        public void Each(Func<BetaBreak, IReadOnlyList<Drop>> rule, params int[] ids)
        {
            foreach (int id in ids) Add(id, rule);
        }

        public void Fixed(Drop drop, params int[] ids) => Each(_ => [drop], ids);
    }

    private static readonly BetaRuleTable BetaRules = BuildBetaRules();

    private static BetaRuleTable BuildBetaRules()
    {
        var r = new BetaRuleTable();
        // Ice turns into a water source if the block below is solid or liquid; silverfish stone spawns a silverfish.
        r.Each(_ => [], Blocks.Air, Blocks.Bedrock, Blocks.FlowingWater, Blocks.Water, Blocks.FlowingLava, Blocks.Lava, Blocks.PistonHead,
            Blocks.PistonMoving, Blocks.Fire, Blocks.Portal, Blocks.Glass, Blocks.Bookshelf, Blocks.MobSpawner, Blocks.DeadBush,
            Blocks.Cake, Blocks.Ice, Blocks.MonsterEgg, Blocks.LockedChest);

        r.Fixed(BetaBreak.One(Blocks.Cobblestone), Blocks.Stone);
        r.Fixed(BetaBreak.One(Blocks.Dirt), Blocks.Grass, Blocks.Farmland);
        r.Each(_ => [new(Blocks.Gravel, 0, 1, 1, 0.9f), new(Items.Flint, 0, 1, 1, 0.1f)], Blocks.Gravel);
        r.Fixed(BetaBreak.One(Items.Coal), Blocks.CoalOre);
        r.Fixed(BetaBreak.One(Items.Diamond), Blocks.DiamondOre);
        r.Fixed(new(Items.Dye, 4, 4, 8), Blocks.LapisOre);
        r.Fixed(new(Items.Redstone, 0, 4, 5), Blocks.RedstoneOre, Blocks.LitRedstoneOre);
        r.Fixed(new(Items.GlowstoneDust, 0, 2, 4), Blocks.Glowstone);
        r.Each(c => c.Self(c.Meta & 3), Blocks.Sapling, Blocks.Log);
        r.Each(c => c.Shears ? [BetaBreak.One(Blocks.Leaves, c.Meta & 3)] : [new(Blocks.Sapling, c.Meta & 3, 1, 1, 1f / 20f)], Blocks.Leaves);
        r.Each(c => c.Shears ? [BetaBreak.One(Blocks.TallGrass, c.Meta)] : [new(Items.Seeds, 0, 1, 1, 1f / 8f)], Blocks.TallGrass);
        r.Each(c => c.Shears ? [BetaBreak.One(Blocks.Vine)] : [], Blocks.Vine);
        r.Fixed(BetaBreak.One(Items.String), Blocks.Web);
        r.Each(c => c.Self(c.Meta), Blocks.Wool, Blocks.Slab, Blocks.StoneBricks);
        r.Each(c => [new(Blocks.Slab, c.Meta, 2, 2)], Blocks.DoubleSlab);
        r.Each(c => (c.Meta & 8) == 0 ? [BetaBreak.One(Items.Bed)] : [], Blocks.Bed); // foot half drops the bed
        // Stairs drop their model block's drop.
        r.Fixed(BetaBreak.One(Blocks.Planks), Blocks.WoodenStairs);
        r.Fixed(BetaBreak.One(Blocks.Cobblestone), Blocks.CobblestoneStairs);
        r.Fixed(BetaBreak.One(Blocks.Bricks), Blocks.BrickStairs);
        r.Fixed(BetaBreak.One(Blocks.StoneBricks), Blocks.StoneBrickStairs);
        r.Fixed(BetaBreak.One(Items.Redstone), Blocks.RedstoneWire);
        r.Each(c => c.Meta >= 7 ? [BetaBreak.One(Items.Wheat), new(Items.Seeds, 0, 0, 3)] : [new(Items.Seeds, 0, 0, 3)], Blocks.Crops);
        r.Fixed(BetaBreak.One(Blocks.Furnace), Blocks.Furnace, Blocks.LitFurnace);
        r.Fixed(BetaBreak.One(Items.Sign), Blocks.SignPost, Blocks.WallSign);
        r.Each(c => (c.Meta & 8) == 0 ? [BetaBreak.One(Items.WoodenDoor)] : [], Blocks.WoodenDoor);
        r.Each(c => (c.Meta & 8) == 0 ? [BetaBreak.One(Items.IronDoor)] : [], Blocks.IronDoor);
        r.Fixed(BetaBreak.One(Blocks.RedstoneTorch), Blocks.RedstoneTorchOff, Blocks.RedstoneTorch);
        r.Fixed(BetaBreak.One(Items.Snowball), Blocks.SnowLayer);
        r.Fixed(new(Items.Snowball, 0, 4, 4), Blocks.SnowBlock);
        r.Fixed(new(Items.ClayBall, 0, 4, 4), Blocks.Clay);
        r.Fixed(BetaBreak.One(Items.SugarCane), Blocks.SugarCane);
        r.Fixed(BetaBreak.One(Items.Repeater), Blocks.RepeaterOff, Blocks.RepeaterOn);
        r.Fixed(new(Blocks.BrownMushroom, 0, 0, 2), Blocks.BrownMushroomBlock);
        r.Fixed(new(Blocks.RedMushroom, 0, 0, 2), Blocks.RedMushroomBlock);
        r.Fixed(new(Items.MelonSlice, 0, 3, 7), Blocks.Melon);
        r.Fixed(new(Items.PumpkinSeeds, 0, 0, 3), Blocks.PumpkinStem);
        r.Fixed(new(Items.MelonSeeds, 0, 0, 3), Blocks.MelonStem);
        return r;
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
