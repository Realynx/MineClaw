using MineClaw.Agent.Chat;
using MineClaw.Agent.Events;
using MineClaw.Agent.Llm;

namespace MineClaw.Agent.Tests;

/// <summary>
/// Live 10-27 23:30-23:37 (alice CT 114): every background harvest_generator run finishing woke the brain with a task event and
/// its text went to public chat ("I didn't say anything in chat." / "The last run worked: 32 more cobble mined and stored. I
/// started another 32-cobble run for Fox's standing order."). Task/system turns are quiet now; only an engaged say speaks.
/// </summary>
public class QuietTurnTests
{
    private const string Leak1 = "I didn't say anything in chat. The last run worked: 32 more cobble mined and stored. I started another 32-cobble run for Fox's standing order.";
    private const string Leak2 = "I didn't say anything in chat. The fourth run worked: another 32 cobble mined and stored. I started a fifth 32-cobble run. My stone pickaxe is down to about 34 uses, so it'll probably wear out during this run.";

    private static EventBatch Chat(string player, string text) => new([GameEvent.Chat(player, text)]);
    private static EventBatch Task(string text) => new([GameEvent.TaskDone(text)]);

    [Fact]
    public async System.Threading.Tasks.Task Replayed_standing_order_runs_never_reach_public_chat()
    {
        var (rt, llm, sent) = TestRuntime.Create();
        var internalLines = new List<string>();
        rt.Agent.InternalSpeech += internalLines.Add;
        // 23:29:24 Fox: "how do I make a fence?" / "maid": a real answer, said.
        llm.ThenText("fence is 6 sticks, two full rows of 3 on the crafting table, makes 2 fences uwu");
        var a = await rt.Agent.HandleBatchAsync(Chat("PoofimaFox", "clawd how do I make a fence?"), default);
        Assert.NotEmpty(a.Said);
        int before = sent.Count;

        // 23:29:58 triage → Main (task event): round 0 starts the next run, round 1 writes the leak.
        llm.ThenTool("list_memory", new { }, id: "tu_a").ThenText(Leak1);
        var b = await rt.Agent.HandleBatchAsync(Task("#3 harvest_generator: ok: mined 32 cobblestone, stored 32 in the chest"), default);
        // 23:31:59: the fourth run, same shape.
        llm.ThenTool("list_memory", new { }, id: "tu_b").ThenText(Leak2);
        var c = await rt.Agent.HandleBatchAsync(Task("#4 harvest_generator: ok: mined 32 cobblestone, stored 32 in the chest"), default);

        Assert.Empty(b.Said);
        Assert.Empty(c.Said);
        Assert.Equal(before, sent.Count);
        Assert.DoesNotContain(sent, l => l.Contains("didn't say", StringComparison.OrdinalIgnoreCase) || l.Contains("run worked", StringComparison.OrdinalIgnoreCase));
        Assert.Equal([Leak1, Leak2], internalLines); // kept internal (log / hive board), not dropped silently
    }

    [Fact]
    public async System.Threading.Tasks.Task Quiet_turn_never_streams_its_text()
    {
        var (rt, llm, sent) = TestRuntime.Create(o => o.Brainstem = new Brainstem.BrainstemOptions { Enabled = true });
        llm.ThenText("Another batch of cobble is stored, starting the next one~");
        var o = await rt.Agent.HandleBatchAsync(Task("#5 harvest_generator: ok: mined 32 cobblestone"), default);
        Assert.Empty(o.Said);
        Assert.Empty(sent);
        Assert.Null(llm.Requests[0].OnTextDelta);
    }

    [Fact]
    public async System.Threading.Tasks.Task Say_in_a_quiet_turn_is_refused_when_nobody_is_engaged()
    {
        var (rt, llm, sent) = TestRuntime.Create();
        llm.ThenTool(BotAgent.SayToolName, new { text = "cobble's stocked up~" }).ThenText("");
        var o = await rt.Agent.HandleBatchAsync(Task("#6 harvest_generator: ok: mined 32 cobblestone"), default);
        Assert.Empty(sent);
        Assert.Empty(o.Said);
        var result = llm.Requests[1].Messages[^1].Content.OfType<LlmToolResult>().Single();
        Assert.StartsWith("not said", result.Content);
    }

    [Fact]
    public async System.Threading.Tasks.Task Say_reaches_an_engaged_player_but_routine_run_status_still_does_not()
    {
        var (rt, llm, sent) = TestRuntime.Create();
        rt.Chat.SetOnlinePlayers(["PoofimaFox", "kiwi"]);
        llm.ThenText("on it~");
        rt.Chat.OnServerChat("<PoofimaFox> clawd mine the gen for me");
        var batch = rt.Events.TryTakeBatch() ?? Chat("PoofimaFox", "clawd mine the gen for me");
        await rt.Agent.HandleBatchAsync(batch, default);
        int before = sent.Count;

        // Fox spoke within 2 min: an explicit say may reach him; the routine run report is still filtered.
        llm.ThenTool(BotAgent.SayToolName, new { text = "The fourth run worked: another 32 cobble mined and stored." }, id: "tu_1")
            .ThenTool(BotAgent.SayToolName, new { text = "foxy, my pickaxe is about to break, got a spare?" }, id: "tu_2")
            .ThenText("");
        var o = await rt.Agent.HandleBatchAsync(Task("#7 harvest_generator: ok: mined 32 cobblestone"), default);
        var lines = sent.Skip(before).ToList();
        Assert.Single(lines);
        Assert.Contains("pickaxe", lines[0]);
        Assert.Single(o.Said);
    }

    [Fact]
    public async System.Threading.Tasks.Task A_failure_needing_help_may_be_said()
    {
        var (rt, llm, sent) = TestRuntime.Create();
        llm.ThenTool(BotAgent.SayToolName, new { text = "my pickaxe broke, can someone make me a stone one?" }).ThenText("");
        var o = await rt.Agent.HandleBatchAsync(new EventBatch([GameEvent.TaskFail("#8 harvest_generator: no pickaxe left")]), default);
        Assert.Single(o.Said);
        Assert.Contains(sent, l => l.Contains("pickaxe broke"));
    }

    [Fact]
    public async System.Threading.Tasks.Task Plain_text_in_a_failure_turn_is_still_internal()
    {
        var (rt, llm, sent) = TestRuntime.Create();
        llm.ThenText("The run failed because the pickaxe broke.");
        var o = await rt.Agent.HandleBatchAsync(new EventBatch([GameEvent.TaskFail("#8 harvest_generator: no pickaxe left")]), default);
        Assert.Empty(o.Said);
        Assert.Empty(sent);
    }

    [Fact]
    public async System.Threading.Tasks.Task Player_chat_mid_turn_makes_the_turn_speak_again()
    {
        var (rt, llm, sent) = TestRuntime.Create();
        llm.Then(r =>
        {
            rt.Events.Post(GameEvent.Chat("PoofimaFox", "clawd how much cobble do we have?"));
            return llm.Resp(r, StopReasons.ToolUse, new LlmToolUse("tu_1", "list_memory", System.Text.Json.JsonSerializer.SerializeToElement(new { })));
        }).ThenText("about 3 stacks in the chests, foxy~");
        var o = await rt.Agent.HandleBatchAsync(Task("#9 harvest_generator: ok: mined 32 cobblestone"), default);
        Assert.Contains(sent, l => l.Contains("3 stacks"));
        Assert.NotEmpty(o.Said);
    }

    [Fact]
    public async System.Threading.Tasks.Task A_player_in_danger_gets_one_short_warning_even_after_asking_for_quiet()
    {
        var (rt, llm, sent) = TestRuntime.Create();
        // DangerMonitor: "[noticed] kiwi is taking damage near the tree" (Activity, Normal priority, Player = kiwi).
        llm.ThenText("Robin is taking damage near the tree, but she's asked me to stay quiet unless spoken to, and I don't know what's hurting her, so I didn't step in.\nrobin! something's hurting you by the tree");
        var o = await rt.Agent.HandleBatchAsync(new EventBatch([GameEvent.Activity("kiwi is taking damage near the tree", "kiwi", EventPriority.Normal)]), default);
        Assert.Equal(["robin! something's hurting you by the tree"], sent);
        Assert.Single(o.Said);
    }

    [Fact]
    public void Quiet_batches_are_task_and_system_events_only()
    {
        Assert.True(BotAgent.IsQuietBatch(Task("#1 harvest_generator: ok")));
        Assert.True(BotAgent.IsQuietBatch(new EventBatch([GameEvent.TaskFail("#1 x: failed")])));
        Assert.True(BotAgent.IsQuietBatch(new EventBatch([GameEvent.Notice("[hive] 67 finished", EventPriority.Normal)])));
        Assert.True(BotAgent.IsQuietBatch(new EventBatch([GameEvent.Idle()])));
        Assert.True(BotAgent.IsQuietBatch(new EventBatch([GameEvent.Activity("kiwi built a wall", "kiwi")])));
        Assert.False(BotAgent.IsQuietBatch(Chat("PoofimaFox", "clawd hi")));
        Assert.False(BotAgent.IsQuietBatch(new EventBatch([GameEvent.Joined("robinchan")])));
        Assert.False(BotAgent.IsQuietBatch(new EventBatch([new GameEvent(EventKind.Notice, "PoofimaFox gave you 12 fence", "PoofimaFox", EventPriority.Normal)])));
        Assert.False(BotAgent.IsQuietBatch(new EventBatch([GameEvent.Activity("kiwi is taking damage", "kiwi", EventPriority.Normal)])));
        Assert.False(BotAgent.IsQuietBatch(new EventBatch([GameEvent.LowHealth(4)])));
        Assert.False(BotAgent.IsQuietBatch(new EventBatch([GameEvent.TaskDone("x"), GameEvent.Chat("kiwi", "clawd?")])));
    }

    [Fact]
    public void Prompts_no_longer_invite_narrating_silence()
    {
        Assert.DoesNotContain("you already said", BotAgent.AckNoteFormat);
        Assert.Contains("don't repeat or mention it", BotAgent.AckNoteFormat);
        var sys = Prompts.System("", "", "", false, delegation: true);
        Assert.DoesNotContain("or (silent) if", sys);
        Assert.Contains("private log, not chat", sys);
        Assert.Contains("Never mention your own", sys);
    }
}

/// <summary>MetaSpeech: the bot's own speech/silence, its addressing reasoning, and routine run status (live 10-27 23:30-23:45).</summary>
public class MetaSpeechSelfNarrationTests
{
    [Theory]
    [InlineData("I didn't say anything in chat.")]
    [InlineData("I haven't said anything yet.")]
    [InlineData("I said nothing.")]
    [InlineData("I'd already answered robin in chat (\"robin-sama noticed~\") and saved a note to call her robin-sama from now on.")]
    [InlineData("Robin said \"don't say anything unless addressed\" while talking to robinchan, so I'm treating it as not for me.")]
    [InlineData("Robin is taking damage near the tree, but she's asked me to stay quiet unless spoken to, and I don't know what's hurting her, so I didn't step in.")]
    [InlineData("I'm staying quiet.")]
    [InlineData("That line wasn't addressed to me.")]
    [InlineData("The last run worked: 32 more cobble mined and stored.")]
    [InlineData("The fourth run worked: another 32 cobble mined and stored.")]
    [InlineData("I started another 32-cobble run for Fox's standing order.")]
    [InlineData("I started a fifth 32-cobble run.")]
    [InlineData("Another 32-cobble run for Fox has started.")]
    public void Self_narration_and_run_status_are_dropped(string line)
    {
        Assert.Equal("", MetaSpeech.Strip(line, out var gone));
        Assert.NotEmpty(gone);
    }

    [Fact]
    public void Live_leak_keeps_only_real_speech()
    {
        var t = MetaSpeech.Strip("I didn't say anything in chat. The fourth run worked: another 32 cobble mined and stored. I started a fifth 32-cobble run. My stone pickaxe is down to about 34 uses, so it'll probably wear out during this run.", out var gone);
        Assert.Equal("My stone pickaxe is down to about 34 uses, so it'll probably wear out during this run.", t);
        Assert.Equal(3, gone.Count);
    }

    [Fact]
    public void Run_status_is_kept_when_a_player_asked_for_it()
    {
        Assert.True(MetaSpeech.AsksStatus(["how's it going?"]));
        Assert.True(MetaSpeech.AsksStatus(["how much cobble do we have"]));
        Assert.False(MetaSpeech.AsksStatus(["how do I make a fence?"]));
        Assert.Equal("The fourth run worked: another 32 cobble mined and stored.",
            MetaSpeech.Strip("The fourth run worked: another 32 cobble mined and stored.", out _, statusAsked: true));
        // ...but self-narration never is.
        Assert.Equal("", MetaSpeech.Strip("I didn't say anything in chat.", out _, statusAsked: true));
    }

    [Theory]
    [InlineData("oki, i'll stay quiet~")]
    [InlineData("robin! something's hurting you by the tree")]
    [InlineData("fence is 6 sticks, two full rows of 3 on the crafting table, makes 2 fences uwu")]
    [InlineData("I'll run over to the gen and mine some cobble!")]
    [InlineData("say hi to robin for me~")]
    [InlineData("ty Foxy, got 12 fences!")]
    [InlineData("that was a fun run to the tree")]
    public void Real_speech_stays(string line) => Assert.Equal(line, MetaSpeech.Strip(line, out _));
}
