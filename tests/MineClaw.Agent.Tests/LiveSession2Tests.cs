using System.Text.Json;
using MineClaw.Agent.Chat;
using MineClaw.Agent.Events;
using MineClaw.Agent.Human;
using MineClaw.Agent.Llm;
using MineClaw.Agent.Tools;

namespace MineClaw.Agent.Tests;

/// <summary>Fixes from the second live session (Fox teaching by gestures, loose answers, clones in parallel).</summary>
public class LiveSession2Tests
{
    private static JsonElement J(object o) => JsonSerializer.SerializeToElement(o);

    // ---------------------------------------------------------------- 2: natural completions

    [Theory]
    [InlineData("did it")]
    [InlineData("done")]
    [InlineData("there")]
    [InlineData("ok")]
    [InlineData("finished")]
    [InlineData("that's it")]
    [InlineData("like that")]
    [InlineData("see?")]
    [InlineData("here")]
    [InlineData("here is the cell")]
    [InlineData("yep")]
    [InlineData("PoofimaFox: did it")]
    [InlineData("ok i did it")]
    public void Loose_completions_end_a_demo(string reply) => Assert.True(ReplyIntent.IsCompletion(reply), reply);

    [Theory]
    [InlineData("wait")]
    [InlineData("not yet")]
    [InlineData("hold on, one sec")]
    [InlineData("I punched the brick that get's re generated, but you have my pickaxe")]
    [InlineData("I gave it a wooden pickaxe, watch me punch the correct brick to mine for your clone")]
    public void Narration_and_not_yet_do_not(string reply) => Assert.False(ReplyIntent.IsCompletion(reply), reply);

    [Theory]
    [InlineData("here")]
    [InlineData("here is the cell")]
    [InlineData("this spot")]
    [InlineData("ok")]
    [InlineData("i'm here")]
    public void Ask_stand_accepts_loose_here(string reply) => Assert.True(ReplyIntent.IsHere(reply), reply);

    [Fact]
    public void Ask_stand_does_not_take_wait_as_here() => Assert.False(ReplyIntent.IsHere("wait, walking there"));

    private sealed class FakeSession(Func<DateTimeOffset?> last, int count) : IDemonstrationSession
    {
        public Task<IReadOnlyList<DemoAction>> StopAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<DemoAction>>([]);
        public DateTimeOffset? LastActivity => last();
        public int ActionCount => count;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Demo_ends_on_did_it_and_keeps_other_lines_for_the_brain()
    {
        var (rt, _, sent) = TestRuntime.Create();
        var session = new FakeSession(() => DateTimeOffset.UtcNow, 1);
        var run = DemoWait.RunAsync(rt.Chat, session, "PoofimaFox", 30, null, default);
        await Task.Delay(50);
        rt.Chat.OnServerChat("<PoofimaFox> I punched the brick that get's re generated, but you have my pickaxe");
        await Task.Delay(30);
        Assert.False(run.IsCompleted);
        rt.Chat.OnServerChat("<PoofimaFox> did it");
        var o = await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("said done", o.Ended);
        Assert.Contains("you have my pickaxe", DemoWait.SaidSuffix(o));
        Assert.Single(sent); // the tool's own prompt: the brain said nothing this turn
    }

    [Fact]
    public async Task Demo_auto_completes_when_the_teacher_went_idle_then_speaks()
    {
        var (rt, _, _) = TestRuntime.Create();
        var session = new FakeSession(() => DateTimeOffset.UtcNow - TimeSpan.FromSeconds(10), 3);
        var run = DemoWait.RunAsync(rt.Chat, session, "PoofimaFox", 30, null, default);
        await Task.Delay(50);
        rt.Chat.OnServerChat("<PoofimaFox> so yeah that brick regenerates");
        var o = await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("idle then spoke", o.Ended);
    }

    // ---------------------------------------------------------------- 3: one prompt only

    [Fact]
    public async Task Demo_tool_stays_quiet_when_the_brain_already_spoke_this_turn()
    {
        var (rt, _, sent) = TestRuntime.Create();
        rt.Chat.MarkTurnStart();
        await rt.Chat.SayAsync("ooh show me how you mine it foxy, tell me when you're done");
        rt.Chat.NoteModelSpeech();
        var run = DemoWait.RunAsync(rt.Chat, new FakeSession(() => null, 0), "PoofimaFox", 30, null, default);
        await Task.Delay(50);
        rt.Chat.OnServerChat("<PoofimaFox> done");
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(sent); // only the brain's line
    }

    // ---------------------------------------------------------------- 4: thread ownership hook

    [Fact]
    public async Task Reply_owned_by_another_bot_never_completes_our_waiter()
    {
        var (rt, _, _) = TestRuntime.Create();
        rt.Chat.OwnedElsewhere = m => m.Player == "PoofimaFox"; // e.g. clone 67 asked Fox something
        var wait = rt.Chat.WaitForReplyAsync("PoofimaFox", TimeSpan.FromMilliseconds(300), default);
        rt.Chat.OnServerChat("<PoofimaFox> the one next to the lava");
        Assert.Null(await wait);
    }

    [Fact]
    public void Hold_thread_raises_open_and_close_once()
    {
        var (rt, _, _) = TestRuntime.Create();
        var log = new List<string>();
        rt.Chat.ThreadChanged += (p, open, _) => log.Add($"{p}:{open}");
        var h = rt.Chat.HoldThread("PoofimaFox", TimeSpan.FromSeconds(60));
        h.Dispose();
        h.Dispose();
        Assert.Equal(["PoofimaFox:True", "PoofimaFox:False"], log);
    }

    // ---------------------------------------------------------------- 10: relational questions

    [Fact]
    public void Questions_swap_coordinates_for_landmarks_unless_the_player_used_coords()
    {
        Func<int, int, int, string?> lm = (x, y, z) => (x, y, z) == (3, 64, 5) ? "the west cell of the gen, right next to the lava" : null;
        Assert.Equal("Quick check foxy, is the clone's brick the one at the west cell of the gen, right next to the lava? uwu",
            Questions.Humanize("Quick check foxy, is the clone's brick the one at 3,64,5? uwu", ["I want you both working each cell at once :3"], lm));
        // They talk in coordinates themselves: keep ours.
        Assert.Contains("3,64,5", Questions.Humanize("the one at 3,64,5?", ["mine at 3,64,5 please"], lm));
        Assert.Contains("3,64,5", Questions.Humanize("the one at 3,64,5?", ["what are the coords?"], lm));
        // No landmark for a triple: unchanged.
        Assert.Equal("at 9,64,9?", Questions.Humanize("at 9,64,9?", [], lm));
        // needs_input for the brain: coordinates stay, the landmark rides along.
        Assert.Equal("needs_input: is 3,64,5 (the west cell of the gen, right next to the lava) the clone's cell?",
            Questions.Annotate("needs_input: is 3,64,5 the clone's cell?", lm));
    }

    [Fact]
    public async Task Ask_player_uses_the_landmark_in_chat()
    {
        var (rt, _, sent) = TestRuntime.Create();
        rt.Chat.Landmark = (x, y, z) => (x, y, z) == (10, 64, 8) ? "the east cell of the gen" : null;
        var run = rt.Tools.ExecuteAsync("ask_player", J(new { question = "is that what you're doing at 10,64,8?", name = "PoofimaFox" }), default);
        await Task.Delay(80);
        rt.Chat.OnServerChat("<PoofimaFox> yep");
        var r = await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(r.Ok, r.Text);
        Assert.Contains(sent, s => s.Contains("the east cell of the gen") && !s.Contains("10,64,8"));
    }

    // ---------------------------------------------------------------- 16: no meta narration

    [Theory]
    [InlineData("Delegating the craft.")]
    [InlineData("running script")]
    [InlineData("checking memory")]
    [InlineData("calling the worker")]
    [InlineData("using tool")]
    public void Pure_meta_lines_are_dropped(string line)
    {
        Assert.Equal("", MetaSpeech.Strip(line, out var dropped));
        Assert.Single(dropped);
    }

    [Theory]
    [InlineData("running over to you!")]
    [InlineData("ok master, 64 cobble coming up")]
    [InlineData("/me curtsies")]
    public void Real_speech_is_kept(string line) => Assert.Equal(line, MetaSpeech.Strip(line, out _));

    [Fact]
    public async Task Brain_text_that_is_only_meta_never_reaches_chat()
    {
        var (rt, llm, sent) = TestRuntime.Create();
        llm.ThenText("Delegating the craft.");
        await rt.Agent.HandleBatchAsync(new EventBatch([GameEvent.Chat("PoofimaFox", "clawd make me a pickaxe")]), default);
        Assert.Empty(sent);
        llm.ThenText("Delegating the craft. on it foxy uwu");
        await rt.Agent.HandleBatchAsync(new EventBatch([GameEvent.Chat("PoofimaFox", "clawd make me a pickaxe")]), default);
        Assert.Equal(["on it foxy uwu"], sent);
    }

    // ---------------------------------------------------------------- 15: honest corrections

    [Fact]
    public async Task Stop_reports_what_the_worker_really_did_so_the_correction_is_true()
    {
        var options = new AgentOptions { KnowledgeDirectory = null, IdleCheckInMinutes = 0, InlineToolWaitSeconds = 1 };
        options.Chat.Username = "maid_chan_67";
        options.Chat.DisplayName = "maid chan 67";
        options.Chat.Aliases = [];
        options.Chat.LineDelayMs = 0;
        options.Events.CoalesceWindowMs = 10;
        options.Events.MaxBatchDelayMs = 50;
        options.ChatDebounce.QuietMs = 10;
        options.ChatDebounce.MaxMs = 50;
        options.ChatDebounce.ReplyQuietMs = 0;
        var llm = new TieredLlm();
        var sent = new List<string>();
        var rt = AgentRuntime.Create(options, (line, _) => { lock (sent) sent.Add(line); return Task.CompletedTask; }, llm, persist: false);
        var started = new TaskCompletionSource();
        rt.Tools.Register("craft", "Craft.", ToolSchema.Create().Str("item").Build(),
            (_, _) => Task.FromResult(ToolResult.Error("missing 3 planks for wooden_pickaxe")), longRunning: true);
        rt.Tools.Register("mine", "Mine.", ToolSchema.Create().Str("block").Build(), async (_, ct) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return ToolResult.Success("never");
        }, longRunning: true);
        rt.ApplyBrainSplit();

        llm.Tool(ModelTier.Main, "delegate", new { task = "mine 64 cobblestone at the gen's west cell", done_when = "64 cobble" }, "d1")
           .Text(ModelTier.Main, "")
           .Tool(ModelTier.Worker, "craft", new { item = "wooden_pickaxe" }, "w1")
           .Tool(ModelTier.Worker, "mine", new { block = "log" }, "w2");
        await rt.Agent.HandleBatchAsync(new EventBatch([GameEvent.Chat("PoofimaFox", "67 mine 64 cobble at the west cell")]), default);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        string? stopResult = null;
        llm.Tool(ModelTier.Main, "stop", new { }, "s1")
           .On(ModelTier.Main, r =>
           {
               stopResult = r.Messages[^1].ToolResults.Single().Content;
               // A model following the prompt rule corrects itself with the real cause from the tool result.
               var text = stopResult.Contains("missing 3 planks") ? "oops my bad, no planks for a pickaxe so i went for wood. back to cobble"
                   : "oopsie, i got distracted by a shiny tree nya~";
               return llm.Resp(r, StopReasons.EndTurn, new LlmText(text));
           });
        await rt.Agent.HandleBatchAsync(new EventBatch([GameEvent.Chat("PoofimaFox", "67 you're mining a tree?")]), default);

        Assert.NotNull(stopResult);
        Assert.Contains("worker was:", stopResult);
        Assert.Contains("missing 3 planks", stopResult);
        Assert.Contains("mine", stopResult);
        Assert.Contains(sent, s => s.Contains("no planks for a pickaxe"));
        Assert.DoesNotContain(sent, s => s.Contains("shiny tree"));
        Assert.Contains("Never invent a cute excuse", llm.Of(ModelTier.Main).First().System[0].Text);
    }
}
