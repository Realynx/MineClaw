namespace MineClaw.Data;

/// <summary>
/// Beta 1.8.1 harvesting and dig-speed rules, mirroring Block.blockStrength / EntityPlayer.getCurrentPlayerStrVsBlock /
/// ItemTool, ItemPickaxe, ItemSpade, ItemAxe, ItemSword and ItemShears from the client jar.
/// </summary>
/// <remarks>
/// Per tick the client adds <c>strength</c> to the dig progress and breaks the block once it reaches 1.0, where
/// <c>strength = toolSpeed / hardness / 30</c> if the player can harvest the block, else <c>1 / hardness / 100</c>.
/// toolSpeed is divided by 5 when the head is in water and again by 5 when not on the ground. Hardness 0 breaks instantly.
/// After a block breaks the client waits 5 ticks before damaging the next one. The b1.8 server accepts a dig once
/// about 70% of that time has elapsed, but clients should use the full value.
/// </remarks>
public static class Tools
{
    /// <summary>ItemPickaxe's effective-block list (speed bonus applies only to these!).</summary>
    public static readonly IReadOnlySet<int> PickaxeEffective = new HashSet<int>
    {
        Blocks.Cobblestone, Blocks.DoubleSlab, Blocks.Slab, Blocks.Stone, Blocks.Sandstone, Blocks.MossyCobblestone,
        Blocks.IronOre, Blocks.IronBlock, Blocks.CoalOre, Blocks.GoldBlock, Blocks.GoldOre, Blocks.DiamondOre,
        Blocks.DiamondBlock, Blocks.Ice, Blocks.Netherrack, Blocks.LapisOre, Blocks.LapisBlock,
    };

    /// <summary>ItemSpade's effective-block list.</summary>
    public static readonly IReadOnlySet<int> ShovelEffective = new HashSet<int>
    {
        Blocks.Grass, Blocks.Dirt, Blocks.Sand, Blocks.Gravel, Blocks.SnowLayer, Blocks.SnowBlock, Blocks.Clay, Blocks.Farmland,
    };

    /// <summary>ItemAxe's effective-block list (note: not fences, crafting tables, doors, stairs...).</summary>
    public static readonly IReadOnlySet<int> AxeEffective = new HashSet<int>
    {
        Blocks.Planks, Blocks.Bookshelf, Blocks.Log, Blocks.Chest,
    };

    /// <summary>Blocks whose material is not harvestable by hand (rock, iron, snow, crafted snow, web): they drop nothing without the right tool.</summary>
    public static readonly IReadOnlySet<int> RequiresToolBlocks = new HashSet<int>
    {
        // rock
        1, 4, 7, 14, 15, 16, 21, 22, 23, 24, 43, 44, 45, 48, 49, 52, 56, 61, 62, 67, 70, 73, 74, 87, 89, 98, 108, 109,
        // iron
        41, 42, 57, 71, 101,
        // snow layer, snow block, web
        78, 80, 30,
    };

    /// <summary>Efficiency by tier (0 wood, 1 stone, 2 iron, 3 diamond, 4 gold).</summary>
    public static float Efficiency(int tier) => tier switch { 0 => 2f, 1 => 4f, 2 => 6f, 3 => 8f, 4 => 12f, _ => 1f };

    /// <summary>Harvest level by tier: gold harvests like wood.</summary>
    public static int HarvestLevel(int tier) => tier switch { 4 => 0, < 0 => -1, _ => tier };

    /// <summary>True if the block material needs a specific tool to drop anything.</summary>
    public static bool RequiresTool(int blockId) => RequiresToolBlocks.Contains(blockId);

    /// <summary>Whether breaking the block with this held item (-1/0 = empty hand) yields drops (EntityPlayer.canHarvestBlock).</summary>
    public static bool CanHarvest(int blockId, int heldItemId) =>
        Versioning.GameData.IsBeta(out var d) ? CanHarvestB(blockId, heldItemId) : d.CanHarvest(blockId, heldItemId);

    internal static bool CanHarvestB(int blockId, int heldItemId)
    {
        if (!RequiresTool(blockId)) return true;
        if (heldItemId <= 0) return false;
        var item = Items.GetB(heldItemId);
        return item.Tool switch
        {
            ToolType.Pickaxe => PickaxeHarvests(blockId, HarvestLevel(item.ToolTier)),
            ToolType.Shovel => blockId is Blocks.SnowLayer or Blocks.SnowBlock,
            ToolType.Sword or ToolType.Shears => blockId == Blocks.Web,
            _ => false,
        };
    }

    /// <summary>The harvest level an ore or storage block needs from a pickaxe (any other rock or iron material: any pickaxe).</summary>
    private static readonly Dictionary<int, int> PickaxeLevelNeeded = new()
    {
        [Blocks.DiamondBlock] = 2, [Blocks.DiamondOre] = 2, [Blocks.GoldBlock] = 2, [Blocks.GoldOre] = 2,
        [Blocks.IronBlock] = 1, [Blocks.IronOre] = 1, [Blocks.LapisBlock] = 1, [Blocks.LapisOre] = 1,
        [Blocks.RedstoneOre] = 2, [Blocks.LitRedstoneOre] = 2,
    };

    /// <summary>A pickaxe of harvest level <paramref name="lvl"/>: obsidian takes diamond, snow and cobweb never drop for it.</summary>
    private static bool PickaxeHarvests(int blockId, int lvl)
    {
        if (blockId == Blocks.Obsidian) return lvl == 3;
        if (blockId is Blocks.SnowLayer or Blocks.SnowBlock or Blocks.Web) return false;
        return !PickaxeLevelNeeded.TryGetValue(blockId, out int need) || lvl >= need;
    }

    /// <summary>The held item's speed multiplier against the block (1.0 = bare hand).</summary>
    public static float StrVsBlock(int blockId, int heldItemId)
    {
        if (heldItemId <= 0) return 1f;
        var item = Items.GetB(heldItemId);
        return item.Tool switch
        {
            ToolType.Pickaxe => EffectiveSpeed(PickaxeEffective, blockId, item.ToolTier),
            ToolType.Shovel => EffectiveSpeed(ShovelEffective, blockId, item.ToolTier),
            ToolType.Axe => EffectiveSpeed(AxeEffective, blockId, item.ToolTier),
            ToolType.Sword => blockId == Blocks.Web ? 15f : 1.5f,
            ToolType.Shears => ShearsSpeed(blockId),
            _ => 1f,
        };
    }

    private static float EffectiveSpeed(IReadOnlySet<int> effectiveOn, int blockId, int tier) =>
        effectiveOn.Contains(blockId) ? Efficiency(tier) : 1f;

    private static float ShearsSpeed(int blockId) =>
        blockId is Blocks.Web or Blocks.Leaves ? 15f : blockId == Blocks.Wool ? 5f : 1f;

    /// <summary>Dig progress added per tick (Block.blockStrength). 0 for unbreakable blocks and liquids; +∞ for hardness 0.</summary>
    public static float DigProgressPerTick(int blockId, int heldItemId, bool onGround = true, bool inWater = false)
    {
        var b = Blocks.GetB(blockId);
        if (b.Liquid || blockId == Blocks.Air) return 0f;
        float h = b.Hardness;
        if (h < 0f) return 0f;
        if (!CanHarvestB(blockId, heldItemId)) return 1f / h / 100f;
        float speed = StrVsBlock(blockId, heldItemId);
        if (inWater) speed /= 5f;
        if (!onGround) speed /= 5f;
        return speed / h / 30f;
    }

    /// <summary>
    /// Ticks of continuous digging needed to break the block (0 = instant on first hit,
    /// <see cref="int.MaxValue"/> = unbreakable/liquid). Emulates the client's float accumulation.
    /// </summary>
    public static int DigTicks(int blockId, int heldItemId, bool onGround = true, bool inWater = false) =>
        Versioning.GameData.IsBeta(out var d) ? DigTicksB(blockId, heldItemId, onGround, inWater) : d.DigTicks(blockId, heldItemId, onGround, inWater);

    internal static int DigTicksB(int blockId, int heldItemId, bool onGround = true, bool inWater = false)
    {
        float s = DigProgressPerTick(blockId, heldItemId, onGround, inWater);
        if (s <= 0f) return int.MaxValue;
        if (s >= 1f || float.IsInfinity(s)) return 0;
        float acc = 0f;
        int ticks = 0;
        while (acc < 1f) { acc += s; ticks++; }
        return ticks;
    }

    /// <summary>Dig time in seconds at 20 TPS.</summary>
    public static double DigSeconds(int blockId, int heldItemId, bool onGround = true, bool inWater = false)
    {
        int t = DigTicks(blockId, heldItemId, onGround, inWater);
        return t == int.MaxValue ? double.PositiveInfinity : t / 20.0;
    }

    /// <summary>
    /// Best item in the inventory for breaking the block, or null if the bare hand is as good (or nothing helps).
    /// Prefers stacks that can harvest the block, then fastest, then the least valuable tier. Swords are only
    /// suggested for cobwebs (they lose 2 durability per block); shears for leaves/wool/web/vines.
    /// </summary>
    public static ItemStack? BestToolFor(int blockId, IEnumerable<ItemStack> inventory)
    {
        var list = inventory as IReadOnlyList<ItemStack> ?? inventory.ToList();
        int idx = BestToolIndex(blockId, list);
        return idx < 0 ? null : list[idx];
    }

    /// <summary>Index into <paramref name="slots"/> of the best tool for the block, or -1 for bare hand.</summary>
    public static int BestToolIndex(int blockId, IReadOnlyList<ItemStack> slots)
    {
        bool handHarvests = CanHarvest(blockId, -1);
        int handTicks = DigTicks(blockId, -1);
        bool wantsShears = blockId is Blocks.Leaves or Blocks.Vine or Blocks.TallGrass; // shears change the drop
        int best = -1, bestTicks = handTicks, bestTier = int.MaxValue;
        bool bestHarvest = handHarvests;
        for (int i = 0; i < slots.Count; i++)
        {
            var s = slots[i];
            if (s.IsEmpty) continue;
            var info = Items.Get(s.Id);
            if (info.Tool is ToolType.None or ToolType.Hoe) continue;
            if (info.Tool == ToolType.Sword && blockId != Blocks.Web) continue;
            bool harvest = CanHarvest(blockId, s.Id);
            int ticks = DigTicks(blockId, s.Id);
            bool shearBonus = wantsShears && info.Tool == ToolType.Shears;
            if (!shearBonus && ticks >= handTicks && harvest == handHarvests) continue; // no improvement over hand
            int tier = info.Tool == ToolType.Shears ? 0 : info.ToolTier == 4 ? 0 : info.ToolTier;
            bool better =
                harvest != bestHarvest ? harvest :
                ticks != bestTicks ? ticks < bestTicks :
                tier < bestTier;
            if (best < 0 || better || (shearBonus && Items.Get(slots[best].Id).Tool != ToolType.Shears))
            {
                best = i; bestTicks = ticks; bestTier = tier; bestHarvest = harvest;
            }
        }
        return best;
    }
}
