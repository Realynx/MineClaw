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
