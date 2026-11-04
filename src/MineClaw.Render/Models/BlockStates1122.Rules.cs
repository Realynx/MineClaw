namespace MineClaw.Render.Models;

/// <summary>
/// The per-block rules of <see cref="BlockStates1122.Resolve"/>: one <see cref="Rule"/> per block id (<c>getStateFromMeta</c>, the
/// client's state mapper and, for the connected blocks, <c>getActualState</c>), grouped by family. An id without a rule is its
/// registry name with no properties.
/// </summary>
public static partial class BlockStates1122
{
    /// <summary>One block's resolution: its id, meta, position, neighbours and the properties being filled in.</summary>
    private readonly struct Ctx(int id, int meta, IVoxelWorld? w, int x, int y, int z, Func<int, int, bool> solidFull,
        SortedDictionary<string, string> props, string name)
    {
        public int Id { get; } = id;
        public int Meta { get; } = meta;
        public IVoxelWorld? World { get; } = w;
        public int X { get; } = x;
        public int Y { get; } = y;
        public int Z { get; } = z;
        public SortedDictionary<string, string> P { get; } = props;
        public string Name { get; } = name;

        public StateRef S(string file, string? key = null) => new(file, P, key);
        public StateRef Named() => new(Name, P);
        public StateRef Builtin() => new(Name, P, null, true);
        public int Bk(int dx, int dy, int dz) => World?.Block(X + dx, Y + dy, Z + dz) ?? 0;
        public int Mt(int dx, int dy, int dz) => World?.Meta(X + dx, Y + dy, Z + dz) ?? 0;

        /// <summary>A solid full cube neighbour that attaches (not an exception block).</summary>
        public bool Solid(int dx, int dy, int dz)
        {
            int b = Bk(dx, dy, dz);
            return b != 0 && !AttachException(b) && solidFull(b, Mt(dx, dy, dz));
        }
    }

    private delegate StateRef Rule(in Ctx c);

    private static readonly string[] Horizontals = ["north", "east", "south", "west"];

    private static string Half(int meta) => (meta & 8) != 0 ? "top" : "bottom";

    /// <summary>The registry-named state with properties read from the meta.</summary>
    private static Rule Props(params (string Key, Func<int, string> Value)[] props) => (in Ctx c) =>
    {
        foreach (var (k, v) in props) c.P[k] = v(c.Meta);
        return c.Named();
    };

    /// <summary>A file named by the meta, no properties.</summary>
    private static Rule File(Func<int, string> file) => (in Ctx c) => c.S(file(c.Meta));

    private static void Add(Rule?[] rules, Rule rule, params int[] ids)
    {
        foreach (int id in ids) rules[id] = rule;
    }

    private static void AddRange(Rule?[] rules, int from, int to, Rule rule)
    {
        for (int id = from; id <= to; id++) rules[id] = rule;
    }

    private static Rule?[] BuildRules()
    {
        var r = new Rule?[256];
        AddTerrain(r);
        AddWood(r);
        AddColoured(r);
        AddSlabs(r);
        AddFacing(r);
        AddRedstone(r);
        AddPlants(r);
        AddConnected(r);
        AddMisc(r);
        return r;
    }

    // ------------------------------------------------------------------ families

    private static void AddTerrain(Rule?[] r)
    {
        Add(r, File(m => (m % 7) switch { 1 => "granite", 2 => "smooth_granite", 3 => "diorite", 4 => "smooth_diorite", 5 => "andesite", 6 => "smooth_andesite", _ => "stone" }), 1);
        Add(r, (in Ctx c) => { c.P["snowy"] = B(c.Bk(0, 1, 0) is 78 or 80); return c.Named(); }, 2, 110);
        Add(r, Dirt, 3);
        Add(r, File(m => (m & 1) == 1 ? "red_sand" : "sand"), 12);
        Add(r, Props(("wet", m => B((m & 1) == 1))), 19);
        Add(r, File(m => (m % 3) switch { 1 => "chiseled_sandstone", 2 => "smooth_sandstone", _ => "sandstone" }), 24);
        Add(r, File(m => (m % 3) switch { 1 => "chiseled_red_sandstone", 2 => "smooth_red_sandstone", _ => "red_sandstone" }), 179);
        Add(r, Props(("layers", m => ((m & 7) + 1).ToString())), 78);
        Add(r, (in Ctx c) => c.S("snow"), 80);
        Add(r, File(m => (m % 6) switch { 1 => "cobblestone", 2 => "stone_brick", 3 => "mossy_brick", 4 => "cracked_brick", 5 => "chiseled_brick", _ => "stone" } + "_monster_egg"), 97);
        Add(r, File(m => (m & 3) switch { 1 => "mossy_stonebrick", 2 => "cracked_stonebrick", 3 => "chiseled_stonebrick", _ => "stonebrick" }), 98);
        Add(r, File(m => (m % 3) switch { 1 => "prismarine_bricks", 2 => "dark_prismarine", _ => "prismarine" }), 168);
        Add(r, Props(("axis", m => (m & 12) switch { 4 => "x", 8 => "z", _ => "y" })), 170, 202, 216);
        Add(r, Quartz, 155);
    }

    private static StateRef Dirt(in Ctx c)
    {
        if (c.Meta % 3 == 2) { c.P["snowy"] = B(c.Bk(0, 1, 0) is 78 or 80); return c.S("podzol"); }
        return c.S(c.Meta % 3 == 1 ? "coarse_dirt" : "dirt");
    }

    private static StateRef Quartz(in Ctx c) => c.Meta switch
    {
        1 => c.S("chiseled_quartz_block"),
        2 => c.S("quartz_column", "axis=y"),
        3 => c.S("quartz_column", "axis=x"),
        4 => c.S("quartz_column", "axis=z"),
        _ => c.S("quartz_block"),
    };

    private static void AddWood(Rule?[] r)
    {
        Add(r, File(m => Wood[m % 6] + "_planks"), 5);
        Add(r, (in Ctx c) => { c.P["stage"] = ((c.Meta >> 3) & 1).ToString(); return c.S(Wood[(c.Meta & 7) % 6] + "_sapling"); }, 6);
        Add(r, (in Ctx c) =>
        {
            c.P["axis"] = (c.Meta & 12) switch { 0 => "y", 4 => "x", 8 => "z", _ => "none" };
            return c.S(Wood[c.Id == 162 ? Math.Min(5, (c.Meta & 3) + 4) : c.Meta & 3] + "_log");
        }, 17, 162);
        Add(r, File(m => Wood[m & 3] + "_leaves"), 18);
        Add(r, File(m => Wood[4 + (m & 1)] + "_leaves"), 161);
    }

    private static void AddColoured(Rule?[] r)
    {
        Add(r, File(m => Colors[m & 15] + "_wool"), 35);
        Add(r, File(m => Colors[m & 15] + "_carpet"), 171);
        Add(r, File(m => Colors[m & 15] + "_stained_hardened_clay"), 159);
        Add(r, File(m => Colors[m & 15] + "_stained_glass"), 95);
        Add(r, File(m => Colors[m & 15] + "_concrete"), 251);
        Add(r, File(m => Colors[m & 15] + "_concrete_powder"), 252);
        Add(r, (in Ctx c) => { Pane(c); return c.S(Colors[c.Meta & 15] + "_stained_glass_pane"); }, 160);
        AddRange(r, 219, 234, (in Ctx c) => c.S("mineclaw_shulker_" + Colors[c.Id - 219]));
        AddRange(r, 235, 250, (in Ctx c) => { c.P["facing"] = HFacing(c.Meta); return c.S(Colors[c.Id - 235] + "_glazed_terracotta"); });
    }

    private static void AddSlabs(Rule?[] r)
    {
        Add(r, (in Ctx c) => c.S(StoneSlab[c.Meta & 7] + "_double_slab", (c.Meta & 8) != 0 ? "all" : "normal"), 43);
        Add(r, (in Ctx c) => { c.P["half"] = Half(c.Meta); return c.S(StoneSlab[c.Meta & 7] + "_slab"); }, 44);
        Add(r, File(m => Wood[(m & 7) % 6] + "_double_slab"), 125);
        Add(r, (in Ctx c) => { c.P["half"] = Half(c.Meta); return c.S(Wood[(c.Meta & 7) % 6] + "_slab"); }, 126);
        Add(r, (in Ctx c) => c.S("red_sandstone_double_slab", (c.Meta & 8) != 0 ? "all" : "normal"), 181);
        Add(r, (in Ctx c) => { c.P["half"] = Half(c.Meta); return c.S(c.Id == 182 ? "red_sandstone_slab" : "purpur_slab"); }, 182, 205);
        Add(r, (in Ctx c) => c.S("purpur_double_slab"), 204);
    }

    private static void AddFacing(Rule?[] r)
    {
        Add(r, Props(("facing", m => Facing(m & 7))), 23, 158);
        Add(r, Props(("facing", m => Facing(m & 7)), ("extended", m => B((m & 8) != 0))), 29, 33);
        Add(r, Props(("facing", m => Facing(m & 7)), ("type", m => (m & 8) != 0 ? "sticky" : "normal"), ("short", _ => "false")), 34);
        Add(r, Props(("facing", m => m switch { 1 => "east", 2 => "west", 3 => "south", 4 => "north", _ => "up" })), 50, 75, 76);
        Add(r, Chest, 54, 130, 146);
        Add(r, Props(("facing", Horizontal)), 61, 62, 65);
        Add(r, Props(("facing", HFacing)), 86, 91);
        Add(r, Props(("facing", m => (m & 3) switch { 0 => "north", 1 => "south", 2 => "west", _ => "east" }), ("open", m => B((m & 4) != 0)),
            ("half", Half)), 96, 167);
        Add(r, Props(("eye", m => B((m & 4) != 0)), ("facing", m => HFacing(m & 3))), 120);
        Add(r, Props(("facing", m => HFacing(m & 3)), ("damage", m => ((m >> 2) % 3) switch { 1 => "slightly_damaged", 2 => "very_damaged", _ => "intact" })), 145);
        Add(r, Props(("facing", m => Facing(m & 7) == "up" ? "down" : Facing(m & 7)), ("enabled", m => B((m & 8) == 0))), 154);
        Add(r, Props(("facing", Facing)), 198);
        Add(r, Props(("facing", m => Facing(m & 7)), ("powered", m => B((m & 8) != 0))), 218);
        Add(r, Props(("facing", m => Facing(m & 7)), ("conditional", m => B((m & 8) != 0))), 137, 210, 211);
        Add(r, Props(("mode", m => (m & 3) switch { 1 => "load", 2 => "corner", 3 => "data", _ => "save" })), 255);
    }

    private static StateRef Chest(in Ctx c)
    {
        // A pair is one large chest (LargeChest): each cell draws its half of the 30-wide model, lid and latch across.
        string kind = c.Id == 130 ? "ender" : c.Id == 146 ? "trapped" : "normal";
        if (c.World is { } w && c.Id != 130 && LargeChest.Partner(w, c.X, c.Y, c.Z) is { } mate)
        {
            int f = LargeChest.PairFacing(w, c.X, c.Y, c.Z, mate);
            c.P["facing"] = Facing(f);
            c.P["half"] = LargeChest.ModelHalf(f, mate);
            return c.S("mineclaw_chest_" + kind + "_double");
        }
        c.P["facing"] = Horizontal(c.Meta);
        return c.S("mineclaw_chest_" + kind);
    }

    private static void AddRedstone(Rule?[] r)
    {
        Add(r, Props(("shape", m => Rail[(m & 7) % 6]), ("powered", m => B((m & 8) != 0))), 27, 28, 157);
        Add(r, Props(("shape", m => Rail[m % 10])), 66);
        Add(r, RedstoneWire, 55);
        Add(r, Props(("facing", m => Lever[m & 7]), ("powered", m => B((m & 8) != 0))), 69);
        Add(r, Props(("powered", m => B(m == 1))), 70, 72);
        Add(r, Props(("power", m => (m & 15).ToString())), 147, 148);
        Add(r, Props(("facing", m => Button[m & 7]), ("powered", m => B((m & 8) != 0))), 77, 143);
        Add(r, Props(("facing", HFacing), ("delay", m => (1 + ((m >> 2) & 3)).ToString()), ("locked", _ => "false")), 93, 94);
        Add(r, Props(("facing", HFacing), ("mode", m => (m & 4) != 0 ? "subtract" : "compare"), ("powered", m => B((m & 8) != 0))), 149, 150);
        Add(r, Props(("facing", m => HFacing(m & 3)), ("attached", m => B((m & 4) != 0)), ("powered", m => B((m & 8) != 0))), 131);
    }

    private static StateRef RedstoneWire(in Ctx c)
    {
        foreach (var f in Horizontals) c.P[f] = Wire(c, f);
        c.P["power"] = (c.Meta & 15).ToString(); // ignored by the model (the mapper drops it); the tint reads it
        return c.Named();
    }

    private static string Wire(in Ctx c, string f)
    {
        var (dx, dz) = Step(f);
        int side = c.Bk(dx, 0, dz);
        if (WireAttracts(side, c.Mt(dx, 0, dz), f)) return "side";
        bool sideSolid = c.Solid(dx, 0, dz);
        if (!sideSolid && c.Bk(dx, -1, dz) == 55) return "side";
        if (sideSolid && !c.Solid(0, 1, 0) && c.Bk(dx, 1, dz) == 55) return "up";
        return "none";
    }

    private static void AddPlants(Rule?[] r)
    {
        Add(r, File(m => (m % 3) switch { 0 => "dead_bush", 2 => "fern", _ => "tall_grass" }), 31);
        Add(r, (in Ctx c) => c.S("dead_bush"), 32);
        Add(r, (in Ctx c) => c.S("dandelion"), 37);
        Add(r, File(m => Flowers[m % 9]), 38);
        Add(r, Props(("age", m => (m & 7).ToString())), 59, 141, 142);
        Add(r, Props(("age", m => (m & 3).ToString())), 207, 115, 212);
        Add(r, Props(("moisture", m => (m & 7).ToString())), 60);
        Add(r, Props(("variant", m => Mushroom[m & 15])), 99, 100);
        Add(r, Stem, 104, 105);
        Add(r, (in Ctx c) =>
        {
            c.P["south"] = B((c.Meta & 1) != 0); c.P["west"] = B((c.Meta & 2) != 0); c.P["north"] = B((c.Meta & 4) != 0); c.P["east"] = B((c.Meta & 8) != 0);
            c.P["up"] = B(c.Solid(0, 1, 0));
            return c.Named();
        }, 106);
        Add(r, Props(("facing", m => HFacing(m & 3)), ("age", m => Math.Min(2, (m & 15) >> 2).ToString())), 127);
        Add(r, DoublePlant, 175);
        Add(r, Props(("age", m => Math.Min(5, m).ToString())), 200);
        Add(r, Props(("bites", m => Math.Min(6, m).ToString())), 92);
    }

    private static StateRef Stem(in Ctx c)
    {
        c.P["age"] = (c.Meta & 7).ToString();
        string facing = "up";
        if ((c.Meta & 7) == 7)
            foreach (var f in Horizontals)
            {
                var (dx, dz) = Step(f);
                if (c.Bk(dx, 0, dz) == (c.Id == 104 ? 86 : 103)) { facing = f; break; }
            }
        c.P["facing"] = facing;
        if (facing != "up") c.P.Remove("age");
        return c.Named();
    }

    private static StateRef DoublePlant(in Ctx c)
    {
        bool upper = (c.Meta & 8) != 0;
        int variant = upper ? (c.Bk(0, -1, 0) == 175 ? c.Mt(0, -1, 0) & 7 : 0) : c.Meta & 7;
        c.P["half"] = upper ? "upper" : "lower";
        return c.S(DoublePlants[variant % 6]);
    }

    private static void AddConnected(Rule?[] r)
    {
        Add(r, (in Ctx c) => { Pane(c); return c.Named(); }, 101, 102);
        Rule fence = (in Ctx c) => { Fence(c); return c.Named(); };
        Add(r, fence, 85, 113);
        AddRange(r, 188, 192, fence);
        Add(r, FenceGate, 107);
        AddRange(r, 183, 187, FenceGate);
        Add(r, (in Ctx c) => { Wall(c); return c.S((c.Meta == 1 ? "mossy_cobblestone" : "cobblestone") + "_wall"); }, 139);
        Rule door = (in Ctx c) => { Door(c); return c.Named(); };
        Add(r, door, 64, 71);
        AddRange(r, 193, 197, door);
        Add(r, Stairs, 53, 67, 108, 109, 114, 128, 134, 135, 136, 156, 163, 164, 180, 203);
    }

    private static void Fence(in Ctx c)
    {
        foreach (var f in Horizontals)
        {
            var (dx, dz) = Step(f);
            int b = c.Bk(dx, 0, dz);
            bool same = c.Id == 113 ? b == 113 : IsWoodFence(b);
            c.P[f] = B(same || IsFenceGate(b) || c.Solid(dx, 0, dz));
        }
    }

    private static void Pane(in Ctx c)
    {
        foreach (var f in Horizontals)
        {
            var (dx, dz) = Step(f);
            c.P[f] = B(IsPane(c.Bk(dx, 0, dz)) || c.Solid(dx, 0, dz));
        }
    }

    private static bool WallAttaches(in Ctx c, int dx, int dz) => c.Bk(dx, 0, dz) == 139 || IsFenceGate(c.Bk(dx, 0, dz)) || c.Solid(dx, 0, dz);

    private static void Wall(in Ctx c)
    {
        bool n = WallAttaches(c, 0, -1), e = WallAttaches(c, 1, 0), s = WallAttaches(c, 0, 1), wv = WallAttaches(c, -1, 0);
        bool straight = n && !e && s && !wv || !n && e && !s && wv;
        c.P["north"] = B(n); c.P["east"] = B(e); c.P["south"] = B(s); c.P["west"] = B(wv);
        c.P["up"] = B(!straight || c.Bk(0, 1, 0) != 0);
    }

    private static StateRef FenceGate(in Ctx c)
    {
        string f = HFacing(c.Meta);
        c.P["facing"] = f; c.P["open"] = B((c.Meta & 4) != 0);
        bool zAxis = f is "north" or "south";
        c.P["in_wall"] = B(zAxis ? c.Bk(-1, 0, 0) == 139 || c.Bk(1, 0, 0) == 139 : c.Bk(0, 0, -1) == 139 || c.Bk(0, 0, 1) == 139);
        return c.Named();
    }

    private static void Door(in Ctx c)
    {
        bool upper = (c.Meta & 8) != 0;
        int lower = upper ? (c.Bk(0, -1, 0) == c.Id ? c.Mt(0, -1, 0) : 0) : c.Meta;
        int top = upper ? c.Meta : (c.Bk(0, 1, 0) == c.Id ? c.Mt(0, 1, 0) : 8);
        c.P["half"] = upper ? "upper" : "lower";
        c.P["facing"] = RotateYCCW(HFacing(lower & 3));
        c.P["open"] = B((lower & 4) != 0);
        c.P["hinge"] = (top & 1) != 0 ? "right" : "left";
        c.P["powered"] = B((top & 2) != 0);
    }

    private static StateRef Stairs(in Ctx c)
    {
        c.P["facing"] = StairFacing(c.Meta);
        c.P["half"] = StairHalf(c.Meta);
        c.P["shape"] = c.World is { } w ? StairShape(w, c.X, c.Y, c.Z, c.Meta) : "straight";
        return c.Named();
    }

    private static void AddMisc(Rule?[] r)
    {
        // Fluids and fire: the procedural b1.8.1 path; beds, signs, skulls, banners, piston heads...: tile-entity renderers.
        Add(r, (in Ctx c) => c.Builtin(), 8, 9, 10, 11, 51, 26, 63, 68, 144, 176, 177, 119, 209, 166, 36, 217);
        Add(r, Props(("axis", m => (m & 3) == 2 ? "z" : "x")), 90);
        Add(r, Props(("has_bottle_0", m => B((m & 1) != 0)), ("has_bottle_1", m => B((m & 2) != 0)), ("has_bottle_2", m => B((m & 4) != 0))), 117);
        Add(r, Props(("level", m => (m & 3).ToString())), 118);
    }
}
