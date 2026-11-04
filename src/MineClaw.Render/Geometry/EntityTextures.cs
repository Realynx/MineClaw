using System.Collections.Concurrent;
using MineClaw.Data;

namespace MineClaw.Render;

/// <summary>
/// Entity textures by id: the player skin (<c>mob/char.png</c> from the atlas), the mob textures (<c>mob/*.png</c>, read from the
/// cached client jar or the assets dir when present: never downloaded) and, when a mob texture isn't on this machine, a
/// procedural stand-in painted onto the same UV layout (flat part colours with a little noise, the eyes and faces drawn in),
/// so models stay recognisable and their facing reads correctly.
/// </summary>
public static class EntityTextures
{
    private static readonly string[] Names =
        ["char", "zombie", "pigzombie", "skeleton", "creeper", "spider", "pig", "cow", "sheep", "sheep_fur", "chicken", "enderman", "slime", "wolf", "squid", "ghast", "silverfish"];

    private static readonly ConcurrentDictionary<int, (int W, int H, uint[] Px)> Procedural = new();

    public static int Id(string name)
    {
        int i = Array.IndexOf(Names, name);
        return i < 0 ? 1 : i;
    }

    public static string Name(int id) => (uint)id < (uint)Names.Length ? Names[id] : "zombie";

    /// <summary>Whether the real texture of <paramref name="id"/> is available (false = procedural stand-in).</summary>
    public static bool IsReal(TextureAtlas? atlas, int id) =>
        id == 0 ? atlas?.Skin is { W: >= 64, H: >= 32 } : atlas?.Mobs is { } m && m.ContainsKey(Name(id));

    /// <summary>The texture image (0xAARRGGBB).</summary>
    public static (int W, int H, uint[] Px) Get(TextureAtlas? atlas, int id)
    {
        if (id == 0 && atlas?.Skin is { W: >= 64, H: >= 32 } skin) return skin;
        if (atlas?.Mobs is { } mobs && mobs.TryGetValue(Name(id), out var real)) return real;
        return Procedural.GetOrAdd(id, Paint);
    }

    /// <summary>EntitySheep.fleeceColorTable (b1.8.1), by wool colour 0..15.</summary>
    public static uint Fleece(int c)
    {
        float[][] t =
        [
            [1, 1, 1], [0.95f, 0.7f, 0.2f], [0.9f, 0.5f, 0.85f], [0.6f, 0.7f, 0.95f], [0.9f, 0.9f, 0.2f], [0.5f, 0.8f, 0.1f], [0.95f, 0.7f, 0.8f],
            [0.3f, 0.3f, 0.3f], [0.6f, 0.6f, 0.6f], [0.3f, 0.6f, 0.7f], [0.7f, 0.4f, 0.9f], [0.2f, 0.4f, 0.8f], [0.5f, 0.4f, 0.3f], [0.4f, 0.5f, 0.2f],
            [0.8f, 0.3f, 0.3f], [0.1f, 0.1f, 0.1f],
        ];
        var f = t[c & 15];
        return (uint)(f[0] * 255) << 16 | (uint)(f[1] * 255) << 8 | (uint)(f[2] * 255);
    }

    // ------------------------------------------------------------------ procedural stand-ins

    private static string ModelOf(string tex) => tex switch { "sheep_fur" => "sheep", "char" => "player", _ => tex };

    private static bool IsHead(string part) => part is "head" or "hat";
    private static bool IsArm(string part) => part is "rarm" or "larm";

    /// <summary>The base colour of a model part in a procedural texture, per texture.</summary>
    private static readonly Dictionary<string, Func<string, uint>> PartColours = new(StringComparer.Ordinal)
    {
        ["char"] = part => IsHead(part) ? 0xc69680u : part == "body" ? 0x2f9fb0u : IsArm(part) ? 0xc69680u : 0x3a3a90u,
        ["zombie"] = part => IsHead(part) || IsArm(part) ? 0x5f9a46u : part == "body" ? 0x2b8a8fu : 0x3b3e8fu,
        ["pigzombie"] = part => IsHead(part) ? 0xe3a2a0u : IsArm(part) ? 0xd8968eu : part == "body" ? 0x7a5634u : 0x5b4226u,
        ["skeleton"] = _ => 0xc9c9c4u,
        ["creeper"] = _ => 0x5cb84au,
        ["spider"] = part => part == "head" ? 0x2c241eu : part == "body" ? 0x3a3028u : 0x332a22u,
        ["pig"] = _ => 0xefa5a3u,
        ["cow"] = part => part == "head" ? 0x4a3326u : part == "body" ? 0x433024u : 0x4f3a2cu,
        ["sheep"] = part => part == "head" ? 0xc9b59cu : 0xd8cab4u,
        ["sheep_fur"] = _ => 0xefefefu,
        ["chicken"] = part => part.StartsWith("leg") ? 0xf2b030u : 0xf3f3f3u,
        ["enderman"] = _ => 0x141414u,
        ["slime"] = part => part == "inner" ? 0x4a9a3au : 0x72c45cu,
        ["wolf"] = _ => 0xd9d2c8u,
        ["squid"] = _ => 0x2c3d5eu,
        ["ghast"] = _ => 0xefefefu,
        ["silverfish"] = _ => 0x8c8c8cu,
    };

    private static uint PartColour(string tex, string part) => PartColours.TryGetValue(tex, out var c) ? c(part) : 0x808080u;

    /// <summary>A 64×32 procedural texture being painted: noisy rectangles from a seeded generator (the order of the calls is
    /// part of the picture).</summary>
    private sealed class Painter(int seed)
    {
        public const int W = 64, H = 32;
        public readonly uint[] Px = new uint[W * H];
        public readonly Random Rnd = new(seed);

        public void Rect(int x, int y, int w, int h, uint c, bool noise = true)
        {
            for (int j = y; j < y + h; j++)
                for (int i = x; i < x + w; i++)
                {
                    if ((uint)i >= W || (uint)j >= H) continue;
                    float k = noise ? 0.88f + (float)Rnd.NextDouble() * 0.2f : 1f;
                    Px[j * W + i] = 0xff000000 | Lighting.Scale(c, Math.Min(1f, k)) & 0xffffff;
                }
        }
    }

    private static (int W, int H, uint[] Px) Paint(int id)
    {
        string tex = Name(id);
        var p = new Painter(1000 + id);
        var model = EntityGeometry.Create(ModelOf(tex));
        foreach (var part in model.Parts) PaintPart(p, tex, model, part);
        // Faces on the head's front (u+d, v+d, w×h): eyes and the like, so a mob's facing is readable.
        var headPart = model.Parts.FirstOrDefault(part => part.Name == "head");
        if (headPart != null && tex != "sheep_fur" && Faces.TryGetValue(tex, out var face))
        {
            var hb = headPart.Boxes[0];
            face(p, hb.U + hb.D, hb.V + hb.D, hb.W, hb.H);
        }
        if (tex == "cow") CowPatches(p);
        if (tex == "slime") SlimeJelly(p);
        return (Painter.W, Painter.H, p.Px);
    }

    /// <summary>White patches on the hide.</summary>
    private static void CowPatches(Painter p)
    {
        for (int i = 0; i < 6; i++) p.Rect(18 + p.Rnd.Next(40), 4 + p.Rnd.Next(24), 3 + p.Rnd.Next(4), 2 + p.Rnd.Next(4), 0xe8e8e8);
    }

    /// <summary>The outer cube translucent, a dark core.</summary>
    private static void SlimeJelly(Painter p)
    {
        for (int i = 0; i < Painter.W * Painter.H; i++) if (p.Px[i] != 0 && i / Painter.W < 16) p.Px[i] = (p.Px[i] & 0xffffff) | 0xb0000000;
        p.Rect(32, 0, 8, 12, 0x1c3a18, false);
    }

    /// <summary>One part's boxes in its colour (the sheep's fur parts only in the fur texture, never the hat layer).</summary>
    private static void PaintPart(Painter p, string tex, EntityModel model, ModelPart part)
    {
        bool fur = part.Name.StartsWith("wool");
        if (tex == "sheep_fur" != fur && model.Kind == "sheep") return;
        if (part.Name == "hat") return; // the hat layer stays transparent (an overlay, not a second head)
        foreach (var b in part.Boxes)
        {
            uint c = PartColour(tex, part.Name);
            // A head's extra boxes are the snout / beak / horns: colour them apart.
            if (part.Name == "head" && b != part.Boxes[0]) c = HeadExtraColour(tex, b, c);
            if (tex == "cow" && part.Name == "body" && b != part.Boxes[0]) c = 0xe8a0a0;
            p.Rect(b.U, b.V, 2 * (b.D + b.W), b.D + b.H, c);
        }
    }

    private static uint HeadExtraColour(string tex, ModelBox b, uint c) =>
        tex switch { "pig" => 0xd88a88, "chicken" => b.V == 4 ? 0xd02020u : 0xf2b030u, "cow" => 0xd8d6c8, "wolf" => 0xc9c2b8, _ => c };

    /// <summary>The face painted on a head's front (painter, front x, y, width, height), per texture.</summary>
    private static readonly Dictionary<string, Action<Painter, int, int, int, int>> Faces = new(StringComparer.Ordinal)
    {
        ["char"] = (p, fx, fy, fw, fh) =>
        {
            p.Rect(fx, fy, fw, 2, 0x3b2a1a); p.Rect(fx + 1, fy + 4, 2, 1, 0xffffff, false); p.Rect(fx + 2, fy + 4, 1, 1, 0x4a3aa0, false);
            p.Rect(fx + 5, fy + 4, 2, 1, 0xffffff, false); p.Rect(fx + 5, fy + 4, 1, 1, 0x4a3aa0, false); p.Rect(fx + 3, fy + 6, 2, 1, 0x8a4a3a, false);
        },
        ["zombie"] = ZombieFace,
        ["pigzombie"] = ZombieFace,
        ["skeleton"] = (p, fx, fy, fw, fh) =>
        {
            p.Rect(fx + 1, fy + 3, 2, 2, 0x202020, false); p.Rect(fx + 5, fy + 3, 2, 2, 0x202020, false); p.Rect(fx + 2, fy + 6, 4, 1, 0x303030, false);
        },
        ["creeper"] = (p, fx, fy, fw, fh) =>
        {
            p.Rect(fx + 1, fy + 2, 2, 2, 0x0a0a0a, false); p.Rect(fx + 5, fy + 2, 2, 2, 0x0a0a0a, false);
            p.Rect(fx + 3, fy + 4, 2, 3, 0x0a0a0a, false); p.Rect(fx + 2, fy + 5, 1, 3, 0x0a0a0a, false); p.Rect(fx + 5, fy + 5, 1, 3, 0x0a0a0a, false);
        },
        ["spider"] = (p, fx, fy, fw, fh) =>
        {
            foreach (var (ex, ey) in new[] { (1, 2), (6, 2), (2, 3), (5, 3), (3, 2), (4, 2), (1, 4), (6, 4) }) p.Rect(fx + ex, fy + ey, 1, 1, 0xd01818, false);
        },
        ["pig"] = (p, fx, fy, fw, fh) =>
        {
            p.Rect(fx + 1, fy + 2, 1, 1, 0xffffff, false); p.Rect(fx + 6, fy + 2, 1, 1, 0xffffff, false);
            p.Rect(fx + 1, fy + 3, 1, 1, 0x101010, false); p.Rect(fx + 6, fy + 3, 1, 1, 0x101010, false);
        },
        ["cow"] = (p, fx, fy, fw, fh) =>
        {
            p.Rect(fx + 3, fy, 2, fh, 0xe6e6e6); p.Rect(fx + 1, fy + 2, 1, 1, 0x101010, false); p.Rect(fx + fw - 2, fy + 2, 1, 1, 0x101010, false);
        },
        ["sheep"] = SideEyes,
        ["chicken"] = (p, fx, fy, fw, fh) => { p.Rect(fx, fy + 1, 1, 1, 0x101010, false); p.Rect(fx + fw - 1, fy + 1, 1, 1, 0x101010, false); },
        ["enderman"] = (p, fx, fy, fw, fh) => { p.Rect(fx + 1, fy + 4, 2, 1, 0xd65cff, false); p.Rect(fx + 5, fy + 4, 2, 1, 0xd65cff, false); },
        ["wolf"] = SideEyes,
    };

    private static void ZombieFace(Painter p, int fx, int fy, int fw, int fh)
    {
        p.Rect(fx + 1, fy + 4, 2, 1, 0x101010, false); p.Rect(fx + 5, fy + 4, 2, 1, 0x101010, false); p.Rect(fx + 2, fy + 6, 4, 1, 0x2a4a20, false);
    }

    private static void SideEyes(Painter p, int fx, int fy, int fw, int fh)
    {
        p.Rect(fx + 1, fy + 2, 1, 1, 0x101010, false); p.Rect(fx + fw - 2, fy + 2, 1, 1, 0x101010, false);
    }

    /// <summary>Texel (0xAARRGGBB) at normalised (u, v) of texture <paramref name="id"/>.</summary>
    public static uint Sample(TextureAtlas? atlas, int id, float u, float v)
    {
        var (w, h, px) = Get(atlas, id);
        int x = (int)(u * w), y = (int)(v * h);
        x = x < 0 ? 0 : x >= w ? w - 1 : x;
        y = y < 0 ? 0 : y >= h ? h - 1 : y;
        return px[y * w + x];
    }
}

/// <summary>
/// Item icons for dropped items and held items: b1.8.1 Item icon indices into <c>gui/items.png</c> (Item.a(x, y) = y·16 + x),
/// and terrain tiles for block items. Unknown items fall back to a flat colour.
/// </summary>
public static class ItemIcons
{
    private static readonly Dictionary<int, int> Icons = new()
    {
        [256] = 82, [257] = 98, [258] = 114, [259] = 5, [260] = 10, [261] = 21, [262] = 37, [263] = 7, [264] = 55, [265] = 23, [266] = 39,
        [267] = 66, [268] = 64, [269] = 80, [270] = 96, [271] = 112, [272] = 65, [273] = 81, [274] = 97, [275] = 113, [276] = 67, [277] = 83,
        [278] = 99, [279] = 115, [280] = 53, [281] = 71, [282] = 72, [283] = 68, [284] = 84, [285] = 100, [286] = 116, [287] = 8, [288] = 24,
        [289] = 40, [290] = 128, [291] = 129, [292] = 130, [293] = 131, [294] = 132, [295] = 9, [296] = 25, [297] = 41, [298] = 0, [299] = 16,
        [300] = 32, [301] = 48, [302] = 1, [303] = 17, [304] = 33, [305] = 49, [306] = 2, [307] = 18, [308] = 34, [309] = 50, [310] = 3,
        [311] = 19, [312] = 35, [313] = 51, [314] = 4, [315] = 20, [316] = 36, [317] = 52, [318] = 6, [319] = 87, [320] = 88, [321] = 26,
        [322] = 11, [323] = 42, [324] = 43, [325] = 74, [326] = 75, [327] = 76, [328] = 135, [329] = 104, [330] = 44, [331] = 56, [332] = 14,
        [333] = 136, [334] = 103, [335] = 77, [336] = 22, [337] = 57, [338] = 27, [339] = 58, [340] = 59, [341] = 30, [342] = 151, [343] = 167,
        [344] = 12, [345] = 54, [346] = 69, [347] = 70, [348] = 73, [349] = 89, [350] = 90, [351] = 78, [352] = 28, [353] = 13, [354] = 29,
        [355] = 45, [356] = 86, [357] = 92, [358] = 60, [359] = 93, [360] = 109, [361] = 61, [362] = 62, [363] = 105, [364] = 106, [365] = 121,
        [366] = 122, [367] = 91, [368] = 107, [2256] = 240, [2257] = 241,
    };

    /// <summary>Which texture and tile draw an item: block items use their terrain tile, items their items.png icon.</summary>
    public static (QuadTex Tex, int Tile) Of(int id, int damage)
    {
        if (id is > 0 and < 256)
        {
            var t = BlockTextures.For(id, damage, Face.North);
            if (id == Blocks.Torch) return (QuadTex.Terrain, 80);
            return t.IsNone ? (QuadTex.Flat, -1) : (QuadTex.Terrain, t.Tile);
        }
        if (id == 351) return (QuadTex.Item, 78 + (damage & 15) % 8 * 16 + (damage & 15) / 8);
        return Icons.TryGetValue(id, out var i) ? (QuadTex.Item, i) : (QuadTex.Flat, -1);
    }

    /// <summary>A flat colour for an item (no texture available).</summary>
    public static uint Colour(int id) =>
        id is > 0 and < 256 ? BlockGeometry.FlatColour(id, Face.North) : Colours.TryGetValue(id, out var c) ? c : 0xe0d070;

    private static readonly Dictionary<int, uint> Colours = new()
    {
        [263] = 0x2a2a2a, [264] = 0x5fe0e0, [265] = 0xd8d8d8, [266] = 0xf0d040, [280] = 0x8a6a3a, [295] = 0x6ab040, [296] = 0xc8b048, [331] = 0xc01010,
        [338] = 0x8ac060, [344] = 0xf0e0c0, [352] = 0xe8e8e0, [287] = 0xf0f0f0, [288] = 0xf0f0f0, [289] = 0x505050, [334] = 0x8a5030, [337] = 0xa0a8b8,
    };
}

/// <summary>
/// The animated b1.8.1 liquid textures (blank in terrain.png: the client generates them every tick). A frame of each after
/// a fixed warm-up, from the client's TextureWaterFX / TextureWaterFlowFX / TextureLavaFX / TextureLavaFlowFX recurrences
/// (seeded, so pictures are deterministic). 16×16, 0xAARRGGBB; water alpha 146..196 like the client.
/// </summary>
public static class FluidTextures
{
    public static readonly uint[] WaterStill = Water(flow: false), WaterFlow = Water(flow: true), LavaStill = Lava(flow: false), LavaFlow = Lava(flow: true);

    private static uint[] Water(bool flow)
    {
        float[] a = new float[256], b = new float[256], c = new float[256], d = new float[256];
        var rnd = new Random(flow ? 81 : 80);
        for (int tick = 0; tick < 240; tick++)
        {
            WaterSpread(a, b, c, flow);
            WaterDrops(c, d, rnd, flow);
            (a, b) = (b, a);
        }
        var px = new uint[256];
        for (int i = 0; i < 256; i++)
        {
            float f1 = Math.Clamp(a[i], 0, 1), f2 = f1 * f1;
            uint r = (uint)(32 + f2 * 32), g = (uint)(50 + f2 * 64), bl = 255, al = (uint)(146 + f2 * 50);
            px[i] = al << 24 | r << 16 | g << 8 | bl;
        }
        return px;
    }

    /// <summary>One water tick's blur of field a into b (still: across i; flow: the two rows above along j), plus the heat c.</summary>
    private static void WaterSpread(float[] a, float[] b, float[] c, bool flow)
    {
        for (int i = 0; i < 16; i++)
            for (int j = 0; j < 16; j++)
            {
                float f = 0;
                if (flow) for (int k = j - 2; k <= j; k++) f += a[(i & 15) + (k & 15) * 16];
                else for (int k = i - 1; k <= i + 1; k++) f += a[(k & 15) + (j & 15) * 16];
                b[i + j * 16] = f / (flow ? 3.2f : 3.3f) + c[i + j * 16] * 0.8f;
            }
    }

    /// <summary>One water tick's heat c and its decaying drive d, with random new drops.</summary>
    private static void WaterDrops(float[] c, float[] d, Random rnd, bool flow)
    {
        for (int i = 0; i < 256; i++)
        {
            c[i] += d[i] * 0.05f;
            if (c[i] < 0) c[i] = 0;
            d[i] -= flow ? 0.3f : 0.1f;
            if (rnd.NextDouble() < (flow ? 0.2 : 0.05)) d[i] = 0.5f;
        }
    }

    private static uint[] Lava(bool flow)
    {
        float[] a = new float[256], b = new float[256], c = new float[256], d = new float[256];
        var rnd = new Random(flow ? 91 : 90);
        for (int tick = 0; tick < 240; tick++)
        {
            for (int i = 0; i < 16; i++)
                for (int j = 0; j < 16; j++)
                    LavaCell(a, b, c, d, rnd, i, j);
            (a, b) = (b, a);
            // TextureLavaFlowFX: the same field scrolling down one row per few ticks (streaks along v).
            if (flow && tick % 3 == 0) a = ScrollDown(a);
        }
        var px = new uint[256];
        for (int i = 0; i < 256; i++)
        {
            float f1 = Math.Clamp(a[i] * 2f, 0, 1);
            uint r = (uint)(f1 * 100 + 155), g = (uint)(f1 * f1 * 255), bl = (uint)(f1 * f1 * f1 * f1 * 128);
            px[i] = 0xff000000 | r << 16 | g << 8 | bl;
        }
        return px;
    }

    /// <summary>One lava cell per tick: a wobbling 3×3 blur of a plus the heat around it into b, then the heat's own step.</summary>
    private static void LavaCell(float[] a, float[] b, float[] c, float[] d, Random rnd, int i, int j)
    {
        float f = 0;
        int l = (int)(MathF.Sin(j * MathF.PI * 2 / 16f) * 1.2f), i1 = (int)(MathF.Sin(i * MathF.PI * 2 / 16f) * 1.2f);
        for (int k = i - 1; k <= i + 1; k++)
            for (int l1 = j - 1; l1 <= j + 1; l1++)
                f += a[((k + l) & 15) + ((l1 + i1) & 15) * 16];
        b[i + j * 16] = f / 10f + (c[(i & 15) + (j & 15) * 16] + c[((i + 1) & 15) + (j & 15) * 16] + c[((i + 1) & 15) + ((j + 1) & 15) * 16]
            + c[(i & 15) + ((j + 1) & 15) * 16]) / 4f * 0.8f;
        c[i + j * 16] += d[i + j * 16] * 0.01f;
        if (c[i + j * 16] < 0) c[i + j * 16] = 0;
        d[i + j * 16] -= 0.06f;
        if (rnd.NextDouble() < 0.005) d[i + j * 16] = 1.5f;
    }

    /// <summary>The field moved down one row (wrapping).</summary>
    private static float[] ScrollDown(float[] a)
    {
        var s = new float[256];
        for (int i = 0; i < 256; i++) s[i] = a[(i + 240) & 255];
        return s;
    }

    /// <summary>TextureFlamesFX frames (the two fire tiles, 31 and 47): a 16×20 heat field rising from a random bottom row;
    /// r = f·155 + 100, g = f²·255, b = f¹⁰·255, alpha cut at f &lt; 0.5.</summary>
    public static readonly uint[] FireA = Fire(31), FireB = Fire(47);

    private static uint[] Fire(int seed)
    {
        float[] a = new float[320], b = new float[320];
        var rnd = new Random(seed);
        for (int tick = 0; tick < 160; tick++)
        {
            for (int i = 0; i < 16; i++)
                for (int j = 0; j < 20; j++)
                {
                    b[i + j * 16] = FireHeat(a, i, j);
                    if (j >= 19) b[i + j * 16] = (float)(rnd.NextDouble() * rnd.NextDouble() * rnd.NextDouble() * 4 + rnd.NextDouble() * 0.1 + 0.2);
                }
            (a, b) = (b, a);
        }
        return FirePixels(a);
    }

    /// <summary>The heat rising into (i, j): the cell below weighted 18, plus the in-range cells of the 3×2 box above it.</summary>
    private static float FireHeat(float[] a, int i, int j)
    {
        int l = 18;
        float f = a[i + (j + 1) % 20 * 16] * l;
        for (int k = i - 1; k <= i + 1; k++)
            for (int i1 = j; i1 <= j + 1; i1++)
                if (k >= 0 && i1 >= 0 && k < 16 && i1 < 20) { f += a[k + i1 * 16]; l++; }
        return f / (l * 1.06f);
    }

    private static uint[] FirePixels(float[] a)
    {
        var px = new uint[256];
        for (int k = 0; k < 256; k++)
        {
            float f1 = Math.Clamp(a[k] * 1.8f, 0, 1);
            uint r = (uint)(f1 * 155 + 100), g = (uint)(f1 * f1 * 255), bl = (uint)(MathF.Pow(f1, 10) * 255), al = f1 < 0.5f ? 0u : 255u;
            px[k] = al << 24 | r << 16 | g << 8 | bl;
        }
        return px;
    }

    /// <summary>Texel at (u, v), wrapping (fluid UVs run outside 0..1 when rotated to the flow).</summary>
    public static uint Sample(uint[] tex, float u, float v)
    {
        int x = (int)MathF.Floor(u * 16) & 15, y = (int)MathF.Floor(v * 16) & 15;
        return tex[y * 16 + x];
    }
}
