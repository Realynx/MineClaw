using System.Collections.Concurrent;
using MineClaw.Data;
using MineClaw.Scripting;

namespace MineClaw.Scripting.Tests.Js;

/// <summary>The JS engine (Jint): sandbox, limits, bindings, interrupts, pause/resume, checkpoints, per-body routing.</summary>
public class JsSandboxTests
{
    [Theory]
    [InlineData("eval(\"go('escaped')\")")]
    [InlineData("new Function(\"go('escaped')\")()")]
    [InlineData("Function(\"return 1\")()")]
    [InlineData("require('fs')")]
    [InlineData("importNamespace('System.IO')")]
    [InlineData("System.IO.File.ReadAllText('appsettings.json')")]
    [InlineData("process.exit(1)")]
    [InlineData("__h.wait(1)")]
    [InlineData("gen = () => 1")]
    [InlineData("me.hp = 1; if (me.hp !== 20) {} else throw new Error('frozen')")]
    [InlineData("inv.free = () => 99")]
    [InlineData("'x'.repeat(1e9)")]
    [InlineData("'x'.padStart(1e9)")]
    [InlineData("let s = 'x'; for (let i = 0; i < 40; i++) s = s + s + 'y'")]
    [InlineData("const a = []; for (let i = 0; i < 1e8; i++) a.push(i)")]
    [InlineData("new Array(1e9).fill(0)")]
    [InlineData("/(a+)+$/.test('a'.repeat(5000) + 'b')")]
    [InlineData("setTimeout(() => go('escaped'), 1)")]
    public async Task Escapes_and_bombs_fail_without_touching_the_host(string code)
    {
        using var rig = Rig.Js();
        var s = await rig.Run(code + "\ngo('escaped')");
        Assert.Equal(ScriptStatus.Failed, s.Status);
        Assert.DoesNotContain(rig.CallList, c => c.Contains("escaped"));
        Assert.StartsWith("failed: script t", s.Outcome);
    }

    [Fact]
    public async Task Host_signals_cannot_be_caught_by_try_catch()
    {
        using var rig = Rig.Js(new ScriptOptions { MaxActions = 2 });
        var s = await rig.Run("for (let i = 0; i < 10; i++) { try { mine('dirt', 1) } catch (e) { log('caught ' + e) } }\nreturn 'escaped the limit'");
        Assert.Equal(ScriptStatus.Failed, s.Status);
        Assert.Contains("action limit (2)", s.Outcome);
        Assert.DoesNotContain(rig.Logs, l => l.Contains("caught"));
    }

    [Fact]
    public async Task Only_safe_globals_exist()
    {
        using var rig = Rig.Js();
        var s = await rig.Run("""
            const names = ['eval', 'require', 'importNamespace', 'System', 'process', 'setTimeout', 'fetch', '__h', 'H', 'run_command', 'ask_player']
            const bad = names.filter(n => typeof globalThis[n] !== 'undefined' && n !== 'eval')
            return 'bad:' + bad.join(',') + '.' + typeof me + typeof inv + typeof machine
            """);
        Assert.Equal(ScriptStatus.Done, s.Status);
        Assert.Contains("bad:.objectfunctionfunction", s.Outcome);
    }
}

public class JsLimitTests
{
    [Theory]
    [InlineData("while (true) {}")]
    [InlineData("for (;;) {}")]
    [InlineData("while (inv('dirt') < 5) {}")]
    [InlineData("for (let i = 0; i < 1e12; i++) { const x = me.hp }")]
    public async Task Busy_loops_are_stopped(string code)
    {
        using var rig = Rig.Js(new ScriptOptions { MaxInstructionsWithoutAction = 20_000 });
        var s = await rig.Run(code);
        Assert.Equal(ScriptStatus.Failed, s.Status);
        Assert.Contains("busy loop", s.Outcome);
    }

    [Fact]
    public async Task Statement_limit()
    {
        using var rig = Rig.Js(new ScriptOptions { MaxInstructions = 50_000, MaxActions = 100_000 });
        var s = await rig.Run("for (let i = 0; i < 1e9; i++) { if (i % 50 === 0) mine('dirt', 1) }");
        Assert.Contains("instruction limit", s.Outcome);
    }

    [Fact]
    public async Task Wall_time_and_action_limits()
    {
        using var rig = Rig.Js(new ScriptOptions { MaxMinutes = 0.01 });
        var s = await rig.Run("while (true) wait(0.1)");
        Assert.Contains("time limit", s.Outcome);
        using var rig2 = Rig.Js(new ScriptOptions { MaxActions = 5 });
        var t = await rig2.Run("while (true) mine('dirt', 1)");
        Assert.Contains("action limit (5)", t.Outcome);
        Assert.Equal(5, rig2.CallList.Count);
    }

    [Fact]
    public async Task Recursion_syntax_and_runtime_errors_name_the_line()
    {
        using var rig = Rig.Js();
        var r = await rig.Run("function f() { return f() + 1 }\nf()");
        Assert.Contains("call depth limit", r.Outcome);
        var s = await rig.Run("const a = 1\nconst x = ;", "syn");
        Assert.Equal(ScriptStatus.Failed, s.Status);
        Assert.Contains("syntax", s.Outcome);
        Assert.Contains("line 2", s.Outcome);
        var e = await rig.Run("const a = 1\nconst b = null\nb.x = 1", "err");
        Assert.StartsWith("failed: script err at line 3 (error: ", e.Outcome);
        var u = await rig.Run("mine('dirt', 1)\nnotAThing()", "ref");
        Assert.Contains("line 2", u.Outcome);
        Assert.Contains("notAThing is not defined", u.Outcome);
    }

    [Fact]
    public async Task Await_is_accepted_and_promises_that_never_settle_fail()
    {
        using var rig = Rig.Js();
        var s = await rig.Run("const r = await mine('dirt', 2)\nasync function twice() { await wait(0.05); return (await mine('dirt', 1)).ok }\nreturn r.ok && await twice() ? 'awaited' : 'no'");
        Assert.Contains("awaited", s.Outcome);
        var p = await rig.Run("await new Promise(() => {})\nreturn 'never'", "p");
        Assert.Equal(ScriptStatus.Failed, p.Status);
        Assert.Contains("never settled", p.Outcome);
    }
}

public class JsBindingTests
{
    [Fact]
    public async Task Action_failure_is_a_result_not_a_throw()
    {
        using var rig = Rig.Js();
        rig.On["goto"] = (_, _) => Task.FromResult(ActResult.Fail("no route"));
        var s = await rig.Run("const r = go('home')\nif (r.ok) return 'bad'\nreturn 'handled ' + r.msg");
        Assert.Contains("handled no route", s.Outcome);
        var f = await rig.Run("mine('cobblestone', 4)\nif (!go('home').ok) fail()", "trip");
        Assert.Equal(ScriptStatus.Failed, f.Status);
        Assert.StartsWith("failed: script trip at line 2 (goto: no route): 2 acts", f.Outcome);
        var g = await rig.Run("mine('cobblestone', 4)\ngo('home')", "silent");
        Assert.Equal(ScriptStatus.Failed, g.Status); // ended right after a failed action without saying how it went
    }

    [Fact]
    public async Task Positional_and_named_args_map_to_tool_input()
    {
        using var rig = Rig.Js();
        var s = await rig.Run("""
            go(1, 64, 2); go(me.pos); go({x: 3, y: 4, z: 5}); go('home', {dist: 2}); go('spawn')
            mine('cobblestone', 16); mine({block: 'dirt', n: 3}); mine('dirt', undefined)
            deposit({items: ['cobblestone'], at: {x: 1, y: 2, z: 3}}); deposit({}); deposit()
            dig('4,5,6'); chop(); chopTree(); take('coal', 8, {at: '1,64,2'}); goto('x')
            """);
        Assert.True(s.Ok, s.Outcome);
        Assert.Equal([
            """goto {"to":"1,64,2"}""", """goto {"to":"0,65,0"}""", """goto {"to":"3,4,5"}""", """goto {"dist":2,"to":"home"}""", """goto {"to":"spawn"}""",
            """mine {"block":"cobblestone","count":16}""", """mine {"block":"dirt","count":3}""", """mine {"block":"dirt"}""",
            """deposit {"at":"1,2,3","items":["cobblestone"]}""", "deposit {}", "deposit {}",
            """dig {"at":"4,5,6"}""", "chop_tree {}", "chop_tree {}", """withdraw {"at":"1,64,2","count":8,"item":"coal"}""", """goto {"to":"x"}""",
        ], rig.Norm());
    }

    [Fact]
    public async Task Results_carry_ok_msg_n()
    {
        using var rig = Rig.Js();
        rig.On["mine"] = (_, _) => Task.FromResult(ActResult.Success("mined 12 cobblestone (inv 44)"));
        var s = await rig.Run("const r = mine('cobblestone', 12)\nreturn `${r.ok}/${r.msg}/${r.n}`");
        Assert.Contains("true/mined 12 cobblestone (inv 44)/12", s.Outcome);
        var t = await rig.Run("return mine('cobblestone', 12)", "ret");
        Assert.True(t.Ok, t.Outcome); // returning an action's own result: its ok decides, its msg is the text
        Assert.Contains("mined 12 cobblestone", t.Outcome);
    }

    [Fact]
    public async Task Queries_read_the_world()
    {
        using var rig = Rig.Js();
        rig.Give(36, Items.ByName("cobblestone")!.Id, 5);
        var s = await rig.Run("""
            const n = near('stone'), p = players()
            const got = [inv('cobblestone'), inv.has('cobblestone', 5), inv.has('cobblestone', 6), inv.free(), me.hp, me.food, me.pos.y, me.dim,
              block(0, 64, 0), block({x: 0, y: 70, z: 0}), n !== null && n.d < 3, where('iron'), where('gold'), typeof time(), night(),
              p[0].name + '+' + p[1].name, near('home').x, me.name, me.body].join('|')
            const want = '5|true|false|35|20|20|65|0|stone|air|true|iron: chest 3,64,2 (x12)||number|false|kiwi+robin|5|maid_chan|main'
            if (got === want) return 'same'
            log(got)
            return 'diff'
            """);
        Assert.True(s.Outcome!.Contains("(same,"), s.Outcome + " " + string.Join(" ", rig.Logs));
    }

    [Fact]
    public async Task Skills_aliases_camelCase_and_the_skill_fallback()
    {
        using var rig = Rig.Js();
        var s = await rig.Run("""
            move({item: 'log', n: 'all', to: 'wood'})
            harvest_cane(4); harvestCane({radius: 5}); skill('harvest_cane', {radius: 6}); skill('log'); log('hi')
            const r = skill('toss_it')
            return 'said: ' + r.msg
            """);
        Assert.True(s.Ok, s.Outcome);
        Assert.Equal([
            """skill:move_items {"moves":[{"count":"all","item":"log","to":"wood"}]}""", """skill:harvest_cane {"radius":4}""",
            """skill:harvest_cane {"radius":5}""", """skill:harvest_cane {"radius":6}""", "skill:log {}",
        ], rig.Norm());
        Assert.Contains("script t: hi", rig.Logs);
        Assert.Contains("no skill 'toss_it'", s.Outcome);
    }

    [Fact]
    public async Task No_server_commands_and_chat_is_rate_limited()
    {
        using var rig = Rig.Js(new ScriptOptions { MaxSayPerRun = 10, SayMinIntervalSeconds = 0 });
        var s = await rig.Run("say('/op maid_chan'); say('hi /kill @a'); emote('/gamemode 1 waves'); ask('kiwi', '/stop now?', 5)");
        Assert.Equal(["op maid_chan", "hi kill @a", "kiwi: stop now?"], rig.Said);
        Assert.Equal(["gamemode 1 waves"], rig.Emoted);
        using var rig2 = Rig.Js(new ScriptOptions { MaxSayPerRun = 10, SayMinIntervalSeconds = 0 });
        var t = await rig2.Run("let ok = 0\nfor (let i = 1; i <= 8; i++) if (say('hi ' + i).ok) ok++\nreturn ok + ' ' + ask('kiwi', 'which chest?', 10)");
        Assert.Contains("(6 yes,", t.Outcome);
    }

    [Fact]
    public async Task Wake_ask_brain_and_resume_answer()
    {
        using var rig = Rig.Js();
        var w = await rig.Run("const a = wake('no saplings left'), b = wake('again')\nreturn `${a}${b}`");
        Assert.Contains("truefalse", w.Outcome);
        Assert.Equal(["no saplings left"], rig.Wakes);
        var s = rig.Start("const a = ask('brain', 'which chest?')\nreturn 'got ' + a", "q");
        await s.WhenSettled().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(PauseKind.Script, s.PausedFor);
        Assert.Contains("needs_input: which chest?", s.State());
        s.Resume("chest 2");
        await s.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("got chest 2", s.Outcome);
    }

    [Fact]
    public async Task Fail_is_for_errors_not_data()
    {
        using var rig = Rig.Js();
        var s = await rig.Run("fail('4,6=cobblestone 5,6=water 6,6=air')");
        Assert.Contains("fail() is for errors, not data", s.Outcome);
    }
}

public class JsInterruptTests
{
    [Fact]
    public async Task Pause_waits_for_the_atomic_action_then_stops_between_actions()
    {
        using var rig = Rig.Js();
        var inside = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        bool cancelled = false;
        rig.On["deposit"] = async (_, ct) =>
        {
            inside.TrySetResult();
            await release.Task;
            cancelled |= ct.IsCancellationRequested;
            return ActResult.Success("deposited 5 stacks");
        };
        var s = rig.Start("deposit()\nmine('dirt', 1)\nreturn 'done'");
        await inside.Task.WaitAsync(TimeSpan.FromSeconds(5));
        s.Deliver(new ScriptEvent(ScriptEventKind.Danger, "hp 5/20"));
        await Task.Delay(100);
        Assert.Equal(ScriptStatus.Running, s.Status); // still inside the atomic action
        release.SetResult();
        await s.WhenSettled().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ScriptStatus.Paused, s.Status);
        Assert.False(cancelled);
        Assert.DoesNotContain(rig.CallList, c => c.StartsWith("mine"));
        s.Resume();
        await s.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(rig.CallList, c => c.StartsWith("mine"));
    }

    [Fact]
    public async Task Danger_aborts_the_walk_pauses_with_state_and_reruns_it_on_resume()
    {
        using var rig = Rig.Js();
        int gotos = 0;
        var release = new TaskCompletionSource();
        var started = new SemaphoreSlim(0);
        rig.On["goto"] = Rig.Blocking(release, () => { Interlocked.Increment(ref gotos); started.Release(); });
        string? notice = null;
        rig.C.Paused += p => notice = p.State();
        var s = rig.Start("cp('walk')\ncarry.n = 3; carry.who = 'kiwi'\nprog(40, 'walking')\ngo('home')\nreturn 'arrived'", "fetch");
        Assert.True(await started.WaitAsync(5000));
        s.Deliver(new ScriptEvent(ScriptEventKind.Danger, "[DANGER] hp 4/20 skeleton"));
        await s.WhenSettled().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(PauseKind.Danger, s.PausedFor);
        foreach (var part in new[] { "script fetch paused (danger: [DANGER] hp 4/20", "L4", "`go('home')`", "cp 'walk' 1/1", "40% walking", "carry.n=3", "carry.who='kiwi'", "1 acts" })
            Assert.Contains(part, notice);
        release.SetResult();
        s.Resume();
        await s.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, gotos); // aborted through its own cancellation, re-run after resume
        Assert.Contains("arrived", s.Outcome);
    }

    [Fact]
    public async Task Danger_handler_runs_first_and_the_script_continues()
    {
        using var rig = Rig.Js();
        int gotos = 0;
        var release = new TaskCompletionSource();
        rig.On["goto"] = Rig.Blocking(release, () => Interlocked.Increment(ref gotos));
        bool paused = false;
        rig.C.Paused += _ => paused = true;
        var s = rig.Start("let hits = 0\non('danger', e => { hits++; carry.last = e.text; eat() })\ngo('home')\nreturn 'h' + hits + ' ' + carry.last");
        await Rig.Until(() => gotos == 1);
        var ev = new ScriptEvent(ScriptEventKind.Danger, "hp 6/20");
        Assert.True(s.HasHandler(ev));
        s.Deliver(ev);
        await Rig.Until(() => gotos == 2);
        release.SetResult();
        await s.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(paused);
        Assert.Contains("h1 hp 6/20", s.Outcome);
        Assert.Contains(rig.CallList, c => c.StartsWith("eat"));
    }

    [Fact]
    public async Task Trusted_stop_is_a_hard_pause_and_chat_filters_work()
    {
        using var rig = Rig.Js();
        var s = rig.Start("on('chat', {from: 'kiwi'}, e => { carry.said = e.text })\nwhile (!carry.said) wait(0.05)\nwhile (true) wait(0.05)");
        var mine = new ScriptEvent(ScriptEventKind.Chat, "grab 32 logs", "KIWI");
        await Rig.Until(() => s.HasHandler(mine));
        Assert.False(s.HasHandler(new ScriptEvent(ScriptEventKind.Chat, "hey", "robin")));
        s.Deliver(mine);
        await Rig.Until(() => s.Vars?.Contains("grab 32") == true);
        s.Deliver(new ScriptEvent(ScriptEventKind.Stop, "stop!", "kiwi"));
        await s.WhenSettled().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(PauseKind.Stop, s.PausedFor);
        await Task.Delay(1200);
        Assert.Equal(ScriptStatus.Paused, s.Status); // no auto-resume
        rig.C.Cancel();
    }

    [Fact]
    public async Task WaitFor_chat_and_item()
    {
        using var rig = Rig.Js();
        var s = rig.Start("const r = waitFor('chat', {from: 'kiwi', text: 'go'}, 5)\nreturn r.ok + ':' + r.msg");
        await Rig.Until(() => s.Snippet?.Contains("waitFor") == true);
        var ev = new ScriptEvent(ScriptEventKind.Chat, "ok go now", "kiwi");
        Assert.True(s.HasHandler(ev));
        s.Deliver(ev);
        await s.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("true:ok go now", s.Outcome);
        var t = await rig.Run("return waitFor('item', {item: 'cobblestone', n: 99}, 0.3).msg", "w2");
        Assert.Contains("timeout", t.Outcome);
    }
}

public class JsCheckpointTests
{
    [Fact]
    public async Task Replace_hands_over_carry()
    {
        using var rig = Rig.Js();
        var s1 = rig.Start("while (true) { carry.n = (carry.n || 0) + 1; wait(0.05) }", "loop");
        await Rig.Until(() => s1.Actions >= 3);
        var (s2, err) = await rig.C.ReplaceAsync(new ScriptRequest { Name = "loop2", Code = "return 'n=' + carry.n", Language = ScriptLanguage.Js });
        Assert.Null(err);
        await s2!.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("replaced", s1.Reason);
        Assert.True(int.Parse(System.Text.RegularExpressions.Regex.Match(s2.Outcome!, @"n=(\d+)").Groups[1].Value) >= 3, s2.Outcome);
    }

    [Fact]
    public async Task Resume_from_a_checkpoint_skips_finished_stages_and_restores_carry()
    {
        using var rig = Rig.Js();
        bool broken = true;
        rig.On["goto"] = (_, _) => Task.FromResult(broken ? ActResult.Fail("no route") : ActResult.Success("arrived"));
        const string code = """
            cp('chop', () => { mine('log', 4); carry.logs = 4 })
            cp('store', () => { if (!go('chest').ok) fail(); deposit() })
            return 'stored ' + carry.logs
            """;
        var r1 = await rig.Run(code, "job");
        Assert.StartsWith("failed: script job at line 2 (goto: no route): cp 2/2", r1.Outcome);
        broken = false;
        var r2 = await rig.Run(code, "job", resumeFrom: "store");
        Assert.Contains("stored 4", r2.Outcome);
        Assert.Single(rig.CallList, c => c.StartsWith("mine"));
        var r3 = await rig.Run("mine('dirt', 1)\ncp('here')\ngo('x')\nreturn 'ok'", "marker", resumeFrom: "here");
        Assert.Equal(ScriptStatus.Done, r3.Status);
        Assert.DoesNotContain(rig.CallList, c => c.Contains("dirt"));
    }

    [Fact]
    public async Task Checkpoints_survive_a_restart_through_the_durable_store()
    {
        var store = new MemoryCheckpoints();
        const string code = "carry.logs = 7\ncp('store')\nreturn 'stored ' + carry.logs";
        using (var rig = Rig.Js(checkpoints: store)) await rig.Run(code, "job");
        Assert.Contains(("job", "maid_chan", "store"), store.Keys); // keyed by this process's account (or the lane's body)
        using var fresh = Rig.Js(checkpoints: store); // a new process: nothing in memory
        var s = await fresh.Run("cp('store')\nreturn 'stored ' + carry.logs", "job", resumeFrom: "store");
        Assert.Contains("stored 7", s.Outcome);
    }

    [Fact]
    public async Task Args_params_and_telemetry()
    {
        using var rig = Rig.Js();
        var s = await rig.Run("mine('cobblestone', args.n)\nreturn args.n * 2", args: """{"n":7}""");
        Assert.Contains("(14,", s.Outcome);
        await rig.Run("mine('dirt', 1); mine('dirt', 1); wait(0.1)", "tele");
        var rec = rig.C.Runs.Last();
        Assert.Equal(3, rec.Actions);
        Assert.Equal("js", rec.Language);
        Assert.Contains("mine 2x", rec.Timings);
    }

    [Fact]
    public async Task Each_action_runs_inside_the_lanes_body_scope()
    {
        using var rig = Rig.Js();
        var scope = new AsyncLocal<string?>();
        int pushes = 0;
        rig.C.StepScope = () => { Interlocked.Increment(ref pushes); var prev = scope.Value; scope.Value = "67"; return new Restore(() => scope.Value = prev); };
        var seen = new ConcurrentQueue<string?>();
        rig.On["mine"] = (_, _) => { seen.Enqueue(scope.Value); return Task.FromResult(ActResult.Success("mined")); };
        await rig.Run("mine('dirt', 1); mine('dirt', 1)");
        Assert.Equal(["67", "67"], seen);
        Assert.True(pushes >= 3); // the thread + each action
    }

    private sealed class Restore(Action a) : IDisposable { public void Dispose() => a(); }
}

/// <summary>An in-memory durable checkpoint store (what the Bot layer keeps in the routine row).</summary>
public sealed class MemoryCheckpoints : IScriptCheckpointStore
{
    private readonly ConcurrentDictionary<(string, string, string), string> _d = new();
    public IEnumerable<(string, string, string)> Keys => _d.Keys;
    public void Save(string script, string? lane, string label, string snapshot) => _d[(script, lane ?? "main", label)] = snapshot;
    public string? Load(string script, string? lane, string label) => _d.TryGetValue((script, lane ?? "main", label), out var v) ? v : null;
}
