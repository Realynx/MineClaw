using MineClaw.Scripting;

namespace MineClaw.Scripting.Tests;

/// <summary>Second live session: `toss`, `false` glued onto a result, ok after a failed last action, unknown skill names.</summary>
public class LiveSession2ScriptTests
{
    [Fact]
    public async Task Toss_and_give_are_aliases_for_give_items()
    {
        using var rig = new Rig();
        var s = await rig.Run("toss{'PoofimaFox', 'log', 5}\ngive{'PoofimaFox', 'planks', 2}\nlocal ok = skill('toss', {player='PoofimaFox', item='log'})\nreturn 'gave'");
        Assert.Equal(ScriptStatus.Done, s.Status);
        Assert.Equal(3, rig.CallList.Count(c => c.StartsWith("skill:give_items")));
        Assert.DoesNotContain(rig.CallList, c => c.Contains("no skill"));
    }

    [Fact]
    public async Task Glued_false_result_is_a_failure_not_ok()
    {
        using var rig = new Rig();
        rig.On["skill:give_items"] = (_, _) => Task.FromResult(ActResult.Fail("player too far"));
        var a = await rig.Run("local ok, m = toss{'PoofimaFox', 'log', 5}\nreturn tostring(ok) .. m", "script");
        Assert.Equal(ScriptStatus.Failed, a.Status);
        Assert.StartsWith("failed: script script", a.Outcome);
        Assert.Contains("(give_items: player too far)", a.Outcome);
        Assert.DoesNotContain("false", a.Outcome);

        var b = await rig.Run("return toss{'PoofimaFox', 'log', 5}", "script");
        Assert.Equal(ScriptStatus.Failed, b.Status);
        Assert.DoesNotContain("ok:", b.Outcome);

        var c = await rig.Run("toss{'PoofimaFox', 'log', 5}", "script");
        Assert.Equal(ScriptStatus.Failed, c.Status);
        Assert.Contains("give_items: player too far", c.Outcome);
    }

    [Fact]
    public async Task Handled_failures_still_report_ok()
    {
        using var rig = new Rig();
        rig.On["skill:give_items"] = (_, _) => Task.FromResult(ActResult.Fail("player too far"));
        var a = await rig.Run("local ok, m = toss{'PoofimaFox', 'log', 5}\nif not ok then drop{'log', 5} end");
        Assert.Equal(ScriptStatus.Done, a.Status);
        var b = await rig.Run("local ok, m = toss{'PoofimaFox', 'log', 5}\nreturn 'dropped them instead'");
        Assert.Equal(ScriptStatus.Done, b.Status);
        Assert.Contains("dropped them instead", b.Outcome);
        var c = await rig.Run("local ok = toss{'PoofimaFox', 'log', 5}\nreturn true");
        Assert.Equal(ScriptStatus.Done, c.Status);
    }

    [Fact]
    public async Task Leading_boolean_is_not_glued_into_the_done_text()
    {
        using var rig = new Rig();
        var s = await rig.Run("return toss{'PoofimaFox', 'log', 5}");
        Assert.Equal(ScriptStatus.Done, s.Status);
        Assert.StartsWith("ok: script t done (give_items ok", s.Outcome);
        Assert.DoesNotContain("true", s.Outcome);
    }

    [Fact]
    public async Task Unknown_skill_lists_closest_matches()
    {
        using var rig = new Rig();
        var s = await rig.Run("local ok, m = skill('harvst_cane', {})\nreturn 'r:' .. m");
        Assert.Contains("no skill 'harvst_cane'; closest: harvest_cane", s.Outcome);
        Assert.DoesNotContain(rig.CallList, c => c.StartsWith("skill:harvst"));
        Assert.Equal(["give_items"], ScriptSession.Closest("give_item", ["give_items", "move_items", "mine"], 1));
        Assert.Contains("drop_items", ScriptSession.Closest("drop", ["give_items", "drop_items", "deposit"]));
    }
}
