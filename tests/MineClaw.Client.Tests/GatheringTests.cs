using MineClaw.Data;
using MineClaw.Protocol;
using MineClaw.Protocol.Packets;
using MineClaw.World;

namespace MineClaw.Client.Tests;

public class GatheringTests
{
    [Fact]
    public async Task HarvestGeneratorDigsRegeneratingCobble()
    {
        var (c, s) = await Harness.StartAsync(srv =>
        {
            srv.Blocks[(10, 64, 8)] = Blocks.Cobblestone;
            srv.Regenerate[(10, 64, 8)] = Blocks.Cobblestone;
            srv.DropOnDig = true;
            srv.Inventory[36] = Harness.Stack(Items.WoodenPickaxe, 1); // cobblestone drops nothing without a pickaxe
        });
        await using var _ = c;
        using var __ = s;
        var r = await c.HarvestGeneratorAsync(10, 64, 8, 3);
        Assert.True(r.Ok, r.ToString());
        Assert.Equal(3, r.Count);
        Assert.Equal(3, s.Got<PlayerDiggingPacket>().Count(d => d.Status == PlayerDiggingPacket.StatusFinished));
        Assert.Contains("+3 cobblestone", r.Message);
    }

    [Fact]
    public async Task HarvestGeneratorStopsWhenTheCellDoesNotYieldCobble()
    {
        // Live 10-27: "dug 64, +33 cobblestone" at a remembered but wrong cell. The yield guard stops after one window.
        var (c, s) = await Harness.StartAsync(srv =>
        {
            srv.Blocks[(10, 64, 8)] = Blocks.Cobblestone;
            srv.Regenerate[(10, 64, 8)] = Blocks.Cobblestone;
            srv.DropOnDig = false; // the block comes back but nothing reaches the inventory
            srv.Inventory[36] = Harness.Stack(Items.WoodenPickaxe, 1);
        });
        await using var _ = c;
        using var __ = s;
        var r = await c.HarvestGeneratorAsync(10, 64, 8, 64);
        Assert.False(r.Ok, r.ToString());
        Assert.Contains("10,64,8 isn't regenerating cobble for me (yield 0%", r.Message);
        Assert.Contains("need the right cell", r.Message);
        Assert.Equal(MineClaw.Client.MinecraftClient.YieldWindow,
            s.Got<PlayerDiggingPacket>().Count(d => d.Status == PlayerDiggingPacket.StatusFinished)); // never digs on
    }

    [Fact]
    public async Task MineSkipsProtectedAndSupportingBlocks()
    {
        var (c, s) = await Harness.StartAsync(srv =>
        {
            srv.Blocks[(8, 63, 8)] = Blocks.Dirt;  // under my feet
            srv.Blocks[(10, 64, 7)] = Blocks.Dirt;
            srv.Blocks[(6, 64, 9)] = Blocks.Dirt;  // protected
            srv.DropOnDig = false;                 // stay put: (8,63,8) remains the block holding me up
        });
        await using var _ = c;
        using var __ = s;
        var r = await c.MineAsync("dirt", 5, 8, protect: (p, _) => p == new BlockPos(6, 64, 9));
        Assert.True(r.Ok, r.ToString());
        Assert.Equal(1, r.Count);
        var digs = s.Got<PlayerDiggingPacket>().Where(d => d.Status == 0).Select(d => new BlockPos(d.X, d.Y, d.Z)).Distinct().ToList();
        Assert.Equal([new BlockPos(10, 64, 7)], digs);
        Assert.Contains("no more reachable", r.Message);
    }

    [Fact]
    public async Task CollectDropsWalksToItems()
    {
        var (c, s) = await Harness.StartAsync();
        await using var _ = c;
        using var __ = s;
        s.SpawnDrop(10.5, 64.1, 10.5, new ItemStack(Items.Stick, 2, 0));
        Assert.True(await Harness.WaitAsync(() => c.World.Entities.Count > 0));
        var r = await c.CollectDropsAsync(6);
        Assert.True(r.Ok, r.ToString());
        Assert.Equal(2, c.World.Inventory.CountItem(Items.Stick));
    }

    [Fact]
    public async Task CollectDropsIgnoresItemsOverTheVoid()
    {
        var (c, s) = await Harness.StartAsync();
        await using var _ = c;
        using var __ = s;
        s.SpawnDrop(14.5, 64.1, 8.5, new ItemStack(Items.Stick, 1, 0));
        Assert.True(await Harness.WaitAsync(() => c.World.Entities.Count > 0));
        var r = await c.CollectDropsAsync(8);
        Assert.False(r.Ok);
        Assert.True(c.World.Player.Position.X < 11);
    }

    [Fact]
    public async Task WithdrawPartialStackUsesRightClicksAndPutsTheRestBack()
    {
        var (c, s) = await Harness.StartAsync(srv =>
        {
            srv.Blocks[(8, 64, 10)] = Blocks.Chest;
            var chest = Enumerable.Repeat(ItemStack.Empty, 27).ToArray();
            chest[0] = new ItemStack(Blocks.Cobblestone, 10, 0);
            srv.Chests[(8, 64, 10)] = chest;
        });
        await using var _ = c;
        using var __ = s;
        var r = await c.WithdrawAsync(Blocks.Cobblestone, 3, new BlockPos(8, 64, 10));
        Assert.True(r.Ok, r.ToString());
        Assert.Equal(3, c.World.Inventory.CountItem(Blocks.Cobblestone));
        var seq = s.Got<WindowClickPacket>().Select(k => ((int)k.Slot, k.RightClick)).ToList();
        Assert.Equal([(0, false), (54, true), (54, true), (54, true), (0, false)], seq);
        Assert.True(s.WaitFor(p => p is CloseWindowPacket));
    }

    [Fact]
    public async Task SmeltLoadsInputAndJustEnoughFuel()
    {
        var (c, s) = await Harness.StartAsync(srv =>
        {
            srv.Inventory[20] = new ItemStack(Blocks.Cobblestone, 12, 0);
            srv.Inventory[21] = new ItemStack(Blocks.Planks, 10, 0);
            srv.Blocks[(10, 64, 10)] = Blocks.Furnace;
            srv.Furnaces[(10, 64, 10)] = [ItemStack.Empty, ItemStack.Empty, ItemStack.Empty];
        });
        await using var _ = c;
        using var __ = s;
        var r = await c.SmeltAsync(Blocks.Cobblestone, 6);
        Assert.True(r.Ok, r.ToString());
        Assert.Equal(6, r.Count);
        // 6 items × 200 ticks / 300 ticks per plank = 4 planks.
        Assert.Equal(6, c.World.Inventory.CountItem(Blocks.Cobblestone));
        Assert.Equal(10 - Smelting.FuelNeeded(6, Blocks.Planks), c.World.Inventory.CountItem(Blocks.Planks));
        Assert.Contains("ready in", r.Message);
    }

    [Fact]
    public async Task PartialChunkRegionsAreApplied()
    {
        var (c, s) = await Harness.StartAsync();
        await using var _ = c;
        using var __ = s;
        var region = new ChunkRegion(4, 70, 4, 2, 2, 2);
        Array.Fill(region.BlockIds, (byte)Blocks.Glass);
        s.Send(ChunkDataDecoder.Encode(region));
        Assert.True(await Harness.WaitAsync(() => c.World.GetBlock(5, 71, 5) == Blocks.Glass));
        Assert.Equal(0, c.World.GetBlock(6, 71, 5));
    }
}
