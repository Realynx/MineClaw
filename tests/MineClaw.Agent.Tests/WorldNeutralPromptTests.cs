using MineClaw.Agent.Admin;
using MineClaw.Agent.Brainstem;
using MineClaw.Agent.Perception;
using MineClaw.Agent.Rag;
using MineClaw.Data.Versioning;

namespace MineClaw.Agent.Tests;

/// <summary>
/// Multi-version P6: the hard-coded prompt text is world- and version-neutral; skyblock and b1.8.1 facts reach the cached prompt
/// only through the active packs' knowledge cores. A 1.12.2 bot's brain/worker prompts carry no skyblock text, and a b1.8.1
/// skyblock bot still gets the void and island rules (from skyblock_b181.md's core).
/// </summary>
public sealed class WorldNeutralPromptTests : IDisposable
{
    private static readonly string[] SkyblockWords = ["skyblock", "Beta 1.8.1", "the void is below", "island", "no bridges"];

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mineclaw-neutral-" + Guid.NewGuid().ToString("N")[..8]);

    public WorldNeutralPromptTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static string KnowledgeDir()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "MineClaw.sln"))) d = d.Parent;
        return Path.Combine(d?.FullName ?? throw new InvalidOperationException("repo root not found"), "docs", "knowledge");
    }

    /// <summary>The core a bot of <paramref name="version"/> gets from a legacy (b1.8.1 + every mode doc) db.</summary>
    private string Core(string? version)
    {
        var path = Path.Combine(_dir, (version ?? "default") + ".db");
        using var ctx = MemoryAdminContext.OpenLocal(path, new AgentOptions { KnowledgeDirectory = null, DataDirectory = _dir },
            NullEmbeddingProvider.Instance, llm: null);
        return KnowledgeIndexer.Sync(ctx.Rag, KnowledgeDir(), new RagOptions(), version).Core;
    }

    private static IEnumerable<string> CachedPrompts(string core) =>
    [
        Prompts.System("", "", core, retrieval: true, delegation: true),
        Prompts.System("", "", core, retrieval: true, delegation: false),
        Prompts.WorkerSystem(core),
        Prompts.MemorySystem, Prompts.BodiesHint, Prompts.HiveMainHint, PerceptionPrompt.System,
        QuickReply.TriageSystem("maid", "", null, 12),
    ];

    [Fact]
    public void A_1122_bot_gets_no_skyblock_or_b181_text_in_any_cached_prompt()
    {
        var core = Core("1.12.2");
        Assert.Contains("Core rules (1.12.2)", core);
        foreach (var prompt in CachedPrompts(core))
            foreach (var w in SkyblockWords)
                Assert.DoesNotContain(w, prompt, StringComparison.OrdinalIgnoreCase);
        // The neutral safety rule still stands in both tiers.
        Assert.Contains("Never dig the block you stand on near an edge", Prompts.System("", "", core, retrieval: true, delegation: true));
        Assert.Contains("Never dig the block you stand on near an edge", Prompts.WorkerSystem(core));
    }

    [Fact]
    public void A_b181_skyblock_bot_still_gets_the_void_and_island_rules_from_its_core()
    {
        var core = Core(null);
        Assert.Contains("Core rules (b1.8.1)", core);
        foreach (var prompt in new[] { Prompts.System("", "", core, retrieval: true, delegation: true), Prompts.WorkerSystem(core) })
        {
            Assert.Contains("# World core rules", prompt);
            Assert.Contains("The void is below", prompt);
            Assert.Contains("don't walk off the island", prompt);
            Assert.Contains("no bridges to land", prompt);
            Assert.Contains("never dig the block you stand on", prompt);
            Assert.Contains("the one bucket", prompt);
            Assert.Contains("Cobble generator", prompt);
        }
    }

    [Theory]
    [InlineData("mine 64 cobble")]
    [InlineData("get some stone")]
    public void Mining_stone_is_a_gen_job_only_in_a_skyblock_world(string line)
    {
        Assert.Equal(IntentKind.SpecGenerator, IntentMatcher.Speculative(line).Kind);
        using (WorldMode.Override(WorldMode.Normal))
            Assert.NotEqual(IntentKind.SpecGenerator, IntentMatcher.Speculative(line).Kind);
        using (WorldMode.Override(WorldMode.Normal))
            Assert.Equal(IntentKind.SpecGenerator, IntentMatcher.Speculative("go work the cobble gen").Kind);
    }
}
