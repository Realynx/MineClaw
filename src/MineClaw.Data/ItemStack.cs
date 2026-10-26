namespace MineClaw.Data;

/// <summary>An item stack as it exists in a slot. Empty slots are <see cref="Empty"/> (Id = -1). b1.8.1 slots carry no NBT.</summary>
public readonly record struct ItemStack(short Id, byte Count, short Damage)
{
    public static readonly ItemStack Empty = new(-1, 0, 0);
    public bool IsEmpty => Id < 0 || Count == 0;
    public override string ToString() => IsEmpty ? "(empty)" : $"{Id}:{Damage} x{Count}";
}
