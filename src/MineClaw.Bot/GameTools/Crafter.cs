using MineClaw.Bot.Game;
using MineClaw.Client;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Bot.GameTools;

/// <summary>
/// <c>craft</c> on top of the client's planner-driven <see cref="IGameClient.CraftAsync"/>, the craft stage of the shared
/// resolver (<see cref="Obtainer"/>): the whole recipe tree is planned first over the inventory plus every known chest
/// (<see cref="ChestStock"/>), exactly the raw ingredients it uses are withdrawn (one stop per chest, nearest first, in
/// batches the inventory holds), then the intermediates are crafted (2x2 in the inventory, a table only for 3x3: placed from
/// the inventory or crafted from planks when none is in range), smelting first when the only way is through a furnace. An
/// unnamed variant takes the recipe that's possible without smelting ("slab" = cobblestone slabs from cobble), else it asks
/// which one (stone slabs smelt only when named: stone_slab = 44:0). What can't be made is one
/// line: "need 1 more log for a chest; none in inventory or the 3 known chests". Reserved items and carried food are never
/// ingredients; the carried target never counts as already made.
/// </summary>
public sealed class Crafter(IGameClient client)
{
    /// <summary>WorldIndexer container snapshots (null = the bot's acquisition sources' store).</summary>
    public MineClaw.Agent.Rag.ObservationStore? Observations { get; init; }

    private MineClaw.Agent.Rag.ObservationStore? Obs => Observations ?? Acquisition.Of(client)?.Observations;

    /// <summary>Withdraw batches per craft (each fits the inventory); a 256-slab order crafts in several.</summary>
    private const int MaxRounds = 8;

    public async Task<ActionResult> CraftAsync(string itemName, int count, CancellationToken ct)
    {
        if (Items.ByNameWithDamage(itemName) is not { } r) return ActionResult.Fail($"unknown item '{itemName}'");
        return await CraftAsync(r.Item.Id, r.Damage, count, ct).ConfigureAwait(false);
    }

    /// <summary>Crafts <paramref name="count"/> more of (id, damage; -1 = any variant).</summary>
    public async Task<ActionResult> CraftAsync(int id, int dmg, int count, CancellationToken ct)
    {
        count = Math.Max(1, count);
        if (Recipes.ForOutput(id, dmg).Count == 0) return ActionResult.Fail($"{Names.Item(id, Math.Max(dmg, 0))} has no crafting recipe");
        var chests = Chests();
        var notes = new List<string>();
        if (dmg < 0 && Variants(id).Count > 1)
        {
            // An unnamed variant ("slab") takes only one that's craftable now without smelting; never a guess that smelts (live
            // 10-28: "slab" x60 with 51 cobble + 9 stone became smooth stone slabs, 51 cobble smelted, for a cobblestone-slab bill).
            if (SmeltFreeVariant(id, count, chests) is not { } pick) return ActionResult.Fail(Ambiguous(id));
            dmg = pick;
            notes.Add($"{Family(id)} = {Items.VariantName(id, dmg)} ({Recipes.VariantTag(id, dmg)})");
        }
        string name = Names.Item(id, Math.Max(dmg, 0));
        string? last = null;
        int crafted = 0, batches = 0;
        _hold.Value = Held(); // what's reserved now stays: withdrawn copies of the same item may still be used

        for (int round = 0; round < MaxRounds && crafted < count; round++)
        {
            int want = count - crafted;
            bool smelt = false;
            var plan = Plan(id, dmg, want, chests, smelting: false);
            if (!plan.Complete)
            {
                var withSmelt = Plan(id, dmg, want, chests, smelting: true);
                if (!withSmelt.Complete || !FurnaceAvailable()) return Fail(notes, Shortfall(id, dmg, want, plan, chests));
                plan = withSmelt;
                smelt = true;
            }
            // The batch the inventory holds (ingredients from storage + room to spare for the products).
            int n = want;
            var take = Withdrawals(plan, id, dmg, chests);
            while (n > 1 && !Fits(take))
            {
                n = Math.Max(1, n / 2);
                plan = Plan(id, dmg, n, chests, smelt);
                take = Withdrawals(plan, id, dmg, chests);
            }
            if (!Fits(take)) return Fail(notes, $"inventory full: no room for the {name} ingredients ({string.Join(", ", take.Select(t => t.Need))})");
            var took = await WithdrawAsync(take, chests, ct).ConfigureAwait(false);
            if (took.Count > 0) notes.Add("took " + string.Join("; ", took));
            // A chest that had less than remembered: plan again on what's really there.
            if (!Plan(id, dmg, n, ChestStock.Empty, smelt).Complete) { last = "storage held less than remembered"; continue; }

            int before = Have(id, dmg);
            var made = await CraftBatchAsync(id, dmg, n, smelt, notes, ct).ConfigureAwait(false);
            if (!made.Ok) return Fail(notes, crafted > 0 ? $"crafted {crafted}/{count} {name}, then: {made.Message}" : made.Message);
            // The client's own count (an inventory that didn't show it yet counts the batch as made).
            crafted += Math.Max(Have(id, dmg) - before, made.Count > 0 ? made.Count : n);
            batches++;
            last = made.Message;
        }
        if (crafted <= 0) return Fail(notes, last ?? $"crafting {name} produced nothing");
        if (batches == 1 && crafted >= count) return ActionResult.Success(Join(notes) + last, crafted);
        string done = crafted >= count ? $"crafted {crafted} {name} (have {Have(id, dmg)})" : $"crafted only {crafted}/{count} {name} ({last})";
        return crafted >= count ? ActionResult.Success(Join(notes) + done, crafted) : ActionResult.Fail(Join(notes) + done, crafted);
    }

    private ChestStock Chests() => ChestStock.Read(client, Obs, Acquisition.Of(client)?.SkipChest);

    private static ActionResult Fail(List<string> notes, string why) => ActionResult.Fail(Join(notes) + why);

    /// <summary>One batch whose ingredients are all carried now: smelting first when needed, a table when 3x3, then the craft.</summary>
    private async Task<ActionResult> CraftBatchAsync(int id, int dmg, int n, bool smelt, List<string> notes, CancellationToken ct)
    {
        string name = Names.Item(id, Math.Max(dmg, 0));
        if (smelt)
        {
            var withSmelt = Plan(id, dmg, n, ChestStock.Empty, smelting: true);
            var furnace = await EnsureStationAsync(Blocks.Furnace, ct).ConfigureAwait(false);
            if (!furnace.Ok) return ActionResult.Fail($"need smelting ({string.Join("; ", withSmelt.Steps.Where(s => s.IsSmelt))}) but {furnace.Message}");
            if (furnace.Note != null) notes.Add(furnace.Note);
            foreach (var step in withSmelt.Steps.Where(s => s.IsSmelt))
            {
                var s = step.Smelt!;
                var res = await client.SmeltAsync(s.InputId, step.Times, null, wait: true, furnace: furnace.Pos, ct: ct).ConfigureAwait(false);
                if (!res.Ok) return ActionResult.Fail($"smelting {Names.Item(s.InputId)} failed: {res.Message}");
                notes.Add($"smelted {Names.Item(s.OutputId, s.OutputDamage)} x{step.OutputCount}");
            }
            var after = Plan(id, dmg, n, ChestStock.Empty, smelting: false);
            if (!after.Complete) return ActionResult.Fail($"still missing {string.Join(", ", after.Missing)}");
        }
        var plan = Plan(id, dmg, n, ChestStock.Empty, smelting: false);
        if (plan.Steps.Any(s => s.RequiresCraftingTable))
        {
            var table = await EnsureStationAsync(Blocks.Workbench, ct).ConfigureAwait(false);
            if (!table.Ok) return ActionResult.Fail($"{name} needs a crafting table: {table.Message}");
            if (table.Note != null) notes.Add(table.Note);
        }
        return await client.CraftAsync(id, dmg, n, ct).ConfigureAwait(false);
    }

    private int Have(int id, int dmg) => client.World.Inventory.CountItem(id, dmg < 0 ? null : dmg);

    private bool TableAvailable()
    {
        var w = client.World;
        return w.Inventory.CountItem(Blocks.Workbench) > 0 || w.FindNearestBlock(w.Player.Position, client.StationSearchRadius, Blocks.Workbench) != null;
    }

    private bool FurnaceAvailable()
    {
        var w = client.World;
        return w.Inventory.CountItem(Blocks.Furnace) > 0
               || w.FindNearestBlock(w.Player.Position, client.StationSearchRadius, Blocks.Furnace, Blocks.LitFurnace) != null;
    }

    /// <summary>Held back for this craft: the reserved items and the food carried when it started (hand-offs, meals).</summary>
    /// <remarks>Per async flow: the craft tool's one Crafter serves every body.</remarks>
    private readonly AsyncLocal<Dictionary<int, int>?> _hold = new();

    private Dictionary<int, int> Held()
    {
        var reserved = client.ReservedItems;
        return client.World.Inventory.PlayerSlots().Skip(9)
            .Where(s => !s.IsEmpty && (reserved.Contains(s.Id) || Items.Food(s.Id) != null))
            .GroupBy(s => (int)s.Id).ToDictionary(g => g.Key, g => g.Sum(s => s.Count));
    }

    /// <summary>The inventory the craft may use: never the target, nor the held-back reserved items and food.</summary>
    private List<ItemStack> Carried(int id, int dmg)
    {
        var hold = new Dictionary<int, int>(_hold.Value ?? Held());
        var list = new List<ItemStack>();
        foreach (var s in client.World.Inventory.PlayerSlots().Skip(9))
        {
            if (s.IsEmpty || (s.Id == id && (dmg < 0 || s.Damage == dmg))) continue;
            int keep = Math.Min(hold.GetValueOrDefault(s.Id), s.Count);
            if (keep > 0) hold[s.Id] -= keep;
            if (s.Count - keep > 0) list.Add(keep == 0 ? s : new ItemStack(s.Id, (byte)(s.Count - keep), s.Damage));
        }
        return list;
    }

    private CraftPlan Plan(int id, int dmg, int count, ChestStock chests, bool smelting) =>
        RecipePlanner.Plan(id, count, Carried(id, dmg).Concat(chests.Stacks()).ToList(), dmg,
            new PlannerOptions { AllowSmelting = smelting, HasCraftingTable = TableAvailable() });

    /// <summary>The family word of a variant item ("slab", never block 44's name "stone_slab").</summary>
    private static string Family(int id) => Items.Get(id).DisplayName.ToLowerInvariant();

    private static List<int> Variants(int id) => Recipes.ForOutput(id, -1).Select(r => r.OutputDamage).Distinct().ToList();

    /// <summary>The variant of an unnamed item the inventory + chests can craft now without a furnace, or null.</summary>
    private int? SmeltFreeVariant(int id, int count, ChestStock chests)
    {
        foreach (var d in Variants(id))
            if (Plan(id, d, count, chests, false).Complete) return d;
        return null;
    }

    /// <summary>"'slab' is several items, name one: cobblestone_slab (44:3, 3 cobblestone → 3), stone_slab (44:0, 3 stone
    /// (smelted cobblestone) → 3), ..."</summary>
    internal static string Ambiguous(int id) =>
        $"'{Family(id)}' is several items and none can be crafted from what's here without smelting; name one: "
        + string.Join(", ", Variants(id).Select(d => $"{Items.VariantName(id, d)} ({Recipes.VariantTag(id, d)})"));

    /// <summary>An unnamed variant ("slab"): the one the inventory + chests can make (smelt-free first), else the closest.</summary>
    private int PickVariant(int id, int dmg, int count, ChestStock chests)
    {
        if (dmg >= 0) return dmg;
        var variants = Variants(id);
        if (variants.Count < 2) return dmg;
        foreach (bool smelt in new[] { false, true })
            foreach (var d in variants)
                if (Plan(id, d, count, chests, smelt).Complete) return d;
        return variants.OrderBy(d => Plan(id, d, count, chests, false).Missing.Sum(m => m.Count)).First();
    }

    private sealed record Take(ChestStock.Chest Chest, MaterialNeed Need);

    /// <summary>What the plan uses beyond the carried stock, spread over the chests nearest first (one stop per chest).</summary>
    private List<Take> Withdrawals(CraftPlan plan, int id, int dmg, ChestStock chests)
    {
        var carried = Carried(id, dmg);
        var list = new List<Take>();
        foreach (var u in plan.Used)
        {
            int onHand = carried.Where(s => s.Id == u.Id && (Items.Get(s.Id).MaxDamage > 0 || s.Damage == u.Damage)).Sum(s => s.Count);
            int short_ = u.Count - onHand;
            if (short_ <= 0) continue;
            foreach (var (ch, k) in chests.Allocate(u.Id, u.Damage, short_)) list.Add(new Take(ch, u with { Count = k }));
        }
        // Fuel for the smelts when the carried fuel (minus what the recipe itself uses) won't cover them: coal, else planks/logs.
        if (plan.ItemsToSmelt > 0)
        {
            var used = plan.Used.Select(u => u.Id).ToHashSet();
            int ticks = carried.Where(st => Smelting.IsFuel(st.Id) && !used.Contains(st.Id) && st.Id != Items.LavaBucket).Sum(st => st.Count * Smelting.FuelTicks(st.Id));
            if (ticks < plan.ItemsToSmelt * Smelting.CookTicks)
                foreach (var f in new[] { Items.Coal, Blocks.Planks, Blocks.Log })
                {
                    int need = Smelting.FuelNeeded(plan.ItemsToSmelt, f);
                    var alloc = chests.Allocate(f, -1, need);
                    if (alloc.Sum(a => a.N) < need) continue;
                    list.AddRange(alloc.Select(a => new Take(a.Chest, new MaterialNeed(f, -1, a.N))));
                    break;
                }
        }
        return list.OrderBy(t => t.Chest.Dist).ToList();
    }

    /// <summary>Whether the withdrawals fit: free slots and partial stacks, one slot left for the products.</summary>
    private bool Fits(List<Take> take)
    {
        if (take.Count == 0) return true;
        var slots = client.World.Inventory.PlayerSlots();
        int empty = 0;
        for (int i = 9; i < 45 && i < slots.Length; i++) if (slots[i].IsEmpty) empty++;
        int need = 0;
        foreach (var g in take.GroupBy(t => (t.Need.Id, t.Need.Damage)))
        {
            int max = Math.Max(1, (int)Items.Get(g.Key.Id).MaxStack);
            int partial = 0;
            for (int i = 9; i < 45 && i < slots.Length; i++)
                if (!slots[i].IsEmpty && slots[i].Id == g.Key.Id && slots[i].Damage == g.Key.Damage) partial += Math.Max(0, max - slots[i].Count);
            int n = Math.Max(0, g.Sum(t => t.Need.Count) - partial);
            need += (n + max - 1) / max;
        }
        return need <= empty;
    }

    /// <summary>Takes the planned amounts, chest by chest; a short take makes that chest's memory of the item stale.</summary>
    private async Task<List<string>> WithdrawAsync(List<Take> take, ChestStock chests, CancellationToken ct)
    {
        var lines = new List<string>();
        foreach (var stop in take.GroupBy(t => t.Chest.Pos))
        {
            var got = new List<string>();
            foreach (var t in stop)
            {
                int id = t.Need.Id;
                int? dmg = Items.Get(id).MaxDamage > 0 || t.Need.Damage < 0 ? null : t.Need.Damage;
                int before = client.World.Inventory.CountItem(id, dmg);
                await client.WithdrawAsync(id, t.Need.Count, stop.Key, dmg, ct).ConfigureAwait(false);
                int n = client.World.Inventory.CountItem(id, dmg) - before;
                if (n > 0) got.Add($"{n} {Names.Item(id, dmg ?? 0)}");
                if (n < t.Need.Count) chests.Forget(stop.Key, id);
            }
            if (got.Count > 0) lines.Add($"{string.Join(", ", got)} from {stop.Key.X},{stop.Key.Y},{stop.Key.Z}");
        }
        if (client.World.Inventory.OpenWindow != null) await client.CloseWindowAsync(ct).ConfigureAwait(false);
        return lines;
    }

    /// <summary>
    /// "need 1 more log for a chest; none in inventory or the 3 known chests": the raw materials the whole tree still lacks
    /// with everything the bot carries and storage holds, plus a variant hint ("cobblestone slab can be made now").
    /// </summary>
    public string Shortfall(int id, int dmg, int count)
    {
        var chests = Chests();
        dmg = PickVariant(id, dmg, count, chests);
        return Shortfall(id, dmg, count, Plan(id, dmg, count, chests, false), chests);
    }

    private string Shortfall(int id, int dmg, int count, CraftPlan plan, ChestStock chests)
    {
        string name = Names.Item(id, Math.Max(dmg, 0));
        string target = count > 1 ? $"{count} {Plural(name)}" : $"{("aeiou".Contains(name[0]) ? "an" : "a")} {name}";
        if (plan.Complete) return $"{target} can be crafted";
        var carried = Carried(id, dmg);
        var parts = new List<string>();
        var have = new List<string>();
        foreach (var m in plan.Missing)
        {
            // A wildcard of a variant item reads as its family ("log", not "oak wood").
            string mn = m.Damage < 0 && Items.VariantName(m.Id, 0) != Items.VariantName(m.Id, 1) ? Items.Get(m.Id).Name.Replace('_', ' ')
                : Names.Item(m.Id, Math.Max(0, m.Damage));
            parts.Add($"{m.Count} more {(m.Count > 1 ? Plural(mn) : mn)}");
            int avail = carried.Where(s => s.Id == m.Id && (m.Damage < 0 || s.Damage == m.Damage)).Sum(s => s.Count) + chests.Count(m.Id, m.Damage);
            if (avail > 0) have.Add($"{avail} {(avail > 1 ? Plural(mn) : mn)}");
        }
        string chestsText = $"{chests.Known} known chest{(chests.Known == 1 ? "" : "s")}";
        string where = chests.Known == 0 ? "none in inventory and no known chests"
            : have.Count == 0 ? $"none in inventory or the {chestsText}"
            : $"inventory + the {chestsText} have only {string.Join(", ", have)}";
        var hints = new List<string>();
        if (dmg >= 0)
            foreach (var d in Recipes.ForOutput(id, -1).Select(r => r.OutputDamage).Distinct().Where(d => d != dmg))
                if (Plan(id, d, count, chests, false).Complete) { hints.Add($"{Names.Item(id, d)} can be made now"); break; }
        if (!FurnaceAvailable() && Plan(id, dmg, count, chests, true).Complete) hints.Add("smelting would do it, but no furnace is in range");
        string hint = hints.Count > 0 ? $" ({string.Join("; ", hints)})" : "";
        return $"need {string.Join(", ", parts)} for {target}; {where}{hint}";
    }

    /// <summary>"sticks", "logs"; mass nouns stay ("cobblestone", "oak wood", "glass", "wheat").</summary>
    internal static string Plural(string name) =>
        name.EndsWith('s') || new[] { "stone", "glass", "wood", "wool", "dirt", "sand", "gravel", "wheat", "sugar", "coal", "leather", "redstone" }
            .Any(m => name.EndsWith(m, StringComparison.Ordinal)) ? name : name + "s";

    private static string Join(List<string> notes) => notes.Count == 0 ? "" : string.Join("; ", notes) + "; ";

    public sealed record Station(bool Ok, BlockPos? Pos, string Message, string? Note = null);

    /// <summary>Finds a station block in range, else places one from the inventory (crafting a table from planks if needed).</summary>
    public async Task<Station> EnsureStationAsync(int blockId, CancellationToken ct)
    {
        var w = client.World;
        int[] ids = blockId == Blocks.Furnace ? [Blocks.Furnace, Blocks.LitFurnace] : [blockId];
        if (w.FindNearestBlock(w.Player.Position, client.StationSearchRadius, ids) is { } found) return new(true, found.Pos, "ok");

        string what = Names.Block(blockId);
        string? crafted = null;
        if (w.Inventory.CountItem(blockId) == 0)
        {
            if (blockId != Blocks.Workbench) return new(false, null, $"no {what} within {client.StationSearchRadius:0} blocks and none carried");
            var c = await client.CraftAsync(Blocks.Workbench, -1, 1, ct).ConfigureAwait(false);
            if (!c.Ok) return new(false, null, $"none near or carried, and can't craft one: {c.Message}");
            crafted = "crafted a crafting table";
        }

        if (FindPlacementSpot() is not { } spot) return new(false, null, $"no safe spot to place a {what}");
        var p = await client.PlaceBlockAsync(spot.X, spot.Y, spot.Z, blockId, ct).ConfigureAwait(false);
        if (!p.Ok) return new(false, null, $"couldn't place {what}: {p.Message}");
        var note = $"placed {what} at {Locations.Fmt(spot)}";
        return new(true, spot, "ok", crafted != null ? crafted + ", " + note : note);
    }

    /// <summary>An air cell next to the bot (not its own column) with solid ground under it and air above, nearest first.</summary>
    public BlockPos? FindPlacementSpot()
    {
        var w = client.World;
        var feet = w.Player.Position.ToBlockPos();
        var candidates = new List<BlockPos>();
        for (int dx = -2; dx <= 2; dx++)
        for (int dz = -2; dz <= 2; dz++)
        {
            if (dx == 0 && dz == 0) continue;
            for (int dy = 0; dy >= -1; dy--)
                candidates.Add(new BlockPos(feet.X + dx, feet.Y + dy, feet.Z + dz));
        }
        foreach (var c in candidates.OrderBy(c => c.DistanceSquaredTo(feet)))
        {
            if (w.GetBlock(c) != 0 || w.GetBlock(c.Up()) != 0) continue;
            if (!w.Info(w.GetBlock(c.Down())).Solid) continue;
            return c;
        }
        return null;
    }
}

/// <summary>Short lower-case names for results.</summary>
public static class Names
{
    public static string Item(int id, int damage = 0) => Items.DisplayName(id, damage).ToLowerInvariant();
    public static string Block(int id) => Blocks.Exists(id) ? Blocks.Get(id).DisplayName.ToLowerInvariant() : "block " + id;
}
