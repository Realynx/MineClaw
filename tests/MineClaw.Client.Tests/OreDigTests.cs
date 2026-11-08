using MineClaw.Data;
using MineClaw.Data.Versioning;
using MineClaw.Protocol.Packets;
using MineClaw.World;

namespace MineClaw.Client.Tests;

public sealed class OreDigTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Visible_ore_is_mined_from_a_pillar_without_moving_or_modifying_access(bool modern)
    {
        using var mode = WorldMode.Override(WorldMode.Normal);
        var (c, s) = await Harness.StartAsync(srv =>
        {
            for (int y = 64; y < 70; y++) srv.Blocks[(8, y, 8)] = 3;
            srv.Blocks[(10, 71, 8)] = 56;
            srv.SpawnAt = (8.5, 70, 8.5);
            srv.Inventory[36] = Harness.Stack(Items.IronPickaxe, 1);
        }, release1122: modern);
        await using var _ = c;
        using var __ = s;
        var before = s.Blocks.ToDictionary(k => k.Key, v => v.Value);
        var r = await c.DigOreHereAsync(new BlockPos(10, 71, 8), 56);
        Assert.True(r.Ok, r.Message);
        before.Remove((10, 71, 8));
        Assert.Equal(before.OrderBy(k => k.Key), s.Blocks.OrderBy(k => k.Key));
        Assert.InRange(c.World.Player.Position.DistanceTo(new Vec3d(8.5, 70, 8.5)), 0, .02);
    }

    [Theory]
    [InlineData("protected")]
    [InlineData("replacement")]
    [InlineData("water")]
    [InlineData("lava")]
    [InlineData("falling")]
    public async Task Ore_permission_never_bypasses_protection_or_exposure_safety(string kind)
    {
        var at = new BlockPos(9, 64, 8);
        var (c, s) = await Harness.StartAsync(srv =>
        {
            srv.Blocks[(9, 64, 8)] = kind == "replacement" ? 98 : 56;
            if (kind is "water" or "lava") srv.Blocks[(10, 64, 8)] = kind == "water" ? 9 : 11;
            if (kind == "falling") srv.Blocks[(9, 65, 8)] = 13;
            srv.Inventory[36] = Harness.Stack(Items.IronPickaxe, 1);
        });
        await using var _ = c;
        using var __ = s;
        if (kind == "protected") c.IsProtected = (p, _) => p == at;
        var r = await c.DigOreHereAsync(at, 56);
        Assert.False(r.Ok, r.Message);
        Assert.Equal(kind == "replacement" ? 98 : 56, c.World.GetBlock(at));
        Assert.Empty(s.Got<PlayerDiggingPacket>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_late_replacement_cancels_before_the_finish_packet(bool modern)
    {
        var (c, s) = await Harness.StartAsync(srv =>
        {
            srv.Blocks[(9, 64, 8)] = 56;
            srv.Inventory[36] = Harness.Stack(Items.IronPickaxe, 1);
        }, release1122: modern);
        await using var _ = c;
        using var __ = s;
        s.Hook = p =>
        {
            if (p is PlayerDiggingPacket { Status: PlayerDiggingPacket.StatusStarted })
            {
                s.Blocks[(9, 64, 8)] = 98;
                c.World.SetBlock(9, 64, 8, 98, 0);
            }
            return false;
        };
        var r = await c.DigOreHereAsync(new BlockPos(9, 64, 8), 56);
        Assert.False(r.Ok, r.Message);
        Assert.Contains("changed", r.Message);
        Assert.DoesNotContain(s.Got<PlayerDiggingPacket>(), p => p.Status == PlayerDiggingPacket.StatusFinished);
        Assert.Equal(98, c.World.GetBlock(9, 64, 8));
    }

    [Theory]
    [InlineData(73)]
    [InlineData(74)]
    public async Task Lit_and_unlit_redstone_share_the_same_expected_identity(int block)
    {
        var (c, s) = await Harness.StartAsync(srv =>
        {
            srv.Blocks[(9, 64, 8)] = block;
            srv.Inventory[36] = Harness.Stack(Items.IronPickaxe, 1);
        });
        await using var _ = c;
        using var __ = s;
        var r = await c.DigOreHereAsync(new BlockPos(9, 64, 8), 73);
        Assert.True(r.Ok, r.Message);
        Assert.Equal(0, c.World.GetBlock(9, 64, 8));
    }
}
