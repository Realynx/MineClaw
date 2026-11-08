using MineClaw.Data;
using MineClaw.Data.Versioning;
using MineClaw.World;

namespace MineClaw.Client.Tests;

public sealed class StationaryPlacementTests
{
    private static void Tower(FakeServer s)
    {
        s.OrientBlocks = true;
        s.ConsumeOnPlace = true;
        for (int y = 64; y < 70; y++) s.Blocks[(8, y, 8)] = Blocks.Dirt;
        s.SpawnAt = (8.5, 70, 8.5);
        s.Inventory[36] = Harness.Stack(Blocks.Furnace, 4);
        s.Inventory[37] = Harness.Stack(Blocks.WoodenStairs, 4);
        s.Inventory[38] = Harness.Stack(Blocks.Torch, 4);
        s.Inventory[39] = Harness.Stack(Blocks.Cobblestone, 4);
    }

    private static void StillOnTower(MinecraftClient c)
    {
        Assert.InRange(c.World.Player.Position.X, 8.49, 8.51);
        Assert.InRange(c.World.Player.Position.Z, 8.49, 8.51);
        Assert.InRange(c.World.Player.Position.Y, 69.99, 70.01);
        Assert.True(c.World.Player.OnGround);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_construction_run_places_blocks_and_requested_orientations_from_the_same_pillar(bool modern)
    {
        using var mode = WorldMode.Override(WorldMode.Normal);
        var (c, s) = await Harness.StartAsync(srv =>
        {
            Tower(srv);
            srv.Blocks[(10, 69, 8)] = Blocks.Stone;
            srv.Blocks[(10, 69, 9)] = Blocks.Stone;
            srv.Blocks[(12, 70, 8)] = Blocks.Stone;
            srv.Blocks[(8, 69, 10)] = Blocks.Stone;
        }, release1122: modern);
        await using var _ = c;
        using var __ = s;
        var expected = s.Blocks.ToDictionary(k => k.Key, v => v.Value);
        (BlockPos At, int Item, int? Meta)[] run =
        [
            (new(10, 70, 8), Blocks.Furnace, 5),
            (new(10, 70, 9), Blocks.WoodenStairs, 3),
            (new(11, 70, 8), Blocks.Torch, 2),
            (new(8, 70, 10), Blocks.Cobblestone, null),
        ];
        foreach (var (at, item, meta) in run)
        {
            var r = await c.PlaceHereAsync(at, item, meta: meta);
            Assert.True(r.Ok, $"{at}: {r.Message}");
            StillOnTower(c);
            Assert.Equal(item, c.World.GetBlock(at));
            if (meta is { } turn) Assert.True(BlockOrientation.Same(item, turn, c.World.GetMeta(at), modern));
            expected[(at.X, at.Y, at.Z)] = item;
        }
        Assert.Equal(expected.OrderBy(k => k.Key), s.Blocks.OrderBy(k => k.Key));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_hidden_requested_face_is_refused_without_walking_or_using_another_rotation(bool modern)
    {
        using var mode = WorldMode.Override(WorldMode.Normal);
        var (c, s) = await Harness.StartAsync(srv =>
        {
            Tower(srv);
            srv.Blocks[(9, 70, 8)] = Blocks.Stone; // its east face is behind it from the tower
            srv.Blocks[(10, 69, 8)] = Blocks.Stone; // another face exists, but gives the wrong torch orientation
        }, release1122: modern);
        await using var _ = c;
        using var __ = s;
        var before = s.Blocks.ToDictionary(k => k.Key, v => v.Value);
        var r = await c.PlaceHereAsync(new BlockPos(10, 70, 8), Blocks.Torch, meta: 1);
        Assert.False(r.Ok);
        Assert.Contains("no visible support face", r.Message);
        StillOnTower(c);
        Assert.Equal(4, c.World.Inventory.CountItem(Blocks.Torch));
        Assert.Equal(before.OrderBy(k => k.Key), s.Blocks.OrderBy(k => k.Key));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_stationary_placement_keeps_protection_and_refuses_distant_work_without_moving(bool modern)
    {
        using var mode = WorldMode.Override(WorldMode.Normal);
        var (c, s) = await Harness.StartAsync(srv =>
        {
            Tower(srv);
            srv.Blocks[(10, 69, 8)] = Blocks.Stone;
            srv.Blocks[(14, 69, 8)] = Blocks.Stone;
        }, release1122: modern);
        await using var _ = c;
        using var __ = s;
        var before = s.Blocks.ToDictionary(k => k.Key, v => v.Value);
        c.AdoptProtectedRegion(new LearnedProtectedRegion(new BlockPos(10, 70, 8), 0, c.World.Dimension, 2, "test protection", DateTime.UtcNow));
        var blocked = await c.PlaceHereAsync(new BlockPos(10, 70, 8), Blocks.Furnace, meta: 5);
        Assert.False(blocked.Ok);
        Assert.Contains("protection", blocked.Message);
        var far = await c.PlaceHereAsync(new BlockPos(14, 70, 8), Blocks.Cobblestone);
        Assert.False(far.Ok);
        Assert.Contains("no visible support face", far.Message);
        StillOnTower(c);
        Assert.Equal(before.OrderBy(k => k.Key), s.Blocks.OrderBy(k => k.Key));
    }
}
