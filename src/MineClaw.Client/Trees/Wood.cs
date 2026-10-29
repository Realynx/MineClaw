using MineClaw.Data;

namespace MineClaw.Client.Trees;

/// <summary>
/// Wood species across both log blocks (multi-version P6): <c>log</c> 17 / <c>leaves</c> 18 carry oak, spruce, birch, jungle
/// in meta &amp; 3; <c>log2</c> 162 / <c>leaves2</c> 161 (1.12.2) carry acacia and dark oak in meta &amp; 1. The species index is
/// the sapling damage (0 oak, 1 spruce, 2 birch, 3 jungle, 4 acacia, 5 dark oak). Ids 161/162 never occur in a b1.8.1 world,
/// so b1.8.1 sees exactly the old meta &amp; 3 behaviour.
/// </summary>
public static class Wood
{
    public const int Log2 = 162, Leaves2 = 161;
    public const int Oak = 0, Spruce = 1, Birch = 2, Jungle = 3, Acacia = 4, DarkOak = 5;

    public static bool IsLog(int id) => id is Blocks.Log or Log2;
    public static bool IsLeaves(int id) => id is Blocks.Leaves or Leaves2;

    /// <summary>The species of a log/leaves block (or of a log item: its damage).</summary>
    public static int Type(int id, int meta) => id is Log2 or Leaves2 ? (meta & 1) + 4 : meta & 3;

    /// <summary>The log block/item id that holds <paramref name="wood"/>.</summary>
    public static int LogId(int wood) => wood >= Acacia ? Log2 : Blocks.Log;
    /// <summary>The leaves block id that holds <paramref name="wood"/>.</summary>
    public static int LeavesId(int wood) => wood >= Acacia ? Leaves2 : Blocks.Leaves;
    /// <summary>The log item's damage for <paramref name="wood"/> (axis bits stripped, like the dropped item).</summary>
    public static int LogDamage(int wood) => wood >= Acacia ? wood - Acacia : wood;

    /// <summary>A sapling of this species only grows as a 2x2 of four (dark oak; jungle/spruce can also grow alone).</summary>
    public static bool NeedsFourSaplings(int wood) => wood == DarkOak;
}
