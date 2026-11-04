using System.Collections.Concurrent;
using MineClaw.World;

namespace MineClaw.Client;

public sealed partial class MinecraftClient
{
    // Only movement scaffolding and reflex shelter placements enter this ledger. Explicit build/design placements do not.
    private readonly ConcurrentDictionary<(int Dim, BlockPos Pos), PlacedBlock> _temporaryBlocks = new();

    private void RememberTemporaryBlock(BlockPos pos)
    {
        if (World.Placed.Get(pos, World.Dimension) is { } placed && placed.Bot == Options.Username)
            _temporaryBlocks[(placed.Dimension, pos)] = placed;
    }

    private bool OwnTemporaryBlock(BlockPos pos) =>
        _temporaryBlocks.TryGetValue((World.Dimension, pos), out var placed)
        && World.Placed.Get(pos, World.Dimension) == placed
        && PlacedBlockLog.SameBlock(placed.BlockId, World.GetBlock(pos));

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
    /// cells stay tracked for a later pass and are counted in the result; cleanup never builds more scaffolding.</summary>
    public Task<ActionResult> CleanupTemporaryBlocksAsync(CancellationToken ct = default) =>
        RunActionAsync("clear temporary blocks", ct, async c =>
        {
            int removed = 0, left = 0;
            string? why = null;
            foreach (var entry in _temporaryBlocks.Values.OrderByDescending(e => e.Pos.Y).ToList())
            {
                var p = entry.Pos;
                string? hold = CleanupHold(entry, out bool forget);
                if (forget) { _temporaryBlocks.TryRemove((entry.Dimension, p), out _); continue; }
                if (hold is not null) { left++; why ??= hold; continue; }
                var r = await DigCoreAsync(p, c, allowPathing: false).ConfigureAwait(false);
                if (r.Ok) { removed++; _temporaryBlocks.TryRemove((entry.Dimension, p), out _); }
                else { left++; why ??= r.Message; }
            }
            return ActionResult.Success($"temporary blocks: removed {removed}, left {left}" + (why == null ? "" : $" ({why})"));
        });
}
