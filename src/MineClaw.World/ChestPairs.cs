namespace MineClaw.World;

/// <summary>
/// Double chests, the one rule every chest reader shares (user report 10-29: "chests placed next to each other don't combine
/// into a double chest, so the bot can't tell"). Vanilla (b1.8.1 <c>BlockChest.interact</c>, 1.12.2 <c>BlockChest.getContainer</c>
/// / <c>TileEntityChest.isChestAt</c>): two horizontally touching blocks of the SAME chest kind (chest 54 with chest 54, trapped
/// chest 146 with trapped chest 146; the ender chest 130 never pairs) are ONE 54-slot window, whichever block is clicked; its
/// slots 0-26 are the west (-X) or north (-Z) half, 27-53 the east/south one. Stacked or diagonal chests never pair. Placement
/// never lets a third chest touch a pair (<c>canPlace</c>), so a chest with two same-kind neighbours, or whose neighbour has
/// another one, is a layout vanilla can't build (world edits; the game then nests 81-slot windows): each such block reads as
/// a single here, the same answer from every block, never two pairs fighting over one block.
/// </summary>
public static class ChestPairs
{
    public const int Chest = 54, TrappedChest = 146;
    public const int HalfSlots = 27, DoubleSlots = 54;

    private static readonly BlockPos[] Sides = [new(-1, 0, 0), new(1, 0, 0), new(0, 0, -1), new(0, 0, 1)];

    /// <summary>A block that pairs into a double chest with its own kind (b1.8.1 has no 146: never seen there).</summary>
    public static bool IsPairable(int id) => id is Chest or TrappedChest;

    /// <summary>The other half of the double chest at <paramref name="p"/>, or null (a single, not a chest, an unbuildable layout).
    /// An unloaded neighbour reads as air: callers that remember a pair keep it while the other half's column is away.</summary>
    public static BlockPos? Partner(IBlockAccess w, BlockPos p)
    {
        int id = w.GetBlock(p.X, p.Y, p.Z);
        if (!IsPairable(id) || Only(w, p, id) is not { } q) return null;
        return Only(w, q, id) == p ? q : null;
    }

    /// <summary>The single same-kind horizontal neighbour of <paramref name="p"/>; null for none or several.</summary>
    private static BlockPos? Only(IBlockAccess w, BlockPos p, int id)
    {
        BlockPos? found = null;
        foreach (var o in Sides)
        {
            var q = p + o;
            if (w.GetBlock(q.X, q.Y, q.Z) != id) continue;
            if (found != null) return null;
            found = q;
        }
        return found;
    }

    /// <summary>True when <paramref name="a"/> is the west/north half of the pair (a, b): its slots come first (0-26).</summary>
    public static bool IsFirst(BlockPos a, BlockPos b) => a.X != b.X ? a.X < b.X : a.Z < b.Z;

    /// <summary>(west/north half, the other half or null for a single) of the chest at <paramref name="p"/>.</summary>
    public static (BlockPos First, BlockPos? Second) Halves(IBlockAccess w, BlockPos p) =>
        Partner(w, p) is { } q ? IsFirst(p, q) ? (p, q) : (q, p) : (p, null);

    /// <summary>The one cell that names the container at <paramref name="p"/> (its west/north half): the same from either block.</summary>
    public static BlockPos Canonical(IBlockAccess w, BlockPos p) => Halves(w, p).First;

    /// <summary>The blocks of the container at <paramref name="p"/> (one, or both halves first-half first).</summary>
    public static BlockPos[] Cells(IBlockAccess w, BlockPos p) => Halves(w, p) is (var a, { } b) ? [a, b] : [p];

    /// <summary>One position per container (its <see cref="Canonical"/> cell), in first-seen order: both halves of a double
    /// chest found by a block search are one chest to open, count and claim.</summary>
    public static List<BlockPos> Distinct(IBlockAccess w, IEnumerable<BlockPos> cells)
    {
        var seen = new HashSet<BlockPos>();
        var list = new List<BlockPos>();
        foreach (var c in cells)
        {
            var key = Canonical(w, c);
            if (seen.Add(key)) list.Add(key);
        }
        return list;
    }
}
