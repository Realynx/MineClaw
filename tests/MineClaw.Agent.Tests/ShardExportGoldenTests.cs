using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MineClaw.Agent.Admin;
using MineClaw.Agent.Admin.Shards;
using MineClaw.Agent.Admin.Shards.Packs;
using MineClaw.Agent.Rag;
using MineClaw.Agent.Rules;
using MineClaw.Agent.Self;

namespace MineClaw.Agent.Tests;

/// <summary>
/// Pins <see cref="ShardExporter.Build"/> / <see cref="ShardExporter.Preview"/> and <see cref="DefaultPackBuilder"/> (WP06 refactor):
/// the exported shard bytes (wall-clock stamps fixed) for person, section, filter, pack and secret-bearing selections with and without
/// vectors and summaries, each preview, and every file the b1.8.1 pack build writes (with vectors, without, and over the size cap).
/// </summary>
public sealed class ShardExportGoldenTests : IDisposable
{
    private const string ExpectedExport = "2E355FB943D6ED3ACFC7E6057BC58CAF0A93AD9B1104CAFFA9164937A74BC119";
    private const string ExpectedPacks = "74B7748E2C7356B4673C422763D9C9FC9092D56036F0A279E46B37AEDB8D5A75";

    private static readonly DateTimeOffset Fixed = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mineclaw-export-golden-" + Guid.NewGuid().ToString("N"));

    public ShardExportGoldenTests() => Directory.CreateDirectory(_dir);

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

    private static string Hash(string dump, string file)
    {
        var dir = Path.Combine(Path.GetTempPath(), "mineclaw-golden");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, file), dump);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(dump)));
    }

    /// <summary>The shard with every wall-clock stamp fixed, as file bytes.</summary>
    private static string Stable(Shard s)
    {
        var fixedShard = new Shard
        {
            Manifest = s.Manifest with { Created = Fixed },
            Records = s.Records.Select(r => r with { Created = Fixed, Updated = Fixed }).ToList(),
            Embeddings = s.Embeddings,
            Summaries = s.Summaries,
        };
        return Convert.ToHexString(SHA256.HashData(ShardFile.ToBytes(fixedShard)));
    }

    [Fact]
    public async Task Exports_build_the_same()
    {
        var o = new AgentOptions { KnowledgeDirectory = null, DataDirectory = "unused" };
        o.Self.TrustedPlayers = ["PoofImaFox", "kiwi"];
        o.Chat.Regulars();
        var path = Path.Combine(_dir, "a.db");
        using var ctx = MemoryAdminContext.OpenLocal(path, o, new FakeEmbedder(), RaptorIntegrationTests.SummaryLlm());
        var mem = ctx.Memory;
        mem.AddNote("PoofImaFox", "likes the cobblestone generator tidy");
        mem.AddNote("world", "Fox built the wheat farm north of spawn");
        mem.AddNote("kiwi", "Robin prefers the wool sheep pen");
        mem.AddNote("base", "the furnace bank is by the chests");
        mem.AddNote("base", "the server api key is in the chest");
        mem.AddNote("base", "Fox pasted token sk-ant-api03-ABCDEFGHIJKLMNOP by mistake");
        ctx.Beliefs.Ingest("PoofImaFox", "the iron ore is under the island");
        ctx.Beliefs.Ingest("kiwi", "sugar cane grows next to water");
        ctx.Self!.Apply(new SelfChange("add", SelfSection.Relationship, "Fox is my oldest friend", "PoofImaFox"), SelfProfile.SourceAdmin);
        ctx.Self!.Apply(new SelfChange("add", SelfSection.Like, "smelting iron at dawn"), SelfProfile.SourceAdmin);
        var rules = DirectiveStore.For(ctx.Db, ctx.Rag);
        rules.IsTrusted = _ => true;
        rules.Add("PoofImaFox", "only build the tower in daytime");
        mem.SetWaypoint("mine", 5, 40, 5, "iron here", 0);
        mem.SetWaypoint("home", 0, 64, 0, null, 0);
        ctx.Rag.Upsert(new RagDoc("ep:r1:1", "episode", "episode 10-28 14:02", "Fox and I mined iron ore under the island") { Source = "episode" });
        ctx.Rag.Upsert(new RagDoc("gd:recipe:61:0:1", "gamedata", "recipe Furnace x1", "Crafting recipe: Furnace. Ingredients: 8 Cobblestone.") { Tag = "recipe", Source = "seed" });
        ctx.Rag.Upsert(new RagDoc("gd:recipe:58:0:1", "gamedata", "recipe Crafting Table x1", "Crafting recipe: Crafting Table. Ingredients: 4 Wooden Planks.") { Tag = "recipe", Source = "seed" });
        for (int i = 0; i < 30; i++)
            ctx.Rag.Upsert(new RagDoc($"kb:t.md:{i}", "knowledge", $"note {i}", $"furnace cobblestone smelting fuel coal detail {i} item{i % 7}") { Source = "t.md" });
        await ctx.Rag.FlushEmbeddingsAsync();
        await ctx.Raptor!.BuildAsync();

        var exporter = new ShardExporter(ctx.Db, ctx.Raptor.Index);
        var plain = new ShardExporter(ctx.Db);
        var requests = new (string Label, ShardExporter X, ShardExportRequest Req)[]
        {
            ("person", exporter, new ShardExportRequest("fox", new ShardSelection { Mode = "person", People = ["PoofImaFox", "Fox"], IncludeRules = true }, "Fox")),
            ("section", exporter, new ShardExportRequest("base", new ShardSelection { Kinds = ["fact", "place", "rule", "claim", "trait"], IncludeRules = true, IncludeWorld = true })),
            ("section-no-vectors", exporter, new ShardExportRequest("base", new ShardSelection { Kinds = ["fact", "knowledge"] }, "  ", IncludeEmbeddings: false)),
            ("knowledge+summaries", exporter, new ShardExportRequest("kb", new ShardSelection { Kinds = ["knowledge"] }, "token: abcdefghijklmnop1234", CreatedBy: "golden", Bot: "maid_chan")),
            ("knowledge-plain", plain, new ShardExportRequest(" kb ", new ShardSelection { Kinds = ["knowledge"] }, IncludeSummaries: true)),
            ("filter", exporter, new ShardExportRequest("q", new ShardSelection { Mode = "filter", Query = "furnace" })),
            ("pack", exporter, new ShardExportRequest("b1.8.1 recipes", new ShardSelection { Mode = "pack" })),
            ("pack-version", exporter, new ShardExportRequest("b1.7.3", new ShardSelection { Mode = "pack", PackVersion = " b1.7.3 " })),
        };
        var sb = new StringBuilder();
        foreach (var (label, x, req) in requests)
        {
            var s = x.Build(req);
            sb.Append(label).Append(' ').Append(Stable(s)).Append(' ').Append(JsonSerializer.Serialize(s.Manifest with { Created = Fixed }, Json)).Append('\n');
            var p = x.Preview(req);
            sb.Append("  preview ").Append(JsonSerializer.Serialize(p with { Bytes = 0 }, Json)).Append('\n');
        }
        foreach (var bad in new[] { new ShardExportRequest(" ", new ShardSelection()), new ShardExportRequest("x", new ShardSelection { Mode = "pack", PackVersion = "nope!" }) })
        {
            try { exporter.Build(bad); sb.Append("no error\n"); }
            catch (ArgumentException ex) { sb.Append("error ").Append(ex.Message).Append('\n'); }
        }
        Assert.Equal(ExpectedExport, Hash(sb.ToString(), "shard-export.txt"));
    }

    [Fact]
    public void Default_packs_build_the_same()
    {
        var knowledge = Path.Combine(Root(), "docs", "knowledge");
        var seed = Path.Combine(Root(), "data", "seed", "mineclaw-seed.db");
        var builds = new (string Name, PackBuildOptions O)[]
        {
            ("vectors", new PackBuildOptions { Source = new MineClawDataSource(), KnowledgeDirectory = knowledge, OutputDirectory = Path.Combine(_dir, "v"), SeedPath = seed }),
            ("plain", new PackBuildOptions { Source = new MineClawDataSource(), KnowledgeDirectory = knowledge, OutputDirectory = Path.Combine(_dir, "p"), IncludeVectors = false }),
            ("capped", new PackBuildOptions { Source = new MineClawDataSource(), KnowledgeDirectory = knowledge, OutputDirectory = Path.Combine(_dir, "c"), SeedPath = seed, MaxPackBytes = 64 * 1024 }),
        };
        var sb = new StringBuilder();
        foreach (var (name, o) in builds)
        {
            var r = DefaultPackBuilder.Build(o);
            sb.Append("build ").Append(name).Append('\n');
            foreach (var n in r.Notes) sb.Append("  note ").Append(n).Append('\n');
            foreach (var p in r.Packs) sb.Append("  pack ").Append(JsonSerializer.Serialize(p, Json)).Append('\n');
            foreach (var f in Directory.GetFiles(o.OutputDirectory).Order(StringComparer.Ordinal))
                sb.Append("  file ").Append(Path.GetFileName(f)).Append(' ').Append(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f)))).Append('\n');
        }
        Assert.Equal(ExpectedPacks, Hash(sb.ToString(), "default-packs.txt"));
    }
}
