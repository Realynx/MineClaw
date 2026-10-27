using MineClaw.Bot.Tests;
using MineClaw.Data;

namespace MineClaw.Scripting.Tests;

/// <summary>2026-10-27 night log, B15: clone scripts failed on a muted say() and guessed the deposit signature wrong.</summary>
public class NightLogScriptTests
{
    private static Task<Agent.Tools.ToolResult> Script(Bot.BotHost h, object input) => h.Run("script", input);

    [Fact]
    public async Task A_muted_say_is_report_output_not_a_failure()
    {
        // 67 at 06:56:12: "script script Failed: 5 actions in 0s — failed: script script at line 1 (say: muted (hive clone speaks
        // only when addressed))" after printing its scan; 07:37:06 a chop script died on say after +4 oak wood.
        var (h, c, _) = BotScriptTests.Bot(scripts: new ScriptOptions { SayMinIntervalSeconds = 0 });
        h.Runtime.Agent.SpeechGate = (_, _, _) => null; // hive clone: speaks only when addressed
        var r = await Script(h, new { op = "run", code = "mine{'cobblestone',2} say'got the cobble, heading back'" });
        Assert.True(r.Ok, r.Text);
        Assert.Contains("report: got the cobble, heading back", r.Text);
        var r2 = await Script(h, new { op = "run", code = "say'west cell is cobble, east is water' return 'scanned'" });
        Assert.True(r2.Ok, r2.Text);
        Assert.Contains("scanned", r2.Text);
        Assert.Contains("report: west cell is cobble, east is water", r2.Text);
        await Task.Delay(100);
        Assert.Empty(c.Said);
    }

    [Fact]
    public async Task Deposit_with_an_item_or_all_where_the_chest_goes_still_deposits()
    {
        // 67 at 07:41:34 / 07:41:36: "deposit: unknown place 'all'", "deposit: unknown place 'stone_axe'".
        var (h, c, _) = BotScriptTests.Bot();
        c.World.SetBlock(2, 64, 2, Blocks.Chest, 0);
        var r = await Script(h, new { op = "run", code = "local a, m1 = deposit{'all'} local b, m2 = deposit{'stone_axe'}\nreturn tostring(a) .. tostring(b) .. ' ' .. m1 .. ' | ' .. m2" });
        Assert.True(r.Ok, r.Text);
        Assert.Contains("truetrue", r.Text);
        Assert.Contains("deposit 2,64,2 filter=False", c.CallList);
        Assert.Contains("deposit 2,64,2 filter=True", c.CallList);  // only the stone axe
        var bad = await h.Runtime.Tools.ExecuteAsync("deposit", TestBot.J(new { at = "nowhere_special" }), CancellationToken.None);
        Assert.False(bad.Ok);
        Assert.Contains("deposit{at?, items?}", bad.Text);
    }
}
