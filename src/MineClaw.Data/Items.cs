namespace MineClaw.Data;

/// <summary>
/// Item registry for Beta 1.8.1: block items (ids 1-109), items 256-368 and records 2256/2257.
/// Stack sizes, durabilities and tool tiers were read from the b1.8.1 client jar.
/// </summary>
/// <remarks>
/// b1.8.1 rules: tools/weapons/armor/buckets (including empty)/signs/doors/minecarts/boats/saddle/beds/cakes/
/// mushroom stew/bow/fishing rod/map/shears/records stack to 1; snowballs and eggs to 16; everything else to 64.
/// The bow has no durability yet. Gold armor has the same durability as chain armor.
/// </remarks>
public static partial class Items
{
    public const int
        IronShovel = 256, IronPickaxe = 257, IronAxe = 258, FlintAndSteel = 259, Apple = 260, Bow = 261, Arrow = 262,
        Coal = 263, Diamond = 264, IronIngot = 265, GoldIngot = 266, IronSword = 267, WoodenSword = 268,
        WoodenShovel = 269, WoodenPickaxe = 270, WoodenAxe = 271, StoneSword = 272, StoneShovel = 273,
        StonePickaxe = 274, StoneAxe = 275, DiamondSword = 276, DiamondShovel = 277, DiamondPickaxe = 278,
        DiamondAxe = 279, Stick = 280, Bowl = 281, MushroomStew = 282, GoldenSword = 283, GoldenShovel = 284,
        GoldenPickaxe = 285, GoldenAxe = 286, String = 287, Feather = 288, Gunpowder = 289, WoodenHoe = 290,
        StoneHoe = 291, IronHoe = 292, DiamondHoe = 293, GoldenHoe = 294, Seeds = 295, Wheat = 296, Bread = 297,
        LeatherHelmet = 298, LeatherChestplate = 299, LeatherLeggings = 300, LeatherBoots = 301,
        ChainHelmet = 302, ChainChestplate = 303, ChainLeggings = 304, ChainBoots = 305,
        IronHelmet = 306, IronChestplate = 307, IronLeggings = 308, IronBoots = 309,
        DiamondHelmet = 310, DiamondChestplate = 311, DiamondLeggings = 312, DiamondBoots = 313,
        GoldenHelmet = 314, GoldenChestplate = 315, GoldenLeggings = 316, GoldenBoots = 317,
        Flint = 318, Porkchop = 319, CookedPorkchop = 320, Painting = 321, GoldenApple = 322, Sign = 323,
        WoodenDoor = 324, Bucket = 325, WaterBucket = 326, LavaBucket = 327, Minecart = 328, Saddle = 329,
        IronDoor = 330, Redstone = 331, Snowball = 332, Boat = 333, Leather = 334, MilkBucket = 335, Brick = 336,
        ClayBall = 337, SugarCane = 338, Paper = 339, Book = 340, Slimeball = 341, ChestMinecart = 342,
        FurnaceMinecart = 343, Egg = 344, Compass = 345, FishingRod = 346, Clock = 347, GlowstoneDust = 348,
        RawFish = 349, CookedFish = 350, Dye = 351, Bone = 352, Sugar = 353, Cake = 354, Bed = 355, Repeater = 356,
        Cookie = 357, Map = 358, Shears = 359, MelonSlice = 360, PumpkinSeeds = 361, MelonSeeds = 362,
        RawBeef = 363, Steak = 364, RawChicken = 365, CookedChicken = 366, RottenFlesh = 367, EnderPearl = 368,
        Record13 = 2256, RecordCat = 2257;

    /// <summary>Dye (351) damage values.</summary>
    public const int InkSac = 0, RoseRed = 1, CactusGreen = 2, CocoaBeans = 3, LapisLazuli = 4, PurpleDye = 5,
        CyanDye = 6, LightGrayDye = 7, GrayDye = 8, PinkDye = 9, LimeDye = 10, DandelionYellow = 11,
        LightBlueDye = 12, MagentaDye = 13, OrangeDye = 14, BoneMeal = 15;

    private static readonly Dictionary<int, ItemInfo> _byId = Build();
    private static readonly ItemInfo[] _all = _byId.Values.OrderBy(i => i.Id).ToArray();
    private static Dictionary<string, ItemInfo>? _byNameCache;
    private static Dictionary<string, ItemInfo> _byName => _byNameCache ??= BuildNames();

    public static IReadOnlyList<ItemInfo> All => _all;

    /// <summary>Returns the item for an id; unknown ids return a placeholder (stack 64).</summary>
    public static ItemInfo Get(int id) =>
        _byId.TryGetValue(id, out var i) ? i : new ItemInfo(id, $"unknown_{id}", $"Unknown ({id})", 64, 0, ToolType.None, -1, id is > 0 and < 256);

    public static bool Exists(int id) => _byId.ContainsKey(id);

    /// <summary>
    /// Forgiving lookup (case, spaces/underscores, aliases, plurals, "minecraft:" prefix, numeric ids).
    /// Where a block and an item share a name ("bed", "cake", "sugar_cane", "redstone"...) the real item wins.
    /// Variant names like "charcoal", "bone_meal" or "red_wool" resolve to their base item; use <see cref="ByNameWithDamage"/> to get the damage too.
    /// </summary>
    public static ItemInfo? ByName(string name) => ByNameWithDamage(name)?.Item;

    /// <summary>Like <see cref="ByName"/> but also returns the metadata for variant names (-1 when the name does not imply one).</summary>
    public static (ItemInfo Item, int Damage)? ByNameWithDamage(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var key = Blocks.Normalize(name);
        if (Variants.TryGetValue(key, out var v)) return (Get(v.Id), v.Damage);
        if (_byName.TryGetValue(key, out var i)) return (i, -1);
        foreach (var suffix in new[] { "s", "es" })
            if (key.EndsWith(suffix))
            {
                var k2 = key[..^suffix.Length];
                if (Variants.TryGetValue(k2, out v)) return (Get(v.Id), v.Damage);
                if (_byName.TryGetValue(k2, out i)) return (i, -1);
            }
        if (int.TryParse(key, out var id) && Exists(id)) return (_byId[id], -1);
        if (Blocks.ByName(name) is { } b && Exists(b.Id)) return (_byId[b.Id], -1);
        // "20 cobble slabs", "cobble slab x20", "a red wool": drop the amount and try again.
        if (ParseAmount(name) is { Count: not null } a && a.Name != name.Trim()) return ByNameWithDamage(a.Name);
        return null;
    }

    /// <summary>Splits a leading/trailing amount off an item phrase: "20 cobble slabs" → (20, "cobble slabs"),
    /// "torch x16" / "16x torch" → (16, "torch"), "a bucket" → (1, "bucket"); no amount → (null, name).</summary>
    public static (int? Count, string Name) ParseAmount(string name)
    {
        var s = name.Trim();
        var m = AmountRx().Match(s);
        if (!m.Success) return (null, s);
        string num = m.Groups["n1"].Success ? m.Groups["n1"].Value : m.Groups["n2"].Value;
        string rest = (m.Groups["r1"].Success ? m.Groups["r1"].Value : m.Groups["r2"].Value).Trim();
        if (rest.Length == 0) return (null, s);
        int? n = num.ToLowerInvariant() is "a" or "an" or "one" ? 1 : int.TryParse(num, out var v) && v > 0 ? v : null;
        return (n, rest);
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^(?:(?<n1>\d+|a|an|one)\s*x?\s+(?<r1>.+)|(?<r2>.+?)\s+x?\s*(?<n2>\d+))$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex AmountRx();

    /// <summary>Snake-case name that keeps the variant ("cobblestone_slab", "red_wool", "birch_wood"); it resolves back to
    /// the same (id, meta) through <see cref="ByNameWithDamage"/>. Ids without variants give their plain name.</summary>
    public static string VariantName(int id, int meta)
    {
        bool variant = id is Blocks.Slab or Blocks.Wool or Blocks.Log or Blocks.Leaves or Blocks.Sapling or Blocks.StoneBricks
            or Blocks.TallGrass or Dye or Coal;
        if (!variant) return Exists(id) ? Get(id).Name : Blocks.Get(id).Name;
        return DisplayName(id, meta).ToLowerInvariant().Replace(' ', '_');
    }

    public static bool IsTool(int id) => Get(id).Tool != ToolType.None;

    /// <summary>Wool colour names indexed by wool metadata (dye metadata is 15 - wool metadata).</summary>
    public static readonly string[] WoolColors =
        ["White", "Orange", "Magenta", "Light Blue", "Yellow", "Lime", "Pink", "Gray", "Light Gray", "Cyan", "Purple", "Blue", "Brown", "Green", "Red", "Black"];

    /// <summary>Dye names indexed by dye (351) metadata.</summary>
    public static readonly string[] DyeNames =
        ["Ink Sac", "Rose Red", "Cactus Green", "Cocoa Beans", "Lapis Lazuli", "Purple Dye", "Cyan Dye", "Light Gray Dye",
         "Gray Dye", "Pink Dye", "Lime Dye", "Dandelion Yellow", "Light Blue Dye", "Magenta Dye", "Orange Dye", "Bone Meal"];

    private static readonly string[] WoodTypes = ["Oak", "Spruce", "Birch"];
    private static readonly string[] SlabTypes = ["Stone", "Sandstone", "Wooden", "Cobblestone", "Brick", "Stone Brick"];

    /// <summary>Human-readable name including metadata variants (wool colours, dyes, wood types, slabs, coal vs charcoal...).</summary>
    public static string DisplayName(int id, int damage = 0)
    {
        if (id <= 0) return id == 0 ? "Air" : "(empty)";
        int m = damage < 0 ? 0 : damage;
        switch (id)
        {
            case Blocks.Wool: return $"{WoolColors[m & 15]} Wool";
            case Dye: return DyeNames[m & 15];
            case Coal: return m == 1 ? "Charcoal" : "Coal";
            case Blocks.Log: return (m & 3) switch { 1 => "Spruce Wood", 2 => "Birch Wood", _ => "Oak Wood" };
            case Blocks.Leaves: return $"{WoodTypes[Math.Min(m & 3, 2)]} Leaves";
            case Blocks.Sapling: return $"{WoodTypes[Math.Min(m & 3, 2)]} Sapling";
            case Blocks.Slab: case Blocks.DoubleSlab:
                return $"{SlabTypes[Math.Min(m & 7, 5)]} {(id == Blocks.Slab ? "Slab" : "Double Slab")}";
            case Blocks.TallGrass: return (m & 3) switch { 0 => "Dead Shrub", 2 => "Fern", _ => "Tall Grass" };
            case Blocks.StoneBricks: return (m & 3) switch { 1 => "Mossy Stone Bricks", 2 => "Cracked Stone Bricks", _ => "Stone Bricks" };
            case Blocks.MonsterEgg: return (m & 3) switch { 1 => "Silverfish Cobblestone", 2 => "Silverfish Stone Bricks", _ => "Silverfish Stone" };
        }
        if (id < 256 && !_byId.ContainsKey(id)) return Blocks.Get(id).DisplayName;
        var info = Get(id);
        if (info.MaxDamage > 0 && damage > 0) return $"{info.DisplayName} ({info.MaxDamage - damage}/{info.MaxDamage})";
        return info.DisplayName;
    }

    /// <summary>Food value: hunger points restored (half-shanks) and saturation modifier. Null if not edible.</summary>
    public static (int Hunger, float Saturation)? Food(int id) => id switch
    {
        Apple => (4, 0.3f), MushroomStew => (8, 0.6f), Bread => (5, 0.6f), Porkchop => (3, 0.3f), CookedPorkchop => (8, 0.8f),
        GoldenApple => (10, 1.2f), RawFish => (2, 0.3f), CookedFish => (5, 0.6f), Cookie => (1, 0.1f), MelonSlice => (2, 0.3f),
        RawBeef => (3, 0.3f), Steak => (8, 0.8f), RawChicken => (2, 0.3f), CookedChicken => (6, 0.6f), RottenFlesh => (4, 0.1f),
        _ => null,
    };

    /// <summary>Foods with a chance of the Hunger effect: raw chicken 30%, rotten flesh 80%.</summary>
    public static float FoodPoisonChance(int id) => id switch { RawChicken => 0.3f, RottenFlesh => 0.8f, _ => 0f };

    /// <summary>Block ids that are not obtainable as an item in b1.8.1 (placed-only forms). Their item form is listed alongside.</summary>
    public static int ItemFormOf(int blockId) => blockId switch
    {
        Blocks.FlowingWater or Blocks.Water => WaterBucket,
        Blocks.FlowingLava or Blocks.Lava => LavaBucket,
        Blocks.Bed => Bed, Blocks.RedstoneWire => Redstone, Blocks.Crops => Seeds, Blocks.LitFurnace => Blocks.Furnace,
        Blocks.SignPost or Blocks.WallSign => Sign, Blocks.WoodenDoor => WoodenDoor, Blocks.IronDoor => IronDoor,
        Blocks.LitRedstoneOre => Blocks.RedstoneOre, Blocks.RedstoneTorchOff => Blocks.RedstoneTorch,
        Blocks.SugarCane => SugarCane, Blocks.Cake => Cake, Blocks.RepeaterOff or Blocks.RepeaterOn => Repeater,
        Blocks.PumpkinStem => PumpkinSeeds, Blocks.MelonStem => MelonSeeds, Blocks.Grass => Blocks.Dirt,
        _ => blockId,
    };

    private static readonly HashSet<int> NonItemBlocks =
    [
        Blocks.Air, Blocks.FlowingWater, Blocks.Water, Blocks.FlowingLava, Blocks.Lava, Blocks.Bed, Blocks.PistonHead,
        Blocks.PistonMoving, Blocks.Fire, Blocks.RedstoneWire, Blocks.Crops, Blocks.LitFurnace, Blocks.SignPost,
        Blocks.WoodenDoor, Blocks.WallSign, Blocks.IronDoor, Blocks.LitRedstoneOre, Blocks.RedstoneTorchOff,
        Blocks.SugarCane, Blocks.Portal, Blocks.Cake, Blocks.RepeaterOff, Blocks.RepeaterOn, Blocks.PumpkinStem, Blocks.MelonStem,
    ];

    /// <summary>True if the block id can exist as an inventory item in survival-ish play (e.g. not water, bed block, door block).</summary>
    public static bool IsObtainableBlockItem(int blockId) => Blocks.Exists(blockId) && !NonItemBlocks.Contains(blockId);

    private static Dictionary<int, ItemInfo> Build()
    {
        var d = new Dictionary<int, ItemInfo>();
        foreach (var b in Blocks.All)
            if (b.Id > 0)
                d[b.Id] = new ItemInfo(b.Id, b.Name, b.DisplayName, 64, 0, ToolType.None, -1, true);

        void It(int id, string name, string display, int stack = 64, int dur = 0, ToolType tool = ToolType.None, int tier = -1) =>
            d[id] = new ItemInfo(id, name, display, stack, dur, tool, tier, false);

        string[] mat = ["wooden", "stone", "iron", "diamond", "golden"];
        string[] matD = ["Wooden", "Stone", "Iron", "Diamond", "Gold"];
        int[] dur = [59, 131, 250, 1561, 32];
        // tier index: 0 wood, 1 stone, 2 iron, 3 diamond, 4 gold
        int[][] ids =
        [
            [269, 270, 271, 268, 290], // wood: shovel, pickaxe, axe, sword, hoe
            [273, 274, 275, 272, 291],
            [256, 257, 258, 267, 292],
            [277, 278, 279, 276, 293],
            [284, 285, 286, 283, 294],
        ];
        (string N, string D, ToolType T)[] kinds =
        [
            ("shovel", "Shovel", ToolType.Shovel), ("pickaxe", "Pickaxe", ToolType.Pickaxe), ("axe", "Axe", ToolType.Axe),
            ("sword", "Sword", ToolType.Sword), ("hoe", "Hoe", ToolType.Hoe),
        ];
        for (int t = 0; t < 5; t++)
            for (int k = 0; k < 5; k++)
                It(ids[t][k], $"{mat[t]}_{kinds[k].N}", $"{matD[t]} {kinds[k].D}", 1, dur[t], kinds[k].T, t);

        It(259, "flint_and_steel", "Flint and Steel", 1, 64);
        It(260, "apple", "Apple");
        It(261, "bow", "Bow", 1);
        It(262, "arrow", "Arrow");
        It(263, "coal", "Coal");
        It(264, "diamond", "Diamond");
        It(265, "iron_ingot", "Iron Ingot");
        It(266, "gold_ingot", "Gold Ingot");
        It(280, "stick", "Stick");
        It(281, "bowl", "Bowl");
        It(282, "mushroom_stew", "Mushroom Stew", 1);
        It(287, "string", "String");
        It(288, "feather", "Feather");
        It(289, "gunpowder", "Gunpowder");
        It(295, "wheat_seeds", "Seeds");
        It(296, "wheat", "Wheat");
        It(297, "bread", "Bread");

        // Armor: durability = base[slot] * 3 << materialShift (gold uses the chain shift).
        int[] armorBase = [11, 16, 15, 13];
        string[] slot = ["helmet", "chestplate", "leggings", "boots"];
        string[] slotD = ["Cap", "Tunic", "Pants", "Boots"];
        string[] slotD2 = ["Helmet", "Chestplate", "Leggings", "Boots"];
        (int First, string N, string D, int Shift)[] armors =
            [(298, "leather", "Leather", 0), (302, "chainmail", "Chain", 1), (306, "iron", "Iron", 2), (310, "diamond", "Diamond", 3), (314, "golden", "Gold", 1)];
        foreach (var (first, n, dn, shift) in armors)
            for (int s = 0; s < 4; s++)
                It(first + s, $"{n}_{slot[s]}", $"{dn} {(n == "leather" ? slotD[s] : slotD2[s])}", 1, armorBase[s] * 3 << shift);

        It(318, "flint", "Flint");
        It(319, "porkchop", "Raw Porkchop");
        It(320, "cooked_porkchop", "Cooked Porkchop");
        It(321, "painting", "Painting");
        It(322, "golden_apple", "Golden Apple");
        It(323, "sign", "Sign", 1);
        It(324, "wooden_door", "Wooden Door", 1);
        It(325, "bucket", "Bucket", 1);
        It(326, "water_bucket", "Water Bucket", 1);
        It(327, "lava_bucket", "Lava Bucket", 1);
        It(328, "minecart", "Minecart", 1);
        It(329, "saddle", "Saddle", 1);
        It(330, "iron_door", "Iron Door", 1);
        It(331, "redstone", "Redstone");
        It(332, "snowball", "Snowball", 16);
        It(333, "boat", "Boat", 1);
        It(334, "leather", "Leather");
        It(335, "milk_bucket", "Milk", 1);
        It(336, "brick", "Brick");
        It(337, "clay_ball", "Clay");
        It(338, "sugar_cane", "Sugar Canes");
        It(339, "paper", "Paper");
        It(340, "book", "Book");
        It(341, "slime_ball", "Slimeball");
        It(342, "chest_minecart", "Minecart with Chest", 1);
        It(343, "furnace_minecart", "Minecart with Furnace", 1);
        It(344, "egg", "Egg", 16);
        It(345, "compass", "Compass");
        It(346, "fishing_rod", "Fishing Rod", 1, 64);
        It(347, "clock", "Clock");
        It(348, "glowstone_dust", "Glowstone Dust");
        It(349, "fish", "Raw Fish");
        It(350, "cooked_fish", "Cooked Fish");
        It(351, "dye", "Dye");
        It(352, "bone", "Bone");
        It(353, "sugar", "Sugar");
        It(354, "cake", "Cake", 1);
        It(355, "bed", "Bed", 1);
        It(356, "repeater", "Redstone Repeater");
        It(357, "cookie", "Cookie");
        It(358, "map", "Map", 1);
        It(359, "shears", "Shears", 1, 238, ToolType.Shears);
        It(360, "melon", "Melon");
        It(361, "pumpkin_seeds", "Pumpkin Seeds");
        It(362, "melon_seeds", "Melon Seeds");
        It(363, "beef", "Raw Beef");
        It(364, "cooked_beef", "Steak");
        It(365, "chicken", "Raw Chicken");
        It(366, "cooked_chicken", "Cooked Chicken");
        It(367, "rotten_flesh", "Rotten Flesh");
        It(368, "ender_pearl", "Ender Pearl");
        It(2256, "record_13", "Music Disc (13)", 1);
        It(2257, "record_cat", "Music Disc (cat)", 1);
        return d;
    }

    private static Dictionary<string, ItemInfo> BuildNames()
    {
        var d = new Dictionary<string, ItemInfo>();
        // Blocks first, then items overwrite (items win name collisions).
        foreach (var i in _all.Where(i => i.IsBlock)) { d[Blocks.Normalize(i.Name)] = i; d.TryAdd(Blocks.Normalize(i.DisplayName), i); }
        foreach (var (alias, id) in Blocks.Aliases) d[Blocks.Normalize(alias)] = _byId[id];
        foreach (var i in _all.Where(i => !i.IsBlock)) d[Blocks.Normalize(i.Name)] = i;
        foreach (var i in _all.Where(i => !i.IsBlock)) d.TryAdd(Blocks.Normalize(i.DisplayName), i);
        foreach (var (alias, id) in ItemAliases) d[Blocks.Normalize(alias)] = _byId[id];
        return d;
    }

    private static readonly (string, int)[] ItemAliases =
    [
        ("wood_shovel", 269), ("wood_pickaxe", 270), ("wood_axe", 271), ("wood_sword", 268), ("wood_hoe", 290),
        ("gold_shovel", 284), ("gold_pickaxe", 285), ("gold_axe", 286), ("gold_sword", 283), ("gold_hoe", 294),
        ("gold_helmet", 314), ("gold_chestplate", 315), ("gold_leggings", 316), ("gold_boots", 317),
        ("chain_helmet", 302), ("chain_chestplate", 303), ("chain_leggings", 304), ("chain_boots", 305),
        ("seeds", 295), ("seed", 295), ("sulphur", 289), ("gun_powder", 289), ("raw_porkchop", 319), ("pork", 319),
        ("grilled_pork", 320), ("cooked_pork", 320), ("reeds", 338), ("sugarcane", 338), ("clay_balls", 337),
        ("slimeball", 341), ("raw_fish", 349), ("steak", 364), ("raw_beef", 363), ("raw_chicken", 365),
        ("melon_slice", 360), ("redstone_dust", 331), ("lighter", 259), ("flint_steel", 259), ("bucket_of_water", 326),
        ("bucket_of_lava", 327), ("milk", 335), ("door", 324), ("pearl", 368), ("pickaxe", 270), ("pick", 270),
        ("diode", 356), ("redstone_repeater", 356), ("wheat_seed", 295), ("watch", 347), ("glowdust", 348),
        ("dyes", 351), ("disc_13", 2256), ("disc_cat", 2257),
    ];

    /// <summary>Variant names → (id, damage).</summary>
    private static Dictionary<string, (int Id, int Damage)>? _variants;
    private static Dictionary<string, (int Id, int Damage)> Variants => _variants ??= BuildVariants();

    private static Dictionary<string, (int, int)> BuildVariants()
    {
        var v = new Dictionary<string, (int, int)>();
        void Add(string n, int id, int dmg) => v[Blocks.Normalize(n)] = (id, dmg);
        Add("charcoal", Coal, 1);
        for (int i = 0; i < 16; i++)
        {
            Add($"{WoolColors[i]} wool", Blocks.Wool, i);
            Add(DyeNames[i], Dye, i);
        }
        Add("light grey wool", Blocks.Wool, 8); Add("grey wool", Blocks.Wool, 7);
        Add("bonemeal", Dye, 15); Add("ink", Dye, 0); Add("cocoa", Dye, 3); Add("lapis", Dye, 4); Add("blue dye", Dye, 4);
        Add("red dye", Dye, 1); Add("green dye", Dye, 2); Add("yellow dye", Dye, 11); Add("black dye", Dye, 0);
        Add("white dye", Dye, 15); Add("brown dye", Dye, 3); Add("grey dye", Dye, 8); Add("light grey dye", Dye, 7);
        // Wood types: log/leaves/sapling meta 0 oak, 1 spruce (pine), 2 birch. b1.8.1 has ONE plank type (any log → planks
        // meta 0), so "<wood> planks" all mean plain planks. A bare "log"/"wood" stays any type.
        for (int i = 0; i < 3; i++)
        {
            var w = WoodTypes[i].ToLowerInvariant();
            Add($"{w} log", Blocks.Log, i); Add($"{w} wood", Blocks.Log, i);
            Add($"{w} leaves", Blocks.Leaves, i); Add($"{w} sapling", Blocks.Sapling, i);
            Add($"{w} planks", Blocks.Planks, -1); Add($"{w} plank", Blocks.Planks, -1);
        }
        Add("pine log", Blocks.Log, 1); Add("pine wood", Blocks.Log, 1); Add("pine sapling", Blocks.Sapling, 1); Add("pine leaves", Blocks.Leaves, 1);
        Add("pine planks", Blocks.Planks, -1);
        // Slabs (44): 0 stone (smooth, from stone), 1 sandstone, 2 wooden (planks), 3 cobblestone, 4 brick, 5 stone brick.
        for (int i = 0; i < SlabTypes.Length; i++) Add($"{SlabTypes[i]} slab", Blocks.Slab, i);
        Add("smooth stone slab", Blocks.Slab, 0); Add("smoothstone slab", Blocks.Slab, 0);
        Add("wood slab", Blocks.Slab, 2); Add("plank slab", Blocks.Slab, 2); Add("planks slab", Blocks.Slab, 2);
        Add("wooden plank slab", Blocks.Slab, 2); Add("wood plank slab", Blocks.Slab, 2); Add("oak slab", Blocks.Slab, 2);
        Add("cobble slab", Blocks.Slab, 3);
        Add("bricks slab", Blocks.Slab, 4); Add("stone bricks slab", Blocks.Slab, 5);
        Add("mossy stone bricks", Blocks.StoneBricks, 1); Add("cracked stone bricks", Blocks.StoneBricks, 2);
        Add("fern", Blocks.TallGrass, 2); Add("dead shrub", Blocks.TallGrass, 0);
        return v;
    }
}
