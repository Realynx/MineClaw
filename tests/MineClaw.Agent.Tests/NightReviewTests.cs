using System.Text.Json;
using MineClaw.Agent.Chat;
using MineClaw.Agent.Events;
using MineClaw.Agent.Llm;
using MineClaw.Agent.Rag;
using MineClaw.Agent.Tools;

namespace MineClaw.Agent.Tests;

/// <summary>Regressions from the 2026-10-27 night log review (docs/reviews/2026-10-27-night-log-review.md, Agent layer).</summary>
public class NightReviewTests
{
    private static JsonElement J(object o) => JsonSerializer.SerializeToElement(o);

    // ---------------------------------------------------------------- B1: the guard over-blocked

    [Fact]
    public void B1_guard_keys_failures_on_intent_never_on_a_shared_coordinate_and_never_blocks_movement()
    {
        var (rt, _, _) = TestRuntime.Create();
        var g = rt.Agent.Guard;
        g.RecordFailure("delegate", J(new { task = "craft a stone pickaxe from cobble in the chest at 14,64,-5", done_when = "pickaxe in inventory" }),
            "failed: no spare stone_pickaxe");
        // A different plan that mentions the same chest: allowed (was refused for 10 minutes).
        Assert.Null(g.Check("delegate", J(new { task = "store 10 oak logs in the chest at 14,64,-5", done_when = "logs stored" })));
        // A walk is never refused from memory, even to that chest (was "goto → not retried: delegate failed here").
        Assert.Null(g.Check("goto", J(new { to = "14,64,-5" })));
        g.RecordFailure("goto", J(new { to = "PoofimaFox" }), "no path to within 3 (no route)");
        Assert.Null(g.Check("goto", J(new { to = "PoofimaFox" })));
        // The same job again is still caught (the point of the memory).
        var again = g.Check("delegate", J(new { task = "craft a stone pickaxe from the cobble in chest 14,64,-5", done_when = "pickaxe in inventory" }));
        Assert.NotNull(again);
        Assert.StartsWith("not retried", again!.Text);
        // A question back is not a failure.
        g.RecordFailure("delegate", J(new { task = "build the wall" }), "needs_input: which side?");
        Assert.Null(g.Check("delegate", J(new { task = "build the wall" })));
    }

    [Fact]
    public void B1_new_player_chat_clears_the_failure_memory()
    {
        var (rt, _, _) = TestRuntime.Create();
        var g = rt.Agent.Guard;
        g.RecordFailure("mine", J(new { block = "cobblestone", at = "6,64,8" }), "nothing to mine");
        Assert.NotNull(g.Check("mine", J(new { block = "cobblestone", at = "6,64,8" })));
        rt.Events.Post(GameEvent.Chat("PoofimaFox", "opened the door for you"));
        Assert.Null(g.Check("mine", J(new { block = "cobblestone", at = "6,64,8" })));
    }

    // ---------------------------------------------------------------- B3: history must stay byte-stable

    [Fact]
    public async Task B3_thinking_blocks_stay_in_history_so_the_next_request_extends_the_cached_prefix()
    {
        var (rt, llm, _) = TestRuntime.Create();
        rt.Tools.Register("look", "Look.", ToolSchema.Empty, (_, _) => Task.FromResult(ToolResult.Success("grass")));
        llm.Then(r => llm.Resp(r, StopReasons.ToolUse, new LlmThinking("", "sig-round-0"), new LlmToolUse("tu_1", "look", J(new { }))))
           .Then(r => llm.Resp(r, StopReasons.EndTurn, new LlmThinking("", "sig-round-1"), new LlmText("ok")))
           .Then(r => llm.Resp(r, StopReasons.EndTurn, new LlmThinking("", "sig-turn-2"), new LlmText("sure")));
        await rt.Agent.HandleBatchAsync(new EventBatch([GameEvent.Chat("Alice", "clawd look around")]), default);
        await rt.Agent.HandleBatchAsync(new EventBatch([GameEvent.Chat("Alice", "clawd thanks")]), default);
        var reqs = llm.Requests.Where(r => r.Tier == ModelTier.Main).ToList();
        Assert.Equal(3, reqs.Count);
        // The last call of turn 1 sent [user, assistant(thinking+tool_use), user(tool_result)]; turn 2 must start with exactly that.
        var prev = reqs[1].Messages;
        Assert.Equal(Snapshot.Of(prev), Snapshot.Of(reqs[2].Messages.Take(prev.Count)));
        Assert.Contains(reqs[2].Messages.SelectMany(m => m.Content), b => b is LlmThinking t && t.Signature == "sig-round-0");
    }

    // ---------------------------------------------------------------- B4: injected chat is consumed by its turn

    [Fact]
    public async Task B4_chat_injected_mid_turn_is_consumed_and_never_triggers_a_second_turn()
    {
        var (rt, llm, _) = TestRuntime.Create();
        rt.Tools.Register("look", "Look.", ToolSchema.Empty, (_, _) =>
        {
            rt.Events.Post(GameEvent.Chat("PoofimaFox", "also bring me 40 cobble blocks"));
            return Task.FromResult(ToolResult.Success("grass"));
        });
        llm.ThenTool("look", new { }).ThenText("Sure thing foxy~ 40 cobble coming with the slabs");
        rt.Events.Post(GameEvent.Chat("PoofimaFox", "clawd make me 30 slabs"));
        var batch = rt.Events.TryTakeBatch()!;
        await rt.Agent.HandleBatchAsync(batch, default);
        Assert.Contains("also bring me 40 cobble", llm.Requests[1].Messages[^1].Content.OfType<LlmText>().Last().Text);
        Assert.Null(rt.Events.TryTakeBatch()); // nothing left to wake a second turn

        // Even if the same events were delivered again, the turn is skipped without a model call.
        int calls = llm.Requests.Count;
        var injected = rt.Context.Episodes[^1].Events.Where(e => e.Wakes).ToList();
        var again = await rt.Agent.HandleBatchAsync(new EventBatch(injected), default);
        Assert.Equal("already handled", again.Skipped);
        Assert.Equal(calls, llm.Requests.Count);
    }

    // ---------------------------------------------------------------- B5: one chat output per addressed message

    [Fact]
    public async Task B5_a_paraphrase_after_a_tool_round_is_not_sent_again()
    {
        var (rt, llm, sent) = TestRuntime.Create();
        rt.Tools.Register("remember_note", "Note.", ToolSchema.Empty, (_, _) => Task.FromResult(ToolResult.Success("ok")));
        llm.ThenTool("remember_note", new { }, text: "Okie foxy, I'll make you a door too~ and bring it with the rest uwu")
           .ThenText("I'll make you a door too, foxy~ it'll come with the slabs and cobble uwu");
        await rt.Agent.HandleBatchAsync(new EventBatch([GameEvent.Chat("PoofimaFox", "clawd can you make me a wooden door too?")]), default);
        Assert.Single(sent);
        Assert.StartsWith("Okie foxy", sent[0]);
    }

    [Fact]
    public async Task B5_a_mid_turn_line_waits_for_the_end_and_is_said_only_if_nothing_newer_was()
    {
        var (rt, llm, sent) = TestRuntime.Create();
        rt.Tools.Register("look", "Look.", ToolSchema.Empty, (_, _) => Task.FromResult(ToolResult.Success("grass")));
        llm.ThenTool("look", new { }, text: "on it!")
           .ThenTool("look", new { }, text: "the ledge is 2 blocks west of the door", id: "tu_2")
           .ThenText("(silent)");
        await rt.Agent.HandleBatchAsync(new EventBatch([GameEvent.Chat("PoofimaFox", "clawd where's the ledge")]), default);
        Assert.Equal(["on it!", "the ledge is 2 blocks west of the door"], sent);
    }

    [Fact]
    public void B5_speech_dedupe_keeps_new_information()
    {
        Assert.Equal("", SpeechDedupe.DropRepeats("I'll make you a door too, foxy~ it'll come with the slabs and cobble uwu",
            ["Okie foxy, I'll make you a door too~ and bring it with the rest uwu"], out _));
        Assert.Equal("done! 40 cobble at your feet", SpeechDedupe.DropRepeats("done! 40 cobble at your feet", ["on it, bringing 40 cobble"], out _));
    }

    // ---------------------------------------------------------------- B8: no 20 s inline hold on long jobs

    [Fact]
    public async Task B8_a_long_job_answers_running_in_background_at_once()
    {
        var (rt, llm, _) = TestRuntime.Create(o => o.InlineToolWaitSeconds = 20);
        var release = new TaskCompletionSource();
        rt.Tools.Register("chop_tree", "Chop.", ToolSchema.Empty, async (_, ct) => { await release.Task.WaitAsync(ct); return ToolResult.Success("chopped"); }, longRunning: true);
        rt.ApplyBrainSplit();
        llm.ThenTool("do", new { action = "chop_tree" }).ThenText("chopping!");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await rt.Agent.HandleBatchAsync(new EventBatch([GameEvent.Chat("PoofimaFox", "clawd chop that tree")]), default);
        Assert.True(sw.ElapsedMilliseconds < 5000, $"held {sw.ElapsedMilliseconds} ms");
        Assert.Contains("running in background", llm.Requests[1].Messages.SelectMany(m => m.Content).OfType<LlmToolResult>().Last().Content);
        release.SetResult();
    }

    // ---------------------------------------------------------------- B9: idle check-ins

    [Fact]
    public void B9_idle_check_ins_run_on_the_cheap_tier_and_never_over_a_script()
    {
        var triage = new Triage(new TriageOptions());
        var d = triage.Choose(new EventBatch([GameEvent.Idle()]), Budget.BudgetMode.Normal);
        Assert.Equal(ModelTier.Cheap, d.Tier);
        Assert.True(d.CanEscalate);
        Assert.Equal(ModelTier.Main, triage.Choose(new EventBatch([GameEvent.Idle("nothing to do for 5m: pick a job")]), Budget.BudgetMode.Normal).Tier);

        var time = new ManualTime();
        var (rt, _, _) = TestRuntime.Create(o => { o.IdleCheckInMinutes = 1; o.Idle.IdleDefault = false; }, time: time);
        rt.Agent.HumansOnline = () => true;
        rt.Agent.BodyHolder = () => "script cobble_loop 40%";
        time.Advance(TimeSpan.FromMinutes(5));
        Assert.Null(rt.Agent.IdleTick()); // the script is the plan: no check-in
        rt.Agent.BodyHolder = null;
        Assert.Equal("check-in", rt.Agent.IdleTick());
    }

    // ---------------------------------------------------------------- B11: restarts keep continuity

    [Fact]
    public async Task B11_a_restart_rehydrates_the_last_episodes_into_the_journal()
    {
        using var dir = new TempDir();
        void Configure(AgentOptions o) { o.DataDirectory = dir.Path; o.KnowledgeDirectory = null; o.IdleCheckInMinutes = 0; o.Chat.LineDelayMs = 0; }
        var o1 = new AgentOptions(); Configure(o1);
        var llm = new FakeLlm().ThenText("spawned 420 for you, it's on the island");
        using (var rt = AgentRuntime.Create(o1, (_, _) => Task.CompletedTask, llm))
            await rt.Agent.HandleBatchAsync(new EventBatch([GameEvent.Chat("PoofimaFox", "claudechan spawn 420 on the island")]), default);
        await Task.Delay(1100); // the next run has a different run id
        var o2 = new AgentOptions(); Configure(o2);
        using var rt2 = AgentRuntime.Create(o2, (_, _) => Task.CompletedTask, new FakeLlm());
        Assert.Contains("[before restart]", rt2.Context.Journal);
        Assert.Contains("spawn 420", rt2.Context.Journal);
    }
}
