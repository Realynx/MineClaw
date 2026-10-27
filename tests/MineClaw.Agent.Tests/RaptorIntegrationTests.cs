using System.Text.Json;
using System.Text.RegularExpressions;
using MineClaw.Agent.Budget;
using MineClaw.Agent.Llm;
using MineClaw.Agent.Rag;
using MineClaw.Agent.Rag.Raptor;
using MineClaw.Raptor;

namespace MineClaw.Agent.Tests;

/// <summary>RAPTOR integration: AgentDb store, adapters, tree policy, fusion, mode switch, scheduling budget, seed db. Offline.</summary>
public class RaptorIntegrationTests
{
    private static readonly string[] Topics =
    [
        "furnace cobblestone smelting fuel coal", "wheat farm seeds water hoe", "iron ingots pickaxe ore mining",
        "sugar cane paper books bookshelf", "cobblestone generator lava water", "wool sheep dye colours bed",
    ];

    /// <summary>FakeLlm whose every call returns a deterministic extractive "summary" of the prompt.</summary>
    internal static FakeLlm SummaryLlm(LlmUsage? usage = null)
    {
        var llm = new FakeLlm { UsagePerCall = usage ?? new LlmUsage(300, 60, 0, 0) };
        llm.Fallback = r =>
        {
            var text = r.Messages[0].Text;
            var words = Regex.Matches(text.ToLowerInvariant(), "[a-z]{4,}").Select(m => m.Value)
                .Where(w => w is not ("write" or "summary" or "following" or "including" or "many" or "details" or "possible"))
                .GroupBy(w => w).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).Take(12).Select(g => g.Key);
            return llm.Resp(r, StopReasons.EndTurn, new LlmText("summary: " + string.Join(" ", words)));
        };
        return llm;
    }

    private sealed class Fixture : IDisposable
    {
        public required AgentDb Db { get; init; }
        public required RagIndex Rag { get; init; }
        public required FakeEmbedder Embedder { get; init; }
        public required FakeLlm Llm { get; init; }
        public required HaikuSummarizer Summarizer { get; init; }
        public required RaptorMemory Raptor { get; set; }
        public required UsageTracker Usage { get; init; }

        public RaptorMemory Reopen(RaptorMemoryOptions? o = null)
        {
            Raptor.Dispose();
            Raptor = new RaptorMemory(Db, Rag, new CachedEmbedder(Db, Embedder), Summarizer, o ?? Raptor.Options, Opts());
            return Raptor;
        }

        public void Dispose()
        {
            Raptor.Dispose();
            Rag.Dispose();
            Db.Dispose();
        }
    }

    private static RaptorOptions Opts() => new() { SummaryConcurrency = 1, MaxDegreeOfParallelism = 2 };

    private static Fixture Open(RaptorMemoryOptions? o = null, string? path = null, LlmUsage? usage = null)
    {
        var db = AgentDb.Open(path);
        var emb = new FakeEmbedder();
        var tracker = new UsageTracker(new BudgetOptions(), path: null) { Classify = m => new LlmOptions().TierOfModel(m) };
        var rag = new RagIndex(db, new RagOptions(), emb, tracker, backgroundEmbedding: false);
        var llm = SummaryLlm(usage);
        var sum = new HaikuSummarizer(llm, new LlmOptions(), tracker);
        var raptor = new RaptorMemory(db, rag, new CachedEmbedder(db, emb, usage: tracker), sum, o ?? new RaptorMemoryOptions(), Opts());
        return new Fixture { Db = db, Rag = rag, Embedder = emb, Llm = llm, Summarizer = sum, Raptor = raptor, Usage = tracker };
    }

    private static void AddKnowledge(RagIndex rag, int n, string prefix = "kb:t.md:", int offset = 0)
    {
        for (int i = 0; i < n; i++)
        {
            var topic = Topics[(i + offset) % Topics.Length];
            rag.Upsert(new RagDoc($"{prefix}{i + offset}", "knowledge", $"note {i + offset}", $"{topic} detail {i + offset} item{(i + offset) % 7}") { Source = "t.md" });
        }
    }

    private static async Task<Fixture> BuiltAsync(int docs = 40, RaptorMemoryOptions? o = null, string? path = null, LlmUsage? usage = null)
    {
        var f = Open(o, path, usage);
        AddKnowledge(f.Rag, docs);
        await f.Rag.FlushEmbeddingsAsync();
        await f.Raptor.BuildAsync();
        return f;
    }

    private static (long Docs, long Edges, long Vectors, string? Meta, string Map) DbState(AgentDb db) => (
        db.ScalarLong("SELECT count(*) FROM docs"), db.ScalarLong("SELECT count(*) FROM doc_edges"), db.ScalarLong("SELECT count(*) FROM vectors"),
        db.GetMeta(AgentDbRaptorStore.MetaKey),
        string.Join(",", db.Query("SELECT ref || '=' || coalesce(raptor_idx, -1) || ':' || raptor_flags FROM docs ORDER BY ref", r => r.GetString(0))));

    // ======================= store =======================

    [Fact]
    public async Task Store_RoundTripsTree_NodesAreDocsEdgesAndVectors()
    {
        using var f = await BuiltAsync();
        var idx = f.Raptor.Index;
        Assert.True(idx.NumLayers >= 1);
        Assert.Empty(idx.Read(t => t.Validate()));
        int summaries = idx.NodeCount - idx.LeafCount;
        Assert.Equal(summaries, (int)f.Db.ScalarLong("SELECT count(*) FROM docs WHERE kind = 'summary' AND level > 0 AND raptor_idx IS NOT NULL"));
        Assert.Equal(idx.NodeCount, (int)f.Db.ScalarLong("SELECT count(*) FROM docs WHERE raptor_idx IS NOT NULL"));
        Assert.Equal(idx.Read(t => t.AllNodes.Values.Sum(n => n.Children.Count)), (int)f.Db.ScalarLong("SELECT count(*) FROM doc_edges"));
        Assert.Equal(0, f.Db.ScalarLong("SELECT count(*) FROM docs WHERE kind = 'knowledge' AND dirty = 1")); // leaves synced
        Assert.Equal(summaries, (int)f.Db.ScalarLong("SELECT count(*) FROM vectors v JOIN docs d ON d.id = v.doc_id WHERE d.kind = 'summary'"));

        var before = idx.Read(t => t.AllNodes.Values.OrderBy(n => n.Index).Select(n => $"{n.Index}/{n.Layer}/{n.Key}/{string.Join(".", n.Children)}").ToList());
        var again = f.Reopen();
        Assert.True(await again.LoadAsync());
        var after = again.Index.Read(t => t.AllNodes.Values.OrderBy(n => n.Index).Select(n => $"{n.Index}/{n.Layer}/{n.Key}/{string.Join(".", n.Children)}").ToList());
        Assert.Equal(before, after);
        Assert.Empty(again.Index.Read(t => t.Validate()));
    }

    [Fact]
    public async Task Store_AppliesEachChangeSetInOneTransaction()
    {
        using var f = await BuiltAsync();
        var state = DbState(f.Db);
        AddKnowledge(f.Rag, 6, "kb:new.md:", 100);
        await f.Rag.FlushEmbeddingsAsync();
        var stateWithNewDocs = DbState(f.Db);

        // Inserting attaches leaves under existing summaries (leaf upserts + parent upserts). Fail on the first summary
        // record, after the leaf records were already written inside the transaction: nothing may stick.
        f.Raptor.Store.FailOn = r => r.Layer > 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Raptor.SyncAsync());
        Assert.Equal(stateWithNewDocs, DbState(f.Db));
        Assert.NotEqual(state.Docs, stateWithNewDocs.Docs);

        // The persisted tree is still the old, consistent one.
        f.Raptor.Store.FailOn = null;
        var reopened = f.Reopen();
        Assert.True(await reopened.LoadAsync());
        Assert.Equal(40, reopened.Index.LeafCount);
        Assert.Empty(reopened.Index.Read(t => t.Validate()));

        // And a clean retry applies everything at once.
        var sync = await reopened.SyncAsync();
        Assert.Equal(6, sync.Inserted);
        Assert.Equal(46, reopened.Index.LeafCount);
        Assert.Equal(0, f.Db.ScalarLong("SELECT count(*) FROM docs WHERE ref LIKE 'kb:new.md:%' AND (raptor_idx IS NULL OR dirty = 1)"));
    }

    [Fact]
    public async Task Sync_RemovesDeletedDocs_AndReinsertsChangedOnes()
    {
        using var f = await BuiltAsync();
        f.Rag.Delete("kb:t.md:3");
        f.Rag.Upsert(new RagDoc("kb:t.md:4", "knowledge", "note 4", "completely different nether portal obsidian text") { Source = "t.md" });
        await f.Rag.FlushEmbeddingsAsync();
        var sync = await f.Raptor.SyncAsync();
        Assert.Equal(1, sync.Removed);
        Assert.Equal(1, sync.Reinserted);
        var keys = f.Raptor.Index.Read(t => t.Leaves.Select(l => l.Key).ToList());
        Assert.DoesNotContain("kb:t.md:3", keys);
        Assert.Contains("kb:t.md:4", keys);
        Assert.Contains("obsidian", f.Raptor.Index.Read(t => t.Leaves.Single(l => l.Key == "kb:t.md:4").Text));
        Assert.Empty(f.Raptor.Index.Read(t => t.Validate()));
        Assert.True(f.Raptor.Index.DirtyCount > 0);
    }

    // ======================= policy =======================

    [Fact]
    public async Task TreePolicy_KeepsStructuredAndTestDataOutOfTheTree()
    {
        using var f = Open();
        AddKnowledge(f.Rag, 20);
        f.Rag.Upsert(new RagDoc("ep:run:1", "episode", "chat", "Bob asked for a furnace") { Source = "episode" });
        f.Rag.Upsert(new RagDoc("act:bob:1", "episode", "bob activity", "bob mined cobblestone at the generator") { Source = "activity" });
        f.Rag.Upsert(new RagDoc("st:gen", "structure", "gen", "struct gen cobblestone generator") { Source = "memory" });
        f.Rag.Upsert(new RagDoc("note:1", "player", "bob", "note#1 bob: likes wheat farms") { Source = "memory" });
        f.Rag.Upsert(new RagDoc("eptest:run:1", "episode", "test", "[test] fake chat") { Source = "test" });
        f.Rag.Upsert(new RagDoc("self:1", "episode", "personality change", "personality add trait") { Source = "self" });
        f.Rag.Upsert(new RagDoc("wp:home", "waypoint", "home", "wp home 1,64,2") { Bounds = Box.Point(1, 64, 2) });
        f.Rag.Upsert(new RagDoc("obs:chest:1,2,3", "world", "chest 1,2,3", "chest with 12 iron ingots") { Tag = "chest", Embed = false });
        f.Rag.Upsert(new RagDoc("obs:furnace:4,5,6", "world", "furnace 4,5,6", "furnace near spawn") { Tag = "furnace" });
        f.Rag.Upsert(new RagDoc("rt:cobble", "routine", "cobble", "routine cobble: mine>deposit"));
        await f.Rag.FlushEmbeddingsAsync();
        await f.Raptor.BuildAsync();

        var keys = f.Raptor.Index.Read(t => t.Leaves.Select(l => l.Key!).ToHashSet());
        Assert.Equal(24, keys.Count);
        Assert.Contains("ep:run:1", keys);
        Assert.Contains("act:bob:1", keys);
        Assert.Contains("st:gen", keys);
        Assert.Contains("note:1", keys);
        foreach (var excluded in new[] { "eptest:run:1", "self:1", "wp:home", "obs:chest:1,2,3", "obs:furnace:4,5,6", "rt:cobble" })
            Assert.DoesNotContain(excluded, keys);
        Assert.False(new RaptorTreePolicy(new RaptorMemoryOptions()).Includes("raptor:5", "summary", "raptor", 1));
    }

    // ======================= fusion =======================

    [Fact]
    public void Fusion_RanksDocsInSeveralListsFirst_AndGatesTreeOnlyHitsByCosine()
    {
        using var db = AgentDb.Open(null);
        using var rag = new RagIndex(db, new RagOptions(), backgroundEmbedding: false);
        long a = rag.Upsert(new RagDoc("a", "knowledge", "furnace", "furnace from eight cobblestone"));
        long c = rag.Upsert(new RagDoc("c", "knowledge", "furnace fuel", "coal burns in a furnace"));
        long b = rag.Upsert(new RagDoc("b", "knowledge", "oven", "smelting ore needs an oven"));
        long d = rag.Upsert(new RagDoc("d", "knowledge", "boat", "boats float"));
        var aug = new SearchAugment([new RankedList("raptor-collapsed", [(b, 0.62), (a, 0.58), (d, 0.10)])], id => id == a || id == b || id == d);

        var hits = rag.Search(new RagQuery { Text = "furnace", Limit = 4, Strict = true }, null, aug);
        var refs = hits.Select(h => h.Ref).ToList();
        Assert.Equal("a", refs[0]);                        // FTS + tree beats either list alone (RRF)
        Assert.Contains("b", refs);                        // tree-only, passes the cosine gate
        Assert.Contains("c", refs);                        // FTS-only
        Assert.DoesNotContain("d", refs);                  // tree-only below MinSimilarity: gated out
        Assert.Equal(0.62, hits.Single(h => h.Ref == "b").Cosine, 3);
        Assert.Equal(0, hits.Single(h => h.Ref == "b").Lexical);

        var flat = rag.Search(new RagQuery { Text = "furnace", Limit = 4, Strict = true });
        Assert.DoesNotContain("b", flat.Select(h => h.Ref));
    }

    [Fact]
    public async Task Fusion_EndToEnd_TreeFindsSemanticMatchesAndSummariesFormat()
    {
        using var f = await BuiltAsync();
        f.Rag.Augmenter = f.Raptor;
        var hits = await f.Rag.SearchAsync(new RagQuery { Text = "metal ore", Limit = 6 });
        Assert.NotEmpty(hits);
        Assert.Contains(hits, h => h.Body.Contains("iron ingots", StringComparison.Ordinal));
        var summary = f.Rag.GetByRef(AgentDbRaptorStore.SummaryRefPrefix + f.Raptor.Index.Read(t => t.Layer(1).First().Index))!;
        Assert.StartsWith($"#{summary.Id} sum ", f.Rag.FormatHit(summary, 200));
        // kind filters reach the tree as leaf filters; summaries are dropped when a kind is asked for
        var episodes = await f.Rag.SearchAsync(new RagQuery { Text = "metal ore", Kinds = ["episode"], Limit = 6 });
        Assert.Empty(episodes);
        var aug = f.Raptor.Augment(new RagQuery { Text = "x", Kinds = ["knowledge"] }, (await f.Embedder.EmbedAsync(["iron ore"], default)).Vectors[0])!;
        Assert.All(aug.Lists[0].Items, i => Assert.Equal("knowledge", f.Rag.Get(i.Id)!.Kind));
    }

    // ======================= mode =======================

    [Fact]
    public async Task Mode_SwitchesBetweenCollapsedTraversalAndHybrid()
    {
        Assert.Equal(RaptorMode.Collapsed, new RaptorMemoryOptions().ParsedMode);
        Assert.Equal(RaptorMode.Traversal, new RaptorMemoryOptions { Mode = "traversal" }.ParsedMode);
        Assert.Equal(RaptorMode.Hybrid, new RaptorMemoryOptions { Mode = "HYBRID" }.ParsedMode);
        Assert.Equal(RaptorMode.Collapsed, new RaptorMemoryOptions { Mode = "nonsense" }.ParsedMode);

        using var f = await BuiltAsync(o: new RaptorMemoryOptions { Mode = "traversal" });
        var qv = (await f.Embedder.EmbedAsync(["wheat farm water"], default)).Vectors[0];
        string Lists() => string.Join("+", f.Raptor.Augment(new RagQuery { Text = "wheat" }, qv)!.Lists.Select(l => l.Name));
        Assert.Equal(RaptorMode.Traversal, f.Raptor.Mode);
        Assert.Equal(RaptorMemory.TraversalList, Lists());
        f.Raptor.Mode = RaptorMode.Collapsed;
        Assert.Equal(RaptorMemory.CollapsedList, Lists());
        f.Raptor.Mode = RaptorMode.Hybrid;
        Assert.Equal(RaptorMemory.CollapsedList + "+" + RaptorMemory.TraversalList, Lists());

        // Traversal ranks by score and stays inside the tree's top-k per layer.
        f.Raptor.Mode = RaptorMode.Traversal;
        var trav = f.Raptor.Augment(new RagQuery { Text = "wheat" }, qv)!.Lists[0].Items;
        Assert.Equal(trav.OrderByDescending(i => i.Cosine).Select(i => i.Id), trav.Select(i => i.Id));

        var (rt, _, _) = TestRuntime.Create(o => o.Rag.Raptor.Mode = "hybrid", embeddings: new FakeEmbedder());
        using (rt)
        {
            Assert.NotNull(rt.Raptor);
            Assert.Equal(RaptorMode.Hybrid, rt.Raptor!.Mode);
            Assert.Same(rt.Raptor, rt.Rag.Augmenter);
            Assert.Null(rt.RaptorMaintenance); // in-memory runtimes never run background summary jobs
        }
        var (flat, _, _) = TestRuntime.Create(o => o.Rag.Raptor.Enabled = false, embeddings: new FakeEmbedder());
        using (flat) Assert.Null(flat.Raptor);
    }

    // ======================= scheduling =======================

    [Fact]
    public async Task Scheduler_InsertsIncrementally_RefreshesInBatches_AndRespectsDailyBudget()
    {
        // Each fake Haiku call reports 20k input tokens: $0.00205 at Haiku 5.5 prices.
        var o = new RaptorMemoryOptions
        {
            RefreshEveryLeaves = 5, RefreshEveryMinutes = 600, DailySummaryBudgetUsd = 0.003m, RefreshMaxUsd = 0.01m,
            RebuildAnytimeBelowLeaves = 0, OffPeakStartHour = 3, OffPeakEndHour = 4,
        };
        using var f = await BuiltAsync(o: o, usage: new LlmUsage(20_000, 100, 0, 0));
        var time = new ManualTime { Now = new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.Zero) };
        using var jobs = new RaptorScheduler(f.Raptor, f.Summarizer, f.Db, f.Usage, time);
        int callsAfterBuild = f.Llm.Requests.Count;

        AddKnowledge(f.Rag, 3, "kb:a.md:", 200);
        await f.Rag.FlushEmbeddingsAsync();
        var step = await jobs.StepAsync();
        Assert.Equal(3, step.Sync.Inserted);
        Assert.Null(step.Refresh);                       // 3 < RefreshEveryLeaves: inserted, not yet summarized
        Assert.Equal(callsAfterBuild, f.Llm.Requests.Count);
        Assert.True(f.Raptor.Index.DirtyCount > 0);

        AddKnowledge(f.Rag, 3, "kb:b.md:", 300);
        await f.Rag.FlushEmbeddingsAsync();
        step = await jobs.StepAsync();
        Assert.NotNull(step.Refresh);                    // 6 ≥ 5: one batched refresh on the cheap tier
        Assert.True(step.CostUsd > 0);
        Assert.True(step.CostUsd <= 0.003m + 0.00205m);  // job cap = min(per-job, remaining today); ≤ one call overshoot
        Assert.Equal(step.CostUsd, jobs.SpentToday);
        Assert.All(f.Llm.Requests.Skip(callsAfterBuild), r => { Assert.Equal(ModelTier.Cheap, r.Tier); Assert.Equal("raptor", r.Purpose); });
        Assert.True(f.Usage.Today.Tier(ModelTier.Cheap).Calls > 0);  // reported under the memory tier

        AddKnowledge(f.Rag, 6, "kb:c.md:", 400);
        await f.Rag.FlushEmbeddingsAsync();
        int calls = f.Llm.Requests.Count;
        step = await jobs.StepAsync();
        Assert.Equal(6, step.Sync.Inserted);
        Assert.Equal("daily summary budget spent", step.Skipped);
        Assert.Equal(calls, f.Llm.Requests.Count);

        time.Advance(TimeSpan.FromDays(1));              // new day, fresh budget
        Assert.Equal(0, jobs.SpentToday);
        step = await jobs.StepAsync();
        Assert.NotNull(step.Refresh);
    }

    [Fact]
    public async Task Scheduler_RebuildsOnlyOffPeak_UnlessTreeIsSmall()
    {
        var o = new RaptorMemoryOptions
        {
            RefreshEveryLeaves = 10_000, RefreshEveryMinutes = 100_000, RebuildAnytimeBelowLeaves = 0, OffPeakStartHour = 3, OffPeakEndHour = 5,
            DailySummaryBudgetUsd = 1m, RebuildMaxUsd = 1m,
        };
        using var f = await BuiltAsync(docs: 30, o: o);
        var time = new ManualTime { Now = new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.Zero) };
        using var jobs = new RaptorScheduler(f.Raptor, f.Summarizer, f.Db, f.Usage, time);
        AddKnowledge(f.Rag, 12, "kb:more.md:", 500);     // 40% changed > 25%: ShouldRebuild fires
        await f.Rag.FlushEmbeddingsAsync();
        var step = await jobs.StepAsync();
        Assert.True(f.Raptor.Index.ShouldRebuild().Rebuild);
        Assert.Null(step.Rebuild);                       // noon: not off-peak

        time.Now = new DateTimeOffset(2026, 10, 27, 3, 30, 0, TimeSpan.Zero);
        step = await jobs.StepAsync();
        Assert.NotNull(step.Rebuild);
        Assert.Equal(42, step.Rebuild!.Leaves);
        Assert.False(f.Raptor.Index.ShouldRebuild().Rebuild);
        Assert.Empty(f.Raptor.Index.Read(t => t.Validate()));
        Assert.Equal(42, (int)f.Db.ScalarLong("SELECT count(*) FROM docs WHERE level = 0 AND raptor_idx IS NOT NULL"));
    }

    [Fact]
    public async Task Scheduler_IsCancellable_AndNeverBlocksTheCaller()
    {
        using var dir = new TempDir();
        using var f = await BuiltAsync(path: dir.File("m.db"));
        using var jobs = new RaptorScheduler(f.Raptor, f.Summarizer, f.Db, f.Usage);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        jobs.Start();
        Assert.True(sw.ElapsedMilliseconds < 500);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => jobs.StepAsync(cts.Token));
    }

    // ======================= adapters =======================

    [Fact]
    public async Task Summarizer_SendsPaperPromptOnCheapTier_RecordsMemoryTierUsage_AndHonoursCostCap()
    {
        var llm = new FakeLlm { UsagePerCall = new LlmUsage(20_000, 100, 0, 0) };
        llm.ThenText("First sentence about furnaces. Second sentence cut off mid").Then(r => llm.Resp(r, StopReasons.MaxTokens,
            new LlmText("Cobblestone makes furnaces and stone tools and other useful things. This one is cut")));
        var usage = new UsageTracker(new BudgetOptions(), path: null) { Classify = m => new LlmOptions().TierOfModel(m) };
        var s = new HaikuSummarizer(llm, new LlmOptions(), usage, maxCostUsd: 0.003m);
        var o = new RaptorOptions();
        var req = new SummaryRequest("ctx", o.SummarySystemPrompt, o.SummaryPromptTemplate.Replace("{context}", "ctx"), o.SummarizationLength);

        var r1 = await s.SummarizeAsync(req, default);
        Assert.Equal("First sentence about furnaces. Second sentence cut off mid", r1.Text); // end_turn: kept as is
        var sent = llm.Requests[0];
        Assert.Equal(ModelTier.Cheap, sent.Tier);
        Assert.StartsWith("You are a Summarizing Text Portal", sent.System[0].Text);
        Assert.Equal("Write a summary of the following, including as many key details as possible: ctx:", sent.Messages[0].Text);
        Assert.Equal(150, sent.MaxTokens);
        Assert.False(sent.CacheHistory);
        Assert.Equal(0.00205, r1.CostUsd!.Value, 6);
        Assert.Equal(1, usage.Today.Tier(ModelTier.Cheap).Calls);

        var r2 = await s.SummarizeAsync(req, default);
        Assert.Equal("Cobblestone makes furnaces and stone tools and other useful things.", r2.Text); // max_tokens: trimmed
        await Assert.ThrowsAsync<SummaryBudgetExceededException>(() => s.SummarizeAsync(req, default));      // $0.0041 ≥ $0.003 cap
        Assert.Equal(2, llm.Requests.Count);
    }

    [Fact]
    public async Task CachedEmbedder_ReusesEmbedCache_AndReportsTokens()
    {
        using var db = AgentDb.Open(null);
        var emb = new FakeEmbedder();
        var usage = new UsageTracker(new BudgetOptions(), path: null);
        var e = new CachedEmbedder(db, emb, new EmbeddingOptions { MaxBatch = 2 }, usage);
        var first = await e.EmbedAsync(["iron ingots", "wheat farm", "iron ingots", "sugar cane"], default);
        Assert.Equal(4, first.Vectors.Count);
        Assert.Equal(first.Vectors[0], first.Vectors[2]);
        Assert.Equal(3, emb.Texts);                       // duplicate embedded once
        Assert.True(usage.Today.EmbeddingTokens > 0);
        var again = await e.EmbedAsync(["sugar cane", "iron ingots"], default);
        Assert.Equal(3, emb.Texts);                       // all from embed_cache
        Assert.Equal(first.Vectors[3], again.Vectors[0]);
        Assert.Equal(1.0, Math.Sqrt(again.Vectors[1].Sum(x => (double)x * x)), 4);
    }

    // ======================= seed =======================

    private static async Task<(SeedBuildReport Report, string Path)> BuildSeedAsync(TempDir dir, FakeEmbedder emb, FakeLlm llm, decimal cap = 0.50m)
    {
        var kdir = Path.Combine(dir.Path, "knowledge");
        Directory.CreateDirectory(kdir);
        File.WriteAllText(Path.Combine(kdir, "sky.md"), "# Sky\n<!-- core -->\nCore rule: never fall.\n<!-- /core -->\n## Generator\n" +
            "Water touching flowing lava makes cobblestone. Mine it and it refills.\n## Farms\nWheat needs water within four blocks.\n");
        var path = dir.File("seed.db");
        var report = await SeedDatabase.BuildAsync(new SeedBuildOptions
        {
            OutputPath = path, KnowledgeDirectory = kdir, MaxCostUsd = cap,
            Embeddings = new EmbeddingOptions { PricePerMTok = 0.13m },
            Raptor = new RaptorOptions { SummaryConcurrency = 2 },
        }, emb, llm);
        return (report, path);
    }

    [Fact]
    public async Task Seed_HoldsOnlyGameDataKnowledgeAndTree_AndValidatorCatchesForbiddenKinds()
    {
        using var dir = new TempDir();
        var (report, path) = await BuildSeedAsync(dir, new FakeEmbedder(), SummaryLlm());
        Assert.Empty(report.Violations);
        Assert.True(report.Tree.NumLayers >= 1);
        Assert.True(report.LeavesByTag["recipe"] > 100);
        Assert.Equal(new[] { "gamedata", "knowledge", "summary" }, report.DocsByKind.Keys.Order().ToArray());
        Assert.True(report.SummaryCalls > 0);
        Assert.True(report.TotalUsd < 0.50m);
        Assert.False(File.Exists(path + ".building"));

        using (var seed = AgentDb.Open(path))
        {
            Assert.Equal(AgentDb.LatestSchemaVersion, seed.SchemaVersion);
            Assert.Empty(SeedDatabase.Validate(seed));
            Assert.Equal(0, seed.ScalarLong("SELECT count(*) FROM self_profile") + seed.ScalarLong("SELECT count(*) FROM chat_log")
                            + seed.ScalarLong("SELECT count(*) FROM identity"));
            Assert.Equal(new[] { AgentDbRaptorStore.MetaKey }, seed.Query("SELECT key FROM meta", r => r.GetString(0)).ToArray());
            Assert.Contains("Furnace", seed.Scalar("SELECT body FROM docs WHERE ref = 'gd:recipe:61:0:1'") as string);
            Assert.Equal(0, seed.ScalarLong("SELECT count(*) FROM docs WHERE body LIKE '%never fall%'")); // core text stays in the prompt

            // Pollute it like a live db would be: every kind of forbidden content is reported.
            seed.Exec("INSERT INTO docs(ref, kind, title, body, hash, source, created, updated) VALUES ('eptest:1', 'episode', 't', 'b', 'h', 'test', 0, 0)");
            seed.Exec("INSERT INTO chat_log(at, kind, player, text) VALUES (0, 'chat', 'bob', 'hi')");
            seed.Exec("INSERT INTO self_profile(section, text, strength, created, reinforced) VALUES ('trait', 'cheerful', 1, 0, 0)");
            seed.SetMeta(RaptorScheduler.SpendMetaKey, "2026-10-26|0.01");
            var v = SeedDatabase.Validate(seed);
            Assert.Contains(v, e => e.Contains("'episode'"));
            Assert.Contains(v, e => e.Contains("live/test sources"));
            Assert.Contains(v, e => e.Contains("chat_log"));
            Assert.Contains(v, e => e.Contains("self_profile"));
            Assert.Contains(v, e => e.Contains(RaptorScheduler.SpendMetaKey));
        }
    }

    [Fact]
    public async Task Seed_AbortsBeforeSpendingWhenProjectionExceedsCap()
    {
        using var dir = new TempDir();
        var emb = new FakeEmbedder();
        var llm = SummaryLlm();
        await Assert.ThrowsAsync<SeedBudgetExceededException>(() => BuildSeedAsync(dir, emb, llm, cap: 0.000001m));
        Assert.Equal(0, emb.Calls);
        Assert.Empty(llm.Requests);
        Assert.False(File.Exists(dir.File("seed.db")));
    }

    [Fact]
    public async Task CleanStart_FromSeed_RecallGoesThroughRaptorInBothModes()
    {
        using var dir = new TempDir();
        var emb = new FakeEmbedder();
        var (_, seed) = await BuildSeedAsync(dir, emb, SummaryLlm());
        var data = Path.Combine(dir.Path, "data");
        foreach (var mode in new[] { "collapsed", "traversal" })
        {
            var (rt, llm, _) = TestRuntimeWithSeed(Path.Combine(data, mode), seed, mode, emb, Path.Combine(dir.Path, "knowledge"));
            using (rt)
            {
                Assert.True(await rt.Raptor!.StartLoading());
                Assert.Equal(0, rt.Rag.EmbeddedTexts);       // nothing re-embedded at start: seed vectors reused
                Assert.True(rt.Raptor.Index.LeafCount > 400);
                var recall = await rt.Tools.ExecuteAsync("recall", JsonSerializer.SerializeToElement(new { query = "how do I make a furnace" }), default);
                Assert.True(recall.Ok);
                Assert.Contains("Furnace", recall.Text);
                var hits = await rt.Rag.SearchAsync(new RagQuery { Text = "what smelts into glass", Limit = 3 });
                Assert.Contains(hits, h => h.Ref == "gd:smelt:12");
                Assert.Empty(llm.Requests);                  // no Claude calls on a clean start
            }
        }
    }

    private static (AgentRuntime, FakeLlm, List<string>) TestRuntimeWithSeed(string dataDir, string seed, string mode, FakeEmbedder emb, string knowledge)
    {
        var options = new AgentOptions { KnowledgeDirectory = knowledge, IdleCheckInMinutes = 0, DataDirectory = dataDir };
        options.Chat.Username = "Clawd_bot";
        options.Chat.DisplayName = "Clawd";
        options.Chat.Aliases = [];
        options.Rag.SeedFile = seed;
        options.Rag.Raptor.Mode = mode;
        options.Rag.Raptor.Maintenance = false;
        options.Self.Enabled = false;
        var llm = new FakeLlm();
        var sent = new List<string>();
        var rt = AgentRuntime.Create(options, (l, _) => { lock (sent) sent.Add(l); return Task.CompletedTask; }, llm, embeddings: emb);
        return (rt, llm, sent);
    }
}
