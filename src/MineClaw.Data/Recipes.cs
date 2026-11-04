namespace MineClaw.Data;

/// <summary>A recipe input. <see cref="Damage"/> = -1 matches any metadata (wildcard).</summary>
public readonly record struct Ingredient(int Id, int Damage = -1)
{
    public bool Matches(int id, int damage) => id == Id && (Damage < 0 || damage == Damage);
    public bool Matches(ItemStack s) => !s.IsEmpty && Matches(s.Id, s.Damage);
    public override string ToString() => Damage < 0 ? Items.Get(Id).DisplayName : Items.DisplayName(Id, Damage);
}

/// <summary>
/// A b1.8.1 crafting recipe. Shaped recipes keep their pattern (row-major, <see cref="Width"/> x <see cref="Height"/>,
/// null = empty cell) and may be placed anywhere in the grid and mirrored horizontally. Shapeless recipes only list inputs.
/// </summary>
public sealed class Recipe
{
    internal Recipe(int outputId, int outputCount, int outputDamage, bool shaped, int width, int height,
        Ingredient?[] pattern, Ingredient[] ingredients, string[] rows)
    {
        OutputId = outputId; OutputCount = outputCount; OutputDamage = outputDamage; Shaped = shaped;
        Width = width; Height = height; Pattern = pattern; Ingredients = ingredients; PatternRows = rows;
        Totals = ingredients.GroupBy(i => i).Select(g => (g.Key, g.Count())).ToArray();
    }

    public int OutputId { get; }
    public int OutputCount { get; }
    /// <summary>Metadata of the result (e.g. wool colour, slab type, charcoal...).</summary>
    public int OutputDamage { get; }
    public bool Shaped { get; }
    public int Width { get; }
    public int Height { get; }
    /// <summary>Shaped: Width*Height cells, row-major, null = must be empty. Shapeless: empty.</summary>
    public IReadOnlyList<Ingredient?> Pattern { get; }
    /// <summary>Every input item, one entry per consumed item (shaped: non-empty cells in pattern order).</summary>
    public IReadOnlyList<Ingredient> Ingredients { get; }
    /// <summary>Ingredients grouped with the count consumed per craft.</summary>
    public IReadOnlyList<(Ingredient Ingredient, int Count)> Totals { get; }
    /// <summary>Human-readable pattern rows using the original key characters (shapeless: empty).</summary>
    public IReadOnlyList<string> PatternRows { get; }

    public ItemStack Output => new((short)OutputId, (byte)OutputCount, (short)OutputDamage);

    /// <summary>True if it can be crafted in the 2x2 inventory grid (no crafting table needed).</summary>
    public bool FitsIn2x2 => Shaped ? Width <= 2 && Height <= 2 : Ingredients.Count <= 4;
    public bool RequiresCraftingTable => !FitsIn2x2;

    /// <summary>
    /// Lays the recipe out into a square crafting grid (gridSize 2 = inventory, 3 = crafting table), top-left aligned.
    /// Returns gridSize*gridSize cells, row-major; index i maps to crafting slot i+1 in both window types
    /// (slot 0 is the result slot). Returns null if it does not fit.
    /// </summary>
    public Ingredient?[]? LayoutFor(int gridSize)
    {
        var cells = new Ingredient?[gridSize * gridSize];
        if (Shaped)
        {
            if (Width > gridSize || Height > gridSize) return null;
            for (int r = 0; r < Height; r++)
                for (int c = 0; c < Width; c++)
                    cells[r * gridSize + c] = Pattern[r * Width + c];
        }
        else
        {
            if (Ingredients.Count > cells.Length) return null;
            for (int i = 0; i < Ingredients.Count; i++) cells[i] = Ingredients[i];
        }
        return cells;
    }

    public override string ToString()
    {
        var outName = Items.DisplayName(OutputId, OutputDamage);
        var inputs = string.Join(", ", Totals.Select(t => $"{t.Count} {t.Ingredient}"));
        return $"{OutputCount} {outName} <- {inputs}{(Shaped ? $" [{string.Join("/", PatternRows)}]" : " (shapeless)")}";
    }
}

/// <summary>
/// All Beta 1.8.1 crafting recipes, transcribed from the client jar's CraftingManager (and its tool/weapon/armor/ingot/
/// food/block/dye helpers). Notable b1.8 quirks: stairs give 4, slabs give 3 (six slab types incl. wooden, brick and
/// stone brick), signs give 1, ladders give 2, golden apple needs 8 gold BLOCKS, buttons are 2 stone stacked vertically,
/// chain armor needs fire (uncraftable), no enchanting table/brewing/nether wart, no pumpkin seeds from pumpkins,
/// no gold nuggets. Shaped recipes given a block accept any metadata of it (wildcard); item ingredients need damage 0.
/// </summary>
public static class Recipes
{
    private static readonly Recipe[] _all = Build();
    private static readonly ILookup<int, Recipe> _byOutput = _all.ToLookup(r => r.OutputId);

    /// <summary>Every recipe of this process's version (<see cref="Versioning.GameData.Current"/>).</summary>
    public static IReadOnlyList<Recipe> All => Versioning.GameData.IsBeta(out var d) ? _all : d.AllRecipes;
    internal static IReadOnlyList<Recipe> AllB => _all;

    /// <summary>Recipes producing <paramref name="itemId"/>; damage -1 = any variant.</summary>
    public static IReadOnlyList<Recipe> ForOutput(int itemId, int damage = -1) =>
        Versioning.GameData.IsBeta(out var d) ? ForOutputB(itemId, damage) : d.RecipesFor(itemId, damage);

    internal static IReadOnlyList<Recipe> ForOutputB(int itemId, int damage = -1) =>
        _byOutput[itemId].Where(r => damage < 0 || r.OutputDamage == damage).ToArray();

    /// <summary>
    /// "44:3, 3 cobblestone → 3": the exact variant and its recipe's input and yield (from the recipe data), so a cobblestone
    /// slab can't be read as a stone slab; a smelted input says so ("44:0, 3 stone (smelted cobblestone) → 3"). No recipe: "id:meta".
    /// </summary>
    public static string VariantTag(int itemId, int damage)
    {
        string tag = $"{itemId}:{Math.Max(0, damage)}";
        if (ForOutput(itemId, Math.Max(0, damage)).FirstOrDefault() is not { } r) return tag;
        var inputs = r.Totals.Select(t =>
        {
            var (id, d) = (t.Ingredient.Id, t.Ingredient.Damage);
            string n = (d < 0 ? Items.Get(id).Name : Items.VariantName(id, d)).Replace('_', ' ');
            if (ForOutput(id).Count == 0 && Smelting.ForOutput(id, Math.Max(0, d)) is [var s, ..]) n += $" (smelted {Items.Get(s.InputId).Name.Replace('_', ' ')})";
            return $"{t.Count} {n}";
        });
        return $"{tag}, {string.Join(" + ", inputs)} → {r.OutputCount}";
    }

    /// <summary>Recipes producing the named item (see <see cref="Items.ByNameWithDamage"/>).</summary>
    public static IReadOnlyList<Recipe> ForOutput(string name) =>
        Items.ByNameWithDamage(name) is { } r ? ForOutput(r.Item.Id, r.Damage) : [];

    /// <summary>Recipes that consume the item (any matching ingredient).</summary>
    public static IReadOnlyList<Recipe> UsingIngredient(int itemId, int damage = 0) =>
        All.Where(r => r.Ingredients.Any(i => i.Matches(itemId, damage))).ToArray();

    /// <summary>
    /// Finds the recipe matching a crafting grid (row-major, gridSize x gridSize, empty = <see cref="ItemStack.IsEmpty"/>),
    /// honouring position-independence and horizontal mirroring like b1.8's ShapedRecipes.
    /// </summary>
    public static Recipe? Match(IReadOnlyList<ItemStack> grid, int gridSize) =>
        Versioning.GameData.IsBeta(out var d) ? MatchB(grid, gridSize) : d.MatchRecipe(grid, gridSize);

    internal static Recipe? MatchB(IReadOnlyList<ItemStack> grid, int gridSize) => MatchIn(_all, grid, gridSize);

    /// <summary>The first recipe of <paramref name="recipes"/> the grid matches (b1.8 and 1.12.2 match the same way:
    /// <c>ShapedRecipes.checkMatch</c> anywhere in the grid, mirrored or not; <c>ShapelessRecipes.matches</c>).</summary>
    internal static Recipe? MatchIn(IReadOnlyList<Recipe> recipes, IReadOnlyList<ItemStack> grid, int gridSize)
    {
        foreach (var r in recipes)
            if (r.Shaped ? MatchShaped(r, grid, gridSize) : MatchShapeless(r, grid)) return r;
        return null;
    }

    private static bool MatchShapeless(Recipe r, IReadOnlyList<ItemStack> grid)
    {
        var need = r.Ingredients.ToList();
        foreach (var s in grid)
        {
            if (s.IsEmpty) continue;
            var idx = need.FindIndex(i => i.Matches(s));
            if (idx < 0) return false;
            need.RemoveAt(idx);
        }
        return need.Count == 0;
    }

    private static bool MatchShaped(Recipe r, IReadOnlyList<ItemStack> grid, int n)
    {
        for (int ox = 0; ox <= n - r.Width; ox++)
            for (int oy = 0; oy <= n - r.Height; oy++)
                foreach (var mirror in new[] { false, true })
                    if (ShapedAt(r, grid, n, ox, oy, mirror)) return true;
        return false;
    }

    /// <summary>The pattern placed at (<paramref name="ox"/>, <paramref name="oy"/>) (mirrored or not) is exactly the grid: every
    /// pattern cell matches and every other cell is empty.</summary>
    private static bool ShapedAt(Recipe r, IReadOnlyList<ItemStack> grid, int n, int ox, int oy, bool mirror)
    {
        for (int x = 0; x < n; x++)
            for (int y = 0; y < n; y++)
            {
                var have = grid[y * n + x];
                if (!(PatternAt(r, x - ox, y - oy, mirror) is { } w ? w.Matches(have) : have.IsEmpty)) return false;
            }
        return true;
    }

    private static Ingredient? PatternAt(Recipe r, int rx, int ry, bool mirror)
    {
        if (rx < 0 || ry < 0 || rx >= r.Width || ry >= r.Height) return null;
        return r.Pattern[ry * r.Width + (mirror ? r.Width - rx - 1 : rx)];
    }

    // ---------------------------------------------------------------- table

    /// <summary>Block ingredient in a shaped recipe: wildcard metadata (as the jar does for Block arguments).</summary>
    private static Ingredient Bk(int id) => new(id, -1);
    /// <summary>Item ingredient: metadata must be 0 unless given.</summary>
    private static Ingredient It(int id, int dmg = 0) => new(id, dmg);

    internal static Recipe Shaped(int outId, int count, int outDmg, string[] rows, params (char Key, Ingredient Ing)[] keys)
    {
        int w = rows.Max(r => r.Length), h = rows.Length;
        var map = keys.ToDictionary(k => k.Key, k => k.Ing);
        var pattern = new Ingredient?[w * h];
        var list = new List<Ingredient>();
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                char c = x < rows[y].Length ? rows[y][x] : ' ';
                if (c == ' ') continue;
                pattern[y * w + x] = map[c];
                list.Add(map[c]);
            }
        return new Recipe(outId, count, outDmg, true, w, h, pattern, list.ToArray(), rows);
    }

    internal static Recipe Shapeless(int outId, int count, int outDmg, params Ingredient[] inputs) =>
        new(outId, count, outDmg, false, 0, 0, [], inputs, []);

    private static Recipe[] Build()
    {
        var l = new List<Recipe>();
        void S(int o, int n, string[] rows, params (char, Ingredient)[] k) => l.Add(Shaped(o, n, 0, rows, k));
        void SD(int o, int n, int d, string[] rows, params (char, Ingredient)[] k) => l.Add(Shaped(o, n, d, rows, k));

        const int stick = Items.Stick, planks = Blocks.Planks, cobble = Blocks.Cobblestone, iron = Items.IronIngot,
            gold = Items.GoldIngot, diamond = Items.Diamond, redstone = Items.Redstone, str = Items.String;

        // --- RecipesTools: pickaxe, shovel, axe, hoe x wood/stone/iron/diamond/gold
        Ingredient[] toolMats = [Bk(planks), Bk(cobble), It(iron), It(diamond), It(gold)];
        string[][] toolRows = [["XXX", " # ", " # "], ["X", "#", "#"], ["XX", "X#", " #"], ["XX", " #", " #"]];
        int[][] toolOut = [[270, 274, 257, 278, 285], [269, 273, 256, 277, 284], [271, 275, 258, 279, 286], [290, 291, 292, 293, 294]];
        for (int t = 0; t < 4; t++)
            for (int m = 0; m < 5; m++)
                S(toolOut[t][m], 1, toolRows[t], ('#', It(stick)), ('X', toolMats[m]));
        S(Items.Shears, 1, [" #", "# "], ('#', It(iron)));

        // --- RecipesWeapons
        int[] swords = [268, 272, 267, 276, 283];
        for (int m = 0; m < 5; m++) S(swords[m], 1, ["X", "X", "#"], ('#', It(stick)), ('X', toolMats[m]));
        S(Items.Bow, 1, [" #X", "# X", " #X"], ('X', It(str)), ('#', It(stick)));
        S(Items.Arrow, 4, ["X", "#", "Y"], ('Y', It(Items.Feather)), ('X', It(Items.Flint)), ('#', It(stick)));

        // --- RecipesIngots (storage blocks both ways)
        (int Block, Ingredient Item)[] storage =
            [(Blocks.GoldBlock, It(gold)), (Blocks.IronBlock, It(iron)), (Blocks.DiamondBlock, It(diamond)), (Blocks.LapisBlock, It(Items.Dye, 4))];
        foreach (var (blk, item) in storage)
        {
            S(blk, 1, ["###", "###", "###"], ('#', item));
            SD(item.Id, 9, Math.Max(item.Damage, 0), ["#"], ('#', Bk(blk)));
        }

        // --- RecipesFood
        S(Items.MushroomStew, 1, ["Y", "X", "#"], ('X', Bk(Blocks.BrownMushroom)), ('Y', Bk(Blocks.RedMushroom)), ('#', It(Items.Bowl)));
        S(Items.MushroomStew, 1, ["Y", "X", "#"], ('X', Bk(Blocks.RedMushroom)), ('Y', Bk(Blocks.BrownMushroom)), ('#', It(Items.Bowl)));
        S(Items.Cookie, 8, ["#X#"], ('X', It(Items.Dye, 3)), ('#', It(Items.Wheat)));
        S(Blocks.Melon, 1, ["MMM", "MMM", "MMM"], ('M', It(Items.MelonSlice)));
        S(Items.MelonSeeds, 1, ["M"], ('M', It(Items.MelonSlice)));

        // --- RecipesCrafting
        S(Blocks.Chest, 1, ["###", "# #", "###"], ('#', Bk(planks)));
        S(Blocks.Furnace, 1, ["###", "# #", "###"], ('#', Bk(cobble)));
        S(Blocks.CraftingTable, 1, ["##", "##"], ('#', Bk(planks)));
        S(Blocks.Sandstone, 1, ["##", "##"], ('#', Bk(Blocks.Sand)));
        S(Blocks.StoneBricks, 4, ["##", "##"], ('#', Bk(Blocks.Stone)));
        S(Blocks.IronBars, 16, ["###", "###"], ('#', It(iron)));
        S(Blocks.GlassPane, 16, ["###", "###"], ('#', Bk(Blocks.Glass)));

        // --- RecipesArmor (chainmail needs fire: listed for completeness, not craftable in practice)
        Ingredient[] armorMats = [It(Items.Leather), Bk(Blocks.Fire), It(iron), It(diamond), It(gold)];
        string[][] armorRows = [["XXX", "X X"], ["X X", "XXX", "XXX"], ["XXX", "X X", "X X"], ["X X", "X X"]];
        int[] armorFirst = [298, 302, 306, 310, 314];
        for (int s = 0; s < 4; s++)
            for (int m = 0; m < 5; m++)
                S(armorFirst[m] + s, 1, armorRows[s], ('X', armorMats[m]));

        // --- CraftingManager body
        S(Items.Paper, 3, ["###"], ('#', It(Items.SugarCane)));
        S(Items.Book, 1, ["#", "#", "#"], ('#', It(Items.Paper)));
        S(Blocks.Fence, 2, ["###", "###"], ('#', It(stick)));
        S(Blocks.FenceGate, 1, ["#W#", "#W#"], ('#', It(stick)), ('W', Bk(planks)));
        S(Blocks.Jukebox, 1, ["###", "#X#", "###"], ('#', Bk(planks)), ('X', It(diamond)));
        S(Blocks.NoteBlock, 1, ["###", "#X#", "###"], ('#', Bk(planks)), ('X', It(redstone)));
        S(Blocks.Bookshelf, 1, ["###", "XXX", "###"], ('#', Bk(planks)), ('X', It(Items.Book)));
        S(Blocks.SnowBlock, 1, ["##", "##"], ('#', It(Items.Snowball)));
        S(Blocks.Clay, 1, ["##", "##"], ('#', It(Items.ClayBall)));
        S(Blocks.Bricks, 1, ["##", "##"], ('#', It(Items.Brick)));
        S(Blocks.Glowstone, 1, ["##", "##"], ('#', It(Items.GlowstoneDust)));
        S(Blocks.Wool, 1, ["##", "##"], ('#', It(str)));
        S(Blocks.Tnt, 1, ["X#X", "#X#", "X#X"], ('X', It(Items.Gunpowder)), ('#', Bk(Blocks.Sand)));
        SD(Blocks.Slab, 3, 3, ["###"], ('#', Bk(cobble)));
        SD(Blocks.Slab, 3, 0, ["###"], ('#', Bk(Blocks.Stone)));
        SD(Blocks.Slab, 3, 1, ["###"], ('#', Bk(Blocks.Sandstone)));
        SD(Blocks.Slab, 3, 2, ["###"], ('#', Bk(planks)));
        SD(Blocks.Slab, 3, 4, ["###"], ('#', Bk(Blocks.Bricks)));
        SD(Blocks.Slab, 3, 5, ["###"], ('#', Bk(Blocks.StoneBricks)));
        S(Blocks.Ladder, 2, ["# #", "###", "# #"], ('#', It(stick)));
        S(Items.WoodenDoor, 1, ["##", "##", "##"], ('#', Bk(planks)));
        S(Blocks.Trapdoor, 2, ["###", "###"], ('#', Bk(planks)));
        S(Items.IronDoor, 1, ["##", "##", "##"], ('#', It(iron)));
        S(Items.Sign, 1, ["###", "###", " X "], ('#', Bk(planks)), ('X', It(stick)));
        S(Items.Cake, 1, ["AAA", "BEB", "CCC"], ('A', It(Items.MilkBucket)), ('B', It(Items.Sugar)), ('C', It(Items.Wheat)), ('E', It(Items.Egg)));
        S(Items.Sugar, 1, ["#"], ('#', It(Items.SugarCane)));
        S(planks, 4, ["#"], ('#', Bk(Blocks.Log)));
        S(stick, 4, ["#", "#"], ('#', Bk(planks)));
        S(Blocks.Torch, 4, ["X", "#"], ('X', It(Items.Coal)), ('#', It(stick)));
        S(Blocks.Torch, 4, ["X", "#"], ('X', It(Items.Coal, 1)), ('#', It(stick)));
        S(Items.Bowl, 4, ["# #", " # "], ('#', Bk(planks)));
        S(Blocks.Rail, 16, ["X X", "X#X", "X X"], ('X', It(iron)), ('#', It(stick)));
        S(Blocks.PoweredRail, 6, ["X X", "X#X", "XRX"], ('X', It(gold)), ('R', It(redstone)), ('#', It(stick)));
        S(Blocks.DetectorRail, 6, ["X X", "X#X", "XRX"], ('X', It(iron)), ('R', It(redstone)), ('#', Bk(Blocks.StonePressurePlate)));
        S(Items.Minecart, 1, ["# #", "###"], ('#', It(iron)));
        S(Blocks.JackOLantern, 1, ["A", "B"], ('A', Bk(Blocks.Pumpkin)), ('B', Bk(Blocks.Torch)));
        S(Items.ChestMinecart, 1, ["A", "B"], ('A', Bk(Blocks.Chest)), ('B', It(Items.Minecart)));
        S(Items.FurnaceMinecart, 1, ["A", "B"], ('A', Bk(Blocks.Furnace)), ('B', It(Items.Minecart)));
        S(Items.Boat, 1, ["# #", "###"], ('#', Bk(planks)));
        S(Items.Bucket, 1, ["# #", " # "], ('#', It(iron)));
        S(Items.FlintAndSteel, 1, ["A ", " B"], ('A', It(iron)), ('B', It(Items.Flint)));
        S(Items.Bread, 1, ["###"], ('#', It(Items.Wheat)));
        S(Blocks.WoodenStairs, 4, ["#  ", "## ", "###"], ('#', Bk(planks)));
        S(Items.FishingRod, 1, ["  #", " #X", "# X"], ('#', It(stick)), ('X', It(str)));
        S(Blocks.CobblestoneStairs, 4, ["#  ", "## ", "###"], ('#', Bk(cobble)));
        S(Blocks.BrickStairs, 4, ["#  ", "## ", "###"], ('#', Bk(Blocks.Bricks)));
        S(Blocks.StoneBrickStairs, 4, ["#  ", "## ", "###"], ('#', Bk(Blocks.StoneBricks)));
        S(Items.Painting, 1, ["###", "#X#", "###"], ('#', It(stick)), ('X', Bk(Blocks.Wool)));
        S(Items.GoldenApple, 1, ["###", "#X#", "###"], ('#', Bk(Blocks.GoldBlock)), ('X', It(Items.Apple)));
        S(Blocks.Lever, 1, ["X", "#"], ('#', Bk(cobble)), ('X', It(stick)));
        S(Blocks.RedstoneTorch, 1, ["X", "#"], ('#', It(stick)), ('X', It(redstone)));
        S(Items.Repeater, 1, ["#X#", "III"], ('#', Bk(Blocks.RedstoneTorch)), ('X', It(redstone)), ('I', Bk(Blocks.Stone)));
        S(Items.Clock, 1, [" # ", "#X#", " # "], ('#', It(gold)), ('X', It(redstone)));
        S(Items.Compass, 1, [" # ", "#X#", " # "], ('#', It(iron)), ('X', It(redstone)));
        S(Items.Map, 1, ["###", "#X#", "###"], ('#', It(Items.Paper)), ('X', It(Items.Compass)));
        S(Blocks.StoneButton, 1, ["#", "#"], ('#', Bk(Blocks.Stone)));
        S(Blocks.StonePressurePlate, 1, ["##"], ('#', Bk(Blocks.Stone)));
        S(Blocks.WoodenPressurePlate, 1, ["##"], ('#', Bk(planks)));
        S(Blocks.Dispenser, 1, ["###", "#X#", "#R#"], ('#', Bk(cobble)), ('X', It(Items.Bow)), ('R', It(redstone)));
        S(Blocks.Piston, 1, ["TTT", "#X#", "#R#"], ('#', Bk(cobble)), ('X', It(iron)), ('R', It(redstone)), ('T', Bk(planks)));
        S(Blocks.StickyPiston, 1, ["S", "P"], ('S', It(Items.Slimeball)), ('P', Bk(Blocks.Piston)));
        S(Items.Bed, 1, ["###", "XXX"], ('#', Bk(Blocks.Wool)), ('X', Bk(planks)));

        // --- RecipesDyes (shapeless; block/item arguments mean metadata 0)
        for (int dye = 0; dye < 16; dye++)
            l.Add(Shapeless(Blocks.Wool, 1, ~dye & 15, It(Items.Dye, dye), It(Blocks.Wool, 0)));
        Ingredient D(int m) => It(Items.Dye, m);
        l.Add(Shapeless(Items.Dye, 2, 11, It(Blocks.Dandelion)));
        l.Add(Shapeless(Items.Dye, 2, 1, It(Blocks.Rose)));
        l.Add(Shapeless(Items.Dye, 3, 15, It(Items.Bone)));
        l.Add(Shapeless(Items.Dye, 2, 9, D(1), D(15)));
        l.Add(Shapeless(Items.Dye, 2, 14, D(1), D(11)));
        l.Add(Shapeless(Items.Dye, 2, 10, D(2), D(15)));
        l.Add(Shapeless(Items.Dye, 2, 8, D(0), D(15)));
        l.Add(Shapeless(Items.Dye, 2, 7, D(8), D(15)));
        l.Add(Shapeless(Items.Dye, 3, 7, D(0), D(15), D(15)));
        l.Add(Shapeless(Items.Dye, 2, 12, D(4), D(15)));
        l.Add(Shapeless(Items.Dye, 2, 6, D(4), D(2)));
        l.Add(Shapeless(Items.Dye, 2, 5, D(4), D(1)));
        l.Add(Shapeless(Items.Dye, 2, 13, D(5), D(9)));
        l.Add(Shapeless(Items.Dye, 3, 13, D(4), D(1), D(9)));
        l.Add(Shapeless(Items.Dye, 4, 13, D(4), D(1), D(1), D(15)));
        return l.ToArray();
    }

    /// <summary>True for "unpack a storage block into 9" recipes (planners should prefer other routes).</summary>
    public static bool IsUnpack(Recipe r) => Versioning.GameData.IsBeta(out var d) ? IsUnpackB(r) : d.IsUnpack(r);

    internal static bool IsUnpackB(Recipe r) =>
        r.OutputCount == 9 && r.Ingredients.Count == 1 && r.Ingredients[0].Id is Blocks.GoldBlock or Blocks.IronBlock or Blocks.DiamondBlock or Blocks.LapisBlock;
}
