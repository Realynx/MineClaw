namespace MineClaw.World;

public sealed partial class PathFollower
{
    /// <summary>The most one tick may travel at a boosted speed (Story Mode's Fast / Faster): a cell is never skipped, so the
    /// follower sees the body in each step's cell.</summary>
    public const double MaxBoostRoom = 1.0;

    /// <summary>
    /// How far the body may travel this tick at a boosted speed (<see cref="SpeedBoost"/>) without passing a turn, a stop or anything
    /// the step must do on the way, from the state the follower just ticked on: 0 = no boost (precise steps, actions, sneaking,
    /// jumps, drops, gap jumps, bridges, pillars, doors, the last step). A plain walk or diagonal gives the distance to its cell centre
    /// plus the following steps that go on in a straight line at the same floor; a swim its own distance; a ladder climb up the rest of
    /// the ladder above. Never more than <see cref="MaxBoostRoom"/>.
    /// </summary>
    public double BoostRoom(PhysicsState s)
    {
        if (Commitment is not null || StepIndex >= _steps.Count) return 0;
        var step = _steps[StepIndex];
        if (NoBoost(step)) return 0;
        var pos = s.Position;
        return step.Kind switch
        {
            MoveKind.ClimbUp => LadderRoom(step, pos),
            MoveKind.Swim => NeedsPrecision(step) ? 0 : Math.Min(Horizontal(pos, step.Target), MaxBoostRoom),
            MoveKind.Walk or MoveKind.Diagonal => NeedsPrecision(step) ? 0 : WalkRoom(step, pos),
            _ => 0,
        };
    }

    /// <summary>A step that does something on the way, sneaks, jumps or needs a mechanism's effect: never boosted.</summary>
    private static bool NoBoost(PathStep step) => step.HasActions || step.Sneak || step.Jump is not null || step.Requires is not null;

    /// <summary>Up the rest of the ladder above (the climb steps in the same column after this one).</summary>
    private double LadderRoom(PathStep step, Vec3d pos)
    {
        double top = step.Y;
        for (int i = StepIndex + 1; i < _steps.Count; i++)
        {
            var n = _steps[i];
            if (n.Kind != MoveKind.ClimbUp || n.X != step.X || n.Z != step.Z || n.HasActions) break;
            top = n.Y;
        }
        return Math.Clamp(top - pos.Y, 0, MaxBoostRoom);
    }

    /// <summary>To the step's cell centre, plus the following plain legs that go on in a straight line at the same floor.</summary>
    private double WalkRoom(PathStep step, Vec3d pos)
    {
        double room = Horizontal(pos, step.Target);
        var from = step.Target;
        double dirX = from.X - pos.X, dirZ = from.Z - pos.Z;
        for (int i = StepIndex + 1; i < _steps.Count && room < MaxBoostRoom; i++)
        {
            var n = _steps[i];
            if (EndsStraightRun(n, step, i)) break;
            double nx = n.Target.X - from.X, nz = n.Target.Z - from.Z;
            if (dirX * dirX + dirZ * dirZ < 1e-12) { dirX = nx; dirZ = nz; } // standing on the centre: the next leg sets the line
            else if (!Straight(dirX, dirZ, nx, nz)) break;
            room += Math.Sqrt(nx * nx + nz * nz);
            from = n.Target;
        }
        return Math.Min(room, MaxBoostRoom);
    }

    /// <summary>A leg that isn't a plain walk at the same floor, or the last step (its stop is precise).</summary>
    private bool EndsStraightRun(PathStep n, PathStep step, int i) =>
        n.Kind is not (MoveKind.Walk or MoveKind.Diagonal) || n.HasActions || n.Sneak || n.Jump is not null
        || Math.Abs(n.Floor - step.Floor) > 0.01 || i == _steps.Count - 1;

    private static double Horizontal(Vec3d a, Vec3d b) => Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Z - a.Z) * (b.Z - a.Z));

    /// <summary>Two directions within ~15° of each other.</summary>
    private static bool Straight(double ax, double az, double bx, double bz)
    {
        double la = Math.Sqrt(ax * ax + az * az), lb = Math.Sqrt(bx * bx + bz * bz);
        if (la < 1e-6 || lb < 1e-6) return false;
        return (ax * bx + az * bz) / (la * lb) > 0.966;
    }
}
