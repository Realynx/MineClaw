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
}

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
