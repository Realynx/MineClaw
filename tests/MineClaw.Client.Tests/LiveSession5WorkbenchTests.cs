using MineClaw.Data;
using MineClaw.Protocol;
using MineClaw.Protocol.Packets;
using MineClaw.World;

namespace MineClaw.Client.Tests;

/// <summary>
/// Live 10-27 22:50: "crafting bread: the server kept rejecting my window clicks (3 resyncs); crafted 0/4, server-confirmed
/// inventory has 25 Wheat". Rejections came in pairs per attempt (41/45, 72/76, 103/107, 122/126, 160/164): the shift-click
/// on the output, then the 4th grid return. b1.8.1 opens the crafting table with Packet100OpenWindow(id, 1, "Crafting", 9)
/// (mc-dev EntityPlayer.b(x,y,z)): 9 counts the grid only, ContainerWorkbench has 10 own slots. The client took the 9, so its
/// player part started at window slot 9 = grid cell 9. With the hotbar full, the leftover wheat (25 - 3x8, 25 - 3x4) was
/// "stashed" into slot 9 — on the server the bottom-right grid cell — so the grid matched no recipe, the output shift-click
/// returned null (rejected), and the model's predicted craft ate the leftover (ledger "+1 / +4 Wheat: server correction").
/// The 21:05 craft (21 wheat, no leftover) never stashed, so it got through.
/// Server side here: the client's own b1.8.1 click rules with the real 10-slot workbench, the 9 in the 0x64, expected-item
/// check, 0x6A, and 0x68 + cursor on a mismatch (NetServerHandler.a(Packet102WindowClick)).
/// </summary>
public class LiveSession5WorkbenchTests
{
    private static readonly (int X, int Y, int Z) Table = (9, 64, 8);

    private sealed class VanillaWorkbench
    {
        public readonly InventoryManager Srv = new() { CraftResult = (g, w) => Recipes.Match(g, w)?.Output ?? ItemStack.Empty };
        public int Rejected, Clicks, Opens;
        private sbyte _nextId;

        public bool Handle(FakeServer s, IPacket p)
        {
            switch (p)
            {
                case PlayerBlockPlacementPacket place when place.Direction != PlayerBlockPlacementPacket.NoDirection
                                                           && (place.X, place.Y, place.Z) == Table:
                {
                    sbyte id = ++_nextId;
                    Opens++;
                    var w = Srv.Open(id, WindowType.Workbench, "Crafting", 10); // the server's real container: 10 + 36 slots
                    s.Send(new OpenWindowPacket { WindowId = id, InventoryType = 1, Title = "Crafting", SlotCount = 9 }); // what b1.8.1 sends
                    s.Send(new WindowItemsPacket { WindowId = id, Items = Enumerable.Range(0, w.SlotCount).Select(w.Get).ToArray() });
                    return true;
                }
                case CloseWindowPacket close:
                    if (Srv.OpenWindow is { } open && open.Id == close.WindowId)
                    {
                        // ContainerWorkbench.a(EntityHuman): the grid goes back to the player (dropped in vanilla); keep it countable.
                        for (int i = 1; i <= 9; i++)
                            if (!open.Get(i).IsEmpty) { Give(open.Get(i)); open.Set(i, ItemStack.Empty); }
                        Srv.Close(close.WindowId);
                    }
                    return true;
                case TransactionPacket:
                    return true;
                case WindowClickPacket c:
                {
                    var active = Srv.ActiveWindow;
                    if (c.WindowId != active.Id) return true; // vanilla ignores a click for another window (no 0x6A)
                    Clicks++;
                    bool probe = c.ClickedItem.Damage == 0x7FFF;
                    var r = Srv.ApplyClick(c.Slot, c.RightClick, c.Shift);
                    bool ok = Same(r.ExpectedItem, c.ClickedItem);
                    s.Send(new TransactionPacket { WindowId = c.WindowId, ActionNumber = c.ActionNumber, Accepted = ok });
                    if (!ok)
                    {
                        if (!probe) Rejected++;
                        s.Send(new WindowItemsPacket { WindowId = c.WindowId, Items = Enumerable.Range(0, active.SlotCount).Select(active.Get).ToArray() });
                        s.Send(new SetSlotPacket { WindowId = -1, Slot = -1, Item = Srv.Cursor });
                    }
                    return true;
                }
            }
            return false;
        }

        private void Give(ItemStack item)
        {
            var slots = Srv.PlayerSlots();
            for (int i = 9; i < 45; i++)
                if (slots[i].IsEmpty) { Srv.SetSlot(0, i, item); return; }
        }

        private static bool Same(ItemStack a, ItemStack b) => a.IsEmpty ? b.IsEmpty : !b.IsEmpty && a.Id == b.Id && a.Count == b.Count && a.Damage == b.Damage;
    }

    /// <summary>The 22:50 pack: hotbar full (tools, seeds, flesh, cobble), the last main slot taken, 25 wheat in the main inventory.</summary>
    private static void LivePack(ItemStack[] inv)
    {
        int[] hotbar = [Items.StoneHoe, Items.StoneSword, Items.StonePickaxe, Blocks.Cobblestone, Items.Seeds, Items.RottenFlesh, Items.Stick, Blocks.Dirt, Blocks.Sapling];
        for (int i = 0; i < 9; i++)
            inv[36 + i] = Harness.Stack(hotbar[i], i < 3 ? 1 : 7 + i);
        inv[35] = Harness.Stack(Blocks.Cobblestone, 64);
        inv[20] = Harness.Stack(Items.Wheat, 25);
    }

    private static async Task<(MinecraftClient C, FakeServer S, VanillaWorkbench V)> StartAsync(Action<ItemStack[]> inv)
    {
        var v = new VanillaWorkbench();
        var (c, s) = await Harness.StartAsync(srv =>
        {
            inv(srv.Inventory);
            srv.Blocks[Table] = Blocks.Workbench;
            v.Srv.SetWindowItems(0, srv.Inventory);
            srv.Hook = p => v.Handle(srv, p);
        });
        return (c, s, v);
    }

    private static void AssertSameAsServer(MinecraftClient c, VanillaWorkbench v)
    {
        var local = c.World.Inventory.PlayerSlots();
        var srv = v.Srv.PlayerSlots();
        for (int i = 9; i < 45; i++) Assert.True(local[i].Equals(srv[i]), $"slot {i}: local {local[i]} vs server {srv[i]}");
        Assert.Equal(v.Srv.Cursor, c.World.Inventory.Cursor);
    }

    [Theory]
    [InlineData(4, 13)] // the failing call: 3 cells x 4, 13 wheat left on the cursor to stash
    [InlineData(8, 1)]  // the first call: 3 cells x 8, 1 wheat left over
    public async Task Bread_with_a_leftover_and_a_full_hotbar_crafts_without_a_single_rejected_click(int bread, int wheatLeft)
    {
        var (c, s, v) = await StartAsync(LivePack);
        await using var _ = c;
        using var __ = s;

        var r = await c.CraftAsync(Items.Bread, -1, bread);

        Assert.True(r.Ok, r.ToString());
        Assert.True(await Harness.WaitAsync(() => v.Srv.OpenWindow is null)); // the 0x65 reached the server; grid emptied first
        Assert.Equal(0, v.Rejected);
        Assert.Equal(1, v.Opens);
        Assert.Equal(bread, v.Srv.CountItem(Items.Bread));
        Assert.Equal(wheatLeft, v.Srv.CountItem(Items.Wheat));
        Assert.Contains($"crafted {bread} bread", r.Message, StringComparison.OrdinalIgnoreCase);
        AssertSameAsServer(c, v);
        Assert.True(v.Clicks <= 3 * bread + 8, $"{v.Clicks} clicks");   // pick up, 3 cells, stash, output, no retries
    }

    [Fact]
    public async Task A_chest_ring_uses_grid_cell_nine_and_is_produced()
    {
        // Live 10-27 23:10: "grid didn't produce chest (recipe mismatch?)" after withdrawing 2 oak wood. The ring of 8 planks
        // fills cell 9, which the 9-slot window took for an inventory slot: the predicted output was never recomputed.
        var (c, s, v) = await StartAsync(inv =>
        {
            LivePack(inv);
            inv[20] = ItemStack.Empty;
            inv[12] = Harness.Stack(Blocks.Log, 2);
        });
        await using var _ = c;
        using var __ = s;

        var r = await c.CraftAsync(Blocks.Chest, -1, 1);

        Assert.True(r.Ok, r.ToString());
        Assert.True(await Harness.WaitAsync(() => v.Srv.OpenWindow is null));
        Assert.Equal(0, v.Rejected);
        Assert.Equal(1, v.Srv.CountItem(Blocks.Chest));
        Assert.Equal(0, v.Srv.CountItem(Blocks.Planks));
        Assert.Equal(0, v.Srv.CountItem(Blocks.Log));
        AssertSameAsServer(c, v);
    }

    [Fact]
    public async Task The_workbench_window_has_ten_own_slots_although_the_server_says_nine()
    {
        var (c, s, v) = await StartAsync(LivePack);
        await using var _ = c;
        using var __ = s;
        Window? opened = null;
        c.World.Inventory.WindowOpened += w => opened = w;
        s.Send(new OpenWindowPacket { WindowId = 7, InventoryType = 1, Title = "Crafting", SlotCount = 9 });
        Assert.True(await Harness.WaitAsync(() => opened is not null));
        Assert.Equal(10, opened!.ContainerSlots);
        Assert.Equal(46, opened.SlotCount);
        Assert.Equal(10, opened.MainStart);
        Assert.Equal(37, opened.HotbarStart);
        Assert.Equal(SlotKind.CraftGrid, opened.KindOf(9));
        Assert.Equal(44, opened.ToPlayerSlot(45));                      // the last hotbar slot is reachable
    }

    [Theory]
    [InlineData(WindowType.Chest, 27, 27)]
    [InlineData(WindowType.Chest, 54, 54)]
    [InlineData(WindowType.Furnace, 3, 3)]   // getSize() is the real slot count for these (mc-dev EntityPlayer.a(...))
    [InlineData(WindowType.Dispenser, 9, 9)]
    [InlineData(WindowType.Workbench, 9, 10)]
    [InlineData(WindowType.Workbench, 10, 10)]
    public void Container_slot_counts_follow_the_server_container(WindowType type, int sent, int own)
    {
        var inv = new InventoryManager();
        var w = inv.Open(3, type, type.ToString(), sent);
        Assert.Equal(own, w.ContainerSlots);
        Assert.Equal(own + 36, w.SlotCount);
        Assert.Equal(9, w.ToPlayerSlot(w.MainStart));
        Assert.Equal(44, w.ToPlayerSlot(w.SlotCount - 1));
    }
}
