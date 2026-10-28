namespace MineClaw.Data.Versioning;

/// <summary>
/// Static game data of one version (docs/design/multi-version.md, "Seam: game data"): blocks, items, recipes, smelting
/// and dig rules. b1.8.1 = the hand-transcribed tables in this project (<see cref="Blocks"/>, <see cref="Items"/>,
/// <see cref="Recipes"/>, <see cref="Smelting"/>, <see cref="Tools"/>). 1.12.2 = tables generated from a data source
/// (P5); its block and item ids are a superset of b1.8.1's (same numbers for the old ones; block state = id &lt;&lt; 4 | meta),
/// so id constants such as <see cref="Blocks.Stone"/> stay valid and only the lookups swap.
/// <para>P5: the static tables (<see cref="Items"/>, <see cref="Recipes"/>, <see cref="Smelting"/>, <see cref="Drops"/>,
/// <see cref="Tools"/>.CanHarvest/DigTicks) are facades over <see cref="GameData.Current"/> (one version per process); the
/// b1.8.1 implementation below reads their own tables directly (the <c>…B</c> members).</para>
/// </summary>
public interface IGameData
{
    /// <summary>The version id these tables describe ("b1.8.1").</summary>
    string VersionId { get; }
    BlockInfo Block(int id);
    bool BlockExists(int id);
    BlockInfo? BlockByName(string name);
    ItemInfo Item(int id);
    bool ItemExists(int id);
    ItemInfo? ItemByName(string name);
    Recipe? MatchRecipe(IReadOnlyList<ItemStack> grid, int gridSize);
    IReadOnlyList<Recipe> RecipesFor(int itemId, int damage = -1);
    SmeltingRecipe? SmeltingFor(int inputId);
    int FuelTicks(int itemId);
    bool CanHarvest(int blockId, int heldItemId);
    int DigTicks(int blockId, int heldItemId, bool onGround = true, bool inWater = false);

    /// <summary>Ticks of continuous digging with everything the version's rule reads (1.12.2: the held stack's efficiency,
    /// haste, mining fatigue, aqua affinity). b1.8.1 reads only the block, the item id, on-ground and in-water.</summary>
    int DigTicks(in DigInput input) => DigTicks(input.BlockId, input.Held.IsEmpty ? -1 : input.Held.Id, input.OnGround, input.InWater);

    /// <summary>Human-readable item name with its variant ("Birch Wood Planks", "Charcoal").</summary>
    string DisplayName(int id, int damage = 0) => Items.DisplayNameB(id, damage);

    /// <summary>Forgiving name lookup that also returns the variant's damage (−1 = the name implies none).</summary>
    (ItemInfo Item, int Damage)? ItemByNameWithDamage(string name) => Items.ByNameWithDamageB(name);

    /// <summary>Snake-case name that keeps the variant and resolves back through <see cref="ItemByNameWithDamage"/>.</summary>
    string VariantName(int id, int meta) => Items.VariantNameB(id, meta);

    /// <summary>Every crafting recipe, in match order.</summary>
    IReadOnlyList<Recipe> AllRecipes => Recipes.AllB;

    /// <summary>"Unpack a storage block into 9" (planners prefer other routes).</summary>
    bool IsUnpack(Recipe r) => Recipes.IsUnpackB(r);

    /// <summary>Every furnace recipe.</summary>
    IReadOnlyList<SmeltingRecipe> AllSmelting => Smelting.AllB;

    /// <summary>What smelting this exact input variant yields (1.12.2 keys some inputs by metadata: wet sponge, stone bricks,
    /// fish types); b1.8.1 ignores the metadata.</summary>
    SmeltingRecipe? SmeltingFor(int inputId, int damage) => SmeltingFor(inputId);

    /// <summary>Ticks to smelt one item (both versions: 200).</summary>
    int CookTicks => Smelting.CookTicks;

    /// <summary>Typical drops when a player breaks the block holding <paramref name="heldItemId"/> (−1/0 = hand); empty when
    /// nothing drops, including when the held item can't harvest it. Fortune and silk touch are ignored.</summary>
    IReadOnlyList<Drop> DropsFor(int blockId, int meta = 0, int heldItemId = -1) => Drops.ForB(blockId, meta, heldItemId);

    /// <summary>The block ids this version has (for "which blocks drop X").</summary>
    IEnumerable<int> BlockIds => Blocks.All.Select(b => b.Id);

    /// <summary>Food value of an item variant (hunger points, saturation modifier), or null if not edible.</summary>
    (int Hunger, float Saturation)? Food(int itemId, int damage = 0) => Items.FoodB(itemId);

    /// <summary>Chance that eating the item gives Hunger or Poison (raw chicken 0.3, rotten flesh 0.8...).</summary>
    float FoodPoisonChance(int itemId, int damage = 0) => Items.FoodPoisonChanceB(itemId);

    /// <summary>Capability fact: slab and stair halves follow the clicked face and the cursor height (1.12.2
    /// <c>BlockSlab</c>/<c>BlockStairs.getStateForPlacement</c>: an underside click places a top half). b1.8.1 has bottom
    /// halves only, whatever the face.</summary>
    bool HalvesFollowCursor => false;
}

/// <summary>One dig, as the version's dig-speed rule sees it (1.12.2 <c>Block.getPlayerRelativeBlockHardness</c> +
/// <c>EntityPlayer.getDigSpeed</c>).</summary>
/// <param name="Haste">Haste amplifier + 1 (0 = no haste effect).</param>
/// <param name="Fatigue">Mining fatigue amplifier + 1 (0 = none).</param>
/// <param name="InWater">Eyes inside water.</param>
public readonly record struct DigInput(int BlockId, int BlockMeta, ItemStack Held, bool OnGround = true, bool InWater = false,
    int Haste = 0, int Fatigue = 0, bool AquaAffinity = false);

/// <summary>b1.8.1: wraps the existing static tables, unchanged.</summary>
public sealed class Beta181GameData : IGameData
{
    public static readonly Beta181GameData Instance = new();

    public string VersionId => "b1.8.1";
    public BlockInfo Block(int id) => Blocks.Get(id);
    public bool BlockExists(int id) => Blocks.Exists(id);
    public BlockInfo? BlockByName(string name) => Blocks.ByName(name);
    public ItemInfo Item(int id) => Items.GetB(id);
    public bool ItemExists(int id) => Items.ExistsB(id);
    public ItemInfo? ItemByName(string name) => Items.ByNameWithDamageB(name)?.Item;
    public Recipe? MatchRecipe(IReadOnlyList<ItemStack> grid, int gridSize) => Recipes.MatchB(grid, gridSize);
    public IReadOnlyList<Recipe> RecipesFor(int itemId, int damage = -1) => Recipes.ForOutputB(itemId, damage);
    public SmeltingRecipe? SmeltingFor(int inputId) => Smelting.ResultForB(inputId);
    public int FuelTicks(int itemId) => Smelting.FuelTicksB(itemId);
    public bool CanHarvest(int blockId, int heldItemId) => Tools.CanHarvestB(blockId, heldItemId);
    public int DigTicks(int blockId, int heldItemId, bool onGround = true, bool inWater = false) =>
        Tools.DigTicksB(blockId, heldItemId, onGround, inWater);
}
