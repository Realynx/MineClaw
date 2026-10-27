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
    {
        var s = start.Clone();
        var track = record ? new List<Vec3d>(sch.JumpTick + 24) : null;
        bool hazardBefore = s.InLava || Safety.TouchesHazard(w, s.BoundingBox);
        bool airborne = fromTick > sch.JumpTick && !s.OnGround;
        double peak = s.Position.Y + (s.OnGround ? 0 : Math.Max(0, s.FallDistance));
        int contact = fromTick > sch.JumpTick && s.OnGround ? fromTick : -1;
        double margin = contact >= 0 ? SupportMargin(w, s.Position, sch.Yaw) : double.NegativeInfinity;
        Vec3d takeoff = s.Position;
        double distance = 0;
        int end = Math.Max(fromTick, sch.JumpTick) + MaxFlightTicks + SettleTicks;
        for (int k = fromTick; k < end; k++)
        {
            track?.Add(s.Position);
            if (k == sch.JumpTick)
            {
                if (!s.OnGround) return ParkourOutcome.Fail("no ground under the takeoff");
                takeoff = s.Position;
            }
            // After the touchdown the simulation just brakes (sneak: vanilla edge clamping holds the body on a narrow
            // roof); the follower then sneak-homes onto the cell centre, a plain walk on the same floor.
            var input = contact >= 0 ? new MoveInput(Sneak: true, Yaw: sch.Yaw) : sch.InputAt(k, s);
            PlayerPhysics.Tick(w, s, input);
            if (s.Frozen) return ParkourOutcome.Fail("unloaded chunk");
            if (s.Position.Y < 1) return ParkourOutcome.Fail("void");
            if (!hazardBefore && (s.InLava || Safety.TouchesHazard(w, s.BoundingBox))) return ParkourOutcome.Fail("hazard");
            if (s.InWater) return ParkourOutcome.Fail("water");

            if (k < sch.JumpTick)
            {
                if (!s.OnGround) return ParkourOutcome.Fail("ran off the edge before the takeoff");
                if (s.CollidedHorizontally) return ParkourOutcome.Fail("run-up blocked");
                if (sch.Sprint && !s.Sprinting) return ParkourOutcome.Fail("sprint lost");
                continue;
            }
            if (contact < 0)
            {
                if (s.Position.Y > peak) peak = s.Position.Y;
                if (s.CollidedHorizontally) return ParkourOutcome.Fail("hit a wall in flight");
                if (!s.OnGround)
                {
                    airborne = true;
                    if (k - sch.JumpTick > MaxFlightTicks) return ParkourOutcome.Fail("too long in the air");
                    // Falling below the target floor: it can no longer be landed on (fail fast instead of falling on).
                    if (s.Velocity.Y < 0 && s.Position.Y < toFloor - 0.6) return ParkourOutcome.Fail("fell short");
                    continue;
                }
                if (!airborne) return ParkourOutcome.Fail("no jump (head bump)");
                contact = k;
                double fall = peak - s.Position.Y;
                if (fall > maxFall + 1e-6) return ParkourOutcome.Fail($"fall {fall:0.0}");
                if (s.Position.Y < toFloor - 0.5 - 1e-3 || s.Position.Y > toFloor + 1e-3)
                    return ParkourOutcome.Fail($"landed at y {s.Position.Y:0.##} instead of {toFloor:0.##}");
                margin = SupportMargin(w, s.Position, sch.Yaw);
                double dx = s.Position.X - takeoff.X, dz = s.Position.Z - takeoff.Z;
                distance = Math.Sqrt(dx * dx + dz * dz);
                continue;
            }
            // Landed: brake to rest on or near the target cell at the target floor (or its stair half).
            if (!s.OnGround) return ParkourOutcome.Fail("slid off the landing");
            if (s.Velocity.HorizontalLength < 0.003)
            {
                var p = s.Position;
                int fx = (int)Math.Floor(p.X), fz = (int)Math.Floor(p.Z);
                // Overshooting onto the same roof is fine (the follower sneak-walks back); further means another place.
                if (Math.Abs(fx - to.X) > 2 || Math.Abs(fz - to.Z) > 2) return ParkourOutcome.Fail("came to rest off the landing");
                if (p.Y < toFloor - 0.5 - 1e-3 || p.Y > toFloor + 1e-3) return ParkourOutcome.Fail("came to rest at the wrong height");
                track?.Add(p);
                return new ParkourOutcome(true, null, margin, contact, distance, peak, track);
            }
            if (k - contact > SettleTicks) return ParkourOutcome.Fail("did not settle on the landing");
        }
        return ParkourOutcome.Fail("timeout");
    }

    /// <summary>
    /// How far the contact point could have been shorter (positive: the hitbox centre is over support with that much to
    /// spare backwards along the flight direction, capped at 0.6) or how far it is short of the support (negative, the
    /// centre overhangs: −0.3 = only the hitbox front edge touches).
    /// </summary>
    public static double SupportMargin(IBlockAccess w, Vec3d feet, float yaw)
    {
        double rad = yaw * Math.PI / 180;
        double fx = -Math.Sin(rad), fz = Math.Cos(rad);
        var boxes = new List<AABB>(8);
        var area = new AABB(feet.X - 1.0, feet.Y - 0.06, feet.Z - 1.0, feet.X + 1.0, feet.Y - 0.0005, feet.Z + 1.0);
        BlockShapes.GetCollidingBoxes(w, area, boxes);
        boxes.RemoveAll(b => b.MaxY < feet.Y - 0.06 || b.MaxY > feet.Y + 1e-6);
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
        w = new RegionSnapshot(w, Math.Min(from.X, to.X) - 3, Math.Max(from.X, to.X) + 3,
            (int)Math.Floor(Math.Min(fromFloor, toFloor)) - 6, (int)Math.Floor(Math.Max(fromFloor, toFloor)) + 4,
            Math.Min(from.Z, to.Z) - 3, Math.Max(from.Z, to.Z) + 3);
        var startPos = new Vec3d(from.X + 0.5, fromFloor, from.Z + 0.5);
        double tx = to.X + 0.5, tz = to.Z + 0.5;
        float yaw = (float)(Math.Atan2(-(tx - startPos.X), tz - startPos.Z) * 180 / Math.PI);

        // Ground run without jumping: the last tick that still starts on the ground is the latest possible takeoff.
        var run = new List<PhysicsState>(MaxRunUpTicks + 1);
        int lastGround = LastGroundTick(w, startPos, yaw, sprint, run);
        if (lastGround < 2) { why = "no run-up"; return null; }

        // Nominal runs for every candidate takeoff tick (latest = longest jump); validate them closest landing first.
        var nominals = new List<(int T, ParkourSchedule Sch, ParkourOutcome O, double Err)>();
        for (int t = lastGround - 1; t >= Math.Max(1, lastGround - 4); t--)
        {
            var sch = new ParkourSchedule(yaw, sprint, t, tx, tz);
            // The run-up up to the takeoff is shared by every candidate tick: start from the recorded state at t.
            var o = Simulate(w, run[t], sch, t, to, toFloor, maxFall, record: true);
            if (!o.Ok) { why ??= o.Reason; continue; }
            var full = new List<Vec3d>(t + o.Track!.Count);
            for (int i = 0; i < t; i++) full.Add(run[i].Position);
            full.AddRange(o.Track);
            o = o with { Track = full };
            var c = o.Track![Math.Min(o.LandTick + 1, o.Track.Count - 1)]; // first contact
            double err = Math.Sqrt((c.X - tx) * (c.X - tx) + (c.Z - tz) * (c.Z - tz));
            nominals.Add((t, sch, o, err));
        }
        foreach (var (t, sch, nominal, _) in nominals.OrderBy(n => n.Err))
        {
            bool overVoid = MissIsDeadly(w, nominal.Track!, sch.JumpTick, nominal.LandTick, fromFloor, toFloor, safeMissFall);
            if (overVoid && !allowVoid) { why = "a miss falls into the void"; return null; }
            double need = overVoid ? VoidMargin : GroundMargin;
            if (nominal.Margin < need) { why = $"landing margin {nominal.Margin:0.00}"; continue; }
            if (overVoid && !ClutchCoverage(w, nominal.Track!, sch.JumpTick, nominal.LandTick))
            {
                why = "no block within clutch reach mid-flight";
                continue;
            }
            double worst = nominal.Margin;
            bool robust = true;
            foreach (var (along, across, dt, dyaw) in Perturbations(overVoid))
            {
                var p = Perturb(startPos, yaw, along, across);
                var ps = sch with { JumpTick = t + dt, Yaw = yaw + dyaw };
                var o = Simulate(w, StandingAt(p, ps.Yaw), ps, 0, to, toFloor, maxFall);
                if (!o.Ok || o.Margin < (overVoid ? 0 : PerturbedMargin))
                {
                    robust = false;
                    why = $"not robust ({(o.Ok ? $"margin {o.Margin:0.00}" : o.Reason)} at {along:+0.##;-0.##;0},{across:+0.##;-0.##;0} tick {dt:+0;-0;0})";
                    break;
                }
                worst = Math.Min(worst, o.Margin);
            }
            if (!robust) continue;
            return new ParkourPlan(from, to, fromFloor, toFloor, sch, nominal.LandTick, nominal.Distance, worst, overVoid,
                nominal.Track!, maxFall);
        }
        why ??= "no takeoff tick lands";
        return null;
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
        for (int i = Math.Max(0, jumpTick); i <= Math.Min(track.Count - 1, landTick + 1); i++)
        {
            var bb = AABB.ForPlayer(track[i]).Grow(0.3, 0, 0.3); // the hitbox plus the perturbation range
            foreach (var (x, z) in new[] { (bb.MinX, bb.MinZ), (bb.MaxX, bb.MinZ), (bb.MinX, bb.MaxZ), (bb.MaxX, bb.MaxZ), (track[i].X, track[i].Z) })
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
        const double r = ClutchCoverageRadius;
        for (int i = Math.Max(0, jumpTick); i <= Math.Min(track.Count - 1, landTick); i++)
        {
            var eye = track[i] with { Y = track[i].Y + PlayerPhysics.EyeHeight };
            bool found = false;
            for (int x = (int)Math.Floor(eye.X - r); x <= (int)Math.Floor(eye.X + r) && !found; x++)
            for (int y = (int)Math.Floor(eye.Y - r); y <= (int)Math.Floor(eye.Y + r) && !found; y++)
            for (int z = (int)Math.Floor(eye.Z - r); z <= (int)Math.Floor(eye.Z + r) && !found; z++)
            {
                double dx = Math.Max(0, Math.Max(x - eye.X, eye.X - (x + 1)));
                double dy = Math.Max(0, Math.Max(y - eye.Y, eye.Y - (y + 1)));
                double dz = Math.Max(0, Math.Max(z - eye.Z, eye.Z - (z + 1)));
                if (dx * dx + dy * dy + dz * dz > r * r) continue;
                if (BlockShapes.IsFullCube(w, x, y, z)) found = true;
            }
            if (!found) return false;
        }
        return true;
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

    private static double RunwayAirTravel(int dy, bool sprint)
    {
        var w = new Runway();
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
    private sealed class Runway : IBlockAccess
    {
        public int GetBlock(int x, int y, int z) => y == 63 && x < 0 ? Blocks.Stone : 0;
        public int GetMeta(int x, int y, int z) => 0;
        public bool IsLoaded(int x, int z) => true;
        public BlockInfo Info(int id) => Blocks.Get(id);
    }
}

/// <summary>A dense copy of a box of blocks (ids, metas, loaded columns); reads outside it go to the source world.</summary>
internal sealed class RegionSnapshot : IBlockAccess
{
    private readonly IBlockAccess _inner;
    private readonly int _x0, _y0, _z0, _sx, _sy, _sz;
    private readonly int[] _ids;
    private readonly byte[] _metas;
    private readonly bool[] _loaded;

    public RegionSnapshot(IBlockAccess inner, int x0, int x1, int y0, int y1, int z0, int z1)
    {
        _inner = inner is RegionSnapshot r ? r._inner : inner;
        y0 = Math.Max(0, y0);
        y1 = Math.Min(WorldState.Height - 1, y1);
        (_x0, _y0, _z0) = (x0, y0, z0);
        (_sx, _sy, _sz) = (x1 - x0 + 1, Math.Max(0, y1 - y0 + 1), z1 - z0 + 1);
        _ids = new int[_sx * _sy * _sz];
        _metas = new byte[_ids.Length];
        _loaded = new bool[_sx * _sz];
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
        int y0 = Math.Max(0, (int)Math.Floor(Math.Min(fa, fb)) - 2), y1 = Math.Min(WorldState.Height - 1, (int)Math.Floor(Math.Max(fa, fb)) + 3);
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
