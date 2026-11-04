using MineClaw.ViewerBridge.Data;
using MineClaw.Raptor.Model;

namespace MineClaw.ViewerBridge.Graph;

/// <summary>What a memory is about, for the graph's "colour by kind" dimension. Summaries take their leaves' majority.</summary>
public enum MemoryCategory { Episode, Activity, Fact, Recipe, Chat, World, Self }

/// <summary>Plain input row for <see cref="GraphModel.FromSpecs"/> (built from the RAPTOR tree, or by hand in tests).</summary>
public sealed record NodeSpec(int Index, int Layer, IReadOnlyList<int> Children, string Text, int Tokens = 0, bool Dirty = false,
    string? Kind = null, string? Tag = null, string? Source = null, string? Title = null, DateTimeOffset? Updated = null);

/// <summary>One node of the graph, addressed by its slot (position in <see cref="GraphModel.Nodes"/>).</summary>
public sealed class GraphNode
{
    public int Index { get; init; }
    public int Layer { get; init; }
    public int Slot { get; init; }
    /// <summary>Primary parent slot (the most specific of its clusters, see <see cref="GraphModel.FromSpecs"/>), -1 for a top-level node.</summary>
    public int Parent { get; internal set; } = -1;
    /// <summary>Children whose primary parent is this node, in layout order (most leaves first).</summary>
    public int[] Children { get; internal set; } = [];
    /// <summary>Soft-clustering parents other than the primary one (slots).</summary>
    public int[] OtherParents { get; internal set; } = [];
    /// <summary>Every child, soft-clustered ones included.</summary>
    public int ChildCount { get; init; }
    /// <summary>Leaves under this node in the partition tree (a leaf counts 1, an empty summary counts 1).</summary>
    public int Leaves { get; internal set; }
    /// <summary>Real leaves under this node in the partition tree (an empty summary has 0); what labels and tooltips show.</summary>
    public int LeafCount { get; internal set; }
    public int Tokens { get; init; }
    public bool Dirty { get; init; }
    public MemoryCategory Category { get; internal set; }
    /// <summary>Unix ms of the newest leaf below (or the node's own update time), 0 when unknown.</summary>
    public long Newest { get; internal set; }
    /// <summary>Quantile of <see cref="Newest"/> among all leaf times (0 oldest … 1 newest), -1 when unknown: the time colour scale,
    /// so a seed dated months back doesn't flatten two busy days into one colour.</summary>
    public double TimeRank { get; internal set; } = -1;
    /// <summary>Slot of the top-level ancestor.</summary>
    public int Root { get; internal set; }
    public string Title { get; init; } = "";
    /// <summary>The node's text on one line, at most ~280 characters.</summary>
    public string Preview { get; init; } = "";
    public string Text { get; init; } = "";
    /// <summary>TF-IDF topic terms (2–4), filled by <see cref="TopicLabeler"/>.</summary>
    public string[] Terms { get; internal set; } = [];
    public bool IsLeaf => Layer == 0;
}

/// <summary>
/// The RAPTOR tree as a partition tree for drawing: every node hangs under its primary parent (soft-clustering second parents are
/// kept as <see cref="GraphNode.OtherParents"/>), weights are leaf counts, plus the per-node colour data (category, newest leaf,
/// root cluster) and the precomputed <see cref="PartitionLayout"/>.
/// </summary>
public sealed class GraphModel
{
    public required GraphNode[] Nodes { get; init; }
    public required Dictionary<int, int> Slot { get; init; }
    /// <summary>Highest summary layer (the leaves are layer 0).</summary>
    public required int NumLayers { get; init; }
    /// <summary>Top-level slots in layout order.</summary>
    public required int[] TopLevel { get; init; }
    public required int[] LayerCounts { get; init; }
    public long MinTime { get; init; }
    public long MaxTime { get; init; }
    public int TotalLeaves => Layout.TotalLeaves;
    public PartitionLayout Layout { get; private set; } = null!;

    public bool TryGetSlot(int index, out int slot) => Slot.TryGetValue(index, out slot);

    /// <summary>Slots from the top-level ancestor down to <paramref name="slot"/> (primary parents).</summary>
    public List<int> PathToTop(int slot)
    {
        var path = new List<int>();
        for (int s = slot, guard = 0; s >= 0 && guard < 64; s = Nodes[s].Parent, guard++) path.Add(s);
        path.Reverse();
        return path;
    }

    /// <summary>Is <paramref name="slot"/> inside the primary subtree of <paramref name="ancestor"/> (or equal to it)?</summary>
    public bool IsUnder(int slot, int ancestor)
    {
        for (int s = slot, guard = 0; s >= 0 && guard < 64; s = Nodes[s].Parent, guard++)
            if (s == ancestor) return true;
        return false;
    }

    public static MemoryCategory Categorize(string? kind, string? tag, string? source) => (kind?.ToLowerInvariant(), source?.ToLowerInvariant()) switch
    {
        ("episode", "activity") => MemoryCategory.Activity,
        ("episode", "journal") => MemoryCategory.Self,
        ("episode", _) => MemoryCategory.Episode,
        ("gamedata", _) or ("recipe", _) or ("skill", _) or ("routine", _) => MemoryCategory.Recipe,
        ("player", _) or ("chat", _) or ("claim", _) => MemoryCategory.Chat,
        ("structure", _) or ("world", _) or ("waypoint", _) => MemoryCategory.World,
        ("selfnote", _) or ("self", _) or ("identity", _) or ("journal", _) => MemoryCategory.Self,
        _ => MemoryCategory.Fact, // knowledge, notes, corrections, anything else
    };

    /// <summary>Builds the graph from the live tree (call inside <see cref="MemoryBank.ReadTree{T}"/>).</summary>
    public static GraphModel Build(RaptorTree t, Func<int, NodeDoc?> docOf)
    {
        var specs = new List<NodeSpec>(t.Count);
        foreach (var n in t.AllNodes.Values)
        {
            var d = docOf(n.Index);
            specs.Add(new NodeSpec(n.Index, n.Layer, n.Children.ToArray(), n.Text, n.TokenCount, n.Dirty || n.SummaryPending,
                d?.Kind ?? n.Kind, d?.Tag, d?.Source ?? n.Source, d?.Title, d?.Updated));
        }
        return FromSpecs(specs, t.NumLayers);
    }

    public static GraphModel FromSpecs(IReadOnlyList<NodeSpec> specs, int numLayers)
    {
        var ordered = specs.OrderByDescending(s => s.Layer).ThenBy(s => s.Index).ToArray();
        var slot = new Dictionary<int, int>(ordered.Length);
        for (int k = 0; k < ordered.Length; k++) slot[ordered[k].Index] = k;
        var parents = ParentsOf(ordered, slot);
        var nodes = new GraphNode[ordered.Length];
        for (int k = 0; k < nodes.Length; k++) nodes[k] = NodeOf(ordered[k], k);
        AssignPrimaryParents(ordered, slot, nodes, parents);
        var kids = KidsOf(nodes);
        RollUp(nodes, kids);
        foreach (var g in nodes)
            g.Children = kids[g.Slot] is { } ks ? ks.OrderByDescending(c => nodes[c].Leaves).ThenBy(c => nodes[c].Index).ToArray() : [];
        var top = nodes.Where(g => g.Parent < 0).OrderByDescending(g => g.Layer).ThenByDescending(g => g.Leaves).ThenBy(g => g.Index)
            .Select(g => g.Slot).ToArray();
        // Roots (top-down: slot ascending is layer descending, so parents are assigned first).
        for (int k = 0; k < nodes.Length; k++) nodes[k].Root = nodes[k].Parent < 0 ? k : nodes[nodes[k].Parent].Root;

        int layers = Math.Max(numLayers, nodes.Length > 0 ? nodes.Max(g => g.Layer) : 0);
        var counts = new int[layers + 1];
        foreach (var g in nodes) counts[g.Layer]++;
        var times = nodes.Where(g => g.Newest > 0).Select(g => g.Newest).ToList();
        RankTimes(nodes);
        var m = new GraphModel
        {
            Nodes = nodes, Slot = slot, NumLayers = layers, TopLevel = top, LayerCounts = counts,
            MinTime = times.Count > 0 ? times.Min() : 0, MaxTime = times.Count > 0 ? times.Max() : 0,
        };
        m.Layout = PartitionLayout.Compute(m);
        return m;
    }

    /// <summary>Every slot's clusters (soft clustering: a leaf may have several), by slot; null = none.</summary>
    private static List<int>?[] ParentsOf(NodeSpec[] ordered, Dictionary<int, int> slot)
    {
        var parents = new List<int>?[ordered.Length];
        for (int k = 0; k < ordered.Length; k++)
            foreach (var c in ordered[k].Children)
                if (slot.TryGetValue(c, out var cs) && cs != k) (parents[cs] ??= []).Add(k);
        return parents;
    }

    private static GraphNode NodeOf(NodeSpec s, int k)
    {
        var one = s.Text.ReplaceLineEndings(" ").Trim();
        return new GraphNode
        {
            Index = s.Index, Layer = s.Layer, Slot = k, ChildCount = s.Children.Count, Tokens = s.Tokens, Dirty = s.Dirty, Text = s.Text,
            Preview = one.Length > 280 ? one[..280] + "…" : one,
            Title = TitleOf(s, one),
            Category = Categorize(s.Kind, s.Tag, s.Source),
            // Seeded docs carry epoch-zero stamps: anything before 2000 counts as unknown.
            Newest = s.Updated is { } u && u.Year >= 2000 ? u.ToUnixTimeMilliseconds() : 0,
        };
    }

    private static string TitleOf(NodeSpec s, string oneLine)
    {
        if (s.Layer != 0) return $"Layer {s.Layer} summary #{s.Index}";
        return string.IsNullOrWhiteSpace(s.Title) ? Short(oneLine, 80) : Short(s.Title!, 80);
    }

    /// <summary>
    /// Primary parent (soft clustering puts most leaves under several clusters): the most specific cluster, i.e. the one with
    /// the fewest children (ties: lowest index). Then a repair pass hands every cluster that would come out empty one child
    /// from a cluster that has more than one, so every summary keeps a band. Last, each node's other clusters.
    /// </summary>
    private static void AssignPrimaryParents(NodeSpec[] ordered, Dictionary<int, int> slot, GraphNode[] nodes, List<int>?[] parents)
    {
        var assigned = new int[nodes.Length];
        for (int k = 0; k < nodes.Length; k++)
        {
            if (parents[k] is not { } ps) continue;
            int best = MostSpecificParent(nodes, k, ps);
            nodes[k].Parent = best;
            if (best >= 0) assigned[best]++;
        }
        for (int p = 0; p < nodes.Length; p++)
            if (assigned[p] == 0 && ordered[p].Children.Count > 0) GiveEmptyClusterAChild(ordered[p], p, slot, nodes, assigned);
        for (int k = 0; k < nodes.Length; k++)
            if (parents[k] is { } ps) nodes[k].OtherParents = ps.Where(p => p != nodes[k].Parent).Distinct().OrderBy(p => nodes[p].Index).ToArray();
    }

    private static int MostSpecificParent(GraphNode[] nodes, int k, List<int> ps)
    {
        int best = -1;
        foreach (var p in ps)
        {
            if (nodes[p].Layer <= nodes[k].Layer) continue;
            if (best < 0 || IsMoreSpecific(nodes[p], nodes[best])) best = p;
        }
        return best;
    }

    private static bool IsMoreSpecific(GraphNode a, GraphNode b) => a.ChildCount < b.ChildCount || (a.ChildCount == b.ChildCount && a.Index < b.Index);

    /// <summary>An empty cluster takes its first lower child whose primary parent has another child to spare (or none at all).</summary>
    private static void GiveEmptyClusterAChild(NodeSpec cluster, int p, Dictionary<int, int> slot, GraphNode[] nodes, int[] assigned)
    {
        foreach (var ci in cluster.Children.OrderBy(i => i))
        {
            if (!slot.TryGetValue(ci, out var c) || nodes[c].Layer >= nodes[p].Layer) continue;
            int owner = nodes[c].Parent;
            if (owner >= 0 && assigned[owner] < 2) continue;
            if (owner >= 0) assigned[owner]--;
            nodes[c].Parent = p;
            assigned[p]++;
            return;
        }
    }

    /// <summary>Each slot's children by primary parent (null = none), in slot order.</summary>
    private static List<int>?[] KidsOf(GraphNode[] nodes)
    {
        var kids = new List<int>?[nodes.Length];
        for (int k = 0; k < nodes.Length; k++) if (nodes[k].Parent >= 0) (kids[nodes[k].Parent] ??= []).Add(k);
        return kids;
    }

    /// <summary>Bottom-up (layer ascending = slot descending): leaf weights, category votes and newest time.</summary>
    private static void RollUp(GraphNode[] nodes, List<int>?[] kids)
    {
        var votes = new int[nodes.Length, 7];
        for (int k = nodes.Length - 1; k >= 0; k--)
        {
            var g = nodes[k];
            if (g.IsLeaf || kids[k] == null)
            {
                g.Leaves = 1;
                g.LeafCount = g.IsLeaf ? 1 : 0;
                if (g.IsLeaf) votes[k, (int)g.Category] = 1;
            }
            else RollUpCluster(nodes, g, kids[k]!, votes);
        }
    }

    private static void RollUpCluster(GraphNode[] nodes, GraphNode g, List<int> kids, int[,] votes)
    {
        int k = g.Slot, sum = 0, real = 0;
        long newest = g.Newest;
        foreach (var c in kids)
        {
            sum += nodes[c].Leaves;
            real += nodes[c].LeafCount;
            newest = Math.Max(newest, nodes[c].Newest);
            for (int v = 0; v < 7; v++) votes[k, v] += votes[c, v];
        }
        g.Leaves = Math.Max(1, sum);
        g.LeafCount = real;
        g.Newest = newest;
        int best = 0;
        for (int v = 1; v < 7; v++) if (votes[k, v] > votes[k, best]) best = v;
        if (votes[k, best] > 0) g.Category = (MemoryCategory)best;
    }

    /// <summary>Each dated node's rank among the leaves' times (0 oldest .. 1 newest; the last of equal times).</summary>
    private static void RankTimes(GraphNode[] nodes)
    {
        var leafTimes = nodes.Where(g => g.IsLeaf && g.Newest > 0).Select(g => g.Newest).Order().ToArray();
        if (leafTimes.Length == 0) return;
        foreach (var g in nodes)
        {
            if (g.Newest <= 0) continue;
            int i = Array.BinarySearch(leafTimes, g.Newest);
            if (i < 0) i = ~i;
            else while (i + 1 < leafTimes.Length && leafTimes[i + 1] == g.Newest) i++;
            g.TimeRank = leafTimes.Length == 1 ? 1 : Math.Clamp((double)i / (leafTimes.Length - 1), 0, 1);
        }
    }

    private static string Short(string s, int max)
    {
        var t = s.ReplaceLineEndings(" ").Trim();
        return t.Length > max ? t[..max] + "…" : t;
    }
}
