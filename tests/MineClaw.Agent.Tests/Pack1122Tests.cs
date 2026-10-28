using MineClaw.Agent.Admin;
using MineClaw.Agent.Admin.Shards;
using MineClaw.Agent.Admin.Shards.Packs;
using MineClaw.Agent.Rag;
using MineClaw.Agent.Rag.Raptor;

namespace MineClaw.Agent.Tests;

/// <summary>
/// The 1.12.2 version pack (shards/defaults/1.12.2-gamedata.mcshard): built deterministically from the vendored minecraft-data, the
/// shards/sources/1.12.2 facts (smelting, fuels, mobs) and docs/knowledge/mechanics_1122.md; the committed file is exactly what the
/// sources build; it round-trips into a fresh db and answers the everyday questions; and a 1.12.2 db never reads the b1.8.1 or
/// skyblock docs. Offline (lexical retrieval).
/// </summary>
public sealed class Pack1122Tests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mineclaw-p1122-" + Guid.NewGuid().ToString("N")[..8]);

    public Pack1122Tests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static string Root()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "MineClaw.sln"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("repo root not found");
    }

    private static string KnowledgeDir => Path.Combine(Root(), "docs", "knowledge");
    private static string Committed => Path.Combine(Root(), "shards", "defaults", "1.12.2-gamedata.mcshard");

    /// <summary>Exactly what <c>tools/ShardPacks -- build --version 1.12.2</c> builds (minus the sdk-js pack, covered in Bot.Tests).</summary>
    private PackBuildResult Build(string outName) => DefaultPackBuilder.Build(new PackBuildOptions
    {
        Source = new MinecraftDataSource(Path.Combine(Root(), "third_party", "minecraft-data", "pc", "1.12.2"), "1.12.2",
            factsDirectory: Path.Combine(Root(), "shards", "sources", "1.12.2"),
            tables: MineClaw.Data.Versioning.Release1122GameData.Instance), // the CLI's drop facts come from the bot's own tables
        KnowledgeDirectory = KnowledgeDir,
        OutputDirectory = Path.Combine(_dir, outName),
        SeedPath = Path.Combine(Root(), "data", "seed", "mineclaw-seed.db"),
    });

    private (MemoryAdminContext Ctx, ShardImporter Importer) Open(string name)
    {
        var path = Path.Combine(_dir, name + ".db");
        var ctx = MemoryAdminContext.OpenLocal(path, new AgentOptions { KnowledgeDirectory = null, DataDirectory = _dir }, NullEmbeddingProvider.Instance, llm: null);
        return (ctx, new ShardImporter(ctx, new AdminAudit(Path.Combine(_dir, name + ".audit.jsonl")), "local:" + path));
    }

    [Fact]
    public void The_1122_pack_builds_deterministically_and_the_committed_file_is_current()
    {
        var a = Build("a");
        var b = Build("b");
        var entry = Assert.Single(a.Packs); // no 1.12.2 mode docs: a plain world's pack only
        Assert.Equal("1.12.2-gamedata.mcshard", entry.File);
        Assert.Equal(File.ReadAllBytes(Path.Combine(_dir, "a", entry.File)), File.ReadAllBytes(Path.Combine(_dir, "b", entry.File)));
        Assert.Equal(entry.ContentHash, Assert.Single(b.Packs).ContentHash);
        // shards/defaults holds exactly this build (ShardPacks verify fails otherwise).
        Assert.True(File.ReadAllBytes(Committed).AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(_dir, "a", entry.File))),
            "shards/defaults/1.12.2-gamedata.mcshard is stale: run dotnet run --project tools/ShardPacks -- build --version 1.12.2");

        var pack = ShardFile.Read(Path.Combine(_dir, "a", entry.File));
        Assert.Equal("1.12.2", pack.Manifest.PackVersion);
        Assert.Equal(ShardPackInfo.VersionKind, pack.Manifest.Pack!.Kind);
        Assert.Equal(["mechanics_1122.md"], pack.Manifest.Pack.Files);
        Assert.Contains("minecraft-data pc/1.12.2", pack.Manifest.Pack.Source);
        Assert.Contains("smelting + mobs facts", pack.Manifest.Pack.Source);
        Assert.Contains("1.12.2", pack.Manifest.Pack.Core);
        Assert.DoesNotContain("void", pack.Manifest.Pack.Core, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(pack.Records.Count(r => r.Get("embed") != "0" && (r.DocHash == null || !pack.Embeddings.ContainsKey(r.DocHash))), pack.Manifest.Pack.NeedsEmbedding);
        Assert.True(pack.Manifest.Pack.NeedsEmbedding > 0); // no 1.12.2 seed: the target embeds on import

        string Body(string id) => pack.Records.Single(r => r.Id == id).Get("body")!;
        int Count(string prefix) => pack.Records.Count(r => r.Id.StartsWith(prefix, StringComparison.Ordinal));
        Assert.True(Count("gd:recipe:") > 400, $"{Count("gd:recipe:")} recipes");
        Assert.True(Count("gd:smelt:") >= 60);
        Assert.True(Count("gd:mob:") >= 45);
        Assert.Contains("4 Wooden Planks", Body("gd:recipe:58:0:1"));
        Assert.Contains("Oak Wood Planks", Body("gd:recipe:5:0:1")); // variants named: oak log → oak planks
        Assert.Contains("3 Iron Ingot, 2 Stick", Body("gd:recipe:257:0:1"));
        Assert.Contains("Needs a crafting table", Body("gd:recipe:257:0:1"));
        Assert.Contains("3 Wheat", Body("gd:recipe:297:0:1"));
        Assert.Contains("Raw Beef (beef) smelts into Steak (cooked_beef)", Body("gd:smelt:363"));
        Assert.Contains("Food: restores 8 hunger", Body("gd:item:364"));
        Assert.Contains("Food: restores 2 hunger", Body("gd:item:360")); // melon slice, listed under the melon block's id in minecraft-data
        Assert.Contains("Charcoal", Body("gd:item:263"));
        Assert.Contains("Needs at least a Stone Pickaxe", Body("gd:block:15")); // iron ore: the lowest tier that drops it
        Assert.Contains("Needs at least a Iron Pickaxe", Body("gd:block:56")); // diamond ore
        Assert.Contains("Needs at least a Wooden Pickaxe", Body("gd:block:1"));
        Assert.Contains("Rotten Flesh", Body("gd:mob:zombie"));
        Assert.Contains("Health 20", Body("gd:mob:zombie"));
        Assert.Contains("1600 ticks", Body("gd:fuel:263"));
        Assert.All(pack.Records.Where(r => r.Kind == ShardKinds.Knowledge), r => Assert.StartsWith("kb:mechanics_1122.md:", r.Id));
    }

    [Fact]
    public async Task The_1122_pack_round_trips_into_a_fresh_db_and_answers_everyday_questions()
    {
        var pack = ShardFile.Read(Committed);
        var (ctx, importer) = Open("fresh");
        using (ctx)
        {
            var res = await importer.ApplyAsync(pack, new ShardImportOptions { Mode = ShardImportMode.SwapPack });
            Assert.True(res.Ok, res.Message);
            Assert.Equal("1.12.2", KnowledgePack.ActiveVersion(ctx.Db));
            Assert.Contains("1.12.2", KnowledgePack.PromptLine(ctx.Db));
            Assert.Equal(pack.Records.Count, ctx.Db.ScalarLong("SELECT count(*) FROM docs WHERE level = 0 AND (ref LIKE 'gd:%' OR ref LIKE 'kb:%')")); // + built-in correction docs

            // The index's own ranking (what recall and auto-retrieval hand the bot), top n.
            List<string> Top(string q, string kind, int n = 3) => ctx.Rag.Search(new RagQuery { Text = q, Kinds = [kind], Limit = n }).Select(h => h.Ref).ToList();
            Assert.Contains("gd:recipe:58:0:1", Top("crafting table recipe", SeedCorpus.Kind));
            Assert.Contains("gd:recipe:257:0:1", Top("iron pickaxe recipe", SeedCorpus.Kind));
            Assert.Contains("gd:recipe:297:0:1", Top("bread recipe", SeedCorpus.Kind));
            Assert.Contains("gd:smelt:363", Top("smelt cooked beef", SeedCorpus.Kind));
            // The doc's top two chunks (the built-in stale-lesson corrections share the kind; they're not the pack's).
            string Kb(string q) => string.Join(" | ", Top(q, KnowledgeIndexer.Kind, 8).Where(r => r.StartsWith("kb:mechanics_1122.md:", StringComparison.Ordinal))
                .Take(2).Select(r => r + ": " + ctx.Rag.GetByRef(r)!.Body));
            Assert.Contains("cooldown", Kb("attack cooldown"), StringComparison.OrdinalIgnoreCase);
            Assert.Matches(@"light (level )?(7|0-7|≤ ?7|of 7)", Kb("hostile mob spawn light level"));
        }
    }

    [Fact]
    public async Task A_1122_db_indexes_only_its_own_docs_and_core_lines()
    {
        var (ctx, importer) = Open("plain");
        using (ctx)
        {
            Assert.True((await importer.ApplyAsync(ShardFile.Read(Committed), new ShardImportOptions { Mode = ShardImportMode.SwapPack })).Ok);
            KnowledgePack.SetActive(ctx.Db, KnowledgePack.Active(ctx.Db) with { Modes = [] });
            var k = KnowledgeIndexer.Sync(ctx.Rag, KnowledgeDir, new RagOptions(), "1.12.2");
            Assert.Null(k.VersionMismatch);
            Assert.Contains("mechanics_b181.md", k.Excluded);
            Assert.Contains("skyblock_b181.md", k.Excluded);
            Assert.Contains("Core rules (1.12.2)", k.Core);
            Assert.DoesNotContain("b1.8.1", k.Core.Replace("1.12.2", ""));
            Assert.DoesNotContain("The void is below", k.Core);
            Assert.Equal(0, ctx.Db.ScalarLong("SELECT count(*) FROM docs WHERE ref LIKE 'kb:mechanics_b181.md:%' OR ref LIKE 'kb:skyblock_b181.md:%'"));
            Assert.True(ctx.Db.ScalarLong("SELECT count(*) FROM docs WHERE ref LIKE 'kb:mechanics_1122.md:%'") > 3);
            // No b1.8.1 game data: every gd doc is the 1.12.2 pack's (b1.8.1 smelting glass is gd:smelt:12 with its "beta" wording).
            Assert.DoesNotContain("beta 1.8.1", string.Join("\n", ctx.Db.Query("SELECT body FROM docs WHERE kind = 'gamedata'", r => r.GetString(0))),
                StringComparison.OrdinalIgnoreCase);

            // The RAG-off path filters the same way.
            var all = Prompts.LoadKnowledge(KnowledgeDir, 1_000_000, out _, KnowledgePack.Active(ctx.Db));
            Assert.Contains("Core rules (1.12.2)", all);
            Assert.DoesNotContain("SkyBlock", all);
            Assert.DoesNotContain("Beta 1.8.1 mechanics", all);
        }
    }

    [Fact]
    public void A_1122_bot_on_a_legacy_b181_db_reads_only_1122_docs_and_says_so()
    {
        var (ctx, _) = Open("legacy");
        using (ctx)
        {
            var b181 = KnowledgeIndexer.Sync(ctx.Rag, KnowledgeDir, new RagOptions()); // a b1.8.1 bot: unchanged
            Assert.Contains("mechanics_1122.md", b181.Excluded);
            Assert.Contains("The void is below", b181.Core);

            var k = KnowledgeIndexer.Sync(ctx.Rag, KnowledgeDir, new RagOptions(), "1.12.2");
            Assert.NotNull(k.VersionMismatch);
            Assert.Contains("1.12.2", k.VersionMismatch);
            Assert.Contains("mechanics_b181.md", k.Excluded);
            Assert.Contains("skyblock_b181.md", k.Excluded);
            Assert.DoesNotContain("The void is below", k.Core);
            Assert.Contains("Core rules (1.12.2)", k.Core);
            Assert.Equal(0, ctx.Db.ScalarLong("SELECT count(*) FROM docs WHERE ref LIKE 'kb:skyblock_b181.md:%'"));
        }
    }

    [Fact]
    public void Version_independent_packs_apply_in_every_world()
    {
        Assert.True(KnowledgePack.Matches(KnowledgePack.AnyVersion, "1.12.2"));
        Assert.True(KnowledgePack.Matches(null, "b1.8.1"));
        Assert.False(KnowledgePack.Matches("b1.8.1", "1.12.2"));
        var sdk = new KnowledgeDocPack(KnowledgePack.AnyVersion, "sdk-js", "sdk-js");
        Assert.True(sdk.ActiveIn(new KnowledgePackInfo("1.12.2") { Modes = [new KnowledgeModeInfo("sdk-js")] }));
        Assert.False(sdk.ActiveIn(new KnowledgePackInfo("1.12.2") { Modes = [] }));
        var stored = KnowledgePack.StoredCores(new KnowledgePackInfo("1.12.2")
        {
            Modes = [new KnowledgeModeInfo("sdk-js", Version: KnowledgePack.AnyVersion) { Core = "- sdk line", Files = ["sdk-js.md"] }],
        }, new HashSet<string>()).ToList();
        Assert.Equal(["- sdk line"], stored);
    }
}
