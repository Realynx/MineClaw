using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Skills;

/// <summary>
/// Rooms off a dug structure and strip-mine corridors off a room (pure planning, zero tokens).
/// Live 10-30 CT 114: "at each level dig out a 4x4 room that expands adjacent to the stairs, so it doesn't break the stairs";
/// the y29 room Fox approved ("see how this room just connects to the stairs easily now? remember this style") and the y16 one
/// he didn't ("see how it doesn't connect with the stairs like the last one did"). A room's floor is a stair landing's level; it
/// never takes a stair cell or a tread; with <c>gap</c> ≥ 1 a wall stays between them except a 1×2 doorway at the landing, with
/// gap 0 it opens straight onto the stair (the y29 room).
/// </summary>
public static class MinePlanner
{
    public sealed record RoomPlan(HashSet<BlockPos> Room, List<BlockPos> Door, BlockPos Landing, BlockPos Dir, BlockPos Min, BlockPos Max)
    {
        public string Side => DirName(Dir);
        public string Box => $"x{Min.X}..{Max.X} z{Min.Z}..{Max.Z} y{Min.Y}..{Max.Y}";
    }

    public static readonly BlockPos[] Dirs = [new(1, 0, 0), new(-1, 0, 0), new(0, 0, 1), new(0, 0, -1)];

    public static string DirName(BlockPos d) => (d.X, d.Z) switch { (1, _) => "east", (-1, _) => "west", (_, 1) => "south", _ => "north" };

    public static BlockPos? ParseDir(string? s) => s?.Trim().ToLowerInvariant() switch
    {
        "e" or "east" or "+x" => Dirs[0], "w" or "west" or "-x" => Dirs[1], "s" or "south" or "+z" => Dirs[2], "n" or "north" or "-z" => Dirs[3],
        _ => null,
    };

    /// <summary>
    /// Landings of a structure at feet level <paramref name="y"/>: dug cells with head room whose floor stays solid (a stair step
    /// you stand on), nearest <paramref name="near"/> first.
    /// </summary>
    public static List<BlockPos> Landings(IBlockAccess w, IReadOnlySet<BlockPos> s, int y, BlockPos near) =>
        s.Where(c => c.Y == y && s.Contains(c.Up()) && !s.Contains(c.Down()) && BlockShapes.HasCollision(w, c.X, c.Y - 1, c.Z))
            .OrderBy(c => c.DistanceSquaredTo(near)).ToList();

    /// <summary>
    /// The best W×D×H room beside a landing at <paramref name="y"/>: never a structure cell or tread, another excavation's cell,
    /// a protected or unbreakable block; with <paramref name="gap"/> ≥ 1 no room cell touches the structure (the wall stays) and
    /// the doorway runs straight out from the landing; rooms in solid rock, centred on the landing, on <paramref name="side"/>
    /// when asked, rank first. Null + why when nothing fits.
    /// </summary>
    public static (RoomPlan? Plan, string? Why) Room(WorldState w, IReadOnlySet<BlockPos> s, int y, int width, int depth, int height,
        int gap, BlockPos? side, BlockPos near, Func<BlockPos, bool>? isProtected = null, string? owner = null)
    {
        var landings = Landings(w, s, y, near);
        if (landings.Count == 0) return (null, $"no stair landing at y{y} (the structure has no dug cell standing on a floor there)");
        var treads = DigPattern.Floors(s);
        bool Bad(BlockPos c)
        {
            if (s.Contains(c) || treads.Contains(c) || !w.IsLoaded(c.X, c.Z) || c.Y <= 0 || c.Y >= w.WorldHeight - 1) return true;
            if (w.KeepAir.Owner(c, w.Dimension) is { } o && !string.Equals(o, owner, StringComparison.OrdinalIgnoreCase)) return true;
            int id = w.GetBlock(c);
            if (id != 0 && (Blocks.Get(id).Hardness < 0 || Blocks.Get(id).Interactable)) return true;
            return isProtected?.Invoke(c) == true;
        }
        RoomPlan? best = null;
        double bestScore = double.MaxValue;
        foreach (var l in landings.Take(4))
        foreach (var d in Dirs)
        {
            if (side is { } sd && sd != d) continue;
            if (s.Contains(l + d)) continue; // inward, into the stair itself
            var p = new BlockPos(d.Z, 0, d.X); // across
            var door = new List<BlockPos>();
            bool ok = true;
            for (int i = 1; i <= gap && ok; i++)
                foreach (var c in new[] { l + Mul(d, i), (l + Mul(d, i)).Up() })
                    if (Bad(c)) ok = false; else door.Add(c);
            if (!ok) continue;
            for (int o = -(width - 1); o <= 0; o++)
            {
                var room = new HashSet<BlockPos>();
                int air = 0;
                for (int i = 0; i < depth && ok; i++)
                for (int j = 0; j < width && ok; j++)
                for (int k = 0; k < height && ok; k++)
                {
                    var c = l + Mul(d, gap + 1 + i) + Mul(p, o + j) + new BlockPos(0, k, 0);
                    if (Bad(c)) { ok = false; break; }
                    if (gap > 0 && BlockFaceExtensions.All.Any(f => s.Contains(c.Offset(f)))) { ok = false; break; }
                    if (!BlockShapes.HasCollision(w, c.X, c.Y, c.Z)) air++;
                    room.Add(c);
                }
                if (!ok) { ok = true; continue; }
                // The doorway (or, gap 0, the room's first layer) meets the landing at its floor and head cells: walk straight off the step.
                var entry = l + Mul(d, gap + 1);
                if (!room.Contains(entry) || !room.Contains(entry.Up())) continue;
                double score = air * 2 + Math.Abs(o + (width - 1) / 2.0) + 0.5 * landings.IndexOf(l);
                if (score >= bestScore) continue;
                var (lo, hi) = DigVolume.Bounds(room);
                bestScore = score;
                best = new RoomPlan(room, door, l, d, lo, hi);
            }
        }
        return best is null ? (null, $"no {width}×{depth}×{height} room fits beside the landing{(landings.Count > 1 ? "s" : "")} at y{y} without cutting the stair or another dig{(side is { } x ? $" on the {DirName(x)} side" : "")}") : (best, null);
    }

    private static BlockPos Mul(BlockPos d, int n) => new(d.X * n, d.Y * n, d.Z * n);

    public sealed record StripPlan(Dictionary<BlockPos, int> Cells, BlockPos Mouth, BlockPos Dir, int Branches, int Length);

    /// <summary>
    /// A strip mine off a room: a main corridor (1 wide, <paramref name="height"/> high) out of the room's middle on the side
    /// away from its door, <paramref name="length"/> long, and side branches both ways every <paramref name="spacing"/> blocks
    /// (Fox: "a labyrinth essentially starting at the 4x4 rooms"). Cells that would cut another excavation (or its floors) end
    /// that corridor early. Phase 0 = the main corridor, 1 = the branches.
    /// </summary>
    public static (StripPlan? Plan, string? Why) Strip(WorldState w, IReadOnlySet<BlockPos> room, BlockPos? dir, BlockPos? away, int length,
        int spacing, int branch, int height, string owner)
    {
        if (room.Count == 0) return (null, "the room has no cells");
        var (lo, hi) = DigVolume.Bounds(room);
        int floor = lo.Y;
        var d = dir ?? away ?? Dirs[0];
        var keepSolid = new HashSet<BlockPos>();
        foreach (var o in w.KeepAir.Owners)
            if (!string.Equals(o, owner, StringComparison.OrdinalIgnoreCase)) keepSolid.UnionWith(DigPattern.Floors(w.KeepAir.Cells(o)));
        bool Bad(BlockPos c) => room.Contains(c) || keepSolid.Contains(c) || !w.IsLoaded(c.X, c.Z)
                                || w.KeepAir.Owner(c, w.Dimension) is { } o && !string.Equals(o, owner, StringComparison.OrdinalIgnoreCase)
                                || w.GetBlock(c) is var id && id != 0 && (Blocks.Get(id).Hardness < 0 || Blocks.Get(id).Interactable);
        // Mouth: the cell just outside the room's wall on side d, in the middle of that wall, at floor level.
        int cx = (lo.X + hi.X) / 2, cz = (lo.Z + hi.Z) / 2;
        var mouth = d.X switch
        {
            1 => new BlockPos(hi.X + 1, floor, cz), -1 => new BlockPos(lo.X - 1, floor, cz),
            _ => d.Z == 1 ? new BlockPos(cx, floor, hi.Z + 1) : new BlockPos(cx, floor, lo.Z - 1),
        };
        var cells = new Dictionary<BlockPos, int>();
        int main = 0;
        for (int i = 0; i < length; i++)
        {
            var c = mouth + Mul(d, i);
            if (Enumerable.Range(0, height).Any(k => Bad(c.Up(k)))) break;
            for (int k = 0; k < height; k++) cells[c.Up(k)] = 0;
            main++;
        }
        if (main == 0) return (null, $"the {DirName(d)} wall of the room is against another dig");
        var across = new BlockPos(d.Z, 0, d.X);
        int branches = 0;
        for (int i = spacing; i < main; i += spacing + 1)
            foreach (var side in new[] { across, Mul(across, -1) })
            {
                int n = 0;
                for (int j = 1; j <= branch; j++)
                {
                    var c = mouth + Mul(d, i) + Mul(side, j);
                    if (Enumerable.Range(0, height).Any(k => Bad(c.Up(k)))) break;
                    // Branches stay apart: never beside a cell of another branch or the room.
                    for (int k = 0; k < height; k++) cells.TryAdd(c.Up(k), 1);
                    n++;
                }
                if (n > 0) branches++;
            }
        return (new StripPlan(cells, mouth, d, branches, main), null);
    }
}
