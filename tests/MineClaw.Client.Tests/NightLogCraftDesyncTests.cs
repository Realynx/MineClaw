using MineClaw.Data;
using MineClaw.Protocol;
using MineClaw.Protocol.Packets;
using MineClaw.World;

namespace MineClaw.Client.Tests;

/// <summary>
/// Night log 2026-10-27, B7: clone 69 crafting stone pickaxes. Window-0 clicks were rejected, the worker saw "9 cobblestone,
/// no planks" while the server held 26 sticks and 2 logs (they reappeared as a phantom "+26 stick, +2 oak wood" on the next
/// container 0x68 and were deposited at logout), and the job died after 25 rounds of "ran out of ingredients" / "missing 1
/// Wood". Cause: b1.8.1 sends no 0x67 for an accepted click's changes and echoes stale slot copies on its next tick (after a
/// closed chest window or a rejected click). An echo generated before the server applied the click in flight arrives ahead
/// of that click's 0x6A and silently overwrote the prediction; the server then accepted the click and never corrected it.
/// These tests run the client against a server-side copy of the b1.8.1 click rules that answers like NetServerHandler.
/// </summary>
public class NightLogCraftDesyncTests
{
    /// <summary>Window 0 on the "server": vanilla slotClick, expected-item check, 0x6A, and on a mismatch 0x68 + cursor.</summary>
    private sealed class VanillaWindow0
    {
        public readonly InventoryManager Srv = new() { CraftResult = (g, w) => Recipes.Match(g, w)?.Output ?? ItemStack.Empty };
        /// <summary>Packets the server's tick sends before it applies this click (null = none).</summary>
        public Func<WindowClickPacket, IEnumerable<IPacket>?>? TickBefore;
        /// <summary>Reject every real (non-probe) click without applying it.</summary>
        public bool RejectAll;
        public int Rejected, Probes, Clicks;

        public bool Handle(FakeServer s, IPacket p)
        {
            if (p is not WindowClickPacket c || c.WindowId != 0) return false;
            foreach (var e in TickBefore?.Invoke(c) ?? []) s.Send(e);
            bool probe = c.ClickedItem.Damage == 0x7FFF;
            if (probe) Probes++; else Clicks++;
            bool ok;
            if (RejectAll && !probe) ok = false;
            else
            {
                var r = Srv.ApplyClick(c.Slot, c.RightClick, c.Shift);
                ok = Same(r.ExpectedItem, c.ClickedItem);
            }
            s.Send(new TransactionPacket { WindowId = 0, ActionNumber = c.ActionNumber, Accepted = ok });
            if (!ok)
            {
                if (!probe) Rejected++;
                s.Send(new WindowItemsPacket { WindowId = 0, Items = Srv.PlayerSlots() });
                s.Send(new SetSlotPacket { WindowId = -1, Slot = -1, Item = Srv.Cursor });
            }
            return true;
        }

        private static bool Same(ItemStack a, ItemStack b) => a.IsEmpty ? b.IsEmpty : !b.IsEmpty && a.Id == b.Id && a.Count == b.Count && a.Damage == b.Damage;
    }

    private static async Task<(MinecraftClient C, FakeServer S, VanillaWindow0 V)> StartAsync(Action<ItemStack[]> inv)
    {
        var v = new VanillaWindow0();
        var (c, s) = await Harness.StartAsync(srv =>
        {
            inv(srv.Inventory);
            v.Srv.SetWindowItems(0, srv.Inventory);
            srv.Hook = p => v.Handle(srv, p);
        });
        return (c, s, v);
    }

    private static void AssertSameAsServer(MinecraftClient c, VanillaWindow0 v)
    {
        var local = c.World.Inventory.PlayerSlots();
        var srv = v.Srv.PlayerSlots();
        for (int i = 0; i < 45; i++) Assert.True(local[i].Equals(srv[i]), $"slot {i}: local {local[i]} vs server {srv[i]}");
        Assert.Equal(v.Srv.Cursor, c.World.Inventory.Cursor);
    }

    [Fact]
    public async Task Tick_echo_of_the_withdrawn_planks_racing_the_first_click_is_resynced_not_kept_as_a_phantom()
    {
        // 69 at 07:14: planks just withdrawn from the house chest into slot 9 (chest window closed), 2 sticks + 9 cobble in
        // the hotbar. ContainerPlayer's copy of slot 9 is stale (the withdrawal went through the chest window), so the next
        // server tick echoes "slot 9 = 8 planks" — generated before it applied our first click (pick up those planks).
        var (c, s, v) = await StartAsync(inv =>
        {
            inv[9] = Harness.Stack(Blocks.Planks, 8);
            inv[37] = Harness.Stack(Items.Stick, 2);
            inv[38] = Harness.Stack(Blocks.Cobblestone, 9);
        });
        await using var _ = c;
        using var __ = s;
        bool echoed = false;
        v.TickBefore = click => echoed ? null : (echoed = true) ? [new SetSlotPacket { WindowId = 0, Slot = 9, Item = v.Srv.GetPlayerSlot(9) }] : null;

        var r = await c.CraftAsync(Items.Stick, -1, 16);

        Assert.True(r.Ok, r.ToString());
        Assert.True(echoed);
        AssertSameAsServer(c, v);                                   // no phantom planks left behind
        Assert.Equal(2 + 16, v.Srv.CountItem(Items.Stick));
        Assert.Equal(0, v.Srv.CountItem(Blocks.Planks));
        Assert.Equal(18, c.World.Inventory.CountItem(Items.Stick));
        Assert.Contains("crafted 16 stick", r.Message, StringComparison.OrdinalIgnoreCase); // the count is the server's
        Assert.Contains("server-confirmed", r.Message);
        Assert.True(v.Probes >= 1);
        Assert.True(v.Rejected <= 1, $"{v.Rejected} real clicks rejected");
    }

    [Fact]
    public async Task Without_a_race_crafting_needs_no_resync()
    {
        var (c, s, v) = await StartAsync(inv => inv[9] = Harness.Stack(Blocks.Planks, 8));
        await using var _ = c;
        using var __ = s;
        var r = await c.CraftAsync(Items.Stick, -1, 16);
        Assert.True(r.Ok, r.ToString());
        AssertSameAsServer(c, v);
        Assert.Equal(0, v.Rejected);
        Assert.Equal(0, v.Probes);
        Assert.DoesNotContain("server-confirmed", r.Message);
    }

    [Fact]
    public async Task Missing_materials_are_rechecked_against_the_server_before_failing()
    {
        // The 07:15 failures: the local copy had lost the 26 sticks the server still held, so craft said "missing" and the
        // worker crafted sticks again and again. Before failing, craft asks the server; it then crafts from the real stacks.
        var (c, s, v) = await StartAsync(inv =>
        {
            inv[40] = Harness.Stack(Items.Stick, 26);
            inv[41] = Harness.Stack(Items.Coal, 2);
        });
        await using var _ = c;
        using var __ = s;
        // A stray update the client mis-applied: locally the sticks are gone (the server still has them).
        s.Send(new SetSlotPacket { WindowId = 0, Slot = 40, Item = ItemStack.Empty });
        Assert.True(await Harness.WaitAsync(() => c.World.Inventory.CountItem(Items.Stick) == 0));

        var r = await c.CraftAsync(Blocks.Torch, -1, 8);

        Assert.True(r.Ok, r.ToString());
        AssertSameAsServer(c, v);
        Assert.Equal(8, v.Srv.CountItem(Blocks.Torch));
        Assert.Equal(24, v.Srv.CountItem(Items.Stick));
        Assert.Equal(1, v.Probes);
    }

    [Fact]
    public async Task A_server_that_keeps_rejecting_gets_capped_retries_and_an_honest_failure()
    {
        var (c, s, v) = await StartAsync(inv => inv[9] = Harness.Stack(Blocks.Planks, 8));
        await using var _ = c;
        using var __ = s;
        v.RejectAll = true;

        var r = await c.CraftAsync(Items.Stick, -1, 4);

        Assert.False(r.Ok);
        Assert.Contains("kept rejecting", r.Message);
        Assert.Contains("crafted 0/", r.Message);
        Assert.Contains("8 ", r.Message);                           // the server-confirmed planks it still has
        Assert.True(v.Clicks <= 3 * (NightLogCraftDesyncTestsLimits.ClicksPerBatch), $"{v.Clicks} clicks");
        AssertSameAsServer(c, v);
        Assert.Equal(0, c.World.Inventory.CountItem(Items.Stick)); // no phantom sticks reported or kept
    }

    [Fact]
    public void Predicted_clicks_report_the_slots_they_changed()
    {
        var inv = new InventoryManager();
        inv.SetWindowItems(0, Enumerable.Range(0, 45).Select(i => i == 9 ? new ItemStack(Blocks.Planks, 8, 0) : ItemStack.Empty).ToArray());
        var pick = inv.ApplyClick(9, false, false);
        Assert.Equal([9], pick.ChangedSlots);
        Assert.True(pick.CursorChanged);
        var place = inv.ApplyClick(1, true, false);                // one plank into the grid: grid cell (no recipe evaluator)
        Assert.Contains(1, place.ChangedSlots);
        Assert.True(place.CursorChanged);
    }

    [Fact]
    public void Probe_slot_is_a_harmless_empty_slot()
    {
        var inv = new InventoryManager();
        Assert.Equal(0, MinecraftClient.ProbeSlot(inv.PlayerWindow));       // empty grid: the crafting output
        inv.SetSlot(0, 1, new ItemStack(Blocks.Planks, 1, 0));
        Assert.Equal(5, MinecraftClient.ProbeSlot(inv.PlayerWindow));       // grid in use: an empty armor slot
    }
}

internal static class NightLogCraftDesyncTestsLimits
{
    /// <summary>Upper bound of clicks one stick batch takes (pick up, place, stash, output, grid cleanup).</summary>
    public const int ClicksPerBatch = 12;
}
