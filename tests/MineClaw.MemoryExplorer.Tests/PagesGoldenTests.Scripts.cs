using System.Text;
using MineClaw.Agent.Rag;
using MineClaw.Agent.Routines;
using MineClaw.Data;
using MineClaw.MemoryExplorer.Services;
using MineClaw.MemoryExplorer.ViewModels;
using MineClaw.World;
using MineClaw.World.Designs;

namespace MineClaw.MemoryExplorer.Tests;

/// <summary>The Scripts page over a scripted library and the Designs page over the bot's design tables.</summary>
public sealed partial class PagesGoldenTests
{
    private static readonly DateTimeOffset S0 = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);

    private static ScriptLintInfo LintOf(string status, int errors, int warnings, string? note, params ScriptLintLine[] findings) => new(status, errors, warnings, findings, note);

    private static ScriptVersionInfo V(int v, string lang, string kind, string? author, bool active, int runs = 0, params string[] failures) =>
        new(v, lang, author, kind, v == 2 ? null : S0.AddDays(v), active, runs, runs / 2, runs - runs / 2, 1.25 * v, runs > 0 ? S0 : null, failures, 3 + v);

    private static ScriptLibraryEntry E(string name, int version, int versions, string lang, string kind, string? author, string? purpose, ScriptLintInfo lint,
        IReadOnlyList<string>? usedBy = null, IReadOnlyList<ScriptHandlerInfo>? handlers = null, string? migration = null) =>
        new(name, version, versions, lang, author, kind, purpose, [new ScriptParamInfo("n", "4", "8"), new ScriptParamInfo("cell", null)], S0, 6, 0.5, 2.5,
            "dig 1.2 s", usedBy ?? [], lint, migration, false, null, null, null, S0, lang == "js" && versions > 1, handlers);

    private sealed class FakeLibrary : IScriptLibrarySource
    {
        public string Label => "fake library";
        public bool IsLive { get; init; }
        public required List<ScriptLibraryEntry> Entries { get; init; }
        public required Dictionary<(string, int), ScriptVersionDetail> Details { get; init; }

        public Task<IReadOnlyList<ScriptLibraryEntry>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<ScriptLibraryEntry>>(Entries);

        public Task<ScriptVersionDetail?> VersionAsync(string name, int? version, CancellationToken ct)
        {
            if (name == "broken") throw new InvalidOperationException("db locked");
            var e = Entries.FirstOrDefault(x => x.Name == name);
            if (e == null) return Task.FromResult<ScriptVersionDetail?>(null);
            return Task.FromResult(Details.GetValueOrDefault((name, version ?? e.Version)));
        }
    }

    private static FakeLibrary Library(bool live)
    {
        var handlers = new List<ScriptHandlerInfo>
        {
            new("lowhp", "lowhp|hp 4/20 zombie near the gen at night|mineGen", "eat()", "resume", "learned", "opus", 2, S0, S0.AddHours(1), 5, 1, false),
            new("stuck", "stuck", "jump()", "abort", null, null, null, null, null, 0, 3, true),
        };
        var minerLint = LintOf("errors", 1, 1, "offline", new ScriptLintLine("error", 2, "undef", "x is not defined"), new ScriptLintLine("warning", 3, "loop", "long loop"));
        var miner = E("miner", 3, 3, "js", "opus", "opus/main", "mine the gen", minerLint, ["67", "main"], handlers, "from lua v1");
        var farm = E("farm", 1, 1, "lua", "legacy", null, null, LintOf("warnings", 0, 2, null, new ScriptLintLine("warning", 1, "global", "global a")));
        var empty = E("empty", 2, 2, "js", "migration", "m", "", LintOf("ok", 0, 0, null), ["Robin"]);
        var weird = E("weird", 1, 1, "js", "other", null, "odd", ScriptLintInfo.Unknown);
        var broken = E("broken", 1, 1, "js", "other", "x", null, LintOf("ok", 0, 0, null));
        var gone = E("gone", 1, 1, "js", "other", "x", null, LintOf("ok", 0, 0, null));
        var minerHistory = new List<ScriptVersionInfo> { V(1, "lua", "legacy", null, false, 2, "blocked"), V(2, "js", "migration", "m", false), V(3, "js", "opus", "opus/main", true, 6, "lava", "fell") };
        var runs = new List<ScriptRunInfo>
        {
            new(S0, true, 12.25, "live", Outcome: "ok: 64 mined", Actions: 40),
            new(S0.AddMinutes(1), false, 3, "live", Status: "aborted", FailLine: 7),
            new(S0.AddMinutes(2), false, 0.5, "saved", Params: "n=4", Timings: "dig 1 s", Yield: 12, Body: "67"),
            new(S0.AddMinutes(3), true, 1, "saved", Yield: 0, Params: ""),
        };
        ScriptVersionDetail Detail(ScriptLibraryEntry e, ScriptVersionInfo v, string code, IReadOnlyList<ScriptVersionInfo> history, IReadOnlyList<ScriptRunInfo>? r = null,
            string? revert = null) => new(e, v, code, v.Lang == "lua" && e.Name == "miner" ? LintOf("unknown", 0, 0, "lua") : e.Lint, e.Params, history, r ?? [], revert, null);
        var details = new Dictionary<(string, int), ScriptVersionDetail>
        {
            [("miner", 1)] = Detail(miner, minerHistory[0], "-- miner\nfor i = 1, 4 do\n  gen('4,64,6')\nend", minerHistory, revert: "reverted from v2"),
            [("miner", 2)] = Detail(miner, minerHistory[1], "// miner\nconst p = params({n: 4})\nfor (let i = 0; i < p.n; i++) gen('4,64,6')", minerHistory),
            [("miner", 3)] = Detail(miner, minerHistory[2], "// miner\nconst p = params({n: 4})\nfor (let i = 0; i < p.n; i++) {\n  gen(x)\n}\ndeposit()", minerHistory, runs),
            [("farm", 1)] = Detail(farm, V(1, "lua", "legacy", null, true), "a = 1\nharvest()", [V(1, "lua", "legacy", null, true)]),
            [("empty", 1)] = Detail(empty, V(1, "js", "migration", "m", false), "", [V(1, "js", "migration", "m", false), V(2, "js", "migration", "m", true)]),
            [("empty", 2)] = Detail(empty, V(2, "js", "migration", "m", true), "say('x')", [V(1, "js", "migration", "m", false), V(2, "js", "migration", "m", true)]),
            [("weird", 1)] = Detail(weird, V(1, "js", "other", null, true), "x()", [V(1, "js", "other", null, true)]),
        };
        return new FakeLibrary { IsLive = live, Entries = [miner, farm, empty, weird, broken, gone], Details = details };
    }

    private string Scripts(MainWindowViewModel vm)
    {
        var s = vm.Scripts;
        var sb = new StringBuilder();
        void Dump(string name) => sb.Append("== ").Append(name).Append('\n').Append(D(s, 3)).Append('\n');
        Pump(s.UseAsync(Library(live: false), CancellationToken.None));
        Dump("library, first selected");
        s.SelectedVersion = s.Versions.First(v => v.V.V == 1);
        Dump("v1 shown");
        s.CompareVersion = s.Versions.First(v => v.V.V == 2);
        Dump("v1 against v2");
        s.CompareVersion = s.Versions.First(v => v.V.V == 1);
        Dump("v1 against itself");
        foreach (var name in new[] { "farm", "empty", "weird", "broken", "gone" })
        {
            s.Selected = s.Scripts.First(r => r.Name == name);
            Dump(name);
        }
        s.Filter = "robin";
        Dump("filter robin");
        s.Filter = "";
        Pump(s.UseAsync(Library(live: true), CancellationToken.None));
        Dump("live library");
        Pump(s.UseAsync(null, CancellationToken.None));
        Dump("no source");
        return sb.ToString();
    }

    // ------------------------------------------------------------------ designs

    private static AgentDb DesignDb()
    {
        var db = AgentDb.Open(null);
        db.Exec("""
            CREATE TABLE blueprints(id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL, description TEXT NOT NULL, requester TEXT, bot TEXT NOT NULL,
                dsl TEXT NOT NULL, status TEXT NOT NULL, dim INTEGER NOT NULL, sx INTEGER, sy INTEGER, sz INTEGER, ox INTEGER, oy INTEGER, oz INTEGER,
                rev INTEGER NOT NULL DEFAULT 0, summary TEXT, eval TEXT, critique TEXT, bill TEXT, cost_usd REAL NOT NULL DEFAULT 0, iterations INTEGER NOT NULL DEFAULT 0,
                accepted_by TEXT, png BLOB, created INTEGER NOT NULL, updated INTEGER NOT NULL, anchor TEXT);
            CREATE TABLE blueprint_revisions(blueprint_id INTEGER NOT NULL, rev INTEGER NOT NULL, author TEXT NOT NULL, author_kind TEXT NOT NULL,
                message TEXT NOT NULL, tag TEXT, created INTEGER NOT NULL, dim INTEGER NOT NULL, dsl TEXT NOT NULL, ox INTEGER, oy INTEGER, oz INTEGER,
                blocks TEXT NOT NULL, air TEXT NOT NULL, n INTEGER NOT NULL, hash TEXT NOT NULL, PRIMARY KEY(blueprint_id, rev));
            CREATE TABLE blueprint_world(blueprint_id INTEGER PRIMARY KEY, rev INTEGER NOT NULL, seen INTEGER NOT NULL, seen_by TEXT NOT NULL,
                blocks TEXT NOT NULL, air TEXT NOT NULL, diff TEXT NOT NULL, causes TEXT NOT NULL);
            INSERT INTO blueprints(name, description, requester, bot, dsl, status, dim, created, updated)
                VALUES('spiral_staircase', 'captured', 'PoofImaFox', 'maid_chan', 'blueprint x', 'captured', 0, 1, 2);
            INSERT INTO blueprints(name, description, requester, bot, dsl, status, dim, created, updated)
                VALUES('tower', 'big', NULL, 'maid_chan', 'blueprint y', 'draft', 0, 1, 3);
            INSERT INTO blueprints(name, description, requester, bot, dsl, status, dim, created, updated)
                VALUES('hut', 'small', 'kiwi', 'maid_chan', 'blueprint z', 'built', 0, 1, 4);
            """);
        var r1 = new DesignSnapshot();
        for (int y = 64; y <= 67; y++) r1.Blocks[new BlockPos(0, y, 0)] = (Blocks.Cobblestone, 0);
        r1.Blocks[new BlockPos(1, 64, 0)] = (Blocks.WoodenStairs, 0);
        r1.Air.Add(new BlockPos(1, 65, 0));
        var r2 = r1.Clone();
        r2.Blocks[new BlockPos(1, 65, 0)] = (Blocks.Planks, 0);
        r2.Blocks[new BlockPos(1, 64, 0)] = (Blocks.WoodenStairs, 2);
        r2.Blocks.Remove(new BlockPos(0, 67, 0));
        var big = new DesignSnapshot();
        for (int x = 0; x < 30; x++)
            for (int z = 0; z < 20; z++) big.Blocks[new BlockPos(x, 64, z)] = (Blocks.Stone, 0);
        void Rev(int id, int rev, string author, string message, DesignSnapshot s, string? tag = null) =>
            db.Exec("INSERT INTO blueprint_revisions VALUES($id,$r,$a,'player',$m,$t,$c,0,'',0,64,0,$b,$air,$n,$h)", ("$id", id), ("$r", rev), ("$a", author), ("$m", message),
                ("$t", tag), ("$c", 1_000 * rev), ("$b", DesignSnapshot.EncodeStates(s.Blocks)), ("$air", DesignSnapshot.EncodeCells(s.Air)), ("$n", s.Count), ("$h", s.Hash()));
        Rev(1, 1, "PoofImaFox", "captured by PoofImaFox", r1);
        Rev(1, 2, "PoofImaFox", "a landing", r2, "good");
        Rev(1, 3, "maid_chan", "nothing", r2);
        Rev(2, 1, "maid_chan", "empty start", new DesignSnapshot());
        Rev(2, 2, "maid_chan", "a floor", big);
        var seen = r2.Clone();
        seen.Blocks.Remove(new BlockPos(0, 64, 0));
        seen.Air.Add(new BlockPos(0, 64, 0));
        db.Exec("INSERT INTO blueprint_world VALUES(1,2,5000,'maid_chan',$b,$a,'−1 cobblestone','kiwi 1')",
            ("$b", DesignSnapshot.EncodeStates(seen.Blocks)), ("$a", DesignSnapshot.EncodeCells(seen.Air)));
        db.Exec("INSERT INTO blueprint_world VALUES(3,0,5000,'maid_chan',$b,$a,'+1 dirt','')",
            ("$b", DesignSnapshot.EncodeStates(seen.Blocks)), ("$a", DesignSnapshot.EncodeCells(seen.Air)));
        return db;
    }

    private string Designs(MainWindowViewModel vm)
    {
        var d = vm.Designs;
        var sb = new StringBuilder();
        void Dump(string name) => sb.Append("== ").Append(name).Append('\n').Append(D(d, 3)).Append('\n');
        using var db = DesignDb();
        d.Use(new DesignHistorySource(db));
        Dump("loaded");
        foreach (var id in new long[] { 1, 2, 3 })
        {
            d.Selected = d.Designs.First(x => x.E.Id == id);
            Dump("design " + id);
            foreach (var to in d.Targets.ToList().Append(null))
            {
                d.To = to;
                Dump($"design {id} to {to}");
            }
            foreach (var from in d.Revisions.ToList())
            {
                d.From = from;
                Dump($"design {id} from {from}");
            }
            d.From = null;
            Dump($"design {id} from nothing");
        }
        d.Filter = "hut";
        Dump("filter hut");
        d.Filter = "";
        using var bare = AgentDb.Open(null);
        d.Use(new DesignHistorySource(bare));
        Dump("no designs table");
        d.Use(null);
        Dump("no source");
        return sb.ToString();
    }

    [Fact]
    public void Scripts_and_designs_pages_match_the_golden_hashes()
    {
        string designs = "";
        var scripts = Session(() =>
        {
            var vm = NewShell();
            try
            {
                designs = Designs(vm);
                return Scripts(vm);
            }
            finally { Close(vm); }
        });
        Compare([("scripts", scripts), ("designs", designs)], ScriptsGolden);
    }

    private const string ScriptsGolden = """
        scripts 23925D9ADD1291B75324
        designs 063336E796E9C0A61D72
        """;
}
