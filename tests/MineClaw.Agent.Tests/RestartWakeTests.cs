using System.Diagnostics;
using System.Text.Json;
using MineClaw.Agent.Events;
using MineClaw.Agent.Human;
using MineClaw.Agent.Llm;
using MineClaw.Agent.Memory;
using MineClaw.Agent.Tools;

namespace MineClaw.Agent.Tests;

/// <summary>
/// Live 10-28 07:37-08:45: after five restarts main had no brain turn for 70 minutes. Its standing order's auto-resume had
/// paused itself in the db at 03:48 (three "already running" answers from the script lane counted as failures), the paused
/// build was only a Background notice, and nothing else woke it. Now: a restart resumes the order, the brain gets one startup
/// wake, and an idle watchdog wakes a stuck main (backing off on "nothing to do").
/// </summary>
public class RestartWakeTests
{
    private static JsonElement J(object o) => JsonSerializer.SerializeToElement(o);

    private static async Task WaitFor(Func<bool> cond, int timeoutMs = 10000)
    {
        var sw = Stopwatch.StartNew();
        while (!cond())
        {
            if (sw.ElapsedMilliseconds > timeoutMs) throw new TimeoutException("condition not met");
            await Task.Delay(10);
        }
    }

    private static (DelegateTool Tool, TaskCompletionSource Release, Func<int> Runs) Job(string name)
    {
        int runs = 0;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tool = new DelegateTool(name, "Mine the gen.", ToolSchema.Empty, async (_, ct) =>
        {
            Interlocked.Increment(ref runs);
            await release.Task.WaitAsync(ct);
            return ToolResult.Success("mined 64");
        }, longRunning: true);
        return (tool, release, () => Volatile.Read(ref runs));
    }

    private static FakeLlm Silent() => new() { Fallback = _ => new LlmResponse([new LlmText("")], StopReasons.EndTurn, LlmUsage.Zero, "claude-opus-5-5") };

    private static AgentRuntime Persistent(string dir, FakeLlm llm, Action<AgentOptions>? more = null)
    {
        var o = new AgentOptions { KnowledgeDirectory = null, IdleCheckInMinutes = 0, DataDirectory = dir };
        o.Chat.Username = "maid_chan";
        o.Chat.DisplayName = "maid_chan";
        o.Chat.Aliases = [];
        o.Chat.LineDelayMs = 0;
        o.Self.Enabled = false;
        o.Rag.SeedFile = "";
        o.Idle.ResumeOnStartSeconds = 0;
        o.ChatDebounce.QuietMs = 10;
        o.ChatDebounce.MaxMs = 50;
        more?.Invoke(o);
        return AgentRuntime.Create(o, (_, _) => Task.CompletedTask, llm, persist: true);
    }

    private static string UserText(LlmRequest r) => string.Concat(r.Messages[^1].Content.OfType<LlmText>().Select(t => t.Text));

    private static int Wakes(FakeLlm llm, string tag) => llm.Requests.Count(r => UserText(r).Contains(tag + ":", StringComparison.Ordinal));

    [Fact]
    public async Task A_restart_resumes_a_stored_order_whose_old_process_paused_its_auto_resume()
    {
        using var dir = new TempDir();
        var llm = Silent();
        // Before the restart (the alice db at 08:45): the order's auto-resume paused after three "already running" answers.
        using (var before = Persistent(dir.Path, llm))
        {
            before.Memory.SetStandingOrder(new StandingOrder { Bot = "maid_chan", Player = "PoofimaFox", Job = "mine the cobble gen for Fox", Tool = "mine_gen", Input = J(new { }) });
            before.Memory.UpdateStandingOrder("maid_chan", o =>
            {
                o.FailuresInRow = 3;
                o.LastOutcome = "already running: script gen_loop running L5";
                o.LastResumed = DateTimeOffset.UtcNow; // inside MinResumeIntervalSeconds: the start resume ignores it
            });
        }

        using var rt = Persistent(dir.Path, llm);
        var (job, release, runs) = Job("mine_gen");
        rt.Tools.Register(job);
        using var cts = new CancellationTokenSource();
        var sw = Stopwatch.StartNew();
        var loop = rt.Agent.RunAsync(cts.Token);
        await WaitFor(() => runs() == 1);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(rt.Options.Idle.CheckSeconds), $"resumed after {sw.Elapsed}");
        var order = rt.Memory.GetStandingOrder("maid_chan")!;
        Assert.Equal(0, order.FailuresInRow);
        Assert.Equal(1, order.Resumes);

        // The brain hears it once: what resumed and why it had stopped.
        await WaitFor(() => Wakes(llm, BotAgent.StartupTag) == 1);
        var wake = UserText(llm.Requests.First(r => UserText(r).Contains("restarted:")));
        Assert.Contains("standing order \"mine the cobble gen for Fox\" (PoofimaFox): resumed", wake);
        Assert.Contains("auto-resume had paused after 3 failures (last: already running", wake);
        Assert.Equal(1, runs()); // the turn didn't start it twice

        cts.Cancel();
        release.SetResult();
        try { await loop; } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task Exactly_one_startup_wake_per_process_carrying_the_order_the_paused_build_and_the_rules()
    {
        using var dir = new TempDir();
        var llm = Silent();
        using var rt = Persistent(dir.Path, llm);
        rt.Memory.SetStandingOrder(new StandingOrder { Bot = "maid_chan", Player = "PoofimaFox", Job = "mine the cobble gen for Fox", Tool = "mine_gen", Input = J(new { }) });
        var (job, release, runs) = Job("mine_gen");
        rt.Tools.Register(job);
        // The blueprint layer's paused build (NotifyPending's line, with its design rule and the time of day).
        rt.Agent.AddPendingWork(() => "the mob drop tower build (#4, 20/14 placed) was paused by a restart; resume it with design build{id=4} " +
                                      "when it fits your rules; rule#6 (Fox, design #4, 3h ago): only in daytime. At night, mine cobble instead; it's night now");
        rt.Agent.StandingRules = text => text.Contains("design #4") ? "[standing rules: rule#6 (Fox, design #4, 3h ago): only in daytime | rule#2 (Fox): store cobble in the gen chest]" : null;

        using var cts = new CancellationTokenSource();
        var loop = rt.Agent.RunAsync(cts.Token);
        await WaitFor(() => runs() == 1 && Wakes(llm, BotAgent.StartupTag) == 1);
        var wake = UserText(llm.Requests.First(r => UserText(r).Contains("restarted:")));
        Assert.Contains("standing order \"mine the cobble gen for Fox\" (PoofimaFox): resumed", wake);
        Assert.Contains("the mob drop tower build (#4, 20/14 placed) was paused by a restart", wake);
        Assert.Contains("it's night now", wake);
        Assert.Contains("rules: rule#2 (Fox): store cobble", wake);                 // a rule the build line didn't quote
        var line = wake.Split('\n').Single(l => l.Contains("restarted:"));
        Assert.Equal(1, line.Split("rule#6").Length - 1);                           // quoted once, not twice

        // The loop paused and resumed (PausableAgent): the same process, no second wake.
        cts.Cancel();
        try { await loop; } catch (OperationCanceledException) { }
        using var cts2 = new CancellationTokenSource();
        var loop2 = rt.Agent.RunAsync(cts2.Token);
        await Task.Delay(300);
        Assert.Equal(1, rt.Agent.StartupWakes);
        Assert.Equal(1, Wakes(llm, BotAgent.StartupTag));
        cts2.Cancel();
        release.SetResult();
        try { await loop2; } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task No_startup_wake_with_nothing_to_tell_or_in_a_hive_clone()
    {
        using var dir = new TempDir();
        var llm = Silent();
        using (var idle = Persistent(dir.Path, llm))
        {
            using var cts = new CancellationTokenSource();
            var loop = idle.Agent.RunAsync(cts.Token);
            await Task.Delay(300);
            Assert.Equal(0, idle.Agent.StartupWakes);
            cts.Cancel();
            try { await loop; } catch (OperationCanceledException) { }
        }

        using var clone = Persistent(dir.Path, llm, o => o.Chat.Username = "maid_chan_67");
        clone.Memory.SetStandingOrder(new StandingOrder { Bot = "maid_chan_67", Player = "hive", Job = "script hive_1", Tool = "mine_gen", Input = J(new { }) });
        var (job, release, runs) = Job("mine_gen");
        clone.Tools.Register(job);
        clone.Agent.WakeOnStart = false; // ClonePolicy.Apply
        using var cts3 = new CancellationTokenSource();
        var loop3 = clone.Agent.RunAsync(cts3.Token);
        await WaitFor(() => runs() == 1);
        await Task.Delay(200);
        Assert.Equal(0, clone.Agent.StartupWakes);
        Assert.Empty(llm.Requests);
        cts3.Cancel();
        release.SetResult();
        try { await loop3; } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task Already_running_is_not_a_failed_resume_and_a_live_script_holds_the_order()
    {
        var (rt, _, _) = TestRuntime.Create(o => o.Idle.MinResumeIntervalSeconds = 0);
        int calls = 0;
        rt.Tools.Register(new DelegateTool("script", "Script.", ToolSchema.Empty, (_, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(ToolResult.Error("already running: script gen_loop running L5 `local ok,m=harvest_generator{...}` | cp 'mine' 1/1"));
        }, longRunning: true));
        rt.Memory.SetStandingOrder(new StandingOrder { Bot = "Clawd_bot", Job = "mine the cobble gen for Fox", Tool = "script", Input = J(new { op = "run", name = "gen_loop" }) });
        var orders = rt.Agent.Orders!;
        for (int i = 0; i < 4; i++)
        {
            Assert.True(orders.ResumeIfIdle("idle"));
            await WaitFor(() => Volatile.Read(ref calls) == i + 1 && !rt.Activities.IsBusy);
            await Task.Delay(30);
            rt.Events.TryTakeBatch();
        }
        Assert.Equal(0, rt.Memory.GetStandingOrder("Clawd_bot")!.FailuresInRow); // live: 3 → paused for good
        Assert.Null(orders.WhyNot());

        // The script lane holds the body: no resume at all (nothing to answer "already running").
        orders.BodyBusy = () => true;
        Assert.False(orders.ResumeIfIdle("idle"));
        Assert.Equal(4, Volatile.Read(ref calls));
    }

    [Fact]
    public async Task An_order_that_cant_run_as_stored_wakes_the_brain_once_with_why()
    {
        var (rt, _, _) = TestRuntime.Create(o => o.Idle.MinResumeIntervalSeconds = 0);
        var (job, release, runs) = Job("script");
        rt.Tools.Register(job);
        rt.Memory.SetStandingOrder(new StandingOrder { Bot = "Clawd_bot", Job = "mine the cobble gen", Tool = "script", Input = J(new { op = "run", name = "gen_loop" }) });
        var orders = rt.Agent.Orders!;
        orders.Prepare = _ => OrderCheck.Invalid("gen_loop v1 is legacy Lua and wasn't migrated to JS");
        Assert.False(orders.ResumeIfIdle("start"));
        var e = Assert.Single(rt.Events.Snapshot());
        Assert.Equal(EventPriority.Normal, e.Priority);
        Assert.True(e.Wakes && e.MustWake);
        Assert.Contains("standing order \"mine the cobble gen\" can't resume: gen_loop v1 is legacy Lua", e.Text);
        rt.Events.TryTakeBatch();
        Assert.False(orders.ResumeIfIdle("idle"));
        Assert.Equal(0, rt.Events.Count);                      // once, not every idle tick
        Assert.StartsWith("can't resume:", orders.WhyNot());
        Assert.Equal(0, runs());

        // A rewritten call (newest version) is persisted and runs.
        orders.Prepare = o => OrderCheck.Rewrite(J(new { op = "run", name = "gen_loop" }), "gen_loop@v1 re-resolved");
        Assert.True(orders.ResumeIfIdle("idle"));
        await WaitFor(() => runs() == 1);
        Assert.Null(orders.WhyNot());
        release.SetResult();
    }

    [Fact]
    public async Task The_idle_watchdog_wakes_a_stuck_main_once_then_backs_off()
    {
        var time = new ManualTime();
        var (rt, _, _) = TestRuntime.Create(o => o.Idle.MinResumeIntervalSeconds = 0, time: time);
        rt.Chat.SetOnlinePlayers([]); // night, nobody on
        var (job, release, _) = Job("mine_gen");
        rt.Tools.Register(job);
        rt.Memory.SetStandingOrder(new StandingOrder { Bot = "Clawd_bot", Player = "PoofimaFox", Job = "mine the cobble gen for Fox", Tool = "mine_gen", Input = J(new { }) });
        rt.Memory.UpdateStandingOrder("Clawd_bot", o => { o.FailuresInRow = 3; o.LastOutcome = "already running"; });
        Assert.Equal(10, rt.Options.Idle.WatchdogMinutes);

        time.Advance(TimeSpan.FromMinutes(9));
        Assert.Null(rt.Agent.IdleTick());
        time.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal("watchdog", rt.Agent.IdleTick());
        var e = Assert.Single(rt.Events.Snapshot());
        Assert.Equal(EventPriority.Normal, e.Priority);
        Assert.True(e.Wakes);
        Assert.Contains("idle watchdog: idle 11m, nothing running: standing order \"mine the cobble gen for Fox\" (PoofimaFox): not running " +
                        "(auto-resume paused after 3 failures (last: already running))", e.Text);
        rt.Events.TryTakeBatch();
        Assert.Null(rt.Agent.IdleTick()); // once

        // The brain answered "nothing to do" (still idle): the next one waits 20 min, then 40.
        time.Advance(TimeSpan.FromMinutes(11));
        Assert.Null(rt.Agent.IdleTick());
        time.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal("watchdog", rt.Agent.IdleTick());
        Assert.Equal(2, rt.Agent.WatchdogStreak);
        rt.Events.TryTakeBatch();
        time.Advance(TimeSpan.FromMinutes(39));
        Assert.Null(rt.Agent.IdleTick());
        time.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal("watchdog", rt.Agent.IdleTick());
        rt.Events.TryTakeBatch();

        // Busy again (the brain started work): the backoff resets.
        await rt.Activities.RunAsync([(job, ToolSchema.Empty)], TimeSpan.Zero, default);
        Assert.Null(rt.Agent.IdleTick());
        Assert.Equal(0, rt.Agent.WatchdogStreak);
        release.SetResult();
        await WaitFor(() => !rt.Activities.IsBusy);

        // An order whose tool is gone: its own wake first, the watchdog only if main still sits idle after it.
        var (rt2, _, _) = TestRuntime.Create(time: time);
        rt2.Memory.SetStandingOrder(new StandingOrder { Bot = "Clawd_bot", Job = "x", Tool = "nope", Input = J(new { }) });
        time.Advance(TimeSpan.FromMinutes(11));
        Assert.Null(rt2.Agent.IdleTick());
        Assert.Contains("can't resume: its tool nope isn't available", Assert.Single(rt2.Events.Snapshot()).Text);
        rt2.Events.TryTakeBatch();
        Assert.Equal("watchdog", rt2.Agent.IdleTick());
        Assert.Contains("not running (can't resume: its tool nope", Assert.Single(rt2.Events.Snapshot()).Text);

        // Off (hive clones): never.
        var (rt3, _, _) = TestRuntime.Create(time: time);
        rt3.Agent.IdleWatchdog = false;
        rt3.Memory.SetStandingOrder(new StandingOrder { Bot = "Clawd_bot", Job = "x", Tool = "nope", Input = J(new { }) });
        time.Advance(TimeSpan.FromMinutes(11));
        rt3.Agent.IdleTick();
        rt3.Events.TryTakeBatch();
        time.Advance(TimeSpan.FromMinutes(30));
        Assert.Null(rt3.Agent.IdleTick());
    }
}
