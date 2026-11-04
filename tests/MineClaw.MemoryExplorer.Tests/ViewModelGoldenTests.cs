using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using MineClaw.Agent.Telemetry;
using MineClaw.MemoryExplorer.LiveHive;
using MineClaw.Scripting;
using MineClaw.Scripting.Sdk;
using Xunit.Abstractions;

namespace MineClaw.MemoryExplorer.Tests;

/// <summary>
/// Pins the Explorer's view models and parsers byte for byte (WP08 complexity refactor): the command line over a corpus, and the
/// script debugger / controller panels' whole public state after scripted sequences of snapshots (see <see cref="StateDump"/>).
/// Set EXPLORER_GOLDEN_OUT to a directory to write the dumps out (to diff after a change).
/// </summary>
public sealed class ViewModelGoldenTests(ITestOutputHelper output)
{
    private static string Hash(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..20];

    private void Compare(List<(string Name, string Dump)> rows, string expected)
    {
        var outDir = Environment.GetEnvironmentVariable("EXPLORER_GOLDEN_OUT");
        var actual = new StringBuilder();
        foreach (var (n, dump) in rows)
        {
            if (outDir is { Length: > 0 }) File.WriteAllText(Path.Combine(outDir, n + ".txt"), dump);
            actual.Append(n).Append(' ').Append(Hash(dump)).Append('\n');
        }
        var want = string.Join("\n", expected.Split('\n').Select(l => l.TrimEnd('\r').Trim()).Where(l => l.Length > 0)) + "\n";
        if (want != actual.ToString()) output.WriteLine(actual.ToString());
        Assert.Equal(want, actual.ToString());
    }

    // ------------------------------------------------------------------ command line

    private static readonly string[][] Args =
    [
        [], ["--db", "a.db"], ["--DB", "b.db", "--config", "c.json", "--page", "graph"], ["--theme", "DARK"], ["--theme"], ["--screenshot", "s.png", "--query", "q"],
        ["--node", "12"], ["--node", "x"], ["--node"], ["--graph"], ["--graph-layout", "Radial"], ["--color-by", "TIME"], ["--focus", "7"], ["--focus", "?"],
        ["--tip", "3"], ["--tip", "-"], ["--freecam-profile", "f.json", "--livehive-watch", "w.json"], ["--profile-tab", "GPU"], ["--profile-seconds", "2.5"],
        ["--profile-seconds", "abc"], ["--profile-seconds"], ["--renderer", "GL"], ["--profile-size", "800x600"], ["--profile-size", "800"], ["--profile-size", "axb"],
        ["--profile-size", "1x2x3"], ["--profile-size"], ["unknown", "--db"], ["--db", "--config", "x"], ["--graph-layout"], ["--color-by"], ["--focus"],
        ["--page", "a", "--page", "b"], ["--renderer"], ["--query"], ["--Graph-Layout", "NodeLink", "--focus", "9", "--tip", "1", "--color-by", "cluster"],
    ];

    private static string ParseCorpus()
    {
        var sb = new StringBuilder();
        foreach (var a in Args) sb.Append(string.Join(' ', a)).Append(" => ").Append(StateDump.Of(StartupOptions.Parse(a))).Append('\n');
        return sb.ToString();
    }

    // ------------------------------------------------------------------ script debugger

    private sealed class NoSource : IScriptDebugSource
    {
        public Task<BodyScriptDto> GetAsync(string body, bool code, CancellationToken ct) => throw new InvalidOperationException();
    }

    private static readonly DateTime T0 = new(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc);

    private static ScriptDebugSnapshot Full => new()
    {
        SessionId = 7, Name = "miner", Version = 3, Lang = "js", Status = "running", Live = true, Role = "dig", Crew = "crew-a", Coordinator = "maid_chan",
        Line = 2, Column = 5, LineLive = true, Snippet = "dig(x)", PausePending = true,
        Stack = [new ScriptFrame("dig", "host", 0, 0, false), new ScriptFrame("mineGen", "sdk/50-jobs.js", 12, 5, false), new ScriptFrame("main", "miner.js", 2, 5, true)],
        Action = new ScriptActionView("dig", "5,64,7", 1.25, true, true), Actions = 1, ElapsedSeconds = 75.5,
        Timings = [new ScriptTimingRow("dig", 4, 1, 3.5, 875), new ScriptTimingRow("move", 1, 0, 0.4, 400)],
        Params = new JsonObject { ["count"] = 64, ["block"] = "cobble" }, Args = new JsonObject { ["count"] = 32, ["fast"] = true },
        Checkpoint = "gen", CheckpointsPassed = 1, CheckpointTotal = 3,
        Machine = new ScriptMachineView("m", "mining", 1, [new MachineStep("idle", "mining", "start", 0.5)], new JsonObject { ["n"] = 3 }, 3),
        Locals = [new ScriptLocal("x", "5", "local"), new ScriptLocal("y", "64", "closure")], LocalsAgeSeconds = 0.5,
        Lanes = [new ScriptLaneView(1, "67", "digging")], ProgressPct = 42, ProgressNote = "32/64", LastFailure = "blocked",
        Logs = ["start", "dig 1"], Code = "// miner\ndig(x);\nmove();\nsay('done');", Controls = [],
    };

    private static string ScriptDebugSession()
    {
        var vm = new ScriptDebugViewModel(new NoSource(), "67", "maid_chan_67 · 67", a => a());
        var sb = new StringBuilder();
        void Step(string name, BodyScriptDto reply, int at)
        {
            vm.Apply(reply, T0.AddSeconds(at));
            sb.Append("== ").Append(name).Append('\n').Append(StateDump.Of(vm)).Append('\n');
        }
        vm.Tick(T0);
        sb.Append("== fresh\n").Append(StateDump.Of(vm)).Append('\n');
        vm.Failed("refused");
        sb.Append("== failed first\n").Append(StateDump.Of(vm)).Append('\n');
        Step("none", new BodyScriptDto("67", false, null, "no script"), 0);
        Step("running no snapshot", new BodyScriptDto("67", true, null), 1);
        Step("full", new BodyScriptDto("67", true, Full), 2);
        Step("same run, no code, paused", new BodyScriptDto("67", true, Full with
        {
            Code = null, Paused = true, PausePending = false, PauseKind = "chat", PauseReason = "Fox said stop", PausedSeconds = 95, PatchNote = "learned: resume",
            Line = 4, LineLive = false, InSdk = "sdk/50-jobs.js:41", Snippet = null, Machine = null, Params = null, Args = new JsonObject { ["x"] = 1 },
            Locals = null, LocalsNote = null, LocalsAgeSeconds = 2.5, ProgressPct = null, Lanes = [], Crew = null, Role = null, Version = null,
            Action = new ScriptActionView("move", null, 0.5, false, false, false), Checkpoint = null, CheckpointTotal = 0, Controls = ["pause"],
        }), 3);
        Step("patching", new BodyScriptDto("67", true, Full with { Code = null, Patching = "planning: lowhp", Paused = true, PauseKind = "danger", PauseReason = "lava" }), 4);
        Step("new run, lua, done", new BodyScriptDto("67", false, Full with
        {
            SessionId = 8, Lang = "lua", Code = "-- lua\nlocal a = 1\nreturn a", Live = false, Status = "done", Outcome = "ok: 64 mined", Line = 9,
            Action = new ScriptActionView("say", "", 0.1, false, false, true), Checkpoint = null, CheckpointTotal = 3,
            Machine = new ScriptMachineView("farm", "harvest", 2, [new MachineStep("a", "b", "go", 1), new MachineStep("b", "harvest", "ripe", 2.5)], null),
            Locals = null, LocalsNote = "not sampled", Role = "dig", Crew = "crew-a", Coordinator = null, ProgressNote = "half", ProgressPct = null,
        }, "ended"), 5);
        Step("new run, code withheld", new BodyScriptDto("67", true, Full with { SessionId = 9, Code = null, Line = 0, LineLive = false, Action = null }), 6);
        Step("same run again, line 1", new BodyScriptDto("67", true, Full with { SessionId = 9, Code = "a\nb", Line = 1, Action = null, Live = false, Status = "failed" }), 7);
        vm.Tick(T0.AddSeconds(20));
        sb.Append("== stale\n").Append(StateDump.Of(vm)).Append('\n');
        vm.Failed("timeout");
        sb.Append("== stale failed\n").Append(StateDump.Of(vm)).Append('\n');
        foreach (var s in new[] { Full, Full with { Live = false, Action = null }, Full with { Action = null }, Full with { Stack = [] },
                     Full with { Stack = [new ScriptFrame("main", "m.js", 1, 1, true)] },
                     Full with { Action = new ScriptActionView("dig", null, 0.2, false, false, null) } })
            sb.Append(ScriptDebugViewModel.ActionLine(s)).Append('\n');
        return sb.ToString();
    }

    // ------------------------------------------------------------------ controller panel

    private sealed class NoController : IControllerSource
    {
        public Task<BodyControllerInfo> ControllerAsync(string body, CancellationToken ct) => throw new InvalidOperationException();
        public Task<BrainContextPage> ContextAsync(string brain, long since, string? body, CancellationToken ct) => throw new InvalidOperationException();
        public Task<BrainTurnDetail> TurnAsync(string brain, long turn, string? body, CancellationToken ct) => throw new InvalidOperationException();
    }

    private static BodyControllerInfo Info(string kind, string? owner, string? brain, List<string> tabs, string? filter = null, string? script = null,
        string? linkOwner = null, string? note = null, string? brainKind = "opus", string? model = "claude-opus-5-5", string? provider = "anthropic") =>
        new("67", kind, owner, brain, brainKind, model, provider, tabs.Contains("script"), script, tabs, filter, new ControllerLinks("ctx", "scr", linkOwner), note);

    private static readonly DateTimeOffset D0 = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);

    private static CtxWindow Window(bool inFlight, long prompt, CtxUsage? usage, int systems, long cached) => new(5, 2, D0, "claude-opus-5-5", "main",
        Enumerable.Range(0, systems).Select(i => new CtxSystemBlock(i, $"system {i}", i % 2 == 0, i == 0 ? "1h" : null, 1500 * (i + 1), i < 2)).ToList(),
        12, 4200, ["do", "say"],
        [
            new CtxMessage(0, "user", [new CtxBlock("text", "turn input", Target: true), new CtxBlock("text", "the gen", "memory"), new CtxBlock("text", "summary", "journal")], "turn"),
            new CtxMessage(1, "assistant", [new CtxBlock("thinking", "hmm", Bytes: 120), new CtxBlock("redacted_thinking", Bytes: 64), new CtxBlock("reasoning", "r"),
                new CtxBlock("tool_use", Name: "do", Id: "t1", Input: "{\"a\":1}"), new CtxBlock("text", "ok")]),
            new CtxMessage(2, "user", [new CtxBlock("tool_result", "done", "for 67", Id: "t1", IsError: true), new CtxBlock("tool_result", "fine"),
                new CtxBlock("image", Bytes: 9000), new CtxBlock("image", Label: "screenshot"), new CtxBlock("weird", "w"), new CtxBlock("redacted_thinking", Label: "x")], "tool_results"),
        ], prompt, 18_500, 100_000, 30_000, cached, usage, inFlight);

    private static BrainContextPage Page(long version, CtxWindow? w, string? note = null, bool enabled = true) =>
        new("main", version, D0, 50, enabled, null, [], w, [], null, note);

    private static string ControllerSession()
    {
        var sb = new StringBuilder();
        void Dump(string name, ControllerPanelViewModel vm) => sb.Append("== ").Append(name).Append('\n').Append(StateDump.Of(vm, 3)).Append('\n');
        var infos = new (string Name, BodyControllerInfo I)[]
        {
            ("main", Info("main", null, "main", ["brain"])),
            ("hive owned", Info("hive", "fox_hive", "h2", ["brain", "script"], script: "miner")),
            ("hive of main", Info("hive", "main", "h2", ["brain"])),
            ("hive no owner", Info("hive", null, "h2", ["brain"], brainKind: null, model: "", provider: null)),
            ("subhive", Info("subhive", "main", "main", ["brain"], filter: "67", linkOwner: "maid_chan")),
            ("subhive no owner", Info("subhive", null, "main", ["brain"], filter: "67")),
            ("script", Info("script", "main", null, ["script"], script: "miner", note: "brain asleep")),
            ("script anon", Info("script", null, null, ["script"])),
            ("other", Info("clone", null, null, [])),
        };
        foreach (var (name, info) in infos)
        {
            using var vm = new ControllerPanelViewModel(new NoController(), "67", "maid_chan_67 · 67", () => new NoSource(), a => a());
            vm.ApplyInfo(info);
            Dump("info " + name, vm);
        }
        using (var vm = new ControllerPanelViewModel(new NoController(), "67", "maid_chan_67 · 67", null, a => a()))
        {
            vm.SelectedTab = 1;
            vm.ApplyInfo(Info("main", null, "main", ["brain"]));
            Dump("tab reset", vm);
            vm.ApplyInfo(Info("hive", null, "h", ["brain", "script"]));
            Dump("both tabs, no script source", vm);
            vm.Apply(Page(1, Window(false, 21_000, new CtxUsage(100, 20, 9000, 1200, 0, 0.0123m, 1500, 21_000), 3, 3000), "full", enabled: false), T0);
            Dump("page full", vm);
            vm.SearchText = "tool";
            Dump("search tool", vm);
            vm.SearchText = "";
            vm.Apply(Page(2, Window(true, 0, null, 1, 0)), T0.AddSeconds(1));
            Dump("page in flight", vm);
            vm.Apply(Page(3, Window(false, 95_000, null, 0, 0), "partial"), T0.AddSeconds(2));
            Dump("page near limit", vm);
            vm.Tick(T0.AddSeconds(30));
            Dump("stale", vm);
        }
        // Every block kind as a row, with no message (a response), a user message, a turn input and an assistant message.
        var w = Window(false, 1, null, 0, 0);
        var blocks = w.Messages.SelectMany(m => m.Blocks).Concat([new CtxBlock("text"), new CtxBlock("tool_use", Name: "x", Input: "not json"),
            new CtxBlock("tool_use"), new CtxBlock("thinking", Label: "mine", Bytes: 0), new CtxBlock("redacted_thinking", "t", Bytes: 5)]).ToList();
        CtxMessage?[] msgs = [null, new CtxMessage(0, "user", []), new CtxMessage(0, "user", [], "turn"), new CtxMessage(0, "assistant", [], "response")];
        foreach (var m in msgs)
            foreach (var b in blocks)
                sb.Append(StateDump.Of(ControllerPanelViewModel.Row(m, b))).Append('\n');
        return sb.ToString();
    }

    [Fact]
    public void View_models_and_parsers_match_the_golden_hashes()
    {
        var rows = new List<(string, string)>
        {
            ("startup-args", ParseCorpus()),
            ("script-debug", ScriptDebugSession()),
            ("controller", ControllerSession()),
        };
        Compare(rows, Golden);
    }

    private const string Golden = """
        pending
        """;
}
