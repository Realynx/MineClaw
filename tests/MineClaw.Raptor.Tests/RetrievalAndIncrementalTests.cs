using MineClaw.Raptor.Model;
using MineClaw.Raptor.Storage;
using Xunit.Abstractions;

namespace MineClaw.Raptor.Tests;

public class RetrievalAndIncrementalTests(ITestOutputHelper output)
{
    private static readonly HashingEmbedder Embedder = new(1024);

    private static async Task<(RaptorIndex Index, SyntheticCorpus Corpus)> BuildAsync(int n, int topics, int subtopics, RaptorOptions? o = null, Func<SyntheticCorpus.Leaf, string?>? kind = null, IRaptorStore? store = null)
    {
        var corpus = new SyntheticCorpus(n, topics, subtopics, seed: n);
        var index = new RaptorIndex(Embedder, new KeywordSummarizer(), o ?? TestOptions.Default, store: store);
        await index.BuildAsync(corpus.Documents(kind));
        return (index, corpus);
    }

    [Fact]
    public async Task Collapsed_and_traversal_recall_on_ground_truth()
    {
        var (index, corpus) = await BuildAsync(1000, 5, 5);
        using var _ = index;
        int collapsedHits = 0, traversalHits = 0, q = 0;
        foreach (var leaf in corpus.Leaves.Where((_, i) => i % 10 == 0))
        {
            q++;
            var c = await index.RetrieveAsync(new RaptorQuery { Text = leaf.Query, Collapsed = true });
            var t = await index.RetrieveAsync(new RaptorQuery { Text = leaf.Query, Collapsed = false });
            if (c.Nodes.Any(n => n.Key == $"leaf:{leaf.Id}")) collapsedHits++;
            if (t.Nodes.Any(n => n.Key == $"leaf:{leaf.Id}")) traversalHits++;
            Assert.True(c.Tokens <= 2000);
        }
        output.WriteLine($"collapsed recall={collapsedHits / (double)q:P0} traversal recall={traversalHits / (double)q:P0}");
        Assert.True(collapsedHits >= 0.95 * q);
        Assert.True(collapsedHits >= traversalHits, "paper: collapsed tree ≥ tree traversal");
    }

    [Fact]
    public async Task Collapsed_stops_at_first_node_over_budget_and_honours_top_k()
    {
        var (index, corpus) = await BuildAsync(300, 3, 3);
        using var _ = index;
        var r = await index.RetrieveAsync(new RaptorQuery { Text = corpus.Leaves[5].Query, MaxTokens = 120 });
        Assert.True(r.Tokens <= 120);
        Assert.True(r.Nodes.Zip(r.Nodes.Skip(1)).All(p => p.First.Score >= p.Second.Score));
        // the node ranked right after the selection must not fit (reference `break`)
        var all = await index.RetrieveAsync(new RaptorQuery { Text = corpus.Leaves[5].Query, MaxTokens = 1_000_000 });
        var next = all.Nodes[r.Nodes.Count];
        Assert.True(r.Tokens + next.Tokens > 120);
        var top3 = await index.RetrieveAsync(new RaptorQuery { Text = corpus.Leaves[5].Query, TopK = 3 });
        Assert.Equal(3, top3.Nodes.Count);
        Assert.Equal(all.Nodes.Take(3).Select(n => n.Index), top3.Nodes.Select(n => n.Index));
    }

    [Fact]
    public async Task Traversal_descends_from_roots_through_children()
    {
        var (index, corpus) = await BuildAsync(500, 4, 4);
        using var _ = index;
        var r = await index.RetrieveAsync(new RaptorQuery { Text = corpus.Leaves[3].Query, Collapsed = false, TopK = 2 });
        int layers = index.NumLayers;
        // top_k per layer, layers = num_layers + 1
        Assert.True(r.Nodes.Count <= 2 * (layers + 1));
        Assert.Equal(layers, r.Nodes[0].Layer);
        index.Read(t =>
        {
            for (int i = 2; i < r.Nodes.Count; i++)
            {
                var node = r.Nodes[i];
                var prevLayer = r.Nodes.Where(p => p.Layer == node.Layer + 1).Select(p => p.Index).ToHashSet();
                Assert.True(t[node.Index].Parents.Overlaps(prevLayer), "every selected node is a child of a node selected one layer up");
            }
            return 0;
        });
        var thr = await index.RetrieveAsync(new RaptorQuery { Text = corpus.Leaves[3].Query, Collapsed = false, Selection = SelectionMode.Threshold, Threshold = 0.3 });
        Assert.All(thr.Nodes, n => Assert.True(n.Score >= 0.3));
    }

    [Fact]
    public async Task Filters_apply_at_leaf_level_in_both_modes()
    {
        var (index, corpus) = await BuildAsync(500, 5, 2, kind: l => l.Id % 10 == 0 ? "waypoint" : "fact");
        using var _ = index;
        var filter = new RaptorFilter { Kinds = ["waypoint"] };
        var target = corpus.Leaves[40];
        var c = await index.RetrieveAsync(new RaptorQuery { Text = target.Query, Filter = filter });
        Assert.Contains(c.Nodes, n => n.Key == "leaf:40");
        Assert.All(c.Nodes.Where(n => n.Leaf), n => Assert.Equal("waypoint", n.Kind));
        index.Read(t =>
        {
            foreach (var n in c.Nodes.Where(n => !n.Leaf))
                Assert.True(HasDescendant(t, n.Index, l => l.Kind == "waypoint"));
            return 0;
        });
        var tr = await index.RetrieveAsync(new RaptorQuery { Text = target.Query, Filter = filter, Collapsed = false, TopK = 10 });
        Assert.All(tr.Nodes.Where(n => n.Leaf), n => Assert.Equal("waypoint", n.Kind));
        // a non-waypoint fact cannot be returned under the filter
        var miss = await index.RetrieveAsync(new RaptorQuery { Text = corpus.Leaves[41].Query, Filter = filter });
        Assert.DoesNotContain(miss.Nodes, n => n.Key == "leaf:41");
        // source filter
        var src = await index.RetrieveAsync(new RaptorQuery { Text = corpus.Leaves[41].Query, Filter = new RaptorFilter { Sources = ["topic1"] } });
        Assert.All(src.Nodes.Where(n => n.Leaf), n => Assert.Equal("topic1", n.Source));
    }

    private static bool HasDescendant(Model.RaptorTree t, int i, Func<RaptorNode, bool> pred)
    {
        var n = t[i];
        return n.IsLeaf ? pred(n) : n.Children.Any(c => HasDescendant(t, c, pred));
    }

    [Fact]
    public async Task Pinned_leaves_come_first_when_requested()
    {
        var (index, corpus) = await BuildAsync(200, 4, 2);
        using var _ = index;
        await index.InsertAsync([new RaptorDocument("base home coordinates are 10 64 -20", Key: "home", Kind: "waypoint", Pinned: true)]);
        var r = await index.RetrieveAsync(new RaptorQuery { Text = corpus.Leaves[9].Query, IncludePinned = true });
        Assert.Equal("home", r.Nodes[0].Key);
        var p = await index.RetrieveAsync(new RaptorQuery { Text = corpus.Leaves[9].Query, Filter = new RaptorFilter { PinnedOnly = true } });
        Assert.All(p.Nodes.Where(n => n.Leaf), n => Assert.True(n.Pinned));
        var none = await index.RetrieveAsync(new RaptorQuery { Text = corpus.Leaves[9].Query });
        Assert.NotEqual("home", none.Nodes[0].Key);
    }

    [Fact]
    public async Task Insert_attaches_under_similar_parents_and_marks_ancestors_dirty()
    {
        var store = new InMemoryRaptorStore();
        var (index, corpus) = await BuildAsync(400, 5, 4, store: store);
        using var _ = index;
        int topic = 2;
        string tw = SyntheticCorpus.W('t', topic), sw = SyntheticCorpus.W('s', topic * 4 + 1);
        // new facts written like existing topic-2 / subtopic-1 leaves (same topic, subtopic and filler words)
        var models = corpus.Leaves.Where(l => l.Topic == topic && l.Subtopic == 1).ToList();
        var docs = Enumerable.Range(0, 12).Select(i =>
        {
            var w = models[i % models.Count].Text.TrimEnd('.').Split(' ');
            return new RaptorDocument($"{tw} {sw} {SyntheticCorpus.W('n', 3 * i)} {SyntheticCorpus.W('n', 3 * i + 1)} {SyntheticCorpus.W('n', 3 * i + 2)} {w[5]} {w[6]}.", Key: $"new:{i}");
        }).ToList();
        var rep = await index.InsertAsync(docs);
        output.WriteLine($"insert: {rep} tau={index.Read(t => t.InsertTemperature):F4}");
        Assert.Equal(12, rep.Leaves);
        Assert.Equal(12, rep.Attached);
        index.Read(t =>
        {
            Assert.Empty(t.Validate());
            foreach (var leaf in t.Leaves.Where(l => l.Key!.StartsWith("new:")))
            {
                Assert.NotEmpty(leaf.Parents);
                Assert.True(leaf.Parents.Count <= index.Options.InsertCandidates);
                // the primary (most similar) parent is an on-topic cluster; extra soft parents are allowed
                int primary = leaf.Parents.MaxBy(pp => MineClaw.Raptor.Numerics.VectorMath.Dot(leaf.Embedding, t[pp].Embedding));
                var old = t[primary].Children.Select(c => t[c]).Where(c => c.Key!.StartsWith("leaf:")).ToList();
                int sameTopic = old.Count(c => corpus.Leaves[int.Parse(c.Key!.Split(':')[1])].Topic == topic);
                Assert.True(sameTopic >= 0.8 * old.Count, $"leaf {leaf.Key} primary parent #{primary} is off-topic: {string.Join(" | ", t.Layer(1).Select(p => (p, s: MineClaw.Raptor.Numerics.VectorMath.Dot(leaf.Embedding, p.Embedding))).OrderByDescending(x => x.s).Take(4).Select(x => $"#{x.p.Index} {x.s:F3} n={x.p.Children.Count} topics={string.Join(",", x.p.Children.Select(c => t[c]).Where(c => c.Key!.StartsWith("leaf:")).Select(c => corpus.Leaves[int.Parse(c.Key!.Split(':')[1])].Topic).GroupBy(z => z).Select(g => $"{g.Key}x{g.Count()}"))} text={x.p.Text[..Math.Min(60, x.p.Text.Length)]}"))}");
                foreach (var p in leaf.Parents)
                {
                    for (var a = new Queue<int>([p]); a.Count > 0;)
                    {
                        var n = t[a.Dequeue()];
                        Assert.True(n.Dirty, "ancestors are dirty");
                        foreach (var pp in n.Parents) a.Enqueue(pp);
                    }
                }
            }
            return 0;
        });
        // new leaves are retrievable immediately, before any refresh
        var hit = await index.RetrieveAsync(new RaptorQuery { Text = $"{tw} {SyntheticCorpus.W('n', 9)} {SyntheticCorpus.W('n', 10)}" });
        Assert.Contains(hit.Nodes, n => n.Key == "new:3");

        int dirty = index.DirtyCount;
        Assert.True(dirty > 0);
        var limited = await index.RefreshDirtyAsync(new RaptorBudget { MaxSummaries = 1 });
        Assert.Equal(1, limited.Refreshed);
        Assert.True(limited.BudgetExhausted);
        Assert.Equal(dirty - 1, index.DirtyCount);
        var full = await index.RefreshDirtyAsync();
        Assert.Equal(0, index.DirtyCount);
        Assert.Empty(index.Read(t => t.Validate()));
        // refreshed layer-1 parents now summarize their new children
        index.Read(t =>
        {
            var parent = t[t.Leaves.First(l => l.Key == "new:0").Parents.First()];
            Assert.Contains(tw, parent.Text);
            return 0;
        });
        // persisted incrementally: the store reloads to the same tree
        var reloaded = RaptorSnapshotMapper.FromSnapshot((await store.LoadAsync(default))!);
        index.Read(t =>
        {
            Assert.Equal(t.Count, reloaded.Count);
            foreach (var n in t.AllNodes.Values)
            {
                Assert.Equal(n.Text, reloaded[n.Index].Text);
                Assert.Equal(n.Children, reloaded[n.Index].Children);
                Assert.Equal(n.Dirty, reloaded[n.Index].Dirty);
            }
            return 0;
        });
    }

    [Fact]
    public async Task Rebuild_trigger_fires_on_change_fraction_and_drift()
    {
        var (index, corpus) = await BuildAsync(200, 4, 2);
        using var _ = index;
        Assert.False(index.ShouldRebuild().Rebuild);
        // on-topic inserts: 20 / 200 = 10% → no rebuild
        await index.InsertAsync(Enumerable.Range(0, 20).Select(i => new RaptorDocument(corpus.Leaves[i].Text + " again", Key: $"dup:{i}")));
        var d1 = index.ShouldRebuild();
        output.WriteLine($"after 10%: {d1}");
        Assert.False(d1.Rebuild);
        // 40 more → 30% > 25%
        await index.InsertAsync(Enumerable.Range(20, 40).Select(i => new RaptorDocument(corpus.Leaves[i].Text + " again", Key: $"dup:{i}")));
        var d2 = index.ShouldRebuild();
        Assert.True(d2.Rebuild);
        Assert.Contains("changed fraction", d2.Reason);

        var stats = await index.RebuildAsync();
        output.WriteLine($"rebuild: summaries={stats.Summaries} reused={stats.ReusedSummaries} layers=[{string.Join(",", stats.NodesPerLayer)}]");
        Assert.Equal(260, index.LeafCount);
        Assert.False(index.ShouldRebuild().Rebuild);
        Assert.Empty(index.Read(t => t.Validate()));
        Assert.Equal(0, index.Read(t => t.InsertedSinceBuild));

        // drift: off-topic inserts (new vocabulary) sit far from every parent
        var (index2, _) = await BuildAsync(300, 3, 3);
        using var __ = index2;
        await index2.InsertAsync(Enumerable.Range(0, 20).Select(i => new RaptorDocument($"{SyntheticCorpus.W('z', 5 * i)} {SyntheticCorpus.W('z', 5 * i + 1)} {SyntheticCorpus.W('z', 5 * i + 2)} {SyntheticCorpus.W('z', 5 * i + 3)}", Key: $"odd:{i}")));
        var d3 = index2.ShouldRebuild();
        output.WriteLine($"drift: {d3}");
        Assert.True(d3.Rebuild);
        Assert.Contains("drift", d3.Reason);
    }

    [Fact]
    public async Task Bootstrap_empty_index_by_inserting_then_rebuilding()
    {
        using var index = new RaptorIndex(Embedder, new KeywordSummarizer(), TestOptions.Default);
        var corpus = new SyntheticCorpus(60, 3, 2);
        await index.InsertAsync(corpus.Documents().Take(10));
        Assert.False(index.ShouldRebuild().Rebuild); // 10 ≤ reduction_dimension + 1
        await index.InsertAsync(corpus.Documents().Skip(10));
        Assert.True(index.ShouldRebuild().Rebuild);
        await index.RebuildAsync();
        Assert.True(index.NumLayers >= 1);
        Assert.Empty(index.Read(t => t.Validate()));
    }

    [Fact]
    public async Task Remove_prunes_leaves_and_empty_summaries()
    {
        var (index, corpus) = await BuildAsync(200, 4, 2);
        using var _ = index;
        int before = index.NodeCount;
        int removed = await index.RemoveAsync(corpus.Leaves.Where(l => l.Topic == 1).Select(l => $"leaf:{l.Id}"));
        Assert.Equal(50, removed);
        Assert.Equal(150, index.LeafCount);
        Assert.Empty(index.Read(t => t.Validate()));
        output.WriteLine($"before={before} after={index.NodeCount} dirty={index.DirtyCount}");
        Assert.True(index.NodeCount < before - 50, "summaries that only covered topic 1 were pruned");
        var r = await index.RetrieveAsync(new RaptorQuery { Text = corpus.Leaves[1].Query });
        Assert.DoesNotContain(r.Nodes, n => n.Key == "leaf:1");
        // removing part of a cluster leaves its ancestors dirty
        Assert.Equal(3, await index.RemoveAsync(["leaf:2", "leaf:6", "leaf:10"]));
        Assert.True(index.DirtyCount > 0);
        Assert.Empty(index.Read(t => t.Validate()));
    }

    [Fact]
    public async Task Refresh_splits_parent_that_grew_over_the_token_cap()
    {
        var o = TestOptions.Default with { MaxLengthInCluster = 160 };
        var (index, corpus) = await BuildAsync(300, 3, 3, o);
        using var _ = index;
        string tw = SyntheticCorpus.W('t', 0), sw = SyntheticCorpus.W('s', 0);
        await index.InsertAsync(Enumerable.Range(0, 40).Select(i => new RaptorDocument($"{tw} {sw} {SyntheticCorpus.W('g', 2 * i)} {SyntheticCorpus.W('g', 2 * i + 1)} {SyntheticCorpus.W('w', i % 40)}.", Key: $"grow:{i}")));
        var r = await index.RefreshDirtyAsync();
        output.WriteLine($"refresh: {r}");
        Assert.True(r.Splits > 0);
        Assert.Equal(0, index.DirtyCount);
        index.Read(t =>
        {
            Assert.Empty(t.Validate());
            foreach (var n in t.Layer(1).Where(n => n.Children.Count > 1))
                Assert.True(n.Children.Sum(c => t[c].TokenCount) <= 160, $"#{n.Index} still over cap");
            return 0;
        });
    }
}

