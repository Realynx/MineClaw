using System.Text.Json.Nodes;
using MineClaw.Data;

namespace MineClaw.Hive.Story;

/// <summary>
/// Bills at the 1.12.2 stations (docs/web/story-mode.md "Stations: brewing stand, enchanting table, anvil"): the job each one gives a
/// pawn (StoryBrewing.cs, StoryEnchanting.cs, StoryAnvil.cs), the ingredients it gathers (the pawn's own first, then a chest within the
/// bill's radius, the ingredient filter applied and the builds' reservations left), the XP levels enchanting and the anvil spend (a pawn
/// without them leaves the bill to one that has them; none has: suspended with the best level), a station that is gone (a worn-through
/// anvil), and what the bench inspector shows (bookshelves, the anvil's wear, the stand's water).
/// </summary>
public sealed partial class StoryColony
{
    /// <summary>What a station job takes: the pawn's own stock first, then one chest within the bill's radius.</summary>
    private sealed class StationGathering(StoryColony c, BillState bill, PawnLive live, P3 near)
    {
        public readonly List<(P3 Chest, Stack Stack, string Name)> Fetch = new();
        public string? Missing;

        /// <summary>The pawn ends up holding <paramref name="need"/> of <paramref name="pick"/> (or, <paramref name="partial"/>, what
        /// there is); returns how many, 0 with <see cref="Missing"/> set when short.</summary>
        public int Get(Func<Stack, bool> pick, int need, string label, bool partial = false, bool storeOnly = false)
        {
            if (Missing != null || need <= 0) return 0;
            int own = Own(pick, storeOnly);
            if (own >= need) return need;
            bool Ok(Stack s) => pick(s) && (c.Cats?.Accepts(bill.Ingredients, s.Id, s.Meta) ?? true);
            int want = need - own;
            var src = c.FindStored(s => Ok(s) && s.Count >= want, near, bill.Radius) ?? c.FindStored(Ok, near, bill.Radius);
            int got = src is { } s0 ? Math.Min(want, s0.Stack.Count) : 0;
            if (got > 0 && ReservedAway(pick, got))
            {
                Missing = $"the {label} in storage is reserved for builds";
                return 0;
            }
            if (Short(own, got, need, partial))
            {
                Missing = $"no {label} within {bill.Radius} blocks";
                return 0;
            }
            if (got > 0) Fetch.Add((src!.Value.Store.Chest, src.Value.Stack with { Count = got }, c.TakeName(src.Value.Stack)));
            return own + got;
        }

        /// <summary>What the pawn carries of it (none counted when only storage will do).</summary>
        private int Own(Func<Stack, bool> pick, bool storeOnly) => storeOnly ? 0 : live.Inventory.Where(pick).Sum(s => s.Count);

        /// <summary>A bill not for a build may not take what the builds reserved. Only an item something holds is checked against what
        /// is free (any count: the pick looks at the item, not the stack).</summary>
        private bool ReservedAway(Func<Stack, bool> pick, int got)
        {
            bool Held(int id, int meta) => pick(new Stack(id, meta, int.MaxValue));
            return bill.ForBuild == null && c._reserved.Any(kv => kv.Value > 0 && Held(kv.Key.Id, kv.Key.Meta)) && c.Unreserved(Held) < got;
        }

        /// <summary>Less than needed, unless a partial take has something.</summary>
        private static bool Short(int own, int got, int need, bool partial) => own + got < need && !(partial && own + got > 0);

        public void Attach(ActiveJob job, JsonArray fetch)
        {
            foreach (var (chest, stack, name) in Fetch)
            {
                fetch.Add(new JsonObject { ["from"] = chest.ToString(), ["item"] = name, ["n"] = stack.Count });
                job.Takes.Add((chest, stack));
            }
        }
    }

    /// <summary>The name take() knows a stack by: the variant ("birch_wood_planks", "lapis_lazuli", "pufferfish"), a potion as "potion".</summary>
    internal string TakeName(Stack s) => World?.Data is not { } d || !d.ItemExists(s.Id) ? s.Id.ToString()
        : Brewing.IsPotionItem(s.Id) || s.Meta <= 0 || d.Item(s.Id).MaxDamage > 0 ? d.Item(s.Id).Name : d.VariantName(s.Id, s.Meta);

    private static string Pretty(string kind) => kind.Replace('_', ' ');

    /// <summary>How many a bill still wants (count: target − done; until: target − stock; forever: <paramref name="batch"/>).</summary>
    private int Left(BillState b, StoryRecipe r, int batch) => b.Mode switch
    {
        "count" => b.Target - b.Done,
        "until" => b.Target - CountItem(r.Output.Id, r.Output.Meta),
        _ => batch,
    };

    /// <summary>
    /// A pawn short of the XP levels a bill needs: null when it has them, or when another pawn that may take the bill has them (then
    /// <paramref name="skip"/>: that pawn will); else the reason the bill waits ("needs 7 XP levels: no pawn has them (best: Ann 3)").
    /// </summary>
    private string? XpShort(BillState bill, string work, int need, PawnLive live, out bool skip)
    {
        skip = false;
        if (live.XpLevel >= need) return null;
        var best = Pawns.Values.Where(q => q.Priorities.GetValueOrDefault(work) > 0 && MayTake(bill, q, work))
            .Select(q => (Pawn: q, Live: Host.Live(q.Name))).Where(t => t.Live.Online).OrderByDescending(t => t.Live.XpLevel).FirstOrDefault();
        if (best.Pawn != null && best.Live.XpLevel >= need) { skip = true; return null; }
        return $"needs {need} XP levels: no pawn has them ({(best.Pawn == null ? "no pawn with " + work + " on may take it" : $"best: {best.Pawn.Display} {best.Live.XpLevel}")})";
    }

    /// <summary>A station whose block is gone (an anvil worn through, a stand broken): its bills wait with why, said once.</summary>
    private bool StationGone(WorkbenchState bench)
    {
        if (World is not { } w || !w.Loaded(bench.Pos.X, bench.Pos.Z)) return false;
        if (StoryRecipes.BenchOf(w.Block(bench.Pos.X, bench.Pos.Y, bench.Pos.Z).Id) == bench.Kind) return false;
        var why = $"the {Pretty(bench.Kind)} at {bench.Pos} is gone" + (bench.Kind == "anvil" ? " (worn through): place a new one and designate it" : "");
        foreach (var b in bench.Bills.Where(b => b.SuspendedReason != why)) { b.SuspendedReason = why; bench.Rev = Touch(); }
        if (_billAlerted.GetValueOrDefault("bench:" + bench.Pos) != why) { _billAlerted["bench:" + bench.Pos] = why; Alert("warn", char.ToUpperInvariant(why[0]) + why[1..]); }
        return true;
    }

    /// <summary>A station bill's job for this pawn: (job, null) | (null, why it waits) | (null, null) = not this pawn now.</summary>
    private (ActiveJob? Job, string? Missing) StationJob(PawnState p, PawnLive live, string work, WorkbenchState bench, BillState bill, StoryRecipe r, string key) =>
        r.Station?.Kind switch
        {
            "brew" => BrewJob(p, live, work, bench, bill, r, key),
            "enchant" => EnchantJob(p, live, work, bench, bill, r, key),
            "repair" or "combine" or "book" => AnvilJob(p, live, work, bench, bill, r, key),
            _ => (null, null),
        };

    /// <summary>A station job done: the brewing stand's fuel, what it made (the note), the anvil's wear.</summary>
    private void StationDone(WorkbenchState bench, BillState bill, ActiveJob j, string? note)
    {
        if (!string.IsNullOrWhiteSpace(note)) bill.Last = note.Length > 160 ? note[..159] + "…" : note;
        if (bench.Kind == "brewing_stand" && j.Json["steps"]?.GetValue<int>() is { } steps)
            bench.Fuel = Math.Max(0, (bench.Fuel is { } f && f >= steps ? f : (bench.Fuel ?? 0) + Brewing.BrewsPerPowder) - steps);
        _noOffer.Remove(bill.Id);
        if (bench.Kind == "anvil" && note?.Contains("anvil broke", StringComparison.OrdinalIgnoreCase) == true) StationGone(bench);
    }

    /// <summary>Pawns whose table showed no offer for an enchant bill's preferred enchantment (their offers change only after they
    /// enchant something): they leave it to the others.</summary>
    private readonly Dictionary<string, HashSet<string>> _noOffer = new(StringComparer.Ordinal);

    /// <summary>
    /// A station job failed: short of XP levels ("needs N XP levels") teaches the bill the levels and leaves it to a pawn that has them;
    /// too expensive (40+) suspends at once; no offer showing the preferred enchantment leaves it to the other pawns. True = handled.
    /// </summary>
    private bool StationFailed(PawnState p, WorkbenchState bench, BillState bill, string why)
    {
        if (StoryRecipes.Get(World!.Data, bill.Recipe)?.Station is null) return false;
        var m = System.Text.RegularExpressions.Regex.Match(why, @"needs (\d+) XP levels");
        if (m.Success)
        {
            bill.XpNeeded = int.Parse(m.Groups[1].Value);
            return true;
        }
        if (why.Contains("too expensive", StringComparison.OrdinalIgnoreCase))
        {
            bill.SuspendedReason = why;
            Alert("warn", $"Bill {bill.Recipe} suspended: {why}", p.Name);
            return true;
        }
        if (why.Contains("no offer shows", StringComparison.OrdinalIgnoreCase))
        {
            if (!_noOffer.TryGetValue(bill.Id, out var set)) _noOffer[bill.Id] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            set.Add(p.Name);
            return true;
        }
        if (why.Contains("anvil broke", StringComparison.OrdinalIgnoreCase) || why.Contains("no anvil at", StringComparison.Ordinal)
            || why.Contains("no brewing stand at", StringComparison.Ordinal) || why.Contains("no enchanting table at", StringComparison.Ordinal))
            return StationGone(bench);
        return false;
    }

    /// <summary>A station the colony's own build placed (the Architect's production tab: a brewing stand, anvil or enchanting table) is
    /// the colony's at once, as if designated.</summary>
    private void StationBuilt(GhostState g)
    {
        if (g.Remove || StoryRecipes.BenchOf(g.Id) is not { } kind || !StoryRecipes.Stations.Contains(kind) || Benches.Any(b => b.Pos == g.At)) return;
        Benches.Add(new WorkbenchState { Pos = g.At, Kind = kind, Rev = Touch() });
        WakeIdle();
    }

    /// <summary>The bench inspector's station facts: an enchanting table's bookshelves and level range, an anvil's wear, a brewing stand's
    /// water and fuel. Null for crafting tables and furnaces.</summary>
    private JsonObject? StationInfo(WorkbenchState bench)
    {
        if (World is not { } w || !StoryRecipes.Stations.Contains(bench.Kind)) return null;
        var (id, meta) = w.Block(bench.Pos.X, bench.Pos.Y, bench.Pos.Z);
        bool present = !w.Loaded(bench.Pos.X, bench.Pos.Z) || StoryRecipes.BenchOf(id) == bench.Kind;
        var o = new JsonObject { ["present"] = present };
        switch (bench.Kind)
        {
            case "enchanting_table":
            {
                int shelves = Enchanting.Bookshelves((x, y, z) => w.Block(x, y, z).Id, bench.Pos.X, bench.Pos.Y, bench.Pos.Z);
                var (min, max) = Enchanting.TopOffer(shelves);
                o["bookshelves"] = shelves;
                o["max_bookshelves"] = Enchanting.MaxShelves;
                o["top_offer"] = new JsonArray(min, max);
                break;
            }
            case "anvil":
                o["condition"] = present && id == Anvil.Block ? Anvil.Condition(meta) : "gone";
                break;
            case "brewing_stand":
                o["water"] = WaterNear(bench.Pos)?.Json();
                o["fuel"] = bench.Fuel;
                break;
        }
        return o;
    }
}
