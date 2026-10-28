using System.Diagnostics;
using System.Text.Json;
using MineClaw.Scripting;
using Xunit.Abstractions;

namespace MineClaw.Scripting.Tests.Js;

/// <summary>The live debugger's snapshot (Explorer script panel): line tracking, stack, locals, params, machine, overhead.</summary>
public class ScriptDebugTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Line_tracking_follows_the_script_while_it_runs()
    {
        using var rig = Rig.Js();
        var first = new TaskCompletionSource();
        var second = new TaskCompletionSource();
        int gotos = 0;
        rig.On["goto"] = (args, ct) => Interlocked.Increment(ref gotos) == 1 ? Rig.Blocking(first)(args, ct) : Rig.Blocking(second)(args, ct);
        var s = rig.Start("""
            // purpose: walk two places
            const a = 1
            go(1, 64, 2)
            const b = a + 1
            function hop(p) {
              const far = p * 10
              go(far, 64, 0)
            }
            hop(b)
            return 'done'
            """);
        await Rig.Until(() => Volatile.Read(ref gotos) == 1);
        var snap = s.DebugSnapshot(code: true);
        Assert.True(snap.Live);
        Assert.Equal("running", snap.Status);
        Assert.Equal(3, snap.Line);
        Assert.True(snap.LineLive);
        Assert.Equal("go(1, 64, 2)", snap.Snippet);
        Assert.Equal("goto", snap.Action?.Label);
        Assert.True(snap.Action!.Running);
        Assert.Equal("1,64,2", snap.Action.Args);
        Assert.Contains("hop(b)", snap.Code);
        output.WriteLine("raw 1: [" + RawStack(s)?.ReplaceLineEndings(" / ") + "]");
        output.WriteLine("stack 1: " + string.Join(" | ", snap.Stack));
        Assert.Contains(snap.Stack, f => f.Script && f.Line == 3);

        first.SetResult();
        await Rig.Until(() => Volatile.Read(ref gotos) == 2);
        await Rig.Until(() => s.DebugSnapshot().Locals is { Count: > 0 }, 3000);
        snap = s.DebugSnapshot();
        output.WriteLine("stack 2: " + string.Join(" | ", snap.Stack));
        output.WriteLine("locals: " + string.Join(", ", snap.Locals!.Select(l => $"{l.Scope}:{l.Name}={l.Value}")));
        Assert.Equal(7, snap.Line);
        Assert.Equal(["go", "hop", "(top level)"], snap.Stack.Select(f => f.Function));
        Assert.Equal([7, 7, 9], snap.Stack.Select(f => f.Line));
        Assert.Contains(snap.Locals!, l => l.Name == "far" && l.Value == "20");
        Assert.Contains(snap.Locals!, l => l.Name == "p" && l.Value == "2");
        Assert.Equal(1, snap.Timings.Single(t => t.Action == "goto").Calls);
        Assert.Equal(1, snap.Actions - 1); // the first goto finished, the second is in flight

        second.SetResult();
        await s.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        var end = s.DebugSnapshot();
        Assert.False(end.Live);
        Assert.Equal("done", end.Status);
        Assert.Contains("ok: script t done", end.Outcome);
    }

    [Fact]
    public async Task Params_machine_state_and_blackboard_are_in_the_snapshot()
    {
        using var rig = Rig.Js();
        var release = new TaskCompletionSource();
        int mines = 0;
        rig.On["mine"] = (args, ct) => Interlocked.Increment(ref mines) >= 2 ? Rig.Blocking(release)(args, ct) : Task.FromResult(ActResult.Success("mined 1"));
        var s = rig.Start("""
            const p = params({n: 3, cell: 'A'})
            return machine({
              initial: 'prep',
              bb: {mined: 0},
              states: {
                prep: { enter: (bb) => 'mine' },
                mine: {
                  tick: (bb) => {
                    mine('cobblestone', p.n)
                    bb.mined += 1
                    if (bb.mined > 5) return 'done'
                  },
                },
              },
            })
            """, @params: """{"n": 5}""");
        await Rig.Until(() => Volatile.Read(ref mines) >= 2);
        await Rig.Until(() => s.DebugSnapshot().Machine?.Blackboard?["mined"]?.GetValue<double>() >= 1, 3000);
        var snap = s.DebugSnapshot();
        output.WriteLine(JsonSerializer.Serialize(snap, new JsonSerializerOptions { WriteIndented = true }));
        Assert.Equal(5, snap.Params!["n"]!.GetValue<int>());
        Assert.Equal("A", snap.Params!["cell"]!.GetValue<string>());
        Assert.NotNull(snap.Machine);
        Assert.Equal("mine", snap.Machine!.State);
        Assert.Contains(snap.Machine.History, h => h.From == "prep" && h.To == "mine");
        Assert.Equal(1, snap.Machine.Blackboard!["mined"]!.GetValue<double>());
        Assert.Equal("mine", snap.Action?.Label);
        Assert.Equal("cobblestone 5", snap.Action?.Args);
        Assert.Equal(9, snap.Line);
        release.SetResult();
        rig.On.Clear();
        await s.Completion.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Pause_state_and_reason_are_in_the_snapshot()
    {
        using var rig = Rig.Js();
        var release = new TaskCompletionSource();
        bool started = false;
        rig.On["goto"] = Rig.Blocking(release, () => started = true);
        var s = rig.Start("go(1, 64, 2)\nreturn 'x'");
        await Rig.Until(() => started);
        s.Deliver(new ScriptEvent(ScriptEventKind.Danger, "hp 5/20", Topic: "lowhp"));
        await Rig.Until(() => s.Status == ScriptStatus.Paused);
        var snap = s.DebugSnapshot();
        Assert.True(snap.Paused);
        Assert.Equal("danger", snap.PauseKind);
        Assert.Contains("danger", snap.PauseReason);
        Assert.Equal(1, snap.Line);
        Assert.Empty(snap.Controls); // read-only in this version
        s.Stop("test");
        await s.Completion.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static string? RawStack(ScriptSession s) =>
        (typeof(ScriptSession).GetField("_runtime", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(s) as IScriptDebugProbe)?.LastStackText();

    [Fact]
    public void Stack_text_parses_into_frames()
    {
        var frames = ScriptSession.ParseStack(string.Join(Environment.NewLine, "    at gen (sdk/50-jobs.js:41:7)", "    at mineGen (script:7:3)",
            "    at (anonymous) (script:9:1)", "    at script:12:2"));
        Assert.Equal(3, frames.Count);
        Assert.Equal(new ScriptFrame("gen", "sdk/50-jobs.js", 41, 7, false), frames[0]);
        Assert.Equal("mineGen", frames[1].Function);
        Assert.True(frames[1].Script);
        Assert.Equal(new ScriptFrame("(top level)", "script", 9, 1, true), frames[2]);
    }

    /// <summary>
    /// Nobody watching, the probe costs one volatile read per 16 statements and one small object per action: a compute-heavy
    /// benchmark (≈ 4 M statements, 800 actions) runs within 2 % of the same build with the probe off (best of 9, interleaved).
    /// </summary>
    [Fact]
    public async Task Overhead_without_a_subscriber_is_under_two_percent()
    {
        const string bench = """
            let acc = 0
            for (let round = 0; round < 800; round++) {
              for (let i = 0; i < 1000; i++) { acc = (acc + i * 7) % 1000003; const t = acc ^ i; if (t < 0) acc++ }
              mine('dirt', 1)
            }
            return 'acc ' + acc
            """;
        var off = new ScriptOptions { DebugProbe = false, MaxActions = 10_000 };
        var on = new ScriptOptions { DebugProbe = true, MaxActions = 10_000 };
        async Task<double> Once(ScriptOptions o)
        {
            using var rig = Rig.Js(o);
            var sw = Stopwatch.StartNew();
            var s = await rig.Run(bench, "bench");
            sw.Stop();
            Assert.Equal(ScriptStatus.Done, s.Status);
            return sw.Elapsed.TotalMilliseconds;
        }
        await Once(off); await Once(on); // JIT warm-up
        var a = new List<double>();
        var b = new List<double>();
        for (int i = 0; i < 9; i++)
            if (i % 2 == 0) { a.Add(await Once(off)); b.Add(await Once(on)); }
            else { b.Add(await Once(on)); a.Add(await Once(off)); }
        double ratio = b.Min() / a.Min();
        output.WriteLine($"probe off best {a.Min():0.0} ms, probe on (no subscriber) best {b.Min():0.0} ms, ratio {ratio:0.000}");

        // While watched (≈ 5 Hz sampling) the cost is reported, not asserted tightly.
        using (var rig = Rig.Js(on))
        {
            var sw = Stopwatch.StartNew();
            var s = rig.Start(bench, "bench");
            using var poll = new Timer(_ => s.DebugSnapshot(), null, 0, 200);
            await s.Completion.WaitAsync(TimeSpan.FromSeconds(30));
            output.WriteLine($"watched at 5 Hz: {sw.Elapsed.TotalMilliseconds:0.0} ms ({sw.Elapsed.TotalMilliseconds / a.Min():0.000}x)");
        }
        Assert.True(ratio < 1.02, $"overhead {ratio:0.000}x");
    }
}
