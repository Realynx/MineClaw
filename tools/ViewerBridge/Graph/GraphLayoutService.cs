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
        var L = m.Layout;
        int focusSlot = focus is { } f && m.TryGetSlot(f, out var fs) ? fs : -1;
        IEnumerable<int> slots = focusSlot >= 0 ? Enumerable.Range(0, m.Nodes.Length).Where(s => m.IsUnder(s, focusSlot)) : Enumerable.Range(0, m.Nodes.Length);
        var included = slots.ToHashSet();
        double bx0 = focusSlot >= 0 ? L.X0[focusSlot] : 0, bx1 = focusSlot >= 0 ? L.X1[focusSlot] : 1;
        double span = Math.Max(1e-12, bx1 - bx0);
        int rowBase = focusSlot >= 0 ? L.Row[focusSlot] : 0;
        int rows = Math.Max(1, L.Rows - rowBase);
        double rowH = 1.0 / rows;
        double ring = (0.5 - Hole) / rows;
        int totalLeaves = focusSlot >= 0 ? m.Nodes[focusSlot].Leaves : Math.Max(1, L.TotalLeaves);

        // Root cluster rank: TopLevel is ordered largest first.
        var clusterRank = new Dictionary<int, int>();
        for (int i = 0; i < m.TopLevel.Length; i++) clusterRank[m.TopLevel[i]] = i;
        var maxTokens = new Dictionary<int, int>();
        foreach (var s in included) maxTokens[m.Nodes[s].Layer] = Math.Max(maxTokens.GetValueOrDefault(m.Nodes[s].Layer), m.Nodes[s].Tokens);

        var nodes = new List<GraphNodeDto>(included.Count);
        var edges = new List<GraphEdgeDto>();
        foreach (var s in included.Order())
        {
            var g = m.Nodes[s];
            double x0 = (L.X0[s] - bx0) / span, x1 = (L.X1[s] - bx0) / span;
            int row = L.Row[s] - rowBase;
            double cx = (x0 + x1) / 2, cy = (row + 0.5) * rowH;
            double? w = null, h = null, a0 = null, a1 = null, r0 = null, r1 = null, r = null;
            double x = cx, y = cy;
            switch (layout)
            {
                case "radial":
                {
                    a0 = x0 * Math.PI * 2;
                    a1 = x1 * Math.PI * 2;
                    r0 = Hole + row * ring;
                    r1 = r0 + ring;
                    double am = (a0.Value + a1.Value) / 2, rm = (r0.Value + r1.Value) / 2;
                    x = 0.5 + rm * Math.Sin(am);
                    y = 0.5 - rm * Math.Cos(am);
                    break;
                }
                case "nodelink":
                {
                    double share = (double)g.Leaves / totalLeaves;
                    r = g.IsLeaf ? Math.Clamp(0.3 / totalLeaves, 0.0015, 0.006) : Math.Clamp(0.004 + 0.03 * Math.Sqrt(share), 0.005, 0.04);
                    break;
                }
                default:
                    w = x1 - x0;
                    h = rowH;
                    break;
            }
            int cluster = clusterRank.GetValueOrDefault(g.Root, m.TopLevel.Length);
            string color = colorBy switch
            {
                "time" => g.TimeRank < 0 ? GraphPalette.Other : GraphPalette.Mix(GraphPalette.TimeOld, GraphPalette.TimeNew, g.TimeRank),
                "cluster" => cluster < GraphPalette.ClusterSlots ? GraphPalette.Categorical[cluster] : GraphPalette.Other,
                _ => GraphPalette.Categorical[(int)g.Category],
            };
            int mt = Math.Max(1, maxTokens.GetValueOrDefault(g.Layer));
            double opacity = Math.Round(0.45 + 0.55 * Math.Sqrt(Math.Clamp((double)g.Tokens / mt, 0, 1)), 3);
            int? parent = g.Parent >= 0 && included.Contains(g.Parent) ? m.Nodes[g.Parent].Index : null;
            nodes.Add(new GraphNodeDto(g.Index, parent, g.Layer, g.IsLeaf, R6(x), R6(y), R6(w), R6(h), R6(a0), R6(a1), R6(r0), R6(r1), R6(r),
                Label(g), g.Terms, g.Category.ToString(), g.Newest > 0 ? g.Newest : null, Math.Round(Math.Max(0, g.TimeRank), 4), cluster, g.LeafCount, g.Tokens,
                color, opacity));
            if (parent is { } p) edges.Add(new GraphEdgeDto(p, g.Index, true, 1));
            foreach (var op in g.OtherParents)
                if (included.Contains(op)) edges.Add(new GraphEdgeDto(m.Nodes[op].Index, g.Index, false, 0.5));
        }

        var crumbs = new List<GraphCrumb>();
        if (focusSlot >= 0)
            foreach (var s in m.PathToTop(focusSlot))
            {
                var g = m.Nodes[s];
                crumbs.Add(new GraphCrumb(g.Index, g.Layer, g.IsLeaf ? $"leaf #{g.Index}" : g.Terms.Length > 0 ? $"L{g.Layer} · {g.Terms[0]}" : $"L{g.Layer} #{g.Index}"));
            }
        return new RaptorGraphDto(layout, colorBy, stateKey, focusSlot >= 0 ? focus : null, crumbs, nodes, edges, Legend(m, colorBy));
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
