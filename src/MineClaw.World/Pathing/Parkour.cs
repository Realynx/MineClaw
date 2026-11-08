using System.Buffers;
using MineClaw.Data;

namespace MineClaw.World;

/// <summary>
/// The deterministic input schedule of one gap jump, used verbatim by the planner's simulation, the follower and the
/// edge guard's re-simulation (one source of truth). Tick k counts from the run-up start (standing still on the
/// takeoff cell centre): k &lt; <see cref="JumpTick"/> run (forward, sprint if planned), k = JumpTick also jump, then hold
/// forward (+sprint) through the flight; once back on the ground: brake and sneak-home onto the landing cell centre
/// (sneaking so vanilla edge clamping keeps the body on a narrow roof).
/// </summary>
public readonly record struct ParkourSchedule(float Yaw, bool Sprint, int JumpTick, double TargetX, double TargetZ)
{
    public MoveInput InputAt(int k, PhysicsState s)
    {
        if (k <= JumpTick || !s.OnGround)
            return new MoveInput(Forward: 1, Jump: k == JumpTick, Sprint: Sprint, Yaw: Yaw);
        double dx = TargetX - s.Position.X, dz = TargetZ - s.Position.Z;
        double d = Math.Sqrt(dx * dx + dz * dz);
        double speed = s.Velocity.HorizontalLength;
        if (d < 0.08 || d < speed * 2.5) return new MoveInput(Sneak: true, Yaw: Yaw);
        float yaw = (float)(Math.Atan2(-dx, dz) * 180 / Math.PI);
        return new MoveInput(Forward: 1, Sneak: true, Yaw: yaw);
    }
}

/// <summary>
/// A validated gap jump from foot cell <see cref="From"/> (run-up starts standing still on its centre) to
/// <see cref="To"/>. <see cref="Nominal"/> holds the simulated feet position at the start of every tick up to landing;
/// <see cref="Margin"/> is the worst landing margin over the perturbation set (see <see cref="Parkour.SupportMargin"/>).
/// <see cref="OverVoid"/>: a miss would fall into the void / lava / further than the safe fall.
/// </summary>
public sealed record ParkourPlan(
    BlockPos From,
    BlockPos To,
    double FromFloor,
    double ToFloor,
    ParkourSchedule Schedule,
    int LandTick,
    double Distance,
    double Margin,
    bool OverVoid,
    IReadOnlyList<Vec3d> Nominal,
    double MaxFall = 4.3)
{
    public MoveKind Kind => Schedule.Sprint ? MoveKind.SprintJump : MoveKind.JumpGap;
    public Vec3d Start => new(From.X + 0.5, FromFloor, From.Z + 0.5);
    public Vec3d Landing => new(To.X + 0.5, ToFloor, To.Z + 0.5);
    /// <summary>Margin a re-simulation must still show for the jump to stay committed.</summary>
    public double RequiredMargin => OverVoid ? Parkour.VoidMargin : Parkour.GroundMargin;

    public string Describe() => $"{(Schedule.Sprint ? "sprint-jump" : "gap-jump")} {Distance:0.0} blocks";
}

/// <summary>The follower is executing tick <see cref="Tick"/> of <see cref="Plan"/> (run-up or flight): the edge guard
/// lets the planned input through while its re-simulation still lands safely with margin.</summary>
public readonly record struct ParkourCommit(ParkourPlan Plan, int Tick);

/// <summary>Result of one simulated execution. <see cref="Track"/> (when recorded) = feet position at the start of each tick.</summary>
public readonly record struct ParkourOutcome(bool Ok, string? Reason, double Margin, int LandTick, double Distance, double Peak,
    List<Vec3d>? Track)
{
    public static ParkourOutcome Fail(string reason) => new(false, reason, double.NegativeInfinity, -1, 0, 0, null);
}

/// <summary>
/// Gap-jump ("parkour") physics validation. b1.8.1 facts (Bukkit mc-dev 1.8 sources, which share EntityLiving/
/// EntityHuman with the client): jump motY = 0.42 (EntityLiving.S()), and when sprinting (Entity flag bit 3,
/// <c>at()</c>) motX -= sin(yaw)·0.2, motZ += cos(yaw)·0.2 — the sprint-jump boost exists in b1.8; ground/air factors
/// 0.1/0.02 (+30 % sprinting, EntityHuman.s()); exhaustion 0.8 per sprint-jump, 0.2 per jump. Client-only (MCP
/// EntityPlayerSP, not in mc-dev): sprint needs food &gt; 6 and forward ≥ 0.8 and stops on horizontal collision.
/// <para>Validation simulates the whole schedule against the real world model from a standing start on the takeoff
/// cell centre, then again under perturbations (start ±0.1 along/across, jump tick ±1; void jumps: ±0.15, ±1 tick,
/// yaw ±2° and combined worst cases, plus clutch coverage on every flight tick) and requires every run to land on the target cell and settle there.</para>
/// </summary>
public static class Parkour
{
    /// <summary>Min nominal landing margin over solid ground (the hitbox centre may be up to 0.15 short of the edge).</summary>
    public const double GroundMargin = -0.15;
    /// <summary>Min landing margin when a miss means the void: the hitbox centre over the landing block.</summary>
    public const double VoidMargin = 0.05;
    /// <summary>Any perturbed run must still land with at least this margin (≥ 0.05 hitbox overlap).</summary>
    public const double PerturbedMargin = -0.25;
    public const double Jitter = 0.1, VoidJitter = 0.15;
    public const float VoidYawJitter = 2f;
    public const int MaxRunUpTicks = 30, MaxFlightTicks = 40, SettleTicks = 40;
    /// <summary>Flat run-up cells ahead of the start node the planner considers.</summary>
    public const int MaxRunCells = 3;
    /// <summary>Largest rise a gap jump may land on (jump apex ≈ 1.25).</summary>
    public const double MaxRise = 1.0;

    // ------------------------------------------------------------------ simulation

    /// <summary>Standing still on the ground at <paramref name="feet"/>.</summary>
    public static PhysicsState StandingAt(Vec3d feet, float yaw)
    {
        var s = new PhysicsState();
        s.Teleport(feet, yaw, 0);
        s.OnGround = true;
        s.Velocity = new Vec3d(0, -PlayerPhysics.Gravity * PlayerPhysics.VerticalDrag, 0); // resting: last tick's gravity
        return s;
    }

    /// <summary>
    /// Runs <paramref name="sch"/> from tick <paramref name="fromTick"/> on a copy of <paramref name="start"/>. Fails on: leaving
    /// the ground, a horizontal collision or lost sprint during the run-up; no ground at the jump tick; a horizontal
    /// collision in flight; hazard/water/void contact; a first contact not at the target floor (−0.5 for stair halves);
    /// a fall above <paramref name="maxFall"/>; sliding off the landing; not settling on <paramref name="to"/>'s centre.
    /// </summary>
    public static ParkourOutcome Simulate(IBlockAccess w, PhysicsState start, ParkourSchedule sch, int fromTick, BlockPos to,
        double toFloor, double maxFall, bool record = false)
        => SimulateCore(w, start, sch, fromTick, to, toFloor, maxFall, record ? new List<Vec3d>(sch.JumpTick + 24) : null);

    private static ParkourOutcome SimulateCore(IBlockAccess w, PhysicsState start, ParkourSchedule sch, int fromTick, BlockPos to,
        double toFloor, double maxFall, List<Vec3d>? track)
        => SimulateOwned(w, start.Clone(), sch, fromTick, to, toFloor, maxFall, track);

    private static ParkourOutcome SimulateOwned(IBlockAccess w, PhysicsState state, ParkourSchedule sch, int fromTick, BlockPos to,
        double toFloor, double maxFall, List<Vec3d>? track)
    {
        var run = new SimRun(w, state, sch, fromTick, track);
        int end = Math.Max(fromTick, sch.JumpTick) + MaxFlightTicks + SettleTicks;
        for (int k = fromTick; k < end; k++)
            if (SimTick(w, ref run, sch, k, to, toFloor, maxFall) is { } outcome) return outcome;
        return ParkourOutcome.Fail("timeout");
    }

    /// <summary>The state of one <see cref="Simulate"/> run between its ticks.</summary>
    private struct SimRun
    {
        public readonly PhysicsState S;
        public readonly List<Vec3d>? Track;
        public readonly bool HazardBefore;
        public bool Airborne;
        public double Peak;
        /// <summary>The touchdown tick (−1 still before it).</summary>
        public int Contact;
        public double Margin;
        public Vec3d Takeoff;
        public double Distance;

        public SimRun(IBlockAccess w, PhysicsState s, ParkourSchedule sch, int fromTick, List<Vec3d>? track)
        {
            S = s;
            Track = track;
            HazardBefore = s.InLava || Safety.TouchesHazard(w, s.BoundingBox);
            Airborne = fromTick > sch.JumpTick && !s.OnGround;
            Peak = s.Position.Y + (s.OnGround ? 0 : Math.Max(0, s.FallDistance));
            Contact = fromTick > sch.JumpTick && s.OnGround ? fromTick : -1;
            Margin = Contact >= 0 ? SupportMargin(w, s.Position, sch.Yaw) : double.NegativeInfinity;
            Takeoff = s.Position;
            Distance = 0;
        }
    }

    /// <summary>One tick of the run: the outcome once decided, null to go on.</summary>
    private static ParkourOutcome? SimTick(IBlockAccess w, ref SimRun run, ParkourSchedule sch, int k, BlockPos to, double toFloor, double maxFall)
    {
        var s = run.S;
        run.Track?.Add(s.Position);
        if (k == sch.JumpTick)
        {
            if (!s.OnGround) return ParkourOutcome.Fail("no ground under the takeoff");
            run.Takeoff = s.Position;
        }
        // After the touchdown the simulation just brakes (sneak: vanilla edge clamping holds the body on a narrow
        // roof); the follower then sneak-homes onto the cell centre, a plain walk on the same floor.
        var input = run.Contact >= 0 ? new MoveInput(Sneak: true, Yaw: sch.Yaw) : sch.InputAt(k, s);
        PlayerPhysics.Tick(w, s, input);
        if (Broken(w, run) is { } why) return ParkourOutcome.Fail(why);
        if (k < sch.JumpTick) return RunUpTick(s, sch);
        if (run.Contact < 0) return FlightTick(w, ref run, sch, k, toFloor, maxFall);
        return SettleTick(run, k, to, toFloor);
    }

    /// <summary>What ends any run at once: an unloaded chunk, the void, a hazard touched (unless started in one), water.</summary>
    private static string? Broken(IBlockAccess w, in SimRun run)
    {
        var s = run.S;
        if (s.Frozen) return "unloaded chunk";
        if (s.Position.Y < 1) return "void";
        if (!run.HazardBefore && (s.InLava || Safety.TouchesHazard(w, s.BoundingBox))) return "hazard";
        if (s.InWater) return "water";
        return null;
    }

    private static ParkourOutcome? RunUpTick(PhysicsState s, ParkourSchedule sch)
    {
        if (!s.OnGround) return ParkourOutcome.Fail("ran off the edge before the takeoff");
        if (s.CollidedHorizontally) return ParkourOutcome.Fail("run-up blocked");
        if (sch.Sprint && !s.Sprinting) return ParkourOutcome.Fail("sprint lost");
        return null;
    }

    private static ParkourOutcome? FlightTick(IBlockAccess w, ref SimRun run, ParkourSchedule sch, int k, double toFloor, double maxFall)
    {
        var s = run.S;
        if (s.Position.Y > run.Peak) run.Peak = s.Position.Y;
        if (s.CollidedHorizontally) return ParkourOutcome.Fail("hit a wall in flight");
        if (!s.OnGround)
        {
            run.Airborne = true;
            return StillFlying(s, sch, k, toFloor);
        }
        if (!run.Airborne) return ParkourOutcome.Fail("no jump (head bump)");
        return FirstContact(w, ref run, sch, k, toFloor, maxFall);
    }

    private static ParkourOutcome? StillFlying(PhysicsState s, ParkourSchedule sch, int k, double toFloor)
    {
        if (k - sch.JumpTick > MaxFlightTicks) return ParkourOutcome.Fail("too long in the air");
        // Falling below the target floor: it can no longer be landed on (fail fast instead of falling on).
        if (s.Velocity.Y < 0 && s.Position.Y < toFloor - 0.6) return ParkourOutcome.Fail("fell short");
        return null;
    }

    /// <summary>The first ground contact after the flight: within the fall budget, at the target floor (−0.5 for a stair half).</summary>
    private static ParkourOutcome? FirstContact(IBlockAccess w, ref SimRun run, ParkourSchedule sch, int k, double toFloor, double maxFall)
    {
        var s = run.S;
        run.Contact = k;
        double fall = run.Peak - s.Position.Y;
        if (fall > maxFall + 1e-6) return ParkourOutcome.Fail($"fall {fall:0.0}");
        if (s.Position.Y < toFloor - 0.5 - 1e-3 || s.Position.Y > toFloor + 1e-3)
            return ParkourOutcome.Fail($"landed at y {s.Position.Y:0.##} instead of {toFloor:0.##}");
        run.Margin = SupportMargin(w, s.Position, sch.Yaw);
        double dx = s.Position.X - run.Takeoff.X, dz = s.Position.Z - run.Takeoff.Z;
        run.Distance = Math.Sqrt(dx * dx + dz * dz);
        return null;
    }

    /// <summary>Landed: brake to rest on or near the target cell at the target floor (or its stair half).</summary>
    private static ParkourOutcome? SettleTick(in SimRun run, int k, BlockPos to, double toFloor)
    {
        var s = run.S;
        if (!s.OnGround) return ParkourOutcome.Fail("slid off the landing");
        if (s.Velocity.HorizontalLength < 0.003) return AtRest(run, to, toFloor);
        if (k - run.Contact > SettleTicks) return ParkourOutcome.Fail("did not settle on the landing");
        return null;
    }

    private static ParkourOutcome AtRest(in SimRun run, BlockPos to, double toFloor)
    {
        var p = run.S.Position;
        int fx = (int)Math.Floor(p.X), fz = (int)Math.Floor(p.Z);
        // Overshooting onto the same roof is fine (the follower sneak-walks back); further means another place.
        if (Math.Abs(fx - to.X) > 2 || Math.Abs(fz - to.Z) > 2) return ParkourOutcome.Fail("came to rest off the landing");
        if (p.Y < toFloor - 0.5 - 1e-3 || p.Y > toFloor + 1e-3) return ParkourOutcome.Fail("came to rest at the wrong height");
        run.Track?.Add(p);
        return new ParkourOutcome(true, null, run.Margin, run.Contact, run.Distance, run.Peak, run.Track);
    }

    [ThreadStatic] private static List<AABB>? _supportBoxes;

    /// <summary>
    /// How far the contact point could have been shorter (positive: the hitbox centre is over support with that much to
    /// spare backwards along the flight direction, capped at 0.6) or how far it is short of the support (negative, the
    /// centre overhangs: −0.3 = only the hitbox front edge touches).
    /// </summary>
    public static double SupportMargin(IBlockAccess w, Vec3d feet, float yaw)
    {
        double rad = yaw * Math.PI / 180;
        double fx = -Math.Sin(rad), fz = Math.Cos(rad);
        var boxes = _supportBoxes ??= new List<AABB>(8);
        var area = new AABB(feet.X - 1.0, feet.Y - 0.06, feet.Z - 1.0, feet.X + 1.0, feet.Y - 0.0005, feet.Z + 1.0);
        BlockShapes.GetCollidingBoxes(w, area, boxes);
        for (int i = boxes.Count - 1; i >= 0; i--)
            if (boxes[i].MaxY < feet.Y - 0.06 || boxes[i].MaxY > feet.Y + 1e-6) boxes.RemoveAt(i);
        bool Supported(double t)
        {
            double x = feet.X + fx * t, z = feet.Z + fz * t;
            foreach (var b in boxes)
                if (x >= b.MinX && x <= b.MaxX && z >= b.MinZ && z <= b.MaxZ) return true;
            return false;
        }
        const double step = 0.02;
        if (Supported(0))
        {
            double t = 0;
            while (t < 0.6 && Supported(-(t + step))) t += step;
            return t;
        }
        for (double t = step; t <= 0.6 + 1e-9; t += step)
            if (Supported(t)) return -t;
        return -0.6;
    }

    /// <summary>Nominal re-simulation from the current state for a committed jump (edge guard, follower).</summary>
    public static ParkourOutcome Check(IBlockAccess w, PhysicsState s, ParkourCommit c, bool record = false) =>
        Simulate(w, s, c.Plan.Schedule, c.Tick, c.Plan.To, c.Plan.ToFloor, c.Plan.MaxFall, record);

    // ------------------------------------------------------------------ planning

    /// <summary>
    /// Finds a robust schedule from <paramref name="from"/> to <paramref name="to"/>, or null. Tries the latest takeoff ticks
    /// first (longest jump) that still leave one tick of slack. <paramref name="allowVoid"/> false refuses jumps whose miss
    /// would be deadly; when true they must pass the stricter margin and perturbation set. <paramref name="maxFall"/> bounds
    /// the landing fall from the apex; <paramref name="safeMissFall"/> is the drop (from the lower floor) a miss may take
    /// before it counts as deadly.
    /// </summary>
    public static ParkourPlan? Plan(IBlockAccess w, BlockPos from, double fromFloor, BlockPos to, double toFloor, bool sprint,
        bool allowVoid, double maxFall, double safeMissFall, out string? why)
    {
        why = null;
        // Every simulation below reads the same few hundred blocks: snapshot them once (≈5x faster physics ticks).
        using var snapshot = new RegionSnapshot(w, Math.Min(from.X, to.X) - 3, Math.Max(from.X, to.X) + 3,
            (int)Math.Floor(Math.Min(fromFloor, toFloor)) - 6, (int)Math.Floor(Math.Max(fromFloor, toFloor)) + 4,
            Math.Min(from.Z, to.Z) - 3, Math.Max(from.Z, to.Z) + 3);
        w = snapshot;
        var startPos = new Vec3d(from.X + 0.5, fromFloor, from.Z + 0.5);
        double tx = to.X + 0.5, tz = to.Z + 0.5;
        float yaw = (float)(Math.Atan2(-(tx - startPos.X), tz - startPos.Z) * 180 / Math.PI);
        var spec = new JumpSpec(from, fromFloor, to, toFloor, startPos, yaw, sprint, tx, tz, allowVoid, maxFall, safeMissFall);

        // Ground run without jumping: the last tick that still starts on the ground is the latest possible takeoff.
        var run = new List<PhysicsState>(MaxRunUpTicks + 1);
        int lastGround = LastGroundTick(w, startPos, yaw, sprint, run);
        if (lastGround < 2) { why = "no run-up"; return null; }

        // Nominal runs for every candidate takeoff tick (latest = longest jump); validate them closest landing first.
        var nominals = Nominals(w, run, lastGround, spec, ref why);
        try
        {
            foreach (var nominal in nominals.OrderBy(n => n.Err))
            {
                var verdict = Validate(w, spec, run, nominal, ref why, out var plan);
                if (verdict == Verdict.Accept) return plan;
                if (verdict == Verdict.Abort) return null;
            }
        }
        finally { foreach (var nominal in nominals) ReturnTrack(nominal.O.Track!); }
        why ??= "no takeoff tick lands";
        return null;
    }

    /// <summary>What a plan is asked for: the cells and floors, the standing start, the run's yaw and sprint, the landing
    /// target, and the fall limits.</summary>
    private readonly record struct JumpSpec(BlockPos From, double FromFloor, BlockPos To, double ToFloor, Vec3d Start, float Yaw,
        bool Sprint, double Tx, double Tz, bool AllowVoid, double MaxFall, double SafeMissFall);

    private readonly record struct Nominal(int T, ParkourSchedule Sch, ParkourOutcome O, double Err);

    private enum Verdict { Next, Accept, Abort }

    [ThreadStatic] private static Stack<List<Vec3d>>? _planningTracks;

    private static List<Vec3d> RentTrack() => _planningTracks is { Count: > 0 } tracks ? tracks.Pop() : new List<Vec3d>(80);

    private static void ReturnTrack(List<Vec3d> track)
    {
        track.Clear();
        var tracks = _planningTracks ??= new Stack<List<Vec3d>>(4);
        if (tracks.Count < 8) tracks.Push(track);
    }

    /// <summary>Each candidate's flight starts at its takeoff tick; scratch tracks stay private to this planning call.
    /// Only an accepted plan receives an owned copy including the shared run-up.</summary>
    private static List<Nominal> Nominals(IBlockAccess w, List<PhysicsState> run, int lastGround, JumpSpec spec, ref string? why)
    {
        var nominals = new List<Nominal>();
        for (int t = lastGround - 1; t >= Math.Max(1, lastGround - 4); t--)
        {
            var sch = new ParkourSchedule(spec.Yaw, spec.Sprint, t, spec.Tx, spec.Tz);
            // The run-up up to the takeoff is shared by every candidate tick: start from the recorded state at t.
            var track = RentTrack();
            ParkourOutcome o;
            try { o = SimulateCore(w, run[t], sch, t, spec.To, spec.ToFloor, spec.MaxFall, track); }
            catch
            {
                ReturnTrack(track);
                foreach (var previous in nominals) ReturnTrack(previous.O.Track!);
                throw;
            }
            if (!o.Ok) { ReturnTrack(track); why ??= o.Reason; continue; }
            var c = track[Math.Min(o.LandTick + 1 - t, track.Count - 1)]; // first contact, relative to takeoff
            double err = Math.Sqrt((c.X - spec.Tx) * (c.X - spec.Tx) + (c.Z - spec.Tz) * (c.Z - spec.Tz));
            nominals.Add(new Nominal(t, sch, o, err));
        }
        return nominals;
    }

    private static List<Vec3d> WithRunUp(List<PhysicsState> run, int t, List<Vec3d> track)
    {
        var full = new List<Vec3d>(t + track.Count);
        for (int i = 0; i < t; i++) full.Add(run[i].Position);
        full.AddRange(track);
        return full;
    }

    /// <summary>A nominal flight as a plan: refused over the void unless allowed (abort), with too little margin, without a
    /// clutch block in reach (over the void), or when a perturbed run misses; else accepted.</summary>
    private static Verdict Validate(IBlockAccess w, JumpSpec spec, List<PhysicsState> run, Nominal n, ref string? why, out ParkourPlan? plan)
    {
        plan = null;
        var (t, sch, nominal, _) = n;
        bool overVoid = MissIsDeadly(w, nominal.Track!, 0, nominal.LandTick - t, spec.FromFloor, spec.ToFloor, spec.SafeMissFall);
        if (overVoid && !spec.AllowVoid) { why = "a miss falls into the void"; return Verdict.Abort; }
        double need = overVoid ? VoidMargin : GroundMargin;
        if (nominal.Margin < need) { why = $"landing margin {nominal.Margin:0.00}"; return Verdict.Next; }
        if (overVoid && !ClutchCoverage(w, nominal.Track!, 0, nominal.LandTick - t))
        {
            why = "no block within clutch reach mid-flight";
            return Verdict.Next;
        }
        if (!Robust(w, spec, sch, t, overVoid, nominal.Margin, ref why, out double worst)) return Verdict.Next;
        plan = new ParkourPlan(spec.From, spec.To, spec.FromFloor, spec.ToFloor, sch, nominal.LandTick, nominal.Distance, worst, overVoid,
            WithRunUp(run, t, nominal.Track!), spec.MaxFall);
        return Verdict.Accept;
    }

    /// <summary>Every perturbation (start, takeoff tick, yaw) still lands with its margin; <paramref name="worst"/> is the
    /// smallest margin seen.</summary>
    private static bool Robust(IBlockAccess w, JumpSpec spec, ParkourSchedule sch, int t, bool overVoid, double margin, ref string? why,
        out double worst)
    {
        worst = margin;
        foreach (var (along, across, dt, dyaw) in Perturbations(overVoid))
        {
            var p = Perturb(spec.Start, spec.Yaw, along, across);
            var ps = sch with { JumpTick = t + dt, Yaw = spec.Yaw + dyaw };
            // This state belongs only to this perturbation; there is no caller snapshot to preserve with a clone.
            var o = SimulateOwned(w, StandingAt(p, ps.Yaw), ps, 0, spec.To, spec.ToFloor, spec.MaxFall, null);
            if (!o.Ok || o.Margin < (overVoid ? 0 : PerturbedMargin))
            {
                why = $"not robust ({(o.Ok ? $"margin {o.Margin:0.00}" : o.Reason)} at {along:+0.##;-0.##;0},{across:+0.##;-0.##;0} tick {dt:+0;-0;0})";
                return false;
            }
            worst = Math.Min(worst, o.Margin);
        }
        return true;
    }

    private static IEnumerable<(double Along, double Across, int Dt, float DYaw)> Perturbations(bool strict)
    {
        double j = strict ? VoidJitter : Jitter;
        yield return (0, 0, -1, 0);
        yield return (0, 0, 1, 0);
        yield return (j, 0, 0, 0);
        yield return (-j, 0, 0, 0);
        yield return (0, j, 0, 0);
        yield return (0, -j, 0, 0);
        if (!strict) yield break;
        yield return (j, 0, 1, 0);   // closer to the edge and late: runs off?
        yield return (-j, 0, -1, 0); // further back and early: falls short?
        yield return (0, 0, 0, VoidYawJitter);
        yield return (0, 0, 0, -VoidYawJitter);
        yield return (-j, j, -1, VoidYawJitter);
        yield return (-j, -j, -1, -VoidYawJitter);
        yield return (j, j, 1, -VoidYawJitter);
        yield return (j, -j, 1, VoidYawJitter);
    }

    private static Vec3d Perturb(Vec3d p, float yaw, double along, double across)
    {
        double rad = yaw * Math.PI / 180;
        double fx = -Math.Sin(rad), fz = Math.Cos(rad);
        return new Vec3d(p.X + fx * along - fz * across, p.Y, p.Z + fz * along + fx * across);
    }

    /// <summary>Runs forward (no jump) from a standing start; <paramref name="states"/> receives the state at the start of
    /// each tick. Returns the last tick that starts on the ground with a clean run-up behind it.</summary>
    private static int LastGroundTick(IBlockAccess w, Vec3d start, float yaw, bool sprint, List<PhysicsState>? states = null)
    {
        var s = StandingAt(start, yaw);
        var run = new MoveInput(Forward: 1, Sprint: sprint, Yaw: yaw);
        for (int k = 0; k < MaxRunUpTicks; k++)
        {
            states?.Add(s.Clone());
            if (!s.OnGround) return k - 1;
            PlayerPhysics.Tick(w, s, run);
            if (s.Frozen || s.CollidedHorizontally || (sprint && !s.Sprinting)) return k; // a wall: jump on this tick at the latest
        }
        states?.Add(s.Clone());
        return MaxRunUpTicks;
    }

    /// <summary>A miss is deadly when any column under the hitbox (grown 0.3) along the flight is a deadly drop (void, lava, water
    /// over the void, deeper than the safe fall) from the lower of the two floors.</summary>
    private static bool MissIsDeadly(IBlockAccess w, List<Vec3d> track, int jumpTick, int landTick, double fromFloor,
        double toFloor, double maxFall)
    {
        int level = (int)Math.Floor(Math.Min(fromFloor, toFloor) + 1e-6);
        var seen = new HashSet<(int, int)>();
        Span<(double X, double Z)> corners = stackalloc (double, double)[5];
        for (int i = Math.Max(0, jumpTick); i <= Math.Min(track.Count - 1, landTick + 1); i++)
        {
            var bb = AABB.ForPlayer(track[i]).Grow(0.3, 0, 0.3); // the hitbox plus the perturbation range
            corners[0] = (bb.MinX, bb.MinZ);
            corners[1] = (bb.MaxX, bb.MinZ);
            corners[2] = (bb.MinX, bb.MaxZ);
            corners[3] = (bb.MaxX, bb.MaxZ);
            corners[4] = (track[i].X, track[i].Z);
            foreach (var (x, z) in corners)
            {
                int cx = (int)Math.Floor(x), cz = (int)Math.Floor(z);
                if (!seen.Add((cx, cz))) continue;
                if (Safety.IsDeadlyColumn(w, cx, level, cz, maxFall)) return true;
            }
        }
        return false;
    }

    /// <summary>Distance from the eye within which some solid block must exist on every flight tick of a void jump, so a
    /// knocked-off bot can still place a clutch block (reach 4.5 minus a block of knockback drift).</summary>
    public const double ClutchCoverageRadius = 3.5;

    /// <summary>Every flight tick has a collidable block whose nearest point is within <see cref="ClutchCoverageRadius"/> of the eye.</summary>
    private static bool ClutchCoverage(IBlockAccess w, List<Vec3d> track, int jumpTick, int landTick)
    {
        for (int i = Math.Max(0, jumpTick); i <= Math.Min(track.Count - 1, landTick); i++)
        {
            var eye = track[i] with { Y = track[i].Y + PlayerPhysics.EyeHeight };
            if (!FullCubeWithin(w, eye, ClutchCoverageRadius)) return false;
        }
        return true;
    }

    /// <summary>A full cube whose nearest point is within <paramref name="r"/> of <paramref name="eye"/>.</summary>
    private static bool FullCubeWithin(IBlockAccess w, Vec3d eye, double r)
    {
        for (int x = (int)Math.Floor(eye.X - r); x <= (int)Math.Floor(eye.X + r); x++)
        for (int y = (int)Math.Floor(eye.Y - r); y <= (int)Math.Floor(eye.Y + r); y++)
        for (int z = (int)Math.Floor(eye.Z - r); z <= (int)Math.Floor(eye.Z + r); z++)
        {
            double dx = Math.Max(0, Math.Max(x - eye.X, eye.X - (x + 1)));
            double dy = Math.Max(0, Math.Max(y - eye.Y, eye.Y - (y + 1)));
            double dz = Math.Max(0, Math.Max(z - eye.Z, eye.Z - (z + 1)));
            if (dx * dx + dy * dy + dz * dz > r * r) continue;
            if (BlockShapes.IsFullCube(w, x, y, z)) return true;
        }
        return false;
    }

    // ------------------------------------------------------------------ reach bounds (pre-filter)

    private static readonly double[] ReachSprint = new double[5], ReachWalk = new double[5];

    static Parkour()
    {
        for (int dy = -3; dy <= 1; dy++)
        {
            ReachSprint[dy + 3] = RunwayAirTravel(dy, true);
            ReachWalk[dy + 3] = RunwayAirTravel(dy, false);
        }
    }

    /// <summary>
    /// Upper bound of the feet travel from the takeoff point to the moment the feet come back down to
    /// <paramref name="dy"/> blocks relative to the takeoff floor, with a full run-up and a perfect last-tick takeoff
    /// (measured with <see cref="PlayerPhysics"/> on a synthetic runway).
    /// </summary>
    public static double AirTravel(int dy, bool sprint) =>
        dy is < -3 or > 1 ? 0 : (sprint ? ReachSprint : ReachWalk)[dy + 3];

    /// <summary><see cref="AirTravel(int, bool)"/> under a version's movement rules (multi-version P3: 1.12.2's sprint lag, jump
    /// ticks and float constants give slightly different reaches), measured once per rules instance.</summary>
    public static double AirTravel(int dy, bool sprint, Versioning.IPhysicsRules rules)
    {
        if (rules is Versioning.Beta181PhysicsRules) return AirTravel(dy, sprint);
        if (dy is < -3 or > 1) return 0;
        var table = ReachByRules.GetOrAdd(rules, r =>
        {
            var t = new double[10];
            for (int d = -3; d <= 1; d++)
            {
                t[d + 3] = RunwayAirTravel(d, true, r);
                t[d + 8] = RunwayAirTravel(d, false, r);
            }
            return t;
        });
        return table[dy + (sprint ? 3 : 8)];
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Versioning.IPhysicsRules, double[]> ReachByRules = new();

    private static double RunwayAirTravel(int dy, bool sprint, Versioning.IPhysicsRules? rules = null)
    {
        var w = new Runway(rules ?? Versioning.Beta181PhysicsRules.Instance);
        const float yaw = -90f; // +X
        var start = new Vec3d(-6.5, 64, 0.5);
        int last = LastGroundTick(w, start, yaw, sprint);
        var s = StandingAt(start, yaw);
        double takeoff = 0;
        bool up = false;
        for (int k = 0; k < 120; k++)
        {
            if (k == last) takeoff = s.Position.X;
            PlayerPhysics.Tick(w, s, new MoveInput(Forward: 1, Sprint: sprint, Jump: k == last, Yaw: yaw));
            if (k < last) continue;
            if (s.Velocity.Y > 0) up = true;
            if (up && s.Velocity.Y < 0 && s.Position.Y <= 64 + dy) return s.Position.X - takeoff;
        }
        return 0;
    }

    /// <summary>Floor at y = 63 for x &lt; 0, air elsewhere.</summary>
    private sealed class Runway(Versioning.IPhysicsRules rules) : IBlockAccess
    {
        public Versioning.IPhysicsRules Physics => rules;
        public int GetBlock(int x, int y, int z) => y == 63 && x < 0 ? Blocks.Stone : 0;
        public int GetMeta(int x, int y, int z) => 0;
        public bool IsLoaded(int x, int z) => true;
        public BlockInfo Info(int id) => Blocks.Get(id);
    }
}

/// <summary>A dense copy of a box of blocks (ids, metas, loaded columns); reads outside it go to the source world.</summary>
internal sealed class RegionSnapshot : IBlockAccess, IDisposable
{
    private readonly IBlockAccess _inner;
    private readonly int _x0, _y0, _z0, _sx, _sy, _sz;
    private readonly int[] _ids = [];
    private readonly byte[] _metas = [];
    private readonly bool[] _loaded = [];
    private bool _disposed;

    public RegionSnapshot(IBlockAccess inner, int x0, int x1, int y0, int y1, int z0, int z1)
    {
        _inner = inner is RegionSnapshot r ? r._inner : inner;
        y0 = Math.Max(0, y0);
        y1 = Math.Min(_inner.WorldHeight - 1, y1);
        (_x0, _y0, _z0) = (x0, y0, z0);
        (_sx, _sy, _sz) = (x1 - x0 + 1, Math.Max(0, y1 - y0 + 1), z1 - z0 + 1);
        int cells = _sx * _sy * _sz;
        try
        {
            _ids = ArrayPool<int>.Shared.Rent(cells);
            _metas = ArrayPool<byte>.Shared.Rent(cells);
            _loaded = ArrayPool<bool>.Shared.Rent(_sx * _sz);
            // Unloaded columns and air metadata must never inherit a previous jump's blocks.
            _ids.AsSpan(0, cells).Clear();
            _metas.AsSpan(0, cells).Clear();
            Copy(inner, x0, y0, z0);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private void Copy(IBlockAccess inner, int x0, int y0, int z0)
    {
        for (int x = 0; x < _sx; x++)
        for (int z = 0; z < _sz; z++)
        {
            bool loaded = _loaded[x * _sz + z] = inner.IsLoaded(x0 + x, z0 + z);
            if (!loaded) continue;
            for (int y = 0; y < _sy; y++)
            {
                int i = (x * _sz + z) * _sy + y;
                int id = _ids[i] = inner.GetBlock(x0 + x, y0 + y, z0 + z);
                if (id != 0) _metas[i] = (byte)inner.GetMeta(x0 + x, y0 + y, z0 + z);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_ids.Length > 0) ArrayPool<int>.Shared.Return(_ids);
        if (_metas.Length > 0) ArrayPool<byte>.Shared.Return(_metas);
        if (_loaded.Length > 0) ArrayPool<bool>.Shared.Return(_loaded);
    }

    private int Index(int x, int y, int z)
    {
        int dx = x - _x0, dy = y - _y0, dz = z - _z0;
        if ((uint)dx >= (uint)_sx || (uint)dy >= (uint)_sy || (uint)dz >= (uint)_sz) return -1;
        return (dx * _sz + dz) * _sy + dy;
    }

    public int GetBlock(int x, int y, int z)
    {
        int i = Index(x, y, z);
        return i < 0 ? _inner.GetBlock(x, y, z) : _ids[i];
    }

    public int GetMeta(int x, int y, int z)
    {
        int i = Index(x, y, z);
        return i < 0 ? _inner.GetMeta(x, y, z) : _metas[i];
    }

    public bool IsLoaded(int x, int z)
    {
        int dx = x - _x0, dz = z - _z0;
        if ((uint)dx >= (uint)_sx || (uint)dz >= (uint)_sz) return _inner.IsLoaded(x, z);
        return _loaded[dx * _sz + dz];
    }

    public BlockInfo Info(int id) => _inner.Info(id);

    public int WorldHeight => _inner.WorldHeight;

    public Versioning.IPhysicsRules Physics => _inner.Physics;
}

/// <summary>
/// Validated parkour edges, reusable across re-plans: an entry stays valid while a hash of every block id/meta in the
/// jump's neighbourhood (both columns ±1, floors −2..+3) is unchanged. Thread-safe.
/// </summary>
public sealed class ParkourCache
{
    private readonly record struct Key(BlockPos From, BlockPos To, bool Sprint, bool AllowVoid, int MaxFall, int MissFall);
    private readonly record struct Entry(ulong Hash, ParkourPlan? Plan, string? Why);
    private readonly Dictionary<Key, Entry> _map = new();
    private readonly object _lock = new();
    private long _hits, _misses;

    public long Hits => Interlocked.Read(ref _hits);
    public long Misses => Interlocked.Read(ref _misses);
    public int Count { get { lock (_lock) return _map.Count; } }

    public ParkourPlan? GetOrPlan(IBlockAccess w, BlockPos from, double fromFloor, BlockPos to, double toFloor, bool sprint,
        bool allowVoid, double maxFall, double safeMissFall)
    {
        var key = new Key(from, to, sprint, allowVoid, (int)Math.Round(maxFall * 100), (int)Math.Round(safeMissFall * 100));
        ulong h = RegionHash(w, from, to, fromFloor, toFloor);
        lock (_lock)
        {
            if (_map.TryGetValue(key, out var e) && e.Hash == h)
            {
                Interlocked.Increment(ref _hits);
                return e.Plan;
            }
        }
        Interlocked.Increment(ref _misses);
        var plan = Parkour.Plan(w, from, fromFloor, to, toFloor, sprint, allowVoid, maxFall, safeMissFall, out var why);
        lock (_lock)
        {
            if (_map.Count > 4096) _map.Clear();
            _map[key] = new Entry(h, plan, why);
        }
        return plan;
    }

    public void Clear() { lock (_lock) _map.Clear(); }

    private static ulong RegionHash(IBlockAccess w, BlockPos a, BlockPos b, double fa, double fb)
    {
        int x0 = Math.Min(a.X, b.X) - 1, x1 = Math.Max(a.X, b.X) + 1;
        int z0 = Math.Min(a.Z, b.Z) - 1, z1 = Math.Max(a.Z, b.Z) + 1;
        int y0 = Math.Max(0, (int)Math.Floor(Math.Min(fa, fb)) - 2), y1 = Math.Min(w.WorldHeight - 1, (int)Math.Floor(Math.Max(fa, fb)) + 3);
        ulong h = 1469598103934665603UL;
        for (int x = x0; x <= x1; x++)
        for (int z = z0; z <= z1; z++)
        {
            h = (h ^ (w.IsLoaded(x, z) ? 1UL : 2UL)) * 1099511628211UL;
            for (int y = y0; y <= y1; y++)
            {
                h = (h ^ (ulong)w.GetBlock(x, y, z)) * 1099511628211UL;
                h = (h ^ (ulong)w.GetMeta(x, y, z)) * 1099511628211UL;
            }
        }
        return h;
    }
}
