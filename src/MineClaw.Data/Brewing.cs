namespace MineClaw.Data;

/// <summary>
/// 1.12.2 brewing as the game plays it (b1.8.1 has no potions): the potion types (the <c>Potion</c> NBT of a potion, splash or
/// lingering potion), the brewing stand's type mixes (water + nether wart → awkward, awkward + sugar → swiftness, + redstone →
/// extended, + glowstone → strengthened, + fermented spider eye → corrupted) and form mixes (+ gunpowder → splash, + dragon's
/// breath → lingering). A stand brews up to three bottles per ingredient in 400 ticks (20 s); one blaze powder fuels 20 brews.
/// </summary>
public static class Brewing
{
    public const int Potion = 373, SplashPotion = 438, LingeringPotion = 441, GlassBottle = 374, NetherWart = 372, BlazePowder = 377;
    public const int CauldronBlock = 118, StandBlock = 117, WaterSource = 9, FlowingWater = 8;
    /// <summary>Seconds one brew takes (400 ticks).</summary>
    public const double BrewSeconds = 20;
    /// <summary>Brews one blaze powder fuels.</summary>
    public const int BrewsPerPowder = 20;
    /// <summary>Bottles a stand holds.</summary>
    public const int Bottles = 3;

    /// <summary>The potion types in registry order (<c>PotionType</c> ids): the index is the meta Story Mode gives a potion of that type
    /// (1.12.2 potions carry their type in NBT, their damage is 0).</summary>
    public static readonly IReadOnlyList<string> Types =
    [
        "empty", "water", "mundane", "thick", "awkward", "night_vision", "long_night_vision", "invisibility", "long_invisibility",
        "leaping", "long_leaping", "strong_leaping", "fire_resistance", "long_fire_resistance", "swiftness", "long_swiftness",
        "strong_swiftness", "slowness", "long_slowness", "water_breathing", "long_water_breathing", "healing", "strong_healing",
        "harming", "strong_harming", "poison", "long_poison", "strong_poison", "regeneration", "long_regeneration",
        "strong_regeneration", "strength", "long_strength", "strong_strength", "weakness", "long_weakness", "luck",
    ];

    /// <summary>The type's registry id (Story Mode's potion meta), or −1.</summary>
    public static int TypeId(string? type)
    {
        if (type is null) return -1;
        if (type.StartsWith("minecraft:", StringComparison.Ordinal)) type = type[10..];
        for (int i = 0; i < Types.Count; i++) if (Types[i] == type) return i;
        return -1;
    }

    public static string? TypeName(int id) => id >= 0 && id < Types.Count ? Types[id] : null;

    public static bool IsPotionItem(int id) => id is Potion or SplashPotion or LingeringPotion;

    /// <summary>A brewing ingredient: the item and the name the take/transfer tools know it by.</summary>
    public sealed record Ingredient(int Id, int Meta, string Name);

    public static readonly Ingredient Wart = new(NetherWart, 0, "nether_wart");
    public static readonly Ingredient Redstone = new(331, 0, "redstone");
    public static readonly Ingredient Glowstone = new(348, 0, "glowstone_dust");
    public static readonly Ingredient FermentedEye = new(376, 0, "fermented_spider_eye");
    public static readonly Ingredient Gunpowder = new(289, 0, "gunpowder");
    public static readonly Ingredient DragonBreath = new(437, 0, "dragon_breath");
    public static readonly Ingredient Powder = new(BlazePowder, 0, "blaze_powder");
    private static readonly Ingredient Sugar = new(353, 0, "sugar"), RabbitFoot = new(414, 0, "rabbit_foot"),
        Melon = new(382, 0, "speckled_melon"), SpiderEye = new(375, 0, "spider_eye"), GhastTear = new(370, 0, "ghast_tear"),
        MagmaCream = new(378, 0, "magma_cream"), GoldenCarrot = new(396, 0, "golden_carrot"), Pufferfish = new(349, 3, "pufferfish");

    /// <summary>The stand's type mixes: (from type, ingredient, to type). Mundane and thick (dead ends) are left out.</summary>
    public static readonly IReadOnlyList<(string From, Ingredient With, string To)> TypeMixes =
    [
        ("water", Wart, "awkward"),
        ("awkward", GoldenCarrot, "night_vision"), ("night_vision", Redstone, "long_night_vision"),
        ("night_vision", FermentedEye, "invisibility"), ("long_night_vision", FermentedEye, "long_invisibility"), ("invisibility", Redstone, "long_invisibility"),
        ("awkward", MagmaCream, "fire_resistance"), ("fire_resistance", Redstone, "long_fire_resistance"),
        ("awkward", RabbitFoot, "leaping"), ("leaping", Redstone, "long_leaping"), ("leaping", Glowstone, "strong_leaping"),
        ("leaping", FermentedEye, "slowness"), ("long_leaping", FermentedEye, "long_slowness"), ("slowness", Redstone, "long_slowness"),
        ("swiftness", FermentedEye, "slowness"), ("long_swiftness", FermentedEye, "long_slowness"),
        ("awkward", Sugar, "swiftness"), ("swiftness", Redstone, "long_swiftness"), ("swiftness", Glowstone, "strong_swiftness"),
        ("awkward", Pufferfish, "water_breathing"), ("water_breathing", Redstone, "long_water_breathing"),
        ("awkward", Melon, "healing"), ("healing", Glowstone, "strong_healing"),
        ("healing", FermentedEye, "harming"), ("strong_healing", FermentedEye, "strong_harming"), ("harming", Glowstone, "strong_harming"),
        ("poison", FermentedEye, "harming"), ("long_poison", FermentedEye, "harming"), ("strong_poison", FermentedEye, "strong_harming"),
        ("awkward", SpiderEye, "poison"), ("poison", Redstone, "long_poison"), ("poison", Glowstone, "strong_poison"),
        ("awkward", GhastTear, "regeneration"), ("regeneration", Redstone, "long_regeneration"), ("regeneration", Glowstone, "strong_regeneration"),
        ("awkward", Powder, "strength"), ("strength", Redstone, "long_strength"), ("strength", Glowstone, "strong_strength"),
        ("water", FermentedEye, "weakness"), ("weakness", Redstone, "long_weakness"),
    ];

    /// <summary>One brew: add <see cref="With"/> to bottles of <see cref="FromType"/>/<see cref="FromForm"/>; they become
    /// <see cref="Type"/>/<see cref="Form"/> (a potion item id).</summary>
    public sealed record Step(Ingredient With, string FromType, int FromForm, string Type, int Form);

    /// <summary>The types a stand can make (every mix's result).</summary>
    public static IEnumerable<string> Brewable => Types.Where(t => t == "awkward" || TypeMixes.Any(m => m.To == t && t != "awkward"));

    /// <summary>
    /// The brews from water bottles to <paramref name="form"/> (potion, splash or lingering) of <paramref name="type"/>: the shortest type
    /// chain, then the form steps (gunpowder, then dragon's breath). Null when no chain makes it.
    /// </summary>
    public static IReadOnlyList<Step>? Chain(int form, string type)
    {
        if (!IsPotionItem(form) || TypeId(type) < 0) return null;
        if (type == "water") return form == Potion ? null : FormSteps(form, type, []); // water bottles come from a water source
        if (MixesFromWater(type) is not { } back) return null;
        var steps = new List<Step>();
        for (var t = type; t != "water"; t = back[t].From) steps.Insert(0, new Step(back[t].With, back[t].From, Potion, t, Potion));
        return FormSteps(form, type, steps);
    }

    /// <summary>The shortest mixes from water (breadth first): each type reached and the type and ingredient it was made from; null when
    /// <paramref name="type"/> isn't reached.</summary>
    private static Dictionary<string, (string From, Ingredient With)>? MixesFromWater(string type)
    {
        var back = new Dictionary<string, (string From, Ingredient With)>();
        var queue = new Queue<string>();
        queue.Enqueue("water");
        var seen = new HashSet<string> { "water" };
        while (queue.Count > 0 && !seen.Contains(type))
        {
            var t = queue.Dequeue();
            foreach (var m in TypeMixes.Where(m => m.From == t))
                if (seen.Add(m.To)) { back[m.To] = (t, m.With); queue.Enqueue(m.To); }
        }
        return seen.Contains(type) ? back : null;
    }

    private static List<Step> FormSteps(int form, string type, List<Step> steps)
    {
        if (form is SplashPotion or LingeringPotion) steps.Add(new Step(Gunpowder, type, Potion, type, SplashPotion));
        if (form == LingeringPotion) steps.Add(new Step(DragonBreath, type, SplashPotion, type, LingeringPotion));
        return steps;
    }

    /// <summary>The water bottle's and the base potions' labels (no effect), else null.</summary>
    private static string? BaseLabel(int form, string type, string prefix) => type switch
    {
        "water" => form == Potion ? "Water Bottle" : prefix + "Water Bottle",
        "awkward" or "mundane" or "thick" => $"{prefix}{char.ToUpperInvariant(type[0])}{type[1..]} Potion",
        _ => null,
    };

    /// <summary>"Potion of Swiftness II", "Splash Potion of Healing", "Awkward Potion", "Water Bottle", "Potion of Fire Resistance (extended)".</summary>
    public static string Label(int form, string type)
    {
        string prefix = form switch { SplashPotion => "Splash ", LingeringPotion => "Lingering ", _ => "" };
        if (BaseLabel(form, type, prefix) is { } plain) return plain;
        bool strong = type.StartsWith("strong_", StringComparison.Ordinal), ext = type.StartsWith("long_", StringComparison.Ordinal);
        var core = strong ? type[7..] : ext ? type[5..] : type;
        var name = string.Join(' ', core.Split('_').Select(w => char.ToUpperInvariant(w[0]) + w[1..]));
        return $"{prefix}Potion of {name}{(strong ? " II" : ext ? " (extended)" : "")}";
    }
}
