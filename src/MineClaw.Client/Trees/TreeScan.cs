using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Client.Trees;

/// <summary>
/// One tree: the connected log cluster of one wood type grown from a single trunk base, plus the leaves that belong to
/// it (closer, through leaves, to its logs than to any other tree's logs: those are the leaves that decay when it's cut).
/// </summary>
public sealed record TreeInfo(BlockPos Base, BlockPos Soil, int SoilId, int Wood, IReadOnlySet<BlockPos> Logs, IReadOnlySet<BlockPos> Leaves)
{
    public string WoodName => TreeScan.WoodName(Wood);
    /// <summary>Grows from dirt/grass (a log column on cobble or planks is someone's build).</summary>
    public bool OnSoil => SoilId is Blocks.Dirt or Blocks.Grass;
    public int Top => Logs.Count == 0 ? Base.Y : Logs.Max(l => l.Y);
    /// <summary>The trunk's base cells: one, or the four of a 2x2 trunk (dark oak, giant spruce/jungle).</summary>
    public IReadOnlyList<BlockPos> Trunk { get; init; } = [Base];
    public override string ToString() => $"{WoodName} tree at {Base.X},{Base.Y},{Base.Z} ({Logs.Count} logs, {Leaves.Count} leaves)";
}

/// <summary>Pure tree analysis over any <see cref="IBlockAccess"/> (no client needed: unit-testable and reusable by the index).</summary>
public static class TreeScan
{
    /// <summary>
    /// b1.8.1 <c>world.k</c> (what BlockFlower.canBlockStay reads): max(raw sky light, block light), with NO night
    /// subtraction, so night alone never makes a spot too dark. Block-change packets carry no light, so a cell that just
    /// stopped being opaque (the chopped trunk) still holds its old 0: the server relights it from its neighbours, so the
    /// estimate is max(stored, brightest non-opaque neighbour - 1) for both sky and block light.
    /// </summary>
    public static int SaplingLight(WorldState world, BlockPos cell)
    {
        int Light(int x, int y, int z) => Math.Max(world.GetSkyLight(x, y, z), world.GetBlockLight(x, y, z));
        int best = Light(cell.X, cell.Y, cell.Z);
        foreach (var f in BlockFaceExtensions.All)
        {
            var n = cell.Offset(f);
            if ((uint)n.Y >= world.WorldHeight) continue;
            int id = world.GetBlock(n);
            if (id != 0 && Blocks.Get(id) is { Solid: true } && !Wood.IsLeaves(id) && id != Blocks.Glass) continue; // opaque: holds no light
            best = Math.Max(best, Light(n.X, n.Y, n.Z) - 1);
        }
        return best;
    }

    public const int MaxLogs = 96;
    /// <summary>Vanilla leaf decay range: a leaf lives while a log is within 4 steps through leaves/logs.</summary>
    public const int LeafRange = 4;
    private const int MaxSpread = 8;

    public static string WoodName(int wood) => wood switch
    {
        Wood.Spruce => "spruce", Wood.Birch => "birch", Wood.Jungle => "jungle", Wood.Acacia => "acacia", Wood.DarkOak => "dark oak", _ => "oak",
    };

    /// <summary>"oak" / "birch" / "spruce" (also "pine", "fir") / "jungle" / "acacia" / "dark oak" → species (= sapling damage), else null.</summary>
    public static int? ParseWood(string? s) => s?.Trim().ToLowerInvariant() switch
    {
        "oak" or "oak tree" => 0,
        "spruce" or "pine" or "fir" or "spruce tree" or "pine tree" => 1,
        "birch" or "birch tree" => 2,
        "jungle" or "jungle tree" => 3,
        "acacia" or "acacia tree" => 4,
        "dark oak" or "darkoak" or "dark_oak" or "roofed oak" or "dark oak tree" => 5,
        _ => null,
    };

    private static readonly BlockPos[] Six =
        [new(1, 0, 0), new(-1, 0, 0), new(0, 1, 0), new(0, -1, 0), new(0, 0, 1), new(0, 0, -1)];

    /// <summary>The tree that <paramref name="log"/> belongs to, or null when it isn't a log.</summary>
    public static TreeInfo? Analyze(IBlockAccess w, BlockPos log)
    {
        int seedId = w.GetBlock(log.X, log.Y, log.Z);
        if (!Wood.IsLog(seedId) || SelfPlaced(w, log)) return null;
        int wood = Wood.Type(seedId, w.GetMeta(log.X, log.Y, log.Z));
        var cluster = LogCluster(w, log, wood);

        // Several trunks can touch (crowns merged): keep the logs closest to the seed's trunk.
        var bases = cluster.Where(p => !Wood.IsLog(w.GetBlock(p.X, p.Y - 1, p.Z))).ToList();
        var natural = bases.Where(b => w.GetBlock(b.X, b.Y - 1, b.Z) is Blocks.Dirt or Blocks.Grass).ToList();
        var candidates = natural.Count > 0 ? natural : bases;
        BlockPos mine = candidates.OrderBy(b => b.X == log.X && b.Z == log.Z && b.Y <= log.Y ? 0 : 1)
            .ThenBy(b => H2(b, log)).ThenBy(b => b.Y).First();
        // A 2x2 trunk (dark oak, giant spruce/jungle): four bases in a square are ONE tree, not four.
        var trunk = Square(candidates, mine);
        var logs = new HashSet<BlockPos>();
        if (candidates.Count == 1 || trunk.Count == candidates.Count) logs.UnionWith(cluster);
        else
            foreach (var p in cluster)
            {
                var owner = candidates.OrderBy(b => H2(b, p)).ThenBy(b => Math.Abs(b.Y - p.Y)).First();
                if (trunk.Contains(owner)) logs.Add(p);
            }

        DropBarePillars(w, logs, trunk);
        var leaves = OwnLeaves(w, logs);
        var soil = mine.Down();
        return new TreeInfo(mine, soil, w.GetBlock(soil.X, soil.Y, soil.Z), wood, logs, leaves) { Trunk = trunk.OrderBy(t => t.X).ThenBy(t => t.Z).ToList() };
    }

    /// <summary>Trees with a log within <paramref name="radius"/> of <paramref name="center"/>, nearest base first.</summary>
    public static List<TreeInfo> FindTrees(WorldState w, Vec3d center, double radius, int? wood = null)
    {
        var found = w.FindBlocks(center, radius, (id, meta) => Wood.IsLog(id) && (wood is null || Wood.Type(id, meta) == wood), 1024);
        var seen = new HashSet<BlockPos>();
        var trees = new List<TreeInfo>();
        foreach (var f in found.OrderBy(f => f.DistanceSquared))
        {
            if (seen.Contains(f.Pos)) continue;
            var t = Analyze(w, f.Pos);
            if (t is null) continue;
            seen.UnionWith(t.Logs);
            seen.Add(f.Pos);
            if (trees.Any(x => x.Base == t.Base)) continue;
            trees.Add(t);
        }
        return trees.OrderBy(t => t.Base.Center.DistanceSquaredTo(center)).ToList();
    }

    /// <summary>The log nearest to <paramref name="p"/> (Chebyshev ≤ <paramref name="range"/>; also looks up from a dirt/sapling spot).</summary>
    public static BlockPos? LogNear(IBlockAccess w, BlockPos p, int range = 3)
    {
        BlockPos? best = null;
        double bestD = double.MaxValue;
        int h = w.WorldHeight;
        for (int dx = -range; dx <= range; dx++)
        for (int dy = -range; dy <= range + 3; dy++)
        for (int dz = -range; dz <= range; dz++)
        {
            var q = p.Offset(dx, dy, dz);
            if ((uint)q.Y >= (uint)h || !Wood.IsLog(w.GetBlock(q.X, q.Y, q.Z))) continue;
            double d = dx * dx + dz * dz + 0.5 * dy * dy;
            if (d < bestD) { bestD = d; best = q; }
        }
        return best;
    }

    /// <summary>The 2x2 square of trunk bases (same y) that holds <paramref name="mine"/>, or just <paramref name="mine"/>.</summary>
    private static HashSet<BlockPos> Square(List<BlockPos> bases, BlockPos mine)
    {
        var set = bases.Where(b => b.Y == mine.Y).ToHashSet();
        foreach (int sx in new[] { -1, 1 })
        foreach (int sz in new[] { -1, 1 })
        {
            BlockPos a = mine.Offset(sx, 0, 0), c = mine.Offset(0, 0, sz), d = mine.Offset(sx, 0, sz);
            if (set.Contains(a) && set.Contains(c) && set.Contains(d)) return [mine, a, c, d];
        }
        return [mine];
    }

    private static int H2(BlockPos a, BlockPos b) => (a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z);

    /// <summary>
    /// The leaf block a dropped item is lying on (item gravity stops on leaves: they have a full collision box), or null
    /// when it rests on anything else / is still falling. A drop up in the crown is out of pickup range from the ground
    /// (the b1.8.1 pickup box only grows sideways) but it is NOT lost: break that leaf and it falls.
    /// </summary>
    public static BlockPos? SupportingLeaf(IBlockAccess w, Vec3d item)
    {
        int x = (int)Math.Floor(item.X), z = (int)Math.Floor(item.Z);
        // An item at rest sits ~0.1-0.125 above the top of the block under it; allow a little bounce either way.
        int y = (int)Math.Floor(item.Y - 0.3);
        if ((uint)y >= (uint)w.WorldHeight) return null;
        double above = item.Y - (y + 1);
        return above is >= -0.05 and <= 0.45 && Wood.IsLeaves(w.GetBlock(x, y, z)) ? new BlockPos(x, y, z) : null;
    }

    /// <summary>
    /// Of <paramref name="leaves"/>, the ones still kept alive by some log (vanilla BlockLeaves: a log within
    /// <see cref="LeafRange"/> steps through leaves). The rest decay by random ticks; these never will while that log stands
    /// (a neighbouring tree in a dense grove keeps a shared crown alive), so waiting for them is pointless.
    /// </summary>
    public static HashSet<BlockPos> SupportedLeaves(IBlockAccess w, IReadOnlyCollection<BlockPos> leaves)
    {
        var result = new HashSet<BlockPos>();
        if (leaves.Count == 0) return result;
        int x0 = leaves.Min(l => l.X) - LeafRange, x1 = leaves.Max(l => l.X) + LeafRange;
        int y0 = Math.Max(0, leaves.Min(l => l.Y) - LeafRange), y1 = Math.Min(w.WorldHeight - 1, leaves.Max(l => l.Y) + LeafRange);
        int z0 = leaves.Min(l => l.Z) - LeafRange, z1 = leaves.Max(l => l.Z) + LeafRange;
        var logs = new HashSet<BlockPos>();
        for (int x = x0; x <= x1; x++)
        for (int y = y0; y <= y1; y++)
        for (int z = z0; z <= z1; z++)
            if (Wood.IsLog(w.GetBlock(x, y, z))) logs.Add(new BlockPos(x, y, z));
        if (logs.Count == 0) return result;
        var reach = LeafDistances(w, logs);
        foreach (var l in leaves) if (reach.ContainsKey(l)) result.Add(l);
        return result;
    }

    /// <summary>
    /// For logs that weren't growing from dirt (a remnant left hanging after its lower trunk was cut): what is under them.
    /// Walks down through air/leaves from <paramref name="below"/>: a sapling already planted there (on dirt/grass), bare
    /// dirt/grass with room (the real replant spot is the cell above it), or something else.
    /// </summary>
    public static (BlockPos? Sapling, BlockPos? Dirt, BlockPos? Other) ColumnUnder(IBlockAccess w, BlockPos below, int maxDepth = 12)
    {
        for (int y = below.Y; y >= Math.Max(0, below.Y - maxDepth); y--)
        {
            int id = w.GetBlock(below.X, y, below.Z);
            if (id == 0 || Wood.IsLeaves(id)) continue;
            var p = new BlockPos(below.X, y, below.Z);
            if (id == Blocks.Sapling && w.GetBlock(p.X, p.Y - 1, p.Z) is Blocks.Dirt or Blocks.Grass) return (p, p.Down(), null);
            if (id is Blocks.Dirt or Blocks.Grass) return (null, p, null);
            return (null, null, p);
        }
        return (null, null, null);
    }

    /// <summary>
    /// Protection rule 2: a tree is logs with leaves on a dirt/grass base; a bare log pillar or log build is not. A log column
    /// next to the trunk (other x,z) that rests on a solid non-log block and touches no leaf is someone's pillar or build that
    /// merely touches the tree: it is not this tree's (never stewarded, never felled as part of it). Branches hang in the crown
    /// (on air/leaves, or touching leaves) and stay.
    /// </summary>
    private static void DropBarePillars(IBlockAccess w, HashSet<BlockPos> logs, HashSet<BlockPos> trunk)
    {
        foreach (var col in logs.Where(l => !trunk.Any(t => t.X == l.X && t.Z == l.Z)).GroupBy(l => (l.X, l.Z)).ToList())
        {
            var cells = col.ToList();
            var bottom = cells.MinBy(c => c.Y);
            int under = w.GetBlock(bottom.X, bottom.Y - 1, bottom.Z);
            bool grounded = under != 0 && !Wood.IsLog(under) && !Wood.IsLeaves(under) && BlockShapes.HasCollision(w, bottom.X, bottom.Y - 1, bottom.Z);
            if (!grounded) continue;
            bool leafy = cells.Any(c => Six.Any(o => Wood.IsLeaves(w.GetBlock(c.X + o.X, c.Y + o.Y, c.Z + o.Z))));
            if (!leafy) logs.ExceptWith(cells);
        }
    }

    /// <summary>
    /// A log column with no dirt under it that a recent hive chop felled next to (or left standing when it stopped): the bot's
    /// own unfinished tree, finishable without leaves or soil (<see cref="ChopLog"/>). Null = not ours (a build or a stranger's).
    /// </summary>
    public static ChopRecord? Unfinished(IBlockAccess w, TreeInfo t)
    {
        if (t.OnSoil || w is not WorldState ws) return null;
        foreach (var l in t.Logs)
            if (ws.Chops.Owner(l, ws.Dimension) is { } r) return r;
        return null;
    }

    /// <summary>A log a hive bot placed itself (its pillar/bridge): never part of a tree.</summary>
    private static bool SelfPlaced(IBlockAccess w, BlockPos p) => w is WorldState ws && ws.Placed.IsPlaced(p, ws.Dimension, w.GetBlock(p.X, p.Y, p.Z));

    private static HashSet<BlockPos> LogCluster(IBlockAccess w, BlockPos seed, int wood)
    {
        var set = new HashSet<BlockPos> { seed };
        int h = w.WorldHeight;
        var q = new Queue<BlockPos>();
        q.Enqueue(seed);
        while (q.Count > 0 && set.Count < MaxLogs)
        {
            var p = q.Dequeue();
            for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
            for (int dz = -1; dz <= 1; dz++)
            {
                if (dx == 0 && dy == 0 && dz == 0) continue;
                var n = p.Offset(dx, dy, dz);
                if ((uint)n.Y >= (uint)h || Math.Abs(n.X - seed.X) > MaxSpread || Math.Abs(n.Z - seed.Z) > MaxSpread) continue;
                int nid = w.GetBlock(n.X, n.Y, n.Z);
                if (set.Contains(n) || !Wood.IsLog(nid) || Wood.Type(nid, w.GetMeta(n.X, n.Y, n.Z)) != wood || SelfPlaced(w, n)) continue;
                set.Add(n);
                q.Enqueue(n);
            }
        }
        return set;
    }

    /// <summary>Leaves whose leaf-path distance to <paramref name="logs"/> is ≤ 4 and strictly less than to any other log.</summary>
    private static HashSet<BlockPos> OwnLeaves(IBlockAccess w, HashSet<BlockPos> logs)
    {
        var mine = LeafDistances(w, logs);
        if (mine.Count == 0) return [];
        int x0 = int.MaxValue, x1 = int.MinValue, y0 = int.MaxValue, y1 = int.MinValue, z0 = int.MaxValue, z1 = int.MinValue;
        foreach (var l in mine.Keys)
        {
            x0 = Math.Min(x0, l.X); x1 = Math.Max(x1, l.X); y0 = Math.Min(y0, l.Y); y1 = Math.Max(y1, l.Y); z0 = Math.Min(z0, l.Z); z1 = Math.Max(z1, l.Z);
        }
        var others = new HashSet<BlockPos>();
        int h = w.WorldHeight;
        for (int x = x0 - LeafRange; x <= x1 + LeafRange; x++)
        for (int y = Math.Max(0, y0 - LeafRange); y <= Math.Min(h - 1, y1 + LeafRange); y++)
        for (int z = z0 - LeafRange; z <= z1 + LeafRange; z++)
        {
            var p = new BlockPos(x, y, z);
            if (!logs.Contains(p) && Wood.IsLog(w.GetBlock(x, y, z))) others.Add(p);
        }
        var theirs = others.Count == 0 ? new Dictionary<BlockPos, int>() : LeafDistances(w, others);
        var result = new HashSet<BlockPos>();
        foreach (var (p, d) in mine)
            if (!theirs.TryGetValue(p, out var o) || d < o) result.Add(p);
        return result;
    }

    private static Dictionary<BlockPos, int> LeafDistances(IBlockAccess w, HashSet<BlockPos> sources)
    {
        var dist = new Dictionary<BlockPos, int>();
        var q = new Queue<(BlockPos P, int D)>();
        foreach (var s in sources) q.Enqueue((s, 0));
        var visited = new HashSet<BlockPos>(sources);
        int h = w.WorldHeight;
        while (q.Count > 0)
        {
            var (p, d) = q.Dequeue();
            if (d >= LeafRange) continue;
            foreach (var o in Six)
            {
                var n = p + o;
                if ((uint)n.Y >= (uint)h || !visited.Add(n) || !Wood.IsLeaves(w.GetBlock(n.X, n.Y, n.Z))) continue;
                dist[n] = d + 1;
                q.Enqueue((n, d + 1));
            }
        }
        return dist;
    }
}
