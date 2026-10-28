using System.Text.Json;
using MineClaw.Agent.Admin;
using MineClaw.Agent.Llm;
using MineClaw.Agent.Rag;
using MineClaw.Agent.Rag.Beliefs;

namespace MineClaw.Agent.Tests;

/// <summary>Memory admin backend: edits through the stores, re-embedding, audit, read-only refusal, backup, RAPTOR section rebuilds. Offline.</summary>
public sealed class AdminMemoryTests : IDisposable
{
    private static readonly string[] Topics =
    [
        "furnace cobblestone smelting fuel coal", "wheat farm seeds water hoe", "iron ingots pickaxe ore mining",
        "sugar cane paper books bookshelf", "cobblestone generator lava water", "wool sheep dye colours bed",
    ];

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mineclaw-admin-" + Guid.NewGuid().ToString("N"));

    public AdminMemoryTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string DbPath => Path.Combine(_dir, "memory.db");

    private static AgentOptions Options() => new() { KnowledgeDirectory = null, DataDirectory = "unused" };

    private (MemoryAdminContext Ctx, MemoryAdmin Admin, AdminAudit Audit, FakeEmbedder Emb, FakeLlm Llm) Open(bool readOnly = false, FakeEmbedder? emb = null)
    {
        emb ??= new FakeEmbedder();
        var llm = RaptorIntegrationTests.SummaryLlm();
        var ctx = MemoryAdminContext.OpenLocal(DbPath, Options(), emb, llm, readOnly: readOnly);
        var audit = new AdminAudit(Path.Combine(_dir, "admin-audit.jsonl"));
        return (ctx, new MemoryAdmin(ctx, audit, "local:" + DbPath), audit, emb, llm);
    }

    private static MemoryEdit Edit(string op, string kind, string? id, params (string K, string? V)[] fields) =>
        new(op, kind, id, fields.ToDictionary(f => f.K, f => f.V), "PoofImaFox", "test");

    private static List<RagHit> Fts(RagIndex rag, string word) => rag.Search(new RagQuery { Text = word, Kinds = ["note", "player"], Limit = 10 });

    [Fact]
    public async Task Editing_a_fact_reembeds_it_and_search_follows_the_new_text()
    {
        var (ctx, admin, audit, emb, _) = Open();
        using var _ = ctx;
        var add = await admin.ApplyAsync(Edit("add", "fact", null, ("subject", "base"), ("text", "the wheat farm is north of spawn")));
        Assert.True(add.Ok, add.Message);
        Assert.True(add.Reembedded);
        var id = add.Id!;
        Assert.Single(Fts(ctx.Rag, "wheat"));

        int calls = emb.Calls;
        var upd = await admin.ApplyAsync(Edit("update", "fact", id, ("text", "the melon patch is east of the cobble generator")));
        Assert.True(upd.Ok, upd.Message);
        Assert.True(upd.Reembedded);
        Assert.True(emb.Calls > calls, "the changed text was embedded again");
        Assert.Contains("wheat", upd.Before);
        Assert.Contains("melon", upd.After);

        Assert.Empty(Fts(ctx.Rag, "wheat"));
        var hit = Assert.Single(Fts(ctx.Rag, "melon"));
        Assert.Equal("note:" + id, hit.Ref);
        // The vector is current (hash matches the new text) and the in-memory mirror follows.
        Assert.Equal(1, ctx.Db.ScalarLong("SELECT count(*) FROM vectors v JOIN docs d ON d.id = v.doc_id WHERE d.ref = $r AND v.hash = d.hash", ("$r", "note:" + id)));
        Assert.Contains("melon", ctx.Memory.Read(d => d.Notes.Single().Text));

        // Audit: one JSONL line per edit, with before -> after.
        var lines = File.ReadAllLines(audit.Path);
        Assert.Equal(2, lines.Length);
        using (var j = JsonDocument.Parse(lines[1]))
        {
            var e = j.RootElement;
            Assert.Equal("update", e.GetProperty("op").GetString());
            Assert.Equal("fact", e.GetProperty("kind").GetString());
            Assert.True(e.GetProperty("ok").GetBoolean());
            Assert.Contains("wheat", e.GetProperty("before").GetString());
            Assert.Contains("melon", e.GetProperty("after").GetString());
            Assert.Equal("PoofImaFox", e.GetProperty("who").GetString());
        }
        var entries = audit.Read();
        Assert.Equal(2, entries.Count);
        Assert.Equal("local:" + DbPath, entries[1].Target);

        var del = await admin.ApplyAsync(Edit("delete", "fact", id));
        Assert.True(del.Ok, del.Message);
        Assert.Empty(Fts(ctx.Rag, "melon"));
        Assert.Null(del.After);
    }

    [Fact]
    public async Task Failed_edits_are_audited_and_change_nothing()
    {
        var (ctx, admin, audit, _, _) = Open();
        using var _ = ctx;
        var r = await admin.ApplyAsync(Edit("update", "fact", "999", ("text", "x")));
        Assert.False(r.Ok);
        Assert.False((await admin.ApplyAsync(Edit("add", "fact", null, ("subject", "base"), ("bogus", "y")))).Ok);
        Assert.False((await admin.ApplyAsync(Edit("add", "observation", null, ("text", "y")))).Ok);
        Assert.False((await admin.ApplyAsync(Edit("frobnicate", "fact", "1"))).Ok);
        Assert.False((await admin.ApplyAsync(Edit("add", "unicorn", null))).Ok);
        var entries = audit.Read();
        Assert.Equal(5, entries.Count);
        Assert.All(entries, e => Assert.False(e.Ok));
        Assert.Equal(0, ctx.Db.ScalarLong("SELECT count(*) FROM notes"));
    }

    [Fact]
    public async Task A_read_only_db_rejects_edits_and_stays_unchanged()
    {
        string id;
        {
            var (ctx, admin, _, _, _) = Open();
            using var _ = ctx;
            var add = await admin.ApplyAsync(Edit("add", "fact", null, ("subject", "base"), ("text", "the wheat farm is north of spawn")));
            Assert.True(add.Ok, add.Message);
            id = add.Id!;
        }
        var bytes = File.ReadAllBytes(DbPath);
        {
            var (ctx, admin, audit, _, _) = Open(readOnly: true);
            using var _ = ctx;
            Assert.True(ctx.ReadOnly);
            var r = await admin.ApplyAsync(Edit("update", "fact", id, ("text", "the melon patch is east")));
            Assert.False(r.Ok);
            Assert.Contains("read-only", r.Message);
            var e = audit.Read().Last();
            Assert.False(e.Ok);
            Assert.Single(Fts(ctx.Rag, "wheat"));
        }
        Assert.Equal(bytes, File.ReadAllBytes(DbPath));
        using var check = AgentDb.OpenReadOnly(DbPath);
        Assert.Equal("the wheat farm is north of spawn", check.Scalar("SELECT text FROM notes WHERE id = $i", ("$i", long.Parse(id))));
    }

    [Fact]
    public async Task Backup_creates_a_readable_consistent_copy()
    {
        var (ctx, admin, _, _, _) = Open();
        using var _ = ctx;
        Assert.True((await admin.ApplyAsync(Edit("add", "place", null, ("name", "melon patch"), ("x", "10"), ("y", "64"), ("z", "-3"), ("dim", "overworld")))).Ok);
        var path = AdminBackup.Create(ctx.Db, Path.Combine(_dir, "backups"));
        Assert.True(File.Exists(path));
        Assert.StartsWith("memory-backup-", Path.GetFileName(path));
        using var copy = AgentDb.OpenReadOnly(path);
        Assert.Equal(1, copy.ScalarLong("SELECT count(*) FROM waypoints WHERE name = 'melon patch'"));
        Assert.Equal(ctx.Db.ScalarLong("SELECT count(*) FROM docs"), copy.ScalarLong("SELECT count(*) FROM docs"));
        Assert.Equal("ok", copy.Scalar("PRAGMA integrity_check") as string);
    }

    [Fact]
    public async Task Every_kind_edits_through_its_store()
    {
        var (ctx, admin, _, _, _) = Open();
        using var _ = ctx;
        async Task<MemoryEditResult> Ok(MemoryEdit e)
        {
            var r = await admin.ApplyAsync(e);
            Assert.True(r.Ok, $"{e.Op} {e.Kind}: {r.Message}");
            return r;
        }

        // place: add, rename + move, delete
        await Ok(Edit("add", "place", null, ("name", "home"), ("x", "1"), ("y", "64"), ("z", "2"), ("note", "the hut")));
        await Ok(Edit("update", "place", "home", ("name", "old hut"), ("x", "5"), ("note", "")));
        Assert.Null(ctx.Memory.GetWaypoint("home"));
        var w = ctx.Memory.GetWaypoint("old hut")!;
        Assert.Equal(5, w.X);
        Assert.Null(w.Note);
        Assert.Null(ctx.Rag.GetByRef("wp:home"));
        Assert.NotNull(ctx.Rag.GetByRef("wp:old hut"));
        await Ok(Edit("delete", "place", "old hut"));
        Assert.Null(ctx.Rag.GetByRef("wp:old hut"));

        // structure
        await Ok(Edit("add", "structure", null, ("name", "gen"), ("kind", "cobble_generator"), ("steps", "stand at 1,64,1\nmine 2,64,1"), ("min", "0,63,0"), ("max", "3,65,3")));
        var st = await Ok(Edit("update", "structure", "gen", ("confidence", "0.9"), ("note", "works")));
        Assert.Contains("\"confidence\":0.9", st.After);
        Assert.Equal(2, ctx.Memory.GetStructure("gen")!.Steps.Count);
        Assert.Contains("works", ctx.Rag.GetByRef("st:gen")!.Body);

        // claim: add, admin verdict (sticky across recompute), delete
        var c = await Ok(Edit("add", "claim", null, ("claimant", "Robin"), ("text", "there are diamonds in the chest at 3,64,7")));
        var v = await Ok(Edit("update", "claim", c.Id, ("status", "refuted"), ("confidence", "0.1"), ("reason", "checked by hand")));
        Assert.Contains("refuted", v.After);
        var beliefs = ctx.Beliefs;
        var claim = beliefs.Recompute(long.Parse(c.Id!))!;
        Assert.Equal(ClaimStatus.Refuted, claim.Status);
        Assert.Equal("checked by hand", claim.Reason);
        await Ok(Edit("update", "claim", c.Id, ("status", "unverified")));
        Assert.Equal(ClaimStatus.Unverified, beliefs.Get(long.Parse(c.Id!))!.Status);
        await Ok(Edit("delete", "claim", c.Id));
        Assert.Null(beliefs.Get(long.Parse(c.Id!)));
        Assert.Null(ctx.Rag.GetByRef("claim:" + c.Id));

        // trait
        Assert.NotNull(ctx.Self);
        var t = await Ok(Edit("add", "trait", null, ("section", "like"), ("text", "watching the sunrise from the island edge")));
        await Ok(Edit("update", "trait", t.Id, ("text", "watching the sunset from the island edge")));
        Assert.Contains("sunset", ctx.Self!.Entries.Single(e => e.Id == long.Parse(t.Id!)).Text);
        Assert.False((await admin.ApplyAsync(Edit("update", "trait", t.Id, ("section", "dislike")))).Ok);
        await Ok(Edit("delete", "trait", t.Id));
        Assert.DoesNotContain(ctx.Self.Entries, e => e.Id == long.Parse(t.Id!));

        // lesson: add (active in retrieval at once), update, built-in can't be deleted, admin one can
        await Ok(Edit("add", "lesson", null, ("title", "lava buckets"), ("correction", "superseded: lava CAN be picked up with an empty bucket."),
            ("patterns", "lava && (?:can'?t|cannot) && bucket")));
        Assert.Contains(ctx.Rag.Corrections!.Items, x => x.Id == "lava_buckets");
        Assert.NotNull(ctx.Rag.GetByRef("corr:lava_buckets"));
        Assert.NotNull(ctx.Rag.Corrections.Superseding("you cannot pick up lava with a bucket."));
        await Ok(Edit("update", "lesson", "lava_buckets", ("correction", "superseded: an empty bucket picks up lava source blocks.")));
        Assert.Contains("source blocks", ctx.Rag.GetByRef("corr:lava_buckets")!.Body);
        Assert.False((await admin.ApplyAsync(Edit("delete", "lesson", KnowledgeCorrections.SaplingDark.Id))).Ok);
        await Ok(Edit("delete", "lesson", "lava_buckets"));
        Assert.DoesNotContain(ctx.Rag.Corrections!.Items, x => x.Id == "lava_buckets");
        Assert.Null(ctx.Rag.GetByRef("corr:lava_buckets"));
        // The admin lesson survives a restart (startup only prunes system rows) — re-add and re-apply.
        await Ok(Edit("add", "lesson", null, ("title", "lava buckets"), ("correction", "lava can be picked up"), ("patterns", "* lava\\s+is\\s+unpickable")));
        KnowledgeCorrections.Apply(ctx.Rag);
        Assert.Contains(ctx.Rag.Corrections!.Items, x => x.Id == "lava_buckets");

        // observation: by ref and by id; text edit protects it, delete removes doc
        var row = ctx.Observations.Upsert(new Observation("chest", "3,64,7", "chest with 4 iron") { Dimension = 0 });
        var o = await Ok(Edit("update", "observation", row.DocRef, ("text", "chest with 4 iron and 2 gold"), ("annotation", "checked")));
        Assert.Contains("taught", o.After);
        Assert.Contains("gold", ctx.Rag.GetByRef(row.DocRef)!.Body);
        await Ok(Edit("delete", "observation", row.Id.ToString()));
        Assert.Null(ctx.Observations.GetById(row.Id));
        Assert.Null(ctx.Rag.GetByRef(row.DocRef));
    }

    [Fact]
    public void Schema_lists_fields_for_every_kind()
    {
        foreach (var k in MemoryKinds.All) Assert.NotEmpty(MemoryEditSchema.For(k));
        Assert.Empty(MemoryEditSchema.For("nope"));
        Assert.Contains(MemoryEditSchema.For("claim"), f => f.Name == "status" && f.Type == "enum" && f.Options!.Contains("refuted"));
    }

    private static async Task BuildTreeAsync(MemoryAdminContext ctx, int knowledge = 40, int facts = 12)
    {
        for (int i = 0; i < knowledge; i++)
            ctx.Rag.Upsert(new RagDoc($"kb:t.md:{i}", "knowledge", $"note {i}", $"{Topics[i % Topics.Length]} detail {i} item{i % 7}") { Source = "t.md" });
        for (int i = 0; i < facts; i++) ctx.Memory.AddNote("base", $"{Topics[i % Topics.Length]} fact number {i}");
        await ctx.Rag.FlushEmbeddingsAsync();
        await ctx.Raptor!.BuildAsync();
        Assert.True(ctx.Raptor.Index.NumLayers >= 1);
    }

    [Fact]
    public async Task Kind_rebuild_estimates_refreshes_within_the_cap_and_reports_diffs()
    {
        var (ctx, admin, audit, _, llm) = Open();
        using var _ = ctx;
        Assert.NotNull(ctx.Raptor);
        await BuildTreeAsync(ctx);
        var rebuilder = new RaptorSectionRebuilder(ctx, audit, "local:test");

        var req = new RebuildRequest("kind", Kind: "fact", MaxUsd: 0.50m, Who: "Fox");
        var est = rebuilder.Estimate(req);
        Assert.Equal("claude-haiku-5-5", est.Model);
        Assert.True(est.Summaries > 0, est.Note);
        Assert.True(est.InputTokens > 0 && est.OutputTokens > 0 && est.Usd > 0, est.Note);
        Assert.True(est.Usd < 0.50m, $"{est.Usd} {est.Note}");

        // Over the cap: refused, nothing spent.
        int calls = llm.Requests.Count;
        var refused = await rebuilder.RunAsync(req with { MaxUsd = 0.000001m }, null, default);
        Assert.False(refused.Ok);
        Assert.Contains("exceeds", refused.Message);
        Assert.Equal(calls, llm.Requests.Count);

        var progress = new List<RebuildProgress>();
        var result = await rebuilder.RunAsync(req, new SyncProgress<RebuildProgress>(progress.Add), default);
        Assert.True(result.Ok, result.Message);
        Assert.True(llm.Requests.Count > calls, "summaries were refreshed on the cheap tier");
        Assert.All(llm.Requests.Skip(calls), r => Assert.Equal(ModelTier.Cheap, r.Tier));
        Assert.NotEmpty(result.Diffs);
        Assert.All(result.Diffs, d => Assert.True(d.Layer >= 1));
        Assert.True(result.SpentUsd > 0 && result.SpentUsd <= 0.50m);
        Assert.Contains(progress, p => p.Stage == "refresh");
        Assert.Equal(0, ctx.Raptor!.Index.DirtyCount);
        Assert.Empty(ctx.Raptor.Index.Read(t => t.Validate()));
        Assert.Equal(12, ctx.Raptor.Index.Read(t => t.Leaves.Count(l => l.Key!.StartsWith("note:", StringComparison.Ordinal))));
        var e = audit.Read().Last();
        Assert.Equal("rebuild", e.Op);
        Assert.True(e.Ok);
    }

    [Fact]
    public async Task Node_rebuild_rewrites_one_subtree_under_the_cap()
    {
        var (ctx, _, audit, _, llm) = Open();
        using var _ = ctx;
        await BuildTreeAsync(ctx, knowledge: 80);
        var rebuilder = new RaptorSectionRebuilder(ctx, audit, "local:test");
        int root = ctx.Raptor!.Index.Read(t => t.Roots.OrderByDescending(n => n.Children.Count).First().Index);
        var req = new RebuildRequest("node", Node: root, MaxUsd: 0.25m);
        var est = rebuilder.Estimate(req);
        Assert.True(est.Summaries >= 1, est.Note);
        int calls = llm.Requests.Count;
        var r = await rebuilder.RunAsync(req, null, default);
        Assert.True(r.Ok, r.Message);
        Assert.Contains(r.Diffs, d => d.Index == root && d.Old != null && d.New != null);
        Assert.True(llm.Requests.Count > calls);
        Assert.Empty(ctx.Raptor.Index.Read(t => t.Validate()));

        int leaf = ctx.Raptor.Index.Read(t => t.Leaves.First().Index);
        var bad = await rebuilder.RunAsync(new RebuildRequest("node", Node: leaf), null, default);
        Assert.False(bad.Ok);
        Assert.Contains("leaf", bad.Message);
    }

    private sealed class SyncProgress<T>(Action<T> on) : IProgress<T>
    {
        public void Report(T value) => on(value);
    }
}
