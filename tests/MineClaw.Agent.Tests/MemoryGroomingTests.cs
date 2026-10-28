using System.Globalization;
using System.Text.RegularExpressions;
using MineClaw.Agent.Admin;
using MineClaw.Agent.Memory.Grooming;
using MineClaw.Agent.Rag;

namespace MineClaw.Agent.Tests;

/// <summary>The memory groomer: each stage, the per-source chain, activity coalescing (routine / novel / crew), the one-off
/// compaction and an end-to-end raw alice log excerpt.</summary>
public sealed class MemoryGroomingTests
{
    private const string Main = "maid_chan";
    private static readonly string[] Crew = [Main, "maid_chan_67", "maid_chan_69", "maid_chan_420", "maid_chan_1337"];
    private static readonly DateTimeOffset T0 = new(2026, 10, 28, 0, 0, 0, TimeSpan.Zero);

    private static MemoryGroomer Groomer(AgentDb? db, GroomOptions? o = null, TimeProvider? time = null) =>
        new(o ?? new GroomOptions(), new GroomEnvironment
        {
            Db = db, MainName = Main, Crew = Crew, Players = ["PoofimaFox", "kiwi"], ChunkMaxTokens = 60,
            Time = time ?? new ManualTime { Now = T0.AddHours(6) },
        });

    private static RagDoc Doc(string body, string source = "log", string kind = "episode", string title = "t", string reference = "r:1") =>
        new(reference, kind, title, body) { Source = source };

    private static string Wall(DateTimeOffset t) => t.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture);

    /// <summary>A rollup doc exactly as ActivityLog writes it (text + meta).</summary>
    private static RagDoc Rollup(string player, DateTimeOffset start, DateTimeOffset end, string what, string placeName = "cobble gen",
        string placeKind = "generator", string placeKey = "gen@1,64,1", int dim = 0)
    {
        var at = placeKind == "island" ? " on " + placeName : " at the " + placeName;
        var text = $"{player} {what}{at} ({Wall(start)}-{end:HH:mm})";
        return new RagDoc($"act:{player.ToLowerInvariant()}:{start.ToUnixTimeMilliseconds()}", "episode", $"{player} activity", text)
        {
            Source = "activity", Dimension = dim, At = end, Bounds = Box.Point(1, 64, 1),
            Meta = new Dictionary<string, string>
            {
                [ActivityMeta.Player] = player, [ActivityMeta.Start] = start.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture),
                [ActivityMeta.End] = end.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture),
                [ActivityMeta.PlaceKind] = placeKind, [ActivityMeta.PlaceKey] = placeKey, [ActivityMeta.PlaceName] = placeName,
            },
        };
    }

    private static (AgentDb Db, RagIndex Rag, MemoryGroomer Groomer) Store(GroomOptions? o = null)
    {
        var db = AgentDb.Open(null);
        var time = new ManualTime { Now = T0.AddHours(6) };
        var rag = new RagIndex(db, time: time, backgroundEmbedding: false);
        var g = Groomer(db, o, time);
        rag.Groomer = g;
        return (db, rag, g);
    }

    private static List<(string Ref, string Title, string Body)> ActivityDocs(AgentDb db) =>
        db.Query("SELECT ref, title, body FROM docs WHERE source = 'activity' ORDER BY id", r => (r.GetString(0), r.GetString(1), r.GetString(2)));

    private static int Sum(IEnumerable<string> bodies, string item) =>
        bodies.Sum(b => Regex.Matches(b, $@"(?:mined|chopped) ~?(\d+) {item}").Sum(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)));

    // ======================= stages =======================

    [Fact]
    public void Normalize_strips_prefixes_canonicalizes_spans_trims_coords_and_fixes_names()
    {
        var g = Groomer(null, new GroomOptions { Names = { ["clone 67"] = "maid_chan_67" } });
        var ctx = g.Context(dryRun: true);
        var raw = "06:45:56.496 INF App:   in   game: pos 13.503, 64, -4.642\n" +
                  "2026-10-27T06:45:53+00:00 claudechan mineclaw[12749]: MineClaw.App[0] poofimafox met clone 67\n" +
                  "maid_chan mined 3 cobblestone at the cobble gen (10-28 01:56-01:56) then (10-28 00:31-02:10, 6 sessions)";
        var body = g.Stage("normalize").Apply(Doc(raw), ctx).Single().Body;
        Assert.Equal("in game: pos 13.5,64,-4.6\n10-27 06:45: PoofimaFox met maid_chan_67\n" +
                     "maid_chan mined 3 cobblestone at the cobble gen (10-28 01:56) then (10-28 00:31–02:10, 6 sessions)", body);
        // Idempotent.
        Assert.Equal(body, g.Stage("normalize").Apply(Doc(body), ctx).Single().Body);
    }

    [Fact]
    public void Denoise_drops_debug_and_telemetry_and_strips_inline_costs()
    {
        var g = Groomer(null);
        var ctx = g.Context(dryRun: true);
        var raw = "06:46:59.223 DBG Agent.BotAgent: triage → Main (config)\n" +
                  "06:46:10.020 INF Agent.BotAgent: turn: tier Main calls 1 in 4 out 111 cr 7203 cw 675 $0.0071 | today $0.831/50.00\n" +
                  "06:46:10.009 INF Agent.BotAgent: call opus (claude-opus-5-5) round 0: in 4 out 111 cr 7203 cw 675 cache 91 %\n" +
                  "06:46:10.020 INF App: episode: Main 1 call(s) $0.0071 [in 4 out 111 cr 7203 cw 675] said: is that the front door?\n" +
                  "06:47:03.718 INF App: chat: <PoofimaFox> grab some cobble";
        var body = g.Stage("denoise").Apply(Doc(raw), ctx).Single().Body;
        Assert.DoesNotContain("triage", body);
        Assert.DoesNotContain("turn: tier", body);
        Assert.DoesNotContain("call opus", body);
        Assert.DoesNotContain("$0.0071", body);
        Assert.DoesNotContain("cr 7203", body);
        Assert.Contains("episode: Main said: is that the front door?", body);
        Assert.Contains("chat: <PoofimaFox> grab some cobble", body);
        Assert.Empty(g.Stage("denoise").Apply(Doc("06:46:59.223 DBG Raptor: raptor sync: +4 ~0 -0"), ctx));
    }

    [Fact]
    public void Redact_replaces_token_shaped_secrets()
    {
        var g = Groomer(null);
        var d = g.Stage("redact").Apply(Doc("the key is sk-ant-api03-abcdefghijklmnop ok"), g.Context(true)).Single();
        Assert.Equal("the key is [redacted] ok", d.Body);
    }

    [Fact]
    public void Phrase_uses_natural_verbs_and_third_person_sentences()
    {
        var g = Groomer(null);
        var ctx = g.Context(true);
        var body = g.Stage("phrase").Apply(Doc("kiwi broke 3 log at the tree\nchat: <PoofimaFox> hi maid\nepisode: Main said: hewwo\njoined 10.1.2.69:25565 as maid_chan\nmaid_chan broke cobblestone"), ctx).Single().Body;
        Assert.Equal("kiwi chopped 3 log at the tree\nPoofimaFox said \"hi maid\"\nmaid_chan said \"hewwo\"\nmaid_chan joined the server\nmaid_chan mined cobblestone", body);
        Assert.Equal("chopped", ActivityVerbs.Broken("oak log"));
        Assert.Equal("dug", ActivityVerbs.Broken("dirt"));
        Assert.Equal("cleared", ActivityVerbs.Broken("leaves"));
        Assert.Equal("mined", ActivityVerbs.Broken("cobblestone"));
    }

    [Fact]
    public void Merge_extends_the_same_fact_within_the_window()
    {
        var (db, rag, g) = Store();
        rag.Upsert(Doc("10-28 05:10: PoofimaFox said \"hi\"", reference: "log:a"));
        rag.Upsert(Doc("10-28 05:40: PoofimaFox said \"hi\"", reference: "log:b"));
        rag.Upsert(Doc("10-28 05:41: kiwi said \"yo\"", reference: "log:c"));
        var bodies = db.Query("SELECT ref, body FROM docs ORDER BY id", r => (r.GetString(0), r.GetString(1)));
        Assert.Equal(2, bodies.Count);
        Assert.Equal(("log:a", "PoofimaFox said \"hi\" (10-28 05:10–05:40, 2×)"), bodies[0]);
        rag.Upsert(Doc("10-28 05:50: PoofimaFox said \"hi\"", reference: "log:d"));
        Assert.Equal("PoofimaFox said \"hi\" (10-28 05:10–05:50, 3×)", db.Query("SELECT body FROM docs WHERE ref = 'log:a'", r => r.GetString(0)).Single());
        db.Dispose();
    }

    [Fact]
    public void Size_fills_titles_and_coords_and_splits_long_docs()
    {
        var g = Groomer(null);
        var ctx = g.Context(true);
        var one = g.Stage("size").Apply(Doc("Fox built a hut @3,64,-2. It has a door.", title: ""), ctx).Single();
        Assert.Equal("Fox built a hut @3,64,-2.", one.Title);
        Assert.Equal(Box.Point(3, 64, -2), one.Bounds);
        var lines = string.Join("\n", Enumerable.Range(0, 20).Select(i => $"line {i}: PoofimaFox placed cobblestone along the west wall of the house"));
        var chunks = g.Stage("size").Apply(Doc(lines, reference: "log:x"), ctx);
        Assert.True(chunks.Count > 2);
        Assert.Equal("log:x", chunks[0].Ref);
        Assert.Equal("log:x#2", chunks[1].Ref);
        Assert.All(chunks, c => Assert.True(c.Body.Length / 3.5 <= 61, c.Body));
        Assert.Equal(lines.Split('\n').Length, chunks.Sum(c => c.Body.Split('\n').Length));
    }

    [Fact]
    public void Chains_are_configured_per_source_and_unknown_sources_pass_through()
    {
        var o = new GroomOptions();
        o.Sources["memory"] = ["redact"];
        var (db, rag, g) = Store(o);
        rag.Upsert(Doc("note   with  sk-ant-api03-abcdefghijklmnop", source: "memory", reference: "note:1"));
        rag.Upsert(Doc("gamedata   spacing   kept", source: "seed", reference: "gd:1"));
        Assert.Equal("note   with  [redacted]", db.Query("SELECT body FROM docs WHERE ref = 'note:1'", r => r.GetString(0)).Single());
        Assert.Equal("gamedata   spacing   kept", db.Query("SELECT body FROM docs WHERE ref = 'gd:1'", r => r.GetString(0)).Single());
        Assert.Equal(0, rag.Upsert(Doc("06:46:59.223 DBG Raptor: raptor sync: +4 ~0 -0", reference: "log:noise")));
        Assert.Equal(["redact"], g.StagesFor("memory"));
        db.Dispose();
    }

    // ======================= activity coalescing =======================

    [Fact]
    public void Three_hours_of_minute_by_minute_gen_mining_yield_at_most_two_episodes_with_correct_totals()
    {
        var (db, rag, _) = Store();
        for (int m = 0; m < 180; m++)
        {
            var s = T0.AddMinutes(m);
            rag.Upsert(Rollup(Main, s, s.AddSeconds(40), m % 2 == 0 ? "broke cobblestone" : "mined 3 cobblestone"));
        }
        var docs = ActivityDocs(db);
        Assert.InRange(docs.Count, 1, 2);
        Assert.Equal(90 * 1 + 90 * 3, Sum(docs.Select(d => d.Body), "cobblestone"));
        Assert.All(docs, d => Assert.DoesNotContain("broke", d.Body));
        Assert.StartsWith("maid_chan mined 240 cobblestone at the cobble gen, first time there (10-28 00:00–01:59, 120 sessions)", docs[0].Body);
        Assert.Equal("maid_chan mined 120 cobblestone at the cobble gen (10-28 02:00–02:59, 60 sessions)", docs[1].Body);
        db.Dispose();
    }

    [Fact]
    public void Novel_events_stay_separate_while_routine_work_around_them_merges()
    {
        var (db, rag, _) = Store();
        rag.Upsert(Rollup("kiwi", T0, T0.AddMinutes(1), "broke 5 log", "tree", "tree", "tree@5,64,5"));
        rag.Upsert(Rollup("kiwi", T0.AddMinutes(2), T0.AddMinutes(3), "was slain by Zombie", "tree", "tree", "tree@5,64,5"));
        rag.Upsert(Rollup("kiwi", T0.AddMinutes(4), T0.AddMinutes(5), "broke 4 log", "tree", "tree", "tree@5,64,5"));
        rag.Upsert(Rollup("kiwi", T0.AddMinutes(6), T0.AddMinutes(7), "fought a zombie (2 hits taken)", "tree", "tree", "tree@5,64,5"));
        rag.Upsert(Rollup("kiwi", T0.AddMinutes(8), T0.AddMinutes(9), "placed ~16 cobblestone", "main island", "island", "13,0"));
        rag.Upsert(Rollup("kiwi", T0.AddMinutes(10), T0.AddMinutes(11), "probably broke cobblestone", "unknown build", "unknown_structure", "-23,63,8"));
        rag.Upsert(Rollup("kiwi", T0.AddMinutes(12), T0.AddMinutes(13), "placed sign \"keep out\"", "tree", "tree", "tree@5,64,5"));
        rag.Upsert(Rollup("kiwi", T0.AddMinutes(14), T0.AddMinutes(15), "broke 2 log", "tree", "tree", "tree@5,64,5"));
        var bodies = ActivityDocs(db).Select(d => d.Body).ToList();
        Assert.Equal(6, bodies.Count);
        Assert.Contains("kiwi chopped 11 log at the tree, first time there (10-28 00:00–00:15, 3 sessions)", bodies);
        Assert.Contains(bodies, b => b.StartsWith("kiwi was slain by Zombie at the tree", StringComparison.Ordinal));
        Assert.Contains(bodies, b => b.StartsWith("kiwi fought a zombie (2 hits taken) at the tree", StringComparison.Ordinal));
        Assert.Contains(bodies, b => b.StartsWith("kiwi placed ~16 cobblestone on main island", StringComparison.Ordinal));
        Assert.Contains(bodies, b => b.StartsWith("kiwi probably mined cobblestone at the unknown build", StringComparison.Ordinal));
        Assert.Contains(bodies, b => b.StartsWith("kiwi placed sign \"keep out\" at the tree", StringComparison.Ordinal));
        db.Dispose();
    }

    [Fact]
    public void Crew_bots_on_the_same_job_merge_into_one_crew_episode()
    {
        var (db, rag, _) = Store();
        for (int m = 0; m < 30; m++)
        {
            var s = T0.AddMinutes(m);
            rag.Upsert(Rollup(Main, s, s.AddSeconds(50), "mined 10 cobblestone"));
            rag.Upsert(Rollup("maid_chan_67", s.AddSeconds(5), s.AddSeconds(55), "mined 10 cobblestone"));
            rag.Upsert(Rollup("maid_chan_69", s.AddSeconds(10), s.AddSeconds(58), "mined 10 cobblestone"));
        }
        // A player at the same gen keeps an episode of their own.
        rag.Upsert(Rollup("PoofimaFox", T0.AddMinutes(3), T0.AddMinutes(4), "mined 7 cobblestone"));
        var docs = ActivityDocs(db);
        Assert.Equal(2, docs.Count);
        var crew = docs.Single(d => d.Title == "crew activity").Body;
        Assert.Equal("the crew (main, 67, 69) mined 900 cobblestone (main 300, 67 300, 69 300) at the cobble gen, first time there (10-28 00:00–00:29, 90 sessions)", crew);
        Assert.StartsWith("PoofimaFox mined 7 cobblestone at the cobble gen", docs.Single(d => d.Title == "PoofimaFox activity").Body);
        db.Dispose();
    }

    [Fact]
    public void Crew_bots_at_different_times_keep_their_own_episodes()
    {
        var (db, rag, _) = Store();
        rag.Upsert(Rollup(Main, T0, T0.AddMinutes(5), "mined 10 cobblestone"));
        rag.Upsert(Rollup("maid_chan_67", T0.AddMinutes(60), T0.AddMinutes(65), "mined 10 cobblestone"));
        Assert.Equal(2, ActivityDocs(db).Count);
        db.Dispose();
    }

    [Fact]
    public void Preview_never_writes_the_episode_state()
    {
        var (db, rag, g) = Store();
        rag.Upsert(Rollup(Main, T0, T0.AddMinutes(1), "mined 4 cobblestone"));
        var trace = g.Preview(Rollup(Main, T0.AddMinutes(2), T0.AddMinutes(3), "broke cobblestone"));
        Assert.Equal("maid_chan mined 5 cobblestone at the cobble gen, first time there (10-28 00:00–00:03, 2 sessions)", trace.Output.Single().Body);
        Assert.Contains(trace.Steps, s => s.Stage == "coalesce");
        Assert.Equal("maid_chan mined 4 cobblestone at the cobble gen, first time there (10-28 00:00–00:01)", ActivityDocs(db).Single().Body);
        db.Dispose();
    }

    // ======================= compaction =======================

    [Fact]
    public async Task Compaction_rewrites_300_per_minute_episodes_into_a_few_spans_preserving_facts()
    {
        var db = AgentDb.Open(null);
        var rag = new RagIndex(db, backgroundEmbedding: false); // the old writer: no groomer, one doc per minute
        db.Exec("""
            CREATE TABLE activity_rollups(id INTEGER PRIMARY KEY, player TEXT NOT NULL, start INTEGER NOT NULL, end INTEGER NOT NULL,
                place_kind TEXT, place_key TEXT, place_name TEXT, text TEXT NOT NULL, doc_ref TEXT, dimension INTEGER NOT NULL DEFAULT 0)
            """);
        var players = new[] { Main, "maid_chan_67", "kiwi" };
        int expected = 0, expectedLog = 0;
        for (int m = 0; m < 300; m++)
        {
            var p = players[m % 3];
            var s = T0.AddMinutes(m);
            var d = Rollup(p, s, s.AddSeconds(30), m % 5 == 0 ? "broke cobblestone" : "mined 2 cobblestone",
                p == "kiwi" ? "tree" : "cobble gen", p == "kiwi" ? "tree" : "generator", p == "kiwi" ? "tree@5,64,5" : "gen@1,64,1");
            if (p == "kiwi") { d = d with { Body = d.Body.Replace("cobblestone", "log") }; expectedLog += m % 5 == 0 ? 1 : 2; }
            else expected += m % 5 == 0 ? 1 : 2;
            rag.UpsertGroomed(d);
            db.Exec("INSERT INTO activity_rollups(player, start, end, place_kind, place_key, place_name, text, doc_ref) VALUES ($p, $s, $e, $k, $key, $n, $t, $r)",
                ("$p", p), ("$s", s.ToUnixTimeMilliseconds()), ("$e", s.AddSeconds(30).ToUnixTimeMilliseconds()), ("$k", d.Meta![ActivityMeta.PlaceKind]),
                ("$key", d.Meta[ActivityMeta.PlaceKey]), ("$n", d.Meta[ActivityMeta.PlaceName]), ("$t", d.Body), ("$r", d.Ref));
        }
        // Non-activity memories that must never be touched.
        rag.UpsertGroomed(new RagDoc("note:1", "player", "PoofimaFox", "note#1 PoofimaFox: likes   the carousel") { Source = "memory" });
        rag.UpsertGroomed(new RagDoc("belief:1", "claim", "claim", "kiwi says the gen broke") { Source = "claim" });
        rag.UpsertGroomed(new RagDoc("rule:1", "rule", "rule", "no building at night") { Source = "rule" });
        rag.UpsertGroomed(new RagDoc("self:1", "episode", "personality change", "10-28 00:00: personality grew") { Source = "self" });
        var keep = db.Query("SELECT ref, body, updated FROM docs WHERE source <> 'activity' ORDER BY ref", r => (r.GetString(0), r.GetString(1), r.GetInt64(2)));
        // A RAPTOR section above some of the per-minute leaves.
        db.Exec("INSERT INTO docs(ref, kind, title, body, hash, source, created, updated, level, raptor_idx, raptor_flags, dirty) VALUES ('raptor:9000', 'summary', 's', 'players broke cobblestone', 'h', 'raptor', 0, 0, 1, 9000, 0, 0)");
        db.Exec("INSERT INTO docs(ref, kind, title, body, hash, source, created, updated, level, raptor_idx, raptor_flags, dirty) VALUES ('raptor:9001', 'summary', 's', 'root', 'h2', 'raptor', 0, 0, 2, 9001, 0, 0)");
        db.Exec("INSERT INTO docs(ref, kind, title, body, hash, source, created, updated, level, raptor_idx, raptor_flags, dirty) VALUES ('raptor:9002', 'summary', 's', 'other section', 'h3', 'raptor', 0, 0, 1, 9002, 0, 0)");
        long sec = db.ScalarLong("SELECT id FROM docs WHERE ref = 'raptor:9000'"), root = db.ScalarLong("SELECT id FROM docs WHERE ref = 'raptor:9001'");
        long other = db.ScalarLong("SELECT id FROM docs WHERE ref = 'raptor:9002'");
        db.Exec("INSERT INTO doc_edges(parent, child) VALUES ($r, $s)", ("$r", root), ("$s", sec));
        db.Exec("INSERT INTO doc_edges(parent, child) SELECT $s, id FROM docs WHERE source = 'activity' AND id % 7 = 0", ("$s", sec));
        db.Exec("INSERT INTO doc_edges(parent, child) VALUES ($o, $n)", ("$o", other), ("$n", db.ScalarLong("SELECT id FROM docs WHERE ref = 'note:1'")));

        var groomer = Groomer(db);
        rag.Groomer = groomer;
        var compaction = new ActivityCompaction(db, rag, groomer);
        var dry = await compaction.RunAsync(dryRun: true);
        Assert.Equal(300, ActivityDocs(db).Count); // the dry run wrote nothing
        Assert.Equal(0, groomer.Episodes.Count);

        var report = await compaction.RunAsync(dryRun: false);
        var docs = ActivityDocs(db);
        Assert.Equal(dry.ActivityAfter, report.ActivityAfter);
        Assert.Equal(300, report.ActivityBefore);
        Assert.Equal(docs.Count, report.ActivityAfter);
        Assert.InRange(docs.Count, 3, 6); // 5 h: three windows of the crew at the gen, three of kiwi at the tree
        Assert.Equal(expected, Sum(docs.Select(d => d.Body), "cobblestone"));
        Assert.Equal(expectedLog, Sum(docs.Select(d => d.Body), "log"));
        Assert.All(docs, d => Assert.DoesNotContain("broke", d.Body));
        Assert.Contains(docs, d => d.Body.StartsWith("the crew (main, 67) mined", StringComparison.Ordinal));
        Assert.Equal(300 - docs.Count, report.Removed);
        // Sections above removed leaves are dirty (and their ancestors); an untouched section is not.
        Assert.Equal(2, db.ScalarLong("SELECT raptor_flags & 2 FROM docs WHERE id = $id", ("$id", sec)));
        Assert.Equal(2, db.ScalarLong("SELECT raptor_flags & 2 FROM docs WHERE id = $id", ("$id", root)));
        Assert.Equal(0, db.ScalarLong("SELECT raptor_flags & 2 FROM docs WHERE id = $id", ("$id", other)));
        Assert.True(report.DirtySummaries >= 2);
        // Non-activity memories untouched.
        Assert.Equal(keep, db.Query("SELECT ref, body, updated FROM docs WHERE source <> 'activity' AND source <> 'raptor' ORDER BY ref", r => (r.GetString(0), r.GetString(1), r.GetInt64(2))));

        // Idempotent.
        var again = await compaction.RunAsync(dryRun: false);
        Assert.Equal(0, again.Processed);
        Assert.Equal(docs, ActivityDocs(db));
        Assert.Contains("nothing to compact", again.Message);
        db.Dispose();
    }

    [Fact]
    public async Task Compaction_output_matches_live_ingestion()
    {
        var rollups = Enumerable.Range(0, 40).Select(m => Rollup(m % 2 == 0 ? Main : "maid_chan_69", T0.AddMinutes(m * 3), T0.AddMinutes(m * 3 + 1),
            m % 4 == 0 ? "broke cobblestone" : "mined 3 cobblestone and picked up 3 cobblestone")).ToList();
        var (live, liveRag, _) = Store();
        foreach (var r in rollups) liveRag.Upsert(r);

        var old = AgentDb.Open(null);
        var oldRag = new RagIndex(old, backgroundEmbedding: false);
        foreach (var r in rollups) oldRag.UpsertGroomed(r);
        var g = Groomer(old);
        oldRag.Groomer = g;
        // Without the rollups table the compaction parses the text (no meta): same episodes.
        await new ActivityCompaction(old, oldRag, g).RunAsync(dryRun: false);
        Assert.Equal(ActivityDocs(live).Select(d => d.Body), ActivityDocs(old).Select(d => d.Body));
        live.Dispose();
        old.Dispose();
    }

    // ======================= end to end: a raw alice log excerpt =======================

    private const string AliceExcerpt = """
        06:46:07.352 INF Agent.BotAgent: call opus (claude-opus-5-5) round 0: in 4 out 178 cr 0 cw 7203 cache 0 %
        06:46:07.488 INF Agent.BotAgent: turn: tier Main calls 1 in 4 out 178 cr 0 cw 7203 $0.0396 | today $0.823/50.00 (228 calls, cache 87 %, emb $0.0027), month $0.82/200.00, 21 calls/h | opus $0.661 77c cache 95 %
        06:46:07.489 INF App: episode: Main 1 call(s) $0.0396 [in 4 out 178 cr 0 cw 7203] said: ooh this big plank build with the slab roof is bussin, is it you two's house? fr fr
        06:46:07.489 DBG Agent.BotAgent: triage → Main (config)
        06:46:07.513 INF App: chat: <maid_chan> ooh this big plank build with the slab roof is bussin, is it you two's house? fr fr
        06:46:50.424 INF Raptor: raptor rebuild (changed fraction 26 % > 25 %): 568 leaves, layers [568,105,17,4], 112 summaries (14 reused, 0 pending), $0.0149 (74064+15071 tok), 8905 embed tok, 54500 ms
        06:47:01.566 INF App: episode: Main 1 call(s) $0.0066 [in 4 out 99 cr 7878 cw 596] said:
        06:47:03.718 INF App: chat: <PoofimaFox> grab some cobble stone and come here I nee you to build for me
        06:47:08.865 DBG Agent.BotAgent: tool where_is → ok: cobblestone x68: chest@14,64,-5..15,64,-5 1m E (seen 1m ago)
        06:47:13.861 INF Worker: worker call 0: claude-sonnet-5-5 in 4 out 99 cr 0 cw 5406 cache 0 %
        06:47:15.971 INF Worker: worker task "Take 64 cobblestone from the chest at 14,64,-5": ok: took 64 cobblestone from chest 14,64,-5, inventory has 64. (2 calls, 1 tools, $0.0162, done)
        06:47:20.443 DBG Raptor: raptor sync: +4 ~0 -0
        06:47:59.590 INF App: chat: <PoofimaFox> opened the door for you
        06:48:17.039 INF App: chat: <maid_chan> I made it out, thanks foxy! I'm right by you with a stack of cobble.
        06:48:18.239 INF App: chat: <maid_chan> uwu
        06:49:18.239 INF App: chat: <maid_chan> uwu
        06:50:01.590 INF App: chat: <PoofimaFox> yes we're extending this way 2 rows just this side of the doors
        """;

    [Fact]
    public async Task A_raw_alice_log_excerpt_grooms_into_memory_style_docs()
    {
        var (db, rag, g) = Store();
        var stored = await LogIngest.IngestAsync(rag, g, AliceExcerpt, "log", LogIngest.DayOf("mineclaw-20261027.log"));
        var bodies = db.Query("SELECT body FROM docs WHERE source = 'log' ORDER BY id", r => r.GetString(0));
        Assert.Equal(stored.Count, bodies.Count);
        var all = string.Join("\n", bodies);
        foreach (var noise in new[] { "call opus", "turn: tier", "triage", "raptor sync", "worker call", "$0.0396", "cr 7203", "tool where_is", "DBG", "INF" })
            Assert.DoesNotContain(noise, all);
        Assert.Contains("10-27 06:47: PoofimaFox said \"grab some cobble stone and come here I nee you to build for me\"", bodies);
        Assert.Contains("10-27 06:46: maid_chan said \"ooh this big plank build with the slab roof is bussin, is it you two's house? fr fr\"", bodies);
        // The bot's repeated "uwu" is one doc spanning both times.
        Assert.Contains("maid_chan said \"uwu\" (10-27 06:48–06:49, 2×)", bodies);
        Assert.DoesNotContain(bodies, b => b.Contains("said \"\""));
        // Re-ingesting the same log changes nothing.
        int before = bodies.Count;
        await LogIngest.IngestAsync(rag, g, AliceExcerpt, "log", "10-27");
        Assert.Equal(before, (int)db.ScalarLong("SELECT count(*) FROM docs WHERE source = 'log'"));
        db.Dispose();
    }

    [Fact]
    public async Task The_rewrite_stage_is_off_by_default_and_capped_when_on()
    {
        var g = Groomer(null);
        var ctx = g.Context(true);
        var trace = await g.GroomAsync(Doc("chat: <kiwi> hi", source: "paste", reference: "p:1"), ctx);
        Assert.Equal("kiwi said \"hi\"", trace.Output.Single().Body);

        var o = new GroomOptions { Rewrite = { Enabled = true } };
        var rewriter = new CountingRewriter();
        var g2 = new MemoryGroomer(o, new GroomEnvironment { MainName = Main, Rewriter = rewriter });
        var t2 = await g2.GroomAsync(Doc("chat: <kiwi> hi", source: "paste", reference: "p:1"), g2.Context(true));
        Assert.Equal("kiwi greeted everyone.", t2.Output.Single().Body);
        Assert.Equal(1, rewriter.Calls);
        // The synchronous write path never calls the model.
        g2.Groom(Doc("chat: <kiwi> hi", source: "paste", reference: "p:2"));
        Assert.Equal(1, rewriter.Calls);
    }

    private sealed class CountingRewriter : IGroomRewriter
    {
        public int Calls;
        public Task<string?> RewriteAsync(string source, string text, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult<string?>("kiwi greeted everyone.");
        }
    }
}
