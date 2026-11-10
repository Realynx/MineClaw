using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MineClaw.Agent.Admin;
using MineClaw.Agent.Admin.Shards;
using MineClaw.Agent.Rag;
using MineClaw.Agent.Rules;
using MineClaw.Agent.Self;

namespace MineClaw.Agent.Tests;

/// <summary>Memory shards: export/import round trip, dedupe, replace-section, knowledge-pack swap, diffs, scoped rebuilds, format gates. Offline.</summary>
public sealed class MemoryShardTests : IDisposable
{
    private static readonly string[] Topics =
    [
        "furnace cobblestone smelting fuel coal", "wheat farm seeds water hoe", "iron ingots pickaxe ore mining",
        "sugar cane paper books bookshelf", "cobblestone generator lava water", "wool sheep dye colours bed",
    ];

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mineclaw-shard-" + Guid.NewGuid().ToString("N"));

    public MemoryShardTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static AgentOptions Options()
    {
        var o = new AgentOptions { KnowledgeDirectory = null, DataDirectory = "unused" };
        o.Self.TrustedPlayers = ["PoofImaFox", "kiwi"];
        o.Chat.Regulars();
        return o;
    }

    private sealed record Db(MemoryAdminContext Ctx, AdminAudit Audit, ShardImporter Importer, string Path) : IDisposable
    {
        public void Dispose() => Ctx.Dispose();
    }

    private Db Open(string name, FakeEmbedder emb)
    {
        var path = Path.Combine(_dir, name + ".db");
        var ctx = MemoryAdminContext.OpenLocal(path, Options(), emb, RaptorIntegrationTests.SummaryLlm());
        var audit = new AdminAudit(Path.Combine(_dir, name + ".admin-audit.jsonl"));
        return new Db(ctx, audit, new ShardImporter(ctx, audit, "local:" + path), path);
    }

    private static ShardSelection Person(params string[] names) => new() { Mode = "person", People = names };

    private string Export(Db db, ShardExportRequest req, string file)
    {
        var path = Path.Combine(_dir, file);
        new ShardExporter(db.Ctx.Db, db.Ctx.Raptor?.Index).Export(req, path);
        return path;
    }

    /// <summary>Fox's and Robin's memories plus world facts: notes, claims, a relationship entry, a standing rule, episodes.</summary>
    private static async Task SeedPeopleAsync(Db db)
    {
        var mem = db.Ctx.Memory;
        mem.AddNote("PoofImaFox", "likes the cobblestone generator tidy");
        mem.AddNote("world", "Fox built the wheat farm north of spawn");
        mem.AddNote("kiwi", "Robin prefers the wool sheep pen");
        mem.AddNote("base", "the furnace bank is by the chests");
        Assert.True(db.Ctx.Beliefs.Ingest("PoofImaFox", "the iron ore is under the island").Stored);
        Assert.True(db.Ctx.Beliefs.Ingest("kiwi", "sugar cane grows next to water").Stored);
        var self = db.Ctx.Self!;
        Assert.True(self.Apply(new SelfChange("add", SelfSection.Relationship, "Fox is my oldest friend", "PoofImaFox"), SelfProfile.SourceAdmin).Ok);
        Assert.True(self.Apply(new SelfChange("add", SelfSection.Like, "smelting iron at dawn"), SelfProfile.SourceAdmin).Ok);
        var rules = DirectiveStore.For(db.Ctx.Db, db.Ctx.Rag);
        rules.IsTrusted = p => p.Equals("PoofImaFox", StringComparison.OrdinalIgnoreCase);
        Assert.True(rules.Add("PoofImaFox", "only build the tower in daytime").Stored);
        db.Ctx.Rag.Upsert(new RagDoc("ep:r1:1", "episode", "episode 10-28 14:02", "Fox and I mined iron ore under the island") { Source = "episode" });
        db.Ctx.Rag.Upsert(new RagDoc("ep:r1:2", "episode", "episode 10-28 15:10", "I sheared the wool sheep alone") { Source = "episode" });
        await db.Ctx.Rag.FlushEmbeddingsAsync();
    }

    // ======================= round trip =======================

    [Fact]
    public async Task A_person_shard_round_trips_into_an_empty_db_with_identical_records_and_embeddings()
    {
        var emb = new FakeEmbedder();
        using var a = Open("a", emb);
        await SeedPeopleAsync(a);
        var path = Export(a, new ShardExportRequest("fox", Person("PoofImaFox", "Fox"), "everything about Fox"), "fox.mcshard");

        var shard = ShardFile.Read(path);
        Assert.Equal("person", shard.Manifest.Kind);
        Assert.Equal(emb.Model, shard.Manifest.Embeddings!.Model);
        var kinds = shard.Records.GroupBy(r => r.Kind).ToDictionary(g => g.Key, g => g.Count());
        Assert.Equal(2, kinds[ShardKinds.Fact]);      // subject PoofImaFox + "Fox built ..."; not Robin's, not the furnace
        Assert.Equal(1, kinds[ShardKinds.Claim]);     // Fox's claim only
        Assert.Equal(1, kinds[ShardKinds.Trait]);     // the relationship entry
        Assert.Equal(1, kinds[ShardKinds.Episode]);   // the episode with Fox
        Assert.False(kinds.ContainsKey(ShardKinds.Rule)); // rules need IncludeRules on the selection
        Assert.DoesNotContain(shard.Records, r => r.Label.Contains("Robin", StringComparison.Ordinal));

        using var b = Open("b", emb);
        int texts = emb.Texts;
        var res = await b.Importer.ApplyAsync(shard, new ShardImportOptions { Who = "test" });
        Assert.True(res.Ok, res.Message);
        // Every shard doc took its vector from the shard; the only texts embedded are the target's own docs (its built-in lesson,
        // the personality-change log line of the imported relationship).
        var own = b.Ctx.Db.Query("SELECT hash FROM docs WHERE embed = 1", r => r.GetString(0)).Count(h => !shard.Embeddings.ContainsKey(h));
        Assert.Equal(own, emb.Texts - texts);
        Assert.Equal(4, shard.Records.Count(r => r.DocHash != null && shard.Embeddings.ContainsKey(r.DocHash)));
        Assert.True(res.ReusedVectors >= 4, res.Message);

        // Same records, same ids and hashes, on the other side.
        var back = new ShardCollector(b.Ctx.Db).Select(Person("PoofImaFox", "Fox"));
        Assert.Equal(shard.Records.Select(r => (r.Id, r.Hash)).OrderBy(x => x.Id), back.Select(r => (r.Id, r.Hash)).OrderBy(x => x.Id));
        // Identical vectors (byte for byte) for every record that carries one.
        foreach (var r in shard.Records.Where(r => r.DocHash != null))
        {
            var mine = b.Ctx.Db.Scalar("SELECT v.vec FROM docs d JOIN vectors v ON v.doc_id = d.id AND v.hash = d.hash WHERE d.hash = $h", ("$h", r.DocHash)) as byte[];
            Assert.NotNull(mine);
            Assert.Equal(VectorMath.ToBlob(shard.Embeddings[r.DocHash!]), mine);
        }
        // Audited once.
        var e = Assert.Single(b.Audit.Read());
        Assert.Equal("import", e.Op);
        Assert.Equal("shard", e.Kind);
        Assert.Equal("fox", e.Id);
    }

    [Fact]
    public async Task Merge_dedupes_by_stable_id_and_an_identical_import_writes_nothing()
    {
        var emb = new FakeEmbedder();
        using var a = Open("a", emb);
        await SeedPeopleAsync(a);
        var shard = ShardFile.Read(Export(a, new ShardExportRequest("fox", Person("PoofImaFox", "Fox")), "fox.mcshard"));

        // Back into its own source: everything is already there.
        var self = a.Importer.Preview(shard, new ShardImportOptions());
        Assert.Equal(0, self.Added);
        Assert.Equal(shard.Records.Count, self.Unchanged);
        Assert.True(self.Empty);
        Assert.Empty(self.Rebuild);

        using var b = Open("b", emb);
        Assert.True((await b.Importer.ApplyAsync(shard, new ShardImportOptions())).Ok);
        long notes = b.Ctx.Db.ScalarLong("SELECT count(*) FROM notes");
        long claims = b.Ctx.Db.ScalarLong("SELECT count(*) FROM claims");
        var again = await b.Importer.ApplyAsync(shard, new ShardImportOptions());
        Assert.True(again.Ok, again.Message);
        Assert.Equal(0, again.Written);
        Assert.Equal(shard.Records.Count, again.Diff!.Unchanged);
        Assert.Equal(notes, b.Ctx.Db.ScalarLong("SELECT count(*) FROM notes"));
        Assert.Equal(claims, b.Ctx.Db.ScalarLong("SELECT count(*) FROM claims"));
        Assert.Equal(1, b.Ctx.Self!.Entries.Count(x => x.Player == "PoofImaFox"));
    }

    [Fact]
    public async Task Replace_section_removes_only_the_records_matching_the_selection()
    {
        var emb = new FakeEmbedder();
        using var a = Open("a", emb);
        a.Ctx.Memory.AddNote("PoofImaFox", "likes the cobblestone generator tidy");
        a.Ctx.Memory.AddNote("PoofImaFox", "wants a melon farm");
        var shard = ShardFile.Read(Export(a, new ShardExportRequest("fox", Person("PoofImaFox", "Fox")), "fox.mcshard"));

        using var c = Open("c", emb);
        c.Ctx.Memory.AddNote("PoofImaFox", "likes the cobblestone generator tidy"); // in the shard: kept
        c.Ctx.Memory.AddNote("PoofImaFox", "used to want a cactus wall");          // Fox's, not in the shard: removed
        c.Ctx.Memory.AddNote("kiwi", "Robin prefers the wool sheep pen");          // Robin's: untouched
        c.Ctx.Memory.AddNote("base", "the furnace bank is by the chests");          // nobody's: untouched

        var diff = c.Importer.Preview(shard, new ShardImportOptions { Mode = ShardImportMode.ReplaceSection });
        Assert.Equal(1, diff.Added);
        Assert.Equal(1, diff.Unchanged);
        Assert.Equal(1, diff.Removed);
        Assert.Contains(diff.Items, i => i.Change == ShardChange.Removed && i.Label.Contains("cactus", StringComparison.Ordinal));

        var res = await c.Importer.ApplyAsync(shard, new ShardImportOptions { Mode = ShardImportMode.ReplaceSection });
        Assert.True(res.Ok, res.Message);
        Assert.Equal(1, res.Removed);
        var texts = c.Ctx.Memory.Read(d => d.Notes.Select(n => n.Text).ToList());
        Assert.Contains("wants a melon farm", texts);
        Assert.Contains("likes the cobblestone generator tidy", texts);
        Assert.DoesNotContain("used to want a cactus wall", texts);
        Assert.Contains("Robin prefers the wool sheep pen", texts);
        Assert.Contains("the furnace bank is by the chests", texts);
        Assert.Empty(c.Ctx.Rag.Search(new RagQuery { Text = "cactus wall", Limit = 5 }));
    }

    // ======================= diff =======================

    [Fact]
    public async Task The_import_diff_counts_added_changed_conflicts_unchanged_and_skipped_and_applies_the_policy()
    {
        var emb = new FakeEmbedder();
        using var b = Open("b", emb);
        b.Ctx.Memory.SetWaypoint("mine", 1, 60, 1, null, 0);             // older than the shard's: changed
        Thread.Sleep(30);
        using var a = Open("a", emb);
        a.Ctx.Memory.AddNote("base", "the furnace bank is by the chests");
        a.Ctx.Memory.AddNote("base", "the sheep pen is east");
        a.Ctx.Memory.SetWaypoint("mine", 5, 40, 5, "iron here", 0);
        a.Ctx.Memory.SetWaypoint("home", 0, 64, 0, null, 0);
        var rules = DirectiveStore.For(a.Ctx.Db, a.Ctx.Rag);
        rules.IsTrusted = _ => true;
        Assert.True(rules.Add("PoofImaFox", "never dig under spawn").Stored);
        var shard = ShardFile.Read(Export(a, new ShardExportRequest("base",
            new ShardSelection { Kinds = ["fact", "place", "rule"], IncludeRules = true, IncludeWorld = true }), "base.mcshard"));
        Assert.Equal(5, shard.Records.Count);
        Thread.Sleep(30);
        b.Ctx.Memory.AddNote("base", "the furnace bank is by the chests"); // identical: unchanged
        b.Ctx.Memory.SetWaypoint("home", 9, 70, 9, null, 0);               // newer than the shard's: conflict

        var keepNewer = b.Importer.Preview(shard, new ShardImportOptions { IncludeWorld = true });
        Assert.Equal(1, keepNewer.Added);      // the sheep pen fact
        Assert.Equal(1, keepNewer.Changed);    // mine
        Assert.Equal(1, keepNewer.Conflicts);  // home
        Assert.Equal(1, keepNewer.Unchanged);  // furnace fact
        Assert.Equal(1, keepNewer.Skipped);    // the rule: not chosen
        Assert.Equal(2, keepNewer.Writes);     // added + changed; the conflict keeps the newer local
        Assert.Contains(keepNewer.Items, i => i.Change == ShardChange.Skipped && i.Note!.Contains("rules", StringComparison.Ordinal));

        var noWorld = b.Importer.Preview(shard, new ShardImportOptions());
        Assert.Equal(3, noWorld.Skipped); // both places + the rule
        Assert.Equal(3, b.Importer.Preview(shard, new ShardImportOptions { IncludeWorld = true, Conflict = ShardConflictPolicy.PreferShard }).Writes);
        Assert.Equal(1, b.Importer.Preview(shard, new ShardImportOptions { IncludeWorld = true, Conflict = ShardConflictPolicy.KeepMine }).Writes);
        var withRules = b.Importer.Preview(shard, new ShardImportOptions { IncludeWorld = true, IncludeRules = true });
        Assert.Equal(0, withRules.Skipped);
        Assert.Equal(2, withRules.Added);

        var res = await b.Importer.ApplyAsync(shard, new ShardImportOptions { IncludeWorld = true });
        Assert.True(res.Ok, res.Message);
        Assert.Equal(5, b.Ctx.Memory.GetWaypoint("mine")!.X);   // changed: shard is newer
        Assert.Equal(9, b.Ctx.Memory.GetWaypoint("home")!.X);   // conflict: the newer local stays
        Assert.Empty(DirectiveStore.For(b.Ctx.Db, b.Ctx.Rag).Active()); // rules only when ticked
    }

    // ======================= knowledge packs =======================

    private static void AddGameData(MemoryAdminContext ctx, string furnaceBody, params (string Ref, string Title, string Body)[] extra)
    {
        var docs = new List<RagDoc>
        {
            new("gd:recipe:61:0:1", "gamedata", "recipe Furnace x1", furnaceBody) { Tag = "recipe", Source = "seed" },
            new("gd:recipe:58:0:1", "gamedata", "recipe Crafting Table x1", "Crafting recipe: Crafting Table. Ingredients: 4 Wooden Planks.") { Tag = "recipe", Source = "seed" },
        };
        docs.AddRange(extra.Select(e => new RagDoc(e.Ref, "gamedata", e.Title, e.Body) { Tag = "recipe", Source = "seed" }));
        ctx.Rag.UpsertMany(docs);
    }

    [Fact]
    public async Task Swapping_a_knowledge_pack_changes_the_active_version_and_the_recipe_answers()
    {
        var emb = new FakeEmbedder();
        using var live = Open("live", emb);
        AddGameData(live.Ctx, "Crafting recipe: Furnace. Needs a crafting table. Ingredients: 8 Cobblestone.");
        live.Ctx.Memory.AddNote("base", "the furnace bank is by the chests");
        await live.Ctx.Rag.FlushEmbeddingsAsync();
        Assert.Equal(KnowledgePack.DefaultVersion, KnowledgePack.ActiveVersion(live.Ctx.Db));
        Assert.Null(KnowledgePack.PromptLine(live.Ctx.Db));

        // The b1.8.1 pack as exported from the live db.
        var original = ShardFile.Read(Export(live, new ShardExportRequest("b1.8.1 recipes", new ShardSelection { Mode = "pack" }), "b181.mcshard"));
        Assert.Equal("b1.8.1", original.Manifest.PackVersion);
        Assert.Equal(2, original.Records.Count);

        // Another version's pack, built elsewhere.
        using var other = Open("other", emb);
        AddGameData(other.Ctx, "Crafting recipe: Furnace (beta 1.7.3). Ingredients: 8 Cobblestone, crafted in a workbench.",
            ("gd:recipe:92:0:1", "recipe Cake x1", "Crafting recipe: Cake (beta 1.7.3). Milk, sugar, egg, wheat."));
        other.Ctx.Rag.Delete("gd:recipe:58:0:1");
        await other.Ctx.Rag.FlushEmbeddingsAsync();
        var b173 = ShardFile.Read(Export(other, new ShardExportRequest("b1.7.3 recipes", new ShardSelection { Mode = "pack", PackVersion = "b1.7.3" }), "b173.mcshard"));
        Assert.Equal("b1.7.3", b173.Manifest.PackVersion);

        // Merging another version is refused; swapping is the way.
        var merge = live.Importer.Preview(b173, new ShardImportOptions());
        Assert.NotNull(merge.Refused);
        Assert.Contains("swap", merge.Refused);
        Assert.False((await live.Importer.ApplyAsync(b173, new ShardImportOptions())).Ok);

        var diff = live.Importer.Preview(b173, new ShardImportOptions { Mode = ShardImportMode.SwapPack });
        Assert.Null(diff.Refused);
        Assert.Equal("b1.8.1", diff.PackFrom);
        Assert.Equal("b1.7.3", diff.PackTo);
        Assert.Equal(1, diff.Added);   // cake
        Assert.Equal(1, diff.Removed); // the crafting table (not in that pack)
        var res = await live.Importer.ApplyAsync(b173, new ShardImportOptions { Mode = ShardImportMode.SwapPack });
        Assert.True(res.Ok, res.Message);
        Assert.Equal("b1.7.3", KnowledgePack.ActiveVersion(live.Ctx.Db));
        Assert.Contains("1.7.3", KnowledgePack.PromptLine(live.Ctx.Db));

        var hit = live.Ctx.Rag.Search(new RagQuery { Text = "furnace recipe", Kinds = ["gamedata"], Limit = 3 }).First();
        Assert.Contains("beta 1.7.3", hit.Body);
        Assert.Null(live.Ctx.Rag.GetByRef("gd:recipe:58:0:1"));
        Assert.NotNull(live.Ctx.Rag.GetByRef("gd:recipe:92:0:1"));
        Assert.Single(live.Ctx.Memory.Read(d => d.Notes)); // nothing else touched

        // And back: the original pack restores b1.8.1 and its answers.
        Assert.True((await live.Importer.ApplyAsync(original, new ShardImportOptions { Mode = ShardImportMode.SwapPack })).Ok);
        Assert.Equal("b1.8.1", KnowledgePack.ActiveVersion(live.Ctx.Db));
        Assert.DoesNotContain("1.7.3", live.Ctx.Rag.Search(new RagQuery { Text = "furnace recipe", Kinds = ["gamedata"], Limit = 3 }).First().Body);
        Assert.NotNull(live.Ctx.Rag.GetByRef("gd:recipe:58:0:1"));
        Assert.Null(live.Ctx.Rag.GetByRef("gd:recipe:92:0:1"));
    }

    // ======================= RAPTOR =======================

    private static void AddKnowledge(RagIndex rag, int n)
    {
        for (int i = 0; i < n; i++)
            rag.Upsert(new RagDoc($"kb:t.md:{i}", "knowledge", $"topic {i}", $"{Topics[i % Topics.Length]} note {i} about {Topics[(i + 2) % Topics.Length]}") { Source = "t.md" });
    }

    [Fact]
    public async Task An_import_triggers_a_rebuild_scoped_to_the_kinds_that_changed()
    {
        var emb = new FakeEmbedder();
        using var a = Open("a", emb);
        await SeedPeopleAsync(a);
        var shard = ShardFile.Read(Export(a, new ShardExportRequest("fox", Person("PoofImaFox", "Fox")), "fox.mcshard"));

        using var b = Open("b", emb);
        AddKnowledge(b.Ctx.Rag, 24);
        AddGameData(b.Ctx, "Crafting recipe: Furnace. Ingredients: 8 Cobblestone.");
        await b.Ctx.Rag.FlushEmbeddingsAsync();
        await b.Ctx.Raptor!.BuildAsync();
        int leaves = b.Ctx.Raptor.Index.LeafCount;

        var diff = b.Importer.Preview(shard, new ShardImportOptions { Who = "test" });
        // Facts and the episode are tree kinds; claims and traits aren't; knowledge and game data didn't change.
        Assert.Equal(["fact", "episode"], diff.Rebuild.Select(r => r.Kind).OrderByDescending(k => k).ToList());
        Assert.All(diff.Rebuild, r => Assert.Equal("kind", r.Scope));
        Assert.NotNull(diff.Estimate);

        var res = await b.Importer.ApplyAsync(shard, new ShardImportOptions { Who = "test" });
        Assert.True(res.Ok, res.Message);
        Assert.Equal(diff.Rebuild.Select(r => r.Kind), res.Rebuild.Select(r => r.Kind));
        Assert.True(b.Ctx.Raptor.Index.LeafCount > leaves, "the imported facts/episode joined the tree");

        var rebuilder = new RaptorSectionRebuilder(b.Ctx, b.Audit, "local:" + b.Path) { ReuseSummaries = ShardImporter.ReuseMap(shard) };
        foreach (var r in res.Rebuild)
        {
            var done = await rebuilder.RunAsync(r with { MaxUsd = 1m }, null, CancellationToken.None);
            Assert.True(done.Ok, done.Message);
        }
        var inTree = b.Ctx.Raptor.Index.Read(t => t.Leaves.Select(l => l.Key).OfType<string>().ToHashSet());
        Assert.Contains(inTree, k => k.StartsWith("note:", StringComparison.Ordinal));
        Assert.Contains("ep:r1:1", inTree);
        Assert.Equal(2, b.Audit.Read().Count(e => e.Op == "rebuild"));
    }

    [Fact]
    public async Task A_subtree_shard_carries_its_summaries_as_a_cache_that_a_rebuild_reuses()
    {
        var emb = new FakeEmbedder();
        using var a = Open("a", emb);
        AddKnowledge(a.Ctx.Rag, 30);
        await a.Ctx.Rag.FlushEmbeddingsAsync();
        await a.Ctx.Raptor!.BuildAsync();
        int root = a.Ctx.Raptor.Index.Read(t => t.Roots.OrderByDescending(n => n.Layer).ThenBy(n => n.Index).First().Index);
        var refs = ShardCollector.LeafRefsUnder(a.Ctx.Raptor, root);
        Assert.NotEmpty(refs);
        var shard = ShardFile.Read(Export(a, new ShardExportRequest("topic", new ShardSelection { Mode = "subtree", Node = root, LeafRefs = refs }), "sub.mcshard"));
        Assert.Equal(refs.Count, shard.Records.Count);
        Assert.NotEmpty(shard.Summaries);
        Assert.Equal(shard.Summaries.Count, shard.Manifest.Summaries!.Count);

        // Rebuilding the same subtree with the cache: identical clusters take the cached text, no model call for them.
        var rebuilder = new RaptorSectionRebuilder(a.Ctx, a.Audit, "local:" + a.Path) { ReuseSummaries = ShardImporter.ReuseMap(shard) };
        var r = await rebuilder.RunAsync(new RebuildRequest("node", Node: root, MaxUsd: 1m), null, CancellationToken.None);
        Assert.True(r.Ok, r.Message);
        Assert.True(rebuilder.LastReused > 0, r.Message);
    }

    // ======================= format gates and secrets =======================

    private static byte[] Rewrite(string path, Func<JsonObject, JsonObject> edit)
    {
        using var src = ZipFile.OpenRead(path);
        using var ms = new MemoryStream();
        using (var dst = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var e in src.Entries)
            {
                using var i = e.Open();
                using var buf = new MemoryStream();
                i.CopyTo(buf);
                var bytes = buf.ToArray();
                if (e.FullName == ShardFile.ManifestEntry)
                    bytes = Encoding.UTF8.GetBytes(edit(JsonNode.Parse(bytes)!.AsObject()).ToJsonString());
                using var o = dst.CreateEntry(e.FullName).Open();
                o.Write(bytes);
            }
        return ms.ToArray();
    }

    [Fact]
    public async Task A_manifest_or_schema_version_mismatch_is_refused_with_a_clear_message()
    {
        var emb = new FakeEmbedder();
        using var a = Open("a", emb);
        await SeedPeopleAsync(a);
        var path = Export(a, new ShardExportRequest("fox", Person("Fox")), "fox.mcshard");
        Assert.NotNull(ShardFile.Read(path));

        var future = Assert.Throws<ShardFormatException>(() => ShardFile.Read(Rewrite(path, m => { m["format"] = 2; return m; })));
        Assert.Contains("format v2 is newer than this build reads (v1)", future.Message);

        var schema = Assert.Throws<ShardFormatException>(() => ShardFile.Read(Rewrite(path, m =>
        {
            m["source"]!["schema_version"] = AgentDb.LatestSchemaVersion + 5;
            return m;
        })));
        Assert.Contains($"schema v{AgentDb.LatestSchemaVersion + 5}, newer than this build (v{AgentDb.LatestSchemaVersion})", schema.Message);

        var tampered = Assert.Throws<ShardFormatException>(() => ShardFile.Read(Rewrite(path, m => { m["content_hash"] = new string('0', 64); return m; })));
        Assert.Contains("content hash mismatch", tampered.Message);

        Assert.Throws<ShardFormatException>(() => ShardFile.Read(Encoding.UTF8.GetBytes("not a zip")));
    }

    [Fact]
    public void Secrets_never_appear_in_a_shard()
    {
        var emb = new FakeEmbedder();
        using var a = Open("a", emb);
        a.Ctx.Memory.AddNote("PoofImaFox", "Fox's server password is hunter2");
        a.Ctx.Memory.AddNote("PoofImaFox", "Fox pasted sk-ant-api03-AbCdEfGhIjKlMnOpQrStUv by accident");
        a.Ctx.Memory.AddNote("PoofImaFox", "Fox likes oak trees");
        var path = Export(a, new ShardExportRequest("fox", Person("PoofImaFox")), "fox.mcshard");
        var text = string.Concat(ZipFile.OpenRead(path).Entries.Select(e => { using var r = new StreamReader(e.Open()); return r.ReadToEnd(); }));
        Assert.DoesNotContain("hunter2", text);
        Assert.DoesNotContain("sk-ant-api03", text);
        Assert.Contains("oak trees", text);
        var shard = ShardFile.Read(path);
        Assert.Equal(2, shard.Manifest.SecretsRedacted);
        Assert.Equal(2, shard.Records.Count); // the password note is dropped, the key redacted
    }
}
