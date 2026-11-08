namespace MineClaw.World;

public sealed partial class PathFollower
{
    private bool _dropAligned;
    private Vec3d? _dropStart;

    private bool BrakesForDrop(PathStep step) => step is { Kind: MoveKind.Drop, Gentle: true }
        || StepIndex + 1 < _steps.Count && _steps[StepIndex + 1] is { Kind: MoveKind.Drop, Gentle: true };

    /// <summary>A one-block descent onto a repaired quarry step with no runout. Lose incoming momentum and centre on
    /// the original support while sneaking, then enter slowly. Air control actively brakes, rather than relying on drag.</summary>
    private FollowerOutput DriveCarefulDrop(IBlockAccess w, PhysicsState s, PathStep step)
    {
        if (!_dropAligned)
        {
            if (!s.OnGround || s.Position.Y < step.Floor + 0.5) _dropAligned = true;
            else
            {
                if (_dropStart == null && Pathfinder.StartCellFor(w, s.Position) is { } cell)
                    _dropStart = new Vec3d(cell.X + 0.5, s.Position.Y, cell.Z + 0.5);
                var start = _dropStart ?? _segmentStart!.Value;
                if (Horizontal(s.Position, start) < 0.12 && Speed(s) < 0.025) _dropAligned = true;
                else return SlowSteer(s, start, 0.04, sneak: true);
            }
        }
        bool landed = s.OnGround && Math.Abs(s.Position.Y - step.Floor) < 0.15;
        return SlowSteer(s, step.Target, landed ? 0.04 : 0.06, sneak: landed);
    }

    private static FollowerOutput SlowSteer(PhysicsState s, Vec3d target, double maximumSpeed, bool sneak)
    {
        var error = new Vec3d(target.X - s.Position.X, 0, target.Z - s.Position.Z);
        double distance = error.HorizontalLength;
        var wanted = distance > 0.02 ? error * (Math.Min(maximumSpeed, distance * 0.25) / distance) : new Vec3d(0, 0, 0);
        var correction = wanted - new Vec3d(s.Velocity.X, 0, s.Velocity.Z);
        double acceleration = s.OnGround ? sneak ? 0.03 : 0.1 : 0.02;
        float forward = (float)Math.Clamp(correction.HorizontalLength / acceleration, 0, 1);
        float yaw = correction.HorizontalLength > 1e-5
            ? PlayerPhysics.YawPitchTo(s.Position, s.Position + correction).Yaw : s.Yaw;
        return Move(new MoveInput(Forward: forward, Sneak: sneak, Yaw: yaw, Pitch: s.Pitch));
    }
}
