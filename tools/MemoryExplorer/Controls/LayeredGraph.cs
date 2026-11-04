using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Styling;
using Avalonia.Threading;

namespace MineClaw.MemoryExplorer.Controls;

/// <summary>
/// The RAPTOR graph: an icicle (default), a sunburst or a tidy node-link view of one <see cref="GraphModel"/>.
/// Wheel zooms around the pointer, drag pans, click selects, double-click focuses a subtree (Esc zooms back out), arrows walk
/// parent/child/sibling and Enter selects. Bands carry TF-IDF topic labels, colour by kind / time / root cluster, opacity by tokens.
/// Drawing batches every band of one colour into one geometry, culls by binary search per row and hides individual leaves until
/// each gets a few pixels (<see cref="GraphLod"/>).
/// </summary>
public sealed class LayeredGraph : Control
{
    public static readonly StyledProperty<GraphModel?> ModelProperty = AvaloniaProperty.Register<LayeredGraph, GraphModel?>(nameof(Model));
    public static readonly StyledProperty<int> SelectedIndexProperty =
        AvaloniaProperty.Register<LayeredGraph, int>(nameof(SelectedIndex), -1, defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);
    public static readonly StyledProperty<int> FocusIndexProperty =
        AvaloniaProperty.Register<LayeredGraph, int>(nameof(FocusIndex), -1, defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);
    public static readonly StyledProperty<GraphLayoutKind> LayoutKindProperty = AvaloniaProperty.Register<LayeredGraph, GraphLayoutKind>(nameof(LayoutKind));
    public static readonly StyledProperty<GraphColorBy> ColorByProperty = AvaloniaProperty.Register<LayeredGraph, GraphColorBy>(nameof(ColorBy));
    public static readonly StyledProperty<IReadOnlyCollection<int>?> SearchHitsProperty =
        AvaloniaProperty.Register<LayeredGraph, IReadOnlyCollection<int>?>(nameof(SearchHits));

    public GraphModel? Model { get => GetValue(ModelProperty); set => SetValue(ModelProperty, value); }
    public int SelectedIndex { get => GetValue(SelectedIndexProperty); set => SetValue(SelectedIndexProperty, value); }
    /// <summary>Node index of the focused (zoomed-into) subtree, -1 for the whole tree.</summary>
    public int FocusIndex { get => GetValue(FocusIndexProperty); set => SetValue(FocusIndexProperty, value); }
    public GraphLayoutKind LayoutKind { get => GetValue(LayoutKindProperty); set => SetValue(LayoutKindProperty, value); }
    public GraphColorBy ColorBy { get => GetValue(ColorByProperty); set => SetValue(ColorByProperty, value); }
    /// <summary>Node indexes to light up (search results).</summary>
    public IReadOnlyCollection<int>? SearchHits { get => GetValue(SearchHitsProperty); set => SetValue(SearchHitsProperty, value); }

    private const double Gutter = 78, TopPad = 6, Footer = 34, RightPad = 8, AnimMs = 200;
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static readonly Typeface Regular = new(FontFamily.Default);
    private static readonly Typeface Semi = new(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold);

    private ViewState _from = ViewState.Whole, _to = ViewState.Whole;
    private double _animStart = -1, _animMs = AnimMs;
    private bool _frameRequested;

    private int _hover = -1, _cursor = -1;
    private bool _keyboardTip, _pinTip;
    private Point _pointer;
    private Point? _press;
    private ViewState _pressView;
    private bool _moved, _dragMinimap;
    private Rect _minimap;
    private bool _minimapShown;
    private GraphViewport? _vp;

    private HashSet<int>? _lit;
    private HashSet<int>? _path;
    private int _litFor = int.MinValue;
    private HashSet<int>? _hitSlots;
    private object? _hitsFor;
    private int[] _rootRank = [];
    private double[] _weight = []; // 0..1 token weight per slot (opacity)
    private int[][] _runMix = [];   // per leaf run: category counts (kind colouring of the aggregated strip)

    private Theme? _theme;
    private readonly Dictionary<string, FormattedText> _text = new();
    private readonly Dictionary<uint, IImmutableSolidColorBrush> _brushes = new();

    static LayeredGraph()
    {
        AffectsRender<LayeredGraph>(LayoutKindProperty, ColorByProperty, SearchHitsProperty);
        FocusableProperty.OverrideDefaultValue<LayeredGraph>(true);
    }

    public LayeredGraph()
    {
        ClipToBounds = true;
        ActualThemeVariantChanged += (_, _) =>
        {
            _theme = null;
            _text.Clear();
            InvalidateVisual();
        };
    }

    // ------------------------------------------------------------------ view state

    private ViewState Current
    {
        get
        {
            if (_animStart < 0) return _to;
            double t = (Clock.Elapsed.TotalMilliseconds - _animStart) / _animMs;
            if (t >= 1) { _animStart = -1; return _to; }
            t = 1 - Math.Pow(1 - t, 3); // ease-out cubic
            return ViewState.Lerp(_from, _to, t);
        }
    }

    private ViewState Target => _to;

    private double MinSpan
    {
        get
        {
            var m = Model;
            double w = Math.Max(100, Bounds.Width - Gutter - RightPad);
            return m == null || m.TotalLeaves == 0 ? 1e-4 : Math.Min(1, w / 400.0 / m.TotalLeaves);
        }
    }

    private ViewState Clamp(ViewState v)
    {
        double span = Math.Clamp(v.Span, MinSpan, 1);
        double x0 = Math.Clamp(v.X0, 0, 1 - span);
        double scale = Math.Clamp(v.Scale, 1, 60);
        double lim = Math.Max(0, Math.Min(Bounds.Width, Bounds.Height) / 2 * scale);
        return new ViewState(x0, span, scale, Math.Clamp(v.PanX, -lim, lim), Math.Clamp(v.PanY, -lim, lim));
    }

    private void SetView(ViewState target, bool animate, double ms = AnimMs)
    {
        target = Clamp(target);
        if (animate)
        {
            _from = Current;
            _to = target;
            _animMs = ms;
            _animStart = Clock.Elapsed.TotalMilliseconds;
        }
        else
        {
            _from = _to = target;
            _animStart = -1;
        }
        InvalidateVisual();
    }

    private ViewState FocusView()
    {
        if (Model is { } m && FocusIndex >= 0 && m.TryGetSlot(FocusIndex, out var s))
            return new ViewState(m.Layout.X0[s], Math.Max(MinSpan, m.Layout.X1[s] - m.Layout.X0[s]), 1, 0, 0);
        return ViewState.Whole;
    }

    /// <summary>Zoom back to the focused subtree (or the whole tree), undoing free zoom and pan.</summary>
    public void Fit() => SetView(FocusView(), true);

    /// <summary>Whole tree, no focus.</summary>
    public void Reset()
    {
        if (FocusIndex != -1) FocusIndex = -1;
        else SetView(ViewState.Whole, true);
    }

    public void ZoomBy(double factor, Point? around = null)
    {
        if (_vp is not { } vp) return;
        var t = Target;
        var p = around ?? vp.Plot.Center;
        if (LayoutKind == GraphLayoutKind.Radial)
        {
            double ns = Math.Clamp(t.Scale * factor, 1, 60);
            var pc = vp.Plot.Center;
            var c = new Point(pc.X + t.PanX, pc.Y + t.PanY);
            var nc = p - (p - c) * (ns / t.Scale);
            SetView(t with { Scale = ns, PanX = nc.X - pc.X, PanY = nc.Y - pc.Y }, true, 160);
        }
        else
        {
            double fx = (p.X - vp.Plot.X) / vp.Plot.Width;
            double anchor = t.X0 + fx * t.Span;
            double span = Math.Clamp(t.Span / factor, MinSpan, 1);
            SetView(t with { X0 = anchor - fx * span, Span = span }, true, 160);
        }
    }

    /// <summary>Shows the tooltip of a node as if the keyboard cursor were on it (headless screenshots).</summary>
    public void PinTooltip(int index)
    {
        if (Model is not { } m || !m.TryGetSlot(index, out var s)) return;
        _cursor = s;
        _keyboardTip = _pinTip = true;
        InvalidateVisual();
    }

    private bool CursorShown => _keyboardTip && (IsFocused || _pinTip) && _cursor >= 0;

    private void RequestFrame()
    {
        if (_frameRequested) return;
        _frameRequested = true;
        if (TopLevel.GetTopLevel(this) is { } tl)
            tl.RequestAnimationFrame(_ => { _frameRequested = false; InvalidateVisual(); });
        else
            Dispatcher.UIThread.Post(() => { _frameRequested = false; InvalidateVisual(); }, DispatcherPriority.Background);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ModelProperty)
        {
            _lit = _path = null;
            _litFor = int.MinValue;
            _hitsFor = null;
            _hover = _cursor = -1;
            _text.Clear();
            Prepare(Model);
            _from = _to = FocusView();
            _animStart = -1;
            InvalidateVisual();
        }
        else if (change.Property == FocusIndexProperty)
        {
            var v = FocusView();
            SetView(v, true);
        }
        else if (change.Property == SelectedIndexProperty)
        {
            if (Model is { } m && m.TryGetSlot(SelectedIndex, out var s))
            {
                _cursor = s;
                EnsureVisible(s);
            }
            InvalidateVisual();
        }
        else if (change.Property == LayoutKindProperty)
        {
            SetView(Target with { Scale = 1, PanX = 0, PanY = 0 }, false);
        }
    }

    private void Prepare(GraphModel? m)
    {
        if (m == null) { _rootRank = []; _weight = []; _runMix = []; return; }
        int n = m.Nodes.Length;
        // Root clusters ranked by size (largest first): the first slots get the categorical hues, the rest fold into "other".
        _rootRank = new int[n];
        var ranked = m.TopLevel.Where(s => m.Nodes[s].Layer > 0).OrderByDescending(s => m.Nodes[s].Leaves).ThenBy(s => m.Nodes[s].Index).ToList();
        Array.Fill(_rootRank, -1);
        for (int i = 0; i < ranked.Count; i++) _rootRank[ranked[i]] = i;
        // Token weight within each layer (log scale) → band opacity.
        _weight = new double[n];
        foreach (var layer in Enumerable.Range(0, n).GroupBy(s => m.Nodes[s].Layer))
        {
            var logs = layer.Select(s => Math.Log(1 + m.Nodes[s].Tokens)).ToArray();
            double lo = logs.Min(), hi = logs.Max();
            int k = 0;
            foreach (var s in layer) _weight[s] = hi - lo < 1e-9 ? 0.6 : (logs[k++] - lo) / (hi - lo);
        }
        var runs = m.Layout.LeafRuns;
        _runMix = new int[runs.Length][];
        var leafRow = m.Layout.RowSlots[m.Layout.LeafRow];
        for (int r = 0; r < runs.Length; r++)
        {
            var mix = new int[7];
            var (from, to) = m.Layout.RowRange(m.Layout.LeafRow, runs[r].X0, runs[r].X1 - 1e-12);
            for (int i = from; i < to; i++)
            {
                int s = leafRow[i];
                if (m.Layout.X0[s] >= runs[r].X0 - 1e-12 && m.Layout.X1[s] <= runs[r].X1 + 1e-12) mix[(int)m.Nodes[s].Category]++;
            }
            _runMix[r] = mix;
        }
    }

    // ------------------------------------------------------------------ theme

    private sealed class Theme
    {
        public required GraphPalette Palette;
        public required IBrush Text, Muted, Faint, Accent, AccentSoft, Card, Border, Warn, Edge;
        public required IPen EdgePen, EdgeDimPen, EdgeLitPen, SelPen, PathPen, HoverPen, CursorPen, SoftPen, HitPen, CardRing, MiniPen;
    }

    private Theme T()
    {
        if (_theme != null) return _theme;
        IBrush Res(string key, IBrush fallback) => this.TryFindResource(key, ActualThemeVariant, out var v) && v is IBrush b ? b : fallback;
        bool dark = ActualThemeVariant == ThemeVariant.Dark;
        var accent = Res("AccentBrush", Brushes.SlateBlue);
        var accentColor = (accent as ISolidColorBrush)?.Color ?? Colors.SlateBlue;
        var faint = Res("TextFaintBrush", Brushes.Gray);
        var faintColor = (faint as ISolidColorBrush)?.Color ?? Colors.Gray;
        var edge = Res("GraphEdgeBrush", new SolidColorBrush(Color.FromArgb(50, 80, 90, 200)));
        var card = Res("CardBrush", Brushes.White);
        var dash = new DashStyle([3, 3], 0);
        return _theme = new Theme
        {
            Palette = new GraphPalette(dark),
            Text = Res("TextBrush", Brushes.Black), Muted = Res("TextMutedBrush", Brushes.Gray), Faint = faint, Accent = accent,
            AccentSoft = Res("AccentSoftBrush", Brushes.Lavender), Card = card, Border = Res("BorderSubtleBrush", Brushes.LightGray),
            Warn = Res("WarnBrush", Brushes.Orange), Edge = edge,
            EdgePen = new Pen(edge, 1),
            EdgeDimPen = new Pen(new ImmutableSolidColorBrush(GraphPalette.WithAlpha(faintColor, dark ? 0.16 : 0.18)), 1),
            EdgeLitPen = new Pen(accent, 1.6),
            SelPen = new Pen(accent, 2),
            PathPen = new Pen(new ImmutableSolidColorBrush(GraphPalette.WithAlpha(accentColor, 0.75)), 1.2),
            HoverPen = new Pen(Res("TextMutedBrush", Brushes.Gray), 1),
            CursorPen = new Pen(accent, 1.5, new DashStyle([4, 3], 0)),
            SoftPen = new Pen(new ImmutableSolidColorBrush(GraphPalette.WithAlpha(faintColor, 0.9)), 1.2, dash),
            HitPen = new Pen(accent, 1.5),
            CardRing = new Pen(card, 1.5),
            MiniPen = new Pen(accent, 1.2),
        };
    }

    private IImmutableSolidColorBrush Brush(Color c)
    {
        uint k = c.ToUInt32();
        if (!_brushes.TryGetValue(k, out var b))
        {
            if (_brushes.Count > 4096) _brushes.Clear();
            _brushes[k] = b = new ImmutableSolidColorBrush(c);
        }
        return b;
    }

    private FormattedText Text(string s, double size, bool semi, IBrush brush, string brushKey, double maxWidth = 0, int maxLines = 0)
    {
        var key = $"{size}|{(semi ? 1 : 0)}|{brushKey}|{maxWidth:0}|{maxLines}|{s}";
        if (_text.TryGetValue(key, out var ft)) return ft;
        if (_text.Count > 6000) _text.Clear();
        ft = new FormattedText(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, semi ? Semi : Regular, size, brush);
        if (maxWidth > 0)
        {
            ft.MaxTextWidth = maxWidth;
            ft.Trimming = TextTrimming.CharacterEllipsis;
        }
        if (maxLines > 0) ft.MaxLineCount = maxLines;
        return _text[key] = ft;
    }

    // ------------------------------------------------------------------ colours

    private Color BaseColor(GraphModel m, int s, Theme th)
    {
        var g = m.Nodes[s];
        var p = th.Palette;
        return ColorBy switch
        {
            GraphColorBy.Time => g.TimeRank < 0 ? p.Other : p.Time(g.TimeRank),
            GraphColorBy.Cluster => p.Cluster(g.Layer == 0 && g.Parent < 0 ? -1 : _rootRank[g.Root]),
            _ => p.Category(g.Category),
        };
    }

    /// <summary>Band tint: soft fill whose opacity grows with the node's token count; dimmed outside the selection.</summary>
    private Color Fill(GraphModel m, int s, Theme th, bool dim, int row, int rows)
    {
        var c = th.Palette.Depth(BaseColor(m, s, th), row, rows);
        bool leaf = m.Nodes[s].IsLeaf;
        double a = leaf ? 0.55 + 0.25 * _weight[s] : (th.Palette.Dark ? 0.20 : 0.16) + 0.26 * _weight[s];
        if (dim) a *= 0.45;
        return GraphPalette.WithAlpha(c, Math.Round(a * 20) / 20); // quantized: fewer brushes, bigger batches
    }

    private Color Stripe(GraphModel m, int s, Theme th, bool dim) =>
        GraphPalette.WithAlpha(BaseColor(m, s, th), dim ? 0.3 : 0.95);

    // ------------------------------------------------------------------ selection sets

    private void Lit(GraphModel m)
    {
        if (_litFor == SelectedIndex) return;
        _litFor = SelectedIndex;
        _lit = _path = null;
        if (!m.TryGetSlot(SelectedIndex, out var sel)) return;
        _path = new HashSet<int>(m.PathToTop(sel));
        var lit = new HashSet<int>(_path);
        var stack = new Stack<int>();
        stack.Push(sel);
        while (stack.Count > 0)
            foreach (var c in m.Nodes[stack.Pop()].Children)
                if (lit.Add(c)) stack.Push(c);
        _lit = lit;
    }

    private HashSet<int>? Hits(GraphModel m)
    {
        var hits = SearchHits;
        if (ReferenceEquals(_hitsFor, hits) && _hitsFor != null) return _hitSlots;
        _hitsFor = hits;
        _hitSlots = hits is { Count: > 0 } ? hits.Where(m.Slot.ContainsKey).Select(i => m.Slot[i]).ToHashSet() : null;
        return _hitSlots;
    }

    // ------------------------------------------------------------------ render

    private Rect PlotRect(Size size) => new(Gutter, TopPad, Math.Max(0, size.Width - Gutter - RightPad), Math.Max(0, size.Height - TopPad - Footer));

    public override void Render(DrawingContext ctx)
    {
        ctx.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        var m = Model;
        var plot = PlotRect(Bounds.Size);
        if (m == null || m.Nodes.Length == 0 || plot.Width < 60 || plot.Height < 60) { _vp = null; return; }
        var th = T();
        var view = Current;
        bool animating = _animStart >= 0;
        var vp = _vp = new GraphViewport(m, LayoutKind, plot, view);
        Lit(m);
        var hits = Hits(m);

        using (ctx.PushClip(LayoutKind == GraphLayoutKind.Radial ? new Rect(Gutter - 4, 0, Bounds.Width - Gutter + 4, plot.Bottom + 2) : plot.Inflate(new Thickness(1, 2))))
        {
            switch (LayoutKind)
            {
                case GraphLayoutKind.Radial: DrawRadial(ctx, m, vp, th, hits); break;
                case GraphLayoutKind.NodeLink: DrawNodeLink(ctx, m, vp, th, hits); break;
                default: DrawIcicle(ctx, m, vp, th, hits); break;
            }
        }
        DrawGutter(ctx, m, vp, th);
        DrawFooter(ctx, m, vp, th, hits);
        DrawTooltip(ctx, m, vp, th);
        if (animating) RequestFrame();
    }

    private sealed class Batch
    {
        private readonly Dictionary<uint, (StreamGeometry G, StreamGeometryContext C)> _b = new();

        public StreamGeometryContext this[Color c]
        {
            get
            {
                uint k = c.ToUInt32();
                if (!_b.TryGetValue(k, out var e)) { var g = new StreamGeometry(); _b[k] = e = (g, g.Open()); }
                return e.C;
            }
        }

        public void Flush(DrawingContext ctx, Func<Color, IBrush> brush, IPen? pen = null)
        {
            foreach (var (k, e) in _b)
            {
                e.C.Dispose();
                ctx.DrawGeometry(brush(Color.FromUInt32(k)), pen, e.G);
            }
            _b.Clear();
        }
    }

    private static void RoundRect(StreamGeometryContext g, Rect r, double rad)
    {
        rad = Math.Min(rad, Math.Min(r.Width, r.Height) / 2);
        if (rad < 0.6)
        {
            g.BeginFigure(r.TopLeft, true);
            g.LineTo(r.TopRight); g.LineTo(r.BottomRight); g.LineTo(r.BottomLeft);
            g.EndFigure(true);
            return;
        }
        var s = new Size(rad, rad);
        g.BeginFigure(new Point(r.X + rad, r.Y), true);
        g.LineTo(new Point(r.Right - rad, r.Y));
        g.ArcTo(new Point(r.Right, r.Y + rad), s, 0, false, SweepDirection.Clockwise);
        g.LineTo(new Point(r.Right, r.Bottom - rad));
        g.ArcTo(new Point(r.Right - rad, r.Bottom), s, 0, false, SweepDirection.Clockwise);
        g.LineTo(new Point(r.X + rad, r.Bottom));
        g.ArcTo(new Point(r.X, r.Bottom - rad), s, 0, false, SweepDirection.Clockwise);
        g.LineTo(new Point(r.X, r.Y + rad));
        g.ArcTo(new Point(r.X + rad, r.Y), s, 0, false, SweepDirection.Clockwise);
        g.EndFigure(true);
    }

    private static void Circle(StreamGeometryContext g, Point c, double r)
    {
        var s = new Size(r, r);
        g.BeginFigure(new Point(c.X, c.Y - r), true);
        g.ArcTo(new Point(c.X, c.Y + r), s, 0, false, SweepDirection.Clockwise);
        g.ArcTo(new Point(c.X, c.Y - r), s, 0, false, SweepDirection.Clockwise);
        g.EndFigure(true);
    }

    /// <summary>Screen rect of a band (clamped a little outside the plot so huge zooms stay in float range), or null when culled.</summary>
    private static Rect? BandRect(GraphViewport vp, int s)
    {
        var L = vp.Layout;
        double x0 = vp.ScreenX(L.X0[s]), x1 = vp.ScreenX(L.X1[s]);
        if (x1 < vp.Plot.X - 1 || x0 > vp.Plot.Right + 1 || x1 - x0 < GraphLod.MinBandPx) return null;
        x0 = Math.Max(x0, vp.Plot.X - 8);
        x1 = Math.Min(x1, vp.Plot.Right + 8);
        var (t, b) = vp.RowSpan(L.Row[s]);
        double gap = x1 - x0 > 4 ? 1 : 0;
        return new Rect(x0 + gap, t + 1, Math.Max(0.5, x1 - x0 - 2 * gap), Math.Max(1, b - t - 4));
    }

    private (int From, int To) Visible(GraphViewport vp, int row) =>
        vp.Layout.RowRange(row, vp.View.X0, vp.View.X0 + vp.View.Span);

    // ---- icicle

    private void DrawIcicle(DrawingContext ctx, GraphModel m, GraphViewport vp, Theme th, HashSet<int>? hits)
    {
        var L = m.Layout;
        var fills = new Batch();
        var stripes = new Batch();
        var marks = new Batch();
        var labels = new List<(int Slot, Rect R)>();
        for (int row = 0; row < L.Rows; row++)
        {
            if (row == L.LeafRow && !vp.ShowLeaves) { DrawLeafRuns(m, vp, th, fills, hits); continue; }
            IcicleRow(m, vp, th, row, new IcicleBatches(fills, stripes, marks), labels);
        }
        fills.Flush(ctx, c => Brush(c));
        stripes.Flush(ctx, c => Brush(c));
        marks.Flush(ctx, c => Brush(c));
        foreach (var (s, r) in labels) DrawBandLabel(ctx, m, th, s, r);
        DrawHitsAndOutlines(ctx, m, vp, th, hits);
    }

    private readonly record struct IcicleBatches(Batch Fills, Batch Stripes, Batch Marks);

    /// <summary>One icicle row: each visible band (fill, top stripe, dirty mark), and the bands big enough to carry a label.</summary>
    private void IcicleRow(GraphModel m, GraphViewport vp, Theme th, int row, IcicleBatches b, List<(int Slot, Rect R)> labels)
    {
        var L = m.Layout;
        bool leafRow = row == L.LeafRow;
        var (from, to) = Visible(vp, row);
        var slots = L.RowSlots[row];
        for (int i = from; i < to; i++)
        {
            int s = slots[i];
            if (BandRect(vp, s) is not { } r) continue;
            IcicleBand(m, th, s, r, row, leafRow, b);
            if ((r.Width >= 30 || (leafRow && r.Width >= 13)) && r.Height >= 15) labels.Add((s, r));
        }
    }

    private void IcicleBand(GraphModel m, Theme th, int s, Rect r, int row, bool leafRow, IcicleBatches b)
    {
        bool dim = Dimmed(s);
        RoundRect(b.Fills[Fill(m, s, th, dim, row, m.Layout.Rows)], r, leafRow ? 3 : 6);
        if (r.Width >= 6 && r.Height >= 10)
            RoundRect(b.Stripes[Stripe(m, s, th, dim)], new Rect(r.X, r.Y, r.Width, Math.Min(3, r.Height / 3)), 1.5);
        if (m.Nodes[s].Dirty && r.Width >= 22 && r.Height >= 14) Circle(b.Marks[GraphPalette.WithAlpha(GetColor(th.Warn), dim ? 0.3 : 0.75)], new Point(r.Right - 7, r.Y + 9), 2);
    }

    /// <summary>Outside the selection's lit set (when something is selected).</summary>
    private bool Dimmed(int s) => _lit != null && !_lit.Contains(s);

    /// <summary>An aggregated leaf run outside the lit set (a run with no single owner counts as outside).</summary>
    private bool RunDimmed(int owner) => _lit != null && (owner < 0 || !_lit.Contains(owner));

    private static Color GetColor(IBrush b) => (b as ISolidColorBrush)?.Color ?? Colors.Orange;

    private void DrawLeafRuns(GraphModel m, GraphViewport vp, Theme th, Batch fills, HashSet<int>? hits)
    {
        var L = m.Layout;
        var (t, b) = vp.RowSpan(L.LeafRow);
        double top = t + 1, h = Math.Max(2, b - t - 3);
        var runs = L.LeafRuns;
        for (int i = 0; i < runs.Length; i++)
        {
            var (a0, a1, owner) = runs[i];
            double x0 = vp.ScreenX(a0), x1 = vp.ScreenX(a1);
            if (x1 < vp.Plot.X || x0 > vp.Plot.Right || x1 - x0 < GraphLod.MinBandPx) continue;
            x0 = Math.Max(x0, vp.Plot.X - 4);
            x1 = Math.Min(x1, vp.Plot.Right + 4);
            bool dim = RunDimmed(owner);
            double gap = x1 - x0 > 4 ? 0.75 : 0;
            if (ColorBy == GraphColorBy.Kind && x1 - x0 >= 8)
            {
                IcicleRunMix(th, fills, _runMix[i], x0 + gap, x1 - x0 - 2 * gap, top, h, dim);
                continue;
            }
            var color = owner >= 0 ? BaseColor(m, owner, th) : th.Palette.Other;
            RoundRect(fills[GraphPalette.WithAlpha(color, dim ? 0.22 : 0.75)], new Rect(x0 + gap, top, Math.Max(0.5, x1 - x0 - 2 * gap), h), Math.Min(2, h / 2));
        }
    }

    /// <summary>Composition of a leaf run: one segment per category in fixed order.</summary>
    private static void IcicleRunMix(Theme th, Batch fills, int[] mix, double at, double w, double top, double h, bool dim)
    {
        int total = mix.Sum();
        for (int c = 0; c < 7; c++)
        {
            if (mix[c] == 0) continue;
            double sw = w * mix[c] / total;
            var col = GraphPalette.WithAlpha(th.Palette.Category((MemoryCategory)c), dim ? 0.22 : 0.8);
            RoundRect(fills[col], new Rect(at, top, Math.Max(0.5, sw), h), 0);
            at += sw;
        }
    }

    private void DrawBandLabel(DrawingContext ctx, GraphModel m, Theme th, int s, Rect r)
    {
        var g = m.Nodes[s];
        using var _ = ctx.PushOpacity(Dimmed(s) ? 0.55 : 1);
        double pad = 7, avail = r.Width - 2 * pad;
        double y = r.Y + 6;
        if (g.IsLeaf) DrawLeafBandTitle(ctx, g, th, r, pad, avail, y);
        else DrawInnerBandLabel(ctx, g, th, r, pad, avail, y);
    }

    private void DrawLeafBandTitle(DrawingContext ctx, GraphNode g, Theme th, Rect r, double pad, double avail, double y)
    {
        if (avail >= 60)
        {
            var title = Text(g.Title, 10.5, false, th.Text, "t", Math.Floor(avail / 8) * 8, Math.Max(1, (int)((r.Height - 8) / 14)));
            using (ctx.PushClip(r)) ctx.DrawText(title, new Point(r.X + pad, y));
        }
        else if (r.Width >= 13 && r.Height >= 60)
        {
            // Narrow, tall leaf: the title runs up the band.
            var title = Text(g.Title, 10.5, false, th.Text, "t", Math.Floor((r.Height - 14) / 8) * 8, 1);
            using (ctx.PushClip(r))
            using (ctx.PushTransform(Matrix.CreateRotation(-Math.PI / 2) * Matrix.CreateTranslation(r.X + (r.Width - title.Height) / 2, r.Bottom - 7)))
                ctx.DrawText(title, new Point(0, 0));
        }
    }

    /// <summary>A summary band: its terms, then (room permitting) layer/id/leaf count and the preview.</summary>
    private void DrawInnerBandLabel(DrawingContext ctx, GraphNode g, Theme th, Rect r, double pad, double avail, double y)
    {
        var head = TermsFitting(g, avail, 12, th);
        if (head == null) return;
        ctx.DrawText(head, new Point(r.X + pad, y));
        y += head.Height + 1;
        if (r.Bottom - y < 14) return;
        var meta = Text($"L{g.Layer} #{g.Index} · {g.LeafCount:N0} leaves", 10.5, false, th.Muted, "m");
        if (meta.Width <= avail) { ctx.DrawText(meta, new Point(r.X + pad, y)); y += meta.Height + 3; }
        else return;
        int lines = (int)((r.Bottom - y - 4) / 15);
        if (lines >= 1 && avail >= 120)
        {
            var body = Text(g.Preview, 11, false, th.Muted, "m", Math.Floor(avail / 16) * 16, Math.Min(lines, 5));
            ctx.DrawText(body, new Point(r.X + pad, y));
        }
    }

    /// <summary>The longest prefix of the node's terms that fits (semi-bold), or the id when it has none.</summary>
    private FormattedText? TermsFitting(GraphNode g, double avail, double size, Theme th)
    {
        var terms = g.Terms;
        for (int k = Math.Min(terms.Length, 3); k >= 1; k--)
        {
            var ft = Text(string.Join(" · ", terms.Take(k)), size, true, th.Text, "t");
            if (ft.Width <= avail) return ft;
        }
        if (terms.Length > 0 && avail >= 34) return Text(terms[0], size, true, th.Text, "t", Math.Floor(avail), 1);
        var id = Text($"#{g.Index}", size - 1, true, th.Muted, "m");
        return id.Width <= avail ? id : null;
    }

    private void DrawHitsAndOutlines(DrawingContext ctx, GraphModel m, GraphViewport vp, Theme th, HashSet<int>? hits)
    {
        if (hits != null) DrawIcicleHits(ctx, m, vp, th, hits);
        DrawIciclePath(ctx, m, vp, th);
        DrawSoftParents(ctx, m, vp, th);
        if (_hover >= 0 && BandRect(vp, _hover) is { } hr) ctx.DrawRectangle(null, th.HoverPen, hr.Deflate(0.5), 6, 6);
        if (Sel(m) is var sel && sel >= 0 && BandRect(vp, sel) is { } sr) ctx.DrawRectangle(null, th.SelPen, sr.Inflate(0.5), 6, 6);
        if (CursorShown && BandRect(vp, _cursor) is { } cr) ctx.DrawRectangle(null, th.CursorPen, cr.Inflate(2), 7, 7);
    }

    /// <summary>Search hits: an outlined band with a dot, or a tick on the aggregated leaf strip.</summary>
    private void DrawIcicleHits(DrawingContext ctx, GraphModel m, GraphViewport vp, Theme th, HashSet<int> hits)
    {
        var L = m.Layout;
        var ticks = new Batch();
        var accent = GetColor(th.Accent);
        foreach (var s in hits)
        {
            if (L.Row[s] == L.LeafRow && !vp.ShowLeaves)
            {
                var (t, b) = vp.RowSpan(L.Row[s]);
                double x = vp.ScreenX((L.X0[s] + L.X1[s]) / 2);
                if (x < vp.Plot.X || x > vp.Plot.Right) continue;
                RoundRect(ticks[accent], new Rect(x - 1.25, t - 3, 2.5, b - t + 2), 1);
                continue;
            }
            if (BandRect(vp, s) is not { } r) continue;
            ctx.DrawRectangle(null, th.HitPen, r.Deflate(0.5), 5, 5);
            Circle(ticks[accent], new Point(r.X + 5, r.Bottom - 5), 2.5);
        }
        ticks.Flush(ctx, c => Brush(c));
    }

    /// <summary>The selection's path to the top, outlined (the selection itself gets its own outline).</summary>
    private void DrawIciclePath(DrawingContext ctx, GraphModel m, GraphViewport vp, Theme th)
    {
        if (_path == null) return;
        foreach (var s in _path)
            if (s != Sel(m) && BandRect(vp, s) is { } r) ctx.DrawRectangle(null, th.PathPen, r.Deflate(0.5), 6, 6);
    }

    private int Sel(GraphModel m) => m.TryGetSlot(SelectedIndex, out var s) ? s : -1;

    /// <summary>Soft-clustering second parents of the hovered (else selected) node: faint dashed curves.</summary>
    private void DrawSoftParents(DrawingContext ctx, GraphModel m, GraphViewport vp, Theme th)
    {
        int s = _hover >= 0 ? _hover : Sel(m);
        if (s < 0 || m.Nodes[s].OtherParents.Length == 0) return;
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            var from = Anchor(vp, s, top: true);
            foreach (var p in m.Nodes[s].OtherParents)
            {
                var to = Anchor(vp, p, top: false);
                if (vp.Kind == GraphLayoutKind.Radial)
                {
                    var c = vp.Center;
                    var mid = new Point((from.X + to.X) / 2 * 0.6 + c.X * 0.4, (from.Y + to.Y) / 2 * 0.6 + c.Y * 0.4);
                    g.BeginFigure(from, false);
                    g.QuadraticBezierTo(mid, to);
                    g.EndFigure(false);
                }
                else
                {
                    double dy = (from.Y - to.Y) * 0.5;
                    g.BeginFigure(from, false);
                    g.CubicBezierTo(new Point(from.X, from.Y - dy), new Point(to.X, to.Y + dy), to);
                    g.EndFigure(false);
                }
            }
        }
        ctx.DrawGeometry(null, th.SoftPen, geo);
        foreach (var p in m.Nodes[s].OtherParents)
        {
            if (vp.Kind == GraphLayoutKind.Icicle && BandRect(vp, p) is { } r) ctx.DrawRectangle(null, th.SoftPen, r.Deflate(1), 6, 6);
            else if (vp.Kind == GraphLayoutKind.NodeLink) ctx.DrawEllipse(null, th.SoftPen, vp.NodeCenter(p), vp.NodeRadius(p) + 3, vp.NodeRadius(p) + 3);
        }
    }

    /// <summary>Where a link attaches to a node: its band's top/bottom centre (clamped to the screen), ring edge, or circle.</summary>
    private static Point Anchor(GraphViewport vp, int s, bool top)
    {
        var L = vp.Layout;
        double ux = (Math.Max(L.X0[s], vp.View.X0) + Math.Min(L.X1[s], vp.View.X0 + vp.View.Span)) / 2;
        switch (vp.Kind)
        {
            case GraphLayoutKind.Radial:
            {
                var (r0, r1) = vp.RingSpan(L.Row[s]);
                return GraphViewport.Polar(vp.Center, top ? r0 : r1, vp.Angle(Math.Clamp((L.X0[s] + L.X1[s]) / 2, vp.View.X0, vp.View.X0 + vp.View.Span)));
            }
            case GraphLayoutKind.NodeLink:
            {
                var c = vp.NodeCenter(s);
                double r = vp.NodeRadius(s);
                return new Point(Math.Clamp(c.X, vp.Plot.X, vp.Plot.Right), c.Y + (top ? -r : r));
            }
            default:
            {
                var (t, b) = vp.RowSpan(L.Row[s]);
                return new Point(Math.Clamp(vp.ScreenX(ux), vp.Plot.X, vp.Plot.Right), top ? t + 1 : b - 3);
            }
        }
    }

    // ---- radial

    private static void Sector(StreamGeometryContext g, Point c, double r0, double r1, double a0, double a1)
    {
        if (a1 - a0 >= Math.PI * 2 - 1e-6)
        {
            // Full ring: outer circle clockwise, inner counter-clockwise (non-zero fill leaves the hole).
            g.BeginFigure(new Point(c.X, c.Y - r1), true);
            g.ArcTo(new Point(c.X, c.Y + r1), new Size(r1, r1), 0, false, SweepDirection.Clockwise);
            g.ArcTo(new Point(c.X, c.Y - r1), new Size(r1, r1), 0, false, SweepDirection.Clockwise);
            g.EndFigure(true);
            if (r0 > 0.5)
            {
                g.BeginFigure(new Point(c.X, c.Y - r0), true);
                g.ArcTo(new Point(c.X, c.Y + r0), new Size(r0, r0), 0, false, SweepDirection.CounterClockwise);
                g.ArcTo(new Point(c.X, c.Y - r0), new Size(r0, r0), 0, false, SweepDirection.CounterClockwise);
                g.EndFigure(true);
            }
            return;
        }
        bool large = a1 - a0 > Math.PI;
        g.BeginFigure(GraphViewport.Polar(c, r1, a0), true);
        g.ArcTo(GraphViewport.Polar(c, r1, a1), new Size(r1, r1), 0, large, SweepDirection.Clockwise);
        g.LineTo(GraphViewport.Polar(c, r0, a1));
        g.ArcTo(GraphViewport.Polar(c, r0, a0), new Size(r0, r0), 0, large, SweepDirection.CounterClockwise);
        g.EndFigure(true);
    }

    /// <summary>Angles of a band clipped to the angular window, with a hairline gap; null when too thin to see.</summary>
    private static (double A0, double A1)? Arc(GraphViewport vp, double u0, double u1, double r1)
    {
        double w0 = vp.View.X0, w1 = vp.View.X0 + vp.View.Span;
        if (u1 <= w0 || u0 >= w1) return null;
        bool full = u0 <= w0 + 1e-12 && u1 >= w1 - 1e-12;
        double a0 = vp.Angle(Math.Max(u0, w0)), a1 = vp.Angle(Math.Min(u1, w1));
        if (full) return (0, Math.PI * 2);
        if ((a1 - a0) * r1 < GraphLod.MinBandPx) return null;
        if ((a1 - a0) * r1 > 4) { double pad = 0.6 / r1; a0 += pad; a1 -= pad; }
        return (a0, a1);
    }

    /// <summary>Cheap cull: the bounding box of a sector's corner and mid-arc points against the visible rect (wide sectors always pass).</summary>
    private static bool SectorVisible(Point c, double r0, double r1, double a0, double a1, Rect clip)
    {
        if (a1 - a0 > Math.PI / 3) return true;
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        void Add(Point p) { minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X); minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y); }
        Add(GraphViewport.Polar(c, r0, a0)); Add(GraphViewport.Polar(c, r0, a1));
        Add(GraphViewport.Polar(c, r1, a0)); Add(GraphViewport.Polar(c, r1, a1)); Add(GraphViewport.Polar(c, r1, (a0 + a1) / 2));
        return maxX >= clip.X && minX <= clip.Right && maxY >= clip.Y && minY <= clip.Bottom;
    }

    private static bool InsideSector(Rect box, Point c, double r0, double r1, double a0, double a1, bool full)
    {
        foreach (var q in new[] { box.TopLeft, box.TopRight, box.BottomLeft, box.BottomRight, new Point(box.Center.X, box.Top), new Point(box.Center.X, box.Bottom) })
        {
            double dx = q.X - c.X, dy = q.Y - c.Y, r = Math.Sqrt(dx * dx + dy * dy);
            if (r < r0 || r > r1) return false;
            if (full) continue;
            double ang = Math.Atan2(dx, -dy);
            // Bring the angle into [a0, a0 + 2π).
            while (ang < a0) ang += Math.PI * 2;
            while (ang >= a0 + Math.PI * 2) ang -= Math.PI * 2;
            if (ang > a1) return false;
        }
        return true;
    }

    private void DrawRadial(DrawingContext ctx, GraphModel m, GraphViewport vp, Theme th, HashSet<int>? hits)
    {
        var c = vp.Center;
        var clip = new Rect(Gutter - 4, 0, Bounds.Width - Gutter + 4, vp.Plot.Bottom + 2);
        var fills = new Batch();
        var stripes = new Batch();
        var labels = new List<(int Slot, double R, double A, double Arc, double Thick)>();
        for (int row = 0; row < m.Layout.Rows; row++)
        {
            var (r0, r1) = vp.RingSpan(row);
            r0 += 1; r1 -= 1.5;
            if (r1 <= r0) continue;
            var ring = new Ring(c, r0, r1, clip);
            if (row == m.Layout.LeafRow && !vp.ShowLeaves) RadialLeafRuns(m, vp, th, fills, ring);
            else RadialRow(m, vp, th, row, ring, fills, stripes, labels);
        }
        fills.Flush(ctx, col => Brush(col));
        stripes.Flush(ctx, col => Brush(col));

        // Labels: horizontal, centred on the band's mid-arc, kept only when the whole text box sits inside the sector.
        foreach (var label in labels) RadialLabel(ctx, m, vp, th, label);
        RadialCentre(ctx, m, vp, th);
        RadialMarks(ctx, m, vp, th, hits);
    }

    /// <summary>A ring of the radial view: centre, inner and outer radius (inset), and the visible rect for culling.</summary>
    private readonly record struct Ring(Point C, double R0, double R1, Rect Clip);

    /// <summary>The aggregated leaf ring: one sector per run (split by category when colouring by kind and wide enough).</summary>
    private void RadialLeafRuns(GraphModel m, GraphViewport vp, Theme th, Batch fills, Ring ring)
    {
        var runs = m.Layout.LeafRuns;
        for (int i = 0; i < runs.Length; i++)
        {
            var (u0, u1, owner) = runs[i];
            if (Arc(vp, u0, u1, ring.R1) is not { } a || !SectorVisible(ring.C, ring.R0, ring.R1, a.A0, a.A1, ring.Clip)) continue;
            bool dim = RunDimmed(owner);
            if (ColorBy == GraphColorBy.Kind && (a.A1 - a.A0) * ring.R1 >= 8)
            {
                RadialRunMix(th, fills, ring, _runMix[i], a.A0, a.A1, dim);
                continue;
            }
            var color = owner >= 0 ? BaseColor(m, owner, th) : th.Palette.Other;
            Sector(fills[GraphPalette.WithAlpha(color, dim ? 0.22 : 0.75)], ring.C, ring.R0, ring.R1, a.A0, a.A1);
        }
    }

    /// <summary>Composition of a leaf run on the ring: one sub-sector per category in fixed order.</summary>
    private static void RadialRunMix(Theme th, Batch fills, Ring ring, int[] mix, double a0, double a1, bool dim)
    {
        int total = mix.Sum();
        double at = a0;
        for (int k = 0; k < 7; k++)
        {
            if (mix[k] == 0) continue;
            double sw = (a1 - a0) * mix[k] / total;
            Sector(fills[GraphPalette.WithAlpha(th.Palette.Category((MemoryCategory)k), dim ? 0.22 : 0.8)], ring.C, ring.R0, ring.R1, at, at + sw);
            at += sw;
        }
    }

    /// <summary>One ring of bands: fill and inner stripe per visible sector, and the sectors long and thick enough for a label.</summary>
    private void RadialRow(GraphModel m, GraphViewport vp, Theme th, int row, Ring ring, Batch fills, Batch stripes,
        List<(int Slot, double R, double A, double Arc, double Thick)> labels)
    {
        var L = m.Layout;
        double r0 = ring.R0, r1 = ring.R1;
        var (from, to) = Visible(vp, row);
        var slots = L.RowSlots[row];
        for (int i = from; i < to; i++)
        {
            int s = slots[i];
            if (Arc(vp, L.X0[s], L.X1[s], r1) is not { } a || !SectorVisible(ring.C, r0, r1, a.A0, a.A1, ring.Clip)) continue;
            bool dim = Dimmed(s);
            Sector(fills[Fill(m, s, th, dim, row, L.Rows)], ring.C, r0, r1, a.A0, a.A1);
            if ((a.A1 - a.A0) * r0 > 5 && r1 - r0 > 8) Sector(stripes[Stripe(m, s, th, dim)], ring.C, r0, r0 + 2.5, a.A0, a.A1);
            double arcLen = (a.A1 - a.A0) * (r0 + r1) / 2;
            if (arcLen > 34 && r1 - r0 > 14) labels.Add((s, (r0 + r1) / 2, (a.A0 + a.A1) / 2, arcLen, r1 - r0));
        }
    }

    /// <summary>The longest label (title, or 3..1 terms) whose box fits inside its sector, centred on the mid-arc.</summary>
    private void RadialLabel(DrawingContext ctx, GraphModel m, GraphViewport vp, Theme th, (int Slot, double R, double A, double Arc, double Thick) label)
    {
        var (s, r, a, arcLen, _) = label;
        var c = vp.Center;
        var g = m.Nodes[s];
        bool dim = Dimmed(s);
        var (r0, r1) = vp.RingSpan(m.Layout.Row[s]);
        bool full = arcLen >= Math.PI * 2 * r - 1;
        double ang = full ? 0 : a;
        double half = full ? Math.PI : (arcLen / r) / 2;
        var p = GraphViewport.Polar(c, r, ang);
        for (int k = g.IsLeaf ? 1 : Math.Min(3, g.Terms.Length); k >= 1; k--)
        {
            var ft = g.IsLeaf ? Text(g.Title, 10, false, th.Text, "t", Math.Min(160, arcLen * 0.7), 1)
                : Text(string.Join(" · ", g.Terms.Take(k)), 11.5, true, th.Text, "t");
            var box = new Rect(p.X - ft.Width / 2, p.Y - ft.Height / 2, ft.Width, ft.Height);
            if (!InsideSector(box, c, r0 + 2, r1 - 2, ang - half, ang + half, full)) continue;
            using (ctx.PushOpacity(dim ? 0.55 : 1)) ctx.DrawText(ft, box.TopLeft);
            break;
        }
    }

    /// <summary>Centre: what the window shows (the focused node's first term, or all memory) and its leaf count.</summary>
    private void RadialCentre(DrawingContext ctx, GraphModel m, GraphViewport vp, Theme th)
    {
        var c = vp.Center;
        var hole = vp.RingSpan(0).R0;
        if (hole <= 30) return;
        string head = FocusIndex >= 0 && m.TryGetSlot(FocusIndex, out var fs) ? (m.Nodes[fs].Terms.Length > 0 ? m.Nodes[fs].Terms[0] : $"#{FocusIndex}") : "all memory";
        int leaves = FocusIndex >= 0 && m.TryGetSlot(FocusIndex, out var fs2) ? m.Nodes[fs2].LeafCount : m.LayerCounts[0];
        var t1 = Text(head, 12, true, th.Text, "t", hole * 1.7, 1);
        var t2 = Text($"{leaves:N0} leaves", 10.5, false, th.Muted, "m");
        ctx.DrawText(t1, new Point(c.X - t1.Width / 2, c.Y - t1.Height + 1));
        ctx.DrawText(t2, new Point(c.X - t2.Width / 2, c.Y + 2));
    }

    /// <summary>Search hits, path, soft parents, hover, selection and the keyboard cursor over the rings.</summary>
    private void RadialMarks(DrawingContext ctx, GraphModel m, GraphViewport vp, Theme th, HashSet<int>? hits)
    {
        if (hits != null) RadialHits(ctx, m, vp, th, hits);
        if (_path != null) foreach (var s in _path) if (s != Sel(m)) RadialOutline(ctx, m, vp, s, th.PathPen, 0);
        DrawSoftParents(ctx, m, vp, th);
        if (_hover >= 0) RadialOutline(ctx, m, vp, _hover, th.HoverPen, 0);
        if (Sel(m) is var sel && sel >= 0) RadialOutline(ctx, m, vp, sel, th.SelPen, 0.5);
        if (CursorShown) RadialOutline(ctx, m, vp, _cursor, th.CursorPen, 2);
    }

    private void RadialHits(DrawingContext ctx, GraphModel m, GraphViewport vp, Theme th, HashSet<int> hits)
    {
        var L = m.Layout;
        var c = vp.Center;
        var ticks = new Batch();
        var accent = GetColor(th.Accent);
        foreach (var s in hits)
        {
            if (L.Row[s] == L.LeafRow && !vp.ShowLeaves)
            {
                var (r0, r1) = vp.RingSpan(L.LeafRow);
                double u = (L.X0[s] + L.X1[s]) / 2;
                if (u < vp.View.X0 || u > vp.View.X0 + vp.View.Span) continue;
                double ang = vp.Angle(u), w = 1.4 / Math.Max(1, r1);
                Sector(ticks[accent], c, r0 - 2, r1 + 3, ang - w, ang + w);
            }
            else RadialOutline(ctx, m, vp, s, th.HitPen, 0);
        }
        ticks.Flush(ctx, col => Brush(col));
    }

    /// <summary>A band's sector outlined with a pen, grown outward by <paramref name="grow"/> px.</summary>
    private static void RadialOutline(DrawingContext ctx, GraphModel m, GraphViewport vp, int s, IPen pen, double grow)
    {
        var L = m.Layout;
        var (r0, r1) = vp.RingSpan(L.Row[s]);
        if (Arc(vp, L.X0[s], L.X1[s], r1) is not { } a) return;
        var geo = new StreamGeometry();
        using (var g = geo.Open()) Sector(g, vp.Center, Math.Max(0, r0 + 1 - grow), r1 - 1.5 + grow, a.A0, a.A1);
        ctx.DrawGeometry(null, pen, geo);
    }

    // ---- node-link

    private void DrawNodeLink(DrawingContext ctx, GraphModel m, GraphViewport vp, Theme th, HashSet<int>? hits)
    {
        int sel = Sel(m);
        var drawn = NodeLinkShown(m, vp, hits, sel);
        var drawnSet = drawn.ToHashSet();
        DrawNodeLinkEdges(ctx, m, vp, th, drawn, drawnSet);
        var labelSlots = DrawNodeLinkNodes(ctx, m, vp, th, drawn);
        foreach (var s in labelSlots) DrawNodeLinkLabel(ctx, m, vp, th, s);

        if (hits != null)
            foreach (var s in hits)
                if (drawnSet.Contains(s)) NodeRing(ctx, vp, s, th.HitPen, 3);
        DrawSoftParents(ctx, m, vp, th);
        if (_hover >= 0) NodeRing(ctx, vp, _hover, th.HoverPen, 2);
        if (sel >= 0) NodeRing(ctx, vp, sel, th.SelPen, 2.5);
        if (CursorShown) NodeRing(ctx, vp, _cursor, th.CursorPen, 5);
    }

    /// <summary>The nodes worth drawing: wide enough on screen (with a little slack so edges to just-offscreen nodes still enter
    /// the view), or important (selected, on the selection's path, a search hit); leaves only when not aggregated.</summary>
    private List<int> NodeLinkShown(GraphModel m, GraphViewport vp, HashSet<int>? hits, int sel)
    {
        var L = m.Layout;
        var drawn = new List<int>();
        for (int row = 0; row < L.Rows; row++)
        {
            if (row == L.LeafRow && !vp.ShowLeaves) continue;
            double slack = vp.View.Span * 0.15;
            var (from, to) = L.RowRange(row, vp.View.X0 - slack, vp.View.X0 + vp.View.Span + slack);
            var slots = L.RowSlots[row];
            for (int i = from; i < to; i++)
            {
                int s = slots[i];
                double w = (L.X1[s] - L.X0[s]) / vp.View.Span * vp.Plot.Width;
                if (w < 3 && !Important(s, sel, hits)) continue;
                drawn.Add(s);
            }
        }
        return drawn;
    }

    private bool Important(int s, int sel, HashSet<int>? hits) => s == sel || (_lit != null && _path!.Contains(s)) || (hits?.Contains(s) ?? false);

    /// <summary>Edges: parent trunk shares its first control point, so siblings bundle into one stem before fanning out.</summary>
    private void DrawNodeLinkEdges(DrawingContext ctx, GraphModel m, GraphViewport vp, Theme th, List<int> drawn, HashSet<int> drawnSet)
    {
        var normal = new StreamGeometry();
        var faint = new StreamGeometry();
        var lit = new StreamGeometry();
        using (var gn = normal.Open())
        using (var gf = faint.Open())
        using (var gl = lit.Open())
        {
            var edges = new EdgeSinks(gn, gf, gl);
            foreach (var s in drawn) NodeLinkEdge(m, vp, s, drawnSet, edges);
        }
        ctx.DrawGeometry(null, th.EdgeDimPen, faint);
        ctx.DrawGeometry(null, th.EdgePen, normal);
        ctx.DrawGeometry(null, th.EdgeLitPen, lit);
    }

    /// <summary>Where edges go: normal, faint (dimmed or to a leaf), lit (inside the selection).</summary>
    private readonly record struct EdgeSinks(StreamGeometryContext Normal, StreamGeometryContext Faint, StreamGeometryContext Lit);

    private void NodeLinkEdge(GraphModel m, GraphViewport vp, int s, HashSet<int> drawnSet, EdgeSinks edges)
    {
        int p = m.Nodes[s].Parent;
        if (p < 0 || !drawnSet.Contains(p)) return;
        var pc = vp.NodeCenter(p);
        var cc = vp.NodeCenter(s);
        if (BothOffscreen(vp, pc, cc)) return;
        double rp = vp.NodeRadius(p), rc = vp.NodeRadius(s);
        var a = new Point(pc.X, pc.Y + rp);
        var b = new Point(cc.X, cc.Y - rc);
        double dy = b.Y - a.Y;
        var g = EdgeSink(edges, s, p, m.Nodes[s].IsLeaf);
        g.BeginFigure(a, false);
        g.CubicBezierTo(new Point(a.X, a.Y + dy * 0.55), new Point(b.X, b.Y - dy * 0.45), b);
        g.EndFigure(false);
    }

    private static bool BothOffscreen(GraphViewport vp, Point pc, Point cc) =>
        (pc.X < vp.Plot.X && cc.X < vp.Plot.X) || (pc.X > vp.Plot.Right && cc.X > vp.Plot.Right);

    private StreamGeometryContext EdgeSink(EdgeSinks edges, int s, int p, bool leaf)
    {
        bool on = _lit != null && _lit.Contains(s) && _lit.Contains(p);
        bool dim = _lit != null && !on;
        return on ? edges.Lit : dim || leaf ? edges.Faint : edges.Normal;
    }

    /// <summary>Node circles (opacity by token weight, dimmed outside the selection) and dirty marks; returns the slots drawn.</summary>
    private List<int> DrawNodeLinkNodes(DrawingContext ctx, GraphModel m, GraphViewport vp, Theme th, List<int> drawn)
    {
        var fills = new Batch();
        var marks = new Batch();
        var labelSlots = new List<int>();
        foreach (var s in drawn)
        {
            var cc = vp.NodeCenter(s);
            if (cc.X < vp.Plot.X - 20 || cc.X > vp.Plot.Right + 20) continue;
            double r = vp.NodeRadius(s);
            bool dim = Dimmed(s);
            var col = th.Palette.Depth(BaseColor(m, s, th), m.Layout.Row[s], m.Layout.Rows);
            Circle(fills[GraphPalette.WithAlpha(col, (dim ? 0.3 : 0.62 + 0.38 * _weight[s]))], cc, r);
            if (m.Nodes[s].Dirty) Circle(marks[GetColor(th.Warn)], new Point(cc.X + r * 0.75, cc.Y - r * 0.75), 2.4);
            labelSlots.Add(s);
        }
        fills.Flush(ctx, c => Brush(c), th.CardRing);
        marks.Flush(ctx, c => Brush(c));
        return labelSlots;
    }

    /// <summary>The label under a node: a leaf's title when it has room, an inner node's terms that fit.</summary>
    private void DrawNodeLinkLabel(DrawingContext ctx, GraphModel m, GraphViewport vp, Theme th, int s)
    {
        var L = m.Layout;
        var g = m.Nodes[s];
        double w = (L.X1[s] - L.X0[s]) / vp.View.Span * vp.Plot.Width;
        var cc = vp.NodeCenter(s);
        double r = vp.NodeRadius(s);
        FormattedText? ft = g.IsLeaf
            ? (w >= 40 ? Text(g.Title, 10, false, th.Muted, "m", Math.Min(w - 4, 160), 2) : null)
            : TermsFitting(g, w - 6, 11, th);
        if (ft == null) return;
        using (ctx.PushOpacity(Dimmed(s) ? 0.45 : 1)) ctx.DrawText(ft, new Point(cc.X - ft.Width / 2, cc.Y + r + 3));
    }

    /// <summary>A ring around a node, <paramref name="grow"/> px outside its circle.</summary>
    private static void NodeRing(DrawingContext ctx, GraphViewport vp, int s, IPen pen, double grow)
    {
        var cc = vp.NodeCenter(s);
        double r = vp.NodeRadius(s) + grow;
        ctx.DrawEllipse(null, pen, cc, r, r);
    }

    // ---- gutter, legend, minimap, tooltip

    private void DrawGutter(DrawingContext ctx, GraphModel m, GraphViewport vp, Theme th)
    {
        var L = m.Layout;
        double x = 14;
        if (vp.Kind == GraphLayoutKind.Radial)
        {
            double y = vp.Plot.Y + 4;
            ctx.DrawText(Text("rings, inside out", 10, false, th.Faint, "f"), new Point(x, y));
            y += 20;
            for (int row = 0; row < L.Rows; row++)
            {
                int layer = m.NumLayers - row;
                ctx.DrawText(Text(layer == 0 ? "leaves" : $"L{layer}", 11.5, true, th.Muted, "m"), new Point(x, y));
                ctx.DrawText(Text($"{m.LayerCounts[layer]:N0}", 10.5, false, th.Faint, "f"), new Point(x, y + 15));
                y += 36;
            }
            return;
        }
        for (int row = 0; row < L.Rows; row++)
        {
            int layer = m.NumLayers - row;
            var (t, b) = vp.RowSpan(row);
            var name = Text(layer == 0 ? "leaves" : $"L{layer}", 11.5, true, th.Muted, "m");
            var count = Text($"{m.LayerCounts[layer]:N0}", 10.5, false, th.Faint, "f");
            if (b - t >= name.Height + count.Height + 2)
            {
                double y = (t + b) / 2 - (name.Height + count.Height) / 2;
                ctx.DrawText(name, new Point(x, y));
                ctx.DrawText(count, new Point(x, y + name.Height));
            }
            else
            {
                double y = (t + b) / 2 - name.Height / 2;
                ctx.DrawText(name, new Point(x, y));
                ctx.DrawText(count, new Point(x + name.Width + 6, y + 1));
            }
            if (row > 0) ctx.DrawLine(new Pen(th.Border, 1), new Point(x, t - 0.5), new Point(Gutter - 12, t - 0.5));
        }
    }

    private void DrawFooter(DrawingContext ctx, GraphModel m, GraphViewport vp, Theme th, HashSet<int>? hits)
    {
        var row = new FooterRow(this, ctx, th, vp.Plot.Bottom + 12, vp.Plot.Right - 190) { X = vp.Plot.X };
        switch (ColorBy)
        {
            case GraphColorBy.Time: FooterTimeScale(ctx, m, th, row); break;
            case GraphColorBy.Cluster: FooterClusters(m, th, row); break;
            default: FooterKinds(m, th, row); break;
        }
        if (m.Nodes.Any(g => g.Dirty)) row.Swatch(GetColor(th.Warn), "awaiting refresh", round: true);
        if (hits is { Count: > 0 }) row.Swatch(GetColor(th.Accent), $"{hits.Count} search hits", round: true);
        DrawMinimap(ctx, m, vp, th);
    }

    /// <summary>The footer legend being laid out left to right: swatches stop before the minimap's room.</summary>
    private sealed class FooterRow(LayeredGraph owner, DrawingContext ctx, Theme th, double y, double limit)
    {
        public double X;
        public double Y => y;

        public void Swatch(Color c, string label, bool round = false)
        {
            var ft = owner.Text(label, 11, false, th.Muted, "m");
            if (X + 16 + ft.Width > limit) return;
            if (round) ctx.DrawEllipse(owner.Brush(c), null, new Point(X + 4.5, y + 7), 3.5, 3.5);
            else ctx.DrawRectangle(owner.Brush(c), null, new Rect(X, y + 2, 10, 10), 3, 3);
            ctx.DrawText(ft, new Point(X + 15, y));
            X += 15 + ft.Width + 14;
        }
    }

    /// <summary>Colouring by time: oldest and newest leaf times around the gradient.</summary>
    private void FooterTimeScale(DrawingContext ctx, GraphModel m, Theme th, FooterRow row)
    {
        if (m.MaxTime <= m.MinTime) return;
        var p = th.Palette;
        double y = row.Y;
        var older = Text(DateTimeOffset.FromUnixTimeMilliseconds(m.MinTime).ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.InvariantCulture), 11, false, th.Muted, "m");
        var newer = Text(DateTimeOffset.FromUnixTimeMilliseconds(m.MaxTime).ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.InvariantCulture) + "   newest leaf, by rank", 11, false, th.Muted, "m");
        ctx.DrawText(older, new Point(row.X, y));
        row.X += older.Width + 8;
        var grad = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
            GradientStops = { new GradientStop(p.TimeOld, 0), new GradientStop(p.TimeNew, 1) },
        };
        ctx.DrawRectangle(grad, null, new Rect(row.X, y + 3, 110, 8), 4, 4);
        row.X += 118;
        ctx.DrawText(newer, new Point(row.X, y));
        row.X += newer.Width + 16;
    }

    /// <summary>Colouring by cluster: the ranked root clusters by their first term, then "other".</summary>
    private void FooterClusters(GraphModel m, Theme th, FooterRow row)
    {
        var p = th.Palette;
        var ranked = m.TopLevel.Where(s => _rootRank[s] >= 0).OrderBy(s => _rootRank[s]).ToList();
        foreach (var s in ranked.Take(GraphPalette.ClusterSlots))
            row.Swatch(p.Cluster(_rootRank[s]), m.Nodes[s].Terms.Length > 0 ? m.Nodes[s].Terms[0] : $"#{m.Nodes[s].Index}");
        if (ranked.Count > GraphPalette.ClusterSlots || m.TopLevel.Any(s => _rootRank[s] < 0)) row.Swatch(p.Other, "other");
    }

    /// <summary>Colouring by kind: the categories present among the leaves, in fixed order.</summary>
    private static void FooterKinds(GraphModel m, Theme th, FooterRow row)
    {
        var present = new bool[7];
        foreach (var g in m.Nodes) if (g.IsLeaf) present[(int)g.Category] = true;
        for (int c = 0; c < 7; c++) if (present[c]) row.Swatch(th.Palette.Category((MemoryCategory)c), GraphPalette.CategoryNames[c]);
    }

    /// <summary>Minimap: only once zoomed in. The whole icicle in miniature with the visible window.</summary>
    private void DrawMinimap(DrawingContext ctx, GraphModel m, GraphViewport vp, Theme th)
    {
        _minimapShown = vp.View.Span < 0.999;
        if (!_minimapShown) return;
        var L = m.Layout;
        var mm = _minimap = new Rect(vp.Plot.Right - 176, vp.Plot.Bottom + 9, 176, 20);
        ctx.DrawRectangle(th.Card, new Pen(th.Border, 1), mm.Inflate(1), 4, 4);
        var batch = new Batch();
        int rowsDrawn = Math.Max(1, L.Rows - 1);
        double rh = mm.Height / rowsDrawn;
        for (int row = 0; row < rowsDrawn; row++)
            foreach (var s in L.RowSlots[row])
            {
                double x0 = mm.X + L.X0[s] * mm.Width, x1 = mm.X + L.X1[s] * mm.Width;
                if (x1 - x0 < 0.4) continue;
                RoundRect(batch[GraphPalette.WithAlpha(BaseColor(m, s, th), 0.55)], new Rect(x0 + 0.2, mm.Y + row * rh + 0.3, Math.Max(0.3, x1 - x0 - 0.4), rh - 0.6), 0);
            }
        batch.Flush(ctx, c => Brush(c));
        var win = new Rect(mm.X + vp.View.X0 * mm.Width, mm.Y - 1, Math.Max(3, vp.View.Span * mm.Width), mm.Height + 2);
        ctx.DrawRectangle(th.AccentSoft, th.MiniPen, win, 2, 2);
    }

    private void DrawTooltip(DrawingContext ctx, GraphModel m, GraphViewport vp, Theme th)
    {
        int s = _hover;
        Point at = _pointer + new Vector(14, 18);
        if (s < 0 && CursorShown)
        {
            s = _cursor;
            at = CursorTipAnchor(vp, s);
        }
        if (s < 0 || _press != null && _moved) return;
        var tip = TipTexts(m.Nodes[s], th);
        var box = TipBox(at, tip);
        ctx.DrawRectangle(new ImmutableSolidColorBrush(Color.FromArgb(th.Palette.Dark ? (byte)90 : (byte)28, 0, 0, 0)), null, box.Translate(new Vector(0, 3)).Inflate(1), 9, 9);
        ctx.DrawRectangle(th.Card, new Pen(th.Border, 1), box, 8, 8);
        // Category swatch in the corner ties the card to the band's colour.
        ctx.DrawRectangle(Brush(BaseColor(m, s, th)), null, new Rect(box.X, box.Y + 10, 3, Math.Min(26, box.Height - 20)), 1.5, 1.5);
        double x = box.X, ty = box.Y + TipPad;
        ctx.DrawText(tip.Title, new Point(x + TipPad, ty)); ty += tip.Title.Height + 3;
        ctx.DrawText(tip.Meta, new Point(x + TipPad, ty)); ty += tip.Meta.Height + 7;
        ctx.DrawText(tip.Preview, new Point(x + TipPad, ty)); ty += tip.Preview.Height + 6;
        if (tip.When != null) ctx.DrawText(tip.When, new Point(x + TipPad, ty));
    }

    private const double TipWidth = 320, TipPad = 12;

    /// <summary>The tooltip card's lines: title (or terms), meta, preview and when (null when there's nothing to say).</summary>
    private readonly record struct Tip(FormattedText Title, FormattedText Meta, FormattedText Preview, FormattedText? When);

    /// <summary>Where the keyboard cursor's tooltip hangs: under its band, beside its circle, or off its ring edge.</summary>
    private static Point CursorTipAnchor(GraphViewport vp, int s) => vp.Kind switch
    {
        GraphLayoutKind.Icicle => BandRect(vp, s) is { } r ? new Point(Math.Clamp(r.Center.X, vp.Plot.X, vp.Plot.Right), r.Bottom + 6) : vp.Plot.Center,
        GraphLayoutKind.NodeLink => vp.NodeCenter(s) + new Vector(12, 12),
        _ => Anchor(vp, s, top: false) + new Vector(10, 10),
    };

    private Tip TipTexts(GraphNode g, Theme th)
    {
        string title = g.IsLeaf ? g.Title : g.Terms.Length > 0 ? string.Join(" · ", g.Terms) : g.Title;
        var t1 = Text(title, 12.5, true, th.Text, "t", TipWidth - 2 * TipPad, 2);
        var t2 = Text(TipMeta(g), 10.5, false, th.Muted, "m", TipWidth - 2 * TipPad, 2);
        var t3 = Text(g.Preview, 11.5, false, th.Text, "t", TipWidth - 2 * TipPad, 2);
        string when = TipWhen(g);
        var t4 = when.Length > 0 ? Text(when, 10.5, false, g.Dirty ? th.Warn : th.Faint, g.Dirty ? "w" : "f") : null;
        return new Tip(t1, t2, t3, t4);
    }

    private static string TipMeta(GraphNode g)
    {
        string meta = g.IsLeaf
            ? $"leaf #{g.Index} · {GraphPalette.CategoryNames[(int)g.Category]} · {g.Tokens:N0} tokens"
            : $"L{g.Layer} #{g.Index} · {g.ChildCount} children · {g.LeafCount:N0} leaves · {g.Tokens:N0} tokens";
        if (g.OtherParents.Length > 0) meta += $" · {g.OtherParents.Length + 1} parents";
        return meta;
    }

    private static string TipWhen(GraphNode g)
    {
        string when = g.Newest > 0 ? (g.IsLeaf ? "updated " : "newest leaf ") +
            DateTimeOffset.FromUnixTimeMilliseconds(g.Newest).ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : "";
        if (g.Dirty) when += (when.Length > 0 ? " · " : "") + "awaiting refresh";
        return when;
    }

    /// <summary>The card's rect: sized to its lines (180..320 wide), flipped left/up when it would leave the control.</summary>
    private Rect TipBox(Point at, Tip tip)
    {
        double h = TipPad + tip.Title.Height + 3 + tip.Meta.Height + 7 + tip.Preview.Height + (tip.When != null ? 6 + tip.When.Height : 0) + TipPad;
        double w = Math.Max(Math.Max(tip.Title.Width, tip.Meta.Width), Math.Max(tip.Preview.Width, tip.When?.Width ?? 0)) + 2 * TipPad;
        w = Math.Min(TipWidth, Math.Max(180, w));
        double x = at.X, y = at.Y;
        if (x + w > Bounds.Width - 4) x = Math.Max(4, at.X - w - 24);
        if (y + h > Bounds.Height - 4) y = Math.Max(4, at.Y - h - 26);
        return new Rect(x, y, w, h);
    }

    // ------------------------------------------------------------------ input

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        if (_vp == null) return;
        double delta = e.Delta.Y != 0 ? e.Delta.Y : e.Delta.X;
        ZoomBy(Math.Pow(1.3, delta), e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        Focus();
        var p = e.GetPosition(this);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _keyboardTip = _pinTip = false;
        if (_minimapShown && _minimap.Inflate(4).Contains(p))
        {
            _dragMinimap = true;
            CenterOnMinimap(p.X);
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }
        if (e.ClickCount == 2 && _vp is { } vp && Model is { } m)
        {
            DoubleClick(m, vp.HitTest(p));
            e.Handled = true;
            return;
        }
        _press = p;
        _pressView = Current;
        SetView(_pressView, false);
        _moved = false;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    private void CenterOnMinimap(double x)
    {
        var t = Target;
        double c = Math.Clamp((x - _minimap.X) / _minimap.Width, 0, 1);
        SetView(t with { X0 = c - t.Span / 2 }, false);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        var p = e.GetPosition(this);
        _pointer = p;
        if (_dragMinimap) { CenterOnMinimap(p.X); return; }
        if (_press is { } d)
        {
            DragTo(p, d);
            return;
        }
        int hit = _vp?.HitTest(p) ?? -1;
        if (hit != _hover || hit >= 0)
        {
            _hover = hit;
            InvalidateVisual();
        }
    }

    /// <summary>A drag past 4 px pans: the radial view moves its centre, the others scroll the window along the tree.</summary>
    private void DragTo(Point p, Point d)
    {
        if (!_moved && Math.Abs(p.X - d.X) + Math.Abs(p.Y - d.Y) > 4) _moved = true;
        if (!_moved || _vp is not { } vp) return;
        if (LayoutKind == GraphLayoutKind.Radial)
            SetView(_pressView with { PanX = _pressView.PanX + p.X - d.X, PanY = _pressView.PanY + p.Y - d.Y }, false);
        else
            SetView(_pressView with { X0 = _pressView.X0 - (p.X - d.X) / vp.Plot.Width * _pressView.Span }, false);
    }

    /// <summary>Double-click on a node: select it and focus it (a leaf focuses its parent); again on the focus fits it.</summary>
    private void DoubleClick(GraphModel m, int s)
    {
        if (s < 0) return;
        int f = m.Nodes[s].Children.Length == 0 && m.Nodes[s].Parent >= 0 ? m.Nodes[s].Parent : s;
        SelectedIndex = m.Nodes[s].Index;
        if (FocusIndex == m.Nodes[f].Index) Fit();
        else FocusIndex = m.Nodes[f].Index;
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_hover != -1) { _hover = -1; InvalidateVisual(); }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        e.Pointer.Capture(null);
        if (_dragMinimap) { _dragMinimap = false; return; }
        var start = _press;
        _press = null;
        if (start == null || _moved || _vp is not { } vp || Model is not { } m) { InvalidateVisual(); return; }
        int s = vp.HitTest(e.GetPosition(this));
        if (s >= 0)
        {
            _cursor = s;
            SelectedIndex = m.Nodes[s].Index;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (Model is not { } m || m.Nodes.Length == 0) { base.OnKeyDown(e); return; }
        if (_cursor < 0 || _cursor >= m.Nodes.Length) _cursor = Sel(m) >= 0 ? Sel(m) : m.TopLevel[0];
        int c = _cursor;
        if (KeyCommand(e.Key, m, c)) { e.Handled = true; return; }
        if (NavigateKey(e.Key, m, c) is not { } next) { base.OnKeyDown(e); return; }
        _cursor = next;
        _keyboardTip = true;
        _hover = -1;
        EnsureVisible(next);
        InvalidateVisual();
        e.Handled = true;
    }

    /// <summary>The keys that act rather than move the cursor: Enter selects, Esc steps the focus out, Home resets, F fits,
    /// +/- zoom. False for any other key.</summary>
    private bool KeyCommand(Key key, GraphModel m, int c)
    {
        switch (key)
        {
            case Key.Enter: SelectedIndex = m.Nodes[c].Index; return true;
            case Key.Escape: FocusOut(m); return true;
            case Key.Home: Reset(); return true;
            case Key.F: Fit(); return true;
            case Key.OemPlus: case Key.Add: ZoomBy(1.5); return true;
            case Key.OemMinus: case Key.Subtract: ZoomBy(1 / 1.5); return true;
            default: return false;
        }
    }

    /// <summary>Esc: focus the focused node's parent (none above a root), or zoom out to the whole tree when nothing is focused.</summary>
    private void FocusOut(GraphModel m)
    {
        if (FocusIndex >= 0 && m.TryGetSlot(FocusIndex, out var fs))
            FocusIndex = m.Nodes[fs].Parent >= 0 ? m.Nodes[m.Nodes[fs].Parent].Index : -1;
        else SetView(ViewState.Whole, true);
    }

    /// <summary>Where an arrow key moves the cursor (up: parent, down: first child, left/right: the neighbour band in its row;
    /// it stays put at an edge); null for a key that doesn't navigate.</summary>
    private static int? NavigateKey(Key key, GraphModel m, int c) => key switch
    {
        Key.Up => m.Nodes[c].Parent >= 0 ? m.Nodes[c].Parent : c,
        Key.Down => m.Nodes[c].Children.Length > 0 ? m.Nodes[c].Children[0] : c,
        Key.Left => Neighbour(m.Layout, c, -1),
        Key.Right => Neighbour(m.Layout, c, 1),
        _ => null,
    };

    private static int Neighbour(PartitionLayout L, int c, int step)
    {
        int row = L.Row[c];
        int i = Array.BinarySearch(L.RowStarts[row], L.X0[c]);
        if (i < 0) return c;
        i += step;
        return i >= 0 && i < L.RowSlots[row].Length ? L.RowSlots[row][i] : c;
    }

    /// <summary>Pans (and if needed zooms) so the band is inside the window; leaves hidden by LOD get zoomed into view.</summary>
    private void EnsureVisible(int s)
    {
        if (Model is not { } m || Bounds.Width < 10) return;
        var L = m.Layout;
        var t = Target;
        double a = L.X0[s], b = L.X1[s];
        double span = t.Span;
        bool outside = b <= t.X0 || a >= t.X0 + t.Span;
        if (LayoutKind == GraphLayoutKind.Radial)
        {
            if (outside) SetView(t with { X0 = 0, Span = 1 }, true);
            return;
        }
        if (m.Nodes[s].IsLeaf && LeafDetailSpan(L) is { } need && span > need) { span = need; outside = true; }
        if (outside) CentreOn(t, a, b, span);
    }

    /// <summary>The span at which each leaf gets 2.5× the leaf-detail threshold (null for an empty tree).</summary>
    private double? LeafDetailSpan(PartitionLayout L)
    {
        if (L.TotalLeaves <= 0) return null;
        double plotW = Math.Max(100, Bounds.Width - Gutter - RightPad);
        return plotW / (GraphLod.LeafDetailPx * 2.5) / L.TotalLeaves;
    }

    /// <summary>Animates the window to centre on [a, b] at <paramref name="span"/> (widened to fit the band), unless it already is.</summary>
    private void CentreOn(ViewState t, double a, double b, double span)
    {
        if (b - a > span) span = Math.Min(1, (b - a) * 1.15);
        double x0 = (a + b) / 2 - span / 2;
        if (Math.Abs(span - t.Span) < 1e-12 && Math.Abs(x0 - t.X0) < 1e-12) return;
        SetView(t with { X0 = x0, Span = span }, true);
    }
}
