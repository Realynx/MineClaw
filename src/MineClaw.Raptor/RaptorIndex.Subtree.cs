using MineClaw.Raptor.Clustering;
using MineClaw.Raptor.Model;

namespace MineClaw.Raptor;

/// <summary>Subtree rebuild (MineClaw admin extension): re-cluster and re-summarize one summary node's section of the tree.</summary>
public sealed partial class RaptorIndex
{
    private RaptorTreeBuilder BuilderFor(ISummarizer? summarizer) =>
        summarizer == null ? _builder : new RaptorTreeBuilder(_embedder, summarizer, _o, _tok);

    /// <summary>Prompt tokens every summary call pays on top of its cluster text (system line + the paper's user template).</summary>
    public int PromptOverheadTokens => _tok.Count(_o.SummarySystemPrompt) + _tok.Count(_o.UserPrompt(""));

    /// <summary>
    /// The subtree of summary node N at layer L: its summary descendants (layers 1..L-1), split into the ones that belong only
    /// to it (every parent is N or another exclusive descendant) and the ones shared with parents outside it, plus every leaf
    /// reachable from N.
    /// </summary>
    private sealed record SubtreePlan(int Node, int Layer, List<RaptorNode> Leaves, List<int> Exclusive, HashSet<int> Owned, List<int> Shared,
        Dictionary<int, List<int>> ExclusiveByLayer, int NextIndex);

    private static SubtreePlan PlanSubtree(RaptorTree tree, int nodeIndex)
    {
        if (!tree.TryGet(nodeIndex, out var node)) throw new ArgumentException($"no node #{nodeIndex} in the tree", nameof(nodeIndex));
        if (node.IsLeaf) throw new ArgumentException($"#{nodeIndex} is a leaf; only a summary node (layer >= 1) can be rebuilt", nameof(nodeIndex));
        int layer = node.Layer;
        var owned = new HashSet<int> { nodeIndex };
        var exclusive = new List<int>();
        var shared = new List<int>();
        var byLayer = new Dictionary<int, List<int>>();
        var frontier = new SortedSet<int>(node.Children);
        for (int l = layer - 1; l >= 1; l--)
        {
            var next = new SortedSet<int>();
            byLayer[l] = [];
            foreach (var i in frontier)
            {
                var n = tree[i];
                if (n.Parents.All(owned.Contains))
                {
                    owned.Add(i);
                    exclusive.Add(i);
                    byLayer[l].Add(i);
                }
                else shared.Add(i);
                next.UnionWith(n.Children);
            }
            frontier = next;
        }
        var leaves = frontier.Select(i => tree[i]).Where(n => n.IsLeaf).OrderBy(n => n.Index).ToList();
        return new SubtreePlan(nodeIndex, layer, leaves, exclusive, owned, shared, byLayer, tree.NextIndex);
    }

    /// <summary>Size of <see cref="RebuildSubtreeAsync"/> before it runs: one summary per current exclusive summary descendant
    /// (at least one per intermediate layer) plus N; inputs = the current layer below each, plus prompt overhead.</summary>
    public SubtreeEstimate EstimateSubtree(int nodeIndex) => Read(t =>
    {
        var plan = PlanSubtree(t, nodeIndex);
        int overhead = PromptOverheadTokens;
        int summaries = 0;
        long input = 0;
        long below = plan.Leaves.Sum(l => (long)l.TokenCount);
        for (int l = 1; l < plan.Layer; l++)
        {
            int n = Math.Max(1, plan.ExclusiveByLayer.GetValueOrDefault(l)?.Count ?? 0);
            summaries += n;
            input += below + (long)n * overhead;
            var ex = plan.ExclusiveByLayer.GetValueOrDefault(l);
            below = ex is { Count: > 0 } ? ex.Sum(i => (long)t[i].TokenCount) : _o.SummarizationLength;
        }
        summaries += 1;
        input += below + overhead;
        return new SubtreeEstimate(nodeIndex, plan.Layer, plan.Leaves.Count, summaries, input, (long)summaries * _o.SummarizationLength);
    });

    /// <summary>
    /// Rebuilds the section of the tree under summary node <paramref name="nodeIndex"/> (layer L ≥ 1): all leaves under it are
    /// re-clustered with the RAPTOR clustering layer by layer into exactly L−1 intermediate layers (a layer too small for UMAP
    /// becomes one cluster, split only by max_length_in_cluster), each cluster is summarized and embedded, and N itself is
    /// re-summarized over its new children. N keeps its index and its parents. Summary descendants that belonged only to N are
    /// replaced; descendants shared with parents outside the subtree keep those links (and their text, embedding, children).
    /// Every node outside the subtree is left untouched. Nothing changes until all summaries are done (cancellation leaves the
    /// tree as it was); clusters the budget cannot cover get extractive placeholders marked SummaryPending.
    /// </summary>
    /// <param name="summarizer">Overrides the index's summarizer for this call (an admin job with its own spend cap).</param>
    public async Task<SubtreeRebuildReport> RebuildSubtreeAsync(int nodeIndex, RaptorBudget? budget = null,
        IProgress<(string Stage, int Done, int Total)>? progress = null, CancellationToken ct = default, ISummarizer? summarizer = null)
    {
        budget ??= RaptorBudget.Unlimited();
        var builder = BuilderFor(summarizer);
        await _writer.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var plan = Read(t => PlanSubtree(t, nodeIndex));
            int L = plan.Layer;
            int totalSteps = L; // L-1 intermediate layers + N
            int nextIndex = plan.NextIndex;
            long embedTokens = 0;
            int summaries = 0, placeholders = 0;
            var clustering = new RaptorClustering(_o);
            var created = new List<RaptorNode>();
            var links = new List<(int Parent, int Child)>();
            var current = plan.Leaves;
            if (current.Count == 0) throw new InvalidOperationException($"#{nodeIndex} has no leaves under it");

            for (int layer = 1; layer < L; layer++)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(($"cluster layer {layer}", layer - 1, totalSteps));
                var cur = current;
                var clusters = await Task.Run(() => Group(clustering, cur), ct).ConfigureAwait(false);
                var groups = clusters.Select(c => (IReadOnlyList<RaptorNode>)c.Select(i => cur[i]).ToList()).ToList();
                progress?.Report(($"summarize layer {layer} ({groups.Count})", layer - 1, totalSteps));
                var outcomes = await builder.SummarizeManyAsync(groups, budget, ct).ConfigureAwait(false);
                var (vecs, tokens) = await builder.EmbedAllAsync(outcomes.Select(o => o.Text).ToList(), ct).ConfigureAwait(false);
                embedTokens += tokens;
                var next = new List<RaptorNode>(groups.Count);
                for (int g = 0; g < groups.Count; g++)
                {
                    var o = outcomes[g];
                    if (o.Summarized) summaries++;
                    if (o.Pending) placeholders++;
                    var n = new RaptorNode(nextIndex++, layer, o.Text, vecs[g], _tok.Count(o.Text)) { SummaryPending = o.Pending };
                    created.Add(n);
                    foreach (var child in groups[g]) links.Add((n.Index, child.Index));
                    next.Add(n);
                }
                current = next;
            }

            ct.ThrowIfCancellationRequested();
            progress?.Report(("summarize root", L - 1, totalSteps));
            var top = current;
            var rootOutcome = (await builder.SummarizeManyAsync([top], budget, ct).ConfigureAwait(false))[0];
            var (rootVecs, rootTokens) = await builder.EmbedAllAsync([rootOutcome.Text], ct).ConfigureAwait(false);
            embedTokens += rootTokens;
            if (rootOutcome.Summarized) summaries++;
            if (rootOutcome.Pending) placeholders++;
            ct.ThrowIfCancellationRequested();

            var changes = new List<SubtreeNodeChange>();
            Mutate(tree =>
            {
                var node = tree[nodeIndex];
                changes.Add(new SubtreeNodeChange(nodeIndex, L, node.Text, rootOutcome.Text));
                // Shared descendants: keep their outside links, drop the ones into the subtree.
                foreach (var s in plan.Shared)
                    foreach (var p in tree[s].Parents.Where(plan.Owned.Contains).ToList())
                    {
                        tree.Unlink(p, s);
                        Touch(s);
                    }
                foreach (var e in plan.Exclusive)
                {
                    var old = tree[e];
                    changes.Add(new SubtreeNodeChange(e, old.Layer, old.Text, null));
                    tree.Remove(e);
                    _deletes.Add(e);
                    _upserts.Remove(e);
                }
                foreach (var c in node.Children.ToList()) tree.Unlink(nodeIndex, c);
                foreach (var n in created)
                {
                    tree.Add(n);
                    Touch(n.Index);
                    changes.Add(new SubtreeNodeChange(n.Index, n.Layer, null, n.Text));
                }
                foreach (var (p, c) in links) tree.Link(p, c);
                foreach (var t in top) tree.Link(nodeIndex, t.Index);
                node.Text = rootOutcome.Text;
                node.Embedding = rootVecs[0];
                node.TokenCount = _tok.Count(rootOutcome.Text);
                node.Dirty = false;
                node.SummaryPending = rootOutcome.Pending;
                Touch(nodeIndex);
            });
            progress?.Report(("persist", L, totalSteps));
            await PersistAsync(false, ct).ConfigureAwait(false);
            var ordered = changes.OrderByDescending(c => c.Layer).ThenBy(c => c.NewText == null ? 0 : 1).ThenBy(c => c.Index).ToList();
            return new SubtreeRebuildReport(nodeIndex, L, plan.Leaves.Count, plan.Exclusive.Count, plan.Shared.Count, created.Count, summaries, placeholders,
                embedTokens, ordered);
        }
        finally { _writer.Release(); }
    }

    /// <summary>
    /// RAPTOR clustering of one layer of the rebuilt section. A layer too small for UMAP (≤ reduction_dimension + 1 nodes, the
    /// builder's own stop rule) is one cluster, split into consecutive token-capped groups if it exceeds max_length_in_cluster.
    /// Every node lands in at least one group.
    /// </summary>
    private List<int[]> Group(RaptorClustering clustering, IReadOnlyList<RaptorNode> nodes)
    {
        List<int[]> groups;
        if (nodes.Count <= _o.ReductionDimension + 1) groups = Pack(Enumerable.Range(0, nodes.Count).ToArray(), nodes);
        else groups = clustering.Cluster(nodes.Select(n => n.Embedding).ToList(), nodes.Select(n => n.TokenCount).ToList());
        groups = groups.Where(g => g.Length > 0).ToList();
        var covered = groups.SelectMany(g => g).ToHashSet();
        for (int i = 0; i < nodes.Count; i++) if (!covered.Contains(i)) groups.Add([i]);
        return groups;
    }

    private List<int[]> Pack(int[] members, IReadOnlyList<RaptorNode> nodes)
    {
        long total = members.Sum(i => (long)nodes[i].TokenCount);
        if (total <= _o.MaxLengthInCluster || members.Length <= 1) return [members];
        var res = new List<int[]>();
        var cur = new List<int>();
        long len = 0;
        foreach (var i in members)
        {
            if (cur.Count > 0 && len + nodes[i].TokenCount > _o.MaxLengthInCluster)
            {
                res.Add(cur.ToArray());
                cur.Clear();
                len = 0;
            }
            cur.Add(i);
            len += nodes[i].TokenCount;
        }
        if (cur.Count > 0) res.Add(cur.ToArray());
        return res;
    }
}
