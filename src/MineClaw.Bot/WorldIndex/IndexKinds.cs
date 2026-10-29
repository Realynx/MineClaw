using System.Runtime.CompilerServices;
using MineClaw.Data;
using MineClaw.Data.Versioning;

namespace MineClaw.Bot.WorldIndex;

/// <summary>Observation kinds written by the <see cref="WorldIndexer"/> (source=auto).</summary>
public static class IndexKinds
{
    public const string Chest = "chest", Furnace = "furnace", CraftingTable = "crafting_table", Dispenser = "dispenser",
        Bed = "bed", Jukebox = "jukebox", NoteBlock = "note_block", Spawner = "spawner", Sign = "sign",
        Generator = "cobble_generator", Farm = "farm", Tree = "tree", Island = "island", Hangout = "hangout",
        Activity = "player_activity", UnknownStructure = "unknown_structure",
        /// <summary>Nether portal: frame box + the exact spot to stand in (fields "stand"); pairs live in PortalLinks.</summary>
        Portal = "portal", // == MineClaw.Agent.Rag.PortalLinks.Kind
        /// <summary>Nether stand-in for islands: per 64x64 area, lava/fire/soul sand summary.</summary>
        Hazard = "nether_hazard",
        // 1.12.2 stations (ids absent from b1.8.1 worlds).
        Hopper = "hopper", Dropper = "dropper", EnderChest = "ender_chest", BrewingStand = "brewing_stand",
        EnchantingTable = "enchanting_table", Anvil = "anvil", ShulkerBox = "shulker_box";

    /// <summary>Kinds anchored on a single block (key = "x,y,z"); gone when that block is gone.</summary>
    public static readonly IReadOnlySet<string> PointKinds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { Chest, Furnace, CraftingTable, Dispenser, Bed, Jukebox, NoteBlock, Spawner, Sign,
          Hopper, Dropper, EnderChest, BrewingStand, EnchantingTable, Anvil, ShulkerBox };

    /// <summary>Kinds recomputed from clusters of cells.</summary>
    public static readonly IReadOnlySet<string> ClusterKinds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { Generator, Farm, Tree, Island, UnknownStructure, Portal };

    /// <summary>History-like kinds: never tombstoned by the indexer.</summary>
    public static readonly IReadOnlySet<string> HistoryKinds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Hangout, Activity };

    /// <summary>Kinds whose box must never be protected wholesale (it covers walkable/minable ground).</summary>
    public static readonly IReadOnlySet<string> NeverBoxProtected = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { Island, Hangout, Activity, Tree, Sign, Hazard };

    /// <summary>Area summaries (not things): never "near X", never a sign target, never a place for activity.</summary>
    public static bool IsArea(string kind) => kind is Island or Hangout or Activity or Hazard;

    public static string? StationKind(int blockId) => blockId switch
    {
        Blocks.Chest or Ids1122.TrappedChest => Chest,
        Ids1122.Hopper => Hopper,
        Ids1122.Dropper => Dropper,
        Ids1122.EnderChest => EnderChest,
        Ids1122.BrewingStand => BrewingStand,
        Ids1122.EnchantingTable => EnchantingTable,
        Ids1122.Anvil => Anvil,
        >= Ids1122.ShulkerFirst and <= Ids1122.ShulkerLast => ShulkerBox,
        Blocks.Furnace or Blocks.LitFurnace => Furnace,
        Blocks.Workbench => CraftingTable,
        Blocks.Dispenser => Dispenser,
        Blocks.Bed => Bed,
        Blocks.Jukebox => Jukebox,
        Blocks.NoteBlock => NoteBlock,
        Blocks.MobSpawner => Spawner,
        _ => null,
    };

    /// <summary>Model-facing aliases → kind ("cobble_gen", "generators", "workbench"...).</summary>
    public static string? FromAlias(string name)
    {
        var n = name.Trim().ToLowerInvariant().Replace(' ', '_');
        n = n switch
        {
            "gen" or "generator" or "cobble_gen" or "cobblegen" or "cobblestone_generator" or "stone_generator" => Generator,
            "workbench" or "crafting" or "table" => CraftingTable,
            "chests" or "storage" => Chest,
            "furnaces" => Furnace,
            "trees" or "sapling" or "saplings" => Tree,
            "farms" or "wheat" or "sugar_cane" or "sugarcane" or "cactus" => Farm,
            "portals" or "nether_portal" => Portal,
            _ => n,
        };
        if (n.EndsWith('s') && !PointKinds.Contains(n) && !ClusterKinds.Contains(n)) n = n[..^1];
        return PointKinds.Contains(n) || ClusterKinds.Contains(n) ? n : null;
    }
}

/// <summary>Per-block-id classification used by the column scan (one table lookup per block).</summary>
internal static class BlockClass
{
    [Flags]
    public enum C : ushort
    {
        None = 0,
        Station = 1,
        Sign = 2,
        Lava = 4,
        Water = 8,
        /// <summary>Counts towards an island's footprint.</summary>
        Ground = 16,
        /// <summary>Unusual / clearly player-placed block (unknown-structure seed).</summary>
        Build = 32,
        /// <summary>Generator output (cobblestone, stone).</summary>
        GenOutput = 64,
        Farm = 128,
        Tree = 256,
        /// <summary>Nether portal block.</summary>
        Portal = 512,
    }

    /// <summary>Blocks that are natural terrain in the nether (no "unknown build" from them there; mushrooms aren't farms).</summary>
    public static bool NaturalNether(int id) =>
        id is Blocks.Netherrack or Blocks.SoulSand or Blocks.Glowstone or Blocks.Gravel or Blocks.Obsidian or Blocks.BrownMushroom
            or Blocks.RedMushroom or Blocks.Fire or Ids1122.NetherBrick or Ids1122.NetherBrickFence or Ids1122.NetherBrickStairs
            or Ids1122.NetherWart or Ids1122.QuartzOre or Ids1122.Magma or Ids1122.NetherWartBlock;

    private sealed record Cache(IGameData Data, C[] Table);
    private static volatile Cache? _last;
    private static readonly ConditionalWeakTable<IGameData, C[]> Tables = new();

    /// <summary>The classification of the process's version (<see cref="GameData.Current"/>): 1.12.2's own blocks (log2, the
    /// new crops and stations...) join the b1.8.1 ids. Hot paths fetch it once per scan.</summary>
    public static C[] Table
    {
        get
        {
            var d = GameData.Current;
            var l = _last;
            if (l != null && ReferenceEquals(l.Data, d)) return l.Table;
            var t = Tables.GetValue(d, Build);
            _last = new Cache(d, t);
            return t;
        }
    }

    public static C Of(int id) => Of(Table, id);

    public static C Of(C[] table, int id) => (uint)id < (uint)table.Length ? table[id] : C.None;

    private static C[] Build(IGameData d)
    {
        var t = new C[256];
        for (int id = 1; id < 256; id++)
            if (d.BlockExists(id)) t[id] = C.Ground;
        foreach (var id in new[] { Blocks.Water, Blocks.FlowingWater, Blocks.Lava, Blocks.FlowingLava, Blocks.Fire, Blocks.Portal })
            t[id] = C.None;
        t[Blocks.Water] = t[Blocks.FlowingWater] = C.Water;
        t[Blocks.Lava] = t[Blocks.FlowingLava] = C.Lava;
        t[Blocks.Portal] = C.Portal;
        foreach (var id in new[] { Blocks.Chest, Blocks.Furnace, Blocks.LitFurnace, Blocks.Workbench, Blocks.Dispenser, Blocks.Bed,
                     Blocks.Jukebox, Blocks.NoteBlock, Blocks.MobSpawner })
            t[id] |= C.Station;
        t[Blocks.SignPost] |= C.Sign;
        t[Blocks.WallSign] |= C.Sign;
        t[Blocks.Cobblestone] |= C.GenOutput;
        t[Blocks.Stone] |= C.GenOutput;
        foreach (var id in new[] { Blocks.Crops, Blocks.Farmland, Blocks.SugarCane, Blocks.Cactus, Blocks.MelonStem, Blocks.PumpkinStem,
                     Blocks.Melon, Blocks.Pumpkin, Blocks.BrownMushroom, Blocks.RedMushroom })
            t[id] |= C.Farm;
        t[Blocks.Log] |= C.Tree;
        t[Blocks.Sapling] |= C.Tree;
        foreach (var id in new[]
                 {
                     Blocks.Planks, Blocks.Glass, Blocks.Wool, Blocks.Bricks, Blocks.StoneBricks, Blocks.Fence, Blocks.FenceGate, Blocks.Torch,
                     Blocks.Ladder, Blocks.WoodenDoor, Blocks.IronDoor, Blocks.WoodenStairs, Blocks.CobblestoneStairs, Blocks.BrickStairs,
                     Blocks.StoneBrickStairs, Blocks.Slab, Blocks.DoubleSlab, Blocks.Bookshelf, Blocks.Tnt, Blocks.Rail, Blocks.PoweredRail,
                     Blocks.DetectorRail, Blocks.RedstoneWire, Blocks.RedstoneTorch, Blocks.RedstoneTorchOff, Blocks.RepeaterOff, Blocks.RepeaterOn,
                     Blocks.Lever, Blocks.StoneButton, Blocks.StonePressurePlate, Blocks.WoodenPressurePlate, Blocks.Piston, Blocks.StickyPiston,
                     Blocks.PistonHead, Blocks.Trapdoor, Blocks.GlassPane, Blocks.IronBars, Blocks.Obsidian, Blocks.GoldBlock, Blocks.IronBlock,
                     Blocks.DiamondBlock, Blocks.LapisBlock, Blocks.Sandstone, Blocks.MossyCobblestone, Blocks.Glowstone, Blocks.JackOLantern,
                     Blocks.Netherrack, Blocks.SoulSand, Blocks.Sponge, Blocks.Web, Blocks.SnowBlock, Blocks.Clay, Blocks.Cake,
                 })
            t[id] |= C.Build;
        // 1.12.2's own ids (absent from b1.8.1, so its table is unchanged).
        void Add(C c, params int[] ids)
        {
            foreach (var id in ids)
                if (id > Blocks.MaxId && id < t.Length && d.BlockExists(id)) t[id] |= c;
        }
        Add(C.Station, Ids1122.TrappedChest, Ids1122.Hopper, Ids1122.Dropper, Ids1122.EnderChest, Ids1122.BrewingStand,
            Ids1122.EnchantingTable, Ids1122.Anvil);
        for (int id = Ids1122.ShulkerFirst; id <= Ids1122.ShulkerLast; id++) Add(C.Station, id);
        Add(C.Farm, Ids1122.Carrots, Ids1122.Potatoes, Ids1122.Beetroots, Ids1122.NetherWart);
        Add(C.Tree, Ids1122.Log2);
        // Crafted-only (or structure) blocks: the new woods' slabs/stairs/fences/gates/doors, stained glass/clay, concrete,
        // carpets, quartz, purpur, nether/end bricks, redstone parts, banners, hay, sea lanterns, glazed terracotta...
        Add(C.Build, 112, 113, 114, 123, 124, 125, 126, 128, 131, 132, 134, 135, 136, 138, 139, 140, 143, 144, 147, 148, 149, 150, 151,
            152, 155, 156, 157, 160, 163, 164, 165, 167, 169, 170, 171, 176, 177, 178, 180, 181, 182, 183, 184, 185, 186, 187, 188, 189,
            190, 191, 192, 193, 194, 195, 196, 197, 198, 201, 202, 203, 204, 205, 206, 216, 218, 251, 252);
        for (int id = 235; id <= 250; id++) Add(C.Build, id);
        if (d.BlockByName("stained_glass") is { Id: var sg } && sg <= Blocks.MaxId) t[sg] |= C.Build; // 95 (b1.8.1: locked chest)
        return t;
    }

    /// <summary>Farm sub-type of a farm block, or null.</summary>
    public static string? FarmType(int id) => id switch
    {
        Blocks.Crops or Blocks.Farmland => "wheat",
        Blocks.SugarCane => "sugar_cane",
        Blocks.Cactus => "cactus",
        Blocks.MelonStem or Blocks.Melon => "melon",
        Blocks.PumpkinStem or Blocks.Pumpkin => "pumpkin",
        Blocks.BrownMushroom or Blocks.RedMushroom => "mushroom",
        Ids1122.Carrots => "carrot",
        Ids1122.Potatoes => "potato",
        Ids1122.Beetroots => "beetroot",
        Ids1122.NetherWart => "nether_wart",
        _ => null,
    };

    public static readonly string[] FarmTypes = ["wheat", "sugar_cane", "cactus", "melon", "pumpkin", "mushroom", "carrot", "potato", "beetroot", "nether_wart"];

    /// <summary>Farm types that grow wild in a normal world (cane by rivers, desert cactus, cave mushrooms, pumpkin patches):
    /// indexed there as resources, but their soil and water are not protected unless terrain is scarce.</summary>
    public static bool WildFarmType(string type) => type is "sugar_cane" or "cactus" or "mushroom" or "pumpkin" or "melon";
}
