using MineClaw.Data;
using MineClaw.Data.Versioning;
using MineClaw.World;

namespace MineClaw.Client.Tests;

public sealed class DesignReplacementDigTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Explicit_replacement_clears_old_objects_but_does_not_release_neighbors_or_safety(bool release1122)
    {
        using var mode = WorldMode.Override(WorldMode.Normal);
        var (c, s) = await Harness.StartAsync(srv =>
        {
            srv.Blocks[(9, 64, 8)] = Blocks.Chest;
            srv.Blocks[(9, 64, 9)] = Blocks.Planks;
            srv.Blocks[(7, 64, 8)] = Blocks.Planks;
            srv.Blocks[(8, 64, 9)] = Blocks.Bedrock;
            srv.Inventory[36] = Harness.Stack(Items.DiamondAxe, 1);
        }, release1122: release1122);
        await using var _ = c;
        using var __ = s;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        c.IsProtected = (p, _) => p.Y == 64;
        c.Preserved = (p, _) => p.Y == 64;
        Assert.False((await c.DigForBuildAsync(new BlockPos(9, 64, 8), timeout.Token)).Ok);
        var chest = await c.DigForBuildAsync(new BlockPos(9, 64, 8), timeout.Token, replacement: true);
        Assert.True(chest.Ok, chest.Message);
        var oldWall = await c.DigForBuildAsync(new BlockPos(9, 64, 9), timeout.Token, replacement: true);
        Assert.True(oldWall.Ok, oldWall.Message);
        Assert.False((await c.DigForBuildAsync(new BlockPos(7, 64, 8), timeout.Token)).Ok);
        Assert.False((await c.DigForBuildAsync(new BlockPos(8, 64, 9), timeout.Token, replacement: true)).Ok);
        Assert.Equal(Blocks.Planks, c.World.GetBlock(7, 64, 8));
        Assert.Equal(Blocks.Bedrock, c.World.GetBlock(8, 64, 9));
    }
}
