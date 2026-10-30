using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Scripting.Tests.Js;

/// <summary>
/// Fox 10-30: "The hunt script should probably just make them go in a random direction until they encounter animals. That's how
/// that works." Bodies only see their loaded chunks; the area round the base was hunted out ("no animals within a big radius").
/// scout(query) walks out in steps (one direction per body by default) until find(query) sees one.
/// </summary>
public class ScoutTests
{
    private static Rig Walking(List<string> gotos)
    {
        var rig = Rig.Js(body: "101");
        rig.On["goto"] = (args, ct) =>
        {
            var to = args.GetProperty("to").GetString()!;
            gotos.Add(to);
            var p = to.Split(',').Select(double.Parse).ToArray();
            rig.World.Player.ServerTeleport(new Vec3d(p[0] + 0.5, p[1], p[2] + 0.5), p[1] + 1.62, 0, 0, true);
            return Task.FromResult(ActResult.Success("arrived"));
        };
        return rig;
    }

    [Fact]
    public async Task Scout_walks_out_until_it_sees_a_cow_then_stops()
    {
        var gotos = new List<string>();
        using var rig = Walking(gotos);
        rig.World.Entities.SpawnMob(50, MobTypes.Cow, new Vec3d(0.5, 65, 60.5), 0, 0, null);
        var s = await rig.Run("const r = scout('cow', { dir: 0 }); return r.ok + '|' + r.found[0].name + '|' + r.msg");
        Assert.True(s.Ok, s.Outcome);
        Assert.Contains("true|cow|found cow", s.Outcome);
        Assert.InRange(gotos.Count, 2, 3); // 16, 32 (,48) south: within 24 of the cow, no further
        Assert.All(gotos, g => Assert.StartsWith("0,", g));
    }

    [Fact]
    public async Task Scout_with_nothing_out_there_gives_up_at_max_and_says_which_way()
    {
        var gotos = new List<string>();
        using var rig = Walking(gotos);
        var s = await rig.Run("return scout('cow', { dir: 90, max: 64 }).msg");
        Assert.True(s.Ok, s.Outcome);
        Assert.Contains("no cow within 64 m that way (90 deg)", s.Outcome);
        Assert.Equal(4, gotos.Count);
        Assert.StartsWith("-64,", gotos[^1]); // yaw 90 = west (-x)
    }
}
