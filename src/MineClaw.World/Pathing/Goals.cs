namespace MineClaw.World;

/// <summary>A pathfinding goal over foot cells (the block the player's feet occupy).</summary>
public abstract class Goal
{
    public abstract bool IsReached(int x, int y, int z);

    /// <summary>Admissible-ish distance estimate in blocks (multiplied by the walk cost by the search).</summary>
    public abstract double Distance(int x, int y, int z);

    public bool IsReached(BlockPos p) => IsReached(p.X, p.Y, p.Z);
}

/// <summary>Feet exactly in a given cell.</summary>
public sealed class GoalBlock(int x, int y, int z) : Goal
{
    public BlockPos Target { get; } = new(x, y, z);
    public GoalBlock(BlockPos p) : this(p.X, p.Y, p.Z) { }
    public override bool IsReached(int x, int y, int z) => x == Target.X && y == Target.Y && z == Target.Z;
    public override double Distance(int x, int y, int z) =>
        Math.Sqrt(Target.DistanceSquaredTo(new BlockPos(x, y, z)));
    public override string ToString() => $"block {Target}";
}

/// <summary>Feet cell within <see cref="Range"/> blocks (Euclidean, cell to cell) of a target block.</summary>
public sealed class GoalNear(int x, int y, int z, double range) : Goal
{
    public BlockPos Target { get; } = new(x, y, z);
    public double Range { get; } = range;
    public GoalNear(BlockPos p, double range) : this(p.X, p.Y, p.Z, range) { }
    public override bool IsReached(int x, int y, int z) =>
        Target.DistanceSquaredTo(new BlockPos(x, y, z)) <= Range * Range;
    public override double Distance(int x, int y, int z) =>
        Math.Max(0, Math.Sqrt(Target.DistanceSquaredTo(new BlockPos(x, y, z))) - Range);
    public override string ToString() => $"within {Range} of {Target}";
}

/// <summary>
/// A cell from which the player's eye (feet + 1.62) is within <see cref="Reach"/> of the target block's center —
/// use for "go to where I can dig/use/place at this block". Optionally excludes standing in/on the target itself.
/// </summary>
public sealed class GoalWithinReach(BlockPos target, double reach = 4.0, bool excludeTargetColumn = true) : Goal
{
    public BlockPos Target { get; } = target;
    public double Reach { get; } = reach;

    public override bool IsReached(int x, int y, int z)
    {
        if (excludeTargetColumn && x == Target.X && z == Target.Z && (y == Target.Y || y == Target.Y - 1 || y == Target.Y + 1))
            return false;
        double dx = x + 0.5 - (Target.X + 0.5), dy = y + PlayerPhysics.EyeHeight - (Target.Y + 0.5), dz = z + 0.5 - (Target.Z + 0.5);
        return dx * dx + dy * dy + dz * dz <= Reach * Reach;
    }

    public override double Distance(int x, int y, int z)
    {
        double dx = x - Target.X, dy = y + 1.12 - Target.Y, dz = z - Target.Z;
        return Math.Max(0, Math.Sqrt(dx * dx + dy * dy + dz * dz) - Reach);
    }

    public override string ToString() => $"reach {Target} ({Reach})";
}

/// <summary>
/// A cell from which the target block can actually be clicked: the eye is within <see cref="Reach"/> AND a ray from the eye
/// reaches one of the target's faces (<see cref="Aiming.FindDigAim"/>). <see cref="GoalWithinReach"/> is distance only, so it
/// is satisfied on the wrong side of a wall (live 10-27: clones stopped outside the plank house at 14,64,-3 "within reach" of
/// chest 14,64,-5 behind the wall and reported "can't reach chest"); this goal sends the search through the door instead.
/// Visibility is judged from the cell center with the live blocks (doors as they are now; cells inside a room see the
/// container whatever state the door is in).
/// </summary>
public sealed class GoalUseBlock(IBlockAccess world, BlockPos target, double reach = 3.9) : Goal
{
    public BlockPos Target { get; } = target;
    public double Reach { get; } = reach;

    /// <summary>Eye position of a player standing centered in feet cell (x,y,z), on a slab/snow layer when there is one.</summary>
    public static Vec3d EyeAt(IBlockAccess w, int x, int y, int z)
    {
        double top = BlockShapes.CollisionTop(w, x, y, z);
        double feet = y + (top is > 0 and < 1 ? top : 0);
        return new Vec3d(x + 0.5, feet + PlayerPhysics.EyeHeight, z + 0.5);
    }

    public override bool IsReached(int x, int y, int z)
    {
        if (x == Target.X && z == Target.Z && y >= Target.Y - 1 && y <= Target.Y + 1) return false;
        double dx = x + 0.5 - (Target.X + 0.5), dy = y + PlayerPhysics.EyeHeight - (Target.Y + 0.5), dz = z + 0.5 - (Target.Z + 0.5);
        if (dx * dx + dy * dy + dz * dz > (Reach + 0.6) * (Reach + 0.6)) return false;
        // Seen from the center AND from eyes a quarter block off it: the follower stops anywhere near the center, and a
        // spot that sees the block only along one exact line past a door panel / wall corner failed on arrival (13.4,64,-2.5).
        var eye = EyeAt(world, x, y, z);
        if (Aiming.FindDigAim(world, eye, Target, Reach) is null) return false;
        foreach (var (ox, oz) in Offsets)
            if (Aiming.FindDigAim(world, new Vec3d(eye.X + ox, eye.Y, eye.Z + oz), Target, Reach + 0.3) is null) return false;
        return true;
    }

    private static readonly (double, double)[] Offsets = [(0.25, 0), (-0.25, 0), (0, 0.25), (0, -0.25)];

    public override double Distance(int x, int y, int z)
    {
        double dx = x - Target.X, dy = y + 1.12 - Target.Y, dz = z - Target.Z;
        return Math.Max(0, Math.Sqrt(dx * dx + dy * dy + dz * dz) - Reach);
    }

    public override string ToString() => $"use {Target} ({Reach})";
}

/// <summary>Within range of a (moving) point, e.g. an entity's position.</summary>
public sealed class GoalNearPosition(Vec3d position, double range) : Goal
{
    public Vec3d Position { get; } = position;
    public double Range { get; } = range;

    private double Dist(int x, int y, int z)
    {
        double dx = x + 0.5 - Position.X, dy = y - Position.Y, dz = z + 0.5 - Position.Z;
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    public override bool IsReached(int x, int y, int z) => Dist(x, y, z) <= Range;
    public override double Distance(int x, int y, int z) => Math.Max(0, Dist(x, y, z) - Range);
    public override string ToString() => $"within {Range} of {Position}";
}

/// <summary>Any cell with feet at a given y level.</summary>
public sealed class GoalYLevel(int y) : Goal
{
    public int Y { get; } = y;
    public override bool IsReached(int x, int y, int z) => y == Y;
    public override double Distance(int x, int y, int z) => Math.Abs(y - Y);
    public override string ToString() => $"y={Y}";
}

/// <summary>Reached when any sub-goal is.</summary>
public sealed class GoalAny(params Goal[] goals) : Goal
{
    public IReadOnlyList<Goal> Goals { get; } = goals;
    public override bool IsReached(int x, int y, int z) => Goals.Any(g => g.IsReached(x, y, z));
    public override double Distance(int x, int y, int z) => Goals.Min(g => g.Distance(x, y, z));
    public override string ToString() => $"any({string.Join(", ", Goals)})";
}

/// <summary>An inner goal restricted to cells accepted by a predicate (results cached per cell).</summary>
public sealed class GoalFiltered(Goal inner, Func<int, int, int, bool> accept, string label) : Goal
{
    private readonly Dictionary<BlockPos, bool> _cache = new();

    public Goal Inner { get; } = inner;

    public override bool IsReached(int x, int y, int z) => Inner.IsReached(x, y, z) && Accepts(x, y, z);

    private bool Accepts(int x, int y, int z)
    {
        var p = new BlockPos(x, y, z);
        if (!_cache.TryGetValue(p, out bool ok)) _cache[p] = ok = accept(x, y, z);
        return ok;
    }

    /// <summary>Rejected cells close to the goal look further away, so a partial path ends on an accepted cell
    /// rather than the nearest rejected one (e.g. on the rim of the island).</summary>
    public override double Distance(int x, int y, int z)
    {
        double d = Inner.Distance(x, y, z);
        return d < 2.5 && !Accepts(x, y, z) ? d + 2.5 : d;
    }
    public override string ToString() => $"{Inner} ({label})";

    /// <summary>
    /// "Within <paramref name="range"/> of" a spot, but never from under the ground below it: a cell lower than the spot (feet below its
    /// y − 1) counts only when nothing solid stands on the line from its eyes to the spot's centre (leaves aside: a crown is no ground).
    /// Live 11-04 story-1: "within 3 of" a tree's base was met in a cave three blocks under it, and the chop found every log out of
    /// reach ("chopped 0/39"). Cells beside or above the spot are unchanged.
    /// </summary>
    public static Goal NearNotFromBelow(IBlockAccess w, BlockPos target, double range)
    {
        var near = new GoalNear(target, range);
        if (range < 2) return near;
        return new GoalFiltered(near, (x, y, z) => y >= target.Y - 1 || ClearLine(w, x, y, z, target), "not from under the ground");
    }

    /// <summary>Nothing solid (leaves aside) on the line from the eyes of a body in foot cell x,y,z to <paramref name="target"/>'s centre;
    /// the body's own cells and the target cell don't count.</summary>
    public static bool ClearLine(IBlockAccess w, int x, int y, int z, BlockPos target)
    {
        double ex = x + 0.5, ey = y + PlayerPhysics.EyeHeight, ez = z + 0.5;
        double tx = target.X + 0.5, ty = target.Y + 0.5, tz = target.Z + 0.5;
        double dx = tx - ex, dy = ty - ey, dz = tz - ez, len = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        int n = Math.Max(1, (int)Math.Ceiling(len / 0.2));
        for (int i = 1; i < n; i++)
        {
            double f = (double)i / n;
            int cx = (int)Math.Floor(ex + dx * f), cy = (int)Math.Floor(ey + dy * f), cz = (int)Math.Floor(ez + dz * f);
            if (OwnOrTargetCell(cx, cy, cz, x, y, z, target)) continue;
            int id = w.GetBlock(cx, cy, cz);
            if (id is 18 or 161 || !BlockShapes.HasCollision(w, cx, cy, cz)) continue;
            return false;
        }
        return true;
    }

    /// <summary>The target cell, or one of the body's own two cells.</summary>
    private static bool OwnOrTargetCell(int cx, int cy, int cz, int x, int y, int z, BlockPos target) =>
        (cx == target.X && cy == target.Y && cz == target.Z) || (cx == x && cz == z && (cy == y || cy == y + 1));

    /// <summary>
    /// <paramref name="inner"/>, but only standing cells at least <paramref name="minEdgeDistance"/> from any deadly
    /// drop (2 = never on the rim of the island) and, optionally, with no lava within 1 block of feet/head.
    /// </summary>
    public static GoalFiltered AwayFromEdge(IBlockAccess w, Goal inner, int minEdgeDistance = 2, bool avoidLava = true,
        double maxSafeFall = 3) =>
        new(inner, (x, y, z) =>
                Safety.EdgeDistance(w, x, y, z, minEdgeDistance, maxSafeFall) >= minEdgeDistance
                && (!avoidLava || !Safety.LavaNear(w, x, y, z)),
            avoidLava ? "away from edges and lava" : "away from edges");
}
