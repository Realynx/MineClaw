using System.Collections.Concurrent;
using MineClaw.World;

namespace MineClaw.Client;

public sealed partial class MinecraftClient
{
    // Only movement scaffolding, reflex shelter and clutch placements enter this ledger. Explicit build/design placements do not.
    // Fox 11-05: "Bots should delete whatever they put down unless it was a directive to build it."
    private readonly ConcurrentDictionary<(int Dim, BlockPos Pos), PlacedBlock> _temporaryBlocks = new();
    // A predicted placement (a clutch block) waits here until the server's block change records it as ours.
    private readonly ConcurrentDictionary<(int Dim, BlockPos Pos), long> _pendingTemporary = new();

    private void RememberTemporaryBlock(BlockPos pos)
    {
        if (World.Placed.Get(pos, World.Dimension) is { } placed && placed.Bot == Options.Username)
            _temporaryBlocks[(placed.Dimension, pos)] = placed;
    }

    /// <summary>A placement sent without waiting for the server (the fall clutch): adopted as temporary once it is confirmed ours.</summary>
    private void MarkTemporarySoon(BlockPos pos) => _pendingTemporary[(World.Dimension, pos)] = TickCount;

    /// <summary>Pending placements the server confirmed become temporary blocks; ones never confirmed in 10 s are dropped.</summary>
    private void AdoptPendingTemporary()
    {
        foreach (var ((dim, pos), tick) in _pendingTemporary.ToArray())
        {
            if (dim == World.Dimension && World.Placed.Get(pos, dim) is { } placed && placed.Bot == Options.Username)
                _temporaryBlocks[(dim, pos)] = placed;
            else if (TickCount - tick < 200) continue;
            _pendingTemporary.TryRemove((dim, pos), out _);
        }
    }

    private bool OwnTemporaryBlock(BlockPos pos) =>
        _temporaryBlocks.TryGetValue((World.Dimension, pos), out var placed)
        && World.Placed.Get(pos, World.Dimension) == placed
        && PlacedBlockLog.SameBlock(placed.BlockId, World.GetBlock(pos));

    /// <summary>
    /// This body's temporary blocks still standing (scaffolds its walks placed, a shelter box, a clutch block), for its colony's
    /// registry: what the body can't take back itself becomes a cleanup job for any pawn (docs/web/story-mode.md "Temporary blocks").
    /// </summary>
    public IReadOnlyList<PlacedBlock> TemporaryBlocks()
    {
        AdoptPendingTemporary();
        foreach (var e in _temporaryBlocks.Values)
            if (e.Dimension == World.Dimension && World.IsLoaded(e.Pos.X, e.Pos.Z) && !OwnTemporaryBlock(e.Pos))
                _temporaryBlocks.TryRemove((e.Dimension, e.Pos), out _); // dug, replaced or someone else's now
        return _temporaryBlocks.Values.ToList();
    }

    /// <summary>Why a temporary block stays for a later pass (another dimension, not loaded, shelter or threat, out of reach), or null to
    /// dig it; <paramref name="forget"/> when it is no longer ours (dropped from the list).</summary>
    private string? CleanupHold(PlacedBlock entry, out bool forget)
    {
        forget = false;
        if (entry.Dimension != World.Dimension) return "in another dimension";
        var p = entry.Pos;
        if (!World.IsLoaded(p.X, p.Z)) return "chunk not loaded";
        forget = !OwnTemporaryBlock(p);
        if (forget) return null;
        if (Volatile.Read(ref _sheltering) != 0 || NearestThreat(World.Player.Position, 16) is not null) return "shelter still needed or threat nearby";
        // DigCore enforces support/player safety. Do not walk away from a still-running successor job to clean up.
        return Aiming.FindDigAim(World, World.Player.EyePosition, p, Options.Reach) is null ? "out of visible reach" : null;
    }

    /// <summary>Reclaims this body's temporary blocks after its job, never design/player placements. Unsafe or unreachable
    /// cells stay tracked for a later pass and are counted (and named) in the result; cleanup never builds more scaffolding.</summary>
    public Task<ActionResult> CleanupTemporaryBlocksAsync(CancellationToken ct = default)
    {
        AdoptPendingTemporary();
        if (_temporaryBlocks.IsEmpty) return Task.FromResult(ActionResult.Success("temporary blocks: none"));
        return RunActionAsync("clear temporary blocks", ct, async c =>
        {
            int removed = 0;
            var left = new List<BlockPos>();
            string? why = null;
            foreach (var entry in _temporaryBlocks.Values.OrderByDescending(e => e.Pos.Y).ToList())
            {
                var p = entry.Pos;
                string? hold = CleanupHold(entry, out bool forget);
                if (forget) { _temporaryBlocks.TryRemove((entry.Dimension, p), out _); continue; }
                if (hold is not null) { left.Add(p); why ??= hold; continue; }
                var r = await DigCoreAsync(p, c, allowPathing: false).ConfigureAwait(false);
                if (r.Ok) { removed++; _temporaryBlocks.TryRemove((entry.Dimension, p), out _); }
                else { left.Add(p); why ??= r.Message; }
            }
            return ActionResult.Success($"temporary blocks: removed {removed}, left {left.Count}" + (why == null ? "" : $" ({why})")
                + (left.Count == 0 ? "" : " at " + string.Join(" ", left.Take(8).Select(Fmt))));
        });
    }
}
