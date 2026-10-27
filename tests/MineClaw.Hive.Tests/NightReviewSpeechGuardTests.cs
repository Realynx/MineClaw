using System.Text.Json;
using MineClaw.Agent;
using MineClaw.Hive.Ipc;
using MineClaw.Hive.Supervisor;
using MineClaw.World;

namespace MineClaw.Hive.Tests;

/// <summary>
/// Night review 2026-10-27, main's hive speech guard (B17, B20) and the hand-off stand-still (B24). Lines are the real ones
/// from data/alice-logs/mineclaw-20261027.log.
/// </summary>
public class NightReviewSpeechGuardTests
{
    private static readonly TurnInfo Fox = new("PoofimaFox", ["PoofimaFox"], false);

    private static CloneSlot Out(TestHive h, string name, string task, object? turn = null)
    {
        var s = h.Supervisor.Slot(name)!;
        s.Task = new TaskMsg("t-" + name, task);
        s.State = CloneState.Running;
        s.Connected = true;
        s.AssignedTurn = turn;
        return s;
    }

    private static HiveMessage Frame(string type, object? payload) => new() { T = type, D = HiveJson.ToElement(payload) };

    [Fact] // B17: 07:54:53 "dropped a pending-result claim from main's chat: , the only helpers I've got are 67 and 69."
    public async Task B17_mains_own_clause_naming_the_clones_is_not_a_clone_result()
    {
        await using var h = new TestHive();
        Out(h, "maid_chan_67", "mine cobble at the gen");
        Out(h, "maid_chan_69", "chop trees");
        const string line = "I tried, but I can't summon 420 anymore, the only helpers I've got are 67 and 69.";
        Assert.Equal(line, h.Supervisor.GuardSpeech(line));
        // A real early report is still caught.
        Assert.DoesNotContain("dropped off", h.Supervisor.GuardSpeech("ok foxy, 67 dropped off the cobble"));
    }

    [Fact] // B20: 07:52:17.014 "[hive] 420 failed: spawned ... in the void"; 07:52:17.390 "Yes foxy sama <3 420 is spawning back in, ..."
    public async Task B20_a_clone_whose_outing_failed_this_turn_is_not_reported_as_spawning()
    {
        await using var h = new TestHive();
        TurnContext.Current = Fox;
        try
        {
            var s420 = Out(h, "maid_chan_420", "spawn in and get onto the island", turn: Fox);
            await h.Supervisor.HandleAsync(s420, s420.Generation, Frame(Msg.Result, new ResultMsg("t-maid_chan_420", false,
                "spawned at -6,0,-9 falling in the void, empty inventory, no path up to island (64 blocks). Can't reach base.")), default);
            Assert.True(s420.ResultIn);
            Assert.False(s420.ResultOk);

            var said = h.Supervisor.GuardSpeech("Yes foxy sama <3 420 is spawning back in, and I'm off chopping trees uwu");
            Assert.Equal("Yes foxy sama <3 420 couldn't get in, and I'm off chopping trees uwu", said);
        }
        finally { TurnContext.Current = null; }

        // A respawn asked for in a LATER turn (the line comes before the spawn call runs): the old failure says nothing.
        TurnContext.Current = new TurnInfo("PoofimaFox", ["PoofimaFox"], false);
        try { Assert.Equal("420 is spawning back in uwu", h.Supervisor.GuardSpeech("420 is spawning back in uwu")); }
        finally { TurnContext.Current = null; }
    }

    private sealed class Receiver : IHandoffReceiver
    {
        public int Holding;
        public Vec3d? Position => new Vec3d(20.5, 64, 1.5);
        public int Count;
        public void BeginCounting(string from) => Count = 0;
        public int Collected(string from) => Count;
        public IDisposable? HoldStill(TimeSpan max)
        {
            Interlocked.Increment(ref Holding);
            return new Release(this);
        }

        private sealed class Release(Receiver r) : IDisposable
        {
            private int _done;
            public void Dispose() { if (Interlocked.Exchange(ref _done, 1) == 0) Interlocked.Decrement(ref r.Holding); }
        }
    }

    [Fact] // B24: 07:49:25 67 "handoff to main: tossed 29 item(s); main collected 28" while main walked on chopping
    public async Task B24_main_stands_still_from_the_handoff_offer_until_the_clone_confirms()
    {
        await using var h = new TestHive();
        var recv = new Receiver();
        h.Supervisor.Handoff = recv;
        var s67 = Out(h, "maid_chan_67", "spar with 69");

        var offer = await h.Supervisor.HandleAsync(s67, s67.Generation, Frame(Msg.Handoff, new HandoffMsg(29)), default);
        Assert.True(Assert.IsType<HandoffReplyMsg>(offer).Ok);
        Assert.Equal(1, recv.Holding);                 // main is held at the feet it offered
        recv.Count = 29;
        var ack = await h.Supervisor.HandleAsync(s67, s67.Generation, Frame(Msg.HandoffAck, null), default);
        Assert.Equal(29, Assert.IsType<HandoffAckMsg>(ack).Collected);
        Assert.Equal(0, recv.Holding);                 // and moves again once the clone confirmed

        // A second offer replaces the first hold; a clone that never confirms can't freeze main: the client hold expires,
        // and the clone going away releases it at once.
        await h.Supervisor.HandleAsync(s67, s67.Generation, Frame(Msg.Handoff, new HandoffMsg(3)), default);
        await h.Supervisor.HandleAsync(s67, s67.Generation, Frame(Msg.Handoff, new HandoffMsg(3)), default);
        Assert.Equal(1, recv.Holding);
        Assert.True(HiveSupervisor.HandoffHoldTime <= TimeSpan.FromSeconds(20));
    }
}
