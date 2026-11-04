using MineClaw.Data;

namespace MineClaw.Agent.Rag.Beliefs;

/// <summary>A seed/jar verdict: +1 true, -1 false. <see cref="Ref"/> is the matching seed doc ref (gd:*).</summary>
public sealed record SeedVerdict(int Polarity, string Ref, string Reason);

/// <summary>
/// Checks recipe/mechanic claims against the b1.8.1 tables in <c>MineClaw.Data</c> (the same data the seed corpus is built
/// from: crafting, smelting, block drops). Zero tokens. Conservative: it only refutes when the output resolves, every input
/// resolves, no liquid is involved (cobble/obsidian are mechanics, not recipes), the seed knows at least one way to make the
/// output, and none of them matches.
/// </summary>
public static class SeedOracle
{
    public static SeedVerdict? Check(RecipeClaim claim)
    {
        if (Resolve(claim) is not { } resolved) return null;
        var (output, inputs) = resolved;
        int outDmg = output.Damage;
        var recipes = Recipes.ForOutput(output.Id, outDmg);
        var smelts = Smelting.ForOutput(output.Id, outDmg);
        IReadOnlyList<int> dropSources = inputs.Count == 1 ? Drops.SourcesOf(output.Id, outDmg) : [];
        string outName = Items.DisplayName(output.Id, Math.Max(0, outDmg)).ToLowerInvariant();

        // Furnace.
        if (inputs.Count == 1 && smelts.FirstOrDefault(s => SameItem(s.InputId, -1, inputs[0])) is { } sm)
            return new(+1, $"gd:smelt:{sm.InputId}", $"seed: smelt {Lower(Items.Get(sm.InputId).DisplayName)}");

        // Crafting: every claimed input is an ingredient of one recipe (a subset is fine: "torches from coal").
        var (crafted, countMismatch, countWhy) = CraftVerdict(output, inputs, recipes);
        if (crafted != null) return crafted;

        // Block drops ("flint from gravel").
        if (inputs.Count == 1 && dropSources.Contains(inputs[0].Id))
            return new(+1, $"gd:block:{inputs[0].Id}", $"seed: {Lower(Blocks.Get(inputs[0].Id).DisplayName)} drops {outName}");

        if (countMismatch != null) return new(-1, RecipeRef(countMismatch), countWhy!);
        if (recipes.Count + smelts.Count + dropSources.Count == 0) return null; // mob drops, mechanics... unknown to the seed
        return Refutation(claim, recipes, smelts, dropSources);
    }

    /// <summary>The claim's output and inputs as items; null when any doesn't resolve or a liquid (or its bucket) is involved.</summary>
    private static (ClaimParser.ItemRef Output, List<ClaimParser.ItemRef> Inputs)? Resolve(RecipeClaim claim)
    {
        if (ClaimParser.ResolveItem(claim.Output) is not { } output) return null;
        var inputs = new List<ClaimParser.ItemRef>();
        foreach (var i in claim.Inputs)
        {
            if (ClaimParser.ResolveItem(i) is not { } r) return null;
            inputs.Add(r);
        }
        if (inputs.Any(i => Blocks.Exists(i.Id) && (Blocks.IsWater(i.Id) || Blocks.IsLava(i.Id)) || i.Id is 326 or 327)) return null;
        return (output, inputs);
    }

    /// <summary>The first recipe whose ingredients include every input with the claimed counts; else the last count mismatch and why.</summary>
    private static (SeedVerdict? Verdict, Recipe? CountMismatch, string? CountWhy) CraftVerdict(ClaimParser.ItemRef output, List<ClaimParser.ItemRef> inputs,
        IReadOnlyList<Recipe> recipes)
    {
        Recipe? countMismatch = null;
        string? countWhy = null;
        foreach (var r in recipes)
        {
            if (!inputs.All(i => r.Totals.Any(t => SameItem(t.Ingredient.Id, t.Ingredient.Damage, i)))) continue;
            if (CountMismatch(output, inputs, r) is { } why)
            {
                countMismatch = r;
                countWhy = why;
                continue;
            }
            return (new(+1, RecipeRef(r), "seed: " + Lower(string.Join(", ", r.Totals.Select(t => $"{t.Count} {t.Ingredient}")))), null, null);
        }
        return (null, countMismatch, countWhy);
    }

    /// <summary>"seed says 4 per craft" / "seed says 8 cobblestone" when a claimed count is off, else null.</summary>
    private static string? CountMismatch(ClaimParser.ItemRef output, List<ClaimParser.ItemRef> inputs, Recipe r)
    {
        if (output.Count is { } oc && oc != r.OutputCount) return $"seed says {r.OutputCount} per craft";
        var wrong = inputs.FirstOrDefault(i => i.Count is { } c && r.Totals.First(t => SameItem(t.Ingredient.Id, t.Ingredient.Damage, i)).Count != c);
        if (wrong == null) return null;
        return $"seed says {r.Totals.First(t => SameItem(t.Ingredient.Id, t.Ingredient.Damage, wrong)).Count} {Lower(Items.DisplayName(wrong.Id, Math.Max(0, wrong.Damage)))}";
    }

    /// <summary>What the seed says makes it instead: the furnace (when claimed or the only way), a recipe, or the blocks that drop it.</summary>
    private static SeedVerdict Refutation(RecipeClaim claim, IReadOnlyList<Recipe> recipes, IReadOnlyList<SmeltingRecipe> smelts, IReadOnlyList<int> dropSources)
    {
        if (smelts.Count > 0 && (claim.Smelt || recipes.Count == 0))
            return new(-1, $"gd:smelt:{smelts[0].InputId}", "seed says " + string.Join(" or ", smelts.Select(s => Lower(Items.Get(s.InputId).DisplayName)).Distinct()));
        if (recipes.Count > 0)
            return new(-1, RecipeRef(recipes[0]), "seed says " + Lower(string.Join(", ", recipes[0].Totals.Select(t => $"{t.Count} {t.Ingredient}"))));
        return new(-1, $"gd:block:{dropSources[0]}", "seed says " + string.Join(" or ", dropSources.Take(3).Select(id => Lower(Blocks.Get(id).DisplayName))));
    }

    private static bool SameItem(int id, int damage, ClaimParser.ItemRef claimed) =>
        id == claimed.Id && (damage < 0 || claimed.Damage < 0 || damage == claimed.Damage);

    /// <summary>Same ref scheme as <see cref="Raptor.SeedCorpus"/> (gd:recipe:{out}:{dmg}:{n}, n = 1-based per output).</summary>
    private static string RecipeRef(Recipe r)
    {
        int n = 0;
        foreach (var x in Recipes.All)
        {
            if (x.OutputId != r.OutputId || x.OutputDamage != r.OutputDamage) continue;
            n++;
            if (ReferenceEquals(x, r)) break;
        }
        return $"gd:recipe:{r.OutputId}:{r.OutputDamage}:{n}";
    }

    private static string Lower(string s) => s.ToLowerInvariant();
}
