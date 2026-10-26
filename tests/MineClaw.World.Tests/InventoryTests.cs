using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.World.Tests;

public class InventoryTests
{
    private static ItemStack S(int id, int count, int dmg = 0) => new((short)id, (byte)count, (short)dmg);

    private static InventoryManager New() => new(new ItemRules
    {
        MaxStack = id => id switch { 344 => 16, 276 => 1, 298 => 1, _ => 64 },
        IsDamageable = id => id is 276 or 298,
        HasSubtypes = id => id == 35,
    });

    [Fact]
    public void LeftClick_PickUpAndPutDown()
    {
        var inv = New();
        inv.SetSlot(0, 9, S(4, 20));
        var r = inv.ApplyClick(9, false, false);
        Assert.Equal(S(4, 20), r.ExpectedItem);
        Assert.Equal(S(4, 20), inv.Cursor);
        Assert.True(inv.GetPlayerSlot(9).IsEmpty);
        var r2 = inv.ApplyClick(10, false, false);
        Assert.True(r2.ExpectedItem.IsEmpty);
        Assert.True(inv.Cursor.IsEmpty);
        Assert.Equal(S(4, 20), inv.GetPlayerSlot(10));
        Assert.Equal(r.ActionNumber + 1, r2.ActionNumber);
    }

    [Fact]
    public void RightClick_SplitsHalf_AndPlacesOne()
    {
        var inv = New();
        inv.SetSlot(0, 9, S(4, 5));
        inv.ApplyClick(9, true, false);
        Assert.Equal(S(4, 3), inv.Cursor); // (5+1)/2
        Assert.Equal(S(4, 2), inv.GetPlayerSlot(9));
        inv.ApplyClick(10, true, false);
        Assert.Equal(S(4, 1), inv.GetPlayerSlot(10));
        Assert.Equal(S(4, 2), inv.Cursor);
        inv.ApplyClick(9, true, false); // onto same item: +1
        Assert.Equal(S(4, 3), inv.GetPlayerSlot(9));
        Assert.Equal(S(4, 1), inv.Cursor);
    }

    [Fact]
    public void Merge_RespectsMaxStack_AndSwapsDifferentItems()
    {
        var inv = New();
        inv.SetSlot(0, 9, S(4, 30));
        inv.SetSlot(-1, -1, S(4, 40));
        inv.ApplyClick(9, false, false);
        Assert.Equal(S(4, 64), inv.GetPlayerSlot(9));
        Assert.Equal(S(4, 6), inv.Cursor);

        inv.SetSlot(0, 10, S(344, 10)); // eggs stack to 16
        inv.SetSlot(-1, -1, S(344, 10));
        inv.ApplyClick(10, false, false);
        Assert.Equal(S(344, 16), inv.GetPlayerSlot(10));
        Assert.Equal(S(344, 4), inv.Cursor);

        inv.SetSlot(-1, -1, S(3, 7));
        var r = inv.ApplyClick(9, false, false);
        Assert.Equal(S(4, 64), r.ExpectedItem);
        Assert.Equal(S(3, 7), inv.GetPlayerSlot(9));
        Assert.Equal(S(4, 64), inv.Cursor);

        // Different wool colours don't merge (subtypes) — they swap.
        inv.SetSlot(0, 11, S(35, 5, 1));
        inv.SetSlot(-1, -1, S(35, 5, 2));
        inv.ApplyClick(11, false, false);
        Assert.Equal(S(35, 5, 2), inv.GetPlayerSlot(11));
        Assert.Equal(S(35, 5, 1), inv.Cursor);
    }

    [Fact]
    public void ArmorSlot_AcceptsOnlyMatchingArmor()
    {
        var inv = New();
        inv.SetSlot(-1, -1, S(4, 1));
        inv.ApplyClick(5, false, false);
        Assert.True(inv.GetPlayerSlot(5).IsEmpty);
        inv.SetSlot(-1, -1, S(298, 1)); // leather helmet
        inv.ApplyClick(6, false, false); // chestplate slot: rejected
        Assert.True(inv.GetPlayerSlot(6).IsEmpty);
        inv.ApplyClick(5, false, false);
        Assert.Equal(S(298, 1), inv.GetPlayerSlot(5));
    }

    [Fact]
    public void ShiftClick_PlayerWindow_MainToHotbarAndBack()
    {
        var inv = New();
        inv.SetSlot(0, 40, S(4, 60));
        inv.SetSlot(0, 9, S(4, 10));
        var r = inv.ApplyClick(9, false, true); // main → hotbar: tops up slot 40, rest to first empty hotbar (36)
        Assert.Equal(S(4, 10), r.ExpectedItem);
        Assert.Equal(S(4, 64), inv.GetPlayerSlot(40));
        Assert.Equal(S(4, 6), inv.GetPlayerSlot(36));
        Assert.True(inv.GetPlayerSlot(9).IsEmpty);

        inv.ApplyClick(36, false, true); // hotbar → main (first empty main slot 9)
        Assert.Equal(S(4, 6), inv.GetPlayerSlot(9));
        Assert.True(inv.GetPlayerSlot(36).IsEmpty);

        // Nothing moves → expected item is empty.
        var full = New();
        for (int i = 36; i < 45; i++) full.SetSlot(0, i, S(3, 64));
        full.SetSlot(0, 9, S(4, 1));
        Assert.True(full.ApplyClick(9, false, true).ExpectedItem.IsEmpty);
        Assert.Equal(S(4, 1), full.GetPlayerSlot(9));
    }

    [Fact]
    public void Chest_LayoutAndShiftClick()
    {
        var inv = New();
        inv.SetSlot(0, 36, S(4, 64)); // first hotbar slot
        var w = inv.Open(1, WindowType.Chest, "Chest", 27);
        Assert.Equal(63, w.SlotCount);
        Assert.Equal(27, w.MainStart);
        Assert.Equal(54, w.HotbarStart);
        Assert.Equal(S(4, 64), w[54]);
        Assert.Equal(36, w.ToPlayerSlot(54));

        inv.ApplyClick(54, false, true); // player → chest (first empty chest slot)
        Assert.Equal(S(4, 64), w[0]);
        Assert.True(inv.GetPlayerSlot(36).IsEmpty);

        inv.ApplyClick(0, false, true); // chest → player, filled from the end (last hotbar slot)
        Assert.Equal(S(4, 64), inv.GetPlayerSlot(44));
        Assert.True(w[0].IsEmpty);

        var large = inv.Open(2, WindowType.Chest, "Large chest", 54);
        Assert.Equal(90, large.SlotCount);
        Assert.Equal(S(4, 64), large[89]);
        inv.SetSlot(2, 53, S(5, 3));
        Assert.Equal(S(5, 3), inv.OpenWindow![53]);
        inv.Close(2);
        Assert.Null(inv.OpenWindow);
    }

    [Fact]
    public void Furnace_OutputIsTakeOnly_AndProgress()
    {
        var inv = New();
        var f = inv.Open(3, WindowType.Furnace, "Furnace", 3);
        Assert.Equal(39, f.SlotCount);
        inv.SetSlot(3, 2, S(265, 5)); // iron ingots in output
        inv.SetSlot(-1, -1, S(4, 1));
        inv.ApplyClick(2, false, false); // can't place into output; different item → nothing
        Assert.Equal(S(265, 5), f[2]);
        inv.SetSlot(-1, -1, S(265, 2));
        inv.ApplyClick(2, false, false); // same item → take all onto cursor
        Assert.Equal(S(265, 7), inv.Cursor);
        Assert.True(f[2].IsEmpty);

        inv.SetSlot(3, 2, S(265, 4));
        inv.SetSlot(-1, -1, ItemStack.Empty);
        inv.ApplyClick(2, false, true); // shift output → player (reverse: last hotbar)
        Assert.Equal(S(265, 4), inv.GetPlayerSlot(44));

        inv.SetProperty(3, 0, 100);
        inv.SetProperty(3, 1, 800);
        inv.SetProperty(3, 2, 1600);
        Assert.Equal(0.5, f.SmeltProgress, 6);
        Assert.Equal(0.5, f.FuelFraction, 6);
        Assert.True(f.IsBurning);
    }

    [Fact]
    public void CraftingOutput_ConsumesGrid_AndRecomputes()
    {
        var inv = New();
        // 1 log → 4 planks recipe stand-in.
        inv.CraftResult = (grid, width) =>
            grid.Count(g => !g.IsEmpty) == 1 && grid.First(g => !g.IsEmpty).Id == 17 ? S(5, 4) : ItemStack.Empty;
        var wb = inv.Open(4, WindowType.Workbench, "Crafting", 10);
        Assert.Equal(46, wb.SlotCount);
        inv.SetSlot(-1, -1, S(17, 2));
        inv.ApplyClick(5, false, false); // grid center
        Assert.Equal(S(5, 4), wb[0]);

        var r = inv.ApplyClick(0, true, false); // right-click output takes the whole stack
        Assert.Equal(S(5, 4), r.ExpectedItem);
        Assert.Equal(S(5, 4), inv.Cursor);
        Assert.Equal(S(17, 1), wb[5]);
        Assert.Equal(S(5, 4), wb[0]); // recomputed

        inv.ApplyClick(0, false, false); // cursor same item: add output
        Assert.Equal(S(5, 8), inv.Cursor);
        Assert.True(wb[5].IsEmpty);
        Assert.True(wb[0].IsEmpty);

        // Shift-click crafts repeatedly while the output stays the same item.
        inv.SetSlot(-1, -1, ItemStack.Empty);
        inv.SetSlot(4, 1, S(17, 3));
        inv.SetSlot(4, 0, S(5, 4));
        inv.ApplyClick(0, false, true);
        Assert.True(wb[1].IsEmpty);
        Assert.Equal(12, inv.CountItem(5));
    }

    [Fact]
    public void PlayerCraftOutput_WithoutRecipeFunction_ClearsWhenGridEmpty()
    {
        var inv = New();
        inv.SetSlot(0, 1, S(17, 1));
        inv.SetSlot(0, 0, S(5, 4));
        inv.ApplyClick(0, false, false);
        Assert.Equal(S(5, 4), inv.Cursor);
        Assert.True(inv.GetPlayerSlot(1).IsEmpty);
        Assert.True(inv.GetPlayerSlot(0).IsEmpty);
    }

    [Fact]
    public void OutsideClick_DropsCursor()
    {
        var inv = New();
        inv.SetSlot(-1, -1, S(4, 5));
        inv.ApplyClick(InventoryManager.OutsideSlot, true, false);
        Assert.Equal(S(4, 4), inv.Cursor);
        inv.ApplyClick(InventoryManager.OutsideSlot, false, false);
        Assert.True(inv.Cursor.IsEmpty);
    }

    [Fact]
    public void Queries()
    {
        var inv = New();
        inv.SetSlot(0, 9, S(4, 10));
        inv.SetSlot(0, 38, S(4, 5));
        inv.SetSlot(0, 36, S(276, 1));
        Assert.Equal(15, inv.CountItem(4));
        Assert.Equal([38, 9], inv.FindSlots(4));
        Assert.Equal(37, inv.FirstEmptySlot());
        Assert.Equal(2, inv.FindHotbarSlot(4));
        Assert.Equal(0, inv.BestHotbarSlot(s => s.Id == 276 ? 10 : s.Id == 4 ? 1 : 0));
        Assert.Equal(S(276, 1), inv.HeldItem(0));
        Assert.Equal(1, inv.FreeHotbarSlot());
        Assert.Equal(33 * 64 + 54 + 59, inv.RoomFor(S(4, 1)));
    }
}
