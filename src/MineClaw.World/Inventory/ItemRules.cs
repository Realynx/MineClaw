using System.Reflection;
using MineClaw.Data;

namespace MineClaw.World;

/// <summary>
/// Item properties needed for click prediction. Defaults read <c>MineClaw.Data.Items.Get(id).MaxStack/MaxDamage</c>
/// when that API exists (looked up by reflection so World builds against any Data version), otherwise a built-in
/// b1.8.1 table. Every rule can be overridden (tests, or the Client wiring Data explicitly).
/// </summary>
public sealed class ItemRules
{
    public static ItemRules Default { get; set; } = new();

    public Func<int, int> MaxStack { get; init; } = DefaultMaxStack;
    public Func<int, bool> IsDamageable { get; init; } = DefaultIsDamageable;
    /// <summary>Items whose damage value is a sub-type (wool colour, dye, log type...): damage must match to stack.</summary>
    public Func<int, bool> HasSubtypes { get; init; } = DefaultHasSubtypes;

    /// <summary>ItemStack.isStackable: max stack &gt; 1 and not a damaged tool.</summary>
    public bool IsStackable(ItemStack s) => MaxStack(s.Id) > 1 && (!IsDamageable(s.Id) || s.Damage == 0);

    /// <summary>Same item for merge purposes (Container.slotClick / mergeItemStack).</summary>
    public bool SameItem(ItemStack a, ItemStack b) =>
        a.Id == b.Id && (!HasSubtypes(a.Id) || a.Damage == b.Damage) && (a.Damage == b.Damage || IsDamageable(a.Id));

    // ---- defaults ----

    private static readonly Func<int, object?>? DataItemsGet = FindDataItemsGet();

    private static Func<int, object?>? FindDataItemsGet()
    {
        try
        {
            var t = typeof(Blocks).Assembly.GetType("MineClaw.Data.Items");
            var m = t?.GetMethod("Get", BindingFlags.Public | BindingFlags.Static, [typeof(int)]);
            if (m is null) return null;
            return id => m.Invoke(null, [id]);
        }
        catch
        {
            return null;
        }
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(int, string), int?> DataCache = new();

    private static int? DataInt(int id, string prop) =>
        DataItemsGet is null ? null : DataCache.GetOrAdd((id, prop), k => ReadDataInt(k.Item1, k.Item2));

    private static int? ReadDataInt(int id, string prop)
    {
        try
        {
            var info = DataItemsGet!(id);
            var p = info?.GetType().GetProperty(prop);
            return p?.GetValue(info) is int v ? v : null;
        }
        catch
        {
            return null;
        }
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
