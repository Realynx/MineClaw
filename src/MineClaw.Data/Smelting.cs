namespace MineClaw.Data;

/// <summary>A furnace recipe. b1.8.1 smelting is keyed by input id only (any metadata smelts the same).</summary>
public sealed record SmeltingRecipe(int InputId, int OutputId, int OutputDamage, int OutputCount = 1)
{
    public override string ToString() => $"{Items.Get(InputId).DisplayName} -> {Items.DisplayName(OutputId, OutputDamage)}";
}

/// <summary>Beta 1.8.1 furnace data (FurnaceRecipes + TileEntityFurnace.getItemBurnTime from the client jar).</summary>
public static class Smelting
{
    /// <summary>Ticks to smelt one item (10 s at 20 TPS).</summary>
    public const int CookTicks = 200;

    private static readonly SmeltingRecipe[] _all =
    [
        new(Blocks.IronOre, Items.IronIngot, 0),
        new(Blocks.GoldOre, Items.GoldIngot, 0),
        new(Blocks.DiamondOre, Items.Diamond, 0),
        new(Blocks.Sand, Blocks.Glass, 0),
        new(Items.Porkchop, Items.CookedPorkchop, 0),
        new(Items.RawBeef, Items.Steak, 0),
        new(Items.RawChicken, Items.CookedChicken, 0),
        new(Items.RawFish, Items.CookedFish, 0),
        new(Blocks.Cobblestone, Blocks.Stone, 0),
        new(Items.ClayBall, Items.Brick, 0),
        new(Blocks.Cactus, Items.Dye, 2),       // cactus green
        new(Blocks.Log, Items.Coal, 1),         // charcoal
    ];

    private static readonly Dictionary<int, SmeltingRecipe> _byInput = _all.ToDictionary(r => r.InputId);

    public static IReadOnlyList<SmeltingRecipe> All => _all;

    /// <summary>What smelting this input yields, or null.</summary>
    public static SmeltingRecipe? ResultFor(int inputId) => _byInput.GetValueOrDefault(inputId);

    public static bool IsSmeltable(int inputId) => _byInput.ContainsKey(inputId);

    /// <summary>Furnace recipes producing the item (damage -1 = any).</summary>
    public static IReadOnlyList<SmeltingRecipe> ForOutput(int outputId, int damage = -1) =>
        _all.Where(r => r.OutputId == outputId && (damage < 0 || r.OutputDamage == damage)).ToArray();

    /// <summary>Block ids made of wood material in b1.8.1 (each burns 300 ticks).</summary>
    private static readonly HashSet<int> WoodBlocks =
    [
        Blocks.Planks, Blocks.Log, Blocks.NoteBlock, Blocks.Bookshelf, Blocks.WoodenStairs, Blocks.Chest, Blocks.CraftingTable,
        Blocks.SignPost, Blocks.WoodenDoor, Blocks.WallSign, Blocks.WoodenPressurePlate, Blocks.Jukebox, Blocks.Fence,
        Blocks.LockedChest, Blocks.Trapdoor, Blocks.BrownMushroomBlock, Blocks.RedMushroomBlock, Blocks.FenceGate,
    ];

    /// <summary>
    /// Burn time in ticks: wood-material blocks 300, sticks 100, saplings 100, coal/charcoal 1600, lava bucket 20000, else 0.
    /// (Wooden tools, slabs, bowls etc. are NOT fuel in b1.8.1.)
    /// </summary>
    public static int FuelTicks(int itemId) => itemId switch
    {
        Items.Stick => 100,
        Items.Coal => 1600,
        Items.LavaBucket => 20000,
        Blocks.Sapling => 100,
        _ when itemId is > 0 and < 256 && WoodBlocks.Contains(itemId) => 300,
        _ => 0,
    };

    public static bool IsFuel(int itemId) => FuelTicks(itemId) > 0;

    /// <summary>How many items one unit of this fuel smelts (e.g. coal 8, planks/log 1.5, stick 0.5).</summary>
    public static double ItemsPerFuel(int fuelItemId) => FuelTicks(fuelItemId) / (double)CookTicks;

    /// <summary>Fuel units needed to smelt <paramref name="items"/> items in one continuous run (0 if not a fuel).</summary>
    public static int FuelNeeded(int items, int fuelItemId)
    {
        int t = FuelTicks(fuelItemId);
        if (items <= 0) return 0;
        if (t <= 0) return 0;
        return (int)Math.Ceiling(items * (double)CookTicks / t);
    }

    /// <summary>Picks the fuel item id whose stack offers the most total burn time (lava buckets excluded), or null.</summary>
    public static int? BestFuel(IEnumerable<ItemStack> inventory) =>
        inventory.Where(s => !s.IsEmpty && IsFuel(s.Id) && s.Id != Items.LavaBucket)
                 .OrderByDescending(s => FuelTicks(s.Id) * (long)s.Count)
                 .Select(s => (int?)s.Id).FirstOrDefault();
}
