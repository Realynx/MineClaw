using MineClaw.Data;
using MineClaw.Protocol.Packets;

namespace MineClaw.Client.Tests;

/// <summary>Live session 2, item 17: deposit-before-logout must read the server's inventory, not a stale/predicted copy.</summary>
public class InventoryResyncTests
{
    [Fact]
    public async Task Resync_replaces_a_stale_local_inventory_with_the_servers()
    {
        var (c, s) = await Harness.StartAsync();
        await using var _ = c;
        using var __ = s;
        Assert.Equal(0, c.World.Inventory.CountItem(Items.Stick));
        // The server holds 4 sticks the client's copy never heard about (e.g. a click it predicted away was bounced).
        s.Inventory[20] = Harness.Stack(Items.Stick, 4);
        s.RejectActions.Add(1); // vanilla: the probe's expected item can't match the empty craft-output slot
        var r = await c.ResyncInventoryAsync();
        Assert.True(r.Ok, r.ToString());
        Assert.Equal(4, c.World.Inventory.CountItem(Items.Stick));
        var probe = Assert.Single(s.Got<WindowClickPacket>());
        Assert.Equal(0, probe.WindowId);
        Assert.Equal(0, probe.Slot);
        Assert.False(probe.ClickedItem.IsEmpty);
        Assert.True(s.WaitFor(p => p is TransactionPacket { ActionNumber: 1, Accepted: true })); // the rejection is acknowledged
    }
}
