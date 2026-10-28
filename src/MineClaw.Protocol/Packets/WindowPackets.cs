using MineClaw.Data;

namespace MineClaw.Protocol.Packets;

/// <summary>
/// 0x64 Open Window (S→C). Fields: byte WindowId, ubyte InventoryType, string16 Title (string16 since b1.8 pre2;
/// string8 before), ubyte SlotCount (container slots, excluding the player inventory).
/// InventoryType: 0 chest, 1 workbench, 2 furnace, 3 dispenser.
/// </summary>
public sealed class OpenWindowPacket : IPacket
{
    public const byte PacketId = 0x64;
    public byte Id => PacketId;
    public sbyte WindowId { get; set; }
    public byte InventoryType { get; set; }
    public string Title { get; set; } = string.Empty;
    public byte SlotCount { get; set; }

    public void Read(MinecraftReader r, PacketDirection d)
    {
        WindowId = r.ReadSByte();
        InventoryType = r.ReadUByte();
        Title = r.ReadString16();
        SlotCount = r.ReadUByte();
    }

    public void Write(MinecraftWriter w, PacketDirection d)
    {
        w.WriteSByte(WindowId);
        w.WriteUByte(InventoryType);
        w.WriteString16(Title);
        w.WriteUByte(SlotCount);
    }
}

/// <summary>0x65 Close Window (both directions). Fields: byte WindowId (0 = player inventory).</summary>
public sealed class CloseWindowPacket : IPacket
{
    public const byte PacketId = 0x65;
    public byte Id => PacketId;
    public sbyte WindowId { get; set; }

    public void Read(MinecraftReader r, PacketDirection d) => WindowId = r.ReadSByte();
    public void Write(MinecraftWriter w, PacketDirection d) => w.WriteSByte(WindowId);
}

/// <summary>
/// 0x66 Window Click (C→S). Fields: byte WindowId, short Slot (−999 = outside window: drop cursor),
/// byte RightClick (0 left, 1 right), short ActionNumber (per-window counter echoed in 0x6A), bool Shift,
/// slot ClickedItem (what the client believes is in the slot <i>before</i> the click; short −1 if empty).
/// The server answers with 0x6A Transaction (accepted or not); on rejection it resends the window contents and the
/// client must send 0x6A back with the same action number and accepted = true to unlock the window.
/// </summary>
public sealed class WindowClickPacket : IPacket
{
    public const byte PacketId = 0x66;
    public const short OutsideWindowSlot = -999;
    public byte Id => PacketId;
    public sbyte WindowId { get; set; }
    public short Slot { get; set; }
    public bool RightClick { get; set; }
    public short ActionNumber { get; set; }
    public bool Shift { get; set; }
    public ItemStack ClickedItem { get; set; } = ItemStack.Empty;
    /// <summary>1.12.2 click type (<c>ClickType</c> ordinals): 0 pickup, 1 quick move (shift), 2 swap with hotbar key
    /// <see cref="Button"/> (0..8), 3 clone (creative middle click), 4 throw (button 0 one, 1 the stack), 5 quick craft (drag:
    /// button = stage | mode &lt;&lt; 2), 6 pickup all (double click). 0 here with <see cref="Shift"/> set means 1. b1.8.1 knows
    /// only 0 and 1.</summary>
    public byte Mode { get; set; }
    /// <summary>The mouse button / hotbar key / drag code; null = <see cref="RightClick"/> ? 1 : 0.</summary>
    public sbyte? Button { get; set; }

    public const byte ModePickup = 0, ModeQuickMove = 1, ModeSwap = 2, ModeClone = 3, ModeThrow = 4, ModeQuickCraft = 5, ModePickupAll = 6;

    /// <summary>The click type actually meant (<see cref="Shift"/> folds into 1).</summary>
    public byte EffectiveMode => Mode != 0 ? Mode : Shift ? ModeQuickMove : ModePickup;
    public sbyte EffectiveButton => Button ?? (sbyte)(RightClick ? 1 : 0);

    public void Read(MinecraftReader r, PacketDirection d)
    {
        WindowId = r.ReadSByte();
        Slot = r.ReadShort();
        RightClick = r.ReadSByte() != 0;
        ActionNumber = r.ReadShort();
        Shift = r.ReadBool();
        ClickedItem = r.ReadSlot();
    }

    public void Write(MinecraftWriter w, PacketDirection d)
    {
        w.WriteSByte(WindowId);
        w.WriteShort(Slot);
        w.WriteBool(RightClick);
        w.WriteShort(ActionNumber);
        w.WriteBool(Shift);
        w.WriteSlot(ClickedItem);
    }
}

/// <summary>
/// 0x67 Set Slot (S→C). Fields: byte WindowId, short Slot, slot Item.
/// WindowId −1 with Slot −1 sets the item held on the cursor.
/// Player inventory (window 0) slots: 0 craft output, 1–4 craft grid, 5–8 armour, 9–35 main, 36–44 hotbar.
/// </summary>
public sealed class SetSlotPacket : IPacket
{
    public const byte PacketId = 0x67;
    public byte Id => PacketId;
    public sbyte WindowId { get; set; }
    public short Slot { get; set; }
    public ItemStack Item { get; set; } = ItemStack.Empty;

    public bool IsCursor => WindowId == -1 && Slot == -1;

    public void Read(MinecraftReader r, PacketDirection d)
    {
        WindowId = r.ReadSByte();
        Slot = r.ReadShort();
        Item = r.ReadSlot();
    }

    public void Write(MinecraftWriter w, PacketDirection d)
    {
        w.WriteSByte(WindowId);
        w.WriteShort(Slot);
        w.WriteSlot(Item);
    }
}

/// <summary>0x68 Window Items (S→C). Fields: byte WindowId, short Count, Count × slot.</summary>
public sealed class WindowItemsPacket : IPacket
{
    public const byte PacketId = 0x68;
    public byte Id => PacketId;
    public sbyte WindowId { get; set; }
    public ItemStack[] Items { get; set; } = [];

    public void Read(MinecraftReader r, PacketDirection d)
    {
        WindowId = r.ReadSByte();
        short count = r.ReadShort();
        if (count < 0) throw new ProtocolException($"Window items count {count} is negative");
        var items = new ItemStack[count];
        for (int i = 0; i < count; i++) items[i] = r.ReadSlot();
        Items = items;
    }

    public void Write(MinecraftWriter w, PacketDirection d)
    {
        if (Items.Length > short.MaxValue) throw new InvalidOperationException("Too many window items");
        w.WriteSByte(WindowId);
        w.WriteShort((short)Items.Length);
        foreach (var item in Items) w.WriteSlot(item);
    }
}

/// <summary>
/// 0x69 Update Progress Bar (S→C). Fields: byte WindowId, short ProgressBar, short Value.
/// Furnace: bar 0 = cook progress (0..200), bar 1 = fuel burn time left, bar 2 = total burn time of current fuel.
/// </summary>
public sealed class UpdateProgressBarPacket : IPacket
{
    public const byte PacketId = 0x69;
    public byte Id => PacketId;
    public sbyte WindowId { get; set; }
    public short ProgressBar { get; set; }
    public short Value { get; set; }

    public void Read(MinecraftReader r, PacketDirection d)
    {
        WindowId = r.ReadSByte();
        ProgressBar = r.ReadShort();
        Value = r.ReadShort();
    }

    public void Write(MinecraftWriter w, PacketDirection d)
    {
        w.WriteSByte(WindowId);
        w.WriteShort(ProgressBar);
        w.WriteShort(Value);
    }
}

/// <summary>
/// 0x6A Transaction (both directions). Fields: byte WindowId, short ActionNumber, bool Accepted.
/// S→C: result of a 0x66 click. C→S: acknowledgement of a rejected click (Accepted = true).
/// </summary>
public sealed class TransactionPacket : IPacket
{
    public const byte PacketId = 0x6A;
    public byte Id => PacketId;
    public sbyte WindowId { get; set; }
    public short ActionNumber { get; set; }
    public bool Accepted { get; set; }

    public void Read(MinecraftReader r, PacketDirection d)
    {
        WindowId = r.ReadSByte();
        ActionNumber = r.ReadShort();
        Accepted = r.ReadSByte() != 0;
    }

    public void Write(MinecraftWriter w, PacketDirection d)
    {
        w.WriteSByte(WindowId);
        w.WriteShort(ActionNumber);
        w.WriteBool(Accepted);
    }
}

/// <summary>
/// 0x6B Creative Inventory Action (both directions). b1.8 layout (switched to slot format in 1.0):
/// short Slot, short ItemId, short Count, short Damage. Only valid in creative mode.
/// </summary>
public sealed class CreativeInventoryActionPacket : IPacket
{
    public const byte PacketId = 0x6B;
    public byte Id => PacketId;
    public short Slot { get; set; }
    public short ItemId { get; set; }
    public short Count { get; set; }
    public short Damage { get; set; }

    public void Read(MinecraftReader r, PacketDirection d)
    {
        Slot = r.ReadShort();
        ItemId = r.ReadShort();
        Count = r.ReadShort();
        Damage = r.ReadShort();
    }

    public void Write(MinecraftWriter w, PacketDirection d)
    {
        w.WriteShort(Slot);
        w.WriteShort(ItemId);
        w.WriteShort(Count);
        w.WriteShort(Damage);
    }
}
