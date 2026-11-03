using System.Text.RegularExpressions;
using MineClaw.Agent;
using MineClaw.Agent.Memory;
using MineClaw.Agent.Rag;
using MineClaw.Agent.Rag.Raptor;
using MineClaw.Raptor;

namespace MineClaw.ViewerBridge.Tests;

/// <summary>Deterministic bag-of-words embedding provider (no network): texts sharing words are similar.</summary>
public sealed partial class HashEmbeddings(int dims = 48) : IEmbeddingProvider
{
    public string Model => "fake-hash";
    public int Dimensions => dims;
    public bool IsAvailable => true;

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

    public Task<EmbeddingResponse> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken ct) =>
        Task.FromResult(new EmbeddingResponse(inputs.Select(Embed).ToList(), inputs.Sum(t => (long)t.Length / 4)));
}

/// <summary>Extractive fake summarizer: the first words of the cluster.</summary>
public sealed class FirstWordsSummarizer : ISummarizer
{
    public Task<SummaryResult> SummarizeAsync(SummaryRequest request, CancellationToken ct)
    {
        var words = request.Context.Split((char[])[' ', '\n'], StringSplitOptions.RemoveEmptyEntries).Take(24);
        return Task.FromResult(new SummaryResult("summary: " + string.Join(' ', words), 10, 10));
    }
}

/// <summary>
/// A small but real MineClaw db, built the way the bot builds it (tools/MemoryExplorer tests' fixture): notes, waypoints and a
/// structure through MemoryStore, episodes and a knowledge doc through RagIndex, flat vectors from the fake provider, a faithful
/// RAPTOR build (fake summarizer), plus 250 chat lines, claims, self-profile rows and a design with two revisions (plain SQL in
/// the bot's own schema).
/// </summary>
public sealed class FixtureDb : IDisposable
{
    public string Dir { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mineclaw-bridge-tests", Guid.NewGuid().ToString("N"));
    public string Path => System.IO.Path.Combine(Dir, "fixture.db");
    public int TreeNodes { get; private set; }

    private static readonly string[] Topics =
    [
        "cobblestone generator lava water flowing cobble mining",
        "wheat farm hoe seeds bonemeal harvest crops",
        "oak tree sapling logs planks chop wood",
        "chest storage sorted items deposit withdraw",
        "nether portal obsidian flint steel netherrack",
        "mob grinder zombies skeletons drop arrows bones",
    ];

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static FixtureDb? _shared;

    /// <summary>One fixture for the whole test run (read-only use).</summary>
    public static async Task<FixtureDb> SharedAsync()
    {
        await Gate.WaitAsync();
        try { return _shared ??= await CreateAsync(); }
        finally { Gate.Release(); }
    }

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
        using (var raptor = new RaptorMemory(db, rag, new CachedEmbedder(db, provider, options.Embeddings), new FirstWordsSummarizer(), options.Rag.Raptor))
        {
            await raptor.BuildAsync();
            TreeNodes = raptor.Index.NodeCount;
        }
        db.Tx(() =>
        {
            for (int i = 0; i < 250; i++)
                db.Exec("INSERT INTO chat_log(at, kind, player, text) VALUES($a, 'chat', $p, $t)", ("$a", 1_762_000_000_000L + i * 1000), ("$p", i % 2 == 0 ? "poofimafox" : "kiwi"),
                    ("$t", $"line {i} {(i % 10 == 0 ? "cobble" : "hello")}"));
        });
        db.Exec("""
            CREATE TABLE IF NOT EXISTS blueprints(id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL, description TEXT NOT NULL, requester TEXT, bot TEXT NOT NULL,
                dsl TEXT NOT NULL, status TEXT NOT NULL, dim INTEGER NOT NULL, sx INTEGER, sy INTEGER, sz INTEGER, ox INTEGER, oy INTEGER, oz INTEGER,
                rev INTEGER NOT NULL DEFAULT 0, summary TEXT, eval TEXT, critique TEXT, bill TEXT, cost_usd REAL NOT NULL DEFAULT 0, iterations INTEGER NOT NULL DEFAULT 0,
                accepted_by TEXT, png BLOB, created INTEGER NOT NULL, updated INTEGER NOT NULL, anchor TEXT);
            CREATE TABLE IF NOT EXISTS blueprint_revisions(blueprint_id INTEGER NOT NULL, rev INTEGER NOT NULL, author TEXT NOT NULL, author_kind TEXT NOT NULL,
                message TEXT NOT NULL, tag TEXT, created INTEGER NOT NULL, dim INTEGER NOT NULL, dsl TEXT NOT NULL, ox INTEGER, oy INTEGER, oz INTEGER,
                blocks TEXT NOT NULL, air TEXT NOT NULL, n INTEGER NOT NULL, hash TEXT NOT NULL, PRIMARY KEY(blueprint_id, rev));
            INSERT INTO blueprints(name, description, requester, bot, dsl, status, dim, created, updated, png)
                VALUES('spiral_staircase', 'captured', 'PoofImaFox', 'maid_chan', 'blueprint x', 'captured', 0, 1, 2, X'89504E47');
            """);
        var r1 = new MineClaw.World.Designs.DesignSnapshot();
        for (int y = 64; y <= 66; y++) r1.Blocks[new MineClaw.World.BlockPos(0, y, 0)] = (4, 0);
        var r2 = r1.Clone();
        r2.Blocks[new MineClaw.World.BlockPos(1, 64, 0)] = (5, 0);
        r2.Blocks.Remove(new MineClaw.World.BlockPos(0, 66, 0));
        foreach (var (rev, s) in new[] { (1, r1), (2, r2) })
            db.Exec("INSERT INTO blueprint_revisions VALUES(1,$r,'PoofImaFox','player',$m,NULL,$c,0,'',0,64,0,$b,$air,$n,$h)", ("$r", rev), ("$m", "rev " + rev),
                ("$c", 1000 * rev), ("$b", MineClaw.World.Designs.DesignSnapshot.EncodeStates(s.Blocks)), ("$air", MineClaw.World.Designs.DesignSnapshot.EncodeCells(s.Air)),
                ("$n", s.Count), ("$h", s.Hash()));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(Dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
