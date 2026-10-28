using MineClaw.Data;

namespace MineClaw.World.Tests;

/// <summary>
/// Multi-version P2: the 1.12.2 inventory model (off hand at slot 45) and its click prediction, ported from the decompiled
/// <c>Container.slotClick</c> and the containers' <c>transferStackInSlot</c>; and the placement cursor rules
/// (<c>BlockSlab</c>/<c>BlockStairs.getStateForPlacement</c>, <c>BlockLog</c>, <c>BlockTorch</c>).
/// </summary>
public class Inventory1122Tests
{
    private static ItemStack St(int id, int n, int dmg = 0) => new((short)id, (byte)n, (short)dmg);

    private static InventoryManager Inv()
    {
        var inv = new InventoryManager(model: InventoryModel.Release1122)
        {
            CraftResult = (grid, width) => Recipes.Match(grid, width)?.Output ?? ItemStack.Empty,
        };
        return inv;
    }

    [Fact]
    public void Player_window_has_the_offhand_at_45()
    {
        var inv = Inv();
        Assert.Equal(46, inv.PlayerWindow.SlotCount);
        Assert.Equal(SlotKind.Offhand, inv.PlayerWindow.KindOf(45));
        inv.SetWindowItems(0, Enumerable.Range(0, 46).Select(i => i == 45 ? St(Blocks.Torch, 10) : ItemStack.Empty).ToArray());
        Assert.Equal(St(Blocks.Torch, 10), inv.Offhand);
        Assert.Equal(0, inv.CountItem(Blocks.Torch));             // equipment, like armor
        Assert.Equal(10, inv.CountItem(Blocks.Torch, includeAll: true));
        // A chest window still maps 36 player slots (9..44); the off hand isn't part of it.
        var chest = inv.Open(1, WindowType.Chest, "Chest", 27);
        Assert.Equal(63, chest.SlotCount);
        Assert.Equal(-1, chest.FromPlayerSlot(45));
        // The b1.8.1 model is unchanged.
        Assert.Equal(45, new InventoryManager().PlayerWindow.SlotCount);
        Assert.Throws<NotSupportedException>(() => new InventoryManager().ApplyClick(36, ClickMode.Swap, 0));
    }

    [Fact]
    public void Swap_hands_and_number_keys()
    {
        var inv = Inv();
        inv.SetSlot(0, 36, St(Items.Bread, 5));
        Assert.True(inv.SwapHands(0));
        Assert.Equal(St(Items.Bread, 5), inv.Offhand);
        Assert.True(inv.GetPlayerSlot(36).IsEmpty);
        // Swap (number key 2): slot 20 ↔ hotbar 2; the expected item is always empty for a swap.
        inv.SetSlot(0, 20, St(Blocks.Cobblestone, 30));
        inv.SetSlot(0, 38, St(Blocks.Dirt, 7));
        var r = inv.ApplyClick(20, ClickMode.Swap, 2);
        Assert.True(r.ExpectedItem.IsEmpty);
        Assert.Equal((St(Blocks.Dirt, 7), St(Blocks.Cobblestone, 30)), (inv.GetPlayerSlot(20), inv.GetPlayerSlot(38)));
        Assert.Contains(20, r.ChangedSlots);
        Assert.Contains(38, r.ChangedSlots);
        // A number key onto the off hand works like any slot.
        inv.ApplyClick(45, ClickMode.Swap, 2);
        Assert.Equal(St(Blocks.Cobblestone, 30), inv.Offhand);
        Assert.Equal(St(Items.Bread, 5), inv.GetPlayerSlot(38));
    }

    [Fact]
    public void Shift_click_returns_what_the_server_compares()
    {
        var inv = Inv();
        // A full move empties the slot: slotClick's loop never assigns, the expected item is EMPTY (b1.8.1 returned the stack).
        inv.SetSlot(0, 10, St(Blocks.Cobblestone, 20));
        var full = inv.ApplyClick(10, ClickMode.QuickMove, 0);
        Assert.True(full.ExpectedItem.IsEmpty);
        Assert.Equal(St(Blocks.Cobblestone, 20), inv.GetPlayerSlot(36)); // main → hotbar
        // A partial move (the hotbar has room for 10) leaves the same item behind: the expected item is the stack before.
        for (int i = 36; i < 45; i++) inv.SetSlot(0, i, St(Blocks.Dirt, i == 44 ? 54 : 64));
        inv.SetSlot(0, 11, St(Blocks.Dirt, 30));
        var partial = inv.ApplyClick(11, ClickMode.QuickMove, 0);
        Assert.Equal(St(Blocks.Dirt, 30), partial.ExpectedItem);
        Assert.Equal(St(Blocks.Dirt, 20), inv.GetPlayerSlot(11));
        Assert.Equal(64, inv.GetPlayerSlot(44).Count);

        // Armor goes to its empty armor slot, a shield to the empty off hand.
        var inv2 = Inv();
        inv2.SetSlot(0, 12, St(Items.IronHelmet, 1));
        inv2.ApplyClick(12, ClickMode.QuickMove, 0);
        Assert.Equal(St(Items.IronHelmet, 1), inv2.GetPlayerSlot(5));
        inv2.SetSlot(0, 13, St(442, 1));
        inv2.ApplyClick(13, ClickMode.QuickMove, 0);
        Assert.Equal(St(442, 1), inv2.Offhand);
    }

    [Fact]
    public void Furnace_shift_click_routes_smeltables_and_fuel()
    {
        var inv = Inv();
        inv.SetSlot(0, 9, St(Blocks.IronOre, 8));
        inv.SetSlot(0, 10, St(Items.Coal, 4));
        var f = inv.Open(2, WindowType.Furnace, "Furnace", 3);
        inv.ApplyClick(f.FromPlayerSlot(9), ClickMode.QuickMove, 0);
        inv.ApplyClick(f.FromPlayerSlot(10), ClickMode.QuickMove, 0);
        Assert.Equal(St(Blocks.IronOre, 8), f.Get(0));
        Assert.Equal(St(Items.Coal, 4), f.Get(1));
    }

    [Fact]
    public void Throw_double_click_and_drag()
    {
        var inv = Inv();
        inv.SetSlot(0, 36, St(Blocks.Cobblestone, 10));
        inv.ApplyClick(36, ClickMode.Throw, 0); // Q: one
        Assert.Equal(9, inv.GetPlayerSlot(36).Count);
        inv.ApplyClick(36, ClickMode.Throw, 1); // ctrl+Q: the stack
        Assert.True(inv.GetPlayerSlot(36).IsEmpty);

        // Double click: gathers the same item into the cursor, partial stacks first.
        inv.SetSlot(0, 9, St(Blocks.Dirt, 64));
        inv.SetSlot(0, 10, St(Blocks.Dirt, 5));
        inv.SetSlot(0, 11, St(Blocks.Dirt, 7));
        inv.ApplyClick(11, ClickMode.Pickup, 0); // 7 on the cursor
        inv.ApplyClick(11, ClickMode.PickupAll, 0);
        Assert.Equal(St(Blocks.Dirt, 64), inv.Cursor); // 7 + 5 + 52 of the full stack
        Assert.True(inv.GetPlayerSlot(10).IsEmpty);
        Assert.Equal(12, inv.GetPlayerSlot(9).Count);

        // Drag: start (button 0 = stage 0, split evenly), add slots (stage 1 = button 1), end (stage 2 = button 2).
        var d = Inv();
        d.SetSlot(-1, -1, St(Blocks.Sand, 10));
        d.ApplyClick(-999, ClickMode.QuickCraft, 0);
        foreach (int s in new[] { 36, 37, 38 }) d.ApplyClick(s, ClickMode.QuickCraft, 1);
        var end = d.ApplyClick(-999, ClickMode.QuickCraft, 2);
        Assert.True(end.ExpectedItem.IsEmpty);
        Assert.Equal([3, 3, 3], new[] { 36, 37, 38 }.Select(i => (int)d.GetPlayerSlot(i).Count));
        Assert.Equal(St(Blocks.Sand, 1), d.Cursor);
        // Right-drag (split mode 1 = one each): button = stage | 1 << 2.
        d.ApplyClick(-999, ClickMode.QuickCraft, 4);
        d.ApplyClick(40, ClickMode.QuickCraft, 5);
        d.ApplyClick(-999, ClickMode.QuickCraft, 6);
        Assert.Equal(St(Blocks.Sand, 1), d.GetPlayerSlot(40));
        Assert.True(d.Cursor.IsEmpty);
    }

    [Fact]
    public void Nbt_keeps_stacks_apart_and_rides_along()
    {
        var inv = Inv();
        var tag = new ItemTag([10, 0, 0, 1, 0, 1, 0x61, 0, 0]);
        var named = St(Blocks.Cobblestone, 10) with { Tag = tag };
        inv.SetSlot(0, 36, named);
        inv.SetSlot(0, 37, St(Blocks.Cobblestone, 10));
        inv.ApplyClick(36, ClickMode.Pickup, 0);
        var r = inv.ApplyClick(37, ClickMode.Pickup, 0); // different tags: swap, not merge
        Assert.Equal(St(Blocks.Cobblestone, 10), r.ExpectedItem);
        Assert.Equal(named, inv.GetPlayerSlot(37));
        Assert.Equal(St(Blocks.Cobblestone, 10), inv.Cursor);
        Assert.Equal(tag, inv.GetPlayerSlot(37).Tag);
    }

    [Fact]
    public void Crafting_output_right_click_takes_the_whole_stack()
    {
        var inv = Inv();
        inv.SetSlot(0, 1, St(Blocks.Log, 2));
        inv.SetWindowItems(0, inv.PlayerSlots()); // grid change → output recomputed on the next click; set it like the server
        inv.SetSlot(0, 0, St(Blocks.Planks, 4));
        inv.ApplyClick(0, ClickMode.Pickup, 1); // InventoryCraftResult.decrStackSize: getAndRemove
        Assert.Equal(St(Blocks.Planks, 4), inv.Cursor);
        Assert.Equal(1, inv.GetPlayerSlot(1).Count); // one log consumed
        Assert.Equal(St(Blocks.Planks, 4), inv.GetPlayerSlot(0)); // the next craft shows
    }

    // ------------------------------------------------------------------ placement cursors

    [Theory]
    [InlineData(BlockFace.Top, PlaceHalf.Bottom, 0.5f, 1f, 0.5f, PlaceHalf.Bottom)]
    [InlineData(BlockFace.Bottom, PlaceHalf.Top, 0.5f, 0f, 0.5f, PlaceHalf.Top)]   // the Hidden underside click: a top slab
    [InlineData(BlockFace.North, PlaceHalf.Top, 0.5f, 0.75f, 0f, PlaceHalf.Top)]
    [InlineData(BlockFace.East, PlaceHalf.Bottom, 1f, 0.25f, 0.5f, PlaceHalf.Bottom)]
    [InlineData(BlockFace.West, PlaceHalf.Auto, 0f, 0.5f, 0.5f, PlaceHalf.Bottom)]   // 0.5 is not > 0.5: bottom
    public void Slab_and_stair_cursor(BlockFace face, PlaceHalf want, float x, float y, float z, PlaceHalf result)
    {
        Assert.Equal((x, y, z), PlaceCursor.For(face, want));
        Assert.Equal(result, PlaceCursor.Result(face, y));
    }

    [Fact]
    public void Halves_metas_and_faces()
    {
        Assert.True(PlaceCursor.IsSlab(Blocks.Slab) && PlaceCursor.IsSlab(126) && PlaceCursor.IsStairs(53) && PlaceCursor.IsStairs(203));
        Assert.Equal(PlaceHalf.Top, PlaceCursor.HalfOf(Blocks.Slab, 8 | 3));  // cobblestone slab, upper half
        Assert.Equal(PlaceHalf.Bottom, PlaceCursor.HalfOf(126, 2));
        Assert.Equal(PlaceHalf.Top, PlaceCursor.HalfOf(53, 4 | 1));           // upside-down oak stairs
        Assert.Equal(PlaceHalf.Auto, PlaceCursor.HalfOf(Blocks.Log, 8));
        Assert.False(PlaceCursor.FaceAllows(BlockFace.Bottom, PlaceHalf.Bottom)); // an underside click can't make a bottom slab
        Assert.False(PlaceCursor.FaceAllows(BlockFace.Top, PlaceHalf.Top));
        Assert.True(PlaceCursor.FaceAllows(BlockFace.South, PlaceHalf.Top));
        // Logs take the clicked face's axis; torches the face (metas 1..5 as b1.8.1). Neither reads the cursor.
        Assert.Equal(2 | 4, PlaceCursor.LogMeta(2, BlockFace.West));
        Assert.Equal(1 | 8, PlaceCursor.LogMeta(1, BlockFace.North));
        Assert.Equal(0, PlaceCursor.LogMeta(0, BlockFace.Top));
        Assert.Equal(5, PlaceRules.TorchMeta(BlockFace.Top));
        Assert.Equal(1, PlaceRules.TorchMeta(BlockFace.East));
    }
}
