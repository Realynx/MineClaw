namespace MineClaw.Data;

/// <summary>
/// An item stack as it exists in a slot. Empty slots are <see cref="Empty"/> (Id = -1). b1.8.1 slots carry no NBT; 1.12.2
/// slots may carry a <see cref="Tag"/> (enchantments, a custom name, book pages...), which takes part in equality: the
/// 1.12.2 server compares a click's expected item with <c>ItemStack.areItemStacksEqual</c>, NBT included, and stacks only
/// merge when their tags are equal.
/// </summary>
public readonly record struct ItemStack(short Id, byte Count, short Damage)
{
    public static readonly ItemStack Empty = new(-1, 0, 0);
    public bool IsEmpty => Id < 0 || Count == 0;

    /// <summary>The stack's NBT compound (1.12.2), or null. b1.8.1 stacks never have one.</summary>
    public ItemTag? Tag { get; init; }

    public override string ToString() => IsEmpty ? "(empty)" : $"{Id}:{Damage} x{Count}{(Tag is null ? "" : " +nbt")}";
}

/// <summary>
/// An item's NBT compound in its network form (1.12.2 <c>NBTTagCompound</c> as written by <c>PacketBuffer.writeCompoundTag</c>:
/// type 10, the empty root name, the payload, TAG_End), kept verbatim so a click echoes exactly what the server sent.
/// Equality is byte equality. The protocol layer fills the summary fields it can read (Data has no NBT reader).
/// </summary>
public sealed class ItemTag : IEquatable<ItemTag>
{
    private readonly byte[] _bytes;
    private readonly int _hash;

    public ItemTag(byte[] bytes, IReadOnlyDictionary<int, int>? enchantments = null, string? displayName = null, int? displayColor = null)
    {
        _bytes = bytes ?? throw new ArgumentNullException(nameof(bytes));
        Enchantments = enchantments ?? new Dictionary<int, int>();
        DisplayName = displayName;
        DisplayColor = displayColor;
        var h = new HashCode();
        h.AddBytes(bytes);
        _hash = h.ToHashCode();
    }

    /// <summary>The compound as sent on the wire (root type byte included).</summary>
    public ReadOnlySpan<byte> Bytes => _bytes;
    /// <summary>Enchantment id → level from the "ench" list (16 protection … 32 efficiency, 33 silk touch, 35 fortune).</summary>
    public IReadOnlyDictionary<int, int> Enchantments { get; }
    /// <summary>display.Name, if the item was renamed.</summary>
    public string? DisplayName { get; }
    /// <summary>display.color (0xRRGGBB): the dye of leather armour; null when undyed.</summary>
    public int? DisplayColor { get; }

    /// <summary>The stack carries enchantments, its own ("ench") or an enchanted book's stored ones ("StoredEnchantments"): vanilla
    /// draws the enchantment glint on it (<c>ItemStack.isItemEnchanted</c>, <c>ItemEnchantedBook</c>). The viewer's looks only.</summary>
    public bool Enchanted { get; init; }
    /// <summary>A potion's NBT names effects (a "Potion" type other than water, mundane, thick, awkward or empty, or
    /// "CustomPotionEffects"): a potion glints (<c>ItemPotion.hasEffect</c>). The viewer's looks only.</summary>
    public bool PotionEffects { get; init; }

    public int EnchantmentLevel(int id) => Enchantments.TryGetValue(id, out int l) ? l : 0;

    public bool Equals(ItemTag? other) => other is not null && (ReferenceEquals(this, other) || _bytes.AsSpan().SequenceEqual(other._bytes));
    public override bool Equals(object? obj) => obj is ItemTag t && Equals(t);
    public override int GetHashCode() => _hash;
    public override string ToString() => $"nbt({_bytes.Length} bytes)";
}
