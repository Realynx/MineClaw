using System.Collections.Concurrent;
using System.Text.Json;
using MineClaw.Agent.Budget;
using MineClaw.Agent.Context;
using MineClaw.Agent.Events;
using MineClaw.Agent.Human;
using MineClaw.Agent.Llm;
using MineClaw.Agent.Memory;
using MineClaw.Agent.Self;
using MineClaw.Agent.Tools;
using MineClaw.Agent.Worker;

namespace MineClaw.Agent.Tests;

/// <summary>Fake LLM with one script per tier; answers with the tier's real model id so per-tier accounting is exercised.</summary>
public sealed class TieredLlm : ILlmClient
{
    private readonly ConcurrentDictionary<ModelTier, ConcurrentQueue<Func<LlmRequest, Task<LlmResponse>>>> _scripts = new();
    public List<LlmRequest> Requests { get; } = new();
    public LlmUsage UsagePerCall { get; set; } = new(100, 20, 1000, 0);
    public LlmOptions Options { get; } = new();

    public IEnumerable<LlmRequest> Of(ModelTier tier) { lock (Requests) return Requests.Where(r => r.Tier == tier).ToList(); }

    public TieredLlm On(ModelTier tier, Func<LlmRequest, LlmResponse> h) => OnAsync(tier, r => Task.FromResult(h(r)));

    public TieredLlm OnAsync(ModelTier tier, Func<LlmRequest, Task<LlmResponse>> h)
    {
        _scripts.GetOrAdd(tier, _ => new()).Enqueue(h);
        return this;
    }

    public TieredLlm Text(ModelTier tier, string text) => On(tier, r => Resp(r, StopReasons.EndTurn, new LlmText(text)));

    public TieredLlm Tool(ModelTier tier, string name, object input, string id = "tu_1", string? text = null) => On(tier, r =>
    {
        var blocks = new List<LlmBlock>();
        if (text != null) blocks.Add(new LlmText(text));
        blocks.Add(new LlmToolUse(id, name, JsonSerializer.SerializeToElement(input)));
        return Resp(r, StopReasons.ToolUse, blocks.ToArray());
    });

    public LlmResponse Resp(LlmRequest r, string stop, params LlmBlock[] blocks) =>
        new(blocks, stop, UsagePerCall, r.Model ?? Options.ModelFor(r.Tier));

    public Func<LlmRequest, LlmResponse>? Fallback { get; set; }

    public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct)
    {
        lock (Requests) Requests.Add(request);
        if (_scripts.TryGetValue(request.Tier, out var q) && q.TryDequeue(out var h)) return await h(request).WaitAsync(ct);
        if (Fallback != null) return Fallback(request);
        throw new InvalidOperationException($"TieredLlm: nothing scripted for {request.Tier} ({request.Purpose})");
    }
}

public class BrainTierTests
{
    private static EventBatch Chat(string player, string text) => new([GameEvent.Chat(player, text)]);

    private static (AgentRuntime Rt, TieredLlm Llm, List<string> Sent) Create(Action<AgentOptions>? configure = null)
    {
        var options = new AgentOptions { KnowledgeDirectory = null, IdleCheckInMinutes = 0 };
        options.Chat.Username = "maid_chan";
        options.Chat.DisplayName = "claudechan";
        options.Chat.Aliases = [];
        options.Chat.LineDelayMs = 0;
        options.Events.CoalesceWindowMs = 10;
        options.Events.MaxBatchDelayMs = 50;
        options.ChatDebounce.QuietMs = 10;
        options.ChatDebounce.MaxMs = 50;
        options.ChatDebounce.ReplyQuietMs = 0;
        // Per-call accounting: the two-phase reply calls (quick ack, cheap-only triage) have their own tests (QuickReplyTests).
        options.Brainstem.Reply.QuickAck = false;
        options.Brainstem.Reply.CheapOnly = false;
        configure?.Invoke(options);
        var llm = new TieredLlm();
        var sent = new List<string>();
        var rt = AgentRuntime.Create(options, (line, _) => { lock (sent) sent.Add(line); return Task.CompletedTask; }, llm, persist: false);
        return (rt, llm, sent);
    }

    /// <summary>Registers fake game tools the way BotHost does, then applies the brain/worker split.</summary>
    private static void GameTools(AgentRuntime rt, Func<JsonElement, CancellationToken, Task<ToolResult>>? mine = null)
    {
        rt.Tools.Register("status", "Status.", ToolSchema.Empty, (_, _) => Task.FromResult(ToolResult.Success("pos 0,64,0 hp 20")), cheapTier: true);
        rt.Tools.Register("goto", "Go.", ToolSchema.Create().Str("to").Build(), (_, _) => Task.FromResult(ToolResult.Success("arrived")), longRunning: true);
        rt.Tools.Register("mine", "Mine.", ToolSchema.Create().Str("block").Int("count", required: false).Build(),
            mine ?? ((i, _) => Task.FromResult(ToolResult.Success($"mined {i.Int("count") ?? 8} {i.Str("block")}"))), longRunning: true);
        rt.ApplyBrainSplit();
    }

    private static int RequestTokens(LlmRequest r, LlmOptions o) => BotAgent.EstimateRequestTokens(r, o.MaxTokensFor(r.Tier));

    [Fact]
    public void BrainSplit_GivesEachTierItsOwnTightToolSet()
    {
        var (rt, _, _) = Create();
        GameTools(rt);
        var brain = rt.Tools.Definitions(ModelTier.Main).Select(d => d.Name).ToList();
        var worker = rt.Tools.Definitions(ModelTier.Worker).Select(d => d.Name).ToList();
        var cheap = rt.Tools.Definitions(ModelTier.Cheap).Select(d => d.Name).ToList();

        Assert.Contains("delegate", brain);
        Assert.Contains("ask_memory", brain);
        Assert.Contains("do", brain);                  // goto/follow/... run through the compact do meta-tool
        Assert.DoesNotContain("goto", brain);
        Assert.Contains("status", brain);
        Assert.Contains("ask_player", brain);
        Assert.DoesNotContain("mine", brain);          // multi-step work goes through delegate
        Assert.DoesNotContain("recall", brain);        // superseded by ask_memory
        Assert.DoesNotContain("save_routine", brain);

        Assert.Contains("mine", worker);
        Assert.Contains("goto", worker);
        Assert.Contains("recall", worker);
        Assert.DoesNotContain("delegate", worker);
        Assert.DoesNotContain("ask_player", worker);   // the worker never talks to players
        Assert.DoesNotContain("identity", worker);
        Assert.DoesNotContain("stop", worker);

        Assert.DoesNotContain("ask_player", cheap);
        Assert.Contains("recall", cheap);
    }

    [Fact]
    public async Task Delegate_RoundTrip_WorkerChatterNeverReachesBrain_AndTiersAreAccounted()
    {
        var (rt, llm, sent) = Create();
        GameTools(rt);
        rt.Agent.StateHeader = () => "pos 0,64,0 hp 20/20 food 20 held air";

        llm.Tool(ModelTier.Main, "delegate", new { task = "mine 8 cobblestone at the gen", done_when = "8 cobblestone in inventory" }, "d1", "on it")
           .Text(ModelTier.Main, "got your cobble!")
           .Tool(ModelTier.Worker, "mine", new { block = "cobblestone", count = 8 }, "w1")
           .Text(ModelTier.Worker, "ok: mined 8 cobblestone, inv 8");

        var outcome = await rt.Agent.HandleBatchAsync(Chat("poofimafox", "claudechan grab me some cobble"), default);

        Assert.Equal(ModelTier.Main, outcome.Tier);
        Assert.Equal(["on it", "got your cobble!"], sent);
        var brainCalls = llm.Of(ModelTier.Main).ToList();
        Assert.Equal(2, brainCalls.Count);
        var result = brainCalls[1].Messages[^1].ToolResults.Single();
        Assert.Equal("d1", result.ToolUseId);
        Assert.Equal("ok: mined 8 cobblestone, inv 8", result.Content);
        Assert.False(result.IsError);

        // The brain never sees the worker's tool chatter: not in any request, not in committed history.
        foreach (var r in brainCalls)
        {
            Assert.DoesNotContain(r.Messages.SelectMany(m => m.ToolUses), u => u.Name == "mine");
            Assert.DoesNotContain(r.Messages.SelectMany(m => m.ToolResults), t => t.ToolUseId == "w1");
            Assert.Contains(r.Tools, t => t.Name == "delegate");
            Assert.DoesNotContain(r.Tools, t => t.Name == "mine");
        }
        var ep = Assert.Single(rt.Context.Episodes);
        Assert.DoesNotContain(ep.Messages.SelectMany(m => m.ToolUses), u => u.Name == "mine");

        // Worker: own prefix (worker system prompt + worker tools), task in its first message.
        var workerCalls = llm.Of(ModelTier.Worker).ToList();
        Assert.Equal(2, workerCalls.Count);
        Assert.Contains("hands of a Minecraft", workerCalls[0].System[0].Text);
        Assert.True(workerCalls[0].System[0].Cache);
        Assert.Contains(workerCalls[0].Tools, t => t.Name == "mine");
        Assert.DoesNotContain(workerCalls[0].Tools, t => t.Name == "delegate");
        Assert.Contains("task: mine 8 cobblestone", workerCalls[0].Messages[0].Text);
        Assert.Contains("done when: 8 cobblestone", workerCalls[0].Messages[0].Text);
        Assert.Contains("pos 0,64,0", workerCalls[0].Messages[0].Text);
        Assert.Equal("w1", workerCalls[1].Messages[^1].ToolResults.Single().ToolUseId);

        // Per-tier accounting.
        var today = rt.Usage.Today;
        Assert.Equal(2, today.Tier("opus").Calls);
        Assert.Equal(2, today.Tier("worker").Calls);
        Assert.Equal(0, today.Tier("memory").Calls);
        Assert.True(today.Tier("opus").Usd > today.Tier("worker").Usd); // same tokens, Opus prices higher
        Assert.Equal(today.Usd, today.Tiers.Values.Sum(t => t.Usd));
        var summary = rt.Usage.Summary();
        Assert.Contains("opus $", summary);
        Assert.Contains("worker $", summary);
    }

    [Fact]
    public async Task Delegate_NeedsInput_InBackground_WakesBrainWithTaskFailed()
    {
        var (rt, llm, _) = Create(o => o.InlineToolWaitSeconds = 0);
        GameTools(rt);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        llm.Tool(ModelTier.Main, "delegate", new { task = "put the logs away", done_when = "no logs in inventory" }, "d1")
           .Text(ModelTier.Main, "")
           .OnAsync(ModelTier.Worker, async r =>
           {
               await gate.Task;
               return llm.Resp(r, StopReasons.EndTurn, new LlmText("needs_input: two chests nearby (wood_chest, main_chest), which one?"));
           });

        await rt.Agent.HandleBatchAsync(Chat("kiwi", "claudechan put the logs away"), default);
        var inline = llm.Of(ModelTier.Main).ElementAt(1).Messages[^1].ToolResults.Single();
        Assert.Contains("background", inline.Content);
        Assert.True(rt.Activities.IsBusy);

        gate.SetResult();
        var batch = await rt.Events.WaitBatchAsync(new CancellationTokenSource(5000).Token);
        var ev = Assert.Single(batch.Events);
        Assert.Equal(EventKind.TaskFailed, ev.Kind);
        Assert.True(ev.Wakes);
        Assert.Contains("needs_input: two chests nearby", ev.Text);
        Assert.Contains("[task failed]", ev.Format());
    }

    [Fact]
    public async Task Delegate_IsCancelledByStop_AndByInterrupts()
    {
        var (rt, llm, _) = Create(o => o.InlineToolWaitSeconds = 0);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        GameTools(rt, async (_, ct) =>
        {
            started.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { cancelled.TrySetResult(); throw; }
            return ToolResult.Success("never");
        });
        var workerDone = 0;
        rt.Worker!.Finished += _ => Interlocked.Increment(ref workerDone);
        llm.Fallback = r => r.Tier == ModelTier.Worker
            ? llm.Resp(r, StopReasons.ToolUse, new LlmToolUse("w" + Guid.NewGuid().ToString("N")[..6], "mine", JsonSerializer.SerializeToElement(new { block = "cobblestone" })))
            : llm.Resp(r, StopReasons.EndTurn, new LlmText(""));
        llm.Tool(ModelTier.Main, "delegate", new { task = "mine 64 cobble", done_when = "64 cobble" }, "d1");

        await rt.Agent.HandleBatchAsync(Chat("poofimafox", "claudechan mine a stack of cobble"), default);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("delegate", rt.Activities.Describe());

        var stop = await rt.Tools.ExecuteAsync("stop", ToolSchema.Empty, default);
        Assert.Contains("stopped delegate", stop.Text);
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitUntil(() => !rt.Activities.IsBusy);
        Assert.Equal(0, workerDone); // cancelled: no summary produced, and no wake event for a model-initiated stop
        Assert.Null(rt.Events.TryTakeUrgent(EventPriority.Background));

        // An interrupt (danger) cancels a running delegate too.
        started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tool = rt.Tools.TryGet("delegate", out var d) ? d : throw new InvalidOperationException();
        var run = rt.Activities.RunAsync([(tool, JsonSerializer.SerializeToElement(new { task = "mine", done_when = "x" }))], TimeSpan.FromSeconds(10), default);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        rt.Events.Post(GameEvent.LowHealth(4));
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var r = (await run.WaitAsync(TimeSpan.FromSeconds(5)))[0];
        Assert.False(r.Ok);
        Assert.Contains("cancelled", r.Text);
    }

    private static async Task WaitUntil(Func<bool> cond)
    {
        for (int i = 0; i < 200 && !cond(); i++) await Task.Delay(10);
        Assert.True(cond());
    }

    [Fact]
    public async Task Worker_RespectsRoundBudget_AndWrapsUpWithOneSummary()
    {
        var (rt, llm, _) = Create(o => o.Worker.MaxRounds = 3);
        GameTools(rt);
        int n = 0;
        llm.Fallback = r => r.Messages[^1].Text.Contains("[max rounds]")
            ? llm.Resp(r, StopReasons.EndTurn, new LlmText("failed: mined 24 of 64 cobblestone, gen too slow"))
            : llm.Resp(r, StopReasons.ToolUse, new LlmToolUse($"w{n++}", "mine", JsonSerializer.SerializeToElement(new { block = "cobblestone" })));
        var o = await rt.Worker!.RunAsync("mine 64 cobble", "64 cobble", null, default);
        Assert.False(o.Ok);
        Assert.Equal("max rounds", o.StopReason);
        Assert.Equal(4, o.Calls);         // 3 rounds + 1 wrap-up
        Assert.Equal(3, o.ToolCalls);
        Assert.Equal("failed: mined 24 of 64 cobblestone, gen too slow", o.Summary);
        Assert.True(o.Summary.Length <= rt.Options.Worker.SummaryChars);

        // A long summary is cut to the cap; an unprefixed one gets a prefix.
        llm.Text(ModelTier.Worker, new string('y', 1000));
        var big = await rt.Worker.RunAsync("x", null, null, default);
        Assert.True(big.Summary.Length <= rt.Options.Worker.SummaryChars);
        Assert.StartsWith("ok: ", big.Summary);
    }

    [Fact]
    public async Task Worker_StopsAtCostCap()
    {
        var (rt, llm, _) = Create(o => o.Worker.MaxCostUsd = 0.0005m);
        GameTools(rt);
        llm.UsagePerCall = new LlmUsage(100, 100, 0, 0); // ≈ $0.0012 per Sonnet call
        int n = 0;
        llm.Fallback = r => r.Messages[^1].Text.Contains("[cost cap]")
            ? llm.Resp(r, StopReasons.EndTurn, new LlmText("failed: stopped early (cost)"))
            : llm.Resp(r, StopReasons.ToolUse, new LlmToolUse($"w{n++}", "mine", JsonSerializer.SerializeToElement(new { block = "dirt" })));
        var o = await rt.Worker!.RunAsync("mine", null, null, default);
        Assert.Equal("cost cap", o.StopReason);
        Assert.True(o.Calls <= 3);
    }

    [Fact]
    public void Budget_FallbackOrder_Normal_Economy_CheapOnly_Paused()
    {
        var t = new UsageTracker(new BudgetOptions { DailyCapUsd = 1m, MonthlyCapUsd = 100m, WorkerBrainFraction = 0.6, CheapOnlyFraction = 0.8 }, path: null);
        var modes = new List<BudgetMode> { t.Mode };
        void Spend(decimal usd) => t.Record("claude-opus-5-5", new LlmUsage(0, (long)(usd / 20m * 1_000_000m), 0, 0));
        Spend(0.5m); modes.Add(t.Mode);   // $0.50
        Spend(0.15m); modes.Add(t.Mode);  // $0.65
        Spend(0.20m); modes.Add(t.Mode);  // $0.85
        Spend(0.20m); modes.Add(t.Mode);  // $1.05
        Assert.Equal([BudgetMode.Normal, BudgetMode.Normal, BudgetMode.Economy, BudgetMode.CheapOnly, BudgetMode.Paused], modes);
    }

    [Fact]
    public async Task Budget_Economy_RunsBrainOnWorkerModel_CheapOnly_DropsToHaikuWithoutDelegate()
    {
        var (rt, llm, _) = Create(o => { o.Budget.DailyCapUsd = 1m; o.Budget.MonthlyCapUsd = 100m; o.Budget.WorkerBrainFraction = 0.6; o.Budget.CheapOnlyFraction = 0.8; });
        GameTools(rt);
        rt.Usage.Record("claude-opus-5-5", new LlmUsage(0, 32_500, 0, 0)); // $0.65 → economy
        Assert.Equal(BudgetMode.Economy, rt.Usage.Mode);
        llm.Text(ModelTier.Main, "hey!");
        await rt.Agent.HandleBatchAsync(Chat("kiwi", "claudechan hi"), default);
        var req = llm.Requests.Single();
        Assert.Equal(ModelTier.Main, req.Tier);
        Assert.Equal("claude-sonnet-5-5", req.Model);
        Assert.Contains(req.Tools, t => t.Name == "delegate");     // same brain tool set
        Assert.Equal(1, rt.Usage.Today.Tier("worker").Calls);      // accounted by the model that answered

        rt.Usage.Record("claude-opus-5-5", new LlmUsage(0, 10_000, 0, 0)); // $0.85 → cheap only
        Assert.Equal(BudgetMode.CheapOnly, rt.Usage.Mode);
        llm.Text(ModelTier.Cheap, "saving energy rn");
        await rt.Agent.HandleBatchAsync(Chat("kiwi", "claudechan mine cobble"), default);
        var cheap = llm.Requests[^1];
        Assert.Equal(ModelTier.Cheap, cheap.Tier);
        Assert.DoesNotContain(cheap.Tools, t => t.Name is "delegate" or "mine" or "goto");
        // A delegated task refuses to start in cheap-only mode.
        var o = await rt.Worker!.RunAsync("mine", null, null, default);
        Assert.False(o.Ok);
        Assert.Contains("budget", o.Summary);
    }

    [Fact]
    public async Task ChatDefaultsToOpus_CheapFirstIsOptIn()
    {
        var (rt, llm, _) = Create();
        llm.Text(ModelTier.Main, "heyy");
        await rt.Agent.HandleBatchAsync(Chat("kiwi", "claudechan how's it going"), default);
        Assert.Equal(ModelTier.Main, llm.Requests.Single().Tier);
        Assert.False(new TriageOptions().CheapFirstForChat);
    }

    [Fact]
    public async Task HardCap_NeverExceeded_UnderAdversarialGrowth()
    {
        var (rt, llm, _) = Create(o => { o.MaxToolRounds = 8; o.ToolResultCaps["spam"] = 60_000; });
        rt.Tools.Register("spam", "Spam.", ToolSchema.Empty, (_, _) => Task.FromResult(ToolResult.Success(new string('z', 60_000))));
        // Pre-existing history far over the cap (as if compaction kept failing / a huge episode slipped in).
        for (int i = 0; i < 3; i++)
        {
            var e = new Episode(ModelTier.Main, DateTimeOffset.UtcNow);
            e.Messages.Add(LlmMessage.User("[chat] Bob: " + new string('h', 200_000)));
            e.Messages.Add(LlmMessage.Assistant("ok"));
            rt.Context.Commit(e);
        }
        Assert.True(rt.Context.EstimateTokens() > 150_000);
        // Every brain call asks for 30 huge tool results; the batch itself is huge too.
        int id = 0;
        llm.Fallback = r => r.Purpose == "compact"
            ? llm.Resp(r, StopReasons.EndTurn, new LlmText("journal"))
            : llm.Resp(r, StopReasons.ToolUse, Enumerable.Range(0, 30)
                .Select(_ => (LlmBlock)new LlmToolUse($"s{id++}", "spam", ToolSchema.Empty)).ToArray());
        var batch = new EventBatch(Enumerable.Range(0, 40).Select(i => GameEvent.Chat("Bob", $"claudechan {i} " + new string('q', 5_000))).ToList());

        for (int turn = 0; turn < 2; turn++) await rt.Agent.HandleBatchAsync(batch, default);

        var brain = llm.Of(ModelTier.Main).ToList();
        Assert.True(brain.Count >= 8);
        foreach (var r in brain)
            Assert.True(RequestTokens(r, llm.Options) <= rt.Options.Context.MaxTokens, $"request ≈{RequestTokens(r, llm.Options)} tokens");
        Assert.True(rt.Context.EstimateTokens() <= rt.Options.Context.MaxTokens);
        Assert.True(rt.Agent.LastRequestTokens <= rt.Options.Context.MaxTokens);
    }

    [Fact]
    public void ContextShrink_And_EnforceHardCap_AreBounded()
    {
        var msgs = new List<LlmMessage>
        {
            LlmMessage.User(new string('a', 100_000)),
            LlmMessage.Assistant(new LlmText("x"), new LlmToolUse("t1", "look", ToolSchema.Empty)),
            LlmMessage.User(new LlmToolResult("t1", new string('b', 50_000))),
        };
        Assert.True(ContextManager.Shrink(msgs, 2_000) <= 2_000);
        Assert.Equal("t1", msgs[2].ToolResults.Single().ToolUseId); // pairing kept

        var ctx = new ContextManager(new ContextOptions());
        for (int i = 0; i < 5; i++)
        {
            var e = new Episode(ModelTier.Main, DateTimeOffset.UtcNow);
            e.Messages.Add(LlmMessage.User("[chat] Bob: " + new string('h', 20_000)));
            e.Messages.Add(LlmMessage.Assistant("ok"));
            ctx.Commit(e);
        }
        Assert.True(ctx.EnforceHardCap(3_000, ModelTier.Main) <= 3_000);
        Assert.True(ctx.EnforceHardCap(3_000, ModelTier.Cheap) <= 3_000);
    }

    [Fact]
    public async Task AskMemory_RunsCheapRetrievalLoop_ReturnsBoundedAnswer()
    {
        var (rt, llm, _) = Create();
        rt.Memory.SetWaypoint("cobble_gen", 4, 64, -2, "the main generator");
        llm.Tool(ModelTier.Cheap, "recall", new { query = "cobble generator" }, "m1")
           .On(ModelTier.Cheap, r =>
           {
               // The lookup result reached the memory loop...
               Assert.Contains("cobble_gen", r.Messages[^1].ToolResults.Single().Content);
               return llm.Resp(r, StopReasons.EndTurn, new LlmText("cobble_gen waypoint at 4,64,-2 (#1). " + new string('x', 900)));
           });

        var res = await rt.Tools.ExecuteAsync("ask_memory", JsonSerializer.SerializeToElement(new { question = "where is the cobble gen?" }), default);

        Assert.True(res.Ok);
        Assert.StartsWith("cobble_gen waypoint at 4,64,-2", res.Text);
        Assert.True(res.Text.Length <= rt.Options.AskMemory.AnswerChars);
        var calls = llm.Of(ModelTier.Cheap).ToList();
        Assert.Equal(2, calls.Count);
        Assert.All(calls, c => Assert.Equal("ask_memory", c.Purpose));
        Assert.Equal(["recall", "where_is", "recall_detail"], calls[0].Tools.Select(t => t.Name));
        Assert.Equal(2, rt.Usage.Today.Tier("memory").Calls);
        Assert.Equal(0, rt.Usage.Today.Tier("opus").Calls);
    }

    [Fact]
    public async Task AskMemory_GivesUpAfterMaxRounds_StillBounded()
    {
        var (rt, llm, _) = Create(o => o.AskMemory.MaxRounds = 2);
        int n = 0;
        llm.Fallback = r => llm.Resp(r, StopReasons.ToolUse, new LlmToolUse($"m{n++}", "recall", JsonSerializer.SerializeToElement(new { query = "melons" })));
        var res = await rt.Tools.ExecuteAsync("ask_memory", JsonSerializer.SerializeToElement(new { question = "melons?" }), default);
        Assert.Equal(2, llm.Requests.Count);
        Assert.True(res.Text.Length <= rt.Options.AskMemory.AnswerChars);
        Assert.Contains("last round", llm.Requests[1].Messages[^1].Text);
    }

    [Fact]
    public async Task AskPlayer_RefusesNonQuestions_AndIsBrainOnly()
    {
        var (rt, _, sent) = Create();
        var res = await rt.Tools.ExecuteAsync("ask_player", JsonSerializer.SerializeToElement(new { question = "sure, the chest is by the tree" }), default);
        Assert.False(res.Ok);
        Assert.Equal(HumanTools.NotAQuestion, res.Text);
        Assert.Empty(sent); // nothing was said, and no reply waiter swallowed the next message
        rt.Tools.TryGet("ask_player", out var tool);
        Assert.Equal(ToolTiers.Main, rt.Tools.TiersOf(tool));
        Assert.Contains("real question", tool.Description);
        Assert.Contains("default", tool.Description);
        Assert.Contains("plain text", tool.Description);
        Assert.Contains("ask_player is only for a real question", rt.Agent.SystemPrompt[0].Text);

        // A real question still works: sent, then the reply comes back as the result.
        var ask = rt.Tools.ExecuteAsync("ask_player", JsonSerializer.SerializeToElement(new { question = "which chest?", name = "kiwi", timeout = 10 }), default);
        await WaitUntil(() => { lock (sent) return sent.Count > 0; });
        rt.Chat.OnServerChat("<kiwi> the left one");
        var reply = await ask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(reply.Ok);
        Assert.EndsWith("the left one", reply.Text);
    }

    [Fact]
    public async Task PerToolResultCaps_OverrideTheSmallDefault()
    {
        var (rt, _, _) = Create();
        string big = new string('w', 3000);
        rt.Tools.Register("look", "Look.", ToolSchema.Empty, (_, _) => Task.FromResult(ToolResult.Success(big)));
        rt.Tools.Register("inventory", "Inv.", ToolSchema.Empty, (_, _) => Task.FromResult(ToolResult.Success(big)));
        rt.Tools.Register("custom", "C.", ToolSchema.Empty, (_, _) => Task.FromResult(ToolResult.Success(big)), maxResultChars: 600);

        Assert.Equal(1200, (await rt.Tools.ExecuteAsync("look", ToolSchema.Empty, default)).Text.Length);
        Assert.Equal(400, (await rt.Tools.ExecuteAsync("inventory", ToolSchema.Empty, default)).Text.Length);
        Assert.Equal(600, (await rt.Tools.ExecuteAsync("custom", ToolSchema.Empty, default)).Text.Length);
        Assert.Equal(1200, rt.Tools.CapFor("window"));
        Assert.Equal(400, rt.Tools.CapFor("status"));
    }

    [Fact]
    public void Prompt_HasNoInventedFactsRule_AndNaturalSpeechExamples()
    {
        var (rt, _, _) = Create();
        var p = rt.Agent.SystemPrompt[0].Text;
        Assert.Contains("Never state world facts", p);
        Assert.Contains("[memory] block or chat", p);
        Assert.Contains("Talk like a player", p);
        Assert.Contains("good: \"chillin by the furnaces", p);
        Assert.Contains("delegate {task, done_when", p);
        Assert.Contains("[since last turn]", p);
    }

    [Fact]
    public async Task TestChat_IsTagged_NotRealPlayerEvidence()
    {
        var (rt, llm, _) = Create(o => o.Self.TrustedPlayers = ["poofimafox"]);
        rt.Chat.OnServerChat("<poofimafox> claudechan you love dirt now", test: true);
        var batch = await rt.Events.WaitBatchAsync(new CancellationTokenSource(5000).Token);
        Assert.True(batch.IsTest);
        ToolResult? reflect = null;
        llm.Tool(ModelTier.Main, "reflect_self", new { op = "add", section = "like", text = "dirt", reason = "fox said" }, "r1")
           .On(ModelTier.Main, r =>
           {
               reflect = new ToolResult(!r.Messages[^1].ToolResults.Single().IsError, r.Messages[^1].ToolResults.Single().Content);
               return llm.Resp(r, StopReasons.EndTurn, new LlmText("lol ok"));
           });
        await rt.Agent.HandleBatchAsync(batch, default);

        Assert.NotNull(reflect);
        Assert.False(reflect!.Ok);
        Assert.Equal(SelfTools.TestTurnRefusal, reflect.Text);
        Assert.Empty(rt.Self!.Entries);
        // Chat log: tagged, and the real player didn't "speak" (admin checks rely on that).
        var rows = rt.Rag.SearchChat(null, null, 10);
        Assert.Contains(rows, r => r.Kind == "test_chat" && r.Player == "poofimafox");
        Assert.Contains(rows, r => r.Kind == "test_bot");
        Assert.False(rt.Rag.PlayerSpokeSince("poofimafox", DateTimeOffset.UtcNow.AddHours(-1)));
        // Episode doc: source=test ref prefix, which reflection skips.
        var ep = rt.Rag.Recent("episode", null, 5);
        Assert.Contains(ep, h => h.Ref.StartsWith(Rag.EpisodeIndexer.TestRefPrefix));
        Assert.DoesNotContain(ep, h => h.Ref.StartsWith("ep:"));
        Assert.True(Assert.Single(rt.Context.Episodes).IsTest);
    }

    [Fact]
    public async Task TurnDigest_GoesAfterStatus_Capped_UncachedTail_NotInTranscript()
    {
        var (rt, llm, _) = Create(o => o.TurnDigestMaxChars = 110);
        rt.Agent.StateHeader = () => "pos 1,64,1 hp 20";
        DateTimeOffset? since = null;
        rt.Agent.TurnDigest = s => { since = s; return "kiwi probably built a cobble gen by the tree\nFox mined 20 cobble; " + new string('x', 200); };
        llm.Text(ModelTier.Main, "ooh robin's been busy");
        await rt.Agent.HandleBatchAsync(Chat("kiwi", "claudechan hi"), default);

        var lines = llm.Requests.Single().Messages[^1].Text.Split('\n');
        Assert.StartsWith("pos 1,64,1", lines[0]);
        Assert.StartsWith("[since last turn] kiwi probably built a cobble gen by the tree; Fox mined 20 cobble", lines[1]);
        Assert.True(lines[1].Length <= 110 && lines[1].EndsWith("..."));
        Assert.NotNull(since);
        // Not part of the cached prefix, and not copied into history renderings.
        Assert.DoesNotContain("since last turn", string.Concat(rt.Agent.SystemPrompt.Select(s => s.Text)).Replace("\"[since last turn]\"", ""));
        Assert.DoesNotContain("since last turn", ContextManager.Flatten(rt.Context.Episodes.Single(), 200));
    }

    [Fact]
    public void ActivityEvent_Wakes_WithoutInterrupting()
    {
        var e = GameEvent.Activity("kiwi probably built a cobble gen", "kiwi");
        Assert.True(e.Wakes);
        Assert.True(e.Priority < EventPriority.High);
        Assert.Equal("[noticed] kiwi: kiwi probably built a cobble gen", e.Format());
        Assert.True(new EventBatch([e]).IsConversational);
    }

    [Fact]
    public void Landmarks_PhraseRelationally()
    {
        Assert.Equal("E", Landmarks.Compass(5, 0));
        Assert.Equal("N", Landmarks.Compass(0, -5));
        Assert.Equal("SW", Landmarks.Compass(-3, 3));
        Assert.Equal("here", Landmarks.Offset(0, 0, 0.5, 0.5));
        Assert.Equal("3 W", Landmarks.Offset(10, 10, 7, 10));

        var (rt, _, _) = Create();
        rt.Memory.SetWaypoint("cobble_gen", 10, 64, 0);
        Assert.Equal("2 E of cobble_gen", Landmarks.Near(rt.Memory, null, 12.5, 64, 0.5));
        Assert.Equal("at cobble_gen", Landmarks.Near(rt.Memory, null, 10.5, 64, 0.5));
        Assert.Null(Landmarks.Near(rt.Memory, null, 200, 64, 200));
    }
}
