using MineClaw.Data;
using MineClaw.Data.Versioning;
using MineClaw.Protocol.Packets;
using MineClaw.World;

namespace MineClaw.Client.Tests;

public sealed class ExactPlacementTests
{
    [Theory]
    [InlineData(false, 53, 3, 0)]
    [InlineData(true, 53, 3, 0)]
    [InlineData(false, 54, 5, 0)]
    [InlineData(true, 54, 5, 0)]
    [InlineData(false, 61, 2, 0)]
    [InlineData(true, 61, 2, 0)]
    [InlineData(true, 17, 5, 1)]
    [InlineData(true, 126, 8, 0)]
    [InlineData(true, 53, 7, 0)]
    [InlineData(false, 35, 14, 14)]
    [InlineData(true, 35, 14, 14)]
    public async Task Exact_placement_preserves_facing_half_axis_and_item_variant(bool modern, int id, int meta, int damage)
    {
        using var mode = WorldMode.Override(WorldMode.Normal);
        var (c, s) = await Harness.StartAsync(srv =>
        {
            srv.OrientBlocks = true;
            srv.ConsumeOnPlace = true;
            srv.Blocks[(5, 64, 8)] = Blocks.Stone;
            srv.Inventory[36] = Harness.Stack(id, 2, damage);
        }, release1122: modern);
        await using var _ = c;
        using var __ = s;
        var at = new BlockPos(6, 64, 8);
        var r = await c.PlaceHereAsync(at, id, damage, meta, exact: true);
        Assert.True(r.Ok, r.Message);
        Assert.Equal(id, c.World.GetBlock(at));
        Assert.Equal(meta, c.World.GetMeta(at));
        Assert.Equal(1, c.World.Inventory.CountItem(id, damage));
        Assert.InRange(c.World.Player.Position.X, 8.49, 8.51);
        Assert.InRange(c.World.Player.Position.Z, 8.49, 8.51);
    }

    [Theory]
    [InlineData(true, 17, 12)]
    [InlineData(true, 54, 0)]
    [InlineData(false, 53, 4)]
    [InlineData(false, 17, 4)]
    [InlineData(false, 44, 8)]
    [InlineData(true, 107, 4)]
    [InlineData(true, 96, 4)]
    [InlineData(true, 33, 8)]
    [InlineData(true, 66, 0)]
    [InlineData(true, 64, 0)]
    [InlineData(true, 26, 0)]
    [InlineData(true, 35, 16)]
    public async Task Unsupported_exact_states_are_refused_before_spending_or_moving(bool modern, int id, int meta)
    {
        Assert.NotNull(BlockOrientation.ExactPlacementRefusal(id, meta, modern));
        var (c, s) = await Harness.StartAsync(srv => srv.Inventory[36] = Harness.Stack(id, 2), release1122: modern);
        await using var _ = c;
        using var __ = s;
        var at = new BlockPos(6, 64, 8);
        var stationary = await c.PlaceHereAsync(at, id, meta: meta, exact: true);
        var walking = await c.PlaceTurnedAsync(at, id, null, meta, exact: true);
        Assert.False(stationary.Ok);
        Assert.False(walking.Ok);
        Assert.Contains("unsupported exact state", stationary.Message);
        Assert.Contains("unsupported exact state", walking.Message);
        Assert.Empty(s.Got<PlayerBlockPlacementPacket>());
        Assert.Empty(s.Got<PlayerDiggingPacket>());
        Assert.Equal(2, c.World.Inventory.CountItem(id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_wrong_server_state_is_reported_without_digging_the_now_changed_cell(bool modern)
    {
        var (c, s) = await Harness.StartAsync(srv =>
        {
            srv.OrientBlocks = false;
            srv.Inventory[36] = Harness.Stack(Blocks.Furnace, 2);
            srv.Inventory[37] = Harness.Stack(Items.DiamondPickaxe, 1);
        }, release1122: modern);
        await using var _ = c;
        using var __ = s;
        var at = new BlockPos(6, 64, 8);
        var r = await c.PlaceTurnedAsync(at, Blocks.Furnace, 0, 5, exact: true);
        Assert.False(r.Ok);
        Assert.Contains("wrong exact state", r.Message);
        Assert.Single(s.Got<PlayerBlockPlacementPacket>());
        Assert.Empty(s.Got<PlayerDiggingPacket>());
        Assert.Equal(Blocks.Furnace, c.World.GetBlock(at));
        Assert.Equal(0, c.World.GetMeta(at));
    }

    [Fact]
    public async Task Explicit_item_variant_must_match_the_exact_cell()
    {
        var (c, s) = await Harness.StartAsync(srv => srv.Inventory[36] = Harness.Stack(Blocks.Log, 2, 0), release1122: true);
        await using var _ = c;
        using var __ = s;
        var r = await c.PlaceHereAsync(new BlockPos(6, 64, 8), Blocks.Log, damage: 0, meta: 5, exact: true);
        Assert.False(r.Ok);
        Assert.Contains("item variant", r.Message);
        Assert.Empty(s.Got<PlayerBlockPlacementPacket>());
    }
}
