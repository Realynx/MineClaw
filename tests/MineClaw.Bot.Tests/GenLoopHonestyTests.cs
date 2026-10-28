using MineClaw.Bot.GameTools;
using MineClaw.Client;
using MineClaw.Data;
using WPos = MineClaw.World.BlockPos;

namespace MineClaw.Bot.Tests;

/// <summary>
/// Live 10-28 05:53-06:49: "[task ok] harvest_generator: generator: dug 64/64 at 5,64,7 (have 0 cobblestone)" and "dug 64/64,
/// stored 4 cobblestone"; the brain then said "I mined all 64". The loop's target is cobble gained, its result says so, and a run
/// whose drops don't arrive stops instead of wearing the pickaxe down.
/// </summary>
public class GenLoopHonestyTests
{
    [Fact]
    public async Task Digs_whose_drops_wash_away_stop_with_the_real_count_not_dug_64_of_64()
    {
        var (h, c) = TestBot.Create();
        using var _ = h;
        c.Give(36, Items.StonePickaxe, 1);
        c.Results["generator"] = () => ActionResult.Success("generator: dug 8, +1 cobblestone (have 1); 7 drop(s) washed into the gen's water/lava (not collected)", 8);
        var r = await new GeneratorLoop(c) { RetryPause = TimeSpan.Zero }.RunAsync(new WPos(5, 64, 7), 64, null, default);
        Assert.False(r.Ok, r.Message);
        Assert.StartsWith("generator: 2/64 cobblestone in ", r.Message);
        Assert.Contains("(dug 16 at 5,64,7)", r.Message);
        Assert.Contains("stalled: 16 digs gave 2 cobble (14 drop(s) washed into the gen's water/lava, not collected)", r.Message);
        Assert.DoesNotContain("64/64", r.Message);
        Assert.Equal(2, c.CallList.Count(x => x.StartsWith("generator", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task The_loop_digs_until_the_cobble_target_not_the_dig_count()
    {
        // Some drops lost, most collected: keep digging until 24 cobble really arrived.
        var (h, c) = TestBot.Create();
        using var _ = h;
        c.Give(36, Items.StonePickaxe, 1);
        c.Results["generator"] = () => ActionResult.Success("generator: dug 8, +6 cobblestone (have 6); 2 drop(s) washed into the gen's water/lava (not collected)", 8);
        var r = await new GeneratorLoop(c) { RetryPause = TimeSpan.Zero }.RunAsync(new WPos(5, 64, 7), 24, null, default);
        Assert.True(r.Ok, r.Message);
        Assert.StartsWith("generator: 24/24 cobblestone in ", r.Message);
        Assert.Contains("(dug 32 at 5,64,7)", r.Message);
        Assert.Contains("8 drop(s) washed into the gen's water/lava (not collected)", r.Message);
    }
}
