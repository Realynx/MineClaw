using System.Globalization;
using System.Text;
using MineClaw.Data;

namespace MineClaw.Agent.Rag.Raptor;

/// <summary>
/// Turns the static b1.8.1 tables in <c>MineClaw.Data</c> into short (≤ ~100-token), human-readable RAPTOR leaves:
/// every crafting recipe (shape in words, ingredients, output count, 2×2 vs table), smelting recipes and fuel burn
/// times, items (stack size, durability, tool tier, food), blocks (hardness, best tool, harvest level, drops, beta
/// quirks) and dig-time highlights. Docs are kind <see cref="Kind"/>, tag = sub-kind, source "seed"; refs and order are
/// deterministic.
/// </summary>
public static class SeedCorpus
{
    public const string Kind = "gamedata";
    public const string Source = "seed";
    /// <summary>Fixed timestamp for seed docs (deterministic output).</summary>
    public static readonly DateTimeOffset At = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly string[] TierNames = ["wooden", "stone", "iron", "diamond", "gold"];

    public static List<RagDoc> Build()
    {
        var docs = new List<RagDoc>();
        docs.AddRange(RecipeDocs());
        docs.AddRange(SmeltingDocs());
        docs.AddRange(ItemDocs());
        docs.AddRange(BlockDocs());
        docs.AddRange(DigDocs());
        return docs;
    }

    private static RagDoc Doc(string reference, string tag, string title, string body) =>
        new(reference, Kind, title, body) { Tag = tag, Source = Source, At = At };

    private static string F(double v) => v.ToString("0.##", Inv);

    // ======================= crafting =======================

    private static string Name(Ingredient? i) => i is { } x ? x.ToString() : "empty";

    private static IEnumerable<RagDoc> RecipeDocs()
    {
        var counts = new Dictionary<(int, int), int>();
        foreach (var r in Recipes.All)
        {
            var key = (r.OutputId, r.OutputDamage);
            int n = counts[key] = counts.GetValueOrDefault(key) + 1;
            var output = Items.DisplayName(r.OutputId, r.OutputDamage);
            var sb = new StringBuilder();
            sb.Append($"Crafting recipe: {output}. Makes {r.OutputCount} per craft. ");
            sb.Append(r.FitsIn2x2 ? "Fits the 2x2 inventory grid (no crafting table needed). " : "Needs a crafting table (3x3 grid). ");
            sb.Append("Ingredients: ").Append(string.Join(", ", r.Totals.Select(t => $"{t.Count} {t.Ingredient}"))).Append(". ");
            if (r.Shaped)
            {
                var rows = new List<string>();
                for (int y = 0; y < r.Height; y++)
                {
                    var cells = Enumerable.Range(0, r.Width).Select(x => Name(r.Pattern[y * r.Width + x])).ToList();
                    string label = r.Height == 1 ? "one row" : y == 0 ? "top row" : y == r.Height - 1 ? "bottom row" : "middle row";
                    rows.Add($"{label}: {string.Join(", ", cells)}");
                }
                sb.Append($"Shape {r.Width} wide by {r.Height} tall; ").Append(string.Join("; ", rows)).Append('.');
                if (r.Width > 1) sb.Append(" Can be mirrored and placed anywhere in the grid.");
            }
            else sb.Append("Shapeless: put the ingredients anywhere in the grid.");
            var quirk = RecipeQuirk(r);
            if (quirk != null) sb.Append(' ').Append(quirk);
            yield return Doc($"gd:recipe:{r.OutputId}:{r.OutputDamage}:{n}", "recipe", $"recipe {output} x{r.OutputCount}", sb.ToString());
        }
    }

    private static string? RecipeQuirk(Recipe r)
    {
        if (r.OutputId is Blocks.WoodenStairs or Blocks.CobblestoneStairs or Blocks.BrickStairs or Blocks.StoneBrickStairs)
            return "Beta 1.8 stairs give 4 per craft (6 blocks in).";
        if (r.OutputId == Blocks.Slab) return "Beta 1.8 slabs give 3 per craft.";
        if (r.OutputId == Items.GoldenApple) return "Beta golden apples need 8 gold BLOCKS, not ingots.";
        if (r.OutputId == Blocks.StoneButton) return "Beta buttons are 2 stone stacked vertically.";
        if (Recipes.IsUnpack(r)) return "Unpacks a storage block back into 9.";
        return null;
    }

    // ======================= furnace =======================

    private static IEnumerable<RagDoc> SmeltingDocs()
    {
        foreach (var s in Smelting.All)
        {
            var input = Items.Get(s.InputId).DisplayName;
            var output = Items.DisplayName(s.OutputId, s.OutputDamage);
            var body = $"Smelting: {input} smelts into {output} in a furnace ({s.OutputCount} {output} per {input}). " +
                       $"Each item takes {Smelting.CookTicks / 20} s ({Smelting.CookTicks} ticks); any furnace fuel works.";
            yield return Doc($"gd:smelt:{s.InputId}", "smelting", $"smelt {input} -> {output}", body);
        }

        var fuels = Items.All.Where(i => Smelting.IsFuel(i.Id)).OrderByDescending(i => Smelting.FuelTicks(i.Id)).ThenBy(i => i.Id).ToList();
        foreach (var f in fuels)
        {
            int t = Smelting.FuelTicks(f.Id);
            yield return Doc($"gd:fuel:{f.Id}", "fuel", $"fuel {f.DisplayName}",
                $"Furnace fuel: {f.DisplayName} burns {t} ticks ({F(t / 20.0)} s), enough to smelt {F(Smelting.ItemsPerFuel(f.Id))} items.");
        }
        var groups = fuels.GroupBy(f => Smelting.FuelTicks(f.Id)).OrderByDescending(g => g.Key)
            .Select(g => $"{F(g.Key / (double)Smelting.CookTicks)} items per {string.Join("/", g.Take(4).Select(x => x.DisplayName))}{(g.Count() > 4 ? " and other wood blocks" : "")}");
        yield return Doc("gd:fuel:overview", "fuel", "furnace fuels overview",
            "Furnace fuels in beta 1.8.1 (one item smelts in 10 s): " + string.Join("; ", groups) +
            ". Wooden tools, slabs and bowls are not fuel in b1.8.1. Coal or charcoal is the efficient choice; a lava bucket smelts 100 items.");
    }

    // ======================= items =======================

    private static string ToolLine(ItemInfo it)
    {
        if (it.Tool == ToolType.None || it.ToolTier < 0) return "";
        string tier = TierNames[Math.Clamp(it.ToolTier, 0, 4)];
        int level = MineClaw.Data.Tools.HarvestLevel(it.ToolTier);
        string harvest = it.Tool == ToolType.Pickaxe ? PickaxeHarvest(level) : "";
        string speed = it.Tool is ToolType.Pickaxe or ToolType.Shovel or ToolType.Axe ? $" Speed x{F(MineClaw.Data.Tools.Efficiency(it.ToolTier))} on its blocks." : "";
        string gold = it.ToolTier == 4 ? " Gold is the fastest tier but has wood harvest level and very low durability." : "";
        return $" {char.ToUpper(it.Tool.ToString()[0]) + it.Tool.ToString()[1..].ToLowerInvariant()}, {tier} tier (harvest level {level}).{harvest}{speed}{gold}";
    }

    private static string PickaxeHarvest(int level) => level switch
    {
        0 => " Mines stone, cobblestone and coal ore (not iron ore).",
        1 => " Mines iron and lapis ore (not gold, diamond or redstone ore).",
        2 => " Mines gold, diamond and redstone ore (not obsidian).",
        _ => " Mines everything, including obsidian.",
    };

    private static IEnumerable<RagDoc> ItemDocs()
    {
        foreach (var it in Items.All.Where(i => !i.IsBlock).OrderBy(i => i.Id))
            yield return Doc($"gd:item:{it.Id}", "item", $"item {it.DisplayName}", ItemText(it));
    }

    private static string ItemText(ItemInfo it)
    {
        var sb = new StringBuilder($"Item: {it.DisplayName} (id {it.Id}). ");
        sb.Append(it.MaxStack == 1 ? "Does not stack." : $"Stacks to {it.MaxStack}.");
        if (it.MaxDamage > 0) sb.Append($" Durability {it.MaxDamage + 1} uses.");
        sb.Append(ToolLine(it));
        AppendFood(sb, it);
        if (Smelting.IsFuel(it.Id)) sb.Append($" Fuel: smelts {F(Smelting.ItemsPerFuel(it.Id))} items.");
        var made = Recipes.ForOutput(it.Id);
        if (made.Count > 0) sb.Append(" Crafted (").Append(made[0].RequiresCraftingTable ? "table" : "2x2").Append(") from ")
            .Append(string.Join(", ", made[0].Totals.Select(t => $"{t.Count} {t.Ingredient}"))).Append('.');
        var smelted = Smelting.ForOutput(it.Id);
        if (smelted.Count > 0) sb.Append(" Smelted from ").Append(string.Join(", ", smelted.Select(s => Items.Get(s.InputId).DisplayName))).Append('.');
        var sources = Drops.SourcesOf(it.Id).Where(b => b != it.Id).Take(3).Select(b => Blocks.Get(b).DisplayName).ToList();
        if (sources.Count > 0) sb.Append(" Dropped by ").Append(string.Join(", ", sources)).Append('.');
        return sb.ToString();
    }

    private static void AppendFood(StringBuilder sb, ItemInfo it)
    {
        if (Items.Food(it.Id) is not { } food) return;
        sb.Append($" Food: restores {food.Hunger} hunger ({F(food.Hunger / 2.0)} shanks), saturation {F(food.Saturation)}.");
        float poison = Items.FoodPoisonChance(it.Id);
        if (poison > 0) sb.Append($" {poison:P0} chance of food poisoning.");
    }

    // ======================= blocks =======================

    private static readonly Dictionary<int, string> Quirks = new()
    {
        [Blocks.Grass] = "Spreads to nearby dirt in light; breaking it gives dirt.",
        [Blocks.Ice] = "Breaking ice leaves a water source if the block below is solid or liquid; it never drops itself.",
        [Blocks.Glass] = "Drops nothing when broken (no silk touch in b1.8.1).",
        [Blocks.Bookshelf] = "Drops nothing when broken in b1.8.1.",
        [Blocks.Leaves] = "Shears give the leaf block; otherwise a 1 in 20 sapling chance. Leaves decay without a log nearby.",
        [Blocks.Gravel] = "Falls when unsupported; 10% flint chance.",
        [Blocks.Sand] = "Falls when unsupported. Smelts into glass.",
        [Blocks.Cobblestone] = "Generated where flowing lava meets water: a cobblestone generator.",
        [Blocks.Obsidian] = "Forms where flowing water meets a lava source; needs a diamond pickaxe.",
        [Blocks.Stone] = "Drops cobblestone. Also made by smelting cobblestone.",
        [Blocks.Farmland] = "Made by hoeing dirt or grass; trampling turns it back to dirt; needs water within 4 blocks to stay wet.",
        [Blocks.SugarCane] = "Must be planted on dirt, grass or sand next to water; grows up to 3 tall.",
        [Blocks.Cactus] = "Hurts on contact; must be planted on sand with no block beside it.",
        [Blocks.Water] = "Water source; a bucket picks it up. Two sources with one empty cell between them (or a 2x2 pool) make an infinite water source.",
        [Blocks.Lava] = "Lava source; sets nearby flammable blocks alight; a bucket picks it up.",
        [Blocks.Web] = "Shears or a sword break it fast and it drops string.",
        [Blocks.MonsterEgg] = "Silverfish stone: spawns a silverfish when broken.",
        [Blocks.TallGrass] = "Drops seeds 1 in 8 times; shears give the tall grass itself.",
    };

    private static IEnumerable<RagDoc> BlockDocs()
    {
        foreach (var b in Blocks.All.Where(b => b.Id > 0).OrderBy(b => b.Id))
            yield return Doc($"gd:block:{b.Id}", "block", $"block {b.DisplayName}", BlockText(b));
    }

    private static string BlockText(BlockInfo b)
    {
        var sb = new StringBuilder($"Block: {b.DisplayName} (id {b.Id}). ");
        sb.Append(b.Hardness < 0 ? "Unbreakable." : b.Hardness == 0 ? "Breaks instantly." : $"Hardness {F(b.Hardness)}.");
        if (b.Liquid) sb.Append(" Liquid.");
        if (b.BestTool != ToolType.None)
            sb.Append($" Best tool: {b.BestTool.ToString().ToLowerInvariant()}.");
        if (MineClaw.Data.Tools.RequiresTool(b.Id)) sb.Append(HarvestNeed(b));
        if (b.Hardness >= 0 && !b.Liquid) sb.Append(DropsText(b));
        AppendBlockTraits(sb, b);
        return sb.ToString();
    }

    private static string HarvestNeed(BlockInfo b)
    {
        var tool = b.BestTool == ToolType.None ? "pickaxe" : b.BestTool.ToString().ToLowerInvariant();
        return b.MinHarvestLevel > 0
            ? $" Needs a {TierNames[b.MinHarvestLevel]} {tool} or better to drop anything."
            : $" Drops nothing without a {tool}.";
    }

    private static string DropsText(BlockInfo b)
    {
        int tool = BestHarvester(b);
        var drops = Drops.For(b.Id, 0, tool);
        return drops.Count == 0 ? " Drops nothing." : " Drops " + string.Join(", ", drops.Select(d => d.ToString())) + ".";
    }

    private static void AppendBlockTraits(StringBuilder sb, BlockInfo b)
    {
        if (b.Hazard) sb.Append(" Hazard: damages players.");
        if (b.Interactable) sb.Append(" Right-click to use.");
        if (!b.Solid && !b.Liquid) sb.Append(" Not solid (walk through).");
        if (Quirks.TryGetValue(b.Id, out var q)) sb.Append(' ').Append(q);
    }

    private static int BestHarvester(BlockInfo b) =>
        MineClaw.Data.Tools.CanHarvest(b.Id, -1) ? -1
        : b.Id == Blocks.Web ? Items.Shears
        : b.BestTool == ToolType.Shovel || b.Id is Blocks.SnowLayer or Blocks.SnowBlock ? Items.DiamondShovel
        : Items.DiamondPickaxe;

    // ======================= dig times =======================

    private static readonly int[] DigBlocks =
    [
        Blocks.Dirt, Blocks.Grass, Blocks.Sand, Blocks.Gravel, Blocks.Clay, Blocks.Stone, Blocks.Cobblestone, Blocks.CoalOre,
        Blocks.IronOre, Blocks.GoldOre, Blocks.DiamondOre, Blocks.Obsidian, Blocks.Log, Blocks.Planks, Blocks.Leaves, Blocks.Wool,
        Blocks.Glass, Blocks.Sandstone, Blocks.Netherrack, Blocks.SnowBlock, Blocks.Ice, Blocks.Pumpkin, Blocks.Melon, Blocks.Chest,
        Blocks.CraftingTable, Blocks.Furnace,
    ];

    private static readonly (ToolType Type, int[] Ids)[] ToolsByType =
    [
        (ToolType.Pickaxe, [270, 274, 257, 278, 285]),
        (ToolType.Shovel, [269, 273, 256, 277, 284]),
        (ToolType.Axe, [271, 275, 258, 279, 286]),
    ];

    private static string Secs(double s) => double.IsInfinity(s) ? "never" : s == 0 ? "instant" : F(s) + "s";

    private static IEnumerable<RagDoc> DigDocs()
    {
        foreach (var id in DigBlocks.Where(Blocks.Exists))
        {
            var b = Blocks.Get(id);
            yield return Doc($"gd:dig:{id}", "digtime", $"dig time {b.DisplayName}", DigText(id, b));
        }
        yield return Doc("gd:dig:tiers", "digtime", "tool tiers",
            "Tool tiers in beta 1.8.1: wooden (speed x2, 60 uses), stone (x4, 132 uses), iron (x6, 251 uses), diamond (x8, 1562 uses), " +
            "gold (x12, 33 uses; harvests like wood). Speed only applies to the tool's own blocks: pickaxe for rock and ores, shovel for dirt, " +
            "sand, gravel, clay and snow, axe for logs, planks, chests and bookshelves. Swords cut cobwebs; shears cut leaves, wool and webs.");
    }

    private static string DigText(int id, BlockInfo b)
    {
        var sb = new StringBuilder($"Dig times for {b.DisplayName}: by hand {Secs(MineClaw.Data.Tools.DigSeconds(id, -1))}");
        if (!MineClaw.Data.Tools.CanHarvest(id, -1)) sb.Append(" (drops nothing by hand)");
        int bestTicks = MineClaw.Data.Tools.CanHarvest(id, -1) ? MineClaw.Data.Tools.DigTicks(id, -1) : int.MaxValue;
        var best = new List<string> { "bare hand" };
        foreach (var (type, ids) in ToolsByType) AppendToolTimes(sb, id, b, type, ids, ref bestTicks, ref best);
        sb.Append($". Fastest tool for {b.DisplayName}: {string.Join(" or ", best)}");
        if (bestTicks != int.MaxValue) sb.Append($" ({Secs(bestTicks / 20.0)})");
        sb.Append(best.Any(n => n.StartsWith("Gold", StringComparison.Ordinal)) ? "; gold is fast but wears out quickly." : ".");
        if (b.BestTool != ToolType.None) sb.Append($" Right tool type: {b.BestTool.ToString().ToLowerInvariant()}.");
        return sb.ToString();
    }

    /// <summary>One tool type's dig times (skipped when no tier beats the hand and it isn't the block's tool); tracks the fastest tools.</summary>
    private static void AppendToolTimes(StringBuilder sb, int id, BlockInfo b, ToolType type, int[] ids, ref int bestTicks, ref List<string> best)
    {
        double plain = MineClaw.Data.Tools.DigSeconds(id, -1);
        var times = ids.Select(t => (Tool: Items.Get(t), S: MineClaw.Data.Tools.DigSeconds(id, t))).ToList();
        if (times.All(t => Math.Abs(t.S - plain) < 1e-9) && type != b.BestTool) return;
        sb.Append($"; {type.ToString().ToLowerInvariant()}: ").Append(string.Join(", ",
            times.Select(t => $"{TierNames[t.Tool.ToolTier]} {Secs(t.S)}{(MineClaw.Data.Tools.CanHarvest(id, t.Tool.Id) ? "" : " no drop")}")));
        foreach (var t in times.Where(t => MineClaw.Data.Tools.CanHarvest(id, t.Tool.Id)))
        {
            int ticks = MineClaw.Data.Tools.DigTicks(id, t.Tool.Id);
            if (ticks < bestTicks) { bestTicks = ticks; best = [t.Tool.DisplayName]; }
            else if (ticks == bestTicks && best[0] != "bare hand" && !best.Contains(t.Tool.DisplayName)) best.Add(t.Tool.DisplayName);
        }
    }
}
