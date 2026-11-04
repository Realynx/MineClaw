using MineClaw.Agent.Rag;
using MineClaw.Bot.Game;
using MineClaw.Client;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Bot.GameTools;

/// <summary>What the long generator loop needs beyond the game client (all optional).</summary>
/// <param name="Observations">WorldIndexer container snapshots: spare tools / food, chests with room.</param>
/// <param name="LabelChest">Labels a chest the loop placed (shared-memory waypoint), e.g. "cobble_storage_2".</param>
/// <param name="BadCell">The yield guard proved a cell wrong (memory retracts its dig keys).</param>
/// <param name="TaughtStand">The stand spot a player taught for a dig cell (watch_demo "stood at .. punched .. at cell"), or
/// null; see <see cref="GeneratorStand.Find"/>.</param>
/// <param name="Diagnose">Why a cell stopped yielding, from the gen's taught layout vs the world (null = all as taught):
/// see <see cref="Perception.GenLayout.Diagnose"/>.</param>
public sealed record GeneratorSupport(ObservationStore? Observations = null, Action<string, BlockPos>? LabelChest = null,
    Action<BlockPos, string>? BadCell = null, Func<BlockPos, BlockPos?>? TaughtStand = null, Func<BlockPos, string?>? Diagnose = null);

/// <summary>
/// Unattended cobble-generator mining (zero tokens per block, hours if asked): mines the cell in batches through the
/// client's <see cref="IGameClient.HarvestGeneratorAsync"/> (which keeps its own survival window, EdgeGuard and yield
/// guard) and between batches keeps the job alive by itself:
/// <list type="bullet">
/// <item>pickaxe broke → a spare from storage, else craft one from what's on hand, else stop and say so;</item>
/// <item>inventory full → deposit everything but tools/food into the store chest (or the nearest known chest with room) and
/// continue; every chest full → place a new chest (carried or crafted from planks), label it, continue; else stop and say so;</item>
/// <item>too hungry → food from storage, eat, continue;</item>
/// <item>a batch interrupted (mob, danger, lag) → short pause and resume, giving up only after repeated failures;</item>
/// <item>the cell doesn't yield cobble (wrong block) → stop at once, never dig on.</item>
/// </list>
/// </summary>
public sealed partial class GeneratorLoop(IGameClient c, GeneratorSupport? support = null)
{
    private readonly GeneratorSupport _s = support ?? new GeneratorSupport();

    public int Batch { get; init; } = 64;
    public int MaxConsecutiveFailures { get; init; } = 4;
    public TimeSpan RetryPause { get; init; } = TimeSpan.FromSeconds(3);

    private static bool Keep(ItemStack s, IReadOnlySet<int> reserved) =>
        s.IsEmpty || reserved.Contains(s.Id) || (Items.Exists(s.Id) && Items.Get(s.Id).MaxDamage > 0) || Items.Food(s.Id) != null;

    /// <param name="stand">A player-taught stand spot for <paramref name="cell"/>: every batch digs from there (null = the
    /// client picks a safe spot itself).</param>
    /// <param name="rotation">Other dig cells of the same generator: dug in turn whenever they're in reach from where the bot
    /// stands (never hammering one cell while the others sit ready). Null = <paramref name="cell"/> only.</param>
    public async Task<ActionResult> RunAsync(BlockPos cell, int count, BlockPos? store, CancellationToken ct, BlockPos? stand = null,
        IReadOnlyList<BlockPos>? rotation = null)
    {
        IReadOnlyList<BlockPos> cells = [cell, .. (rotation ?? []).Where(p => p != cell).Distinct()];
        // Live 10-28: "dug 64/64, stored 4 cobblestone" read as "I mined all 64". The target is cobble, not digs: count what
        // each batch really gained (the client's "+N cobblestone", its inventory delta) and dig until that reaches the target.
        var run = new GenRun(cell, count);
        var started = DateTime.UtcNow;
        while (run.Gained < count && run.Stop == null)
        {
            ct.ThrowIfCancellationRequested();
            if (!await PrepareBatchAsync(run, store, ct).ConfigureAwait(false)) break;
            var res = await HarvestBatchAsync(run, cells, stand, ct).ConfigureAwait(false);
            var step = await AfterBatchAsync(run, res, stand != null, ct).ConfigureAwait(false);
            if (step == GenStep.Stop) break;
            if (step == GenStep.Retry) await Task.Delay(RetryPause, ct).ConfigureAwait(false);
        }
        await FinalDepositAsync(run, store, ct).ConfigureAwait(false);
        // "generator: 7/64 cobblestone in 2m10s (dug 9 at 5,64,7), stored ...": the count is cobble gained, never digs.
        var text = $"generator: {run.Gained}/{count} cobblestone in {Duration(DateTime.UtcNow - started)} (dug {run.Mined} at {cell.X},{cell.Y},{cell.Z})"
                   + (run.Deposited > 0 ? StoredText() : "")
                   + $" (have {c.World.Inventory.CountItem(Blocks.Cobblestone)} cobblestone)"
                   + run.Tail();
        return run.Ok ? ActionResult.Success(text, run.Mined) : ActionResult.Fail(text, run.Mined);
    }

    /// <summary>What the loop does after a batch: dig on, pause and retry, or stop.</summary>
    private enum GenStep { Next, Retry, Stop }

    /// <summary>One <see cref="RunAsync"/>: its counts, notes and the reason it stopped.</summary>
    private sealed class GenRun(BlockPos cell, int count)
    {
        public readonly BlockPos Cell = cell;
        public readonly int Count = count;
        public int Mined, Gained, LostDrops, Deposited, Fails;
        public bool Upgraded, ReplacedWorn, Ok = true;
        public readonly List<string> Notes = new();
        public string? Stop;

        /// <summary>Stops with <paramref name="why"/>: a failure unless something was dug.</summary>
        public GenStep Halt(string why)
        {
            Stop = why;
            Ok = Mined > 0;
            return GenStep.Stop;
        }

        /// <summary>A repeat of a handled condition: dug something = progress; nothing over and over = stop (never spin).</summary>
        public GenStep NoProgress(int dug, string msg, int maxFailures)
        {
            if (dug > 0) Fails = 0;
            else if (++Fails >= maxFailures) return Halt(msg);
            return GenStep.Next;
        }

        /// <summary>Digs whose drops don't arrive (fewer than half, or twice the target dug) only wear the pickaxe out.</summary>
        public bool Stalled(int stallDigs) => (Mined >= stallDigs && Gained * 2 < Mined) || (Gained < Count && Mined >= Count * 2 + stallDigs);

        /// <summary>The result's tail: lost drops (unless the stall line says so), notes and the stop reason.</summary>
        public string Tail() =>
            (LostDrops > 0 && Stop?.StartsWith("stalled", StringComparison.Ordinal) != true ? $"; {LostDrops} drop(s) washed into the gen's water/lava (not collected)" : "")
            + (Notes.Count > 0 ? "; " + string.Join("; ", Notes.Distinct().Take(3)) : "") + (Stop != null ? "; " + Stop : "");
    }

    /// <summary>
    /// Tool and room before a batch: the loop never digs bare-handed (cobble drops nothing without a pickaxe), and deposits
    /// first when there's no room for cobble. False = stopped.
    /// </summary>
    private async Task<bool> PrepareBatchAsync(GenRun run, BlockPos? store, CancellationToken ct)
    {
        await ReplaceWornPickaxeAsync(run, ct).ConfigureAwait(false);
        if (Loadout.Have(c.World, Loadout.Kinds["pickaxe"]) == 0)
        {
            var r = await Loadout.FetchAsync(c, _s.Observations, ["pickaxe"], ct).ConfigureAwait(false);
            if (r.Missing.Count > 0) { run.Halt("no pickaxe left: none in storage and no materials to craft one"); return false; }
            run.Notes.Add(r.Text);
        }
        if (c.World.Inventory.RoomFor(new ItemStack(Blocks.Cobblestone, 1, 0)) != 0) return true;
        var d = await DepositAsync(store, ct).ConfigureAwait(false);
        if (!d.Ok) { run.Halt(d.Message); return false; }
        run.Deposited += d.Count;
        if (d.Message.Contains("placed", StringComparison.Ordinal)) run.Notes.Add(d.Message);
        return true;
    }

    /// <summary>A worn pick (1/131 left) is replaced once before it breaks mid-batch, from storage / chest cobble + sticks.</summary>
    private async Task ReplaceWornPickaxeAsync(GenRun run, CancellationToken ct)
    {
        if (run.ReplacedWorn || Loadout.Have(c.World, Loadout.Kinds["pickaxe"]) <= 0 || Loadout.HaveUsable(c.World, Loadout.Kinds["pickaxe"]) != 0) return;
        run.ReplacedWorn = true;
        var w = await Loadout.FetchAsync(c, _s.Observations, ["pickaxe"], ct).ConfigureAwait(false);
        if (w.Got.Count > 0) run.Notes.Add(w.Text);
    }

    /// <summary>One batch (every rotation cell in reach, else the cell from the taught stand or a spot the client picks);
    /// counts the cobble it really gained and the drops lost to the gen's fluids.</summary>
    private async Task<ActionResult> HarvestBatchAsync(GenRun run, IReadOnlyList<BlockPos> cells, BlockPos? stand, CancellationToken ct)
    {
        var cell = cells[0];
        int want = Math.Min(Batch, run.Count - run.Gained), invBefore = c.World.Inventory.CountItem(Blocks.Cobblestone);
        var res = cells.Count > 1
            ? await c.HarvestGeneratorAsync(cells, want, stand, ct).ConfigureAwait(false)
            : stand is { } st
                ? await c.HarvestGeneratorAsync(cell.X, cell.Y, cell.Z, want, st, ct).ConfigureAwait(false)
                : await c.HarvestGeneratorAsync(cell.X, cell.Y, cell.Z, want, ct).ConfigureAwait(false);
        run.Gained += BatchGain(res.Message) ?? Math.Max(0, c.World.Inventory.CountItem(Blocks.Cobblestone) - invBefore);
        run.LostDrops += LostRx().Match(res.Message) is { Success: true } lm ? int.Parse(lm.Groups[1].Value) : 0;
        return res;
    }

    private async Task<GenStep> AfterBatchAsync(GenRun run, ActionResult res, bool taughtStand, CancellationToken ct)
    {
        if (RecordBatch(run, res, taughtStand) is { } stop) return stop;
        var msg = res.Message;
        // Handled at the top of the loop.
        if (msg.Contains("inventory full", StringComparison.Ordinal) || msg.Contains("pickaxe", StringComparison.Ordinal))
            return run.NoProgress(res.Count, msg, MaxConsecutiveFailures);
        if (msg.Contains("hungry", StringComparison.OrdinalIgnoreCase) || msg.Contains("starving", StringComparison.OrdinalIgnoreCase))
            return await FeedAsync(run, res, ct).ConfigureAwait(false);
        if (res.Count > 0) return await AfterDigAsync(run, ct).ConfigureAwait(false);
        if (res.Ok) return GenStep.Stop; // the client says done without digging: nothing to loop on
        // Interrupted (mob, danger, can't reach): pause and resume; stop only after repeated failures.
        return ++run.Fails >= MaxConsecutiveFailures ? run.Halt(msg) : GenStep.Retry;
    }

    /// <summary>Counts the batch's digs; a stop when the batch says digging this cell can't go on, else null.</summary>
    private GenStep? RecordBatch(GenRun run, ActionResult res, bool taughtStand)
    {
        var msg = res.Message;
        // Never dig the cell from anywhere but where the player showed.
        if (taughtStand && res.Count == 0 && msg.Contains("taught stand spot", StringComparison.Ordinal)) return run.Halt(msg);
        run.Mined += res.Count;
        // Enclosed cell: retrying can't help; the caller asks a player once where to stand. Busy: another bot holds this cell,
        // the caller picks another one (never wait out retries on it).
        if (res.Count == 0 && (msg.Contains(" is enclosed (", StringComparison.Ordinal) || msg.StartsWith("busy:", StringComparison.Ordinal)))
            return run.Halt(msg);
        if (msg.Contains("isn't regenerating", StringComparison.Ordinal) || msg.Contains("stopped producing", StringComparison.Ordinal))
            return CellStopped(run, msg);
        return null;
    }

    /// <summary>The wrong cell (or a broken gen): never keep digging there. The concrete cause from the taught layout ("the lava
    /// at 4,64,7 is gone") means the gen is broken and the cell is right, so it stays taught; only an intact layout means the
    /// cell itself is wrong.</summary>
    private GenStep CellStopped(GenRun run, string msg)
    {
        var cell = run.Cell;
        run.Stop = msg.StartsWith("generator: ", StringComparison.Ordinal) ? msg["generator: ".Length..] : msg;
        run.Ok = false;
        string? cause = null;
        try { cause = _s.Diagnose?.Invoke(cell); } catch (Exception) { /* memory busy */ }
        if (cause != null) run.Stop += "; cause: " + cause;
        else if (msg.Contains("isn't regenerating", StringComparison.Ordinal))
        {
            try { _s.BadCell?.Invoke(cell, "yield guard: " + run.Stop); } catch (Exception) { /* memory busy */ }
        }
        return GenStep.Stop;
    }

    private async Task<GenStep> FeedAsync(GenRun run, ActionResult res, CancellationToken ct)
    {
        var f = await Loadout.FetchAsync(c, _s.Observations, ["food"], ct).ConfigureAwait(false);
        if (f.Missing.Count > 0) return run.Halt("too hungry to keep mining and no food in storage");
        await FoodSupply.EatAsync(c, ct).ConfigureAwait(false);
        run.Notes.Add(f.Text);
        if (res.Count == 0 && ++run.Fails >= MaxConsecutiveFailures) return run.Halt(res.Message);
        return GenStep.Next;
    }

    /// <summary>A batch dug something: stop when the drops don't arrive (say what happened to them); else upgrade a wood pick
    /// to stone as soon as the gen's cobble allows it (one craft, no walking).</summary>
    private async Task<GenStep> AfterDigAsync(GenRun run, CancellationToken ct)
    {
        run.Fails = 0;
        if (run.Stalled(StallDigs))
        {
            run.Stop = $"stalled: {run.Mined} digs gave {run.Gained} cobble ("
                       + (run.LostDrops > 0 ? $"{run.LostDrops} drop(s) washed into the gen's water/lava, not collected)" : "the drops never reached my inventory)");
            run.Ok = false;
            return GenStep.Stop;
        }
        if (!run.Upgraded && run.Gained < run.Count && await Acquisition.Planner(c, _s.Observations).UpgradeIfCheapAsync("pickaxe", ct).ConfigureAwait(false) is { } up)
        {
            run.Upgraded = true;
            run.Notes.Add(up);
        }
        return GenStep.Next;
    }

    /// <summary>
    /// Live 10-27 21:12: "chest 14,64,-5 not topped up (have 8 cobble in inventory)": whatever the job got goes into the store at
    /// the end, target met or not (a stop for storage itself excepted).
    /// </summary>
    private async Task FinalDepositAsync(GenRun run, BlockPos? store, CancellationToken ct)
    {
        if (store == null || run.Mined <= 0 || c.World.Inventory.CountItem(Blocks.Cobblestone) <= 0
            || run.Stop?.StartsWith("storage full", StringComparison.Ordinal) == true) return;
        try
        {
            var d = await DepositAsync(store, ct).ConfigureAwait(false);
            if (d.Ok) run.Deposited += d.Count;
            else run.Notes.Add("final deposit: " + d.Message);
        }
        catch (OperationCanceledException) { /* cancelled: keep the partial result */ }
    }

    /// <summary>Digs before the stall check may stop a run whose drops don't arrive (fewer than half).</summary>
    public int StallDigs { get; init; } = 16;

    [System.Text.RegularExpressions.GeneratedRegex(@"\+(\d+) cobblestone\b")]
    private static partial System.Text.RegularExpressions.Regex GainRx();

    [System.Text.RegularExpressions.GeneratedRegex(@"\b(\d+) drop\(s\) washed into")]
    private static partial System.Text.RegularExpressions.Regex LostRx();

    /// <summary>The client batch's own "+N cobblestone" (its inventory delta), or null when the text doesn't say.</summary>
    internal static int? BatchGain(string message) => GainRx().Match(message) is { Success: true } m ? int.Parse(m.Groups[1].Value) : null;

    /// <summary>"45s", "2m10s", "1h05m".</summary>
    internal static string Duration(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}h{t.Minutes:00}m" : t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes}m{t.Seconds:00}s" : $"{Math.Max(0, (int)t.TotalSeconds)}s";

    /// <summary>
    /// Everything but tools/food/reserved into storage: the store chest, else every known chest (with room in memory first,
    /// then the ones memory calls full: opened and re-checked, never trusted blind), else a new one. A failure names each chest
    /// tried and why.
    /// </summary>
    public async Task<ActionResult> DepositAsync(BlockPos? store, CancellationToken ct)
    {
        var reserved = c.ReservedItems;
        int before = Loose(reserved);
        if (before == 0) return ActionResult.Success("nothing to deposit");
        var storing = c.World.Inventory.PlayerSlots().Skip(9).Where(s => !Keep(s, reserved)).Select(s => (int)s.Id).ToHashSet();
        var tried = new HashSet<BlockPos>();
        var why = new List<string>();
        foreach (var chest in Candidates(c, _s.Observations, store, storing))
        {
            if (!tried.Add(chest)) continue;
            foreach (var cell in ChestPairs.Cells(c.World, chest)) tried.Add(cell); // the other half is this window
            if (tried.Count > MaxChestsTried) break;
            if (await DepositIntoAsync(chest, reserved, before, why, ct).ConfigureAwait(false) is { } done) return done;
        }
        // Every known chest is really full (or none is known): a new one, labelled in shared memory.
        return await DepositIntoNewChestAsync(reserved, before, why, ct).ConfigureAwait(false);
    }

    private int Loose(IReadOnlySet<int> reserved) => c.World.Inventory.PlayerSlots().Skip(9).Count(s => !Keep(s, reserved));

    /// <summary>One known chest: the result when the inventory emptied (or has room again); else why it didn't take more.</summary>
    private async Task<ActionResult?> DepositIntoAsync(BlockPos chest, IReadOnlySet<int> reserved, int before, List<string> why, CancellationToken ct)
    {
        int was = Loose(reserved);
        var items = LooseItems(reserved);
        var r = await c.DepositAllAsync(chest, s => !Keep(s, reserved), ct).ConfigureAwait(false);
        NoteStored(chest, items, LooseItems(reserved));
        int now = Loose(reserved);
        if (now == 0 || c.World.Inventory.RoomFor(new ItemStack(Blocks.Cobblestone, 1, 0)) > 0 && now < before)
            return ActionResult.Success($"deposited at {chest.X},{chest.Y},{chest.Z}", before - now);
        why.Add($"{chest.X},{chest.Y},{chest.Z} {(r.Ok || now < was ? "filled up" : Short(r.Message))}");
        return null;
    }

    /// <summary>Every known chest is really full (or none is known): a new one, labelled in shared memory.</summary>
    private async Task<ActionResult> DepositIntoNewChestAsync(IReadOnlySet<int> reserved, int before, List<string> why, CancellationToken ct)
    {
        var placed = await PlaceChestAsync(ct).ConfigureAwait(false);
        string tries = why.Count == 0 ? "no known chest" : "tried " + string.Join("; ", why.Take(6));
        if (placed is not { } p) return ActionResult.Fail($"storage full: {tries}; no chest (or planks) to place a new one");
        var held = LooseItems(reserved);
        await c.DepositAllAsync(p, s => !Keep(s, reserved), ct).ConfigureAwait(false);
        NoteStored(p, held, LooseItems(reserved));
        var label = $"cobble_storage_{p.X}_{p.Z}";
        try { _s.LabelChest?.Invoke(label, p); } catch (Exception) { /* label is best effort */ }
        return Loose(reserved) < before
            ? ActionResult.Success($"placed and labelled a new chest '{label}' at {p.X},{p.Y},{p.Z}", before - Loose(reserved))
            : ActionResult.Fail($"placed a chest at {p.X},{p.Y},{p.Z} but couldn't deposit into it");
    }

    private readonly Dictionary<BlockPos, Dictionary<int, int>> _stored = new();

    private Dictionary<int, int> LooseItems(IReadOnlySet<int> reserved) =>
        c.World.Inventory.PlayerSlots().Skip(9).Where(s => !Keep(s, reserved)).GroupBy(s => (int)s.Id).ToDictionary(g => g.Key, g => g.Sum(s => (int)s.Count));

    private void NoteStored(BlockPos chest, Dictionary<int, int> before, Dictionary<int, int> after)
    {
        foreach (var (id, n) in before)
        {
            int moved = n - after.GetValueOrDefault(id);
            if (moved <= 0) continue;
            if (!_stored.TryGetValue(chest, out var m)) _stored[chest] = m = new();
            m[id] = m.GetValueOrDefault(id) + moved;
        }
    }

    /// <summary>", stored 64 cobblestone in chest 14,64,-5" (where the haul went; never a bare "deposited N item(s)").</summary>
    private string StoredText()
    {
        if (_stored.Count == 0) return ", deposited the haul";
        return ", stored " + string.Join(", ", _stored.Take(3).Select(kv =>
            string.Join(" + ", kv.Value.OrderByDescending(x => x.Value).Take(2).Select(x => $"{x.Value} {Names.Item(x.Key)}")) + $" in chest {kv.Key.X},{kv.Key.Y},{kv.Key.Z}"));
    }

    /// <summary>Most chests one deposit opens before calling storage full.</summary>
    public int MaxChestsTried { get; init; } = 12;

    private static string Short(string s) => s.Length <= 50 ? s : s[..50];

    /// <summary>
    /// Chests to try, in order: <paramref name="first"/> (the store), then every remembered chest by <see cref="ChestRoom.Ranked"/>
    /// (room in memory first, then the "full" ones, oldest memory first), then chests in sight that memory doesn't know yet.
    /// </summary>
    public static IEnumerable<BlockPos> Candidates(IGameClient c, ObservationStore? obs, BlockPos? first, IReadOnlyCollection<int>? storing = null)
    {
        var me = c.World.Player.Position;
        var known = new HashSet<BlockPos>();
        if (first is { } s)
        {
            known.UnionWith(ChestPairs.Cells(c.World, s)); // the store's other half is the store (was: offered again from memory)
            yield return s;
        }
        if (obs != null)
        {
            IReadOnlyList<(ObservationRow Row, double Distance, bool RoomInMemory)> rows;
            try { rows = ChestRoom.Ranked(obs, me.X, me.Y, me.Z, storing); }
            catch (Exception) { rows = []; }
            foreach (var (row, _, _) in rows)
            {
                var b = row.Bounds!.Value;
                var p = new BlockPos((int)Math.Floor(b.MinX), (int)Math.Floor(b.MinY), (int)Math.Floor(b.MinZ));
                var far = new BlockPos((int)Math.Floor(b.MaxX), (int)Math.Floor(b.MaxY), (int)Math.Floor(b.MaxZ));
                if (known.Contains(p) || known.Contains(far)) continue;
                known.Add(p);
                known.Add(far);
                known.UnionWith(ChestPairs.Cells(c.World, p));
                yield return p;
            }
        }
        List<BlockPos> seen;
        try { seen = c.World.FindBlocks(me, 24, (id, _) => Ids1122.IsChest(id), 16).Select(h => h.Pos).ToList(); }
        catch (Exception) { yield break; }
        foreach (var p in seen)
        {
            if (known.Contains(p)) continue;
            // The other half of a pair is the same 54-slot window (only the real half: a trapped chest beside is its own chest).
            var cells = ChestPairs.Cells(c.World, p);
            known.UnionWith(cells);
            yield return cells[0];
        }
    }

    /// <summary>Places a chest (carried, else crafted from planks/logs) next to the bot, never over the void.</summary>
    public async Task<BlockPos?> PlaceChestAsync(CancellationToken ct)
    {
        if (c.World.Inventory.CountItem(Blocks.Chest) <= 0)
        {
            var made = await new Crafter(c) { Observations = _s.Observations }.CraftAsync("chest", 1, ct).ConfigureAwait(false);
            if (!made.Ok || c.World.Inventory.CountItem(Blocks.Chest) <= 0) return null;
        }
        var w = c.World;
        var me = w.Player.Position.ToBlockPos();
        foreach (var (dx, dz) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (-1, -1), (1, -1), (-1, 1), (2, 0), (-2, 0), (0, 2), (0, -2) })
        {
            var p = new BlockPos(me.X + dx, me.Y, me.Z + dz);
            if (!ChestSpot(p)) continue;
            var r = await c.PlaceBlockAsync(p.X, p.Y, p.Z, Blocks.Chest, ct).ConfigureAwait(false);
            if (r.Ok) return p;
        }
        return null;
    }

    /// <summary>Two free cells on solid ground, no fluid beside or under (not inside the gen), not protected.</summary>
    private bool ChestSpot(BlockPos p)
    {
        var w = c.World;
        if (w.GetBlock(p.X, p.Y, p.Z) != 0 || w.GetBlock(p.X, p.Y + 1, p.Z) != 0) return false;
        if (!BlockShapes.HasCollision(w, p.X, p.Y - 1, p.Z)) return false;
        if (Neighbours(p).Any(n => Blocks.IsLava(w.GetBlock(n)) || Blocks.IsWater(w.GetBlock(n)))) return false; // not inside the gen
        return c.IsProtected?.Invoke(p, Blocks.Chest) != true;
    }

    private static IEnumerable<BlockPos> Neighbours(BlockPos p) =>
        [p.Offset(1, 0, 0), p.Offset(-1, 0, 0), p.Offset(0, 0, 1), p.Offset(0, 0, -1), p.Offset(0, -1, 0)];
}
