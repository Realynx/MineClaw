// SeedBuilder — builds data/seed/mineclaw-seed.db (the clean-state knowledge db copied in on first start) and checks it.
//
//   dotnet run --project tools/SeedBuilder              build (real Azure embeddings + Haiku summaries; hard cap $0.50)
//   dotnet run --project tools/SeedBuilder -- --dry-run corpus + spend projection only, no API calls
//   dotnet run --project tools/SeedBuilder -- check     clean start from the seed in a temp folder + recall queries
//
// Config comes from the gitignored src/MineClaw.App/appsettings.json (Agent:Embeddings, Agent:Llm). Keys are never printed.

using System.Diagnostics;
using MineClaw.Agent;
using MineClaw.Agent.Llm;
using MineClaw.Agent.Rag;
using MineClaw.Agent.Rag.Raptor;
using Microsoft.Extensions.Configuration;

var root = FindRoot();
var settings = Path.Combine(root, "src", "MineClaw.App", "appsettings.json");
var config = new ConfigurationBuilder().AddJsonFile(settings, optional: true).AddEnvironmentVariables().Build();
var options = new AgentOptions();
config.GetSection("Agent").Bind(options);
if (string.IsNullOrWhiteSpace(options.Llm.ApiKey) || options.Llm.ApiKey.EndsWith("...", StringComparison.Ordinal))
    options.Llm.ApiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
var seedPath = Path.Combine(root, "data", "seed", "mineclaw-seed.db");
var knowledgeDir = Path.Combine(root, "docs", "knowledge");

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

bool check = args.Contains("check", StringComparer.OrdinalIgnoreCase);
bool dryRun = args.Contains("--dry-run", StringComparer.OrdinalIgnoreCase);
Console.WriteLine($"embeddings: {options.Embeddings.Deployment} @ {options.Embeddings.Dimensions} dims (configured: {options.Embeddings.IsConfigured}); " +
                  $"summaries: {options.Llm.CheapModel} (key set: {!string.IsNullOrWhiteSpace(options.Llm.ApiKey)})");
try
{
    return check ? await CheckAsync() : dryRun ? DryRun() : await BuildAsync();
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("cancelled");
    return 130;
}
catch (SeedBudgetExceededException ex)
{
    Console.Error.WriteLine("ABORTED (budget): " + ex.Message);
    return 3;
}

int DryRun()
{
    var docs = SeedCorpus.Build();
    var texts = docs.Select(d => RaptorTreePolicy.LeafText(d.Title, d.Body)).ToList();
    foreach (var g in docs.GroupBy(d => d.Tag).OrderBy(g => g.Key)) Console.WriteLine($"  {g.Key,-10} {g.Count(),5}");
    var tok = MineClaw.Raptor.Text.ApproxTokenCounter.Instance;
    var counts = texts.Select(tok.Count).ToList();
    Console.WriteLine($"{docs.Count} game-data leaves, tokens avg {counts.Average():F0} max {counts.Max()} total {counts.Sum()}");
    foreach (var d in docs.Where(d => d.Ref is "gd:recipe:61:0:1" or "gd:smelt:12" or "gd:dig:3" or "gd:block:4" or "gd:item:257"))
        Console.WriteLine($"  [{d.Ref}] {d.Title}: {d.Body}");
    var projected = SeedDatabase.Project(texts, options.Embeddings, new MineClaw.Agent.Budget.PriceTable(), options.Llm.CheapModel, new MineClaw.Raptor.RaptorOptions());
    Console.WriteLine($"projected spend (game data only) ≤ ${projected:F4}");
    return 0;
}

async Task<int> BuildAsync()
{
    if (!options.Embeddings.IsConfigured) { Console.Error.WriteLine("Agent:Embeddings is not configured"); return 2; }
    if (string.IsNullOrWhiteSpace(options.Llm.ApiKey)) { Console.Error.WriteLine("Agent:Llm:ApiKey / ANTHROPIC_API_KEY missing"); return 2; }
    var sw = Stopwatch.StartNew();
    using var embedder = new AzureOpenAIEmbeddingProvider(options.Embeddings);
    var llm = new AnthropicLlmClient(options.Llm);
    var report = await SeedDatabase.BuildAsync(new SeedBuildOptions
    {
        OutputPath = seedPath,
        KnowledgeDirectory = knowledgeDir,
        MaxCostUsd = 0.50m,
        Rag = options.Rag,
        Embeddings = options.Embeddings,
        Llm = options.Llm,
        Progress = s => Console.WriteLine($"[{sw.Elapsed:mm\\:ss}] {s}"),
    }, embedder, llm, cts.Token);

    Console.WriteLine();
    Console.WriteLine($"seed written: {seedPath} ({report.FileBytes / 1024.0 / 1024.0:F2} MB)");
    Console.WriteLine("docs by kind: " + string.Join(", ", report.DocsByKind.Select(kv => $"{kv.Key}={kv.Value}")));
    Console.WriteLine("tree leaves by tag: " + string.Join(", ", report.LeavesByTag.Select(kv => $"{kv.Key}={kv.Value}")));
    var t = report.Tree;
    Console.WriteLine($"tree: {t.Leaves} leaves, {t.NumLayers} summary layers, nodes per layer [{string.Join(", ", t.NodesPerLayer)}], " +
                      $"{t.Summaries} summaries, {t.Placeholders} placeholders, {t.SummaryFailures} failures, {t.Reclusters} reclusters");
    Console.WriteLine($"cost: projected ≤ ${report.ProjectedUsd:F4}; embeddings {report.EmbeddingTokens} tok ${report.EmbeddingUsd:F4}; " +
                      $"summaries {report.SummaryCalls} calls {report.SummaryInputTokens}+{report.SummaryOutputTokens} tok ${report.SummaryUsd:F4}; " +
                      $"TOTAL ${report.TotalUsd:F4}");
    Console.WriteLine($"validation: {(report.Violations.Count == 0 ? "clean" : string.Join("; ", report.Violations))}; elapsed {sw.Elapsed:mm\\:ss}");
    return 0;
}

async Task<int> CheckAsync()
{
    if (!File.Exists(seedPath)) { Console.Error.WriteLine("no seed at " + seedPath); return 2; }
    var temp = Path.Combine(Path.GetTempPath(), "mineclaw-seedcheck-" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(temp);
    try
    {
        // Work on a COPY of the live db, then rename the copy away: the start below sees "no db" and bootstraps from the seed.
        var live = Path.Combine(root, "data", "mineclaw.db");
        if (File.Exists(live))
        {
            File.Copy(live, Path.Combine(temp, "mineclaw.db"));
            File.Move(Path.Combine(temp, "mineclaw.db"), Path.Combine(temp, "mineclaw.db.renamed"));
            Console.WriteLine("copied the live db into the temp folder and renamed the copy (the real data/mineclaw.db is untouched)");
        }
        options.DataDirectory = temp;
        options.KnowledgeDirectory = knowledgeDir;
        options.Rag.SeedFile = seedPath;
        options.Rag.Raptor.Maintenance = false;
        options.Self.Enabled = false;
        using var embedder = new AzureOpenAIEmbeddingProvider(options.Embeddings);
        using var rt = AgentRuntime.Create(options, (_, _) => Task.CompletedTask, llm: new NoLlm(), embeddings: embedder);
        if (rt.Raptor == null) { Console.Error.WriteLine("RAPTOR not active (embeddings not configured?)"); return 2; }
        await rt.Raptor.StartLoading(cts.Token);
        var db = rt.Db;
        Console.WriteLine($"clean start from seed: {db.ScalarLong("SELECT count(*) FROM docs")} docs, tree {rt.Raptor.Index.LeafCount} leaves / " +
                          $"{rt.Raptor.Index.NodeCount} nodes / {rt.Raptor.Index.NumLayers} layers, knowledge chunks {rt.KnowledgeChunks}, " +
                          $"re-embedded at start: {rt.Rag.EmbeddedTexts}");

        string[] queries = ["how do I make a furnace", "what smelts into glass", "fastest tool for dirt", "what is a cobblestone generator", "stairs recipe output"];
        foreach (var mode in new[] { RaptorMode.Collapsed, RaptorMode.Traversal })
        {
            rt.Raptor.Mode = mode;
            Console.WriteLine();
            Console.WriteLine($"=== mode: {mode.ToString().ToLowerInvariant()} ===");
            foreach (var q in queries)
            {
                var hits = await rt.Rag.SearchAsync(new RagQuery { Text = q, Limit = 3 }, cts.Token);
                Console.WriteLine($"Q: {q}");
                foreach (var h in hits) Console.WriteLine($"   {h.Score:F4} cos {h.Cosine:F2} lex {h.Lexical:F2}  {rt.Rag.FormatHit(h, 150)}");
                var qv = await rt.Rag.EmbedQueryAsync(q, cts.Token);
                var aug = rt.Raptor.Augment(new RagQuery { Text = q }, qv);
                var tree = aug?.Lists.SelectMany(l => l.Items).Take(3).Select(i => rt.Rag.Get(i.Id) is { } g ? $"{i.Cosine:F2} {g.Ref}" : "?") ?? [];
                Console.WriteLine("   raptor list: " + string.Join(" | ", tree));
            }
        }
        return 0;
    }
    finally
    {
        try { Directory.Delete(temp, true); } catch (IOException) { }
    }
}

static string FindRoot()
{
    for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        if (File.Exists(Path.Combine(dir.FullName, "ARCHITECTURE.md"))) return dir.FullName;
    return Directory.GetCurrentDirectory();
}

/// <summary>The check never talks to Claude.</summary>
sealed class NoLlm : ILlmClient
{
    public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct) =>
        throw new InvalidOperationException("seed check makes no LLM calls");
}
