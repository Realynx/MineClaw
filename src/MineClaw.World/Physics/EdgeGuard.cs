namespace MineClaw.World;

/// <summary>Tuning for <see cref="EdgeGuard"/>.</summary>
public sealed record EdgeGuardOptions
{
    public bool Enabled { get; init; } = true;
    /// <summary>Largest fall (blocks) treated as safe. Vanilla fall damage is ceil(fall − 3), so 3 = never hurt.</summary>
    public double MaxSafeFall { get; init; } = 3.0;
    /// <summary>Horizontal margin around the hitbox scanned for deadly drops ("near an edge").</summary>
    public double EdgeMargin { get; init; } = 1.0;
    /// <summary>Force sneak while on ground near an edge, so vanilla edge clamping applies to all motion.</summary>
    public bool ForceSneakNearEdge { get; init; } = true;
    /// <summary>Simulation horizon for safety predictions.</summary>
    public int MaxSimTicks { get; init; } = 60;
    /// <summary>Largest fall a last-chance clutch block may still leave (taking some damage beats the void).</summary>
    public double MaxClutchFall { get; init; } = 12;
}

/// <summary>What <see cref="EdgeGuard.Filter"/> changed about the proposed input.</summary>
[Flags]
public enum GuardActions
{
    None = 0,
    /// <summary>Sneak forced on (on ground near an edge).</summary>
    ForcedSneak = 1,
    /// <summary>Sprint suppressed near an edge.</summary>
    StoppedSprint = 2,
    /// <summary>Jump cancelled: the landing was not verified safe.</summary>
    CancelledJump = 4,
    /// <summary>Horizontal input zeroed/reduced/reversed: it would have left support over the void.</summary>
    ClampedInput = 8,
    /// <summary>Airborne: steering with air control toward support.</summary>
    AirSteer = 16,
    /// <summary>No legal input avoids a deadly fall: a clutch block is needed now.</summary>
    Unrecoverable = 32,
}

public readonly record struct GuardDecision(MoveInput Input, GuardActions Actions, bool NearEdge)
{
    public bool Unrecoverable => (Actions & GuardActions.Unrecoverable) != 0;
    /// <summary>Anything beyond the routine forced sneak (worth logging).</summary>
    public bool Notable => (Actions & ~GuardActions.ForcedSneak) != 0;
}

/// <summary>A block to place right now to catch a fall: click <see cref="Aim"/>.Against on <see cref="Aim"/>.Face.</summary>
public readonly record struct ClutchPlan(BlockPos Target, PlaceAim Aim, bool Saves, double PredictedFall);

/// <summary>Intervention counters (thread-safe reads).</summary>
public sealed class EdgeGuardStats
{
    private long _sneak, _sprint, _jump, _clamp, _steer, _doomed, _clutchTry, _clutchSave;

    public long ForcedSneakTicks => Interlocked.Read(ref _sneak);
    public long SprintStops => Interlocked.Read(ref _sprint);
    public long JumpsCancelled => Interlocked.Read(ref _jump);
    public long InputsClamped => Interlocked.Read(ref _clamp);
    public long AirSteerTicks => Interlocked.Read(ref _steer);
    public long UnrecoverableTicks => Interlocked.Read(ref _doomed);
    public long ClutchAttempts => Interlocked.Read(ref _clutchTry);
    public long ClutchSaves => Interlocked.Read(ref _clutchSave);
    /// <summary>All interventions except routine forced-sneak ticks.</summary>
    public long Interventions => SprintStops + JumpsCancelled + InputsClamped + AirSteerTicks + UnrecoverableTicks + ClutchAttempts;

    internal void Count(GuardActions a)
    {
        if ((a & GuardActions.ForcedSneak) != 0) Interlocked.Increment(ref _sneak);
        if ((a & GuardActions.StoppedSprint) != 0) Interlocked.Increment(ref _sprint);
        if ((a & GuardActions.CancelledJump) != 0) Interlocked.Increment(ref _jump);
        if ((a & GuardActions.ClampedInput) != 0) Interlocked.Increment(ref _clamp);
        if ((a & GuardActions.AirSteer) != 0) Interlocked.Increment(ref _steer);
        if ((a & GuardActions.Unrecoverable) != 0) Interlocked.Increment(ref _doomed);
    }

    public void CountClutch(bool saved)
    {
        Interlocked.Increment(ref _clutchTry);
        if (saved) Interlocked.Increment(ref _clutchSave);
    }

    public override string ToString() =>
        $"sneak-ticks {ForcedSneakTicks}, sprint-stops {SprintStops}, jumps-cancelled {JumpsCancelled}, clamps {InputsClamped}, " +
        $"air-steer {AirSteerTicks}, doomed {UnrecoverableTicks}, clutch {ClutchSaves}/{ClutchAttempts}";
}

/// <summary>
/// Always-on void protection below every action. Each tick, <see cref="Filter"/> takes the proposed movement input and
/// returns one that provably keeps the player out of the void, using the exact same physics as the real tick:
/// <list type="number">
/// <item>Near a deadly drop (void, lava, or deeper than <see cref="EdgeGuardOptions.MaxSafeFall"/>) on ground: force sneak
/// (vanilla edge clamping then also applies to knockback) and stop sprinting.</item>
/// <item>Jumps only when the simulated landing is solid, non-void and within the safe fall.</item>
/// <item>Accept the input only if after one tick of it the "brake" (no input, sneak) settles safely without the hitbox
/// ever being entirely over the void; otherwise try the sneak version, single axes, brake, reverse, then air steering.
/// By induction the brake is always available unless something external (server velocity, block changes) intervenes.</item>
/// <item>Airborne after knockback: steer with legal air control toward the last support; if nothing works the decision
/// is <see cref="GuardDecision.Unrecoverable"/> and the caller should place a block from <see cref="PlanClutch"/>.</item>
/// </list>
/// Not thread-safe: call from the tick thread only.
/// </summary>
public sealed class EdgeGuard
{
    private Vec3d? _clutchTarget;

    public EdgeGuard(EdgeGuardOptions? options = null)
    {
        Options = options ?? new EdgeGuardOptions();
    }

    public EdgeGuardOptions Options { get; set; }
    public EdgeGuardStats Stats { get; } = new();

    /// <summary>Top-center of the block that last held the player up (steering target after knockback).</summary>
    public Vec3d? LastSupport { get; private set; }

    /// <summary>Forget support history (teleport / respawn).</summary>
    public void Reset()
    {
        LastSupport = null;
        _clutchTarget = null;
    }

    /// <summary>Tell the guard a clutch block was placed (it then steers onto it).</summary>
    public void NoteClutchPlaced(BlockPos target) => _clutchTarget = new Vec3d(target.X + 0.5, target.Y + 1, target.Z + 0.5);

    // ------------------------------------------------------------------ queries

    /// <summary>Any column within <see cref="EdgeGuardOptions.EdgeMargin"/> of the hitbox is a deadly drop at feet level.</summary>
    public bool IsNearEdge(IBlockAccess w, PhysicsState s) => IsNearEdge(w, s.Position, Options.EdgeMargin, Options.MaxSafeFall);

    public static bool IsNearEdge(IBlockAccess w, Vec3d feet, double margin = 1.0, double maxFall = 3.0)
    {
        var bb = AABB.ForPlayer(feet);
        int y = (int)Math.Floor(feet.Y + 1e-6);
        int x0 = (int)Math.Floor(bb.MinX - margin), x1 = (int)Math.Ceiling(bb.MaxX + margin) - 1;
        int z0 = (int)Math.Floor(bb.MinZ - margin), z1 = (int)Math.Ceiling(bb.MaxZ + margin) - 1;
        for (int x = x0; x <= x1; x++)
        for (int z = z0; z <= z1; z++)
            if (Safety.IsDeadlyColumn(w, x, y, z, maxFall)) return true;
        return false;
    }

    /// <summary>One tick of <paramref name="input"/> followed by braking settles safely (never over the void).</summary>
    public bool IsViable(IBlockAccess w, PhysicsState s, MoveInput input) =>
        Simulate(w, s, ThenBrake(input), strict: true, Options.MaxSafeFall, out _);

    /// <summary>Holding <paramref name="input"/> (jump on the first tick only) until landing lands safely.</summary>
    public bool LandsSafely(IBlockAccess w, PhysicsState s, MoveInput input) =>
        Simulate(w, s, HeldUntilLanding(input), strict: true, Options.MaxSafeFall, out _);

    // ------------------------------------------------------------------ the filter

    /// <summary>
    /// Returns the input to actually apply this tick. <paramref name="allowDrop"/> marks an intentional, planned drop
    /// to a lower floor (path step): forced sneak is skipped only if the unsneaked step is verified to land safely.
    /// </summary>
    public GuardDecision Filter(IBlockAccess w, PhysicsState s, MoveInput proposed, bool allowDrop = false)
    {
        if (!Options.Enabled) return new GuardDecision(proposed, GuardActions.None, false);
        if (!w.IsLoaded((int)Math.Floor(s.Position.X), (int)Math.Floor(s.Position.Z)))
            return new GuardDecision(proposed, GuardActions.None, false); // physics freezes anyway
        UpdateLastSupport(w, s);
        bool near = IsNearEdge(w, s);
        var acts = GuardActions.None;
        var input = proposed;

        if (near && input.Sprint)
        {
            input = input with { Sprint = false };
            acts |= GuardActions.StoppedSprint;
        }
        if (near && s.OnGround && !input.Sneak && Options.ForceSneakNearEdge)
        {
            bool verifiedDrop = allowDrop && Simulate(w, s, ThenBrake(input), true, Options.MaxSafeFall, out _);
            if (!verifiedDrop)
            {
                input = input with { Sneak = true };
                acts |= GuardActions.ForcedSneak;
            }
        }
        if (input.Jump && s.OnGround && !s.InWater && !s.InLava && !LandsSafely(w, s, input))
        {
            input = input with { Jump = false };
            acts |= GuardActions.CancelledJump;
        }

        if (IsViable(w, s, input)) return Done(input, acts, near);

        foreach (var c in Fallbacks(s, input))
            if (IsViable(w, s, c)) return Done(c, acts | GuardActions.ClampedInput, near);

        var steers = SteerPolicies(w, s, input);
        foreach (var p in steers)
            if (Simulate(w, s, p, true, Options.MaxSafeFall, out _)) return Done(p(0, s), acts | GuardActions.AirSteer, near);
        // Already committed to leaving support (e.g. knocked out over the void): accept any path that settles safely.
        foreach (var p in steers)
            if (Simulate(w, s, p, false, Options.MaxSafeFall, out _)) return Done(p(0, s), acts | GuardActions.AirSteer, near);

        var best = steers.Count > 0 ? steers[0](0, s) : Brake(s) with { Yaw = input.Yaw, Pitch = input.Pitch };
        return Done(best, acts | GuardActions.AirSteer | GuardActions.Unrecoverable, near);
    }

    private GuardDecision Done(MoveInput input, GuardActions acts, bool near)
    {
        Stats.Count(acts);
        return new GuardDecision(input, acts, near);
    }

    private void UpdateLastSupport(IBlockAccess w, PhysicsState s)
    {
        if (!s.OnGround) return;
        _clutchTarget = null;
        double best = double.MaxValue;
        foreach (var b in Safety.SupportBlocks(w, s.Position))
        {
            double dx = b.X + 0.5 - s.Position.X, dz = b.Z + 0.5 - s.Position.Z;
            double d = dx * dx + dz * dz;
            if (d >= best) continue;
            best = d;
            LastSupport = new Vec3d(b.X + 0.5, b.Y + BlockShapes.CollisionTop(w, b.X, b.Y, b.Z), b.Z + 0.5);
        }
    }

    private static MoveInput Brake(PhysicsState s) => new(Yaw: s.Yaw, Pitch: s.Pitch, Sneak: true);

    private static IEnumerable<MoveInput> Fallbacks(PhysicsState s, MoveInput input)
    {
        var b = input with { Jump = false, Sprint = false, Sneak = true };
        yield return b;
        if (b.Forward != 0 && b.Strafe != 0)
        {
            yield return b with { Strafe = 0 };
            yield return b with { Forward = 0 };
        }
        yield return b with { Forward = 0, Strafe = 0 };
        if (b.Forward != 0 || b.Strafe != 0) yield return b with { Forward = -b.Forward, Strafe = -b.Strafe };
    }

    private List<Func<int, PhysicsState, MoveInput>> SteerPolicies(IBlockAccess w, PhysicsState s, MoveInput proposed)
    {
        var list = new List<Func<int, PhysicsState, MoveInput>>(4);
        if (_clutchTarget is { } ct) list.Add(Homing(ct));
        if (LastSupport is { } ls) list.Add(Homing(ls));
        if (NearestSupport(w, s) is { } ns && (LastSupport is not { } l || l.DistanceSquaredTo(ns) > 0.01)) list.Add(Homing(ns));
        list.Add(HeldAir(proposed));
        return list;
    }

    /// <summary>Top-center of the nearest column within 3 blocks that is a safe landing from feet level.</summary>
    private Vec3d? NearestSupport(IBlockAccess w, PhysicsState s)
    {
        int y = (int)Math.Floor(s.Position.Y + 1e-6);
        int cx = (int)Math.Floor(s.Position.X), cz = (int)Math.Floor(s.Position.Z);
        Vec3d? best = null;
        double bestD = double.MaxValue;
        for (int x = cx - 3; x <= cx + 3; x++)
        for (int z = cz - 3; z <= cz + 3; z++)
        {
            if (Safety.IsDeadlyColumn(w, x, y, z, Options.MaxSafeFall) || BlockShapes.HasCollision(w, x, y, z)) continue;
            int top = Safety.SupportBelow(w, x, y - 1, z);
            if (top < 0) continue;
            double dx = x + 0.5 - s.Position.X, dz = z + 0.5 - s.Position.Z;
            double d = dx * dx + dz * dz;
            if (d < bestD) { bestD = d; best = new Vec3d(x + 0.5, top + BlockShapes.CollisionTop(w, x, top, z), z + 0.5); }
        }
        return best;
    }

    private static Func<int, PhysicsState, MoveInput> ThenBrake(MoveInput first) =>
        (t, s) => t == 0 ? first : Brake(s);

    /// <summary>The proposed input with full air control (no sneak in the air, sneak on the ground), brake once landed.</summary>
    private static Func<int, PhysicsState, MoveInput> HeldAir(MoveInput input) => (t, s) =>
        t > 0 && s.OnGround ? Brake(s) : input with { Jump = t == 0 && input.Jump, Sneak = s.OnGround, Sprint = false };

    private static Func<int, PhysicsState, MoveInput> HeldUntilLanding(MoveInput input) =>
        (t, s) => t == 0 ? input : s.OnGround ? Brake(s) : input with { Jump = false };

    /// <summary>Full air control toward <paramref name="target"/> (sneak only while on ground), brake once landed.</summary>
    private static Func<int, PhysicsState, MoveInput> Homing(Vec3d target) => (t, s) =>
    {
        if (t > 0 && s.OnGround) return Brake(s);
        double dx = target.X - s.Position.X, dz = target.Z - s.Position.Z;
        if (dx * dx + dz * dz < 0.15 * 0.15) return new MoveInput(Yaw: s.Yaw, Pitch: s.Pitch, Sneak: s.OnGround);
        float yaw = PlayerPhysics.YawPitchTo(s.Position, target with { Y = s.Position.Y }).Yaw;
        return new MoveInput(Forward: 1, Yaw: yaw, Pitch: s.Pitch, Sneak: s.OnGround);
    };

    /// <summary>
    /// Runs <paramref name="policy"/> on a copy of the state until it settles (on ground and slow, on a ladder, or in
    /// water that is not over the void). Fails on lava/fire contact (unless already touching), a landing fall above
    /// <paramref name="maxFall"/>, an unloaded chunk, or — when <paramref name="strict"/> — any tick with the hitbox
    /// entirely over the void.
    /// </summary>
    private bool Simulate(IBlockAccess w, PhysicsState start, Func<int, PhysicsState, MoveInput> policy, bool strict,
        double maxFall, out double fall)
    {
        var s = start.Clone();
        bool hazardBefore = s.InLava || Safety.TouchesHazard(w, s.BoundingBox);
        double peak = s.Position.Y + (s.OnGround ? 0 : Math.Max(0, s.FallDistance));
        bool wasGround = s.OnGround;
        fall = 0;
        for (int t = 0; t < Options.MaxSimTicks; t++)
        {
            PlayerPhysics.Tick(w, s, policy(t, s));
            if (s.Frozen || s.Position.Y < 1) return false;
            if (!hazardBefore && (s.InLava || Safety.TouchesHazard(w, s.BoundingBox))) return false;
            bool overVoid = Safety.IsPlayerOverVoid(w, s.Position);
            if (strict && overVoid) return false;
            if (s.OnGround)
            {
                if (!wasGround)
                {
                    fall = Math.Max(fall, peak - s.Position.Y);
                    if (fall > maxFall + 1e-6) return false;
                }
                peak = s.Position.Y;
                wasGround = true;
                if (s.Velocity.HorizontalLength < 0.02) return true;
            }
            else
            {
                wasGround = false;
                if (s.Position.Y > peak) peak = s.Position.Y;
                if (!overVoid && (s.OnLadder || (s.InWater && !s.InLava))) return true;
            }
        }
        return false;
    }

    // ------------------------------------------------------------------ last-chance clutch

    /// <summary>
    /// Picks a block to place right now (reachable from the current eye, not overlapping the hitbox, against an existing
    /// solid face) so that steering lands on it. Prefers placements that save the player with the smallest fall; if none
    /// saves alone, returns the placement furthest along the predicted trajectory (Saves = false) so the next tick can
    /// chain another block from it. Null when nothing at all can be placed.
    /// </summary>
    public ClutchPlan? PlanClutch(IBlockAccess w, PhysicsState s, double reach, int blockId = 1)
    {
        var box = s.BoundingBox;
        var eye = s.EyePosition;
        var steer = _clutchTarget is { } ct ? Homing(ct) : LastSupport is { } ls ? Homing(ls) : ThenBrake(Brake(s));

        var traj = new List<Vec3d> { s.Position };
        var sim = s.Clone();
        for (int t = 0; t < 24 && sim.Position.Y > 1; t++)
        {
            PlayerPhysics.Tick(w, sim, steer(t, sim));
            if (sim.Frozen) break;
            traj.Add(sim.Position);
            if (sim.OnGround) break;
        }

        int floorLevel = (int)Math.Floor(s.Position.Y + 1e-6) - 1;
        var cands = new List<(BlockPos Cell, int TrajIndex)>();
        var seen = new HashSet<BlockPos>();
        for (int i = 0; i < traj.Count; i++)
        {
            var p = traj[i];
            var b = AABB.ForPlayer(p);
            int ly = Math.Min((int)Math.Floor(p.Y + 1e-6) - 1, floorLevel);
            for (int x = (int)Math.Floor(b.MinX); x <= (int)Math.Floor(b.MaxX - 1e-9); x++)
            for (int z = (int)Math.Floor(b.MinZ); z <= (int)Math.Floor(b.MaxZ - 1e-9); z++)
            for (int y = ly; y >= ly - 1 && y >= 0; y--)
            {
                var c = new BlockPos(x, y, z);
                if (seen.Add(c)) cands.Add((c, i));
            }
        }

        ClutchPlan? bestSave = null;
        (ClutchPlan Plan, int Index)? furthest = null;
        foreach (var (cell, idx) in cands)
        {
            if (!w.IsLoaded(cell.X, cell.Z) || !Aiming.IsReplaceable(w, w.GetBlock(cell.X, cell.Y, cell.Z))) continue;
            if (Aiming.FindPlaceAim(w, eye, cell, reach, box) is not { } aim) continue;
            var overlay = new BlockOverlay(w).Set(cell, blockId);
            var top = new Vec3d(cell.X + 0.5, cell.Y + 1, cell.Z + 0.5);
            bool saves = Simulate(overlay, s, Homing(top), false, Options.MaxClutchFall, out double fall);
            if (!saves && Simulate(overlay, s, steer, false, Options.MaxClutchFall, out double fall2)) { saves = true; fall = fall2; }
            if (saves)
            {
                if (bestSave is null || fall < bestSave.Value.PredictedFall - 1e-6)
                    bestSave = new ClutchPlan(cell, aim, true, fall);
            }
            else if (furthest is null || idx > furthest.Value.Index)
                furthest = (new ClutchPlan(cell, aim, false, double.PositiveInfinity), idx);
        }
        return bestSave ?? furthest?.Plan;
    }

    // ------------------------------------------------------------------ interior

    /// <summary>A standable cell within <paramref name="radius"/> at the current level that is further from every edge
    /// (edge distance ≥ 2 and better than here) — where to retreat when mobs are close and we are near the void.</summary>
    public BlockPos? FindInterior(IBlockAccess w, PhysicsState s, int radius = 4)
    {
        int y = (int)Math.Floor(s.Position.Y + 1e-6);
        int cx = (int)Math.Floor(s.Position.X), cz = (int)Math.Floor(s.Position.Z);
        int here = Safety.EdgeDistance(w, cx, y, cz, 3, Options.MaxSafeFall);
        BlockPos? best = null;
        double bestScore = double.MinValue;
        for (int x = cx - radius; x <= cx + radius; x++)
        for (int z = cz - radius; z <= cz + radius; z++)
        {
            if (!Safety.IsStandable(w, x, y, z)) continue;
            int ed = Safety.EdgeDistance(w, x, y, z, 3, Options.MaxSafeFall);
            if (ed < 2 || ed <= here) continue;
            double d = Math.Sqrt((x + 0.5 - s.Position.X) * (x + 0.5 - s.Position.X) + (z + 0.5 - s.Position.Z) * (z + 0.5 - s.Position.Z));
            if (d > radius) continue;
            double score = ed * 10 - d;
            if (score > bestScore) { bestScore = score; best = new BlockPos(x, y, z); }
        }
        return best;
    }
}
