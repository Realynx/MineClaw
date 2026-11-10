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

    /// <summary>Why vanilla refuses a chest: the neighbour that is half of a double chest (<see cref="Other"/> null), or the two
    /// same-kind neighbours it would sit between.</summary>
    public readonly record struct Clash(BlockPos At, BlockPos? Other)
    {
        /// <summary>The refusal in words, e.g. "it would touch the double chest at 1,64,0".</summary>
        public string Words => Other is { } o
            ? $"it would sit between the chests at {At.X},{At.Y},{At.Z} and {o.X},{o.Y},{o.Z}"
            : $"it would touch the double chest at {At.X},{At.Y},{At.Z}";
    }

    /// <summary>The start of the pawn client's refusal (<see cref="Refusal"/>): the colony reads it back as the chest rule.</summary>
    public const string RefusalStart = "can't place a chest at ";

    /// <summary>The pawn client's words for a chest it won't click into <paramref name="p"/>.</summary>
    public static string Refusal(BlockPos p, Clash c) => $"{RefusalStart}{p.X},{p.Y},{p.Z}: {c.Words}";

    /// <summary>
    /// Vanilla's <c>canPlace</c> for chest <paramref name="id"/> at <paramref name="p"/> (b1.8.1 BlockChest.canPlace, 1.12.2
    /// BlockChest.canPlaceBlockAt): refused when two horizontal neighbours are the same kind, or one is and has a same-kind neighbour of
    /// its own (a double chest). <paramref name="blockAt"/> gives a cell's id, or -1 when it is unknown (unloaded): an unknown
    /// neighbour is no chest, so the answer is yes. Any other block (an ender chest, a non-chest) is always allowed here.
    /// </summary>
    public static bool CanPlace(Func<BlockPos, int> blockAt, BlockPos p, int id, out Clash clash)
    {
        clash = default;
        if (!IsPairable(id)) return true;
        BlockPos? first = null, second = null;
        foreach (var o in Sides)
        {
            var q = p + o;
            if (blockAt(q) != id) continue;
            if (HasOther(blockAt, q, p, id)) { clash = new Clash(q, null); return false; }
            if (first == null) first = q;
            else second ??= q;
        }
        if (second is not { } b) return true;
        clash = new Clash(first!.Value, b);
        return false;
    }

    /// <inheritdoc cref="CanPlace(Func{BlockPos, int}, BlockPos, int, out Clash)"/>
    public static bool CanPlace(Func<BlockPos, int> blockAt, BlockPos p, int id) => CanPlace(blockAt, p, id, out _);

    /// <summary><see cref="CanPlace(Func{BlockPos, int}, BlockPos, int, out Clash)"/> over a world view (an unloaded column is unknown).</summary>
    public static bool CanPlace(IBlockAccess w, BlockPos p, int id, out Clash clash) =>
        CanPlace(q => w.IsLoaded(q.X, q.Z) ? w.GetBlock(q.X, q.Y, q.Z) : -1, p, id, out clash);

    /// <summary>The chest at <paramref name="q"/> has a same-kind horizontal neighbour other than <paramref name="except"/>.</summary>
    private static bool HasOther(Func<BlockPos, int> blockAt, BlockPos q, BlockPos except, int id)
    {
        foreach (var o in Sides)
        {
            var r = q + o;
            if (r != except && blockAt(r) == id) return true;
        }
        return false;
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
