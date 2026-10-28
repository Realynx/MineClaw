using MineClaw.World;

namespace MineClaw.Client;

public sealed partial class MinecraftClient
{
    /// <summary>
    /// Places a block variant (slab type, wool colour, log type: the item's <paramref name="damage"/>) at (x,y,z), optionally by
    /// clicking a chosen <paramref name="face"/> of its neighbour (doors: <see cref="BlockFace.Top"/> of the block below, as
    /// mc-dev ItemDoor requires). The blueprint executor's placement; same checks, sneaking and verification as every place.
    /// </summary>
    public Task<ActionResult> PlaceVariantAsync(int x, int y, int z, int itemId, int? damage, BlockFace? face, CancellationToken ct = default) =>
        RunActionAsync("place", ct, c => PlaceCoreAsync(new BlockPos(x, y, z), itemId, c, damage: damage, face: face));
}
