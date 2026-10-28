namespace MineClaw.Scripting.Tests.Js;

public class JsSmokeTests
{
    [Fact]
    public async Task Hello()
    {
        using var rig = Rig.Js();
        var s = await rig.Run("const r = mine('cobblestone', 4)\nlog('got', r.n)\nreturn r.ok ? 'mined ' + inv('dirt') : 'no'");
        Assert.True(s.Ok, s.Outcome + " | " + string.Join(";", rig.Logs));
        Assert.Contains("mine {\"block\":\"cobblestone\",\"count\":4}", rig.Norm());
        Assert.Contains("mined 0", s.Outcome);
    }
}
