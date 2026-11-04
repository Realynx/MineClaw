using System.Text.Json;
using MineClaw.Data.Versioning;

namespace MineClaw.Data;

/// <summary>An enchantment (1.12.2, minecraft-data): its id, names, max level, weight (the rarity: 10 common, 5 uncommon, 2 rare,
/// 1 very rare), what it goes on (<see cref="Category"/>) and the ones it can't share an item with.</summary>
public sealed record EnchantmentInfo(int Id, string Name, string DisplayName, int MaxLevel, int Weight, string Category, IReadOnlyList<string> Exclude,
    bool Treasure)
{
    /// <summary>The anvil's per-level cost: common 1, uncommon 2, rare 4, very rare 8; from a book half that (at least 1).</summary>
    public int AnvilMultiplier(bool fromBook)
    {
        int m = Weight >= 10 ? 1 : Weight >= 5 ? 2 : Weight >= 2 ? 4 : 8;
        return fromBook ? Math.Max(1, m / 2) : m;
    }

    public bool CompatibleWith(EnchantmentInfo other) => other.Id != Id && !Exclude.Contains(other.Name) && !other.Exclude.Contains(Name);
}

/// <summary>
/// The enchanting table as the 1.12.2 game plays it (b1.8.1 has no enchanting): which items take enchantments, the bookshelves that
/// power a table (<see cref="Bookshelves"/>), the three offers (each costs its slot's lapis and levels, and needs the offer's level),
/// and the choice a bill makes among them (<see cref="Choose"/>).
/// </summary>
public static class Enchanting
{
    public const int TableBlock = 116, Bookshelf = 47, Lapis = 351, LapisMeta = 4, Book = 340, EnchantedBook = 403;
    /// <summary>Bookshelves count up to 15 (a level 30 top offer).</summary>
    public const int MaxShelves = 15;

    private static readonly Lazy<IReadOnlyList<EnchantmentInfo>> _all = new(Load);

    public static IReadOnlyList<EnchantmentInfo> All => _all.Value;

    public static EnchantmentInfo? ById(int id) => All.FirstOrDefault(e => e.Id == id);

    /// <summary>"silk_touch", "Silk Touch", "silk touch" or the id as text.</summary>
    public static EnchantmentInfo? ByName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var key = name.Trim().ToLowerInvariant().Replace(' ', '_');
        if (key.StartsWith("minecraft:", StringComparison.Ordinal)) key = key[10..];
        return int.TryParse(key, out int id) ? ById(id) : All.FirstOrDefault(e => e.Name == key);
    }

    private static IReadOnlyList<EnchantmentInfo> Load()
    {
        using var doc = JsonDocument.Parse(Release1122GameData.Resource("mcdata.1.12.2.enchantments.json"));
        var list = new List<EnchantmentInfo>();
        foreach (var e in doc.RootElement.EnumerateArray())
            list.Add(new EnchantmentInfo(e.GetProperty("id").GetInt32(), e.GetProperty("name").GetString()!, e.GetProperty("displayName").GetString()!,
                e.GetProperty("maxLevel").GetInt32(), e.GetProperty("weight").GetInt32(), e.GetProperty("category").GetString()!,
                e.TryGetProperty("exclude", out var x) ? x.EnumerateArray().Select(v => v.GetString()!).ToList() : [],
                e.TryGetProperty("treasureOnly", out var t) && t.GetBoolean()));
        return list;
    }

    private static readonly string[] Tools = ["pickaxe", "axe", "shovel"];
    private static readonly string[] Armour = ["helmet", "chestplate", "leggings", "boots"];
    private static readonly string[] Materials = ["wooden", "stone", "iron", "golden", "diamond"];
    private static readonly string[] ArmourMaterials = ["leather", "chainmail", "iron", "golden", "diamond"];

    private static bool Is(string item, string kind) => item.EndsWith("_" + kind, StringComparison.Ordinal);
    private static bool IsArmour(string item) => Armour.Any(a => Is(item, a));

    /// <summary>The items an enchanting table takes (an enchantability above 0): pickaxes, axes, shovels and swords, armour, the bow,
    /// the fishing rod and the book. Hoes, shears and the rest only take enchantments through an anvil.</summary>
    public static IReadOnlyList<string> TableItems { get; } =
    [
        .. Materials.SelectMany(m => new[] { "pickaxe", "axe", "shovel", "sword" }.Select(k => $"{m}_{k}")),
        .. ArmourMaterials.SelectMany(m => Armour.Select(a => $"{m}_{a}")),
        "bow", "fishing_rod", "book",
    ];

    /// <summary>Whether <paramref name="e"/> can go on <paramref name="item"/> (a registry name): at the table (no treasure) or, with
    /// <paramref name="anvil"/>, through an anvil (treasure too; sharpness and its kin also on axes).</summary>
    public static bool Fits(EnchantmentInfo e, string item, bool anvil = false)
    {
        if (e.Treasure && !anvil) return false;
        if (item is "book" or "enchanted_book") return true;
        return e.Category is { } category && CategoryFits.TryGetValue(category, out var fits) && fits(new FitCheck(e, item, anvil));
    }

    /// <summary>One enchantment against one item, with the item kinds the categories ask about.</summary>
    private readonly record struct FitCheck(EnchantmentInfo E, string Item, bool Anvil)
    {
        public bool Sword => Is(Item, "sword");

        /// <summary>A digging tool (efficiency also goes on shears through an anvil).</summary>
        public bool Tool => IsTool(Item) || Anvil && Item == "shears" && E.Name == "efficiency";

        public bool Damageable => Sword || Tool || IsArmour(Item) || OtherDamageable.Contains(Item) || Is(Item, "hoe");

        public bool Armour => IsArmour(Item);
    }

    private static bool IsTool(string item) => Tools.Any(t => Is(item, t));

    private static readonly HashSet<string> OtherDamageable = ["bow", "fishing_rod", "shears", "flint_and_steel", "carrot_on_a_stick", "elytra", "shield"];
    private static readonly HashSet<string> SwordOnlyOnAxes = ["sharpness", "smite", "bane_of_arthropods"];

    /// <summary>What each enchantment category goes on.</summary>
    private static readonly Dictionary<string, Func<FitCheck, bool>> CategoryFits = new()
    {
        ["armor"] = f => f.Armour,
        ["armor_feet"] = f => Is(f.Item, "boots"),
        ["armor_head"] = f => Is(f.Item, "helmet"),
        ["armor_chest"] = f => Is(f.Item, "chestplate"),
        ["wearable"] = f => f.Armour || f.Item is "elytra" or "pumpkin" or "skull",
        ["weapon"] = f => f.Sword || f.Anvil && Is(f.Item, "axe") && SwordOnlyOnAxes.Contains(f.E.Name),
        ["digger"] = f => f.Tool,
        ["breakable"] = f => f.Damageable,
        ["bow"] = f => f.Item == "bow",
        ["fishing_rod"] = f => f.Item == "fishing_rod",
        ["vanishable"] = f => f.Damageable || f.Item is "pumpkin" or "skull",
    };

    /// <summary>The enchantments the table can put on <paramref name="item"/>.</summary>
    public static IEnumerable<EnchantmentInfo> TableEnchantments(string item) => All.Where(e => Fits(e, item));

    /// <summary>
    /// The bookshelves powering a table at x,y,z (the game's walk): for each of the 8 directions whose two cells next to the table (the
    /// table's level and one up) are air, the bookshelves two out at both levels; for a corner direction also the two cells beside it.
    /// At most <see cref="MaxShelves"/> count.
    /// </summary>
    public static int Bookshelves(Func<int, int, int, int> blockAt, int x, int y, int z)
    {
        int n = 0;
        bool Shelf(int dx, int dy, int dz) => blockAt(x + dx, y + dy, z + dz) == Bookshelf;
        for (int dz = -1; dz <= 1; dz++)
            for (int dx = -1; dx <= 1; dx++)
            {
                if (dx == 0 && dz == 0 || blockAt(x + dx, y, z + dz) != 0 || blockAt(x + dx, y + 1, z + dz) != 0) continue;
                for (int dy = 0; dy <= 1; dy++)
                {
                    if (Shelf(dx * 2, dy, dz * 2)) n++;
                    if (dx != 0 && dz != 0)
                    {
                        if (Shelf(dx * 2, dy, dz)) n++;
                        if (Shelf(dx, dy, dz * 2)) n++;
                    }
                }
            }
        return Math.Min(MaxShelves, n);
    }

    /// <summary>The level range of the top (third) offer with <paramref name="shelves"/> bookshelves: the game rolls
    /// 1..8 + shelves/2 + 0..shelves and takes at least 2 × shelves. 15 shelves: always 30.</summary>
    public static (int Min, int Max) TopOffer(int shelves)
    {
        shelves = Math.Clamp(shelves, 0, MaxShelves);
        return (Math.Max(1 + shelves / 2, shelves * 2), Math.Max(8 + shelves / 2 + shelves, shelves * 2));
    }

    /// <summary>One of the table's three offers once an item is in it: the slot (0..2: costs slot+1 lapis and levels), the level it
    /// needs, and the hint the table shows (an enchantment id, −1 for none, and its level).</summary>
    public sealed record Offer(int Slot, int Level, int ClueId, int ClueLevel)
    {
        public int Lapis => Slot + 1;
        public string Describe() => Level <= 0 ? $"#{Slot + 1}: none"
            : $"#{Slot + 1}: level {Level}" + (ById(ClueId) is { } e ? $" ({e.DisplayName} {Roman(ClueLevel)}?)" : "");
    }

    /// <summary>
    /// The offer a bill takes: among the offers up to <paramref name="maxOption"/> (1..3) that the pawn can pay (its level ≥ the offer's,
    /// lapis ≥ slot + 1), the highest one showing <paramref name="preferId"/> when there is one; else (unless <paramref name="preferOnly"/>)
    /// the highest it can pay. The offers are what the table shows for this item: a reroll isn't possible without changing the item.
    /// Null with why when none.
    /// </summary>
    public static (Offer? Pick, string? Why) Choose(IReadOnlyList<Offer> offers, int playerLevel, int lapis, int maxOption, int? preferId, bool preferOnly)
    {
        maxOption = Math.Clamp(maxOption, 1, 3);
        var shown = offers.Where(o => o.Level > 0 && o.Slot < maxOption).ToList();
        if (shown.Count == 0) return (null, "the table offers nothing for it");
        var payable = shown.Where(o => playerLevel >= o.Level && lapis >= o.Lapis).OrderByDescending(o => o.Slot).ToList();
        if (preferId is { } pid)
        {
            if (payable.FirstOrDefault(o => o.ClueId == pid) is { } hit) return (hit, null);
            if (preferOnly)
            {
                var name = ById(pid)?.DisplayName ?? "#" + pid;
                return (null, shown.Any(o => o.ClueId == pid)
                    ? $"the {name} offer needs {shown.Where(o => o.ClueId == pid).Min(o => o.Level)} XP levels (has {playerLevel})"
                    : $"no offer shows {name} ({string.Join(", ", shown.Select(o => o.Describe()))})");
            }
        }
        if (payable.Count > 0) return (payable[0], null);
        var cheapest = shown.OrderBy(o => o.Level).First();
        return (null, playerLevel < cheapest.Level ? $"needs {cheapest.Level} XP levels (has {playerLevel})" : $"needs {cheapest.Lapis} lapis (has {lapis})");
    }

    public static string Roman(int n) => n switch { 1 => "I", 2 => "II", 3 => "III", 4 => "IV", 5 => "V", _ => n.ToString() };

    /// <summary>"Fortune III, Unbreaking II".</summary>
    public static string Describe(IReadOnlyDictionary<int, int> ench) =>
        string.Join(", ", ench.Select(kv => $"{ById(kv.Key)?.DisplayName ?? "#" + kv.Key} {Roman(kv.Value)}"));
}
