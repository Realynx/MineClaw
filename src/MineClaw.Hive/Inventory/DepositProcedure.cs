using MineClaw.Client;
using MineClaw.Hive.Ipc;
using MineClaw.World;
using MineClaw.World.Claims;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MineClaw.Hive.Inventory;

/// <summary>A known storage chest and an estimate of its free slots (from WorldIndexer container snapshots).</summary>
public sealed record StorageChest(BlockPos Pos, int FreeSlots, double Distance);

/// <summary>What the deposit procedure needs from a bot (real: <see cref="GameInventoryPort"/>; tests: a fake).</summary>
public interface IInventoryPort
{
    /// <summary>Every non-empty stack in the inventory window (armor, main, hotbar, crafting grid, cursor).</summary>
    IReadOnlyList<ItemMsg> Items();
    /// <summary>In game, alive, not in danger: safe to walk to a chest.</summary>
    bool CanActSafely { get; }
    /// <summary>The bot's current dimension (0 overworld, -1 nether): claims are per dimension.</summary>
    int Dimension => 0;
    /// <summary>Known chests, nearest first, with free-slot estimates.</summary>
    IReadOnlyList<StorageChest> Chests();
    Task<ActionResult> DepositToAsync(BlockPos chest, CancellationToken ct);
    /// <summary>Toss everything at <paramref name="at"/> (the receiver's feet).</summary>
    Task<ActionResult> TossAllAtAsync(Vec3d at, CancellationToken ct);
    /// <summary>Place a chest from the inventory near here and label it with a sign. Returns its position.</summary>
    Task<(ActionResult Result, BlockPos? Chest)> PlaceLabelledChestAsync(string label, CancellationToken ct);
    /// <summary>
    /// Makes <see cref="Items"/> reflect the server, not a predicted/stale copy: no open window, no click still waiting for
    /// its 0x6A, the latest set-slot/window-items applied (and, on the real client, a forced full resync). False when the
    /// state couldn't be confirmed (the caller then trusts nothing it can't re-read).
    /// </summary>
    Task<bool> SettleAsync(CancellationToken ct) => Task.FromResult(true);
    /// <summary>
    /// Walk to an open, standable spot nearby (out of a hole or a generator cell) so a chest that "can't be reached" from
    /// here may be reachable from there. False when there's nowhere better to stand. Default: unsupported.
    /// </summary>
    Task<bool> StepOutAsync(CancellationToken ct) => Task.FromResult(false);
    /// <summary>Walk to within ~2 blocks of <paramref name="at"/> (to receive a hand-off from main). Default: already there.</summary>
    Task<bool> GoNearAsync(Vec3d at, CancellationToken ct) => Task.FromResult(true);
}

/// <summary>Who to hand items to when storage is full (main), over IPC.</summary>
public interface IHandoffTarget
{
    /// <summary>Where the receiver stands, or null if it can't take items now.</summary>
    Task<Vec3d?> OfferAsync(int items, CancellationToken ct);
    /// <summary>Items the receiver collected since the offer (its collect events).</summary>
    Task<int> ConfirmAsync(CancellationToken ct);
}

public sealed record DepositReport(bool Empty, int Remaining, int Deposited, string Text);

/// <summary>
/// Mandatory deposit before a planned logout: empty the ENTIRE inventory into storage chests (WorldIndexer data for space,
/// claim board leases so two clones don't use one chest at once); if storage is full, hand the items to the main bot (toss
/// at its feet, confirmed by its collect events), else place and label a new chest. Repeats until the inventory window is
/// verified empty or the deadline passes. The caller logs out only when <see cref="DepositReport.Empty"/>.
/// </summary>
public sealed class DepositProcedure(IInventoryPort port, IClaimBoard claims, string label, IHandoffTarget? handoff = null, ILogger? log = null,
    TimeProvider? time = null)
{
    private readonly ILogger _log = log ?? NullLogger.Instance;
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(2);

    public static int CountOf(IEnumerable<ItemMsg> items) => items.Sum(i => i.Count);

    public async Task<DepositReport> RunAsync(TimeSpan timeout, CancellationToken ct)
    {
        var deadline = _time.GetUtcNow() + timeout;
        // Never decide "empty" from a stale snapshot (a cancelled worker may leave predicted clicks / an open window behind).
        await SettleAsync(ct).ConfigureAwait(false);
        int start = CountOf(port.Items());
        bool fresh = true; // Items() was just settled and nothing has changed it since
        var full = new HashSet<BlockPos>();
        int handoffTries = 0, placeTries = 0, stepOuts = 0;
        var unreachable = new HashSet<BlockPos>();
        var notes = new List<string>();
        while (!ct.IsCancellationRequested && _time.GetUtcNow() < deadline)
        {
            int left = CountOf(port.Items());
            if (left == 0 && !fresh)
            {
                await SettleAsync(ct).ConfigureAwait(false);
                fresh = true;
                left = CountOf(port.Items());
                if (left > 0) _log.LogInformation("deposit: fresh inventory still holds {N} item(s)", left);
            }
            if (left == 0) break;
            fresh = false; // whatever happens next changes the inventory: settle again before trusting "empty"
            if (!port.CanActSafely) { await Delay(ct); continue; }

            // 1. Storage chests with room (nearest first), skipping ones another bot is using.
            var chest = port.Chests().FirstOrDefault(c => c.FreeSlots > 0 && !full.Contains(c.Pos) && claims.IsFree(ClaimKey.Chest(c.Pos, port.Dimension)));
            if (chest != null)
            {
                var key = ClaimKey.Chest(chest.Pos, port.Dimension);
                var lease = await claims.TryClaimAsync(key, TimeSpan.FromSeconds(60), "deposit", ct).ConfigureAwait(false);
                if (!lease.Ok) { full.Add(chest.Pos); continue; }
                try
                {
                    var r = await port.DepositToAsync(chest.Pos, ct).ConfigureAwait(false);
                    _log.LogInformation("deposit into chest {Pos}: {Result}", chest.Pos, r.Message);
                    if (!r.Ok && IsUnreachable(r)) unreachable.Add(chest.Pos);
                    if (!r.Ok || CountOf(port.Items()) >= left) full.Add(chest.Pos); // full or unusable: next one
                }
                finally { await claims.ReleaseAsync(key, CancellationToken.None).ConfigureAwait(false); }
                continue;
            }

            // 1b. A chest with room was only out of reach from HERE (live2: a recall from inside the generator hole handed
            // Fox's pickaxe to main on the first "can't reach chest"): step out to open ground and try those chests again
            // before handing anything off. Tools and items belong in storage, not at main's feet.
            if (unreachable.Count > 0 && stepOuts < 2)
            {
                stepOuts++;
                bool moved = false;
                try { moved = await port.StepOutAsync(ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { _log.LogWarning(ex, "deposit: step-out failed"); }
                _log.LogInformation("deposit: {N} chest(s) out of reach from here; stepped out: {Moved}", unreachable.Count, moved);
                if (moved)
                {
                    foreach (var u in unreachable) full.Remove(u);
                    unreachable.Clear();
                    notes.Add("stepped out to reach storage");
                    continue;
                }
                unreachable.Clear();
            }

            // 2. Storage full: hand the items to main (toss at its feet, confirmed by its collect events).
            if (handoff != null && handoffTries < 2)
            {
                handoffTries++;
                var at = await handoff.OfferAsync(left, ct).ConfigureAwait(false);
                if (at is { } feet)
                {
                    var r = await port.TossAllAtAsync(feet, ct).ConfigureAwait(false);
                    await Task.Delay(TimeSpan.FromSeconds(2), _time, ct).ConfigureAwait(false);
                    int got = await handoff.ConfirmAsync(ct).ConfigureAwait(false);
                    notes.Add($"handed {got} to main");
                    _log.LogInformation("handoff to main: {Result}; main collected {Got}", r.Message, got);
                    continue;
                }
            }

            // 3. Place and label a new chest (needs a chest item), then deposit into it.
            if (placeTries < 1)
            {
                placeTries++;
                var (r, pos) = await port.PlaceLabelledChestAsync(label, ct).ConfigureAwait(false);
                _log.LogInformation("new storage chest: {Result}", r.Message);
                if (r.Ok && pos is { } p)
                {
                    notes.Add($"placed chest at {p.X},{p.Y},{p.Z}");
                    var d = await port.DepositToAsync(p, ct).ConfigureAwait(false);
                    _log.LogInformation("deposit into new chest: {Result}", d.Message);
                }
                continue;
            }

            // Nothing worked yet: wait (someone may free space / come online) and try every chest again.
            full.Clear();
            handoffTries = Math.Min(handoffTries, 1);
            await Delay(ct);
        }
        if (!fresh) await SettleAsync(CancellationToken.None).ConfigureAwait(false);
        int remaining = CountOf(port.Items());
        string text = remaining == 0
            ? $"inventory empty (deposited {start})" + (notes.Count > 0 ? "; " + string.Join("; ", notes) : "")
            : $"{remaining} item(s) still held (no storage space)" + (notes.Count > 0 ? "; " + string.Join("; ", notes) : "");
        return new DepositReport(remaining == 0, remaining, start - remaining, text);
    }

    /// <summary>The deposit failed because the chest couldn't be reached (path/reach), not because it was full.</summary>
    internal static bool IsUnreachable(ActionResult r) =>
        r.Message is { } m && (m.Contains("reach", StringComparison.OrdinalIgnoreCase) || m.Contains("no route", StringComparison.OrdinalIgnoreCase)
            || m.Contains("no path", StringComparison.OrdinalIgnoreCase));

    private async Task SettleAsync(CancellationToken ct)
    {
        try
        {
            if (!await port.SettleAsync(ct).ConfigureAwait(false)) _log.LogWarning("deposit: couldn't confirm the inventory with the server");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _log.LogWarning(ex, "deposit: inventory settle failed"); }
    }

    private async Task Delay(CancellationToken ct)
    {
        try { await Task.Delay(RetryDelay, _time, ct).ConfigureAwait(false); } catch (OperationCanceledException) { }
    }
}
