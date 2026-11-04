namespace MineClaw.World;

/// <summary>A water column to ride: the source cell (x, SourceY, z), poured beside the edge floor block the bot stands on.</summary>
public readonly record struct SwimColumn(int X, int SourceY, int Z)
{
    public BlockPos Source => new(X, SourceY, Z);
    public Vec3d Center(double y) => new(X + 0.5, y, Z + 0.5);
    public override string ToString() => $"column {X},{SourceY},{Z}";
}

/// <summary>Outcome of a simulated swim: ticks taken, the lowest feet height reached, or why it failed.</summary>
public sealed record SwimResult(bool Ok, int Ticks, double MinFeetY, PhysicsState End, string? Why = null);

/// <summary>
/// The per-tick swim policies the client drives with, and a forward simulation of them on <see cref="PlayerPhysics"/> (the
/// b1.8.1 port), so a water-column descent is planned and verified with exactly the moves the bot will make. mc-dev 218a2a71
/// EntityLiving.a(float, float) in water: moveFlying 0.02, move, motion x 0.8, motY - 0.02; jumping in water: motY + 0.04
/// (EntityLiving.d); World.a(AABB, Material, Entity) adds the summed flow vector x 0.014, and falling water (meta 8+) beside a
/// buildable block flows straight down (BlockFluids.c: + (0, -6, 0)). So in a free column the bot sinks at about 0.1 block/tick
/// and rises at about 0.06 with jump held; beside a wall the current makes it about 0.156 down and only 0.004 up (slow: the
/// planner pays for it). Collided horizontally with the space 0.6 above free of liquid: motY = 0.3 (climbing out at the top).
/// </summary>
public static class SwimSim
{
    /// <summary>Feet tolerance band while hovering at a depth (the placement stand's feet height).</summary>
    public const double HoverBand = 0.25;

    /// <summary>
    /// Steer horizontally toward the column centre at a FIXED look (<paramref name="yaw"/>, <paramref name="pitch"/>): forward and
    /// strafe are solved from Entity.moveFlying (vx = strafe*cos - forward*sin, vz = forward*cos + strafe*sin), so hovering never
    /// turns the head the server reads for a placement's facing or a bucket's raytrace. Pushes only while the remaining distance
    /// exceeds what the drift carries (water drag 0.8 settles it).
    /// </summary>
    public static MoveInput Centre(PhysicsState s, SwimColumn c, bool jump, float? yaw = null, float pitch = 30)
    {
        float y = yaw ?? s.Yaw;
        double dx = c.X + 0.5 - s.Position.X, dz = c.Z + 0.5 - s.Position.Z;
        double d = Math.Sqrt(dx * dx + dz * dz);
        double hv = Math.Sqrt(s.Velocity.X * s.Velocity.X + s.Velocity.Z * s.Velocity.Z);
        if (d <= 0.06 || d <= hv * 3) return new MoveInput(Jump: jump, Yaw: y, Pitch: pitch);
        double rad = y * Math.PI / 180, sin = Math.Sin(rad), cos = Math.Cos(rad);
        double ux = dx / d, uz = dz / d, gain = Math.Min(1, d / 0.25);
        float fwd = (float)((-ux * sin + uz * cos) * gain), strafe = (float)((ux * cos + uz * sin) * gain);
        return new MoveInput(Forward: fwd, Strafe: strafe, Jump: jump, Yaw: y, Pitch: pitch);
    }

    /// <summary>Descent: walk off the edge into the column, then sink centred until the feet reach <paramref name="feetY"/>.</summary>
    public static MoveInput Descend(PhysicsState s, SwimColumn c, double feetY)
    {
        bool overColumn = (int)Math.Floor(s.Position.X) == c.X && (int)Math.Floor(s.Position.Z) == c.Z;
        if (!s.InWater && !overColumn)
        {
            // A slow step (0.3 forward, NOT sneaking: sneak edge clamping would hold the body on the block): the drop into the
            // source is short, and at walking speed the body would sail past the 1-wide column.
            float yaw = PlayerPhysics.YawPitchTo(s.Position, c.Center(s.Position.Y)).Yaw;
            return new MoveInput(Forward: s.OnGround ? 0.3f : 0, Yaw: yaw, Pitch: 40);
        }
        if (!s.InWater) return Centre(s, c, jump: false); // over the source, dropping in: steer to the centre
        return Hover(s, c, feetY, s.Yaw, 30);
    }

    /// <summary>Hold the feet within <see cref="HoverBand"/> of <paramref name="feetY"/> at a fixed look: jump while the feet,
    /// predicted two ticks ahead at the current speed, would be low (bang-bang on the prediction stays inside the band).</summary>
    public static MoveInput Hover(PhysicsState s, SwimColumn c, double feetY, float yaw, float pitch)
    {
        bool jump = s.InWater && s.Position.Y + s.Velocity.Y * 2 < feetY + 0.05;
        return Centre(s, c, jump, yaw, pitch);
    }

    /// <summary>Ascent: jump held, centred, until the source level; then press toward the exit stand to climb out (0.3 boost).</summary>
    public static MoveInput Ascend(PhysicsState s, SwimColumn c, BlockPos exit)
    {
        if (s.Position.Y < c.SourceY - 0.1 && s.InWater) return Centre(s, c, jump: true, yaw: s.Yaw);
        var target = exit.BottomCenter;
        double dx = target.X - s.Position.X, dz = target.Z - s.Position.Z;
        float yaw = PlayerPhysics.YawPitchTo(s.Position, new Vec3d(target.X, s.Position.Y, target.Z)).Yaw;
        bool there = dx * dx + dz * dz < 0.2 * 0.2;
        return new MoveInput(Forward: there ? 0 : 1, Jump: !s.OnGround || !there && s.InWater, Sneak: s.OnGround, Yaw: yaw, Pitch: 30);
    }

    /// <summary>The ascent ended on the exit stand.</summary>
    public static bool Exited(PhysicsState s, BlockPos exit) =>
        s.OnGround && !s.InWater && (int)Math.Floor(s.Position.Y + 1e-6) == exit.Y
        && Math.Abs(s.Position.X - (exit.X + 0.5)) < 0.45 && Math.Abs(s.Position.Z - (exit.Z + 0.5)) < 0.45;

    /// <summary>Hovering steadily at the depth (and centred enough to place from).</summary>
    public static bool Settled(PhysicsState s, SwimColumn c, double feetY) =>
        s.InWater && Math.Abs(s.Position.Y - feetY) <= HoverBand && Math.Abs(s.Velocity.Y) < 0.12
        && Math.Abs(s.Position.X - (c.X + 0.5)) < 0.3 && Math.Abs(s.Position.Z - (c.Z + 0.5)) < 0.3;

    /// <summary>
    /// Whether the body at <paramref name="feet"/> is inside the column with continuous water from its feet down to
    /// <paramref name="depthY"/> (or above the source, dropping in): the condition under which the client's void guard
    /// stands aside for a deliberate descent. A broken column (source taken, a gap) ends it.
    /// </summary>
    public static bool InColumn(IBlockAccess w, Vec3d feet, SwimColumn c, double depthY)
    {
        if (Math.Abs(feet.X - (c.X + 0.5)) > 0.8 || Math.Abs(feet.Z - (c.Z + 0.5)) > 0.8) return false;
        if (!BlockShapes.IsWater(w.GetBlock(c.X, c.SourceY, c.Z))) return false;
        int from = Math.Min(c.SourceY, (int)Math.Floor(feet.Y)), to = (int)Math.Floor(depthY) - 1;
        for (int y = from; y >= to && y >= 0; y--)
            if (!BlockShapes.IsWater(w.GetBlock(c.X, y, c.Z)) && !BlockShapes.HasCollision(w, c.X, y, c.Z)) return false;
        return feet.Y <= c.SourceY + 2.0;
    }

    /// <summary>
    /// Simulates stepping from <paramref name="stand"/> (a floor cell beside the column) into the column and sinking to
    /// <paramref name="feetY"/>. Fails if the body ever leaves the column's water below the source (the column must reach
    /// past the depth: never falling past the last water), drifts out of the column, or doesn't settle in time.
    /// </summary>
    public static SwimResult SimulateDescent(IBlockAccess w, BlockPos stand, SwimColumn c, double feetY, int maxTicks = 600)
    {
        var s = new PhysicsState { Position = stand.BottomCenter, OnGround = true };
        return Run(w, s, c, maxTicks, st => Descend(st, c, feetY), st => Settled(st, c, feetY), settleTicks: 6);
    }

    /// <summary>Simulates moving the hover from the current state to another depth in the same column.</summary>
    public static SwimResult SimulateHover(IBlockAccess w, PhysicsState from, SwimColumn c, double feetY, int maxTicks = 900) =>
        Run(w, from.Clone(), c, maxTicks, st => Hover(st, c, feetY, st.Yaw, 30), st => Settled(st, c, feetY), settleTicks: 6);

    /// <summary>Simulates the ascent from a hovering state to standing on <paramref name="exit"/>.</summary>
    public static SwimResult SimulateAscent(IBlockAccess w, PhysicsState from, SwimColumn c, BlockPos exit, int maxTicks = 2400) =>
        Run(w, from.Clone(), c, maxTicks, st => Ascend(st, c, exit), st => Exited(st, exit), settleTicks: 1, exit: exit);

    private static SwimResult Run(IBlockAccess w, PhysicsState s, SwimColumn c, int maxTicks, Func<PhysicsState, MoveInput> policy,
        Func<PhysicsState, bool> done, int settleTicks, BlockPos? exit = null)
    {
        double minY = s.Position.Y;
        int good = 0;
        for (int t = 1; t <= maxTicks; t++)
        {
            PlayerPhysics.Tick(w, s, policy(s));
            if (s.Frozen) return new SwimResult(false, t, minY, s, "chunk not loaded");
            minY = Math.Min(minY, s.Position.Y);
            if (Strayed(s, c, exit) is { } why) return new SwimResult(false, t, minY, s, why);
            if (done(s)) { if (++good >= settleTicks) return new SwimResult(true, t, minY, s); }
            else good = 0;
        }
        return new SwimResult(false, maxTicks, minY, s, $"didn't settle within {maxTicks} ticks (at y {s.Position.Y:0.0})");
    }

    /// <summary>Why the swim went wrong this tick: out of the water below the source (the column ends above the depth), or out
    /// of the column (and off the exit) below the source's top; null while it's fine.</summary>
    private static string? Strayed(PhysicsState s, SwimColumn c, BlockPos? exit)
    {
        bool inCell = Math.Abs(s.Position.X - (c.X + 0.5)) < 0.8 && Math.Abs(s.Position.Z - (c.Z + 0.5)) < 0.8;
        bool onExit = exit is { } e && Math.Abs(s.Position.X - (e.X + 0.5)) < 0.8 && Math.Abs(s.Position.Z - (e.Z + 0.5)) < 0.8;
        if (!s.InWater && !s.OnGround && s.Position.Y < c.SourceY - 0.5)
            return $"left the water at {s.Position.X:0.00},{s.Position.Y:0.0},{s.Position.Z:0.00} (the column ends above the depth?)";
        if (!inCell && !onExit && s.Position.Y < c.SourceY + 0.5)
            return "drifted out of the column";
        return null;
    }
}
