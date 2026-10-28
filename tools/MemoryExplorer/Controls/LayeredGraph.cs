using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using MineClaw.Raptor.Model;

namespace MineClaw.MemoryExplorer.Controls;

/// <summary>
/// Layout of the whole tree as layered rows (roots on top, leaves at the bottom). Leaves get evenly spaced x positions in DFS
/// order from the roots, a summary sits at the mean x of its children, so every subtree occupies one horizontal band.
/// </summary>
public sealed class GraphModel
{
    public required int[] Index { get; init; }
    public required int[] Layer { get; init; }
    /// <summary>0..1 horizontal position.</summary>
    public required double[] X { get; init; }
    public required (int Parent, int Child)[] Edges { get; init; }
    public required int NumLayers { get; init; }
    public required Dictionary<int, int> Slot { get; init; }

    public static GraphModel Build(RaptorTree t)
    {
        var order = new List<int>();
        var seen = new HashSet<int>();
        // Iterative DFS from the roots: leaves in visiting order.
        foreach (var root in t.Roots.OrderBy(r => r.Index))
        {
            var stack = new Stack<int>();
            stack.Push(root.Index);
            while (stack.Count > 0)
            {
                int i = stack.Pop();
                if (!seen.Add(i) || !t.TryGet(i, out var n)) continue;
                if (n.IsLeaf) order.Add(i);
                foreach (var c in n.Children.Reverse()) stack.Push(c);
            }
        }
        foreach (var l in t.Leaves) if (seen.Add(l.Index)) order.Add(l.Index);

        var x = new Dictionary<int, double>();
        for (int k = 0; k < order.Count; k++) x[order[k]] = order.Count <= 1 ? 0.5 : (k + 0.5) / order.Count;
        for (int layer = 1; layer <= t.NumLayers; layer++)
            foreach (var n in t.Layer(layer))
            {
                var xs = n.Children.Where(x.ContainsKey).Select(c => x[c]).ToList();
                x[n.Index] = xs.Count > 0 ? xs.Average() : 0.5;
            }
        var nodes = t.AllNodes.Values.OrderBy(n => n.Layer).ThenBy(n => n.Index).ToList();
        var slot = new Dictionary<int, int>(nodes.Count);
        for (int k = 0; k < nodes.Count; k++) slot[nodes[k].Index] = k;
        var edges = new List<(int, int)>();
        foreach (var n in nodes)
            foreach (var c in n.Children)
                if (slot.TryGetValue(c, out var cs)) edges.Add((slot[n.Index], cs));
        return new GraphModel
        {
            Index = nodes.Select(n => n.Index).ToArray(),
            Layer = nodes.Select(n => n.Layer).ToArray(),
            X = nodes.Select(n => x.GetValueOrDefault(n.Index, 0.5)).ToArray(),
            Edges = edges.ToArray(),
            NumLayers = t.NumLayers,
            Slot = slot,
        };
    }
}

/// <summary>
/// Draws a <see cref="GraphModel"/>: one row per layer, edges as faint lines, the selected node's subtree and ancestors in the
/// accent colour. Wheel = zoom (around the pointer), drag = pan, click = select.
/// </summary>
public sealed class LayeredGraph : Control
{
    public static readonly StyledProperty<GraphModel?> ModelProperty = AvaloniaProperty.Register<LayeredGraph, GraphModel?>(nameof(Model));
    public static readonly StyledProperty<int> SelectedIndexProperty =
        AvaloniaProperty.Register<LayeredGraph, int>(nameof(SelectedIndex), -1, defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    private double _zoom = 1, _offset; // horizontal zoom and pan (in content pixels)
    private Point? _drag;
    private double _dragOffset;
    private bool _moved;
    private HashSet<int>? _lit;
    private int _litFor = int.MinValue;

    static LayeredGraph()
    {
        AffectsRender<LayeredGraph>(ModelProperty, SelectedIndexProperty);
    }

    public LayeredGraph() => ClipToBounds = true;

    public GraphModel? Model { get => GetValue(ModelProperty); set => SetValue(ModelProperty, value); }
    public int SelectedIndex { get => GetValue(SelectedIndexProperty); set => SetValue(SelectedIndexProperty, value); }

    private const double Pad = 28;

    private double RowY(int layer, GraphModel m, double h)
    {
        int rows = Math.Max(1, m.NumLayers);
        return Pad + (m.NumLayers - layer) * (h - 2 * Pad) / rows;
    }

    private double ColX(double x, double w) => Pad + (x * (w - 2 * Pad)) * _zoom - _offset;

    private IBrush Res(string key, IBrush fallback) =>
        this.TryFindResource(key, ActualThemeVariant, out var v) && v is IBrush b ? b : fallback;

    public override void Render(DrawingContext ctx)
    {
        var m = Model;
        var w = Bounds.Width;
        var h = Bounds.Height;
        ctx.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        if (m == null || m.Index.Length == 0 || w < 50 || h < 50) return;
        var edge = new Pen(Res("GraphEdgeBrush", new SolidColorBrush(Color.FromArgb(50, 80, 90, 200))), 1);
        var accent = Res("AccentBrush", Brushes.SlateBlue);
        var accentPen = new Pen(accent, 1.4);
        var text = Res("TextMutedBrush", Brushes.Gray);
        var node = Res("TextFaintBrush", Brushes.Gray);
        var lit = Lit(m);

        // Layer labels.
        for (int l = 0; l <= m.NumLayers; l++)
        {
            var ft = new FormattedText(l == 0 ? "leaves" : $"L{l}", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                Typeface.Default, 10, text);
            ctx.DrawText(ft, new Point(4, RowY(l, m, h) - 16));
        }
        foreach (var (p, c) in m.Edges)
        {
            var a = new Point(ColX(m.X[p], w), RowY(m.Layer[p], m, h));
            var b = new Point(ColX(m.X[c], w), RowY(m.Layer[c], m, h));
            if ((a.X < 0 && b.X < 0) || (a.X > w && b.X > w)) continue;
            bool on = lit != null && lit.Contains(p) && lit.Contains(c);
            // Leaf edges would paint the bottom band solid (thousands of them): only the selected subtree's are drawn.
            if (!on && m.Layer[c] == 0) continue;
            ctx.DrawLine(on ? accentPen : edge, a, b);
        }
        for (int k = 0; k < m.Index.Length; k++)
        {
            double x = ColX(m.X[k], w);
            if (x < -6 || x > w + 6) continue;
            double y = RowY(m.Layer[k], m, h);
            bool on = lit != null && lit.Contains(k);
            double r = m.Layer[k] == 0 ? 1.6 : 2.5 + Math.Min(4, m.Layer[k]);
            if (m.Index[k] == SelectedIndex) { ctx.DrawEllipse(accent, null, new Point(x, y), r + 3, r + 3); continue; }
            ctx.DrawEllipse(on ? accent : node, null, new Point(x, y), r, r);
        }
    }

    /// <summary>Slots of the selected node, its ancestors and its whole subtree.</summary>
    private HashSet<int>? Lit(GraphModel m)
    {
        if (SelectedIndex < 0 || !m.Slot.TryGetValue(SelectedIndex, out var s)) return null;
        if (_litFor == SelectedIndex && _lit != null) return _lit;
        var children = new Dictionary<int, List<int>>();
        var parents = new Dictionary<int, List<int>>();
        foreach (var (p, c) in m.Edges)
        {
            (children.TryGetValue(p, out var l) ? l : children[p] = []).Add(c);
            (parents.TryGetValue(c, out var q) ? q : parents[c] = []).Add(p);
        }
        var lit = new HashSet<int> { s };
        var stack = new Stack<int>();
        stack.Push(s);
        while (stack.Count > 0)
            if (children.TryGetValue(stack.Pop(), out var cs))
                foreach (var c in cs) if (lit.Add(c)) stack.Push(c);
        stack.Push(s);
        while (stack.Count > 0)
            if (parents.TryGetValue(stack.Pop(), out var ps))
                foreach (var p in ps) if (lit.Add(p)) stack.Push(p);
        _litFor = SelectedIndex;
        return _lit = lit;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        var w = Bounds.Width;
        var px = e.GetPosition(this).X;
        double content = (px + _offset - Pad) / _zoom; // content x under the pointer
        _zoom = Math.Clamp(_zoom * (e.Delta.Y > 0 ? 1.25 : 0.8), 1, 400);
        _offset = Math.Max(0, Pad + content * _zoom - px);
        Clamp(w);
        InvalidateVisual();
        e.Handled = true;
    }

    private void Clamp(double w) => _offset = Math.Clamp(_offset, 0, Math.Max(0, (w - 2 * Pad) * (_zoom - 1)));

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        _drag = e.GetPosition(this);
        _dragOffset = _offset;
        _moved = false;
        e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (_drag is not { } d) return;
        var p = e.GetPosition(this);
        if (Math.Abs(p.X - d.X) > 3) _moved = true;
        _offset = _dragOffset - (p.X - d.X);
        Clamp(Bounds.Width);
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        var start = _drag;
        _drag = null;
        e.Pointer.Capture(null);
        if (start == null || _moved || Model is not { } m) return;
        var p = e.GetPosition(this);
        double best = 14 * 14;
        int hit = -1;
        for (int k = 0; k < m.Index.Length; k++)
        {
            double dx = ColX(m.X[k], Bounds.Width) - p.X, dy = RowY(m.Layer[k], m, Bounds.Height) - p.Y;
            double d2 = dx * dx + dy * dy;
            if (d2 < best) { best = d2; hit = m.Index[k]; }
        }
        if (hit >= 0) SelectedIndex = hit;
    }
}
