namespace MineClaw.Client.Tests;

/// <summary>
/// 2026-10-27 night log, B24: 67 tossed 29 items at the feet main had offered (07:49:25) while main walked on chopping; main
/// collected 28. Main now stands still for the hand-off: its running walk is paused, not aborted.
/// </summary>
public class NightLogHandoffHoldTests
{
    [Fact]
    public async Task B24_a_handoff_hold_pauses_the_running_walk_and_it_resumes_after()
    {
        var (c, s) = await Harness.StartAsync(srv =>
        {
            for (int x = 6; x <= 14; x++)
            for (int z = 6; z <= 10; z++)
                srv.Blocks[(x, 63, z)] = MineClaw.Data.Blocks.Stone;
        });
        await using var _ = c;
        using var __ = s;
        var start = c.World.Player.Position;

        var hold = c.HoldStill(TimeSpan.FromSeconds(10));
        Assert.True(c.IsHeldStill);
        var walk = c.GotoAsync(12, 64, 8);
        long t0 = c.TickCount;
        Assert.True(await Harness.WaitAsync(() => c.TickCount > t0 + 40));
        var held = c.World.Player.Position;
        Assert.True(Math.Abs(held.X - start.X) < 0.05 && Math.Abs(held.Z - start.Z) < 0.05, $"moved while held: {start} -> {held}");
        Assert.False(walk.IsCompleted, walk.IsCompleted ? walk.Result.ToString() : "");

        hold.Dispose();
        Assert.False(c.IsHeldStill);
        var r = await walk.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(r.Ok, r.ToString());
        Assert.Equal(12, (int)Math.Floor(c.World.Player.Position.X));
    }

    [Fact]
    public async Task B24_a_hold_nobody_releases_expires_on_its_own()
    {
        var (c, s) = await Harness.StartAsync();
        await using var _ = c;
        using var __ = s;
        using var hold = c.HoldStill(TimeSpan.FromMilliseconds(100)); // 20 ticks at the harness's 5 ms
        Assert.True(c.IsHeldStill);
        Assert.True(await Harness.WaitAsync(() => !c.IsHeldStill, 3000));
    }
}
