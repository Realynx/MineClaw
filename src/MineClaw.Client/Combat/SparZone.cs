using MineClaw.World;

namespace MineClaw.Client.Combat;

/// <summary>Result of <see cref="SparZone.Find"/>: the zone, or the honest reason there is none.</summary>
public readonly record struct SparZoneResult(SparZone? Zone, string? Reason);

/// <summary>
/// Adaptive sparring area computed from the island as it is (no designated pad): a connected set of standable cells
/// (neighbours may differ by <see cref="CombatOptions.ArenaMaxStep"/> blocks) that are all at least
/// <see cref="CombatOptions.MinEdgeDistance"/> from the nearest void/deadly column (<see cref="Safety.EdgeDistance"/>
/// semantics: rim cell = 1), with no lava/fire/cactus within <see cref="CombatOptions.ArenaHazardRadius"/>, and at
/// least <see cref="CombatOptions.ArenaMinCells"/> cells. <see cref="Center"/> is the cell with the largest edge distance
/// (nearest the players on ties).
/// </summary>
public sealed class SparZone
{
    private readonly Dictionary<(int X, int Z), int> _cells;
    private readonly Dictionary<(int X, int Z), int> _edge;

    private SparZone(Dictionary<(int X, int Z), int> cells, Dictionary<(int X, int Z), int> edge, BlockPos center)
    {
        _cells = cells;
        _edge = edge;
        Center = center;
    }

    public BlockPos Center { get; }
    public int Count => _cells.Count;
    /// <summary>Edge distance of the center cell (capped at the search depth, 8).</summary>
    public int CenterEdge => _edge[(Center.X, Center.Z)];
    public IEnumerable<BlockPos> Cells => _cells.Select(kv => new BlockPos(kv.Key.X, kv.Value, kv.Key.Z));

    /// <summary>The feet are on a zone cell (column in the zone, within one block of its floor level).</summary>
    public bool Contains(Vec3d feet)
    {
        int x = (int)Math.Floor(feet.X), z = (int)Math.Floor(feet.Z), y = (int)Math.Floor(feet.Y + 1e-6);
        return _cells.TryGetValue((x, z), out int cy) && Math.Abs(cy - y) <= 1;
    }

    public bool Contains(BlockPos c) => _cells.TryGetValue((c.X, c.Z), out int cy) && Math.Abs(cy - c.Y) <= 1;

    /// <summary>Horizontal distance from the feet to the nearest zone cell (0 inside); vertical offsets over 1 count too.</summary>
    public double OutsideDistance(Vec3d feet)
    {
        if (Contains(feet)) return 0;
        double best = double.MaxValue;
        int fy = (int)Math.Floor(feet.Y + 1e-6);
        foreach (var ((x, z), y) in _cells)
        {
            double dx = Math.Max(0, Math.Abs(feet.X - (x + 0.5)) - 0.5), dz = Math.Max(0, Math.Abs(feet.Z - (z + 0.5)) - 0.5);
            double d = Math.Max(dx, dz) + Math.Max(0, Math.Abs(fy - y) - 1);
            if (d < best) best = d;
        }
        return best;
    }

    /// <summary>Edge distance (rim = 1) of the column under the feet at feet level.</summary>
    public static int EdgeAt(IBlockAccess w, Vec3d feet, int max = 4) =>
        Safety.EdgeDistance(w, (int)Math.Floor(feet.X), (int)Math.Floor(feet.Y + 1e-6), (int)Math.Floor(feet.Z), max);

    // ------------------------------------------------------------------ search

    /// <summary>
    /// Finds the best zone within <see cref="CombatOptions.ArenaSearchRadius"/> of <paramref name="near"/> (columns, ±4
    /// levels). <paramref name="players"/> (the bot and the opponent) break ties toward the people.
    /// </summary>
    public static SparZoneResult Find(IBlockAccess w, Vec3d near, CombatOptions o, IReadOnlyList<Vec3d>? players = null)
    {
        var origin = near.ToBlockPos() with { Y = (int)Math.Floor(near.Y + 1e-6) };
        int minEdge = Math.Max(1, o.MinEdgeDistance);
        players ??= [near];

        // 1. Surface: the standable cell in each column closest to the reference level.
        var floor = SurfaceFloor(w, origin, o.ArenaSearchRadius);
        if (floor.Count == 0) return new(null, "no standable ground here");

        // 2. Edge distance + hazards per surface cell (deadly-column lookups cached).
        var search = new ArenaSearch(w, o);
        var (edge, good, bestEdgeAny, hazards) = search.Rate(floor, minEdge);
        if (good.Count == 0)
            return new(null, hazards
                ? $"no spot {minEdge}+ blocks from the void and {o.ArenaHazardRadius + 1}+ from lava/fire/cactus"
                : $"island too small: the best spot is {bestEdgeAny} block(s) from the void, need {minEdge}");

        // 3. Connected components (4-neighbour, step ≤ ArenaMaxStep with head room for the step).
        return search.BestZone(good, edge, players, minEdge);
    }

    private static Dictionary<(int, int), int> SurfaceFloor(IBlockAccess w, BlockPos origin, int r)
    {
        var floor = new Dictionary<(int, int), int>();
        for (int x = origin.X - r; x <= origin.X + r; x++)
        for (int z = origin.Z - r; z <= origin.Z + r; z++)
        {
            if (!w.IsLoaded(x, z)) continue;
            for (int d = 0; d <= 4; d++)
            {
                if (Safety.IsStandable(w, x, origin.Y + d, z)) { floor[(x, z)] = origin.Y + d; break; }
                if (d > 0 && Safety.IsStandable(w, x, origin.Y - d, z)) { floor[(x, z)] = origin.Y - d; break; }
            }
        }
        return floor;
    }

    /// <summary>One <see cref="Find"/>: the world and options, and the deadly-column lookups cached across cells.</summary>
    private sealed class ArenaSearch(IBlockAccess w, CombatOptions o)
    {
        private const int EdgeDepth = 8;
        private static readonly (int Dx, int Dz)[] Neighbours = [(1, 0), (-1, 0), (0, 1), (0, -1)];
        private readonly Dictionary<(int, int, int), bool> _deadly = new();

        private bool Deadly(int x, int y, int z)
        {
            if (!_deadly.TryGetValue((x, y, z), out bool v)) _deadly[(x, y, z)] = v = Safety.IsDeadlyColumn(w, x, y, z, 3);
            return v;
        }

        private int Edge(int x, int y, int z)
        {
            for (int k = 1; k <= EdgeDepth; k++)
            for (int dx = -k; dx <= k; dx++)
            for (int dz = -k; dz <= k; dz++)
                if (Math.Max(Math.Abs(dx), Math.Abs(dz)) == k && Deadly(x + dx, y, z + dz)) return k;
            return EdgeDepth + 1;
        }

        private bool Hazard(int x, int y, int z)
        {
            int h = o.ArenaHazardRadius;
            for (int dx = -h; dx <= h; dx++)
            for (int dz = -h; dz <= h; dz++)
            for (int dy = -1; dy <= 2; dy++)
                if (Safety.IsHazard(w, w.GetBlock(x + dx, y + dy, z + dz))) return true;
            return false;
        }

        /// <summary>Each surface cell's edge distance (lava pools count as drops too); the good ones: far enough from the edge, no
        /// hazard near.</summary>
        public (Dictionary<(int X, int Z), int> Edge, Dictionary<(int X, int Z), int> Good, int BestEdgeAny, bool Hazards) Rate(
            Dictionary<(int, int), int> floor, int minEdge)
        {
            var edge = new Dictionary<(int X, int Z), int>();
            var good = new Dictionary<(int X, int Z), int>();
            int bestEdgeAny = 0;
            bool hazards = false;
            foreach (var ((x, z), y) in floor)
            {
                int e = Edge(x, y, z);
                edge[(x, z)] = e;
                bestEdgeAny = Math.Max(bestEdgeAny, e);
                bool hazard = Hazard(x, y, z);
                hazards |= hazard;
                if (e >= minEdge && !hazard) good[(x, z)] = y;
            }
            return (edge, good, bestEdgeAny, hazards);
        }

        /// <summary>The best centre over every component big enough: deepest from the edge, then nearest the players.</summary>
        public SparZoneResult BestZone(Dictionary<(int X, int Z), int> good, Dictionary<(int X, int Z), int> edge, IReadOnlyList<Vec3d> players,
            int minEdge)
        {
            var seen = new HashSet<(int, int)>();
            SparZone? best = null;
            double bestScore = double.MinValue;
            int largest = 0;
            foreach (var start in good.Keys)
            {
                if (!seen.Add(start)) continue;
                var comp = Component(start, good, seen);
                largest = Math.Max(largest, comp.Count);
                if (comp.Count < o.ArenaMinCells) continue;
                foreach (var ((x, z), y) in comp)
                {
                    double toPlayers = players.Average(p => Math.Sqrt((p.X - x - 0.5) * (p.X - x - 0.5) + (p.Z - z - 0.5) * (p.Z - z - 0.5)));
                    double score = Math.Min(edge[(x, z)], EdgeDepth) * 4 - toPlayers;
                    if (score <= bestScore) continue;
                    bestScore = score;
                    best = new SparZone(comp, edge, new BlockPos(x, y, z));
                }
            }
            if (best is null)
                return new(null, $"only {largest} connected cell(s) {minEdge}+ blocks from the void, need {o.ArenaMinCells}");
            return new(best, null);
        }

        private Dictionary<(int X, int Z), int> Component((int X, int Z) start, Dictionary<(int X, int Z), int> good, HashSet<(int, int)> seen)
        {
            var comp = new Dictionary<(int X, int Z), int> { [start] = good[start] };
            var queue = new Queue<(int X, int Z)>();
            queue.Enqueue(start);
            while (queue.Count > 0)
            {
                var c = queue.Dequeue();
                int cy = good[c];
                foreach (var (dx, dz) in Neighbours)
                {
                    var n = (c.X + dx, c.Z + dz);
                    if (!good.TryGetValue(n, out int ny) || seen.Contains(n) || !CanStep(c, cy, n, ny)) continue;
                    seen.Add(n);
                    comp[n] = ny;
                    queue.Enqueue(n);
                }
            }
            return comp;
        }

        /// <summary>A step of at most ArenaMaxStep; the lower cell needs head room to jump up / drop down: clear at lower floor + 2.</summary>
        private bool CanStep((int X, int Z) c, int cy, (int X, int Z) n, int ny)
        {
            int step = ny - cy;
            if (Math.Abs(step) > o.ArenaMaxStep) return false;
            if (step == 0) return true;
            var (lx, lz, ly) = step > 0 ? (c.X, c.Z, cy) : (n.X, n.Z, ny);
            return !BlockShapes.HasCollision(w, lx, ly + 2, lz);
        }
    }

    // ------------------------------------------------------------------ positioning during the spar

    /// <summary>
    /// Where the bot should stand against an opponent at <paramref name="opp"/>: a zone cell within 4 blocks whose own
    /// knockback room (away from the opponent) is clear for <paramref name="depth"/> blocks — required — and, preferably,
    /// from which a hit sends the opponent inward too; then deeper in the interior, near hitting distance, short moves.
    /// Null when no cell keeps the bot's back safe.
    /// </summary>
    public BlockPos? Stance(IBlockAccess w, Vec3d me, Vec3d opp, double depth)
    {
        BlockPos? best = null;
        double bestScore = double.MinValue;
        foreach (var ((x, z), y) in _cells)
        {
            var c = new Vec3d(x + 0.5, y, z + 0.5);
            double move = Math.Sqrt((c.X - me.X) * (c.X - me.X) + (c.Z - me.Z) * (c.Z - me.Z));
            if (move > 4.5) continue;
            double toOpp = Math.Sqrt((c.X - opp.X) * (c.X - opp.X) + (c.Z - opp.Z) * (c.Z - opp.Z));
            if (toOpp < 1.2) continue;
            if (CombatGeometry.EdgeBehind(w, c, opp, depth)) continue;                 // my back must be safe
            if (y == (int)Math.Floor(me.Y + 1e-6) && !CombatGeometry.WalkSafe(w, me, new BlockPos(x, y, z), 2)) continue;
            bool oppSafe = !CombatGeometry.EdgeBehind(w, opp, c, depth);              // their knockback lands inward
            double score = (oppSafe ? 20 : 0) + Math.Min(_edge[(x, z)], 8) * 2 - Math.Abs(toOpp - 2.5) - move * 0.8;
            if (score > bestScore) { bestScore = score; best = new BlockPos(x, y, z); }
        }
        return best;
    }
}
