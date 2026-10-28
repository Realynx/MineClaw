using System.Diagnostics;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Client.Tests;

/// <summary>
/// Live 10-28 05:30-06:50 at the real three-cell gen (tests/fixtures/live-gen/gen.txt; the 06:52 db snapshot shows the same
/// cells, lava and water): 880 digs reported, 630 drops "not chasing item 4:0 x1 at 6.2,64.1,7.9 / 5.3,64.1,6.8: in lava/water
/// (hazard)", tasks "dug 64/64, stored 4 cobblestone". The gen did regenerate; its drops were pushed out of the regenerated
/// cell into the water corners 6,64,7 and 5,64,6, which the pit 7,63,8 and the nook 4,64,5 touch with mc-dev's pickup box
/// (1.425 from the body, 1.375 from those cells' centres) but the old ±1.3 test did not. The server here uses mc-dev's
/// vertical box overlap too, so a slab stand (feet 64.5) really can't touch a floor drop.
/// </summary>
public class GenWaterDropTests
{
    /// <summary>Where the regen's push-out (mc-dev Entity.g) and the water leave a dug cell's drop.</summary>
    internal static readonly Vec3d WaterCornerEast = new(6.15, 64.1, 7.85), WaterCornerNorth = new(5.25, 64.1, 6.85);
    /// <summary>Tonight's harvest position: on the cobble slab at 7,64,6 (episodes "@7,64,6").</summary>
    private static readonly (double, double, double) SlabStand = (7.5, 64.5, 6.5);

    private static void Gen(FakeServer srv)
    {
        LiveGenHarvestTests.LiveGen(srv);
        srv.StrictPickup = true;
        srv.SpawnAt = SlabStand;
    }

    [Theory]
    [InlineData(6.15, 64.1, 7.85)] // 6,64,7 corner (log: 262 of 630 unchased)
    [InlineData(5.25, 64.1, 6.85)] // 5,64,6 corner (192 of 630)
    public async Task A_drop_in_the_gens_flowing_water_is_collected(double x, double y, double z)
    {
        var (c, s) = await Harness.StartAsync(Gen);
        await using var _ = c;
        using var __ = s;
        s.SpawnDrop(x, y, z, Harness.Stack(Blocks.Cobblestone, 1));
        Assert.True(await Harness.WaitAsync(() => c.World.Entities.Count > 0));
        var r = await c.CollectDropsAsync(6);
        Assert.True(r.Ok, r.Message);
        Assert.DoesNotContain("hazard", r.Message);
        Assert.Equal(1, c.World.Inventory.CountItem(Blocks.Cobblestone));
        // Never into lava's reach to get it.
        var p = c.World.Player.Position.ToBlockPos();
        Assert.False(Safety.LavaNear(c.World, p.X, p.Y, p.Z));
    }

    [Fact]
    public async Task N_digs_at_the_real_gen_give_N_cobble_at_the_expected_rate()
    {
        // Every dig's drop ends up where the live log saw them: the middle cell's in either water corner, the west cell's in
        // the north water corner, the east cell's in the east one.
        const int N = 8;
        int n = 0;
        var (c, s) = await Harness.StartAsync(srv =>
        {
            Gen(srv);
            srv.DigDrop = (cell, _, _) =>
            {
                var at = cell switch
                {
                    (4, 64, 6) => WaterCornerNorth,
                    (6, 64, 8) => WaterCornerEast,
                    _ => n++ % 2 == 0 ? WaterCornerEast : WaterCornerNorth,
                };
                srv.SpawnDrop(at.X, at.Y, at.Z, Harness.Stack(Blocks.Cobblestone, 1));
                return null;
            };
        });
        await using var _ = c;
        using var __ = s;
        var sw = Stopwatch.StartNew();
        var r = await c.HarvestGeneratorAsync([LiveGenHarvestTests.Middle, LiveGenHarvestTests.West, LiveGenHarvestTests.East], N, null);
        sw.Stop();
        Assert.True(r.Ok, r.Message);
        Assert.Equal(N, s.Dug.Count);
        Assert.Equal(N, c.World.Inventory.CountItem(Blocks.Cobblestone));
        Assert.DoesNotContain("washed into", r.Message);
        // b1.8.1 regen is ~1.5 s per cell (lava flows every 30 ticks); with the fake's instant regen, digging plus the pickup
        // trips to the pit/nook must stay well under the 4 s per cobble seen live.
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(N * 3.0), $"{sw.Elapsed.TotalSeconds:0.0}s for {N}: {r.Message}");
    }
}
