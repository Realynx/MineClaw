using MineClaw.Data;
using MineClaw.Sim;

namespace MineClaw.Client.Tests;

public class SkyblockGeneratorTests
{
    [Fact]
    public async Task SkyblockHasContainedFlowingGeneratorAndOneSecondRefill()
    {
        var (c, s) = await Harness.StartAsync(srv =>
        {
            SkyblockStart.Configure(srv);
            srv.Inventory[36] = Harness.Stack(Items.WoodenPickaxe, 1);
        });
        await using var client = c;
        using var server = s;
        Assert.Equal(Blocks.Cobblestone, c.World.GetBlock(10, 64, 6));
        Assert.Equal(Blocks.Water, c.World.GetBlock(8, 64, 6));
        Assert.Equal(0, c.World.GetMeta(8, 64, 6));
        Assert.Equal(Blocks.FlowingWater, c.World.GetBlock(9, 64, 6));
        Assert.Equal(1, c.World.GetMeta(9, 64, 6));
        Assert.Equal(Blocks.FlowingLava, c.World.GetBlock(11, 64, 6));
        Assert.Equal(1, c.World.GetMeta(11, 64, 6));
        Assert.Equal(Blocks.Lava, c.World.GetBlock(12, 64, 6));
        Assert.Equal(0, c.World.GetMeta(12, 64, 6));
        Assert.Equal(Blocks.Air, c.World.GetBlock(9, 63, 6));
        var result = await c.DigBlockAsync(10, 64, 6);
        Assert.True(result.Ok, result.ToString());
        Assert.Equal(Blocks.Air, c.World.GetBlock(10, 64, 6));
        await Task.Delay(250);
        Assert.Equal(Blocks.Air, c.World.GetBlock(10, 64, 6));
        Assert.True(await Harness.WaitAsync(() => c.World.GetBlock(10, 64, 6) == Blocks.Cobblestone, 2000));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_drop_beside_a_standing_player_is_collected_only_with_stationary_pickup(bool stationary)
    {
        var (c, s) = await Harness.StartAsync(srv => srv.StationaryPickup = stationary);
        await using var client = c;
        using var server = s;
        // Position packets pick drops up on their own (the player "moved"): ignore them, so only the standing-still path is left.
        s.Hook = p => p is MineClaw.Protocol.Packets.PlayerPositionPacket or MineClaw.Protocol.Packets.PlayerPositionLookPacket;
        int id = s.SpawnDrop(9, 64, 8, Harness.Stack(Blocks.Dirt, 1)); // within reach of the spawn point, the player never moves
        bool collected = await Harness.WaitAsync(() => c.World.Inventory.CountItem(Blocks.Dirt) == 1, 1000);
        Assert.Equal(stationary, collected);
        Assert.Equal(!stationary, s.Drops.ContainsKey(id));
        Assert.Equal(!stationary, c.World.Entities.Get(id) is not null);
    }

    [Fact]
    public void The_skyblock_sandbox_turns_stationary_pickup_on()
    {
        using var s = new FakeServer();
        Assert.False(s.StationaryPickup);
        SkyblockStart.Configure(s);
        Assert.True(s.StationaryPickup);
    }
}
