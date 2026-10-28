using MineClaw.Bot.Blueprints;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Bot.Building;

public enum ExitKind { Walk, DigDown, Place }

/// <summary>One move of a way down: walk to <see cref="Stand"/>; dig <see cref="Target"/> (my own scaffold) from on top of it; or
/// place a block at <see cref="Target"/> by clicking <see cref="Face"/> of its neighbour from <see cref="Stand"/>.</summary>
public sealed record ExitMove(ExitKind Kind, BlockPos Target, BlockPos Stand, BlockFace? Face = null, bool Hidden = false)
{
    public override string ToString() => $"{Kind} {Target.X},{Target.Y},{Target.Z} from {Stand.X},{Stand.Y},{Stand.Z}";
}

/// <summary>The way down from where a body stands to home level: the moves, and where it ends.</summary>
public sealed record ExitPlan(List<ExitMove> Moves, BlockPos End)
{
    public int Dug => Moves.Count(m => m.Kind == ExitKind.DigDown);
    public int Placed => Moves.Count(m => m.Kind == ExitKind.Place);
}

/// <summary>
/// Never strand a body (zero tokens): when a build run ends with the body somewhere it can't walk down from (<see cref="WayHome"/>),
/// the way down is planned on a simulated world, one level at a time, in order of preference:
/// <list type="number">
/// <item>walk (drops ≤ 3) — nothing to do;</item>
/// <item>my own pillar: dig the scaffold block I stand on from on top (a 1-block drop onto a full block, no void beside the cell I
///   drop into: the client's own-pillar rule);</item>
/// <item>a 1-wide staircase down: the next tread one down and one out, clicked against whatever solid face it has (a wall, the
///   block under my floor), else a support first under my own floor (its bottom face: hidden, distance-only on the b1.8.1
///   server). A tread is never over the void (its column has ground under it), never beside it, never in a design block's
///   cell, and every step down is a walk the pathfinder verifies (a 1-block drop).</item>
/// </list>
/// Each level re-checks the walk home first; no plan = stranded (the caller says so; the danger reflex boxes the body in if it
/// must). Pure: the executor runs the moves through the body's own primitives.
/// </summary>
public static class ExitRoute
{
    private static readonly BlockFace[] Sides = [BlockFace.North, BlockFace.East, BlockFace.South, BlockFace.West];

    /// <param name="mine">My own scaffold (dug from on top only when it is mine).</param>
    /// <param name="mayPlace">A cell a tread or support may fill (not a design block, not protected).</param>
    /// <param name="blocks">Blocks on hand for treads.</param>
    public static ExitPlan? Plan(IBlockAccess world, BlockPos feet, BlockPos home, Func<BlockPos, bool> mine, Func<BlockPos, bool> mayPlace,
        int blocks, int maxLevels = 48, double reach = 4.5, List<string>? trace = null)
    {
        var sim = new PlanWorld(world);
        var moves = new List<ExitMove>();
        var cur = feet;
        for (int level = 0; level <= maxLevels; level++)
        {
            if (WayHome.WalkDown(sim, cur, home) is { } end)
            {
                if (end != cur) moves.Add(new ExitMove(ExitKind.Walk, end, end));
                return new ExitPlan(moves, end);
            }
            var flood = Pathfinder.Explore(sim, cur, WayHome.Walk, 3000);
            if (flood.Count == 0) flood[cur] = 0;
            // The lowest reachable cells first (a drop already taken is progress), then the nearest.
            var cells = flood.OrderBy(kv => kv.Key.Y).ThenBy(kv => kv.Value).Select(kv => kv.Key).Take(400).ToList();
            if (DigDown(sim, cells, mine) is { } dig)
            {
                if (dig != cur) moves.Add(new ExitMove(ExitKind.Walk, dig, dig));
                moves.Add(new ExitMove(ExitKind.DigDown, dig.Down(), dig));
                sim.Set(dig.Down(), 0);
                cur = dig.Down();
                continue;
            }
            if (Stair(sim, cells, mayPlace, reach, ref blocks, trace) is not { } st) { trace?.Add($"no stair down from {cur} ({cells.Count} cells)"); return null; }
            trace?.Add($"stair {st.From} -> {st.To} ({st.Places.Count} placed)");
            if (st.From != cur) moves.Add(new ExitMove(ExitKind.Walk, st.From, st.From));
            moves.AddRange(st.Places);
            moves.Add(new ExitMove(ExitKind.Walk, st.To, st.To));
            cur = st.To;
        }
        return null;
    }

    /// <summary>A reachable cell standing on my own scaffold that can be dug from on top: a 1-block drop onto a full block.</summary>
    private static BlockPos? DigDown(PlanWorld sim, List<BlockPos> cells, Func<BlockPos, bool> mine)
    {
        foreach (var c in cells)
        {
            var b = c.Down();
            int id = sim.Get(b);
            if (id == 0 || !BuildBlocks.Supports(id) || !mine(b)) continue;
            if (!BuildBlocks.NormalCube(sim.Get(b.Down())) || Safety.VoidNeighbourCount(sim, b.X, b.Y, b.Z) > 0) continue;
            return c;
        }
        return null;
    }

    private sealed record StairStep(BlockPos From, BlockPos To, List<ExitMove> Places);

    /// <summary>One stair step down from a reachable cell: the tread (and its support), verified walkable.</summary>
    private static StairStep? Stair(PlanWorld sim, List<BlockPos> cells, Func<BlockPos, bool> mayPlace, double reach, ref int blocks, List<string>? trace = null)
    {
        var reachable = cells.ToHashSet();
        StairStep? best = null;
        int bestFall = int.MaxValue, bestPlaces = int.MaxValue;
        foreach (var c in cells)
        {
            if (!BuildBlocks.Supports(sim.Get(c.Down()))) continue;
            var eye = new Vec3d(c.X + 0.5, c.Y + PlayerPhysics.EyeHeight, c.Z + 0.5);
            foreach (var f in Sides)
            {
                var d = f.Normal();
                var side = new BlockPos(c.X + d.X, c.Y, c.Z + d.Z); // the cell walked through (feet), then dropped from
                var to = side.Down();                              // the next feet cell
                var tread = to.Down();
                if (reachable.Contains(to) || !Open(sim, side) || !Open(sim, side.Up()) || !Open(sim, to)) continue;
                if (!sim.IsLoaded(tread.X, tread.Z) || Safety.IsOverVoid(sim, tread.X, tread.Y, tread.Z)) continue; // never over the void
                if (Safety.VoidNeighbourCount(sim, to.X, to.Y, to.Z) > 0) continue;                                  // nor beside it
                // The lowest landing first (each flight goes on down from its own end), then the fewest blocks.
                int fall = to.Y;
                if (fall > bestFall) continue;
                var places = new List<ExitMove>();
                var fork = sim.Fork();
                if (!BuildBlocks.Supports(fork.Get(tread)))
                {
                    if (fork.Get(tread) != 0 && !Aiming.IsReplaceable(fork, fork.Get(tread)) || !mayPlace(tread)) continue;
                    var face = SupportFace(fork, tread, to, eye, reach);
                    if (face == null)
                    {
                        // A support under my own floor first (its bottom face, hidden), then the tread against it.
                        var floor = c.Down();
                        var under = floor.Down();
                        if (fork.Get(under) != 0 || !mayPlace(under) || !InReach(eye, floor, BlockFace.Bottom, reach)) continue;
                        places.Add(new ExitMove(ExitKind.Place, under, c, BlockFace.Bottom, Hidden: true));
                        fork.Set(under, Blocks.Cobblestone);
                        face = SupportFace(fork, tread, to, eye, reach);
                        if (face == null) continue;
                    }
                    places.Add(new ExitMove(ExitKind.Place, tread, c, face.Value, Hidden: !Seen(face.Value, tread, eye)));
                    fork.Set(tread, Blocks.Cobblestone);
                }
                // The follower's overshoot guard: past the landing there must be footing or a wall, never a deadly drop (the tread
                // made two long when nothing is there: the next step turns or goes on from its end).
                var beyond = new BlockPos(to.X + d.X, to.Y, to.Z + d.Z);
                if (Open(fork, beyond) && Safety.IsDeadlyColumn(fork, beyond.X, beyond.Y, beyond.Z, 3))
                {
                    var ext = beyond.Down();
                    if (fork.Get(ext) != 0 || !mayPlace(ext) || Safety.IsOverVoid(fork, ext.X, ext.Y, ext.Z)) continue;
                    if (SupportFace(fork, ext, beyond, eye, reach) is not { } fx) continue;
                    places.Add(new ExitMove(ExitKind.Place, ext, c, fx, Hidden: !Seen(fx, ext, eye)));
                    fork.Set(ext, Blocks.Cobblestone);
                }
                if (places.Count > blocks || fall == bestFall && places.Count >= bestPlaces) continue;
                // Walkable: from where I stand, down onto the new tread (a 1-block drop the follower takes).
                if (!Pathfinder.FindPath(fork, c, new GoalBlock(to), WayHome.Walk).Success) continue;
                best = new StairStep(c, to, places);
                (bestFall, bestPlaces) = (fall, places.Count);
            }
            if (best != null && bestFall < c.Y - 1) break; // every cell from here on is higher: nothing lower to land on
        }
        if (best == null) return null;
        foreach (var p in best.Places) sim.Set(p.Target, Blocks.Cobblestone);
        blocks -= best.Places.Count;
        return best;
    }

    /// <summary>A face of a solid neighbour of <paramref name="cell"/> (not the cell above it, where the feet go) within reach.</summary>
    private static BlockFace? SupportFace(PlanWorld w, BlockPos cell, BlockPos above, Vec3d eye, double reach)
    {
        BlockFace? pick = null;
        double bestD = double.MaxValue;
        foreach (var face in BlockFaceExtensions.All)
        {
            var n = face.Normal();
            var against = new BlockPos(cell.X - n.X, cell.Y - n.Y, cell.Z - n.Z);
            if (against == above) continue;
            int id = w.Get(against);
            if (!BuildBlocks.Supports(id) || Blocks.Get(id).Interactable) continue;
            var point = FacePoint(cell, face);
            double dist = eye.DistanceTo(point);
            if (dist > reach || dist >= bestD) continue;
            (pick, bestD) = (face, dist);
        }
        return pick;
    }

    private static bool InReach(Vec3d eye, BlockPos against, BlockFace face, double reach)
    {
        var n = face.Normal();
        return eye.DistanceTo(FacePoint(new BlockPos(against.X + n.X, against.Y + n.Y, against.Z + n.Z), face)) <= reach;
    }

    /// <summary>The clicked point: on the boundary between the target cell and the block it is placed against.</summary>
    private static Vec3d FacePoint(BlockPos target, BlockFace face)
    {
        var n = face.Normal();
        return new Vec3d(target.X + 0.5 - n.X * 0.5, target.Y + 0.5 - n.Y * 0.5, target.Z + 0.5 - n.Z * 0.5);
    }

    /// <summary>The eyes are on the open side of the clicked face (looking at it, not through the block).</summary>
    private static bool Seen(BlockFace face, BlockPos target, Vec3d eye)
    {
        var n = face.Normal();
        var p = FacePoint(target, face);
        return (eye.X - p.X) * n.X + (eye.Y - p.Y) * n.Y + (eye.Z - p.Z) * n.Z > 0;
    }

    private static bool Open(IBlockAccess w, BlockPos p) =>
        !BlockShapes.HasCollision(w, p.X, p.Y, p.Z) && !BlockShapes.IsLiquid(w.GetBlock(p.X, p.Y, p.Z));
}
