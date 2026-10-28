using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using MineClaw.MemoryExplorer.LiveHive;
using MineClaw.Scripting;
using MineClaw.Scripting.Sdk;

namespace MineClaw.MemoryExplorer.Tests.LiveHive;

/// <summary>The Live Hive's script debugger panel: snapshots (as the Supervisor sends them) update it while the script steps.</summary>
public class ScriptDebugPanelTests
{
    private const string Code = """
        const p = params({n: 3})
        return machine({
          initial: 'mine',
          states: {
            mine: { tick: (bb) => { mineGen('A', p.n); bb.mined++ } },
          },
        })
        """;

    /// <summary>Plays the Supervisor: hands out queued snapshots, serialized and parsed exactly like the wire (snake_case JSON).</summary>
    private sealed class FakeSource : IScriptDebugSource
    {
        private static readonly JsonSerializerOptions Wire = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        public readonly Queue<object> Next = new();
        public readonly List<bool> CodeAsked = [];

        public Task<BodyScriptDto> GetAsync(string body, bool code, CancellationToken ct)
        {
            CodeAsked.Add(code);
            var item = Next.Dequeue();
            if (item is Exception ex) throw ex;
            var reply = (BodyScriptDto)item;
            if (!code && reply.Snapshot != null) reply = reply with { Snapshot = reply.Snapshot with { Code = null } };
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new { body = reply.Body, running = reply.Running, snapshot = reply.Snapshot, note = reply.Note }, Wire);
            return Task.FromResult(HiveJson.Parse<BodyScriptDto>(bytes)!);
        }
    }

    private static ScriptDebugSnapshot Snap(int session, int line, string action, string? args, ScriptFrame[] stack, double mined, bool paused = false) => new()
    {
        SessionId = session, Name = "cobble_loop", Version = 3, Status = paused ? "paused" : "running", Live = true, Line = line, Column = 5, LineLive = true,
        Snippet = "mineGen('A', p.n)", Stack = stack, Action = new ScriptActionView(action, args, 1.25, false, true), Actions = 12, ElapsedSeconds = 75,
        Timings = [new ScriptTimingRow("harvest_generator", 11, 1, 30.5, 2772.7), new ScriptTimingRow("dig", 4, 0, 1.2, 300)],
        Params = JsonNode.Parse("""{"n": 5}"""), Checkpoint = "@m", CheckpointsPassed = 1, CheckpointTotal = 0,
        Machine = new ScriptMachineView("m", "mine", 2, [new MachineStep("start", "mine", "enter", 0.1)], JsonNode.Parse($$"""{"mined": {{mined}}}""")),
        Locals = [new ScriptLocal("bb", "{mined: " + mined + "}", "local")], Paused = paused, PauseKind = paused ? "danger" : null,
        PauseReason = paused ? "danger: hp 5/20" : null, PausedSeconds = paused ? 4 : null, Code = Code,
    };

    private static readonly ScriptFrame[] InMineGen =
    [
        new("gen", "sdk/50-jobs.js", 41, 7, false), new("mineGen", "sdk/50-jobs.js", 41, 7, false), new("tick", "script", 5, 25, true),
        new("(top level)", "script", 2, 1, true),
    ];

    [Fact]
    public async Task Panel_follows_the_snapshots_line_state_params_and_goes_stale()
    {
        var src = new FakeSource();
        var vm = new ScriptDebugViewModel(src, "67", "maid_chan_67 · 67", post: a => a()) { StaleAfter = TimeSpan.FromSeconds(2) };
        src.Next.Enqueue(new BodyScriptDto("67", true, Snap(7, 5, "harvest_generator", "4,64,6 5", InMineGen, 1)));
        src.Next.Enqueue(new BodyScriptDto("67", true, Snap(7, 2, "dig", "5,64,7", [new("dig", "script", 2, 1, true), new("(top level)", "script", 2, 1, true)], 2)));
        src.Next.Enqueue(new BodyScriptDto("67", true, Snap(7, 5, "harvest_generator", "4,64,6 5", InMineGen, 3, paused: true)));

        await vm.PollOnceAsync();
        Assert.Equal(7, vm.Lines.Count); // the source arrived with the first snapshot
        Assert.Equal(5, vm.CurrentLine);
        Assert.Equal(4, vm.CurrentIndex);
        Assert.True(vm.Lines[4].IsCurrent);
        Assert.Equal("cobble_loop v3", vm.ScriptName);
        Assert.StartsWith("mineGen → harvest_generator 4,64,6 5", vm.ActionText);
        Assert.Equal(["gen", "mineGen", "tick", "(top level)"], vm.Stack.Select(f => f.Function));
        Assert.Contains(vm.Params, p => p is { Name: "n", Value: "5" });
        Assert.Equal("mine  ·  2 transitions", vm.MachineText);
        Assert.Contains(vm.Blackboard, b => b is { Name: "mined", Value: "1" });
        Assert.Equal("start → mine  (enter)", vm.History.Single().Text);
        Assert.Contains(vm.Locals, l => l.Name == "bb");
        Assert.Equal("harvest_generator", vm.Timings[0].Action);
        Assert.True(vm.Timings[0].HasFails);
        Assert.Equal("12 actions", vm.ActionsText);
        Assert.Equal("1m15s", vm.ElapsedText);
        Assert.False(vm.CanControl); // read-only: no pause/resume yet

        await vm.PollOnceAsync();
        Assert.Equal(2, vm.CurrentLine);
        Assert.False(vm.Lines[4].IsCurrent);
        Assert.True(vm.Lines[1].IsCurrent);
        Assert.StartsWith("dig 5,64,7", vm.ActionText);
        Assert.Contains(vm.Blackboard, b => b is { Name: "mined", Value: "2" });
        Assert.Equal(7, vm.Lines.Count); // same run: the code isn't fetched again
        Assert.Equal([true, false], src.CodeAsked);

        await vm.PollOnceAsync();
        Assert.True(vm.IsPaused);
        Assert.Equal("paused", vm.StatusKind);
        Assert.Contains("danger: hp 5/20", vm.PauseText);

        // No new snapshot for longer than StaleAfter: the badge says so; a failed poll adds why.
        vm.Tick(vm.LastOkAt.AddSeconds(1));
        Assert.False(vm.IsStale);
        vm.Tick(vm.LastOkAt.AddSeconds(3));
        Assert.True(vm.IsStale);
        Assert.Equal("stale 3 s", vm.StaleText);
        src.Next.Enqueue(new HiveApiException(0, "script: timed out"));
        await Assert.ThrowsAsync<HiveApiException>(() => vm.PollOnceAsync());
        vm.Failed("script: timed out");
        vm.Tick(vm.LastOkAt.AddSeconds(4));
        Assert.True(vm.IsStale);
        Assert.Contains("timed out", vm.StaleText);

        // A new run on the body (another session): its code is asked for again.
        src.Next.Enqueue(new BodyScriptDto("67", true, Snap(8, 1, "dig", null, [], 0) with { Code = "dig(1, 64, 2)\nreturn 'x'" }));
        src.Next.Enqueue(new BodyScriptDto("67", true, Snap(8, 1, "dig", null, [], 0) with { Code = "dig(1, 64, 2)\nreturn 'x'" }));
        await vm.PollOnceAsync();
        await vm.PollOnceAsync();
        Assert.Equal(2, vm.Lines.Count);
        Assert.False(vm.IsStale);
        Assert.Equal([true, false, false, false, false, true], src.CodeAsked);
    }

    [Fact]
    public async Task A_body_without_a_script_and_an_ended_run_read_plainly()
    {
        var src = new FakeSource();
        var vm = new ScriptDebugViewModel(src, "main", "maid_chan · main", post: a => a());
        src.Next.Enqueue(new BodyScriptDto("main", false, null, "no script has run on this body yet"));
        await vm.PollOnceAsync();
        Assert.Equal("no script running", vm.StatusText);
        Assert.Equal("no script has run on this body yet", vm.NoteText);
        Assert.Empty(vm.Lines);

        var ended = Snap(3, 4, "dig", "1,64,2", [], 0) with { Live = false, Status = "failed", Outcome = "failed: script cobble_loop at line 4 (dig: no block)", Action = new ScriptActionView("dig", "1,64,2", 0.2, true, false, false) };
        src.Next.Enqueue(new BodyScriptDto("main", false, ended));
        await vm.PollOnceAsync();
        Assert.Equal("failed · line 4", vm.StatusText);
        Assert.Equal("failed: script cobble_loop at line 4 (dig: no block)", vm.FailureText);
        Assert.EndsWith("done in 0.2 s (failed)", vm.ActionText);
    }
}
