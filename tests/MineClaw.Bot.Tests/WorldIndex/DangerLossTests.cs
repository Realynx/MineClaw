using MineClaw.Agent;
using MineClaw.Agent.Events;
using MineClaw.Bot.WorldIndex;
using MineClaw.Data;
using MineClaw.World;
using Xunit.Abstractions;

namespace MineClaw.Bot.Tests.WorldIndex;

/// <summary>
/// Live 10-28: "interrupted script #12 due to [noticed] PoofimaFox: PoofimaFox fell into the void and died off the main island"
/// (03:16) and "interrupted script #2 due to [noticed] robinchan: robinchan was slain by Spider at the furnace" (02:23). A human's
/// death wakes the brain once without interrupting; another bot's danger or death is a Background note.
/// </summary>
public class DangerLossTests(ITestOutputHelper output)
{
    private void Dump(ActivityFixture a)
    {
        foreach (var e in a.Posted) output.WriteLine($"{(e.Wakes ? "WAKE" : "ctx ")} {e.Priority}{(e.MustWake ? " must" : "")} {e.Text}");
    }

    [Fact]
    public void A_regulars_void_death_wakes_once_and_never_interrupts()
    {
        using var a = new ActivityFixture();
        int fox = a.Player("PoofimaFox", 0.5, 64, 0.5);
        a.Step();
        a.Events.Chat("PoofimaFox fell out of the world"); // verbatim, 03:16:04
        a.Step();
        a.World.Entities.Destroy(fox);
        a.Step();
        a.Step(6);
        Dump(a);
        var w = Assert.Single(a.Posted, e => e.Wakes);
        Assert.Equal("PoofimaFox fell into the void off the main island", w.Text);
        Assert.True(w.Priority < new AgentOptions().InterruptPriority);
        Assert.True(w.MustWake); // reaches the brain even while a script sleeps
    }

    [Fact]
    public void Another_bots_fight_and_death_are_background_notes_only()
    {
        using var a = new ActivityFixture();
        a.Tracker.Danger.IsOtherBot = n => n.Equals("robinchan", StringComparison.OrdinalIgnoreCase);
        int robin = a.Player("robinchan", 0.5, 64, 0.5);
        a.World.Entities.SpawnMob(700, MobTypes.Spider, new Vec3d(1.5, 64, 0.5), 0, 0);
        a.Step();
        for (int i = 0; i < 4; i++) { a.Events.Hurt(robin); a.Step(1); } // "losing a fight with a spider (3 hits)"
        a.Events.Chat("robinchan was slain by Spider"); // verbatim, 02:23:26
        a.Step();
        a.World.Entities.Destroy(robin);
        a.Step();
        a.Step(6);
        Dump(a);
        Assert.Equal(0, a.Wakes);
        Assert.Contains(a.Posted, e => e.Text.Contains("robinchan was slain by Spider") && e.Priority == EventPriority.Background);
        Assert.Contains(a.Recorded, r => r.Kind == ActivityKinds.Died && r.Player == "robinchan"); // still in the digest
    }
}
