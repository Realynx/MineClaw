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
            if ((uint)n.Y >= WorldState.Height) continue;
            int id = world.GetBlock(n);
            if (id != 0 && Blocks.Get(id) is { Solid: true } && id != Blocks.Leaves && id != Blocks.Glass) continue; // opaque: holds no light
            best = Math.Max(best, Light(n.X, n.Y, n.Z) - 1);
        }
        return best;
    }

    public const int MaxLogs = 96;
    /// <summary>Vanilla leaf decay range: a leaf lives while a log is within 4 steps through leaves/logs.</summary>
    public const int LeafRange = 4;
    private const int MaxSpread = 8;

    public static string WoodName(int meta) => (meta & 3) switch { 1 => "spruce", 2 => "birch", _ => "oak" };

    /// <summary>"oak" / "birch" / "spruce" (also "pine", "fir") → wood metadata, else null.</summary>
    public static int? ParseWood(string? s) => s?.Trim().ToLowerInvariant() switch
    {
        "oak" or "oak tree" => 0,
        "spruce" or "pine" or "fir" or "spruce tree" or "pine tree" => 1,
        "birch" or "birch tree" => 2,
        _ => null,
    };

    private static readonly BlockPos[] Six =
        [new(1, 0, 0), new(-1, 0, 0), new(0, 1, 0), new(0, -1, 0), new(0, 0, 1), new(0, 0, -1)];

    /// <summary>The tree that <paramref name="log"/> belongs to, or null when it isn't a log.</summary>
    public static TreeInfo? Analyze(IBlockAccess w, BlockPos log)
    {
        if (w.GetBlock(log.X, log.Y, log.Z) != Blocks.Log) return null;
        int wood = w.GetMeta(log.X, log.Y, log.Z) & 3;
        var cluster = LogCluster(w, log, wood);

        // Several trunks can touch (crowns merged): keep the logs closest to the seed's trunk.
        var bases = cluster.Where(p => w.GetBlock(p.X, p.Y - 1, p.Z) != Blocks.Log).ToList();
        var natural = bases.Where(b => w.GetBlock(b.X, b.Y - 1, b.Z) is Blocks.Dirt or Blocks.Grass).ToList();
        var candidates = natural.Count > 0 ? natural : bases;
        BlockPos mine = candidates.OrderBy(b => b.X == log.X && b.Z == log.Z && b.Y <= log.Y ? 0 : 1)
            .ThenBy(b => H2(b, log)).ThenBy(b => b.Y).First();
        var logs = new HashSet<BlockPos>();
        if (candidates.Count == 1) logs.UnionWith(cluster);
        else
            foreach (var p in cluster)
            {
                var owner = candidates.OrderBy(b => H2(b, p)).ThenBy(b => Math.Abs(b.Y - p.Y)).First();
                if (owner == mine) logs.Add(p);
            }

        var leaves = OwnLeaves(w, logs);
        var soil = mine.Down();
        return new TreeInfo(mine, soil, w.GetBlock(soil.X, soil.Y, soil.Z), wood, logs, leaves);
    }

    /// <summary>Trees with a log within <paramref name="radius"/> of <paramref name="center"/>, nearest base first.</summary>
    public static List<TreeInfo> FindTrees(WorldState w, Vec3d center, double radius, int? wood = null)
    {
        var found = w.FindBlocks(center, radius, (id, meta) => id == Blocks.Log && (wood is null || (meta & 3) == wood), 1024);
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
        for (int dx = -range; dx <= range; dx++)
        for (int dy = -range; dy <= range + 3; dy++)
        for (int dz = -range; dz <= range; dz++)
        {
            var q = p.Offset(dx, dy, dz);
            if (q.Y is < 0 or >= WorldState.Height || w.GetBlock(q.X, q.Y, q.Z) != Blocks.Log) continue;
            double d = dx * dx + dz * dz + 0.5 * dy * dy;
            if (d < bestD) { bestD = d; best = q; }
        }
        return best;
    }

    private static int H2(BlockPos a, BlockPos b) => (a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z);

    private static HashSet<BlockPos> LogCluster(IBlockAccess w, BlockPos seed, int wood)
    {
        var set = new HashSet<BlockPos> { seed };
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
                if (n.Y is < 0 or >= WorldState.Height || Math.Abs(n.X - seed.X) > MaxSpread || Math.Abs(n.Z - seed.Z) > MaxSpread) continue;
                if (set.Contains(n) || w.GetBlock(n.X, n.Y, n.Z) != Blocks.Log || (w.GetMeta(n.X, n.Y, n.Z) & 3) != wood) continue;
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
        for (int x = x0 - LeafRange; x <= x1 + LeafRange; x++)
        for (int y = Math.Max(0, y0 - LeafRange); y <= Math.Min(WorldState.Height - 1, y1 + LeafRange); y++)
        for (int z = z0 - LeafRange; z <= z1 + LeafRange; z++)
        {
            var p = new BlockPos(x, y, z);
            if (!logs.Contains(p) && w.GetBlock(x, y, z) == Blocks.Log) others.Add(p);
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
        while (q.Count > 0)
        {
            var (p, d) = q.Dequeue();
            if (d >= LeafRange) continue;
            foreach (var o in Six)
            {
                var n = p + o;
                if (n.Y is < 0 or >= WorldState.Height || !visited.Add(n) || w.GetBlock(n.X, n.Y, n.Z) != Blocks.Leaves) continue;
                dist[n] = d + 1;
                q.Enqueue((n, d + 1));
            }
        }
        return dist;
    }
}
