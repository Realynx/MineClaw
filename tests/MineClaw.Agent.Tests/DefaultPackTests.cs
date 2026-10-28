using System.IO.Compression;
using System.Text;
using MineClaw.Agent.Admin;
using MineClaw.Agent.Admin.Shards;
using MineClaw.Agent.Admin.Shards.Packs;
using MineClaw.Agent.Rag;
using MineClaw.Agent.Rag.Raptor;

namespace MineClaw.Agent.Tests;

/// <summary>
/// Default knowledge packs (shards/defaults, tools/ShardPacks): reproducible builds from pristine sources, the b1.8.1 version pack
/// round-tripping into a fresh db with the seed's answers, the skyblock mode pack adding/removing its core lines, the minecraft-data
/// adapter on a hand-written fixture (tests/fixtures/minecraft-data, PrismarineJS layout), and no secrets in a pack. Offline.
/// </summary>
public sealed class DefaultPackTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mineclaw-packs-" + Guid.NewGuid().ToString("N"));

    public DefaultPackTests() => Directory.CreateDirectory(_dir);

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
    private static string SeedPath => Path.Combine(Root(), "data", "seed", "mineclaw-seed.db");

    private PackBuildResult BuildB181(string outName, bool vectors = true, string? knowledge = null) => DefaultPackBuilder.Build(new PackBuildOptions
    {
        Source = new MineClawDataSource(),
        KnowledgeDirectory = knowledge ?? KnowledgeDir,
        OutputDirectory = Path.Combine(_dir, outName),
        SeedPath = vectors ? SeedPath : null,
        IncludeVectors = vectors,
    });

    private static AgentOptions Options() => new() { KnowledgeDirectory = null, DataDirectory = "unused" };

    private (MemoryAdminContext Ctx, ShardImporter Importer) Open(string name, IEmbeddingProvider? emb = null)
    {
        var path = Path.Combine(_dir, name + ".db");
        var ctx = MemoryAdminContext.OpenLocal(path, Options(), emb ?? NullEmbeddingProvider.Instance, llm: null);
        return (ctx, new ShardImporter(ctx, new AdminAudit(Path.Combine(_dir, name + ".audit.jsonl")), "local:" + path));
    }

    // ======================= reproducible build =======================

    [Fact]
    public void Two_builds_from_the_same_sources_are_byte_identical_and_split_by_concern()
    {
        var a = BuildB181("a");
        var b = BuildB181("b");
        var files = Directory.GetFiles(Path.Combine(_dir, "a")).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(["b1.8.1-gamedata.mcshard", "index.json", "skyblock.mcshard"], files);
        foreach (var f in files)
            Assert.Equal(File.ReadAllBytes(Path.Combine(_dir, "a", f!)), File.ReadAllBytes(Path.Combine(_dir, "b", f!)));
        Assert.Equal(a.Packs.Select(p => p.ContentHash), b.Packs.Select(p => p.ContentHash));

        var version = ShardFile.Read(Path.Combine(_dir, "a", "b1.8.1-gamedata.mcshard"));
        Assert.Equal("pack", version.Manifest.Kind);
        Assert.Equal("b1.8.1", version.Manifest.PackVersion);
        Assert.Equal(ShardPackInfo.VersionKind, version.Manifest.Pack!.Kind);
        Assert.Equal(SeedCorpus.At, version.Manifest.Created); // no wall clock anywhere
        Assert.Equal(SeedCorpus.Build().Count, version.Manifest.Counts[ShardKinds.GameData]);
        Assert.Equal(["mechanics_b181.md"], version.Manifest.Pack.Files);
        Assert.All(version.Records.Where(r => r.Kind == ShardKinds.Knowledge), r => Assert.StartsWith("kb:mechanics_b181.md:", r.Id));
        Assert.Contains("Health regenerates only while food is 18+", version.Manifest.Pack.Core);
        Assert.DoesNotContain("void", version.Manifest.Pack.Core, StringComparison.OrdinalIgnoreCase); // no skyblock advice in a plain world
        // The seed's vectors ride along (same model + dims), so a default import costs no embedding calls for those docs.
        Assert.Equal("text-embedding-3-large", version.Manifest.Embeddings!.Model);
        Assert.True(version.Embeddings.Count > 400);
        Assert.Equal(version.Records.Count(r => r.Get("embed") != "0" && !version.Embeddings.ContainsKey(r.DocHash!)), version.Manifest.Pack.NeedsEmbedding);

        var mode = ShardFile.Read(Path.Combine(_dir, "a", "skyblock.mcshard"));
        Assert.Equal("skyblock", mode.Manifest.Pack!.Mode);
        Assert.Equal(ShardPackInfo.ModeKind, mode.Manifest.Pack.Kind);
        Assert.False(mode.Manifest.Counts.ContainsKey(ShardKinds.GameData));
        Assert.All(mode.Records, r => Assert.StartsWith("kb:skyblock_b181.md:", r.Id));
        Assert.Contains("The void is below", mode.Manifest.Pack.Core);
        Assert.DoesNotContain("Health regenerates", mode.Manifest.Pack.Core);

        var index = PackIndex.Load(Path.Combine(_dir, "a"));
        Assert.Equal(2, index.Packs.Count);
        var vi = index.Packs.Single(p => p.Kind == "version");
        Assert.Equal(new FileInfo(Path.Combine(_dir, "a", vi.File)).Length, vi.Bytes);
        Assert.Equal(DefaultPackBuilder.BuilderVersion, vi.Builder);
        Assert.NotNull(vi.SourceHash);
        Assert.True(vi.Bytes < 20L * 1024 * 1024);
    }

    [Fact]
    public void Without_a_seed_the_packs_carry_no_vectors_and_say_so()
    {
        var r = BuildB181("novec", vectors: false);
        var p = r.Packs.Single(x => x.Kind == "version");
        Assert.Equal(0, p.Vectors);
        Assert.Equal(p.Records, p.NeedsEmbedding);
        Assert.Null(ShardFile.Read(Path.Combine(_dir, "novec", p.File)).Manifest.Embeddings);
    }

    // ======================= round trip =======================

    [Fact]
    public async Task The_b181_pack_round_trips_into_a_fresh_db_and_answers_like_the_seed()
    {
        BuildB181("rt");
        var pack = ShardFile.Read(Path.Combine(_dir, "rt", "b1.8.1-gamedata.mcshard"));

        var (ctx, importer) = Open("fresh");
        using (ctx)
        {
            var diff = importer.Preview(pack, new ShardImportOptions { Mode = ShardImportMode.SwapPack });
            Assert.Null(diff.Refused);
            Assert.Equal(pack.Records.Count, diff.Added);
            int withVector = pack.Records.Count(r => r.DocHash != null && pack.Embeddings.ContainsKey(r.DocHash));
            Assert.Equal(withVector, diff.ReusableVectors);
            Assert.Equal(pack.Manifest.Pack!.NeedsEmbedding, diff.ToEmbed);
            var res = await importer.ApplyAsync(pack, new ShardImportOptions { Mode = ShardImportMode.SwapPack });
            Assert.True(res.Ok, res.Message);
            Assert.Equal(0, res.Embedded); // no embedding provider was even available: the pack's vectors went straight in
            var active = KnowledgePack.Active(ctx.Db);
            Assert.Equal("b1.8.1-gamedata", active.Name);
            Assert.Equal(["mechanics_b181.md"], active.Files);
            Assert.Null(active.Modes); // a swap leaves the modes as they were (a legacy db: none listed)
            Assert.Null(KnowledgePack.PromptLine(ctx.Db)); // b1.8.1 is the default: the cached prefix is unchanged
            // Every doc whose material hash the pack carries has its vector (digits fold, so a few docs share one).
            Assert.Equal(withVector, ctx.Db.ScalarLong("SELECT count(*) FROM vectors"));
        }

        // Same game-data answers as the seed (lexical retrieval everywhere: no network). References: the seed's corpus as its builder
        // lays it down from the pristine sources (SeedCorpus + the version pack's doc), and the committed seed file.
        using var freshDb = AgentDb.Open(Path.Combine(_dir, "fresh.db"));
        using var fresh = new RagIndex(freshDb, new RagOptions(), NullEmbeddingProvider.Instance, backgroundEmbedding: false);
        using var builtDb = AgentDb.Open(null);
        using var built = new RagIndex(builtDb, new RagOptions(), NullEmbeddingProvider.Instance, backgroundEmbedding: false);
        built.UpsertMany(SeedCorpus.Build());
        built.UpsertMany(KnowledgeIndexer.Docs("mechanics_b181.md", File.ReadAllText(Path.Combine(KnowledgeDir, "mechanics_b181.md")), new RagOptions()).Docs);
        var seedCopy = Path.Combine(_dir, "seed-copy.db");
        File.Copy(SeedPath, seedCopy);
        using var seedDb = AgentDb.Open(seedCopy);
        using var seed = new RagIndex(seedDb, new RagOptions(), NullEmbeddingProvider.Instance, backgroundEmbedding: false);

        // Top 3 by BM25 with ties broken by ref (insertion order differs between a seed build and an import).
        static List<(string, string)> Top(RagIndex rag, string q) => rag.Search(new RagQuery { Text = q, Kinds = [SeedCorpus.Kind], Limit = 40 })
            .OrderByDescending(h => Math.Round(h.Lexical, 6)).ThenBy(h => h.Ref, StringComparer.Ordinal).Take(3).Select(h => (h.Ref, h.Body)).ToList();
        foreach (var (q, expect) in new (string, string?)[]
                 {
                     ("crafting table planks", null), ("stone pickaxe recipe", "gd:recipe:274:0:1"), ("bread", "gd:recipe:297:0:1"),
                     ("furnace smelting", null), ("what smelts into glass", "gd:smelt:12"),
                 })
        {
            var mine = Top(fresh, q);
            Assert.Equal(Top(built, q), mine);
            if (expect == null) continue;
            Assert.Contains(expect, mine.Select(a => a.Item1));
            Assert.Contains(expect, Top(seed, q).Select(a => a.Item1)); // the committed seed answers it the same way
        }
        // The answer docs themselves are the seed's, word for word (lexical ranking alone can't single out "crafting table").
        foreach (var r in new[] { "gd:recipe:58:0:1", "gd:recipe:274:0:1", "gd:item:274", "gd:recipe:297:0:1", "gd:recipe:61:0:1", "gd:smelt:12", "gd:fuel:overview" })
            Assert.Equal(seed.GetByRef(r)!.Body, fresh.GetByRef(r)!.Body);
    }

    // ======================= mode packs =======================

    [Fact]
    public async Task The_skyblock_mode_pack_adds_and_removes_its_core_lines_in_the_prompt()
    {
        BuildB181("modes");
        var version = ShardFile.Read(Path.Combine(_dir, "modes", "b1.8.1-gamedata.mcshard"));
        var skyblock = ShardFile.Read(Path.Combine(_dir, "modes", "skyblock.mcshard"));
        var (ctx, importer) = Open("modes");
        using (ctx)
        {
            var rag = new RagOptions();
            string Core() => KnowledgeIndexer.Sync(ctx.Rag, KnowledgeDir, rag).Core;
            long SkyChunks() => ctx.Db.ScalarLong("SELECT count(*) FROM docs WHERE ref LIKE 'kb:skyblock_b181.md:%'");

            // A legacy db (no pack meta, like alice today): every doc on disk, skyblock included.
            var legacy = Core();
            Assert.Contains("The void is below", legacy);
            Assert.Contains("Health regenerates only while food is 18+", legacy);
            Assert.True(SkyChunks() > 0);

            // The wrong flow for each pack is refused with a pointer to the right one.
            Assert.Contains("add or remove", importer.Preview(skyblock, new ShardImportOptions { Mode = ShardImportMode.SwapPack }).Refused);
            Assert.Contains("swap", importer.Preview(version, new ShardImportOptions { Mode = ShardImportMode.AddMode }).Refused);
            Assert.NotNull(importer.Preview(skyblock, new ShardImportOptions()).Refused);

            Assert.True((await importer.ApplyAsync(version, new ShardImportOptions { Mode = ShardImportMode.SwapPack })).Ok);
            var remove = importer.Preview(skyblock, new ShardImportOptions { Mode = ShardImportMode.RemoveMode });
            Assert.Null(remove.Refused);
            Assert.Equal(SkyChunks(), remove.Removed);
            Assert.Equal(0, remove.Writes);
            Assert.Null(remove.ModesFrom);
            Assert.Equal([], remove.ModesTo!);
            var removed = await importer.ApplyAsync(skyblock, new ShardImportOptions { Mode = ShardImportMode.RemoveMode });
            Assert.True(removed.Ok, removed.Message);
            Assert.Equal([], KnowledgePack.Active(ctx.Db).Modes!.Select(m => m.Mode));

            // A plain world: no skyblock core lines, no skyblock chunks, even though the file is still on disk.
            var plain = KnowledgeIndexer.Sync(ctx.Rag, KnowledgeDir, rag);
            Assert.DoesNotContain("The void is below", plain.Core);
            Assert.DoesNotContain("Unrenewable", plain.Core);
            Assert.Contains("Health regenerates only while food is 18+", plain.Core);
            Assert.Contains("skyblock_b181.md", plain.Excluded);
            Assert.Equal(0, SkyChunks());
            Assert.NotNull(ctx.Rag.GetByRef("kb:mechanics_b181.md:0"));

            // Back to a skyblock.
            var add = importer.Preview(skyblock, new ShardImportOptions { Mode = ShardImportMode.AddMode });
            Assert.Equal(skyblock.Records.Count, add.Added);
            Assert.Equal(["skyblock"], add.ModesTo!);
            Assert.True((await importer.ApplyAsync(skyblock, new ShardImportOptions { Mode = ShardImportMode.AddMode })).Ok);
            var sky = Core();
            Assert.Contains("The void is below", sky);
            Assert.Contains("Health regenerates only while food is 18+", sky);
            Assert.Equal(skyblock.Records.Count, SkyChunks());
            Assert.Equal("skyblock", Assert.Single(KnowledgePack.Active(ctx.Db).Modes!).Mode);

            // A bot without the doc files on disk takes the core stored with each active pack.
            var empty = Path.Combine(_dir, "no-docs");
            Directory.CreateDirectory(empty);
            var stored = KnowledgeIndexer.Sync(ctx.Rag, empty, rag).Core;
            Assert.Contains("The void is below", stored);
            Assert.Contains("Health regenerates only while food is 18+", stored);
            Assert.True((await importer.ApplyAsync(skyblock, new ShardImportOptions { Mode = ShardImportMode.RemoveMode })).Ok);
            Assert.DoesNotContain("The void is below", KnowledgeIndexer.Sync(ctx.Rag, empty, rag).Core);
        }
    }

    [Fact]
    public void Doc_pack_markers_decide_which_docs_a_world_reads()
    {
        var v = KnowledgeDocPack.Parse("# x\n<!-- pack version=b1.8.1 -->\n");
        var m = KnowledgeDocPack.Parse("<!-- pack mode=Skyblock version=b1.8.1 name=skyblock -->");
        Assert.Null(KnowledgeDocPack.Parse("# notes\n<!-- core -->\n- hi\n<!-- /core -->"));
        Assert.Equal("b1.8.1-gamedata", v!.PackName("b1.8.1"));
        Assert.Equal("skyblock", m!.PackName("b1.8.1"));
        Assert.Equal("skyblock", m.Mode);

        var legacy = new KnowledgePackInfo("b1.8.1");
        var plain = legacy with { Modes = [] };
        var sky = legacy with { Modes = [new KnowledgeModeInfo("skyblock")] };
        var other = new KnowledgePackInfo("1.8.9") { Modes = [new KnowledgeModeInfo("skyblock")] };
        Assert.True(v.ActiveIn(legacy) && m.ActiveIn(legacy));
        Assert.True(v.ActiveIn(plain) && !m.ActiveIn(plain));
        Assert.True(m.ActiveIn(sky));
        Assert.False(v.ActiveIn(other) || m.ActiveIn(other)); // b1.8.1 docs stay out of a 1.8.9 world

        // The live route takes the new modes by name too.
        foreach (var mode in new[] { ShardImportMode.AddMode, ShardImportMode.RemoveMode, ShardImportMode.SwapPack })
            Assert.Equal(mode, ShardImportOptions.ParseMode(ShardImportOptions.Name(mode)));
        Assert.Equal("b1.8.1 (built-in seed) + every mode doc on disk", legacy.Label);
        Assert.Equal("b1.8.1 (built-in seed), no mode", plain.Label);
    }

    // ======================= next version: minecraft-data =======================

    [Fact]
    public async Task A_minecraft_data_folder_builds_a_valid_version_pack()
    {
        var fixture = Path.Combine(Root(), "tests", "fixtures", "minecraft-data");
        var docs = Path.Combine(_dir, "docs189");
        Directory.CreateDirectory(docs);
        File.WriteAllText(Path.Combine(docs, "mechanics_189.md"),
            "# 1.8.9 notes\n<!-- pack version=1.8.9 -->\n<!-- core -->\n- Hunger works like 1.8.9 says.\n<!-- /core -->\n\n## Sprinting\n- Sprinting costs hunger.\n");
        File.WriteAllText(Path.Combine(docs, "skyblock_b181.md"), "<!-- pack mode=skyblock version=b1.8.1 name=skyblock -->\n## old\n- not this version\n");

        var source = new MinecraftDataSource(fixture, "1.8.9");
        Assert.Contains("pc/1.8", source.Description);
        var result = DefaultPackBuilder.Build(new PackBuildOptions { Source = source, KnowledgeDirectory = docs, OutputDirectory = Path.Combine(_dir, "out189") });
        var entry = Assert.Single(result.Packs); // the b1.8.1 skyblock doc isn't this version's
        Assert.Equal("1.8.9-gamedata.mcshard", entry.File);
        var pack = ShardFile.Read(Path.Combine(_dir, "out189", entry.File)); // verifies hashes
        Assert.Equal("1.8.9", pack.Manifest.PackVersion);
        Assert.Equal(["mechanics_189.md"], pack.Manifest.Pack!.Files);
        Assert.Contains("Hunger works like 1.8.9", pack.Manifest.Pack.Core);

        string Body(string id) => pack.Records.Single(r => r.Id == id).Get("body")!;
        Assert.Contains("Fits the 2x2 inventory grid", Body("gd:recipe:58:0:1"));
        Assert.Contains("4 Wood Planks", Body("gd:recipe:58:0:1"));
        Assert.Contains("Makes 4 per craft", Body("gd:recipe:5:0:1"));
        Assert.Contains("Shapeless", Body("gd:recipe:5:0:1"));
        Assert.Contains("Needs a crafting table", Body("gd:recipe:61:0:1"));
        Assert.Contains("8 Cobblestone", Body("gd:recipe:61:0:1"));
        Assert.Contains("Food: restores 5 hunger", Body("gd:item:297"));
        Assert.Contains("Durability 59", Body("gd:item:270"));
        Assert.Contains("Hardness 1.5", Body("gd:block:1"));
        Assert.Contains("Drops Cobblestone", Body("gd:block:1"));
        Assert.Contains("Best tool: pickaxe", Body("gd:block:1"));
        Assert.DoesNotContain(pack.Records, r => r.Id == "gd:block:0"); // air
        Assert.DoesNotContain(pack.Records, r => r.Id.StartsWith("gd:item:", StringComparison.Ordinal) && r.Get("title") == "item Crafting Table");

        // One command later it is the db's game data.
        var (ctx, importer) = Open("v189");
        using (ctx)
        {
            var res = await importer.ApplyAsync(pack, new ShardImportOptions { Mode = ShardImportMode.SwapPack });
            Assert.True(res.Ok, res.Message);
            Assert.Equal("1.8.9", KnowledgePack.ActiveVersion(ctx.Db));
            Assert.Contains("1.8.9", KnowledgePack.PromptLine(ctx.Db));
            Assert.Equal("gd:recipe:58:0:1", ctx.Rag.Search(new RagQuery { Text = "crafting table recipe", Kinds = [SeedCorpus.Kind], Limit = 1 }).Single().Ref);
            // The b1.8.1 docs on disk stay out of a 1.8.9 world.
            var k = KnowledgeIndexer.Sync(ctx.Rag, KnowledgeDir, new RagOptions());
            Assert.DoesNotContain("The void is below", k.Core);
            Assert.DoesNotContain("Health regenerates only while food is 18+", k.Core);
            Assert.Contains("Hunger works like 1.8.9", k.Core); // from the pack (its doc isn't in that folder)
        }
    }

    // ======================= secrets =======================

    [Fact]
    public void A_pack_never_carries_an_api_key()
    {
        var docs = Path.Combine(_dir, "leaky");
        Directory.CreateDirectory(docs);
        File.WriteAllText(Path.Combine(docs, "mechanics_b181.md"), "# notes\n<!-- pack version=b1.8.1 -->\n<!-- core -->\n" +
            "- Use sk-ant-api03-AAAAbbbbCCCCddddEEEE to call home.\n<!-- /core -->\n\n## Furnace\n- Furnaces smelt with coal. token: abcdefghijklmnopqrstuvwxyz012345\n\n" +
            "## Admin\n- The server admin password is hunter2, keep it handy.\n");
        var r = DefaultPackBuilder.Build(new PackBuildOptions { Source = new MineClawDataSource(), KnowledgeDirectory = docs, OutputDirectory = Path.Combine(_dir, "leak") });
        var path = Path.Combine(_dir, "leak", "b1.8.1-gamedata.mcshard");
        var pack = ShardFile.Read(path);
        Assert.True(pack.Manifest.SecretsRedacted >= 2);
        Assert.DoesNotContain("sk-ant-", pack.Manifest.Pack!.Core);
        Assert.DoesNotContain(pack.Records, x => x.Fields.Values.Any(v => v != null && (v.Contains("hunter2") || v.Contains("abcdefghijklmnopqrstuvwxyz012345"))));
        Assert.All(Text(path), t => Assert.DoesNotContain("sk-ant-", t));
        Assert.Contains(r.Notes, n => n.Contains("redacted", StringComparison.Ordinal));

        // The real packs: nothing token-shaped in any record, core or description (the manifest's own sha256 hashes aside).
        BuildB181("real");
        foreach (var f in Directory.GetFiles(Path.Combine(_dir, "real"), "*.mcshard"))
        {
            Assert.All(Text(f, recordsOnly: true), t => Assert.Equal(t, ShardRedactor.Redact(t, out _)));
            var m = ShardFile.Read(f).Manifest;
            Assert.Equal(m.Pack!.Core, ShardRedactor.Redact(m.Pack.Core!, out _));
            Assert.Equal(m.Description, ShardRedactor.Redact(m.Description!, out _));
            Assert.Equal(0, m.SecretsRedacted);
        }
    }

    /// <summary>Every text entry of a .mcshard (manifest, records).</summary>
    private static List<string> Text(string path, bool recordsOnly = false)
    {
        using var zip = ZipFile.OpenRead(path);
        return zip.Entries.Where(e => e.FullName.EndsWith(".jsonl", StringComparison.Ordinal) || (!recordsOnly && e.FullName.EndsWith(".json", StringComparison.Ordinal)))
            .Select(e => { using var s = e.Open(); using var rd = new StreamReader(s, Encoding.UTF8); return rd.ReadToEnd(); }).ToList();
    }
}
