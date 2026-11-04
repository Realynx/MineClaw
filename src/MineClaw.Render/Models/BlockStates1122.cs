namespace MineClaw.Render.Models;

/// <summary>A block state as the 1.12.2 model layer names it: the blockstate file, its properties, an exact variant key
/// (seamless double slabs: "all"), or <see cref="Builtin"/> (drawn by a tile-entity renderer, not a model).</summary>
public sealed record StateRef(string File, SortedDictionary<string, string> Props, string? Key = null, bool Builtin = false)
{
    /// <summary>Cache key: file + properties (+ key).</summary>
    public string CacheKey => Props.Count == 0 && Key == null ? File : File + "[" + string.Join(",", Props.Select(p => p.Key + "=" + p.Value)) + (Key == null ? "" : ";" + Key) + "]";
    public string Get(string k) => Props.TryGetValue(k, out var v) ? v : "";
}

/// <summary>
/// The 1.12.2 meta → blockstate mapping MineClaw renders with, ported from the decompiled client (read-only ground truth at
/// <c>mc-1.12.2-src</c>; the logic is ported, nothing copied):
/// <list type="bullet">
/// <item><c>Block.getStateFromMeta</c> per block class (BlockStairs: facing = byIndex(5 − (meta &amp; 3)), half = bit 4; BlockSlab
/// variants + half bit 8 (doubles: seamless bit 8); BlockLog axis bits 4/8 (12 = none); BlockTorch 1 E, 2 W, 3 S, 4 N, 5 up;
/// BlockDoor lower = facing (byHorizontalIndex(meta &amp; 3).rotateYCCW()) + open bit 4, upper = hinge bit 1 + powered bit 2;
/// BlockTrapDoor N/S/W/E + open 4 + top 8; ladders, furnaces, chests, wall signs, dispensers byIndex; fence gates, pumpkins,
/// beds, repeaters byHorizontalIndex; buttons, levers, rails, crops, snow layers, vines, leaves, saplings, flowers, colours,
/// double plants, pillars, quartz, anvils, hoppers, cocoa, mushroom blocks).</item>
/// <item>The client's <c>BlockModelShapes.registerAllBlocks</c> state mappers: the file is named by the variant ("birch_log",
/// "red_wool", "stone_brick_slab", "granite"), ignored properties are dropped (leaf decay, fence-gate/door power, cactus and
/// reed age, redstone power: the wire's power becomes its tint), and the built-in blocks (chests, signs, beds, skulls,
/// banners, shulker boxes, fluids) are drawn without a model.</item>
/// <item><c>getActualState</c>, from the neighbours: stair shapes (<c>BlockStairs.getStairsShape</c>: outer/inner corners from
/// the stairs in front of / behind), fence/pane/wall/iron bar connections (a solid full cube, the same kind, a fence gate for
/// fences and walls), the wall's post, fence gates in walls, door halves combined, the double plant's upper half taking the
/// lower half's variant, grass/mycelium/podzol under snow, vines' top, stems bending to their fruit, redstone wire sides and
/// climbs (approximate: wire, torches, repeaters and levers attract it).</item>
/// </list>
/// Block ids come from the world as (id, meta); the registry names follow minecraft-data's 1.12.2 ids. The per-block rules
/// live in BlockStates1122.Rules.cs, one table entry per id.
/// </summary>
public static partial class BlockStates1122
{
    public static readonly string[] Wood = ["oak", "spruce", "birch", "jungle", "acacia", "dark_oak"];
    public static readonly string[] Colors =
        ["white", "orange", "magenta", "light_blue", "yellow", "lime", "pink", "gray", "silver", "cyan", "purple", "blue", "brown", "green", "red", "black"];
    private static readonly string[] StoneSlab = ["stone", "sandstone", "wood_old", "cobblestone", "brick", "stone_brick", "nether_brick", "quartz"];
    private static readonly string[] Rail = ["north_south", "east_west", "ascending_east", "ascending_west", "ascending_north", "ascending_south", "south_east", "south_west", "north_west", "north_east"];
    private static readonly string[] Lever = ["down_x", "east", "west", "south", "north", "up_z", "up_x", "down_z"];
    private static readonly string[] Button = ["down", "east", "west", "south", "north", "up", "up", "up"];
    private static readonly string[] Mushroom = ["all_inside", "north_west", "north", "north_east", "west", "center", "east", "south_west", "south", "south_east", "stem", "all_inside", "all_inside", "all_inside", "all_outside", "all_stem"];
    private static readonly string[] Flowers = ["poppy", "blue_orchid", "allium", "houstonia", "red_tulip", "orange_tulip", "white_tulip", "pink_tulip", "oxeye_daisy"];
    private static readonly string[] DoublePlants = ["sunflower", "syringa", "double_grass", "double_fern", "double_rose", "paeonia"];

    private static readonly bool[] StairIds = IdSet(53, 67, 108, 109, 114, 128, 134, 135, 136, 156, 163, 164, 180, 203);
    /// <summary>Block.isExceptionBlockForAttaching (+ pistons, shulker boxes 219..234): never a fence/pane/wall attachment even when full.</summary>
    private static readonly bool[] AttachExceptions = IdSet([18, 161, 20, 95, 138, 118, 86, 91, 103, 166, 29, 33, 34, 96, 167, .. Enumerable.Range(219, 16)]);
    /// <summary>Power sources and wire that a redstone wire always turns toward.</summary>
    private static readonly bool[] WireSources = IdSet(55, 75, 76, 69, 70, 72, 77, 143, 147, 148, 151, 178, 152, 131, 149, 150);

    private static readonly Rule?[] Rules = BuildRules();

    private static bool[] IdSet(params int[] ids)
    {
        var set = new bool[256];
        foreach (int id in ids) set[id] = true;
        return set;
    }

    private static bool In(bool[] set, int id) => (uint)id < (uint)set.Length && set[id];

    /// <summary>EnumFacing.byIndex: 0 down, 1 up, 2 north, 3 south, 4 west, 5 east.</summary>
    public static string Facing(int i) => (Math.Abs(i) % 6) switch { 0 => "down", 1 => "up", 2 => "north", 3 => "south", 4 => "west", _ => "east" };
    /// <summary>EnumFacing.byHorizontalIndex: 0 south, 1 west, 2 north, 3 east.</summary>
    public static string HFacing(int i) => (i & 3) switch { 0 => "south", 1 => "west", 2 => "north", _ => "east" };
    private static string Horizontal(int i) => Facing(i) is "down" or "up" ? "north" : Facing(i);
    private static string B(bool b) => b ? "true" : "false";

    public static bool IsStairs(int id) => In(StairIds, id);
    public static bool IsWoodFence(int id) => id is 85 or (>= 188 and <= 192);
    public static bool IsFence(int id) => IsWoodFence(id) || id == 113;
    public static bool IsFenceGate(int id) => id is 107 or (>= 183 and <= 187);
    public static bool IsPane(int id) => id is 101 or 102 or 160;
    public static bool IsDoor(int id) => id is 64 or 71 or (>= 193 and <= 197);
    private static bool AttachException(int id) => In(AttachExceptions, id);

    /// <summary>EnumFacing name → (dx, dz).</summary>
    public static (int Dx, int Dz) Step(string f) => f switch { "north" => (0, -1), "south" => (0, 1), "west" => (-1, 0), "east" => (1, 0), _ => (0, 0) };
    private static string RotateYCCW(string f) => f switch { "north" => "west", "west" => "south", "south" => "east", "east" => "north", _ => f };
    private static string Opposite(string f) => f switch { "north" => "south", "south" => "north", "west" => "east", "east" => "west", "up" => "down", _ => "up" };
    public static string StairFacing(int meta) => Facing(5 - (meta & 3));
    public static string StairHalf(int meta) => (meta & 4) != 0 ? "top" : "bottom";

    private static string Name(int id) => id switch
    {
        227 => "silver_shulker_box",
        243 => "silver_glazed_terracotta",
        _ => MineClaw.Data.Versioning.Release1122GameData.Instance.NameOf(id) ?? "air",
    };

    /// <param name="solidFull">Whether a neighbour (id, meta) is a solid full cube (BlockFaceShape.SOLID on every side).</param>
    public static StateRef Resolve(int id, int meta, IVoxelWorld? w, int x, int y, int z, Func<int, int, bool> solidFull)
    {
        var c = new Ctx(id, meta, w, x, y, z, solidFull, new SortedDictionary<string, string>(StringComparer.Ordinal), Name(id));
        return (uint)id < (uint)Rules.Length && Rules[id] is { } rule ? rule(in c) : c.Named();
    }

    private static bool WireAttracts(int id, int meta, string dir) =>
        In(WireSources, id)
        || id is 93 or 94 && HFacing(meta) is var f && (f == dir || Opposite(f) == dir)
        || id == 218 && Facing(meta & 7) == dir;

    /// <summary>BlockStairs.getStairsShape: outer corner from the stairs in front (its facing across ours, not matched on our
    /// left/right), inner corner from the stairs behind.</summary>
    public static string StairShape(IVoxelWorld w, int x, int y, int z, int meta)
    {
        string facing = StairFacing(meta), half = StairHalf(meta);
        var (fx, fz) = Step(facing);
        // Outer: the stairs in front, unless the one on that side of ours already continues our row.
        if (CornerStair(w, x + fx, y, z + fz, half, facing) is { } f1 && IsDifferent(w, x, y, z, Opposite(f1), facing, half))
            return f1 == RotateYCCW(facing) ? "outer_left" : "outer_right";
        // Inner: the stairs behind.
        if (CornerStair(w, x - fx, y, z - fz, half, facing) is { } f2 && IsDifferent(w, x, y, z, f2, facing, half))
            return f2 == RotateYCCW(facing) ? "inner_left" : "inner_right";
        return "straight";
    }

    private static bool XAxis(string f) => f is "east" or "west";

    /// <summary>The facing of stairs at (x, y, z) in our half turned across our axis, or null.</summary>
    private static string? CornerStair(IVoxelWorld w, int x, int y, int z, string half, string facing)
    {
        int id = w.Block(x, y, z), m = w.Meta(x, y, z);
        if (!IsStairs(id) || StairHalf(m) != half) return null;
        string f = StairFacing(m);
        return XAxis(f) != XAxis(facing) ? f : null;
    }

    /// <summary>The block toward <paramref name="dir"/> isn't stairs of our facing and half.</summary>
    private static bool IsDifferent(IVoxelWorld w, int x, int y, int z, string dir, string facing, string half)
    {
        var (dx, dz) = Step(dir);
        int id = w.Block(x + dx, y, z + dz), m = w.Meta(x + dx, y, z + dz);
        return !IsStairs(id) || StairFacing(m) != facing || StairHalf(m) != half;
    }
}
