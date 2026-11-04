using System.Text.Json.Nodes;
using MineClaw.Data.Versioning;

namespace MineClaw.Hive.Story;

/// <summary>One option-list ingredient of a recipe (any of these items, this many per craft).</summary>
public sealed record RecipeInput(IReadOnlyList<ItemKey> Options, int Count);

/// <summary>A bill's recipe at a workbench (docs/web/story-mode.md "Workbenches &amp; bills").</summary>
public sealed record StoryRecipe(string Id, string Label, string Workbench, string WorkType, Stack Output, IReadOnlyList<RecipeInput> Inputs, bool Fuel)
{
    /// <summary>The name the craft/smelt tool takes: the output (craft) or the input (smelt).</summary>
    public string ToolItem { get; init; } = "";

    /// <summary>A station recipe (brewing stand, enchanting table, anvil): what it asks of the pawn and the bench. Null for crafting and
    /// smelting.</summary>
    public StationSpec? Station { get; init; }

    /// <summary>Takes any variant of a material family (planks, logs, sand, coal): StoryRecipeFamilies.</summary>
    public bool Any { get; init; }
    /// <summary>The family its "any" input is (logs, planks, sand, coal), or null.</summary>
    public string? Family { get; init; }
    /// <summary>The output's variant follows the input's (planks of the log's wood): its stock counts every variant.</summary>
    public bool AnyOutput { get; init; }

    public JsonObject Json()
    {
        var json = new JsonObject
        {
            ["id"] = Id, ["label"] = Label, ["workbench"] = Workbench, ["work_type"] = WorkType, ["output"] = Output.Json(),
            ["any"] = Any, ["family"] = Family, ["any_output"] = AnyOutput,
            ["inputs"] = new JsonArray(Inputs.Select(i => (JsonNode)new JsonObject
            {
                ["options"] = new JsonArray(i.Options.Select(o => (JsonNode)o.Json()).ToArray()), ["count"] = i.Count,
            }).ToArray()),
            ["fuel"] = Fuel,
        };
        if (Station is { } s)
        {
            json["requires"] = new JsonObject
            {
                ["xp_levels"] = s.XpLevels, ["lapis"] = s.Lapis, ["fuel_item"] = s.FuelItem, ["water"] = s.Water, ["note"] = s.Note,
            };
            if (s.Chain is { Count: > 0 } chain) json["chain"] = new JsonArray(chain.Select(c => (JsonNode)c!).ToArray());
            if (s.Enchantments is { Count: > 0 } ench) json["enchantments"] = new JsonArray(ench.Select(e => (JsonNode)e!).ToArray());
            json["modes"] = new JsonArray(s.Modes.Select(m => (JsonNode)m!).ToArray());
        }
        return json;
    }
}

/// <param name="Kind">brew | enchant | repair | combine | book.</param>
/// <param name="Item">The item a brew makes (potion, splash_potion, lingering_potion) or the item an enchant / anvil job works on.</param>
/// <param name="Potion">A brew's potion type (swiftness, long_swiftness...).</param>
/// <param name="XpLevels">The least XP levels a pawn needs (the offer's level or the anvil's price can be more: learned per bill).</param>
/// <param name="Lapis">Lapis per enchant (1–3: the bill's option); null otherwise.</param>
/// <param name="FuelItem">blaze_powder for a brew.</param>
/// <param name="Water">A brew from water bottles fills glass bottles at a water source or cauldron near the stand.</param>
/// <param name="Chain">A brew's steps from water bottles ("Water Bottle + Nether Wart → Awkward Potion", ...).</param>
/// <param name="Enchantments">What an enchant can prefer: the table's enchantments for the item.</param>
/// <param name="Modes">The bill modes it takes (an enchanted or repaired item can't be told apart in storage: no "until").</param>
public sealed record StationSpec(string Kind, string Item, string? Potion = null, int XpLevels = 0, int? Lapis = null, string? FuelItem = null,
    bool Water = false, IReadOnlyList<string>? Chain = null, IReadOnlyList<string>? Enchantments = null, string? Note = null)
{
    public IReadOnlyList<string> Modes { get; init; } = ["count", "until", "forever"];
}

/// <summary>
/// The recipes each workbench kind offers, per game version, from the bot's recipe data: the crafting table every crafting
/// recipe (one entry per distinct output: the craft tool picks the pattern), the furnace every smelting recipe (food → Cook, the rest
/// Craft), and on 1.12.2 the brewing stand (every potion, splash and lingering potion a chain from water bottles makes: Cook), the
/// enchanting table (every item the table takes: Craft) and the anvil (repair with the material, combine two, apply a book: Craft).
/// b1.8.1 has none of those three blocks.
/// </summary>
public static class StoryRecipes
{
    public static readonly IReadOnlyList<string> Benches = ["crafting_table", "furnace", "brewing_stand", "anvil", "enchanting_table"];
    /// <summary>Workbench kinds whose bills a pawn can run on every version.</summary>
    public static readonly IReadOnlyList<string> Supported = ["crafting_table", "furnace"];
    /// <summary>The 1.12.2 stations (b1.8.1 has no brewing stand, anvil or enchanting table).</summary>
    public static readonly IReadOnlyList<string> Stations = ["brewing_stand", "anvil", "enchanting_table"];

    /// <summary>Does this version's colony take bills at a <paramref name="bench"/>?</summary>
    public static bool SupportedIn(IGameData d, string bench) => Supported.Contains(bench) || Stations.Contains(bench) && d.BlockExists(117) && d.ItemExists(373);

    /// <summary>
    /// The other blocks a player can open or use, and why they take no bills (docs/web/story-mode.md "Interactive blocks"): bills are
    /// for stations that turn inputs into a product with a pawn's work.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> NotBenches = new Dictionary<string, string>
    {
        ["chest"] = "storage (chests take storage settings, not bills)",
        ["hopper"] = "moves items by itself; no bills (a \"fill with X\" order may come later)",
        ["dropper"] = "a container fired by redstone; no bills (a \"fill with X\" order may come later)",
        ["dispenser"] = "a container fired by redstone; no bills (a \"fill with X\" order may come later)",
        ["cauldron"] = "a water source for brewing: a brew fills its glass bottles at it (a level each)",
        ["beacon"] = "a one-time setup (pyramid and payment), not repeated work; no bills",
        ["jukebox"] = "plays a disc; no bills",
        ["noteblock"] = "plays a note; no bills",
    };

    public static string? BenchOf(int blockId) => blockId switch
    {
        58 => "crafting_table", 61 or 62 => "furnace", 117 => "brewing_stand", 145 => "anvil", 116 => "enchanting_table", _ => null,
    };

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, IReadOnlyList<StoryRecipe>> Cache = new();

    public static IReadOnlyList<StoryRecipe> For(IGameData d, string? bench = null)
    {
        var all = Cache.GetOrAdd(d.VersionId, _ => Build(d));
        return bench == null ? all : all.Where(r => r.Workbench == bench).ToList();
    }

    public static StoryRecipe? Get(IGameData d, string id) => For(d).FirstOrDefault(r => r.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    private static string Name(IGameData d, int id, int meta) =>
        meta > 0 ? d.VariantName(id, meta) is { Length: > 0 } v ? v : d.Item(id).Name : d.ItemExists(id) ? d.Item(id).Name : d.Block(id).Name;

    private static readonly HashSet<string> SmallWords = new(StringComparer.OrdinalIgnoreCase) { "of", "and", "with", "on", "a", "the", "into" };

    /// <summary>The item's display name in title case ("Light gray Wool" → "Light Gray Wool"); any-variant inputs (damage -1) take the
    /// generic name ("Wood", not "Oak Wood").</summary>
    internal static string Label(IGameData d, int id, int damage)
    {
        string raw = damage < 0 && d.ItemExists(id) ? d.Item(id).DisplayName : d.DisplayName(id, Math.Max(0, damage));
        var words = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < words.Length; i++)
        {
            var w = words[i];
            if (i > 0 && SmallWords.Contains(w)) words[i] = w.ToLowerInvariant();
            else if (w.Length > 0 && char.IsLower(w[0])) words[i] = char.ToUpperInvariant(w[0]) + w[1..];
        }
        return string.Join(' ', words);
    }

    private static IReadOnlyList<StoryRecipe> Build(IGameData d)
    {
        var list = new List<StoryRecipe>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in d.AllRecipes)
        {
            var name = Name(d, r.OutputId, r.OutputDamage);
            var id = "craft:" + name;
            if (!seen.Add(id)) continue;
            var inputs = r.Totals.Select(t => new RecipeInput([new ItemKey(t.Ingredient.Id, t.Ingredient.Damage)], t.Count)).ToList();
            list.Add(StoryRecipeFamilies.Mark(new StoryRecipe(id, $"Make {Label(d, r.OutputId, r.OutputDamage)}", "crafting_table", WorkTypes.Craft,
                new Stack(r.OutputId, Math.Max(0, r.OutputDamage), r.OutputCount), inputs, false) { ToolItem = name }));
        }
        foreach (var s in d.AllSmelting)
        {
            var name = Name(d, s.InputId, s.InputDamage);
            var id = "smelt:" + name;
            if (!seen.Add(id)) continue;
            bool food = d.Food(s.OutputId, s.OutputDamage) != null;
            list.Add(StoryRecipeFamilies.Mark(new StoryRecipe(id, $"{(food ? "Cook" : "Smelt")} {Label(d, s.InputId, s.InputDamage)} → {Label(d, s.OutputId, s.OutputDamage)}", "furnace",
                food ? WorkTypes.Cook : WorkTypes.Craft, new Stack(s.OutputId, Math.Max(0, s.OutputDamage), s.OutputCount),
                [new RecipeInput([new ItemKey(s.InputId, s.InputDamage)], 1)], true) { ToolItem = name }));
        }
        // The "any" bills (planks from any logs, charcoal from any logs) come first in their bench's list (StoryRecipeFamilies).
        list.InsertRange(0, StoryRecipeFamilies.Build(d, (i, m) => Name(d, i, m), (i, m) => Label(d, i, m)).Where(f => seen.Add(f.Id)));
        if (SupportedIn(d, "brewing_stand"))
        {
            list.AddRange(StoryBrewing.Recipes(d));
            list.AddRange(StoryEnchanting.Recipes(d));
            list.AddRange(StoryAnvil.Recipes(d));
        }
        return list;
    }

    /// <summary>Fuel by preference: coal (and charcoal), planks, logs → (item, items per smelted item).</summary>
    public static readonly IReadOnlyList<(int Id, double PerItem)> Fuels = [(263, 1 / 8.0), (5, 1 / 1.5), (17, 1 / 1.5), (162, 1 / 1.5)];

    /// <summary>Seconds a furnace takes per item (both versions: 200 ticks).</summary>
    public const double SmeltSeconds = 10;
}
