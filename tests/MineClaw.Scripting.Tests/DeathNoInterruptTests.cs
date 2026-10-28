using MineClaw.Agent.Events;
using MineClaw.Bot.Tests;

namespace MineClaw.Scripting.Tests;

/// <summary>
/// Live 10-28 03:16: "interrupted script #12 due to [noticed] PoofimaFox: PoofimaFox fell into the void and died off the main
/// island" and gen_loop stayed paused until the brain restarted it. A human's death wakes the brain; the script keeps the body.
/// </summary>
public class DeathNoInterruptTests
{
    [Fact]
    public async Task A_regulars_death_wakes_the_brain_and_the_gen_script_keeps_running()
    {
        var llm = new CountingLlm();
        var (h, _, b) = BotScriptTests.Bot(o => { o.IdleCheckInMinutes = 20; o.InlineToolWaitSeconds = 20; },
            new ScriptOptions { MaxPauseMinutes = 600, ChatAutoResumeSeconds = 6000 }, llm, new ScaledTime(600));
        using var cts = new CancellationTokenSource();
        _ = h.Runtime.Agent.RunAsync(cts.Token);
        try
        {
            await Task.Delay(50);
            await h.Run("script", new { op = "run", name = "gen_loop", code = "for i = 1, 30 do wait(60) end" });
            await Task.Delay(200);
            // What DangerMonitor posts for a human's void death now: Normal priority, MustWake.
            h.Runtime.Events.Post(GameEvent.Activity("PoofimaFox fell into the void and died off the main island", "PoofimaFox", EventPriority.Normal)
                with { MustWake = true });
            await Rig.Until(() => llm.Brain >= 1, 5000);
            Assert.Contains("PoofimaFox fell into the void and died", llm.LastBrainText());
            var s = b.Controller.Current;
            Assert.NotNull(s);
            Assert.NotEqual(ScriptStatus.Paused, s!.Status);
        }
        finally { b.Controller.Cancel(); cts.Cancel(); }
    }
}
