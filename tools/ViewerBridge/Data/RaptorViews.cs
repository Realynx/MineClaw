using MineClaw.Raptor.Model;
using MineClaw.Raptor.Storage;
using MineClaw.ViewerBridge.Graph;

namespace MineClaw.ViewerBridge.Data;

public sealed record RaptorTreeNodeDto(int Index, int Layer, bool IsLeaf, string Title, string KindLabel, int Tokens, int ChildCount, int LeavesBelow, bool Dirty,
    IReadOnlyList<int> Parents, IReadOnlyList<int>? Children, string? DocRef);
/// <summary>RaptorTreeMeta on the wire: <c>build_id</c> is an i64 beyond JS's safe range, so it goes as its exact decimal string.</summary>
public sealed record RaptorMetaDto(int NumLayers, int NextIndex, int Dimensions, string EmbeddingModel, string BuildId, DateTimeOffset BuiltAt, int LeavesAtBuild,
    int InsertedSinceBuild, int RemovedSinceBuild, double BaselineLeafParentSimilarity, double InsertedSimilaritySum, int InsertedSimilarityCount, long InsertSeq,
    double BaselineLeafParentSimilarityStd, double InsertTemperature)
{
    public static RaptorMetaDto? From(RaptorTreeMeta? m) => m == null ? null : new(m.NumLayers, m.NextIndex, m.Dimensions, m.EmbeddingModel,
        m.BuildId.ToString(System.Globalization.CultureInfo.InvariantCulture), m.BuiltAt, m.LeavesAtBuild, m.InsertedSinceBuild, m.RemovedSinceBuild,
        m.BaselineLeafParentSimilarity, m.InsertedSimilaritySum, m.InsertedSimilarityCount, m.InsertSeq, m.BaselineLeafParentSimilarityStd, m.InsertTemperature);
}

public sealed record RaptorNodeRef(int Index, int Layer, string Label, int Tokens);
public sealed record RaptorDocInfo(long Id, string Ref, string Kind, string? Tag, string? Title, string? Source, long? Updated, int? Dimension);
public sealed record RaptorNodeDetail(RaptorTreeNodeDto Node, string Title, string Text, bool Pinned, bool Placeholder, RaptorDocInfo? Doc,
    IReadOnlyList<GraphCrumb> Breadcrumb, IReadOnlyList<RaptorNodeRef> Children, IReadOnlyList<RaptorNodeRef> Parents);
public sealed record RaptorLeaf(int Index, string DocRef, string Kind, string Title, int Tokens);
public sealed record RaptorLeavesResponse(int Node, IReadOnlyList<RaptorLeaf> Leaves, int TokenSum);

/// <summary>RAPTOR node detail and leaves (explorer-spec §4, ported from the explorer's RaptorTreeViewModel) for the Memory activity detail panel.</summary>
public static class RaptorViews
{
    public static RaptorNodeDetail? Node(MemoryBank bank, int index)
    {
        if (!bank.HasTree) return null;
        var g = bank.Graph();
        return bank.ReadTree(t =>
        {
            if (!t.TryGet(index, out var n)) return null;
            var doc = bank.DocOf(index);
            RaptorNodeRef Ref(RaptorNode c) => new(c.Index, c.Layer, c.IsLeaf ? bank.DocOf(c.Index)?.Kind ?? "leaf" : $"L{c.Layer}", c.TokenCount);
            var crumbs = Path(t, g, index).Select(p => t.TryGet(p, out var pn) ? new GraphCrumb(p, pn.Layer, CrumbLabel(bank, g, pn)) : new GraphCrumb(p, 0, $"#{p}")).ToList();
            return new RaptorNodeDetail(Row(bank, g, t, n, SortedChildren(t, n)),
                n.IsLeaf ? doc?.Title ?? $"Leaf #{index}" : $"Layer {n.Layer} summary #{index}", n.Text, n.Pinned, n.SummaryPending,
                doc == null ? null : new RaptorDocInfo(doc.Id, doc.Ref, doc.Kind, doc.Tag, doc.Title, doc.Source, Ms(doc.Updated), doc.Dimension),
                crumbs,
                n.Children.Where(t.AllNodes.ContainsKey).Select(c => Ref(t[c])).OrderByDescending(c => c.Tokens).ToList(),
                n.Parents.Where(t.AllNodes.ContainsKey).Select(p => Ref(t[p])).ToList());
        }, null);
    }

    public static RaptorLeavesResponse? Leaves(MemoryBank bank, int index)
    {
        if (!bank.HasTree) return null;
        return bank.ReadTree(t =>
        {
            if (!t.TryGet(index, out _)) return null;
            var list = new List<RaptorLeaf>();
            foreach (var i in LeafIndexes(t, index))
            {
                var x = t[i];
                var d = bank.DocOf(i);
                list.Add(new RaptorLeaf(i, d?.Ref ?? x.Key ?? $"leaf:{i}", d?.Kind ?? x.Kind ?? "leaf", d?.Title ?? One(x.Text, 80), x.TokenCount));
            }
            list.Sort((a, b) => a.Index.CompareTo(b.Index));
            return new RaptorLeavesResponse(index, list, list.Sum(l => l.Tokens));
        }, null);
    }

    /// <summary>Every leaf below a node, deduplicated across soft-cluster parents.</summary>
    public static List<int> LeafIndexes(RaptorTree t, int index)
    {
        var leaves = new List<int>();
        var seen = new HashSet<int>();
        var stack = new Stack<int>();
        stack.Push(index);
        while (stack.Count > 0)
        {
            var i = stack.Pop();
            if (!seen.Add(i) || !t.TryGet(i, out var x)) continue;
            if (x.IsLeaf) leaves.Add(i);
            else foreach (var c in x.Children) stack.Push(c);
        }
        return leaves;
    }

    /// <summary>Root → node along the graph's primary parents (so tree, graph and heat agree), else the bank's path.</summary>
    public static IReadOnlyList<int> Path(RaptorTree t, GraphModel? g, int index) =>
        g != null && g.TryGetSlot(index, out var s) ? g.PathToTop(s).Select(x => g.Nodes[x].Index).ToList() : MemoryBank.PathToRoot(t, index);

    public static string CrumbLabel(MemoryBank bank, GraphModel? g, RaptorNode n)
    {
        if (n.IsLeaf) return bank.DocOf(n.Index) is { } d ? One(d.Title, 48) : $"leaf #{n.Index}";
        if (g != null && g.TryGetSlot(n.Index, out var s) && g.Nodes[s].Terms.Length > 0) return $"L{n.Layer} · {g.Nodes[s].Terms[0]}";
        return $"L{n.Layer} #{n.Index}";
    }

    private static List<int> SortedChildren(RaptorTree t, RaptorNode n) =>
        n.Children.Where(t.AllNodes.ContainsKey).Select(c => t[c]).OrderByDescending(c => c.Children.Count).ThenBy(c => c.Index).Select(c => c.Index).ToList();

    private static RaptorTreeNodeDto Row(MemoryBank bank, GraphModel? g, RaptorTree t, RaptorNode n, List<int>? kids)
    {
        var doc = bank.DocOf(n.Index);
        var one = n.Text.ReplaceLineEndings(" ");
        string title = n.IsLeaf ? (doc != null ? $"{doc.Kind} · {Trim(doc.Title, 70)}" : $"leaf #{n.Index}") : Trim(one, 90);
        // Primary parent first (the graph's partition parent), then the other soft-cluster parents.
        var parents = n.Parents.ToList();
        if (g != null && g.TryGetSlot(n.Index, out var s) && g.Nodes[s].Parent >= 0)
        {
            int primary = g.Nodes[g.Nodes[s].Parent].Index;
            if (parents.Remove(primary)) parents.Insert(0, primary);
        }
        return new RaptorTreeNodeDto(n.Index, n.Layer, n.IsLeaf, title, n.IsLeaf ? doc?.Kind ?? n.Kind ?? "leaf" : $"L{n.Layer}", n.TokenCount, n.Children.Count,
            n.IsLeaf ? 1 : LeafIndexes(t, n.Index).Count, n.Dirty || n.SummaryPending, parents, kids, doc?.Ref);
    }

    private static long? Ms(DateTimeOffset t) => t.Year >= 1971 ? t.ToUnixTimeMilliseconds() : t.ToUnixTimeMilliseconds();
    private static string Trim(string s, int n) => s.Length > n ? s[..n] + "…" : s;
    private static string One(string s, int max)
    {
        var x = s.ReplaceLineEndings(" ");
        return x.Length > max ? x[..max] + "…" : x;
    }
}
