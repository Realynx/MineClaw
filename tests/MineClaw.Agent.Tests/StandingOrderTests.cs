using System.Diagnostics;
using System.Text.Json;
using MineClaw.Agent.Events;
using MineClaw.Agent.Human;
using MineClaw.Agent.Llm;
using MineClaw.Agent.Memory;
using MineClaw.Agent.Tools;

namespace MineClaw.Agent.Tests;

/// <summary>Bots keep working: non-blocking questions with a recommended default, standing orders, the idle default.</summary>
public class StandingOrderTests
{
    private static JsonElement J(object o) => JsonSerializer.SerializeToElement(o);

    private static async Task WaitFor(Func<bool> cond, int timeoutMs = 8000)
    {
        var sw = Stopwatch.StartNew();
        while (!cond())
        {
            if (sw.ElapsedMilliseconds > timeoutMs) throw new TimeoutException("condition not met");
            await Task.Delay(10);
        }
    }

    private static bool SentContains(List<string> sent, string s) { lock (sent) return sent.Any(l => l.Contains(s)); }

    /// <summary>A long-running fake job (the bot's "current work"); release to finish.</summary>
    private static (DelegateTool Tool, TaskCompletionSource Release, Func<int> Runs) Job(string name = "mine_gen")
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

    private static (AgentRuntime Rt, FakeLlm Llm, List<string> Sent) Quick(Action<AgentOptions>? more = null) =>
        TestRuntime.Create(o =>
        {
            o.Questions.MinTimeoutSeconds = 1;
            o.Questions.TimeoutSeconds = 1;
            o.Questions.LateAnswerSeconds = 0;
            more?.Invoke(o);
        });

    // ------------------------------------------------------------------ questions

    [Fact]
    public async Task QuestionWithDefault_ReturnsAtOnce_KeepsWorking_AndProceedsAfterTimeout()
    {
        var (rt, _, sent) = Quick();
        var (job, release, _) = Job();
        await rt.Activities.RunAsync([(job, ToolSchema.Empty)], TimeSpan.Zero, default);

        var sw = Stopwatch.StartNew();
        var res = await rt.Tools.ExecuteAsync("ask_player", J(new { question = "left cell or right cell?", name = "fox", @default = "left cell" }), default);
        Assert.True(sw.ElapsedMilliseconds < 800, $"ask blocked {sw.ElapsedMilliseconds}ms");
        Assert.True(res.Ok, res.Text);
        Assert.Contains("don't wait: go ahead with \"left cell\"", res.Text);
        Assert.True(SentContains(sent, "left cell or right cell?"));
        Assert.True(rt.Activities.IsBusy); // the job never stopped

        await WaitFor(() => SentContains(sent, "went with left cell since you're busy"));
        Assert.True(rt.Activities.IsBusy);
        Assert.Contains(rt.Events.Snapshot(), e => e.Text.Contains("question timed out") && !e.Wakes);
        release.SetResult();
    }

    [Fact]
    public async Task AnswerThatAgrees_IsQuietContext_AndDoesNotWake()
    {
        var (rt, _, sent) = Quick(o => o.Questions.TimeoutSeconds = 10);
        rt.Chat.SetOnlinePlayers(["fox"]);
        await rt.Tools.ExecuteAsync("ask_player", J(new { question = "left cell ok?", name = "fox", @default = "left cell" }), default);
        rt.Chat.OnServerChat("<fox> yep");
        await WaitFor(() => rt.Events.Count > 0);
        var e = Assert.Single(rt.Events.Snapshot());
        Assert.False(e.Wakes);
        Assert.Contains("same as your default", e.Text);
        Assert.False(SentContains(sent, "went with"));
    }

    [Fact]
    public async Task LateAnswer_ComesBackAsChat_AndTheBrainAmendsTheRunningJob()
    {
        var (rt, llm, sent) = Quick(o => o.Questions.LateAnswerSeconds = 10);
        rt.Chat.SetOnlinePlayers(["fox"]);

        // The job: a delegated task, blocked in its first tool until released.
        var inTool = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var go = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rt.Tools.Register("harvest", "Harvest.", ToolSchema.Empty, async (_, _) => { inTool.TrySetResult(); await go.Task; return ToolResult.Success("64 cobble"); },
            tiers: ToolTiers.Worker);
        llm.ThenTool("harvest", new { })                                                            // worker round 1
           .ThenTool("amend_task", new { note = "fox wants the right cell, not the left" }, id: "tu_a") // brain, woken by the answer
           .ThenText("switching to the right cell!")                                                 // brain
           .ThenText("ok: harvested the right cell");                                               // worker round 2
        var task = rt.Worker!.RunAsync("harvest the left gen cell", null, 1, default);
        await inTool.Task;

        await rt.Tools.ExecuteAsync("ask_player", J(new { question = "left cell or right cell?", name = "fox", @default = "left cell" }), default);
        await WaitFor(() => SentContains(sent, "went with left cell"));
        rt.Chat.OnServerChat("<fox> no the right one");                   // after the timeout: still applies
        await WaitFor(() => rt.Events.HasTypedChat);
        var batch = rt.Events.TryTakeBatch()!;
        var answer = batch.Waking.Single(e => e.Kind == EventKind.Chat);
        Assert.Equal("fox", answer.Player);
        Assert.Contains("no the right one (maybe a late answer to your \"left cell or right cell?\"; you went with \"left cell\"", answer.Text);

        await rt.Agent.HandleBatchAsync(batch, default);
        go.SetResult();
        var outcome = await task;
        Assert.True(outcome.Ok);
        var workerSecond = llm.Requests.Last(r => r.Tier == ModelTier.Worker);
        Assert.Contains("[brain amends task] fox wants the right cell", string.Concat(workerSecond.Messages[^1].Content.OfType<LlmText>().Select(t => t.Text)));
    }

    [Fact]
    public async Task UnsafeChoice_TakesTheSafeDefault()
    {
        var (rt, _, sent) = Quick();
        var res = await rt.Tools.ExecuteAsync("ask_player",
            J(new { question = "can i break your wall to reach the chest?", name = "fox", @default = "yes, break it" }), default);
        Assert.True(res.Ok, res.Text);
        Assert.Contains("go ahead with \"skip that part\"", res.Text);
        Assert.Contains("except the risky part", res.Text);
        await WaitFor(() => SentContains(sent, "skipped \"break your wall to reach the chest\" for now")); // names the question (live 10-28)

        Assert.True(new QuestionOptions().IsRisky("bridge out over the void?"));
        Assert.True(new QuestionOptions().IsRisky("use the diamonds?"));
        Assert.False(new QuestionOptions().IsRisky("the cell next to the lava?"));
        // Explicit risky with a safe answer already given keeps it.
        var r2 = await rt.Tools.ExecuteAsync("ask_player", J(new { question = "dig here?", name = "fox", @default = "don't dig", risky = true }), default);
        Assert.Contains("go ahead with \"don't dig\"", r2.Text);
    }

    [Fact]
    public void Agrees_ReadsConfirmations()
    {
        Assert.True(HumanTools.Agrees("yep", "left cell"));
        Assert.True(HumanTools.Agrees("the left cell", "left cell"));
        Assert.False(HumanTools.Agrees("no the right one", "left cell"));
        Assert.False(HumanTools.Agrees("no left cell is bad", "left cell"));
    }

    [Fact]
    public async Task WorkerNeedsInputWithDefault_CarriesOn_AndTellsTheBrain()
    {
        var (rt, llm, _) = Quick();
        string? asked = null;
        rt.Worker!.Asked += (q, d) => asked = q + " => " + d;
        llm.ThenText("needs_input: which chest, house or tree? default: the house chest")
           .ThenText("ok: stored 64 cobble in the house chest");
        var o = await rt.Worker.RunAsync("store the cobble", null, 1, default);
        Assert.True(o.Ok, o.Summary);
        Assert.Equal("which chest, house or tree? => the house chest", asked);
        Assert.Contains("[planner] go ahead with \"the house chest\"", string.Concat(llm.Requests[1].Messages[^1].Content.OfType<LlmText>().Select(t => t.Text)));
        Assert.Contains(rt.Events.Snapshot(), e => e.Wakes && e.Text.StartsWith("worker asks: which chest"));
    }

    // ------------------------------------------------------------------ standing orders

    private static AgentRuntime Persistent(string dir, string username, FakeLlm llm, List<string>? sent = null)
    {
        var o = new AgentOptions { KnowledgeDirectory = null, IdleCheckInMinutes = 0, DataDirectory = dir };
        o.Chat.Username = username;
        o.Chat.DisplayName = username;
        o.Chat.Aliases = [];
        o.Chat.LineDelayMs = 0;
        o.Self.Enabled = false;
        o.Rag.SeedFile = "";
        o.Idle.ResumeOnStartSeconds = 0;
        o.Idle.MinResumeIntervalSeconds = 0;
        o.ChatDebounce.QuietMs = 10;
        o.ChatDebounce.MaxMs = 50;
        return AgentRuntime.Create(o, (l, _) => { if (sent != null) lock (sent) sent.Add(l); return Task.CompletedTask; }, llm, persist: true);
    }

    [Fact]
    public async Task StandingOrder_ResumesAfterRestart_AndAfterACloneRespawn()
    {
        using var dir = new TempDir();
        var llm = new FakeLlm { Fallback = r => new LlmResponse([new LlmText("")], StopReasons.EndTurn, LlmUsage.Zero, "claude-opus-5-5") };

        // Session 1: main gets an order for itself and one for its clone, then "crashes".
        using (var main = Persistent(dir.Path, "Clawd_bot", llm))
        {
            main.Memory.SetStandingOrder(new StandingOrder { Bot = "Clawd_bot", Player = "fox", Job = "mine gen cell B", Tool = "mine_gen", Input = J(new { cell = "B" }) });
            main.Memory.SetStandingOrder(new StandingOrder { Bot = "maid_chan_67", Player = "kiwi", Job = "keep chopping trees", Tool = "chop", Input = J(new { }) });
        }

        // Restart: the order resumes by itself once the bot is up (zero model calls).
        var (job, release, runs) = Job("mine_gen");
        using var main2 = Persistent(dir.Path, "Clawd_bot", llm);
        main2.Tools.Register(job);
        var (chop, release2, chops) = Job("chop");
        main2.Tools.Register(chop);
        using var cts = new CancellationTokenSource();
        var loop = main2.Agent.RunAsync(cts.Token);
        await WaitFor(() => runs() == 1);
        Assert.Equal(0, chops());                    // the clone's order is not main's job
        Assert.Contains("[standing: mine gen cell B (fox)]", main2.Agent.Orders!.HeaderTag());
        Assert.Equal(1, main2.Memory.GetStandingOrder("Clawd_bot")!.Resumes);

        // Clone respawn: a fresh clone process (same shared db) picks up the order main/hive gave it.
        var (cloneChop, release3, cloneChops) = Job("chop");
        using var clone = Persistent(dir.Path, "maid_chan_67", llm);
        clone.Tools.Register(cloneChop);
        using var cts2 = new CancellationTokenSource();
        var loop2 = clone.Agent.RunAsync(cts2.Token);
        await WaitFor(() => cloneChops() == 1);
        Assert.Contains(main2.Memory.StandingOrders(), o => o.Bot == "maid_chan_67" && o.Resumes == 1); // the supervisor's view

        cts.Cancel(); cts2.Cancel();
        release.SetResult(); release2.SetResult(); release3.SetResult();
        try { await loop; } catch (OperationCanceledException) { }
        try { await loop2; } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task StandingOrder_SetThroughTheTool_ShowsInTheHeader_ResumesAfterChat_AndCanBeCancelled()
    {
        var (rt, llm, _) = TestRuntime.Create(o => o.Idle.MinResumeIntervalSeconds = 0);
        var (script, release, runs) = Job("script");
        rt.Tools.Register(script);
        TurnContext.Current = new TurnInfo("fox", ["fox"], false);
        var set = await rt.Tools.ExecuteAsync(StandingOrders.ToolName,
            J(new { op = "set", job = "cobble loop at the gen", script = "cobble_loop", args = new { n = 64 } }), default);
        Assert.True(set.Ok, set.Text);
        Assert.Contains("started", set.Text);
        await WaitFor(() => runs() == 1);
        var order = rt.Memory.GetStandingOrder("Clawd_bot")!;
        Assert.Equal("script", order.Tool);
        Assert.Equal("run", order.Input.GetProperty("op").GetString());
        Assert.Equal("cobble_loop", order.Input.GetProperty("name").GetString());
        Assert.Equal(64, order.Input.GetProperty("args").GetProperty("n").GetInt32());

        // Answering chat while it runs: the header shows the order; the job keeps going.
        llm.ThenText("sure!");
        await rt.Agent.HandleBatchAsync(new EventBatch([GameEvent.Chat("fox", "how's it going")]), default);
        Assert.Contains("[standing: cobble loop at the gen (fox)]", string.Concat(llm.Requests[0].Messages[^1].Content.OfType<LlmText>().Select(t => t.Text)));
        Assert.Equal(1, runs());

        // The job ends (e.g. a crash of the loop): the next idle moment brings it back.
        release.SetResult();
        await WaitFor(() => !rt.Activities.IsBusy);
        rt.Events.TryTakeBatch(); // the [task ok] event
        Assert.Equal("resumed", rt.Agent.IdleTick());
        await WaitFor(() => runs() == 2);

        // "that's enough": cleared, kept as a trace, never resumed again.
        rt.Activities.Cancel("stop");
        await WaitFor(() => !rt.Activities.IsBusy);
        var clear = await rt.Tools.ExecuteAsync(StandingOrders.ToolName, J(new { op = "clear" }), default);
        Assert.Contains("cleared", clear.Text);
        Assert.Null(rt.Agent.Orders!.Current);
        rt.Events.TryTakeBatch();
        Assert.Null(rt.Agent.IdleTick());
        Assert.Equal(2, runs());
        var ended = Assert.Single(rt.Memory.StandingOrders(activeOnly: false));
        Assert.Equal("fox", ended.EndedBy);
        TurnContext.Current = null;
    }

    [Fact]
    public async Task StandingOrder_ForAClone_IsStoredForItOnly()
    {
        var (rt, _, _) = TestRuntime.Create();
        var (script, release, runs) = Job("script");
        rt.Tools.Register(script);
        var set = await rt.Tools.ExecuteAsync(StandingOrders.ToolName, J(new { op = "set", job = "chop trees", script = "chop_loop", @for = "maid_chan_67" }), default);
        Assert.Contains("resumes when idle", set.Text);
        Assert.Equal(0, runs());
        Assert.Null(rt.Agent.Orders!.Current);
        Assert.Equal("chop trees", rt.Memory.GetStandingOrder("maid_chan_67")!.Job);
        release.SetResult();
    }

    // ------------------------------------------------------------------ idle default

    [Fact]
    public void IdleDefault_FiresOnlyWithPlayersOnline_OncePerIdleStretch()
    {
        var time = new ManualTime();
        var (rt, _, _) = TestRuntime.Create(o => o.Idle.AfterMinutes = 2, time: time);
        rt.Chat.SetOnlinePlayers([]);
        time.Advance(TimeSpan.FromMinutes(3));
        Assert.Null(rt.Agent.IdleTick());            // nobody online: sleep
        Assert.Equal(0, rt.Events.Count);

        rt.Chat.SetOnlinePlayers(["fox"]);
        Assert.Equal("idle default", rt.Agent.IdleTick());
        var e = Assert.Single(rt.Events.Snapshot());
        Assert.Equal(EventKind.IdleTick, e.Kind);
        Assert.True(e.Wakes);
        Assert.Contains("nothing to do for 3m", e.Text);
        Assert.Contains("top up cobblestone", e.Text);
        rt.Events.TryTakeBatch();
        Assert.Null(rt.Agent.IdleTick());            // once per stretch

        // Too soon after activity: nothing.
        var (rt2, _, _) = TestRuntime.Create(o => o.Idle.AfterMinutes = 2, time: time);
        rt2.Chat.SetOnlinePlayers(["fox"]);
        time.Advance(TimeSpan.FromMinutes(1));
        Assert.Null(rt2.Agent.IdleTick());
    }
}
