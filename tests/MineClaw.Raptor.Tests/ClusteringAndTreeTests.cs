using MineClaw.Raptor.Clustering;
using MineClaw.Raptor.Model;
using MineClaw.Raptor.Storage;
using MineClaw.Raptor.Text;
using Xunit.Abstractions;

namespace MineClaw.Raptor.Tests;

public class ClusteringAndTreeTests(ITestOutputHelper output)
{
    private static readonly HashingEmbedder Embedder = new(256);

    [Fact]
    public void Two_stage_clustering_finds_topics_then_subtopics()
    {
        // 4 topics × 4 subtopics × 20 leaves; topic words dominate, subtopic words separate locally
        var corpus = new SyntheticCorpus(320, 4, 4, seed: 3);
        var emb = corpus.Leaves.Select(l => Embedder.Embed(l.Text)).ToList();
        var rc = new RaptorClustering(TestOptions.Default);
        var labels = rc.PerformClustering(emb, 10, 0.1);
        output.WriteLine($"global={rc.LastGlobalClusters} total={rc.LastTotalClusters}");
        Assert.InRange(rc.LastGlobalClusters, 4, 16);
        Assert.True(rc.LastTotalClusters > rc.LastGlobalClusters, "local stage must split global clusters");
        Assert.All(labels, l => Assert.NotEmpty(l));
        // each cluster is topic-pure
        foreach (var c in labels.SelectMany(l => l).Distinct())
        {
            var members = Enumerable.Range(0, labels.Length).Where(i => labels[i].Contains(c)).ToList();
            var topics = members.GroupBy(i => corpus.Leaves[i].Topic).OrderByDescending(g => g.Count()).ToList();
            Assert.True(topics[0].Count() >= 0.9 * members.Count, $"cluster {c} impure");
        }
    }

    [Fact]
    public void Recursive_reclustering_respects_token_cap()
    {
        var corpus = new SyntheticCorpus(240, 3, 2, seed: 4);
        var emb = corpus.Leaves.Select(l => Embedder.Embed(l.Text)).ToList();
        var tokens = corpus.Leaves.Select(l => ApproxTokenCounter.Instance.Count(l.Text)).ToList();
        var opts = TestOptions.Default with { MaxLengthInCluster = 24 };
        var rc = new RaptorClustering(opts);
        var groups = rc.Cluster(emb, tokens);
        output.WriteLine($"groups={groups.Count} reclusters={rc.ReclusterCount} fallback={rc.FallbackSplits} leafTokens={tokens.Average():F1}");
        Assert.True(rc.ReclusterCount > 0);
        foreach (var g in groups.Where(g => g.Length > 1)) Assert.True(g.Sum(i => tokens[i]) <= 24, $"group of {g.Length} over cap");
        Assert.Equal(240, groups.SelectMany(g => g).Distinct().Count()); // every node kept
    }

    [Fact]
    public async Task Tree_layers_terminate_and_invariants_hold()
    {
        var corpus = new SyntheticCorpus(600, 6, 4, seed: 5);
        var builder = new RaptorTreeBuilder(Embedder, new KeywordSummarizer(), TestOptions.Default);
        var (tree, stats) = await builder.BuildAsync(corpus.Documents());
        output.WriteLine($"layers={stats.NumLayers} perLayer=[{string.Join(",", stats.NodesPerLayer)}] summaries={stats.Summaries} reclusters={stats.Reclusters} cluster={stats.ClusteringTime.TotalSeconds:F1}s total={stats.Elapsed.TotalSeconds:F1}s");
        Assert.Empty(tree.Validate());
        Assert.InRange(tree.NumLayers, 1, 5);
        Assert.True(tree.NumLayers == 5 || tree.LayerIndices(tree.NumLayers).Count <= 11 || tree.NumLayers == builder.Options.NumLayers);
        // indices follow the reference: leaves 0..n-1 then summaries in creation order
        Assert.Equal(Enumerable.Range(0, 600), tree.Leaves.Select(l => l.Index).Order());
        for (int l = 1; l <= tree.NumLayers; l++)
        {
            Assert.True(tree.LayerIndices(l).Min() > tree.LayerIndices(l - 1).Max());
            Assert.True(tree.LayerIndices(l).Count < tree.LayerIndices(l - 1).Count);
        }
        foreach (var n in tree.AllNodes.Values.Where(n => n.Layer < tree.NumLayers)) Assert.NotEmpty(n.Parents);
        Assert.Equal(stats.Summaries, tree.Count - 600);
        Assert.True(tree.BaselineLeafParentSimilarity > 0.2);
    }

    [Fact]
    public async Task Small_corpus_has_no_summary_layers()
    {
        var builder = new RaptorTreeBuilder(Embedder, new KeywordSummarizer(), TestOptions.Default);
        var (tree, stats) = await builder.BuildAsync(new SyntheticCorpus(11, 2, 2).Documents());
        Assert.Equal(0, tree.NumLayers);
        Assert.Equal(11, tree.Roots.Count());
        Assert.Equal(0, stats.Summaries);
    }

    [Fact]
    public async Task Summary_prompt_is_the_papers_verbatim()
    {
        var summarizer = new KeywordSummarizer();
        var builder = new RaptorTreeBuilder(Embedder, summarizer, TestOptions.Default);
        await builder.BuildAsync(new SyntheticCorpus(60, 3, 2).Documents());
        var r = summarizer.Requests.First();
        Assert.Equal("You are a Summarizing Text Portal", r.SystemPrompt);
        Assert.StartsWith("Write a summary of the following, including as many key details as possible: ", r.UserPrompt);
        Assert.EndsWith(":", r.UserPrompt);
        Assert.Equal(100, r.MaxTokens);
        Assert.Contains("\n\n", r.Context); // utils.get_text joins children with blank lines
    }

    [Fact]
    public async Task Build_is_deterministic_with_fixed_seed()
    {
        var corpus = new SyntheticCorpus(400, 5, 3, seed: 8);
        var a = (await new RaptorTreeBuilder(Embedder, new KeywordSummarizer(), TestOptions.Default).BuildAsync(corpus.Documents())).Tree;
        var b = (await new RaptorTreeBuilder(Embedder, new KeywordSummarizer(), TestOptions.Default with { MaxDegreeOfParallelism = 1 }).BuildAsync(corpus.Documents())).Tree;
        Assert.Equal(a.Count, b.Count);
        foreach (var n in a.AllNodes.Values)
        {
            var m = b[n.Index];
            Assert.Equal(n.Text, m.Text);
            Assert.Equal(n.Children, m.Children);
            Assert.Equal(n.Embedding, m.Embedding);
        }
    }

    [Fact]
    public async Task Budget_cap_leaves_placeholders_that_refresh_fills()
    {
        var corpus = new SyntheticCorpus(300, 4, 3, seed: 9);
        var summarizer = new KeywordSummarizer();
        using var index = new RaptorIndex(Embedder, summarizer, TestOptions.Default);
        var budget = new RaptorBudget { MaxSummaries = 3 };
        var stats = await index.BuildAsync(corpus.Documents(), budget);
        Assert.Equal(3, stats.Summaries);
        Assert.True(stats.Placeholders > 0);
        Assert.Equal(3, summarizer.Calls);
        Assert.Equal(stats.Placeholders, index.DirtyCount);
        Assert.Empty(index.Read(t => t.Validate()));
        var r = await index.RefreshDirtyAsync();
        Assert.Equal(stats.Placeholders, r.Refreshed);
        Assert.Equal(0, index.DirtyCount);
    }

    [Fact]
    public async Task File_store_round_trips_tree()
    {
        var path = Path.Combine(Path.GetTempPath(), $"raptor-{Guid.NewGuid():N}.bin");
        try
        {
            var corpus = new SyntheticCorpus(200, 4, 2, seed: 10);
            using (var index = new RaptorIndex(Embedder, new KeywordSummarizer(), TestOptions.Default, store: new FileRaptorStore(path)))
            {
                await index.BuildAsync(corpus.Documents(m => m.Id % 10 == 0 ? "waypoint" : "fact"));
                await index.InsertAsync([new RaptorDocument("tb sb extra fact zzq", Key: "new", Pinned: true)]);
            }
            using var loaded = new RaptorIndex(Embedder, new KeywordSummarizer(), TestOptions.Default, store: new FileRaptorStore(path));
            Assert.True(await loaded.LoadAsync());
            Assert.Equal(201, loaded.LeafCount);
            Assert.Empty(loaded.Read(t => t.Validate()));
            var pinned = loaded.Read(t => t.Leaves.Single(l => l.Key == "new"));
            Assert.True(pinned.Pinned);
            Assert.True(pinned.InsertedSeq > 0);
            Assert.Equal(1, loaded.Read(t => t.InsertedSinceBuild));
            var hit = await loaded.RetrieveAsync(new RaptorQuery { Text = corpus.Leaves[7].Query });
            Assert.Contains(hit.Nodes, n => n.Key == "leaf:7");
            // a different embedding model is refused
            using var other = new RaptorIndex(new HashingEmbedder(128), new KeywordSummarizer(), TestOptions.Default, store: new FileRaptorStore(path));
            Assert.False(await other.LoadAsync());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Split_text_reference_and_preserving_modes()
    {
        var tok = ApproxTokenCounter.Instance;
        var text = "Base is at 12.5 64 -3. Iron farm is east! Is it lit? yes\nnext line";
        var reference = TextSplitter.Split(text, tok, 100, 0, SplitMode.Reference);
        Assert.Single(reference);
        Assert.DoesNotContain(".", reference[0].Replace("12.5", "")); // reference strips delimiters…
        Assert.Contains("12 5", reference[0]);                        // …including the decimal point
        var keep = TextSplitter.Split(text, tok, 100, 0, SplitMode.PreservePunctuation);
        Assert.Single(keep);
        Assert.Contains("12.5 64 -3.", keep[0]);
        Assert.Contains("east!", keep[0]);
        // packing: sentences never split across chunks, each chunk ≤ max tokens
        var many = string.Join(" ", Enumerable.Range(0, 60).Select(i => $"Sentence number {i} talks about cobblestone generators."));
        var chunks = TextSplitter.Split(many, tok, 100, 0, SplitMode.PreservePunctuation);
        Assert.True(chunks.Count > 1);
        Assert.All(chunks, c => Assert.True(tok.Count(c) <= 100 + chunks.Count));
        Assert.All(chunks, c => Assert.EndsWith(".", c));
    }
}
