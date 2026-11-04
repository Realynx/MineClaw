using MineClaw.Data.Versioning;

namespace MineClaw.Hive.Story;

/// <summary>
/// Material families for bills (docs/web/story-mode.md "Any-variant recipes"). Fox: "a bill item for just making wood planks out of any
/// logs … then sticks out of any type of planks … anything that follows this pattern should repeat it." Vanilla already takes any
/// planks for sticks, tables, chests and wooden tools, and coal or charcoal for torches (a wildcard input): those recipes are labelled
/// "(from any planks)" and flagged <c>any</c>. Where vanilla has one recipe per variant of an input family, they become one "any" bill:
/// <list type="bullet">
/// <item>the outputs differ by the input's variant (planks per wood): <c>craft:planks_any</c>, "Planks (from any logs)": each craft
/// takes whatever log is stocked and makes that wood's planks; its stock counts every plank variant;</item>
/// <item>the output is the same (charcoal from log or log2): <c>smelt:charcoal_any</c>, "Charcoal (from any logs)".</item>
/// </list>
/// Families are derived from the version's recipes: a group of recipes for one output item whose single ingredient is from one
/// material family (logs, sand, coal). Mixed families (slabs from stone, sandstone, bricks…) never merge.
/// </summary>
public static class StoryRecipeFamilies
{
    /// <summary>Item ids of one material, any variant: logs (log, log2), planks, sand, coal (coal, charcoal).</summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<int>> Members = new Dictionary<string, IReadOnlyList<int>>
    {
        ["logs"] = [17, 162], ["planks"] = [5], ["sand"] = [12], ["coal"] = [263],
    };

    /// <summary>The family an item belongs to, or null.</summary>
    public static string? FamilyOf(int id) => Members.FirstOrDefault(kv => kv.Value.Contains(id)).Key;

    /// <summary>Where a family's raw material comes from (a missing ingredient's hint), or null.</summary>
    public static string? Source(int id) => SourceById.GetValueOrDefault(id);

    private static readonly (string Hint, int[] Ids)[] SourceHints =
    [
        ("chop trees", [17, 162, 5, 280]),
        ("mine stone", [1, 4]),
        ("dig sand", [12]),
        ("mine coal or smelt charcoal", [263]),
        ("mine ore", [14, 15, 16, 21, 56, 73, 129, 153, 265, 266, 264, 331, 351]),
    ];

    private static readonly Dictionary<int, string> SourceById = SourceHints.SelectMany(h => h.Ids.Select(id => (Id: id, h.Hint))).ToDictionary(x => x.Id, x => x.Hint);

    /// <summary>A recipe with a wildcard input of a family ("planks", any meta) is an "any" recipe: label suffix and flag.</summary>
    internal static StoryRecipe Mark(StoryRecipe r)
    {
        var fam = r.Inputs.Where(i => i.Options.All(o => o.Meta < 0) && i.Options.Select(o => FamilyOf(o.Id)).Distinct().Count() == 1)
            .Select(i => FamilyOf(i.Options[0].Id)).FirstOrDefault(f => f != null);
        return fam == null ? r : r with { Label = r.Label + $" (from any {fam})", Any = true, Family = fam };
    }

    /// <summary>The version's "any" bills from per-variant recipe groups (crafting and smelting), listed before the exact ones.</summary>
    internal static List<StoryRecipe> Build(IGameData d, Func<int, int, string> name, Func<int, int, string> label)
    {
        var list = new List<StoryRecipe>();
        AddAnyCrafts(d, name, label, list);
        AddAnySmelts(d, name, label, list);
        return list;
    }

    /// <summary>Crafting: one output item, each recipe one ingredient type from one family (planks from log:0..3, log2:0..1).</summary>
    private static void AddAnyCrafts(IGameData d, Func<int, int, string> name, Func<int, int, string> label, List<StoryRecipe> list)
    {
        foreach (var grp in d.AllRecipes.Where(r => r.Totals.Count == 1).GroupBy(r => r.OutputId))
        {
            var rs = grp.ToList();
            if (UniformFamily(rs) is not { } fam) continue;
            int id = grp.Key;
            var options = rs.Select(r => new ItemKey(r.Totals[0].Ingredient.Id, r.Totals[0].Ingredient.Damage)).Distinct().ToList();
            var outName = d.ItemExists(id) ? d.Item(id).Name : name(id, 0);
            list.Add(new StoryRecipe($"craft:{outName}_any", $"Make {label(id, -1)} (from any {fam})", "crafting_table", WorkTypes.Craft,
                new Stack(id, 0, rs[0].OutputCount), [new RecipeInput(options, rs[0].Totals[0].Count)], false)
            {
                ToolItem = outName, Any = true, Family = fam, AnyOutput = rs.Select(r => r.OutputDamage).Distinct().Count() > 1,
            });
        }
    }

    /// <summary>The family of a group of recipes that differ only in their ingredient's variant (two or more, one family, the same
    /// output and ingredient counts), else null.</summary>
    private static string? UniformFamily(List<MineClaw.Data.Recipe> rs)
    {
        var fams = rs.Select(r => FamilyOf(r.Totals[0].Ingredient.Id)).Distinct().ToList();
        if (rs.Count < 2 || fams.Count != 1 || fams[0] is not { } fam || rs.Select(r => r.OutputCount).Distinct().Count() != 1
            || rs.Select(r => r.Totals[0].Count).Distinct().Count() != 1) return null;
        return fam;
    }

    /// <summary>Smelting: one output (item and meta) from several inputs of one family (charcoal from log and log2).</summary>
    private static void AddAnySmelts(IGameData d, Func<int, int, string> name, Func<int, int, string> label, List<StoryRecipe> list)
    {
        foreach (var grp in d.AllSmelting.GroupBy(s => (s.OutputId, s.OutputDamage)))
        {
            var ss = grp.ToList();
            var fams = ss.Select(s => FamilyOf(s.InputId)).Distinct().ToList();
            if (ss.Count < 2 || fams.Count != 1 || fams[0] is not { } fam) continue;
            var (oid, odmg) = grp.Key;
            bool food = d.Food(oid, odmg) != null;
            var outName = name(oid, odmg);
            list.Add(new StoryRecipe($"smelt:{outName}_any", $"{(food ? "Cook" : "Smelt")} {label(oid, odmg)} (from any {fam})", "furnace",
                food ? WorkTypes.Cook : WorkTypes.Craft, new Stack(oid, Math.Max(0, odmg), ss[0].OutputCount),
                [new RecipeInput(ss.Select(s => new ItemKey(s.InputId, s.InputDamage)).Distinct().ToList(), 1)], true)
            {
                ToolItem = name(ss[0].InputId, Math.Max(0, ss[0].InputDamage)), Any = true, Family = fam,
            });
        }
    }
}
