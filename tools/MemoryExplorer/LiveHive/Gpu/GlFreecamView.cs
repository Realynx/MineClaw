using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Avalonia.Threading;
using MineClaw.Render;

namespace MineClaw.MemoryExplorer.LiveHive.Gpu;

/// <summary>
/// The GPU free camera's state shared by the GL control and its overlay: the section store and its mesh worker (off the UI
/// thread), the frame scene, the held keys and the label snapshot the overlay draws.
/// </summary>
public sealed class GpuFreecam : IFreecamInput, IDisposable
{
    private volatile int _keys;
    private readonly object _labelLock = new();
    private List<LabelItem> _labelsFront = new(128), _labelsBack = new(128);

    public GpuFreecam(HiveWorldCache cache, FlyCamera camera, Func<TextureAtlas?> atlas)
    {
        Cache = cache;
        Camera = camera;
        Atlas = atlas;
        Store = new SectionStore(cache) { ModelsOf = () => atlas()?.Models };
        Worker = new MeshWorker(Store, cache);
        Scene = new FrameScene(cache);
    }

    public HiveWorldCache Cache { get; }
    public FlyCamera Camera { get; }
    public Func<TextureAtlas?> Atlas { get; }
    public SectionStore Store { get; }
    public MeshWorker Worker { get; }
    public FrameScene Scene { get; }
    public int MaxDistance { get => Scene.MaxDistance; set => Scene.MaxDistance = Math.Clamp(value, 32, 192); }
    /// <summary>Per-frame GPU upload budget.</summary>
    public double UploadBudgetMs { get; set; } = 2;
    public int MaxUploadsPerFrame { get; set; } = 16; // a big ingest spreads over frames instead of one long hitch
    public double Fps { get; internal set; }
    public bool ShowStats { get; set; }
    public string GlInfo { get; internal set; } = "";
    /// <summary>Save the next GPU frame as a PNG here (profiling).</summary>
    public string? ShotPath { get; set; }

    public bool Ready => Scene.Sections.Count > 0 && Worker.Idle && Store.Ready.IsEmpty;

    /// <summary>The camera has the keyboard and mouse (a click engages it, Esc or losing focus releases it).</summary>
    public bool Engaged { get; internal set; }
    /// <summary>"reconnecting…" / "world stale 12 s" while the hive link is unhealthy (the HUD shows it); null when fine.</summary>
    public string? LinkNote { get; set; }
    /// <summary>Why the last frame failed, if one did (shown in the HUD; the loop keeps running).</summary>
    public string? LastError { get; internal set; }
    public long FramesFailed;

    public void SetKeys(bool w, bool s, bool a, bool d, bool up, bool down, bool fast) =>
        _keys = (w ? 1 : 0) | (s ? 2 : 0) | (a ? 4 : 0) | (d ? 8 : 0) | (up ? 16 : 0) | (down ? 32 : 0) | (fast ? 64 : 0);

    public void Poke() { }

    /// <summary>Integrates movement from the held keys over <paramref name="dt"/> seconds (per frame, so speed is frame-rate independent).</summary>
    public bool Step(double dt)
    {
        int k = _keys;
        double fwd = ((k & 1) != 0 ? 1 : 0) - ((k & 2) != 0 ? 1 : 0), strafe = ((k & 8) != 0 ? 1 : 0) - ((k & 4) != 0 ? 1 : 0);
        double up = ((k & 16) != 0 ? 1 : 0) - ((k & 32) != 0 ? 1 : 0);
        return Camera.Move(fwd, strafe, up, Math.Min(dt, 0.1), (k & 64) != 0);
    }

    internal void PublishLabels(List<LabelItem> src)
    {
        lock (_labelLock)
        {
            _labelsBack.Clear();
            _labelsBack.AddRange(src);
            (_labelsFront, _labelsBack) = (_labelsBack, _labelsFront);
        }
    }

    internal void CopyLabels(List<LabelItem> into)
    {
        lock (_labelLock)
        {
            into.Clear();
            into.AddRange(_labelsFront);
        }
    }

    public void Start() => Worker.Start();
    public void Stop() => Worker.Stop();
    public void Dispose() => Worker.Dispose();
}

/// <summary>
/// The free camera on the GPU (Avalonia <see cref="OpenGlControlBase"/>): renders every compositor frame (the display rate),
/// integrating WASD per frame with the real frame time and reading mouse look straight from the camera, so input shows on the
/// next frame. Chunk meshes arrive from the mesh worker and are uploaded within a small per-frame budget. Raises
/// <see cref="GlFailed"/> when GL is unavailable (no context within a few seconds, too old, a shader error): the host then shows
/// the CPU raycaster instead.
/// </summary>
public sealed class GlFreecamView : OpenGlControlBase, Avalonia.Rendering.ICustomHitTest
{
    /// <summary>The GL surface draws nothing Avalonia can hit-test, so without this clicks fell through: no focus, no keys.</summary>
    public bool HitTest(Point point) => new Rect(Bounds.Size).Contains(point);

    public static readonly StyledProperty<GpuFreecam?> ModelProperty = AvaloniaProperty.Register<GlFreecamView, GpuFreecam?>(nameof(Model));

    private GlFreecamRenderer? _r;
    private bool _failed, _inited;
    private Point? _drag;
    private readonly HashSet<Key> _held = [];
    private double _lastFrame, _fpsT;
    private int _fpsN;
    private DispatcherTimer? _watchdog;

    public GlFreecamView()
    {
        Focusable = true;
        Cursor = new Cursor(StandardCursorType.Cross);
    }

    public GpuFreecam? Model { get => GetValue(ModelProperty); set => SetValue(ModelProperty, value); }

    /// <summary>GL could not be used (reason); raised once, on the UI thread.</summary>
    public event Action<string>? GlFailed;
    /// <summary>A frame was drawn (the overlay redraws its labels).</summary>
    public event Action? FrameRendered;
    public event Action<double>? SpeedChanged;
    /// <summary>F3 pressed.</summary>
    public event Action? StatsToggled;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (IsVisible) Model?.Start();
        _watchdog?.Stop();
        _watchdog = new DispatcherTimer(TimeSpan.FromSeconds(4), DispatcherPriority.Background, (_, _) =>
        {
            _watchdog?.Stop();
            if (!_inited && IsEffectivelyVisible) Fail("no OpenGL context (the platform is not rendering through GL)");
        });
        _watchdog.Start();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != IsVisibleProperty || VisualRoot == null) return;
        // Hidden (the CPU fallback is showing): no meshing, no frames.
        if (IsVisible) { Model?.Start(); RequestNextFrameRendering(); }
        else Model?.Stop();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _watchdog?.Stop();
        Model?.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    private void Fail(string why)
    {
        if (_failed) return;
        _failed = true;
        Dispatcher.UIThread.Post(() => GlFailed?.Invoke(why));
    }

    protected override void OnOpenGlInit(GlInterface gl) => TryInit(gl);

    private bool TryInit(GlInterface gl)
    {
        if (Model is not { } m) return false;
        try
        {
            _r = new GlFreecamRenderer();
            _r.Init(gl, GlVersion, m.Atlas());
            m.GlInfo = _r.Info;
            m.Scene.Textured = _r.Textured;
            m.Store.RequestResend();
            _inited = true;
            _initFailures = 0;
            return true;
        }
        catch (Exception ex)
        {
            _r = null;
            // A bad shader or a too-old context won't fix itself: fall back to the CPU raycaster after a few tries.
            if (++_initFailures >= 3) Fail("OpenGL init failed: " + ex.Message);
            return false;
        }
    }

    private int _initFailures, _consecutiveFailures;

    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        if (_r != null && Model is { } m) _r.Release(m.Scene);
        _r?.Dispose();
        _r = null;
        // The store must re-send every section to the next context.
        Model?.Store.RequestResend();
    }

    protected override void OnOpenGlLost()
    {
        // Device reset / context loss: every GL object is gone. Forget them (no GL calls) and rebuild on the next frame.
        _r = null;
        if (Model is { } m) { m.Scene.Sections.Clear(); m.Store.RequestResend(); m.LastError = "GL context lost: rebuilding"; }
        base.OnOpenGlLost();
        Dispatcher.UIThread.Post(RequestNextFrameRendering);
    }

    protected override void OnOpenGlRender(GlInterface gl, int fb)
    {
        try { RenderFrame(gl, fb); }
        finally
        {
            // The loop must never stop: an early return or an exception still asks for the next frame.
            if (!_failed && IsEffectivelyVisible) RequestNextFrameRendering();
        }
    }

    private void RenderFrame(GlInterface gl, int fb)
    {
        if (Model is not { } m) return;
        if (_r == null && !TryInit(gl)) return;
        try { DrawFrame(gl, fb, m, _r!); }
        catch (Exception ex)
        {
            OnFrameFailed(m, ex);
            return;
        }
        FrameRendered?.Invoke();
    }

    /// <summary>One frame: step the camera, follow the atlas and the sky, aim the mesher, upload, draw, then the frame stats.</summary>
    private void DrawFrame(GlInterface gl, int fb, GpuFreecam m, GlFreecamRenderer r)
    {
        double t0 = FreecamStats.NowMs;
        var st = FreecamStats.Shared;
        double dt = _lastFrame == 0 ? 0 : (t0 - _lastFrame) / 1000.0;
        _lastFrame = t0;
        m.Step(dt);
        double input = m.Camera.TakeInputStamp();
        var pose = m.Camera.Pose;
        r = FollowAtlas(gl, m, r);
        int sub = UpdateSky(m, pose);
        m.Worker.Aim(pose.Dim, (int)Math.Floor(pose.X) >> 4, (int)Math.Floor(pose.Z) >> 4, m.MaxDistance / 16 + 1, r.Textured, sub);
        r.Ingest(m.Store, m.Scene, m.UploadBudgetMs, m.MaxUploadsPerFrame);
        double scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        int w = Math.Max(16, (int)(Bounds.Width * scale)), h = Math.Max(16, (int)(Bounds.Height * scale));
        long alloc0 = GC.GetAllocatedBytesForCurrentThread();
        m.Scene.Prepare(pose, w, h, DateTimeOffset.UtcNow);
        if (m.ShotPath is { } shot) { m.ShotPath = null; r.ShotPath = shot; }
        st.DrawCalls = r.Draw(m.Scene, fb, w, h);
        st.Renderer = "gl";
        st.Width = w; st.Height = h;
        m.PublishLabels(m.Scene.Labels);
        st.AllocBytes.Add(GC.GetAllocatedBytesForCurrentThread() - alloc0);
        double spent = FreecamStats.NowMs - t0;
        st.FrameMs.Add(spent);
        st.PresentMs.Add(spent);
        st.OnPresented(input);
        Interlocked.Increment(ref st.Frames);
        CountFps(m, t0);
        _consecutiveFailures = 0;
    }

    /// <summary>
    /// Textures arrived after the context, or the hive's version changed (b1.8.1 ↔ 1.12.2 models): rebuild the atlas texture and
    /// every mesh (the worker re-meshes with the new models on its next pass). The renderer to draw with.
    /// </summary>
    private GlFreecamRenderer FollowAtlas(GlInterface gl, GpuFreecam m, GlFreecamRenderer r)
    {
        if (m.Atlas() is not { } atlas || ReferenceEquals(atlas, r.Atlas)) return r;
        var nr = new GlFreecamRenderer();
        nr.Init(gl, GlVersion, atlas);
        r.Release(m.Scene);
        r.Dispose();
        _r = nr;
        m.Scene.Textured = true;
        m.Store.RequestResend();
        return nr;
    }

    /// <summary>The sky darkening (none in the nether or without a time) and the weather of the camera's dimension.</summary>
    private static int UpdateSky(GpuFreecam m, CamPose pose)
    {
        long? time = m.Cache.TimeNow(pose.Dim);
        var wx = m.Cache.WeatherIn(pose.Dim);
        int sub = pose.Dim == -1 || time is not { } t ? 0 : Lighting.SkylightSubtracted(t, wx.Rain, wx.Thunder);
        m.Scene.SkySubtracted = sub;
        m.Scene.Weather = wx;
        return sub;
    }

    private void CountFps(GpuFreecam m, double t0)
    {
        _fpsN++;
        if (t0 - _fpsT >= 1000) { m.Fps = _fpsN * 1000.0 / (t0 - _fpsT); _fpsN = 0; _fpsT = t0; }
    }

    /// <summary>
    /// One bad frame (a racing eviction, a driver hiccup) must not end the GPU camera: log it once, keep going; only a persistent
    /// failure (two seconds of nothing but errors) falls back to the CPU raycaster.
    /// </summary>
    private void OnFrameFailed(GpuFreecam m, Exception ex)
    {
        Interlocked.Increment(ref m.FramesFailed);
        if (m.LastError == null || !m.LastError.EndsWith(ex.Message, StringComparison.Ordinal))
        {
            m.LastError = $"frame failed: {ex.GetType().Name}: {ex.Message}";
            System.Diagnostics.Trace.WriteLine("livehive gl: " + ex);
        }
        if (++_consecutiveFailures > 240) Fail("OpenGL keeps failing: " + ex.Message);
    }

    // ------------------------------------------------------------------ input

    /// <summary>Takes the keyboard and mouse (click). Raises <see cref="EngagedChanged"/>.</summary>
    public void Engage()
    {
        if (Model is { } m && !m.Engaged) { m.Engaged = true; EngagedChanged?.Invoke(true); }
        Focus(NavigationMethod.Pointer);
        RequestNextFrameRendering();
    }

    /// <summary>Lets go (Esc, focus lost): held keys are released so nothing drifts.</summary>
    public void Release()
    {
        _held.Clear();
        _drag = null;
        PushKeys();
        if (Model is { } m && m.Engaged) { m.Engaged = false; EngagedChanged?.Invoke(false); }
    }

    public event Action<bool>? EngagedChanged;

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Engage();
        _drag = e.GetPosition(this);
        e.Pointer.Capture(this);
        e.Handled = true;
        RequestNextFrameRendering();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_drag is not { } last || Model is not { } m) return;
        // Every coalesced sample since the last event, so fast flicks are not lost.
        var p = e.GetPosition(this);
        m.Camera.Look(p.X - last.X, p.Y - last.Y);
        _drag = p;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _drag = null;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _drag = null;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (Model is not { } m) return;
        m.Camera.Speed = Math.Clamp(m.Camera.Speed * Math.Pow(1.2, e.Delta.Y), 1, 200);
        SpeedChanged?.Invoke(m.Camera.Speed);
        e.Handled = true;
    }

    private static bool Movement(Key k) => FreecamKeys.IsMovement(k);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!e.Handled) HandleKey(e, down: true);
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (!e.Handled) HandleKey(e, down: false);
    }

    /// <summary>A key for the camera (from its own handlers, or the page's tunnel handler while engaged). Handled = consumed.</summary>
    public void HandleKey(KeyEventArgs e, bool down)
    {
        if (down && e.Key == Avalonia.Input.Key.F3) { StatsToggled?.Invoke(); e.Handled = true; return; }
        if (down && e.Key == Avalonia.Input.Key.Escape) { Release(); TopLevel.GetTopLevel(this)?.Focus(); e.Handled = true; return; }
        if (Model is not { Engaged: true }) return;
        if (!Movement(e.Key)) return;
        bool changed = down ? _held.Add(e.Key) : _held.Remove(e.Key);
        if (changed) PushKeys();
        e.Handled = true;
    }

    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
        Release();
    }

    private void PushKeys()
    {
        if (Model is not { } m) return;
        m.Camera.StampInput();
        bool H(Key a, Key b, Key c = Key.None) => _held.Contains(a) || _held.Contains(b) || c != Key.None && _held.Contains(c);
        m.SetKeys(H(Key.W, Key.Up), H(Key.S, Key.Down), H(Key.A, Key.Left), H(Key.D, Key.Right), H(Key.E, Key.Space, Key.PageUp),
            H(Key.Q, Key.PageDown), H(Key.LeftShift, Key.RightShift));
        RequestNextFrameRendering();
    }
}

/// <summary>
/// Draws over the GPU free camera: overlay labels (crisp text, cached per string), the crosshair, the position HUD and the
/// F3 stats. Redrawn after every GL frame; not hit-testable.
/// </summary>
public sealed class FreecamOverlay : Control
{
    public static readonly StyledProperty<GpuFreecam?> ModelProperty = AvaloniaProperty.Register<FreecamOverlay, GpuFreecam?>(nameof(Model));
    private readonly List<LabelItem> _labels = new(128);
    private readonly Dictionary<(string, uint), FormattedText> _text = new();
    private static readonly Typeface Face = new(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold);
    private static readonly Typeface Mono = new(new FontFamily("Cascadia Mono, Consolas, Menlo, monospace"));
    private static readonly IBrush ChipBg = new SolidColorBrush(Color.FromArgb(0xC8, 0x10, 0x13, 0x18));
    private static readonly IBrush HudBg = new SolidColorBrush(Color.FromArgb(0xA8, 0x0E, 0x10, 0x16));
    private static readonly IPen Cross = new Pen(new SolidColorBrush(Color.FromArgb(0xD0, 0xFF, 0xFF, 0xFF)), 1.5);
    private static readonly Comparison<LabelItem> FarFirst = (a, b) => b.Dist.CompareTo(a.Dist);
    private static readonly IPen EngagedPen = new Pen(new SolidColorBrush(Color.FromArgb(0xE0, 0x5E, 0xB8, 0xFF)), 3);
    private FormattedText? _hud;
    private double _hudAt;

    public FreecamOverlay() => IsHitTestVisible = false;

    private int _lastLabelCount = -1;
    private bool _wasEngaged;

    /// <summary>Whether the overlay must redraw after this GL frame: labels move with the camera (or just vanished), else only
    /// the HUD's 4 Hz refresh. Avoids re-recording an unchanged overlay at the display rate.</summary>
    public bool NeedsRedraw()
    {
        if (Model is not { } m) return false;
        int n = m.Scene.Labels.Count;
        bool redraw = n > 0 || _lastLabelCount != 0 || FreecamStats.NowMs - _hudAt > 250 || m.Engaged != _wasEngaged;
        _wasEngaged = m.Engaged;
        _lastLabelCount = n;
        return redraw;
    }

    public GpuFreecam? Model { get => GetValue(ModelProperty); set => SetValue(ModelProperty, value); }

    private FormattedText Text(string s, uint colour)
    {
        if (_text.TryGetValue((s, colour), out var ft)) return ft;
        if (_text.Count > 1024) _text.Clear();
        var c = Color.FromRgb((byte)(colour >> 16), (byte)(colour >> 8), (byte)colour);
        return _text[(s, colour)] = new FormattedText(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, 11.5, new SolidColorBrush(c));
    }

    public override void Render(DrawingContext ctx)
    {
        if (Model is not { } m) return;
        var b = new Rect(Bounds.Size);
        double scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        DrawLabels(ctx, m, scale);
        var c = b.Center;
        ctx.DrawLine(Cross, new Point(c.X - 7, c.Y), new Point(c.X + 7, c.Y));
        ctx.DrawLine(Cross, new Point(c.X, c.Y - 7), new Point(c.X, c.Y + 7));
        var hud = Hud(m);
        ctx.DrawRectangle(HudBg, null, new Rect(10, 10, hud.Width + 16, hud.Height + 10), 6, 6);
        ctx.DrawText(hud, new Point(18, 15));
        if (m.Engaged)
            ctx.DrawRectangle(null, EngagedPen, new Rect(b.Size).Deflate(1.5), 4, 4); // visible focus: the camera has the keys
        else DrawHint(ctx, b);
    }

    /// <summary>The 80 nearest labels as chips, far to near (occluded ones faded).</summary>
    private void DrawLabels(DrawingContext ctx, GpuFreecam m, double scale)
    {
        m.CopyLabels(_labels);
        _labels.Sort(FarFirst);
        int start = Math.Max(0, _labels.Count - 80);
        for (int i = start; i < _labels.Count; i++)
        {
            var l = _labels[i];
            var ft = Text(l.Text, l.Colour);
            double x = l.X / scale - ft.Width / 2, y = l.Y / scale - ft.Height - 4;
            using (ctx.PushOpacity(l.Occluded ? 0.55 : 1))
            {
                ctx.DrawRectangle(ChipBg, null, new Rect(x - 5, y - 2, ft.Width + 10, ft.Height + 4), 4, 4);
                ctx.DrawText(ft, new Point(x, y));
            }
        }
    }

    /// <summary>The HUD text (fps, size, position, facing, speed, link, errors, stats), rebuilt at most 4 times a second.</summary>
    private FormattedText Hud(GpuFreecam m)
    {
        double now = FreecamStats.NowMs;
        if (_hud != null && now - _hudAt <= 250) return _hud;
        _hudAt = now;
        var p = m.Camera.Pose;
        string text = string.Create(CultureInfo.InvariantCulture,
            $"{m.Fps,4:0} fps  gpu  {m.Scene.Width}x{m.Scene.Height}\n{p.X:0.0}, {p.Y:0.0}, {p.Z:0.0}  {(p.Dim == -1 ? "nether" : "overworld")}  facing {EyeRenderer.Compass(p.Yaw)} ({p.Yaw:0}°, {p.Pitch:0}°)  speed {m.Camera.Speed:0}");
        if (m.LinkNote is { } ln) text += "\n" + ln;
        if (m.LastError is { } err) text += $"\n{err} ({Interlocked.Read(ref m.FramesFailed)} frames)";
        if (m.ShowStats) text += "\n" + FreecamStats.Shared.Overlay(m.Fps) + $"\n{m.GlInfo}  entities {m.Scene.EntitiesDrawn}";
        return _hud = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Mono, 11.5, Brushes.White);
    }

    private void DrawHint(DrawingContext ctx, Rect b)
    {
        var hint = Text("click to control · WASD move · Q/E down/up · drag to look · wheel speed · Shift fast · Esc releases · F3 stats", 0xffffff);
        var r = new Rect((b.Width - hint.Width) / 2 - 10, b.Height - hint.Height - 22, hint.Width + 20, hint.Height + 10);
        ctx.DrawRectangle(HudBg, null, r, 6, 6);
        ctx.DrawText(hint, new Point(r.X + 10, r.Y + 5));
    }
}
