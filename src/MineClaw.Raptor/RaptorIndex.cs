using MineClaw.Raptor.Clustering;
using MineClaw.Raptor.Model;
using MineClaw.Raptor.Numerics;
using MineClaw.Raptor.Storage;
using MineClaw.Raptor.Text;

namespace MineClaw.Raptor;

public sealed record InsertReport(int Leaves, int Attached, int DirtyNodes, long EmbeddingTokens, double MeanParentSimilarity);

public sealed record RefreshReport(int Refreshed, int Splits, int RemainingDirty, long EmbeddingTokens, bool BudgetExhausted);

public sealed record RebuildDecision(bool Rebuild, string Reason, double ChangedFraction, double SimilarityDrift, int DirtyNodes);

/// <summary>One summary node a subtree rebuild replaced (old text, new null), created (old null, new text) or rewrote (both).</summary>
public sealed record SubtreeNodeChange(int Index, int Layer, string? OldText, string? NewText);

/// <param name="Replaced">Summary descendants that belonged only to the subtree and were removed.</param>
/// <param name="Kept">Summary descendants shared with parents outside the subtree (kept, unlinked from it).</param>
/// <param name="Created">New intermediate summaries.</param>
public sealed record SubtreeRebuildReport(int Node, int Layer, int Leaves, int Replaced, int Kept, int Created, int Summaries, int Placeholders,
    long EmbeddingTokens, IReadOnlyList<SubtreeNodeChange> Changes);

/// <summary>Up-front size of a subtree rebuild: summaries it will write and the summarizer tokens they read/write (library estimate:
/// output = summarization_length per summary).</summary>
public sealed record SubtreeEstimate(int Node, int Layer, int Leaves, int Summaries, long InputTokens, long OutputTokens);

/// <summary>
/// Thread-safe RAPTOR index: the faithful static build, both retrieval modes, and MineClaw's incremental maintenance
/// (insert / remove / refresh dirty / rebuild trigger), persisted through an optional <see cref="IRaptorStore"/>.
/// Writers are serialized; readers run concurrently with each other and never wait on a summarizer or embedder call.
/// </summary>
public sealed partial class RaptorIndex : IDisposable
{
    private readonly IEmbedder _embedder;
    private readonly RaptorTreeBuilder _builder;
    private readonly RaptorOptions _o;
    private readonly ITokenCounter _tok;
    private readonly IRaptorStore? _store;
    private readonly SemaphoreSlim _writer = new(1, 1);
    private readonly ReaderWriterLockSlim _rw = new(LockRecursionPolicy.NoRecursion);

    private RaptorTree _tree = new();
    private RaptorRetriever _retriever;
    private readonly HashSet<int> _upserts = new();
    private readonly HashSet<int> _deletes = new();
    private bool _replaceAll;

    public RaptorIndex(IEmbedder embedder, ISummarizer summarizer, RaptorOptions? options = null, ITokenCounter? tokenCounter = null, IRaptorStore? store = null)
    {
        _embedder = embedder;
        _o = options ?? new RaptorOptions();
        _tok = tokenCounter ?? ApproxTokenCounter.Instance;
        _builder = new RaptorTreeBuilder(embedder, summarizer, _o, _tok);
        _store = store;
        _tree.EmbeddingModel = embedder.Model;
        _tree.Dimensions = embedder.Dimensions;
        _retriever = new RaptorRetriever(_tree, _o);
    }

    public RaptorOptions Options => _o;
    /// <summary>Persist after every mutation (default true when a store is given).</summary>
    public bool AutoPersist { get; init; } = true;

    /// <summary>Read access to the current tree. Do not hold across awaits or mutate.</summary>
    public T Read<T>(Func<RaptorTree, T> read)
    {
        _rw.EnterReadLock();
        try { return read(_tree); }
        finally { _rw.ExitReadLock(); }
    }

    public int LeafCount => Read(t => t.LeafCount);
    public int NodeCount => Read(t => t.Count);
    public int NumLayers => Read(t => t.NumLayers);
    public int DirtyCount => Read(t => t.AllNodes.Values.Count(n => n.Dirty || n.SummaryPending));

    // ======================= persistence =======================

    /// <summary>
    /// Loads the stored tree. Returns false when nothing is stored or it was built with a different embedding model or
    /// dimension (the caller should then rebuild from its documents).
    /// </summary>
    public async Task<bool> LoadAsync(CancellationToken ct = default)
    {
        if (_store == null) return false;
        await _writer.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var snap = await _store.LoadAsync(ct).ConfigureAwait(false);
            if (snap == null) return false;
            if (snap.Meta.Dimensions != _embedder.Dimensions && _embedder.Dimensions > 0) return false;
            if (!string.Equals(snap.Meta.EmbeddingModel, _embedder.Model, StringComparison.Ordinal)) return false;
            var tree = RaptorSnapshotMapper.FromSnapshot(snap);
            Swap(tree, replaceAll: false);
            _upserts.Clear();
            _deletes.Clear();
            _replaceAll = false;
            return true;
        }
        finally { _writer.Release(); }
    }

    public async Task SaveAsync(CancellationToken ct = default)
    {
        await _writer.WaitAsync(ct).ConfigureAwait(false);
        try { await PersistAsync(force: true, ct).ConfigureAwait(false); }
        finally { _writer.Release(); }
    }

    private async Task PersistAsync(bool force, CancellationToken ct)
    {
        if (_store == null || (!force && !AutoPersist)) return;
        RaptorChangeSet changes;
        _rw.EnterReadLock();
        try
        {
            var ups = _replaceAll ? _tree.AllNodes.Keys.ToList() : _upserts.Where(_tree.AllNodes.ContainsKey).ToList();
            ups.Sort();
            changes = new RaptorChangeSet(RaptorSnapshotMapper.ToMeta(_tree),
                ups.Select(i => RaptorSnapshotMapper.ToRecord(_tree[i])).ToList(),
                _replaceAll ? [] : _deletes.Where(i => !_tree.AllNodes.ContainsKey(i)).Order().ToList(),
                _replaceAll);
        }
        finally { _rw.ExitReadLock(); }
        await _store.ApplyAsync(changes, ct).ConfigureAwait(false);
        _upserts.Clear();
        _deletes.Clear();
        _replaceAll = false;
    }

    private void Swap(RaptorTree tree, bool replaceAll)
    {
        var retriever = new RaptorRetriever(tree, _o);
        _rw.EnterWriteLock();
        try
        {
            _tree = tree;
            _retriever = retriever;
            if (replaceAll) _replaceAll = true;
        }
        finally { _rw.ExitWriteLock(); }
    }

    private void Mutate(Action<RaptorTree> change)
    {
        _rw.EnterWriteLock();
        try
        {
            change(_tree);
            _retriever.Refresh();
        }
        finally { _rw.ExitWriteLock(); }
    }

    private void Touch(int index) { _upserts.Add(index); _deletes.Remove(index); }

    // ======================= static build (paper) =======================

    /// <summary>Replaces the tree with a fresh faithful RAPTOR build over <paramref name="documents"/>.</summary>
    public async Task<BuildStats> BuildAsync(IEnumerable<RaptorDocument> documents, RaptorBudget? budget = null, CancellationToken ct = default)
    {
        await _writer.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var (tree, stats) = await _builder.BuildAsync(documents, budget, ct).ConfigureAwait(false);
            Swap(tree, replaceAll: true);
            await PersistAsync(false, ct).ConfigureAwait(false);
            return stats;
        }
        finally { _writer.Release(); }
    }

    /// <summary>
    /// Full faithful rebuild over the current leaves (texts and embeddings kept, summaries regenerated; identical
    /// clusters reuse their previous summary).
    /// </summary>
    public async Task<BuildStats> RebuildAsync(RaptorBudget? budget = null, CancellationToken ct = default)
    {
        await _writer.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var (leaves, cache) = Read(t => (t.Leaves.OrderBy(n => n.Index).ToList(), SummaryCache.FromTree(t)));
            var (tree, stats) = await _builder.BuildFromLeavesAsync(leaves, budget, ct, cache).ConfigureAwait(false);
            tree.EmbeddingModel = _embedder.Model;
            Swap(tree, replaceAll: true);
            await PersistAsync(false, ct).ConfigureAwait(false);
            return stats;
        }
        finally { _writer.Release(); }
    }

    // ======================= retrieval =======================

    public async Task<RaptorResult> RetrieveAsync(RaptorQuery query, CancellationToken ct = default)
    {
        long tokens = 0;
        var vector = query.Vector;
        if (vector == null)
        {
            if (string.IsNullOrWhiteSpace(query.Text)) return RaptorResult.Empty;
            if (NodeCount == 0) return RaptorResult.Empty;
            var r = await _embedder.EmbedAsync([query.Text], ct).ConfigureAwait(false);
            vector = r.Vectors[0];
            tokens = r.Tokens;
        }
        return Retrieve(vector, query) with { EmbeddingTokens = tokens };
    }

    public RaptorResult Retrieve(float[] queryVector, RaptorQuery query)
    {
        _rw.EnterReadLock();
        try { return _retriever.Retrieve(queryVector, query); }
        finally { _rw.ExitReadLock(); }
    }

    // ======================= incremental maintenance (MineClaw extension) =======================

    /// <summary>
    /// Adds documents as new leaves. Each leaf attaches under the layer-1 node(s) whose soft responsibility exceeds
    /// <see cref="RaptorOptions.InsertThreshold"/> (always at least the best one); those parents and all their
    /// ancestors become dirty. With no summary layers yet, leaves simply join the leaf/root layer.
    /// </summary>
    public async Task<InsertReport> InsertAsync(IEnumerable<RaptorDocument> documents, CancellationToken ct = default)
    {
        await _writer.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var (start, seq) = Read(t => (t.NextIndex, t.InsertSeq));
            var (leaves, tokens) = await _builder.CreateLeavesAsync(documents, start, seq, ct).ConfigureAwait(false);
            if (leaves.Count == 0) return new InsertReport(0, 0, 0, tokens, 0);
            var report = Attach(leaves, tokens);
            await PersistAsync(false, ct).ConfigureAwait(false);
            return report;
        }
        finally { _writer.Release(); }
    }

    /// <summary>
    /// Atomic in-place update of documents already in the tree (admin "rebuild a kind"): every leaf whose Key is one of the
    /// documents' keys is removed (counted as replacing) and the documents are inserted again, under one writer hold, so a
    /// concurrent sync never sees the documents half-removed. The new leaves are created (embedded) before anything changes.
    /// </summary>
    public async Task<(int Removed, InsertReport Insert)> ReplaceLeavesAsync(IReadOnlyList<RaptorDocument> documents, CancellationToken ct = default)
    {
        var keys = new HashSet<string>(documents.Where(d => d.Key != null).Select(d => d.Key!), StringComparer.Ordinal);
        await _writer.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var (start, seq) = Read(t => (t.NextIndex, t.InsertSeq));
            var (leaves, tokens) = await _builder.CreateLeavesAsync(documents, start, seq, ct).ConfigureAwait(false);
            int removed = keys.Count > 0 ? RemoveCore(keys, replacing: true) : 0;
            var report = leaves.Count == 0 ? new InsertReport(0, 0, 0, tokens, 0) : Attach(leaves, tokens);
            await PersistAsync(false, ct).ConfigureAwait(false);
            return (removed, report);
        }
        finally { _writer.Release(); }
    }

    /// <summary>Adds created leaves and soft-attaches them (caller holds the writer).</summary>
    private InsertReport Attach(List<RaptorNode> leaves, long tokens)
    {
        int attached = 0;
        double simSum = 0;
        var dirty = new HashSet<int>();
        Mutate(tree =>
        {
            if (tree.Count == 0) tree.Dimensions = leaves[0].Embedding.Length;
            if (leaves[0].Embedding.Length != tree.Dimensions)
                throw new InvalidOperationException($"Embedding dims {leaves[0].Embedding.Length} differ from the tree's {tree.Dimensions}.");
            foreach (var leaf in leaves)
            {
                tree.Add(leaf);
                Touch(leaf.Index);
                tree.InsertedSinceBuild++;
                tree.InsertSeq = Math.Max(tree.InsertSeq, leaf.InsertedSeq);
                if (tree.NumLayers < 1) continue;
                var (parents, bestSim) = ChooseParents(tree, leaf);
                foreach (var p in parents)
                {
                    tree.Link(p, leaf.Index);
                    Touch(p);
                    MarkDirtyUpward(tree, p, dirty);
                }
                attached++;
                simSum += bestSim;
                tree.InsertedSimilaritySum += bestSim;
                tree.InsertedSimilarityCount++;
            }
        });
        return new InsertReport(leaves.Count, attached, dirty.Count, tokens, attached == 0 ? 0 : simSum / attached);
    }

    /// <summary>
    /// Soft attachment (README D12): <c>r_j = softmax_j(cos(leaf, node_j) / τ)</c> over the top layer-1 candidates,
    /// τ fitted by maximum likelihood to the build's own GMM memberships (<see cref="InsertCalibration"/>). The leaf
    /// joins the most similar node and every other j with r_j &gt; InsertThreshold (the 0.1 rule of GMM_cluster).
    /// </summary>
    private (List<int> Parents, double BestSim) ChooseParents(RaptorTree tree, RaptorNode leaf)
    {
        var cands = tree.LayerIndices(1)
            .Select(i => (Index: i, Sim: (double)VectorMath.Dot(leaf.Embedding, tree[i].Embedding)))
            .OrderByDescending(c => c.Sim).ThenBy(c => c.Index)
            .Take(Math.Max(1, _o.InsertCandidates)).ToList();
        if (cands.Count == 0) return ([], 0);
        double tau = tree.InsertTemperature > 0 ? tree.InsertTemperature : InsertCalibration.DefaultTemperature;
        double z = cands.Sum(c => Math.Exp((c.Sim - cands[0].Sim) / tau));
        var parents = new List<int> { cands[0].Index };
        for (int t = 1; t < cands.Count; t++)
            if (Math.Exp((cands[t].Sim - cands[0].Sim) / tau) / z > _o.InsertThreshold) parents.Add(cands[t].Index);
        return (parents, cands[0].Sim);
    }

    private static void MarkDirtyUpward(RaptorTree tree, int index, HashSet<int> dirty)
    {
        var stack = new Stack<int>();
        stack.Push(index);
        while (stack.Count > 0)
        {
            int i = stack.Pop();
            if (!dirty.Add(i)) continue;
            var n = tree[i];
            n.Dirty = true;
            foreach (var p in n.Parents) stack.Push(p);
        }
    }

    /// <summary>Removes every leaf whose Key is in <paramref name="keys"/>; empty summaries are pruned upward.</summary>
    public Task<int> RemoveAsync(IEnumerable<string> keys, CancellationToken ct = default) => RemoveAsync(keys, replacing: false, ct);

    /// <summary>
    /// <paramref name="replacing"/> = the leaves are about to be re-inserted with their doc's new text (an in-place update):
    /// the re-insert alone counts toward the rebuild threshold, so one edited doc is one change, not two (night review B23).
    /// </summary>
    public async Task<int> RemoveAsync(IEnumerable<string> keys, bool replacing, CancellationToken ct = default)
    {
        var set = new HashSet<string>(keys, StringComparer.Ordinal);
        if (set.Count == 0) return 0;
        await _writer.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            int removed = RemoveCore(set, replacing);
            await PersistAsync(false, ct).ConfigureAwait(false);
            return removed;
        }
        finally { _writer.Release(); }
    }

    /// <summary>Removes leaves by key and prunes empty summaries upward (caller holds the writer).</summary>
    private int RemoveCore(HashSet<string> set, bool replacing)
    {
        {
            int removed = 0;
            Mutate(tree =>
            {
                var dirty = new HashSet<int>();
                var victims = tree.Leaves.Where(l => l.Key != null && set.Contains(l.Key)).Select(l => l.Index).ToList();
                var queue = new Queue<int>(victims);
                while (queue.Count > 0) removed += RemoveQueued(tree, queue.Dequeue(), replacing, queue, dirty);
                                while (tree.NumLayers > 0 && tree.LayerIndices(tree.NumLayers).Count == 0) tree.NumLayers--;
                foreach (var d in dirty) Touch(d);
            });
            return removed;
        }
    }

    /// <summary>
    /// Removes one queued node; parents left without children are queued, the others marked dirty upward.
    /// Returns 1 when it removed a leaf.
    /// </summary>
    private int RemoveQueued(RaptorTree tree, int i, bool replacing, Queue<int> queue, HashSet<int> dirty)
    {
        if (!tree.TryGet(i, out var node)) return 0;
        var parents = node.Parents.ToList();
        tree.Remove(i);
        _deletes.Add(i);
        _upserts.Remove(i);
        int removed = 0;
        if (node.IsLeaf)
        {
            removed = 1;
            if (!replacing) tree.RemovedSinceBuild++;
        }
        foreach (var p in parents)
        {
            if (!tree.TryGet(p, out var pn)) continue;
            Touch(p);
            if (pn.Children.Count == 0) queue.Enqueue(p);
            else MarkDirtyUpward(tree, p, dirty);
        }
        return removed;
    }

    /// <summary>
    /// Re-summarizes and re-embeds dirty / placeholder summaries bottom-up (layer 1 first) within the budget. With
    /// <see cref="RaptorOptions.SplitOverCapOnRefresh"/>, a dirty node whose children now exceed max_length_in_cluster
    /// is first split by running RAPTOR clustering on its children. Nodes the budget cannot cover stay dirty.
    /// </summary>
    /// <param name="summarizer">Overrides the index's summarizer for this call (an admin job with its own spend cap); null = the index's.</param>
    public async Task<RefreshReport> RefreshDirtyAsync(RaptorBudget? budget = null, CancellationToken ct = default, ISummarizer? summarizer = null)
    {
        budget ??= RaptorBudget.Unlimited();
        var builder = BuilderFor(summarizer);
        await _writer.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            int refreshed = 0, splits = 0;
            long embedTokens = 0;
            int numLayers = Read(t => t.NumLayers);
            for (int layer = 1; layer <= numLayers; layer++)
            {
                if (budget.IsExhausted) break;
                if (_o.SplitOverCapOnRefresh) splits += await SplitOverCapAsync(layer, ct).ConfigureAwait(false);
                var work = Read(t => t.Layer(layer).Where(n => n.Dirty || n.SummaryPending).OrderBy(n => n.Index)
                    .Select(n => (n.Index, Children: (IReadOnlyList<RaptorNode>)n.Children.Select(c => t[c]).ToList())).ToList());
                if (work.Count == 0) continue;
                var outcomes = await builder.SummarizeManyAsync(work.Select(w => w.Children).ToList(), budget, ct).ConfigureAwait(false);
                var done = Enumerable.Range(0, work.Count).Where(i => outcomes[i].Summarized && !outcomes[i].Pending).ToList();
                var (vecs, tokens) = await builder.EmbedAllAsync(done.Select(i => outcomes[i].Text).ToList(), ct).ConfigureAwait(false);
                embedTokens += tokens;
                Mutate(tree =>
                {
                    for (int t = 0; t < done.Count; t++)
                    {
                        var n = tree[work[done[t]].Index];
                        n.Text = outcomes[done[t]].Text;
                        n.Embedding = vecs[t];
                        n.TokenCount = _tok.Count(n.Text);
                        n.Dirty = false;
                        n.SummaryPending = false;
                        Touch(n.Index);
                    }
                });
                refreshed += done.Count;
            }
            await PersistAsync(false, ct).ConfigureAwait(false);
            return new RefreshReport(refreshed, splits, DirtyCount, embedTokens, budget.IsExhausted);
        }
        finally { _writer.Release(); }
    }

    private async Task<int> SplitOverCapAsync(int layer, CancellationToken ct)
    {
        var over = Read(t => t.Layer(layer)
            .Where(n => n.Dirty && n.Children.Count >= 2 && n.Children.Sum(c => (long)t[c].TokenCount) > _o.MaxLengthInCluster)
            .OrderBy(n => n.Index)
            .Select(n => (n.Index, Children: n.Children.ToArray(), Emb: n.Children.Select(c => t[c].Embedding).ToList(), Tok: n.Children.Select(c => t[c].TokenCount).ToList()))
            .ToList());
        int splits = 0;
        foreach (var o in over)
        {
            var groups = await Task.Run(() => new RaptorClustering(_o).Cluster(o.Emb, o.Tok), ct).ConfigureAwait(false);
            if (groups.Count < 2) continue;
            Mutate(tree =>
            {
                var node = tree[o.Index];
                var parents = node.Parents.ToList();
                foreach (var c in o.Children) tree.Unlink(o.Index, c);
                for (int g = 0; g < groups.Count; g++)
                {
                    RaptorNode target;
                    if (g == 0) target = node;
                    else
                    {
                        target = new RaptorNode(tree.NextIndex, layer, node.Text, node.Embedding, node.TokenCount) { Dirty = true, SummaryPending = true };
                        tree.Add(target);
                        foreach (var p in parents) tree.Link(p, target.Index);
                    }
                    foreach (var ci in groups[g]) tree.Link(target.Index, o.Children[ci]);
                    target.Dirty = true;
                    Touch(target.Index);
                }
                foreach (var p in parents) Touch(p);
            });
            splits++;
        }
        return splits;
    }

    /// <summary>
    /// Rebuild trigger: the tree has no summary layers but now could; or (inserted + removed) / leaves-at-build &gt;
    /// RebuildChangedFraction; or the mean inserted-leaf→parent cosine drifted more than RebuildSimilarityDrift below
    /// the build-time baseline.
    /// </summary>
    public RebuildDecision ShouldRebuild() => Read(t =>
    {
        int dirtyNodes = t.AllNodes.Values.Count(n => n.Dirty || n.SummaryPending);
        if (t.LeafCount == 0) return new RebuildDecision(false, "empty", 0, 0, dirtyNodes);
        double changed = (t.InsertedSinceBuild + t.RemovedSinceBuild) / (double)Math.Max(1, t.LeavesAtBuild);
        double drift = t.InsertedSimilarityCount > 0 ? t.BaselineLeafParentSimilarity - t.InsertedSimilaritySum / t.InsertedSimilarityCount : 0;
        if (t.NumLayers == 0 && t.LeafCount > _o.ReductionDimension + 1 && t.InsertedSinceBuild > 0)
            return new RebuildDecision(true, "no summary layers yet and enough leaves to cluster", changed, drift, dirtyNodes);
        if (t.LeavesAtBuild > 0 && changed > _o.RebuildChangedFraction)
            return new RebuildDecision(true, $"changed fraction {changed:P0} > {_o.RebuildChangedFraction:P0}", changed, drift, dirtyNodes);
        double driftLimit = Math.Max(_o.RebuildSimilarityDrift, _o.RebuildDriftStdDevs * t.BaselineLeafParentSimilarityStd);
        if (t.InsertedSimilarityCount >= _o.RebuildDriftMinSamples && drift > driftLimit)
            return new RebuildDecision(true, $"leaf→parent similarity drift {drift:F3} > {driftLimit:F3}", changed, drift, dirtyNodes);
        return new RebuildDecision(false, "ok", changed, drift, dirtyNodes);
    });

    public void Dispose()
    {
        _writer.Dispose();
        _rw.Dispose();
    }
}
