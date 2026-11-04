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

/// <summary>Break/Place a block, or Activate = right-click <see cref="FollowerAction.Pos"/> (a door/gate/trapdoor, or the
/// button/lever of an iron door) and wait for the server's block change before calling Tick again.</summary>
public enum FollowerActionKind { Break, Place, Activate }

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
    /// <summary>Ticks a Jump step may have its jump cancelled by the edge guard (<see cref="PathFollower.NoteJumpCancelled"/>)
    /// before the follower gives up on that move and reports it in <see cref="PathFollower.BlockedJump"/>.</summary>
    public int JumpCancelLimit { get; init; } = 8;
    /// <summary>Put doors/gates/trapdoors back the way they were found once past them (default; buttons and plates
    /// reset themselves).</summary>
    public bool RestoreDoors { get; init; } = true;
    /// <summary>Right-clicks per openable before giving up ("locked": a protection plugin, or an iron door that ignored
    /// its button).</summary>
    public int MaxActivations { get; init; } = 3;
    /// <summary>Ticks to stand on an iron door's pressure plate waiting for it to open.</summary>
    public int PlateWaitTicks { get; init; } = 30;
    /// <summary>Ticks to wait after a Trigger step's click for the mechanism's effects (plus its learned lag).</summary>
    public int TriggerWaitTicks { get; init; } = 20;
    /// <summary>Whether sprinting is possible right now (b1.8.1: food &gt; 6). Checked before and during a sprint-jump run-up.</summary>
    public Func<bool>? CanSprint { get; init; }
    /// <summary>The body moves faster than vanilla (Story Mode's Fast / Faster, <see cref="SpeedBoost"/>): it keeps sprinting along
    /// straight runs (<see cref="PathFollower.BoostRoom"/>) instead of easing off near each cell centre. Null = never.</summary>
    public Func<bool>? Boosted { get; init; }
    /// <summary>Max distance between the run-up and its planned trajectory before the jump is aborted.</summary>
    public double ParkourDeviation { get; init; } = 0.12;
}

/// <summary>
/// Converts a path into per-tick physics inputs. Call <see cref="Tick"/> once per client tick before
/// <see cref="PlayerPhysics.Tick"/>, feeding the returned input. Sneaks on edge steps (vanilla edge protection keeps
/// the player from walking off), centers precisely before edge/bridge/pillar moves, and fails fast when falling
/// toward the void, stuck, or knocked off the path.
/// </summary>
public sealed partial class PathFollower
{
    private readonly IReadOnlyList<PathStep> _steps;
    private readonly PathFollowerOptions _o;
    private Vec3d? _segmentStart;
    private double _bestDist = double.MaxValue;
    private int _noProgress;
    private int _jumpCancels;
    private BlockPos? _jumpFrom;
    private readonly Dictionary<BlockPos, int> _activations = new();
    private readonly List<(DoorToggle Toggle, int StepIndex)> _restore = new();
    private int _plateWait;
    private int _triggerWait;
    private bool _triggerClicked;
    private readonly List<(TriggerUse Use, int AfterStep)> _leverRestore = new();
    private readonly ParkourRun _pk = new();
    private readonly List<ParkourPlan> _jumps = new();

    public PathFollower(IReadOnlyList<PathStep> steps, PathFollowerOptions? options = null)
    {
        _steps = steps;
        _o = options ?? new PathFollowerOptions();
    }

    public IReadOnlyList<PathStep> Steps => _steps;
    public int StepIndex { get; private set; }
    public PathStep? CurrentStep => StepIndex < _steps.Count ? _steps[StepIndex] : null;
    public bool IsFinished => StepIndex >= _steps.Count;

    /// <summary>Set when the follower failed because the edge guard kept cancelling the jump of a Jump step: the move
    /// (from foot cell, to foot cell) to put into <see cref="PathOptions.AvoidJumps"/> for the re-plan.</summary>
    public (BlockPos From, BlockPos To)? BlockedJump { get; private set; }

    /// <summary>The safety layer cancelled the jump this follower asked for on the previous tick (landing not verified).</summary>
    public void NoteJumpCancelled(PhysicsState s)
    {
        if (CurrentStep is not { Kind: MoveKind.Jump }) return;
        _jumpCancels++;
        if (s.OnGround) _jumpFrom = new BlockPos((int)Math.Floor(s.Position.X), (int)Math.Floor(s.Position.Y + 1e-6), (int)Math.Floor(s.Position.Z));
    }

    /// <summary>The gap jump being executed this tick (run-up or flight), for the edge guard's planned-parkour exemption.
    /// Null outside a committed run.</summary>
    public ParkourCommit? Commitment { get; private set; }

    /// <summary>Gap jumps completed (landed and settled) so far.</summary>
    public IReadOnlyList<ParkourPlan> CompletedJumps => _jumps;

    /// <summary>The edge guard refused the committed jump on the previous tick (its re-simulation no longer lands safely).</summary>
    public void NoteParkourRejected()
    {
        if (CurrentStep?.Jump is not null) _pk.Rejected = true;
    }

    /// <summary>Openables that were flipped and are still waiting to be put back (or could not be).</summary>
    public IReadOnlyList<DoorToggle> Unrestored => _restore.Select(r => r.Toggle).ToArray();

    /// <summary>Levers of Trigger steps still waiting to be flipped back (or that could not be).</summary>
    public IReadOnlyList<TriggerUse> UnrestoredLevers => _leverRestore.Select(r => r.Use).ToArray();

    /// <summary>Set when a Trigger step failed: the mechanism didn't respond (veto it for the re-plan).</summary>
    public BlockPos? FailedTrigger { get; private set; }

    /// <summary>Set when a step's mechanism effect reverted before the body got through (pulse missed: re-plan, the
    /// trigger may be used again).</summary>
    public TriggerUse? PulseMissed { get; private set; }

    public FollowerOutput Tick(IBlockAccess w, PhysicsState s)
    {
        Commitment = null;
        for (int guard = 0; guard < 4; guard++)
        {
            if (RestoreRequest(w, s) is { } restore) return restore;
            if (StepIndex >= _steps.Count)
                return new FollowerOutput(FollowerStatus.Finished, MoveInput.Idle(s.Yaw, s.Pitch));
            var step = _steps[StepIndex];
            _segmentStart ??= s.Position;

            if (Reached(w, s, step))
            {
                if (_o.RestoreDoors)
                    foreach (var tg in step.Opens)
                        if (tg.CanRestore(w) && !_restore.Any(r => r.Toggle.Pos == tg.Pos)) _restore.Add((tg, StepIndex));
                if (step.Trigger is { Restore: true, Link.Kind: TriggerKind.Lever } lever && _leverRestore.All(r => r.Use.Pos != lever.Pos))
                    _leverRestore.Add((lever, LastRequiring(lever)));
                _plateWait = 0;
                _triggerWait = 0;
                _triggerClicked = false;
                if (step.Jump is { } done) _jumps.Add(done);
                _pk.Reset();
                StepIndex++;
                _segmentStart = s.Position;
                _bestDist = double.MaxValue;
                _noProgress = 0;
                _jumpCancels = 0;
                _jumpFrom = null;
                continue;
            }
            return Drive(w, s, step);
        }
        return new FollowerOutput(FollowerStatus.Moving, MoveInput.Idle(s.Yaw, s.Pitch));
    }

    /// <summary>Index of the last step that needs <paramref name="tu"/>'s effects (the trigger step itself if none).</summary>
    private int LastRequiring(TriggerUse tu)
    {
        int last = StepIndex;
        for (int i = StepIndex + 1; i < _steps.Count; i++)
            if (_steps[i].Requires is { } r && r.Pos == tu.Pos) last = i;
        return last;
    }

    private bool NeedsPrecision(PathStep step)
    {
        if (StepIndex == _steps.Count - 1 || step.Sneak || step.Opens.Count > 0 || step.Trigger is not null) return true;
        var next = StepIndex + 1 < _steps.Count ? _steps[StepIndex + 1] : null;
        // Doorways are 0.81 wide once the panel swings in: line up on the cell centre before and inside them.
        return next is not null && (next.Kind is MoveKind.Pillar or MoveKind.Bridge or MoveKind.ClimbUp or MoveKind.ClimbDown
            or MoveKind.OpenDoor or MoveKind.OpenGate or MoveKind.OpenTrapdoor || next.Sneak || next.Opens.Count > 0
            || next.Jump is not null);
    }

    /// <summary>Restores a door once the body is a full step past it, still within reach, and clear of its old box.</summary>
    private FollowerOutput? RestoreRequest(IBlockAccess w, PhysicsState s)
    {
        if (LeverRestoreRequest(w, s) is { } lever) return lever;
        for (int i = _restore.Count - 1; i >= 0; i--)
        {
            var (tg, at) = _restore[i];
            if (tg.Restored(w)) { _restore.RemoveAt(i); continue; }
            if (StepIndex <= at + 1 && StepIndex < _steps.Count) continue; // not past it yet
            var target = tg.Actuator ?? tg.Pos;
            var c = new Vec3d(target.X + 0.5, target.Y + 0.5, target.Z + 0.5);
            // A lever behind the wall can't be clicked through it.
            bool reachable = (c - s.EyePosition).Length <= _o.Reach
                             && (tg.Actuator is null || Aiming.FindDigAim(w, s.EyePosition, target, _o.Reach) is not null);
            if (!reachable) { if (StepIndex >= _steps.Count) _restore.RemoveAt(i); continue; }
            var box = s.BoundingBox;
            var after = new List<AABB>(2);
            BlockShapes.AddCollisionBoxes(w, tg.Id, tg.FromMeta, tg.Pos.X, tg.Pos.Y, tg.Pos.Z, after);
            if (Openables.IsDoor(tg.Id))
                BlockShapes.AddCollisionBoxes(w, tg.Id, tg.FromMeta | Openables.UpperBit, tg.Pos.X, tg.Pos.Y + 1, tg.Pos.Z, after);
            if (after.Any(b => b.Intersects(box))) continue; // would close it on myself
            if (!Activation(target)) { _restore.RemoveAt(i); continue; }
            return new FollowerOutput(FollowerStatus.NeedsAction, MoveInput.Idle(s.Yaw, s.Pitch),
                new FollowerAction(FollowerActionKind.Activate, target));
        }
        return null;
    }

    /// <summary>Flips a Trigger step's lever back once past the last step needing its effects, when it can be clicked
    /// from here and nothing it moves would close on the body.</summary>
    private FollowerOutput? LeverRestoreRequest(IBlockAccess w, PhysicsState s)
    {
        for (int i = _leverRestore.Count - 1; i >= 0; i--)
        {
            var (tu, after) = _leverRestore[i];
            var p = tu.Pos;
            if (w.GetBlock(p.X, p.Y, p.Z) != Openables.Lever || Openables.IsActuatorOn(Openables.Lever, w.GetMeta(p.X, p.Y, p.Z)) != tu.TurnOn)
            {
                _leverRestore.RemoveAt(i);
                continue;
            }
            if (StepIndex <= after + 1 && StepIndex < _steps.Count) continue;
            if (Aiming.FindDigAim(w, s.EyePosition, p, _o.Reach) is null) { if (StepIndex >= _steps.Count) _leverRestore.RemoveAt(i); continue; }
            var box = s.BoundingBox;
            if (tu.Link.Effects.Any(e => box.Intersects(AABB.Block(e.Pos.X, e.Pos.Y, e.Pos.Z)))) continue;
            if (!Activation(p)) { _leverRestore.RemoveAt(i); continue; }
            return new FollowerOutput(FollowerStatus.NeedsAction, MoveInput.Idle(s.Yaw, s.Pitch), new FollowerAction(FollowerActionKind.Activate, p));
        }
        return null;
    }

    /// <summary>Counts a right-click on <paramref name="p"/>; false once <see cref="PathFollowerOptions.MaxActivations"/> is used up.</summary>
    private bool Activation(BlockPos p)
    {
        _activations.TryGetValue(p, out int n);
        if (n >= _o.MaxActivations) return false;
        _activations[p] = n + 1;
        return true;
    }

    private bool Reached(IBlockAccess w, PhysicsState s, PathStep step)
    {
        if (step.Opens.Count > 0 && step.Opens.Any(o => !o.Done(w))) return false;
        if (step.Trigger is { } tu && !tu.Fired(w)) return false;
        if (step.Break.Count > 0 && step.Break.Any(b => BlockPresent(w, b))) return false;
        if (step.Place is { } p && Aiming.IsReplaceable(w, w.GetBlock(p.X, p.Y, p.Z))) return false;
        var pos = s.Position;
        if ((int)Math.Floor(pos.X) != step.X || (int)Math.Floor(pos.Z) != step.Z) return false;
        bool settled = s.OnGround || s.InWater || s.OnLadder || PlayerPhysics.IsOnLadder(w, s);
        if (!settled) return false;
        if (step.Jump is not null)
        {
            // Landed and braked on the landing cell (the plan's own settle condition, a little looser).
            var c = step.Target;
            double ex = c.X - pos.X, ez = c.Z - pos.Z;
            return _pk.Phase == ParkourPhase.Landed && Math.Abs(pos.Y - step.Floor) < 0.02 && ex * ex + ez * ez <= 0.3 * 0.3
                   && s.Velocity.HorizontalLength < 0.02;
        }
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
        if (step.Jump is { } plan) return DriveParkour(w, s, step, plan);
        var pos = s.Position;
        var target = step.Target;
        float yaw = s.Yaw;
        double dx = target.X - pos.X, dz = target.Z - pos.Z;
        double dist = Math.Sqrt(dx * dx + dz * dz);
        if (dist > 0.05) yaw = PlayerPhysics.YawPitchTo(pos, target with { Y = pos.Y }).Yaw;
        bool sneak = step.Sneak && step.Kind != MoveKind.Drop;
        var idle = new MoveInput(Yaw: s.Yaw, Pitch: s.Pitch, Sneak: sneak);
        if (DangerCheck(w, s, step, idle) is { } danger) return danger;
        if (StallCheck(w, s, step, idle, dist) is { } stalled) return stalled;
        if (StepAction(w, s, step, idle, sneak, yaw, dist) is { } action) return action;
        return Walk(w, s, step, idle, sneak, yaw, dist);
    }

    // ---- danger / failure checks ----

    private FollowerOutput? DangerCheck(IBlockAccess w, PhysicsState s, PathStep step, MoveInput idle)
    {
        var pos = s.Position;
        if (FallingIntoVoid(w, s)) return Fail(idle, "falling into the void");
        double lowest = Math.Min(step.Floor, _segmentStart!.Value.Y);
        if (pos.Y < lowest - (step.Kind == MoveKind.Drop ? 1.0 : 1.5) && (s.OnGround || s.InWater))
            return Fail(idle, "fell below the path");
        if (SegmentDeviation(pos, _segmentStart.Value, step.Target) > _o.MaxDeviation)
            return Fail(idle, "off path");
        return null;
    }

    private static bool FallingIntoVoid(IBlockAccess w, PhysicsState s) =>
        !s.OnGround && !s.InWater && !s.OnLadder && s.Velocity.Y < -0.2 && Safety.IsPlayerOverVoid(w, s.Position);

    /// <summary>A jump the edge guard keeps refusing, a mechanism's passage that closed again, or no progress for too long.</summary>
    private FollowerOutput? StallCheck(IBlockAccess w, PhysicsState s, PathStep step, MoveInput idle, double dist)
    {
        if (step.Kind == MoveKind.Jump && _jumpCancels >= _o.JumpCancelLimit && _jumpFrom is { } from)
        {
            BlockedJump = (from, step.Pos);
            return Fail(idle, $"jump to {step.Pos} refused by the edge guard (landing not verified)");
        }
        if (step.Requires is { } rq && !rq.Fired(w) && EffectAhead(w, s, step, rq))
        {
            PulseMissed = rq;
            return Fail(idle, $"the passage of the {rq} closed again before I got through");
        }
        double progressMetric = dist + Math.Abs(s.Position.Y - step.Floor);
        if (progressMetric < _bestDist - 0.05) { _bestDist = progressMetric; _noProgress = 0; }
        else if (++_noProgress > _o.StuckTicks) return Fail(idle, "stuck");
        return null;
    }

    // ---- actions ----

    /// <summary>What the step needs done before walking it: fire its mechanism, open its doors, break its blocks, place its block.</summary>
    private FollowerOutput? StepAction(IBlockAccess w, PhysicsState s, PathStep step, MoveInput idle, bool sneak, float yaw, double dist)
    {
        // Fire a known mechanism (button/lever from here, or stand on its plate) and wait for its effects.
        if (step.Trigger is { } trig && !trig.Fired(w) && TriggerAction(w, s, step, trig, idle, dist) is { } fired) return fired;
        if (OpenAction(w, step, idle) is { } open) return open;
        if (BreakAction(w, step, idle) is { } dig) return dig;
        if (step.Place is { } p && Aiming.IsReplaceable(w, w.GetBlock(p.X, p.Y, p.Z))) return PlaceAction(w, s, step, p, idle, sneak, yaw);
        return null;
    }

    /// <summary>Doors/gates/trapdoors to open (state read from the world's metadata, never by bumping).</summary>
    private FollowerOutput? OpenAction(IBlockAccess w, PathStep step, MoveInput idle)
    {
        foreach (var tg in step.Opens)
        {
            if (tg.Done(w)) continue;
            _noProgress = 0;
            if (w.GetBlock(tg.Pos.X, tg.Pos.Y, tg.Pos.Z) != tg.Id) return Fail(idle, $"{Openables.Name(tg.Id)} at {tg.Pos} is gone");
            if (tg.Actuator is { } plate && Openables.IsPlate(w.GetBlock(plate.X, plate.Y, plate.Z)))
            {
                // Iron door behind a pressure plate: stand on it and wait for the door.
                if (++_plateWait > _o.PlateWaitTicks) return Fail(idle, $"iron door at {tg.Pos} didn't open from its plate");
                return Move(idle);
            }
            var click = tg.Actuator ?? tg.Pos;
            if (!Activation(click))
                return Fail(idle, $"{Openables.Name(tg.Id)} at {tg.Pos} won't open (locked?)");
            return new FollowerOutput(FollowerStatus.NeedsAction, idle, new FollowerAction(FollowerActionKind.Activate, click));
        }
        return null;
    }

    /// <summary>Blocks to break first.</summary>
    private FollowerOutput? BreakAction(IBlockAccess w, PathStep step, MoveInput idle)
    {
        foreach (var b in step.Break)
            if (BlockPresent(w, b))
            {
                _noProgress = 0;
                return new FollowerOutput(FollowerStatus.NeedsAction, idle, new FollowerAction(FollowerActionKind.Break, b));
            }
        return null;
    }

    /// <summary>A block to place: a pillar's under the feet, a bridge's ahead (sneak toward the target until the support
    /// face becomes visible, then place).</summary>
    private FollowerOutput PlaceAction(IBlockAccess w, PhysicsState s, PathStep step, BlockPos p, MoveInput idle, bool sneak, float yaw)
    {
        if (step.Kind == MoveKind.Pillar) return PillarAction(w, s, p, idle, sneak);
        if (Aiming.FindPlaceAim(w, s.EyePosition, p, _o.Reach, s.BoundingBox) is not null && s.OnGround)
        {
            _noProgress = 0;
            return new FollowerOutput(FollowerStatus.NeedsAction, idle with { Sneak = true },
                new FollowerAction(FollowerActionKind.Place, p));
        }
        return Move(new MoveInput(Forward: 1, Sneak: true, Yaw: yaw, Pitch: s.Pitch));
    }

    private FollowerOutput PillarAction(IBlockAccess w, PhysicsState s, BlockPos p, MoveInput idle, bool sneak)
    {
        var pos = s.Position;
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

    // ---- movement ----

    /// <summary>The movement keys of one tick, shaped per step kind.</summary>
    private struct Gait
    {
        public float Forward;
        public bool Jump;
        public bool Sneak;
        public float Yaw;
    }

    private FollowerOutput Walk(IBlockAccess w, PhysicsState s, PathStep step, MoveInput idle, bool sneak, float yaw, double dist)
    {
        var pos = s.Position;
        bool precise = NeedsPrecision(step);
        double speed = Speed(s);
        var g = new Gait { Forward = !precise || dist > Math.Max(0.08, speed * 2.2) ? 1 : 0, Sneak = sneak, Yaw = yaw };

        // Knocked or slid off a level step into a ditch beside it (a 1-deep trench or water channel: Fox 11-04 "they can't walk out
        // because they have to jump once"): the step's floor is a jump up from here. Next to its cell: jump out onto it, as a Jump
        // step would. Farther: re-plan from the ditch at once (walking into its wall only ends as "stuck" after 4 s).
        var kind = step.Kind;
        if (InDitchBeside(s, step))
        {
            int cx = (int)Math.Floor(pos.X), cz = (int)Math.Floor(pos.Z);
            if (Math.Abs(step.X - cx) > 1 || Math.Abs(step.Z - cz) > 1) return Fail(idle, "fell off the path into a ditch");
            kind = MoveKind.Jump;
            g.Sneak = false;
        }
        KindGait(ref g, w, s, step, kind, dist, speed);
        if (s.InWater && step.Kind != MoveKind.Swim && step.Floor >= pos.Y - 0.2) g.Jump = true;
        bool sprint = ShouldSprint(s, step, kind, g, dist);
        return Move(new MoveInput(g.Forward, 0, g.Jump, g.Sneak, sprint, g.Yaw, 0));
    }

    private static bool InDitchBeside(PhysicsState s, PathStep step) =>
        step.Kind is MoveKind.Walk or MoveKind.Diagonal && (s.OnGround || s.InWater) && s.Position.Y < step.Floor - 0.65
        && s.Position.Y >= step.Floor - 1.3;

    private static void KindGait(ref Gait g, IBlockAccess w, PhysicsState s, PathStep step, MoveKind kind, double dist, double speed)
    {
        var pos = s.Position;
        switch (kind)
        {
            case MoveKind.Jump:
                g.Jump = (s.OnGround || s.InWater) && pos.Y < step.Floor - 0.3 && (s.CollidedHorizontally || dist < 1.3);
                g.Forward = 1;
                break;
            case MoveKind.ClimbUp:
                ClimbUpGait(ref g, w, s, dist);
                break;
            case MoveKind.ClimbDown:
                g.Forward = dist > 0.15 ? 1 : 0;
                g.Sneak = false;
                break;
            case MoveKind.Swim:
                g.Jump = step.Target.Y >= pos.Y - 0.2 || !s.InWater;
                g.Forward = dist > 0.1 ? 1 : 0;
                break;
            case MoveKind.Drop:
                g.Sneak = false;
                // Arrive gently: in the air keep homing on the landing cell center, never accelerate past it.
                if (!s.OnGround) g.Forward = dist > Math.Max(0.1, speed * 4) ? 1 : 0;
                break;
        }
    }

    /// <summary>Push into the wall the ladder hangs on: collidedHorizontally on a ladder → climb at 0.2/tick.</summary>
    private static void ClimbUpGait(ref Gait g, IBlockAccess w, PhysicsState s, double dist)
    {
        var pos = s.Position;
        var wall = LadderWall(w, (int)Math.Floor(pos.X), (int)Math.Floor(pos.Y + 0.01), (int)Math.Floor(pos.Z));
        if (wall is { } wd && dist < 0.35)
            g.Yaw = PlayerPhysics.YawPitchTo(pos, new Vec3d(pos.X + wd.X, pos.Y, pos.Z + wd.Z)).Yaw;
        g.Forward = 1;
        g.Jump = s.OnGround;
    }

    /// <summary>A boosted body (Story Mode's Fast / Faster) keeps sprinting on a straight line: cells go by in a tick or two.</summary>
    private bool ShouldSprint(PhysicsState s, PathStep step, MoveKind kind, Gait g, double dist) =>
        _o.AllowSprint && !g.Sneak && !step.Gentle && g.Forward > 0 && kind is MoveKind.Walk or MoveKind.Diagonal
        && StepIndex + 3 < _steps.Count && (dist > 0.8 || _o.Boosted?.Invoke() == true && BoostRoom(s) >= 0.8);

    // ------------------------------------------------------------------ gap jumps

    private enum ParkourPhase { Align, Run, Landed }

    private sealed class ParkourRun
    {
        public ParkourPhase Phase;
        public int Tick;
        public int Ticks;
        public bool Rejected;
        public IReadOnlyList<Vec3d>? Nominal;

        public void Reset()
        {
            Phase = ParkourPhase.Align;
            Tick = 0;
            Ticks = 0;
            Rejected = false;
            Nominal = null;
        }
    }

    /// <summary>
    /// Executes a validated gap jump: (1) align — sneak onto the takeoff cell centre and stand still, then re-validate
    /// the jump from the actual state (world changes since planning, food); (2) run — feed the plan's schedule tick by
    /// tick (forward + sprint, jump at the planned tick, hold through the flight) while publishing
    /// <see cref="Commitment"/>; before the takeoff, abort (sneak-brake at the edge, re-plan) when the run deviates
    /// from the planned trajectory, sprint is lost or no longer possible, the guard refused it, or the re-simulated
    /// landing is no longer safe; (3) landed — verify the landing height/cell, then brake onto the cell centre.
    /// </summary>
    private FollowerOutput DriveParkour(IBlockAccess w, PhysicsState s, PathStep step, ParkourPlan plan)
    {
        var idle = new MoveInput(Yaw: s.Yaw, Pitch: s.Pitch, Sneak: true);
        _pk.Ticks++;
        return _pk.Phase switch
        {
            ParkourPhase.Align => ParkourAlign(w, s, step, plan, idle),
            ParkourPhase.Run => ParkourRunTick(w, s, step, plan, idle),
            _ => ParkourLanded(w, s, plan, idle),
        };
    }

    /// <summary>(1) Sneak onto the takeoff cell centre and stand still, then re-validate the jump from the actual state.</summary>
    private FollowerOutput ParkourAlign(IBlockAccess w, PhysicsState s, PathStep step, ParkourPlan plan, MoveInput idle)
    {
        var pos = s.Position;
        if (_pk.Ticks > _o.StuckTicks) return Fail(idle, "stuck lining up a gap jump");
        if (!s.OnGround) return Move(idle);
        var start = plan.Start;
        double dx = start.X - pos.X, dz = start.Z - pos.Z;
        double dist = Math.Sqrt(dx * dx + dz * dz);
        if (dist > 1.6 || Math.Abs(pos.Y - plan.FromFloor) > 0.05) return Fail(idle, "not at the takeoff of the planned jump");
        double speed = Speed(s);
        if (dist > 0.07) return WalkToTakeoff(s, start, dist, speed);
        if (speed > 0.003) return Move(idle);
        if (plan.Schedule.Sprint && !(_o.CanSprint?.Invoke() ?? true)) return Fail(idle, "too hungry to sprint-jump");
        return StartRun(w, s, step, plan, idle);
    }

    private static FollowerOutput WalkToTakeoff(PhysicsState s, Vec3d start, double dist, double speed)
    {
        var pos = s.Position;
        float yaw = PlayerPhysics.YawPitchTo(pos, start with { Y = pos.Y }).Yaw;
        return Move(new MoveInput(Forward: dist > Math.Max(0.05, speed * 2.5) ? 1 : 0, Sneak: true, Yaw: yaw, Pitch: s.Pitch));
    }

    /// <summary>Lined up and still: the jump is re-checked (recording its nominal track) and the run starts this tick.</summary>
    private FollowerOutput StartRun(IBlockAccess w, PhysicsState s, PathStep step, ParkourPlan plan, MoveInput idle)
    {
        var check = Parkour.Check(w, s, new ParkourCommit(plan, 0), record: true);
        if (!check.Ok || check.Margin < plan.RequiredMargin)
            return Abort(idle, plan, $"jump to {plan.To} no longer safe ({check.Reason ?? $"margin {check.Margin:0.00}"})");
        _pk.Nominal = check.Track;
        _pk.Phase = ParkourPhase.Run;
        _pk.Tick = 0;
        _pk.Rejected = false;
        return ParkourRunTick(w, s, step, plan, idle);
    }

    /// <summary>(2) Feed the plan's schedule tick by tick, aborting a run-up that went wrong; verify the touchdown.</summary>
    private FollowerOutput ParkourRunTick(IBlockAccess w, PhysicsState s, PathStep step, ParkourPlan plan, MoveInput idle)
    {
        int k = _pk.Tick;
        var sch = plan.Schedule;
        if (k <= sch.JumpTick)
        {
            if (RunUpProblem(w, s, plan, idle, k) is { } stop) return stop;
        }
        else if (s.OnGround || s.InWater || s.OnLadder) return TouchDown(w, s, step, plan, idle, k);
        else if (k - sch.JumpTick > Parkour.MaxFlightTicks) return Fail(idle, "gap jump: still in the air");
        var input = sch.InputAt(k, s);
        Commitment = new ParkourCommit(plan, k);
        _pk.Tick = k + 1;
        _noProgress = 0;
        return Move(input);
    }

    /// <summary>Why the run-up must stop before the takeoff (null = carry on).</summary>
    private FollowerOutput? RunUpProblem(IBlockAccess w, PhysicsState s, ParkourPlan plan, MoveInput idle, int k)
    {
        if (_pk.Rejected) return Abort(idle, plan, "the edge guard refused the run-up");
        if (_pk.Nominal is { } nom && k < nom.Count)
        {
            var e = nom[k];
            var pos = s.Position;
            double dev = Math.Sqrt(Math.Pow(e.X - pos.X, 2) + Math.Pow(e.Y - pos.Y, 2) + Math.Pow(e.Z - pos.Z, 2));
            if (dev > _o.ParkourDeviation) return Abort(idle, plan, $"run-up off its line by {dev:0.00}");
        }
        if (SprintProblem(s, plan.Schedule, k) is { } why) return Abort(idle, plan, why);
        if (!s.OnGround) return Abort(idle, plan, "left the ground before the takeoff");
        return k > 0 ? LandingStillSafe(w, s, plan, idle, k) : null;
    }

    private string? SprintProblem(PhysicsState s, ParkourSchedule sch, int k)
    {
        if (sch.Sprint && k >= 1 && !s.Sprinting) return "lost sprint on the run-up";
        if (sch.Sprint && !(_o.CanSprint?.Invoke() ?? true)) return "too hungry to keep sprinting";
        return null;
    }

    private FollowerOutput? LandingStillSafe(IBlockAccess w, PhysicsState s, ParkourPlan plan, MoveInput idle, int k)
    {
        var chk = Parkour.Check(w, s, new ParkourCommit(plan, k));
        if (!chk.Ok || chk.Margin < plan.RequiredMargin)
            return Abort(idle, plan, $"landing no longer safe ({chk.Reason ?? $"margin {chk.Margin:0.00}"})");
        return null;
    }

    /// <summary>Touched down: verify the landing — at the target floor (a stair's lower half counts), near the planned
    /// touchdown or on the target cell; the landed phase then brakes onto the cell centre.</summary>
    private FollowerOutput TouchDown(IBlockAccess w, PhysicsState s, PathStep step, ParkourPlan plan, MoveInput idle, int k)
    {
        if (LandedWrong(s, step, k))
        {
            BlockedJump = (plan.From, plan.To);
            return Fail(idle, $"gap jump missed: landed at {s.Position} instead of {step.Pos}");
        }
        _pk.Phase = ParkourPhase.Landed;
        _pk.Ticks = 0;
        return ParkourLanded(w, s, plan, idle);
    }

    private bool LandedWrong(PhysicsState s, PathStep step, int k)
    {
        var pos = s.Position;
        var tgt = step.Target;
        double off = Math.Sqrt((tgt.X - pos.X) * (tgt.X - pos.X) + (tgt.Z - pos.Z) * (tgt.Z - pos.Z));
        double fromNominal = _pk.Nominal is { } nm && k < nm.Count
            ? Math.Sqrt(Math.Pow(nm[k].X - pos.X, 2) + Math.Pow(nm[k].Z - pos.Z, 2)) : double.MaxValue;
        bool onTarget = (int)Math.Floor(pos.X) == step.X && (int)Math.Floor(pos.Z) == step.Z;
        return pos.Y < step.Floor - 0.55 || pos.Y > step.Floor + 0.05 || s.InWater || s.OnLadder
               || !(onTarget || (fromNominal < 0.5 && off < 3.2));
    }

    /// <summary>(3) Landed: brake onto the cell centre with the schedule's post-takeoff input.</summary>
    private FollowerOutput ParkourLanded(IBlockAccess w, PhysicsState s, ParkourPlan plan, MoveInput idle)
    {
        // Never planned again (live 10-28, 67: the same jump by the main house re-planned 4x per goto, 3 gotos, each
        // "stuck after a gap jump" at 8.4,64.2,-8.3): the re-plan routes around it (door, ladder, stairs).
        if (_pk.Ticks > Parkour.SettleTicks + 20) { BlockedJump = (plan.From, plan.To); return Fail(idle, "stuck after a gap jump"); }
        if (!s.OnGround && !s.InWater && s.Velocity.Y < -0.2 && Safety.IsPlayerOverVoid(w, s.Position))
            return Fail(idle, "falling into the void");
        return Move(plan.Schedule.InputAt(plan.Schedule.JumpTick + 1, s));
    }

    /// <summary>Gives up on a jump before the takeoff: brake with sneak (vanilla edge clamping stops at the edge) and
    /// mark the move so the re-plan avoids it.</summary>
    private FollowerOutput Abort(MoveInput idle, ParkourPlan plan, string reason)
    {
        BlockedJump = (plan.From, plan.To);
        _pk.Reset();
        return Fail(idle with { Sneak = true }, "gap jump aborted: " + reason);
    }

    private static double Speed(PhysicsState s) => s.Velocity.HorizontalLength;

    /// <summary>
    /// The Trigger step once on its cell and still: button — click (again only after it popped), lever — click until it
    /// is in the wanted position (never back), plate — stand on it; then wait for the effects (learned lag + slack).
    /// Null while still walking/centering onto the cell (the normal movement code runs).
    /// </summary>
    private FollowerOutput? TriggerAction(IBlockAccess w, PhysicsState s, PathStep step, TriggerUse tu, MoveInput idle, double dist)
    {
        var pos = s.Position;
        bool onCell = (int)Math.Floor(pos.X) == step.X && (int)Math.Floor(pos.Z) == step.Z && Math.Abs(pos.Y - step.Floor) < 0.2
                      && (s.OnGround || s.OnLadder);
        if (!onCell || dist > 0.25 || Speed(s) > 0.06) return null;
        _noProgress = 0;
        int wait = _o.TriggerWaitTicks + tu.Link.LagTicks;
        var p = tu.Pos;
        if (tu.IsPlate)
        {
            if (++_triggerWait > _o.PlateWaitTicks + tu.Link.LagTicks) return TriggerFailed(idle, tu, "nothing happened on the plate");
            return Move(idle);
        }
        int id = w.GetBlock(p.X, p.Y, p.Z);
        if (id != tu.Link.TriggerId) return TriggerFailed(idle, tu, "it's gone");
        bool on = Openables.IsActuatorOn(id, w.GetMeta(p.X, p.Y, p.Z));
        bool click = tu.Link.Kind == TriggerKind.Lever ? on != tu.TurnOn : !on && (!_triggerClicked || _triggerWait > wait);
        if (click)
        {
            if (!Activation(p)) return TriggerFailed(idle, tu, "it didn't respond");
            _triggerClicked = true;
            _triggerWait = 0;
            return new FollowerOutput(FollowerStatus.NeedsAction, idle, new FollowerAction(FollowerActionKind.Activate, p));
        }
        if (++_triggerWait > wait * 2 + 10) return TriggerFailed(idle, tu, "its effect never showed");
        return Move(idle);
    }

    private FollowerOutput TriggerFailed(MoveInput idle, TriggerUse tu, string why)
    {
        FailedTrigger = tu.Pos;
        return Fail(idle, $"{Mechanisms.TriggerName(tu.Link.Kind)} at {tu.Link.Key}: {why}");
    }

    /// <summary>A reverted effect still between the body and the step's target (one behind us, or one we are already
    /// inside, doesn't stop us: a closing door never blocks a body that overlaps it).</summary>
    private static bool EffectAhead(IBlockAccess w, PhysicsState s, PathStep step, TriggerUse rq)
    {
        var box = s.BoundingBox;
        var t = step.Target;
        double body = Math.Sqrt(Math.Pow(t.X - s.Position.X, 2) + Math.Pow(t.Z - s.Position.Z, 2));
        foreach (var e in rq.Link.Effects)
        {
            if (e.IsIn(w, rq.TurnOn)) continue;
            var cell = AABB.Block(e.Pos.X, e.Pos.Y, e.Pos.Z);
            if (cell.Intersects(box)) continue;
            double d = Math.Sqrt(Math.Pow(t.X - (e.Pos.X + 0.5), 2) + Math.Pow(t.Z - (e.Pos.Z + 0.5), 2));
            if (d < body - 0.1) return true;
        }
        return false;
    }

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
