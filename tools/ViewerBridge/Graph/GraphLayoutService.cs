namespace MineClaw.ViewerBridge.Graph;

/// <summary>Wire shapes of <c>/v1/raptor/graph</c> (contracts raptor.ts: RaptorGraph, GraphNode, GraphEdge, GraphLegendEntry).</summary>
public sealed record GraphCrumb(int Index, int Layer, string Label);
public sealed record GraphNodeDto(int Id, int? Parent, int Layer, bool IsLeaf, double X, double Y, double? W, double? H, double? A0, double? A1, double? R0,
    double? R1, double? R, string Label, IReadOnlyList<string> Terms, string Category, long? Newest, double TimeRank, int Cluster, int Weight, int Tokens,
    string Color, double Opacity);
public sealed record GraphEdgeDto(int Source, int Target, bool Primary, double Weight);
public sealed record GraphLegendDto(string Key, string Label, string Color);
public sealed record RaptorGraphDto(string Layout, string ColorBy, string StateKey, int? Focus, IReadOnlyList<GraphCrumb> Breadcrumb,
    IReadOnlyList<GraphNodeDto> Nodes, IReadOnlyList<GraphEdgeDto> Edges, IReadOnlyList<GraphLegendDto> Legend);

/// <summary>Categorical hues in a fixed, CVD-validated order (the explorer's light palette); time is one blue ramp old → new.</summary>
public static class GraphPalette
{
    public static readonly string[] Categorical = ["#2a78d6", "#eb6834", "#1baf7a", "#eda100", "#e87ba4", "#008300", "#6250d6", "#e34948"];
    public const string Other = "#b4b9c6";
    public const string TimeOld = "#b7d3f6";
    public const string TimeNew = "#184f95";
    public const int ClusterSlots = 7;

    public static string Mix(string a, string b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        int Ch(string h, int i) => Convert.ToInt32(h.Substring(1 + i * 2, 2), 16);
        int M(int i) => (int)Math.Round(Ch(a, i) + (Ch(b, i) - Ch(a, i)) * t);
        return $"#{M(0):x2}{M(1):x2}{M(2):x2}";
    }
}

/// <summary>
/// Server-side layout of the RAPTOR partition graph in unit space (x, y ∈ [0,1]), from <see cref="PartitionLayout"/>:
/// icicle bands (x,y centre + w,h), radial rings (a0,a1 radians from 12 o'clock clockwise; r0,r1 ∈ [0,0.5]; x,y = the ring
/// segment's centroid around (0.5,0.5)), node-link circles (x,y centre + r). A focus lays out that node's primary subtree alone.
/// </summary>
public static class GraphLayoutService
{
    private const double Hole = 0.08;

    public static RaptorGraphDto Layout(GraphModel m, string layout, string colorBy, int? focus, string stateKey)
    {
        var frame = Frame.Of(m, focus);
        var place = new Placer(m, frame, layout, colorBy);
        var nodes = new List<GraphNodeDto>(frame.Included.Count);
        var edges = new List<GraphEdgeDto>();
        foreach (var s in frame.Included.Order())
        {
            var node = place.Node(s);
            nodes.Add(node);
            if (node.Parent is { } p) edges.Add(new GraphEdgeDto(p, node.Id, true, 1));
            foreach (var op in m.Nodes[s].OtherParents)
                if (frame.Included.Contains(op)) edges.Add(new GraphEdgeDto(m.Nodes[op].Index, node.Id, false, 0.5));
        }
        bool focused = frame.FocusSlot >= 0;
        return new RaptorGraphDto(layout, colorBy, stateKey, focused ? focus : null, focused ? Crumbs(m, frame.FocusSlot) : [], nodes, edges, Legend(m, colorBy));
    }

    /// <summary>
    /// What is laid out and in which box: every node, or the focus node's primary subtree stretched to the unit width with its row on
    /// top; the row height and ring width for that many rows; the leaves the node-link sizes are shares of.
    /// </summary>
    private sealed record Frame(int FocusSlot, HashSet<int> Included, double Bx0, double Span, int RowBase, double RowH, double Ring, int TotalLeaves)
    {
        public static Frame Of(GraphModel m, int? focus)
        {
            var L = m.Layout;
            int focusSlot = focus is { } f && m.TryGetSlot(f, out var fs) ? fs : -1;
            bool focused = focusSlot >= 0;
            IEnumerable<int> slots = focused ? Enumerable.Range(0, m.Nodes.Length).Where(s => m.IsUnder(s, focusSlot)) : Enumerable.Range(0, m.Nodes.Length);
            double bx0 = focused ? L.X0[focusSlot] : 0, bx1 = focused ? L.X1[focusSlot] : 1;
            int rowBase = focused ? L.Row[focusSlot] : 0;
            int rows = Math.Max(1, L.Rows - rowBase);
            int totalLeaves = focused ? m.Nodes[focusSlot].Leaves : Math.Max(1, L.TotalLeaves);
            return new Frame(focusSlot, slots.ToHashSet(), bx0, Math.Max(1e-12, bx1 - bx0), rowBase, 1.0 / rows, (0.5 - Hole) / rows, totalLeaves);
        }
    }

    /// <summary>One node's geometry in the chosen layout (the unused fields stay null).</summary>
    private readonly record struct Shape(double X, double Y, double? W = null, double? H = null, double? A0 = null, double? A1 = null, double? R0 = null,
        double? R1 = null, double? R = null);

    /// <summary>Places and colours the frame's nodes: root cluster ranks (TopLevel is ordered largest first) and each layer's biggest node.</summary>
    private sealed class Placer
    {
        private readonly GraphModel _m;
        private readonly Frame _f;
        private readonly string _layout, _colorBy;
        private readonly Dictionary<int, int> _clusterRank = new();
        private readonly Dictionary<int, int> _maxTokens = new();

        public Placer(GraphModel m, Frame f, string layout, string colorBy)
        {
            (_m, _f, _layout, _colorBy) = (m, f, layout, colorBy);
            for (int i = 0; i < m.TopLevel.Length; i++) _clusterRank[m.TopLevel[i]] = i;
            foreach (var s in f.Included) _maxTokens[m.Nodes[s].Layer] = Math.Max(_maxTokens.GetValueOrDefault(m.Nodes[s].Layer), m.Nodes[s].Tokens);
        }

        public GraphNodeDto Node(int s)
        {
            var g = _m.Nodes[s];
            var p = Place(s, g);
            int cluster = _clusterRank.GetValueOrDefault(g.Root, _m.TopLevel.Length);
            int mt = Math.Max(1, _maxTokens.GetValueOrDefault(g.Layer));
            double opacity = Math.Round(0.45 + 0.55 * Math.Sqrt(Math.Clamp((double)g.Tokens / mt, 0, 1)), 3);
            int? parent = g.Parent >= 0 && _f.Included.Contains(g.Parent) ? _m.Nodes[g.Parent].Index : null;
            return new GraphNodeDto(g.Index, parent, g.Layer, g.IsLeaf, R6(p.X), R6(p.Y), R6(p.W), R6(p.H), R6(p.A0), R6(p.A1), R6(p.R0), R6(p.R1), R6(p.R),
                Label(g), g.Terms, g.Category.ToString(), g.Newest > 0 ? g.Newest : null, Math.Round(Math.Max(0, g.TimeRank), 4), cluster, g.LeafCount, g.Tokens,
                Color(g, cluster), opacity);
        }

        private Shape Place(int s, GraphNode g)
        {
            var L = _m.Layout;
            double x0 = (L.X0[s] - _f.Bx0) / _f.Span, x1 = (L.X1[s] - _f.Bx0) / _f.Span;
            int row = L.Row[s] - _f.RowBase;
            double cx = (x0 + x1) / 2, cy = (row + 0.5) * _f.RowH;
            return _layout switch
            {
                "radial" => Radial(x0, x1, row),
                "nodelink" => new Shape(cx, cy, R: NodeRadius(g)),
                _ => new Shape(cx, cy, W: x1 - x0, H: _f.RowH),
            };
        }

        /// <summary>Radial rings: a0,a1 radians from 12 o'clock clockwise, r0,r1 ∈ [0,0.5]; x,y = the ring segment's centroid around (0.5,0.5).</summary>
        private Shape Radial(double x0, double x1, int row)
        {
            double a0 = x0 * Math.PI * 2, a1 = x1 * Math.PI * 2;
            double r0 = Hole + row * _f.Ring, r1 = r0 + _f.Ring;
            double am = (a0 + a1) / 2, rm = (r0 + r1) / 2;
            return new Shape(0.5 + rm * Math.Sin(am), 0.5 - rm * Math.Cos(am), A0: a0, A1: a1, R0: r0, R1: r1);
        }

        private double NodeRadius(GraphNode g)
        {
            double share = (double)g.Leaves / _f.TotalLeaves;
            return g.IsLeaf ? Math.Clamp(0.3 / _f.TotalLeaves, 0.0015, 0.006) : Math.Clamp(0.004 + 0.03 * Math.Sqrt(share), 0.005, 0.04);
        }

        private string Color(GraphNode g, int cluster) => _colorBy switch
        {
            "time" => g.TimeRank < 0 ? GraphPalette.Other : GraphPalette.Mix(GraphPalette.TimeOld, GraphPalette.TimeNew, g.TimeRank),
            "cluster" => cluster < GraphPalette.ClusterSlots ? GraphPalette.Categorical[cluster] : GraphPalette.Other,
            _ => GraphPalette.Categorical[(int)g.Category],
        };
    }

    /// <summary>The focus node's path to the top: a leaf by index, a summary by its first term (or index).</summary>
    private static List<GraphCrumb> Crumbs(GraphModel m, int focusSlot)
    {
        var crumbs = new List<GraphCrumb>();
        foreach (var s in m.PathToTop(focusSlot))
        {
            var g = m.Nodes[s];
            crumbs.Add(new GraphCrumb(g.Index, g.Layer, g.IsLeaf ? $"leaf #{g.Index}" : g.Terms.Length > 0 ? $"L{g.Layer} · {g.Terms[0]}" : $"L{g.Layer} #{g.Index}"));
        }
        return crumbs;
    }

    public static string Label(GraphNode g) =>
        g.IsLeaf ? g.Title : g.Terms.Length > 0 ? string.Join(" · ", g.Terms) : $"L{g.Layer} #{g.Index}";

    private static IReadOnlyList<GraphLegendDto> Legend(GraphModel m, string colorBy)
    {
        switch (colorBy)
        {
            case "time":
                return [new("old", "older", GraphPalette.TimeOld), new("new", "newer", GraphPalette.TimeNew), new("unknown", "unknown time", GraphPalette.Other)];
            case "cluster":
            {
                var list = new List<GraphLegendDto>();
                for (int i = 0; i < Math.Min(GraphPalette.ClusterSlots, m.TopLevel.Length); i++)
                {
                    var g = m.Nodes[m.TopLevel[i]];
                    list.Add(new($"cluster{i}", g.Terms.Length > 0 ? g.Terms[0] : $"#{g.Index}", GraphPalette.Categorical[i]));
                }
                if (m.TopLevel.Length > GraphPalette.ClusterSlots) list.Add(new("other", "other clusters", GraphPalette.Other));
                return list;
            }
            default:
                return Enum.GetValues<MemoryCategory>().Select(c => new GraphLegendDto(c.ToString(), c.ToString().ToLowerInvariant(), GraphPalette.Categorical[(int)c])).ToList();
        }
    }

    private static double R6(double v) => Math.Round(v, 6);
    private static double? R6(double? v) => v is { } x ? Math.Round(x, 6) : null;
}
