namespace MineClaw.Data;

/// <summary>
/// 1.12.2 enchantment ids and their registry names (<c>Enchantment.REGISTRY</c>); b1.8.1 has no enchantments. Item tags carry the
/// ids (<see cref="ItemTag.Enchantments"/>); Story Mode's gear and material rules speak the names.
/// </summary>
public static class Enchantments
{
    public const int Efficiency = 32, SilkTouch = 33, Unbreaking = 34, Fortune = 35, Sharpness = 16;

    private static readonly Dictionary<int, string> Names = new()
    {
        [0] = "protection", [1] = "fire_protection", [2] = "feather_falling", [3] = "blast_protection", [4] = "projectile_protection",
        [5] = "respiration", [6] = "aqua_affinity", [7] = "thorns", [8] = "depth_strider", [9] = "frost_walker", [10] = "binding_curse",
        [16] = "sharpness", [17] = "smite", [18] = "bane_of_arthropods", [19] = "knockback", [20] = "fire_aspect", [21] = "looting",
        [22] = "sweeping", [32] = "efficiency", [33] = "silk_touch", [34] = "unbreaking", [35] = "fortune", [48] = "power", [49] = "punch",
        [50] = "flame", [51] = "infinity", [61] = "luck_of_the_sea", [62] = "lure", [70] = "mending", [71] = "vanishing_curse",
    };

    private static readonly Dictionary<string, int> Ids = Names.ToDictionary(kv => kv.Value, kv => kv.Key, StringComparer.OrdinalIgnoreCase);

    /// <summary>The registry name of an enchantment id ("fortune"), or "enchantment_&lt;id&gt;".</summary>
    public static string Name(int id) => Names.TryGetValue(id, out var n) ? n : "enchantment_" + id;

    /// <summary>The id of a registry name, or null.</summary>
    public static int? Id(string name) => Ids.TryGetValue(name.Trim().Replace(' ', '_'), out var id) ? id : null;

    /// <summary>A stack's enchantments by name (empty for none).</summary>
    public static IReadOnlyDictionary<string, int> Of(ItemStack s) =>
        s.Tag is { Enchantments.Count: > 0 } t ? t.Enchantments.ToDictionary(kv => Name(kv.Key), kv => kv.Value) : new Dictionary<string, int>();

    /// <summary>
    /// Does the stack satisfy an enchantment wish: "fortune" / "silk_touch" (that one, any level), "any" (any enchantment), null or ""
    /// (anything).
    /// </summary>
    public static bool Satisfies(ItemStack s, string? wish)
    {
        if (string.IsNullOrWhiteSpace(wish)) return true;
        if (s.Tag is not { Enchantments.Count: > 0 } t) return false;
        if (wish.Equals("any", StringComparison.OrdinalIgnoreCase)) return true;
        return Id(wish) is { } id && t.EnchantmentLevel(id) > 0;
    }
}
