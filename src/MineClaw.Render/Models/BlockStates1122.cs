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
/// Block ids come from the world as (id, meta); the registry names follow minecraft-data's 1.12.2 ids.
/// </summary>
public static class BlockStates1122
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

    /// <summary>EnumFacing.byIndex: 0 down, 1 up, 2 north, 3 south, 4 west, 5 east.</summary>
    public static string Facing(int i) => (Math.Abs(i) % 6) switch { 0 => "down", 1 => "up", 2 => "north", 3 => "south", 4 => "west", _ => "east" };
    /// <summary>EnumFacing.byHorizontalIndex: 0 south, 1 west, 2 north, 3 east.</summary>
    public static string HFacing(int i) => (i & 3) switch { 0 => "south", 1 => "west", 2 => "north", _ => "east" };
    private static string Horizontal(int i) => Facing(i) is "down" or "up" ? "north" : Facing(i);
    private static string B(bool b) => b ? "true" : "false";

    public static bool IsStairs(int id) => id is 53 or 67 or 108 or 109 or 114 or 128 or 134 or 135 or 136 or 156 or 163 or 164 or 180 or 203;
    public static bool IsWoodFence(int id) => id is 85 or (>= 188 and <= 192);
    public static bool IsFence(int id) => IsWoodFence(id) || id == 113;
    public static bool IsFenceGate(int id) => id is 107 or (>= 183 and <= 187);
    public static bool IsPane(int id) => id is 101 or 102 or 160;
    public static bool IsDoor(int id) => id is 64 or 71 or (>= 193 and <= 197);
    /// <summary>Block.isExceptionBlockForAttaching (+ pistons): never a fence/pane/wall attachment even when full.</summary>
    private static bool AttachException(int id) => id is 18 or 161 or 20 or 95 or 138 or 118 or 86 or 91 or 103 or 166 or 29 or 33 or 34 or 96 or 167 or (>= 219 and <= 234);

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
        var p = new SortedDictionary<string, string>(StringComparer.Ordinal);
        string name = Name(id);
        StateRef S(string file, string? key = null) => new(file, p, key);
        StateRef Builtin() => new(name, p, null, true);
        int Bk(int dx, int dy, int dz) => w?.Block(x + dx, y + dy, z + dz) ?? 0;
        int Mt(int dx, int dy, int dz) => w?.Meta(x + dx, y + dy, z + dz) ?? 0;
        bool Solid(int dx, int dy, int dz) { int b = Bk(dx, dy, dz); return b != 0 && !AttachException(b) && solidFull(b, Mt(dx, dy, dz)); }

        switch (id)
        {
            case 1: return S((meta % 7) switch { 1 => "granite", 2 => "smooth_granite", 3 => "diorite", 4 => "smooth_diorite", 5 => "andesite", 6 => "smooth_andesite", _ => "stone" });
            case 2 or 110:
                p["snowy"] = B(Bk(0, 1, 0) is 78 or 80);
                return S(name);
            case 3:
                if (meta % 3 == 2) { p["snowy"] = B(Bk(0, 1, 0) is 78 or 80); return S("podzol"); }
                return S(meta % 3 == 1 ? "coarse_dirt" : "dirt");
            case 5: return S(Wood[meta % 6] + "_planks");
            case 6: p["stage"] = ((meta >> 3) & 1).ToString(); return S(Wood[(meta & 7) % 6] + "_sapling");
            case 8 or 9 or 10 or 11 or 51: return Builtin(); // fluids and fire: the procedural b1.8.1 path
            case 12: return S((meta & 1) == 1 ? "red_sand" : "sand");
            case 17 or 162:
                p["axis"] = (meta & 12) switch { 0 => "y", 4 => "x", 8 => "z", _ => "none" };
                return S(Wood[id == 162 ? Math.Min(5, (meta & 3) + 4) : meta & 3] + "_log");
            case 18: return S(Wood[meta & 3] + "_leaves");
            case 161: return S(Wood[4 + (meta & 1)] + "_leaves");
            case 19: p["wet"] = B((meta & 1) == 1); return S(name);
            case 23 or 158: p["facing"] = Facing(meta & 7); return S(name);
            case 24: return S((meta % 3) switch { 1 => "chiseled_sandstone", 2 => "smooth_sandstone", _ => "sandstone" });
            case 179: return S((meta % 3) switch { 1 => "chiseled_red_sandstone", 2 => "smooth_red_sandstone", _ => "red_sandstone" });
            case 27 or 28 or 157: p["shape"] = Rail[(meta & 7) % 6]; p["powered"] = B((meta & 8) != 0); return S(name);
            case 66: p["shape"] = Rail[meta % 10]; return S(name);
            case 29 or 33: p["facing"] = Facing(meta & 7); p["extended"] = B((meta & 8) != 0); return S(name);
            case 34: p["facing"] = Facing(meta & 7); p["type"] = (meta & 8) != 0 ? "sticky" : "normal"; p["short"] = "false"; return S(name);
            case 31: return S((meta % 3) switch { 0 => "dead_bush", 2 => "fern", _ => "tall_grass" });
            case 32: return S("dead_bush");
            case 35: return S(Colors[meta & 15] + "_wool");
            case 171: return S(Colors[meta & 15] + "_carpet");
            case 159: return S(Colors[meta & 15] + "_stained_hardened_clay");
            case 95: return S(Colors[meta & 15] + "_stained_glass");
            case 251: return S(Colors[meta & 15] + "_concrete");
            case 252: return S(Colors[meta & 15] + "_concrete_powder");
            case 160: Pane(); return S(Colors[meta & 15] + "_stained_glass_pane");
            case 101 or 102: Pane(); return S(name);
            case 37: return S("dandelion");
            case 38: return S(Flowers[meta % 9]);
            case 43: return S(StoneSlab[meta & 7] + "_double_slab", (meta & 8) != 0 ? "all" : "normal");
            case 44: p["half"] = (meta & 8) != 0 ? "top" : "bottom"; return S(StoneSlab[meta & 7] + "_slab");
            case 125: return S(Wood[(meta & 7) % 6] + "_double_slab");
            case 126: p["half"] = (meta & 8) != 0 ? "top" : "bottom"; return S(Wood[(meta & 7) % 6] + "_slab");
            case 181: return S("red_sandstone_double_slab", (meta & 8) != 0 ? "all" : "normal");
            case 182 or 205: p["half"] = (meta & 8) != 0 ? "top" : "bottom"; return S(id == 182 ? "red_sandstone_slab" : "purpur_slab");
            case 204: return S("purpur_double_slab");
            case 50 or 75 or 76:
                p["facing"] = meta switch { 1 => "east", 2 => "west", 3 => "south", 4 => "north", _ => "up" };
                return S(name);
            case 54 or 130 or 146:
            {
                // A pair is one large chest (LargeChest): each cell draws its half of the 30-wide model, lid and latch across.
                string kind = id == 130 ? "ender" : id == 146 ? "trapped" : "normal";
                if (w != null && id != 130 && LargeChest.Partner(w, x, y, z) is { } mate)
                {
                    int f = LargeChest.PairFacing(w, x, y, z, mate);
                    p["facing"] = Facing(f);
                    p["half"] = LargeChest.ModelHalf(f, mate);
                    return S("mineclaw_chest_" + kind + "_double");
                }
                p["facing"] = Horizontal(meta);
                return S("mineclaw_chest_" + kind);
            }
            case >= 219 and <= 234: return S("mineclaw_shulker_" + Colors[id - 219]);
            case 26 or 63 or 68 or 144 or 176 or 177 or 119 or 209 or 166 or 36 or 217:
                return Builtin();
            case 55:
                foreach (var f in new[] { "north", "east", "south", "west" }) p[f] = Wire(f);
                p["power"] = (meta & 15).ToString(); // ignored by the model (the mapper drops it); the tint reads it
                return S(name);
            case 59 or 141 or 142: p["age"] = (meta & 7).ToString(); return S(name);
            case 207 or 115 or 212: p["age"] = (meta & 3).ToString(); return S(name);
            case 60: p["moisture"] = (meta & 7).ToString(); return S(name);
            case 61 or 62 or 65: p["facing"] = Horizontal(meta); return S(name);
            case 64 or 71 or >= 193 and <= 197: Door(); return S(name);
            case 69: p["facing"] = Lever[meta & 7]; p["powered"] = B((meta & 8) != 0); return S(name);
            case 70 or 72: p["powered"] = B(meta == 1); return S(name);
            case 147 or 148: p["power"] = (meta & 15).ToString(); return S(name);
            case 77 or 143: p["facing"] = Button[meta & 7]; p["powered"] = B((meta & 8) != 0); return S(name);
            case 78: p["layers"] = ((meta & 7) + 1).ToString(); return S(name);
            case 80: return S("snow");
            case 85 or 113 or >= 188 and <= 192: Fence(); return S(name);
            case 86 or 91: p["facing"] = HFacing(meta); return S(name);
            case 90: p["axis"] = (meta & 3) == 2 ? "z" : "x"; return S(name);
            case 92: p["bites"] = Math.Min(6, meta).ToString(); return S(name);
            case 93 or 94: p["facing"] = HFacing(meta); p["delay"] = (1 + ((meta >> 2) & 3)).ToString(); p["locked"] = "false"; return S(name);
            case 149 or 150: p["facing"] = HFacing(meta); p["mode"] = (meta & 4) != 0 ? "subtract" : "compare"; p["powered"] = B((meta & 8) != 0); return S(name);
            case 96 or 167:
                p["facing"] = (meta & 3) switch { 0 => "north", 1 => "south", 2 => "west", _ => "east" };
                p["open"] = B((meta & 4) != 0);
                p["half"] = (meta & 8) != 0 ? "top" : "bottom";
                return S(name);
            case 97: return S((meta % 6) switch { 1 => "cobblestone", 2 => "stone_brick", 3 => "mossy_brick", 4 => "cracked_brick", 5 => "chiseled_brick", _ => "stone" } + "_monster_egg");
            case 98: return S((meta & 3) switch { 1 => "mossy_stonebrick", 2 => "cracked_stonebrick", 3 => "chiseled_stonebrick", _ => "stonebrick" });
            case 99 or 100: p["variant"] = Mushroom[meta & 15]; return S(name);
            case 104 or 105:
            {
                p["age"] = (meta & 7).ToString();
                string facing = "up";
                if ((meta & 7) == 7)
                    foreach (var f in new[] { "north", "east", "south", "west" })
                    {
                        var (dx, dz) = Step(f);
                        if (Bk(dx, 0, dz) == (id == 104 ? 86 : 103)) { facing = f; break; }
                    }
                p["facing"] = facing;
                if (facing != "up") p.Remove("age");
                return S(name);
            }
            case 106:
                p["south"] = B((meta & 1) != 0); p["west"] = B((meta & 2) != 0); p["north"] = B((meta & 4) != 0); p["east"] = B((meta & 8) != 0);
                p["up"] = B(Solid(0, 1, 0));
                return S(name);
            case 107 or >= 183 and <= 187:
            {
                string f = HFacing(meta);
                p["facing"] = f; p["open"] = B((meta & 4) != 0);
                bool zAxis = f is "north" or "south";
                p["in_wall"] = B(zAxis ? Bk(-1, 0, 0) == 139 || Bk(1, 0, 0) == 139 : Bk(0, 0, -1) == 139 || Bk(0, 0, 1) == 139);
                return S(name);
            }
            case 117: p["has_bottle_0"] = B((meta & 1) != 0); p["has_bottle_1"] = B((meta & 2) != 0); p["has_bottle_2"] = B((meta & 4) != 0); return S(name);
            case 118: p["level"] = (meta & 3).ToString(); return S(name);
            case 120: p["eye"] = B((meta & 4) != 0); p["facing"] = HFacing(meta & 3); return S(name);
            case 127: p["facing"] = HFacing(meta & 3); p["age"] = Math.Min(2, (meta & 15) >> 2).ToString(); return S(name);
            case 131: p["facing"] = HFacing(meta & 3); p["attached"] = B((meta & 4) != 0); p["powered"] = B((meta & 8) != 0); return S(name);
            case 139: Wall(); return S((meta == 1 ? "mossy_cobblestone" : "cobblestone") + "_wall");
            case 145: p["facing"] = HFacing(meta & 3); p["damage"] = ((meta >> 2) % 3) switch { 1 => "slightly_damaged", 2 => "very_damaged", _ => "intact" }; return S(name);
            case 154: p["facing"] = Facing(meta & 7) == "up" ? "down" : Facing(meta & 7); p["enabled"] = B((meta & 8) == 0); return S(name);
            case 155: return meta switch
            {
                1 => S("chiseled_quartz_block"),
                2 => S("quartz_column", "axis=y"),
                3 => S("quartz_column", "axis=x"),
                4 => S("quartz_column", "axis=z"),
                _ => S("quartz_block"),
            };
            case 168: return S((meta % 3) switch { 1 => "prismarine_bricks", 2 => "dark_prismarine", _ => "prismarine" });
            case 170 or 202 or 216: p["axis"] = (meta & 12) switch { 4 => "x", 8 => "z", _ => "y" }; return S(name);
            case 175:
            {
                bool upper = (meta & 8) != 0;
                int variant = upper ? (Bk(0, -1, 0) == 175 ? Mt(0, -1, 0) & 7 : 0) : meta & 7;
                p["half"] = upper ? "upper" : "lower";
                return S(DoublePlants[variant % 6]);
            }
            case 198: p["facing"] = Facing(meta); return S(name);
            case 200: p["age"] = Math.Min(5, meta).ToString(); return S(name);
            case 218: p["facing"] = Facing(meta & 7); p["powered"] = B((meta & 8) != 0); return S(name);
            case >= 235 and <= 250: p["facing"] = HFacing(meta); return S(Colors[id - 235] + "_glazed_terracotta");
            case 255: p["mode"] = (meta & 3) switch { 1 => "load", 2 => "corner", 3 => "data", _ => "save" }; return S(name);
            case 137 or 210 or 211: p["facing"] = Facing(meta & 7); p["conditional"] = B((meta & 8) != 0); return S(name);
        }
        if (IsStairs(id))
        {
            p["facing"] = StairFacing(meta);
            p["half"] = StairHalf(meta);
            p["shape"] = w == null ? "straight" : StairShape(w, x, y, z, meta);
            return S(name);
        }
        return S(name);

        void Fence()
        {
            foreach (var f in new[] { "north", "east", "south", "west" })
            {
                var (dx, dz) = Step(f);
                int b = Bk(dx, 0, dz);
                bool same = id == 113 ? b == 113 : IsWoodFence(b);
                p[f] = B(same || IsFenceGate(b) || Solid(dx, 0, dz));
            }
        }
        void Pane()
        {
            foreach (var f in new[] { "north", "east", "south", "west" })
            {
                var (dx, dz) = Step(f);
                p[f] = B(IsPane(Bk(dx, 0, dz)) || Solid(dx, 0, dz));
            }
        }
        void Wall()
        {
            bool Wl(int dx, int dz) => Bk(dx, 0, dz) == 139 || IsFenceGate(Bk(dx, 0, dz)) || Solid(dx, 0, dz);
            bool n = Wl(0, -1), e = Wl(1, 0), s = Wl(0, 1), wv = Wl(-1, 0);
            bool straight = n && !e && s && !wv || !n && e && !s && wv;
            p["north"] = B(n); p["east"] = B(e); p["south"] = B(s); p["west"] = B(wv);
            p["up"] = B(!straight || Bk(0, 1, 0) != 0);
        }
        void Door()
        {
            bool upper = (meta & 8) != 0;
            int lower = upper ? (Bk(0, -1, 0) == id ? Mt(0, -1, 0) : 0) : meta;
            int top = upper ? meta : (Bk(0, 1, 0) == id ? Mt(0, 1, 0) : 8);
            p["half"] = upper ? "upper" : "lower";
            p["facing"] = RotateYCCW(HFacing(lower & 3));
            p["open"] = B((lower & 4) != 0);
            p["hinge"] = (top & 1) != 0 ? "right" : "left";
            p["powered"] = B((top & 2) != 0);
        }
        string Wire(string f)
        {
            var (dx, dz) = Step(f);
            int side = Bk(dx, 0, dz);
            if (WireAttracts(side, Mt(dx, 0, dz), f)) return "side";
            bool sideSolid = Solid(dx, 0, dz);
            if (!sideSolid && Bk(dx, -1, dz) == 55) return "side";
            if (sideSolid && !Solid(0, 1, 0) && Bk(dx, 1, dz) == 55) return "up";
            return "none";
        }
    }

    private static bool WireAttracts(int id, int meta, string dir) => id switch
    {
        55 or 75 or 76 or 69 or 70 or 72 or 77 or 143 or 147 or 148 or 151 or 178 or 152 or 131 or 149 or 150 => true,
        93 or 94 => HFacing(meta) is var f && (f == dir || Opposite(f) == dir),
        218 => Facing(meta & 7) == dir,
        _ => false,
    };

    /// <summary>BlockStairs.getStairsShape: outer corner from the stairs in front (its facing across ours, not matched on our
    /// left/right), inner corner from the stairs behind.</summary>
    public static string StairShape(IVoxelWorld w, int x, int y, int z, int meta)
    {
        string facing = StairFacing(meta), half = StairHalf(meta);
        bool IsDifferent(string dir)
        {
            var (dx, dz) = Step(dir);
            int id = w.Block(x + dx, y, z + dz), m = w.Meta(x + dx, y, z + dz);
            return !IsStairs(id) || StairFacing(m) != facing || StairHalf(m) != half;
        }
        bool XAxis(string f) => f is "east" or "west";
        var (fx, fz) = Step(facing);
        int front = w.Block(x + fx, y, z + fz), fm = w.Meta(x + fx, y, z + fz);
        if (IsStairs(front) && StairHalf(fm) == half)
        {
            string f1 = StairFacing(fm);
            if (XAxis(f1) != XAxis(facing) && IsDifferent(Opposite(f1)))
                return f1 == RotateYCCW(facing) ? "outer_left" : "outer_right";
        }
        int back = w.Block(x - fx, y, z - fz), bm = w.Meta(x - fx, y, z - fz);
        if (IsStairs(back) && StairHalf(bm) == half)
        {
            string f2 = StairFacing(bm);
            if (XAxis(f2) != XAxis(facing) && IsDifferent(f2))
                return f2 == RotateYCCW(facing) ? "inner_left" : "inner_right";
        }
        return "straight";
    }
}
