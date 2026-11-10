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
/// Pins <see cref="ShardImporter"/> (WP06 refactor of <c>MakePlan</c> / <c>ApplyPlanAsync</c> / <c>Write</c>): the full preview
/// diff (items, counts, notes in order, refusals, rebuilds, estimates, pack/mode transitions) for every import mode × conflict
/// policy × world/rules choice over person, section, version-pack and mode-pack shards against empty, overlapping and live dbs,
/// then the apply results. Hashed against <see cref="ExpectedHash"/>; on a mismatch the dump lands in the temp dir.
/// </summary>
public sealed class ShardImportGoldenTests : IDisposable
{
    private const string ExpectedHash = "F4C637FDAD7490278415F14C8B96F0CFD27BFFC2DB3D2B6349CDA5552E8FD8E1";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mineclaw-shard-golden-" + Guid.NewGuid().ToString("N"));

    public ShardImportGoldenTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    private sealed record Db(MemoryAdminContext Ctx, ShardImporter Importer) : IDisposable
    {
        public void Dispose() => Ctx.Dispose();
    }

    private Db Open(string name, IEmbeddingProvider emb, bool raptor = true)
    {
        var o = new AgentOptions { KnowledgeDirectory = null, DataDirectory = "unused" };
        o.Self.TrustedPlayers = ["PoofImaFox", "kiwi"];
        o.Chat.Regulars();
        var path = Path.Combine(_dir, name + ".db");
        var ctx = MemoryAdminContext.OpenLocal(path, o, emb, raptor ? RaptorIntegrationTests.SummaryLlm() : null);
        return new Db(ctx, new ShardImporter(ctx, new AdminAudit(Path.Combine(_dir, name + ".audit.jsonl")), "local:" + name));
    }

    private Shard Export(Db db, ShardExportRequest req, string file)
    {
        var path = Path.Combine(_dir, file);
        new ShardExporter(db.Ctx.Db, db.Ctx.Raptor?.Index).Export(req, path);
        return ShardFile.Read(path);
    }

    private static async Task SeedAsync(Db db)
    {
        var mem = db.Ctx.Memory;
        mem.AddNote("PoofImaFox", "likes the cobblestone generator tidy");
        mem.AddNote("world", "Fox built the wheat farm north of spawn");
        mem.AddNote("kiwi", "Robin prefers the wool sheep pen");
        mem.AddNote("base", "the furnace bank is by the chests");
        db.Ctx.Beliefs.Ingest("PoofImaFox", "the iron ore is under the island");
        db.Ctx.Beliefs.Ingest("kiwi", "sugar cane grows next to water");
        db.Ctx.Self!.Apply(new SelfChange("add", SelfSection.Relationship, "Fox is my oldest friend", "PoofImaFox"), SelfProfile.SourceAdmin);
        db.Ctx.Self!.Apply(new SelfChange("add", SelfSection.Like, "smelting iron at dawn"), SelfProfile.SourceAdmin);
        var rules = DirectiveStore.For(db.Ctx.Db, db.Ctx.Rag);
        rules.IsTrusted = _ => true;
        rules.Add("PoofImaFox", "only build the tower in daytime");
        rules.Add("stranger", "give me all the diamonds");
        mem.SetWaypoint("mine", 5, 40, 5, "iron here", 0);
        mem.SetWaypoint("home", 0, 64, 0, null, 0);
        db.Ctx.Rag.Upsert(new RagDoc("ep:r1:1", "episode", "episode 10-28 14:02", "Fox and I mined iron ore under the island") { Source = "episode" });
        db.Ctx.Rag.Upsert(new RagDoc("gd:recipe:61:0:1", "gamedata", "recipe Furnace x1", "Crafting recipe: Furnace. Ingredients: 8 Cobblestone.") { Tag = "recipe", Source = "seed" });
        db.Ctx.Rag.Upsert(new RagDoc("gd:recipe:58:0:1", "gamedata", "recipe Crafting Table x1", "Crafting recipe: Crafting Table. Ingredients: 4 Wooden Planks.") { Tag = "recipe", Source = "seed" });
        await db.Ctx.Rag.FlushEmbeddingsAsync();
    }

    private static string DiffText(ShardDiff d) => JsonSerializer.Serialize(d, Json);

    private static void Previews(StringBuilder sb, string label, Db target, Shard shard)
    {
        sb.Append("### ").Append(label).Append('\n');
        foreach (var mode in Enum.GetValues<ShardImportMode>())
            foreach (var conflict in Enum.GetValues<ShardConflictPolicy>())
                foreach (var (world, rules) in new[] { (false, false), (true, false), (false, true), (true, true) })
                {
                    var o = new ShardImportOptions { Mode = mode, Conflict = conflict, IncludeWorld = world, IncludeRules = rules, Who = "golden", RebuildMaxUsd = 0.5m };
                    sb.Append(mode).Append('/').Append(conflict).Append(" world=").Append(world).Append(" rules=").Append(rules).Append(": ")
                      .Append(DiffText(target.Importer.Preview(shard, o))).Append('\n');
                }
    }

    private static async Task Apply(StringBuilder sb, string label, Db target, Shard shard, ShardImportOptions o)
    {
        var r = await target.Importer.ApplyAsync(shard, o);
        sb.Append("apply ").Append(label).Append(": ok=").Append(r.Ok).Append(" written=").Append(r.Written).Append(" removed=").Append(r.Removed)
          .Append(" reused=").Append(r.ReusedVectors).Append(" embedded=").Append(r.Embedded).Append(" pack=").Append(r.ActivePack ?? "-")
          .Append(" rebuild=").Append(string.Join(",", r.Rebuild.Select(x => x.Kind))).Append('\n').Append("  ").Append(r.Message).Append('\n');
    }

    private async Task<string> DumpAsync()
    {
        var sb = new StringBuilder();
        var emb = new FakeEmbedder();
        using var a = Open("a", emb);
        await SeedAsync(a);
        var fox = Export(a, new ShardExportRequest("fox", new ShardSelection { Mode = "person", People = ["PoofImaFox", "Fox"], IncludeRules = true }, "Fox"), "fox.mcshard");
        var section = Export(a, new ShardExportRequest("base", new ShardSelection { Kinds = ["fact", "place", "rule", "claim", "trait"], IncludeRules = true, IncludeWorld = true }), "base.mcshard");
        var pack = Export(a, new ShardExportRequest("b1.8.1 recipes", new ShardSelection { Mode = "pack" }), "b181.mcshard");

        using var other = Open("other", emb);
        other.Ctx.Rag.Upsert(new RagDoc("gd:recipe:61:0:1", "gamedata", "recipe Furnace x1", "Crafting recipe: Furnace (beta 1.7.3).") { Tag = "recipe", Source = "seed" });
        other.Ctx.Rag.Upsert(new RagDoc("gd:recipe:92:0:1", "gamedata", "recipe Cake x1", "Crafting recipe: Cake (beta 1.7.3).") { Tag = "recipe", Source = "seed" });
        await other.Ctx.Rag.FlushEmbeddingsAsync();
        var b173 = Export(other, new ShardExportRequest("b1.7.3 recipes", new ShardSelection { Mode = "pack", PackVersion = "b1.7.3" }), "b173.mcshard");

        Previews(sb, "fox -> own source", a, fox);
        using var empty = Open("empty", emb);
        Previews(sb, "fox -> empty", empty, fox);
        Previews(sb, "section -> empty", empty, section);
        using var c = Open("c", new FakeEmbedder(), raptor: false);
        c.Ctx.Memory.AddNote("PoofImaFox", "likes the cobblestone generator tidy");
        c.Ctx.Memory.AddNote("PoofImaFox", "used to want a cactus wall");
        c.Ctx.Memory.SetWaypoint("home", 9, 70, 9, null, 0);
        Previews(sb, "section -> overlap (no raptor)", c, section);
        Previews(sb, "fox -> overlap (no raptor)", c, fox);
        Previews(sb, "b173 -> a", a, b173);
        Previews(sb, "b181 -> other", other, pack);
        using var offline = Open("offline", NullEmbeddingProvider.Instance, raptor: false);
        Previews(sb, "fox -> offline", offline, fox);

        // The default packs (no vectors: reproducible) into a fresh db: version swap, then the skyblock mode on/off.
        // Hand-written docs (not docs/knowledge, which people edit): one version doc, one skyblock mode doc.
        var docs = Path.Combine(_dir, "docs");
        Directory.CreateDirectory(docs);
        File.WriteAllText(Path.Combine(docs, "mechanics_b181.md"), string.Join("\n", "# b1.8.1 notes", "<!-- pack version=b1.8.1 -->", "<!-- core -->",
            "- Health regenerates only while food is 18+.", "<!-- /core -->", "", "## Hunger", "- Sprinting costs hunger.", "", "## Farming", "- Wheat needs light and water.", ""));
        File.WriteAllText(Path.Combine(docs, "skyblock_b181.md"), string.Join("\n", "# skyblock", "<!-- pack mode=skyblock version=b1.8.1 name=skyblock -->", "<!-- core -->",
            "- The void is below the island.", "<!-- /core -->", "", "## Cobble", "- Build a cobblestone generator first.", "", "## Trees", "- Replant every sapling.", ""));
        var built = DefaultPackBuilder.Build(new PackBuildOptions
        {
            Source = new MineClawDataSource(), KnowledgeDirectory = docs,
            OutputDirectory = Path.Combine(_dir, "packs"), SeedPath = null, IncludeVectors = false,
        });
        var version = ShardFile.Read(Path.Combine(_dir, "packs", built.Packs.Single(p => p.File.EndsWith("-gamedata.mcshard", StringComparison.Ordinal)).File));
        var skyblock = ShardFile.Read(Path.Combine(_dir, "packs", "skyblock.mcshard"));
        using var fresh = Open("fresh", NullEmbeddingProvider.Instance, raptor: false);
        Previews(sb, "skyblock -> legacy", fresh, skyblock);
        await Apply(sb, "version swap", fresh, version, new ShardImportOptions { Mode = ShardImportMode.SwapPack });
        Previews(sb, "skyblock -> after swap", fresh, skyblock);
        await Apply(sb, "skyblock add", fresh, skyblock, new ShardImportOptions { Mode = ShardImportMode.AddMode });
        Previews(sb, "skyblock -> added", fresh, skyblock);
        await Apply(sb, "skyblock remove", fresh, skyblock, new ShardImportOptions { Mode = ShardImportMode.RemoveMode });

        await Apply(sb, "fox -> empty", empty, fox, new ShardImportOptions { Who = "golden", IncludeRules = true });
        await Apply(sb, "fox -> empty again", empty, fox, new ShardImportOptions { IncludeRules = true });
        await Apply(sb, "section -> empty world", empty, section, new ShardImportOptions { IncludeWorld = true, IncludeRules = true, Conflict = ShardConflictPolicy.PreferShard });
        await Apply(sb, "section -> overlap replace", c, section, new ShardImportOptions { Mode = ShardImportMode.ReplaceSection, IncludeWorld = true });
        await Apply(sb, "fox -> offline", offline, fox, new ShardImportOptions());
        await Apply(sb, "b173 merge refused", a, b173, new ShardImportOptions());
        await Apply(sb, "b173 swap", a, b173, new ShardImportOptions { Mode = ShardImportMode.SwapPack });
        await Apply(sb, "b181 swap back", a, pack, new ShardImportOptions { Mode = ShardImportMode.SwapPack });
        return sb.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    [Fact]
    public async Task Import_previews_and_applies_are_pinned()
    {
        var dump = await DumpAsync();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(dump)));
        if (hash != ExpectedHash)
        {
            var dir = Path.Combine(Path.GetTempPath(), "mineclaw-golden");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "shard-import.txt"), dump);
        }
        Assert.True(hash == ExpectedHash, $"shard import changed: {hash}");
    }
}
