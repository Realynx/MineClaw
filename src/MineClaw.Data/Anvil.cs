namespace MineClaw.Data;

/// <summary>
/// The anvil as the 1.12.2 game prices it (b1.8.1 has no anvil). An anvil job costs XP levels: the left item's and the right item's
/// prior work (their "RepairCost", which doubles plus one with every anvil use), plus
/// <list type="bullet">
/// <item>a material repair: 1 per unit used (each unit mends a quarter of the item's durability);</item>
/// <item>two of the same item: 2 when the merge mends the left one, plus the right one's enchantments;</item>
/// <item>an enchanted book: its enchantments;</item>
/// </list>
/// where each enchantment carried over costs its rarity multiplier (common 1, uncommon 2, rare 4, very rare 8; halved from a book)
/// × its resulting level, and each conflicting enchantment already on the item 1. 40 levels or more is "Too Expensive!". Every use
/// has a 12% chance to wear the anvil a step (intact → chipped → damaged → gone).
/// </summary>
public static class Anvil
{
    public const int Block = 145;
    /// <summary>A cost at or above this is refused ("Too Expensive!").</summary>
    public const int TooExpensive = 40;
    public const double WearChance = 0.12;

    /// <summary>The anvil's wear from its block meta (bits 2–3): intact, chipped, damaged.</summary>
    public static string Condition(int meta) => ((meta >> 2) & 3) switch { 0 => "intact", 1 => "chipped", _ => "damaged" };

    /// <summary>The material that repairs <paramref name="item"/> (a registry name): planks for wood (any kind: meta −1), cobblestone,
    /// iron or gold ingots, diamonds, leather (leather armour, elytra); chainmail takes iron. Null when nothing does.</summary>
    public static (int Id, int Meta, string Name)? RepairMaterial(string item)
    {
        if (item is "elytra") return (334, 0, "leather");
        if (item is "shield") return (5, -1, "planks");
        int cut = item.IndexOf('_');
        if (cut < 0) return null;
        string kind = item[(cut + 1)..];
        if (kind is not ("pickaxe" or "axe" or "shovel" or "hoe" or "sword" or "helmet" or "chestplate" or "leggings" or "boots")) return null;
        return item[..cut] switch
        {
            "wooden" => (5, -1, "planks"),
            "stone" => (4, 0, "cobblestone"),
            "iron" or "chainmail" => (265, 0, "iron_ingot"),
            "golden" => (266, 0, "gold_ingot"),
            "diamond" => (264, 0, "diamond"),
            "leather" => (334, 0, "leather"),
            _ => null,
        };
    }

    /// <summary>Material units a repair uses and the damage left: each unit mends min(damage, max/4), up to <paramref name="have"/> units.</summary>
    public static (int Units, int DamageLeft) Repair(int maxDamage, int damage, int have)
    {
        int units = 0;
        for (int step = Math.Min(damage, maxDamage / 4); step > 0 && units < have; step = Math.Min(damage, maxDamage / 4))
        {
            damage -= step;
            units++;
        }
        return (units, damage);
    }

    /// <summary>Units that mend <paramref name="damage"/> fully (or as far as quarters go).</summary>
    public static int UnitsFor(int maxDamage, int damage) => Repair(maxDamage, damage, 64).Units;

    /// <summary>A material repair: one level per unit plus the item's prior work.</summary>
    public static int RepairCost(int units, int priorWork = 0) => units + priorWork;

    /// <summary>
    /// The levels it takes to carry <paramref name="right"/>'s enchantments onto <paramref name="item"/> (holding <paramref name="left"/>):
    /// per enchantment its multiplier × the resulting level (equal levels go up one, to the max), 1 per conflict on the item; one that
    /// doesn't fit (wrong item, or only conflicts) adds nothing. Also says whether anything carried over.
    /// </summary>
    public static (int Cost, bool Any) EnchantCost(string item, IReadOnlyDictionary<int, int> left, IReadOnlyDictionary<int, int> right, bool fromBook)
    {
        int cost = 0;
        bool any = false;
        foreach (var (id, rl) in right)
        {
            if (Enchanting.ById(id) is not { } e) continue;
            int ll = left.GetValueOrDefault(id);
            int level = ll == rl ? rl + 1 : Math.Max(rl, ll);
            bool fits = Enchanting.Fits(e, item, anvil: true);
            foreach (var other in left.Keys)
                if (other != id && Enchanting.ById(other) is { } o && !e.CompatibleWith(o)) { fits = false; cost++; }
            if (!fits) continue;
            any = true;
            cost += e.AnvilMultiplier(fromBook) * Math.Min(level, e.MaxLevel);
        }
        return (cost, any);
    }

    /// <summary>Two of the same item: 2 when the right one's durability (plus a 12% bonus) mends the left one, plus its enchantments, plus
    /// both prior works.</summary>
    public static int CombineCost(string item, int maxDamage, int leftDamage, int rightDamage, IReadOnlyDictionary<int, int> left,
        IReadOnlyDictionary<int, int> right, int leftWork = 0, int rightWork = 0)
    {
        int cost = leftWork + rightWork;
        if (maxDamage > 0)
        {
            int merged = Math.Max(0, maxDamage - ((maxDamage - leftDamage) + (maxDamage - rightDamage) + maxDamage * 12 / 100));
            if (merged < leftDamage) cost += 2;
        }
        return cost + EnchantCost(item, left, right, fromBook: false).Cost;
    }

    /// <summary>An enchanted book onto an item: the book's enchantments (halved multipliers) plus both prior works.</summary>
    public static int BookCost(string item, IReadOnlyDictionary<int, int> left, IReadOnlyDictionary<int, int> book, int leftWork = 0, int bookWork = 0) =>
        leftWork + bookWork + EnchantCost(item, left, book, fromBook: true).Cost;

    /// <summary>The prior work the result carries: the larger of the two, doubled plus one.</summary>
    public static int NextWork(int leftWork, int rightWork) => Math.Max(leftWork, rightWork) * 2 + 1;
}
