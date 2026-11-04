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

    /// <summary>Direction names (a letter, the word, or the axis sign) → <see cref="Dirs"/>.</summary>
    private static readonly Dictionary<string, BlockPos> DirNames = new()
    {
        ["e"] = Dirs[0], ["east"] = Dirs[0], ["+x"] = Dirs[0],
        ["w"] = Dirs[1], ["west"] = Dirs[1], ["-x"] = Dirs[1],
        ["s"] = Dirs[2], ["south"] = Dirs[2], ["+z"] = Dirs[2],
        ["n"] = Dirs[3], ["north"] = Dirs[3], ["-z"] = Dirs[3],
    };

    public static BlockPos? ParseDir(string? s) => s?.Trim().ToLowerInvariant() is { } k && DirNames.TryGetValue(k, out var d) ? d : null;

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
        var search = new RoomSearch(w, s, width, depth, height, gap, isProtected, owner, landings);
        foreach (var l in landings.Take(4))
        foreach (var d in Dirs)
            if ((side is not { } sd || sd == d) && !s.Contains(l + d)) search.Try(l, d); // never inward, into the stair itself
        return search.Best is { } best ? (best, null) : (null, NoRoom(width, depth, height, landings.Count, y, side));
    }

    private static string NoRoom(int width, int depth, int height, int landings, int y, BlockPos? side) =>
        $"no {width}×{depth}×{height} room fits beside the landing{(landings > 1 ? "s" : "")} at y{y} without cutting the stair or another dig{(side is { } x ? $" on the {DirName(x)} side" : "")}";

    /// <summary>The rooms tried beside the landings, keeping the best: least air to dig, centred on the landing, nearest landing.</summary>
    private sealed class RoomSearch(WorldState w, IReadOnlySet<BlockPos> s, int width, int depth, int height, int gap,
        Func<BlockPos, bool>? isProtected, string? owner, List<BlockPos> landings)
    {
        private readonly HashSet<BlockPos> _treads = DigPattern.Floors(s);
        private double _bestScore = double.MaxValue;
        public RoomPlan? Best { get; private set; }

        /// <summary>A cell no room may take: the structure, its treads, unloaded or out of the world, another dig's, an
        /// unbreakable or interactable block, a protected one.</summary>
        private bool Bad(BlockPos c)
        {
            if (s.Contains(c) || _treads.Contains(c) || !w.IsLoaded(c.X, c.Z) || c.Y <= 0 || c.Y >= w.WorldHeight - 1) return true;
            if (w.KeepAir.Owner(c, w.Dimension) is { } o && !string.Equals(o, owner, StringComparison.OrdinalIgnoreCase)) return true;
            return Unbreakable(w.GetBlock(c)) || isProtected?.Invoke(c) == true;
        }

        private static bool Unbreakable(int id) => id != 0 && (Blocks.Get(id).Hardness < 0 || Blocks.Get(id).Interactable);

        /// <summary>Every width offset of a room off landing <paramref name="l"/> in direction <paramref name="d"/>.</summary>
        public void Try(BlockPos l, BlockPos d)
        {
            if (Door(l, d) is not { } door) return;
            var p = new BlockPos(d.Z, 0, d.X); // across
            for (int o = -(width - 1); o <= 0; o++) Consider(l, d, p, o, door);
        }

        /// <summary>The 1×2 doorway cells straight out from the landing (none with gap 0); null when one is bad.</summary>
        private List<BlockPos>? Door(BlockPos l, BlockPos d)
        {
            var door = new List<BlockPos>();
            for (int i = 1; i <= gap; i++)
                foreach (var c in new[] { l + Mul(d, i), (l + Mul(d, i)).Up() })
                {
                    if (Bad(c)) return null;
                    door.Add(c);
                }
            return door;
        }

        private void Consider(BlockPos l, BlockPos d, BlockPos p, int o, List<BlockPos> door)
        {
            if (Cells(l, d, p, o, out int air) is not { } room) return;
            // The doorway (or, gap 0, the room's first layer) meets the landing at its floor and head cells: walk straight off the step.
            var entry = l + Mul(d, gap + 1);
            if (!room.Contains(entry) || !room.Contains(entry.Up())) return;
            double score = air * 2 + Math.Abs(o + (width - 1) / 2.0) + 0.5 * landings.IndexOf(l);
            if (score >= _bestScore) return;
            var (lo, hi) = DigVolume.Bounds(room);
            _bestScore = score;
            Best = new RoomPlan(room, door, l, d, lo, hi);
        }

        /// <summary>The room's cells at width offset <paramref name="o"/> and how many are already air; null when one is bad or
        /// (with a wall to keep) touches the structure.</summary>
        private HashSet<BlockPos>? Cells(BlockPos l, BlockPos d, BlockPos p, int o, out int air)
        {
            var room = new HashSet<BlockPos>();
            air = 0;
            for (int i = 0; i < depth; i++)
            for (int j = 0; j < width; j++)
            for (int k = 0; k < height; k++)
            {
                var c = l + Mul(d, gap + 1 + i) + Mul(p, o + j) + new BlockPos(0, k, 0);
                if (Bad(c) || gap > 0 && Touches(c)) return null;
                if (!BlockShapes.HasCollision(w, c.X, c.Y, c.Z)) air++;
                room.Add(c);
            }
            return room;
        }

        private bool Touches(BlockPos c) => BlockFaceExtensions.All.Any(f => s.Contains(c.Offset(f)));
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
        var d = dir ?? away ?? Dirs[0];
        var keepSolid = new HashSet<BlockPos>();
        foreach (var o in w.KeepAir.Owners)
            if (!string.Equals(o, owner, StringComparison.OrdinalIgnoreCase)) keepSolid.UnionWith(DigPattern.Floors(w.KeepAir.Cells(o)));
        bool Bad(BlockPos c) => room.Contains(c) || keepSolid.Contains(c) || !w.IsLoaded(c.X, c.Z)
                                || w.KeepAir.Owner(c, w.Dimension) is { } o && !string.Equals(o, owner, StringComparison.OrdinalIgnoreCase)
                                || w.GetBlock(c) is var id && id != 0 && (Blocks.Get(id).Hardness < 0 || Blocks.Get(id).Interactable);
        bool Blocked(BlockPos c) => Enumerable.Range(0, height).Any(k => Bad(c.Up(k)));
        var mouth = Mouth(lo, hi, d);
        var cells = new Dictionary<BlockPos, int>();
        int main = Corridor(cells, mouth, d, length, height, 0, Blocked);
        if (main == 0) return (null, $"the {DirName(d)} wall of the room is against another dig");
        var across = new BlockPos(d.Z, 0, d.X);
        int branches = 0;
        for (int i = spacing; i < main; i += spacing + 1)
            foreach (var side in new[] { across, Mul(across, -1) })
                if (Corridor(cells, mouth + Mul(d, i) + side, side, branch, height, 1, Blocked) > 0) branches++;
        return (new StripPlan(cells, mouth, d, branches, main), null);
    }

    /// <summary>The cell just outside the room's wall on side <paramref name="d"/>, in the middle of that wall, at floor level.</summary>
    private static BlockPos Mouth(BlockPos lo, BlockPos hi, BlockPos d)
    {
        int cx = (lo.X + hi.X) / 2, cz = (lo.Z + hi.Z) / 2;
        return d.X switch
        {
            1 => new BlockPos(hi.X + 1, lo.Y, cz), -1 => new BlockPos(lo.X - 1, lo.Y, cz),
            _ => d.Z == 1 ? new BlockPos(cx, lo.Y, hi.Z + 1) : new BlockPos(cx, lo.Y, lo.Z - 1),
        };
    }

    /// <summary>Up to <paramref name="count"/> columns <paramref name="height"/> high from <paramref name="start"/> along
    /// <paramref name="step"/>, ending early at a blocked one; their cells added with <paramref name="phase"/> (a cell already in
    /// keeps its phase: branches stay apart from the main corridor). The columns dug.</summary>
    private static int Corridor(Dictionary<BlockPos, int> cells, BlockPos start, BlockPos step, int count, int height, int phase, Func<BlockPos, bool> blocked)
    {
        int n = 0;
        for (int j = 0; j < count; j++)
        {
            var c = start + Mul(step, j);
            if (blocked(c)) break;
            for (int k = 0; k < height; k++) cells.TryAdd(c.Up(k), phase);
            n++;
        }
        return n;
    }
}
