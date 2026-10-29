using System.Diagnostics;
using MineClaw.Render;

namespace MineClaw.MemoryExplorer.LiveHive;

/// <summary>A finished frame: BGRA pixels (0xFFRRGGBB as int, so it copies straight into a bitmap), its size, and the labels.</summary>
public sealed class FrameOut
{
    public int[] Px = [];
    public int W, H;
    public List<OverlayLabel> Labels = [];
    public CamPose Pose;
    public double RasterMs;
    /// <summary>Oldest input this frame includes (ms on <see cref="FreecamStats.NowMs"/>; 0 = none).</summary>
    public double InputMs;
}

/// <summary>
/// The free camera's renderer: a dedicated background thread raycasts the cached hive world (<see cref="HiveVoxels"/> +
/// <see cref="EyeRenderer"/> on every core), paints the overlays, and hands finished frames over through a triple buffer
/// (render → pending → front) so neither side waits. It integrates movement from the held keys every frame, redraws only when
/// the camera, the world cache, the overlays or the bots changed (or every 500 ms), drops to <see cref="MovingScale"/> of the
/// target resolution while moving and renders the full size once still, capped at <see cref="MaxFps"/>.
/// </summary>
public sealed class FreecamEngine : IFrameSource, IDisposable
{
    private readonly HiveWorldCache _cache;
    private readonly Func<TextureAtlas?> _atlas;
    private readonly HiveVoxels _vox;
    private readonly Dictionary<(int, int), EyeRenderer> _eyes = new();
    private readonly object _swap = new();
    private FrameOut _back = new(), _pending = new(), _front = new();
    private bool _hasPending;
    private Thread? _thread;
    private volatile bool _run, _visible;
    private int _tw = 960, _th = 540;
    private volatile int _keys; // bit 0 W, 1 S, 2 A, 3 D, 4 E(up), 5 Q(down), 6 fast
    private long _lastLookRev, _lookAt;
    private string? _lastError;

    public FreecamEngine(HiveWorldCache cache, FlyCamera camera, Func<TextureAtlas?> atlas)
    {
        _cache = cache;
        Camera = camera;
        _atlas = atlas;
        _vox = new HiveVoxels(cache);
    }

    public FlyCamera Camera { get; }
    public Func<OverlaysDto?> Overlays { get; set; } = () => null;
    public Func<IReadOnlyList<BotDto>> Bots { get; set; } = () => [];
    public Func<IReadOnlyList<EntityDto>> Entities { get; set; } = () => [];
    /// <summary>Bumped by the owner when bots/entities/overlays changed.</summary>
    public long DataVersion;

    public Overlay Flags { get; set; } = Overlay.Default;
    public int MaxDistance { get; set; } = 64;
    public double MovingScale { get; set; } = 0.6;
    public int MaxFps { get; set; } = 60;
    /// <summary>Largest frame rendered (pixels); a bigger view is upscaled.</summary>
    public int MaxPixels { get; set; } = 1280 * 720;
    // Leave two cores for the UI thread and the compositor: a raster on every core starved them (input and pictures stalled).
    public int Threads { get; set; } = Math.Clamp(Environment.ProcessorCount - 2, 1, 64);

    // stats (render thread writes, UI reads)
    public double Fps { get; private set; }
    public double RasterMs { get; private set; }
    public double FrameMs { get; private set; }
    public int LastW { get; private set; }
    public int LastH { get; private set; }

    /// <summary>A new frame is pending (raised on the render thread; take it with <see cref="TakeFrame"/> on the UI thread).</summary>
    public event Action? FrameReady;

    public void SetTarget(int w, int h)
    {
        _tw = Math.Max(64, w);
        _th = Math.Max(48, h);
    }

    public void SetKeys(bool w, bool s, bool a, bool d, bool up, bool down, bool fast) =>
        _keys = (w ? 1 : 0) | (s ? 2 : 0) | (a ? 4 : 0) | (d ? 8 : 0) | (up ? 16 : 0) | (down ? 32 : 0) | (fast ? 64 : 0);

    public bool Visible { get => _visible; set => _visible = value; }

    public void Start()
    {
        if (_thread != null) return;
        _run = true;
        // Normal priority, like the UI thread it feeds (the raster's pool helpers run at Normal anyway): a below-normal frame
        // thread is starved outright whenever normal-priority work fills every core (a build, a test run, the hive itself), and
        // Windows only boosts a starved thread after seconds, so the camera froze 0.3–10 s under load while the raster's own
        // cost hadn't changed. The core budget (Threads) is what leaves the UI and compositor their share.
        _thread = new Thread(Loop) { IsBackground = true, Name = "livehive-freecam", Priority = ThreadPriority.Normal };
        _thread.Start();
    }

    public void Stop()
    {
        _run = false;
        _thread?.Join(TimeSpan.FromSeconds(2));
        _thread = null;
    }

    /// <summary>The newest frame (UI thread); null when nothing new. Valid until the next call.</summary>
    public FrameOut? TakeFrame()
    {
        lock (_swap)
        {
            if (!_hasPending) return null;
            (_front, _pending) = (_pending, _front);
            _hasPending = false;
            return _front;
        }
    }

    [System.Runtime.InteropServices.DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint ms);
    [System.Runtime.InteropServices.DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint ms);

    /// <summary>Sleeps until <paramref name="untilMs"/> on <paramref name="clock"/> with ~1 ms precision (the timer period is
    /// raised to 1 ms while the loop runs; Windows' default 15.6 ms quantum used to cap this loop near 30 fps).</summary>
    private static void SleepUntil(Stopwatch clock, double untilMs)
    {
        while (true)
        {
            double left = untilMs - clock.Elapsed.TotalMilliseconds;
            if (left <= 0) return;
            if (left > 1.5) Thread.Sleep(1);
            else Thread.SpinWait(200);
        }
    }

    private void Loop()
    {
        bool hiRes = OperatingSystem.IsWindows();
        if (hiRes) try { timeBeginPeriod(1); } catch (Exception) { hiRes = false; }
        try { LoopCore(); }
        finally { if (hiRes) try { timeEndPeriod(1); } catch (Exception) { } }
    }

    private void LoopCore()
    {
        var clock = Stopwatch.StartNew();
        double last = clock.Elapsed.TotalSeconds, lastDraw = -1, fpsT = last;
        int frames = 0;
        long camRev = -1, cacheVer = -1, dataVer = -1;
        int lastW = 0, lastH = 0;
        bool wasMoving = false;
        while (_run)
        {
            try { Iteration(); }
            catch (Exception ex)
            {
                // Never let the render thread die (a frozen camera): log once, breathe, continue.
                if (ex.Message != _lastError) { _lastError = ex.Message; System.Diagnostics.Trace.WriteLine("livehive freecam: " + ex); }
                Thread.Sleep(20);
            }
        }

        void Iteration()
        {
            double now = clock.Elapsed.TotalSeconds, dt = Math.Min(0.1, now - last);
            last = now;
            if (!_visible) { Thread.Sleep(50); return; }
            int k = _keys;
            double fwd = ((k & 1) != 0 ? 1 : 0) - ((k & 2) != 0 ? 1 : 0), strafe = ((k & 8) != 0 ? 1 : 0) - ((k & 4) != 0 ? 1 : 0);
            double up = ((k & 16) != 0 ? 1 : 0) - ((k & 32) != 0 ? 1 : 0);
            bool moved = Camera.Move(fwd, strafe, up, dt, (k & 64) != 0);
            long rev = Camera.Revision;
            if (!moved && rev != _lastLookRev) { _lastLookRev = rev; _lookAt = clock.ElapsedMilliseconds; }
            bool moving = moved || clock.ElapsedMilliseconds - _lookAt < 160;
            double scale = moving ? MovingScale : 1;
            int w = (int)(_tw * scale), h = (int)(_th * scale);
            double px = (double)w * h;
            if (px > MaxPixels) { double f = Math.Sqrt(MaxPixels / px); w = (int)(w * f); h = (int)(h * f); }
            w = Math.Max(64, w); h = Math.Max(48, h);
            bool dirty = rev != camRev || _cache.Version != cacheVer || Interlocked.Read(ref DataVersion) != dataVer || w != lastW || h != lastH
                || (wasMoving && !moving) || now - lastDraw > 0.5;
            if (!dirty) { Thread.Sleep(1); return; } // ~1 ms: a look or key shows up on the next frame, not 15 ms later
            camRev = rev; cacheVer = _cache.Version; dataVer = Interlocked.Read(ref DataVersion); lastW = w; lastH = h; wasMoving = moving;
            double t0 = clock.Elapsed.TotalMilliseconds;
            try { Draw(w, h); }
            catch (Exception) { Thread.Sleep(50); return; }
            lastDraw = clock.Elapsed.TotalSeconds;
            FrameMs = clock.Elapsed.TotalMilliseconds - t0;
            FreecamStats.Shared.FrameMs.Add(FrameMs);
            frames++;
            if (now - fpsT >= 1) { Fps = frames / (now - fpsT); frames = 0; fpsT = now; }
            SleepUntil(clock, t0 + 1000.0 / Math.Max(1, MaxFps));
        }
    }

    private void Draw(int w, int h)
    {
        if (!_eyes.TryGetValue((w, h), out var eye))
        {
            if (_eyes.Count > 4) _eyes.Clear();
            _eyes[(w, h)] = eye = new EyeRenderer { Width = w, Height = h, MaxDistance = Math.Clamp(MaxDistance, 16, 128), MaxThreads = Threads };
        }
        long alloc0 = GC.GetAllocatedBytesForCurrentThread();
        double inputMs = Camera.TakeInputStamp();
        var pose = Camera.Pose;
        var now = DateTimeOffset.UtcNow;
        _vox.Prepare(pose.Dim, (int)Math.Floor(pose.X), (int)Math.Floor(pose.Z), eye.MaxDistance / 16 + 1, now);
        var bots = Bots();
        var ents = Entities();
        var flags = Flags;
        var visible = new List<VisionEntity>();
        if (flags.HasFlag(Overlay.Bots))
        {
            foreach (var b in bots) if (b.Dim == pose.Dim) visible.Add(new VisionEntity("hive", b.Key, b.X, b.Y, b.Z) { Yaw = b.Yaw });
            foreach (var e in ents) if (e.Dim == pose.Dim && e.Kind is "player" or "mob" or "item") visible.Add(Gpu.FrameScene.Vision(e));
        }
        var scene = new EyeScene
        {
            World = _vox, Atlas = _atlas(), EyeX = pose.X, EyeY = pose.Y, EyeZ = pose.Z, Yaw = pose.Yaw, Pitch = pose.Pitch, Time = _cache.TimeNow(pose.Dim) ?? 6000,
            Nether = pose.Dim == -1, Entities = visible, Viewer = "freecam",
        };
        var fr = eye.Raster(scene);
        RasterMs = eye.LastMs;
        var cam = new ViewProjector(pose, w, h, eye.FovDegrees);
        var labels = OverlayPainter.Paint(fr, cam, flags, Overlays(), bots, ents, _vox, now, eye.MaxDistance);
        var back = _back;
        if (back.Px.Length < w * h) back.Px = new int[Math.Max(w * h, _tw * _th)]; // grow-only: moving/still sizes share it
        FrameSwap.CopyOpaque(fr.Px, back.Px, w * h);
        back.W = w; back.H = h; back.Labels = labels; back.Pose = pose; back.RasterMs = RasterMs; back.InputMs = inputMs;
        LastW = w; LastH = h;
        var st = FreecamStats.Shared;
        st.RasterMs.Add(RasterMs);
        st.AllocBytes.Add(GC.GetAllocatedBytesForCurrentThread() - alloc0);
        st.Width = w; st.Height = h;
        bool signal;
        lock (_swap)
        {
            (_back, _pending) = (_pending, _back);
            signal = !_hasPending;
            _hasPending = true;
        }
        if (signal) FrameReady?.Invoke();
    }

    /// <summary>Renders one frame synchronously and returns it (benchmarks and tests; not while the thread runs).</summary>
    public FrameOut RenderOnce(int w, int h)
    {
        Draw(w, h);
        return TakeFrame()!;
    }

    public void Dispose() => Stop();
}
