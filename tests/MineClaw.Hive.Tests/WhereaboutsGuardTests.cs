using MineClaw.Agent;
using MineClaw.Hive.Ipc;
using MineClaw.Hive.Supervisor;

namespace MineClaw.Hive.Tests;

/// <summary>
/// Live 10-29: "maid tell 420 to come here" → main: "omw~", then "420 is right next to u now foxy~"; Fox: "no 420 is in a pool".
/// No order had gone out (main's goto with body 420 walked main itself) and no arrival came back. Main states a clone's
/// whereabouts only when it sees that clone beside a player; otherwise the line says what is true.
/// </summary>
public class WhereaboutsGuardTests
{
    private static readonly TurnInfo Fox = new("PoofimaFox", ["PoofimaFox"], false);

    private static CloneSlot Live(TestHive h, string name, object? turn = null)
    {
        var s = h.Supervisor.Slot(name)!;
        s.Task = new TaskMsg("t-" + name, "deforest around log_chest");
        s.State = CloneState.Running;
        s.Connected = true;
        s.AssignedTurn = turn;
        return s;
    }

    [Fact]
    public async Task A_relay_with_no_ack_never_becomes_an_arrival_claim()
    {
        await using var h = new TestHive();
        TurnContext.Current = Fox;
        try
        {
            Live(h, "maid_chan_420");
            h.Supervisor.NearPlayer = _ => null; // main can't see 420 (it's in a pool across the forest)
            var said = h.Supervisor.GuardSpeech("omw~ 420 is right next to u now foxy~");
            Assert.DoesNotContain("next to u", said);
            Assert.Contains("420 isn't with you yet", said);
            Assert.StartsWith("omw~", said);

            // The order went out this turn (hive handover): say that, not the arrival.
            Live(h, "maid_chan_420", turn: Fox);
            var told = h.Supervisor.GuardSpeech("420 is here now foxy!");
            Assert.Equal("told 420 to come, I'll say when it's there.", told);

            // Main sees 420 standing by Fox: true, and it stays.
            h.Supervisor.NearPlayer = n => n == "maid_chan_420" ? "PoofimaFox" : null;
            Assert.Equal("420 is right next to u now foxy~", h.Supervisor.GuardSpeech("420 is right next to u now foxy~"));
            // Lines that aren't whereabouts are untouched.
            Assert.Equal("420 is chopping the spruce by the river", h.Supervisor.GuardSpeech("420 is chopping the spruce by the river"));
        }
        finally { TurnContext.Current = null; }
    }
}
