using Avalonia;

namespace MineClaw.MemoryExplorer.Controls;

/// <summary>
/// Icicle/partition layout in unit space: each node is the band [X0, X1) on its row (row 0 = the top layer, the last row = leaves),
/// its width proportional to its leaf count and nested inside its primary parent's band. The radial and node-link views reuse it
/// (x becomes an angle, or a node sits at its band's centre). Computed once per model.
/// </summary>
public sealed class PartitionLayout
{
    public required double[] X0 { get; init; }
    public required double[] X1 { get; init; }
    public required int[] Row { get; init; }
    public required int Rows { get; init; }
    /// <summary>Slots of each row sorted by X0 (for culling and hit-testing by binary search).</summary>
    public required int[][] RowSlots { get; init; }
    /// <summary>X0 of <see cref="RowSlots"/>, parallel arrays.</summary>
    public required double[][] RowStarts { get; init; }
    /// <summary>Contiguous runs of leaves under one parent: the leaf strip drawn as one band per run when leaves are hidden (LOD).</summary>
    public required (double X0, double X1, int Owner)[] LeafRuns { get; init; }
    public int TotalLeaves { get; init; }
    public int LeafRow => Rows - 1;

    public static PartitionLayout Compute(GraphModel m)
    {
        int n = m.Nodes.Length;
        var x0 = new double[n];
        var x1 = new double[n];
        var row = new int[n];
        int total = m.TopLevel.Sum(s => m.Nodes[s].Leaves);
        double unit = total > 0 ? 1.0 / total : 1;
        // Top-down: each node splits its band among its primary children by leaf count.
        var stack = new Stack<(int Slot, double A, double B)>();
        double cursor = 0;
        foreach (var s in m.TopLevel)
        {
            double w = m.Nodes[s].Leaves * unit;
            stack.Push((s, cursor, cursor + w));
            cursor += w;
        }
        while (stack.Count > 0)
        {
            var (s, a, b) = stack.Pop();
            var g = m.Nodes[s];
            x0[s] = a;
            x1[s] = b;
            row[s] = m.NumLayers - g.Layer;
            if (g.Children.Length == 0) continue;
            int sum = g.Children.Sum(c => m.Nodes[c].Leaves);
            double at = a;
            for (int i = 0; i < g.Children.Length; i++)
            {
                int c = g.Children[i];
                double end = i == g.Children.Length - 1 ? b : at + (b - a) * m.Nodes[c].Leaves / sum;
                stack.Push((c, at, end));
                at = end;
            }
        }
        int rows = m.NumLayers + 1;
        var bucket = Enumerable.Range(0, rows).Select(_ => new List<int>()).ToArray();
        for (int s = 0; s < n; s++) bucket[row[s]].Add(s);
        var rowSlots = bucket.Select(b => b.OrderBy(s => x0[s]).ThenBy(s => s).ToArray()).ToArray();
        var rowStarts = rowSlots.Select(r => r.Select(s => x0[s]).ToArray()).ToArray();

        var runs = new List<(double, double, int)>();
        foreach (var s in rowSlots[rows - 1])
        {
            int owner = m.Nodes[s].Parent;
            if (runs.Count > 0 && runs[^1].Item3 == owner && Math.Abs(runs[^1].Item2 - x0[s]) < 1e-12)
                runs[^1] = (runs[^1].Item1, x1[s], owner);
            else runs.Add((x0[s], x1[s], owner));
        }
        return new PartitionLayout
        {
            X0 = x0, X1 = x1, Row = row, Rows = rows, RowSlots = rowSlots, RowStarts = rowStarts, LeafRuns = runs.ToArray(), TotalLeaves = total,
        };
    }

    /// <summary>The slot on <paramref name="row"/> whose band contains unit x <paramref name="ux"/>, or -1.</summary>
    public int SlotAt(int row, double ux)
    {
        if (row < 0 || row >= Rows) return -1;
        var starts = RowStarts[row];
        int lo = 0, hi = starts.Length - 1, found = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            if (starts[mid] <= ux) { found = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        if (found < 0) return -1;
        int s = RowSlots[row][found];
        return ux < X1[s] ? s : -1;
    }

    /// <summary>Index range [from, to) of the row's slots whose bands can overlap the unit range [a, b].</summary>
    public (int From, int To) RowRange(int row, double a, double b)
    {
        var starts = RowStarts[row];
        // First band starting after b is the end; bands are disjoint, so the start is the last band starting at or before a.
        int lo = 0, hi = starts.Length;
        while (lo < hi) { int mid = (lo + hi) >> 1; if (starts[mid] <= b) lo = mid + 1; else hi = mid; }
        int to = lo;
        lo = 0; hi = to;
        while (lo < hi) { int mid = (lo + hi) >> 1; if (starts[mid] <= a) lo = mid + 1; else hi = mid; }
        return (Math.Max(0, lo - 1), to);
    }
}

public enum GraphLayoutKind { Icicle, Radial, NodeLink }

public enum GraphColorBy { Kind, Time, Cluster }

/// <summary>Level-of-detail thresholds (screen pixels).</summary>
public static class GraphLod
{
    /// <summary>Individual leaves are drawn only when each one gets at least this many pixels; below it the leaf strip is one band
    /// per parent.</summary>
    public const double LeafDetailPx = 3;
    /// <summary>Bands thinner than this are culled (their parent's band already covers them).</summary>
    public const double MinBandPx = 0.6;
    /// <summary>Leaf strip height as a fraction of a summary row when leaves are hidden.</summary>
    public const double LeafStripFrac = 0.16;

    public static bool ShowLeaves(double pixelsPerLeaf) => pixelsPerLeaf >= LeafDetailPx;

    /// <summary>Leaf row height fraction: a thin strip below the threshold, growing to a full row by twice the threshold.</summary>
    public static double LeafRowFrac(double pixelsPerLeaf)
    {
        double t = Math.Clamp((pixelsPerLeaf - LeafDetailPx) / LeafDetailPx, 0, 1);
        t = t * t * (3 - 2 * t);
        return LeafStripFrac + (1 - LeafStripFrac) * t;
    }
}

/// <summary>The animated part of the view: the visible unit-x window (zoom/pan for the icicle and node-link views, the angular
/// window of the sunburst) and the sunburst's geometric zoom and pan.</summary>
public readonly record struct ViewState(double X0, double Span, double Scale, double PanX, double PanY)
{
    public static ViewState Whole => new(0, 1, 1, 0, 0);

    public static ViewState Lerp(ViewState a, ViewState b, double t)
    {
        // Zoom interpolates in log space so a deep zoom doesn't rush through the last frames; the window centre moves linearly.
        double span = Math.Exp(Math.Log(a.Span) + (Math.Log(b.Span) - Math.Log(a.Span)) * t);
        double ca = a.X0 + a.Span / 2, cb = b.X0 + b.Span / 2;
        double c = ca + (cb - ca) * t;
        double scale = Math.Exp(Math.Log(a.Scale) + (Math.Log(b.Scale) - Math.Log(a.Scale)) * t);
        return new ViewState(c - span / 2, span, scale, a.PanX + (b.PanX - a.PanX) * t, a.PanY + (b.PanY - a.PanY) * t);
    }
}

/// <summary>
/// Pure screen geometry of one frame: unit space → pixels for the three views, row/ring spans with the LOD leaf strip, and
/// hit-testing. Shared by drawing and tests.
/// </summary>
public sealed class GraphViewport
{
    public GraphViewport(GraphModel model, GraphLayoutKind kind, Rect plot, ViewState view)
    {
        Model = model;
        Kind = kind;
        Plot = plot;
        View = view;
        PixelsPerLeaf = ComputePixelsPerLeaf();
        LeafFrac = GraphLod.LeafRowFrac(PixelsPerLeaf);
        ShowLeaves = GraphLod.ShowLeaves(PixelsPerLeaf);
    }

    public GraphModel Model { get; }
    public PartitionLayout Layout => Model.Layout;
    public GraphLayoutKind Kind { get; }
    public Rect Plot { get; }
    public ViewState View { get; }
    public double PixelsPerLeaf { get; }
    public double LeafFrac { get; }
    public bool ShowLeaves { get; }
    public int Rows => Layout.Rows;

    // ---- icicle / node-link ----

    public double ScreenX(double ux) => Plot.X + (ux - View.X0) / View.Span * Plot.Width;
    public double UnitX(double sx) => View.X0 + (sx - Plot.X) / Plot.Width * View.Span;

    private double RowUnit => Plot.Height / Math.Max(0.5, Rows - 1 + LeafFrac);

    public (double Top, double Bottom) RowSpan(int row)
    {
        double h = RowUnit;
        double top = Plot.Y + row * h;
        return (top, top + (row == Rows - 1 ? h * LeafFrac : h));
    }

    // ---- radial ----

    public Point Center => new(Plot.X + Plot.Width / 2 + View.PanX, Plot.Y + Plot.Height / 2 + View.PanY);
    public double Radius => Math.Min(Plot.Width, Plot.Height) / 2 * View.Scale;
    private double Hole => Radius * 0.16;

    public (double R0, double R1) RingSpan(int row)
    {
        double ring = (Radius - Hole) / Math.Max(0.5, Rows - 1 + LeafFrac);
        double r0 = Hole + row * ring;
        return (r0, r0 + (row == Rows - 1 ? ring * LeafFrac : ring));
    }

    /// <summary>Angle in radians (0 = 12 o'clock, clockwise) of a unit x inside the angular window.</summary>
    public double Angle(double ux) => (ux - View.X0) / View.Span * Math.PI * 2;

    public static Point Polar(Point c, double r, double angle) => new(c.X + r * Math.Sin(angle), c.Y - r * Math.Cos(angle));

    private double ComputePixelsPerLeaf()
    {
        if (Layout.TotalLeaves == 0) return 0;
        double unitPerLeaf = 1.0 / Layout.TotalLeaves;
        if (Kind != GraphLayoutKind.Radial) return unitPerLeaf / View.Span * Plot.Width;
        // Arc length at the leaf ring (outer edge of the plot).
        double r = Math.Min(Plot.Width, Plot.Height) / 2 * View.Scale * 0.97;
        return unitPerLeaf / View.Span * Math.PI * 2 * r;
    }

    // ---- node-link ----

    public Point NodeCenter(int slot)
    {
        var (t, b) = RowSpan(Layout.Row[slot]);
        return new Point(ScreenX((Layout.X0[slot] + Layout.X1[slot]) / 2), (t + b) / 2);
    }

    public double NodeRadius(int slot)
    {
        var g = Model.Nodes[slot];
        if (g.IsLeaf) return Math.Clamp(PixelsPerLeaf * 0.3, 2, 5);
        double share = (double)g.Leaves / Math.Max(1, Layout.TotalLeaves);
        return Math.Clamp(3 + 14 * Math.Sqrt(share / View.Span), 3.5, 18);
    }

    /// <summary>The node under a screen point, or -1. With leaves hidden (LOD) the leaf strip answers with the leaf's parent.</summary>
    public int HitTest(Point p)
    {
        switch (Kind)
        {
            case GraphLayoutKind.Radial:
            {
                var c = Center;
                double dx = p.X - c.X, dy = p.Y - c.Y, r = Math.Sqrt(dx * dx + dy * dy);
                double ang = Math.Atan2(dx, -dy);
                if (ang < 0) ang += Math.PI * 2;
                double ux = View.X0 + ang / (Math.PI * 2) * View.Span;
                for (int row = 0; row < Rows; row++)
                {
                    var (r0, r1) = RingSpan(row);
                    if (r >= r0 && r < r1) return Resolve(row, ux);
                }
                return -1;
            }
            case GraphLayoutKind.NodeLink:
            {
                int row = RowAt(p.Y);
                if (row < 0) return -1;
                if (row == Layout.LeafRow && !ShowLeaves) return -1;
                double ux = UnitX(p.X);
                double slack = View.Span * 24 / Math.Max(1, Plot.Width);
                var (from, to) = Layout.RowRange(row, ux - slack, ux + slack);
                int best = -1;
                double bestD = double.MaxValue;
                for (int i = from; i < to; i++)
                {
                    int s = Layout.RowSlots[row][i];
                    var nc = NodeCenter(s);
                    double rr = NodeRadius(s) + 4, d = (nc.X - p.X) * (nc.X - p.X) + (nc.Y - p.Y) * (nc.Y - p.Y);
                    if (d <= rr * rr && d < bestD) { bestD = d; best = s; }
                }
                return best;
            }
            default:
            {
                int row = RowAt(p.Y);
                return row < 0 || p.X < Plot.X || p.X > Plot.Right ? -1 : Resolve(row, UnitX(p.X));
            }
        }
    }

    private int RowAt(double y)
    {
        for (int row = 0; row < Rows; row++)
        {
            var (t, b) = RowSpan(row);
            if (y >= t && y < b) return row;
        }
        return -1;
    }

    private int Resolve(int row, double ux)
    {
        int s = Layout.SlotAt(row, ux);
        if (s >= 0 && row == Layout.LeafRow && !ShowLeaves && Model.Nodes[s].Parent >= 0) return Model.Nodes[s].Parent;
        return s;
    }
}
