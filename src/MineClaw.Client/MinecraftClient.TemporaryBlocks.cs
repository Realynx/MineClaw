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

    /// <summary>Reclaims this body's temporary blocks after its job, never design/player placements. Unsafe or unreachable
    /// cells stay tracked for a later pass and are counted in the result; cleanup never builds more scaffolding.</summary>
    public Task<ActionResult> CleanupTemporaryBlocksAsync(CancellationToken ct = default) =>
        RunActionAsync("clear temporary blocks", ct, async c =>
        {
            int removed = 0, left = 0;
            string? why = null;
            foreach (var entry in _temporaryBlocks.Values.OrderByDescending(e => e.Pos.Y).ToList())
            {
                if (entry.Dimension != World.Dimension) { left++; why ??= "in another dimension"; continue; }
                var p = entry.Pos;
                if (!World.IsLoaded(p.X, p.Z)) { left++; why ??= "chunk not loaded"; continue; }
                if (!OwnTemporaryBlock(p)) { _temporaryBlocks.TryRemove((entry.Dimension, p), out _); continue; }
                if (Volatile.Read(ref _sheltering) != 0 || NearestThreat(World.Player.Position, 16) is not null)
                { left++; why ??= "shelter still needed or threat nearby"; continue; }
                // DigCore enforces support/player safety. Do not walk away from a still-running successor job to clean up.
                if (Aiming.FindDigAim(World, World.Player.EyePosition, p, Options.Reach) is null)
                { left++; why ??= "out of visible reach"; continue; }
                var r = await DigCoreAsync(p, c, allowPathing: false).ConfigureAwait(false);
                if (r.Ok) { removed++; _temporaryBlocks.TryRemove((entry.Dimension, p), out _); }
                else { left++; why ??= r.Message; }
            }
            return ActionResult.Success($"temporary blocks: removed {removed}, left {left}" + (why == null ? "" : $" ({why})"));
        });
}
