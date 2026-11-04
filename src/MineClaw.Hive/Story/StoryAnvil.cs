using System.Text.Json.Nodes;
using MineClaw.Data;
using MineClaw.Data.Versioning;

namespace MineClaw.Hive.Story;

/// <summary>
/// Anvil bills (1.12.2): "Repair Iron Pickaxe (iron ingot)" (the most damaged one, with as many material units as mend it: 1 level each
/// plus its prior work), "Combine two Iron Pickaxes" (2 levels when it mends, plus the second one's enchantments) and "Apply an enchanted
/// book to Iron Pickaxe" (the book's enchantments). Prices are 40 levels at most ("too expensive"); each use may wear the anvil
/// (intact → chipped → damaged → gone). Renaming isn't offered (it needs a typed name, and a pawn has no use for one). Craft work.
/// </summary>
public static class StoryAnvil
{
    private static readonly string[] Kinds = ["pickaxe", "axe", "shovel", "hoe", "sword", "helmet", "chestplate", "leggings", "boots"];
    private static readonly string[] Materials = ["wooden", "stone", "iron", "golden", "diamond", "leather", "chainmail"];

    /// <summary>The damageable items an anvil works on: tools, weapons and armour of every material, and the rest with durability.</summary>
    public static IEnumerable<string> Items(IGameData d) =>
        Materials.SelectMany(m => Kinds.Select(k => $"{m}_{k}"))
            .Concat(["bow", "fishing_rod", "shears", "flint_and_steel", "shield", "elytra", "carrot_on_a_stick"])
            .Where(n => d.ItemByName(n) is { } it && it.Name == n && it.MaxDamage > 0);

    public static IEnumerable<StoryRecipe> Recipes(IGameData d)
    {
        foreach (var name in Items(d))
        {
            var it = d.ItemByName(name)!;
            string label = StoryRecipes.Label(d, it.Id, 0);
            var item = new ItemKey(it.Id, -1);
            IReadOnlyList<string> modes = ["count", "forever"];
            if (Anvil.RepairMaterial(name) is { } mat)
                yield return new StoryRecipe("repair:" + name, $"Repair {label} ({mat.Name.Replace('_', ' ')})", "anvil", WorkTypes.Craft, new Stack(it.Id, 0, 1),
                    [new RecipeInput([item], 1), new RecipeInput([new ItemKey(mat.Id, mat.Meta)], 1)], false)
                {
                    ToolItem = name,
                    Station = new StationSpec("repair", name, XpLevels: 1, Note: "the most damaged one; a level per material unit (up to 4) plus its prior work")
                        { Modes = modes },
                };
            yield return new StoryRecipe("combine:" + name, $"Combine two {label}s", "anvil", WorkTypes.Craft, new Stack(it.Id, 0, 1),
                [new RecipeInput([item], 2)], false)
            {
                ToolItem = name,
                Station = new StationSpec("combine", name, XpLevels: 2, Note: "2 levels when it mends, plus the second one's enchantments and both prior works")
                    { Modes = modes },
            };
            yield return new StoryRecipe("book:" + name, $"Apply an enchanted book to {label}", "anvil", WorkTypes.Craft, new Stack(it.Id, 0, 1),
                [new RecipeInput([item], 1), new RecipeInput([new ItemKey(Enchanting.EnchantedBook, -1)], 1)], false)
            {
                ToolItem = name,
                Station = new StationSpec("book", name, XpLevels: 1, Note: "the book's enchantments (rarity × level, halved from a book) plus prior work")
                    { Modes = modes },
            };
        }
    }
}

public sealed partial class StoryColony
{
    /// <summary>
    /// An anvil job: repair takes the most damaged one (the pawn's own first, else storage) and the material units that mend it; combine
    /// two of them; book one and an enchanted book. The pawn needs the levels the price starts at (units, 2) or what the last try cost.
    /// </summary>
    private (ActiveJob? Job, string? Missing) AnvilJob(PawnState p, PawnLive live, string work, WorkbenchState bench, BillState bill, StoryRecipe r, string key)
    {
        var spec = r.Station!;
        var d = World!.Data;
        if (d.ItemByName(spec.Item) is not { } it) return (null, $"no item '{spec.Item}' in {d.VersionId}");
        string label = StoryRecipes.Label(d, it.Id, 0).ToLowerInvariant();
        var g = new Gathering(this, bill, live, bench.Pos);
        int least;
        switch (spec.Kind)
        {
            case "repair":
            {
                var mat = Anvil.RepairMaterial(spec.Item)!.Value;
                // The most damaged one the pawn holds, else in storage.
                var own = live.Inventory.Where(s => s.Id == it.Id && s.Meta > 0).OrderByDescending(s => s.Meta).FirstOrDefault();
                var stored = own == null ? FindStored(s => s.Id == it.Id && s.Meta > 0 && (Cats?.Accepts(bill.Ingredients, s.Id, s.Meta) ?? true), bench.Pos, bill.Radius) : null;
                if (own == null && stored == null) return (null, $"no damaged {label} within {bill.Radius} blocks");
                var target = own ?? stored!.Value.Stack;
                if (own == null) g.Fetch.Add((stored!.Value.Store.Chest, target with { Count = 1 }, TakeName(target)));
                int units = Math.Max(1, Anvil.UnitsFor(it.MaxDamage, target.Meta));
                int got = g.Get(s => s.Id == mat.Id && (mat.Meta < 0 || s.Meta == mat.Meta), units, mat.Name.Replace('_', ' '), partial: true);
                if (g.Missing != null) return (null, g.Missing);
                least = Anvil.RepairCost(Math.Min(got, units));
                break;
            }
            case "combine":
                g.Get(s => s.Id == it.Id, 2, $"two {label}s");
                if (g.Missing != null) return (null, g.Missing);
                least = 2;
                break;
            default: // book
                g.Get(s => s.Id == it.Id, 1, label);
                g.Get(s => s.Id == Enchanting.EnchantedBook, 1, "enchanted book");
                if (g.Missing != null) return (null, g.Missing);
                least = 1;
                break;
        }
        int need = Math.Max(least, bill.XpNeeded ?? least);
        if (need >= Anvil.TooExpensive) return (null, $"too expensive ({need} levels; the anvil refuses 40 or more)");
        if (XpShort(bill, work, need, live, out bool skip) is { } shortXp) return (null, shortXp);
        if (skip) return (null, null);
        var fetch = new JsonArray();
        var job = Job(work, "anvil", r.Label + (bill.Mode == "count" ? $" ({bill.Done}/{bill.Target})" : ""), bench.Pos, key, "bill", bill.Id, "working", f =>
        {
            f["item"] = spec.Item;
            f["mode"] = spec.Kind;
            f["fetch"] = fetch;
        }, xp: 25, count: 1);
        g.Attach(job, fetch);
        StoreStationOutput(job, bill, r, bench.Pos);
        return (job, null);
    }
}
