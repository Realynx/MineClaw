using MineClaw.Data;
using MineClaw.Protocol.Packets;
using MineClaw.World;

namespace MineClaw.Client.Tests;

public sealed class StorageSignDepositTests
{
    private static readonly BlockPos Chest = new(9, 64, 8), Sign = new(9, 64, 7);
    private static readonly string[] Lines = ["wood", "", "", ""];

    private static StorageSignPolicy Policy(BlockPos? second = null, IReadOnlyList<string>? lines = null, BlockPos? sign = null,
        Func<int, int, bool>? accepts = null) => new(second is { } p ? [Chest, p] : [Chest],
            [(sign ?? Sign, lines ?? Lines)], accepts ?? ((id, _) => id == Blocks.Log));

    private static async Task<(MinecraftClient Client, FakeServer Server)> Start(bool modern)
    {
        var (c, s) = await Harness.StartAsync(server =>
        {
            server.Blocks[(Chest.X, Chest.Y, Chest.Z)] = Blocks.Chest;
            server.Blocks[(Sign.X, Sign.Y, Sign.Z)] = Blocks.WallSign;
            server.Metas[(Sign.X, Sign.Y, Sign.Z)] = 2;
            server.Chests[(Chest.X, Chest.Y, Chest.Z)] = Enumerable.Repeat(ItemStack.Empty, 27).ToArray();
            server.Inventory[36] = Harness.Stack(Blocks.Cobblestone, 8);
            server.Inventory[37] = Harness.Stack(Blocks.Log, 6);
        }, release1122: modern);
        await Text(c, s, Sign, Lines);
        var policy = Policy();
        c.StorageSignRules = (dim, at) => dim == 0 && at == Chest ? policy : null;
        return (c, s);
    }

    private static async Task Text(MinecraftClient c, FakeServer s, BlockPos at, string[] lines)
    {
        s.Send(new UpdateSignPacket { X = at.X, Y = (short)at.Y, Z = at.Z, Lines = lines });
        Assert.True(await Harness.WaitAsync(() => c.World.BlockEntities.Get(at)?.Lines?.SequenceEqual(lines) == true));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Deposit_prefilters_disallowed_stacks_but_still_stores_allowed_items(bool modern)
    {
        var (c, s) = await Start(modern);
        await using var client = c;
        using var server = s;
        Assert.True((await c.OpenContainerAsync(Chest.X, Chest.Y, Chest.Z)).Ok);
        var r = await c.DepositAllAsync(filter: _ => true);
        Assert.False(r.Ok);
        Assert.Equal(6, r.Count);
        Assert.Equal(8, c.World.Inventory.CountItem(Blocks.Cobblestone));
        Assert.Equal(0, c.World.Inventory.CountItem(Blocks.Log));
        var contents = c.World.Inventory.OpenWindow!.ContainerContents().Select(x => x.Item).ToArray();
        Assert.Equal(6, contents.Where(x => x.Id == Blocks.Log).Sum(x => x.Count));
        Assert.DoesNotContain(contents, x => x.Id == Blocks.Cobblestone);
        if (modern) Assert.Equal(6, s.Chests[(9, 64, 8)].Where(x => x.Id == Blocks.Log).Sum(x => x.Count));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Raw_shift_click_and_transfer_cannot_bypass_sign_but_withdrawal_can(bool modern)
    {
        var (c, s) = await Start(modern);
        await using var client = c;
        using var server = s;
        s.Chests[(9, 64, 8)][0] = Harness.Stack(Blocks.Cobblestone, 4);
        Assert.True((await c.OpenContainerAsync(9, 64, 8)).Ok);
        int clicks = s.Got<WindowClickPacket>().Count();
        var r = await c.ClickSlotAsync(c.World.Inventory.OpenWindow!.HotbarStart, shift: true);
        Assert.False(r.Ok);
        Assert.Contains("storage sign", r.Message);
        Assert.False((await c.TransferAsync(Blocks.Cobblestone, 8, true)).Ok);
        Assert.Equal(clicks, s.Got<WindowClickPacket>().Count());
        Assert.True((await c.ClickSlotAsync(0, shift: true)).Ok);
        Assert.Equal(12, c.World.Inventory.CountItem(Blocks.Cobblestone));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task New_or_edited_labels_wait_for_a_matching_publication_and_plain_labels_allow_items(bool modern)
    {
        var (c, s) = await Start(modern);
        await using var client = c;
        using var server = s;
        c.StorageSignRules = (_, _) => null;
        Assert.False((await c.DepositAllAsync(Chest, _ => true)).Ok);
        await Text(c, s, Sign, ["Alex supplies", "", "", ""]);
        var label = Policy(lines: ["Alex supplies", "", "", ""], accepts: (_, _) => true);
        c.StorageSignRules = (_, at) => at == Chest ? label : null;
        Assert.True((await c.OpenContainerAsync(9, 64, 8)).Ok);
        await Text(c, s, Sign, ["stone", "", "", ""]);
        Assert.False((await c.ClickSlotAsync(c.World.Inventory.OpenWindow!.HotbarStart, shift: true)).Ok);
        await Text(c, s, Sign, ["Alex supplies", "", "", ""]);
        Assert.True((await c.ClickSlotAsync(c.World.Inventory.OpenWindow!.HotbarStart, shift: true)).Ok);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cursor_deposit_is_guarded_without_losing_or_dropping_carried_items(bool modern)
    {
        var (c, s) = await Start(modern);
        await using var client = c;
        using var server = s;
        Assert.True((await c.OpenContainerAsync(9, 64, 8)).Ok);
        int hotbar = c.World.Inventory.OpenWindow!.HotbarStart;
        Assert.True((await c.ClickSlotAsync(hotbar)).Ok);
        Assert.False((await c.ClickSlotAsync(0)).Ok);
        Assert.Equal(8, c.World.Inventory.Cursor.Count);
        Assert.True((await c.ClickSlotAsync(hotbar)).Ok);
        Assert.Equal(8, c.World.Inventory.CountItem(Blocks.Cobblestone));
        Assert.All(s.Chests[(9, 64, 8)], x => Assert.True(x.IsEmpty));
    }

    [Fact]
    public async Task Number_key_swap_and_drag_recheck_the_rule_before_final_insertion()
    {
        var (c, s) = await Start(true);
        await using var client = c;
        using var server = s;
        Assert.True((await c.OpenContainerAsync(9, 64, 8)).Ok);
        Assert.False((await c.ClickSlotAsync(0, ClickMode.Swap, 0)).Ok);
        int hotbar = c.World.Inventory.OpenWindow!.HotbarStart;
        Assert.True((await c.ClickSlotAsync(hotbar + 1)).Ok); // allowed logs on the cursor
        Assert.True((await c.ClickSlotAsync(-999, ClickMode.QuickCraft, 0)).Ok);
        Assert.True((await c.ClickSlotAsync(0, ClickMode.QuickCraft, 1)).Ok);
        await Text(c, s, Sign, ["stone", "", "", ""]);
        Assert.False((await c.ClickSlotAsync(-999, ClickMode.QuickCraft, 2)).Ok);
        Assert.Equal(6, c.World.Inventory.Cursor.Count);
        Assert.All(s.Chests[(9, 64, 8)], x => Assert.True(x.IsEmpty));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_label_on_the_other_double_chest_half_constrains_this_half(bool modern)
    {
        var (c, s) = await Start(modern);
        await using var client = c;
        using var server = s;
        var half = Chest.Offset(1, 0, 0);
        var label = half.Offset(0, 0, -1);
        s.Send(new BlockChangePacket { X = Sign.X, Y = (byte)Sign.Y, Z = Sign.Z, BlockId = 0 });
        s.Send(new BlockChangePacket { X = half.X, Y = (byte)half.Y, Z = half.Z, BlockId = Blocks.Chest });
        s.Send(new BlockChangePacket { X = label.X, Y = (byte)label.Y, Z = label.Z, BlockId = Blocks.WallSign, Metadata = 2 });
        await Text(c, s, label, Lines);
        Assert.True(await Harness.WaitAsync(() => c.World.GetBlock(Sign) == 0 && ChestPairs.Partner(c.World, Chest) == half));
        var policy = Policy(second: half, sign: label);
        c.StorageSignRules = (_, at) => at == Chest || at == half ? policy : null;
        Assert.False((await c.DepositAllAsync(Chest, _ => true)).Ok);
        Assert.Equal(8, c.World.Inventory.CountItem(Blocks.Cobblestone));
        Assert.Equal(0, c.World.Inventory.CountItem(Blocks.Log));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Persisted_label_on_an_unloaded_adjacent_half_keeps_its_restriction(bool modern)
    {
        var chest = new BlockPos(15, 64, 8);
        var half = chest.Offset(1, 0, 0);
        var sign = half.Offset(0, 0, -1);
        var (c, s) = await Harness.StartAsync(server =>
        {
            server.SpawnAt = (14.5, 64, 8.5);
            for (int x = 13; x <= 16; x++) for (int z = 7; z <= 9; z++) server.Blocks[(x, 63, z)] = Blocks.Stone;
            server.Blocks[(15, 64, 8)] = Blocks.Chest;
            server.Chests[(15, 64, 8)] = Enumerable.Repeat(ItemStack.Empty, 27).ToArray();
            server.Inventory[36] = Harness.Stack(Blocks.Cobblestone, 8);
            server.Inventory[37] = Harness.Stack(Blocks.Log, 6);
        }, release1122: modern);
        await using var client = c;
        using var server = s;
        c.World.UnloadColumn(1, 0);
        var policy = new StorageSignPolicy([chest, half], [(sign, Lines)], (id, _) => id == Blocks.Log);
        c.StorageSignRules = (dim, at) => dim == 0 && (at == chest || at == half) ? policy : null;
        Assert.False((await c.DepositAllAsync(chest, _ => true)).Ok);
        Assert.Equal(8, c.World.Inventory.CountItem(Blocks.Cobblestone));
        Assert.Equal(0, c.World.Inventory.CountItem(Blocks.Log));
    }

    [Fact]
    public async Task Policy_lookup_uses_the_body_dimension()
    {
        var (c, s) = await Start(false);
        await using var client = c;
        using var server = s;
        int? asked = null;
        var policy = Policy();
        c.World.Dimension = -1;
        c.StorageSignRules = (dim, _) => { asked = dim; return policy; };
        Assert.False((await c.DepositAllAsync(Chest, _ => true)).Ok);
        Assert.Equal(-1, asked);
        Assert.Equal(8, c.World.Inventory.CountItem(Blocks.Cobblestone));
    }
}
