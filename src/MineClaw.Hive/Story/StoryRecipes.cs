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

    public JsonObject Json() => new()
    {
        ["id"] = Id, ["label"] = Label, ["workbench"] = Workbench, ["work_type"] = WorkType, ["output"] = Output.Json(),
        ["inputs"] = new JsonArray(Inputs.Select(i => (JsonNode)new JsonObject
        {
            ["options"] = new JsonArray(i.Options.Select(o => (JsonNode)o.Json()).ToArray()), ["count"] = i.Count,
        }).ToArray()),
        ["fuel"] = Fuel,
    };
}

/// <summary>
/// The recipes each workbench kind offers, per game version, from the bot's recipe data: the crafting table every crafting
/// recipe (one entry per distinct output: the craft tool picks the pattern), the furnace every smelting recipe (food → Cook, the rest
/// Craft). Brewing stands, anvils and enchanting tables have no primitive yet: they list no recipes.
/// </summary>
public static class StoryRecipes
{
    public static readonly IReadOnlyList<string> Benches = ["crafting_table", "furnace", "brewing_stand", "anvil", "enchanting_table"];
    /// <summary>Workbench kinds whose bills a pawn can run with today's primitives.</summary>
    public static readonly IReadOnlyList<string> Supported = ["crafting_table", "furnace"];

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
            list.Add(new StoryRecipe(id, $"Make {Label(d, r.OutputId, r.OutputDamage)}", "crafting_table", WorkTypes.Craft,
                new Stack(r.OutputId, Math.Max(0, r.OutputDamage), r.OutputCount), inputs, false) { ToolItem = name });
        }
        foreach (var s in d.AllSmelting)
        {
            var name = Name(d, s.InputId, s.InputDamage);
            var id = "smelt:" + name;
            if (!seen.Add(id)) continue;
            bool food = d.Food(s.OutputId, s.OutputDamage) != null;
            list.Add(new StoryRecipe(id, $"{(food ? "Cook" : "Smelt")} {Label(d, s.InputId, s.InputDamage)} → {Label(d, s.OutputId, s.OutputDamage)}", "furnace",
                food ? WorkTypes.Cook : WorkTypes.Craft, new Stack(s.OutputId, Math.Max(0, s.OutputDamage), s.OutputCount),
                [new RecipeInput([new ItemKey(s.InputId, s.InputDamage)], 1)], true) { ToolItem = name });
        }
        return list;
    }

    /// <summary>Fuel by preference: coal (and charcoal), planks, logs → (item, items per smelted item).</summary>
    public static readonly IReadOnlyList<(int Id, double PerItem)> Fuels = [(263, 1 / 8.0), (5, 1 / 1.5), (17, 1 / 1.5), (162, 1 / 1.5)];

    /// <summary>Seconds a furnace takes per item (both versions: 200 ticks).</summary>
    public const double SmeltSeconds = 10;
}
