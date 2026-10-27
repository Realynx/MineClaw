using System.Diagnostics;
using Xunit.Abstractions;

namespace MineClaw.Raptor.Tests;

/// <summary>
/// Traversal vs collapsed tree on a synthetic corpus with exact ground truth (each query targets one leaf).
/// 1k runs in the normal suite; 10k and 50k run when RAPTOR_BENCH=1. Results are also appended to
/// %TEMP%/raptor-bench.txt.
/// </summary>
public class RetrievalBenchmarks(ITestOutputHelper output)
{
    public static bool BenchEnabled => Environment.GetEnvironmentVariable("RAPTOR_BENCH") == "1";

    [Fact]
    public Task Bench_1k() => RunAsync(1_000);

    [Fact]
    public Task Bench_10k() => BenchEnabled ? RunAsync(10_000) : Task.CompletedTask;

    [Fact]
    public Task Bench_50k() => BenchEnabled ? RunAsync(50_000) : Task.CompletedTask;

    private async Task RunAsync(int n)
    {
        var embedder = new HashingEmbedder(256);
        var corpus = new SyntheticCorpus(n, Math.Max(5, n / 200), 5, seed: 42);
        var options = new RaptorOptions(); // paper defaults, full parallelism
        using var index = new RaptorIndex(embedder, new KeywordSummarizer(), options);
        var build = await index.BuildAsync(corpus.Documents());
        Log($"n={n} build {build.Elapsed.TotalSeconds:F1}s (cluster {build.ClusteringTime.TotalSeconds:F1}s) layers=[{string.Join(",", build.NodesPerLayer)}] summaries={build.Summaries} reclusters={build.Reclusters}");

        int queries = 200;
        var rng = new Numerics.Rng(7);
        var targets = Enumerable.Range(0, queries).Select(_ => corpus.Leaves[rng.NextInt(n)]).ToList();
        var vectors = targets.Select(t => embedder.Embed(t.Query)).ToList();

        var configs = new (string Name, RaptorQuery Q)[]
        {
            ("collapsed 2000 tok", new RaptorQuery { Collapsed = true }),
            ("collapsed top-5", new RaptorQuery { Collapsed = true, TopK = 5, MaxTokens = int.MaxValue }),
            ("collapsed top-10", new RaptorQuery { Collapsed = true, TopK = 10, MaxTokens = int.MaxValue }),
            ("traversal top-5", new RaptorQuery { Collapsed = false, TopK = 5 }),
            ("traversal top-10", new RaptorQuery { Collapsed = false, TopK = 10 }),
        };
        foreach (var (name, q) in configs)
        {
            // warm-up
            index.Retrieve(vectors[0], q);
            int hits = 0;
            long nodes = 0, tokens = 0;
            var lat = new List<double>(queries);
            for (int i = 0; i < queries; i++)
            {
                var sw = Stopwatch.StartNew();
                var r = index.Retrieve(vectors[i], q);
                lat.Add(sw.Elapsed.TotalMilliseconds);
                if (r.Nodes.Any(x => x.Key == $"leaf:{targets[i].Id}")) hits++;
                nodes += r.Nodes.Count;
                tokens += r.Tokens;
            }
            lat.Sort();
            Log($"n={n} {name,-20} recall={hits / (double)queries:P1} nodes={nodes / (double)queries:F1} tokens={tokens / (double)queries:F0} latency mean={lat.Average():F2}ms p95={lat[(int)(0.95 * (lat.Count - 1))]:F2}ms");
            if (name.StartsWith("collapsed 2000")) Assert.True(hits >= 0.9 * queries);
        }
    }

    private void Log(string line)
    {
        output.WriteLine(line);
        try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "raptor-bench.txt"), $"{DateTime.Now:HH:mm:ss} {line}{Environment.NewLine}"); } catch { }
    }
}
