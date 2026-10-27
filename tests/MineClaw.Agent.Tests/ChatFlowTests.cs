using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using MineClaw.Agent.Events;
using MineClaw.Agent.Llm;
using MineClaw.Agent.Tools;

namespace MineClaw.Agent.Tests;

/// <summary>
/// Players typing several lines in a row, or adding a line while the brain is thinking or acting: typing debounce, one
/// cancel-and-restart during the first response, injection at round boundaries, the busy hint, dedupe and reply merging.
/// </summary>
public class ChatFlowTests
{
    private static (AgentRuntime Rt, FakeLlm Llm, List<string> Sent) Create(int quietMs, int maxMs, Action<AgentOptions>? more = null) =>
        TestRuntime.Create(o =>
        {
            o.ChatDebounce.QuietMs = quietMs;
            o.ChatDebounce.MaxMs = maxMs;
            more?.Invoke(o);
        });

    private static async Task WaitFor(Func<bool> cond, int timeoutMs = 10_000)
    {
        var sw = Stopwatch.StartNew();
        while (!cond())
        {
            if (sw.ElapsedMilliseconds > timeoutMs) throw new TimeoutException("condition not met");
            await Task.Delay(10);
        }
    }

    private static string LastUserText(LlmRequest r) => string.Concat(r.Messages[^1].Content.OfType<LlmText>().Select(t => t.Text));

    private static LlmResponse Text(LlmRequest r, string text) =>
        new([new LlmText(text)], StopReasons.EndTurn, new LlmUsage(100, 20, 1000, 0), "claude-opus-5-5");

    // ------------------------------------------------------------------ typing debounce

    [Fact]
    public async Task ThreeLinesWithinTwoSeconds_MakeOneModelCall_WithDefaultWindows()
    {
        var (rt, llm, _) = Create(new ChatDebounceOptions().QuietMs, new ChatDebounceOptions().MaxMs);
        llm.Fallback = r => Text(r, "on it");
        using var cts = new CancellationTokenSource();
        var loop = rt.Agent.RunAsync(cts.Token);
        var sw = Stopwatch.StartNew();
        rt.Events.Post(GameEvent.Chat("fox", "mine the gen"));
        await Task.Delay(600);
        rt.Events.Post(GameEvent.Chat("fox", "actually the left cell"));
        await Task.Delay(600);
        rt.Events.Post(GameEvent.Chat("fox", "and bring me 10"));
        await WaitFor(() => llm.Requests.Count >= 1);
        long fired = sw.ElapsedMilliseconds;
        await Task.Delay(1500); // nothing else may fire
        cts.Cancel();
        try { await loop; } catch (OperationCanceledException) { }

        Assert.Single(llm.Requests);
        Assert.Contains("[chat] fox: mine the gen / actually the left cell / and bring me 10", LastUserText(llm.Requests[0]));
        Assert.InRange(fired, 1200 + 1700, 1200 + 3500); // ~1.8 s after the LAST line, not the first
        Assert.Equal(1, rt.Agent.ChatFlow.MergedTurns);
        Assert.Equal(2, rt.Agent.ChatFlow.LinesMerged);
    }

    [Theory]
    [InlineData("stop")]
    [InlineData("wait, not that one")]
    [InlineData("no")]
    [InlineData("clawd stop!")]
    [InlineData("where are you?")]
    public async Task UrgentLine_BypassesDebounce(string line)
    {
        var (rt, llm, _) = Create(5000, 10_000);
        llm.Fallback = r => Text(r, "ok");
        using var cts = new CancellationTokenSource();
        var loop = rt.Agent.RunAsync(cts.Token);
        var sw = Stopwatch.StartNew();
        rt.Events.Post(GameEvent.Chat("fox", line));
        await WaitFor(() => llm.Requests.Count >= 1, 3000);
        Assert.True(sw.ElapsedMilliseconds < 1500, $"took {sw.ElapsedMilliseconds}ms");
        cts.Cancel();
        try { await loop; } catch (OperationCanceledException) { }
        Assert.Equal(1, rt.Agent.ChatFlow.ImmediateFires);
    }

    [Fact]
    public async Task QuestionAfterOtherLines_DoesNotBypass()
    {
        var q = new EventQueue(new EventQueueOptions(), debounce: new ChatDebounceOptions { QuietMs = 400, MaxMs = 4000 });
        var sw = Stopwatch.StartNew();
        var wait = q.WaitBatchAsync(new CancellationTokenSource(5000).Token);
        q.Post(GameEvent.Chat("fox", "so about the gen"));
        q.Post(GameEvent.Chat("fox", "is it working?"));
        var b = await wait;
        Assert.True(sw.ElapsedMilliseconds >= 350);
        Assert.Equal("[chat] fox: so about the gen / is it working?", b.Format());
    }

    [Fact]
    public void ImmediateWords_MatchFirstWordOrShortLines()
    {
        var d = new ChatDebounceOptions();
        Assert.True(d.IsImmediate("stop mining"));
        Assert.True(d.IsImmediate("hey stop"));
        Assert.True(d.IsImmediate("NO!"));
        Assert.False(d.IsImmediate("i know you can't stop me from building here"));
        Assert.False(d.IsImmediate("mine the gen"));
    }

    [Fact]
    public async Task TwoPlayers_SeparateWindows_OneTurn()
    {
        var q = new EventQueue(new EventQueueOptions(), debounce: new ChatDebounceOptions { QuietMs = 250, MaxMs = 4000 });
        var sw = Stopwatch.StartNew();
        var wait = q.WaitBatchAsync(new CancellationTokenSource(5000).Token);
        q.Post(GameEvent.Chat("fox", "mine the gen"));
        await Task.Delay(50);
        q.Post(GameEvent.Chat("kiwi", "can you also make torches"));
        await Task.Delay(150);
        q.Post(GameEvent.Chat("fox", "the left cell"));       // fox is still typing: kiwi's window alone doesn't fire
        var b = await wait;
        Assert.True(sw.ElapsedMilliseconds >= 200 + 230, $"fired at {sw.ElapsedMilliseconds}ms");
        Assert.Equal("[chat] fox: mine the gen / the left cell\n[chat] kiwi: can you also make torches", b.Format().Replace("\r", ""));
        Assert.Equal(0, q.Count);
    }

    [Fact]
    public async Task MaxCap_FiresEvenWhileSomeoneKeepsTyping()
    {
        var q = new EventQueue(new EventQueueOptions(), debounce: new ChatDebounceOptions { QuietMs = 300, MaxMs = 700 });
        var sw = Stopwatch.StartNew();
        var wait = q.WaitBatchAsync(new CancellationTokenSource(5000).Token);
        for (int i = 0; i < 12 && !wait.IsCompleted; i++)
        {
            q.Post(GameEvent.Chat("fox", "line " + i));
            await Task.Delay(100);
        }
        await wait;
        Assert.InRange(sw.ElapsedMilliseconds, 600, 1500);
    }

    // ------------------------------------------------------------------ dedupe

    [Fact]
    public void RepeatedLines_Collapse_AndInTurnRepeatsAreDropped()
    {
        var q = new EventQueue(new EventQueueOptions(), debounce: new ChatDebounceOptions());
        q.Post(GameEvent.Chat("fox", "hello?"));
        q.Post(GameEvent.Chat("fox", "hello?"));
        q.Post(GameEvent.Chat("fox", "hello?"));
        var e = Assert.Single(q.Snapshot());
        Assert.Equal(3, e.Count);
        Assert.Equal("[chat] fox: hello? (x3)", new EventBatch(q.Snapshot()).Format());

        q.BeginTurn();
        Assert.NotNull(q.TryTakeBatch());
        Assert.False(q.Post(GameEvent.Chat("fox", "Hello? ")));  // the running turn already has it
        Assert.True(q.Post(GameEvent.Chat("fox", "hello??? you there"))); // something new still lands
        q.EndTurn();
        Assert.NotNull(q.TryTakeBatch());
        Assert.True(q.Post(GameEvent.Chat("fox", "hello?")));   // after the turn, a repeat means "you didn't answer": wakes
        Assert.Equal(3, q.Stats.RepeatsCollapsed);
    }

    [Fact]
    public async Task AskPlayerReply_InSeveralLines_IsOneAnswer_AndWakesNoTurn()
    {
        var (rt, _, _) = Create(10, 50, o => o.ChatDebounce.ReplyQuietMs = 200);
        rt.Chat.SetOnlinePlayers(["fox"]);
        var wait = rt.Chat.WaitForReplyAsync("fox", TimeSpan.FromSeconds(5), default);
        rt.Chat.OnServerChat("<fox> the left");
        await Task.Delay(60);
        rt.Chat.OnServerChat("<fox> one next to the lava");
        var reply = await wait;
        Assert.Equal("fox: the left / one next to the lava", reply);
        Assert.Equal(0, rt.Events.Count); // routed to the waiter, never queued as a turn
        Assert.Equal(1, rt.Agent.ChatFlow.ReplyMerges);
    }

    // ------------------------------------------------------------------ (a) restart during the first response

    [Fact]
    public async Task MessageDuringFirstRound_CancelsAndRestartsOnce_WithBothMessages()
    {
        var (rt, llm, sent) = Create(80, 1000);
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        llm.ThenAsync(async (r, ct) =>
        {
            firstStarted.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { throw new LlmInterruptedException(new LlmUsage(12, 9, 1500, 0), "claude-opus-5-5"); }
            throw new InvalidOperationException("unreachable");
        });
        llm.ThenAsync(async (r, ct) =>
        {
            secondStarted.TrySetResult();
            await Task.Delay(300, CancellationToken.None); // a third line arrives meanwhile: no second restart
            Assert.False(ct.IsCancellationRequested);
            return Text(r, "left cell it is");
        });

        var turn = rt.Agent.HandleBatchAsync(new EventBatch([GameEvent.Chat("fox", "mine the gen")]), default);
        await firstStarted.Task;
        rt.Events.Post(GameEvent.Chat("fox", "actually the left cell"));
        await secondStarted.Task;
        rt.Events.Post(GameEvent.Chat("fox", "and bring me 10"));
        var outcome = await turn;

        Assert.Equal(2, llm.Requests.Count);
        Assert.True(llm.Requests[0].Interruptible);
        Assert.False(llm.Requests[1].Interruptible); // budget of one restart used up
        Assert.Contains("[chat] fox: mine the gen / actually the left cell", LastUserText(llm.Requests[1]));
        Assert.DoesNotContain("bring me 10", LastUserText(llm.Requests[1]));
        Assert.Contains(rt.Events.Snapshot(), e => e.Text == "and bring me 10"); // next boundary / next turn
        Assert.Equal(["left cell it is"], sent);

        var s = rt.Agent.ChatFlow;
        Assert.Equal(1, s.Restarts);
        Assert.Equal(9, s.WastedOutputTokens);
        Assert.Equal(1500, s.WastedCacheReadTokens);
        Assert.True(s.SavedTokensEstimate > 0);
        Assert.Equal(2, outcome.Calls); // the cancelled call is counted (and billed) honestly
        Assert.Equal(new LlmUsage(112, 29, 2500, 0), outcome.Usage);

        // Same cached prefix: system, tools and history byte-identical between the cancelled and the restarted request.
        var (a, b) = (Params(llm.Requests[0]), Params(llm.Requests[1]));
        Assert.Equal(a.GetProperty("system").GetRawText(), b.GetProperty("system").GetRawText());
        Assert.Equal(a.GetProperty("tools").GetRawText(), b.GetProperty("tools").GetRawText());
        Assert.Equal(a.GetProperty("messages").GetArrayLength(), b.GetProperty("messages").GetArrayLength());
    }

    [Fact]
    public async Task ChatPendingBeforeTheFirstCall_RestartsWithoutCalling()
    {
        var (rt, llm, _) = Create(10, 200);
        llm.Fallback = r => Text(r, "ok");
        rt.Events.Post(GameEvent.Chat("fox", "and some sand")); // arrived after the batch was taken, before the call
        await rt.Agent.HandleBatchAsync(new EventBatch([GameEvent.Chat("fox", "get me dirt")]), default);
        var only = Assert.Single(llm.Requests);
        Assert.Contains("[chat] fox: get me dirt / and some sand", LastUserText(only));
        Assert.Equal(1, rt.Agent.ChatFlow.Restarts);
        Assert.Equal(0, rt.Agent.ChatFlow.WastedOutputTokens);
    }

    // ------------------------------------------------------------------ (b) injection at the round boundary

    [Fact]
    public async Task MessageDuringToolExecution_IsInjectedAtBoundary_NoRestart_PairingValid_PrefixStable()
    {
        var (rt, llm, _) = Create(10, 50);
        rt.Tools.Register("look", "Look.", ToolSchema.Empty, async (_, _) =>
        {
            rt.Events.Post(GameEvent.Chat("fox", "oh and grab the sapling"));
            await Task.Delay(20);
            return ToolResult.Success("grass, a sapling");
        });
        llm.ThenTool("look", new { }, id: "tu_look").ThenText("grabbing it");
        await rt.Agent.HandleBatchAsync(new EventBatch([GameEvent.Chat("fox", "what's around?")]), default);

        Assert.Equal(2, llm.Requests.Count);
        var assistant = llm.Requests[1].Messages[^2];
        var user = llm.Requests[1].Messages[^1];
        Assert.Equal(LlmRole.Assistant, assistant.Role);
        Assert.Equal(LlmRole.User, user.Role);
        var result = Assert.IsType<LlmToolResult>(user.Content[0]);
        Assert.Equal(assistant.ToolUses.Single().Id, result.ToolUseId);           // pairing intact
        Assert.Equal(assistant.ToolUses.Count(), user.ToolResults.Count());
        Assert.Equal("[new chat] fox: oh and grab the sapling", ((LlmText)user.Content[^1]).Text); // after the results
        Assert.Equal(0, rt.Agent.ChatFlow.Restarts);
        Assert.Equal(1, rt.Agent.ChatFlow.MidTurnInjections);

        // Append-only: request 1 = request 0 + new messages; every earlier message byte-identical (cache markers aside).
        var (a, b) = (Params(llm.Requests[0]), Params(llm.Requests[1]));
        Assert.Equal(a.GetProperty("system").GetRawText(), b.GetProperty("system").GetRawText());
        Assert.Equal(a.GetProperty("tools").GetRawText(), b.GetProperty("tools").GetRawText());
        var ma = a.GetProperty("messages");
        var mb = b.GetProperty("messages");
        Assert.True(mb.GetArrayLength() > ma.GetArrayLength());
        for (int i = 0; i < ma.GetArrayLength(); i++)
            Assert.Equal(NoCache(ma[i].GetRawText()), NoCache(mb[i].GetRawText()));
    }

    // ------------------------------------------------------------------ (c) brain asleep, task running

    [Fact]
    public async Task MessageWhileScriptRuns_WakesBrainWithStatusAndBusyLine()
    {
        var (rt, llm, _) = Create(10, 50);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var script = new DelegateTool("script", "Run a script.", ToolSchema.Empty, async (_, ct) =>
        {
            await release.Task.WaitAsync(ct);
            return ToolResult.Success("done");
        }, longRunning: true);
        var started = await rt.Activities.RunAsync([(script, ToolSchema.Empty)], TimeSpan.Zero, default);
        Assert.Contains("background", started[0].Text);

        llm.ThenText("sure, after this batch");
        await rt.Agent.HandleBatchAsync(new EventBatch([GameEvent.Chat("fox", "also grab some sand after")]), default);
        var text = LastUserText(llm.Requests[0]);
        Assert.Contains("task: script #1", text);
        Assert.Contains("[busy] script #1 still running: amend it, don't restart", text);
        Assert.Equal(1, rt.Agent.ChatFlow.BusyWakes);
        Assert.True(rt.Activities.IsBusy); // the reply didn't cancel or restart the script
        release.SetResult();
    }

    [Fact]
    public async Task AmendTask_ReachesTheRunningWorker_WithoutRestart()
    {
        var (rt, llm, _) = Create(10, 50);
        var inTool = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var go = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rt.Tools.Register("dig", "Dig.", ToolSchema.Empty, async (_, _) => { inTool.TrySetResult(); await go.Task; return ToolResult.Success("dug"); },
            tiers: ToolTiers.Worker);
        llm.ThenTool("dig", new { }).ThenText("ok: dug and added torches");
        var worker = rt.Worker!;
        Assert.False(worker.Amend("too early"));
        var task = worker.RunAsync("dig the cell", null, 1, default);
        await inTool.Task;
        Assert.True(worker.IsRunning);
        var amend = await rt.Tools.ExecuteAsync(MineClaw.Agent.Worker.WorkerAgent.AmendToolName,
            JsonSerializer.SerializeToElement(new { note = "fox also wants 4 torches" }), default);
        Assert.True(amend.Ok);
        go.SetResult();
        var o = await task;
        Assert.True(o.Ok);
        Assert.Contains("[brain amends task] fox also wants 4 torches", LastUserText(llm.Requests[1]));
        Assert.False(worker.IsRunning);
        Assert.False((await rt.Tools.ExecuteAsync(MineClaw.Agent.Worker.WorkerAgent.AmendToolName,
            JsonSerializer.SerializeToElement(new { note = "x" }), default)).Ok);
    }

    // ------------------------------------------------------------------ helpers

    private static JsonElement Params(LlmRequest r)
    {
        var o = new LlmOptions { ApiKey = "sk-test-not-real" };
        var p = new AnthropicLlmClient(o).BuildParams(r, o.ModelFor(r.Tier));
        // The wire body, exactly as it would be sent.
        return JsonDocument.Parse(JsonSerializer.Serialize(p)).RootElement.GetProperty("RawBodyData").Clone();
    }

    private static string NoCache(string json) => Regex.Replace(json, @",?""cache_control"":\{[^}]*\}", "");
}
