using System.Diagnostics;
using MineClaw.Agent.Rag;
using MineClaw.Bot.Game;
using MineClaw.Client.Trees;
using MineClaw.Data;
using MineClaw.Data.Versioning;
using MineClaw.World;

namespace MineClaw.Bot.GameTools;

/// <summary>Outcome of one acquisition: one terse line; <see cref="Plan"/> is the announced plan of a non-trivial one.</summary>
public sealed record AcquireResult(bool Ok, string Text, string? Plan = null)
{
    public override string ToString() => (Ok ? "ok: " : "failed: ") + Text;
}

/// <summary>
/// Self-reliant prerequisite planning (zero tokens, no model call per step). Resolves a job's need by cost, cheapest
/// reliable first: (a) the inventory, (b) known chests (WorldIndexer snapshots), (c) a hive member's spare (handoff seam),
/// (d) crafting from inventory + chest materials (<see cref="Crafter"/>: RecipePlanner, table placement), (e) a bootstrap
/// from scratch — logs (chest logs, a tree the stewardship rule allows, a sapling from storage so the felled tree is
/// replanted, a hive handoff of logs) → planks → sticks → table → wood tool → 3 cobble at the generator → stone tool —
/// and (f) asking a player, with "I stop" as the default. Stone is preferred whenever cobble is reachable (on hand, in a
/// chest or at the gen). Only natural/resource blocks are ever taken: living trees, generator cells and chest contents;
/// protected blocks (builds, claims) are never dug, the last trees never felled. Bounded by steps and time.
/// </summary>
public sealed class AcquisitionPlanner(IGameClient c, AcquisitionSources sources)
{
    private readonly AcquisitionSources _s = sources;
    private readonly List<string> _done = new();
    private readonly Stopwatch _clock = new();
    private int _steps, _depth;
    private string? _plan;
    /// <summary>The resolver's exact shortfall of the last failed obtain/craft ("need 1 more log for a chest; none in ...").</summary>
    private string? _shortfall;

    private AcquisitionLimits L => _s.Limits;
    private WorldState W => c.World;
    private int Inv(int id) => W.Inventory.CountItem(id);
    /// <summary>Logs of every wood (1.12.2's acacia/dark oak are log2).</summary>
    private int Logs() => Inv(Blocks.Log) + Inv(Ids1122.Log2);
    /// <summary>A normal world: stone is terrain (it drops cobblestone), so cobble is mined, not dug at a generator.</summary>
    private static bool NaturalStone => !WorldMode.Current.GeneratorStone;
    private Crafter NewCrafter() => new(c) { Observations = _s.Observations };

    private sealed class OutOfBudget(string why) : Exception(why);

    // ------------------------------------------------------------------ entry points

    /// <summary>Gets <paramref name="need"/> (tool kind or item); never throws for game failures.</summary>
    public async Task<AcquireResult> AcquireAsync(Need need, CancellationToken ct)
    {
        bool top = _depth == 0;
        if (top) { _done.Clear(); _steps = 0; _plan = null; _shortfall = null; _clock.Restart(); }
        _depth++;
        try
        {
            var (got, kind) = await GetNeedAsync(need, ct).ConfigureAwait(false);
            if (got == null) NoteToolShortfall(kind);
            return Result(top, got != null, got ?? _shortfall ?? NoneText(need));
        }
        catch (OutOfBudget e) { return Result(top, false, e.Message); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return Result(top, false, "cancelled"); }
        finally
        {
            _depth--;
            if (c.World.Inventory.OpenWindow != null && _depth == 0)
                try { await c.CloseWindowAsync(CancellationToken.None).ConfigureAwait(false); } catch (Exception) { /* best effort */ }
        }
    }

    /// <summary>What a stone one of the tool kind still lacks, when nothing better explained the miss.</summary>
    private void NoteToolShortfall(Loadout.Kind? kind)
    {
        if (_shortfall == null && kind is { Craftable.Length: > 0 }) _shortfall = NewCrafter().Shortfall(kind.Craftable[0], -1, 1);
    }

    private static string NoneText(Need need) =>
        $"no {need} (inventory, chests, hive, crafting{(need.Depth == AcquireDepth.Full ? ", bootstrap" : "")})";

    /// <summary>A tool kind or an item (by name, with its variant); an unknown name is out of budget.</summary>
    private async Task<(string? Got, Loadout.Kind? Kind)> GetNeedAsync(Need need, CancellationToken ct)
    {
        if (Loadout.Kinds.TryGetValue(need.What.Trim(), out var kind)) return (await ToolAsync(kind, need, ct).ConfigureAwait(false), kind);
        if (Items.ByNameWithDamage(need.What.Trim()) is not { } item) throw new OutOfBudget($"unknown item '{need.What}'");
        return (await ItemAsync(item.Item.Id, Math.Max(1, need.Count), need.Depth, ct, item.Damage).ConfigureAwait(false), null);
    }

    /// <summary>The top-level line: "plan | steps done; got …" (a nested acquire returns its text as is).</summary>
    private AcquireResult Result(bool top, bool ok, string text)
    {
        if (!top) return new AcquireResult(ok, text, _plan);
        var steps = _done.Count > 0 && _plan != null ? " | " + string.Join(", ", _done.Distinct().Take(8)) : "";
        var line = _plan != null ? $"{_plan}{steps}; {(ok ? "got " : "")}{text}" : text;
        return new AcquireResult(ok, line, _plan);
    }

    /// <summary>Skill preconditions: one of <paramref name="ids"/> (best first), e.g. any hoe, a bucket, torches.</summary>
    public async Task<AcquireResult> AcquireAnyAsync(IReadOnlyList<int> ids, CancellationToken ct, AcquireDepth depth = AcquireDepth.Full)
    {
        if (ids.Count == 0) return new AcquireResult(true, "nothing needed");
        if (ids.Any(id => Inv(id) > 0)) return new AcquireResult(true, "had it");
        var kind = Loadout.Kinds.Values.FirstOrDefault(k => ids.All(k.Ids.Contains) || k.Ids.All(ids.Contains));
        if (kind != null)
        {
            int min = ids.Where(Items.Exists).Select(Level).DefaultIfEmpty(0).Min();
            return await AcquireAsync(new Need(kind.Name, 1, Math.Max(0, min), depth), ct).ConfigureAwait(false);
        }
        AcquireResult? last = null;
        foreach (var id in ids.Where(Items.Exists).Take(3))
        {
            last = await AcquireAsync(new Need(Items.Get(id).Name, 1, 0, id == ids[0] ? depth : AcquireDepth.Cheap), ct).ConfigureAwait(false);
            if (last.Ok) return last;
        }
        return last ?? new AcquireResult(false, "unknown items");
    }

    /// <summary>
    /// "Take wood now and upgrade during the job": holding only a wood-tier tool of <paramref name="kindName"/> with cobble and
    /// stick material on hand, craft the stone one (one craft, no walking for materials). Null = nothing done.
    /// </summary>
    public async Task<string?> UpgradeIfCheapAsync(string kindName, CancellationToken ct)
    {
        if (!Loadout.Kinds.TryGetValue(kindName, out var k) || k.Craftable.Length < 2) return null;
        int stone = k.Craftable[0];
        if (!k.Ids.Any(id => Inv(id) > 0) || k.Ids.Any(id => Inv(id) > 0 && Level(id) >= 1)) return null;
        if (!PlanCompleteFromInventory(stone) || !TableAvailable(k)) return null;
        var r = await NewCrafter().CraftAsync(Items.Get(stone).Name, 1, ct).ConfigureAwait(false);
        return r.Ok && Inv(stone) > 0 ? "upgraded to " + Names.Item(stone) : null;
    }

    /// <summary>A crafting table in reach or carried, or the wood to make one (plus sticks when short).</summary>
    private bool TableAvailable(Loadout.Kind k) =>
        W.FindNearestBlock(W.Player.Position, c.StationSearchRadius, Blocks.Workbench) != null || Inv(Blocks.Workbench) > 0
        || WoodUnits(false) >= 4 + (Inv(Items.Stick) >= SticksFor(k) ? 0 : 2);

    // ------------------------------------------------------------------ tools

    private static int Level(int id) => Tools.HarvestLevel(Items.Get(id).ToolTier);

    private bool Has(Loadout.Kind k, int minTier) =>
        k.Name == "food" ? Loadout.Have(W, k) > 0 : k.Ids.Any(id => Usable(id) && Level(id) >= minTier);

    /// <summary>A tool this close to breaking is due for replacement: "ensure pickaxe" with 1/131 left gets a new one first
    /// (live 10-28 02:07: "pickaxe 1/131, no cobble or sticks" while the chests held 300+ cobble and sticks).</summary>
    public const int WornUses = 3;

    /// <summary>Carried, and (for a damageable tool) with more than <see cref="WornUses"/> uses left in some stack.</summary>
    private bool Usable(int id)
    {
        if (Inv(id) <= 0) return false;
        int max = Items.Exists(id) ? Items.Get(id).MaxDamage : 0;
        return max <= 0 || W.Inventory.PlayerSlots().Any(s => !s.IsEmpty && s.Id == id && max - s.Damage > WornUses);
    }

    /// <summary>"stone pickaxe (worn: 1/131 left)" for a carried, nearly broken tool of <paramref name="k"/>, else null.</summary>
    private string? Worn(Loadout.Kind k)
    {
        foreach (var s in W.Inventory.PlayerSlots())
        {
            if (s.IsEmpty || !k.Ids.Contains(s.Id) || !Items.Exists(s.Id) || Items.Get(s.Id).MaxDamage <= 0) continue;
            int max = Items.Get(s.Id).MaxDamage;
            return $"{Names.Item(s.Id)} (worn: {Math.Max(0, max - s.Damage)}/{max} left)";
        }
        return null;
    }

    private int[] Acceptable(Loadout.Kind k, int minTier) => k.Name == "food" ? k.Ids : k.Ids.Where(id => Level(id) >= minTier).ToArray();

    private static string Short(Loadout.Kind k) => k.Name == "pickaxe" ? "pick" : k.Name;

    private static int HeadCount(Loadout.Kind k) => k.Name switch { "shovel" => 1, "sword" or "hoe" => 2, _ => 3 };

    private static int SticksFor(Loadout.Kind k) => k.Name == "sword" ? 1 : 2;

    private async Task<string?> ToolAsync(Loadout.Kind k, Need need, CancellationToken ct)
    {
        int min = Math.Max(0, need.MinTier);
        if (Has(k, min)) return "had " + Names.Item(k.Ids.First(Usable));
        // Only a worn one on hand: replace it the usual way (storage, craft from chest cobble + sticks, hive, bootstrap), but
        // never ask a player for it: the worn tool still works meanwhile.
        if (k.Name != "food" && Worn(k) is { } worn)
            return await ToolCoreAsync(k, need with { Depth = AcquireDepth.Cheap }, min, ct).ConfigureAwait(false) is { } got
                ? got + $" (replacing {worn})" : "had " + worn + ": no replacement in storage or materials yet";
        return await ToolCoreAsync(k, need, min, ct).ConfigureAwait(false);
    }

    private async Task<string?> ToolCoreAsync(Loadout.Kind k, Need need, int min, CancellationToken ct)
    {
        var ok = Acceptable(k, min);
        if (ok.Length == 0) return null;
        int stone = k.Craftable.Length > 0 ? k.Craftable[0] : 0, wood = k.Craftable.Length > 1 ? k.Craftable[1] : 0;

        if (await StoredOrCraftedNowAsync(k, ok, min, stone, ct).ConfigureAwait(false) is { } quick) return quick;
        if (k.Name == "food") return await FoodToolAsync(k, need, ok, min, ct).ConfigureAwait(false);

        // (c) a hive member's spare (main had 2 picks while 67 and 69 had none).
        if (await HandoffAsync(k.Name, ok, 1, () => Has(k, min), ct).ConfigureAwait(false) is { } giver) return giver;

        if (stone == 0) return need.Depth == AcquireDepth.Full ? await AskAsync(k.Name, Short(k), () => Has(k, min), ct).ConfigureAwait(false) : null;
        return await CraftOrBootstrapAsync(k, need, ok, min, stone, wood, ct).ConfigureAwait(false);
    }

    /// <summary>(b) a chest — unless that's a wood tool and stone can be crafted right here from the inventory (best tier first).</summary>
    private async Task<string?> StoredOrCraftedNowAsync(Loadout.Kind k, int[] ok, int min, int stone, CancellationToken ct)
    {
        bool stoneNow = stone != 0 && min <= 1 && PlanCompleteFromInventory(stone);
        if (TakeFromStock(k, ok, stoneNow) && await WithdrawAsync(ok, 1, ct).ConfigureAwait(false) is { } from && Has(k, min)) return from;
        return stoneNow && await CraftAsync(stone, ct).ConfigureAwait(false) ? Crafted(stone) : null;
    }

    /// <summary>A chest holds one, and it isn't a wood tool when stone can be crafted right now. Food: never (FoodSupply below;
    /// reserved items stay).</summary>
    private bool TakeFromStock(Loadout.Kind k, int[] ok, bool stoneNow)
    {
        var stocked = k.Name == "food" ? null : ChestBest(ok);
        return stocked is { } best && !(stoneNow && Level(best) < 1);
    }

    /// <summary>The food resolver before bothering the hive: chest food, bread from chest wheat, stew, cooking (FoodSupply); then a feeder, then a player.</summary>
    private async Task<string?> FoodToolAsync(Loadout.Kind k, Need need, int[] ok, int min, CancellationToken ct)
    {
        var food = await new FoodSupply(c, _s) { OnStep = Step }.ObtainAsync(ct).ConfigureAwait(false);
        if (food.Ok) return food.Text;
        _shortfall = food.Text;
        if (await HandoffAsync(k.Name, ok, k.Want, () => Has(k, min), ct).ConfigureAwait(false) is { } feeder) return feeder;
        return need.Depth == AcquireDepth.Full ? await AskAsync(k.Name, "food", () => Has(k, min), ct).ConfigureAwait(false) : null;
    }

    /// <summary>(d) craft from inventory + chests: stone when cobble is reachable there, else (cheap mode) wood; (e) bootstrap from
    /// scratch; (f) a player, last.</summary>
    private async Task<string?> CraftOrBootstrapAsync(Loadout.Kind k, Need need, int[] ok, int min, int stone, int wood, CancellationToken ct)
    {
        if (StoneInReach(k, min) && await CraftAsync(stone, ct).ConfigureAwait(false)) return Crafted(stone);
        if (need.Depth == AcquireDepth.Cheap)
            return WoodInReach(k, min, wood) && await CraftAsync(wood, ct).ConfigureAwait(false) ? Crafted(wood) : null;
        if (min >= 2) return await AskAsync(k.Name, Names.Item(ok[0]), () => Has(k, min), ct).ConfigureAwait(false); // iron+: nothing to bootstrap

        var made = await BootstrapAsync(k, min, stone, wood, ct).ConfigureAwait(false);
        if (made != null) return made;
        return await AskAsync(k.Name, $"{Short(k)} (or 3 logs)", () => Has(k, min), ct).ConfigureAwait(false);
    }

    /// <summary>A stone tool can be crafted from inventory + chests: cobble for the head and wood for the sticks.</summary>
    private bool StoneInReach(Loadout.Kind k, int min)
    {
        int cobbleReach = Inv(Blocks.Cobblestone) + ChestStock(Blocks.Cobblestone);
        return min <= 1 && cobbleReach >= HeadCount(k) && WoodUnits(withChests: true) >= SticksPlanks(k);
    }

    /// <summary>(Cheap mode) a wood tool will do and inventory + chests hold the wood for its head and sticks.</summary>
    private bool WoodInReach(Loadout.Kind k, int min, int wood) =>
        min == 0 && wood != 0 && WoodUnits(withChests: true) >= HeadCount(k) + SticksPlanks(k);

    /// <summary>Planks equivalent of the sticks a tool needs (2 planks = 4 sticks; sticks on hand count).</summary>
    private int SticksPlanks(Loadout.Kind k) => Inv(Items.Stick) >= SticksFor(k) ? 0 : 2;

    /// <summary>A bootstrap's numbers: the tool's head count, what cobble is in reach, whether it goes via stone, a table, a wood
    /// pick first, and the planks it all takes.</summary>
    private sealed record BootPlan(bool Pickaxe, int Head, int CobbleReach, bool ViaStone, bool TableNear, bool NeedWoodPick, bool WoodTool,
        int Planks, bool WoodShort);

    private async Task<string?> BootstrapAsync(Loadout.Kind k, int min, int stone, int wood, CancellationToken ct)
    {
        var gen = FindGeneratorCell();
        if (PlanBootstrap(k, min, gen) is not { } plan) return null;
        // The plan, announced once: "no pick, making one: logs→planks→sticks→wood pick→3 cobble→stone pick".
        Announce($"no {Short(k)}, making one: {string.Join("→", BootSteps(k, plan))}");
        if (plan.WoodShort && !await WoodAsync(plan.Planks, ct).ConfigureAwait(false)) return null;
        var (done, result) = await BootWoodToolAsync(plan, wood, ct).ConfigureAwait(false);
        if (done) return result;
        return await BootStoneAsync(k, min, plan.Head, stone, gen, ct).ConfigureAwait(false);
    }

    private BootPlan? PlanBootstrap(Loadout.Kind k, int min, BlockPos? gen)
    {
        bool pickaxe = k.Name == "pickaxe";
        int head = HeadCount(k);
        int cobbleReach = Inv(Blocks.Cobblestone) + ChestStock(Blocks.Cobblestone);
        bool haveWoodPick = Loadout.Kinds["pickaxe"].Ids.Any(id => Inv(id) > 0);
        bool viaStone = ViaStone(min, cobbleReach, head, gen);
        if (min == 1 && !viaStone) return null;
        bool tableNear = W.FindNearestBlock(W.Player.Position, c.StationSearchRadius, Blocks.Workbench) != null || Inv(Blocks.Workbench) > 0;
        bool needWoodPick = viaStone && cobbleReach < head && !haveWoodPick;   // to dig the gen's cobble
        bool woodTool = !viaStone || (pickaxe && needWoodPick);
        int planks = BootPlanks(head, pickaxe, tableNear, needWoodPick, woodTool);
        return new BootPlan(pickaxe, head, cobbleReach, viaStone, tableNear, needWoodPick, woodTool, planks, WoodUnits(withChests: false) < planks);
    }

    /// <summary>Stone whenever cobble is reachable: on hand / in a chest, at the gen or (normal world) natural stone (with a pick,
    /// or the wood pick made first).</summary>
    private bool ViaStone(int min, int cobbleReach, int head, BlockPos? gen) => min <= 1 && (cobbleReach >= head || gen != null || NaturalStone);

    private static int BootPlanks(int head, bool pickaxe, bool tableNear, bool needWoodPick, bool woodTool) =>
        (woodTool ? head : 0) + (needWoodPick && !pickaxe ? 3 : 0) + 2 + (tableNear ? 0 : 4);

    private List<string> BootSteps(Loadout.Kind k, BootPlan plan)
    {
        var parts = new List<string>();
        if (plan.WoodShort) parts.Add("logs");
        parts.Add("planks");
        if (Inv(Items.Stick) < 4) parts.Add("sticks");
        if (!plan.TableNear) parts.Add("table");
        if (plan.WoodTool) parts.Add("wood " + Short(k));
        else if (plan.NeedWoodPick) parts.Add("wood pick");
        if (!plan.ViaStone) return parts;
        if (plan.CobbleReach < plan.Head) parts.Add($"{plan.Head} cobble");
        parts.Add("stone " + Short(k));
        return parts;
    }

    /// <summary>The wood tool (done unless it goes on to stone), or the wood pick that digs the cobble. Done with null = failed.</summary>
    private async Task<(bool Done, string? Result)> BootWoodToolAsync(BootPlan plan, int wood, CancellationToken ct)
    {
        if (plan.WoodTool)
        {
            int woodId = plan.Pickaxe ? Items.WoodenPickaxe : wood;
            if (!await CraftAsync(woodId, ct).ConfigureAwait(false)) { Note("couldn't craft " + Names.Item(woodId)); return (true, null); }
            Note("crafted " + Names.Item(woodId));
            if (!plan.ViaStone) return (true, "crafted " + Names.Item(woodId));
        }
        else if (plan.NeedWoodPick)
        {
            if (!await CraftAsync(Items.WoodenPickaxe, ct).ConfigureAwait(false)) { Note("couldn't craft a wooden pickaxe"); return (true, null); }
            Note("crafted wooden pickaxe");
        }
        return (false, null);
    }

    /// <summary>The cobble (chest, else the gen), then the stone tool.</summary>
    private async Task<string?> BootStoneAsync(Loadout.Kind k, int min, int head, int stone, BlockPos? gen, CancellationToken ct)
    {
        if (Inv(Blocks.Cobblestone) < head)
        {
            if (ChestStock(Blocks.Cobblestone) > 0) await WithdrawAsync([Blocks.Cobblestone], head - Inv(Blocks.Cobblestone), ct).ConfigureAwait(false);
            if (Inv(Blocks.Cobblestone) < head && !await GenCobbleAsync(head, gen, ct).ConfigureAwait(false)) return HadAnyway(k, min, "no cobble for stone yet");
        }
        if (!await CraftAsync(stone, ct).ConfigureAwait(false)) return HadAnyway(k, min, "stone craft failed");
        Note("crafted " + Names.Item(stone));
        return "crafted " + Names.Item(stone);
    }

    /// <summary>A tool of the kind on hand after all (the wood one), saying why it isn't stone; else null.</summary>
    private string? HadAnyway(Loadout.Kind k, int min, string why) => Has(k, min) ? Names.Item(k.Ids.First(id => Inv(id) > 0)) + $" ({why})" : null;

    // ------------------------------------------------------------------ items

    /// <summary>Whether an item's damage picks a different item (slab type, wool colour, log wood, dye, coal/charcoal).</summary>
    private static bool IsVariant(int id) => Items.VariantName(id, 0) != Items.VariantName(id, 1);

    /// <param name="damage">The exact variant asked for (cobblestone slab = 44:3, never stone slab 44:0, which needs smelted
    /// stone: live 10-28 a tower's 256 cobblestone slabs were sought as stone slabs, "I need a 256 stone slab").</param>
    private async Task<string?> ItemAsync(int id, int count, AcquireDepth depth, CancellationToken ct, int damage = -1)
    {
        int? dmg = damage >= 0 && IsVariant(id) ? damage : null;
        int Have() => W.Inventory.CountItem(id, dmg);
        string name = Names.Item(id, dmg ?? 0);
        if (Have() >= count) return $"had {count} {name}";
        // (b)+(d) the shared resolver: every known chest nearest first, the exact variant, as much as the inventory holds (live
        // 10-28: 500+ cobble in three chests, yet "I need a 256 cobblestone"), then the whole recipe tree planned over
        // inventory + chests (258 cobble for 256 cobblestone slabs, logs→planks for a chest) and crafted.
        var got = await new Obtainer(c, _s) { OnStep = Step }.ObtainAsync(id, dmg ?? -1, count, ct).ConfigureAwait(false);
        if (got.Ok) return got.Text;
        _shortfall = got.Text;
        if (got.Text.Contains("inventory full", StringComparison.Ordinal)) return null;
        // (c) hive
        if (await HandoffAsync(name, [id], count - Have(), () => Have() >= count, ct).ConfigureAwait(false) is { } giver) return giver;
        return depth == AcquireDepth.Full ? await GatherOrAskAsync(id, dmg, count, name, ct).ConfigureAwait(false) : null;
    }

    /// <summary>(e) raw wood/cobble missing: gather them first (and craft); else ask a player.</summary>
    private async Task<string?> GatherOrAskAsync(int id, int? dmg, int count, string name, CancellationToken ct)
    {
        string plural = count > 1 ? $"{count} {Plural(name)}" : $"a {name}";
        var gathered = id is Blocks.Cobblestone or Blocks.Log
            ? await GatherRawAsync(id, count, ct).ConfigureAwait(false)
            : await GatherAndCraftAsync(id, dmg, count, name, ct).ConfigureAwait(false);
        if (gathered != null) return gathered;
        return await AskAsync(name, plural, () => W.Inventory.CountItem(id, dmg) >= count, ct).ConfigureAwait(false);
    }

    private async Task<string?> GatherRawAsync(int id, int count, CancellationToken ct)
    {
        if (id == Blocks.Cobblestone)
            return await GenCobbleAsync(count, FindGeneratorCell(), ct, fetchPick: true).ConfigureAwait(false)
                ? NaturalStone ? $"mined stone for {count} cobblestone" : $"dug {count} cobblestone at the gen"
                : null;
        return await LogsAsync(count, ct).ConfigureAwait(false) && Inv(id) >= count ? $"got {count} logs" : null;
    }

    /// <summary>A wood product from logs, cobblestone slabs/stairs from the gen when storage has too little cobble, or (normal
    /// world) what a craft lacks gathered first.</summary>
    private async Task<string?> GatherAndCraftAsync(int id, int? dmg, int count, string name, CancellationToken ct)
    {
        if (Recipes.ForOutput(id, dmg ?? -1).Count == 0) return null;
        string craftName = dmg is { } d0 ? Items.VariantName(id, d0) : Items.Get(id).Name;
        if (IsWoodProduct(id)) return await WoodProductAsync(id, count, name, ct).ConfigureAwait(false);
        if (dmg is { } d1 && CobbleProduct(id, d1) is { } perCobble)
            return await CobbleProductAsync(id, d1, perCobble, count, name, craftName, ct).ConfigureAwait(false);
        int have = W.Inventory.CountItem(id, dmg);
        return NaturalStone && await GatherThenCraftAsync(id, dmg, name, craftName, count - have, ct).ConfigureAwait(false)
               && W.Inventory.CountItem(id, dmg) >= count ? $"crafted {count} {name}" : null;
    }

    private async Task<string?> WoodProductAsync(int id, int count, string name, CancellationToken ct)
    {
        Announce($"no {name}, making it: logs→{name}");
        return await WoodAsync(WoodNeedFor(id, count), ct).ConfigureAwait(false)
               && await CraftAsync(id, ct, count - Inv(id)).ConfigureAwait(false) && Inv(id) >= count ? $"crafted {name}" : null;
    }

    private async Task<string?> CobbleProductAsync(int id, int dmg, (int In, int Out) perCobble, int count, string name, string craftName,
        CancellationToken ct)
    {
        int Have() => W.Inventory.CountItem(id, dmg);
        int cobble = (count - Have() + perCobble.Out - 1) / perCobble.Out * perCobble.In;
        Announce($"no {name}, making it: cobble→{name}");
        return await GenCobbleAsync(cobble, FindGeneratorCell(), ct, fetchPick: true).ConfigureAwait(false)
               && await CraftVariantAsync(id, dmg, craftName, count - Have(), ct).ConfigureAwait(false) && Have() >= count ? $"crafted {count} {name}" : null;
    }

    /// <summary>
    /// A normal world: the raw cobble and wood a craft lacks (furnace = 8 cobble, stone tools, smooth stone via the furnace)
    /// are gathered (stone mined, a tree felled), then it is crafted. False when it lacks anything else (iron, string...).
    /// </summary>
    private async Task<bool> GatherThenCraftAsync(int id, int? dmg, string name, string craftName, int need, CancellationToken ct)
    {
        var inv = W.Inventory.PlayerSlots().Skip(9).Where(s => !s.IsEmpty).ToList();
        var plan = RecipePlanner.Plan(id, Math.Max(1, need), inv, dmg ?? -1, new PlannerOptions { AllowSmelting = true, HasCraftingTable = true });
        if (RawShortfall(plan.Missing) is not { } raw) return false;
        var (cobble, wood) = raw;
        var steps = new List<string>();
        if (wood > 0) steps.Add("logs");
        if (cobble > 0) steps.Add($"{cobble} cobble (mine stone)");
        if (steps.Count > 0) Announce($"no {name}, making it: {string.Join("→", steps)}→{name}");
        if (wood > 0 && !await WoodAsync(WoodUnits(false) + wood, ct).ConfigureAwait(false)) return false;
        if (cobble > 0 && !await GenCobbleAsync(Inv(Blocks.Cobblestone) + cobble, FindGeneratorCell(), ct, fetchPick: true).ConfigureAwait(false))
            return false;
        return await CraftVariantAsync(id, dmg, craftName, need, ct).ConfigureAwait(false);
    }

    /// <summary>The cobble and wood units (planks) the missing materials come down to; null when something else is missing.</summary>
    private static (int Cobble, int Wood)? RawShortfall(IReadOnlyList<MaterialNeed> missing)
    {
        int cobble = 0, wood = 0;
        foreach (var m in missing)
        {
            if (m.Id == Blocks.Cobblestone) cobble += m.Count;
            else if (WoodUnitsOf(m) is { } units) wood += units;
            else return null;
        }
        return (cobble, wood);
    }

    private static int? WoodUnitsOf(MaterialNeed m) =>
        m.Id is Blocks.Log or Ids1122.Log2 ? 4 * m.Count : m.Id == Blocks.Planks ? m.Count : m.Id == Items.Stick ? (m.Count + 1) / 2 : null;

    /// <summary>Cobble-only recipes: (cobble in, items out) per craft.</summary>
    private static (int In, int Out)? CobbleProduct(int id, int damage) => (id, damage) switch
    {
        (Blocks.Slab, 3) => (3, 3),
        _ => null,
    };

    private static string Plural(string name) => name.EndsWith('s') || name.EndsWith("glass", StringComparison.Ordinal) ? name : name + "s";

    private async Task<bool> CraftVariantAsync(int id, int? damage, string craftName, int count, CancellationToken ct)
    {
        Step("craft");
        int before = W.Inventory.CountItem(id, damage);
        var r = await NewCrafter().CraftAsync(craftName, Math.Max(1, count), ct).ConfigureAwait(false);
        if (!r.Ok) { Note("craft: " + Trim(r.Message)); _shortfall = r.Message; }
        return r.Ok && W.Inventory.CountItem(id, damage) > before;
    }

    private static bool IsWoodProduct(int id) => id is Blocks.Planks or Items.Stick or Blocks.Workbench or Blocks.Chest;

    private static int WoodNeedFor(int id, int count) => id switch
    {
        Items.Stick => (count + 3) / 4 * 2,
        Blocks.Workbench => 4 * count,
        Blocks.Chest => 8 * count,
        _ => count,
    };

    // ------------------------------------------------------------------ wood

    /// <summary>Planks equivalent on hand (and in known chests): planks + 4/log + 1/2 stick.</summary>
    private int WoodUnits(bool withChests)
    {
        int u = Inv(Blocks.Planks) + 4 * Logs() + Inv(Items.Stick) / 2;
        if (withChests) u += ChestStock(Blocks.Planks) + 4 * (ChestStock(Blocks.Log) + ChestStock(Ids1122.Log2)) + ChestStock(Items.Stick) / 2;
        return u;
    }

    /// <summary>At least <paramref name="planks"/> planks' worth of wood on hand: chest wood, else logs from a tree / hive.</summary>
    private async Task<bool> WoodAsync(int planks, CancellationToken ct)
    {
        if (WoodUnits(false) >= planks) return true;
        foreach (var id in new[] { Blocks.Log, Ids1122.Log2, Blocks.Planks })
        {
            if (WoodUnits(false) >= planks) break;
            int stock = ChestStock(id);
            if (stock <= 0) continue;
            int short_ = planks - WoodUnits(false);
            int n = id != Blocks.Planks ? (short_ + 3) / 4 : short_;
            await WithdrawAsync([id], Math.Min(stock, n), ct).ConfigureAwait(false);
        }
        if (WoodUnits(false) >= planks) return true;
        int logs = (planks - WoodUnits(false) + 3) / 4;
        return await LogsAsync(Logs() + logs, ct).ConfigureAwait(false) || WoodUnits(false) >= planks;
    }

    /// <summary>
    /// Logs up to <paramref name="want"/> in the inventory: fell a tree the stewardship rule allows (a sapling from storage
    /// makes the replant certain), else a hive handoff of logs. Never the last trees, never a protected (build) tree.
    /// </summary>
    private async Task<bool> LogsAsync(int want, CancellationToken ct)
    {
        if (Logs() >= want) return true;
        var me = W.Player.Position;
        TreeCensus census;
        try { census = TreeStewardship.Census(W, me); }
        catch (Exception) { census = new TreeCensus([], []); }
        var trees = census.Trees.Where(t => !t.Logs.Any(p => c.IsProtected?.Invoke(p, Blocks.Log) == true))
            .OrderBy(t => t.Base.Center.DistanceTo(me)).ToList();
        if (trees.Count > 0)
        {
            var tree = trees[0];
            string? refusal = TreeStewardship.Refusal(census, tree, Inv(Blocks.Sapling), null, _s.MinTrees);
            if (refusal != null && ChestStock(Blocks.Sapling) > 0)
            {
                await WithdrawAsync([Blocks.Sapling], 1, ct).ConfigureAwait(false);
                refusal = TreeStewardship.Refusal(census, tree, Inv(Blocks.Sapling), null, _s.MinTrees);
            }
            if (refusal == null)
            {
                Step("chop tree");
                int before = Logs();
                var chopper = GameClients.Unclaimed(c);
                var r = await chopper.ChopTreeAsync(tree.Base, null, ct).ConfigureAwait(false);
                int got = Logs() - before;
                Note(got > 0 ? $"chopped a tree ({got} logs)" : "chop: " + Trim(r.Message));
                if (Logs() >= want) return true;
            }
            else Note("won't chop: " + refusal);
        }
        else Note("no tree to chop");
        int need = want - Logs();
        if (await HandoffAsync("logs", [Blocks.Log, Ids1122.Log2, Blocks.Planks], need, () => Logs() >= want || WoodUnits(false) >= 4 * want, ct).ConfigureAwait(false) != null)
            return true;
        return Logs() >= want;
    }

    // ------------------------------------------------------------------ cobble at the gen

    private BlockPos? FindGeneratorCell()
    {
        IEnumerable<BlockPos> cells;
        try
        {
            // Geometry (lava + water beside it) names a gen only on skyblock: a normal world's caves are full of it.
            cells = _s.GeneratorCells?.Invoke()
                    ?? (NaturalStone ? [] : Perception.BlockDescriber.GenCellsNear(W, W.Player.Position.ToBlockPos(), 16));
        }
        catch (Exception) { return null; }
        var me = W.Player.Position;
        // A generator cell is a resource block; a protected one (a build's wall that happens to touch lava+water) is not.
        return cells.Where(p => c.IsProtected?.Invoke(p, W.GetBlock(p)) != true)
            .OrderBy(p => p.Center.DistanceTo(me)).Cast<BlockPos?>().FirstOrDefault();
    }

    /// <summary>Digs cobble at the generator (a normal world: mines natural stone first) until <paramref name="want"/> are on
    /// hand (needs a pickaxe: fetched when asked).</summary>
    private async Task<bool> GenCobbleAsync(int want, BlockPos? gen, CancellationToken ct, bool fetchPick = false)
    {
        if (Inv(Blocks.Cobblestone) >= want) return true;
        if (gen is null && !NaturalStone) { Note("no generator known"); return false; }
        if (!Loadout.Kinds["pickaxe"].Ids.Any(id => Inv(id) > 0) && await FetchPickAsync(want, fetchPick, ct).ConfigureAwait(false) is { } settled)
            return settled;
        // A normal world mines natural stone first; the generator only when that wasn't enough.
        if (NaturalStone && (await MineStoneAsync(want, ct).ConfigureAwait(false) || gen is null)) return Inv(Blocks.Cobblestone) >= want;
        return await DigGenCobbleAsync(gen!.Value, want, ct).ConfigureAwait(false);
    }

    /// <summary>No pickaxe: fetch one when asked. False = can't dig; true = enough cobble came along; null = go on digging.</summary>
    private async Task<bool?> FetchPickAsync(int want, bool fetchPick, CancellationToken ct)
    {
        if (!fetchPick) return false;
        var pick = await AcquireAsync(new Need("pickaxe"), ct).ConfigureAwait(false);
        if (!pick.Ok) return false;
        return Inv(Blocks.Cobblestone) >= want ? true : null;
    }

    /// <summary>Mines natural stone toward <paramref name="want"/> cobble; true when that is enough.</summary>
    private async Task<bool> MineStoneAsync(int want, CancellationToken ct)
    {
        Step("mine stone");
        int had = Inv(Blocks.Cobblestone);
        var mined = await c.MineAsync("stone", Math.Min(256, want - had), 32, ct).ConfigureAwait(false);
        int dug = Inv(Blocks.Cobblestone) - had;
        Note(dug > 0 ? $"mined {dug} stone" : "mine stone: " + Trim(mined.Message));
        return Inv(Blocks.Cobblestone) >= want;
    }

    /// <summary>Digs the generator's free cell nearest <paramref name="gen"/> until <paramref name="want"/> cobble are on hand.</summary>
    private async Task<bool> DigGenCobbleAsync(BlockPos gen, int want, CancellationToken ct)
    {
        var (free, note) = GameToolSet.PickGeneratorCell(c, gen, _s.CellsNear);
        if (free is not { } at) { Note(note ?? "gen busy"); return false; }
        Step("gen cobble");
        int before = Inv(Blocks.Cobblestone);
        var r = await c.HarvestGeneratorAsync(at.X, at.Y, at.Z, want - before, ct).ConfigureAwait(false);
        int got = Inv(Blocks.Cobblestone) - before;
        Note(got > 0 ? $"dug {got} cobble" : "gen: " + Trim(r.Message));
        return Inv(Blocks.Cobblestone) >= want;
    }

    // ------------------------------------------------------------------ sources

    private int ChestStock(int id)
    {
        if (_s.Observations is not { } obs) return 0;
        try { return obs.FindItemInContainers(id, limit: 8).Sum(f => f.Count); }
        catch (Exception) { return 0; } // db busy
    }

    private int? ChestBest(int[] ids)
    {
        foreach (var id in ids) if (ChestStock(id) > 0) return id;
        return null;
    }

    /// <summary>Withdraws up to <paramref name="count"/> of the first stocked id (best first), nearest chest first.</summary>
    private async Task<string?> WithdrawAsync(int[] ids, int count, CancellationToken ct)
    {
        if (_s.Observations is not { } obs) return null;
        var me = W.Player.Position;
        foreach (var id in ids)
        {
            List<ItemLocation> found;
            try { found = obs.FindItemInContainers(id, limit: 6); }
            catch (Exception) { continue; }
            foreach (var f in found.Where(f => f.Container.Bounds != null)
                         .OrderBy(f => new Vec3d(f.Container.Bounds!.Value.MinX, f.Container.Bounds!.Value.MinY, f.Container.Bounds!.Value.MinZ).DistanceTo(me)).Take(3))
            {
                var b = f.Container.Bounds!.Value;
                var p = new BlockPos((int)Math.Floor(b.MinX), (int)Math.Floor(b.MinY), (int)Math.Floor(b.MinZ));
                Step("withdraw");
                int before = Inv(id);
                var r = await c.WithdrawAsync(id, Math.Max(1, Math.Min(count, f.Count)), p, null, ct).ConfigureAwait(false);
                int got = Inv(id) - before;
                if (r.Ok && got > 0)
                {
                    var line = $"{(got > 1 ? got + " " : "")}{Names.Item(id)} from {p.X},{p.Y},{p.Z}";
                    Note("took " + line);
                    return line;
                }
            }
        }
        return null;
    }

    private async Task<string?> HandoffAsync(string what, int[] ids, int count, Func<bool> satisfied, CancellationToken ct)
    {
        if (_s.RequestHandoff is not { } ask || ids.Length == 0) return null;
        Step("handoff");
        string? giver;
        try { giver = await ask(new HandoffAsk(what, ids, Math.Max(1, count)), ct).ConfigureAwait(false); }
        catch (Exception e) when (e is not OperationCanceledException) { giver = null; }
        if (giver == null) return null;
        if (!satisfied()) await c.CollectDropsAsync(6, ct).ConfigureAwait(false);
        if (!satisfied()) { Note($"{giver}'s handoff didn't arrive"); return null; }
        var line = $"{what} handed over by {giver}";
        Note(line);
        return line;
    }

    private async Task<bool> CraftAsync(int id, CancellationToken ct, int count = 1)
    {
        Step("craft");
        int before = Inv(id);
        var r = await NewCrafter().CraftAsync(Items.Get(id).Name, Math.Max(1, count), ct).ConfigureAwait(false);
        if (!r.Ok) _shortfall = r.Message;
        _took = r.Ok && r.Message.StartsWith("took ", StringComparison.Ordinal) ? r.Message[5..].Split("; ")[0] : null;
        return r.Ok && Inv(id) > before;
    }

    /// <summary>What the last craft withdrew ("1 oak wood from 3,65,2"), null = all from the inventory.</summary>
    private string? _took;

    /// <summary>"crafted stone pickaxe (took 1 oak wood from 3,65,2)": one line, the withdrawals the craft needed.</summary>
    private string Crafted(int id) => "crafted " + Names.Item(id) + (_took != null ? $" (took {_took})" : "");

    private bool PlanCompleteFromInventory(int id) =>
        RecipePlanner.Plan(id, 1, W.Inventory.PlayerSlots().Skip(9).Where(s => !s.IsEmpty).ToList(), -1,
            new PlannerOptions { AllowSmelting = false, HasCraftingTable = true }).Complete;

    /// <summary>(f) the last resort: one question with a default ("I stop"); an answer = look again (drops, inventory, chests).</summary>
    private async Task<string?> AskAsync(string what, string ask, Func<bool> satisfied, CancellationToken ct)
    {
        if (_s.AskPlayer is not { } f) return null;
        Step("ask");
        var q = AskQuestion(ask);
        string? answer;
        try { answer = await f(q, L.AskTimeout, ct).ConfigureAwait(false); }
        catch (Exception e) when (e is not OperationCanceledException) { answer = null; }
        if (answer == null) { Note("asked, no answer"); return null; }
        if (!satisfied()) await c.CollectDropsAsync(6, ct).ConfigureAwait(false);
        if (satisfied()) return $"{what} from a player";
        Note("asked: " + Trim(answer));
        return null;
    }

    /// <summary>"I need a stone pickaxe and can't get one (last trees, won't chop them). Can someone toss me one? (no answer in
    /// 60s: I'll stop)".</summary>
    private string AskQuestion(string ask)
    {
        var why = _done.LastOrDefault(d => d.StartsWith("won't chop", StringComparison.Ordinal)) is { } ? " (last trees, won't chop them)" : "";
        // "a cobblestone slab" / "256 cobblestone slabs" / "food": never "a 256 stone slab" (live 10-28).
        bool many = ask.Length > 0 && char.IsDigit(ask[0]) || ask == "food";
        ask = WithArticle(ask, many);
        return $"I need {ask} and can't get {(ask == "food" ? "it" : many ? "them" : "one")}{why}. Can someone toss me {(many ? "some" : "one")}? (no answer in {L.AskTimeout.TotalSeconds:0}s: I'll stop)";
    }

    private static string WithArticle(string ask, bool many) =>
        many || ask.StartsWith("a ", StringComparison.Ordinal) || ask.StartsWith("an ", StringComparison.Ordinal) ? ask
            : ("aeiou".Contains(ask[0]) ? "an " : "a ") + ask;

    // ------------------------------------------------------------------ bookkeeping

    private void Step(string what)
    {
        if (++_steps > L.MaxSteps) throw new OutOfBudget($"gave up: over {L.MaxSteps} steps ({what})");
        if (_clock.Elapsed > L.MaxTime) throw new OutOfBudget($"gave up: over {L.MaxTime.TotalMinutes:0.#} min ({what})");
    }

    private void Note(string s) => _done.Add(s);

    private void Announce(string plan)
    {
        if (_plan != null) return;
        _plan = plan;
        try { _s.Announce?.Invoke(plan); } catch (Exception) { /* log sink only */ }
    }

    private static string Trim(string s) => s.Length <= 60 ? s : s[..57] + "...";
}
