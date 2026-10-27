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
        int start = CountOf(port.Items());
        var full = new HashSet<BlockPos>();
        int handoffTries = 0, placeTries = 0;
        var notes = new List<string>();
        while (!ct.IsCancellationRequested && _time.GetUtcNow() < deadline)
        {
            int left = CountOf(port.Items());
            if (left == 0) break;
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
                    if (!r.Ok || CountOf(port.Items()) >= left) full.Add(chest.Pos); // full or unusable: next one
                }
                finally { await claims.ReleaseAsync(key, CancellationToken.None).ConfigureAwait(false); }
                continue;
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
        int remaining = CountOf(port.Items());
        string text = remaining == 0
            ? $"inventory empty (deposited {start})" + (notes.Count > 0 ? "; " + string.Join("; ", notes) : "")
            : $"{remaining} item(s) still held (no storage space)" + (notes.Count > 0 ? "; " + string.Join("; ", notes) : "");
        return new DepositReport(remaining == 0, remaining, start - remaining, text);
    }

    private async Task Delay(CancellationToken ct)
    {
        try { await Task.Delay(RetryDelay, _time, ct).ConfigureAwait(false); } catch (OperationCanceledException) { }
    }
}
