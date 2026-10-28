using MineClaw.Data;
using MineClaw.Protocol;
using MineClaw.Protocol.Packets;
using MineClaw.World;

namespace MineClaw.Client.Tests;

/// <summary>
/// Live 10-28 02:05-02:14 at Fox's three-cell gen (the live fixture: lava 4,64,7 / 4,64,8 / 5,64,8, water 5..6,64..65,6..7,
/// cells 4,64,6 / 5,64,7 / 6,64,8): clone 1337 stood on 6,64,4 and reported "generator harvest stalled at 6,64,8: the cell is
/// next to lava (4,64,7) and water (6,64,6), so the dig keeps failing", while main, stuck at 7.5,64.5,6.3, "can't reach the
/// stand spot 6,64,4" (1337's body was on it). A regenerating gen cell has lava and water next to it by definition: the dig is
/// allowed, refused only when released lava could reach the bot; a stand spot another body occupies is skipped for a free one.
/// </summary>
public class GenCrewStandTests
{
    private static readonly BlockPos East = LiveGenHarvestTests.East;

    [Fact]
    public async Task A_regenerating_cell_with_lava_and_water_beside_it_is_dug_from_a_spot_lava_cant_reach()
    {
        var (c, s) = await Harness.StartAsync(srv =>
        {
            LiveGenHarvestTests.LiveGen(srv);
            srv.SpawnAt = (6.5, 64, 4.5); // where 1337 stood
        });
        await using var _ = c;
        using var __ = s;
        Assert.True(BlockShapes.IsLava(s.Blocks[(4, 64, 7)]));
        Assert.True(BlockShapes.IsWater(s.Blocks[(6, 64, 6)]));
        var r = await c.HarvestGeneratorAsync(East.X, East.Y, East.Z, 3);
        Assert.True(r.Ok, r.Message);
        Assert.Equal(3, r.Count);
        Assert.DoesNotContain("won't dig", r.Message);
        Assert.DoesNotContain("released lava", r.Message);
        Assert.All(s.Dug, d => Assert.Equal((East.X, East.Y, East.Z), (d.X, d.Y, d.Z)));
    }

    [Fact]
    public async Task A_taught_stand_spot_another_bot_stands_on_is_skipped_for_a_free_spot()
    {
        var (c, s) = await Harness.StartAsync(srv =>
        {
            LiveGenHarvestTests.LiveGen(srv);
            srv.SpawnAt = (7.5, 64, 4.5);
        });
        await using var _ = c;
        using var __ = s;
        // maid_chan_1337's body on the taught stand 6,64,4.
        s.Send(new NamedEntitySpawnPacket { EntityId = 81, PlayerName = "maid_chan_1337", X = FixedPoint.ToAbsolute(6.5), Y = FixedPoint.ToAbsolute(64), Z = FixedPoint.ToAbsolute(4.5) });
        Assert.True(await Harness.WaitAsync(() => c.World.Entities.All().Any(e => e.Kind == EntityKind.Player && e.Name == "maid_chan_1337")));
        var r = await c.HarvestGeneratorAsync(East.X, East.Y, East.Z, 2, new BlockPos(6, 64, 4));
        Assert.True(r.Ok, r.Message);
        Assert.Equal(2, r.Count);
        Assert.Contains("stand 6,64,4 is taken by maid_chan_1337", r.Message);
        Assert.DoesNotContain("can't get onto", r.Message);
    }
}

/// <summary>Live 10-28 01:57 "ripeness not verifiable": the crop stage is block metadata; <c>look</c> says it.</summary>
public class LookCropsTests
{
    [Fact]
    public async Task Look_reports_ripe_and_growing_wheat_and_empty_farmland_from_metadata()
    {
        var (c, s) = await Harness.StartAsync(srv =>
        {
            for (int x = 6; x <= 10; x++)
            {
                srv.Blocks[(x, 63, 11)] = Blocks.Farmland;
                srv.Blocks[(x, 63, 12)] = Blocks.Farmland;
                srv.Blocks[(x, 64, 11)] = Blocks.Crops;
                srv.Metas[(x, 64, 11)] = x <= 8 ? 7 : 2;
            }
        });
        await using var _ = c;
        using var __ = s;
        var text = c.DescribeSurroundings(12, detail: true);
        Assert.Contains("crops: wheat 5 (3 ripe 7/7, 2 growing), empty farmland 5", text);
    }
}
