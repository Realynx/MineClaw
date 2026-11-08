using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Client;

public sealed partial class MinecraftClient
{
    /// <summary>Removes only the exact observed label sign attached to the named chest. Editing/replacing either object
    /// invalidates the request; unrelated signs and chest contents never receive permission.</summary>
    public Task<ActionResult> RemoveStorageSignAsync(BlockPos at, BlockPos chest, IReadOnlyList<string> lines, CancellationToken ct = default) =>
        RunActionAsync("remove storage sign", ct, c => RemoveStorageSignCoreAsync(at, chest, lines, c));

    private Task<ActionResult> RemoveStorageSignCoreAsync(BlockPos at, BlockPos chest, IReadOnlyList<string> lines, CancellationToken ct)
    {
        if (!World.IsLoaded(at.X, at.Z) || !World.IsLoaded(chest.X, chest.Z))
            return Task.FromResult(ActionResult.Fail("storage sign is not loaded"));
        if (World.GetBlock(at) == 0) return Task.FromResult(ActionResult.Success("storage sign already removed"));
        int id = World.GetBlock(at), meta = World.GetMeta(at);
        string? Verify()
        {
            if (!World.IsLoaded(at.X, at.Z) || !World.IsLoaded(chest.X, chest.Z)) return "storage sign is not loaded";
            if (World.GetBlock(at) != id || World.GetMeta(at) != meta || id is not (Blocks.SignPost or Blocks.WallSign))
                return "storage sign changed: block or facing no longer matches";
            BlockPos? attached = id == Blocks.SignPost ? at.Offset(0, -1, 0) : meta switch
            {
                2 => at.Offset(0, 0, 1), 3 => at.Offset(0, 0, -1),
                4 => at.Offset(1, 0, 0), 5 => at.Offset(-1, 0, 0), _ => null,
            };
            if (attached != chest || !ChestPairs.IsPairable(World.GetBlock(chest))) return "storage sign changed: it is not attached to that chest";
            if (lines.Count != 4 || World.BlockEntities.Get(at) is not { Kind: "sign", Lines: { } current }
                || !current.SequenceEqual(lines, StringComparer.Ordinal)) return "storage sign changed: text no longer matches";
            if (NeverBreak?.Invoke(at) == true) return "storage sign is marked never-break";
            return null;
        }
        if (Verify() is { } why) return Task.FromResult(ActionResult.Fail(why));
        return DigCoreAsync(at, ct, ignoreProtection: true, floorGuard: true, requested: true, ordered: true, verify: Verify);
    }
}
