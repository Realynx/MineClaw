using MineClaw.Data;

namespace MineClaw.World;

/// <summary>
/// 1.12.2 click prediction (<see cref="InventoryModel.Release1122"/>), ported from the decompiled <c>Container.slotClick</c>,
/// <c>Container.mergeItemStack</c>, <c>canAddItemToSlot</c>, <c>computeStackSize</c> and the containers'
/// <c>transferStackInSlot</c> (<c>ContainerPlayer</c>, <c>ContainerWorkbench</c>, <c>ContainerFurnace</c>, <c>ContainerChest</c>,
/// <c>ContainerDispenser</c>). The return value is the click's expected item, which the 1.12.2 server compares with
/// <c>ItemStack.areItemStacksEqual</c> (NBT included) to accept or reject the click. Differences from b1.8.1 that matter:
/// <list type="bullet">
/// <item>A shift-click returns the moved stack only while the slot still holds the same item afterwards (a full move returns
/// empty).</item>
/// <item>Shift-click routing: armor to its empty armor slot, a shield to the empty off hand, smeltables into the furnace's
/// input and fuel into its fuel slot.</item>
/// <item>Swap (number keys), throw, pickup-all (double click) and the three-stage drag; clone is creative-only (a no-op).</item>
/// <item>Stacks merge only with equal NBT.</item>
/// </list>
/// Survival only: no creative clone and no creative drag mode 2.
/// </summary>
public sealed partial class InventoryManager
{
    private int _dragEvent, _dragMode;
    private readonly List<int> _dragSlots = new();


    private ItemStack SlotClick1122(Window w, int slotId, ClickMode mode, int button)
    {
        if (mode == ClickMode.QuickCraft)
        {
            QuickCraft1122(w, slotId, button);
            return ItemStack.Empty;
        }
        if (_dragEvent != 0)
        {
            ResetDrag();
            return ItemStack.Empty;
        }
        if (IsMouseClick(mode, button)) return PickupClick1122(w, slotId, mode, button);
        if (IsHotbarSwap(mode, button)) SwapClick1122(w, slotId, button);
        else if (mode == ClickMode.Throw) ThrowClick1122(w, slotId, button);
        else if (mode == ClickMode.PickupAll && slotId >= 0) PickupAll1122(w, slotId, button);
        return ItemStack.Empty; // clone is creative only
    }

    private static bool IsMouseClick(ClickMode mode, int button) => mode is ClickMode.Pickup or ClickMode.QuickMove && button is 0 or 1;

    private static bool IsHotbarSwap(ClickMode mode, int button) => mode == ClickMode.Swap && button is >= 0 and < 9;

    // ---- the three-stage drag (QuickCraft) ----

    private void QuickCraft1122(Window w, int slotId, int button)
    {
        int previous = _dragEvent;
        _dragEvent = button & 3; // getDragEvent
        if ((previous != 1 || _dragEvent != 2) && previous != _dragEvent) ResetDrag();
        else if (_cursor.IsEmpty) ResetDrag();
        else if (_dragEvent == 0) StartDrag(button);
        else if (_dragEvent == 1) { if (CanDragTo(w, slotId)) _dragSlots.Add(slotId); }
        else if (_dragEvent == 2) EndDrag(w);
        else ResetDrag();
    }

    private void StartDrag(int button)
    {
        _dragMode = button >> 2 & 3; // extractDragMode
        if (_dragMode is 0 or 1) { _dragEvent = 1; _dragSlots.Clear(); } // isValidDragMode (2 = creative)
        else ResetDrag();
    }

    private bool CanDragTo(Window w, int slotId) =>
        slotId >= 0 && slotId < w.SlotCount && CanAddItemToSlot(w, slotId, _cursor, true) && IsValid1122(w, slotId, _cursor)
        && (_dragMode == 2 || _cursor.Count > _dragSlots.Count) && CanDragIntoSlot(w, slotId) && !_dragSlots.Contains(slotId);

    private void EndDrag(Window w)
    {
        if (_dragSlots.Count > 0) SpreadDrag(w);
        ResetDrag();
    }

    /// <summary>Spreads the cursor over the dragged slots (computeStackSize: 0 split evenly, 1 one each, 2 a full stack; plus
    /// what the slot holds); the cursor keeps the rest.</summary>
    private void SpreadDrag(Window w)
    {
        var copy = _cursor;
        int left = _cursor.Count;
        foreach (int s in _dragSlots)
            if (CanSpreadInto(w, s)) left -= FillDragSlot(w, s, copy);
        _cursor = WithCount(copy, left);
    }

    private bool CanSpreadInto(Window w, int s) =>
        CanAddItemToSlot(w, s, _cursor, true) && IsValid1122(w, s, _cursor) && (_dragMode == 2 || _cursor.Count >= _dragSlots.Count)
        && CanDragIntoSlot(w, s);

    /// <summary>Fills one dragged slot; the number of items it took from the cursor.</summary>
    private int FillDragSlot(Window w, int s, ItemStack copy)
    {
        int there = w.Get(s).IsEmpty ? 0 : w.Get(s).Count;
        int n = DragShare(copy) + there;
        n = Math.Min(n, Math.Min(Rules.MaxStack(copy.Id), SlotLimit(w.KindOf(s))));
        w.Set(s, WithCount(copy, n));
        OnSlotChanged(w, w.KindOf(s));
        return n - there;
    }

    private int DragShare(ItemStack copy) => _dragMode switch { 0 => copy.Count / _dragSlots.Count, 1 => 1, _ => Rules.MaxStack(copy.Id) };

    // ---- left/right clicks and shift-clicks ----

    private ItemStack PickupClick1122(Window w, int slotId, ClickMode mode, int button)
    {
        if (slotId == OutsideSlot)
        {
            DropFromCursor(one: button != 0);
            return ItemStack.Empty;
        }
        if (slotId < 0 || slotId >= w.SlotCount) return ItemStack.Empty;
        return mode == ClickMode.QuickMove ? ShiftClick1122(w, slotId) : PickupSlot1122(w, slotId, button);
    }

    /// <summary>A click outside the window drops the cursor stack (or one of it).</summary>
    private void DropFromCursor(bool one)
    {
        if (!_cursor.IsEmpty) _cursor = one ? WithCount(_cursor, _cursor.Count - 1) : ItemStack.Empty;
    }

    /// <summary>for (t = transfer(); !t.isEmpty() &amp;&amp; areItemsEqual(slot.getStack(), t); t = transfer()) result = t.copy();</summary>
    private ItemStack ShiftClick1122(Window w, int slotId)
    {
        var result = ItemStack.Empty;
        for (int guard = 0; guard < 256; guard++)
        {
            var t = Transfer1122(w, slotId);
            if (t.IsEmpty || !ItemsEqual(w.Get(slotId), t)) break;
            result = t;
        }
        return result;
    }

    private ItemStack PickupSlot1122(Window w, int slotId, int button)
    {
        var kind = w.KindOf(slotId);
        var inSlot = w.Get(slotId);
        var cursor = _cursor;
        int limit = SlotLimit(kind);
        var result = inSlot.IsEmpty ? ItemStack.Empty : inSlot;
        if (inSlot.IsEmpty) PutCursorInto1122(w, slotId, cursor, button, limit);
        else if (cursor.IsEmpty) TakeFrom1122(w, slotId, kind, inSlot, button);
        else if (IsValid1122(w, slotId, cursor)) MergeOrSwap1122(w, slotId, inSlot, cursor, button, limit);
        else TakeOntoCursor1122(w, slotId, kind, inSlot, cursor);
        OnSlotChanged(w, kind); // slot.onSlotChanged
        return result;
    }

    private void PutCursorInto1122(Window w, int slotId, ItemStack cursor, int button, int limit)
    {
        if (cursor.IsEmpty || !IsValid1122(w, slotId, cursor)) return;
        int u = Math.Min(button == 0 ? cursor.Count : 1, limit);
        w.Set(slotId, WithCount(cursor, u));
        _cursor = WithCount(cursor, cursor.Count - u);
    }

    private void TakeFrom1122(Window w, int slotId, SlotKind kind, ItemStack inSlot, int button)
    {
        int t = button == 0 ? inSlot.Count : (inSlot.Count + 1) / 2;
        if (kind == SlotKind.CraftOutput) t = inSlot.Count; // InventoryCraftResult.decrStackSize: getAndRemove, the whole stack
        _cursor = WithCount(inSlot, t);
        w.Set(slotId, WithCount(inSlot, inSlot.Count - t));
        OnPickup(w, kind); // slot.onTake
    }

    private void MergeOrSwap1122(Window w, int slotId, ItemStack inSlot, ItemStack cursor, int button, int limit)
    {
        if (SameStackType(inSlot, cursor))
        {
            int s = button == 0 ? cursor.Count : 1;
            s = Math.Min(s, limit - inSlot.Count);
            s = Math.Min(s, Rules.MaxStack(cursor.Id) - inSlot.Count);
            if (s == 0) return;
            _cursor = WithCount(cursor, cursor.Count - s);
            w.Set(slotId, WithCount(inSlot, inSlot.Count + s));
        }
        else if (cursor.Count <= limit)
        {
            w.Set(slotId, cursor);
            _cursor = inSlot;
        }
    }

    /// <summary>A slot the cursor can't go into (an output): its whole stack joins an equal stackable cursor if it fits.</summary>
    private void TakeOntoCursor1122(Window w, int slotId, SlotKind kind, ItemStack inSlot, ItemStack cursor)
    {
        if (!SameStackType(inSlot, cursor) || Rules.MaxStack(cursor.Id) <= 1) return;
        int r = inSlot.Count;
        if (r + cursor.Count > Rules.MaxStack(cursor.Id)) return;
        _cursor = WithCount(cursor, cursor.Count + r);
        w.Set(slotId, ItemStack.Empty);
        OnPickup(w, kind);
    }

    /// <summary>Same item, damage and NBT (stacks that merge in 1.12.2).</summary>
    private static bool SameStackType(ItemStack a, ItemStack b) => a.Id == b.Id && a.Damage == b.Damage && Equals(a.Tag, b.Tag);

    // ---- number keys, throw, double click ----

    private void SwapClick1122(Window w, int slotId, int button)
    {
        if (slotId < 0 || slotId >= w.SlotCount) return;
        int hotIndex = 36 + button; // inventoryPlayer.getStackInSlot(button): the hotbar, window-0 numbering
        var hot = _player[hotIndex];
        var inSlot = w.Get(slotId);
        var kind = w.KindOf(slotId);
        if (hot.IsEmpty && inSlot.IsEmpty) return;
        if (hot.IsEmpty)
        {
            _player[hotIndex] = inSlot;
            w.Set(slotId, ItemStack.Empty);
            OnPickup(w, kind); // onSwapCraft + onTake
        }
        else if (IsValid1122(w, slotId, hot)) SwapInHotbar1122(w, slotId, kind, hotIndex, hot, inSlot);
        OnSlotChanged(w, kind);
    }

    /// <summary>The hotbar stack goes into the slot (at most the slot's limit; the rest stays in the hotbar) and the slot's stack
    /// into the hotbar, or into the inventory when the hotbar keeps a rest.</summary>
    private void SwapInHotbar1122(Window w, int slotId, SlotKind kind, int hotIndex, ItemStack hot, ItemStack inSlot)
    {
        int limit = SlotLimit(kind);
        if (hot.Count > limit)
        {
            w.Set(slotId, WithCount(hot, limit));
            _player[hotIndex] = WithCount(hot, hot.Count - limit);
        }
        else
        {
            w.Set(slotId, hot);
            _player[hotIndex] = inSlot.IsEmpty ? ItemStack.Empty : inSlot;
        }
        if (inSlot.IsEmpty) return;
        OnPickup(w, kind);
        if (hot.Count > limit) AddToInventory(inSlot); // addItemStackToInventory, else dropped
    }

    private void ThrowClick1122(Window w, int slotId, int button)
    {
        if (!_cursor.IsEmpty || slotId < 0 || slotId >= w.SlotCount) return;
        var inSlot = w.Get(slotId);
        if (inSlot.IsEmpty) return;
        int n = button == 0 ? 1 : inSlot.Count;
        w.Set(slotId, WithCount(inSlot, inSlot.Count - n)); // decrStackSize, then dropped
        OnPickup(w, w.KindOf(slotId));
        OnSlotChanged(w, w.KindOf(slotId));
    }

    /// <summary>Double click: gathers equal stacks into the cursor, non-full stacks first, from the start (button 0) or the end.</summary>
    private void PickupAll1122(Window w, int slotId, int button)
    {
        var c = _cursor;
        bool slotFree = slotId >= w.SlotCount || w.Get(slotId).IsEmpty;
        if (c.IsEmpty || !slotFree) return;
        int size = w.SlotCount;
        int start = button == 0 ? 0 : size - 1, step = button == 0 ? 1 : -1;
        int max = Rules.MaxStack(c.Id);
        for (int k = 0; k < 2; k++)
            for (int l = start; l >= 0 && l < size && c.Count < max; l += step)
                c = GatherFrom1122(w, l, c, max, fullStacksToo: k > 0);
        _cursor = c;
    }

    private ItemStack GatherFrom1122(Window w, int l, ItemStack c, int max, bool fullStacksToo)
    {
        var s = w.Get(l);
        if (s.IsEmpty || !CanAddItemToSlot(w, l, c, true) || !CanMergeSlot(w, l)) return c;
        if (!fullStacksToo && s.Count == Rules.MaxStack(s.Id)) return c;
        int m = Math.Min(max - c.Count, s.Count);
        w.Set(l, WithCount(s, s.Count - m));
        OnPickup(w, w.KindOf(l));
        return WithCount(c, c.Count + m);
    }

    private void ResetDrag()
    {
        _dragEvent = 0;
        _dragSlots.Clear();
    }

    /// <summary>Container.canAddItemToSlot: an empty slot, or the same item (id, damage, tag) with room.</summary>
    private bool CanAddItemToSlot(Window w, int slot, ItemStack stack, bool stackSizeMatters)
    {
        var s = w.Get(slot);
        if (s.IsEmpty) return true;
        if (s.Id == stack.Id && s.Damage == stack.Damage && Equals(s.Tag, stack.Tag))
            return s.Count + (stackSizeMatters ? 0 : stack.Count) <= Rules.MaxStack(stack.Id);
        return false;
    }

    /// <summary>ItemStack.areItemsEqual: same item and damage (no NBT).</summary>
    private static bool ItemsEqual(ItemStack a, ItemStack b) => !a.IsEmpty && !b.IsEmpty && a.Id == b.Id && a.Damage == b.Damage;

    /// <summary>ContainerWorkbench/ContainerPlayer.canMergeSlot and canDragIntoSlot: never the crafting output.</summary>
    private static bool CanMergeSlot(Window w, int slot) => w.KindOf(slot) != SlotKind.CraftOutput;
    private static bool CanDragIntoSlot(Window w, int slot) => w.KindOf(slot) != SlotKind.CraftOutput;

    /// <summary>Slot.isItemValid in 1.12.2: outputs take nothing; armor slots their piece (pumpkins and skulls on the head,
    /// the elytra on the chest); the furnace fuel slot fuel or a bucket; everything else anything.</summary>
    private bool IsValid1122(Window w, int slot, ItemStack item) => w.KindOf(slot) switch
    {
        SlotKind.CraftOutput or SlotKind.FurnaceOutput => false,
        SlotKind.Armor => EquipmentSlot(item.Id) == slot - 5,
        SlotKind.FurnaceFuel => Rules.IsFuel(item.Id) || item.Id == Items.Bucket,
        _ => true,
    };

    /// <summary>EntityLiving.getSlotForItemStack as an armor index (0 head … 3 feet, the window's slot 5 + it), 4 = off hand
    /// (a shield), −1 = the main hand.</summary>
    public static int EquipmentSlot(int itemId) => itemId switch
    {
        86 or 397 => 0,  // pumpkin, skull
        443 => 1,        // elytra
        442 => 4,        // shield
        _ => ItemRules.ArmorType(itemId),
    };

    /// <summary>The containers' transferStackInSlot (see the class remarks); the original stack when anything moved.</summary>
    private ItemStack Transfer1122(Window w, int index)
    {
        var original = w.Get(index);
        if (original.IsEmpty) return ItemStack.Empty;
        var kind = w.KindOf(index);
        bool ok = TransferByWindow1122(w, index, original, out var rem);
        if (!ok) return ItemStack.Empty;
        w.Set(index, rem);
        // A chest returns the moved stack even when nothing of it moved (ContainerChest has no count check).
        if (w.Type != WindowType.Chest && rem.Count == original.Count && !rem.IsEmpty) return ItemStack.Empty;
        OnPickup(w, kind); // slot.onTake: crafting output consumes the grid (a leftover that didn't fit is dropped)
        OnSlotChanged(w, kind);
        return original;
    }

    private bool TransferByWindow1122(Window w, int index, ItemStack original, out ItemStack rem)
    {
        switch (w.Type)
        {
            case WindowType.Player: return TransferFromPlayer1122(w, index, original, out rem);
            case WindowType.Workbench: return TransferFromWorkbench1122(w, index, original, out rem);
            case WindowType.Furnace: return TransferFromFurnace1122(w, index, original, out rem);
            default:
                // Chest (and the chest-like hopper, shulker box…), dispenser: the container to the inventory (from the end), and back.
                int n = w.ContainerSlots;
                return index < n ? Merge1122(w, original, n, w.SlotCount, true, out rem) : Merge1122(w, original, 0, n, false, out rem);
        }
    }

    /// <summary>ContainerPlayer: the crafting output and grid to the inventory, armor and a shield to their empty slots, the
    /// main inventory to the hotbar and back.</summary>
    private bool TransferFromPlayer1122(Window w, int index, ItemStack original, out ItemStack rem)
    {
        if (index < 9) return Merge1122(w, original, 9, 45, index == 0, out rem); // output (from the end) and grid
        if (EquipTarget1122(w, original) is { } slot) return Merge1122(w, original, slot, slot + 1, false, out rem);
        var (start, end) = index is >= 9 and < 36 ? (36, 45) : index is >= 36 and < 45 ? (9, 36) : (9, 45);
        return Merge1122(w, original, start, end, false, out rem);
    }

    /// <summary>The empty armor slot of the stack's piece, or the empty off hand for a shield; null otherwise.</summary>
    private int? EquipTarget1122(Window w, ItemStack original)
    {
        int eq = EquipmentSlot(original.Id);
        if (eq is >= 0 and <= 3 && w.Get(5 + eq).IsEmpty) return 5 + eq;
        if (eq == 4 && Model.HasOffhand && w.Get(InventoryModel.OffhandSlot).IsEmpty) return InventoryModel.OffhandSlot;
        return null;
    }

    private bool TransferFromWorkbench1122(Window w, int index, ItemStack original, out ItemStack rem)
    {
        int main = w.MainStart, hot = w.HotbarStart, end = w.SlotCount;
        if (index == 0) return Merge1122(w, original, main, end, true, out rem);
        if (index >= main && index < hot) return Merge1122(w, original, hot, end, false, out rem);
        if (index >= hot && index < end) return Merge1122(w, original, main, hot, false, out rem);
        return Merge1122(w, original, main, end, false, out rem);
    }

    /// <summary>ContainerFurnace: the output to the inventory (from the end), the input and fuel back to it, smeltables into the
    /// input, fuel into the fuel slot, anything else between the main inventory and the hotbar.</summary>
    private bool TransferFromFurnace1122(Window w, int index, ItemStack original, out ItemStack rem)
    {
        int main = w.MainStart, hot = w.HotbarStart, end = w.SlotCount;
        if (index == 2) return Merge1122(w, original, main, end, true, out rem);
        if (index is 0 or 1) return Merge1122(w, original, main, end, false, out rem);
        if (Rules.IsSmeltable(original)) return Merge1122(w, original, 0, 1, false, out rem);
        if (Rules.IsFuel(original.Id)) return Merge1122(w, original, 1, 2, false, out rem);
        if (index >= main && index < hot) return Merge1122(w, original, hot, end, false, out rem);
        if (index >= hot && index < end) return Merge1122(w, original, main, hot, false, out rem);
        rem = original;
        return true;
    }

    /// <summary>Container.mergeItemStack (1.12.2): top up equal stacks (id, damage, NBT), then the first empty valid slot.</summary>
    private bool Merge1122(Window w, ItemStack stack, int start, int end, bool reverse, out ItemStack remaining)
    {
        int count = stack.Count;
        bool moved = Rules.IsStackable(stack) && TopUp1122(w, stack, start, end, reverse, ref count);
        if (count > 0) moved |= FillEmpty1122(w, stack, start, end, reverse, ref count);
        remaining = WithCount(stack, count);
        return moved;
    }

    /// <summary>Adds to the equal stacks in [start, end) (from the end when <paramref name="reverse"/>) until the count is used up.</summary>
    private bool TopUp1122(Window w, ItemStack stack, int start, int end, bool reverse, ref int count)
    {
        bool moved = false;
        int max = Rules.MaxStack(stack.Id);
        int step = reverse ? -1 : 1;
        for (int i = reverse ? end - 1 : start; count > 0 && i >= start && i < end; i += step)
        {
            var s = w.Get(i);
            if (s.IsEmpty || !SameStackType(s, stack)) continue;
            int j = s.Count + count;
            if (j <= max) { count = 0; w.Set(i, WithCount(s, j)); moved = true; }
            else if (s.Count < max) { count -= max - s.Count; w.Set(i, WithCount(s, max)); moved = true; }
        }
        return moved;
    }

    /// <summary>Puts what is left (up to the slot's limit) into the first empty slot of [start, end) that takes the item.</summary>
    private bool FillEmpty1122(Window w, ItemStack stack, int start, int end, bool reverse, ref int count)
    {
        int step = reverse ? -1 : 1;
        for (int i = reverse ? end - 1 : start; i >= start && i < end; i += step)
        {
            if (!w.Get(i).IsEmpty || !IsValid1122(w, i, stack)) continue;
            int put = Math.Min(count, SlotLimit(w.KindOf(i)));
            w.Set(i, WithCount(stack, put));
            count -= put;
            return true;
        }
        return false;
    }

    /// <summary>Window-0 slots in addItemStackToInventory's order: the hotbar, then the main inventory.</summary>
    private static readonly int[] HotbarThenMain = [36, 37, 38, 39, 40, 41, 42, 43, 44, .. Enumerable.Range(9, 27)];

    /// <summary>InventoryPlayer.addItemStackToInventory (simplified to the main inventory + hotbar): what doesn't fit is dropped.</summary>
    private void AddToInventory(ItemStack stack)
    {
        int count = TopUpPlayer(stack, stack.Count);
        if (count <= 0) return;
        foreach (int i in HotbarThenMain)
        {
            if (!_player[i].IsEmpty) continue;
            _player[i] = WithCount(stack, count);
            return;
        }
    }

    /// <summary>Adds to the equal, non-full stacks of the hotbar and main inventory; what is left.</summary>
    private int TopUpPlayer(ItemStack stack, int count)
    {
        int max = Rules.MaxStack(stack.Id);
        foreach (int i in HotbarThenMain)
        {
            if (count <= 0) break;
            var s = _player[i];
            if (s.IsEmpty || !SameStackType(s, stack) || s.Count >= max) continue;
            int n = Math.Min(count, max - s.Count);
            _player[i] = WithCount(s, s.Count + n);
            count -= n;
        }
        return count;
    }
}
