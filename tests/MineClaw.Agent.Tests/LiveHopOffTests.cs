using System.Text.Json;
using MineClaw.Agent.Chat;
using MineClaw.Agent.Events;
using MineClaw.Agent.Rag;
using MineClaw.Agent.Rules;
using MineClaw.Agent.Tools;

namespace MineClaw.Agent.Tests;

/// <summary>
/// Live 10-28 15:33-15:45 (alice CT 114): main asked Robin to hop off the old carousel, "I hopped off it" was dropped as "unclear,
/// another bot around"; the retry was then refused for 10 min by "someone is standing on it"; and after a restart "c trees"
/// (robinchan's prefix) woke main again.
/// </summary>
public class LiveHopOffTests
{
    private static JsonElement J(object o) => JsonSerializer.SerializeToElement(o);

    private sealed class Rig
    {
        public readonly ManualTime Time = new() { Now = new DateTimeOffset(2026, 10, 28, 15, 33, 17, TimeSpan.Zero) };
        public readonly EventQueue Queue;
        public readonly ChatRouter Router;
        public readonly List<(ChatMessage Msg, AddressDecision D)> Decisions = new();

        public Rig(params string[] online)
        {
            var o = new ChatOptions { Username = "maid_chan", DisplayName = "maid-chan", Aliases = ["maid", "maidchan"], LineDelayMs = 0 };
            o.Classifier.Enabled = false;
            Queue = new EventQueue(new EventQueueOptions(), Time);
            Router = new ChatRouter(o, Queue, (_, _) => Task.CompletedTask, Time)
            {
                IsRegular = p => p.Equals("kiwi", StringComparison.OrdinalIgnoreCase) || p.Equals("PoofimaFox", StringComparison.OrdinalIgnoreCase),
            };
            Router.SetOnlinePlayers(online.Length > 0 ? online : ["PoofimaFox", "kiwi", "robinchan"]);
            Router.AddressDecided += (m, d) => Decisions.Add((m, d));
        }

        public AddressDecision Hear(string raw, double afterSeconds = 2)
        {
            Time.Advance(TimeSpan.FromSeconds(afterSeconds));
            Router.OnServerChat(raw);
            return Decisions[^1].D;
        }

        public void Raw(string raw, double afterSeconds)
        {
            Time.Advance(TimeSpan.FromSeconds(afterSeconds));
            Router.OnServerChat(raw);
        }

        public void Say(string text, double afterSeconds, params string[] to)
        {
            Time.Advance(TimeSpan.FromSeconds(afterSeconds));
            Router.SayAsync(text, null, to).GetAwaiter().GetResult();
        }

        public List<GameEvent> Waking() => Queue.Snapshot().Where(e => e.Wakes).ToList();
    }

    /// <summary>Robin's "c help" / "c follow", each answered by robinchan within a second, teach the "c " prefix.</summary>
    private static void TeachC(Rig r)
    {
        r.Hear("<kiwi> c help");
        r.Hear("<robinchan> i can: make me a stone pickaxe, get me 20 cobble/some wood, chop trees, mine cobble, harvest wheat,", 0.3);
        r.Hear("<kiwi> c follow", 12);
        r.Hear("<robinchan> following u kiwi!! say stop when u want me to stay", 0.3);
    }

    // ---------------------------------------------------------------- bug 1: the reply to main's request

    [Fact]
    public void Replayed_I_hopped_off_it_goes_to_main()
    {
        var r = new Rig();
        r.Say("kiwi, robin-sama which blocks are the old carousel? punch one of em pls, i only see the gen over there", 25, "kiwi"); // 15:33:42
        r.Say("I haven't deleted anything yet.\nI looked around the cobble gen twice and couldn't find an old carousel.", 3, "kiwi");
        Assert.Equal(AddressReason.AnswerToUs, r.Hear("<kiwi> the ones I'm standing on", 23).Reason);                       // 15:34:08
        r.Say("got it robin-sama, clearing the old carousel you're standing on, gen stays safe uwu~\nhop off it when i get there pls", 18, "kiwi"); // 15:34:26
        r.Raw("robinchan left the game.", 15);                                                                                 // 15:34:41
        r.Raw("robinchan joined the game.", 15);                                                                               // 15:34:56
        Assert.Equal(AddressReason.FromBot, r.Hear("<robinchan> im back! hope that pickaxe from last time is still rizzing it up", 4).Reason);
        var d = r.Hear("<kiwi> I hopped off it", 76);                                                                          // 15:36:16
        Assert.True(d.Addressed, d.Why); // live: "not us (Unclear: unclear and another bot is in the conversation)"
        Assert.Equal(AddressReason.AnswerToUs, d.Reason);
        Assert.Contains(r.Waking(), e => e.Text == "I hopped off it");
    }

    [Fact]
    public void A_plain_remark_opens_no_request_and_an_old_request_lapses()
    {
        var r = new Rig();
        r.Say("the carousel is done uwu", 1, "kiwi");
        r.Hear("<robinchan> uwu hiii everyone!!", 95);
        Assert.Equal(AddressReason.Unclear, r.Hear("<kiwi> nice one", 5).Reason);
        r.Say("hop off it when i get there pls", 1, "kiwi");
        r.Hear("<robinchan> skibidi!!", 100);
        Assert.Equal(AddressReason.Unclear, r.Hear("<kiwi> lol", 30).Reason); // 130 s: past the open-request window
    }

    [Fact]
    public async Task A_pending_ask_player_counts_as_our_request()
    {
        var r = new Rig();
        var wait = r.Router.WaitForReplyAsync("kiwi", TimeSpan.FromMinutes(5), CancellationToken.None);
        r.Hear("<robinchan> uwu hiii everyone!!", 100);
        var d = r.Hear("<kiwi> the left one", 20);
        Assert.Equal(AddressReason.AnswerToUs, d.Reason);
        Assert.Equal("kiwi: the left one", await wait);
    }

    [Theory]
    [InlineData("c trees")]
    [InlineData("robinchan follow me")]
    [InlineData("robin-chan come here")]
    public void Lines_for_robinchan_stay_hers_even_with_our_request_open(string line)
    {
        var r = new Rig();
        TeachC(r);
        r.Say("hop off it when i get there pls", 30, "kiwi");
        int before = r.Waking().Count;
        var d = r.Hear("<kiwi> " + line, 10);
        Assert.Equal(AddressReason.OtherPlayer, d.Reason);
        Assert.Equal("robinchan", d.To);
        Assert.Equal(before, r.Waking().Count);
    }

    [Fact]
    public void Her_last_line_to_robinchan_before_main_spoke_keeps_the_follow_up_with_robinchan()
    {
        var r = new Rig();
        TeachC(r);
        r.Hear("<kiwi> c trees", 60);
        r.Say("hop off the tree pls", 3, "kiwi");
        var d = r.Hear("<kiwi> get an axe, cut them down like real players", 10);
        Assert.Equal(AddressReason.OtherConversation, d.Reason);
        Assert.Equal("robinchan", d.To);
    }

    [Fact]
    public void Our_request_newer_than_robinchans_thread_wins_when_her_last_line_was_to_us()
    {
        var r = new Rig();
        Assert.Equal(AddressReason.Name, r.Hear("<kiwi> maid_chan where's the sapling chest").Reason);
        r.Hear("<robinchan> kiwi wanna play tag?? uwu", 5); // robinchan opens a thread with her
        r.Say("by the furnace robin-sama~ hop on the chest and I'll toss you some", 5, "kiwi");
        var d = r.Hear("<kiwi> ok im on it", 10); // was "continues with robinchan (another bot)"
        Assert.Equal(AddressReason.AnswerToUs, d.Reason);
        Assert.Contains("robinchan", d.Why);
    }

    // ---------------------------------------------------------------- command prefixes: persisted, declared, offline

    [Fact]
    public void A_learned_prefix_survives_a_restart_and_routes_even_with_robinchan_offline()
    {
        using var db = AgentDb.Open(null);
        var before = new Rig();
        CommandPrefixStore.Attach(before.Router, db, time: before.Time);
        TeachC(before);
        Assert.Equal(["c "], before.Router.CommandPrefixesOf("robinchan"));

        var after = new Rig("PoofimaFox", "kiwi"); // restarted; robinchan not online
        CommandPrefixStore.Attach(after.Router, db, time: after.Time);
        Assert.Equal(["c "], after.Router.CommandPrefixesOf("robinchan"));
        after.Router.LookingAt = p => p == "kiwi" ? "maid_chan" : null; // live 15:45: "Gaze: looking at us"
        var d = after.Hear("<kiwi> c trees", 5);
        Assert.Equal(AddressReason.OtherPlayer, d.Reason);
        Assert.Equal("robinchan", d.To);
        Assert.Empty(after.Waking());
        Assert.Contains(after.Queue.Snapshot(), e => e.Kind == EventKind.Notice && !e.Wakes && e.Text == "kiwi is instructing robinchan: c trees");
    }

    [Fact]
    public void A_stored_rule_alone_declares_the_prefix_after_a_restart()
    {
        using var db = AgentDb.Open(null);
        var rules = new DirectiveStore(db, null) { IsTrusted = p => p == "kiwi" };
        Assert.True(rules.Add("kiwi", "when I say c, I'm talking to robinchan").Stored);
        var r = new Rig("PoofimaFox", "kiwi");
        CommandPrefixStore.Attach(r.Router, db, rules, r.Time);
        Assert.Equal(["c "], r.Router.CommandPrefixesOf("robinchan"));
        Assert.Equal(AddressReason.OtherPlayer, r.Hear("<kiwi> c trees").Reason);
        Assert.Empty(r.Waking());
    }

    [Fact]
    public void A_rule_stored_later_and_the_live_chat_phrasing_declare_it_at_once_and_persist()
    {
        using var db = AgentDb.Open(null);
        var rules = new DirectiveStore(db, null) { IsTrusted = p => p == "kiwi" };
        var r = new Rig();
        CommandPrefixStore.Attach(r.Router, db, rules, r.Time);
        Assert.Empty(r.Router.CommandPrefixesOf("robinchan"));
        r.Hear("<kiwi> maid_chan back to work, I'm talking to robinchan when I say c"); // live 00:24
        Assert.Equal(["c "], r.Router.CommandPrefixesOf("robinchan"));
        Assert.Equal(AddressReason.OtherPlayer, r.Hear("<kiwi> c trees", 3).Reason);

        rules.Add("kiwi", "when I say x I'm talking to robinchan");
        Assert.Contains("x ", r.Router.CommandPrefixesOf("robinchan"));
        var restarted = new Rig("kiwi");
        CommandPrefixStore.Attach(restarted.Router, db, time: restarted.Time); // no rules: the table alone
        Assert.Equal(["c ", "x "], restarted.Router.CommandPrefixesOf("robinchan").Order());
    }

    [Theory]
    [InlineData("maidchan, when I say c, I'm talking to robinchan", "c ")]
    [InlineData("maid_chan back to work, I'm talking to robinchan when I say c", "c ")]
    [InlineData("When kiwi says c, she's talking to robinchan", "c ")]
    [InlineData("when i say ! it's for robinchan", "!")]
    [InlineData("when I say come here, I'm talking to you", null)]
    [InlineData("I'm talking to robinchan", null)]
    [InlineData("when I say c, I'm talking to PoofimaFox", null)] // a human friend, not a command bot
    public void Prefix_rules_are_parsed(string text, string? prefix)
    {
        var r = new Rig();
        Assert.Equal(prefix != null, r.Router.LearnPrefixRule("kiwi", text));
        if (prefix != null) Assert.Contains(prefix, r.Router.CommandPrefixesOf("robinchan"));
        else Assert.Empty(r.Router.CommandPrefixesOf("robinchan"));
    }

    [Fact]
    public void Robinchans_own_say_c_help_declares_its_prefix()
    {
        var r = new Rig();
        r.Hear("<robinchan> hm? what should i do, kiwi? say c help for my list uwu"); // live 13:49
        Assert.Equal(["c "], r.Router.CommandPrefixesOf("robinchan"));
        Assert.Equal(AddressReason.OtherPlayer, r.Hear("<kiwi> c help", 3).Reason);
    }

    // ---------------------------------------------------------------- bug 2: passing failures

    private const string StandingOn = "failed: dig failed 2x the same way: won't dig the cobblestone at 4,65,3: someone is standing on it";
    private static readonly JsonElement BreakTask = J(new { task = "Break exactly these cobblestone blocks (kiwi's old carousel) at 4,65,3 5,65,3 6,65,3", done_when = "blocks gone" });

    [Theory]
    [InlineData(StandingOn, TransientCause.Occupied)]
    [InlineData("won't place at 3,64,1: kiwi is standing in it", TransientCause.Occupied)]
    [InlineData("skipped: mob in the way at 5,64,2", TransientCause.Mob)]
    [InlineData("withdraw: chest busy (maid_chan_67 has it open)", TransientCause.Busy)]
    [InlineData("busy: maid_chan_67 is using chest 14,64,-5", TransientCause.Busy)]
    [InlineData("the chunk at 40,64,-2 is not loaded yet", TransientCause.NotLoaded)]
    [InlineData("build paused: it's night (rule#6: daytime only)", TransientCause.TimeOfDay)]
    [InlineData("a build needs a trusted player's go (poofimafox/kiwi); waiting for player", TransientCause.WaitingForPlayer)]
    [InlineData("no path to within 1 of (8, 64, -5)", null)]
    [InlineData("fence at 8,62,-3 is protected (a floor)", null)]
    [InlineData("no cobblestone in the chest at 14,64,-5", null)]
    public void Failure_reasons_are_classified(string reason, TransientCause? cause) =>
        Assert.Equal(cause, ToolCallGuard.TransientCauseOf(reason));

    [Fact]
    public void A_standing_on_it_failure_allows_a_retry_once_the_player_moves()
    {
        var (rt, _, _) = TestRuntime.Create();
        var g = rt.Agent.Guard;
        g.RecordFailure("delegate", BreakTask, StandingOn);
        Assert.StartsWith("not retried", g.Check("delegate", BreakTask)!.Text); // right away: still blocked (no loop)
        // "I hopped off it", even as a line not addressed to us, clears it.
        rt.Events.Post(GameEvent.Chat("kiwi", "I hopped off it", false));
        Assert.Null(g.Check("delegate", BreakTask));
    }

    [Fact]
    public void A_passing_failure_expires_on_its_own_and_a_persistent_one_still_blocks()
    {
        var time = new ManualTime();
        var g = new ToolCallGuard(new ActivityManager(new ToolRegistry(), new EventQueue(), time), time);
        g.RecordFailure("delegate", BreakTask, StandingOn);
        var mine = J(new { block = "fence", at = "8,62,-3" });
        g.RecordFailure("mine", mine, "skipped 2 protected: fence at 8,62,-3 is protected (a floor)");
        time.Advance(TimeSpan.FromSeconds(10));
        Assert.NotNull(g.Check("delegate", BreakTask));
        time.Advance(TimeSpan.FromSeconds(15)); // 25 s: past TransientMemory
        Assert.Null(g.Check("delegate", BreakTask));
        // The persistent one keeps today's behaviour: remembered for minutes, not cleared by "the player moved".
        Assert.Equal(0, g.ClearTransient());
        Assert.StartsWith("not retried", g.Check("mine", mine)!.Text);
        time.Advance(TimeSpan.FromMinutes(5));
        Assert.NotNull(g.Check("mine", mine));
    }

    [Fact]
    public void A_persistent_failure_is_not_cleared_by_unaddressed_chat()
    {
        var (rt, _, _) = TestRuntime.Create();
        var g = rt.Agent.Guard;
        var mine = J(new { block = "fence", at = "8,62,-3" });
        g.RecordFailure("mine", mine, "fence at 8,62,-3 is protected (a floor)");
        rt.Events.Post(GameEvent.Chat("kiwi", "I hopped off it", false));
        Assert.NotNull(g.Check("mine", mine));
    }
}
