using MineClaw.Data;

namespace MineClaw.World;

/// <summary>
/// Item properties needed for click prediction. Defaults read <c>MineClaw.Data.Items.Get(id).MaxStack/MaxDamage</c>
/// for known ids, otherwise a built-in b1.8.1 table. Every rule can be overridden (tests, or the Client wiring Data explicitly).
/// </summary>
public sealed class ItemRules
{
    public static ItemRules Default { get; set; } = new();

    public Func<int, int> MaxStack { get; init; } = DefaultMaxStack;
    public Func<int, bool> IsDamageable { get; init; } = DefaultIsDamageable;
    /// <summary>Items whose damage value is a sub-type (wool colour, dye, log type...): damage must match to stack.</summary>
    public Func<int, bool> HasSubtypes { get; init; } = DefaultHasSubtypes;
    /// <summary>Burns in a furnace (<c>TileEntityFurnace.isItemFuel</c>): the fuel slot accepts it and a shift-click routes it there.</summary>
    public Func<int, bool> IsFuel { get; init; } = id => Smelting.FuelTicks(id) > 0;
    /// <summary>Has a furnace result (<c>FurnaceRecipes.getSmeltingResult</c> is not empty): a shift-click routes it to the input.</summary>
    public Func<ItemStack, bool> IsSmeltable { get; init; } = s => Smelting.ResultFor(s.Id, s.Damage) is not null;

    /// <summary>ItemStack.isStackable: max stack &gt; 1 and not a damaged tool.</summary>
    public bool IsStackable(ItemStack s) => MaxStack(s.Id) > 1 && (!IsDamageable(s.Id) || s.Damage == 0);

    /// <summary>Same item for merge purposes (Container.slotClick / mergeItemStack).</summary>
    public bool SameItem(ItemStack a, ItemStack b) =>
        a.Id == b.Id && (!HasSubtypes(a.Id) || a.Damage == b.Damage) && (a.Damage == b.Damage || IsDamageable(a.Id)) && Equals(a.Tag, b.Tag);

    // ---- defaults ----

    /// <summary>Max stack / durability from <see cref="Items"/> for known ids, else null (fallback table below).</summary>
    private static int? DataInt(int id, string prop)
    {
        if (!Items.Exists(id)) return null;
        var info = Items.Get(id);
        return prop == "MaxStack" ? info.MaxStack : info.MaxDamage;
    }

    public static int DefaultMaxStack(int id)
    {
        if (DataInt(id, "MaxStack") is int m && m > 0) return m;
        if (id < 256) return 64;
        if (IsToolOrArmor(id)) return 1;
        return id switch
        {
            282 or 323 or 324 or 325 or 326 or 327 or 328 or 329 or 330 or 333 or 335 or 342 or 343 or 346
                or 354 or 355 or 358 or 359 or 2256 or 2257 => 1,
            332 or 344 => 16,
            357 => 8,
            _ => 64,
        };
    }

    public static bool DefaultIsDamageable(int id)
    {
        if (DataInt(id, "MaxDamage") is int d) return d > 0;
        return IsToolOrArmor(id);
    }

    public static bool DefaultHasSubtypes(int id) =>
        id is 6 or 17 or 18 or 31 or 35 or 43 or 44 or 98 or 263 or 351 or 97;

    /// <summary>b1.8.1 tool/weapon/armor item ids.</summary>
    public static bool IsToolOrArmor(int id) =>
        id is >= 256 and <= 259 or 261 or >= 267 and <= 279 or >= 283 and <= 286 or >= 290 and <= 294
            or >= 298 and <= 317 or 346 or 359;

    /// <summary>Armor slot (0 helmet .. 3 boots) for an armor item id, or -1.</summary>
    public static int ArmorType(int id) => id is >= 298 and <= 317 ? (id - 298) % 4 : -1;

    /// <summary>Container item left behind when the item is consumed in crafting (buckets).</summary>
    public static ItemStack ContainerItem(int id) => id is 326 or 327 or 335 ? new ItemStack(325, 1, 0) : ItemStack.Empty;
}
