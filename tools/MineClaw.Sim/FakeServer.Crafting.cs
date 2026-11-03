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
        if (packet is PlayerBlockPlacementPacket use && use.Direction != 255 &&
            Blocks.GetValueOrDefault((use.X, use.Y, use.Z)) == 58)
        {
            _craftWindow = (sbyte)++_windowId;
            _craftSlots = Enumerable.Repeat(ItemStack.Empty, 46).ToArray();
            Array.Copy(Inventory, 9, _craftSlots, 10, 36);
            Send(new OpenWindowPacket { WindowId = _craftWindow, InventoryType = 1, Title = "Crafting", SlotCount = 9 });
            Send(new WindowItemsPacket { WindowId = _craftWindow, Items = (ItemStack[])_craftSlots.Clone() });
            return true;
        }
        if (packet is CloseWindowPacket close && close.WindowId == _craftWindow && _craftSlots is not null)
        {
            Array.Copy(_craftSlots, 10, Inventory, 9, 36);
            _craftSlots = null; _craftWindow = 0;
            Send(new WindowItemsPacket { WindowId = 0, Items = (ItemStack[])Inventory.Clone() });
            return true;
        }
        if (packet is not WindowClickPacket click) return false;
        var slots = _craftSlots ?? Inventory;
        int size = _craftSlots is null ? 2 : 3;
        int start = _craftSlots is null ? 9 : 10;
        var recipe = Recipes.Match(slots.Skip(1).Take(size * size).ToArray(), size);
        slots[0] = recipe?.Output ?? ItemStack.Empty;
        bool valid = click.WindowId == _craftWindow && !RejectActions.Contains(click.ActionNumber);
        ItemStack expected = click.Slot >= 0 && click.Slot < slots.Length ? slots[click.Slot] : ItemStack.Empty;
        valid &= expected == click.ClickedItem;
        if (valid && click.Slot >= 0 && click.Slot < slots.Length)
        {
            int at = click.Slot;
            if (click.Shift)
            {
                if (at == 0 && recipe is not null)
                {
                    // Tests lay one item per cell; implement vanilla's repeated shift-craft too.
                    while (recipe is not null)
                    {
                        var trial = (ItemStack[])slots.Clone();
                        if (!CraftInsert(trial, start, recipe.Output)) break;
                        Array.Copy(trial, slots, slots.Length);
                        for (int i = 1; i <= size * size; i++) if (!slots[i].IsEmpty) slots[i] = CraftAmount(slots[i], slots[i].Count - 1);
                        recipe = Recipes.Match(slots.Skip(1).Take(size * size).ToArray(), size);
                    }
                }
                else if (at > 0 && at < start && CraftInsert(slots, start, slots[at])) slots[at] = ItemStack.Empty;
            }
            else if (_craftCursor.IsEmpty)
            {
                _craftCursor = slots[at]; slots[at] = ItemStack.Empty;
            }
            else if (slots[at].IsEmpty || (slots[at].Id == _craftCursor.Id && slots[at].Damage == _craftCursor.Damage))
            {
                int n = click.RightClick ? 1 : _craftCursor.Count;
                int room = CraftLimit(_craftCursor.Id) - (slots[at].IsEmpty ? 0 : slots[at].Count);
                n = Math.Min(n, room);
                slots[at] = CraftAmount(_craftCursor, (slots[at].IsEmpty ? 0 : slots[at].Count) + n);
                _craftCursor = CraftAmount(_craftCursor, _craftCursor.Count - n);
            }
            else { (_craftCursor, slots[at]) = (slots[at], _craftCursor); }
            slots[0] = Recipes.Match(slots.Skip(1).Take(size * size).ToArray(), size)?.Output ?? ItemStack.Empty;
            if (_craftSlots is not null) Array.Copy(slots, 10, Inventory, 9, 36);
        }
        Send(new TransactionPacket { WindowId = click.WindowId, ActionNumber = click.ActionNumber, Accepted = valid });
        if (!valid)
        {
            Send(new WindowItemsPacket { WindowId = _craftWindow, Items = (ItemStack[])slots.Clone() });
            Send(new SetSlotPacket { WindowId = -1, Slot = -1, Item = _craftCursor });
        }
        return true;
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
