using System.Text.Json.Nodes;
using MineClaw.Data.Versioning;
using MineClaw.World;

namespace MineClaw.Scripting.Story;

/// <summary>
/// Where to nerd-pole from to reach a block above (the <c>story.dig</c> SDK primitive's eyes; docs/web/story-mode.md "Reaching high
/// blocks"). Fox 11-04: "With the blocks-out-of-reach prompt for mining, cutting or harvesting, it should nerd-pole up to reach those
/// blocks on its own and destroy the nerd pole on the way back down." A column right under the target (a ceiling) or beside it, with
/// ground under it (never a liquid), free all the way up (no lava beside it, nothing falling into a column under the target), and the
/// fewest blocks to stack until the target's centre is within reach of the eyes. <c>within</c>: no pillar needed;
/// <c>stand</c>/<c>steps</c>: walk there and pillar that many; <c>block</c>/<c>blocks</c>: what it pillars with and how many it
/// carries (dirt, cobblestone, netherrack, planks, stone, sandstone: never anything worth keeping); <c>gather</c>: dirt around the
/// stand to dig for blocks; <c>why</c>: no column will do.
/// </summary>
public static class ReachScan
{
    /// <summary>The highest pillar a pawn builds.</summary>
    public const int MaxPillar = 24;
    /// <summary>Reach for planning (survival 4.5, less a margin for the face's offset and the aim).</summary>
    public const double Reach = 4.0;

    private static readonly int[] PillarIds = [3, 4, 87, 1, 24, 5]; // planks last: a build's material

    private const string LiquidGround = "only liquid to stand on under it";

    private readonly record struct Column(BlockPos Stand, int Steps, double Score);

    /// <param name="walkable">Check access from the pawn before choosing a stand; false is a geometry-only scan.</param>
    public static JsonObject Scan(WorldState w, IGameData data, Vec3d pos, BlockPos target, bool walkable = false)
    {
        var g = new ScanGround(w, data);
        var center = new Vec3d(target.X + 0.5, target.Y + 0.5, target.Z + 0.5);
        var o = new JsonObject { ["within"] = Eye(pos.X, pos.Y, pos.Z, center) <= Reach };
        var (block, blocks) = g.Stock(PillarIds);
        o["block"] = block;
        o["blocks"] = blocks;
        if (o["within"]!.GetValue<bool>()) return o;
        if (Best(g, target, center, walkable ? pos : null, out var why) is not { } pick)
        {
            o["why"] = why;
            return o;
        }
        o["stand"] = ScanGround.Fmt(pick.Stand);
        o["steps"] = pick.Steps;
        o["gather"] = Gather(g, pos, pick.Stand);
        return o;
    }

    private static double Eye(double x, double feetY, double z, Vec3d center) => new Vec3d(x, feetY + 1.62, z).DistanceTo(center);

    /// <summary>The cheapest column, under the target first then rings 1 and 2 around it; else why none will do (the last reason seen).</summary>
    private static Column? Best(ScanGround g, BlockPos target, Vec3d center, Vec3d? from, out string why)
    {
        var columns = new List<Column>();
        why = "no ground to stand on below or beside it within " + MaxPillar + " blocks";
        for (int r = 0; r <= 2; r++)
            for (int dx = -r; dx <= r; dx++)
                for (int dz = -r; dz <= r; dz++)
                {
                    if (Math.Max(Math.Abs(dx), Math.Abs(dz)) != r) continue;
                    if (TryColumn(g, target, center, r, target.X + dx, target.Z + dz, ref why) is { } col) columns.Add(col);
                }
        if (columns.Count == 0) return null;
        var best = columns.MinBy(c => c.Score);
        if (from is not { } pos || Pathfinder.StartCellFor(g.World, pos) is not { } start) return best;
        // A cheap column can be an isolated pocket in the quarry wall. Probe the preferred stand,
        // then search all alternatives together: one bounded search, not 25 failed walks.
        var options = new PathOptions { AllowPartial = false, AllowOpenDoors = true, AllowParkour = false,
            MaxNodes = 4000, Timeout = TimeSpan.FromMilliseconds(150) };
        if (Pathfinder.FindPath(g.World, start, new GoalBlock(best.Stand), options).Status == PathStatus.Found) return best;
        var goals = new GoalAny(columns.Select(c => (Goal)new GoalBlock(c.Stand)).ToArray());
        var path = Pathfinder.FindPath(g.World, start, goals, options with { MaxNodes = 8000, Timeout = TimeSpan.FromMilliseconds(350) });
        if (path.Status == PathStatus.Found)
        {
            var end = path.Steps.Count == 0 ? start : path.Steps[^1].Pos;
            return columns.First(c => c.Stand == end);
        }
        why = "no walkable ground below or beside it";
        return null;
    }

    /// <summary>The column at cx,cz: its ground, how many blocks up until the target is in reach, and a free shaft to get there.</summary>
    private static Column? TryColumn(ScanGround g, BlockPos target, Vec3d center, int r, int cx, int cz, ref string why)
    {
        if (!g.World.IsLoaded(cx, cz)) return null;
        if (r == 0 && ScanGround.Falling.Contains(g.World.GetBlock(target.X, target.Y + 1, target.Z))) { why = "sand or gravel over it would fall on me"; return null; }
        int gy = GroundUnder(g, cx, cz, target.Y, ref why);
        if (gy < 0) return null;
        int feet = gy;
        while (feet - gy <= MaxPillar && Eye(cx + 0.5, feet, cz + 0.5, center) > Reach) feet++;
        if (feet - gy > MaxPillar) { why = "more than " + MaxPillar + " blocks up"; return null; }
        if (feet + 1 >= g.Height - 1) { why = "above the build height"; return null; }
        if (feet > gy && OnALid(g, cx, gy - 1, cz, ref why)) return null; // a pillar never stands on a chest's lid
        if (!ClearShaft(g, cx, cz, gy, feet)) { why = "no free column to pillar up beside it"; return null; }
        return new Column(new BlockPos(cx, gy, cz), feet - gy, (feet - gy) + 0.3 * r);
    }

    /// <summary>Ground: the first standable cell going down from just below the target's height (-1: none, or only liquid).</summary>
    private static int GroundUnder(ScanGround g, int cx, int cz, int targetY, ref string why)
    {
        for (int y = targetY - 1; y >= Math.Max(1, targetY - MaxPillar - 4); y--)
        {
            if (!g.Free(cx, y, cz))
            {
                if (g.Liquid(cx, y, cz)) why = LiquidGround;
                return -1;
            }
            if (g.Solid(cx, y - 1, cz)) return g.Free(cx, y + 1, cz) ? y : -1;
            if (g.Liquid(cx, y - 1, cz)) { why = LiquidGround; return -1; }
        }
        return -1;
    }

    /// <summary>Containers and workstations a pillar never stands on (a chest under a block won't open).</summary>
    private static readonly HashSet<int> LidIds = [54, 146, 130, 61, 62, 58, 23, 158, 154, 117, 116, 145];

    private static bool OnALid(ScanGround g, int x, int y, int z, ref string why)
    {
        if (!LidIds.Contains(g.World.GetBlock(x, y, z))) return false;
        why = "a chest or workstation under the column";
        return true;
    }

    /// <summary>Free from the ground to a head over the pillar's top, with no lava beside it.</summary>
    private static bool ClearShaft(ScanGround g, int cx, int cz, int gy, int feet)
    {
        for (int y = gy; y <= feet + 1; y++)
            if (!g.Free(cx, y, cz) || LavaBeside(g, cx, y, cz)) return false;
        return true;
    }

    private static bool LavaBeside(ScanGround g, int x, int y, int z) =>
        g.Lava(x + 1, y, z) || g.Lava(x - 1, y, z) || g.Lava(x, y, z + 1) || g.Lava(x, y, z - 1);

    /// <summary>Dirt around the stand to dig for blocks when it carries too few (the surface it walks on; never the stand's own floor).</summary>
    private static JsonArray Gather(ScanGround g, Vec3d pos, BlockPos stand)
    {
        var gather = new JsonArray();
        var floor = new BlockPos((int)Math.Floor(pos.X), (int)Math.Floor(pos.Y + 1e-6) - 1, (int)Math.Floor(pos.Z));
        for (int dx = -3; dx <= 3 && gather.Count < 32; dx++)
            for (int dz = -3; dz <= 3 && gather.Count < 32; dz++)
            {
                var c = new BlockPos(stand.X + dx, stand.Y - 1, stand.Z + dz);
                if (dx == 0 && dz == 0 || c == floor) continue; // never the ground under its own feet
                if (Diggable(g, c)) gather.Add(ScanGround.Fmt(c));
            }
        return gather;
    }

    /// <summary>Grass or dirt with no liquid beside or over it.</summary>
    private static bool Diggable(ScanGround g, BlockPos c) => g.World.GetBlock(c.X, c.Y, c.Z) is 2 or 3 && !g.LiquidAround(c.X, c.Y, c.Z);
}
