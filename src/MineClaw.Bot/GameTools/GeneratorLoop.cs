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
public sealed record GeneratorSupport(ObservationStore? Observations = null, Action<string, BlockPos>? LabelChest = null,
    Action<BlockPos, string>? BadCell = null, Func<BlockPos, BlockPos?>? TaughtStand = null);

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
public sealed class GeneratorLoop(IGameClient c, GeneratorSupport? support = null)
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
        int mined = 0, deposited = 0, fails = 0;
        bool upgraded = false;
        var notes = new List<string>();
        string? stop = null;
        bool ok = true;
        while (mined < count && stop == null)
        {
            ct.ThrowIfCancellationRequested();
            // 1) tool: the loop never digs bare-handed (cobble drops nothing without a pickaxe).
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
            var res = cells.Count > 1
                ? await c.HarvestGeneratorAsync(cells, Math.Min(Batch, count - mined), stand, ct).ConfigureAwait(false)
                : stand is { } st
                    ? await c.HarvestGeneratorAsync(cell.X, cell.Y, cell.Z, Math.Min(Batch, count - mined), st, ct).ConfigureAwait(false)
                    : await c.HarvestGeneratorAsync(cell.X, cell.Y, cell.Z, Math.Min(Batch, count - mined), ct).ConfigureAwait(false);
            if (stand != null && res.Count == 0 && res.Message.Contains("taught stand spot", StringComparison.Ordinal))
            {
                stop = res.Message; // never dig the cell from anywhere but where the player showed
                ok = mined > 0;
                break;
            }
            mined += res.Count;
            var msg = res.Message;
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
                if (msg.Contains("isn't regenerating", StringComparison.Ordinal))
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
                // Took a wood pick to get going: upgrade to stone as soon as the gen's cobble allows it (one craft, no walking).
                if (!upgraded && mined < count && await Acquisition.Planner(c, _s.Observations).UpgradeIfCheapAsync("pickaxe", ct).ConfigureAwait(false) is { } up)
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
        var text = $"generator: dug {mined}/{count} at {cell.X},{cell.Y},{cell.Z}" + (deposited > 0 ? $", deposited {deposited} item(s)" : "")
                   + $" (have {c.World.Inventory.CountItem(Blocks.Cobblestone)} cobblestone)"
                   + (notes.Count > 0 ? "; " + string.Join("; ", notes.Distinct().Take(3)) : "") + (stop != null ? "; " + stop : "");
        return ok ? ActionResult.Success(text, mined) : ActionResult.Fail(text, mined);
    }

    /// <summary>Everything but tools/food/reserved into storage: the store chest, else known chests with room, else a new one.</summary>
    public async Task<ActionResult> DepositAsync(BlockPos? store, CancellationToken ct)
    {
        var reserved = c.ReservedItems;
        int Loose() => c.World.Inventory.PlayerSlots().Skip(9).Count(s => !Keep(s, reserved));
        int before = Loose();
        if (before == 0) return ActionResult.Success("nothing to deposit");
        var tried = new HashSet<BlockPos>();
        foreach (var chest in Candidates(store))
        {
            if (!tried.Add(chest)) continue;
            await c.DepositAllAsync(chest, s => !Keep(s, reserved), ct).ConfigureAwait(false);
            if (Loose() == 0 || c.World.Inventory.RoomFor(new ItemStack(Blocks.Cobblestone, 1, 0)) > 0 && Loose() < before)
                return ActionResult.Success($"deposited at {chest.X},{chest.Y},{chest.Z}", before - Loose());
        }
        // Every known chest is full (or none is known): a new one, labelled in shared memory.
        var placed = await PlaceChestAsync(ct).ConfigureAwait(false);
        if (placed is not { } p) return ActionResult.Fail("storage full: every known chest is full and I have no chest (or planks) to place a new one");
        await c.DepositAllAsync(p, s => !Keep(s, reserved), ct).ConfigureAwait(false);
        var label = $"cobble_storage_{p.X}_{p.Z}";
        try { _s.LabelChest?.Invoke(label, p); } catch (Exception) { /* label is best effort */ }
        return Loose() < before
            ? ActionResult.Success($"placed and labelled a new chest '{label}' at {p.X},{p.Y},{p.Z}", before - Loose())
            : ActionResult.Fail($"placed a chest at {p.X},{p.Y},{p.Z} but couldn't deposit into it");
    }

    private IEnumerable<BlockPos> Candidates(BlockPos? store)
    {
        if (store is { } s) yield return s;
        if (_s.Observations is not { } obs) yield break;
        var me = c.World.Player.Position;
        List<(ObservationRow Row, double Distance)> rows;
        try { rows = obs.Nearest("chest", me.X, me.Y, me.Z, limit: 8, maxDistance: 64); }
        catch (Exception) { yield break; }
        foreach (var (row, _) in rows)
        {
            if (row.Bounds is not { } b) continue;
            bool dbl = Math.Abs(b.MaxX - b.MinX) >= 1 || Math.Abs(b.MaxZ - b.MinZ) >= 1;
            int used = 0;
            try
            {
                foreach (var it in obs.Contents(row.Id))
                    used += (it.Count + Math.Max(1, Items.Exists(it.ItemId) ? Items.Get(it.ItemId).MaxStack : 64) - 1) / Math.Max(1, Items.Exists(it.ItemId) ? Items.Get(it.ItemId).MaxStack : 64);
            }
            catch (Exception) { }
            if (used < (dbl ? 54 : 27)) yield return new BlockPos((int)Math.Floor(b.MinX), (int)Math.Floor(b.MinY), (int)Math.Floor(b.MinZ));
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
