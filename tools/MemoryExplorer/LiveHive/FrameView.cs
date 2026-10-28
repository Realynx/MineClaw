using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;

namespace MineClaw.MemoryExplorer.LiveHive;

/// <summary>
/// Shows frames from an <see cref="IFrameSource"/>: each finished frame is copied into a reused <see cref="WriteableBitmap"/> on
/// the UI thread (one memcpy), scaled to cover the control, with the overlay labels drawn as crisp text on top. Tells the source
/// its device-pixel size and whether it is visible, so producers idle when nobody looks.
/// </summary>
public class FrameView : Control
{
    public static readonly StyledProperty<IFrameSource?> SourceProperty = AvaloniaProperty.Register<FrameView, IFrameSource?>(nameof(Source));
    public static readonly StyledProperty<bool> CrosshairProperty = AvaloniaProperty.Register<FrameView, bool>(nameof(Crosshair));
    public static readonly StyledProperty<bool> LabelsProperty = AvaloniaProperty.Register<FrameView, bool>(nameof(Labels), true);

    private WriteableBitmap? _bmp;
    private FrameOut? _frame;
    private List<OverlayLabel> _labels = [];
    private int _fw, _fh;
    private bool _posted;
    private IFrameSource? _attached;
    private static readonly Typeface Face = new(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold);
    private static readonly IBrush ChipBg = new SolidColorBrush(Color.FromArgb(0xC8, 0x10, 0x13, 0x18));

    static FrameView()
    {
        AffectsRender<FrameView>(CrosshairProperty, LabelsProperty);
    }

    public FrameView() => ClipToBounds = true;

    public IFrameSource? Source { get => GetValue(SourceProperty); set => SetValue(SourceProperty, value); }
    public bool Crosshair { get => GetValue(CrosshairProperty); set => SetValue(CrosshairProperty, value); }
    public bool Labels { get => GetValue(LabelsProperty); set => SetValue(LabelsProperty, value); }

    /// <summary>The frame last shown (its pose and stats, for HUDs).</summary>
    public FrameOut? Current => _frame;
    public int FramesShown { get; private set; }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SourceProperty) Rebind();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Rebind();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Unbind();
    }

    private void Rebind()
    {
        Unbind();
        if (Source is not { } s || VisualRoot == null) return;
        _attached = s;
        s.FrameReady += OnFrameReady;
        s.Visible = true;
        PushSize();
        OnFrameReady();
    }

    private void Unbind()
    {
        if (_attached == null) return;
        _attached.FrameReady -= OnFrameReady;
        _attached.Visible = false;
        _attached = null;
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        PushSize();
    }

    private void PushSize()
    {
        if (_attached == null) return;
        double scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        _attached.SetTarget((int)Math.Max(32, Bounds.Width * scale), (int)Math.Max(24, Bounds.Height * scale));
    }

    private void OnFrameReady()
    {
        if (_posted) return;
        _posted = true;
        Dispatcher.UIThread.Post(Present, DispatcherPriority.Render);
    }

    private void Present()
    {
        _posted = false;
        if (_attached?.TakeFrame() is not { } f || f.W <= 0 || f.H <= 0 || f.Px.Length < f.W * f.H) return;
        if (_bmp == null || _fw != f.W || _fh != f.H)
        {
            _bmp?.Dispose();
            _bmp = new WriteableBitmap(new PixelSize(f.W, f.H), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
            (_fw, _fh) = (f.W, f.H);
        }
        using (var fb = _bmp.Lock())
        {
            if (fb.RowBytes == f.W * 4) Marshal.Copy(f.Px, 0, fb.Address, f.W * f.H);
            else
                for (int y = 0; y < f.H; y++) Marshal.Copy(f.Px, y * f.W, fb.Address + y * fb.RowBytes, f.W);
        }
        _frame = f;
        _labels = f.Labels;
        FramesShown++;
        Presented();
        InvalidateVisual();
    }

    /// <summary>After each new frame (UI thread).</summary>
    protected virtual void Presented() { }

    /// <summary>The frame's rectangle in control coordinates (scaled to cover, centred).</summary>
    protected Rect FrameRect()
    {
        if (_fw <= 0 || _fh <= 0) return Bounds.WithX(0).WithY(0);
        double s = Math.Max(Bounds.Width / _fw, Bounds.Height / _fh);
        double w = _fw * s, h = _fh * s;
        return new Rect((Bounds.Width - w) / 2, (Bounds.Height - h) / 2, w, h);
    }

    public override void Render(DrawingContext ctx)
    {
        var bounds = new Rect(Bounds.Size);
        ctx.FillRectangle(Brushes.Black, bounds);
        if (_bmp == null) return;
        var dest = FrameRect();
        using (ctx.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.LowQuality }))
            ctx.DrawImage(_bmp, new Rect(0, 0, _fw, _fh), dest);
        double s = dest.Width / _fw;
        if (Labels)
            foreach (var l in _labels.OrderByDescending(l => l.Dist).TakeLast(60))
            {
                var c = Color.FromRgb((byte)(l.Colour >> 16), (byte)(l.Colour >> 8), (byte)l.Colour);
                var ft = new FormattedText(l.Text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, 11.5, new SolidColorBrush(c));
                double x = dest.X + l.X * s - ft.Width / 2, y = dest.Y + l.Y * s - ft.Height - 4;
                using (ctx.PushOpacity(l.Occluded ? 0.55 : 1))
                {
                    ctx.DrawRectangle(ChipBg, null, new Rect(x - 5, y - 2, ft.Width + 10, ft.Height + 4), 4, 4);
                    ctx.DrawText(ft, new Point(x, y));
                }
            }
        if (Crosshair)
        {
            var pen = new Pen(new SolidColorBrush(Color.FromArgb(0xD0, 0xFF, 0xFF, 0xFF)), 1.5);
            var c = bounds.Center;
            ctx.DrawLine(pen, new Point(c.X - 7, c.Y), new Point(c.X + 7, c.Y));
            ctx.DrawLine(pen, new Point(c.X, c.Y - 7), new Point(c.X, c.Y + 7));
        }
        RenderHud(ctx, bounds);
    }

    protected virtual void RenderHud(DrawingContext ctx, Rect bounds) { }
}
