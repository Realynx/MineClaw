namespace MineClaw.World;

public enum FollowerStatus
{
    /// <summary>Apply <see cref="FollowerOutput.Input"/> to physics this tick.</summary>
    Moving,
    /// <summary>Perform <see cref="FollowerOutput.Request"/> (dig or place), keep applying the (idle) input meanwhile,
    /// then call Tick again — the follower re-checks the world to see whether it was done.</summary>
    NeedsAction,
    Finished,
    /// <summary>Stop and re-plan (or react to danger); see <see cref="FollowerOutput.Reason"/>.</summary>
    Failed,
}

public enum FollowerActionKind { Break, Place }

/// <summary>An action the Client must perform for the path to continue. For Place, use
/// <see cref="Aiming.FindPlaceAim"/> (the follower only requests it once an aim exists from the current position).</summary>
public readonly record struct FollowerAction(FollowerActionKind Kind, BlockPos Pos);

public readonly record struct FollowerOutput(FollowerStatus Status, MoveInput Input, FollowerAction? Action = null, string? Reason = null);

public sealed record PathFollowerOptions
{
    public bool AllowSprint { get; init; }
    /// <summary>Ticks without progress before reporting "stuck".</summary>
    public int StuckTicks { get; init; } = 80;
    public double Reach { get; init; } = Aiming.DefaultReach;
    /// <summary>Max horizontal deviation from the current segment before reporting "off path".</summary>
    public double MaxDeviation { get; init; } = 2.0;
}

/// <summary>
/// Converts a path into per-tick physics inputs. Call <see cref="Tick"/> once per client tick before
/// <see cref="PlayerPhysics.Tick"/>, feeding the returned input. Sneaks on edge steps (vanilla edge protection keeps
/// the player from walking off), centers precisely before edge/bridge/pillar moves, and fails fast when falling
/// toward the void, stuck, or knocked off the path.
/// </summary>
public sealed class PathFollower
{
    private readonly IReadOnlyList<PathStep> _steps;
    private readonly PathFollowerOptions _o;
    private Vec3d? _segmentStart;
    private double _bestDist = double.MaxValue;
    private int _noProgress;

    public PathFollower(IReadOnlyList<PathStep> steps, PathFollowerOptions? options = null)
    {
        _steps = steps;
        _o = options ?? new PathFollowerOptions();
    }

    public IReadOnlyList<PathStep> Steps => _steps;
    public int StepIndex { get; private set; }
    public PathStep? CurrentStep => StepIndex < _steps.Count ? _steps[StepIndex] : null;
    public bool IsFinished => StepIndex >= _steps.Count;

    public FollowerOutput Tick(IBlockAccess w, PhysicsState s)
    {
        for (int guard = 0; guard < 4; guard++)
        {
            if (StepIndex >= _steps.Count)
                return new FollowerOutput(FollowerStatus.Finished, MoveInput.Idle(s.Yaw, s.Pitch));
            var step = _steps[StepIndex];
            _segmentStart ??= s.Position;

            if (Reached(w, s, step))
            {
                StepIndex++;
                _segmentStart = s.Position;
                _bestDist = double.MaxValue;
                _noProgress = 0;
                continue;
            }
            return Drive(w, s, step);
        }
        return new FollowerOutput(FollowerStatus.Moving, MoveInput.Idle(s.Yaw, s.Pitch));
    }

    private bool NeedsPrecision(PathStep step)
    {
        if (StepIndex == _steps.Count - 1 || step.Sneak) return true;
        var next = StepIndex + 1 < _steps.Count ? _steps[StepIndex + 1] : null;
        return next is not null && (next.Kind is MoveKind.Pillar or MoveKind.Bridge or MoveKind.ClimbUp or MoveKind.ClimbDown || next.Sneak);
    }

    private bool Reached(IBlockAccess w, PhysicsState s, PathStep step)
    {
        if (step.Break.Count > 0 && step.Break.Any(b => BlockPresent(w, b))) return false;
        if (step.Place is { } p && Aiming.IsReplaceable(w, w.GetBlock(p.X, p.Y, p.Z))) return false;
        var pos = s.Position;
        if ((int)Math.Floor(pos.X) != step.X || (int)Math.Floor(pos.Z) != step.Z) return false;
        bool settled = s.OnGround || s.InWater || s.OnLadder || PlayerPhysics.IsOnLadder(w, s);
        if (!settled) return false;
        switch (step.Kind)
        {
            case MoveKind.Swim:
            case MoveKind.ClimbUp:
            case MoveKind.ClimbDown:
                if ((int)Math.Floor(pos.Y + 0.2) != step.Y) return false;
                break;
            default:
                if (Math.Abs(pos.Y - step.Floor) > 0.15) return false;
                break;
        }
        if (NeedsPrecision(step))
        {
            var t = step.Target;
            double dx = t.X - pos.X, dz = t.Z - pos.Z;
            if (dx * dx + dz * dz > 0.2 * 0.2) return false;
        }
        return true;
    }

    private static bool BlockPresent(IBlockAccess w, BlockPos b)
    {
        int id = w.GetBlock(b.X, b.Y, b.Z);
        return id != 0 && !BlockShapes.IsLiquid(id);
    }

    private FollowerOutput Drive(IBlockAccess w, PhysicsState s, PathStep step)
    {
        var pos = s.Position;
        var target = step.Target;
        float yaw = s.Yaw;
        double dx = target.X - pos.X, dz = target.Z - pos.Z;
        double dist = Math.Sqrt(dx * dx + dz * dz);
        if (dist > 0.05) yaw = PlayerPhysics.YawPitchTo(pos, target with { Y = pos.Y }).Yaw;
        bool sneak = step.Sneak && step.Kind != MoveKind.Drop;
        var idle = new MoveInput(Yaw: s.Yaw, Pitch: s.Pitch, Sneak: sneak);

        // ---- danger / failure checks ----
        if (!s.OnGround && !s.InWater && !s.OnLadder && s.Velocity.Y < -0.2 && Safety.IsPlayerOverVoid(w, pos))
            return Fail(idle, "falling into the void");
        double lowest = Math.Min(step.Floor, _segmentStart!.Value.Y);
        if (pos.Y < lowest - (step.Kind == MoveKind.Drop ? 1.0 : 1.5) && (s.OnGround || s.InWater))
            return Fail(idle, "fell below the path");
        if (SegmentDeviation(pos, _segmentStart.Value, target) > _o.MaxDeviation)
            return Fail(idle, "off path");
        double progressMetric = dist + Math.Abs(pos.Y - step.Floor);
        if (progressMetric < _bestDist - 0.05) { _bestDist = progressMetric; _noProgress = 0; }
        else if (++_noProgress > _o.StuckTicks) return Fail(idle, "stuck");

        // ---- actions: blocks to break first ----
        foreach (var b in step.Break)
            if (BlockPresent(w, b))
            {
                _noProgress = 0;
                return new FollowerOutput(FollowerStatus.NeedsAction, idle, new FollowerAction(FollowerActionKind.Break, b));
            }

        // ---- actions: place ----
        if (step.Place is { } p && Aiming.IsReplaceable(w, w.GetBlock(p.X, p.Y, p.Z)))
        {
            if (step.Kind == MoveKind.Pillar)
            {
                var box = s.BoundingBox;
                bool clear = !box.Intersects(AABB.Block(p.X, p.Y, p.Z));
                if (clear && Aiming.FindPlaceAim(w, s.EyePosition, p, _o.Reach, box) is not null)
                {
                    _noProgress = 0;
                    return new FollowerOutput(FollowerStatus.NeedsAction, idle with { Pitch = 90 },
                        new FollowerAction(FollowerActionKind.Place, p));
                }
                // Center on the column, then jump.
                var c = new Vec3d(p.X + 0.5, pos.Y, p.Z + 0.5);
                double cd = Math.Sqrt(Math.Pow(c.X - pos.X, 2) + Math.Pow(c.Z - pos.Z, 2));
                float cy = cd > 0.05 ? PlayerPhysics.YawPitchTo(pos, c).Yaw : s.Yaw;
                bool centered = cd < 0.2;
                _noProgress = Math.Max(0, _noProgress - 1);
                return Move(new MoveInput(Forward: cd > 0.1 + Speed(s) * 2.2 ? 1 : 0, Jump: centered && s.OnGround,
                    Sneak: sneak, Yaw: cy, Pitch: 90));
            }

            // Bridge: sneak toward the target until the support face becomes visible, then place.
            if (Aiming.FindPlaceAim(w, s.EyePosition, p, _o.Reach, s.BoundingBox) is not null && s.OnGround)
            {
                _noProgress = 0;
                return new FollowerOutput(FollowerStatus.NeedsAction, idle with { Sneak = true },
                    new FollowerAction(FollowerActionKind.Place, p));
            }
            return Move(new MoveInput(Forward: 1, Sneak: true, Yaw: yaw, Pitch: s.Pitch));
        }

        // ---- movement ----
        bool precise = NeedsPrecision(step);
        double speed = Speed(s);
        float forward = !precise || dist > Math.Max(0.08, speed * 2.2) ? 1 : 0;
        bool jump = false;
        float moveYaw = yaw;

        switch (step.Kind)
        {
            case MoveKind.Jump:
                jump = (s.OnGround || s.InWater) && pos.Y < step.Floor - 0.3 && (s.CollidedHorizontally || dist < 1.3);
                forward = 1;
                break;
            case MoveKind.ClimbUp:
            {
                // Push into the wall the ladder hangs on: collidedHorizontally on a ladder → climb at 0.2/tick.
                var wall = LadderWall(w, (int)Math.Floor(pos.X), (int)Math.Floor(pos.Y + 0.01), (int)Math.Floor(pos.Z));
                if (wall is { } wd && dist < 0.35)
                    moveYaw = PlayerPhysics.YawPitchTo(pos, new Vec3d(pos.X + wd.X, pos.Y, pos.Z + wd.Z)).Yaw;
                forward = 1;
                jump = s.OnGround;
                break;
            }
            case MoveKind.ClimbDown:
                forward = dist > 0.15 ? 1 : 0;
                sneak = false;
                break;
            case MoveKind.Swim:
                jump = target.Y >= pos.Y - 0.2 || !s.InWater;
                forward = dist > 0.1 ? 1 : 0;
                break;
            case MoveKind.Drop:
                sneak = false;
                // Arrive gently: in the air keep homing on the landing cell center, never accelerate past it.
                if (!s.OnGround) forward = dist > Math.Max(0.1, speed * 4) ? 1 : 0;
                break;
        }
        if (s.InWater && step.Kind != MoveKind.Swim && step.Floor >= pos.Y - 0.2) jump = true;

        bool sprint = _o.AllowSprint && !sneak && forward > 0 && step.Kind is MoveKind.Walk or MoveKind.Diagonal
                      && StepIndex + 3 < _steps.Count && dist > 0.8;
        return Move(new MoveInput(forward, 0, jump, sneak, sprint, moveYaw, 0));
    }

    private static double Speed(PhysicsState s) => s.Velocity.HorizontalLength;

    private static FollowerOutput Move(MoveInput input) => new(FollowerStatus.Moving, input);

    private static FollowerOutput Fail(MoveInput idle, string reason) =>
        new(FollowerStatus.Failed, idle, null, reason);

    private static double SegmentDeviation(Vec3d p, Vec3d a, Vec3d b)
    {
        double abx = b.X - a.X, abz = b.Z - a.Z, apx = p.X - a.X, apz = p.Z - a.Z;
        double len2 = abx * abx + abz * abz;
        double t = len2 < 1e-9 ? 0 : Math.Clamp((apx * abx + apz * abz) / len2, 0, 1);
        double cx = a.X + abx * t - p.X, cz = a.Z + abz * t - p.Z;
        return Math.Sqrt(cx * cx + cz * cz);
    }

    /// <summary>Direction (unit x/z) from a ladder/vine cell toward the wall it is attached to.</summary>
    private static (int X, int Z)? LadderWall(IBlockAccess w, int x, int y, int z)
    {
        int id = w.GetBlock(x, y, z);
        if (id == BlockShapes.Ladder)
        {
            return w.GetMeta(x, y, z) switch
            {
                2 => (0, 1),
                3 => (0, -1),
                4 => (1, 0),
                5 => (-1, 0),
                _ => null,
            };
        }
        foreach (var (dx, dz) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
            if (BlockShapes.HasCollision(w, x + dx, y, z + dz)) return (dx, dz);
        return null;
    }
}
