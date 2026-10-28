namespace MineClaw.World;

/// <summary>
/// A water column poured from a bucket at an island edge, per mc-dev (commit 218a2a71):
/// <list type="bullet">
/// <item>ItemBucket (full): the server raytraces from the player's eye along its look (liquids ignored), and puts a SOURCE
/// (meta 0) in the cell beside the face it hits when that cell is empty or a non-buildable material. Empty bucket: the ray
/// stops only at a liquid SOURCE (BlockFluids.a(meta, true) = meta 0) or a solid block, passing through flowing water, and
/// takes the source (only meta 0).</item>
/// <item>BlockFlowing (water tick rate 5): a cell whose block below can take water flows ONLY down, the cell below becoming
/// falling water (meta 8, or level + 8); it spreads sideways (level + 1, at most 7) only where it can't fall. Falling water
/// with nothing feeding it from above evaluates to −1 and is removed: a column dries top-down once its source is gone.</item>
/// <item>BlockFlowing.l/k: water can't flow into a door, sign, ladder, reed, portal or any solid material; lava is never
/// replaced by water flow (it hardens instead, so planners keep columns away from lava).</item>
/// </list>
/// </summary>
public static class WaterColumn
{
    /// <summary>BlockFluids.c(): water ticks every 5 game ticks, so a column grows one cell per 5 ticks.</summary>
    public const int TickRate = 5;

    /// <summary>BlockFlowing.k: blocks that stop flow without being solid material.</summary>
    public static bool BlocksFlow(int id) => id is BlockShapes.WoodDoor or BlockShapes.IronDoor or BlockShapes.Ladder or 63 or 68 or 83 or BlockShapes.Portal
                                             || (id != 0 && !BlockShapes.IsLiquid(id) && BlockShapes.MaterialIsBuildable(id));

    /// <summary>BlockFlowing.l: water may flow into (x,y,z): not water already, not lava, not flow-blocking.</summary>
    public static bool CanFlowInto(IBlockAccess w, int x, int y, int z)
    {
        if (y < 0) return false;
        int id = w.GetBlock(x, y, z);
        if (BlockShapes.IsWater(id) || BlockShapes.IsLava(id)) return false;
        return !BlocksFlow(id);
    }

    /// <summary>
    /// Where the falling water under <paramref name="source"/> ends: the lowest water cell and whether it landed on something
    /// (false = it runs out of the world into the void, y 0).
    /// </summary>
    public static (int BottomY, bool Landed) Extent(IBlockAccess w, BlockPos source)
    {
        int y = source.Y;
        while (y - 1 >= 0 && CanFlowInto(w, source.X, y - 1, source.Z)) y--;
        return (y, y - 1 >= 0);
    }

    /// <summary>
    /// The cells the poured source fills once settled: the source, the falling column, and where it lands the sideways spread
    /// (level 1..7 over a floor, BFS; a spread cell over a drop falls again and is followed down). Approximate where flows meet
    /// (the planner only needs the cells, not exact levels).
    /// </summary>
    public static List<(BlockPos Pos, int Meta)> Cells(IBlockAccess w, BlockPos source, int maxCells = 512)
    {
        var cells = new Dictionary<BlockPos, int> { [source] = 0 };
        var falls = new Queue<(BlockPos From, int Level)>();
        falls.Enqueue((source, 0));
        while (falls.Count > 0 && cells.Count < maxCells)
        {
            var (from, level) = falls.Dequeue();
            var p = from;
            // Down while it can.
            while (p.Y - 1 >= 0 && CanFlowInto(w, p.X, p.Y - 1, p.Z) && !cells.ContainsKey(p.Down()))
            {
                p = p.Down();
                cells[p] = 8;
                level = 0;
                if (cells.Count >= maxCells) break;
            }
            if (p.Y - 1 < 0 || cells.ContainsKey(p.Down())) continue; // the void, or met itself
            // Landed: spread sideways (a falling cell spreads at level 1).
            var q = new Queue<(BlockPos P, int L)>();
            q.Enqueue((p, cells[p] >= 8 ? 0 : level));
            while (q.Count > 0 && cells.Count < maxCells)
            {
                var (c, l) = q.Dequeue();
                int next = l + 1;
                if (next >= 8) continue;
                foreach (var (dx, dz) in Sides)
                {
                    var n = c.Offset(dx, 0, dz);
                    if (cells.ContainsKey(n) || !CanFlowInto(w, n.X, n.Y, n.Z)) continue;
                    cells[n] = next;
                    if (CanFlowInto(w, n.X, n.Y - 1, n.Z)) falls.Enqueue((n, next));
                    else q.Enqueue((n, next));
                }
            }
        }
        return cells.Select(kv => (kv.Key, kv.Value)).ToList();
    }

    /// <summary>Ticks until a column poured at <paramref name="source"/> reaches down to <paramref name="y"/>.</summary>
    public static int TicksToReach(BlockPos source, int y) => Math.Max(0, source.Y - y) * TickRate;

    private static readonly (int Dx, int Dz)[] Sides = [(1, 0), (-1, 0), (0, 1), (0, -1)];
}
