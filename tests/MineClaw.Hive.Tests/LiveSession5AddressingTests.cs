using MineClaw.Agent.Chat;
using MineClaw.Agent.Events;
using MineClaw.Hive.Chat;
using MineClaw.Hive.Clone;

namespace MineClaw.Hive.Tests;

/// <summary>
/// User requirement (live 10-27 evening): when Fox or Robin talk without naming a bot, MAIN is the addressee. A clone answers
/// only when named ("67 ...", "maid_chan_67") or as the continuation of its own conversation (it talked with that player in the
/// last ~30 s, or main handed the player's request to it). No bouncing, no duplicate extra requests.
/// </summary>
public class LiveSession5AddressingTests
{
    private const string Main = "maid_chan", Clone = "maid_chan_67";
    private static readonly string[] MainNames = ["claudechan", "claude chan", "claude", "maid-chan", "maid chan", "maid", Main];
    private static readonly string[] Clones = [Clone, "maid_chan_69", "maid_chan_420", "maid_chan_1337"];

    private sealed class Clock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now = start;
        public override DateTimeOffset GetUtcNow() => Now;
        public void Advance(double s) => Now = Now.AddSeconds(s);
    }

    private sealed class Proc
    {
        public required ChatRouter Chat;
        public required EventQueue Queue;
        public required ChatArbiter Arbiter;

        public List<GameEvent> Feed(string raw)
        {
            Chat.OnServerChat(raw);
            return Queue.TryTakeBatch()?.Waking.Where(e => e.Kind is EventKind.Chat or EventKind.Emote).ToList() ?? [];
        }
    }

    private static Proc Build(string self, TimeProvider time)
    {
        bool isMain = self == Main;
        var arb = new ChatArbiter(self, Main, Clones, mainNames: MainNames, time: time);
        var num = ChatArbiter.NumberOf(self);
        var opts = new ChatOptions
        {
            Username = self, LineDelayMs = 0,
            DisplayName = isMain ? "claudechan" : "claudechan " + num,
            Aliases = isMain ? ["claude chan", "claude", "maid-chan", "maid chan", "maid"] : ["claudechan " + num],
        };
        var q = new EventQueue(new EventQueueOptions { CoalesceWindowMs = 1, MaxBatchDelayMs = 1 }, time);
        var chat = new ChatRouter(opts, q, (_, _) => Task.CompletedTask, time);
        chat.Commands = new CommandGate(new CommandOptions(), () => ["kiwi", "poofimafox"], time);
        if (isMain) { chat.IsHiveBot = arb.IsHiveBot; chat.Arbiter = arb.Decide; }
        else ClonePolicy.Apply(chat, q, arb);
        chat.SetOnlinePlayers(["PoofimaFox", "kiwi", Main, Clone]);
        return new Proc { Chat = chat, Queue = q, Arbiter = arb };
    }

    private static (Proc Main, Proc Clone, Clock Time) Pair()
    {
        var time = new Clock(new DateTimeOffset(2026, 10, 27, 21, 50, 0, TimeSpan.Zero));
        var m = Build(Main, time);
        var c = Build(Clone, time);
        m.Arbiter.Changed += () => c.Arbiter.Apply(m.Arbiter.ToRoster());
        c.Chat.Accepted += msg => { if (msg.Player != null) m.Arbiter.AcceptFromClone(msg.Player, Clone); }; // chat_ok over IPC
        m.Arbiter.SetLive([Clone], MainNames);
        return (m, c, time);
    }

    /// <summary>Both processes see the same server line; returns (main woke, clone woke).</summary>
    private static (bool Main, bool Clone) Say(Proc m, Proc c, string raw) => (m.Feed(raw).Count > 0, c.Feed(raw).Count > 0);

    [Fact]
    public void Unaddressed_request_goes_to_main_only()
    {
        var (m, c, _) = Pair();
        Assert.Equal((true, false), Say(m, c, "<PoofimaFox> make bread"));
        Assert.Equal((true, false), Say(m, c, "<kiwi> chop the big oak"));
        // Not bot-directed at all: main's own rules (and its classifier live) decide; a clone never takes it.
        Assert.Equal(Main, c.Arbiter.OwnerOf(c.Chat.Parse("<PoofimaFox> I need torches")));
        Assert.False(Say(m, c, "<PoofimaFox> I need torches").Clone);
    }

    [Fact]
    public void Named_clone_only_then_its_thread_for_30_s_then_back_to_main()
    {
        var (m, c, t) = Pair();
        Assert.Equal((false, true), Say(m, c, "<PoofimaFox> 67 come here"));
        t.Advance(20);
        Assert.Equal((false, true), Say(m, c, "<PoofimaFox> thanks"));          // continuation of 67's conversation
        t.Advance(60);
        // The thread is over: main owns Fox's lines again (its own rules decide whether a bare "thanks" needs an answer).
        Assert.Equal(Main, m.Arbiter.OwnerOf(m.Chat.Parse("<PoofimaFox> thanks")));
        Assert.Equal(Main, c.Arbiter.OwnerOf(c.Chat.Parse("<PoofimaFox> thanks")));
        Assert.False(Say(m, c, "<PoofimaFox> thanks").Clone);
        Assert.Equal((true, false), Say(m, c, "<PoofimaFox> make bread"));
    }

    [Fact]
    public void A_clone_thread_is_about_30_s_not_the_45_s_sticky()
    {
        var (m, c, t) = Pair();
        Assert.Equal((false, true), Say(m, c, "<PoofimaFox> 67 come here"));
        t.Advance(35);
        Assert.Equal((true, false), Say(m, c, "<PoofimaFox> make bread"));
    }

    [Fact]
    public void A_request_main_handed_to_a_clone_stays_with_the_clone()
    {
        var (m, c, t) = Pair();
        Assert.Equal((true, false), Say(m, c, "<PoofimaFox> make me torches"));
        m.Arbiter.Delegated("PoofimaFox", Clone); // main's hive assign: Fox's torch request is 67's now
        t.Advance(10);
        Assert.Equal((false, true), Say(m, c, "<PoofimaFox> just like 5"));
        t.Advance(45);
        Assert.Equal((true, false), Say(m, c, "<PoofimaFox> make bread")); // the delegation window is over
    }

    [Fact]
    public void A_delegation_never_steals_the_answer_main_is_waiting_for()
    {
        var (m, c, t) = Pair();
        m.Arbiter.OpenThread("PoofimaFox", Main, TimeSpan.FromSeconds(60)); // main asked Fox something
        m.Arbiter.Delegated("PoofimaFox", Clone);
        Assert.Equal(Main, m.Arbiter.OwnerOf(m.Chat.Parse("<PoofimaFox> the big oak")));
        Assert.False(Say(m, c, "<PoofimaFox> the big oak").Clone);
    }

    [Fact]
    public async Task A_clone_that_spoke_to_the_player_keeps_their_next_line_for_30_s()
    {
        var (m, c, t) = Pair();
        c.Chat.SpokeTo += p => m.Arbiter.SpokeFromClone(p, Clone); // chat_ok{spoke} over IPC
        Assert.Equal((false, true), Say(m, c, "<PoofimaFox> 67 come here"));
        t.Advance(25);
        await c.Chat.SayAsync("on my way, master!", audience: ["PoofimaFox"]); // refreshes the 30 s window at 25 s
        t.Advance(20);
        Assert.Equal((false, true), Say(m, c, "<PoofimaFox> thanks"));
        t.Advance(31);
        Assert.Equal(Main, m.Arbiter.OwnerOf(m.Chat.Parse("<PoofimaFox> thanks")));
    }
}
