using System.Text.Json;
using MineClaw.Agent.Budget;
using MineClaw.Agent.Context;
using MineClaw.Agent.Events;
using MineClaw.Agent.Llm;
using MineClaw.Agent.Memory;
using MineClaw.Agent.Rag;
using MineClaw.Agent.Runtime;
using MineClaw.Agent.Tools;

namespace MineClaw.Agent.Tests;

public class RagTests
{
    private static JsonElement J(object o) => JsonSerializer.SerializeToElement(o);

    private static (AgentDb Db, RagIndex Index) Open(IEmbeddingProvider? emb = null, RagOptions? o = null, string? path = null,
        bool noFts = false, bool noRtree = false, UsageTracker? usage = null)
    {
        var db = AgentDb.Open(path, disableFts: noFts, disableRtree: noRtree);
        return (db, new RagIndex(db, o ?? new RagOptions(), emb, usage, backgroundEmbedding: false));
    }

    [Fact]
    public void Db_HasFtsAndRtree_WalAndSchemaVersion()
    {
        using var dir = new TempDir();
        var (db, idx) = Open(path: dir.File("x.db"));
        using (db) using (idx)
        {
            Assert.True(db.HasFts);
            Assert.True(db.HasRtree);
            Assert.Equal(AgentDb.LatestSchemaVersion, db.SchemaVersion);
            Assert.Equal("wal", db.Scalar("PRAGMA journal_mode;") as string);
        }
        using var again = AgentDb.Open(dir.File("x.db")); // reopen: no re-migration
        Assert.Equal(AgentDb.LatestSchemaVersion, (int)again.ScalarLong("PRAGMA user_version;"));
    }

    [Fact]
    public async Task Hybrid_FusesLexicalAndSemantic_WithRrf()
    {
        var emb = new FakeEmbedder();
        var (db, idx) = Open(emb);
        using var _ = db;
        idx.Upsert(new RagDoc("a", "note", "ingots", "iron ingots stacked by the furnace"));     // lexical + semantic
        idx.Upsert(new RagDoc("b", "note", "storage", "metal storage chest holds metal"));      // semantic only
        idx.Upsert(new RagDoc("c", "note", "trees", "birch tree farm on the east side"));       // neither
        idx.Upsert(new RagDoc("d", "note", "ingots", "ingots ingots ingots wanted by bob"));    // lexical only-ish
        Assert.Equal(4, await idx.FlushEmbeddingsAsync());

        var hits = await idx.SearchAsync(new RagQuery { Text = "ingots", Limit = 5 });
        var refs = hits.Select(h => h.Ref).ToList();
        Assert.Contains("b", refs);                     // found only via vectors
        Assert.True(refs.IndexOf("a") < refs.IndexOf("b") && refs.IndexOf("d") < refs.IndexOf("b")); // in both lists beats one list (RRF)
        Assert.DoesNotContain("c", refs);
        var b = hits.Single(h => h.Ref == "b");
        Assert.Equal(0, b.Lexical);
        Assert.True(b.Cosine > 0.5);

        // Same query without a vector: FTS-only, the semantic-only doc disappears.
        var lexical = idx.Search(new RagQuery { Text = "ingots", Limit = 5 });
        Assert.DoesNotContain(lexical, h => h.Ref == "b");
        Assert.Contains(lexical, h => h.Ref == "a");
    }

    [Fact]
    public async Task FtsOnly_WhenUnconfigured_OrProviderFailing()
    {
        var (db, idx) = Open(NullEmbeddingProvider.Instance);
        using var _ = db;
        idx.Upsert(new RagDoc("a", "note", "gen", "cobble generator by the tree"));
        Assert.Equal(0, await idx.FlushEmbeddingsAsync());
        Assert.Single(await idx.SearchAsync(new RagQuery { Text = "cobble generator" }));

        var failing = new FakeEmbedder { Fail = true };
        var (db2, idx2) = Open(failing);
        using var __ = db2;
        idx2.Upsert(new RagDoc("a", "note", "gen", "cobble generator by the tree"));
        Assert.Equal(0, await idx2.FlushEmbeddingsAsync());          // fails, backs off, never throws
        Assert.False(idx2.VectorsAvailable);
        Assert.Equal(0, await idx2.FlushEmbeddingsAsync());          // in backoff: no new attempt
        Assert.Equal(1, failing.Calls);
        Assert.Single(await idx2.SearchAsync(new RagQuery { Text = "generator" }));
    }

    [Fact]
    public void NoFts5_FallsBackToLikeScan()
    {
        using var m = new MemoryStore(AgentDb.Open(null, disableFts: true, disableRtree: true), null);
        Assert.False(m.Db.HasFts);
        for (int i = 0; i < 20; i++) m.AddNote("world", $"fact {i} about trees");
        m.SetWaypoint("tree farm", 10, 64, 10);
        Assert.Matches(@"^#\d+ wp tree farm", m.Recall("tree farm", 3));
        var near = m.Index.Nearby(12, 64, 10, 5);                     // range-scan spatial fallback
        Assert.Equal("wp:tree farm", Assert.Single(near).Ref);
    }

    [Fact]
    public void Spatial_NearbyWithinRadius_StructureBoxes()
    {
        using var m = new MemoryStore(null);
        m.SetWaypoint("home", 0, 64, 0);
        m.SetWaypoint("far", 100, 64, 100);
        m.SetWaypoint("close", 5, 64, 0);
        m.UpsertStructure("gen", s => { s.Min = new BlockPos(-3, 63, -3); s.Max = new BlockPos(-1, 65, -1); });
        var hits = m.Index.Nearby(0, 64, 0, 8);
        Assert.Equal(["wp:home", "st:gen", "wp:close"], hits.OrderBy(h => h.Distance).Select(h => h.Ref).ToList());
        Assert.Equal(0, hits.Single(h => h.Ref == "wp:home").Distance);
        Assert.Equal(Math.Sqrt(2), hits.Single(h => h.Ref == "st:gen").Distance!.Value, 3); // distance to the box, not its corner
        Assert.DoesNotContain(hits, h => h.Ref == "wp:far");
    }

    [Fact]
    public async Task RecallTool_KindNearChat_AndDetailPaging()
    {
        var (rt, _, _) = TestRuntime.Create(position: () => new BlockPos(0, 64, 0));
        using var _r = rt;
        rt.Memory.SetWaypoint("home", 1, 64, 1, "the island center");
        rt.Memory.AddNote("Alice", "likes birch wood");
        rt.Rag.LogChat(DateTimeOffset.UtcNow, "chat", "Bob", "the melon farm is broken again");
        rt.Rag.Upsert(new RagDoc("kb:test:0", "knowledge", "Long > Section", string.Join(" ", Enumerable.Repeat("cactus grows only on sand.", 40))));

        var near = await rt.Tools.ExecuteAsync("recall", J(new { near = "here" }), default);
        Assert.Contains("wp home 1,64,1", near.Text);
        var players = await rt.Tools.ExecuteAsync("recall", J(new { query = "birch", kind = "player" }), default);
        Assert.Contains("likes birch", players.Text);
        var chat = await rt.Tools.ExecuteAsync("recall", J(new { query = "melon", kind = "chat" }), default);
        Assert.Matches(@"^c\d+ .* Bob: the melon farm", chat.Text);
        Assert.False((await rt.Tools.ExecuteAsync("recall", J(new { }), default)).Ok);

        var kb = await rt.Tools.ExecuteAsync("recall", J(new { query = "cactus sand", kind = "knowledge" }), default);
        var id = System.Text.RegularExpressions.Regex.Match(kb.Text, @"#(\d+) kb").Groups[1].Value;
        var p1 = await rt.Tools.ExecuteAsync("recall_detail", J(new { id = "#" + id }), default);
        Assert.Contains("(page 1/", p1.Text);
        Assert.True(p1.Text.Length <= rt.Options.MaxToolResultChars);
        var p2 = await rt.Tools.ExecuteAsync("recall_detail", J(new { id, page = 2 }), default);
        Assert.Contains("(page 2/", p2.Text);
        var c = System.Text.RegularExpressions.Regex.Match(chat.Text, @"^(c\d+)").Groups[1].Value;
        Assert.Contains("melon farm", (await rt.Tools.ExecuteAsync("recall_detail", J(new { id = c }), default)).Text);
    }

    [Fact]
    public void Migration_FromMemoryJson_ImportsOnce()
    {
        using var dir = new TempDir();
        var json = dir.File("memory.json");
        var legacy = new MemoryData
        {
            Waypoints = [new Waypoint { Name = "spawn", X = 0, Y = 64, Z = 0 }],
            Structures = [new Structure { Name = "gen1", Kind = StructureKinds.CobbleGenerator, Steps = ["mine at 4,65,-2"], TaughtBy = "Alice", Confidence = 0.9 }],
            Notes = [new Note { Id = 7, Subject = "Bob", Text = "hates spiders" }],
            Routines = [new Routine { Name = "loop", Steps = [new RoutineStep { Tool = "mine", Args = J(new { block = "cobblestone" }) }] }],
            Identity = new BotIdentity { DisplayName = "Mochi", Aliases = ["mo"] },
            NextNoteId = 8,
        };
        AtomicFile.WriteJson(json, legacy);

        using (var db = AgentDb.Open(dir.File("mineclaw.db")))
        using (var m = new MemoryStore(db, null, null, json))
        {
            Assert.Equal("Mochi", m.Identity!.DisplayName);
            Assert.Equal(0.9, m.GetStructure("GEN1")!.Confidence);
            Assert.Equal("cobblestone", m.GetRoutine("loop")!.Steps[0].Args.GetProperty("block").GetString());
            Assert.Contains("hates spiders", m.Recall("spiders"));
            Assert.Equal(8, m.AddNote("Bob", "likes cats").Id);
        }
        Assert.False(File.Exists(json));
        Assert.True(File.Exists(json + ".imported"));

        File.Copy(json + ".imported", json);                          // even if the file reappears...
        using var db2 = AgentDb.Open(dir.File("mineclaw.db"));
        using var m2 = new MemoryStore(db2, null, null, json);
        Assert.Equal(2, m2.Read(d => d.Notes.Count));                // ...it's not imported twice
        Assert.Single(m2.Read(d => d.Waypoints));
    }

    [Fact]
    public void Knowledge_ChunksByHeading_CoreSection_AndSizes()
    {
        var md = """
            # Guide
            <!-- core -->
            ## Core rules
            - never dig down near the void
            <!-- /core -->
            ## Farming
            Wheat needs water within 4 blocks.
            ## Tiny
            One line.
            ## Huge
            """ + "\n" + string.Join("\n", Enumerable.Range(0, 60).Select(i => $"- fact {i}: sugar cane grows next to water up to three tall."));
        var (core, chunks) = KnowledgeChunker.Parse("g.md", md, 150, 300);
        Assert.Contains("never dig down near the void", core);
        Assert.DoesNotContain(chunks, c => c.Text.Contains("never dig down"));
        Assert.Contains(chunks, c => c.Title.Contains("Guide > Farming") && c.Text.Contains("Wheat") && c.Text.Contains("One line")); // small sections merged
        var huge = chunks.Where(c => c.Title.Contains("Huge")).ToList();
        Assert.True(huge.Count >= 3);
        Assert.All(chunks, c => Assert.True(TextUtil.EstimateTokens(c.Text) <= 300, c.Title));

        var (core2, chunks2) = KnowledgeChunker.Parse("n.md", "## First\nalpha rules\n## Second\nbeta", 150, 300);
        Assert.Equal("## First\nalpha rules", core2);                // no marker: first section is core
        Assert.Equal("beta", Assert.Single(chunks2).Text);
    }

    [Fact]
    public void Knowledge_Sync_ReindexesOnlyOnChange()
    {
        using var dir = new TempDir();
        var kdir = Directory.CreateDirectory(dir.File("knowledge")).FullName;
        File.WriteAllText(Path.Combine(kdir, "a.md"), "<!-- core -->\nvoid is deadly\n<!-- /core -->\n## Melons\nmelon stems need farmland\n");
        var (db, idx) = Open();
        using var _ = db;
        var o = new RagOptions();
        var first = KnowledgeIndexer.Sync(idx, kdir, o);
        Assert.Equal(("void is deadly", 1, 1), (first.Core, first.Reindexed, first.Chunks));
        Assert.Equal(0, KnowledgeIndexer.Sync(idx, kdir, o).Reindexed);
        File.WriteAllText(Path.Combine(kdir, "a.md"), "<!-- core -->\nvoid is deadly\n<!-- /core -->\n## Pumpkins\npumpkin stems too\n");
        Assert.Equal(1, KnowledgeIndexer.Sync(idx, kdir, o).Reindexed);
        Assert.Empty(idx.Search(new RagQuery { Text = "melon" }));
        Assert.Single(idx.Search(new RagQuery { Text = "pumpkin", Kinds = ["knowledge"] }));
        File.Delete(Path.Combine(kdir, "a.md"));
        KnowledgeIndexer.Sync(idx, kdir, o);
        Assert.Equal(0, idx.Count("knowledge"));
    }

    [Fact]
    public async Task EmbeddingCache_NoDuplicateEmbeds_AndUsageTracked()
    {
        var emb = new FakeEmbedder();
        var usage = new UsageTracker(new BudgetOptions(), time: new ManualTime(), path: null);
        var (db, idx) = Open(emb, usage: usage);
        using var _ = db;
        idx.Upsert(new RagDoc("a", "note", "t", "same text here"));
        idx.Upsert(new RagDoc("b", "note", "t", "same text here"));
        idx.Upsert(new RagDoc("n", "note", "t", "chest holds 3 cobble"));
        await idx.FlushEmbeddingsAsync();
        Assert.Equal(2, emb.Texts);                                    // identical content embedded once
        Assert.Equal(3, idx.VectorCount);

        idx.Upsert(new RagDoc("a", "note", "t", "same text here"));     // unchanged: no-op
        idx.Upsert(new RagDoc("n", "note", "t", "chest holds 42 cobble")); // numbers-only change: not material
        await idx.FlushEmbeddingsAsync();
        Assert.Equal(2, emb.Texts);
        Assert.Contains("42", idx.GetByRef("n")!.Body);                    // ...but FTS/body did update

        idx.Upsert(new RagDoc("a", "note", "t", "different words now"));
        await idx.FlushEmbeddingsAsync();
        Assert.Equal(3, emb.Texts);

        idx.Delete("a");
        idx.Upsert(new RagDoc("c", "note", "t", "different words now"));  // re-added content: served from embed_cache
        await idx.FlushEmbeddingsAsync();
        Assert.Equal(3, emb.Texts);
        Assert.True(usage.Today.EmbeddingTokens > 0);
        Assert.True(usage.Today.EmbeddingUsd > 0);
        Assert.Equal(0, usage.Today.Calls);                             // separate line item from Claude calls
    }

    [Fact]
    public async Task AutoRetrieval_Caps_Thresholds_AndDedupes()
    {
        var ctx = new ContextManager();
        using var m = new MemoryStore(null);
        var o = m.Index.Options;
        for (int i = 0; i < 30; i++) m.AddNote("Alice", $"alice fact {i}: likes birch and spruce and oak forests very much indeed");
        m.UpsertStructure("melon farm", s => s.Note = "melons east of the tree");
        var auto = new AutoRetriever(m.Index, ctx);

        var block = await auto.BuildAsync(new EventBatch([GameEvent.Chat("Alice", "clawd is the melon farm ready")]), ModelTier.Main, null, ["clawd"], default);
        Assert.StartsWith("[memory]", block);
        Assert.Contains("melon farm", block);
        Assert.True(block.Length <= o.AutoMaxChars);
        Assert.True(block.Split('\n').Length - 1 <= o.AutoTopK);

        Assert.Equal("", await auto.BuildAsync(new EventBatch([GameEvent.Chat("Bob", "lol nice one")]), ModelTier.Main, null, null, default));

        // Commit an episode carrying that block: while it's in the window the same facts aren't re-injected (main tier)...
        var ep = new Episode(ModelTier.Main, DateTimeOffset.UtcNow);
        ep.Messages.Add(LlmMessage.User("pos\n" + block + "\n[chat] Alice: clawd is the melon farm ready"));
        ep.Messages.Add(LlmMessage.Assistant("yep"));
        ctx.Commit(ep);
        var again = await auto.BuildAsync(new EventBatch([GameEvent.Chat("Alice", "clawd is the melon farm ready")]), ModelTier.Main, null, ["clawd"], default);
        Assert.DoesNotContain("struct melon farm", again);
        // ...but the cheap tier's text transcript drops [memory], so it gets it again.
        var cheap = await auto.BuildAsync(new EventBatch([GameEvent.Chat("Alice", "clawd is the melon farm ready")]), ModelTier.Cheap, null, ["clawd"], default);
        Assert.Contains("struct melon farm", cheap);
        Assert.DoesNotContain("[memory]", ContextManager.Flatten(ep, 200));

        // Hard cap is respected even with long lines.
        o.AutoMaxChars = 120;
        var capped = await auto.BuildAsync(new EventBatch([GameEvent.Chat("Alice", "birch spruce oak forests")]), ModelTier.Cheap, null, null, default);
        Assert.True(capped.Length <= 120);
    }

    [Fact]
    public async Task Episodes_IndexedOnCommit_AndCompaction_Recallable()
    {
        var (rt, llm, _) = TestRuntime.Create(o => { o.Context.CompactAtTokens = 300; o.Context.CompactToTokens = 150; });
        using var _r = rt;
        llm.Fallback = r => r.Purpose == "compact"
            ? llm.Resp(r, StopReasons.EndTurn, new LlmText("journal: built the wheat farm for Alice"))
            : llm.Resp(r, StopReasons.EndTurn, new LlmText("done " + new string('x', 150)));
        string[] spots = ["birch", "spruce", "melon", "cactus", "pumpkin", "sugarcane"];
        for (int i = 0; i < 6; i++)
            await rt.Agent.HandleBatchAsync(new EventBatch([GameEvent.Chat("Alice", $"clawd plant wheat by the {spots[i]}")]), default);

        Assert.True(rt.Rag.Count("episode") >= 7);                   // 6 episodes + at least one journal
        Assert.NotNull(rt.Rag.GetByRef(rt.Rag.Recent("episode", null, 1, "journal:").Single().Ref));
        var hit = await rt.Tools.ExecuteAsync("recall", J(new { query = "birch", kind = "episode" }), default);
        Assert.Contains("wheat by the birch", hit.Text);
        Assert.Contains(rt.Rag.SearchChat("wheat"), c => c.Player == "Alice");   // full chat log
        Assert.Contains(rt.Rag.SearchChat("done"), c => c.Kind == "bot");

        // Turn text for a folded-away topic gets it back from RAG; in-window episodes are never injected.
        var last = llm.Requests.Where(r => r.Purpose != "compact").Last().Messages[^1].Text;
        Assert.DoesNotContain("sugarcane", last[..last.IndexOf("[chat]", StringComparison.Ordinal)]);
    }

    [Fact]
    public void RestoreJournal_AcrossRestart()
    {
        using var dir = new TempDir();
        void Configure(AgentOptions o) { o.DataDirectory = dir.Path; o.KnowledgeDirectory = null; o.IdleCheckInMinutes = 0; }
        var o1 = new AgentOptions(); Configure(o1);
        using (var rt = AgentRuntime.Create(o1, (_, _) => Task.CompletedTask, new FakeLlm()))
        {
            rt.Rag.Upsert(new RagDoc("journal:x:1", "episode", "journal", "10-26 12:00: Alice wants a bigger island") { Source = "journal" });
        }
        var o2 = new AgentOptions(); Configure(o2);
        using var rt2 = AgentRuntime.Create(o2, (_, _) => Task.CompletedTask, new FakeLlm());
        Assert.Equal("Alice wants a bigger island", rt2.Context.Journal);
    }

    [Fact]
    public void SeedDb_CopiedOnFirstStart()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(dir.File("seed"));
        using (var seed = AgentDb.Open(dir.File("seed/mineclaw-seed.db")))
        using (var idx = new RagIndex(seed, backgroundEmbedding: false))
            idx.Upsert(new RagDoc("kb:recipes:0", "knowledge", "Recipes > Torch", "torch = coal + stick"));
        var o = new AgentOptions { DataDirectory = dir.Path, KnowledgeDirectory = null, IdleCheckInMinutes = 0 };
        using var rt = AgentRuntime.Create(o, (_, _) => Task.CompletedTask, new FakeLlm());
        Assert.Contains(rt.Rag.Search(new RagQuery { Text = "torch" }), h => h.Ref == "kb:recipes:0");
    }
}
