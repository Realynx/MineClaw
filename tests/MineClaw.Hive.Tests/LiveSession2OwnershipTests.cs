using MineClaw.Agent;
using MineClaw.Agent.Chat;
using MineClaw.Agent.Events;
using MineClaw.Hive.Chat;
using MineClaw.Hive.Supervisor;

namespace MineClaw.Hive.Tests;

/// <summary>
/// Second live session: one bot owns a conversation thread (main and 67 both started watch_demo for one message), and
/// main never reports a clone's result before it arrives ("the clone dropped off about 11 too" while 67 was still mining).
/// </summary>
public class LiveSession2OwnershipTests
{
    private static readonly string[] Clones = ["maid_chan_67", "maid_chan_69", "maid_chan_420", "maid_chan_1337"];

    private static (ChatRouter Router, EventQueue Events) Bot(string name, ChatArbiter a, string display)
    {
        var q = new EventQueue(new EventQueueOptions { DedupeWindowSeconds = 0 });
        var r = new ChatRouter(new ChatOptions { Username = name, DisplayName = display, Aliases = [], LineDelayMs = 0 }, q, (_, _) => Task.CompletedTask);
        a.Bind(r);
        r.SetOnlinePlayers(["PoofimaFox", .. Clones, "maid_chan"]);
        return (r, q);
    }

    private static int Addressed(EventQueue q) =>
        q.Snapshot().Count(e => e.Kind is EventKind.Chat && e.Priority >= EventPriority.Normal);

    [Fact]
    public async Task The_bot_that_asked_owns_the_reply_until_it_is_resolved()
    {
        var main = new ChatArbiter("maid_chan", "maid_chan", Clones, ["maid-chan", "maid_chan"]);
        main.SetLive(["maid_chan_67"]);
        var clone = new ChatArbiter("maid_chan_67", "main", Clones);
        clone.Apply(main.ToRoster());
        // IPC both ways: the clone's lease reaches main (ThreadMsg); main's roster reaches the clone.
        clone.LocalThreadChanged += (p, open, ttl) => { if (open) main.OpenThread(p, "maid_chan_67", ttl); else main.CloseThread(p, "maid_chan_67"); };
        main.Changed += () => clone.Apply(main.ToRoster());
        var (mainChat, mainQ) = Bot("maid_chan", main, "maid-chan");
        var (cloneChat, cloneQ) = Bot("maid_chan_67", clone, "maid-chan 67");

        // Main talked with Fox last (sticky), then 67 asks Fox which cell is its own.
        main.Accept("PoofimaFox", "maid_chan");
        var lease = cloneChat.HoldThread("PoofimaFox", TimeSpan.FromSeconds(60));
        Assert.Equal("maid_chan_67", main.ThreadOwner("PoofimaFox"));
        Assert.Equal("maid_chan_67", main.ThreadOwner("poofimafox")); // config casing vs server casing
        var mainWaiter = mainChat.WaitForReplyAsync("PoofimaFox", TimeSpan.FromMilliseconds(400), default); // main mid-question too
        var cloneWaiter = cloneChat.WaitForReplyAsync("PoofimaFox", TimeSpan.FromSeconds(5), default);

        const string line = "<PoofimaFox> I gave it a wooden pickaxe, watch me punch the correct brick to mine for your clone";
        mainChat.OnServerChat(line);
        cloneChat.OnServerChat(line);
        Assert.Equal("PoofimaFox: I gave it a wooden pickaxe, watch me punch the correct brick to mine for your clone", await cloneWaiter);
        Assert.Null(await mainWaiter);        // main's waiter never eats 67's reply
        Assert.Equal(0, Addressed(mainQ));     // and main doesn't start its own watch_demo for it

        // Naming another bot still reaches it, lease or not.
        mainChat.OnServerChat("<PoofimaFox> maid-chan come here");
        Assert.Equal(1, Addressed(mainQ));

        // Resolved: the thread is released and replies go back to normal routing.
        lease.Dispose();
        Assert.Null(main.ThreadOwner("PoofimaFox"));
    }

    [Fact]
    public void A_lease_held_by_a_clone_that_left_falls_back_to_main()
    {
        var main = new ChatArbiter("maid_chan", "maid_chan", Clones);
        main.SetLive(["maid_chan_67"]);
        main.OpenThread("PoofimaFox", "maid_chan_67", TimeSpan.FromSeconds(60));
        Assert.Equal("maid_chan_67", main.OwnerOf(new ChatMessage(ChatKind.Public, "PoofimaFox", "the left one", "")));
        main.Forget("maid_chan_67");
        main.SetLive([]);
        Assert.Equal("maid_chan", main.OwnerOf(new ChatMessage(ChatKind.Public, "PoofimaFox", "the left one", "")));
    }

    [Fact]
    public void Roster_carries_thread_leases_to_clones()
    {
        var main = new ChatArbiter("maid_chan", "maid_chan", Clones);
        main.SetLive(["maid_chan_67", "maid_chan_69"]);
        main.OpenThread("PoofimaFox", "maid_chan_67", TimeSpan.FromSeconds(60));
        var c69 = new ChatArbiter("maid_chan_69", "main", Clones);
        c69.Apply(main.ToRoster());
        var msg = new ChatMessage(ChatKind.Public, "PoofimaFox", "the west one", "");
        Assert.True(c69.OwnedElsewhere(msg));
        Assert.False(c69.Decide(msg, locallyAddressed: true));
    }

    [Fact]
    public async Task Pending_clone_results_show_in_the_hint_and_are_never_reported_early()
    {
        await using var h = new TestHive();
        Assert.StartsWith("ok", h.Supervisor.Spawn("mine 10 cobblestone at the west cell", "67"));
        var slot = h.Supervisor.Slot("maid_chan_67")!;
        Assert.True(slot.Pending);
        Assert.StartsWith("[hive: 67 mining (pending)]", h.Supervisor.HintLine());
        Assert.Contains("(pending, no result yet)", h.Supervisor.List());

        var said = h.Supervisor.GuardSpeech("Done foxy! I put 11 cobble in the chest and the clone dropped off about 11 too, so roughly 22 total uwu.");
        Assert.Contains("I put 11 cobble in the chest", said);
        Assert.DoesNotContain("dropped off", said);
        Assert.Contains("67's still on its part", said);
        Assert.DoesNotContain("ready", h.Supervisor.GuardSpeech("67 has the new picks ready uwu"));
        // Talking about it working is fine.
        Assert.Equal("67 is mining the west cell rn", h.Supervisor.GuardSpeech("67 is mining the west cell rn"));
    }

    [Fact]
    public async Task Main_never_hands_a_clones_player_job_to_another_clone_unless_explicit()
    {
        await using var h = new TestHive();
        TurnContext.Current = new TurnInfo("PoofimaFox", ["PoofimaFox"], false);
        try
        {
            Assert.StartsWith("ok", h.Supervisor.Spawn("build 2 more rows of planks on the west edge", "67"));
            // Same request, another clone: refused, 67 owns it.
            var r = h.Supervisor.Spawn("build 2 more rows of cobble on the west edge", "69");
            Assert.StartsWith("no: 67 already owns PoofimaFox's request", r);
            // Explicit handover: allowed, and the previous owner is told.
            Assert.StartsWith("ok", h.Supervisor.Spawn("build 2 more rows of cobble on the west edge", "69", handover: true));
            Assert.Contains("handed PoofimaFox's request from 67 to 69", h.NoticeText());
        }
        finally { TurnContext.Current = null; }
    }
}
