using MineClaw.Data;

namespace MineClaw.World;

/// <summary>Outcome of a predicted window click.</summary>
/// <param name="ExpectedItem">The item stack to send in 0x66 Window Click (what Container.slotClick returns).</param>
/// <param name="ActionNumber">Transaction id to put in 0x66 (match against 0x6A).</param>
public readonly record struct ClickResult(int WindowId, int Slot, bool RightClick, bool Shift, ItemStack ExpectedItem, short ActionNumber)
{
    /// <summary>Window slots whose predicted content this click changed (a server 0x67 for one of them that arrives before
    /// the click's 0x6A was generated before the server applied the click, so it overwrites the prediction with stale
    /// contents).</summary>
    public IReadOnlyList<int> ChangedSlots { get; init; } = [];
    /// <summary>The predicted cursor changed.</summary>
    public bool CursorChanged { get; init; }
}

/// <summary>
/// Player inventory (window 0), the currently open window and the cursor stack, plus b1.8.1 click prediction.
/// Thread-safe via a single lock: the packet thread applies 0x64/0x65/0x67/0x68/0x69, action threads click and query.
/// </summary>
public sealed class InventoryManager
{
    public const int CursorWindow = -1, CursorSlot = -1, OutsideSlot = -999;

    private readonly object _sync = new();
    private readonly ItemStack[] _player = new ItemStack[45];
    private readonly Window _playerWindow;
    private Window? _open;
    private ItemStack _cursor = ItemStack.Empty;
    private short _actionCounter;

    public InventoryManager(ItemRules? rules = null)
    {
        Array.Fill(_player, ItemStack.Empty);
        _playerWindow = new Window(0, WindowType.Player, "Inventory", 9, _player);
        Rules = rules ?? ItemRules.Default;
    }

    public ItemRules Rules { get; set; }

    /// <summary>Optional recipe evaluator: given a crafting grid (row-major, width 2 or 3) returns the output or Empty.
    /// When set, predicted crafting output is recomputed after grid changes; otherwise the server's 0x67 fills it in.</summary>
    public Func<ItemStack[], int, ItemStack>? CraftResult { get; set; }

    public event Action<Window>? WindowOpened;
    public event Action<Window>? WindowClosed;
    /// <summary>Fired after any slot/cursor change from the server or a predicted click.</summary>
    public event Action? Changed;
    /// <summary>A container window's full contents arrived (0x68 for a window other than the player's): its real state.</summary>
    public event Action<int>? WindowItemsSet;

    /// <summary>Lock to hold while doing several reads/clicks that must be consistent.</summary>
    public object SyncRoot => _sync;

    public Window PlayerWindow => _playerWindow;
    public Window? OpenWindow { get { lock (_sync) return _open; } }
    /// <summary>The window clicks go to: the open container, or the player inventory.</summary>
    public Window ActiveWindow { get { lock (_sync) return _open ?? _playerWindow; } }
    public ItemStack Cursor { get { lock (_sync) return _cursor; } }

    public short NextActionNumber() { lock (_sync) return ++_actionCounter; }

    // ---- server → client ----

    /// <summary>0x64 Open Window.</summary>
    public Window Open(int windowId, WindowType type, string title, int slotCount)
    {
        Window w;
        lock (_sync)
        {
            w = new Window(windowId, type, title, slotCount, _player);
            _open = w;
        }
        WindowOpened?.Invoke(w);
        return w;
    }

    /// <summary>0x65 Close Window (from server) or local close. Vanilla drops the cursor stack and crafting-grid items
    /// on close; here only the local model is cleared — the server sends the resulting slot updates.</summary>
    public void Close(int windowId)
    {
        Window? closed = null;
        lock (_sync)
        {
            if (_open is not null && (windowId == _open.Id || windowId < 0))
            {
                closed = _open;
                _open = null;
            }
            _cursor = ItemStack.Empty;
        }
        if (closed is not null) WindowClosed?.Invoke(closed);
    }

    /// <summary>0x67 Set Slot. windowId -1 / slot -1 sets the cursor.</summary>
    public void SetSlot(int windowId, int slot, ItemStack item)
    {
        lock (_sync)
        {
            if (windowId == CursorWindow && slot == CursorSlot) _cursor = Norm(item);
            else if (windowId == 0) _playerWindow.Set(slot, item);
            else if (_open is not null && _open.Id == windowId) _open.Set(slot, item);
            else return;
        }
        Changed?.Invoke();
    }

    /// <summary>0x68 Window Items.</summary>
    public void SetWindowItems(int windowId, IReadOnlyList<ItemStack> items)
    {
        lock (_sync)
        {
            Window? w = windowId == 0 ? _playerWindow : _open is not null && _open.Id == windowId ? _open : null;
            if (w is null) return;
            for (int i = 0; i < items.Count && i < w.SlotCount; i++) w.Set(i, items[i]);
        }
        Changed?.Invoke();
        if (windowId != 0) WindowItemsSet?.Invoke(windowId);
    }

    /// <summary>0x69 Update Window Property (furnace progress bars).</summary>
    public void SetProperty(int windowId, int property, short value)
    {
        lock (_sync)
        {
            if (_open is not null && _open.Id == windowId) _open.SetProperty(property, value);
        }
    }

    public Window? GetWindow(int windowId)
    {
        lock (_sync) return windowId == 0 ? _playerWindow : _open is not null && _open.Id == windowId ? _open : null;
    }

    // ---- click prediction ----

    /// <summary>
    /// Simulates a click on the active window exactly like b1.8.1 Container.slotClick and applies it locally.
    /// <paramref name="slot"/> -999 = click outside (drop cursor; right = drop one).
    /// Send the returned <see cref="ClickResult"/> as 0x66; if the server answers 0x6A accepted=false, reply with
    /// 0x6A accepted=true and wait for its resync (0x68/0x67).
    /// </summary>
    public ClickResult ApplyClick(int slot, bool rightClick, bool shift)
    {
        ClickResult r;
        lock (_sync)
        {
            var w = _open ?? _playerWindow;
            var before = new ItemStack[w.SlotCount];
            for (int i = 0; i < before.Length; i++) before[i] = w.Get(i);
            var cursorBefore = _cursor;
            var expected = SlotClick(w, slot, rightClick, shift);
            var changed = new List<int>();
            for (int i = 0; i < before.Length; i++) if (!before[i].Equals(w.Get(i))) changed.Add(i);
            r = new ClickResult(w.Id, slot, rightClick, shift, expected, ++_actionCounter)
            {
                ChangedSlots = changed,
                CursorChanged = !cursorBefore.Equals(_cursor),
            };
        }
        Changed?.Invoke();
        return r;
    }

    private ItemStack SlotClick(Window w, int slot, bool right, bool shift)
    {
        if (slot == OutsideSlot)
        {
            if (!_cursor.IsEmpty)
            {
                if (!right) _cursor = ItemStack.Empty;
                else _cursor = WithCount(_cursor, _cursor.Count - 1);
            }
            return ItemStack.Empty;
        }
        if (slot < 0 || slot >= w.SlotCount) return ItemStack.Empty;

        if (shift)
        {
            var moved = TransferStackInSlot(w, slot);
            if (moved.IsEmpty) return ItemStack.Empty;
            // retrySlotClick: keep shift-clicking while the slot still holds the same item.
            for (int guard = 0; guard < 64; guard++)
            {
                var now = w.Get(slot);
                if (now.IsEmpty || now.Id != moved.Id) break;
                if (TransferStackInSlot(w, slot).IsEmpty) break;
            }
            return moved;
        }

        var kind = w.KindOf(slot);
        var slotStack = w.Get(slot);
        var cursor = _cursor;
        var result = slotStack.IsEmpty ? ItemStack.Empty : slotStack;
        int limit = SlotLimit(kind);

        if (slotStack.IsEmpty)
        {
            if (!cursor.IsEmpty && IsItemValid(slot, kind, cursor))
            {
                int n = right ? 1 : cursor.Count;
                if (n > limit) n = limit;
                w.Set(slot, WithCount(cursor, n));
                _cursor = WithCount(cursor, cursor.Count - n);
                OnSlotChanged(w, kind);
            }
        }
        else if (cursor.IsEmpty)
        {
            int n = right ? (slotStack.Count + 1) / 2 : slotStack.Count;
            if (kind == SlotKind.CraftOutput) n = slotStack.Count; // InventoryCraftResult.decrStackSize takes all
            _cursor = WithCount(slotStack, n);
            w.Set(slot, WithCount(slotStack, slotStack.Count - n));
            OnPickup(w, kind);
            OnSlotChanged(w, kind);
        }
        else if (IsItemValid(slot, kind, cursor))
        {
            if (!Rules.SameItem(slotStack, cursor))
            {
                if (cursor.Count <= limit)
                {
                    w.Set(slot, cursor);
                    _cursor = slotStack;
                    OnSlotChanged(w, kind);
                }
            }
            else
            {
                int n = right ? 1 : cursor.Count;
                n = Math.Min(n, limit - slotStack.Count);
                n = Math.Min(n, Rules.MaxStack(cursor.Id) - slotStack.Count);
                if (n > 0)
                {
                    _cursor = WithCount(cursor, cursor.Count - n);
                    w.Set(slot, WithCount(slotStack, slotStack.Count + n));
                    OnSlotChanged(w, kind);
                }
            }
        }
        else if (Rules.SameItem(slotStack, cursor) && Rules.MaxStack(cursor.Id) > 1)
        {
            // Output slots: add the whole output to the cursor if it fits.
            int n = slotStack.Count;
            if (n > 0 && n + cursor.Count <= Rules.MaxStack(cursor.Id))
            {
                _cursor = WithCount(cursor, cursor.Count + n);
                w.Set(slot, ItemStack.Empty);
                OnPickup(w, kind);
                OnSlotChanged(w, kind);
            }
        }
        return result;
    }

    /// <summary>Container.transferStackInSlot per window type (b1.8.1: no smart routing into furnace/armor slots).
    /// Returns the original stack if anything moved, else Empty.</summary>
    private ItemStack TransferStackInSlot(Window w, int slot)
    {
        var original = w.Get(slot);
        if (original.IsEmpty) return ItemStack.Empty;
        var kind = w.KindOf(slot);
        int main = w.MainStart, hot = w.HotbarStart, end = w.SlotCount;
        ItemStack remaining;
        bool ok;
        switch (w.Type)
        {
            case WindowType.Chest:
            case WindowType.Dispenser:
                ok = slot < w.ContainerSlots
                    ? Merge(w, original, main, end, true, out remaining)
                    : Merge(w, original, 0, w.ContainerSlots, false, out remaining);
                break;
            default:
            {
                // Player (output 0), Workbench (output 0), Furnace (output 2).
                bool isOutput = kind is SlotKind.CraftOutput or SlotKind.FurnaceOutput;
                if (isOutput) ok = Merge(w, original, main, end, true, out remaining);
                else if (kind == SlotKind.Main) ok = Merge(w, original, hot, end, false, out remaining);
                else if (kind == SlotKind.Hotbar) ok = Merge(w, original, main, hot, false, out remaining);
                else ok = Merge(w, original, main, end, false, out remaining);
                break;
            }
        }
        if (!ok) return ItemStack.Empty;
        w.Set(slot, remaining);
        if (remaining.Count != original.Count)
        {
            OnPickup(w, kind);
            OnSlotChanged(w, kind);
        }
        return original;
    }

    /// <summary>Container.mergeItemStack: first top up matching stacks, then the first empty slot.</summary>
    private bool Merge(Window w, ItemStack stack, int start, int end, bool reverse, out ItemStack remaining)
    {
        bool moved = false;
        int count = stack.Count;
        int max = Rules.MaxStack(stack.Id);
        if (Rules.IsStackable(stack))
        {
            for (int k = reverse ? end - 1 : start; count > 0 && (reverse ? k >= start : k < end); k += reverse ? -1 : 1)
            {
                var s = w.Get(k);
                if (s.IsEmpty || s.Id != stack.Id || (Rules.HasSubtypes(stack.Id) && s.Damage != stack.Damage)) continue;
                int total = s.Count + count;
                if (total <= max)
                {
                    count = 0;
                    w.Set(k, WithCount(s, total));
                    moved = true;
                }
                else if (s.Count < max)
                {
                    count -= max - s.Count;
                    w.Set(k, WithCount(s, max));
                    moved = true;
                }
            }
        }
        if (count > 0)
        {
            for (int k = reverse ? end - 1 : start; reverse ? k >= start : k < end; k += reverse ? -1 : 1)
            {
                if (!w.Get(k).IsEmpty) continue;
                w.Set(k, WithCount(stack, count));
                count = 0;
                moved = true;
                break;
            }
        }
        remaining = WithCount(stack, count);
        return moved;
    }

    private static int SlotLimit(SlotKind kind) => kind == SlotKind.Armor ? 1 : 64;

    /// <summary>Slot.isItemValid: output slots accept nothing; armor slot 5+k accepts only armor of type k.</summary>
    private static bool IsItemValid(int slot, SlotKind kind, ItemStack item) => kind switch
    {
        SlotKind.CraftOutput or SlotKind.FurnaceOutput => false,
        SlotKind.Armor => ItemRules.ArmorType(item.Id) == slot - 5,
        _ => true,
    };

    /// <summary>Taking crafting output consumes one of each grid ingredient (buckets leave an empty bucket).</summary>
    private void OnPickup(Window w, SlotKind kind)
    {
        if (kind != SlotKind.CraftOutput) return;
        int gridEnd = w.Type == WindowType.Player ? 5 : 10;
        for (int i = 1; i < gridEnd; i++)
        {
            var s = w.Get(i);
            if (s.IsEmpty) continue;
            var container = ItemRules.ContainerItem(s.Id);
            w.Set(i, !container.IsEmpty ? container : WithCount(s, s.Count - 1));
        }
        RecomputeCraft(w);
    }

    private void OnSlotChanged(Window w, SlotKind kind)
    {
        if (kind == SlotKind.CraftGrid) RecomputeCraft(w);
    }

    private void RecomputeCraft(Window w)
    {
        bool player = w.Type == WindowType.Player;
        if (!player && w.Type != WindowType.Workbench) return;
        if (CraftResult is null)
        {
            // Unknown recipe: if the grid is now empty the output must be empty; otherwise leave it to the server.
            int end = player ? 5 : 10;
            bool any = false;
            for (int i = 1; i < end; i++) any |= !w.Get(i).IsEmpty;
            if (!any) w.Set(0, ItemStack.Empty);
            return;
        }
        int width = player ? 2 : 3;
        var grid = new ItemStack[width * width];
        for (int i = 0; i < grid.Length; i++) grid[i] = w.Get(1 + i);
        w.Set(0, CraftResult(grid, width));
    }

    private static ItemStack WithCount(ItemStack s, int count) =>
        count <= 0 ? ItemStack.Empty : s with { Count = (byte)count };

    private static ItemStack Norm(ItemStack s) => s.IsEmpty ? ItemStack.Empty : s;

    // ---- queries (window-0 slot numbers) ----

    /// <summary>Copy of the 45 window-0 slots.</summary>
    public ItemStack[] PlayerSlots() { lock (_sync) return (ItemStack[])_player.Clone(); }

    public ItemStack GetPlayerSlot(int slot) { lock (_sync) return slot is >= 0 and < 45 ? _player[slot] : ItemStack.Empty; }

    /// <summary>Item in the selected hotbar slot (0..8).</summary>
    public ItemStack HeldItem(int hotbarSlot) => GetPlayerSlot(36 + Math.Clamp(hotbarSlot, 0, 8));

    /// <summary>Total count of an item in main inventory + hotbar (+ optionally armor/crafting).</summary>
    public int CountItem(int itemId, int? damage = null, bool includeAll = false)
    {
        lock (_sync)
        {
            int n = 0;
            for (int i = includeAll ? 0 : 9; i < 45; i++)
            {
                var s = _player[i];
                if (!s.IsEmpty && s.Id == itemId && (damage is null || s.Damage == damage)) n += s.Count;
            }
            return n;
        }
    }

    /// <summary>Window-0 slots (9–44, hotbar first) holding the item.</summary>
    public List<int> FindSlots(int itemId, int? damage = null)
    {
        lock (_sync)
        {
            var list = new List<int>();
            foreach (int i in InventoryOrder())
            {
                var s = _player[i];
                if (!s.IsEmpty && s.Id == itemId && (damage is null || s.Damage == damage)) list.Add(i);
            }
            return list;
        }
    }

    /// <summary>First empty main/hotbar slot in vanilla pickup order (hotbar 36–44, then main 9–35), or -1.</summary>
    public int FirstEmptySlot()
    {
        lock (_sync)
        {
            foreach (int i in InventoryOrder())
                if (_player[i].IsEmpty) return i;
            return -1;
        }
    }

    public int EmptySlotCount()
    {
        lock (_sync)
        {
            int n = 0;
            for (int i = 9; i < 45; i++) if (_player[i].IsEmpty) n++;
            return n;
        }
    }

    /// <summary>Hotbar index (0..8) holding the item, or -1.</summary>
    public int FindHotbarSlot(int itemId)
    {
        lock (_sync)
        {
            for (int i = 0; i < 9; i++)
                if (!_player[36 + i].IsEmpty && _player[36 + i].Id == itemId) return i;
            return -1;
        }
    }

    /// <summary>Hotbar index with the highest score (score ≤ 0 = unsuitable), or -1. Empty slots are scored too
    /// (pass a scorer that handles <see cref="ItemStack.Empty"/>, e.g. to prefer bare hands).</summary>
    public int BestHotbarSlot(Func<ItemStack, double> score)
    {
        lock (_sync)
        {
            int best = -1;
            double bestScore = 0;
            for (int i = 0; i < 9; i++)
            {
                double s = score(_player[36 + i]);
                if (s > bestScore) { bestScore = s; best = i; }
            }
            return best;
        }
    }

    /// <summary>Hotbar index to swap new items into: an empty hotbar slot, else <paramref name="fallback"/>.</summary>
    public int FreeHotbarSlot(int fallback = 8)
    {
        lock (_sync)
        {
            for (int i = 0; i < 9; i++) if (_player[36 + i].IsEmpty) return i;
            return fallback;
        }
    }

    /// <summary>How many more of the item fit in main inventory + hotbar.</summary>
    public int RoomFor(ItemStack item)
    {
        lock (_sync)
        {
            int max = Rules.MaxStack(item.Id), room = 0;
            for (int i = 9; i < 45; i++)
            {
                var s = _player[i];
                if (s.IsEmpty) room += max;
                else if (Rules.SameItem(s, item) && Rules.IsStackable(s)) room += Math.Max(0, max - s.Count);
            }
            return room;
        }
    }

    private static IEnumerable<int> InventoryOrder()
    {
        for (int i = 36; i < 45; i++) yield return i;
        for (int i = 9; i < 36; i++) yield return i;
    }
}
