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
    public int Brain => Requests.Count(r => r.Tier == ModelTier.Main);

    public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct)
    {
        Requests.Enqueue(request);
        return Task.FromResult(new LlmResponse([new LlmText("")], StopReasons.EndTurn, LlmUsage.Zero, request.Model ?? "claude-opus-5-5"));
    }

    public string LastBrainText() => Requests.LastOrDefault(r => r.Tier == ModelTier.Main) is { } r
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
    public void Script_tool_and_cheat_sheet_stay_inside_their_budgets()
    {
        var (h, _, _) = Bot();
        var tools = h.Runtime.Tools;
        var brain = tools.Definitions(ModelTier.Main);
        var worker = tools.Definitions(ModelTier.Worker);
        var def = Assert.Single(brain, d => d.Name == "script");
        Assert.Contains(worker, d => d.Name == "script");
        int tool = Tokens(def.Name, def.Description, def.InputSchema);
        int pointer = (int)Math.Ceiling(ScriptPrompts.BrainPointer.Length / 3.5);
        int sheet = (int)Math.Ceiling(ScriptPrompts.CheatSheet.Length / 3.5);
        Assert.True(tool <= 150, $"script tool ≈{tool} tok");
        Assert.True(pointer <= 60, $"brain pointer ≈{pointer} tok");
        Assert.True(sheet <= 250, $"cheat-sheet ≈{sheet} tok");
        Assert.Contains("SHORT scripts", h.Runtime.Worker!.SystemPrompt[0].Text);
        Assert.DoesNotContain("# script tool", h.Runtime.Agent.SystemPrompt[0].Text);
        int bt = brain.Sum(d => Tokens(d.Name, d.Description, d.InputSchema));
        Assert.True(bt + 75 <= 2300, $"brain tools ≈{bt} + hive 75"); // same cap as BrainSplitTests
    }

    [Fact]
    public async Task Brain_run_goes_through_the_gated_game_tools()
    {
        var (h, c, b) = Bot();
        var r = await Script(h, new { op = "run", name = "dig_some", code = "mine{'cobblestone', 4}\nlocal ok = deposit{}\nreturn ok and 'stored' or 'no chest'" });
        Assert.True(r.Ok, r.Text);
        Assert.StartsWith("ok: script dig_some done (no chest", r.Text);
        Assert.Contains("mine cobblestone 4 16", c.CallList);
        Assert.Equal("awake", b.BrainState());
        Assert.Contains("no script running; last: ok: script dig_some", (await Script(h, new { op = "status" })).Text);
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
            code = "local a, m1 = deposit{at = '3,64,3'} local b, m2 = dig{'1,64,1'}\nreturn tostring(a) .. tostring(b) .. (m1:find('busy') and 'B' or '') .. (m2:find('busy') and 'B' or '')",
        });
        Assert.Contains("falsefalseBB", r.Text);
        Assert.DoesNotContain(c.CallList, x => x.StartsWith("deposit") || x.StartsWith("dig"));
    }

    [Fact]
    public async Task No_commands_reach_the_server_through_the_real_chat_path()
    {
        var (h, c, _) = Bot();
        var r = await Script(h, new { op = "run", code = "say'/op maid_chan' emote'waves' say(string.rep('a', 95) .. ' /kill @a')" });
        Assert.True(r.Ok, r.Text);
        await Rig.Until(() => c.Said.Count >= 3);
        Assert.Contains("/me waves", c.Said);
        Assert.DoesNotContain(c.Said, l => l.TrimStart().StartsWith('/') && !l.StartsWith("/me "));
        Assert.DoesNotContain(c.Said, l => l.Contains("/kill") || l.Contains("/op"));
    }

    [Fact]
    public async Task Danger_pauses_an_inline_run_and_the_call_returns_the_state()
    {
        var (h, _, b) = Bot();
        var call = Script(h, new { op = "run", name = "loop", code = "while true do wait(0.05) end" });
        await Rig.Until(() => b.Controller.Current is { Actions: > 2 });
        h.Runtime.Events.Post(GameEvent.LowHealth(5));
        var r = await call.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.StartsWith("paused: script loop paused (danger:", r.Text);
        Assert.Contains("L1 `while true do wait(0.05) end`", r.Text);
        Assert.Contains("awake (script loop paused)", b.BrainState());
        var c = await Script(h, new { op = "cancel" });
        Assert.StartsWith("cancelled: script loop", c.Text);
    }

    [Fact]
    public async Task Detached_pause_wakes_the_brain_with_the_state_and_stop_ends_it()
    {
        var (h, _, b) = Bot(o => o.InlineToolWaitSeconds = 1);
        var r = await Script(h, new { op = "run", name = "loop", code = "while true do wait(0.05) end" });
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
        await Script(h, new { op = "run", name = "short", code = "wait(1.5) return 'fin'" });
        await Rig.Until(() => h.Runtime.Events.Snapshot().Any(e => e.Kind == EventKind.TaskCompleted && e.Text.Contains("ok: script short done (fin")), 8000);

        await Script(h, new { op = "run", name = "long", code = "while true do wait(0.05) end" });
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
        const string v1 = "mine{'cobblestone', 2} return 'ok'";
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

        await Script(h, new { op = "run", name = "cobble_loop", code = "mine{'cobblestone', 3}", save = true });
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
        Assert.StartsWith("-- migrated from routine dirt2", r.Script.Code);

        await h.Run("save_routine", new { name = "peek", steps = new[] { new { tool = "look", args = new { } } } });
        var legacy = await h.Run("run_routine", new { name = "peek" });
        Assert.True(legacy.Ok, legacy.Text);
        Assert.Null(h.Runtime.Memory.GetRoutine("peek")!.Script);

        // goto (a Lua keyword) becomes go{...}.
        var code = ScriptLibrary.FromSteps(new Agent.Memory.Routine
        {
            Name = "x", Steps = [new Agent.Memory.RoutineStep { Tool = "goto", Args = TestBot.J(new { to = "home" }) }],
        }, _ => true)!;
        Assert.Contains("ok, m = go{[\"to\"]=\"home\"} if not ok then fail() end", code);
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
            var r = await h.Run("script", new { op = "run", name = "cobble_loop", code = "for i = 1, 30 do wait(60) prog(i * 100 / 30) end return 'done'" });
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
            await h.Run("script", new { op = "run", name = "loop", code = "for i = 1, 30 do wait(60) end" });
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
            await h.Run("script", new { op = "run", name = "trees", code = "wait(60)\nif inv'sapling' == 0 then wake'no saplings left' end\nfor i = 1, 30 do wait(60) end" });
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
            await h.Run("script", new { op = "run", name = "guarded", code = "on('danger', function() eat{} end)\nfor i = 1, 30 do wait(60) end" });
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
            await h.Run("script", new { op = "run", name = "plain", code = "for i = 1, 30 do wait(60) end" });
            await Task.Delay(200);
            h.Runtime.Events.Post(GameEvent.LowHealth(5));
            await Rig.Until(() => b.Controller.Current?.Status == ScriptStatus.Paused, 5000);
            await Rig.Until(() => llm.Brain >= 1, 5000);
            Assert.StartsWith("awake", b.BrainState());
        }
        finally { b.Controller.Cancel(); cts.Cancel(); }
    }
}
