using System.Text.Json;
using MineClaw.Agent.Memory;
using MineClaw.Agent.Routines;
using MineClaw.App.Supervisor;
using MineClaw.Scripting;
using MineClaw.World;

namespace MineClaw.App.Tests;

/// <summary>A fake script source: two saved scripts (one migrated from Lua) and one body running a real JS session.</summary>
internal sealed class FakeScriptData : IScriptSupervisorData, IDisposable
{
    public readonly List<Routine> Rows = [];
    public ScriptController Controller { get; }
    public TaskCompletionSource Release { get; } = new();
    public int LiveCalls;

    public FakeScriptData()
    {
        var lua = new ScriptVersion { V = 1, Code = "-- purpose: mine\nfor i=1,4 do gen() end", Lang = null, Runs = 4, Successes = 3, Failures = 1 };
        var js = new ScriptVersion
        {
            V = 2, Code = "// purpose: mine\nconst p = params({n: 4})\nfor (let i = 0; i < p.n; i++) gen()", Lang = "js", Author = "migration/claude-opus-5-5",
            Runs = 2, Successes = 2, LastRun = DateTimeOffset.Parse("2026-10-28T10:00:00Z"),
            ParamRuns = [new ParamRun("{\"n\":4}", true, 12.5, 16, DateTimeOffset.Parse("2026-10-28T10:00:00Z"), "gen 4x 12.0s")],
        };
        Rows.Add(new Routine
        {
            Name = "cobble_loop",
            Script = new RoutineScript
            {
                Code = js.Code, Version = 2, Versions = [lua, js], Lang = "js", Author = js.Author, Runs = 6, Successes = 5, Failures = 1,
                TotalSeconds = 60, Purpose = "mine", Migration = "ok v2", Params = "{\"n\":8}",
            },
        });
        Rows.Add(new Routine { Name = "chop", Script = new RoutineScript { Code = "chop()", Version = 1, Lang = "js", Author = "main/claude-opus-5-5" } });

        var world = new WorldState();
        var host = new ScriptHost
        {
            World = world, Username = "maid_chan",
            Actions = [new ScriptAction("goto", ["to"])],
            Act = async (_, _, ct) => { await Release.Task.WaitAsync(ct); return ActResult.Success("arrived"); },
        };
        Controller = new ScriptController(host);
    }

    public IReadOnlyList<StandingOrder> Orders() =>
    [
        new StandingOrder { Bot = "maid_chan_67", Job = "mine the gen", Tool = "script", Input = JsonDocument.Parse("""{"name":"cobble_loop"}""").RootElement.Clone() },
    ];

    private static ScriptAnalysis Analyze(string code, bool lua) => LiveScriptData.ScriptAnalysisOf(code, lua, ["gen", "goto", "chop"]);

    public IReadOnlyList<ScriptLibraryEntry> Library() => ScriptLibraryReport.List(Rows, Orders(), Analyze);

    public ScriptVersionDetail? Version(string name, int? version) =>
        Rows.FirstOrDefault(r => r.Name == name) is { } r ? ScriptLibraryReport.Detail(r, version, Orders(), Analyze) : null;

    public Task<BodyScriptReply?> LiveAsync(string body, bool code, CancellationToken ct)
    {
        Interlocked.Increment(ref LiveCalls);
        if (body != "main") return Task.FromResult<BodyScriptReply?>(null);
        return Task.FromResult<BodyScriptReply?>(Controller.Current is { } s ? new BodyScriptReply("main", true, s.DebugSnapshot(code)) : new BodyScriptReply("main", false, null));
    }

    public IReadOnlyList<LiveScriptRow> Running() =>
        Controller.Current is { } s ? [new LiveScriptRow("maid_chan", "main", s.Name, s.Status.ToString().ToLowerInvariant(), s.Line)] : [];

    public void Dispose()
    {
        Release.TrySetResult();
        Controller.Dispose();
    }
}

public class SupervisorScriptRouteTests
{
    private const string Token = "s3cret-token";

    private static SupervisorServer Server(FakeScriptData data, string? token = Token) =>
        new(new SupervisorOptions { Token = token }, new FakeSupervisorData()) { Scripts = new SupervisorScriptRoutes(data, token) };

    private static SupervisorRequest Get(string path, string? token = Token, string query = "")
    {
        var q = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in query.Split('&', StringSplitOptions.RemoveEmptyEntries)) { var p = kv.Split('='); q[p[0]] = p.Length > 1 ? p[1] : ""; }
        return new SupervisorRequest("GET", path, q, token);
    }

    [Fact]
    public async Task Script_routes_are_403_without_the_token()
    {
        using var data = new FakeScriptData();
        await using var s = Server(data);
        foreach (var path in new[] { "/api/scripts", "/api/scripts/cobble_loop", "/api/scripts/cobble_loop/1", "/api/bodies/main/script", "/api/bodies/scripts" })
        {
            Assert.Equal(403, (await s.HandleAsync(Get(path, token: null))).Status);
            Assert.Equal(403, (await s.HandleAsync(Get(path, token: "wrong"))).Status);
            Assert.Equal(403, (await s.HandleAsync(Get(path, token: Token + "x"))).Status);
            Assert.Equal(405, (await s.HandleAsync(new SupervisorRequest("POST", path, new Dictionary<string, string>(), Token, "{}"))).Status);
            Assert.Equal(200, (await s.HandleAsync(Get(path))).Status);
        }
        // ?token= works like the rest of the read API; the live probe was never reached without it.
        Assert.Equal(200, (await s.HandleAsync(Get("/api/scripts", token: null, query: "token=" + Token))).Status);
        Assert.Equal(1, data.LiveCalls);
    }

    [Fact]
    public async Task Library_and_version_payloads_have_the_documented_shape()
    {
        using var data = new FakeScriptData();
        await using var s = Server(data);
        var lib = JsonDocument.Parse((await s.HandleAsync(Get("/api/scripts"))).Text).RootElement;
        var row = lib.GetProperty("scripts").EnumerateArray().First(x => x.GetProperty("name").GetString() == "cobble_loop");
        Assert.Equal(2, row.GetProperty("version").GetInt32());
        Assert.Equal(2, row.GetProperty("versions").GetInt32());
        Assert.Equal("js", row.GetProperty("lang").GetString());
        Assert.Equal("migration", row.GetProperty("author_kind").GetString());
        Assert.True(row.GetProperty("has_lua_history").GetBoolean());
        var p = row.GetProperty("params").EnumerateArray().Single();
        Assert.Equal("n", p.GetProperty("name").GetString());
        Assert.Equal("4", p.GetProperty("default").GetString());
        Assert.Equal("8", p.GetProperty("tuned").GetString());
        Assert.Equal(0.833, row.GetProperty("success_rate").GetDouble(), 3);
        Assert.Equal("gen 4x 12.0s", row.GetProperty("timings").GetString());
        Assert.Contains("maid_chan_67: standing order (mine the gen)", row.GetProperty("used_by").EnumerateArray().Select(x => x.GetString()));
        Assert.NotEqual("errors", row.GetProperty("lint").GetProperty("status").GetString());
        Assert.NotEqual("unknown", row.GetProperty("lint").GetProperty("status").GetString());

        var v1 = JsonDocument.Parse((await s.HandleAsync(Get("/api/scripts/cobble_loop/1"))).Text).RootElement;
        Assert.Equal("lua", v1.GetProperty("version").GetProperty("lang").GetString());
        Assert.Equal("legacy", v1.GetProperty("version").GetProperty("author_kind").GetString());
        Assert.Contains("for i=1,4", v1.GetProperty("code").GetString());
        Assert.Equal(2, v1.GetProperty("history").GetArrayLength());

        var v2 = JsonDocument.Parse((await s.HandleAsync(Get("/api/scripts/cobble_loop/v2"))).Text).RootElement;
        Assert.True(v2.GetProperty("version").GetProperty("active").GetBoolean());
        var run = v2.GetProperty("runs").EnumerateArray().Single();
        Assert.Equal("saved", run.GetProperty("source").GetString());
        Assert.Equal(12.5, run.GetProperty("seconds").GetDouble());

        Assert.Equal(404, (await s.HandleAsync(Get("/api/scripts/nope"))).Status);
        Assert.Equal(404, (await s.HandleAsync(Get("/api/scripts/cobble_loop/9"))).Status);
        Assert.Equal(400, (await s.HandleAsync(Get("/api/scripts/cobble_loop/x"))).Status);
    }

    [Fact]
    public async Task Body_script_payload_is_the_live_debugger_snapshot()
    {
        using var data = new FakeScriptData();
        await using var s = Server(data);
        var idle = JsonDocument.Parse((await s.HandleAsync(Get("/api/bodies/main/script"))).Text).RootElement;
        Assert.False(idle.GetProperty("running").GetBoolean());
        Assert.Equal(404, (await s.HandleAsync(Get("/api/bodies/nobody/script"))).Status);

        var (session, err) = data.Controller.Start(new ScriptRequest
        {
            Name = "walk", Code = "const p = params({x: 3})\ngo(p.x, 64, 2)\nreturn 'ok'", Language = ScriptLanguage.Js, ParamsJson = """{"x": 7}""",
        });
        Assert.True(session != null, err);
        var end = DateTime.UtcNow.AddSeconds(5);
        while (session!.Actions == 0 && DateTime.UtcNow < end) await Task.Delay(10);

        var reply = JsonDocument.Parse((await s.HandleAsync(Get("/api/bodies/main/script", query: "code=1"))).Text).RootElement;
        Assert.True(reply.GetProperty("running").GetBoolean());
        var snap = reply.GetProperty("snapshot");
        Assert.Equal("walk", snap.GetProperty("name").GetString());
        Assert.Equal(2, snap.GetProperty("line").GetInt32());
        Assert.Equal("goto", snap.GetProperty("action").GetProperty("label").GetString());
        Assert.Equal("7,64,2", snap.GetProperty("action").GetProperty("args").GetString());
        Assert.Equal(7, snap.GetProperty("params").GetProperty("x").GetInt32());
        Assert.Contains("go(p.x, 64, 2)", snap.GetProperty("code").GetString());
        Assert.True(snap.TryGetProperty("stack", out _));
        Assert.True(snap.TryGetProperty("timings", out _));
        Assert.Equal(0, snap.GetProperty("controls").GetArrayLength());

        var running = JsonDocument.Parse((await s.HandleAsync(Get("/api/bodies/scripts"))).Text).RootElement;
        Assert.Equal("walk", running.GetProperty("bodies").EnumerateArray().Single().GetProperty("script").GetString());

        data.Release.SetResult();
        await session.Completion.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void Debug_snapshot_round_trips_through_the_hive_ipc_json()
    {
        var snap = new ScriptDebugSnapshot
        {
            Name = "x", Line = 4, Stack = [new ScriptFrame("go", "script", 4, 1, true)], Locals = [new ScriptLocal("n", "3", "local")],
            Machine = new ScriptMachineView("m", "mine", 2, [new MineClaw.Scripting.Sdk.MachineStep("a", "mine", "tick", 1.5)], System.Text.Json.Nodes.JsonNode.Parse("""{"k":1}""")),
            Params = System.Text.Json.Nodes.JsonNode.Parse("""{"n":3}"""), Timings = [new ScriptTimingRow("goto", 2, 0, 1.5, 750)],
        };
        var el = MineClaw.Hive.Ipc.HiveJson.ToElement(snap)!.Value;
        var back = el.Deserialize<ScriptDebugSnapshot>(MineClaw.Hive.Ipc.HiveJson.Options)!;
        Assert.Equal(4, back.Line);
        Assert.Equal("go", back.Stack[0].Function);
        Assert.Equal("mine", back.Machine!.History[0].To);
        Assert.Equal(1, back.Machine.Blackboard!["k"]!.GetValue<int>());
        Assert.Equal("n", back.Locals![0].Name);
        Assert.Equal(750, back.Timings[0].AvgMs);
    }
}
