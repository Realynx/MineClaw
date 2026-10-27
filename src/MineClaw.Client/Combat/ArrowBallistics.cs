using MineClaw.World;

namespace MineClaw.Client.Combat;

/// <summary>
/// b1.8.1 bow and arrow physics (ItemBow / EntityArrow):
/// <list type="bullet">
/// <item>Charge: <c>j</c> = ticks held; <c>f = j/20; f = (f² + 2f)/3</c>, capped at 1; below 0.1 nothing is shot.</item>
/// <item>Launch speed = f × 2 × 1.5 (3.0 blocks/tick at full charge), from the eye − 0.1 (and 0.16 sideways), with a
/// gaussian spread of 0.0075 per axis on the unit heading.</item>
/// <item>Per tick in air: position += motion, then motion × 0.99 and motionY − 0.05 (water: × 0.8).</item>
/// </list>
/// </summary>
public static class ArrowBallistics
{
    public const double Gravity = 0.05, Drag = 0.99, LaunchDrop = 0.1, FullChargeSpeed = 3.0;
    public const int FullChargeTicks = 20;

    /// <summary>Bow power for a draw held <paramref name="ticks"/> server ticks (0 = no arrow).</summary>
    public static double Power(int ticks)
    {
        double f = ticks / 20.0;
        f = (f * f + f * 2) / 3;
        if (f < 0.1) return 0;
        return Math.Min(1, f);
    }

    public static double Speed(int ticks) => Power(ticks) * FullChargeSpeed;

    /// <summary>Launch point for a player whose eye is at <paramref name="eye"/>.</summary>
    public static Vec3d LaunchPoint(Vec3d eye) => new(eye.X, eye.Y - LaunchDrop, eye.Z);

    /// <summary>Height of the arrow (relative to the launch point) when it has travelled <paramref name="horizontal"/>
    /// blocks horizontally, for an elevation of <paramref name="elevationDeg"/> (up = positive) and speed
    /// <paramref name="speed"/>; null if it never gets that far (falls below −64 first or stalls). Also returns flight ticks.</summary>
    public static (double Height, int Ticks)? HeightAt(double horizontal, double elevationDeg, double speed)
    {
        double a = elevationDeg * Math.PI / 180;
        double vh = Math.Cos(a) * speed, vy = Math.Sin(a) * speed;
        double h = 0, y = 0;
        for (int t = 1; t <= 200; t++)
        {
            double nh = h + vh, ny = y + vy;
            if (nh >= horizontal)
            {
                double k = vh <= 1e-9 ? 0 : (horizontal - h) / vh;
                return (y + vy * k, t);
            }
            h = nh;
            y = ny;
            vh *= Drag;
            vy = vy * Drag - Gravity;
            if (y < -64 || vh < 1e-4) return null;
        }
        return null;
    }

    /// <summary>
    /// Look angles (Minecraft yaw/pitch, pitch down = positive) to hit <paramref name="target"/> from <paramref name="eye"/>
    /// with an arrow of <paramref name="speed"/>: the low (direct-fire) solution, found by bisection on the elevation.
    /// Null when the target is out of range at this speed.
    /// </summary>
    public static (float Yaw, float Pitch, int Ticks)? Solve(Vec3d eye, Vec3d target, double speed)
    {
        if (speed <= 0) return null;
        var from = LaunchPoint(eye);
        var d = target - from;
        double horizontal = Math.Sqrt(d.X * d.X + d.Z * d.Z);
        float yaw = PlayerPhysics.YawPitchTo(eye, target).Yaw;
        if (horizontal < 0.3) return (yaw, d.Y >= 0 ? -90f : 90f, 1);
        // Height at range rises with elevation up to the max-range angle (< 45° with drag): bisect below it.
        double lo = -85, hi = 44;
        if (HeightAt(horizontal, hi, speed) is not { } top || top.Height < d.Y) return null;
        for (int i = 0; i < 40; i++)
        {
            double mid = (lo + hi) / 2;
            var r = HeightAt(horizontal, mid, speed);
            if (r is null || r.Value.Height < d.Y) lo = mid; else hi = mid;
        }
        var final = HeightAt(horizontal, hi, speed);
        return (yaw, (float)-hi, final?.Ticks ?? 1);
    }

    /// <summary><see cref="Solve"/> with lead: aims where a target moving at <paramref name="velocity"/> (blocks/tick)
    /// will be when the arrow arrives (two refinement passes).</summary>
    public static (float Yaw, float Pitch, int Ticks)? SolveLeading(Vec3d eye, Vec3d target, Vec3d velocity, double speed)
    {
        var aim = Solve(eye, target, speed);
        for (int i = 0; i < 2 && aim is { } a; i++)
            aim = Solve(eye, target + velocity * a.Ticks, speed) ?? aim;
        return aim;
    }
}
