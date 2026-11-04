using System.Text.Json.Nodes;
using MineClaw.Data;
using MineClaw.Data.Versioning;

namespace MineClaw.Hive.Story;

/// <summary>
/// Enchanting table bills (1.12.2): "Enchant Diamond Pickaxe" for every item the table takes. A bill takes offer 1–3 at most
/// (<see cref="BillState.Option"/>: its lapis and the levels it costs) and may prefer an enchantment: the table only shows its offers
/// once the item is in, so the pawn takes the highest offer hinting the preferred one, else (unless prefer-only) the highest it can pay.
/// The offers are fixed for that pawn and item until the pawn enchants something (no reroll). Craft work.
/// </summary>
public static class StoryEnchanting
{
    public static IEnumerable<StoryRecipe> Recipes(IGameData d)
    {
        foreach (var name in Enchanting.TableItems)
        {
            if (d.ItemByName(name) is not { } it || it.Name != name) continue;
            int output = name == "book" ? Enchanting.EnchantedBook : it.Id;
            yield return new StoryRecipe("enchant:" + name, "Enchant " + StoryRecipes.Label(d, it.Id, 0), "enchanting_table", WorkTypes.Craft,
                new Stack(output, 0, 1),
                [new RecipeInput([new ItemKey(it.Id, -1)], 1), new RecipeInput([new ItemKey(Enchanting.Lapis, Enchanting.LapisMeta)], 3)], false)
            {
                ToolItem = name,
                Station = new StationSpec("enchant", name, XpLevels: 1, Lapis: 3,
                    Enchantments: Enchanting.TableEnchantments(name).Select(e => e.Name).ToList(),
                    Note: "takes offer 1–3 (1–3 lapis and levels); the offer needs its own level (up to 30 with 15 bookshelves)")
                { Modes = ["count", "forever"] },
            };
        }
    }

    /// <summary>What the tools system's "enchant tools for marked materials" prefers per material: ores with drops (coal, diamond,
    /// emerald, lapis, redstone, quartz) Fortune, blocks that only drop themselves with Silk Touch (glass, ice, glowstone, ender chests,
    /// grass) Silk Touch, anything else Efficiency.</summary>
    public static string PreferFor(string material)
    {
        var m = material.ToLowerInvariant();
        if (ContainsAny(m, FortuneHints)) return "fortune";
        return ContainsAny(m, SilkTouchHints) ? "silk_touch" : "efficiency";
    }

    private static readonly string[] FortuneHints = ["coal", "diamond", "emerald", "lapis", "redstone", "quartz"];
    private static readonly string[] SilkTouchHints = ["glass", "ice", "glowstone", "ender_chest", "grass", "mycelium", "bookshel"];

    private static bool ContainsAny(string m, string[] hints)
    {
        foreach (var h in hints)
            if (m.Contains(h)) return true;
        return false;
    }
}

public sealed partial class StoryColony
{
    /// <summary>The online pawns that may take a bill (the work type on, allowed by the bill).</summary>
    private List<(PawnState Pawn, PawnLive Live)> Takers(BillState bill, string work) =>
        Pawns.Values.Where(q => q.Priorities.GetValueOrDefault(work) > 0 && MayTake(bill, q, work))
            .Select(q => (q, Host.Live(q.Name))).Where(t => t.Item2.Online).ToList();

    /// <summary>
    /// An enchant: the item (from storage first: the pawn's own may be its working tool or enchanted already) and the option's lapis to
    /// the table; the pawn needs at least the option's levels (or what the last try needed). A pawn whose table showed no offer for a
    /// prefer-only bill leaves it to the others; when every pawn has tried, the bill waits with why.
    /// </summary>
    private (ActiveJob? Job, string? Missing) EnchantJob(PawnState p, PawnLive live, string work, WorkbenchState bench, BillState bill, StoryRecipe r, string key)
    {
        var spec = r.Station!;
        var d = World!.Data;
        if (d.ItemByName(spec.Item) is not { } it) return (null, $"no item '{spec.Item}' in {d.VersionId}");
        int option = Math.Clamp(bill.Option ?? 3, 1, 3);
        if (_noOffer.TryGetValue(bill.Id, out var tried) && tried.Contains(p.Name)) return (null, NoOfferAnywhere(bill, work, tried));
        int need = Math.Max(option, bill.XpNeeded ?? option);
        if (XpShort(bill, work, need, live, out bool skip) is { } shortXp) return (null, shortXp);
        if (skip) return (null, null);
        string label = StoryRecipes.Label(d, it.Id, 0).ToLowerInvariant();
        var g = new StationGathering(this, bill, live, bench.Pos);
        if (EnchantGather(g, live, it.Id, label, option) is { } missing) return (null, missing);
        var fetch = new JsonArray();
        var job = Job(work, "enchant", r.Label + (bill.Mode == "count" ? $" ({bill.Done}/{bill.Target})" : ""), bench.Pos, key, "bill", bill.Id, "working", f =>
        {
            f["item"] = spec.Item;
            f["option"] = option;
            f["prefer"] = bill.Prefer;
            f["prefer_only"] = bill.PreferOnly;
            f["fetch"] = fetch;
        }, xp: 30, count: 1);
        g.Attach(job, fetch);
        StoreStationOutput(job, bill, r, bench.Pos);
        return (job, null);
    }

    /// <summary>This pawn's table showed no offer for the prefer-only bill: when every pawn that may take it has tried, the bill waits
    /// with why; else (null) it is left to the others.</summary>
    private string? NoOfferAnywhere(BillState bill, string work, HashSet<string> tried) =>
        Takers(bill, work).All(t => tried.Contains(t.Pawn.Name))
            ? $"no pawn's table shows {Enchanting.ByName(bill.Prefer)?.DisplayName ?? bill.Prefer} for it (a pawn's offers change after it enchants something)"
            : null;

    /// <summary>The item (from storage first; the pawn's own when none is stored) and the option's lapis; what is missing, else null.</summary>
    private static string? EnchantGather(StationGathering g, PawnLive live, int itemId, string label, int option)
    {
        g.Get(s => s.Id == itemId, 1, label, storeOnly: true);
        if (g.Missing != null)
        {
            if (live.Inventory.All(s => s.Id != itemId)) return g.Missing;
            g.Missing = null; // its own
        }
        g.Get(s => s.Id == Enchanting.Lapis && s.Meta == Enchanting.LapisMeta, option, "lapis lazuli");
        return g.Missing;
    }

    /// <summary>The product of an enchant or anvil job goes to the bill's chest; with "best", an enchanted book goes to the best chest and a
    /// tool, weapon or armour stays with the pawn (pawns keep their gear).</summary>
    private void StoreStationOutput(ActiveJob job, BillState bill, StoryRecipe r, P3 near)
    {
        var d = World!.Data;
        bool gear = d.ItemExists(r.Output.Id) && d.Item(r.Output.Id).MaxDamage > 0;
        var dst = bill.Store is JsonObject so && P3.Parse(so["chest"]) is { } c ? StorageAt(c) : gear ? null : BestChest(r.Output.Id, 0, near);
        if (dst == null) return;
        job.Json["stash"] = new JsonArray(new JsonObject { ["to"] = dst.Chest.ToString(), ["items"] = new JsonArray(ItemName(r.Output.Id, 0)) });
        job.Puts.Add((dst.Chest, [r.Output with { Meta = 0, Count = 1 }]));
    }

    /// <summary>
    /// The tools system's hook ("enchant tools for marked materials"): count bills at the colony's enchanting table nearest home that
    /// enchant <paramref name="items"/> (default: every pickaxe kind the colony has) preferring <paramref name="prefer"/> (fortune,
    /// silk_touch, efficiency...; <see cref="StoryEnchanting.PreferFor"/> picks one per material). A recipe that already has an unfinished
    /// bill with that preference isn't added twice. Returns the new bills' ids, or why none.
    /// </summary>
    public (IReadOnlyList<string> Ids, string? Error) EnchantTools(string prefer, IReadOnlyList<string>? items = null, int count = 1, bool preferOnly = false)
    {
        if (World is not { } w) return ([], "no world view yet");
        if (!StoryRecipes.SupportedIn(w.Data, "enchanting_table")) return ([], $"{w.Data.VersionId} has no enchanting");
        if (Enchanting.ByName(prefer) is not { } pe) return ([], $"unknown enchantment '{prefer}'");
        lock (_lock)
        {
            if (EnchantingTableNearHome() is not { } table) return ([], "no enchanting table: place one and designate it");
            var list = items is { Count: > 0 } ? items : PickaxesHeld(w);
            if (list.Count == 0) return ([], "the colony has no pickaxe to enchant");
            var ids = new List<string>();
            foreach (var name in list)
                if (AddEnchantBill(w, table, pe, name, Math.Clamp(count, 1, 64), preferOnly, ids) is { } err) return (ids, err);
            table.Rev = Touch();
            WakeIdle();
            return (ids, null);
        }
    }

    private WorkbenchState? EnchantingTableNearHome()
    {
        var home = Home() ?? Benches.FirstOrDefault()?.Pos ?? default;
        return Benches.Where(b => b.Kind == "enchanting_table").OrderBy(b => b.Pos.Dist(home)).FirstOrDefault();
    }

    /// <summary>Every pickaxe kind the table takes that the colony has.</summary>
    private List<string> PickaxesHeld(IStoryWorld w) =>
        Enchanting.TableItems.Where(n => n.EndsWith("_pickaxe", StringComparison.Ordinal) && w.Data.ItemByName(n) is { } it && CountItem(it.Id, -1) > 0).ToList();

    /// <summary>One item's enchant bill (none when an unfinished one with that preference is there already); why it can't be, else null.</summary>
    private string? AddEnchantBill(IStoryWorld w, WorkbenchState table, EnchantmentInfo pe, string name, int target, bool preferOnly, List<string> ids)
    {
        if (StoryRecipes.Get(w.Data, "enchant:" + name) is not { } r) return $"the table can't enchant '{name}'";
        if (!Enchanting.Fits(pe, name)) return $"{pe.DisplayName} doesn't go on {Pretty(name)}";
        if (table.Bills.Any(b => b.Recipe == r.Id && b.Prefer == pe.Name && Wanted(b, r))) return null;
        var bill = new BillState
        {
            Id = NextId("bill"), Recipe = r.Id, Mode = "count", Target = target, Option = 3, Prefer = pe.Name, PreferOnly = preferOnly,
        };
        table.Bills.Add(bill);
        ids.Add(bill.Id);
        return null;
    }
}
