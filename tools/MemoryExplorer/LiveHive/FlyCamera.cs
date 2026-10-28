namespace MineClaw.MemoryExplorer.LiveHive;

/// <summary>A camera pose (b1.8.1 convention: yaw 0 = south/+z, 90 = west/−x; pitch +90 = straight down).</summary>
public readonly record struct CamPose(double X, double Y, double Z, float Yaw, float Pitch, int Dim);

/// <summary>
/// The free camera: position, look, speed. Mouse look comes from the UI thread, movement is integrated by the render thread
/// each frame from the held keys; both go through one lock. Forward/strafe follow the look's heading, up is world up.
/// </summary>
public sealed class FlyCamera
{
    private readonly object _lock = new();
    private CamPose _p = new(0.5, 80, 0.5, 0, 20, 0);
    private long _rev;
    private long _inputTicks;

    /// <summary>Marks an input event (look, key change) for latency measurement: keeps the oldest one not yet shown.</summary>
    public void StampInput() => Interlocked.CompareExchange(ref _inputTicks, System.Diagnostics.Stopwatch.GetTimestamp(), 0);

    /// <summary>The oldest unshown input's time (ms, <see cref="FreecamStats.NowMs"/> clock) and clears it; 0 = none.</summary>
    public double TakeInputStamp()
    {
        long t = Interlocked.Exchange(ref _inputTicks, 0);
        return t == 0 ? 0 : t * FreecamStats.TickMs;
    }

    /// <summary>Blocks per second (Shift = ×4).</summary>
    public double Speed { get; set; } = 12;
    /// <summary>Degrees per pixel of mouse movement.</summary>
    public double Sensitivity { get; set; } = 0.22;
    /// <summary>True once the camera has been placed (a jump, a bot snap, or the first bot seen).</summary>
    public bool Placed { get; private set; }
    /// <summary>Changes so far (the renderer redraws when it moves).</summary>
    public long Revision => Interlocked.Read(ref _rev);

    public CamPose Pose { get { lock (_lock) return _p; } }

    public void Set(CamPose p)
    {
        lock (_lock)
        {
            _p = p with { Pitch = Math.Clamp(p.Pitch, -89.9f, 89.9f), Yaw = Wrap(p.Yaw), Y = Math.Clamp(p.Y, -64, 320) };
            Placed = true;
            _rev++;
        }
    }

    public void SetDim(int dim)
    {
        lock (_lock) { _p = _p with { Dim = dim }; _rev++; }
    }

    public void Look(double dxPixels, double dyPixels)
    {
        StampInput();
        lock (_lock)
        {
            _p = _p with
            {
                Yaw = Wrap((float)(_p.Yaw + dxPixels * Sensitivity)),
                Pitch = (float)Math.Clamp(_p.Pitch + dyPixels * Sensitivity, -89.9, 89.9),
            };
            _rev++;
        }
    }

    /// <summary>Moves by the held keys: forward/strafe/up in −1..1 (strafe + = right), over <paramref name="dt"/> seconds.</summary>
    public bool Move(double forward, double strafe, double up, double dt, bool fast)
    {
        if (forward == 0 && strafe == 0 && up == 0 || dt <= 0) return false;
        lock (_lock)
        {
            double yaw = _p.Yaw * Math.PI / 180;
            double fx = -Math.Sin(yaw), fz = Math.Cos(yaw); // heading
            double rx = -fz, rz = fx;                        // right of the heading (yaw 0 → −x, west)
            double len = Math.Sqrt(forward * forward + strafe * strafe + up * up);
            double k = Speed * (fast ? 4 : 1) * dt / Math.Max(1, len);
            _p = _p with
            {
                X = _p.X + (fx * forward + rx * strafe) * k,
                Z = _p.Z + (fz * forward + rz * strafe) * k,
                Y = Math.Clamp(_p.Y + up * k, -64, 320),
            };
            _rev++;
            return true;
        }
    }

    private static float Wrap(float yaw) => ((yaw % 360) + 360) % 360;
}

/// <summary>
/// The raycaster's camera as a projector (the same basis as <see cref="MineClaw.Render.EyeRenderer"/>): world point → screen pixel
/// (unclipped) and its distance, plus per-pixel ray directions. Used for overlays drawn into the frame after the raster.
/// </summary>
public readonly struct ViewProjector
{
    public readonly double Ox, Oy, Oz, Fx, Fy, Fz, Rx, Ry, Rz, Ux, Uy, Uz, TanX, TanY;
    public readonly int W, H;

    public ViewProjector(CamPose p, int w, int h, float fovDegrees = 70f)
    {
        (Ox, Oy, Oz, W, H) = (p.X, p.Y, p.Z, w, h);
        double yaw = p.Yaw * Math.PI / 180, pitch = Math.Clamp(p.Pitch, -89.9f, 89.9f) * Math.PI / 180;
        Fx = -Math.Sin(yaw) * Math.Cos(pitch); Fy = -Math.Sin(pitch); Fz = Math.Cos(yaw) * Math.Cos(pitch);
        double rx = -Fz, rz = Fx, rl = Math.Sqrt(rx * rx + rz * rz);
        if (rl < 1e-9) { rx = -1; rz = 0; rl = 1; }
        Rx = rx / rl; Ry = 0; Rz = rz / rl;
        Ux = Ry * Fz - Rz * Fy; Uy = Rz * Fx - Rx * Fz; Uz = Rx * Fy - Ry * Fx;
        TanY = Math.Tan(fovDegrees * Math.PI / 360); TanX = TanY * w / h;
    }

    /// <summary>Distance along the view axis (negative = behind).</summary>
    public double Forward(double x, double y, double z) => (x - Ox) * Fx + (y - Oy) * Fy + (z - Oz) * Fz;

    /// <summary>Screen position (pixels, unclipped) and Euclidean distance; false when not in front of the eye.</summary>
    public bool Project(double x, double y, double z, out double sx, out double sy, out double dist)
    {
        double dx = x - Ox, dy = y - Oy, dz = z - Oz;
        double f = dx * Fx + dy * Fy + dz * Fz;
        dist = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        if (f < 0.05) { sx = sy = 0; return false; }
        double r = dx * Rx + dy * Ry + dz * Rz, u = dx * Ux + dy * Uy + dz * Uz;
        sx = (r / f / TanX + 1) / 2 * W;
        sy = (-u / f / TanY + 1) / 2 * H;
        return true;
    }

    /// <summary>The normalized ray through pixel (px, py), as the raycaster casts it.</summary>
    public (double X, double Y, double Z) Ray(int px, int py)
    {
        double sx = (2 * (px + 0.5) / W - 1) * TanX, sy = (1 - 2 * (py + 0.5) / H) * TanY;
        double dx = Fx + Rx * sx + Ux * sy, dy = Fy + Ry * sx + Uy * sy, dz = Fz + Rz * sx + Uz * sy;
        double l = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        return (dx / l, dy / l, dz / l);
    }
}
