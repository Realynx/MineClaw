using MineClaw.Data;
using MineClaw.Protocol.Packets;

namespace MineClaw.Client.Tests;

/// <summary>Story Mode's gear primitives on the client (MinecraftClient.Gear.cs): strict harvest, the held tool kept, hold and wear.</summary>
public sealed class GearClientTests
{
    [Fact]
    public async Task A_strict_dig_refuses_a_block_its_tools_would_lose_and_digs_it_with_the_right_one()
    {
        var (c, s) = await Harness.StartAsync(srv =>
        {
            srv.Inventory[20] = Harness.Stack(Items.WoodenPickaxe, 1);
            srv.Blocks[(9, 64, 8)] = Blocks.IronOre;
        });
        await using var _ = c;
        using var __ = s;
        c.StrictHarvest = true;
        var r = await c.DigBlockAsync(9, 64, 8);
        Assert.False(r.Ok);
        Assert.Contains("needs a stone pickaxe", r.Message);
        Assert.DoesNotContain(s.Got<PlayerDiggingPacket>(), p => p.Status == PlayerDiggingPacket.StatusStarted);
        Assert.Equal(Blocks.IronOre, c.World.GetBlock(9, 64, 8));
        // Off (main's default): the old behaviour, it digs.
        c.StrictHarvest = false;
        Assert.True((await c.DigBlockAsync(9, 64, 8)).Ok);
    }

    [Fact]
    public async Task Prefer_held_tool_keeps_the_chosen_pickaxe_in_hand_and_hold_and_wear_move_items()
    {
        var (c, s) = await Harness.StartAsync(srv =>
        {
            srv.Inventory[36] = Harness.Stack(Items.StonePickaxe, 1);
            srv.Inventory[20] = Harness.Stack(Items.DiamondPickaxe, 1);
            srv.Inventory[21] = Harness.Stack(306, 1); // an iron helmet
            srv.Blocks[(9, 64, 8)] = Blocks.Stone;
        });
        await using var _ = c;
        using var __ = s;
        c.PreferHeldTool = true;
        var held = await c.HoldAsync(st => st.Id == Items.StonePickaxe, "stone pickaxe");
        Assert.True(held.Ok, held.ToString());
        var r = await c.DigBlockAsync(9, 64, 8);
        Assert.True(r.Ok, r.ToString());
        Assert.DoesNotContain("diamond", r.Message.ToLowerInvariant()); // dug with the stone one, not the faster diamond one
        Assert.Equal(Items.StonePickaxe, c.World.Inventory.HeldItem(c.World.Player.HeldSlot).Id);
        Assert.DoesNotContain(s.Got<WindowClickPacket>(), k => k.Slot == 20);
        var wear = await c.WearAsync(st => st.Id == 306, "iron helmet");
        Assert.True(wear.Ok, wear.ToString());
        Assert.Equal(306, c.World.Inventory.GetPlayerSlot(5).Id);
        var off = await c.UnwearAsync();
        Assert.True(off.Ok, off.ToString());
        Assert.True(c.World.Inventory.GetPlayerSlot(5).IsEmpty);
        Assert.Equal(5, MinecraftClient.ArmorSlotOf(306));
        Assert.Equal(8, MinecraftClient.ArmorSlotOf(313));
        Assert.Equal(-1, MinecraftClient.ArmorSlotOf(Items.StonePickaxe));
    }
}
