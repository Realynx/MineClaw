using System.Globalization;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media;

namespace MineClaw.MemoryExplorer.LiveHive;

/// <summary>
/// The free camera: a <see cref="FrameView"/> over a <see cref="FreecamEngine"/> plus input. Click to focus; WASD move, Q/E down/up,
/// Shift = fast, drag (any button, pointer captured) to look, wheel = speed. Keys are handed to the engine, which integrates
/// movement on its own thread every frame, so motion stays smooth whatever the UI thread is doing.
/// </summary>
public sealed class FreecamView : FrameView
{
    private readonly HashSet<Key> _held = [];
    private Point? _drag;
    private static readonly Typeface Mono = new(new FontFamily("Cascadia Mono, Consolas, Menlo, monospace"));
    private static readonly IBrush HudBg = new SolidColorBrush(Color.FromArgb(0xA8, 0x0E, 0x10, 0x16));

    public FreecamView()
    {
        Focusable = true;
        Crosshair = true;
        Cursor = new Cursor(StandardCursorType.Cross);
    }

    private FreecamEngine? Engine => Source as FreecamEngine;

    /// <summary>The wheel changed the speed (blocks/s).</summary>
    public event Action<double>? SpeedChanged;

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus(NavigationMethod.Pointer);
        _drag = e.GetPosition(this);
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_drag is not { } last || Engine is not { } eng) return;
        var p = e.GetPosition(this);
        eng.Camera.Look(p.X - last.X, p.Y - last.Y);
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
        if (Engine is not { } eng) return;
        eng.Camera.Speed = Math.Clamp(eng.Camera.Speed * Math.Pow(1.2, e.Delta.Y), 1, 200);
        SpeedChanged?.Invoke(eng.Camera.Speed);
        e.Handled = true;
    }

    private static bool Movement(Key k) => k is Key.W or Key.A or Key.S or Key.D or Key.Q or Key.E or Key.Up or Key.Down or Key.Left or Key.Right
        or Key.Space or Key.LeftShift or Key.RightShift or Key.PageUp or Key.PageDown;

    /// <summary>F3: the stopwatch overlay (frame/present/interval/input latency/sync/GC).</summary>
    public bool ShowStats { get; set; }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.F3) { ShowStats = !ShowStats; InvalidateVisual(); e.Handled = true; return; }
        if (!Movement(e.Key)) return;
        _held.Add(e.Key);
        PushKeys();
        e.Handled = true;
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (!_held.Remove(e.Key)) return;
        PushKeys();
        e.Handled = true;
    }

    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
        _held.Clear();
        PushKeys();
    }

    private void PushKeys()
    {
        Engine?.Camera.StampInput();
        bool H(params Key[] ks) => ks.Any(_held.Contains);
        Engine?.SetKeys(H(Key.W, Key.Up), H(Key.S, Key.Down), H(Key.A, Key.Left), H(Key.D, Key.Right), H(Key.E, Key.Space, Key.PageUp),
            H(Key.Q, Key.PageDown), H(Key.LeftShift, Key.RightShift));
        InvalidateVisual();
    }

    protected override void RenderHud(DrawingContext ctx, Rect bounds)
    {
        if (Engine is not { } eng || Current is not { } f) return;
        var p = f.Pose;
        var text = string.Create(CultureInfo.InvariantCulture,
            $"{eng.Fps,4:0} fps  raster {f.RasterMs,4:0.0} ms  {f.W}x{f.H}\n{p.X:0.0}, {p.Y:0.0}, {p.Z:0.0}  {(p.Dim == -1 ? "nether" : "overworld")}  facing {MineClaw.Render.EyeRenderer.Compass(p.Yaw)} ({p.Yaw:0}°, {p.Pitch:0}°)  speed {eng.Camera.Speed:0}");
        if (ShowStats) text += Environment.NewLine + FreecamStats.Shared.Overlay(eng.Fps);
        var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Mono, 11.5, Brushes.White);
        ctx.DrawRectangle(HudBg, null, new Rect(10, 10, ft.Width + 16, ft.Height + 10), 6, 6);
        ctx.DrawText(ft, new Point(18, 15));
        if (!IsFocused)
        {
            var hint = new FormattedText("click to fly · WASD move · Q/E down/up · drag to look · wheel speed · Shift fast · F3 stats", CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, Face2, 12, Brushes.White);
            var r = new Rect((bounds.Width - hint.Width) / 2 - 10, bounds.Height - hint.Height - 22, hint.Width + 20, hint.Height + 10);
            ctx.DrawRectangle(HudBg, null, r, 6, 6);
            ctx.DrawText(hint, new Point(r.X + 10, r.Y + 5));
        }
    }

    private static readonly Typeface Face2 = new(FontFamily.Default);

    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);
        InvalidateVisual();
    }
}
