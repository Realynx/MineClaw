using System.Text.Json.Nodes;
using MineClaw.Data;
using MineClaw.Data.Versioning;

namespace MineClaw.Hive.Story;

/// <summary>One carried or worn stack as the director sees a pawn's gear: its inventory slot (5..8 armour, 9..44 bag and hotbar),
/// the item, its damage (durability used) and its enchantments by name (1.12.2).</summary>
public sealed record GearSlot(int Slot, int Id, int Meta, int Count, IReadOnlyDictionary<string, int>? Ench = null)
{
    public bool Enchanted => Ench is { Count: > 0 };
    public bool Has(string? wish) => string.IsNullOrEmpty(wish) || wish == "none" ? !Enchanted
        : wish == "any" ? Enchanted : Ench?.GetValueOrDefault(wish) > 0;
}

/// <summary>
/// Tools and gear for Story Mode (docs/web/story-mode.md "Tools and gear"), from the version's own data: what each block needs to
/// drop anything (the minimum tool and tier: stone needs a wooden pickaxe, iron ore a stone one, diamonds an iron one, obsidian a
/// diamond one), what speeds it up, what keeps it (shears for leaves), the material groups the rules editor offers, and how gear
/// items rank (weapons, armour, tool tiers, durability). Item ids for tools and armour are the same in b1.8.1 and 1.12.2.
/// </summary>
public static class StoryTools
{
    public static readonly IReadOnlyList<string> Kinds = ["pickaxe", "axe", "shovel", "hoe", "shears", "sword"];
    /// <summary>Harvest tiers, by harvest level 0..3 (gold harvests like wood).</summary>
    public static readonly IReadOnlyList<string> Tiers = ["wood", "stone", "iron", "diamond"];
    public static readonly IReadOnlyList<string> Enchants = ["fortune", "silk_touch", "any"];

    /// <summary>Durability left below which a tool counts as worn: swapped for a fresh one when there is one.</summary>
    public const double WornBelow = 0.10;

    public static int Level(string? tier) => tier == null ? -1 : Tiers.ToList().IndexOf(tier.Trim().ToLowerInvariant()) is var i and >= 0 ? i
        : tier.Trim().Equals("gold", StringComparison.OrdinalIgnoreCase) || tier.Trim().Equals("wooden", StringComparison.OrdinalIgnoreCase) ? 0 : -1;
    public static string? TierName(int level) => level is >= 0 and <= 3 ? Tiers[level] : null;

    public static ToolType? Kind(string? name) => name?.Trim().ToLowerInvariant() switch
    {
        "pickaxe" => ToolType.Pickaxe, "axe" => ToolType.Axe, "shovel" => ToolType.Shovel, "hoe" => ToolType.Hoe, "shears" => ToolType.Shears,
        "sword" => ToolType.Sword, _ => null,
    };
    public static string KindName(ToolType t) => t.ToString().ToLowerInvariant();

    // ---------------------------------------------------------------- item ids (shared by both versions)

    private static readonly Dictionary<(ToolType, int), int> ToolIds = new()
    {
        [(ToolType.Sword, 0)] = 268, [(ToolType.Shovel, 0)] = 269, [(ToolType.Pickaxe, 0)] = 270, [(ToolType.Axe, 0)] = 271, [(ToolType.Hoe, 0)] = 290,
        [(ToolType.Sword, 1)] = 272, [(ToolType.Shovel, 1)] = 273, [(ToolType.Pickaxe, 1)] = 274, [(ToolType.Axe, 1)] = 275, [(ToolType.Hoe, 1)] = 291,
        [(ToolType.Sword, 2)] = 267, [(ToolType.Shovel, 2)] = 256, [(ToolType.Pickaxe, 2)] = 257, [(ToolType.Axe, 2)] = 258, [(ToolType.Hoe, 2)] = 292,
        [(ToolType.Sword, 3)] = 276, [(ToolType.Shovel, 3)] = 277, [(ToolType.Pickaxe, 3)] = 278, [(ToolType.Axe, 3)] = 279, [(ToolType.Hoe, 3)] = 293,
        [(ToolType.Shears, 0)] = 359,
    };

    /// <summary>The plain item of a tool kind at a harvest tier (wood/stone/iron/diamond; shears have one), or null.</summary>
    public static int? ItemFor(ToolType kind, int level) => ToolIds.TryGetValue((kind, kind == ToolType.Shears ? 0 : Math.Clamp(level, 0, 3)), out var id) ? id : null;

    /// <summary>Every tool and sword id → its kind and material tier (0 wood, 1 stone, 2 iron, 3 diamond, 4 gold; shears −1).</summary>
    private static readonly Dictionary<int, (ToolType Kind, int Tier)> ById = ToolIds.Where(kv => kv.Key.Item1 != ToolType.Shears)
        .ToDictionary(kv => kv.Value, kv => (kv.Key.Item1, kv.Key.Item2))
        .Concat(new Dictionary<int, (ToolType, int)>
        {
            [283] = (ToolType.Sword, 4), [284] = (ToolType.Shovel, 4), [285] = (ToolType.Pickaxe, 4), [286] = (ToolType.Axe, 4), [294] = (ToolType.Hoe, 4),
            [359] = (ToolType.Shears, -1),
        }).ToDictionary(kv => kv.Key, kv => kv.Value);

    public static ToolType ToolOf(IGameData d, int id) =>
        ById.TryGetValue(id, out var t) ? t.Kind : id > 0 && d.ItemExists(id) ? d.Item(id).Tool : ToolType.None;

    /// <summary>A tool's material tier (0 wood, 1 stone, 2 iron, 3 diamond, 4 gold), or −1.</summary>
    public static int MaterialTier(IGameData d, int id) =>
        ById.TryGetValue(id, out var t) ? t.Tier : id > 0 && d.ItemExists(id) ? d.Item(id).ToolTier : -1;

    /// <summary>A tool item's harvest level (gold = wood; shears 0), or -1.</summary>
    public static int LevelOf(IGameData d, int id)
    {
        var kind = ToolOf(d, id);
        return kind == ToolType.Shears ? 0 : kind == ToolType.None ? -1 : Tools.HarvestLevel(MaterialTier(d, id));
    }

    /// <summary>Armour slot of an item: head | chest | legs | feet, or null.</summary>
    public static string? ArmorSlot(int id) => id is >= 298 and <= 317 ? ((id - 298) % 4) switch { 0 => "head", 1 => "chest", 2 => "legs", _ => "feet" } : null;

    /// <summary>The inventory window slot (5..8) of an armour slot name.</summary>
    public static int ArmorWindowSlot(string slot) => slot switch { "head" => 5, "chest" => 6, "legs" => 7, _ => 8 };

    /// <summary>Armour material rank: leather 1, gold 2, chain 3, iron 4, diamond 5.</summary>
    public static int ArmorRank(int id) => id switch { >= 298 and <= 301 => 1, >= 314 and <= 317 => 2, >= 302 and <= 305 => 3, >= 306 and <= 309 => 4, >= 310 and <= 313 => 5, _ => 0 };

    /// <summary>The item's tier as gear reports it: wood | stone | iron | gold | diamond | leather | chain | null.</summary>
    public static string? ItemTier(IGameData d, int id)
    {
        if (ArmorSlot(id) != null) return ArmorRank(id) switch { 1 => "leather", 2 => "gold", 3 => "chain", 4 => "iron", _ => "diamond" };
        if (ToolOf(d, id) is ToolType.None or ToolType.Shears) return null;
        return MaterialTier(d, id) switch { 0 => "wood", 1 => "stone", 2 => "iron", 3 => "diamond", 4 => "gold", _ => null };
    }

    /// <summary>pickaxe | axe | shovel | hoe | shears | sword | bow | helmet | chestplate | leggings | boots | other.</summary>
    public static string GearKind(IGameData d, int id)
    {
        if (ArmorSlot(id) is { } a) return a switch { "head" => "helmet", "chest" => "chestplate", "legs" => "leggings", _ => "boots" };
        if (id == 261) return "bow";
        var t = ToolOf(d, id);
        return t == ToolType.None ? "other" : KindName(t);
    }

    public static bool IsTool(IGameData d, int id) => ToolOf(d, id) is ToolType.Pickaxe or ToolType.Axe or ToolType.Shovel or ToolType.Hoe or ToolType.Shears;
    public static bool IsWeapon(IGameData d, int id) => ToolOf(d, id) == ToolType.Sword || id == 261;
    public static bool IsGear(IGameData d, int id) => IsTool(d, id) || IsWeapon(d, id) || ArmorSlot(id) != null;

    /// <summary>Durability left (0..1), or null for an item without durability.</summary>
    public static double? Durability(IGameData d, int id, int meta) =>
        id > 0 && d.ItemExists(id) && d.Item(id).MaxDamage is var max and > 0 ? Math.Clamp(1 - meta / (double)max, 0, 1) : null;

    public static bool Worn(IGameData d, int id, int meta) => Durability(d, id, meta) is { } left && left < WornBelow;

    /// <summary>
    /// How good a melee weapon is (0 = not one). Swords by their attack damage (wood/gold 4, stone 5, iron 6, diamond 7), then axes
    /// (they hit hard but slowly on 1.12.2 and barely on b1.8.1), then a bow (ranged: only with arrows, and the melee engine doesn't
    /// draw it, so it ranks last).
    /// </summary>
    public static int WeaponScore(IGameData d, int id, string pref = "best")
    {
        var t = ToolOf(d, id);
        int dmg = AttackDamage(d, id);
        int s = t == ToolType.Sword ? 100 + dmg * 10 : t == ToolType.Axe ? 50 + dmg * 5 : id == 261 ? 20 : 0;
        return s > 0 ? s + PreferredBonus(t, id, pref) : s;
    }

    /// <summary>A sword's attack damage by its material: wood/gold 4, stone 5, iron 6, diamond 7.</summary>
    private static int AttackDamage(IGameData d, int id) => MaterialTier(d, id) switch { 0 or 4 => 4, 1 => 5, 2 => 6, 3 => 7, _ => 0 };

    /// <summary>The policy's preferred weapon kind ranks first.</summary>
    private static int PreferredBonus(ToolType t, int id, string pref) => pref switch
    {
        "axe" when t == ToolType.Axe => 200,
        "sword" when t == ToolType.Sword => 200,
        "bow" when id == 261 => 300,
        _ => 0,
    };

    /// <summary>"a stone pickaxe", "an iron sword", "shears", "a pickaxe" (no tier).</summary>
    public static string Describe(ToolType kind, int level)
    {
        if (kind == ToolType.Shears) return "shears";
        var k = KindName(kind);
        var tier = level switch { 0 => "wooden", 1 => "stone", 2 => "iron", 3 => "diamond", _ => null };
        if (tier == null) return "a " + k;
        return (tier == "iron" ? "an " : "a ") + tier + " " + k;
    }

    // ---------------------------------------------------------------- what a block needs

    /// <summary>
    /// What digging a block takes. <paramref name="Kind"/>: the tool that harvests it (None = the hand does); <paramref name="Floor"/>:
    /// the lowest harvest level that drops it (−1 = the hand); <paramref name="Fast"/>: the tool that digs it fastest; <paramref name="Keep"/>:
    /// a tool that keeps the block itself rather than what it drops (shears for leaves, vines and grass), or None.
    /// </summary>
    public sealed record Need(ToolType Kind, int Floor, ToolType Fast, ToolType Keep)
    {
        public bool Hand => Floor < 0;
        public string Text => Hand ? "the hand" : Describe(Kind, Floor);
    }

    private static readonly Dictionary<(string Version, int Id), Need> NeedCache = new();
    private static readonly HashSet<int> KeptByShears = [18, 161, 31, 32, 106, 175];

    public static Need For(IGameData d, int blockId)
    {
        if (blockId == 74) blockId = 73; // lit redstone ore is redstone ore
        lock (NeedCache)
            if (NeedCache.TryGetValue((d.VersionId, blockId), out var cached)) return cached;
        var b = d.BlockExists(blockId) ? d.Block(blockId) : null;
        var fast = b?.BestTool ?? ToolType.None;
        var kind = ToolType.None;
        int floor = -1;
        if (b != null && !d.CanHarvest(blockId, -1))
        {
            int best = int.MaxValue;
            var order = new[] { fast, ToolType.Pickaxe, ToolType.Shovel, ToolType.Axe, ToolType.Sword, ToolType.Shears }.Where(t => t != ToolType.None).Distinct();
            foreach (var t in order)
                for (int lvl = 0; lvl <= (t == ToolType.Shears ? 0 : 3); lvl++)
                    if (ItemFor(t, lvl) is { } id && d.ItemExists(id) && d.CanHarvest(blockId, id))
                    {
                        if (lvl < best) { best = lvl; kind = t; }
                        break;
                    }
            floor = best == int.MaxValue ? 0 : best;
            if (kind == ToolType.None) kind = fast;
        }
        var need = new Need(kind, floor, fast, KeptByShears.Contains(blockId) ? ToolType.Shears : ToolType.None);
        lock (NeedCache) NeedCache[(d.VersionId, blockId)] = need;
        return need;
    }

    // ---------------------------------------------------------------- material groups (the rules editor)

    public sealed record Group(string Id, string Label, IReadOnlyList<int> Members);

    public static readonly IReadOnlyList<Group> Groups =
    [
        new("group:ores", "Ores", [16, 15, 14, 21, 73, 56, 129, 153]),
        new("group:stone", "Stone", [1, 4, 48, 24, 179, 87, 121, 49]),
        new("group:soil", "Dirt, sand & gravel", [2, 3, 12, 13, 82, 88, 110, 60, 78, 80]),
        new("group:logs", "Logs", [17, 162]),
        new("group:leaves", "Leaves", [18, 161]),
        new("group:wool", "Wool", [35]),
        new("group:web", "Cobweb", [30]),
    ];

    /// <summary>The group a block belongs to, or null.</summary>
    public static Group? GroupOf(int blockId) => Groups.FirstOrDefault(g => g.Members.Contains(blockId == 74 ? 73 : blockId));

    /// <summary>Ores whose drops Fortune multiplies (coal, lapis, redstone, diamond, emerald, quartz; not iron or gold ore).</summary>
    public static readonly HashSet<int> FortuneOres = [16, 21, 73, 74, 56, 129, 153];

    /// <summary>The block a material id names (a block name or legacy id), or null.</summary>
    public static int? BlockOf(IGameData d, string material)
    {
        var m = material.Trim();
        if (int.TryParse(m, out var id)) return d.BlockExists(id) ? id : null;
        return d.BlockByName(m) is { } b ? b.Id : null;
    }

    /// <summary>The floor (lowest harvest level) a material asks for: a block's own, a group's lowest member's (−1 = the hand).</summary>
    public static int FloorOf(IGameData d, string material)
    {
        if (Groups.FirstOrDefault(g => g.Id == material) is { } grp)
            return grp.Members.Where(d.BlockExists).Select(m => For(d, m).Floor).DefaultIfEmpty(-1).Min();
        return BlockOf(d, material) is { } b ? For(d, b).Floor : -1;
    }

    /// <summary>The rules editor's catalog: each group (its lowest member's floor), then its members with their own floors.</summary>
    public static JsonArray Catalog(IGameData d)
    {
        var arr = new JsonArray();
        foreach (var g in Groups)
        {
            var members = g.Members.Where(d.BlockExists).ToList();
            if (members.Count == 0) continue;
            var needs = members.Select(m => For(d, m)).ToList();
            var tool = needs.Select(n => n.Hand ? n.Fast : n.Kind).FirstOrDefault(t => t != ToolType.None);
            int floor = needs.Min(n => n.Floor);
            var keep = needs.Select(n => n.Keep).FirstOrDefault(k => k != ToolType.None);
            arr.Add(new JsonObject
            {
                ["id"] = g.Id, ["label"] = g.Label, ["group"] = null, ["tool"] = tool == ToolType.None ? null : KindName(tool), ["floor"] = TierName(floor),
                ["keep_tool"] = keep == ToolType.None ? null : KindName(keep),
                ["members"] = new JsonArray(members.Select(m => (JsonNode)d.Block(m).Name).ToArray()),
            });
            foreach (var (m, n) in members.Zip(needs))
            {
                var t = n.Hand ? n.Fast : n.Kind;
                arr.Add(new JsonObject
                {
                    ["id"] = d.Block(m).Name, ["label"] = d.Block(m).DisplayName, ["group"] = g.Id, ["tool"] = t == ToolType.None ? null : KindName(t),
                    ["floor"] = TierName(n.Floor), ["keep_tool"] = n.Keep == ToolType.None ? null : KindName(n.Keep), ["members"] = null,
                });
            }
        }
        return arr;
    }

    /// <summary>The tool kinds a work type uses (a pawn's policy "tools by work").</summary>
    public static IReadOnlyList<ToolType> ForWork(string work) => work switch
    {
        WorkTypes.Mine => [ToolType.Pickaxe, ToolType.Shovel],
        WorkTypes.Chop => [ToolType.Axe],
        WorkTypes.Farm => [ToolType.Hoe],
        WorkTypes.Build => [ToolType.Pickaxe, ToolType.Axe],
        _ => [],
    };
}
