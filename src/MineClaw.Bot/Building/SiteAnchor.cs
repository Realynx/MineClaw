using System.Text.RegularExpressions;
using MineClaw.Bot.Blueprints;
using MineClaw.World;

namespace MineClaw.Bot.Building;

/// <summary>A floor/structure the build snaps to: its block extent (inclusive) and how to say it ("the 8×8 floor at the end of the bridge").</summary>
public sealed record SiteExtent(BlockPos Min, BlockPos Max, string Name)
{
    public int W => Max.X - Min.X + 1;
    public int D => Max.Z - Min.Z + 1;
}

/// <summary>
/// Where a design goes, from what the player referenced (live 10-28: Fox meant "on the 8×8 drop floor at the end of the 2×2
/// bridge branch 67 just built", where they both stood; the design went 21 blocks south of the mob chamber). Never the bot's
/// own position or a nearby landmark by default:
/// <list type="bullet">
/// <item>"here" / "where we are" / "where I'm standing" / no place at all: the requester's feet (and facing: an inferred
/// direction, so the confirmation gate always applies).</item>
/// <item>"on the 8x8 floor", "the platform", "the end of the bridge", "that build": the most recent self-placed build near the
/// requester (the hive's placed-block log), its floor found by morphological opening (a 2-wide bridge drops out, the 8×8 floor
/// stays); the footprint snaps to that extent.</item>
/// <item>"on top of / around / on the X": a remembered structure's extent.</item>
/// <item>A punched/pointed block: that corner.</item>
/// </list>
/// </summary>
public static partial class SiteAnchor
{
    [GeneratedRegex(@"^\W*(?:here|right here|over here|where (?:we|i|you) (?:are|am|stand|'re|'m)(?: standing)?|where i'?m standing|by me|next to me|on me|me|my spot)\W*$", RegexOptions.IgnoreCase)]
    private static partial Regex HereRx();

    [GeneratedRegex(@"\b(?:floor|platform|deck|pad|(?:end|tip) of (?:the |that )?(?:bridge|walkway|branch)|that build|the build|what (?:67|69|420|1337|\w+) (?:just )?built|(?:new|recent|last) (?:build|floor|platform))\b", RegexOptions.IgnoreCase)]
    private static partial Regex RecentRx();

    [GeneratedRegex(@"^\W*(?:on top of|on|around|over|atop|above|in|inside)\s+(?:the\s+)?(?<x>.+?)\W*$", RegexOptions.IgnoreCase)]
    private static partial Regex OnRx();

    public static bool IsHere(string? at) => at != null && HereRx().IsMatch(at);
    public static bool IsRecentBuild(string? at) => at != null && RecentRx().IsMatch(at);

    /// <summary>"on top of the main house" → "main house"; null when the text isn't such a relation.</summary>
    public static string? OnWhat(string? at) => at != null && OnRx().Match(at) is { Success: true } m ? m.Groups["x"].Value.Trim() : null;

    /// <summary>
    /// The newest self-placed build near <paramref name="near"/> (within <paramref name="radius"/>, placed within
    /// <paramref name="maxAge"/>): connected placed blocks; its floor = the most common layer, opened with a 3×3 square so thin
    /// arms (bridges, walkways) drop out; the extent is the largest remaining piece (the whole layer when nothing remains).
    /// </summary>
    public static SiteExtent? RecentBuild(IReadOnlyList<PlacedBlock> placed, int dim, BlockPos near, DateTimeOffset now, TimeSpan maxAge, int radius = 16)
    {
        var cells = placed.Where(p => p.Dimension == dim && now - p.At <= maxAge && Math.Abs(p.Pos.X - near.X) <= radius
                                      && Math.Abs(p.Pos.Z - near.Z) <= radius && Math.Abs(p.Pos.Y - near.Y) <= 8).ToList();
        if (cells.Count < 4) return null;
        var set = cells.Select(c => c.Pos).ToHashSet();
        var comps = Components(set);
        // The build the requester stands on/next to first, then the newest, then the nearest.
        var newest = cells.ToDictionary(c => c.Pos, c => c.At);
        var pick = comps.Where(c => c.Count >= 4)
            .OrderBy(c => c.Any(p => Math.Abs(p.X - near.X) <= 1 && Math.Abs(p.Z - near.Z) <= 1 && p.Y <= near.Y && p.Y >= near.Y - 2) ? 0 : 1)
            .ThenByDescending(c => c.Max(p => newest[p]))
            .ThenBy(c => c.Min(p => p.DistanceSquaredTo(near)))
            .FirstOrDefault();
        if (pick == null) return null;
        int floorY = pick.GroupBy(p => p.Y).OrderByDescending(g => g.Count()).ThenByDescending(g => g.Key).First().Key;
        var layer = pick.Where(p => p.Y == floorY).Select(p => (p.X, p.Z)).ToHashSet();
        // Opening: cells whose 3×3 neighbourhood is all floor, grown back by one.
        var core = layer.Where(c => Square(c).All(layer.Contains)).ToHashSet();
        var opened = core.SelectMany(Square).Where(layer.Contains).ToHashSet();
        bool hadArm = opened.Count > 0 && opened.Count < layer.Count;
        var piece = opened.Count > 0 ? Largest(opened) : layer;
        int x0 = piece.Min(c => c.X), x1 = piece.Max(c => c.X), z0 = piece.Min(c => c.Z), z1 = piece.Max(c => c.Z);
        string name = $"{x1 - x0 + 1}×{z1 - z0 + 1} floor" + (hadArm ? " at the end of the bridge" : "");
        return new SiteExtent(new BlockPos(x0, floorY, z0), new BlockPos(x1, floorY, z1), name);
    }

    private static IEnumerable<(int X, int Z)> Square((int X, int Z) c)
    {
        for (int dx = -1; dx <= 1; dx++) for (int dz = -1; dz <= 1; dz++) yield return (c.X + dx, c.Z + dz);
    }

    private static HashSet<(int X, int Z)> Largest(HashSet<(int X, int Z)> cells)
    {
        HashSet<(int, int)> best = new();
        var left = new HashSet<(int X, int Z)>(cells);
        while (left.Count > 0)
        {
            var seed = left.First();
            var comp = new HashSet<(int, int)> { seed };
            var q = new Queue<(int X, int Z)>([seed]);
            left.Remove(seed);
            while (q.Count > 0)
            {
                var (x, z) = q.Dequeue();
                foreach (var n in new[] { (x + 1, z), (x - 1, z), (x, z + 1), (x, z - 1) })
                    if (left.Remove(n)) { comp.Add(n); q.Enqueue(n); }
            }
            if (comp.Count > best.Count) best = comp;
        }
        return best;
    }

    private static List<List<BlockPos>> Components(HashSet<BlockPos> set)
    {
        var left = new HashSet<BlockPos>(set);
        var list = new List<List<BlockPos>>();
        while (left.Count > 0)
        {
            var seed = left.First();
            left.Remove(seed);
            var comp = new List<BlockPos> { seed };
            var q = new Queue<BlockPos>([seed]);
            while (q.Count > 0)
            {
                var p = q.Dequeue();
                for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                for (int dz = -1; dz <= 1; dz++)
                {
                    var n = p.Offset(dx, dy, dz);
                    if (left.Remove(n)) { comp.Add(n); q.Enqueue(n); }
                }
            }
            list.Add(comp);
        }
        return list;
    }

    /// <summary>The ground cell to anchor on: the extent's centre top (the plan snaps to the extent itself).</summary>
    public static BlockPos Ground(SiteExtent e) => new((e.Min.X + e.Max.X) / 2, e.Max.Y, (e.Min.Z + e.Max.Z) / 2);

    /// <summary>"16×16 on the 8×8 floor": a footprint that doesn't match the extent it should sit on, or null.</summary>
    public static string? Mismatch(int fx, int fz, SiteExtent e) =>
        fx == e.W && fz == e.D || fx == e.D && fz == e.W ? null : $"it's {fx}×{fz} but the {e.Name} is {e.W}×{e.D}";

    /// <summary>The direction a yaw faces (b1.8.1: 0 south, 90 west).</summary>
    public static Dir Facing(float yaw) => BuildPlanner.Quarter(yaw);
}
