namespace MineClaw.Data;

/// <summary>
/// Block registry for Beta 1.8.1 (ids 0-109). Hardness values, materials and harvest rules were taken from the
/// b1.8.1 client jar (Block static initializer, Material flags, ItemPickaxe/ItemSpade/ItemAxe tables).
/// </summary>
/// <remarks>
/// Beta quirks worth knowing: glowstone is "rock" material (needs a pickaxe to drop anything); the axe only speeds up
/// planks, logs, bookshelves and chests; the pickaxe only speeds up its own list (not obsidian, bricks, furnaces...)
/// but can still harvest every rock/iron block. Flowing lava (10) has hardness 0 in the jar — liquids are never diggable.
/// </remarks>
public static class Blocks
{
    public const int
        Air = 0, Stone = 1, Grass = 2, Dirt = 3, Cobblestone = 4, Planks = 5, Sapling = 6, Bedrock = 7,
        FlowingWater = 8, Water = 9, FlowingLava = 10, Lava = 11, Sand = 12, Gravel = 13,
        GoldOre = 14, IronOre = 15, CoalOre = 16, Log = 17, Leaves = 18, Sponge = 19, Glass = 20,
        LapisOre = 21, LapisBlock = 22, Dispenser = 23, Sandstone = 24, NoteBlock = 25, Bed = 26,
        PoweredRail = 27, DetectorRail = 28, StickyPiston = 29, Web = 30, TallGrass = 31, DeadBush = 32,
        Piston = 33, PistonHead = 34, Wool = 35, PistonMoving = 36, Dandelion = 37, Rose = 38,
        BrownMushroom = 39, RedMushroom = 40, GoldBlock = 41, IronBlock = 42, DoubleSlab = 43, Slab = 44,
        Bricks = 45, Tnt = 46, Bookshelf = 47, MossyCobblestone = 48, Obsidian = 49, Torch = 50, Fire = 51,
        MobSpawner = 52, WoodenStairs = 53, Chest = 54, RedstoneWire = 55, DiamondOre = 56, DiamondBlock = 57,
        CraftingTable = 58, Workbench = 58, Crops = 59, Farmland = 60, Furnace = 61, LitFurnace = 62,
        SignPost = 63, WoodenDoor = 64, Ladder = 65, Rail = 66, CobblestoneStairs = 67, WallSign = 68,
        Lever = 69, StonePressurePlate = 70, IronDoor = 71, WoodenPressurePlate = 72, RedstoneOre = 73,
        LitRedstoneOre = 74, RedstoneTorchOff = 75, RedstoneTorch = 76, StoneButton = 77, SnowLayer = 78,
        Ice = 79, SnowBlock = 80, Cactus = 81, Clay = 82, SugarCane = 83, Jukebox = 84, Fence = 85,
        Pumpkin = 86, Netherrack = 87, SoulSand = 88, Glowstone = 89, Portal = 90, JackOLantern = 91,
        Cake = 92, RepeaterOff = 93, RepeaterOn = 94, LockedChest = 95, Trapdoor = 96, MonsterEgg = 97,
        StoneBricks = 98, BrownMushroomBlock = 99, RedMushroomBlock = 100, IronBars = 101, GlassPane = 102,
        Melon = 103, PumpkinStem = 104, MelonStem = 105, Vine = 106, FenceGate = 107, BrickStairs = 108,
        StoneBrickStairs = 109;

    /// <summary>Highest block id that exists in b1.8.1.</summary>
    public const int MaxId = 109;

    private static readonly BlockInfo[] _byId = Build();
    private static readonly BlockInfo[] _all = _byId.Where(b => b is not null).ToArray();
    private static Dictionary<string, BlockInfo>? _byNameCache;
    private static Dictionary<string, BlockInfo> _byName => _byNameCache ??= BuildNames();

    public static IReadOnlyList<BlockInfo> All => _all;

    /// <summary>Returns the block for an id; unknown ids return a generic solid placeholder.</summary>
    public static BlockInfo Get(int id) =>
        id >= 0 && id < _byId.Length && _byId[id] is { } b
            ? b
            : new BlockInfo(id, $"unknown_{id}", $"Unknown ({id})", 1f, true, 1.0, false, false, false, false, ToolType.None, -1, false);

    /// <summary>True if the id is a real b1.8.1 block.</summary>
    public static bool Exists(int id) => id >= 0 && id < _byId.Length && _byId[id] is not null;

    /// <summary>Forgiving lookup: case-insensitive, spaces/dashes/underscores ignored, common aliases and "minecraft:" prefix accepted.</summary>
    public static BlockInfo? ByName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var key = Normalize(name);
        if (_byName.TryGetValue(key, out var b)) return b;
        if (key.EndsWith("s") && _byName.TryGetValue(key[..^1], out b)) return b;       // plural
        if (key.EndsWith("es") && _byName.TryGetValue(key[..^2], out b)) return b;
        if (int.TryParse(key, out var id) && Exists(id)) return _byId[id];
        return null;
    }

    /// <summary>Normalizes a name for lookups: lower case, strips "minecraft:", spaces, underscores, dashes, apostrophes.</summary>
    public static string Normalize(string name)
    {
        var s = name.Trim().ToLowerInvariant();
        if (s.StartsWith("minecraft:")) s = s[10..];
        return new string(s.Where(c => c is not (' ' or '_' or '-' or '\'' or '.')).ToArray());
    }

    /// <summary>Liquids (water/lava, flowing or still).</summary>
    public static bool IsWater(int id) => id is FlowingWater or Water;
    public static bool IsLava(int id) => id is FlowingLava or Lava;

    /// <summary>A liquid SOURCE (what an empty bucket can scoop, ItemBucket b1.8.1): water 8/9 or lava 10/11 with meta 0.
    /// The id says nothing about it: a settled flow is id 9 with its level as meta, a fresh source is id 8 meta 0.
    /// Meta 1..7 is a flow level, meta &gt;= 8 falling liquid.</summary>
    public static bool IsWaterSource(int id, int meta) => IsWater(id) && meta == 0;
    public static bool IsLavaSource(int id, int meta) => IsLava(id) && meta == 0;
    public static bool IsLiquidSource(int id, int meta) => (IsWater(id) || IsLava(id)) && meta == 0;

    /// <summary>A player can stand inside this block without being blocked (air, plants, torches, liquids, ...).</summary>
    public static bool IsPassable(int id) => !Get(id).Solid;

    [Flags]
    private enum F { None = 0, Liquid = 1, Climb = 2, Trans = 4, Use = 8, Hazard = 16 }

    private static BlockInfo[] Build()
    {
        var a = new BlockInfo[MaxId + 1];
        const ToolType P = ToolType.Pickaxe, S = ToolType.Shovel, A = ToolType.Axe,
            W = ToolType.Sword, Sh = ToolType.Shears;
        const F T = F.Trans, U = F.Use;

        // coll: top of collision box; 0 = no collision (not solid).
        void Add(int id, string name, string display, float hardness, double coll, ToolType tool = ToolType.None, int lvl = -1, F f = F.None) =>
            a[id] = new BlockInfo(id, name, display, hardness, coll > 0, coll, f.HasFlag(F.Liquid), f.HasFlag(F.Climb),
                f.HasFlag(F.Trans), f.HasFlag(F.Use), tool, lvl, f.HasFlag(F.Hazard));

        Add(0, "air", "Air", 0f, 0, f: T);
        Add(1, "stone", "Stone", 1.5f, 1, P, 0);
        Add(2, "grass", "Grass", 0.6f, 1, S);
        Add(3, "dirt", "Dirt", 0.5f, 1, S);
        Add(4, "cobblestone", "Cobblestone", 2f, 1, P, 0);
        Add(5, "planks", "Wooden Planks", 2f, 1, A);
        Add(6, "sapling", "Sapling", 0f, 0, f: T);
        Add(7, "bedrock", "Bedrock", -1f, 1);
        Add(8, "flowing_water", "Water (flowing)", 100f, 0, f: F.Liquid | T);
        Add(9, "water", "Water", 100f, 0, f: F.Liquid | T);
        Add(10, "flowing_lava", "Lava (flowing)", 0f, 0, f: F.Liquid | T | F.Hazard);
        Add(11, "lava", "Lava", 100f, 0, f: F.Liquid | T | F.Hazard);
        Add(12, "sand", "Sand", 0.5f, 1, S);
        Add(13, "gravel", "Gravel", 0.6f, 1, S);
        Add(14, "gold_ore", "Gold Ore", 3f, 1, P, 2);
        Add(15, "iron_ore", "Iron Ore", 3f, 1, P, 1);
        Add(16, "coal_ore", "Coal Ore", 3f, 1, P, 0);
        Add(17, "log", "Wood", 2f, 1, A);
        Add(18, "leaves", "Leaves", 0.2f, 1, Sh, f: T);
        Add(19, "sponge", "Sponge", 0.6f, 1);
        Add(20, "glass", "Glass", 0.3f, 1, f: T);
        Add(21, "lapis_ore", "Lapis Lazuli Ore", 3f, 1, P, 1);
        Add(22, "lapis_block", "Lapis Lazuli Block", 3f, 1, P, 1);
        Add(23, "dispenser", "Dispenser", 3.5f, 1, P, 0, U);
        Add(24, "sandstone", "Sandstone", 0.8f, 1, P, 0);
        Add(25, "noteblock", "Note Block", 0.8f, 1, f: U);
        Add(26, "bed", "Bed", 0.2f, 0.5625, f: T | U);
        Add(27, "golden_rail", "Powered Rail", 0.7f, 0, f: T);
        Add(28, "detector_rail", "Detector Rail", 0.7f, 0, f: T);
        Add(29, "sticky_piston", "Sticky Piston", 0.5f, 1);
        Add(30, "web", "Cobweb", 4f, 0, W, 0, T);
        Add(31, "tallgrass", "Tall Grass", 0f, 0, f: T);
        Add(32, "deadbush", "Dead Bush", 0f, 0, f: T);
        Add(33, "piston", "Piston", 0.5f, 1);
        Add(34, "piston_head", "Piston Head", 0.5f, 1, f: T);
        Add(35, "wool", "Wool", 0.8f, 1, Sh);
        Add(36, "piston_extension", "Moving Piston", -1f, 0, f: T);
        Add(37, "yellow_flower", "Dandelion", 0f, 0, f: T);
        Add(38, "red_flower", "Rose", 0f, 0, f: T);
        Add(39, "brown_mushroom", "Brown Mushroom", 0f, 0, f: T);
        Add(40, "red_mushroom", "Red Mushroom", 0f, 0, f: T);
        Add(41, "gold_block", "Block of Gold", 3f, 1, P, 2);
        Add(42, "iron_block", "Block of Iron", 5f, 1, P, 1);
        Add(43, "double_stone_slab", "Double Slab", 2f, 1, P, 0);
        Add(44, "stone_slab", "Slab", 2f, 0.5, P, 0, T);
        Add(45, "brick_block", "Bricks", 2f, 1, P, 0);
        Add(46, "tnt", "TNT", 0f, 1);
        Add(47, "bookshelf", "Bookshelf", 1.5f, 1, A);
        Add(48, "mossy_cobblestone", "Moss Stone", 2f, 1, P, 0);
        Add(49, "obsidian", "Obsidian", 10f, 1, P, 3);
        Add(50, "torch", "Torch", 0f, 0, f: T);
        Add(51, "fire", "Fire", 0f, 0, f: T | F.Hazard);
        Add(52, "mob_spawner", "Monster Spawner", 5f, 1, P, 0, T);
        Add(53, "oak_stairs", "Wooden Stairs", 2f, 1, f: T);
        Add(54, "chest", "Chest", 2.5f, 1, A, f: U);
        Add(55, "redstone_wire", "Redstone Wire", 0f, 0, f: T);
        Add(56, "diamond_ore", "Diamond Ore", 3f, 1, P, 2);
        Add(57, "diamond_block", "Block of Diamond", 5f, 1, P, 2);
        Add(58, "crafting_table", "Crafting Table", 2.5f, 1, f: U);
        Add(59, "wheat", "Crops", 0f, 0, f: T);
        Add(60, "farmland", "Farmland", 0.6f, 1, S, f: T);   // collision box is a full block in beta
        Add(61, "furnace", "Furnace", 3.5f, 1, P, 0, U);
        Add(62, "lit_furnace", "Furnace (burning)", 3.5f, 1, P, 0, U);
        Add(63, "standing_sign", "Sign", 1f, 0, f: T);
        Add(64, "wooden_door", "Wooden Door", 3f, 1, f: T | U);
        Add(65, "ladder", "Ladder", 0.4f, 0, f: T | F.Climb);  // thin wall box; treat as passable + climbable
        Add(66, "rail", "Rail", 0.7f, 0, f: T);
        Add(67, "stone_stairs", "Cobblestone Stairs", 2f, 1, P, 0, T);
        Add(68, "wall_sign", "Sign", 1f, 0, f: T);
        Add(69, "lever", "Lever", 0.5f, 0, f: T | U);
        Add(70, "stone_pressure_plate", "Stone Pressure Plate", 0.5f, 0, P, 0, T);
        Add(71, "iron_door", "Iron Door", 5f, 1, P, 0, T);  // redstone only; right-click does nothing
        Add(72, "wooden_pressure_plate", "Wooden Pressure Plate", 0.5f, 0, f: T);
        Add(73, "redstone_ore", "Redstone Ore", 3f, 1, P, 2);
        Add(74, "lit_redstone_ore", "Redstone Ore (glowing)", 3f, 1, P, 2);
        Add(75, "unlit_redstone_torch", "Redstone Torch (off)", 0f, 0, f: T);
        Add(76, "redstone_torch", "Redstone Torch", 0f, 0, f: T);
        Add(77, "stone_button", "Button", 0.5f, 0, f: T | U);
        Add(78, "snow_layer", "Snow", 0.1f, 0, S, 0, T);
        Add(79, "ice", "Ice", 0.5f, 1, f: T);
        Add(80, "snow", "Snow Block", 0.2f, 1, S, 0);
        Add(81, "cactus", "Cactus", 0.4f, 0.9375, f: T | F.Hazard);
        Add(82, "clay", "Clay", 0.6f, 1, S);
        Add(83, "sugar_cane", "Sugar Cane", 0f, 0, f: T);
        Add(84, "jukebox", "Jukebox", 2f, 1, f: U);
        Add(85, "fence", "Fence", 2f, 1.5, f: T);
        Add(86, "pumpkin", "Pumpkin", 1f, 1);
        Add(87, "netherrack", "Netherrack", 0.4f, 1, P, 0);
        Add(88, "soul_sand", "Soul Sand", 0.5f, 0.875);
        Add(89, "glowstone", "Glowstone", 0.3f, 1, P, 0);   // rock material in b1.8.1: no drops without a pickaxe
        Add(90, "portal", "Portal", -1f, 0, f: T);
        Add(91, "lit_pumpkin", "Jack 'o' Lantern", 1f, 1);
        Add(92, "cake", "Cake", 0.5f, 0.4375, f: T | U);
        Add(93, "unpowered_repeater", "Redstone Repeater", 0f, 0.125, f: T | U);
        Add(94, "powered_repeater", "Redstone Repeater (on)", 0f, 0.125, f: T | U);
        Add(95, "locked_chest", "Locked Chest", 0f, 1);
        Add(96, "trapdoor", "Trapdoor", 3f, 0.1875, f: T | U);  // closed; open trapdoors are vertical
        Add(97, "monster_egg", "Silverfish Stone", 0.75f, 1);
        Add(98, "stone_bricks", "Stone Bricks", 1.5f, 1, P, 0);
        Add(99, "brown_mushroom_block", "Huge Brown Mushroom", 0.2f, 1);
        Add(100, "red_mushroom_block", "Huge Red Mushroom", 0.2f, 1);
        Add(101, "iron_bars", "Iron Bars", 5f, 1, P, 0, T);
        Add(102, "glass_pane", "Glass Pane", 0.3f, 1, f: T);
        Add(103, "melon_block", "Melon", 1f, 1);
        Add(104, "pumpkin_stem", "Pumpkin Stem", 0f, 0, f: T);
        Add(105, "melon_stem", "Melon Stem", 0f, 0, f: T);
        Add(106, "vine", "Vines", 0.2f, 0, f: T);  // not climbable in b1.8 (EntityLiving.p() = ladder only; vines climb since 1.2)
        Add(107, "fence_gate", "Fence Gate", 2f, 1.5, f: T | U);  // closed; open gates have no collision
        Add(108, "brick_stairs", "Brick Stairs", 2f, 1, P, 0, T);
        Add(109, "stone_brick_stairs", "Stone Brick Stairs", 1.5f, 1, P, 0, T);
        return a;
    }

    private static Dictionary<string, BlockInfo> BuildNames()
    {
        var d = new Dictionary<string, BlockInfo>();
        foreach (var b in _all)
        {
            d[Normalize(b.Name)] = b;
            d.TryAdd(Normalize(b.DisplayName), b);
        }
        foreach (var (alias, id) in Aliases) d[Normalize(alias)] = _byId[id];
        return d;
    }

    /// <summary>Alias → id (in addition to snake_case names and display names).</summary>
    internal static readonly (string Alias, int Id)[] Aliases =
    [
        ("workbench", 58), ("crafting table", 58), ("table", 58), ("crafting", 58),
        ("wood", 17), ("logs", 17), ("oak_log", 17), ("tree", 17), ("trunk", 17), ("oak_wood", 17), ("spruce_log", 17), ("birch_log", 17),
        ("wooden_planks", 5), ("plank", 5), ("oak_planks", 5), ("wood_planks", 5),
        ("cobble", 4), ("stonebrick", 98), ("stone_brick", 98), ("smooth_stone_brick", 98),
        ("burning_furnace", 62), ("furnace_lit", 62),
        ("still_water", 9), ("stationary_water", 9), ("water_source", 9),
        ("still_lava", 11), ("stationary_lava", 11), ("lava_source", 11),
        ("oak_leaves", 18), ("leaf", 18), ("oak_sapling", 6),
        ("cobweb", 30), ("spider_web", 30), ("tall_grass", 31), ("shrub", 32), ("dead_bush", 32),
        ("dandelion", 37), ("flower", 37), ("rose", 38), ("poppy", 38),
        ("slab", 44), ("step", 44), ("half_slab", 44), ("double_slab", 43), ("bricks", 45), ("brick", 45),
        ("moss_stone", 48), ("mossy_stone", 48), ("spawner", 52), ("monster_spawner", 52),
        ("wooden_stairs", 53), ("wood_stairs", 53), ("stairs", 53), ("cobblestone_stairs", 67), ("stone_stairs", 67),
        ("redstone_dust", 55), ("crops", 59), ("wheat_crops", 59), ("soil", 60), ("sign", 63), ("sign_post", 63),
        ("door", 64), ("oak_door", 64), ("rails", 66), ("minecart_track", 66), ("powered_rail", 27), ("booster_rail", 27),
        ("pressure_plate", 70), ("button", 77), ("glowing_redstone_ore", 74),
        ("snow_block", 80), ("reeds", 83), ("cane", 83), ("note_block", 25), ("music_block", 25),
        ("jack_o_lantern", 91), ("jackolantern", 91), ("pumpkin_lantern", 91),
        ("hellrock", 87), ("hellsand", 88), ("lightstone", 89), ("glowstone_block", 89), ("nether_portal", 90),
        ("repeater", 93), ("diode", 93), ("trap_door", 96), ("silverfish_block", 97), ("silverfish_stone", 97),
        ("fence_iron", 101), ("thin_glass", 102), ("pane", 102), ("melon", 103), ("watermelon", 103),
        ("vines", 106), ("gate", 107), ("brick_stair", 108), ("stonebrick_stairs", 109),
        ("lapis_lazuli_ore", 21), ("lapis_lazuli_block", 22), ("block_of_gold", 41), ("block_of_iron", 42), ("block_of_diamond", 57),
        ("sticky", 29), ("tnt_block", 46), ("dynamite", 46), ("redstone_torch_off", 75), ("torch_off", 75),
    ];

    /// <summary>Display name of a placed block including its metadata variant (wool colour, log type, slab type...).</summary>
    public static string DisplayName(int id, int meta) => Items.DisplayName(id, meta);
}
