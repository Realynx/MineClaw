using MineClaw.Data;
using MineClaw.Data.Versioning;
using MineClaw.World;

namespace MineClaw.Client.Tests;

public sealed class StationaryDigTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_visible_remnant_is_dug_from_a_pillar_without_repositioning(bool release1122)
    {
        using var mode = WorldMode.Override(WorldMode.Normal);
        var (c, s) = await Harness.StartAsync(srv =>
        {
            for (int y = 64; y <= 69; y++) srv.Blocks[(8, y, 8)] = Blocks.Dirt;
            srv.Blocks[(10, 71, 8)] = Blocks.Cobblestone;
            srv.SpawnAt = (8.5, 70, 8.5);
            srv.Inventory[36] = Harness.Stack(Items.StonePickaxe, 1);
        }, release1122: release1122);
        await using var _ = c;
        using var __ = s;
        var before = s.Blocks.ToDictionary(k => k.Key, v => v.Value);
        var r = await c.DigHereAsync(new BlockPos(10, 71, 8), floorGuard: true);
        Assert.True(r.Ok, r.Message);
        before.Remove((10, 71, 8));
        Assert.Equal(before.OrderBy(k => k.Key), s.Blocks.OrderBy(k => k.Key));
        Assert.InRange(c.World.Player.Position.X, 8.49, 8.51);
        Assert.InRange(c.World.Player.Position.Z, 8.49, 8.51);
        Assert.InRange(c.World.Player.Position.Y, 69.99, 70.01);
    }

    [Fact]
    public async Task A_stationary_dig_preserves_protection_and_does_not_walk_to_distant_targets()
    {
        using var mode = WorldMode.Override(WorldMode.Normal);
        var (c, s) = await Harness.StartAsync(srv =>
        {
            srv.Blocks[(9, 64, 8)] = Blocks.Dirt;
            srv.Blocks[(14, 64, 8)] = Blocks.Dirt;
        });
        await using var _ = c;
        using var __ = s;
        c.IsProtected = (p, _) => p == new BlockPos(9, 64, 8);
        var blocked = await c.DigHereAsync(new BlockPos(9, 64, 8), floorGuard: true);
        Assert.False(blocked.Ok);
        Assert.Contains("protected", blocked.Message);
        var far = await c.DigHereAsync(new BlockPos(14, 64, 8), floorGuard: true);
        Assert.False(far.Ok);
        Assert.Contains("can't see/reach", far.Message);
        Assert.Equal(Blocks.Dirt, c.World.GetBlock(9, 64, 8));
        Assert.Equal(Blocks.Dirt, c.World.GetBlock(14, 64, 8));
        Assert.InRange(c.World.Player.Position.X, 8.49, 8.51);
        Assert.InRange(c.World.Player.Position.Z, 8.49, 8.51);
    }

    [Fact]
    public async Task A_stationary_quarry_dig_still_refuses_a_walkway_floor_over_the_void()
    {
        using var mode = WorldMode.Override(WorldMode.SkyblockWorld);
        var (c, s) = await Harness.StartAsync(srv =>
        {
            srv.Blocks.Clear();
            for (int x = 6; x <= 10; x++) srv.Blocks[(x, 63, 8)] = Blocks.Stone;
            srv.Inventory[36] = Harness.Stack(Items.StonePickaxe, 1);
        });
        await using var _ = c;
        using var __ = s;
        var r = await c.DigHereAsync(new BlockPos(9, 63, 8), floorGuard: true);
        Assert.False(r.Ok);
        Assert.Contains("floor of a walkway", r.Message);
        Assert.Equal(Blocks.Stone, c.World.GetBlock(9, 63, 8));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Each_stationary_pillar_step_lands_before_the_next_support_is_removed(bool release1122)
    {
        using var mode = WorldMode.Override(WorldMode.Normal);
        var (c, s) = await Harness.StartAsync(srv =>
        {
            for (int y = 64; y <= 69; y++) srv.Blocks[(8, y, 8)] = Blocks.Dirt;
            srv.SpawnAt = (8.5, 70, 8.5);
            srv.Inventory[36] = Harness.Stack(Items.StoneShovel, 1);
        }, release1122: release1122);
        await using var _ = c;
        using var __ = s;
        for (int y = 64; y <= 69; y++) c.World.Placed.Note(new BlockPos(8, y, 8), c.World.Dimension, Blocks.Dirt, "maid_chan", DateTimeOffset.UtcNow);
        for (int y = 69; y >= 64; y--)
        {
            var r = await c.DigHereAsync(new BlockPos(8, y, 8), floorGuard: true);
            Assert.True(r.Ok, r.Message);
            Assert.True(c.World.Player.OnGround);
            Assert.InRange(c.World.Player.Position.Y, y - 0.01, y + 0.01);
            Assert.Equal(y > 64 ? Blocks.Dirt : Blocks.Stone, c.World.GetBlock(8, y - 1, 8));
        }
        Assert.Equal(Blocks.Stone, c.World.GetBlock(8, 63, 8));
    }
}
