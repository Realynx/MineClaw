using System.Text.Json;
using MineClaw.Agent.Chat;
using MineClaw.Agent.Events;
using MineClaw.Agent.Llm;

namespace MineClaw.Agent.Tests;

/// <summary>
/// Live 10-28 03:16 (alice CT 114): a task-event turn (gen_loop failed) was joined mid-turn by "[noticed] PoofimaFox: PoofimaFox
/// fell into the void and died off the main island"; after the condolence the same turn narrated "...so I told him I'd keep the
/// cobble coming while he respawns. I rewrote the mining script... That script is still paused from the danger alert..." into
/// public chat.
/// </summary>
public class LiveDeathNoticeTests
{
    private const string Condolence = "NOOO foxy-senpai fell in the void... rip master, maid-chan will keep the cobble coming while u respawn uwu";
    private const string Narration =
        "Fox fell into the void and died off the main island, so I told him I'd keep the cobble coming while he respawns. " +
        "I rewrote the mining script to store cobble in the chest next to the gen, because walking to the far chest kept getting me stuck. " +
        "That script is still paused from the danger alert...";

    private static GameEvent FoxDied() =>
        GameEvent.Activity("PoofimaFox fell into the void and died off the main island", "PoofimaFox", EventPriority.Normal) with { MustWake = true };

    [Fact]
    public async Task A_death_noticed_mid_turn_gets_one_short_line_and_the_narration_stays_internal()
    {
        var (rt, llm, sent) = TestRuntime.Create();
        var internalLines = new List<string>();
        rt.Agent.InternalSpeech += internalLines.Add;
        // Round 0 (the task turn) calls a tool; Fox's death arrives while it runs and joins the turn.
        llm.Then(r =>
            {
                rt.Events.Post(FoxDied());
                return llm.Resp(r, StopReasons.ToolUse, new LlmToolUse("tu_a", "list_memory", JsonSerializer.SerializeToElement(new { })));
            })
            .ThenTool("say", new { text = Condolence }, id: "tu_b")
            .ThenTool("say", new { text = "also I rewrote the gen script, it stores next to the gen now" }, id: "tu_c")
            .ThenText(Narration);
        var o = await rt.Agent.HandleBatchAsync(new EventBatch([GameEvent.TaskFail("#11 script gen_loop: failed: can't reach chest at 14,64,-5")]), default);

        Assert.Equal(Condolence, string.Join(" ", sent)); // one line, wrapped at the chat limit
        Assert.Equal(Condolence, string.Join(" ", o.Said));
        Assert.DoesNotContain(sent, l => l.Contains("so I told him") || l.Contains("rewrote") || l.Contains("paused"));
        Assert.Contains(internalLines, l => l.Contains("rewrote the gen script")); // a second say is kept in the log
    }

    [Fact]
    public async Task A_death_turn_says_its_one_line_and_keeps_the_rest_internal()
    {
        var (rt, llm, sent) = TestRuntime.Create();
        llm.ThenText(Condolence + "\n" + Narration);
        var o = await rt.Agent.HandleBatchAsync(new EventBatch([FoxDied()]), default);
        Assert.Equal(Condolence, string.Join(" ", sent));
        Assert.Equal(Condolence, string.Join(" ", o.Said));
    }

    [Fact]
    public void Quiet_turn_joined_by_a_noticed_event_becomes_a_warning_turn_and_chat_makes_it_a_chat_turn()
    {
        Assert.Equal((false, true), BotAgent.AfterJoined(true, false, new EventBatch([FoxDied()])));
        Assert.Equal((false, false), BotAgent.AfterJoined(true, true, new EventBatch([GameEvent.Chat("PoofimaFox", "maid you there?")])));
        Assert.Equal((false, false), BotAgent.AfterJoined(false, false, new EventBatch([FoxDied()]))); // a chat turn stays one
        Assert.Equal((true, false), BotAgent.AfterJoined(true, false, new EventBatch([GameEvent.Activity("kiwi built a wall", "kiwi")])));
        Assert.True(BotAgent.IsWarningBatch(new EventBatch([FoxDied()])));
        Assert.True(FoxDied().Priority < new AgentOptions().InterruptPriority); // wakes the brain, never interrupts the body
    }

    [Theory]
    [InlineData("Fox fell into the void and died off the main island, so I told him I'd keep the cobble coming while he respawns.")]
    [InlineData("I rewrote the mining script to store cobble in the chest next to the gen, because walking to the far chest kept getting me stuck.")]
    [InlineData("That script is still paused from the danger alert...")]
    [InlineData("that pause was from Fox falling, it's running again")]
    [InlineData("I've already restarted the cobble mining after that pause, so there's nothing else to do here.")]
    [InlineData("so I told Robin to grab her stuff")]
    public void MetaSpeech_drops_noticed_event_narration(string line)
    {
        Assert.Equal("", MetaSpeech.Strip(line, out var dropped));
        Assert.NotEmpty(dropped);
    }

    [Theory]
    [InlineData(Condolence)]
    [InlineData("rip foxy, see u at spawn")]
    [InlineData("so I told you! the void is sneaky")]
    [InlineData("i told him he'd fall lol")]
    public void MetaSpeech_keeps_the_condolence(string line) => Assert.Equal(line, MetaSpeech.Strip(line, out _));
}

/// <summary>
/// Live 10-28 02:23-02:24: robinchan (Robin's own bot, a separate account) died and main said "noooo robin got clapped by the
/// spider..."; then "I sent clone 1337 to make a stone pickaxe and hand it to Robin" for Fox's "deliver it to robinchan".
/// </summary>
public class PlayerIdentityTests
{
    private static readonly string[] Online = ["PoofimaFox", "kiwi", "robinchan"];

    private static IEnumerable<string> Nicks(string p) =>
        new ChatOptions().PlayerNicknames.TryGetValue(p, out var n) ? n : [];

    [Theory]
    [InlineData("robinchan", "robinchan")]
    [InlineData("RobinChan", "robinchan")]
    [InlineData("robin-chan", "robinchan")]
    [InlineData("Robin Chan", "robinchan")]
    [InlineData("robin", "kiwi")]
    [InlineData("Robin", "kiwi")]
    [InlineData("foxy", "PoofimaFox")]
    [InlineData("nobody", null)]
    public void Exact_accounts_win_over_nicknames(string name, string? expected) =>
        Assert.Equal(expected, PlayerNames.Resolve(name, Online, Nicks));

    [Fact]
    public void Robin_means_an_online_account_called_robin_before_the_nickname()
    {
        Assert.Equal("Robin", PlayerNames.Resolve("robin", ["kiwi", "Robin"], Nicks));
        Assert.Equal("kiwi", PlayerNames.Resolve("robin", ["kiwi", "robinchan"], Nicks));
    }

    [Fact]
    public void A_nickname_is_never_read_inside_another_account_name()
    {
        Assert.False(AddressingText.Mentions(PlayerNames.WithoutLogins("robin-chan look at this", Online), "robin"));
        Assert.True(AddressingText.Mentions(PlayerNames.WithoutLogins("robin look at this", Online), "robin"));
        Assert.Equal("robinchan look", PlayerNames.Canonical("robin-chan look", Online));
    }

    [Fact]
    public void Replayed_robinchan_death_line_names_robinchan_not_robin()
    {
        var turn = new List<(string?, string)> { ("robinchan", "robinchan was slain by Spider at the furnace") };
        var line = PlayerNames.Unfold("noooo robin got clapped by the spider at the furnace T_T your stuff should still be lying there", turn, Online, Nicks);
        Assert.Equal("noooo robinchan got clapped by the spider at the furnace T_T your stuff should still be lying there", line);
    }

    [Fact]
    public void Replayed_pickaxe_handover_names_robinchan_not_robin()
    {
        var turn = new List<(string?, string)> { ("PoofimaFox", "spawn 1337 to make a pickaxe and deliver it to robinchan") };
        var line = PlayerNames.Unfold("I sent clone 1337 to make a stone pickaxe and hand it to Robin, but she doesn't have it yet.", turn, Online, Nicks);
        Assert.Contains("hand it to robinchan,", line);
        Assert.DoesNotContain("Robin", line);
    }

    [Fact]
    public void Robin_stays_robin_when_the_turn_is_about_kiwi()
    {
        var both = new List<(string?, string)> { ("kiwi", "robinchan come here"), ("robinchan", "omw robin-sama") };
        Assert.Equal("robin, robinchan is on her way", PlayerNames.Unfold("robin, robinchan is on her way", both, Online, Nicks));
        var fox = new List<(string?, string)> { ("PoofimaFox", "give robin a pickaxe") };
        Assert.Equal("ok giving robin one", PlayerNames.Unfold("ok giving robin one", fox, Online, Nicks));
    }

    [Fact]
    public void Router_unfolds_against_the_online_list_and_waking_events_only()
    {
        var (rt, _, _) = TestRuntime.Create();
        rt.Chat.SetOnlinePlayers(Online);
        var turn = new[] { GameEvent.Chat("PoofimaFox", "spawn 1337 to make a pickaxe and deliver it to robinchan") };
        Assert.Equal("1337's bringing it to robinchan", rt.Chat.UnfoldNames("1337's bringing it to Robin", turn));
        var bg = new[] { GameEvent.Chat("robinchan", "i need a pickaxe", addressed: false), GameEvent.Chat("PoofimaFox", "hey maid") };
        Assert.Equal("hi foxy, robin says hi", rt.Chat.UnfoldNames("hi foxy, robin says hi", bg));
    }
}
