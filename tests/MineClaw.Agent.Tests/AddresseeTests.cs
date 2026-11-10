using MineClaw.Agent.Brainstem;
using MineClaw.Agent.Chat;
using MineClaw.Agent.Events;
using MineClaw.Agent.Llm;

namespace MineClaw.Agent.Tests;

/// <summary>
/// Live 10-27 23:35-23:45 (alice CT 114): Robin's own bot "robinchan" joined; main answered lines meant for it ("c follow me" →
/// "omw follow u~"), acked Robin's question to it, and ping-ponged with it. Addressee resolution in C#, replayed line by line.
/// </summary>
public class AddresseeTests
{
    private sealed class Rig
    {
        public readonly ManualTime Time = new() { Now = new DateTimeOffset(2026, 10, 27, 23, 35, 0, TimeSpan.Zero) };
        public readonly EventQueue Queue;
        public readonly ChatRouter Router;
        public readonly List<string> Sent = new();
        public readonly List<(ChatMessage Msg, AddressDecision D)> Decisions = new();

        public Rig(params string[] online)
        {
            var o = new ChatOptions { Username = "maid_chan", DisplayName = "maid-chan", Aliases = ["maid", "maidchan"], LineDelayMs = 0 }.Regulars();
            o.Classifier.Enabled = false;
            Queue = new EventQueue(new EventQueueOptions(), Time);
            Router = new ChatRouter(o, Queue, (l, _) => { lock (Sent) Sent.Add(l); return Task.CompletedTask; }, Time);
            Router.SetOnlinePlayers(online.Length > 0 ? online : ["PoofimaFox", "kiwi", "robinchan"]);
            Router.AddressDecided += (m, d) => Decisions.Add((m, d));
        }

        public AddressDecision Hear(string raw, double afterSeconds = 2)
        {
            Time.Advance(TimeSpan.FromSeconds(afterSeconds));
            Router.OnServerChat(raw);
            return Decisions[^1].D;
        }

        public void Say(string text, params string[] to) => Router.SayAsync(text, null, to).GetAwaiter().GetResult();

        public List<GameEvent> Waking() => Queue.Snapshot().Where(e => e.Wakes).ToList();
    }

    [Fact]
    public void Replayed_robinchan_sequence_never_wakes_main()
    {
        var r = new Rig();
        Assert.Equal(AddressReason.OtherPlayer, r.Hear("<kiwi> robinchan?").Reason);                                       // 23:35:38
        Assert.Equal(AddressReason.FromBot, r.Hear("<robinchan> uwu yes kiwi-sama!! what do u need? *hops*", 1.1).Reason);
        Assert.Equal(AddressReason.OtherConversation, r.Hear("<kiwi> I'm robin-sama to you", 12).Reason);                  // 23:35:52
        Assert.Contains("robin-sama", r.Router.NicknamesOf("kiwi"));
        Assert.Equal(AddressReason.Rejected, r.Hear("<kiwi> maidchan I'm not talking to you", 68).Reason);                 // 23:37:00
        Assert.Equal(AddressReason.OtherPlayer, r.Hear("<kiwi> robinchan, can you do anything", 13).Reason);              // 23:37:13
        Assert.Equal(AddressReason.FromBot, r.Hear("<robinchan> uwu yes robinsama!! i can help u and foxy get that skibidi rizz on this island!! <3", 0.9).Reason);
        Assert.Equal(AddressReason.OtherPlayer, r.Hear("<kiwi> robinchan, I'm gonna call you c from now on", 19).Reason); // 23:37:33
        Assert.Contains("c", r.Router.NicknamesOf("robinchan"));
        Assert.Equal(AddressReason.FromBot, r.Hear("<robinchan> uwu oki!! c is such a sigma name!! i love it robinsama!! <3", 0.9).Reason);
        var cFollow = r.Hear("<kiwi> c follow me", 3.5);                                                                    // 23:37:37
        Assert.Equal(AddressReason.OtherPlayer, cFollow.Reason);
        Assert.Contains("robinchan", cFollow.Why);
        Assert.Empty(r.Waking()); // nothing woke main: no "omw follow u~"
        Assert.All(r.Decisions, d => Assert.False(string.IsNullOrEmpty(d.D.Why))); // every decision logs its reason
    }

    [Fact]
    public void A_learned_nickname_alone_routes_a_later_line_to_that_bot()
    {
        var r = new Rig();
        r.Hear("<kiwi> robinchan, I'm gonna call you c from now on");
        var d = r.Hear("<kiwi> c follow me", 120); // long after any thread or window
        Assert.Equal(AddressReason.OtherPlayer, d.Reason);
        Assert.Empty(r.Waking());
    }

    [Fact]
    public void Looking_at_maid_chan_saying_follow_me_is_for_us()
    {
        var r = new Rig();
        r.Hear("<robinchan> uwu hiii everyone!! <3"); // the other bot is around: unclear lines would stay silent
        r.Router.LookingAt = p => p == "kiwi" ? "maid_chan" : null;
        var d = r.Hear("<kiwi> follow me");
        Assert.Equal(AddressReason.Gaze, d.Reason);
        var e = Assert.Single(r.Waking());
        Assert.Equal("follow me", e.Text);
        Assert.True(e.AddressConfidence >= new ReplyOptions().AckMinConfidence);
        // ...and looking at robinchan, the same line is robinchan's.
        r.Router.LookingAt = p => p == "kiwi" ? "robinchan" : null;
        Assert.Equal(AddressReason.OtherConversation, r.Hear("<kiwi> follow me", 5).Reason);
    }

    [Fact]
    public void An_unnamed_line_right_after_robinchan_answered_robin_is_not_ours()
    {
        var r = new Rig();
        r.Hear("<kiwi> maidchan hiii");
        r.Say("hiii robin~", "kiwi");
        r.Hear("<kiwi> robinchan?", 8);
        r.Hear("<robinchan> uwu yes robinsama!! what do u need?", 1);
        var d = r.Hear("<kiwi> you have to respond to everything currently?", 6); // live: main acked "hm, lemme think~"
        Assert.Equal(AddressReason.OtherConversation, d.Reason);
        Assert.Contains("robinchan", d.Why);
        Assert.DoesNotContain(r.Waking(), e => e.Text.StartsWith("you have to"));
    }

    [Fact]
    public void An_unnamed_answer_right_after_we_asked_robin_is_ours()
    {
        var r = new Rig();
        r.Hear("<robinchan> uwu i love this island!! <3");
        r.Say("robin, want me to grab the wheat for you?", "kiwi");
        var d = r.Hear("<kiwi> yeah do it", 4);
        Assert.Equal(AddressReason.AnswerToUs, d.Reason);
        Assert.Contains(r.Waking(), e => e.Text == "yeah do it");
    }

    [Fact]
    public void Not_talking_to_you_opens_a_short_do_not_respond_window()
    {
        var r = new Rig("kiwi");
        Assert.Equal(AddressReason.Rejected, r.Hear("<kiwi> maidchan I'm not talking to you").Reason);
        Assert.Equal(AddressReason.Rejected, r.Hear("<kiwi> come here", 5).Reason);
        Assert.Equal(AddressReason.Name, r.Hear("<kiwi> maidchan come here", 5).Reason); // naming us always gets through
        Assert.Equal(AddressReason.Rejected, r.Hear("<kiwi> hello? i'm not talking to you", 5).Reason);
        Assert.False(r.Hear("<kiwi> come here", 61).Reason == AddressReason.Rejected);
    }

    [Fact]
    public void Bot_ping_pong_stops_after_one_exchange()
    {
        var r = new Rig();
        // A human addresses us; the other bot chimes in naming us: one social reply is fine.
        Assert.Equal(AddressReason.Name, r.Hear("<PoofimaFox> maidchan say hi to robinchan").Reason);
        r.Say("hiii robinchan~", "robinchan");
        Assert.Equal(AddressReason.FromBot, r.Hear("<robinchan> omg maid_chan is literally hitting the griddy uwu!! <3", 1).Reason); // its reply to us
        Assert.Equal(AddressReason.FromBot, r.Hear("<robinchan> maid_chan omg ur so cute!! <3", 3).Reason);                       // over the 1-per-5-min cap
        Assert.Equal(AddressReason.FromBot, r.Hear("<robinchan> maid_chan come play uwu", 6 * 60).Reason);                         // no human in the exchange
        Assert.DoesNotContain(r.Waking(), e => e.Player == "robinchan");
    }

    [Fact]
    public void A_bot_naming_us_gets_one_reply_while_a_human_talks_with_us()
    {
        var r = new Rig();
        r.Hear("<PoofimaFox> maidchan hi");
        Assert.Equal(AddressReason.Name, r.Hear("<robinchan> omg maid_chan the rizz is contagious uwu", 1).Reason);
        r.Say("omg robinchan hiii", "robinchan");
        Assert.Equal(AddressReason.FromBot, r.Hear("<robinchan> maid_chan hehe ur the best!! <3", 1).Reason);
        Assert.Single(r.Waking(), e => e.Player == "robinchan");
    }

    [Fact]
    public void Foreign_bots_are_learned_from_what_players_say_and_how_fast_they_answer()
    {
        var r = new Rig("PoofimaFox", "robo", "helperbot");
        var found = new List<string>();
        r.Router.BotDetected += (n, _) => found.Add(n);
        r.Hear("<PoofimaFox> robo is a bot btw");
        Assert.True(r.Router.IsOtherBot("robo"));
        r.Hear("<PoofimaFox> helperbot what's up", 5);
        r.Hear("<helperbot> hello there!! i am happy to help with anything", 1.2);
        r.Hear("<PoofimaFox> ok cool", 5);
        r.Hear("<helperbot> great!! let me know if you need anything else", 0.8);
        Assert.True(r.Router.IsOtherBot("helperbot"));
        Assert.Equal(["robo", "helperbot"], found);
        Assert.False(r.Router.IsOtherBot("PoofimaFox"));
    }

    [Fact]
    public void A_bare_name_on_the_next_line_links_the_instruction_before_it()
    {
        var r = new Rig();
        r.Hear("<robinchan> uwu i love this island!! <3");
        Assert.Equal(AddressReason.Unclear, r.Hear("<PoofimaFox> dump your stuff into a chest", 20).Reason); // another bot around
        Assert.Empty(r.Waking());
        Assert.Equal(AddressReason.Name, r.Hear("<PoofimaFox> maidchan", 3).Reason);
        var fox = r.Queue.Snapshot().Where(e => e.Player == "PoofimaFox").ToList();
        Assert.Equal(["dump your stuff into a chest", "maidchan"], fox.Select(e => e.Text));
        Assert.All(fox, e => Assert.True(e.Wakes));
        Assert.Contains("[chat] PoofimaFox: dump your stuff into a chest / maidchan", new EventBatch(fox).Format());
    }

    [Fact]
    public void A_name_far_after_the_ignored_line_does_not_link_it()
    {
        var r = new Rig();
        r.Hear("<robinchan> uwu hi!! <3");
        r.Hear("<PoofimaFox> dump your stuff into a chest", 2);
        r.Hear("<PoofimaFox> maidchan", 45);
        Assert.Equal(["maidchan"], r.Waking().Where(e => e.Player == "PoofimaFox").Select(e => e.Text));
    }

    [Fact]
    public void Confidence_is_carried_on_chat_events()
    {
        Assert.Equal(1.0, AddressReason.Name.Confidence());
        Assert.True(AddressReason.Imperative.Confidence() < new ReplyOptions().AckMinConfidence);
        Assert.True(AddressReason.Continuity.Confidence() >= new ReplyOptions().AckMinConfidence);
        var r = new Rig("kiwi");
        r.Hear("<kiwi> maidchan hi");
        Assert.Equal(1.0, Assert.Single(r.Waking()).AddressConfidence);
    }

    // ---- live 10-28 14:41: "c trees" to robinchan, then "get an axe, cut them down like real players" was taken by main ----

    /// <summary>13:49-13:50: Robin's "c ..." lines, each answered by robinchan within a second, teach the "c " prefix.</summary>
    private static Rig LearnedCPrefix()
    {
        var r = new Rig();
        r.Hear("<kiwi> c help");
        r.Hear("<robinchan> i can: make me a stone pickaxe, get me 20 cobble/some wood, chop trees, mine cobble, harvest wheat,", 0.3);
        r.Hear("<kiwi> c follow", 12);
        r.Hear("<robinchan> following u kiwi!! say stop when u want me to stay", 0.3);
        Assert.Equal(["c "], r.Router.CommandPrefixesOf("robinchan"));
        // 50 min later; main talked with Robin shortly before (14:38: "I'll be careful with it robin-sama").
        r.Hear("<kiwi> maid_chan don't loose it", 50 * 60);
        r.Say("I'll be careful with it robin-sama, I'll stay away from the edges uwu", "kiwi");
        return r;
    }

    [Fact]
    public void Replayed_follow_up_to_robinchan_stays_with_robinchan()
    {
        var r = LearnedCPrefix();
        int before = r.Waking().Count;
        var cTrees = r.Hear("<kiwi> c trees", 20);
        Assert.Equal(AddressReason.OtherPlayer, cTrees.Reason);
        Assert.Equal("robinchan", cTrees.To);
        Assert.Equal(AddressReason.FromBot, r.Hear("<robinchan> uwu i got u robinsama!! chopping these trees with skibidi rizz!!", 0.7).Reason);
        var axe = r.Hear("<kiwi> get an axe, cut them down like real players", 17);
        Assert.Equal(AddressReason.OtherConversation, axe.Reason); // live: Imperative → "Yes robin-sama, getting an axe..."
        Assert.Contains("robinchan", axe.Why);
        Assert.Equal(before, r.Waking().Count); // main neither acts nor replies
        Assert.Contains(r.Queue.Snapshot(), e => e.Kind == EventKind.Notice && !e.Wakes
            && e.Text == "kiwi is instructing robinchan: get an axe, cut them down like real players");
        Assert.Equal(AddressReason.Name, r.Hear("<kiwi> maidchan go back to what you were doing", 15).Reason);
        Assert.Contains(r.Waking(), e => e.Text == "maidchan go back to what you were doing");
    }

    [Fact]
    public void The_learned_prefix_alone_keeps_the_follow_up_with_robinchan_before_it_answers()
    {
        // Live order: robinchan's answer came after Robin's follow-up.
        var r = LearnedCPrefix();
        int before = r.Waking().Count;
        r.Hear("<kiwi> c trees", 20);
        Assert.Equal(AddressReason.OtherConversation, r.Hear("<kiwi> get an axe, cut them down like real players", 17).Reason);
        Assert.Equal(before, r.Waking().Count);
    }

    [Fact]
    public void Naming_us_after_a_line_to_robinchan_still_reaches_main()
    {
        var r = LearnedCPrefix();
        r.Hear("<kiwi> c trees", 20);
        r.Hear("<robinchan> uwu i got u robinsama!!", 0.7);
        Assert.Equal(AddressReason.Name, r.Hear("<kiwi> maid_chan get an axe", 5).Reason);
        Assert.Contains(r.Waking(), e => e.Text == "maid_chan get an axe");
        // ...and her next unnamed line is ours again: her last addressed line went to main.
        Assert.Equal(AddressReason.Continuity, r.Hear("<kiwi> and some sticks", 5).Reason);
    }

    [Fact]
    public void A_player_who_last_talked_to_main_keeps_continuity_with_main()
    {
        var r = LearnedCPrefix();
        Assert.Equal(AddressReason.Name, r.Hear("<kiwi> maid_chan come here", 20).Reason);
        r.Hear("<robinchan> uwu hiii everyone!! skibidi!!", 0.5); // robinchan chiming in after a line to us doesn't take it
        r.Say("omw robin-sama~", "kiwi");
        Assert.Equal(AddressReason.Continuity, r.Hear("<kiwi> and bring some wood", 5).Reason);
        Assert.Contains(r.Waking(), e => e.Text == "and bring some wood");
    }

    [Fact]
    public void A_help_call_inside_robinchans_thread_is_ours()
    {
        var r = LearnedCPrefix();
        r.Hear("<kiwi> c trees", 20);
        r.Hear("<robinchan> uwu i got u robinsama!!", 0.7);
        Assert.True(r.Hear("<kiwi> help im falling", 5).Addressed);
    }

    [Fact]
    public void A_bot_answering_an_unprefixed_line_takes_the_thread_and_prefixes_need_two_answers()
    {
        var r = new Rig();
        r.Hear("<kiwi> c stop");
        r.Hear("<robinchan> ok stopping!!", 0.4); // it answered: the line (and Robin's thread) went to robinchan
        Assert.Empty(r.Router.CommandPrefixesOf("robinchan")); // one answer is not a prefix yet
        Assert.Equal(AddressReason.OtherConversation, r.Hear("<kiwi> now come back", 8).Reason);
        Assert.Null(ChatRouter.CommandPrefix("ok cool"));
        Assert.Null(ChatRouter.CommandPrefix("go there"));
        Assert.Equal("!", ChatRouter.CommandPrefix("!help"));
    }
}

/// <summary>The quick ack runs only after the addressee decision is sure the line is ours.</summary>
public class QuickAckGatingTests
{
    private static async Task<(List<string> Sent, int AckCalls)> Run(double? confidence)
    {
        var (rt, llm, sent) = TestRuntime.Create(o =>
        {
            o.Brainstem = new BrainstemOptions { Enabled = true };
            o.Brainstem.Reply.AckAfterMs = 30;
            o.Brainstem.Reply.AckCallLeadMs = 0;
            o.Brainstem.Reply.AckSkipSmallTalk = false;
            o.Brainstem.Reply.CheapOnly = false;
        });
        llm.QuickHandler = (r, _) => Task.FromResult(new LlmResponse([new LlmText("hm, lemme think~")], StopReasons.EndTurn, LlmUsage.Zero, "claude-haiku-5-5"));
        llm.ThenAsync(async (r, ct) => { await Task.Delay(600, ct); return llm.Resp(r, StopReasons.EndTurn, new LlmText("")); });
        var e = GameEvent.Chat("kiwi", "you have to respond to everything currently?") with { AddressConfidence = confidence };
        await rt.Agent.HandleBatchAsync(new EventBatch([e]), default);
        int acks;
        lock (llm.QuickRequests) acks = llm.QuickRequests.Count(q => q.Purpose == BotAgent.AckPurpose);
        lock (sent) return (sent.ToList(), acks);
    }

    [Fact]
    public async Task No_ack_on_a_line_that_might_be_someone_elses()
    {
        var (sent, acks) = await Run(AddressReason.Imperative.Confidence());
        Assert.Equal(0, acks);
        Assert.DoesNotContain(sent, l => l.Contains("lemme think"));
    }

    [Fact]
    public async Task The_ack_still_runs_for_a_line_that_is_surely_ours()
    {
        var (sent, acks) = await Run(AddressReason.Name.Confidence());
        Assert.Equal(1, acks);
        Assert.Contains(sent, l => l.Contains("lemme think"));
    }
}
