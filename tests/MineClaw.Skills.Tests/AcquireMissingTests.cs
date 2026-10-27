using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Skills.Tests;

/// <summary>Self-reliant preconditions: a missing item is acquired (the Bot's planner behind the seam) before the check.</summary>
public class AcquireMissingTests
{
    private static void Floor(FakeGame g)
    {
        for (int x = -4; x <= 4; x++)
        for (int z = -4; z <= 4; z++) g.World.SetBlock(x, 63, z, Blocks.Stone, 0);
        g.World.Player.ServerTeleport(new Vec3d(0.5, 64, 0.5), 65.62, 0, 0, true);
    }

    [Fact]
    public async Task A_missing_torch_is_acquired_before_the_precondition_check()
    {
        var g = new FakeGame();
        Floor(g);
        var reg = SkillRegistry.CreateDefault();
        int[]? asked = null;
        reg.AcquireMissing = (_, spec, ids, _) =>
        {
            asked = ids;
            g.Give(Blocks.Torch, 4);
            return Task.FromResult<string?>("took 4 torches from 2,64,2");
        };
        var r = await g.Run("place_torch", null, reg);
        Assert.True(r.Ok, r.Text);
        Assert.Equal([Blocks.Torch], asked);
        Assert.StartsWith("took 4 torches from 2,64,2; ", r.Text);
    }

    [Fact]
    public async Task Nothing_acquired_keeps_the_honest_needs_failure()
    {
        var g = new FakeGame();
        Floor(g);
        var reg = SkillRegistry.CreateDefault();
        reg.AcquireMissing = (_, _, _, _) => Task.FromResult<string?>(null);
        var r = await g.Run("place_torch", null, reg);
        Assert.False(r.Ok);
        Assert.Equal("place_torch: needs torch", r.Text);
    }

    [Fact]
    public async Task Items_on_hand_never_call_the_planner()
    {
        var g = new FakeGame();
        Floor(g);
        g.Give(Blocks.Torch, 1);
        var reg = SkillRegistry.CreateDefault();
        int calls = 0;
        reg.AcquireMissing = (_, _, _, _) => { calls++; return Task.FromResult<string?>(null); };
        await g.Run("place_torch", null, reg);
        Assert.Equal(0, calls);
    }
}
