using MineClaw.Agent;
using MineClaw.Agent.Admin;
using MineClaw.Agent.Admin.Shards;
using MineClaw.Agent.Admin.Shards.Packs;
using MineClaw.Agent.Rag;
using MineClaw.Bot.GameTools;
using MineClaw.Scripting.Sdk;

namespace MineClaw.Bot.Tests;

/// <summary>
/// The sdk-js knowledge pack (shards/defaults/sdk-js.mcshard): generated from the SDK modules' doc comments and an offline bot's script
/// globals, so a new <c>def(...)</c> shows up after a rebuild, a stale committed pack fails <c>ShardPacks verify</c>, and retrieval
/// surfaces the exact helper. Offline (no server, no model, lexical retrieval).
/// </summary>
public sealed class ScriptSdkPackTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mineclaw-sdkpack-" + Guid.NewGuid().ToString("N")[..8]);

    public ScriptSdkPackTests() => Directory.CreateDirectory(_dir);

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

    private static string SdkDir => Path.Combine(Root(), "src", "MineClaw.Scripting", "Sdk", "js");

    /// <summary>What <c>tools/ShardPacks -- build</c> writes for sdk-js (the version source is irrelevant: only the generated pack is built).</summary>
    private static PackBuildResult BuildSdk(string sdkDir, string outDir) => DefaultPackBuilder.Build(new PackBuildOptions
    {
        Source = new MineClawDataSource(),
        OutputDirectory = outDir,
        SeedPath = Path.Combine(Root(), "data", "seed", "mineclaw-seed.db"),
        Generated = [ScriptSdkPack.Build(SdkModules.FromDirectory(sdkDir))],
        IncludeVersionPacks = false,
    });

    private static string CopySdk(string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(SdkDir, "*.js")) File.Copy(f, Path.Combine(to, Path.GetFileName(f)));
        return to;
    }

    [Fact]
    public void The_pack_lists_every_documented_helper_and_game_function_per_version()
    {
        var gen = ScriptSdkPack.Build();
        Assert.All(gen.Docs, d => Assert.StartsWith("kb:sdk-js.md:", d.Ref));
        Assert.Equal(gen.Docs.Count, gen.Docs.Select(d => d.Ref).Distinct().Count());
        var bodies = string.Join("\n", gen.Docs.Select(d => d.Body));
        foreach (var name in SdkReference.Documented().Where(n => n.Length > 2 && !n.Contains('$')))
            Assert.Contains(name, bodies);
        var wait = gen.Docs.Single(d => d.Ref == "kb:sdk-js.md:fn:jobs.waitForDay");
        Assert.Contains("wait until it is day", wait.Body);
        // Game functions of the bot, camelCase + snake_case, with the versions that have them.
        var chop = gen.Docs.Single(d => d.Ref == "kb:sdk-js.md:game:chopTree");
        Assert.Contains("chopTree(", chop.Body);
        Assert.Contains("/ chop_tree", chop.Body);
        Assert.Contains("b1.8.1", chop.Body);
        Assert.Contains("1.12.2", chop.Body);
        Assert.Contains(gen.Docs, d => d.Ref == "kb:sdk-js.md:game:go" && d.Body.Contains("alias of goto"));
        Assert.Equal(3, gen.Docs.Count(d => d.Ref.StartsWith("kb:sdk-js.md:example:", StringComparison.Ordinal)));
        Assert.Contains(gen.Docs, d => d.Ref == "kb:sdk-js.md:mod:world" && d.Body.Contains("canSpawn"));
    }

    [Fact]
    public void A_new_def_shows_up_after_a_rebuild_and_verify_catches_the_stale_pack()
    {
        var src = CopySdk(Path.Combine(_dir, "js"));
        var committed = Path.Combine(_dir, "committed");
        BuildSdk(src, committed);
        Assert.DoesNotContain(ShardFile.Read(Path.Combine(committed, "sdk-js.mcshard")).Records, r => r.Id.Contains("countSheep"));

        // Someone adds a helper to the SDK...
        var jobs = Path.Combine(src, "60-jobs.js");
        var text = File.ReadAllText(jobs);
        int end = text.LastIndexOf('}');
        File.WriteAllText(jobs, text[..end] + "  /** countSheep(r=16) - how many sheep are within r blocks (zero tokens). */\n" +
                                "  def('countSheep', (r = 16) => 0);\n" + text[end..]);

        // ...the committed pack is now stale (what `ShardPacks verify` reports)...
        var rebuilt = Path.Combine(_dir, "rebuilt");
        var r = BuildSdk(src, rebuilt);
        var checks = PackVerifier.Compare(committed, rebuilt);
        Assert.Contains(checks, c => c.File == "sdk-js.mcshard" && c.Status == "differs");
        Assert.Contains(checks, c => c.File == "index.json" && !c.Ok);

        // ...and the rebuilt pack carries the new helper as its own chunk.
        var pack = ShardFile.Read(Path.Combine(rebuilt, "sdk-js.mcshard"));
        var doc = pack.Records.Single(x => x.Id == "kb:sdk-js.md:fn:jobs.countSheep");
        Assert.Contains("how many sheep are within r blocks", doc.Get("body"));
        Assert.Contains(pack.Records, x => x.Id == "kb:sdk-js.md:mod:jobs" && x.Get("body")!.Contains("countSheep"));
        Assert.Equal(KnowledgePack.AnyVersion, pack.Manifest.Pack!.Version);
        Assert.Equal("sdk-js", pack.Manifest.Pack.Mode);
        Assert.Equal(ShardPackInfo.ModeKind, Assert.Single(r.Packs).Kind);

        // The same sources rebuild byte-identically (verify passes).
        BuildSdk(src, Path.Combine(_dir, "again"));
        Assert.All(PackVerifier.Compare(rebuilt, Path.Combine(_dir, "again")), c => Assert.True(c.Ok, c.File + " " + c.Status));
    }

    [Fact]
    public void The_committed_sdk_pack_is_current()
    {
        var fresh = Path.Combine(_dir, "fresh");
        BuildSdk(SdkDir, fresh);
        var committed = Path.Combine(Root(), "shards", "defaults", "sdk-js.mcshard");
        Assert.True(File.ReadAllBytes(committed).AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(fresh, "sdk-js.mcshard"))),
            "shards/defaults/sdk-js.mcshard is stale: run dotnet run --project tools/ShardPacks -- build (and commit)");
    }

    [Fact]
    public async Task Imported_into_a_db_it_answers_how_do_I_wait_for_day_in_a_script()
    {
        BuildSdk(SdkDir, Path.Combine(_dir, "p"));
        var pack = ShardFile.Read(Path.Combine(_dir, "p", "sdk-js.mcshard"));
        var path = Path.Combine(_dir, "sdk.db");
        using var ctx = MemoryAdminContext.OpenLocal(path, new AgentOptions { KnowledgeDirectory = null, DataDirectory = _dir }, NullEmbeddingProvider.Instance, llm: null);
        var importer = new ShardImporter(ctx, new AdminAudit(Path.Combine(_dir, "audit.jsonl")), "local:" + path);
        KnowledgePack.SetActive(ctx.Db, new KnowledgePackInfo("1.12.2") { Modes = [] });
        var res = await importer.ApplyAsync(pack, new ShardImportOptions { Mode = ShardImportMode.AddMode });
        Assert.True(res.Ok, res.Message);
        Assert.Equal("sdk-js", Assert.Single(KnowledgePack.Active(ctx.Db).Modes!).Mode);

        var hits = ctx.Rag.Search(new RagQuery { Text = "wait for day script", Kinds = [KnowledgeIndexer.Kind], Limit = 3 }).Select(h => h.Ref).ToList();
        Assert.Contains("kb:sdk-js.md:fn:jobs.waitForDay", hits);
        // A start without the (generated) doc on disk keeps the pack's chunks.
        KnowledgeIndexer.Sync(ctx.Rag, Path.Combine(Root(), "docs", "knowledge"), new RagOptions(), "1.12.2");
        Assert.NotNull(ctx.Rag.GetByRef("kb:sdk-js.md:fn:jobs.waitForDay"));
    }
}
