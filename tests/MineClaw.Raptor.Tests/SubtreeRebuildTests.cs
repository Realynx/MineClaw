using MineClaw.Raptor.Model;
using MineClaw.Raptor.Storage;

namespace MineClaw.Raptor.Tests;

public class SubtreeRebuildTests
{
    private static readonly HashingEmbedder Embedder = new(256);

    private sealed record NodeState(int Layer, string Text, float[] Embedding, int[] Children);

    private static Dictionary<int, NodeState> Snapshot(RaptorIndex index) => index.Read(t =>
        t.AllNodes.Values.ToDictionary(n => n.Index, n => new NodeState(n.Layer, n.Text, n.Embedding.ToArray(), n.Children.ToArray())));

    private static HashSet<int> Descendants(RaptorTree t, int index)
    {
        var set = new HashSet<int>();
        var stack = new Stack<int>(t[index].Children);
        while (stack.Count > 0)
        {
            int i = stack.Pop();
            if (!set.Add(i)) continue;
            foreach (var c in t[i].Children) stack.Push(c);
        }
        return set;
    }

    private static async Task<(RaptorIndex Index, InMemoryRaptorStore Store, KeywordSummarizer Summarizer)> BuildAsync(int leaves = 400, int topics = 4, int subtopics = 4)
    {
        var corpus = new SyntheticCorpus(leaves, topics, subtopics, seed: 7);
        var store = new InMemoryRaptorStore();
        var summarizer = new KeywordSummarizer();
        var index = new RaptorIndex(Embedder, summarizer, TestOptions.Default with { SummaryConcurrency = 2 }, store: store);
        await index.BuildAsync(corpus.Documents());
        return (index, store, summarizer);
    }

    [Fact]
    public async Task Rebuild_of_one_subtree_leaves_the_others_untouched()
    {
        var (index, store, _) = await BuildAsync();
        using var _ = index;
        Assert.True(index.NumLayers >= 2, $"need a layer-2 node, tree has {index.NumLayers} layers");
        int target = index.Read(t => t.Layer(2).OrderByDescending(n => Descendants(t, n.Index).Count).First().Index);
        var before = Snapshot(index);
        var (inSubtree, leavesUnder, parents) = index.Read(t => (Descendants(t, target), Descendants(t, target).Where(i => t[i].IsLeaf).ToHashSet(), t[target].Parents.ToArray()));

        var estimate = index.EstimateSubtree(target);
        Assert.Equal(2, estimate.Layer);
        Assert.Equal(leavesUnder.Count, estimate.Leaves);
        Assert.True(estimate.Summaries >= 2 && estimate.InputTokens > 0 && estimate.OutputTokens > 0);

        var stages = new List<string>();
        var report = await index.RebuildSubtreeAsync(target, progress: new SyncProgress(p => stages.Add(p.Stage)));
        var after = Snapshot(index);

        Assert.Empty(index.Read(t => t.Validate()));
        Assert.Equal(target, report.Node);
        Assert.Equal(leavesUnder.Count, report.Leaves);
        Assert.True(report.Created > 0 && report.Replaced > 0, $"created {report.Created}, replaced {report.Replaced}");
        Assert.Contains(stages, s => s.StartsWith("summarize root", StringComparison.Ordinal));
        // N kept its index, layer and parents; its text was rewritten over new children.
        Assert.True(after.ContainsKey(target));
        Assert.Equal(2, after[target].Layer);
        index.Read(t => { Assert.Equal(parents, t[target].Parents.ToArray()); return 0; });
        Assert.Contains(report.Changes, c => c.Index == target && c.OldText == before[target].Text && c.NewText == after[target].Text);
        // Same leaves under N, now through the new layer-1 summaries.
        var newUnder = index.Read(t => Descendants(t, target));
        Assert.Equal(leavesUnder, newUnder.Where(i => after[i].Layer == 0).ToHashSet());

        // Every node outside N's subtree: same text, embedding and children (byte-identical).
        foreach (var (i, b) in before)
        {
            if (i == target || inSubtree.Contains(i)) continue;
            Assert.True(after.TryGetValue(i, out var a), $"#{i} outside the subtree vanished");
            Assert.Equal(b.Text, a.Text);
            Assert.Equal(b.Embedding, a.Embedding);
            Assert.Equal(b.Children, a.Children);
        }
        // Leaves under N are intact (text/embedding), only their parents changed.
        foreach (var l in leavesUnder)
        {
            Assert.Equal(before[l].Text, after[l].Text);
            Assert.Equal(before[l].Embedding, after[l].Embedding);
        }
        // Replaced descendants are gone, created ones exist; the diff lists both.
        foreach (var c in report.Changes.Where(c => c.Index != target))
        {
            if (c.NewText == null) Assert.False(after.ContainsKey(c.Index));
            else Assert.True(after.ContainsKey(c.Index) && after[c.Index].Text == c.NewText && !before.ContainsKey(c.Index));
        }
        Assert.Equal(report.Created, report.Changes.Count(c => c.OldText == null));
        Assert.Equal(report.Replaced, report.Changes.Count(c => c.NewText == null));

        // Persisted through the store like every other mutation: a reload gives the same tree.
        var reloaded = new RaptorIndex(Embedder, new KeywordSummarizer(), TestOptions.Default, store: store);
        Assert.True(await reloaded.LoadAsync());
        Assert.Empty(reloaded.Read(t => t.Validate()));
        var re = Snapshot(reloaded);
        Assert.Equal(after.Keys.Order(), re.Keys.Order());
        foreach (var (i, a) in after)
        {
            Assert.Equal(a.Text, re[i].Text);
            Assert.Equal(a.Children, re[i].Children);
        }
    }

    [Fact]
    public async Task Rebuild_of_a_layer_one_node_resummarizes_it_over_the_same_leaves()
    {
        var (index, _, summarizer) = await BuildAsync(200, 3, 3);
        using var _ = index;
        int target = index.Read(t => t.Layer(1).First().Index);
        var children = index.Read(t => t[target].Children.ToArray());
        int calls = summarizer.Calls;
        var report = await index.RebuildSubtreeAsync(target);
        Assert.Equal(calls + 1, summarizer.Calls);
        Assert.Equal(0, report.Created);
        Assert.Equal(0, report.Replaced);
        Assert.Single(report.Changes);
        Assert.Equal(children, index.Read(t => t[target].Children.ToArray()));
        Assert.Empty(index.Read(t => t.Validate()));
    }

    [Fact]
    public async Task Shared_descendants_keep_their_outside_links()
    {
        var (index, _, _) = await BuildAsync();
        using var _ = index;
        Assert.True(index.NumLayers >= 2);
        // Find (or make) a layer-1 node with two layer-2 parents: link one into a second parent.
        var (target, shared) = index.Read(t =>
        {
            var l2 = t.Layer(2).OrderBy(n => n.Index).ToList();
            var n2 = l2[0];
            var other = l2.First(n => n.Index != n2.Index);
            return (n2.Index, other.Children.First());
        });
        index.Read(t => { t.Link(target, shared); return 0; });
        Assert.Empty(index.Read(t => t.Validate()));
        var before = Snapshot(index);
        var report = await index.RebuildSubtreeAsync(target);
        var after = Snapshot(index);
        Assert.Empty(index.Read(t => t.Validate()));
        Assert.True(after.ContainsKey(shared), "the shared descendant survives");
        Assert.Equal(before[shared].Text, after[shared].Text);
        Assert.Equal(before[shared].Children, after[shared].Children);
        index.Read(t =>
        {
            Assert.DoesNotContain(target, t[shared].Parents);
            Assert.NotEmpty(t[shared].Parents);
            return 0;
        });
        Assert.True(report.Kept >= 1);
        Assert.DoesNotContain(report.Changes, c => c.Index == shared);
    }

    [Fact]
    public async Task Leaf_or_missing_node_is_an_error_and_cancellation_changes_nothing()
    {
        var (index, _, _) = await BuildAsync(200, 3, 3);
        using var _ = index;
        int leaf = index.Read(t => t.Leaves.First().Index);
        await Assert.ThrowsAsync<ArgumentException>(() => index.RebuildSubtreeAsync(leaf));
        await Assert.ThrowsAsync<ArgumentException>(() => index.RebuildSubtreeAsync(999_999));
        Assert.Throws<ArgumentException>(() => index.EstimateSubtree(leaf));

        int target = index.Read(t => t.Roots.First().Index);
        var before = Snapshot(index);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => index.RebuildSubtreeAsync(target, ct: cts.Token));
        var after = Snapshot(index);
        Assert.Equal(before.Keys.Order(), after.Keys.Order());
        Assert.All(before, kv => Assert.Equal(kv.Value.Text, after[kv.Key].Text));
    }

    [Fact]
    public async Task Budget_denied_summaries_become_pending_placeholders()
    {
        var (index, _, summarizer) = await BuildAsync(200, 3, 3);
        using var _ = index;
        int target = index.Read(t => t.Roots.First().Index);
        int calls = summarizer.Calls;
        var report = await index.RebuildSubtreeAsync(target, new RaptorBudget { MaxSummaries = 0 });
        Assert.Equal(calls, summarizer.Calls);
        Assert.Equal(0, report.Summaries);
        Assert.True(report.Placeholders > 0);
        Assert.True(index.Read(t => t[target].SummaryPending));
        Assert.Empty(index.Read(t => t.Validate()));
    }

    [Fact]
    public async Task Replace_leaves_swaps_documents_atomically_and_summarizer_override_is_used()
    {
        var corpus = new SyntheticCorpus(200, 3, 3, seed: 3);
        var index = new RaptorIndex(Embedder, new KeywordSummarizer(), TestOptions.Default);
        using var _ = index;
        await index.BuildAsync(corpus.Documents());
        int leaves = index.LeafCount;
        var docs = corpus.Documents().Take(5).Select(d => d with { Text = d.Text + " replaced" }).ToList();
        var (removed, insert) = await index.ReplaceLeavesAsync(docs);
        Assert.Equal(5, removed);
        Assert.Equal(5, insert.Leaves);
        Assert.Equal(leaves, index.LeafCount);
        Assert.Equal(5, index.Read(t => t.Leaves.Count(l => l.Text.EndsWith("replaced", StringComparison.Ordinal))));
        Assert.Empty(index.Read(t => t.Validate()));

        var other = new KeywordSummarizer();
        var r = await index.RefreshDirtyAsync(null, default, other);
        Assert.True(r.Refreshed > 0);
        Assert.Equal(r.Refreshed, other.Calls);
    }

    private sealed class SyncProgress(Action<(string Stage, int Done, int Total)> on) : IProgress<(string Stage, int Done, int Total)>
    {
        public void Report((string Stage, int Done, int Total) value) => on(value);
    }
}
