using MineClaw.World.Release1122;
using MineClaw.World.Versioning;

namespace MineClaw.World;

/// <summary>One tick's extra movement: the extra displacement and how many position packets it goes out in.</summary>
/// <param name="Mode">ground | swim | ladder.</param>
/// <param name="Factor">The multiplier that was applied (lowered near edges, drops and liquids, capped in water and on ladders).</param>
public readonly record struct BoostPlan(double Ex, double Ey, double Ez, int Segments, string Mode, double Factor)
{
    public double Horizontal => Math.Sqrt(Ex * Ex + Ez * Ez);
}

/// <summary>
/// Story Mode's Fast / Faster (docs/web/story-mode.md "Speed"): a pawn's movement goes further each tick than vanilla's. The normal
/// physics tick runs unchanged (input, friction, gravity, jumps, falls); after it, the body is moved again by
/// <c>(factor − 1) ×</c> what the tick moved horizontally, with the version's own <c>Entity.move</c> (the same swept collision,
/// step-up and ground rules the server replays), in segments of at most <see cref="MaxPacketStep"/>, each its own position packet.
/// <list type="bullet">
/// <item><b>The server's checks</b>: vanilla moves the player by each packet's delta with that same <c>Entity.move</c> and calls
/// "moved wrongly" when the result is more than 0.25 off (horizontally), "moved too quickly" past 10 blocks (b1.8.1
/// <c>NetServerHandler</c>: a kick; 1.12.2: a teleport back). One packet per collision pass keeps the server's replay identical
/// to the client's, and a packet never carries more than ~1.1 blocks.</item>
/// <item><b>Only where it's safe</b>: on the ground (not sneaking, not in a web, not on lava), swimming (capped at
/// <see cref="SwimCap"/>) and climbing a ladder up (<see cref="LadderCap"/>); never in the air (jumps, gap jumps and falls are
/// vanilla's). The extra move must end supported (ground within a step below), out of liquids on land, in water when swimming,
/// still on the ladder when climbing, or it is halved and retried, then dropped. Near an edge, drop or liquid ahead the
/// factor falls to <see cref="EdgeFactor"/>.</item>
/// <item><b>The driver's room</b>: the path follower says how far the body may go this tick without passing a turn or a
/// precise stop (<c>PathFollower.BoostRoom</c>); the whole tick never goes further, and never above <see cref="MaxTickHorizontal"/>.</item>
/// </list>
/// Velocity, fall distance and fall damage stay what the normal tick left (the extra move is position only; a collision still
/// stops that axis).
/// </summary>
public static class SpeedBoost
{
    /// <summary>Faster: 3× walking and sprinting speed.</summary>
    public const double MaxMultiplier = 3.0;
    /// <summary>The hard cap on one tick's horizontal travel (blocks).</summary>
    public const double MaxTickHorizontal = 3.0;
    /// <summary>One extra position packet carries at most this much (vanilla's threshold is 10 blocks a packet).</summary>
    public const double MaxPacketStep = 0.75;
    /// <summary>Swimming and ladder climbing go at most this much faster.</summary>
    public const double SwimCap = 1.5, LadderCap = 1.5;
    /// <summary>The factor within a block of an edge, a drop or a liquid in the direction of travel.</summary>
    public const double EdgeFactor = 1.25;
    /// <summary>The vertical part of an extra ground move: vanilla's on-ground motionY ((0 − 0.08) × 0.98), so the server's
    /// replay sees the same ground contact.</summary>
    public const double GroundProbe = -0.0784000015258789;

    /// <summary>Below this extra distance nothing is sent.</summary>
    private const double MinExtra = 0.01;

    /// <summary>The version's <c>Entity.move</c> (b1.8.1 <see cref="PlayerPhysics.MoveEntity(IBlockAccess, PhysicsState, double, double, double, double)"/>;
    /// 1.12.2 <see cref="Physics1122.Move"/>), with the server's ySize: <c>NetServerHandler</c> zeroes it before each replay
    /// (b1.8.1), so a step the client's camera smoothing would refuse is taken on both sides. False: an unknown version.</summary>
    public static bool Move(IBlockAccess w, PhysicsState s, double dx, double dy, double dz)
    {
        switch (w.Physics)
        {
            case Beta181PhysicsRules:
            {
                double ys = s.YSize;
                s.YSize = 0;
                PlayerPhysics.MoveEntity(w, s, dx, dy, dz, PlayerPhysics.StepHeight);
                s.YSize = Math.Max(ys, s.YSize);
                return true;
            }
            case Release1122PhysicsRules:
                Physics1122.Move(w, s, dx, dy, dz);
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// The extra move for this tick, or null. <paramref name="s"/> is the state after the normal tick; <paramref name="before"/> and
    /// <paramref name="groundBefore"/> what it was before. <paramref name="factor"/> ≥ 1 is the multiplier the client applies;
    /// <paramref name="room"/> the most the whole tick may travel (∞ = no limit, 0 = none).
    /// </summary>
    public static BoostPlan? Plan(IBlockAccess w, PhysicsState s, Vec3d before, bool groundBefore, double factor, double room)
    {
        if (Refused(s, factor, room)) return null;
        factor = Math.Min(factor, MaxMultiplier);
        var d = s.Position - before;
        double h = Math.Sqrt(d.X * d.X + d.Z * d.Z);
        if (LavaNear(w, s.BoundingBox.Grow(1, 0.5, 1))) return null;
        return s.OnLadder ? PlanLadder(w, s, d, h, factor, room) : PlanMove(w, s, d, h, groundBefore, factor, room);
    }

    /// <summary>No boost at all: no factor, no room, a frozen body, sneaking, in lava or in a web.</summary>
    private static bool Refused(PhysicsState s, double factor, double room) =>
        factor <= 1.0001 || room <= 0 || s.Frozen || s.Sneaking || s.InLava || s.InWeb;

    /// <summary>Climbing up: more of the same vertical motion, as long as the feet stay on the ladder.</summary>
    private static BoostPlan? PlanLadder(IBlockAccess w, PhysicsState s, Vec3d d, double h, double factor, double room)
    {
        if (d.Y < 0.05 || h > 0.2) return null;
        double f = Math.Min(factor, LadderCap);
        double ey = Math.Min(d.Y * (f - 1), Math.Max(0, room - d.Y));
        for (int attempt = 0; attempt < 2 && ey >= MinExtra; attempt++, ey /= 2)
        {
            var plan = new BoostPlan(0, ey, 0, 1, "ladder", f);
            if (LadderTrialHolds(w, s, plan, ey)) return plan;
        }
        return null;
    }

    /// <summary>The trial climb moved the whole way up and the feet are still on the ladder.</summary>
    private static bool LadderTrialHolds(IBlockAccess w, PhysicsState s, BoostPlan plan, double ey)
    {
        var t = s.Clone();
        return Trial(w, t, plan) && w.Physics.IsClimbable(w, (int)Math.Floor(t.Position.X), (int)Math.Floor(t.BoundingBox.MinY), (int)Math.Floor(t.Position.Z))
            && t.Position.Y - s.Position.Y >= ey - 1e-6;
    }

    /// <summary>On the ground or swimming: more of the tick's horizontal move, cut near an edge, a drop or a liquid ahead.</summary>
    private static BoostPlan? PlanMove(IBlockAccess w, PhysicsState s, Vec3d d, double h, bool groundBefore, double factor, double room)
    {
        bool swim = s.InWater;
        bool ground = !swim && groundBefore && s.OnGround;
        if (!swim && !ground) return null;          // in the air: jumps, gap jumps and falls are vanilla's
        if (h < 0.02) return null;                  // standing, braking or turning on the spot
        double fac = swim ? Math.Min(factor, SwimCap) : factor;
        double dirX = d.X / h, dirZ = d.Z / h;
        double total = Total(h, fac, room);
        if (ground && HazardAhead(w, s, dirX, dirZ, total - h)) total = Total(h, fac = Math.Min(fac, EdgeFactor), room);
        return TryExtra(w, s, dirX, dirZ, total - h, swim, fac);
    }

    /// <summary>The extra move in packet-sized segments, halved and retried (three tries) until a trial holds.</summary>
    private static BoostPlan? TryExtra(IBlockAccess w, PhysicsState s, double dirX, double dirZ, double extra, bool swim, double fac)
    {
        for (int attempt = 0; attempt < 3 && extra >= MinExtra; attempt++, extra /= 2)
        {
            int n = Math.Max(1, (int)Math.Ceiling(extra / MaxPacketStep - 1e-9));
            var plan = new BoostPlan(dirX * extra, swim ? 0 : GroundProbe * n, dirZ * extra, n, swim ? "swim" : "ground", fac);
            if (MoveTrialHolds(w, s, plan, swim)) return plan;
        }
        return null;
    }

    /// <summary>The trial move went through and ends in water (swimming), or supported and out of liquids (on land).</summary>
    private static bool MoveTrialHolds(IBlockAccess w, PhysicsState s, BoostPlan plan, bool swim)
    {
        var t = s.Clone();
        if (!Trial(w, t, plan)) return false;
        return swim ? InWater(w, t.BoundingBox) : Supported(w, t) && !AnyLiquid(w, t.BoundingBox);
    }

    private static double Total(double h, double fac, double room) => Math.Min(Math.Min(h * fac, MaxTickHorizontal), Math.Max(h, room));

    /// <summary>Applies a plan to <paramref name="s"/> segment by segment; <paramref name="afterSegment"/> runs after each one but
    /// the last (the caller sends that segment's position packet; the tick's own packet carries the last). Returns the segments
    /// applied.</summary>
    public static int Apply(IBlockAccess w, PhysicsState s, BoostPlan plan, Action<int>? afterSegment = null)
    {
        var v = s.Velocity;
        double fall = s.FallDistance;
        int fallDamage = s.FallDamage;
        int done = MoveSegments(w, s, plan, afterSegment, out bool stopX, out bool stopZ);
        // Position only: the next tick starts from the normal tick's motion (a collision still stops that axis).
        s.Velocity = new Vec3d(stopX ? 0 : v.X, v.Y, stopZ ? 0 : v.Z);
        if (plan.Mode != "ladder") s.FallDistance = fall;
        s.FallDamage = fallDamage;
        return done;
    }

    /// <summary>The plan's segments, each with the version's move; an axis a collision stopped goes nowhere new after it.</summary>
    private static int MoveSegments(IBlockAccess w, PhysicsState s, BoostPlan plan, Action<int>? afterSegment, out bool stopX, out bool stopZ)
    {
        int n = Math.Max(1, plan.Segments);
        double sx = plan.Ex / n, sy = plan.Ey / n, sz = plan.Ez / n;
        stopX = false;
        stopZ = false;
        int done = 0;
        for (int i = 0; i < n; i++)
        {
            var at = s.Position;
            if (!Move(w, s, sx, sy, sz)) break;
            done++;
            // A collision stops that axis (vanilla zeroes its motion); the rest of the extra move goes nowhere new.
            stopX |= Stopped(sx, s.Position.X - at.X);
            stopZ |= Stopped(sz, s.Position.Z - at.Z);
            if (i < n - 1) afterSegment?.Invoke(i);
            if (stopX && stopZ) break;
            if (stopX) sx = 0;
            if (stopZ) sz = 0;
        }
        return done;
    }

    /// <summary>A segment's axis moved other than asked.</summary>
    private static bool Stopped(double step, double moved) => step != 0 && Math.Abs(moved - step) > 1e-9;

    /// <summary>Runs the plan on a copy: true when every segment moved (the last may stop at a wall).</summary>
    private static bool Trial(IBlockAccess w, PhysicsState t, BoostPlan plan)
    {
        var start = t.Position;
        int applied = Apply(w, t, plan);
        if (applied == 0) return false;
        var moved = t.Position - start;
        // A step-up is fine; anything that went down further than the ground probe (it can't: Entity.move clips) or backwards is not.
        return moved.X * plan.Ex + moved.Z * plan.Ez >= -1e-9 && moved.Y > -0.6;
    }

    /// <summary>Ground within a step below the box (the body won't hang over a drop after the extra move).</summary>
    public static bool Supported(IBlockAccess w, PhysicsState s)
    {
        var b = s.BoundingBox;
        var probe = new AABB(b.MinX, b.MinY - w.Physics.StepHeight, b.MinZ, b.MaxX, b.MinY - 0.001, b.MaxZ);
        return w.Physics.GetCollidingBoxes(w, probe, Boxes).Count > 0;
    }

    /// <summary>
    /// Is there an edge (no floor within a block below) or a liquid within about a block past where the extra move
    /// (<paramref name="extra"/>) would end, in the direction of travel? Then the
    /// factor falls to <see cref="EdgeFactor"/>, so the follower's braking and turns work as at normal speed.
    /// </summary>
    public static bool HazardAhead(IBlockAccess w, PhysicsState s, double dirX, double dirZ, double extra = 0)
    {
        var b = s.BoundingBox;
        foreach (double ahead in new[] { extra + 0.5, extra + 1.2 })
        {
            var at = b.Offset(dirX * ahead, 0, dirZ * ahead);
            var floor = new AABB(at.MinX, at.MinY - 1.0, at.MinZ, at.MaxX, at.MinY - 0.001, at.MaxZ);
            if (w.Physics.GetCollidingBoxes(w, floor, Boxes).Count == 0) return true;
            if (AnyLiquid(w, new AABB(at.MinX, at.MinY - 1.0, at.MinZ, at.MaxX, at.MaxY, at.MaxZ))) return true;
        }
        return false;
    }

    [ThreadStatic] private static List<AABB>? _boxes;
    private static List<AABB> Boxes => _boxes ??= new List<AABB>(16);

    private static bool AnyLiquid(IBlockAccess w, AABB bb) => Cells(w, bb, BlockShapes.IsLiquid);
    private static bool InWater(IBlockAccess w, AABB bb) => Cells(w, bb.Grow(0, -0.4, 0), BlockShapes.IsWater);
    private static bool LavaNear(IBlockAccess w, AABB bb) => Cells(w, bb, BlockShapes.IsLava);

    private static bool Cells(IBlockAccess w, AABB bb, Func<int, bool> test)
    {
        int x0 = (int)Math.Floor(bb.MinX), x1 = (int)Math.Floor(bb.MaxX - 1e-7);
        int y0 = (int)Math.Floor(bb.MinY), y1 = (int)Math.Floor(bb.MaxY - 1e-7);
        int z0 = (int)Math.Floor(bb.MinZ), z1 = (int)Math.Floor(bb.MaxZ - 1e-7);
        for (int x = x0; x <= x1; x++)
        for (int y = y0; y <= y1; y++)
        for (int z = z0; z <= z1; z++)
            if (test(w.GetBlock(x, y, z))) return true;
        return false;
    }
}
