using MineClaw.Data;
using MineClaw.Protocol;
using MineClaw.Protocol.Packets;

namespace MineClaw.Sim;

public partial class FakeServer
{
    /// <summary>Opt-in vanilla crafting/click simulation; legacy scripted fixtures keep their existing ACK-only behaviour.</summary>
    public bool CraftingEnabled { get; set; }
    private sbyte _craftWindow;
    private ItemStack[]? _craftSlots;
    private ItemStack _craftCursor = ItemStack.Empty;

    private bool HandleCrafting(IPacket packet)
    {
        switch (packet)
        {
            case PlayerBlockPlacementPacket use when use.Direction != 255 && Blocks.GetValueOrDefault((use.X, use.Y, use.Z)) == 58:
                OpenCraftingTable();
                return true;
            case CloseWindowPacket close when close.WindowId == _craftWindow && _craftSlots is not null:
                CloseCraftingTable();
                return true;
            case WindowClickPacket click:
                HandleCraftClick(click);
                return true;
            default:
                return false;
        }
    }

    private void OpenCraftingTable()
    {
        _craftWindow = (sbyte)++_windowId;
        _craftSlots = Enumerable.Repeat(ItemStack.Empty, 46).ToArray();
        Array.Copy(Inventory, 9, _craftSlots, 10, 36);
        Send(new OpenWindowPacket { WindowId = _craftWindow, InventoryType = 1, Title = "Crafting", SlotCount = 9 });
        Send(new WindowItemsPacket { WindowId = _craftWindow, Items = (ItemStack[])_craftSlots.Clone() });
    }

    private void CloseCraftingTable()
    {
        Array.Copy(_craftSlots!, 10, Inventory, 9, 36);
        _craftSlots = null; _craftWindow = 0;
        Send(new WindowItemsPacket { WindowId = 0, Items = (ItemStack[])Inventory.Clone() });
    }

    /// <summary>The grid a click works on: the crafting table's window (3x3, the inventory from slot 10) or the player's own (2x2,
    /// from slot 9). Slot 0 is the output.</summary>
    private readonly record struct CraftGrid(ItemStack[] Slots, int Size, int Start)
    {
        public Recipe? Match() => Recipes.Match(Slots.Skip(1).Take(Size * Size).ToArray(), Size);
    }

    private CraftGrid CurrentCraftGrid() =>
        new(_craftSlots ?? Inventory, _craftSlots is null ? 2 : 3, _craftSlots is null ? 9 : 10);

    /// <summary>A click is valid in the open window, not on the reject list and with the item the server sees in the slot; a valid
    /// one is applied, an invalid one answered with the window's contents and the cursor (the client resyncs).</summary>
    private void HandleCraftClick(WindowClickPacket click)
    {
        var g = CurrentCraftGrid();
        var slots = g.Slots;
        var recipe = g.Match();
        slots[0] = recipe?.Output ?? ItemStack.Empty;
        bool inRange = click.Slot >= 0 && click.Slot < slots.Length;
        ItemStack expected = inRange ? slots[click.Slot] : ItemStack.Empty;
        bool valid = click.WindowId == _craftWindow && !RejectActions.Contains(click.ActionNumber) && expected == click.ClickedItem;
        if (valid && inRange) ApplyCraftClick(click, g, recipe);
        Send(new TransactionPacket { WindowId = click.WindowId, ActionNumber = click.ActionNumber, Accepted = valid });
        if (valid) return;
        Send(new WindowItemsPacket { WindowId = _craftWindow, Items = (ItemStack[])slots.Clone() });
        Send(new SetSlotPacket { WindowId = -1, Slot = -1, Item = _craftCursor });
    }

    private void ApplyCraftClick(WindowClickPacket click, CraftGrid g, Recipe? recipe)
    {
        var slots = g.Slots;
        int at = click.Slot;
        if (click.Shift) ShiftCraftClick(g, at, recipe);
        else if (_craftCursor.IsEmpty)
        {
            _craftCursor = slots[at]; slots[at] = ItemStack.Empty;
        }
        else if (slots[at].IsEmpty || (slots[at].Id == _craftCursor.Id && slots[at].Damage == _craftCursor.Damage)) PutCraftCursorDown(click, slots, at);
        else { (_craftCursor, slots[at]) = (slots[at], _craftCursor); }
        slots[0] = g.Match()?.Output ?? ItemStack.Empty;
        if (_craftSlots is not null) Array.Copy(slots, 10, Inventory, 9, 36);
    }

    /// <summary>Shift on the output crafts all it can into the inventory; shift on a grid cell moves it to the inventory.</summary>
    private static void ShiftCraftClick(CraftGrid g, int at, Recipe? recipe)
    {
        if (at == 0 && recipe is not null) ShiftCraftAll(g, recipe);
        else if (at > 0 && at < g.Start && CraftInsert(g.Slots, g.Start, g.Slots[at])) g.Slots[at] = ItemStack.Empty;
    }

    /// <summary>Tests lay one item per cell; implement vanilla's repeated shift-craft too.</summary>
    private static void ShiftCraftAll(CraftGrid g, Recipe? recipe)
    {
        var slots = g.Slots;
        while (recipe is not null)
        {
            var trial = (ItemStack[])slots.Clone();
            if (!CraftInsert(trial, g.Start, recipe.Output)) break;
            Array.Copy(trial, slots, slots.Length);
            for (int i = 1; i <= g.Size * g.Size; i++) if (!slots[i].IsEmpty) slots[i] = CraftAmount(slots[i], slots[i].Count - 1);
            recipe = g.Match();
        }
    }

    /// <summary>The cursor stack (or one of it, right click) onto an empty or matching slot, up to the stack limit.</summary>
    private void PutCraftCursorDown(WindowClickPacket click, ItemStack[] slots, int at)
    {
        int n = click.RightClick ? 1 : _craftCursor.Count;
        int there = slots[at].IsEmpty ? 0 : slots[at].Count;
        int room = CraftLimit(_craftCursor.Id) - there;
        n = Math.Min(n, room);
        slots[at] = CraftAmount(_craftCursor, there + n);
        _craftCursor = CraftAmount(_craftCursor, _craftCursor.Count - n);
    }
    private static int CraftLimit(int id) => id is 270 or 271 or 274 or 275 ? 1 : 64;
    private static ItemStack CraftAmount(ItemStack s, int count) => count <= 0 ? ItemStack.Empty : new(s.Id, (byte)count, s.Damage);
    private static bool CraftInsert(ItemStack[] slots, int start, ItemStack item)
    {
        int n = item.IsEmpty ? 0 : item.Count;
        for (int pass = 0; pass < 2 && n > 0; pass++)
            for (int i = start + 35; i >= start && n > 0; i--)
            {
                var d = slots[i]; int room = 0;
                if (pass == 0 && !d.IsEmpty && d.Id == item.Id && d.Damage == item.Damage) room = CraftLimit(item.Id) - d.Count;
                else if (pass == 1 && d.IsEmpty) room = CraftLimit(item.Id);
                if (room <= 0) continue;
                int k = Math.Min(room, n); slots[i] = CraftAmount(item, (d.IsEmpty ? 0 : d.Count) + k); n -= k;
            }
        return n == 0;
    }
}
