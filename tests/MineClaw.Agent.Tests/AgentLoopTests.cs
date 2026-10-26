using MineClaw.Agent.Budget;
using MineClaw.Agent.Events;
using MineClaw.Agent.Llm;
using MineClaw.Agent.Tools;

namespace MineClaw.Agent.Tests;

public class AgentLoopTests
{
    private static EventBatch Chat(string player, string text) => new([GameEvent.Chat(player, text)]);

    [Fact]
    public async Task ToolCall_Result_FinalText()
    {
        var (rt, llm, sent) = TestRuntime.Create();
        int calls = 0;
        rt.Tools.Register("count_blocks", "Count blocks.", ToolSchema.Create().Str("block").Build(), (input, _) =>
        {
            calls++;
            return Task.FromResult(ToolResult.Success($"12 {input.Str("block")}"));
        });
        rt.Agent.StateHeader = () => "pos 1,64,2 hp 20/20 food 20 held air";

        llm.ThenTool("count_blocks", new { block = "cobblestone" }, text: "lemme check", id: "tu_a")
           .ThenText("you've got 12 cobble!");

        var outcome = await rt.Agent.HandleBatchAsync(Chat("Alice", "clawd can you mine some cobble?"), default);

        Assert.Equal(1, calls);
        Assert.Equal(ModelTier.Main, outcome.Tier); // "mine" is an action word
        Assert.Equal(2, outcome.Calls);
        Assert.Equal(["lemme check", "you've got 12 cobble!"], sent);

        // Second request carries the tool_result for the tool_use id.
        var second = llm.Requests[1];
        var result = second.Messages[^1].ToolResults.Single();
        Assert.Equal("tu_a", result.ToolUseId);
        Assert.Equal("12 cobblestone", result.Content);
        Assert.False(result.IsError);

        // First user message has the state header and the event.
        var firstText = llm.Requests[0].Messages[0].Text;
        Assert.Contains("pos 1,64,2", firstText);
        Assert.Contains("[chat] Alice: clawd can you mine some cobble?", firstText);

        // Static prefix is cached; main tier sees every registered tool.
        Assert.True(llm.Requests[0].System[0].Cache);
        Assert.Contains(llm.Requests[0].Tools, t => t.Name == "count_blocks");

        // Committed episode is well-formed and ends with assistant.
        var ep = Assert.Single(rt.Context.Episodes);
        Assert.Equal(LlmRole.Assistant, ep.Messages[^1].Role);
        Assert.True(rt.Usage.Today.Calls == 2 && rt.Usage.Today.Usd > 0);
    }

    [Fact]
    public async Task CheapTier_Escalates_ToMain_AndEscalationNotInHistory()
    {
        var (rt, llm, sent) = TestRuntime.Create();
        llm.ThenTool(BotAgent.EscalateToolName, new { reason = "needs a plan" }, text: "ooh ok one sec")
           .ThenText("plan: build a cobble gen next to the tree");

        var outcome = await rt.Agent.HandleBatchAsync(Chat("Bob", "clawd what should we do about the island"), default);

        Assert.True(outcome.Escalated);
        Assert.Equal(ModelTier.Cheap, llm.Requests[0].Tier);
        Assert.Contains(llm.Requests[0].Tools, t => t.Name == BotAgent.EscalateToolName);
        Assert.DoesNotContain(llm.Requests[0].Tools, t => t.Name == "save_routine"); // main-only tool
        Assert.Equal(ModelTier.Main, llm.Requests[1].Tier);
        Assert.DoesNotContain(llm.Requests[1].Tools, t => t.Name == BotAgent.EscalateToolName);
        Assert.Contains("escalated from quick mode", llm.Requests[1].Messages[0].Text);
        Assert.Equal(["ooh ok one sec", "plan: build a cobble gen next to the tree"], sent);

        var ep = Assert.Single(rt.Context.Episodes);
        Assert.Equal(ModelTier.Main, ep.Tier);
        Assert.DoesNotContain(ep.Messages.SelectMany(m => m.ToolUses), u => u.Name == BotAgent.EscalateToolName);
    }

    [Fact]
    public async Task SilentReply_SendsNothing()
    {
        var (rt, llm, sent) = TestRuntime.Create();
        llm.Then(r => llm.Resp(r, StopReasons.EndTurn));
        await rt.Agent.HandleBatchAsync(Chat("Bob", "lol"), default);
        Assert.Empty(sent);
        Assert.Equal("(silent)", rt.Context.Episodes.Single().Messages[^1].Text);
    }

    [Fact]
    public async Task InstantTools_RunInParallel()
    {
        var (rt, llm, _) = TestRuntime.Create();
        var aStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rt.Tools.Register("a", "a", ToolSchema.Empty, async (_, ct) =>
        {
            aStarted.SetResult();
            await bStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), ct); // deadlocks unless b runs concurrently
            return ToolResult.Success("a done");
        });
        rt.Tools.Register("b", "b", ToolSchema.Empty, async (_, ct) =>
        {
            bStarted.SetResult();
            await aStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            return ToolResult.Success("b done");
        });
        llm.Then(r => llm.Resp(r, StopReasons.ToolUse,
                new LlmToolUse("t1", "a", ToolSchema.Empty), new LlmToolUse("t2", "b", ToolSchema.Empty)))
           .ThenText("both done");

        await rt.Agent.HandleBatchAsync(new EventBatch([GameEvent.TaskDone("x")]), default);

        var results = llm.Requests[1].Messages[^1].ToolResults.ToList();
        Assert.Equal(["t1", "t2"], results.Select(r => r.ToolUseId));
        Assert.All(results, r => Assert.False(r.IsError));
    }

    [Fact]
    public async Task LongRunningTool_DetachesAndReportsViaTaskEvent()
    {
        var (rt, llm, _) = TestRuntime.Create(o => o.InlineToolWaitSeconds = 0);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rt.Tools.Register("mine", "Mine.", ToolSchema.Empty, async (_, ct) =>
        {
            await release.Task.WaitAsync(ct);
            return ToolResult.Success("mined 12 cobblestone");
        }, longRunning: true);

        llm.ThenTool("mine", new { }).ThenText("on it");
        await rt.Agent.HandleBatchAsync(new EventBatch([GameEvent.TaskDone("previous")]), default);

        var r = llm.Requests[1].Messages[^1].ToolResults.Single();
        Assert.Contains("background", r.Content);
        Assert.True(rt.Activities.IsBusy);

        release.SetResult();
        var batch = await rt.Events.WaitBatchAsync(new CancellationTokenSource(5000).Token);
        var ev = Assert.Single(batch.Events);
        Assert.Equal(EventKind.TaskCompleted, ev.Kind);
        Assert.Contains("mined 12 cobblestone", ev.Text);
    }

    [Fact]
    public async Task DangerEvent_CancelsActivity_AndStopToolCancels()
    {
        var (rt, _, _) = TestRuntime.Create();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tool = new DelegateTool("mine", "Mine.", ToolSchema.Empty, async (_, ct) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return ToolResult.Success("never");
        }, longRunning: true);

        var run = rt.Activities.RunAsync([(tool, ToolSchema.Empty)], TimeSpan.FromSeconds(10), default);
        await started.Task;
        rt.Events.Post(GameEvent.LowHealth(4));
        var results = await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(results[0].Ok);
        Assert.Contains("cancelled", results[0].Text);

        // stop tool
        started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run2 = rt.Activities.RunAsync([(tool, ToolSchema.Empty)], TimeSpan.FromSeconds(10), default);
        await started.Task;
        var stop = await rt.Tools.ExecuteAsync("stop", ToolSchema.Empty, default);
        Assert.Contains("stopped mine", stop.Text);
        Assert.False((await run2.WaitAsync(TimeSpan.FromSeconds(5)))[0].Ok);
    }

    [Fact]
    public async Task CheapTier_CannotCallMainOnlyTools()
    {
        var (rt, llm, _) = TestRuntime.Create();
        bool ran = false;
        rt.Tools.Register("dig", "Dig.", ToolSchema.Empty, (_, _) => { ran = true; return Task.FromResult(ToolResult.Success("dug")); });
        llm.ThenTool("dig", new { }).ThenText("hm can't");
        await rt.Agent.HandleBatchAsync(Chat("Bob", "hey clawd how's it going"), default);
        Assert.False(ran);
        Assert.True(llm.Requests[1].Messages[^1].ToolResults.Single().IsError);
    }

    [Fact]
    public async Task MidEpisodeEvents_AreAppendedAfterToolResults()
    {
        var (rt, llm, _) = TestRuntime.Create();
        rt.Tools.Register("look", "Look.", ToolSchema.Empty, (_, _) =>
        {
            rt.Events.Post(GameEvent.Chat("Alice", "clawd wait stop"));
            return Task.FromResult(ToolResult.Success("grass"));
        });
        llm.ThenTool("look", new { }).ThenText("ok stopping");
        await rt.Agent.HandleBatchAsync(new EventBatch([GameEvent.TaskDone("x")]), default);
        var last = llm.Requests[1].Messages[^1];
        Assert.IsType<LlmToolResult>(last.Content[0]);
        Assert.Contains("[new events]", ((LlmText)last.Content[^1]).Text);
        Assert.Contains("wait stop", ((LlmText)last.Content[^1]).Text);
    }

    [Fact]
    public async Task Budget_Paused_MakesNoCalls_AndCheapOnlyForcesCheapTier()
    {
        var (rt, llm, sent) = TestRuntime.Create(o => { o.Budget.DailyCapUsd = 0.001m; o.Budget.MonthlyCapUsd = 100m; });
        rt.Usage.Record("claude-sonnet-5-5", new LlmUsage(1000, 1000, 0, 0)); // $0.012 > cap
        Assert.Equal(BudgetMode.Paused, rt.Usage.Mode);
        var o = await rt.Agent.HandleBatchAsync(Chat("Alice", "clawd build a house"), default);
        Assert.NotNull(o.Skipped);
        Assert.Empty(llm.Requests);
        Assert.Contains(sent, s => s.Contains("paused"));

        var (rt2, llm2, _) = TestRuntime.Create(o2 => { o2.Budget.DailyCapUsd = 1m; o2.Budget.CheapOnlyFraction = 0.5; });
        rt2.Usage.Record("claude-sonnet-5-5", new LlmUsage(0, 60_000, 0, 0)); // $0.60 ≥ 50%
        Assert.Equal(BudgetMode.CheapOnly, rt2.Usage.Mode);
        llm2.ThenText("saving energy rn");
        await rt2.Agent.HandleBatchAsync(Chat("Alice", "clawd mine cobble"), default);
        Assert.Equal(ModelTier.Cheap, llm2.Requests.Single().Tier);
        Assert.DoesNotContain(llm2.Requests[0].Tools, t => t.Name == BotAgent.EscalateToolName);
        Assert.Contains("budget low", llm2.Requests[0].Messages[0].Text);
    }

    [Fact]
    public async Task RunAsync_ProcessesQueuedEvents()
    {
        var (rt, llm, sent) = TestRuntime.Create();
        llm.ThenText("hiii");
        using var cts = new CancellationTokenSource();
        var done = new TaskCompletionSource();
        rt.Agent.EpisodeCompleted += _ => done.TrySetResult();
        var loop = rt.Agent.RunAsync(cts.Token);
        rt.Chat.OnServerChat("<Alice> hi clawd");
        await done.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();
        await loop.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(["hiii"], sent);
    }

    [Fact]
    public async Task ManyEpisodes_TriggerCompaction_WithCheapSummary()
    {
        var (rt, llm, _) = TestRuntime.Create(o => o.Context.HistoryTokenBudget = 300);
        llm.Fallback = r => r.Purpose == "compact"
            ? llm.Resp(r, StopReasons.EndTurn, new LlmText("journal: chatted a lot"))
            : llm.Resp(r, StopReasons.EndTurn, new LlmText(new string('x', 200)));
        for (int i = 0; i < 6; i++)
            await rt.Agent.HandleBatchAsync(new EventBatch([GameEvent.TaskDone("step " + i)]), default);
        Assert.True(rt.Context.EstimateTokens() <= 300);
        Assert.Contains("chatted", rt.Context.Journal);
        Assert.Contains(llm.Requests, r => r.Purpose == "compact" && r.Tier == ModelTier.Cheap && r.Tools.Count == 0);
    }
}
