using System.Text.Json.Nodes;
using MineClaw.Data.Versioning;
using MineClaw.World;

namespace MineClaw.Scripting.Story;

/// <summary>
/// Where to nerd-pole from to reach a block above (the <c>story.dig</c> SDK primitive's eyes; docs/web/story-mode.md "Reaching high
/// blocks"). Fox 11-04: "With the blocks-out-of-reach prompt for mining, cutting or harvesting, it should nerd-pole up to reach those
/// blocks on its own and destroy the nerd pole on the way back down." A column right under the target (a ceiling) or beside it, with
/// ground under it (never a liquid), free all the way up (no lava beside it, nothing falling into a column under the target), and the
/// fewest blocks to stack until a face is visible within reach of the eyes. Existing walkable stands are preferred.
/// <c>within</c>: no pillar needed;
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

    private readonly record struct Column(BlockPos Stand, int Steps, double Score, int Coverage);

    /// <param name="walkable">Check access from the pawn before choosing a stand; false is a geometry-only scan.</param>
    public static JsonObject Scan(WorldState w, IGameData data, Vec3d pos, BlockPos target, bool walkable = false,
        IReadOnlyList<BlockPos>? targets = null, Func<Vec3d, BlockPos, bool>? canReach = null)
    {
        var g = new ScanGround(w, data);
        canReach ??= (feet, at) => Within(w, feet, at);
        var o = new JsonObject { ["within"] = canReach(pos, target) };
        var (block, blocks) = g.Stock(PillarIds);
        o["block"] = block;
        o["blocks"] = blocks;
        if (o["within"]!.GetValue<bool>()) return o;
        var cluster = targets == null ? [target] : targets.Prepend(target).Distinct().Take(24).ToArray();
        if (Best(g, target, cluster, walkable ? pos : null, canReach, out var why) is not { } pick)
        {
            o["why"] = why;
            return o;
        }
        o["stand"] = ScanGround.Fmt(pick.Stand);
        o["steps"] = pick.Steps;
        o["gather"] = Gather(g, pos, pick.Stand);
        return o;
    }

    /// <summary>Being close is not enough: a face must be visible, including from a proposed pillar's top.
    /// Empty cells (placement jobs) still use their centre, with a clear ray into the cell.</summary>
    public static bool Within(WorldState w, Vec3d feet, BlockPos target)
    {
        if (!w.IsLoaded(target.X, target.Z)) return false;
        var eye = feet + new Vec3d(0, PlayerPhysics.EyeHeight, 0);
        int id = w.GetBlock(target);
        if (id != 0 && !BlockShapes.IsLiquid(id)) return Aiming.FindDigAim(w, eye, target, Reach) != null;
        var center = new Vec3d(target.X + 0.5, target.Y + 0.5, target.Z + 0.5);
        double distance = eye.DistanceTo(center);
        return distance <= Reach && (Raycast.Cast(w, eye, center - eye, distance) is not { } hit || hit.Pos == target);
    }

    /// <summary>Construction needs a visible support face and, for a turned block, a click that gives its requested orientation.</summary>
    public static bool PlacementWithin(WorldState w, IGameData data, Vec3d feet, BlockPos target, int item, int? meta)
    {
        if (!w.IsLoaded(target.X, target.Z) || !Aiming.IsReplaceable(w, w.GetBlock(target))
            || AABB.ForPlayer(feet).Intersects(AABB.Block(target.X, target.Y, target.Z))) return false;
        var eye = feet + new Vec3d(0, PlayerPhysics.EyeHeight, 0);
        int block = BlockOrientation.BlockOf(item);
        bool modern = data.VersionId != "b1.8.1";
        if (meta is not { } turn || !BlockOrientation.Turns(block, turn, modern))
            return Aiming.FindPlaceAim(w, eye, target, Reach, AABB.ForPlayer(feet)) is not null;
        return BlockOrientation.Solve(block, turn, target, feet, modern,
                face => PlaceRules.IsSupport(w, target - face.Normal()), sneaking: true)
            .Any(t => Client.FaceAim.Find(w, eye, target - t.Face.Normal(), t.Face, Reach) is not null);
    }

    /// <summary>A visible existing stand, else the cheapest reachable column below or beside the target.</summary>
    private static Column? Best(ScanGround g, BlockPos target, IReadOnlyList<BlockPos> targets, Vec3d? from,
        Func<Vec3d, BlockPos, bool> canReach, out string why)
    {
        var columns = new List<Column>();
        why = "no ground to stand on below or beside it within " + MaxPillar + " blocks";
        for (int r = 0; r <= 3; r++)
            for (int dx = -r; dx <= r; dx++)
                for (int dz = -r; dz <= r; dz++)
                {
                    if (Math.Max(Math.Abs(dx), Math.Abs(dz)) != r) continue;
                    int x = target.X + dx, z = target.Z + dz;
                    // A rim or existing stair can see the block without climbing. GroundUnder only looks below
                    // the target, so consider ordinary standing spots separately before planning any new access.
                    for (int y = Math.Min(g.Height - 2, target.Y + 3); y >= Math.Max(1, target.Y - 5); y--)
                        if (!targets.Contains(new BlockPos(x, y - 1, z)) && g.Stand(x, y, z)
                            && (r != 0 || !ScanGround.Falling.Contains(g.World.GetBlock(target.X, target.Y + 1, target.Z)))
                            && canReach(new Vec3d(x + 0.5, y, z + 0.5), target))
                            columns.Add(new Column(new BlockPos(x, y, z), 0, 0.1 * r,
                                Coverage(new BlockPos(x, y, z), 0, targets, canReach)));
                    if (r <= 2 && TryColumn(g, target, r, x, z, canReach, ref why) is { } col
                        && !targets.Contains(new BlockPos(col.Stand.X, col.Stand.Y - 1, col.Stand.Z)))
                        columns.Add(ClusterColumn(g, col, target, targets, canReach));
                }
        if (columns.Count == 0) return null;
        columns = columns.GroupBy(c => c.Stand).Select(group => Ranked(group).First()).ToList();
        var best = Ranked(columns).First();
        if (from is not { } pos || Pathfinder.StartCellFor(g.World, pos) is not { } start) return best;
        // A cheap column can be an isolated pocket in the quarry wall. Probe the preferred stand first;
        // a bounded walk-only flood then ranks reachable alternatives by coverage and actual walking cost.
        // A nearest-goal search alone would select the pawn's current column even when it covers fewer cells.
        var options = new PathOptions { AllowPartial = false, AllowOpenDoors = true, AllowParkour = false,
            MaxNodes = 4000, Timeout = TimeSpan.FromMilliseconds(150) };
        if (Pathfinder.FindPath(g.World, start, new GoalBlock(best.Stand), options).Status == PathStatus.Found) return best;
        var directed = options with { MaxNodes = 12000, Timeout = TimeSpan.FromMilliseconds(500) };
        // A deep quarry can have a long stair route through a large surrounding surface. Directed searches reach
        // that route before a local flood spends its budget on unrelated ground around the rim.
        if (Probe(g, start, columns.Where(c => c.Steps == 0 && c != best).ToArray(), directed) is { } walking) return walking;
        if (Probe(g, start, Ranked(columns.Where(c => c.Steps > 0)).Take(1).ToArray(), directed) is { } pillar) return pillar;
        var reachable = Pathfinder.Explore(g.World, start, options, maxNodes: 8000);
        var choices = columns.Where(c => reachable.ContainsKey(c.Stand)).ToList();
        if (choices.Count > 0) return Ranked(choices, c => reachable[c.Stand] / 100).First();
        why = "no walkable ground below or beside it (no route within the search limit)";
        return null;
    }

    private static Column? Probe(ScanGround g, BlockPos start, Column[] columns, PathOptions options)
    {
        if (columns.Length == 0) return null;
        var goals = new GoalAny(columns.Select(c => (Goal)new GoalBlock(c.Stand)).ToArray());
        var path = Pathfinder.FindPath(g.World, start, goals, options);
        if (path.Status != PathStatus.Found) return null;
        var end = path.Steps.Count == 0 ? start : path.Steps[^1].Pos;
        return columns.First(c => c.Stand == end);
    }

    private static IOrderedEnumerable<Column> Ranked(IEnumerable<Column> columns, Func<Column, double>? walkCost = null) =>
        columns.OrderBy(c => c.Steps != 0).ThenByDescending(c => c.Coverage).ThenBy(c => c.Score + (walkCost?.Invoke(c) ?? 0));

    private static int Coverage(BlockPos stand, int steps, IReadOnlyList<BlockPos> targets, Func<Vec3d, BlockPos, bool> canReach) =>
        targets.Count(at => canReach(new Vec3d(stand.X + 0.5, stand.Y + steps, stand.Z + 0.5), at));

    /// <summary>Up to two extra blocks can put an adjacent cluster in reach of the same tower. Existing stands stay walk-only.</summary>
    private static Column ClusterColumn(ScanGround g, Column column, BlockPos target, IReadOnlyList<BlockPos> targets,
        Func<Vec3d, BlockPos, bool> canReach)
    {
        var best = column with { Coverage = Coverage(column.Stand, column.Steps, targets, canReach) };
        if (targets.Count < 2 || column.Steps == 0) return best;
        for (int extra = 1; extra <= 2 && column.Steps + extra <= MaxPillar; extra++)
        {
            int steps = column.Steps + extra;
            if (!ClearShaft(g, column.Stand.X, column.Stand.Z, column.Stand.Y, column.Stand.Y + steps)) break;
            if (!canReach(new Vec3d(column.Stand.X + 0.5, column.Stand.Y + steps, column.Stand.Z + 0.5), target)) continue;
            int coverage = Coverage(column.Stand, steps, targets, canReach);
            if (coverage > best.Coverage) best = column with { Steps = steps, Score = column.Score + extra, Coverage = coverage };
        }
        return best;
    }

    /// <summary>The column at cx,cz: its ground, how many blocks up until the target is in reach, and a free shaft to get there.</summary>
    private static Column? TryColumn(ScanGround g, BlockPos target, int r, int cx, int cz, Func<Vec3d, BlockPos, bool> canReach, ref string why)
    {
        if (!g.World.IsLoaded(cx, cz)) return null;
        if (r == 0 && ScanGround.Falling.Contains(g.World.GetBlock(target.X, target.Y + 1, target.Z))) { why = "sand or gravel over it would fall on me"; return null; }
        int gy = GroundUnder(g, cx, cz, target.Y, ref why);
        if (gy < 0) return null;
        int feet = gy;
        while (feet - gy <= MaxPillar && feet <= target.Y + 1
            && !canReach(new Vec3d(cx + 0.5, feet, cz + 0.5), target)) feet++;
        if (feet - gy > MaxPillar) { why = "more than " + MaxPillar + " blocks up"; return null; }
        if (feet > target.Y + 1) { why = "no visible face from the column"; return null; }
        if (feet + 1 >= g.Height - 1) { why = "above the build height"; return null; }
        if (feet > gy && OnALid(g, cx, gy - 1, cz, ref why)) return null; // a pillar never stands on a chest's lid
        if (!ClearShaft(g, cx, cz, gy, feet)) { why = "no free column to pillar up beside it"; return null; }
        return new Column(new BlockPos(cx, gy, cz), feet - gy, (feet - gy) + 0.3 * r, 1);
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
