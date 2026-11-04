using MineClaw.Data;

namespace MineClaw.Protocol.Release1122;

/// <summary>
/// Shared 1.12.2 field codecs for both directions (the client connection and the fake server's): slots with NBT, entity
/// metadata, window types, furnace properties, sign block entities. Ported from the decompiled <c>PacketBuffer</c>,
/// <c>EntityDataManager</c>, <c>DataSerializers</c>, <c>TileEntityFurnace.getField</c> and <c>TileEntitySign</c>.
/// </summary>
public static class Codec1122
{
    // ------------------------------------------------------------------ slots

    /// <summary><c>PacketBuffer.readItemStack</c>: short id (&lt; 0 = empty), byte count, short damage, NBT (TAG_End = none).
    /// A count ≤ 0 is empty (<c>ItemStack.isEmpty</c>).</summary>
    public static ItemStack ReadSlot(ref WireReader r)
    {
        short id = r.ReadShort();
        if (id < 0) return ItemStack.Empty;
        sbyte count = r.ReadSByte();
        short damage = r.ReadShort();
        var rest = r.Unread;
        var tag = Nbt.ReadRoot(rest, out int consumed);
        r.Skip(consumed);
        if (count <= 0) return ItemStack.Empty;
        var stack = new ItemStack(id, (byte)count, damage);
        return tag is null ? stack : stack with { Tag = TagOf(rest[..consumed].ToArray(), tag) };
    }

    /// <summary>Reads a slot at <paramref name="data"/>[<paramref name="pos"/>..], advancing <paramref name="pos"/>.</summary>
    public static ItemStack ReadSlot(ReadOnlySpan<byte> data, ref int pos)
    {
        var r = new WireReader(data[pos..]);
        var s = ReadSlot(ref r);
        pos += r.Position;
        return s;
    }

    /// <summary><c>PacketBuffer.writeItemStack</c>: short −1 for empty, else id, count, damage and the tag (or TAG_End).</summary>
    public static WireWriter WriteSlot(this WireWriter w, ItemStack s)
    {
        if (s.IsEmpty) return w.WriteShort(-1);
        w.WriteShort(s.Id).WriteSByte((sbyte)Math.Min((int)s.Count, 127)).WriteShort(s.Damage);
        if (s.Tag is { } t) w.WriteBytes(t.Bytes);
        else w.WriteUByte(0);
        return w;
    }

    /// <summary>An <see cref="ItemTag"/> for a compound: its exact bytes plus the enchantments ("ench": [{id, lvl}]),
    /// display.Name and display.color (dyed leather armour, <c>ItemArmor.getColor</c>).</summary>
    public static ItemTag TagOf(byte[] bytes, NbtCompound c)
    {
        var ench = Enchantments(c.GetList("ench"));
        var display = c.GetCompound("display");
        int? color = display?.GetInt("color") is { } rgb ? rgb & 0xFFFFFF : null;
        // The glint (ItemStack.isItemEnchanted: an "ench" list, even an empty one; an enchanted book's StoredEnchantments) and a
        // potion's effects (PotionUtils.getEffectsFromStack: its "Potion" type's effects, then CustomPotionEffects).
        bool enchanted = c.GetList("ench") is not null || c.GetList("StoredEnchantments") is { Count: > 0 };
        var potion = PotionType(c);
        bool effects = HasPotionEffects(potion) || c.GetList("CustomPotionEffects") is { Count: > 0 };
        var stored = Enchantments(c.GetList("StoredEnchantments"));
        return new ItemTag(bytes, ench, display?.GetString("Name"), color)
        {
            Enchanted = enchanted, PotionEffects = effects, Potion = potion, StoredEnchantments = stored, RepairCost = c.GetInt("RepairCost") ?? 0,
        };
    }

    /// <summary>An enchantment list's id → level (level 1 when missing; entries without an id skipped).</summary>
    private static Dictionary<int, int> Enchantments(NbtList? list)
    {
        var levels = new Dictionary<int, int>();
        if (list is null) return levels;
        foreach (var o in list.Items)
            if (o is NbtCompound e && e.GetInt("id") is { } id) levels[id] = e.GetInt("lvl") ?? 1;
        return levels;
    }

    /// <summary>The "Potion" type without its namespace, or null.</summary>
    private static string? PotionType(NbtCompound c) =>
        c.GetString("Potion") is { } pt ? (pt.StartsWith("minecraft:", StringComparison.Ordinal) ? pt[10..] : pt) : null;

    /// <summary>A potion type with effects (not the empty, water, mundane, thick or awkward base potions).</summary>
    private static bool HasPotionEffects(string? potion) =>
        potion is not null && potion is not ("empty" or "water" or "mundane" or "thick" or "awkward");

    /// <summary>A tag built from a compound (fixtures, the fake server).</summary>
    public static ItemTag TagOf(NbtCompound c) => TagOf(Nbt.ToBytes(c), c);

    // ------------------------------------------------------------------ entity metadata

    /// <summary>EntityItem's ITEM key (Entity's six keys come first: flags, air, name, name visible, silent, no gravity).</summary>
    public const int ItemMetadataIndex = 6;

    /// <summary>
    /// Reads an Entity Metadata list (<c>EntityDataManager.readEntries</c>: ubyte index, 0xFF ends; VarInt serializer id;
    /// the value) and returns the slot at <paramref name="wantIndex"/> if it is one. Serializer ids (<c>DataSerializers</c>
    /// registration order): 0 byte, 1 VarInt, 2 float, 3 string, 4 chat, 5 slot, 6 bool, 7 rotation, 8 position,
    /// 9 optional position, 10 facing, 11 optional UUID, 12 optional block state, 13 NBT.
    /// </summary>
    public static ItemStack? ReadMetadataSlot(ReadOnlySpan<byte> data, ref int pos, int wantIndex = ItemMetadataIndex)
    {
        ItemStack? found = null;
        foreach (var m in EntityMeta1122.Read(data, ref pos))
            if (m.Index == wantIndex && m.Serializer == 5) found = (ItemStack)m.Value!;
        return found;
    }

    /// <summary>An item entity's metadata: index 6, serializer 5 (slot), then the 0xFF end.</summary>
    public static WireWriter WriteItemMetadata(this WireWriter w, ItemStack item) =>
        w.WriteUByte(ItemMetadataIndex).WriteVarInt(5).WriteSlot(item).WriteUByte(0xFF);

    // ------------------------------------------------------------------ windows

    /// <summary>
    /// Open Window's string type (<c>IInteractionObject.getGuiID</c>, <c>SPacketOpenWindow</c>) → the canonical inventory type
    /// (0 chest, 1 workbench, 2 furnace, 3 dispenser) and the container's own slot count. The crafting table, enchanting table
    /// and anvil are sent with slot count 0 (interaction objects); window types b1.8.1 doesn't have become chest-like windows
    /// of their real size, so the player part of the layout still lines up.
    /// </summary>
    public static (byte Type, int Slots) WindowType(string type, int slotCount) =>
        FixedWindows.TryGetValue(type, out var fixedWindow) ? fixedWindow : ((byte)0, slotCount); // chest, container, shulker box, the rest: as sent

    // The windows whose layout doesn't follow the slot count sent.
    private static readonly Dictionary<string, (byte Type, int Slots)> FixedWindows = new(StringComparer.Ordinal)
    {
        ["minecraft:crafting_table"] = (1, 9),
        ["minecraft:furnace"] = (2, 3),
        ["minecraft:dispenser"] = (3, 9),
        ["minecraft:dropper"] = (3, 9),
        ["minecraft:hopper"] = (0, 5),
        ["minecraft:enchanting_table"] = (0, 2),
        ["minecraft:anvil"] = (0, 3),
        ["minecraft:beacon"] = (0, 1),
        ["minecraft:brewing_stand"] = (0, 5),
        ["minecraft:villager"] = (0, 3),
    };

    /// <summary>The string type the vanilla server sends for a canonical inventory type, and the slot count it puts in.</summary>
    public static (string Type, int SlotCount) WindowTypeName(int canonicalType, int slots) => canonicalType switch
    {
        1 => ("minecraft:crafting_table", 0),
        2 => ("minecraft:furnace", 3),
        3 => ("minecraft:dispenser", 9),
        _ => ("minecraft:chest", slots),
    };

    /// <summary>
    /// Furnace window property, 1.12.2 → canonical. 1.12.2 <c>TileEntityFurnace.getField</c>: 0 burn time left, 1 the current
    /// fuel's total burn time, 2 cook time, 3 total cook time (200). b1.8.1 0x69: 0 cook progress, 1 burn time left, 2 total
    /// burn time. −1 = no canonical counterpart.
    /// </summary>
    public static int FurnacePropertyToCanonical(int p) => p switch { 0 => 1, 1 => 2, 2 => 0, _ => -1 };
    public static int FurnacePropertyFromCanonical(int p) => p switch { 0 => 2, 1 => 0, 2 => 1, _ => p };

    /// <summary>
    /// Set Slot window −2 (<c>NetHandlerPlayClient.handleSetSlot</c>: <c>inventory.setInventorySlotContents</c>) names an
    /// <c>InventoryPlayer</c> index: 0..8 hotbar, 9..35 main, 36..39 armor (boots first), 40 off hand. → window-0 slot.
    /// </summary>
    public static int InventoryIndexToWindowSlot(int i) => i switch
    {
        >= 0 and <= 8 => 36 + i,
        >= 9 and <= 35 => i,
        >= 36 and <= 39 => 8 - (i - 36),
        40 => 45,
        _ => -1,
    };

    // ------------------------------------------------------------------ signs

    /// <summary>Update Block Entity action 9 (<c>NetHandlerPlayClient.handleUpdateTileEntity</c>): a sign.</summary>
    public const int SignAction = 9;

    /// <summary>A sign block entity's four lines (Text1..Text4 are JSON components), or null when it isn't a sign.</summary>
    public static string[]? SignLines(NbtCompound tag, LangTable? lang = null)
    {
        if (tag.GetString("Text1") is null) return null;
        var id = tag.GetString("id");
        if (id is not null && id is not ("minecraft:sign" or "Sign")) return null;
        var lines = new string[4];
        for (int i = 0; i < 4; i++) lines[i] = ChatComponent.ToPlain(tag.GetString("Text" + (i + 1)) ?? "", lang);
        return lines;
    }

    /// <summary>A sign's four lines with their JSON styles as legacy § codes (<see cref="ChatComponent.ToLegacy"/>: colours,
    /// bold, italic, underline, strikethrough, obfuscated, from the component, its <c>extra</c> and § codes in its text, as
    /// <c>getFormattedText</c> hands TileEntitySignRenderer), or null when it isn't a sign or no line has a code.</summary>
    public static string[]? SignLegacy(NbtCompound tag, LangTable? lang = null)
    {
        if (tag.GetString("Text1") is null) return null;
        var id = tag.GetString("id");
        if (id is not null && id is not ("minecraft:sign" or "Sign")) return null;
        var lines = new string[4];
        bool any = false;
        for (int i = 0; i < 4; i++)
        {
            lines[i] = ChatComponent.ToLegacy(tag.GetString("Text" + (i + 1)) ?? "", lang);
            any |= lines[i].Contains('§');
        }
        return any ? lines : null;
    }

    /// <summary>The block entity NBT the server sends for a sign (<c>TileEntitySign.writeToNBT</c>); <paramref name="json"/> = the
    /// lines as raw JSON components (styled text), else plain text components.</summary>
    public static NbtCompound SignTag(int x, int y, int z, IReadOnlyList<string> lines, IReadOnlyList<string>? json = null)
    {
        var c = new NbtCompound { ["id"] = "minecraft:sign", ["x"] = x, ["y"] = y, ["z"] = z };
        for (int i = 0; i < 4; i++)
            c["Text" + (i + 1)] = json is not null && i < json.Count ? json[i] : ChatComponent.Text(i < lines.Count ? lines[i] : "");
        return c;
    }
}
