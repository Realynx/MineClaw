using MineClaw.Agent.Admin;
using MineClaw.Agent.Admin.Shards;
using MineClaw.Agent.Rag;
using MineClaw.MemoryExplorer.Services;

namespace MineClaw.MemoryExplorer.Tests;

/// <summary>The explorer's shard flows: export from the read-only bank (subtree, person, pack), the shard list and audit history,
/// and an import through the local edit target into another db.</summary>
public sealed class ShardExplorerTests : IAsyncLifetime
{
    private FixtureDb _fx = null!;
    private string Out => Path.Combine(_fx.Dir, "shards");

    public async Task InitializeAsync() => _fx = await FixtureDb.CreateAsync();
    public Task DisposeAsync() { _fx.Dispose(); return Task.CompletedTask; }

    [Fact]
    public async Task A_subtree_exported_from_the_read_only_bank_carries_its_leaves_vectors_and_summaries()
    {
        var bytes = File.ReadAllBytes(_fx.Path);
        using (var bank = await MemoryBank.OpenAsync(_fx.Path, FixtureDb.Config(), embeddings: new HashEmbeddings()))
        {
            Assert.True(bank.HasTree);
            int root = bank.ReadTree(t => t.Roots.OrderByDescending(n => n.Layer).ThenBy(n => n.Index).First().Index, -1);
            var refs = bank.ReadTree(t => ShardCollector.LeafRefsUnder(t, root), []);
            var req = new ShardExportRequest("cobble subtree", new ShardSelection { Mode = "subtree", Node = root, LeafRefs = refs }, "a test", CreatedBy: "admin:test");

            var preview = await ShardService.PreviewAsync(bank, req);
            Assert.Equal(refs.Count, preview.Total);
            Assert.True(preview.Vectors > 0); // one per distinct material hash (digits fold, so similar episodes share one)
            Assert.True(preview.Summaries > 0);
            Assert.True(preview.Bytes > 0);

            var path = Path.Combine(Out, "sub.mcshard");
            var (shard, written) = await ShardService.ExportAsync(bank, req, path);
            Assert.InRange(written, preview.Bytes - 16, preview.Bytes + 16); // only the created timestamp differs
            Assert.Equal("subtree", shard.Manifest.Kind);
            Assert.Equal("fixture.db", shard.Manifest.Source.Db); // a file name, never a full path
            Assert.Equal("fake-hash", shard.Manifest.Embeddings!.Model);

            var listed = Assert.Single(ShardService.ListFiles(Out));
            Assert.True(listed.Ok, listed.Error);
            Assert.Equal("cobble subtree", listed.Name);
            Assert.Equal(refs.Count, listed.Records);
        }
        Assert.Equal(bytes, File.ReadAllBytes(_fx.Path)); // the export never wrote the db
        var e = Assert.Single(new AdminAudit(LocalAdminTarget.AuditPathFor(_fx.Path)).Read(), x => x.Op == "export");
        Assert.Equal("shard", e.Kind);
        Assert.Equal("cobble subtree", e.Id);
    }

    [Fact]
    public async Task A_person_shard_imports_into_another_db_through_the_local_target_with_its_vectors()
    {
        string path = Path.Combine(Out, "fox.mcshard");
        using (var bank = await MemoryBank.OpenAsync(_fx.Path, FixtureDb.Config(), embeddings: new HashEmbeddings()))
        {
            var people = ShardService.People(FixtureDb.Config());
            var fox = Assert.Single(people, p => p.Login.Equals("poofimafox", StringComparison.OrdinalIgnoreCase));
            Assert.Contains("fox", fox.Names);
            await ShardService.ExportAsync(bank, new ShardExportRequest("about fox", new ShardSelection { Mode = "person", People = fox.Names.ToList() }), path);
        }
        var shard = ShardFile.Read(path);
        var only = Assert.Single(shard.Records);
        Assert.Equal(ShardKinds.Fact, only.Kind);

        var target = Path.Combine(_fx.Dir, "empty.db");
        AgentDb.Open(target).Dispose();
        var emb = new HashEmbeddings();
        using (var t = new LocalAdminTarget(target, FixtureDb.Config(), emb))
        {
            var diff = await t.PreviewShardAsync(path, new ShardImportOptions(), CancellationToken.None);
            Assert.Equal(1, diff.Added);
            Assert.Equal(1, diff.ReusableVectors);
            int calls = emb.Calls;
            var res = await t.ImportShardAsync(path, new ShardImportOptions { Who = "admin:test" }, CancellationToken.None);
            Assert.True(res.Ok, res.Message);
            Assert.Equal(1, res.ReusedVectors);
            Assert.Equal(1, t.Context.Db.ScalarLong("SELECT count(*) FROM notes WHERE subject = 'PoofimaFox'"));
            Assert.Contains(await t.AuditAsync(50, CancellationToken.None), a => a.Op == "import" && a.Ok);
            // No summary model configured here: the leaves are in, the rebuild says why it didn't summarize.
            var rb = await t.RunImportRebuildAsync(res, new Progress<RebuildStatus>(), CancellationToken.None);
            if (res.Rebuild.Count > 0) Assert.Contains("no summary model", rb!.Error);
            Assert.True(emb.Calls - calls <= 1); // at most the target's own built-in lesson doc
        }
    }

    [Fact]
    public async Task The_knowledge_pack_of_the_bank_is_reported_and_exported_with_its_version()
    {
        using var bank = await MemoryBank.OpenAsync(_fx.Path, FixtureDb.Config());
        Assert.Equal(KnowledgePack.DefaultVersion, KnowledgePack.ActiveVersion(bank.Db));
        var p = await ShardService.PreviewAsync(bank, new ShardExportRequest("recipes", new ShardSelection { Mode = "pack", IncludeKnowledgeDocs = true }));
        Assert.Equal("b1.8.1", p.PackVersion);
        Assert.Equal(1, p.Counts[ShardKinds.Knowledge]); // the fixture has no game data, one knowledge chunk
        Assert.Throws<ArgumentException>(() => new ShardExporter(bank.Db).Build(new ShardExportRequest("x", new ShardSelection { Mode = "pack", PackVersion = "not a version!" })));
    }
}
