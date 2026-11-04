using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Client.Tests;

/// <summary>
/// Fox 11-04 (live, Story Mode): "The pawns are very bad at navigating one-drop-level holes. I have channels for water to flow, one
/// level down, like a hole. They fall into it when they're walking, and they can't walk out because they have to jump once to get
/// out." The whole client (goto, path follower, edge guard, breath, speed boost) against the fake server's movement checks, on both
/// versions: a field crossed by a 1-deep still-water channel (x 6) and an empty 1-deep trench (x 11), the way Story water digs them.
/// </summary>
public class ChannelGotoTests
{
    public static TheoryData<bool, double> Cases => new() { { false, 1 }, { true, 1 }, { false, 3 }, { true, 3 } };

    private static Action<FakeServer> Field((double X, double Y, double Z) spawn, int width = 1) => srv =>
    {
        for (int x = -4; x <= 22; x++)
        for (int z = 2; z <= 14; z++)
        {
            for (int y = 60; y <= 62; y++) srv.Blocks[(x, y, z)] = Blocks.Dirt;
            bool channel = x >= 6 && x < 6 + width, trench = x >= 11 && x < 11 + width;
            if (channel) srv.Blocks[(x, 63, z)] = Blocks.Water;
            else if (!trench) srv.Blocks[(x, 63, z)] = Blocks.Grass;
        }
        srv.SpawnAt = spawn;
        srv.MovementChecks = true;
        srv.ServerTickMs = 5;
    };

    private static async Task<(ActionResult R, MinecraftClient C, FakeServer S, long Ticks)> Go((double, double, double) spawn, BlockPos to,
        bool release1122, double speed, int width = 1)
    {
        var (c, s) = await Harness.StartAsync(Field(spawn, width), release1122: release1122);
        await Harness.WaitAsync(() => c.World.Player.OnGround || c.World.Player.Physics.InWater, 3000);
        c.SpeedMultiplier = speed;
        long t0 = c.TickCount;
        var r = await c.GotoAsync(to.X, to.Y, to.Z);
        return (r, c, s, c.TickCount - t0);
    }

    private static void AssertClean(ActionResult r, MinecraftClient c, FakeServer s, long ticks, long budget)
    {
        Assert.True(r.Ok, $"{r.Message} (at {c.World.Player.Position})");
        Assert.DoesNotContain("re-plan", r.Message);
        Assert.True(ticks <= budget, $"took {ticks} ticks: {r.Message}");
        Assert.False(s.Kicked);
        Assert.True(s.MovedWrongly == 0 && s.MovedTooQuickly == 0, $"{s.MovedWrongly} wrongly, {s.MovedTooQuickly} too quickly: {s.LastRejection}");
    }

    [Theory, MemberData(nameof(Cases))]
    public async Task Walks_across_a_field_with_a_water_channel_and_a_dry_trench(bool release1122, double speed)
    {
        var (r, c, s, ticks) = await Go((0.5, 64, 8.5), new BlockPos(18, 64, 8), release1122, speed);
        await using var _ = c;
        using var __ = s;
        AssertClean(r, c, s, ticks, 400);
        Assert.Equal(64, c.World.Player.Position.Y, 2);
    }

    [Theory, MemberData(nameof(Cases))]
    public async Task Crosses_two_wide_channels(bool release1122, double speed)
    {
        var (r, c, s, ticks) = await Go((0.5, 64, 8.5), new BlockPos(18, 64, 8), release1122, speed, width: 2);
        await using var _ = c;
        using var __ = s;
        AssertClean(r, c, s, ticks, 500);
    }

    [Theory, MemberData(nameof(Cases))]
    public async Task Gets_out_of_the_water_channel(bool release1122, double speed)
    {
        var (r, c, s, ticks) = await Go((6.5, 63, 8.5), new BlockPos(9, 64, 8), release1122, speed);
        await using var _ = c;
        using var __ = s;
        AssertClean(r, c, s, ticks, 200);
    }

    [Theory, MemberData(nameof(Cases))]
    public async Task Gets_out_of_the_dry_trench(bool release1122, double speed)
    {
        var (r, c, s, ticks) = await Go((11.5, 63, 8.5), new BlockPos(14, 64, 8), release1122, speed);
        await using var _ = c;
        using var __ = s;
        AssertClean(r, c, s, ticks, 200);
    }

    [Theory, MemberData(nameof(Cases))]
    public async Task Walks_along_the_banks_without_falling_in(bool release1122, double speed)
    {
        var (r, c, s, ticks) = await Go((5.5, 64, 2.5), new BlockPos(12, 64, 14), release1122, speed);
        await using var _ = c;
        using var __ = s;
        AssertClean(r, c, s, ticks, 500);
    }
}
