using MineClaw.Data;
using MineClaw.Data.Versioning;
using MineClaw.Protocol.Packets;
using MineClaw.World;

namespace MineClaw.Client.Tests;

public sealed class ChestDeconstructionTests
{
    private static readonly BlockPos Target = new(9, 64, 8);
    private static readonly BlockPos Other = new(10, 64, 8);
    private const sbyte WindowId = 6;

    private static Task<(MinecraftClient Client, FakeServer Server)> Start(bool modern, bool pair = false, bool contents = false,
        bool snapshot = true, bool lateDeposit = false) => Harness.StartAsync(s =>
        {
            s.Blocks[(9, 64, 8)] = Blocks.Chest;
            if (pair) s.Blocks[(10, 64, 8)] = Blocks.Chest;
            s.Blocks[(7, 64, 8)] = Blocks.Planks;
            s.Inventory[36] = Harness.Stack(Items.DiamondAxe, 1);
            int size = pair ? 54 : 27;
            s.Hook = packet =>
            {
                if (packet is PlayerBlockPlacementPacket { X: 9, Y: 64, Z: 8 })
                {
                    s.Send(new OpenWindowPacket { WindowId = WindowId, InventoryType = 0, Title = "Chest", SlotCount = (byte)size });
                    if (snapshot)
                    {
                        var slots = Enumerable.Repeat(ItemStack.Empty, size + 36).ToArray();
                        for (int i = 0; i < 36; i++) slots[size + i] = s.Inventory[9 + i];
                        if (contents) slots[size - 1] = Harness.Stack(Items.Diamond, 12);
                        s.Send(new WindowItemsPacket { WindowId = WindowId, Items = slots });
                    }
                    return true;
                }
                if (lateDeposit && packet is PlayerDiggingPacket { Status: PlayerDiggingPacket.StatusStarted, X: 9, Y: 64, Z: 8 })
                    s.Send(new SetSlotPacket { WindowId = WindowId, Slot = (short)(size - 1), Item = Harness.Stack(Items.Diamond, 12) });
                return false;
            };
        }, release1122: modern);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Explicit_empty_chest_deconstruction_releases_only_that_action(bool modern)
    {
        using var mode = WorldMode.Override(WorldMode.Normal);
        var (c, s) = await Start(modern);
        await using var client = c;
        using var server = s;
        c.IsProtected = (_, _) => true;
        Assert.False((await c.DigBlockAsync(Target.X, Target.Y, Target.Z)).Ok);
        var r = await c.DeconstructChestAsync(Target);
        Assert.True(r.Ok, r.Message);
        Assert.Equal(Blocks.Air, c.World.GetBlock(Target));
        Assert.False((await c.DigBlockAsync(7, 64, 8)).Ok);
        Assert.Equal(Blocks.Planks, c.World.GetBlock(7, 64, 8));
        Assert.Single(s.Dug);
        Assert.Null(c.World.Inventory.OpenWindow);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Removing_one_empty_double_chest_half_keeps_the_other(bool modern)
    {
        var (c, s) = await Start(modern, pair: true);
        await using var client = c;
        using var server = s;
        c.IsProtected = (_, _) => true;
        var r = await c.DeconstructChestAsync(Target);
        Assert.True(r.Ok, r.Message);
        Assert.Equal(Blocks.Chest, c.World.GetBlock(Other));
        Assert.Single(s.Dug);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Contents_in_either_single_or_second_double_half_prevent_any_dig(bool modern, bool pair)
    {
        var (c, s) = await Start(modern, pair, contents: true);
        await using var client = c;
        using var server = s;
        var r = await c.DeconstructChestAsync(Target);
        Assert.False(r.Ok);
        Assert.StartsWith("chest deconstruction waiting: evacuate contents", r.Message);
        Assert.Empty(s.Got<PlayerDiggingPacket>());
        Assert.Equal(Blocks.Chest, c.World.GetBlock(Target));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_open_window_without_server_contents_is_not_proof_of_empty_inventory(bool modern)
    {
        var (c, s) = await Start(modern, snapshot: false);
        await using var client = c;
        using var server = s;
        var r = await c.DeconstructChestAsync(Target);
        Assert.False(r.Ok);
        Assert.Contains("fresh chest contents unavailable", r.Message);
        Assert.Empty(s.Got<PlayerDiggingPacket>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_deposit_during_deconstruction_cancels_before_breaking(bool modern)
    {
        var (c, s) = await Start(modern, pair: true, lateDeposit: true);
        await using var client = c;
        using var server = s;
        var r = await c.DeconstructChestAsync(Target);
        Assert.False(r.Ok);
        Assert.Contains("evacuate contents", r.Message);
        if (modern) Assert.Contains(s.Got<PlayerDiggingPacket>(), p => p.Status == PlayerDiggingPacket.StatusCancelled);
        Assert.DoesNotContain(s.Got<PlayerDiggingPacket>(), p => p.Status == PlayerDiggingPacket.StatusFinished);
        Assert.Empty(s.Dug);
        Assert.Equal(Blocks.Chest, c.World.GetBlock(Target));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Explicit_deconstruction_still_honors_never_break(bool modern)
    {
        var (c, s) = await Start(modern);
        await using var client = c;
        using var server = s;
        c.NeverBreak = p => p == Target;
        var r = await c.DeconstructChestAsync(Target);
        Assert.False(r.Ok);
        Assert.Empty(s.Got<PlayerDiggingPacket>());
        Assert.Empty(s.Got<PlayerBlockPlacementPacket>());
    }
}
