using MineClaw.Hive.Supervisor;

namespace MineClaw.Hive.Tests;

/// <summary>Item 18: a clone's deliberate void step (into_void) is an intentional reset — never a failure, a death record or a retirement.</summary>
[Collection("processes")]
public class VoidResetHiveTests
{
    [Fact]
    public async Task An_intentional_void_reset_keeps_the_clone_online_its_claims_and_no_death_record()
    {
        await using var h = new TestHive(mode: "normal", stubArgs: ["--stacks", "2", "--chest", "27"]);
        Assert.StartsWith("ok", h.Supervisor.Spawn("claim: void reset", "67"));
        var slot = h.Supervisor.Slot("maid_chan_67")!;
        await Wait.Until(() => slot.Progress > 0, what: "claim progress");
        await Wait.Until(() => slot.IntentionalResets == 1, what: "intentional reset report");
        Assert.Equal(0, slot.Deaths);
        Assert.Equal(0, h.Store.CountEvents("death", "maid_chan_67"));
        Assert.Equal(1, h.Board.CountOf("maid_chan_67"));    // its claims stay: it respawns and carries on
        Assert.Equal(CloneState.Running, slot.State);
        Assert.DoesNotContain("died", h.Supervisor.List());
        await h.Supervisor.RecallAllAsync("test over", TimeSpan.FromSeconds(20));
    }
}
