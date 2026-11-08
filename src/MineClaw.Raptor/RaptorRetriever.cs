using System.Diagnostics;
using MineClaw.Raptor.Model;
using MineClaw.Raptor.Numerics;

namespace MineClaw.Raptor;

/// <summary>One retrieval request. Unset fields fall back to <see cref="RaptorOptions"/>.</summary>
public sealed record RaptorQuery
{
    public string Text { get; init; } = "";
    /// <summary>Precomputed query embedding (skips the embedder call).</summary>
    public float[]? Vector { get; init; }
    /// <summary>true = collapsed tree, false = tree traversal; null = options.DefaultCollapsed.</summary>
    public bool? Collapsed { get; init; }
    /// <summary>Traversal: nodes per layer (top_k). Collapsed: optional node-count cap.</summary>
    public int? TopK { get; init; }
    /// <summary>Collapsed: token budget (paper 2000). Traversal: optional cap (the reference has none).</summary>
    public int? MaxTokens { get; init; }
    public SelectionMode? Selection { get; init; }
    public double? Threshold { get; init; }
    /// <summary>Traversal start layer (reference default: tree.num_layers, the root layer).</summary>
    public int? StartLayer { get; init; }
    /// <summary>Traversal depth (reference default: num_layers + 1).</summary>
    public int? NumLayers { get; init; }
    public RaptorFilter? Filter { get; init; }
    /// <summary>Offer pinned leaves (that pass the filter) before anything else.</summary>
    public bool IncludePinned { get; init; }
}

public sealed record RetrievedNode(int Index, int Layer, double Score, string Text, int Tokens, string? Key, string? Kind, string? Source, bool Pinned, bool Leaf);

public sealed record RaptorResult(IReadOnlyList<RetrievedNode> Nodes, string Context, int Tokens, bool Collapsed, long EmbeddingTokens, TimeSpan Elapsed)
{
    public static readonly RaptorResult Empty = new([], "", 0, true, 0, TimeSpan.Zero);
}

/// <summary>
/// tree_retriever.py over a <see cref="RaptorTree"/>: <c>retrieve_information</c> (tree traversal) and
/// <c>retrieve_information_collapse_tree</c> (collapsed tree). Similarity is cosine (dot of normalized vectors);
/// ties break on the lower node index.
/// </summary>
public sealed class RaptorRetriever
{
    private readonly RaptorTree _tree;
    private readonly RaptorOptions _o;
    private float[] _matrix = [];
    private float[][]? _embeddingRows;
    private int[] _rowIndex = [];
    private int _dims;

    public RaptorRetriever(RaptorTree tree, RaptorOptions options)
    {
        _tree = tree;
        _o = options;
        Refresh();
    }

    /// <summary>Rebuilds the embedding rows used by collapsed search (call after the tree changed).</summary>
    public void Refresh()
    {
        var nodes = _tree.AllNodes.Values.OrderBy(n => n.Index).ToArray();
        int dims = nodes.Length > 0 ? nodes[0].Embedding.Length : 0;
        var embeddingRows = _o.ReuseImmutableEmbeddings ? new float[nodes.Length][] : null;
        var matrix = embeddingRows == null ? new float[nodes.Length * dims] : [];
        var rowIndex = new int[nodes.Length];
        for (int r = 0; r < nodes.Length; r++)
        {
            rowIndex[r] = nodes[r].Index;
            if (embeddingRows != null)
            {
                if (nodes[r].Embedding.Length != dims) throw new ArgumentException("Vector length mismatch.");
                embeddingRows[r] = nodes[r].Embedding;
            }
            else nodes[r].Embedding.AsSpan().CopyTo(matrix.AsSpan(r * dims, dims));
        }
        // RaptorIndex holds its write lock during refresh. Publish only a completed set of rows.
        _dims = dims;
        _matrix = matrix;
        _embeddingRows = embeddingRows;
        _rowIndex = rowIndex;
    }

    public RaptorResult Retrieve(float[] queryVector, RaptorQuery q)
    {
        var sw = Stopwatch.StartNew();
        if (_tree.Count == 0) return RaptorResult.Empty;
        var qv = VectorMath.Normalized(queryVector);
        if (qv.Length != _dims) throw new ArgumentException($"Query vector has {qv.Length} dims; tree has {_dims}.");
        bool collapsed = q.Collapsed ?? _o.DefaultCollapsed;
        var eligible = Eligibility(q.Filter);
        var selected = collapsed ? CollapsedTree(qv, q, eligible) : Traverse(qv, q, eligible);
        var nodes = selected.Select(s =>
        {
            var n = _tree[s.Index];
            return new RetrievedNode(n.Index, n.Layer, s.Score, n.Text, n.TokenCount, n.Key, n.Kind, n.Source, n.Pinned, n.IsLeaf);
        }).ToList();
        var context = RaptorTreeBuilder.GetText(selected.Select(s => _tree[s.Index]));
        return new RaptorResult(nodes, context, nodes.Sum(n => n.Tokens), collapsed, 0, sw.Elapsed);
    }

    /// <summary>
    /// Filter semantics: a leaf is eligible when it matches; a summary is eligible when at least one descendant leaf
    /// is. Null when no filter (everything eligible).
    /// </summary>
    private HashSet<int>? Eligibility(RaptorFilter? filter)
    {
        if (filter == null || filter.IsEmpty) return null;
        var ok = new HashSet<int>();
        var frontier = new List<int>();
        foreach (var leaf in _tree.Leaves)
            if (filter.Matches(leaf)) { ok.Add(leaf.Index); frontier.Add(leaf.Index); }
        while (frontier.Count > 0)
        {
            var next = new List<int>();
            foreach (var i in frontier)
                foreach (var p in _tree[i].Parents)
                    if (ok.Add(p)) next.Add(p);
            frontier = next;
        }
        return ok;
    }

    private readonly record struct Scored(int Index, double Score);

    private List<Scored> PinnedFirst(float[] qv, RaptorQuery q, HashSet<int>? eligible)
    {
        if (!q.IncludePinned) return [];
        return _tree.Leaves.Where(l => l.Pinned && (eligible == null || eligible.Contains(l.Index)))
            .Select(l => new Scored(l.Index, VectorMath.Dot(qv, l.Embedding)))
            .OrderByDescending(s => s.Score).ThenBy(s => s.Index).ToList();
    }

    /// <summary>
    /// retrieve_information_collapse_tree: score every node of every layer, walk them by descending similarity and
    /// add each while the running token total stays within max_tokens; stop at the first node that does not fit
    /// (the reference <c>break</c>; paper Algorithm 2) or when the optional top_k count is reached.
    /// </summary>
    private List<Scored> CollapsedTree(float[] qv, RaptorQuery q, HashSet<int>? eligible)
    {
        int maxTokens = q.MaxTokens ?? _o.CollapsedMaxTokens;
        int? topK = q.TopK ?? _o.CollapsedTopK;
        if (maxTokens < 1) throw new ArgumentException("max_tokens must be an integer and at least 1");
        var scores = ScoreRows(qv);
        var result = new List<Scored>();
        var taken = new HashSet<int>();
        int total = TakePinned(PinnedFirst(qv, q, eligible), maxTokens, result, taken);
        foreach (var r in RankedRows(scores, eligible))
        {
            if (topK is { } k && result.Count >= k) break;
            int idx = _rowIndex[r];
            if (taken.Contains(idx)) continue;
            int t = _tree[idx].TokenCount;
            if (total + t > maxTokens) break;
            result.Add(new Scored(idx, scores[r]));
            total += t;
        }
        return result;
    }

    /// <summary>Cosine of the query with every embedding row (in parallel blocks for big trees).</summary>
    private float[] ScoreRows(float[] qv)
    {
        int rows = _rowIndex.Length;
        var scores = new float[rows];
        if (rows * (long)_dims > 1_000_000)
            Parallel.For(0, (rows + 2047) / 2048, b =>
            {
                int end = Math.Min(rows, (b + 1) * 2048);
                for (int r = b * 2048; r < end; r++) scores[r] = VectorMath.Dot(qv, EmbeddingRow(r));
            });
        else
            for (int r = 0; r < rows; r++) scores[r] = VectorMath.Dot(qv, EmbeddingRow(r));
        return scores;
    }

    private ReadOnlySpan<float> EmbeddingRow(int row) => _embeddingRows == null
        ? _matrix.AsSpan(row * _dims, _dims)
        : _embeddingRows[row];

    /// <summary>Pinned leaves first, each one that still fits the budget (skipping, not stopping); returns the tokens used.</summary>
    private int TakePinned(List<Scored> pinned, int maxTokens, List<Scored> result, HashSet<int> taken)
    {
        int total = 0;
        foreach (var p in pinned)
        {
            int t = _tree[p.Index].TokenCount;
            if (total + t > maxTokens) continue;
            result.Add(p);
            taken.Add(p.Index);
            total += t;
        }
        return total;
    }

    /// <summary>Rows by descending score (ties: lower index). Partial heap first, full sort only if consumed.</summary>
    private IEnumerable<int> RankedRows(float[] scores, HashSet<int>? eligible)
    {
        const int First = 256;
        var top = TopRows(scores, eligible, First);
        foreach (var r in top) yield return r;
        if (top.Count < First) yield break;
        var seen = new HashSet<int>(top);
        var rest = Enumerable.Range(0, scores.Length).Where(r => RowEligible(r, eligible) && !seen.Contains(r)).ToArray();
        Array.Sort(rest, (a, b) => scores[a] != scores[b] ? scores[b].CompareTo(scores[a]) : a.CompareTo(b));
        foreach (var r in rest) yield return r;
    }

    private bool RowEligible(int r, HashSet<int>? eligible) => eligible == null || eligible.Contains(_rowIndex[r]);

    /// <summary>The best <paramref name="first"/> eligible rows, best first, through a bounded min-heap.</summary>
    private List<int> TopRows(float[] scores, HashSet<int>? eligible, int first)
    {
        var heap = new PriorityQueue<int, (float, int)>(Comparer<(float S, int R)>.Create((a, b) => a.S != b.S ? a.S.CompareTo(b.S) : b.R.CompareTo(a.R)));
        for (int r = 0; r < scores.Length; r++)
        {
            if (!RowEligible(r, eligible)) continue;
            if (heap.Count < first) heap.Enqueue(r, (scores[r], r));
            else
            {
                heap.TryPeek(out _, out var min);
                if (scores[r] > min.Item1 || (scores[r] == min.Item1 && r < min.Item2)) heap.EnqueueDequeue(r, (scores[r], r));
            }
        }
        var top = new List<int>(heap.Count);
        while (heap.Count > 0) top.Add(heap.Dequeue());
        top.Reverse();
        return top;
    }

    /// <summary>
    /// retrieve_information: start at layer_to_nodes[start_layer]; at each of num_layers layers select top_k nodes by
    /// cosine (or those over the threshold), keep them, and continue with the union of their children.
    /// </summary>
    private List<Scored> Traverse(float[] qv, RaptorQuery q, HashSet<int>? eligible)
    {
        var (startLayer, numLayers) = TraversalLayers(q);
        int topK = q.TopK ?? _o.TraversalTopK;
        var mode = q.Selection ?? _o.Selection;
        double threshold = q.Threshold ?? _o.SelectionThreshold;

        var selected = PinnedFirst(qv, q, eligible);
        var taken = new HashSet<int>(selected.Select(s => s.Index));
        int total = selected.Sum(s => _tree[s.Index].TokenCount);
        var nodeList = _tree.LayerIndices(startLayer).Order().Where(i => eligible == null || eligible.Contains(i)).ToList();
        for (int layer = 0; layer < numLayers && nodeList.Count > 0; layer++)
        {
            var scored = nodeList.Select(i => new Scored(i, VectorMath.Dot(qv, _tree[i].Embedding)))
                .OrderByDescending(s => s.Score).ThenBy(s => s.Index).ToList();
            var best = mode == SelectionMode.TopK ? scored.Take(topK).ToList() : scored.Where(s => s.Score >= threshold).ToList();
            total = TakeWithin(best, q.MaxTokens, selected, taken, total);
            if (layer != numLayers - 1) nodeList = ChildrenOf(best, eligible);
        }
        return selected;
    }

    /// <summary>Start layer and depth, defaulted and validated as tree_retriever.py does.</summary>
    private (int StartLayer, int NumLayers) TraversalLayers(RaptorQuery q)
    {
        int startLayer = q.StartLayer ?? _tree.NumLayers;
        if (startLayer < 0 || startLayer > _tree.NumLayers) throw new ArgumentException("start_layer must be an integer between 0 and tree.num_layers");
        int numLayers = q.NumLayers ?? Math.Min(_tree.NumLayers + 1, startLayer + 1);
        if (numLayers < 1) throw new ArgumentException("num_layers must be an integer and at least 1");
        if (numLayers > startLayer + 1) throw new ArgumentException("num_layers must be less than or equal to start_layer + 1");
        return (startLayer, numLayers);
    }

    /// <summary>Adds each not-yet-taken node that fits the optional token cap; returns the new token total.</summary>
    private int TakeWithin(List<Scored> best, int? maxTokens, List<Scored> selected, HashSet<int> taken, int total)
    {
        foreach (var s in best)
        {
            if (taken.Contains(s.Index)) continue;
            int t = _tree[s.Index].TokenCount;
            if (maxTokens is { } m && total + t > m) continue;
            selected.Add(s);
            taken.Add(s.Index);
            total += t;
        }
        return total;
    }

    /// <summary>The eligible children of the selected nodes, first occurrence order.</summary>
    private List<int> ChildrenOf(List<Scored> best, HashSet<int>? eligible)
    {
        var children = new List<int>();
        var seen = new HashSet<int>();
        foreach (var s in best)
            foreach (var c in _tree[s.Index].Children)
                if ((eligible == null || eligible.Contains(c)) && seen.Add(c)) children.Add(c);
        return children;
    }

}
