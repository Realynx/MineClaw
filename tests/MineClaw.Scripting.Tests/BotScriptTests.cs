using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using MineClaw.Agent;
using MineClaw.Agent.Events;
using MineClaw.Agent.Llm;
using MineClaw.Agent.Routines;
using MineClaw.Bot;
using MineClaw.Bot.GameTools;
using MineClaw.Bot.Tests;
using MineClaw.Scripting;
using MineClaw.World;
using MineClaw.World.Claims;

namespace MineClaw.Scripting.Tests;

/// <summary>Records every model call (offline); brain turns end immediately with no text.</summary>
public sealed class CountingLlm : ILlmClient
{
    public ConcurrentQueue<LlmRequest> Requests { get; } = new();
    /// <summary>Brain turns (the interrupt patch call is Opus too, but not a brain turn).</summary>
    public int Brain => Requests.Count(r => r.Tier == ModelTier.Main && r.Purpose != "patch");
    public int Patches => Requests.Count(r => r.Purpose == "patch");

    public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct)
    {
        Requests.Enqueue(request);
        return Task.FromResult(new LlmResponse([new LlmText("")], StopReasons.EndTurn, LlmUsage.Zero, request.Model ?? "claude-opus-5-5"));
    }

    public string LastBrainText() => Requests.LastOrDefault(r => r.Tier == ModelTier.Main && r.Purpose != "patch") is { } r
        ? string.Join("\n", r.Messages.Last().Content.OfType<LlmText>().Select(t => t.Text)) : "";
}

/// <summary>Fast-forward clock: <paramref name="factor"/> fake seconds per real second (timers and delays scaled too).</summary>
public sealed class ScaledTime(double factor) : TimeProvider
{
    private readonly DateTimeOffset _start = DateTimeOffset.UtcNow;
    private readonly Stopwatch _sw = Stopwatch.StartNew();

    public override DateTimeOffset GetUtcNow() => _start + TimeSpan.FromTicks((long)(_sw.Elapsed.Ticks * factor));

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
        new Scaled(System.CreateTimer(callback, state, S(dueTime, factor), S(period, factor)), factor);

    private static TimeSpan S(TimeSpan t, double f) => t == Timeout.InfiniteTimeSpan ? t : TimeSpan.FromTicks(Math.Max(1, (long)(t.Ticks / f)));

    private sealed class Scaled(ITimer inner, double f) : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => inner.Change(S(dueTime, f), S(period, f));
        public void Dispose() => inner.Dispose();
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}

public class BotScriptTests
{
    private static int Tokens(string name, string desc, JsonElement schema) => (int)Math.Ceiling((name.Length + desc.Length + schema.GetRawText().Length) / 3.5) + 6;

    internal static (BotHost H, FakeGameClient C, ScriptBridge B) Bot(Action<AgentOptions>? configure = null, ScriptOptions? scripts = null,
        ILlmClient? llm = null, TimeProvider? time = null, IClaimBoard? claims = null)
    {
        var c = new FakeGameClient();
        var o = TestBot.Options();
        configure?.Invoke(o);
        var h = BotHost.Create(c, o, new BotOptions { Scripts = scripts ?? new ScriptOptions() }, llm ?? new NoLlm(), time: time, persist: false, claims: claims);
        return (h, c, ScriptTools.Of(h.Runtime)!);
    }

    private static Task<Agent.Tools.ToolResult> Script(BotHost h, object input) => h.Run("script", input);

    [Fact]
    public void Script_tool_and_author_line_stay_inside_their_budgets()
    {
        var (h, _, _) = Bot();
        var tools = h.Runtime.Tools;
        var brain = tools.Definitions(ModelTier.Main);
        var worker = tools.Definitions(ModelTier.Worker);
        var def = Assert.Single(brain, d => d.Name == "script");
        Assert.Contains(worker, d => d.Name == "script");
        int tool = Tokens(def.Name, def.Description, def.InputSchema);
        int pointer = (int)Math.Ceiling(ScriptPrompts.BrainPointer.Length / 3.5);
        int line = (int)Math.Ceiling(ScriptPrompts.AuthorLine.Length / 3.5);
        // Tool: op/name/goal/params only (code stays accepted input). The brain never writes code: its cached prefix holds one
        // author line (the SDK reference lives in the separate author's context); the worker keeps one ≤20-token line.
        Assert.True(tool <= 170, $"script tool ≈{tool} tok");
        Assert.True(pointer <= 60, $"brain pointer ≈{pointer} tok");
        Assert.True(line <= 40, $"author line ≈{line} tok");
        var brainPrompt = h.Runtime.Agent.SystemPrompt[0].Text;
        Assert.Contains(ScriptPrompts.AuthorLine.Trim(), brainPrompt);
        Assert.DoesNotContain("# SDK reference", brainPrompt);
        Assert.DoesNotContain(ScriptPrompts.AuthorLine.Trim(), h.Runtime.Worker!.SystemPrompt[0].Text);
        Assert.Contains(ScriptPrompts.WorkerLine.Trim(), h.Runtime.Worker!.SystemPrompt[0].Text);
        int bt = brain.Sum(d => Tokens(d.Name, d.Description, d.InputSchema));
        Assert.True(bt + Agent.Tools.ToolBudgets.HiveTool <= Agent.Tools.ToolBudgets.Brain, $"brain tools ≈{bt} + hive"); // the one shared cap
    }

    [Fact]
    public async Task Brain_run_goes_through_the_gated_game_tools()
    {
        var (h, c, b) = Bot();
        var r = await Script(h, new { op = "run", name = "dig_some", code = "mine('cobblestone', 4)\nconst r = deposit()\nreturn r.ok ? 'stored' : 'no chest'" });
        Assert.True(r.Ok, r.Text);
        Assert.StartsWith("ok: script dig_some done (no chest", r.Text);
        Assert.Contains("mine cobblestone 4 16", c.CallList);
        Assert.Equal("awake", b.BrainState());
        Assert.Contains("no script running; last: ok: script dig_some", (await Script(h, new { op = "status" })).Text);
    }

    [Fact]
    public async Task Inline_lua_through_the_tool_is_refused()
    {
        var (h, c, b) = Bot();
        var r = await Script(h, new { op = "run", name = "old_way", code = "local ok, m = mine{'cobblestone', 4}\nif not ok then fail(m) end" });
        Assert.False(r.Ok);
        Assert.Equal(ScriptPrompts.JsOnly, r.Text);
        Assert.DoesNotContain(c.CallList, x => x.StartsWith("mine"));
        Assert.Null(b.Library.Get("old_way"));
        var save = await Script(h, new { op = "save", name = "old_way", code = "local ok = deposit{}\nreturn ok and 'y' or 'n'" });
        Assert.False(save.Ok);
        Assert.Equal(ScriptPrompts.JsOnly, save.Text);
    }

    [Fact]
    public async Task A_saved_legacy_lua_version_still_runs_while_LegacyLua_is_on()
    {
        var (h, c, b) = Bot(scripts: new ScriptOptions { LegacyLua = true });
        b.Library.Save("old_dig", "local ok, m = mine{'cobblestone', 2}\nreturn ok and 'dug' or m", lang: null);
        Assert.True(b.Library.Get("old_dig")!.Script!.Versions[^1].IsLua);
        var r = await Script(h, new { name = "old_dig" });
        Assert.True(r.Ok, r.Text);
        Assert.StartsWith("ok: script old_dig done (dug", r.Text);
        Assert.Contains("mine cobblestone 2 16", c.CallList);

        var (h2, _, b2) = Bot(scripts: new ScriptOptions { LegacyLua = false });
        b2.Library.Save("old_dig", "local ok, m = mine{'cobblestone', 2}\nreturn ok and 'dug' or m", lang: null);
        var off = await Script(h2, new { name = "old_dig" });
        Assert.False(off.Ok);
        Assert.Contains("legacy Lua", off.Text);
    }

    private sealed class Board : IClaimBoard
    {
        public string Owner => "maid_chan";
        public bool IsFree(ClaimKey key) => key.Kind == ClaimKind.Task;
        public Task<ClaimResult> TryClaimAsync(ClaimKey key, TimeSpan? ttl = null, string? note = null, CancellationToken ct = default) =>
            Task.FromResult(ClaimResult.Refused("maid_chan_67", DateTimeOffset.MaxValue));
        public Task ReleaseAsync(ClaimKey key, CancellationToken ct = default) => Task.CompletedTask;
    }

    [Fact]
    public async Task Claims_are_respected()
    {
        var (h, c, _) = Bot(claims: new Board());
        var r = await Script(h, new
        {
            op = "run",
            code = "const a = deposit({at: '3,64,3'}), b = dig('1,64,1')\nconst busy = r => r.msg.includes('busy') ? 'B' : ''\nreturn String(a.ok) + String(b.ok) + busy(a) + busy(b)",
        });
        Assert.Contains("falsefalseBB", r.Text);
        Assert.DoesNotContain(c.CallList, x => x.StartsWith("deposit") || x.StartsWith("dig"));
    }

    [Fact]
    public async Task No_commands_reach_the_server_through_the_real_chat_path()
    {
        var (h, c, _) = Bot(scripts: new ScriptOptions { MaxSayPerRun = 5, SayMinIntervalSeconds = 0 });
        var r = await Script(h, new { op = "run", code = "say('/op maid_chan')\nemote('waves')\nsay('a'.repeat(95) + ' /kill @a')" });
        Assert.True(r.Ok, r.Text);
        await Rig.Until(() => c.Said.Count >= 3);
        Assert.Contains("/me waves", c.Said);
        Assert.DoesNotContain(c.Said, l => l.TrimStart().StartsWith('/') && !l.StartsWith("/me "));
        Assert.DoesNotContain(c.Said, l => l.Contains("/kill") || l.Contains("/op"));
    }

    [Fact]
    public async Task A_scan_that_says_every_block_sends_at_most_one_line_and_logs_the_rest()
    {
        // Live 10-27: a brain script say-ed each scanned block ("4,64,6 cobblestone", "5,64,6 water"...) into public chat.
        var (h, c, _) = Bot();
        var r = await Script(h, new { op = "run", code =
            "say('checking the gen foxy')\nfor (let x = 4; x <= 6; x++) say(x + ',64,6 cobblestone')\nsay('all done')" });
        Assert.True(r.Ok, r.Text);
        await Task.Delay(300);
        Assert.Equal(["checking the gen foxy"], c.Said);      // 1 per 10 s: the rest went to the log
        Assert.DoesNotContain(c.Said, l => l.Contains("cobblestone"));
    }

    [Fact]
    public async Task A_pure_data_say_is_logged_not_said()
    {
        var (h, c, _) = Bot(scripts: new ScriptOptions { SayMinIntervalSeconds = 0 });
        var r = await Script(h, new { op = "run", code = "say('4,64,6 cobblestone')" });
        Assert.True(r.Ok, r.Text);
        await Task.Delay(200);
        Assert.Empty(c.Said);
    }

    [Fact]
    public async Task Fail_used_to_return_scan_data_says_to_use_return()
    {
        var (h, _, _) = Bot();
        var r = await Script(h, new { op = "run", code = "fail('4,6=cobblestone 5,6=water 6,6=water 4,7=lava')" });
        Assert.False(r.Ok);
        Assert.Contains("fail() is for errors, not data", r.Text);
        var ok = await Script(h, new { op = "run", code = "return '4,6=cobblestone 5,6=water 6,6=water 4,7=lava 5,7=cobblestone 6,7=water'" });
        Assert.True(ok.Ok, ok.Text);
        Assert.Contains("6,7=water", ok.Text);                // return carries the data back (up to 200 chars)
    }

    [Fact]
    public async Task Danger_pauses_an_inline_run_and_the_call_returns_the_state()
    {
        var (h, _, b) = Bot();
        var call = Script(h, new { op = "run", name = "loop", code = "while (true) { wait(0.05) }" });
        await Rig.Until(() => b.Controller.Current is { Actions: > 2 });
        h.Runtime.Events.Post(GameEvent.LowHealth(5));
        var r = await call.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.StartsWith("paused: script loop paused (danger:", r.Text);
        Assert.Contains("L1 `while (true) { wait(0.05) }`", r.Text);
        Assert.Contains("awake (script loop paused)", b.BrainState());
        var c = await Script(h, new { op = "cancel" });
        Assert.StartsWith("cancelled: script loop", c.Text);
    }

    [Fact]
    public async Task Detached_pause_wakes_the_brain_with_the_state_and_stop_ends_it()
    {
        var (h, _, b) = Bot(o => o.InlineToolWaitSeconds = 1);
        var r = await Script(h, new { op = "run", name = "loop", code = "while (true) { wait(0.05) }" });
        Assert.Contains("running in background", r.Text);
        h.Runtime.Events.Post(GameEvent.LowHealth(5));
        await Rig.Until(() => h.Runtime.Events.Snapshot().Any(e => e.Text.StartsWith("script paused: script loop paused (danger")), 10_000);
        var notice = h.Runtime.Events.Snapshot().First(e => e.Text.StartsWith("script paused"));
        Assert.True(notice.Wakes);
        Assert.True(notice.Text.Length <= 480);
        await Task.Delay(100);
        Assert.DoesNotContain(h.Runtime.Events.Snapshot(), e => e.Kind == EventKind.TaskFailed && e.Wakes);
        await h.Run("stop", new { });
        await Rig.Until(() => b.Controller.Current == null);
        Assert.Equal(ScriptStatus.Cancelled, b.Controller.Last!.Status);
    }

    [Fact]
    public async Task Background_end_reports_task_ok_and_a_new_activity_supersedes_the_script()
    {
        var (h, _, b) = Bot(o => o.InlineToolWaitSeconds = 1);
        await Script(h, new { op = "run", name = "short", code = "wait(1.5)\nreturn 'fin'" });
        await Rig.Until(() => h.Runtime.Events.Snapshot().Any(e => e.Kind == EventKind.TaskCompleted && e.Text.Contains("ok: script short done (fin")), 8000);

        await Script(h, new { op = "run", name = "long", code = "while (true) { wait(0.05) }" });
        h.Runtime.Tools.TryGet("goto", out var go);
        await h.Runtime.Activities.RunAsync([(go, TestBot.J(new { to = "1,65,1" }))], TimeSpan.FromSeconds(2), CancellationToken.None);
        await Rig.Until(() => b.Controller.Current == null);
        Assert.Contains("superseded by goto", b.Controller.Last!.Reason);
        Assert.DoesNotContain(h.Runtime.Events.Snapshot(), e => e.Kind == EventKind.TaskFailed && e.Wakes);
    }

    [Fact]
    public async Task Saved_scripts_are_versioned_and_promoted_to_skills_after_three_wins()
    {
        var (h, c, b) = Bot();
        const string v1 = "mine('cobblestone', 2)\nreturn 'ok'";
        for (int i = 0; i < 3; i++)
        {
            var r = await Script(h, i == 0 ? new { op = "run", name = "cobble_loop", code = v1, save = true } : (object)new { op = "run", name = "cobble_loop" });
            Assert.True(r.Ok, r.Text);
        }
        var saved = b.Library.Get("cobble_loop")!.Script!;
        Assert.Equal(3, saved.Successes);
        Assert.Equal("cobble_loop", saved.PromotedAs);
        Assert.Contains("cobble_loop(", (await h.Run("find_skill", new { query = "cobble loop" })).Text);
        var viaSkill = await h.Run("skill", new { name = "cobble_loop" });
        Assert.True(viaSkill.Ok, viaSkill.Text);
        Assert.Equal(4, c.CallList.Count(x => x.StartsWith("mine cobblestone 2")));

        await Script(h, new { op = "run", name = "cobble_loop", code = "mine('cobblestone', 3)", save = true });
        saved = b.Library.Get("cobble_loop")!.Script!;
        Assert.Equal(2, saved.Version);
        Assert.Equal(v1, saved.Previous[0]);
        Assert.Contains("script cobble_loop v2", Agent.Memory.MemoryStore.FormatRoutine(b.Library.Get("cobble_loop")!));
    }

    [Fact]
    public async Task Step_routines_migrate_onto_the_engine_and_unscriptable_ones_still_replay()
    {
        var (h, c, _) = Bot();
        var save = await h.Run("save_routine", new { name = "dirt2", steps = new[] { new { tool = "mine", args = new { block = "dirt", count = 2 } } }, repeat = 2, stop_when = "full" });
        Assert.True(save.Ok, save.Text);
        var run = await h.Run("run_routine", new { name = "dirt2" });
        Assert.True(run.Ok, run.Text);
        Assert.Contains("2 round(s)", run.Text);
        Assert.Equal(2, c.CallList.Count(x => x == "mine dirt 2 16"));
        var r = h.Runtime.Memory.GetRoutine("dirt2")!;
        Assert.True(r.Script!.Migrated);
        Assert.StartsWith("// migrated from routine dirt2", r.Script.Code);

        await h.Run("save_routine", new { name = "peek", steps = new[] { new { tool = "look", args = new { } } } });
        var legacy = await h.Run("run_routine", new { name = "peek" });
        Assert.True(legacy.Ok, legacy.Text);
        Assert.Null(h.Runtime.Memory.GetRoutine("peek")!.Script);

        // goto is a plain JS function: each step is a checked call with its saved args object.
        var code = ScriptLibrary.FromSteps(new Agent.Memory.Routine
        {
            Name = "x", Steps = [new Agent.Memory.RoutineStep { Tool = "goto", Args = TestBot.J(new { to = "home" }) }],
        }, _ => true)!;
        Assert.Contains("{ const r = goto({\"to\":\"home\"}); m = r.msg; if (!r.ok) fail() }", code);
    }
}

/// <summary>While a script runs the brain sleeps: zero model calls unless a real event needs it.</summary>
public class BrainSleepTests
{
    private const double Fast = 600; // 1 real ms = 0.6 fake s; 30 fake minutes ≈ 3 real seconds

    private static async Task<(BotHost H, FakeGameClient C, ScriptBridge B, CountingLlm Llm, CancellationTokenSource Cts)> Awake()
    {
        var llm = new CountingLlm();
        var (h, c, b) = BotScriptTests.Bot(o => { o.IdleCheckInMinutes = 20; o.InlineToolWaitSeconds = 20; },
            new ScriptOptions { MaxPauseMinutes = 600, ChatAutoResumeSeconds = 6000 }, llm, new ScaledTime(Fast));
        var cts = new CancellationTokenSource();
        _ = h.Runtime.Agent.RunAsync(cts.Token);
        await Task.Delay(50);
        return (h, c, b, llm, cts);
    }

    [Fact]
    public async Task A_thirty_minute_script_makes_no_model_calls_and_queued_events_arrive_with_the_end()
    {
        var (h, _, b, llm, cts) = await Awake();
        try
        {
            var r = await h.Run("script", new { op = "run", name = "cobble_loop", code = "for (let i = 1; i <= 30; i++) { wait(60); prog(i * 100 / 30) }\nreturn 'done'" });
            Assert.Contains("running in background", r.Text);
            await Rig.Until(() => b.Controller.Current?.ProgressPct > 10);
            Assert.StartsWith("asleep (script cobble_loop", b.BrainState());
            h.Runtime.Events.Post(GameEvent.Activity("robin probably built a cobble gen by the tree", "robin"));
            h.Runtime.Events.Post(GameEvent.Notice("a chest was emptied", EventPriority.Low));
            h.Runtime.Events.Post(GameEvent.Joined("robin"));
            await Rig.Until(() => b.Controller.Current == null, 15_000);
            var elapsed = b.Controller.Last!.Finished!.Value - b.Controller.Last.Started;
            Assert.True(elapsed >= TimeSpan.FromMinutes(29), $"ran {elapsed}");
            Assert.Equal(ScriptStatus.Done, b.Controller.Last.Status);
            await Rig.Until(() => llm.Brain >= 1, 5000);
            await Task.Delay(300);
            Assert.Equal(1, llm.Brain); // the [task ok] wake; nothing during the 30 minutes (idle check-ins included)
            var turn = llm.LastBrainText();
            Assert.Contains("ok: script cobble_loop done", turn);
            Assert.Contains("cobble gen by the tree", turn); // queued while asleep, delivered with the next real wake
            Assert.Contains("chest was emptied", turn);
        }
        finally { cts.Cancel(); }
    }

    [Fact]
    public async Task Addressed_chat_wakes_the_brain_once()
    {
        var (h, c, b, llm, cts) = await Awake();
        try
        {
            await h.Run("script", new { op = "run", name = "loop", code = "for (let i = 1; i <= 30; i++) wait(60)" });
            await Task.Delay(200);
            Assert.Equal(0, llm.Brain);
            c.RaiseChat("<kiwi> claudechan how's it going?");
            await Rig.Until(() => llm.Brain >= 1, 5000);
            await Task.Delay(500);
            Assert.Equal(1, llm.Brain);
            Assert.Contains("how's it going", llm.LastBrainText());
        }
        finally { b.Controller.Cancel(); cts.Cancel(); }
    }

    [Fact]
    public async Task A_wake_call_wakes_the_brain()
    {
        var (h, _, b, llm, cts) = await Awake();
        try
        {
            await h.Run("script", new { op = "run", name = "trees", code = "wait(60)\nif (inv('sapling') === 0) wake('no saplings left')\nfor (let i = 1; i <= 30; i++) wait(60)" });
            await Rig.Until(() => llm.Brain >= 1, 5000);
            await Task.Delay(500);
            Assert.Equal(1, llm.Brain);
            Assert.Contains("script wake: no saplings left", llm.LastBrainText());
            Assert.StartsWith("asleep", b.BrainState()); // the script keeps going
        }
        finally { b.Controller.Cancel(); cts.Cancel(); }
    }

    [Fact]
    public async Task A_handled_danger_does_not_wake_the_brain()
    {
        var (h, c, b, llm, cts) = await Awake();
        try
        {
            await h.Run("script", new { op = "run", name = "guarded", code = "on('danger', () => { eat() })\nfor (let i = 1; i <= 30; i++) wait(60)" });
            await Task.Delay(200);
            h.Runtime.Events.Post(GameEvent.LowHealth(5));
            await Rig.Until(() => c.CallList.Contains("eat"), 5000);
            await Task.Delay(500);
            Assert.Equal(0, llm.Brain);
            Assert.Equal(ScriptStatus.Running, b.Controller.Current!.Status);
        }
        finally { b.Controller.Cancel(); cts.Cancel(); }
    }

    [Fact]
    public async Task Unhandled_danger_wakes_the_brain_and_pauses_the_script()
    {
        var (h, _, b, llm, cts) = await Awake();
        try
        {
            await h.Run("script", new { op = "run", name = "plain", code = "for (let i = 1; i <= 30; i++) wait(60)" });
            await Task.Delay(200);
            h.Runtime.Events.Post(GameEvent.LowHealth(5));
            await Rig.Until(() => b.Controller.Current?.Status == ScriptStatus.Paused, 5000);
            await Rig.Until(() => llm.Brain >= 1, 5000);
            Assert.StartsWith("awake", b.BrainState());
        }
        finally { b.Controller.Cancel(); cts.Cancel(); }
    }

    // ------------------------------------------------------------------ live 2026-10-27 (task #5): a sleeping brain never misses a failure

    [Fact]
    public async Task A_script_failure_wakes_the_sleeping_brain_with_the_reason()
    {
        var (h, _, b, llm, cts) = await Awake();
        try
        {
            await h.Run("script", new { op = "run", name = "boom", code = "wait(60)\nthrow new Error('no cobble left at the gen')" });
            await Rig.Until(() => llm.Brain >= 1, 5000);
            Assert.Contains("no cobble left at the gen", llm.LastBrainText());
        }
        finally { b.Controller.Cancel(); cts.Cancel(); }
    }

    [Fact]
    public async Task A_script_limit_wakes_the_sleeping_brain_with_the_reason()
    {
        var (h, _, b, llm, cts) = await Awake();
        try
        {
            var r = await h.Run("script", new { op = "run", name = "spin", code = "wait(60)\nlet n = 0\nfor (let i = 1; i <= 100000000; i++) n = n + 1" });
            try { await Rig.Until(() => llm.Brain >= 1, 8000); }
            catch (TimeoutException) { Assert.Fail(r.Text + " | " + b.Controller.Last?.Status + " " + b.Controller.Last?.Outcome + " | cur " + b.Controller.Current?.State()); }
            Assert.Contains("spin", llm.LastBrainText());
            Assert.Contains("[task failed] #1 script: failed: script spin", llm.LastBrainText());
            Assert.Contains("busy loop", llm.LastBrainText());
        }
        finally { b.Controller.Cancel(); cts.Cancel(); }
    }

    [Fact]
    public async Task A_must_wake_notice_reaches_the_sleeping_brain_but_a_plain_notice_does_not()
    {
        var (h, _, b, llm, cts) = await Awake();
        try
        {
            await h.Run("script", new { op = "run", name = "loop", code = "for (let i = 1; i <= 30; i++) wait(60)" });
            await Task.Delay(200);
            h.Runtime.Events.Post(GameEvent.Notice("a chest was emptied", EventPriority.Low));
            await Task.Delay(400);
            Assert.Equal(0, llm.Brain);
            h.Runtime.Events.Post(GameEvent.Notice("[hive] 67 failed: chest 14,64,-5 unreachable", EventPriority.Normal) with { MustWake = true });
            await Rig.Until(() => llm.Brain >= 1, 5000);
            Assert.Contains("67 failed", llm.LastBrainText());
            Assert.NotNull(b.Controller.Current); // woke the brain, the body keeps its script
        }
        finally { b.Controller.Cancel(); cts.Cancel(); }
    }
}
