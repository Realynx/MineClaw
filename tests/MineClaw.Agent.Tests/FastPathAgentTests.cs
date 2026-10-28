using System.Text.Json;
using MineClaw.Agent.Events;
using MineClaw.Agent.Llm;
using MineClaw.Agent.Tools;

namespace MineClaw.Agent.Tests;

/// <summary>
/// The brain's fast path: do (single-step macros + then chains), non-blocking moves, streaming execution, the body lock,
/// simple-turn effort, the [here] pre-observation and latency telemetry.
/// </summary>
public class FastPathAgentTests
{
    private sealed class World
    {
        public readonly List<string> Ran = new();
        public TaskCompletionSource<ToolResult> Walk = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource WalkStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool GatedWalk;
    }

    private static (AgentRuntime Rt, FakeLlm Llm, World W) Create(Action<AgentOptions>? configure = null)
    {
        var w = new World();
        var (rt, llm, _) = TestRuntime.Create(o =>
        {
            o.Brainstem.MoveInlineWaitMs = 60;
            o.InlineToolWaitSeconds = 5;
            configure?.Invoke(o);
        });
        rt.Tools.Register("goto", "walk", ToolSchema.Create().Str("to").Num("dist", required: false).Build(), async (i, ct) =>
        {
            lock (w.Ran) w.Ran.Add("goto " + i.Str("to"));
            w.WalkStarted.TrySetResult();
            if (!w.GatedWalk) return ToolResult.Success("arrived at " + i.Str("to"));
            using var reg = ct.Register(() => w.Walk.TrySetResult(ToolResult.Error("cancelled")));
            return await w.Walk.Task;
        }, longRunning: true);
        rt.Tools.Register("harvest_generator", "harvest", ToolSchema.Create().Str("at").Int("count").Build(), (i, _) =>
        {
            lock (w.Ran) w.Ran.Add("harvest " + i.Str("at") + " " + i.Int("count"));
            return Task.FromResult(ToolResult.Success("dug " + i.Int("count")));
        }, longRunning: true);
        rt.Tools.Register("mine", "mine", ToolSchema.Create().Str("block").Build(), (i, _) =>
        {
            lock (w.Ran) w.Ran.Add("mine " + i.Str("block"));
            return Task.FromResult(ToolResult.Success("mined"));
        }, longRunning: true);
        rt.Tools.Register("look", "look", ToolSchema.Empty, (_, _) =>
        {
            lock (w.Ran) w.Ran.Add("look");
            return Task.FromResult(ToolResult.Success("you: by the gen"));
        });
        rt.ApplyBrainSplit();
        return (rt, llm, w);
    }

    private static EventBatch Chat(AgentRuntime rt, string text, string player = "fox")
    {
        rt.Events.Post(GameEvent.Chat(player, text));
        return rt.Events.TryTakeBatch()!;
    }

    private static LlmToolUse Use(string name, object input, string id = "tu_1") => new(id, name, JsonSerializer.SerializeToElement(input));

    private static string ToolResultText(LlmRequest r, string id) =>
        r.Messages.SelectMany(m => m.Content).OfType<LlmToolResult>().Last(t => t.ToolUseId == id).Content;

    [Fact]
    public async Task Do_runs_a_macro_directly_and_then_chains_after_arrival()
    {
        var (rt, llm, w) = Create();
        Assert.Contains(rt.Tools.Definitions(ModelTier.Main), d => d.Name == "do");
        Assert.DoesNotContain(rt.Tools.Definitions(ModelTier.Main), d => d.Name == "goto");
        llm.ThenTool("do", new { action = "goto", args = new { to = "gen" }, then = new[] { new { action = "harvest_generator", args = new { at = "gen", count = 5 } } } })
           .ThenText("(silent)");
        await rt.Agent.HandleBatchAsync(Chat(rt, "mine 5 cobble at the gen"), default);
        await BrainstemTests.Until(() => w.Ran.Count == 2);
        Assert.Equal(["goto gen", "harvest gen 5"], w.Ran);
        var text = ToolResultText(llm.Requests[1], "tu_1");
        Assert.Contains("then", text);
        Assert.Equal(2, llm.Requests.Count); // no delegate round, no worker
    }

    [Fact]
    public async Task A_brain_goto_returns_at_once_and_read_only_tools_work_while_walking()
    {
        var (rt, llm, w) = Create();
        w.GatedWalk = true;
        llm.ThenTool("do", new { action = "goto", args = new { to = "gen" } })
           .ThenTool("look", new { }, id: "tu_2")
           .ThenTool("do", new { action = "mine", args = new { block = "cobblestone" } }, id: "tu_3")
           .ThenText("on my way!");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await rt.Agent.HandleBatchAsync(Chat(rt, "go mine cobble at the gen"), default);
        Assert.True(sw.ElapsedMilliseconds < 3000, $"turn held {sw.ElapsedMilliseconds} ms by the walk");
        Assert.StartsWith("walking there", ToolResultText(llm.Requests[1], "tu_1"));
        Assert.Equal("you: by the gen", ToolResultText(llm.Requests[2], "tu_2")); // look answered mid-walk
        Assert.StartsWith("queued behind goto", ToolResultText(llm.Requests[3], "tu_3"));
        Assert.DoesNotContain("mine cobblestone", w.Ran);
        w.Walk.SetResult(ToolResult.Success("arrived"));
        await BrainstemTests.Until(() => w.Ran.Contains("mine cobblestone"));
        Assert.Equal(["goto gen", "look", "mine cobblestone"], w.Ran);
    }

    [Fact]
    public async Task A_streamed_movement_starts_before_the_message_ends()
    {
        var (rt, llm, w) = Create();
        bool startedBeforeEnd = false;
        llm.ThenAsync(async (r, ct) =>
        {
            Assert.NotNull(r.OnToolUse);
            var use = Use("do", new { action = "goto", args = new { to = "fox" } });
            r.OnToolUse!(use); // the tool_use block is complete; the model is still writing
            await w.WalkStarted.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
            startedBeforeEnd = true;
            return llm.Resp(r, StopReasons.ToolUse, new LlmText("coming!"), use);
        }).ThenText("(silent)");
        await rt.Agent.HandleBatchAsync(Chat(rt, "come here"), default);
        Assert.True(startedBeforeEnd);
        Assert.Equal(["goto fox"], w.Ran); // started once, not again when the message ended
    }

    [Fact]
    public async Task A_streamed_non_movement_action_waits_for_the_message()
    {
        var (rt, llm, w) = Create();
        llm.ThenAsync((r, _) =>
        {
            var use = Use("do", new { action = "mine", args = new { block = "dirt" } });
            r.OnToolUse!(use);
            Assert.Empty(w.Ran); // mining is not reversible: never started early
            return Task.FromResult(llm.Resp(r, StopReasons.ToolUse, use));
        }).ThenText("(silent)");
        await rt.Agent.HandleBatchAsync(Chat(rt, "mine some dirt"), default);
        Assert.Equal(["mine dirt"], w.Ran);
    }

    [Fact]
    public async Task Body_tools_answer_busy_while_a_script_holds_the_body_and_can_queue()
    {
        var (rt, llm, w) = Create();
        rt.Agent.BodyHolder = () => "script cobble_loop 40%";
        llm.ThenTool("do", new { action = "mine", args = new { block = "dirt" } })
           .ThenTool("look", new { }, id: "tu_2")
           .ThenTool("do", new { action = "mine", args = new { block = "dirt" }, queue = true }, id: "tu_3")
           .ThenText("sure");
        await rt.Agent.HandleBatchAsync(Chat(rt, "mine some dirt"), default);
        Assert.Contains("[body: script cobble_loop 40%]", llm.Requests[0].Messages[^1].Text);
        var busy = ToolResultText(llm.Requests[1], "tu_1");
        Assert.StartsWith("body busy: script cobble_loop 40%", busy);
        Assert.Contains("op=pause", busy);
        Assert.Contains("queue:true", busy);
        Assert.Equal("you: by the gen", ToolResultText(llm.Requests[2], "tu_2")); // free tools still run
        await BrainstemTests.Until(() => w.Ran.Contains("mine dirt"));
    }

    [Fact]
    public async Task Simple_chat_runs_at_low_effort_and_planning_keeps_the_configured_one()
    {
        var (rt, llm, _) = Create();
        llm.ThenText("coming").ThenText("hmm ok");
        await rt.Agent.HandleBatchAsync(Chat(rt, "come here"), default);
        await rt.Agent.HandleBatchAsync(Chat(rt, "yes we're extending this way 2 rows just this side of the doors"), default);
        Assert.Equal("low", llm.Requests[0].Effort);
        Assert.Null(llm.Requests[1].Effort);
    }

    [Fact]
    public async Task Here_block_rides_chat_turns_deduped_and_built_fresh_every_turn()
    {
        var (rt, llm, _) = Create();
        string inv = "cobblestone x64";
        rt.Agent.StateHeader = () => "pos 1,64,2 hp 20/20 held stone pickaxe";
        rt.Agent.HereBlock = _ => $"[here] you: at cobble_gen | inv: {inv} | pos 1,64,2 | near: chest 1 E";
        llm.ThenText("ok").ThenText("ok").ThenText("ok");
        await rt.Agent.HandleBatchAsync(Chat(rt, "what do you have"), default);
        var first = llm.Requests[0].Messages[^1].Text;
        Assert.Contains("[here] you: at cobble_gen | inv: cobblestone x64 | near: chest 1 E", first);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(first, "pos 1,64,2")); // deduped against the header

        inv = "dirt x3"; // the world changed: the next turn must not see the old inventory
        await rt.Agent.HandleBatchAsync(Chat(rt, "and now?"), default);
        var second = llm.Requests[1].Messages[^1].Text;
        Assert.Contains("inv: dirt x3", second);
        Assert.DoesNotContain("cobblestone x64", second);

        // Not a chat turn: no [here].
        await rt.Agent.HandleBatchAsync(new EventBatch([GameEvent.TaskDone("#1 mine: ok")]), default);
        Assert.DoesNotContain("[here]", llm.Requests[2].Messages[^1].Text);
    }

    [Fact]
    public async Task Latency_telemetry_records_reaction_first_action_and_rounds()
    {
        var (rt, llm, _) = Create();
        var records = new List<Telemetry.LatencyRecord>();
        rt.Latency.Recorded += records.Add;
        llm.ThenTool("look", new { }).ThenTool("do", new { action = "goto", args = new { to = "fox" } }, id: "tu_2").ThenText("here!");
        await rt.Agent.HandleBatchAsync(Chat(rt, "come here and help"), default);
        var r = Assert.Single(records);
        Assert.NotNull(r.FirstActionMs);
        Assert.NotNull(r.FirstMoveMs);
        Assert.NotNull(r.ReactionMs);
        Assert.Equal(2, r.RoundsBeforeAction); // look round + the do round completed before the walk started
        Assert.Equal(3, r.Rounds);
        Assert.Contains("goto", r.Tools);
        var s = rt.Latency.Summary();
        Assert.Equal(1, s.Turns);
        Assert.Contains("act ", s.Line());
    }
}
