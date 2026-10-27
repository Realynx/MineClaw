using MineClaw.Agent.Budget;
using MineClaw.Agent.Llm;
using MineClaw.Raptor;
using MineClaw.Raptor.Text;

namespace MineClaw.Agent.Rag.Raptor;

public sealed class SeedBudgetExceededException(string message) : Exception(message);

public sealed class SeedBuildOptions
{
    public required string OutputPath { get; init; }
    /// <summary>docs/knowledge (chunked exactly like the live KnowledgeIndexer, so a live start doesn't re-index).</summary>
    public string? KnowledgeDirectory { get; init; }
    /// <summary>Hard cap: abort before any call if the projected spend exceeds it, and never spend beyond it.</summary>
    public decimal MaxCostUsd { get; init; } = 0.50m;
    public RagOptions Rag { get; init; } = new();
    public EmbeddingOptions Embeddings { get; init; } = new();
    public LlmOptions Llm { get; init; } = new();
    public RaptorOptions? Raptor { get; init; }
    public Action<string>? Progress { get; init; }
}

public sealed record SeedBuildReport(
    IReadOnlyDictionary<string, int> DocsByKind,
    IReadOnlyDictionary<string, int> LeavesByTag,
    BuildStats Tree,
    decimal ProjectedUsd,
    long EmbeddingTokens,
    decimal EmbeddingUsd,
    int SummaryCalls,
    long SummaryInputTokens,
    long SummaryOutputTokens,
    decimal SummaryUsd,
    long FileBytes,
    IReadOnlyList<string> Violations)
{
    public decimal TotalUsd => EmbeddingUsd + SummaryUsd;
}

/// <summary>
/// Builds the clean-state seed database (<c>data/seed/mineclaw-seed.db</c>): same schema/migrations as the live db,
/// holding only the game-data corpus (<see cref="SeedCorpus"/>), the chunked knowledge docs, their vectors and the
/// RAPTOR tree over them. No personality, episodes, chat, memory, observations, test data or usage.
/// </summary>
public static class SeedDatabase
{
    /// <summary>Doc kinds a seed may contain.</summary>
    public static readonly string[] AllowedKinds = [SeedCorpus.Kind, KnowledgeIndexer.Kind, RagIndex.SummaryKind];
    /// <summary>Meta keys a seed may contain.</summary>
    public static readonly string[] AllowedMeta = [AgentDbRaptorStore.MetaKey];
    /// <summary>Tables that must be empty in a seed.</summary>
    public static readonly string[] EmptyTables =
    [
        "chat_log", "waypoints", "structures", "notes", "routines", "identity", "observations", "container_items",
        "self_profile", "self_profile_history", "activity_log", "activity_rollups",
    ];

    /// <summary>Upserts the corpus and knowledge chunks (deterministic order). Returns (game docs, knowledge load).</summary>
    public static (int GameDocs, KnowledgeLoad Knowledge) Populate(RagIndex index, string? knowledgeDirectory, RagOptions options)
    {
        var corpus = SeedCorpus.Build();
        index.UpsertMany(corpus);
        var k = KnowledgeIndexer.Sync(index, knowledgeDirectory, options);
        return (corpus.Count, k);
    }

    /// <summary>Everything that would make a db unfit as a seed (empty = clean).</summary>
    public static List<string> Validate(AgentDb db)
    {
        var errors = new List<string>();
        var kinds = db.Query("SELECT kind, count(*) FROM docs GROUP BY kind", r => (r.GetString(0), r.GetInt64(1)));
        foreach (var (kind, n) in kinds)
            if (!AllowedKinds.Contains(kind)) errors.Add($"{n} doc(s) of forbidden kind '{kind}'");
        long bad = db.ScalarLong("""
            SELECT count(*) FROM docs WHERE source IN ('test', 'self', 'episode', 'journal', 'activity', 'memory', 'auto', 'taught', 'llm')
               OR ref LIKE 'eptest:%' OR ref LIKE 'ep:%' OR ref LIKE 'journal:%' OR ref LIKE 'self:%'
            """);
        if (bad > 0) errors.Add($"{bad} doc(s) from live/test sources");
        foreach (var t in EmptyTables)
        {
            if (db.ScalarLong("SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = $n", ("$n", t)) == 0) continue;
            long n = db.ScalarLong($"SELECT count(*) FROM {t}");
            if (n > 0) errors.Add($"table {t} has {n} row(s)");
        }
        foreach (var key in db.Query("SELECT key FROM meta", r => r.GetString(0)))
            if (!AllowedMeta.Contains(key)) errors.Add($"meta key '{key}' (usage/personality state)");
        long orphanSummaries = db.ScalarLong("SELECT count(*) FROM docs WHERE kind = $k AND raptor_idx IS NULL", ("$k", RagIndex.SummaryKind));
        if (orphanSummaries > 0) errors.Add($"{orphanSummaries} summary doc(s) outside the tree");
        long treeOther = db.ScalarLong("SELECT count(*) FROM docs WHERE raptor_idx IS NOT NULL AND kind NOT IN ($a, $b, $c)",
            ("$a", SeedCorpus.Kind), ("$b", KnowledgeIndexer.Kind), ("$c", RagIndex.SummaryKind));
        if (treeOther > 0) errors.Add($"{treeOther} tree node(s) of a forbidden kind");
        return errors;
    }

    /// <summary>Conservative spend projection: every leaf embedded once; summaries read ≤ 2.5× the leaf tokens
    /// (soft clustering + upper layers + prompt) and write ≤ one summarization_length per two leaves.</summary>
    public static decimal Project(IReadOnlyList<string> leafTexts, EmbeddingOptions emb, PriceTable prices, string cheapModel, RaptorOptions raptor)
    {
        long tokens = leafTexts.Sum(t => (long)ApproxTokenCounter.Instance.Count(t));
        decimal embed = tokens * 1.1m * emb.PricePerMTok / 1_000_000m;
        var p = prices.For(cheapModel);
        decimal input = tokens * 2.5m * p.InputPerM / 1_000_000m;
        decimal output = (leafTexts.Count / 2m) * raptor.SummarizationLength * p.OutputPerM / 1_000_000m;
        return embed + input + output;
    }

    /// <summary>
    /// The whole pipeline: fresh db at a temp path → corpus + knowledge → projection check → embed → RAPTOR build
    /// (paper prompt on the cheap tier) → validate → compact → atomic move to <see cref="SeedBuildOptions.OutputPath"/>.
    /// </summary>
    public static async Task<SeedBuildReport> BuildAsync(SeedBuildOptions o, IEmbeddingProvider embedder, ILlmClient llm, CancellationToken ct = default)
    {
        void Say(string s) => o.Progress?.Invoke(s);
        var output = Path.GetFullPath(o.OutputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var tmp = output + ".building";
        foreach (var f in new[] { tmp, tmp + "-wal", tmp + "-shm" }) if (File.Exists(f)) File.Delete(f);

        var usage = new UsageTracker(new BudgetOptions { DailyCapUsd = 1_000m, MonthlyCapUsd = 1_000m, MaxCallsPerHour = 100_000 }, path: null)
        {
            Classify = m => o.Llm.TierOfModel(m),
        };
        var raptorOptions = o.Raptor ?? new RaptorOptions();
        SeedBuildReport report;
        var db = AgentDb.Open(tmp);
        try
        {
            using var index = new RagIndex(db, o.Rag, embedder, usage, o.Embeddings, backgroundEmbedding: false);
            var (gameDocs, knowledge) = Populate(index, o.KnowledgeDirectory, o.Rag);
            Say($"corpus: {gameDocs} game-data docs, {knowledge.Chunks} knowledge chunks from {knowledge.Files} file(s)");

            var leafTexts = db.Query("SELECT title, body FROM docs WHERE level = 0 ORDER BY id", r => RaptorTreePolicy.LeafText(r.GetString(0), r.GetString(1)));
            decimal projected = Project(leafTexts, o.Embeddings, usage.Prices, o.Llm.CheapModel, raptorOptions);
            Say($"projected spend ≤ ${projected:F4} (cap ${o.MaxCostUsd:F2})");
            if (projected > o.MaxCostUsd)
                throw new SeedBudgetExceededException($"projected spend ${projected:F4} exceeds the ${o.MaxCostUsd:F2} cap; nothing was spent");

            int embedded = await index.FlushEmbeddingsAsync(ct).ConfigureAwait(false);
            long missing = db.ScalarLong("SELECT count(*) FROM docs d LEFT JOIN vectors v ON v.doc_id = d.id WHERE d.embed = 1 AND v.doc_id IS NULL");
            if (missing > 0) throw new InvalidOperationException($"{missing} doc(s) could not be embedded (provider failing?)");
            decimal embedUsd = usage.Today.EmbeddingUsd;
            Say($"embedded {embedded} docs ({usage.Today.EmbeddingTokens} tokens, ${embedUsd:F4})");

            decimal left = o.MaxCostUsd - embedUsd;
            var summarizer = new HaikuSummarizer(llm, o.Llm, usage, maxCostUsd: left);
            var cached = new CachedEmbedder(db, embedder, o.Embeddings, usage);
            var opts = new RaptorMemoryOptions { Maintenance = false, MaxThreads = Environment.ProcessorCount };
            using var memory = new RaptorMemory(db, index, cached, summarizer, opts, raptorOptions);
            var budget = summarizer.Budget(left);
            Say("building RAPTOR tree…");
            var stats = await memory.BuildAsync(budget, ct).ConfigureAwait(false);
            Say($"tree: layers [{string.Join(",", stats.NodesPerLayer)}], {stats.Summaries} summaries, {stats.Placeholders} placeholders, {stats.SummaryFailures} failures");

            var violations = Validate(db);
            var byKind = db.Query("SELECT kind, count(*) FROM docs GROUP BY kind ORDER BY kind", r => (r.GetString(0), (int)r.GetInt64(1)))
                .ToDictionary(x => x.Item1, x => x.Item2);
            var byTag = db.Query("SELECT coalesce(tag, kind), count(*) FROM docs WHERE level = 0 AND raptor_idx IS NOT NULL GROUP BY 1 ORDER BY 1",
                r => (r.GetString(0), (int)r.GetInt64(1))).ToDictionary(x => x.Item1, x => x.Item2);
            decimal totalEmbed = usage.Today.EmbeddingUsd;
            report = new SeedBuildReport(byKind, byTag, stats, projected, usage.Today.EmbeddingTokens, totalEmbed,
                summarizer.Calls, summarizer.InputTokens, summarizer.OutputTokens, summarizer.CostUsd, 0, violations);
            if (violations.Count > 0) throw new InvalidOperationException("seed validation failed: " + string.Join("; ", violations));
            db.Exec("PRAGMA wal_checkpoint(TRUNCATE);");
            db.Exec("PRAGMA journal_mode=DELETE;");
            db.Exec("VACUUM;");
        }
        finally
        {
            db.Dispose();
        }
        File.Move(tmp, output, overwrite: true);
        foreach (var f in new[] { tmp + "-wal", tmp + "-shm" }) if (File.Exists(f)) File.Delete(f);
        return report with { FileBytes = new FileInfo(output).Length };
    }
}
