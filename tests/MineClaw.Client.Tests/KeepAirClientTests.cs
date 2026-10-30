using MineClaw.Data;
using MineClaw.Protocol.Packets;
using MineClaw.World;

namespace MineClaw.Client.Tests;

/// <summary>
/// Live 10-30 CT 114: "you keep filling in the stairs" (02:07); "One extra cobble used by goto bridging at 154,15,276, which is a
/// stair cell" (02:20). A cell a dug design keeps air (World.KeepAir) never gets a colliding block from this client: no goto bridge
/// or pillar, no plain place, no place-against (the box-in reflex, builds). Torches and signs still go in. Both versions.
/// </summary>
public class KeepAirClientTests
{
    /// <summary>Two stone platforms (x 4..7 and 10..13, z 6..10, y 63) with a 2-wide gap at x 8..9: the stair shaft's air.</summary>
    private static void Gap(FakeServer s)
    {
        s.Blocks.Clear();
        for (int z = 6; z <= 10; z++)
        {
            for (int x = 4; x <= 7; x++) s.Blocks[(x, 63, z)] = Blocks.Stone;
            for (int x = 10; x <= 13; x++) s.Blocks[(x, 63, z)] = Blocks.Stone;
        }
        s.SpawnAt = (5.5, 64, 8.5);
        s.Inventory[36] = Harness.Stack(Blocks.Cobblestone, 32);
        s.Inventory[37] = Harness.Stack(Blocks.Torch, 8);
        s.ConsumeOnPlace = true;
    }

    private static List<BlockPos> Shaft() =>
        (from x in new[] { 8, 9 } from y in Enumerable.Range(55, 16) from z in Enumerable.Range(-24, 64) select new BlockPos(x, y, z)).ToList();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Goto_never_bridges_and_place_never_fills_a_must_be_air_cell(bool release1122)
    {
        var (c, s) = await Harness.StartAsync(Gap, release1122: release1122);
        await using var _ = c;
        using var __ = s;
        Assert.True(await Harness.WaitAsync(() => c.World.Player.OnGround && c.World.Inventory.CountItem(Blocks.Cobblestone) == 32, 4000));
        var shaft = Shaft();
        c.World.KeepAir.Set("spiral stair", c.World.Dimension, shaft);

        var go = await c.GotoAsync(new GoalBlock(11, 64, 8), GotoOptions.Default with { AllowPlace = true, Timeout = TimeSpan.FromSeconds(10) });
        Assert.False(go.Ok, go.Message); // the only way over is a bridge through the shaft
        var place = await c.PlaceBlockAsync(7, 64, 8, Blocks.Cobblestone); // beside me, outside the shaft: fine
        Assert.True(place.Ok, place.Message);
        var refused = await c.PlaceBlockAsync(8, 63, 8, Blocks.Cobblestone);
        Assert.False(refused.Ok);
        Assert.Contains("8,63,8 must stay air (spiral stair)", refused.Message);
        var against = await c.PlaceAgainstAsync(new BlockPos(8, 64, 7), BlockFace.East, Blocks.Cobblestone, null, null, allowHidden: true);
        Assert.False(against.Ok);
        Assert.Contains("must stay air", against.Message);
        Assert.DoesNotContain(s.Got<PlayerBlockPlacementPacket>(), p => shaft.Contains(new BlockPos(p.X + Off(p.Direction).X, p.Y + Off(p.Direction).Y, p.Z + Off(p.Direction).Z)) && p.HeldItem.Id == Blocks.Cobblestone);
        Assert.All(shaft, cell => Assert.Equal(0, s.Blocks.GetValueOrDefault((cell.X, cell.Y, cell.Z))));

        // A torch hangs on the shaft wall fine: no collision box.
        var torch = await c.PlaceBlockAsync(8, 64, 8, Blocks.Torch);
        Assert.True(torch.Ok || !torch.Message.Contains("must stay air"), torch.Message);

        // Released: the same goto bridges.
        c.World.KeepAir.Remove("spiral stair");
        var go2 = await c.GotoAsync(new GoalBlock(11, 64, 8), GotoOptions.Default with { AllowPlace = true, Timeout = TimeSpan.FromSeconds(15) });
        Assert.True(go2.Ok, go2.Message);
    }

    private static BlockPos Off(byte face) => face switch
    {
        0 => new(0, -1, 0), 1 => new(0, 1, 0), 2 => new(0, 0, -1), 3 => new(0, 0, 1), 4 => new(-1, 0, 0), 5 => new(1, 0, 0), _ => new(0, 0, 0),
    };
}
