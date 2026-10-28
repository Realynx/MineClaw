using System.Security.Cryptography;
using MineClaw.MemoryExplorer.Services;
using MineClaw.MemoryExplorer.ViewModels;
using Microsoft.Data.Sqlite;

namespace MineClaw.MemoryExplorer.Tests;

public sealed class MemoryBankTests : IAsyncLifetime
{
    private FixtureDb _fx = null!;

    public async Task InitializeAsync() => _fx = await FixtureDb.CreateAsync();
    public Task DisposeAsync() { _fx.Dispose(); return Task.CompletedTask; }

    [Fact]
    public async Task Loads_the_stored_raptor_tree_read_only()
    {
        Assert.True(_fx.TreeLayers >= 1, "fixture should have summary layers");
        using var bank = await MemoryBank.OpenAsync(_fx.Path, FixtureDb.Config());
        Assert.True(bank.HasTree);
        Assert.True(bank.Db.ReadOnly);
        Assert.Equal(_fx.TreeNodes, bank.Raptor!.Index.NodeCount);
        Assert.Equal(_fx.TreeLayers, bank.Raptor.Index.NumLayers);
        Assert.Empty(bank.ReadTree(t => t.Validate(), ["no tree"]));

        // Every leaf maps back to its doc; a leaf's path ends at it and starts at a root.
        var leaf = bank.ReadTree(t => t.Leaves.First(), null!);
        Assert.NotNull(bank.DocOf(leaf.Index));
        var path = bank.PathToRoot(leaf.Index);
        Assert.Equal(leaf.Index, path[^1]);
        Assert.Equal(_fx.TreeLayers, bank.ReadTree(t => t[path[0]].Layer, -1));

        var stats = bank.ReadTree(t => RaptorStats.From(t, bank.TreeMeta), null!);
        Assert.Equal(_fx.TreeLayers + 1, stats.Layers.Count);
        Assert.Contains(stats.Layers, l => l.Layer == 0 && l.Nodes == bank.Raptor.Index.LeafCount);
    }

    [Fact]
    public async Task Graph_layout_places_every_node()
    {
        using var bank = await MemoryBank.OpenAsync(_fx.Path, FixtureDb.Config());
        var g = bank.ReadTree(t => Controls.GraphModel.Build(t, bank.DocOf), null!);
        Assert.Equal(_fx.TreeNodes, g.Nodes.Length);
        Assert.All(g.Nodes, n => Assert.InRange(g.Layout.X0[n.Slot], 0, 1));
        Assert.All(g.Nodes, n => Assert.InRange(g.Layout.X1[n.Slot], g.Layout.X0[n.Slot], 1 + 1e-9));
        Assert.Contains(g.Nodes, n => n.Children.Length > 0);
        Controls.TopicLabeler.Label(g);
        Assert.Contains(g.Nodes, n => !n.IsLeaf && n.Terms.Length >= 2);
    }

    [Fact]
    public async Task Search_falls_back_to_lexical_without_query_embeddings()
    {
        using var bank = await MemoryBank.OpenAsync(_fx.Path, FixtureDb.Config());
        var svc = new SearchService(bank);
        Assert.Equal(SearchMode.Lexical, svc.Mode);
        Assert.Contains("lexical fallback", svc.ModeNote);
        var r = await svc.SearchAsync("melon farm water");
        Assert.Equal(SearchMode.Lexical, r.Mode);
        Assert.False(r.QueryEmbedded);
        Assert.Empty(r.TreeHits);
        Assert.Contains(r.Hits, h => h.Hit.Ref == "note:2");
    }

    [Fact]
    public async Task Search_runs_the_hybrid_pipeline_when_query_embedding_matches_the_stored_vectors()
    {
        var fake = new HashEmbeddings();
        using var bank = await MemoryBank.OpenAsync(_fx.Path, FixtureDb.Config(), embeddings: fake);
        var svc = new SearchService(bank);
        Assert.Equal(SearchMode.Hybrid, svc.Mode);
        var r = await svc.SearchAsync("nether portal obsidian");
        Assert.True(r.QueryEmbedded);
        Assert.Equal(SearchMode.Hybrid, r.Mode);
        Assert.NotEmpty(r.TreeHits);
        Assert.Contains(r.Hits, h => h.Hit.Body.Contains("obsidian"));
        Assert.Contains(r.Hits, h => h.TreePath.Count > 1); // hits inside the tree carry their path from the root
    }

    [Fact]
    public async Task Search_falls_back_when_the_stored_vectors_were_made_by_another_model()
    {
        var other = new OtherModel();
        using var bank = await MemoryBank.OpenAsync(_fx.Path, FixtureDb.Config(), embeddings: other);
        var svc = new SearchService(bank);
        Assert.Equal(SearchMode.Lexical, svc.Mode);
        Assert.Contains("lexical fallback", svc.ModeNote);
        Assert.Equal(0, other.Calls);
    }

    private sealed class OtherModel : MineClaw.Agent.Rag.IEmbeddingProvider
    {
        public int Calls;
        public string Model => "another-model";
        public int Dimensions => 16;
        public bool IsAvailable => true;
        public Task<MineClaw.Agent.Rag.EmbeddingResponse> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new MineClaw.Agent.Rag.EmbeddingResponse(inputs.Select(_ => new float[16]).ToList(), 0));
        }
    }

    [Fact]
    public async Task Read_only_open_rejects_writes_and_leaves_the_file_untouched()
    {
        var before = Hash(_fx.Path);
        using (var bank = await MemoryBank.OpenAsync(_fx.Path, FixtureDb.Config()))
        {
            Assert.ThrowsAny<SqliteException>(() => bank.Db.Exec("INSERT INTO notes(id, subject, text, created) VALUES (99, 'x', 'y', 0)"));
            Assert.ThrowsAny<SqliteException>(() => bank.Db.Exec("UPDATE docs SET title = 'changed'"));
            Assert.ThrowsAny<SqliteException>(() => bank.Db.SetMeta("raptor_meta", null));
            Assert.ThrowsAny<SqliteException>(() => bank.Db.Exec("PRAGMA user_version = 99"));
            // Reads keep working after the refused writes.
            Assert.True(bank.Db.ScalarLong("SELECT count(*) FROM notes") >= 2);
            await new SearchService(bank).SearchAsync("cobblestone");
        }
        Assert.Equal(before, Hash(_fx.Path));
    }

    [Fact]
    public async Task Memory_queries_list_each_kind()
    {
        using var bank = await MemoryBank.OpenAsync(_fx.Path, FixtureDb.Config());
        var q = new MemoryQueries(bank.Db);
        var facts = q.Rows("facts");
        Assert.Equal(2, facts.Count);
        Assert.Contains(facts, f => f.Name == "PoofimaFox" && f.DocRef == "note:1");
        var places = q.Rows("places");
        Assert.Contains(places, p => p.Name == "nether hub" && p.Dimension == -1);
        Assert.Single(q.Rows("structures"));
        Assert.Equal(42, q.Rows("episodes").Count);
        Assert.Empty(q.Rows("claims"));
        Assert.Contains(q.Counts(), c => c.Label == "Facts" && c.Count == 2);
        Assert.All(MemoryQueries.Kinds, k => q.Rows(k.Key)); // no kind throws on this schema
    }

    private static string Hash(string path)
    {
        using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return Convert.ToHexString(SHA256.HashData(s));
    }
}
