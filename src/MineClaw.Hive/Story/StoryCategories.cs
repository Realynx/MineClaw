using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MineClaw.Data.Versioning;

namespace MineClaw.Hive.Story;

/// <summary>One node of the storage category tree (a leaf holds items; a branch its children's).</summary>
public sealed class Category
{
    public string Id { get; init; } = "";
    public string Label { get; init; } = "";
    public List<Category> Children { get; } = new();
    public List<ItemKey> Items { get; } = new();

    public IEnumerable<ItemKey> All() => Items.Concat(Children.SelectMany(c => c.All()));

    public JsonObject Json() => new()
    {
        ["id"] = Id, ["label"] = Label,
        ["children"] = new JsonArray(Children.Select(c => (JsonNode)c.Json()).ToArray()),
        ["items"] = new JsonArray(Items.Select(i => (JsonNode)i.Json()).ToArray()),
    };
}

/// <summary>
/// The storage categories for one game version, built from its item data plus the small curated name rules below: every item
/// (and every dye colour) belongs to exactly one leaf, whatever the version, so a chest filter written as categories is right on
/// b1.8.1 and 1.12.2 alike. Items no rule names go to Misc.
/// </summary>
public sealed class StoryCategories
{
    // (leaf id, parent id, parent label, leaf label, name rule). First match wins; order matters (cooked_ before raw meat...).
    private static readonly (string Leaf, string Parent, string ParentLabel, string Label, string Rule)[] Rules =
    [
        ("food_meals", "food", "Food", "Cooked meals", @"^(cooked_\w+|bread|mushroom_stew|rabbit_stew|beetroot_soup|cake|cookie|pumpkin_pie|golden_apple|golden_carrot|baked_potato)$"),
        ("food_fish", "food", "Food", "Fish", @"^(fish|cod|salmon|pufferfish|clownfish|tropical_fish)$"),
        ("food_raw_meat", "food", "Food", "Raw meat", @"^(porkchop|beef|chicken|mutton|rabbit|raw_\w+)$"),
        ("food_produce", "food", "Food", "Crops & produce", @"^(apple|wheat|carrot|potato|poisonous_potato|beetroot|melon|speckled_melon|sugar|egg|chorus_fruit|milk_bucket)$"),
        ("plants_seeds", "plants", "Plants & farming", "Seeds", @"(_seeds|^nether_wart)$"),
        ("plants_saplings", "plants", "Plants & farming", "Saplings", @"^sapling$"),
        ("plants_flowers", "plants", "Plants & farming", "Flowers & plants", @"^(yellow_flower|red_flower|double_plant|tallgrass|deadbush|vine|waterlily|brown_mushroom|red_mushroom|cactus|sugar_cane|reeds|pumpkin|melon_block|leaves2?|chorus_flower|chorus_plant)$"),
        ("wood_logs", "wood", "Wood", "Logs", @"^log2?$"),
        ("wood_planks", "wood", "Wood", "Planks", @"^planks$"),
        ("wood_sticks", "wood", "Wood", "Sticks", @"^stick$"),
        ("ores", "ores_metals", "Ores & metals", "Ores", @"_ore$"),
        ("ingots", "ores_metals", "Ores & metals", "Ingots", @"_ingot$"),
        ("nuggets", "ores_metals", "Ores & metals", "Nuggets", @"_nugget$"),
        ("metal_blocks", "ores_metals", "Ores & metals", "Metal & gem blocks", @"^(iron|gold|diamond|emerald|lapis|redstone|coal|quartz)_block$"),
        ("gems", "gems_minerals", "Gems & minerals", "Gems & minerals", @"^(coal|diamond|emerald|redstone|quartz|glowstone_dust|prismarine_shard|prismarine_crystals|flint)$"),
        ("tools", "tools", "Tools", "Tools", @"(_pickaxe|_axe|_shovel|_hoe)$|^(shears|flint_and_steel|fishing_rod|bucket|water_bucket|lava_bucket|compass|clock|lead|name_tag|carrot_on_a_stick)$"),
        ("weapons", "weapons", "Weapons", "Weapons", @"(_sword)$|^(bow|arrow|spectral_arrow|tipped_arrow|shield)$"),
        ("armour", "armour", "Armour", "Armour", @"(_helmet|_chestplate|_leggings|_boots|_horse_armor)$|^elytra$"),
        ("mob_drops", "mob_drops", "Mob drops", "Mob drops", @"^(bone|string|gunpowder|rotten_flesh|spider_eye|fermented_spider_eye|ender_pearl|ender_eye|leather|feather|slime_ball|blaze_rod|blaze_powder|ghast_tear|magma_cream|rabbit_hide|rabbit_foot|shulker_shell)$"),
        ("redstone_mech", "redstone_mech", "Redstone & mechanisms", "Redstone & mechanisms", @"(_rail|rail|_button|_pressure_plate|piston|repeater|comparator)$|^(redstone_torch|unlit_redstone_torch|lever|dispenser|dropper|hopper|observer|tripwire_hook|daylight_detector|daylight_detector_inverted|redstone_lamp|lit_redstone_lamp|tnt|noteblock|trapped_chest|redstone_wire|tripwire)$"),
        ("stone_glass", "stone_building", "Stone & building", "Glass", @"glass"),
        ("stone_slabs", "stone_building", "Stone & building", "Slabs, stairs & fences", @"(slab|_stairs|fence|fence_gate|cobblestone_wall)"),
        ("stone_bricks", "stone_building", "Stone & building", "Bricks & clay", @"^(brick|brick_block|nether_brick|red_nether_brick|netherbrick|hardened_clay|stained_hardened_clay|\w+_terracotta|concrete|concrete_powder)$"),
        ("stone_wool", "stone_building", "Stone & building", "Wool & carpet", @"^(wool|carpet)$"),
        ("stone_stone", "stone_building", "Stone & building", "Stone", @"^(stone|cobblestone|mossy_cobblestone|sandstone|red_sandstone|netherrack|end_stone|obsidian|stonebrick|stone_bricks|prismarine|purpur_block|purpur_pillar|quartz_block|monster_egg)$"),
        ("stone_soil", "stone_building", "Stone & building", "Dirt, sand & gravel", @"^(dirt|grass|sand|gravel|clay|soul_sand|mycelium|farmland|snow|snow_layer|ice|packed_ice|grass_path)$"),
    ];

    private static readonly string[] TopOrder = ["food", "plants", "wood", "stone_building", "ores_metals", "gems_minerals", "tools", "weapons", "armour", "mob_drops", "redstone_mech", "misc"];

    private readonly Dictionary<string, Category> _byId = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(int, int), string> _leafOf = new();
    private readonly Dictionary<int, string> _leafAnyMeta = new();

    public IReadOnlyList<Category> Roots { get; }
    public string Version { get; }

    private StoryCategories(IGameData d)
    {
        Version = d.VersionId;
        var tops = new Dictionary<string, Category>();
        var rx = Rules.Select(r => (r, new Regex(r.Rule, RegexOptions.CultureInvariant))).ToList();
        var misc = Leaf(tops, "misc", "misc", "Misc", "Misc");
        for (int id = 1; id < 4096; id++)
        {
            if (!d.ItemExists(id)) continue;
            var name = d.Item(id).Name.ToLowerInvariant();
            if (name is "air" or "") continue;
            if (name == "dye") { PutDye(tops, id, misc); continue; } // one item, sixteen things: ink sac, cocoa, lapis, bone meal and colours
            var hit = rx.FirstOrDefault(x => x.Item2.IsMatch(name));
            Put(hit.r.Leaf != null ? Leaf(tops, hit.r.Leaf, hit.r.Parent, hit.r.ParentLabel, hit.r.Label) : misc, id, -1);
        }
        Roots = TopOrder.Where(tops.ContainsKey).Select(t => tops[t]).ToList();
    }

    /// <summary>A leaf category under its top one (both made on first use; a one-leaf top category is its own leaf).</summary>
    private Category Leaf(Dictionary<string, Category> tops, string leaf, string parent, string parentLabel, string label)
    {
        if (_byId.TryGetValue(leaf, out var c)) return c;
        if (!tops.TryGetValue(parent, out var top)) _byId[parent] = tops[parent] = top = new Category { Id = parent, Label = parentLabel };
        c = new Category { Id = leaf, Label = label };
        if (leaf == parent) return top;
        top.Children.Add(c);
        return _byId[leaf] = c;
    }

    private void Put(Category c, int id, int meta)
    {
        c.Items.Add(new ItemKey(id, meta));
        if (meta < 0) _leafAnyMeta[id] = c.Id; else _leafOf[(id, meta)] = c.Id;
    }

    /// <summary>Dye by meta: the ink sac a mob drop, cocoa produce, lapis a gem, the rest misc.</summary>
    private void PutDye(Dictionary<string, Category> tops, int id, Category misc)
    {
        for (int m = 0; m < 16; m++)
            Put(m switch
            {
                0 => Leaf(tops, "mob_drops", "mob_drops", "Mob drops", "Mob drops"), 3 => Leaf(tops, "food_produce", "food", "Food", "Crops & produce"),
                4 => Leaf(tops, "gems", "gems_minerals", "Gems & minerals", "Gems & minerals"), _ => misc,
            }, id, m);
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, StoryCategories> Cache = new();

    /// <summary>The tree for a version: built once (no closure per call: the haulers ask for it per drop and chest).</summary>
    public static StoryCategories For(IGameData d) =>
        Cache.TryGetValue(d.VersionId, out var c) ? c : Cache.GetOrAdd(d.VersionId, static (_, dd) => new StoryCategories(dd), d);

    public Category? Get(string id) => _byId.GetValueOrDefault(id);

    /// <summary>The leaf an item belongs to.</summary>
    public string LeafOf(int id, int meta) => _leafOf.TryGetValue((id, meta), out var l) ? l : _leafAnyMeta.TryGetValue(id, out var a) ? a : "misc";

    /// <summary>Is the item in this category (any depth)?</summary>
    public bool In(string category, int id, int meta)
    {
        if (category == "*") return true;
        var leaf = LeafOf(id, meta);
        if (leaf.Equals(category, StringComparison.OrdinalIgnoreCase)) return true;
        if (!_byId.TryGetValue(category, out var c)) return false;
        foreach (var ch in c.Children)
            if (ch.Id.Equals(leaf, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>A filter entry: "*", a category id, "id" or "id:meta".</summary>
    public bool Covers(string entry, int id, int meta)
    {
        // Parsed by hand: a regex match per entry per stack per chest was most of a haul pick's garbage (live 11-04).
        var e = entry.AsSpan().Trim();
        if (e is "*") return true;
        if (IdEntry(e) is { } x) return x.Id == id && (x.Meta is not { } em || em < 0 || em == meta);
        return In(e.Length == entry.Length ? entry : e.ToString(), id, meta);
    }

    /// <summary>"id" or "id:meta" (meta may be negative: any), else null.</summary>
    private static (int Id, int? Meta)? IdEntry(ReadOnlySpan<char> e)
    {
        static bool Digits(ReadOnlySpan<char> s)
        {
            if (s.IsEmpty) return false;
            foreach (var ch in s) if (!char.IsAsciiDigit(ch)) return false;
            return true;
        }
        int colon = e.IndexOf(':');
        var head = colon < 0 ? e : e[..colon];
        if (!Digits(head) || !int.TryParse(head, out int id)) return null;
        if (colon < 0) return (id, null);
        var tail = e[(colon + 1)..];
        if (!Digits(tail.StartsWith("-") ? tail[1..] : tail) || !int.TryParse(tail, out int meta)) return null;
        return (id, meta);
    }

    /// <summary>Does any entry cover the item (no closure: called per stack per chest)?</summary>
    public bool AnyCovers(List<string> entries, int id, int meta)
    {
        foreach (var x in entries)
            if (Covers(x, id, meta)) return true;
        return false;
    }

    /// <summary>allow (any entry covers it) and not deny (no entry covers it).</summary>
    public bool Accepts(StorageFilter f, int id, int meta) => AnyCovers(f.Allow, id, meta) && !AnyCovers(f.Deny, id, meta);

    /// <summary>A filter entry is known (a category, "*", or an item id).</summary>
    public bool Valid(string entry) => entry == "*" || _byId.ContainsKey(entry) || Regex.IsMatch(entry, @"^\d+(:-?\d+)?$");

    public JsonArray Json() => new(Roots.Select(r => (JsonNode)r.Json()).ToArray());
}
