namespace MineClaw.Data.Versioning;

/// <summary>
/// Static game data of one version (docs/design/multi-version.md, "Seam: game data"): blocks, items, recipes, smelting
/// and dig rules. b1.8.1 = the hand-transcribed tables in this project (<see cref="Blocks"/>, <see cref="Items"/>,
/// <see cref="Recipes"/>, <see cref="Smelting"/>, <see cref="Tools"/>). 1.12.2 = tables generated from a data source
/// (P5); its block and item ids are a superset of b1.8.1's (same numbers for the old ones; block state = id &lt;&lt; 4 | meta),
/// so id constants such as <see cref="Blocks.Stone"/> stay valid and only the lookups swap.
/// <para>P0: defined and registered in the profile; the ~2,000 static call sites are unchanged. P5 makes the static
/// facades delegate to the process's profile (one version per process).</para>
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
    string DisplayName(int id, int damage = 0) => Items.DisplayName(id, damage);

    /// <summary>Forgiving name lookup that also returns the variant's damage (−1 = the name implies none).</summary>
    (ItemInfo Item, int Damage)? ItemByNameWithDamage(string name) => Items.ByNameWithDamage(name);

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
    public ItemInfo Item(int id) => Items.Get(id);
    public bool ItemExists(int id) => Items.Exists(id);
    public ItemInfo? ItemByName(string name) => Items.ByName(name);
    public Recipe? MatchRecipe(IReadOnlyList<ItemStack> grid, int gridSize) => Recipes.Match(grid, gridSize);
    public IReadOnlyList<Recipe> RecipesFor(int itemId, int damage = -1) => Recipes.ForOutput(itemId, damage);
    public SmeltingRecipe? SmeltingFor(int inputId) => Smelting.ResultFor(inputId);
    public int FuelTicks(int itemId) => Smelting.FuelTicks(itemId);
    public bool CanHarvest(int blockId, int heldItemId) => Tools.CanHarvest(blockId, heldItemId);
    public int DigTicks(int blockId, int heldItemId, bool onGround = true, bool inWater = false) =>
        Tools.DigTicks(blockId, heldItemId, onGround, inWater);
}
