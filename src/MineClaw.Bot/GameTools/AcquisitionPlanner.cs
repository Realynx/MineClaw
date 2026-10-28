using System.Diagnostics;
using MineClaw.Agent.Rag;
using MineClaw.Bot.Game;
using MineClaw.Client.Trees;
using MineClaw.Data;
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

    private AcquisitionLimits L => _s.Limits;
    private WorldState W => c.World;
    private int Inv(int id) => W.Inventory.CountItem(id);
    private Crafter NewCrafter() => new(c) { Observations = _s.Observations };

    private sealed class OutOfBudget(string why) : Exception(why);

    // ------------------------------------------------------------------ entry points

    /// <summary>Gets <paramref name="need"/> (tool kind or item); never throws for game failures.</summary>
    public async Task<AcquireResult> AcquireAsync(Need need, CancellationToken ct)
    {
        bool top = _depth == 0;
        if (top) { _done.Clear(); _steps = 0; _plan = null; _clock.Restart(); }
        _depth++;
        try
        {
            string? got = Loadout.Kinds.TryGetValue(need.What.Trim(), out var kind)
                ? await ToolAsync(kind, need, ct).ConfigureAwait(false)
                : Items.ByNameWithDamage(need.What.Trim()) is { } item
                    ? await ItemAsync(item.Item.Id, Math.Max(1, need.Count), need.Depth, ct).ConfigureAwait(false)
                    : throw new OutOfBudget($"unknown item '{need.What}'");
            return Result(got != null, got ?? $"no {need} (inventory, chests, hive, crafting{(need.Depth == AcquireDepth.Full ? ", bootstrap" : "")})");
        }
        catch (OutOfBudget e) { return Result(false, e.Message); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return Result(false, "cancelled"); }
        finally
        {
            _depth--;
            if (c.World.Inventory.OpenWindow != null && _depth == 0)
                try { await c.CloseWindowAsync(CancellationToken.None).ConfigureAwait(false); } catch (Exception) { /* best effort */ }
        }

        AcquireResult Result(bool ok, string text)
        {
            if (!top) return new AcquireResult(ok, text, _plan);
            var steps = _done.Count > 0 && _plan != null ? " | " + string.Join(", ", _done.Distinct().Take(8)) : "";
            var line = _plan != null ? $"{_plan}{steps}; {(ok ? "got " : "")}{text}" : text;
            return new AcquireResult(ok, line, _plan);
        }
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
        if (!PlanCompleteFromInventory(stone)) return null;
        bool table = W.FindNearestBlock(W.Player.Position, c.StationSearchRadius, Blocks.Workbench) != null || Inv(Blocks.Workbench) > 0
                     || WoodUnits(false) >= 4 + (Inv(Items.Stick) >= SticksFor(k) ? 0 : 2);
        if (!table) return null;
        var r = await NewCrafter().CraftAsync(Items.Get(stone).Name, 1, ct).ConfigureAwait(false);
        return r.Ok && Inv(stone) > 0 ? "upgraded to " + Names.Item(stone) : null;
    }

    // ------------------------------------------------------------------ tools

    private static int Level(int id) => Tools.HarvestLevel(Items.Get(id).ToolTier);

    private bool Has(Loadout.Kind k, int minTier) =>
        k.Name == "food" ? Loadout.Have(W, k) > 0 : k.Ids.Any(id => Inv(id) > 0 && Level(id) >= minTier);

    private int[] Acceptable(Loadout.Kind k, int minTier) => k.Name == "food" ? k.Ids : k.Ids.Where(id => Level(id) >= minTier).ToArray();

    private static string Short(Loadout.Kind k) => k.Name == "pickaxe" ? "pick" : k.Name;

    private static int HeadCount(Loadout.Kind k) => k.Name switch { "shovel" => 1, "sword" or "hoe" => 2, _ => 3 };

    private static int SticksFor(Loadout.Kind k) => k.Name == "sword" ? 1 : 2;

    private async Task<string?> ToolAsync(Loadout.Kind k, Need need, CancellationToken ct)
    {
        int min = Math.Max(0, need.MinTier);
        if (Has(k, min)) return "had " + Names.Item(k.Ids.First(id => Inv(id) > 0));
        var ok = Acceptable(k, min);
        if (ok.Length == 0) return null;
        int stone = k.Craftable.Length > 0 ? k.Craftable[0] : 0, wood = k.Craftable.Length > 1 ? k.Craftable[1] : 0;

        // (b) a chest — unless that's a wood tool and stone can be crafted right here from the inventory (best tier first).
        bool stoneNow = stone != 0 && min <= 1 && PlanCompleteFromInventory(stone);
        var stocked = ChestBest(ok);
        if (stocked is { } best && !(stoneNow && Level(best) < 1))
        {
            if (await WithdrawAsync(ok, k.Name == "food" ? k.Want : 1, ct).ConfigureAwait(false) is { } from && Has(k, min)) return from;
        }
        if (stoneNow && await CraftAsync(stone, ct).ConfigureAwait(false)) return "crafted " + Names.Item(stone);

        // (c) a hive member's spare (main had 2 picks while 67 and 69 had none).
        if (await HandoffAsync(k.Name, ok, k.Name == "food" ? k.Want : 1, () => Has(k, min), ct).ConfigureAwait(false) is { } giver) return giver;

        if (k.Name == "food")
        {
            if (Recipes.ForOutput(Items.Bread, 0).Count > 0 && Inv(Items.Wheat) + ChestStock(Items.Wheat) >= 3 && await CraftAsync(Items.Bread, ct).ConfigureAwait(false))
                return "crafted bread";
            return need.Depth == AcquireDepth.Full ? await AskAsync(k.Name, "food", () => Has(k, min), ct).ConfigureAwait(false) : null;
        }
        if (stone == 0) return need.Depth == AcquireDepth.Full ? await AskAsync(k.Name, Short(k), () => Has(k, min), ct).ConfigureAwait(false) : null;

        // (d) craft from inventory + chests: stone when cobble is reachable there, else (cheap mode) wood.
        int cobbleReach = Inv(Blocks.Cobblestone) + ChestStock(Blocks.Cobblestone);
        if (min <= 1 && cobbleReach >= HeadCount(k) && WoodUnits(withChests: true) >= SticksPlanks(k)
            && await CraftAsync(stone, ct).ConfigureAwait(false)) return "crafted " + Names.Item(stone);
        if (need.Depth == AcquireDepth.Cheap)
            return min == 0 && wood != 0 && WoodUnits(withChests: true) >= HeadCount(k) + SticksPlanks(k) && await CraftAsync(wood, ct).ConfigureAwait(false)
                ? "crafted " + Names.Item(wood) : null;
        if (min >= 2) return await AskAsync(k.Name, Names.Item(ok[0]), () => Has(k, min), ct).ConfigureAwait(false); // iron+: nothing to bootstrap

        // (e) bootstrap from scratch.
        var made = await BootstrapAsync(k, min, stone, wood, ct).ConfigureAwait(false);
        if (made != null) return made;
        // (f) a player, last.
        return await AskAsync(k.Name, $"{Short(k)} (or 3 logs)", () => Has(k, min), ct).ConfigureAwait(false);
    }

    /// <summary>Planks equivalent of the sticks a tool needs (2 planks = 4 sticks; sticks on hand count).</summary>
    private int SticksPlanks(Loadout.Kind k) => Inv(Items.Stick) >= SticksFor(k) ? 0 : 2;

    private async Task<string?> BootstrapAsync(Loadout.Kind k, int min, int stone, int wood, CancellationToken ct)
    {
        var gen = FindGeneratorCell();
        bool pickaxe = k.Name == "pickaxe";
        int head = HeadCount(k);
        int cobbleReach = Inv(Blocks.Cobblestone) + ChestStock(Blocks.Cobblestone);
        bool haveWoodPick = Loadout.Kinds["pickaxe"].Ids.Any(id => Inv(id) > 0);
        // Stone whenever cobble is reachable: on hand / in a chest, or at the gen (with a pick, or the wood pick made first).
        bool viaStone = min <= 1 && (cobbleReach >= head || gen != null);
        if (min == 1 && !viaStone) return null;
        bool tableNear = W.FindNearestBlock(W.Player.Position, c.StationSearchRadius, Blocks.Workbench) != null || Inv(Blocks.Workbench) > 0;
        bool needWoodPick = viaStone && cobbleReach < head && !haveWoodPick;   // to dig the gen's cobble
        bool woodTool = !viaStone || (pickaxe && needWoodPick);

        int planks = (woodTool ? head : 0) + (needWoodPick && !pickaxe ? 3 : 0) + 2 + (tableNear ? 0 : 4);
        bool woodShort = WoodUnits(withChests: false) < planks;

        // The plan, announced once: "no pick, making one: logs→planks→sticks→wood pick→3 cobble→stone pick".
        var parts = new List<string>();
        if (woodShort) parts.Add("logs");
        parts.Add("planks");
        if (Inv(Items.Stick) < 4) parts.Add("sticks");
        if (!tableNear) parts.Add("table");
        if (woodTool) parts.Add("wood " + Short(k));
        else if (needWoodPick) parts.Add("wood pick");
        if (viaStone)
        {
            if (cobbleReach < head) parts.Add($"{head} cobble");
            parts.Add("stone " + Short(k));
        }
        Announce($"no {Short(k)}, making one: {string.Join("→", parts)}");

        if (woodShort && !await WoodAsync(planks, ct).ConfigureAwait(false)) return null;

        if (woodTool)
        {
            int woodId = pickaxe ? Items.WoodenPickaxe : wood;
            if (!await CraftAsync(woodId, ct).ConfigureAwait(false)) { Note("couldn't craft " + Names.Item(woodId)); return null; }
            Note("crafted " + Names.Item(woodId));
            if (!viaStone) return "crafted " + Names.Item(woodId);
        }
        else if (needWoodPick)
        {
            if (!await CraftAsync(Items.WoodenPickaxe, ct).ConfigureAwait(false)) { Note("couldn't craft a wooden pickaxe"); return null; }
            Note("crafted wooden pickaxe");
        }

        if (Inv(Blocks.Cobblestone) < head)
        {
            if (ChestStock(Blocks.Cobblestone) > 0) await WithdrawAsync([Blocks.Cobblestone], head - Inv(Blocks.Cobblestone), ct).ConfigureAwait(false);
            if (Inv(Blocks.Cobblestone) < head && !await GenCobbleAsync(head, gen, ct).ConfigureAwait(false))
                return Has(k, min) ? Names.Item(k.Ids.First(id => Inv(id) > 0)) + " (no cobble for stone yet)" : null;
        }
        if (!await CraftAsync(stone, ct).ConfigureAwait(false))
            return Has(k, min) ? Names.Item(k.Ids.First(id => Inv(id) > 0)) + " (stone craft failed)" : null;
        Note("crafted " + Names.Item(stone));
        return "crafted " + Names.Item(stone);
    }

    // ------------------------------------------------------------------ items

    private async Task<string?> ItemAsync(int id, int count, AcquireDepth depth, CancellationToken ct)
    {
        if (Inv(id) >= count) return $"had {count} {Names.Item(id)}";
        string name = Names.Item(id);
        // (b) chests
        if (ChestStock(id) > 0)
        {
            await WithdrawAsync([id], count - Inv(id), ct).ConfigureAwait(false);
            if (Inv(id) >= count) return $"took {name} from storage";
        }
        // (c) hive
        if (await HandoffAsync(name, [id], count - Inv(id), () => Inv(id) >= count, ct).ConfigureAwait(false) is { } giver) return giver;
        // (d) craft (chest ingredients count); raw wood/cobble missing → (e) gather them first.
        bool craftable = Recipes.ForOutput(id, -1).Count > 0;
        if (craftable && await CraftAsync(id, ct, count - Inv(id)).ConfigureAwait(false) && Inv(id) >= count) return $"crafted {name}";
        if (depth == AcquireDepth.Full)
        {
            if (id == Blocks.Cobblestone)
            {
                if (await GenCobbleAsync(count, FindGeneratorCell(), ct, fetchPick: true).ConfigureAwait(false)) return $"dug {count} cobblestone at the gen";
            }
            else if (id == Blocks.Log)
            {
                if (await LogsAsync(count, ct).ConfigureAwait(false) && Inv(id) >= count) return $"got {count} logs";
            }
            else if (craftable && IsWoodProduct(id))
            {
                Announce($"no {name}, making it: logs→{name}");
                if (await WoodAsync(WoodNeedFor(id, count), ct).ConfigureAwait(false)
                    && await CraftAsync(id, ct, count - Inv(id)).ConfigureAwait(false) && Inv(id) >= count) return $"crafted {name}";
            }
            return await AskAsync(name, count > 1 ? $"{count} {name}" : name, () => Inv(id) >= count, ct).ConfigureAwait(false);
        }
        return null;
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
        int u = Inv(Blocks.Planks) + 4 * Inv(Blocks.Log) + Inv(Items.Stick) / 2;
        if (withChests) u += ChestStock(Blocks.Planks) + 4 * ChestStock(Blocks.Log) + ChestStock(Items.Stick) / 2;
        return u;
    }

    /// <summary>At least <paramref name="planks"/> planks' worth of wood on hand: chest wood, else logs from a tree / hive.</summary>
    private async Task<bool> WoodAsync(int planks, CancellationToken ct)
    {
        if (WoodUnits(false) >= planks) return true;
        foreach (var id in new[] { Blocks.Log, Blocks.Planks })
        {
            if (WoodUnits(false) >= planks) break;
            int stock = ChestStock(id);
            if (stock <= 0) continue;
            int short_ = planks - WoodUnits(false);
            int n = id == Blocks.Log ? (short_ + 3) / 4 : short_;
            await WithdrawAsync([id], Math.Min(stock, n), ct).ConfigureAwait(false);
        }
        if (WoodUnits(false) >= planks) return true;
        int logs = (planks - WoodUnits(false) + 3) / 4;
        return await LogsAsync(Inv(Blocks.Log) + logs, ct).ConfigureAwait(false) || WoodUnits(false) >= planks;
    }

    /// <summary>
    /// Logs up to <paramref name="want"/> in the inventory: fell a tree the stewardship rule allows (a sapling from storage
    /// makes the replant certain), else a hive handoff of logs. Never the last trees, never a protected (build) tree.
    /// </summary>
    private async Task<bool> LogsAsync(int want, CancellationToken ct)
    {
        if (Inv(Blocks.Log) >= want) return true;
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
                int before = Inv(Blocks.Log);
                var chopper = GameClients.Unclaimed(c);
                var r = await chopper.ChopTreeAsync(tree.Base, null, ct).ConfigureAwait(false);
                int got = Inv(Blocks.Log) - before;
                Note(got > 0 ? $"chopped a tree ({got} logs)" : "chop: " + Trim(r.Message));
                if (Inv(Blocks.Log) >= want) return true;
            }
            else Note("won't chop: " + refusal);
        }
        else Note("no tree to chop");
        int need = want - Inv(Blocks.Log);
        if (await HandoffAsync("logs", [Blocks.Log, Blocks.Planks], need, () => Inv(Blocks.Log) >= want || WoodUnits(false) >= 4 * want, ct).ConfigureAwait(false) != null)
            return true;
        return Inv(Blocks.Log) >= want;
    }

    // ------------------------------------------------------------------ cobble at the gen

    private BlockPos? FindGeneratorCell()
    {
        IEnumerable<BlockPos> cells;
        try
        {
            cells = _s.GeneratorCells?.Invoke()
                    ?? Perception.BlockDescriber.GenCellsNear(W, W.Player.Position.ToBlockPos(), 16);
        }
        catch (Exception) { return null; }
        var me = W.Player.Position;
        // A generator cell is a resource block; a protected one (a build's wall that happens to touch lava+water) is not.
        return cells.Where(p => c.IsProtected?.Invoke(p, W.GetBlock(p)) != true)
            .OrderBy(p => p.Center.DistanceTo(me)).Cast<BlockPos?>().FirstOrDefault();
    }

    /// <summary>Digs cobble at the generator until <paramref name="want"/> are on hand (needs a pickaxe: fetched when asked).</summary>
    private async Task<bool> GenCobbleAsync(int want, BlockPos? gen, CancellationToken ct, bool fetchPick = false)
    {
        if (Inv(Blocks.Cobblestone) >= want) return true;
        if (gen is not { } cell) { Note("no generator known"); return false; }
        if (!Loadout.Kinds["pickaxe"].Ids.Any(id => Inv(id) > 0))
        {
            if (!fetchPick) return false;
            var pick = await AcquireAsync(new Need("pickaxe"), ct).ConfigureAwait(false);
            if (!pick.Ok) return false;
            if (Inv(Blocks.Cobblestone) >= want) return true;
        }
        var (free, note) = GameToolSet.PickGeneratorCell(c, cell, _s.CellsNear);
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
        return r.Ok && Inv(id) > before;
    }

    private bool PlanCompleteFromInventory(int id) =>
        RecipePlanner.Plan(id, 1, W.Inventory.PlayerSlots().Skip(9).Where(s => !s.IsEmpty).ToList(), -1,
            new PlannerOptions { AllowSmelting = false, HasCraftingTable = true }).Complete;

    /// <summary>(f) the last resort: one question with a default ("I stop"); an answer = look again (drops, inventory, chests).</summary>
    private async Task<string?> AskAsync(string what, string ask, Func<bool> satisfied, CancellationToken ct)
    {
        if (_s.AskPlayer is not { } f) return null;
        Step("ask");
        var why = _done.LastOrDefault(d => d.StartsWith("won't chop", StringComparison.Ordinal)) is { } tree ? " (last trees, won't chop them)" : "";
        var q = $"I need a {ask} and can't get one{why}. Can someone toss me one? (no answer in {L.AskTimeout.TotalSeconds:0}s: I'll stop)";
        string? answer;
        try { answer = await f(q, L.AskTimeout, ct).ConfigureAwait(false); }
        catch (Exception e) when (e is not OperationCanceledException) { answer = null; }
        if (answer == null) { Note("asked, no answer"); return null; }
        if (!satisfied()) await c.CollectDropsAsync(6, ct).ConfigureAwait(false);
        if (satisfied()) return $"{what} from a player";
        Note("asked: " + Trim(answer));
        return null;
    }

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
