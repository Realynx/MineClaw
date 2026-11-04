using System.Diagnostics;
using MineClaw.Render;

namespace MineClaw.MemoryExplorer.LiveHive;

/// <summary>Something a <see cref="FrameView"/> shows: frames produced off the UI thread, taken on it.</summary>
public interface IFrameSource
{
    /// <summary>Raised (any thread) when a new frame is pending.</summary>
    event Action? FrameReady;
    /// <summary>The newest pending frame, or null; valid until the next call (UI thread).</summary>
    FrameOut? TakeFrame();
    /// <summary>The view's size in device pixels (the producer renders at or below it).</summary>
    void SetTarget(int w, int h);
    /// <summary>False while the view is detached or hidden: the producer idles.</summary>
    bool Visible { get; set; }
}

/// <summary>A triple buffer between one producer thread and the UI (no waiting on either side).</summary>
public sealed class FrameSwap : IFrameSource
{
    private readonly object _lock = new();
    private FrameOut _back = new(), _pending = new(), _front = new();
    private bool _has;
    private volatile int _w = 480, _h = 360;
    private volatile bool _visible;

    public event Action? FrameReady;
    public int TargetW => _w;
    public int TargetH => _h;
    public bool Visible { get => _visible; set => _visible = value; }
    public void SetTarget(int w, int h) { _w = Math.Max(32, w); _h = Math.Max(24, h); }

    /// <summary>The buffer to fill (producer thread).</summary>
    public FrameOut Back => _back;

    public void Publish()
    {
        bool signal;
        lock (_lock)
        {
            (_back, _pending) = (_pending, _back);
            signal = !_has;
            _has = true;
        }
        if (signal) FrameReady?.Invoke();
    }

    public FrameOut? TakeFrame()
    {
        lock (_lock)
        {
            if (!_has) return null;
            (_front, _pending) = (_pending, _front);
            _has = false;
            return _front;
        }
    }

    /// <summary>0xRRGGBB → opaque BGRA (0xFFRRGGBB little-endian = B,G,R,A bytes).</summary>
    public static void CopyOpaque(uint[] src, int[] dst, int count = int.MaxValue)
    {
        int n = Math.Min(count, Math.Min(src.Length, dst.Length));
        for (int i = 0; i < n; i++) dst[i] = unchecked((int)(src[i] | 0xFF000000));
    }
}

/// <summary>A CPU frame's size: the target scaled down to at most a pixel budget (same aspect), never under 64×48.</summary>
public static class FrameFit
{
    public static (int W, int H) Fit(int w, int h, double maxPixels)
    {
        double px = (double)w * h;
        if (px > maxPixels) { double f = Math.Sqrt(maxPixels / px); w = (int)(w * f); h = (int)(h * f); }
        return (Math.Max(64, w), Math.Max(48, h));
    }
}

/// <summary>One bot's live eye feed, rendered locally from the cached world at the bot's pose.</summary>
public sealed class EyeTile(string bot, HiveWorldCache cache)
{
    public string Bot { get; } = bot;
    public FrameSwap Frames { get; } = new();
    internal readonly HiveVoxels Vox = new(cache);
    internal EyeRenderer? Eye;
    internal (double, double, double, float, float, int) LastPose;
    internal long LastVersion = -1, LastData = -1;
    internal long LastDrawMs;
    public double RasterMs { get; internal set; }
    /// <summary>When the tile last got a new frame (any thread).</summary>
    public DateTime LastFrameAt { get; internal set; }
}

/// <summary>
/// Renders every visible <see cref="EyeTile"/> on one background thread at up to <see cref="TileFps"/> each (a tile redraws
/// only when its bot moved or looked, the world cache changed, or a second passed), each raster on a few cores.
/// </summary>
public sealed class TileRenderer(HiveWorldCache cache, Func<TextureAtlas?> atlas) : IDisposable
{
    private Thread? _thread;
    private volatile bool _run;

    public Func<IReadOnlyList<EyeTile>> Tiles { get; set; } = () => [];
    public Func<IReadOnlyList<BotDto>> Bots { get; set; } = () => [];
    public Func<IReadOnlyList<EntityDto>> Entities { get; set; } = () => [];
    public long DataVersion;
    public int TileFps { get; set; } = 12;
    public int ThreadsPerTile { get; set; } = Math.Clamp(Environment.ProcessorCount / 3, 1, 8);
    public int MaxTilePixels { get; set; } = 640 * 480;
    public int MaxDistance { get; set; } = 48;

    public void Start()
    {
        if (_thread != null) return;
        _run = true;
        _thread = new Thread(Loop) { IsBackground = true, Name = "livehive-tiles", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    public void Stop()
    {
        _run = false;
        _thread?.Join(TimeSpan.FromSeconds(2));
        _thread = null;
    }

    private void Loop()
    {
        var clock = Stopwatch.StartNew();
        while (_run)
        {
            long t0 = clock.ElapsedMilliseconds;
            try
            {
                var tiles = Tiles();
                var bots = Bots();
                foreach (var t in tiles)
                {
                    if (!_run) return;
                    if (!t.Frames.Visible) continue;
                    var b = bots.FirstOrDefault(x => x.Key.Equals(t.Bot, StringComparison.OrdinalIgnoreCase) || x.Name.Equals(t.Bot, StringComparison.OrdinalIgnoreCase));
                    if (b == null) continue;
                    try { Draw(t, b, clock.ElapsedMilliseconds); }
                    catch (Exception ex) { Problem(ex); }
                }
            }
            catch (Exception ex) { Problem(ex); } // the thread never dies: a dead renderer is a frozen tile
            long spent = clock.ElapsedMilliseconds - t0, period = 1000 / Math.Max(1, TileFps);
            Thread.Sleep((int)Math.Max(5, period - spent));
        }
    }

    internal void Draw(EyeTile t, BotDto b, long nowMs)
    {
        var (w, h) = FrameFit.Fit(t.Frames.TargetW, t.Frames.TargetH, MaxTilePixels);
        var pose = (b.X, b.Y + 1.62, b.Z, b.Yaw ?? 0f, b.Pitch ?? 20f, b.Dim);
        long data = Interlocked.Read(ref DataVersion);
        bool sizeChanged = t.Eye == null || t.Eye.Width != w || t.Eye.Height != h;
        if (!sizeChanged && UpToDate(t, pose, nowMs)) return;
        if (sizeChanged) t.Eye = new EyeRenderer { Width = w, Height = h, MaxDistance = MaxDistance, MaxThreads = ThreadsPerTile };
        var eye = t.Eye!;
        t.LastPose = pose; t.LastVersion = cache.Version; t.LastData = data; t.LastDrawMs = nowMs;
        var now = DateTimeOffset.UtcNow;
        var cp = new CamPose(pose.X, pose.Item2, pose.Z, pose.Item4, pose.Item5, b.Dim);
        t.Vox.Prepare(b.Dim, (int)Math.Floor(b.X), (int)Math.Floor(b.Z), eye.MaxDistance / 16 + 1, now);
        var fr = eye.Raster(SceneFor(t, b, cp, TileEntities(b)));
        t.RasterMs = eye.LastMs;
        var cam = new ViewProjector(cp, w, h, eye.FovDegrees);
        var labels = OverlayPainter.Paint(fr, cam, Overlay.Bots, null, Bots().Where(o => o.Name != b.Name).ToList(), Entities(), t.Vox, now, eye.MaxDistance);
        var back = t.Frames.Back;
        if (back.Px.Length != w * h) back.Px = new int[w * h];
        FrameSwap.CopyOpaque(fr.Px, back.Px);
        back.W = w; back.H = h; back.Labels = labels; back.Pose = cp; back.RasterMs = eye.LastMs;
        t.Frames.Publish();
        t.LastFrameAt = DateTime.UtcNow;
    }

    /// <summary>Same pose, same world and drawn under a second ago: nothing to redraw.</summary>
    private bool UpToDate(EyeTile t, (double, double, double, float, float, int) pose, long nowMs) =>
        pose == t.LastPose && cache.Version == t.LastVersion && nowMs - t.LastDrawMs < 1000;

    /// <summary>What the tile's bot sees besides blocks: the other bots, players, mobs and drops in its dimension.</summary>
    private List<VisionEntity> TileEntities(BotDto b)
    {
        var ents = new List<VisionEntity>();
        foreach (var o in Bots()) if (o.Dim == b.Dim && o.Name != b.Name) ents.Add(new VisionEntity("hive", o.Key, o.X, o.Y, o.Z) { Yaw = o.Yaw });
        foreach (var e in Entities()) if (e.Dim == b.Dim && e.Kind is "player" or "mob" or "item") ents.Add(Gpu.FrameScene.Vision(e));
        return ents;
    }

    private EyeScene SceneFor(EyeTile t, BotDto b, CamPose cp, List<VisionEntity> ents) => new()
    {
        World = t.Vox, Atlas = atlas(), EyeX = cp.X, EyeY = cp.Y, EyeZ = cp.Z, Yaw = cp.Yaw, Pitch = cp.Pitch, Time = cache.TimeNow(b.Dim) ?? 6000, Nether = b.Dim == -1,
        Entities = ents, Viewer = b.Key, Weather = cache.WeatherIn(b.Dim), PrecipTick = Environment.TickCount64 / 50,
    };

    public string? LastError { get; private set; }

    private void Problem(Exception ex)
    {
        var msg = $"{ex.GetType().Name}: {ex.Message}";
        if (msg == LastError) return;
        LastError = msg;
        System.Diagnostics.Trace.WriteLine("livehive tiles: " + ex);
    }

    public void Dispose() => Stop();
}
