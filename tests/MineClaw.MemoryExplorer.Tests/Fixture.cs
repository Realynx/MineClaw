using System.Text.RegularExpressions;
using MineClaw.Agent;
using MineClaw.Agent.Memory;
using MineClaw.Agent.Rag;
using MineClaw.Agent.Rag.Raptor;
using MineClaw.Raptor;
using MineClaw.MemoryExplorer.Services;

namespace MineClaw.MemoryExplorer.Tests;

/// <summary>Deterministic bag-of-words embedding provider (no network): texts sharing words are similar.</summary>
public sealed partial class HashEmbeddings(int dims = 48) : IEmbeddingProvider
{
    public string Model => "fake-hash";
    public int Dimensions => dims;
    public bool IsAvailable => true;
    public int Calls;

    [GeneratedRegex("[a-z0-9]+")]
    private static partial Regex Word();

    public float[] Embed(string text)
    {
        var v = new float[dims];
        foreach (Match m in Word().Matches(text.ToLowerInvariant()))
        {
            uint h = 2166136261;
            foreach (var c in m.Value) { h ^= c; h *= 16777619; }
            var r = new Random((int)h);
            for (int i = 0; i < dims; i++) v[i] += (float)(r.NextDouble() * 2 - 1);
        }
        return VectorMath.Normalize(v);
    }

    public Task<EmbeddingResponse> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken ct)
    {
        Interlocked.Increment(ref Calls);
        return Task.FromResult(new EmbeddingResponse(inputs.Select(Embed).ToList(), inputs.Sum(t => (long)t.Length / 4)));
    }
}

/// <summary>Extractive fake summarizer: the first words of the cluster.</summary>
public sealed class FirstWordsSummarizer : ISummarizer
{
    public int Calls;
    public Task<SummaryResult> SummarizeAsync(SummaryRequest request, CancellationToken ct)
    {
        Interlocked.Increment(ref Calls);
        var words = request.Context.Split((char[])[' ', '\n'], StringSplitOptions.RemoveEmptyEntries).Take(24);
        return Task.FromResult(new SummaryResult("summary: " + string.Join(' ', words), 10, 10));
    }
}

/// <summary>
/// Builds a small but real MineClaw db the way the bot does: notes through MemoryStore, waypoints, structures, episodes and
/// knowledge docs through RagIndex, flat vectors from the fake provider, then a faithful RAPTOR build (fake summarizer).
/// </summary>
public sealed class FixtureDb : IDisposable
{
    public string Dir { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mineclaw-explorer-tests", Guid.NewGuid().ToString("N"));
    public string Path => System.IO.Path.Combine(Dir, "fixture.db");
    public int TreeNodes { get; private set; }
    public int TreeLayers { get; private set; }

    private static readonly string[] Topics =
    [
        "cobblestone generator lava water flowing cobble mining",
        "wheat farm hoe seeds bonemeal harvest crops",
        "oak tree sapling logs planks chop wood",
        "chest storage sorted items deposit withdraw",
        "nether portal obsidian flint steel netherrack",
        "mob grinder zombies skeletons drop arrows bones",
    ];

    public static async Task<FixtureDb> CreateAsync()
    {
        var f = new FixtureDb();
        Directory.CreateDirectory(f.Dir);
        await f.BuildAsync();
        return f;
    }

    private async Task BuildAsync()
    {
        var provider = new HashEmbeddings();
        var options = new AgentOptions();
        using var db = AgentDb.Open(Path);
        using var rag = new RagIndex(db, options.Rag, provider, embeddingOptions: options.Embeddings, backgroundEmbedding: false);
        using var memory = new MemoryStore(db, rag);
        memory.AddNote("PoofimaFox", "Fox likes the cobblestone generator by the spawn island");
        memory.AddNote("world", "the melon farm needs water on both sides");
        memory.SetWaypoint("spawn", 4, 64, 6, "main island", dimension: 0);
        memory.SetWaypoint("nether hub", 10, 70, -3, null, dimension: -1);
        memory.UpsertStructure("cobble gen", s => { s.Kind = StructureKinds.CobbleGenerator; s.Steps.Add("stand at 3,65,-2"); s.Min = new BlockPos(3, 64, -2); });
        int n = 0;
        foreach (var topic in Topics)
            for (int i = 0; i < 7; i++)
            {
                rag.Upsert(new RagDoc($"ep:fixture:{n}", "episode", $"episode {n} {topic.Split(' ')[0]}", $"{topic} event {i} note number {n}"));
                n++;
            }
        rag.Upsert(new RagDoc("kb:fixture.md:0", "knowledge", "Skyblock basics", "Never break the only dirt block. The cobblestone generator is lava next to water."));
        await rag.FlushEmbeddingsAsync();
        using var raptor = new RaptorMemory(db, rag, new CachedEmbedder(db, provider, options.Embeddings), new FirstWordsSummarizer(), options.Rag.Raptor);
        await raptor.BuildAsync();
        TreeNodes = raptor.Index.NodeCount;
        TreeLayers = raptor.Index.NumLayers;
    }

    public static ExplorerConfig Config() => ExplorerConfig.FromOptions(new AgentOptions());

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(Dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
