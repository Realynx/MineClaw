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
        int mined = 0, gained = 0, lostDrops = 0, deposited = 0, fails = 0;
        var started = DateTime.UtcNow;
        bool upgraded = false, replacedWorn = false;
        var notes = new List<string>();
        string? stop = null;
        bool ok = true;
        while (gained < count && stop == null)
        {
            ct.ThrowIfCancellationRequested();
            // 1) tool: the loop never digs bare-handed (cobble drops nothing without a pickaxe).
            // A worn pick (1/131 left) is replaced once before it breaks mid-batch, from storage / chest cobble + sticks.
            bool worn = !replacedWorn && Loadout.Have(c.World, Loadout.Kinds["pickaxe"]) > 0 && Loadout.HaveUsable(c.World, Loadout.Kinds["pickaxe"]) == 0;
            if (worn)
            {
                replacedWorn = true;
                var w = await Loadout.FetchAsync(c, _s.Observations, ["pickaxe"], ct).ConfigureAwait(false);
                if (w.Got.Count > 0) notes.Add(w.Text);
            }
            if (Loadout.Have(c.World, Loadout.Kinds["pickaxe"]) == 0)
            {
                var r = await Loadout.FetchAsync(c, _s.Observations, ["pickaxe"], ct).ConfigureAwait(false);
                if (r.Missing.Count > 0) { stop = "no pickaxe left: none in storage and no materials to craft one"; ok = mined > 0; break; }
                notes.Add(r.Text);
            }
            // 2) room: deposit before the batch when there's no room for cobble.
            if (c.World.Inventory.RoomFor(new ItemStack(Blocks.Cobblestone, 1, 0)) == 0)
            {
                var d = await DepositAsync(store, ct).ConfigureAwait(false);
                if (!d.Ok) { stop = d.Message; ok = mined > 0; break; }
                deposited += d.Count;
                if (d.Message.Contains("placed", StringComparison.Ordinal)) notes.Add(d.Message);
            }
            int want = Math.Min(Batch, count - gained), invBefore = c.World.Inventory.CountItem(Blocks.Cobblestone);
            var res = cells.Count > 1
                ? await c.HarvestGeneratorAsync(cells, want, stand, ct).ConfigureAwait(false)
                : stand is { } st
                    ? await c.HarvestGeneratorAsync(cell.X, cell.Y, cell.Z, want, st, ct).ConfigureAwait(false)
                    : await c.HarvestGeneratorAsync(cell.X, cell.Y, cell.Z, want, ct).ConfigureAwait(false);
            gained += BatchGain(res.Message) ?? Math.Max(0, c.World.Inventory.CountItem(Blocks.Cobblestone) - invBefore);
            lostDrops += LostRx().Match(res.Message) is { Success: true } lm ? int.Parse(lm.Groups[1].Value) : 0;
            if (stand != null && res.Count == 0 && res.Message.Contains("taught stand spot", StringComparison.Ordinal))
            {
                stop = res.Message; // never dig the cell from anywhere but where the player showed
                ok = mined > 0;
                break;
            }
            mined += res.Count;
            var msg = res.Message;
            // Enclosed cell: retrying can't help; the caller asks a player once where to stand.
            if (res.Count == 0 && msg.Contains(" is enclosed (", StringComparison.Ordinal))
            {
                stop = msg;
                ok = mined > 0;
                break;
            }
            if (res.Count == 0 && msg.StartsWith("busy:", StringComparison.Ordinal))
            {
                stop = msg; // another bot holds this cell: the caller picks another one (never wait out retries on it)
                ok = mined > 0;
                break;
            }
            if (msg.Contains("isn't regenerating", StringComparison.Ordinal) || msg.Contains("stopped producing", StringComparison.Ordinal))
            {
                stop = msg.StartsWith("generator: ", StringComparison.Ordinal) ? msg["generator: ".Length..] : msg;
                ok = false; // the wrong cell (or a broken gen): never keep digging there
                // The concrete cause from the taught layout ("the lava at 4,64,7 is gone"): then the gen is broken and the cell is
                // right, so it stays taught; only an intact layout means the cell itself is wrong.
                string? cause = null;
                try { cause = _s.Diagnose?.Invoke(cell); } catch (Exception) { /* memory busy */ }
                if (cause != null) stop += "; cause: " + cause;
                else if (msg.Contains("isn't regenerating", StringComparison.Ordinal))
                {
                    try { _s.BadCell?.Invoke(cell, "yield guard: " + stop); } catch (Exception) { /* memory busy */ }
                }
                break;
            }
            if (msg.Contains("inventory full", StringComparison.Ordinal) || msg.Contains("pickaxe", StringComparison.Ordinal))
            {
                // Handled at the top of the loop; no progress over and over = stop (never spin).
                if (res.Count > 0) fails = 0;
                else if (++fails >= MaxConsecutiveFailures) { stop = msg; ok = mined > 0; break; }
                continue;
            }
            if (msg.Contains("hungry", StringComparison.OrdinalIgnoreCase) || msg.Contains("starving", StringComparison.OrdinalIgnoreCase))
            {
                var f = await Loadout.FetchAsync(c, _s.Observations, ["food"], ct).ConfigureAwait(false);
                if (f.Missing.Count > 0) { stop = "too hungry to keep mining and no food in storage"; ok = mined > 0; break; }
                await c.EatAsync(ct).ConfigureAwait(false);
                notes.Add(f.Text);
                if (res.Count == 0 && ++fails >= MaxConsecutiveFailures) { stop = msg; ok = mined > 0; break; }
                continue;
            }
            if (res.Count > 0)
            {
                fails = 0;
                // Digging on while the drops are lost only wears the pickaxe out: stop and say what happened to them.
                if ((mined >= StallDigs && gained * 2 < mined) || (gained < count && mined >= count * 2 + StallDigs))
                {
                    stop = $"stalled: {mined} digs gave {gained} cobble ("
                           + (lostDrops > 0 ? $"{lostDrops} drop(s) washed into the gen's water/lava, not collected)" : "the drops never reached my inventory)");
                    ok = false;
                    break;
                }
                // Took a wood pick to get going: upgrade to stone as soon as the gen's cobble allows it (one craft, no walking).
                if (!upgraded && gained < count && await Acquisition.Planner(c, _s.Observations).UpgradeIfCheapAsync("pickaxe", ct).ConfigureAwait(false) is { } up)
                {
                    upgraded = true;
                    notes.Add(up);
                }
                continue;
            }
            if (res.Ok) break; // the client says done without digging: nothing to loop on
            // 3) interrupted (mob, danger, can't reach): pause and resume; stop only after repeated failures.
            if (++fails >= MaxConsecutiveFailures) { stop = msg; ok = mined > 0; break; }
            await Task.Delay(RetryPause, ct).ConfigureAwait(false);
        }
        // Live 10-27 21:12: "chest 14,64,-5 not topped up (have 8 cobble in inventory)": whatever the job got goes into the store
        // at the end, target met or not (a stop for storage itself excepted).
        if (store != null && mined > 0 && c.World.Inventory.CountItem(Blocks.Cobblestone) > 0 && stop?.StartsWith("storage full", StringComparison.Ordinal) != true)
        {
            try
            {
                var d = await DepositAsync(store, ct).ConfigureAwait(false);
                if (d.Ok) deposited += d.Count;
                else notes.Add("final deposit: " + d.Message);
            }
            catch (OperationCanceledException) { /* cancelled: keep the partial result */ }
        }
        // "generator: 7/64 cobblestone in 2m10s (dug 9 at 5,64,7), stored ...": the count is cobble gained, never digs.
        var text = $"generator: {gained}/{count} cobblestone in {Duration(DateTime.UtcNow - started)} (dug {mined} at {cell.X},{cell.Y},{cell.Z})"
                   + (deposited > 0 ? StoredText() : "")
                   + $" (have {c.World.Inventory.CountItem(Blocks.Cobblestone)} cobblestone)"
                   + (lostDrops > 0 && stop?.StartsWith("stalled", StringComparison.Ordinal) != true ? $"; {lostDrops} drop(s) washed into the gen's water/lava (not collected)" : "")
                   + (notes.Count > 0 ? "; " + string.Join("; ", notes.Distinct().Take(3)) : "") + (stop != null ? "; " + stop : "");
        return ok ? ActionResult.Success(text, mined) : ActionResult.Fail(text, mined);
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
        int Loose() => c.World.Inventory.PlayerSlots().Skip(9).Count(s => !Keep(s, reserved));
        int before = Loose();
        if (before == 0) return ActionResult.Success("nothing to deposit");
        var storing = c.World.Inventory.PlayerSlots().Skip(9).Where(s => !Keep(s, reserved)).Select(s => (int)s.Id).ToHashSet();
        var tried = new HashSet<BlockPos>();
        var why = new List<string>();
        foreach (var chest in Candidates(c, _s.Observations, store, storing))
        {
            if (!tried.Add(chest)) continue;
            if (tried.Count > MaxChestsTried) break;
            int was = Loose();
            var items = LooseItems(reserved);
            var r = await c.DepositAllAsync(chest, s => !Keep(s, reserved), ct).ConfigureAwait(false);
            NoteStored(chest, items, LooseItems(reserved));
            int now = Loose();
            if (now == 0 || c.World.Inventory.RoomFor(new ItemStack(Blocks.Cobblestone, 1, 0)) > 0 && now < before)
                return ActionResult.Success($"deposited at {chest.X},{chest.Y},{chest.Z}", before - now);
            why.Add($"{chest.X},{chest.Y},{chest.Z} {(r.Ok || now < was ? "filled up" : Short(r.Message))}");
        }
        // Every known chest is really full (or none is known): a new one, labelled in shared memory.
        var placed = await PlaceChestAsync(ct).ConfigureAwait(false);
        string tries = why.Count == 0 ? "no known chest" : "tried " + string.Join("; ", why.Take(6));
        if (placed is not { } p) return ActionResult.Fail($"storage full: {tries}; no chest (or planks) to place a new one");
        var held = LooseItems(reserved);
        await c.DepositAllAsync(p, s => !Keep(s, reserved), ct).ConfigureAwait(false);
        NoteStored(p, held, LooseItems(reserved));
        var label = $"cobble_storage_{p.X}_{p.Z}";
        try { _s.LabelChest?.Invoke(label, p); } catch (Exception) { /* label is best effort */ }
        return Loose() < before
            ? ActionResult.Success($"placed and labelled a new chest '{label}' at {p.X},{p.Y},{p.Z}", before - Loose())
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
        if (first is { } s) yield return s;
        var me = c.World.Player.Position;
        var known = new HashSet<BlockPos>();
        if (obs != null)
        {
            IReadOnlyList<(ObservationRow Row, double Distance, bool RoomInMemory)> rows;
            try { rows = ChestRoom.Ranked(obs, me.X, me.Y, me.Z, storing); }
            catch (Exception) { rows = []; }
            foreach (var (row, _, _) in rows)
            {
                var b = row.Bounds!.Value;
                var p = new BlockPos((int)Math.Floor(b.MinX), (int)Math.Floor(b.MinY), (int)Math.Floor(b.MinZ));
                known.Add(p);
                if (b.MaxX > b.MinX || b.MaxZ > b.MinZ) known.Add(new BlockPos((int)Math.Floor(b.MaxX), (int)Math.Floor(b.MaxY), (int)Math.Floor(b.MaxZ)));
                yield return p;
            }
        }
        List<BlockPos> seen;
        try { seen = c.World.FindBlocks(me, 24, (id, _) => id == Blocks.Chest, 16).Select(h => h.Pos).ToList(); }
        catch (Exception) { yield break; }
        foreach (var p in seen)
        {
            if (known.Contains(p) || (first is { } f && f == p)) continue;
            // The other half of a pair is the same 54-slot window.
            known.Add(p);
            known.Add(p.Offset(1, 0, 0)); known.Add(p.Offset(-1, 0, 0)); known.Add(p.Offset(0, 0, 1)); known.Add(p.Offset(0, 0, -1));
            yield return p;
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
            if (w.GetBlock(p.X, p.Y, p.Z) != 0 || w.GetBlock(p.X, p.Y + 1, p.Z) != 0) continue;
            if (!BlockShapes.HasCollision(w, p.X, p.Y - 1, p.Z)) continue;
            if (Neighbours(p).Any(n => Blocks.IsLava(w.GetBlock(n)) || Blocks.IsWater(w.GetBlock(n)))) continue; // not inside the gen
            if (c.IsProtected?.Invoke(p, Blocks.Chest) == true) continue;
            var r = await c.PlaceBlockAsync(p.X, p.Y, p.Z, Blocks.Chest, ct).ConfigureAwait(false);
            if (r.Ok) return p;
        }
        return null;
    }

    private static IEnumerable<BlockPos> Neighbours(BlockPos p) =>
        [p.Offset(1, 0, 0), p.Offset(-1, 0, 0), p.Offset(0, 0, 1), p.Offset(0, 0, -1), p.Offset(0, -1, 0)];
}
