using MineClaw.Data;

namespace MineClaw.Render;

/// <summary>What the terminal can show; picks how the iso canvas is coloured.</summary>
public enum IsoColours { None, Ansi16, Ansi256, TrueColor }

public enum IsoFace : byte { None, Top, Left, Right }

[Flags]
public enum MatFlags : byte
{
    None = 0,
    /// <summary>Blended over what's behind (water, ice, portal); never hides entities.</summary>
    Translucent = 1,
    /// <summary>Only the sprite outline is drawn (glass); never hides entities.</summary>
    Outline = 2,
    /// <summary>Thin centre sprite (torch, flower, sapling, door...).</summary>
    Small = 4,
    /// <summary>Lies on the floor of its cell (rail, redstone, plate, snow layer).</summary>
    Flat = 8,
    /// <summary>Glows: little face/depth darkening (lava, glowstone, fire).</summary>
    Emissive = 16,
}

public enum MatTex : byte { Plain, Noise, Speckle, Grass, Log, Leaves, Planks, Chest, Furnace, LitFurnace, Bench, Lava, Water }

/// <summary>Look of one block id in the iso view: base colours (0xRRGGBB) of the top and the sides, ASCII glyphs, texture.</summary>
public sealed record IsoMat(int Id, uint Top, uint Side, char GTop, char GSide, MatTex Tex = MatTex.Plain, MatFlags Flags = MatFlags.None,
    uint Accent = 0)
{
    /// <summary>Per-face indices for 256/16-colour terminals (top, left, right), adjusted so the three faces stay distinct.</summary>
    public byte[] C256 { get; } = IsoPalette.FaceIndices(Top, Side, IsoColours.Ansi256);
    public byte[] C16 { get; } = IsoPalette.FaceIndices(Top, Side, IsoColours.Ansi16);
}

/// <summary>
/// Per-block colours and the colour fallbacks of the isometric viewport. Face shading: top ×1.0, left ×0.78, right ×0.58
/// (light from the upper left). 256 colours: nearest xterm cube/grey entry; 16 colours: nearest ANSI colour; when two faces
/// land on the same index the darker one steps down, so the three faces of a block never merge.
/// Deprecated for pictures: the TUI viewport's colours, plus the flat fallback when no textures exist on the host and the colour
/// maths helpers (<see cref="Mix"/>, <see cref="Scale"/>). Images are textured by <see cref="EyeRenderer"/>.
/// </summary>
public static class IsoPalette
{
    public const float LeftShade = 0.78f, RightShade = 0.58f;
    /// <summary>High bit marks a palette index (low byte) instead of an RGB value.</summary>
    public const uint Indexed = 0x8000_0000;

    private static IsoMat?[]? _mats; // lazy: Build() needs the colour tables below to be initialised

    /// <summary>The look of a block id (null = not drawn: air).</summary>
    public static IsoMat? Mat(int id) { var m = _mats ??= Build(); return (uint)id < (uint)m.Length ? m[id] : null; }

    public static float FaceShade(IsoFace f) => f switch { IsoFace.Left => LeftShade, IsoFace.Right => RightShade, _ => 1f };

    public static uint Scale(uint rgb, float k)
    {
        if (k == 1f) return rgb;
        int r = (int)(((rgb >> 16) & 0xff) * k), g = (int)(((rgb >> 8) & 0xff) * k), b = (int)((rgb & 0xff) * k);
        return (uint)(Math.Clamp(r, 0, 255) << 16 | Math.Clamp(g, 0, 255) << 8 | Math.Clamp(b, 0, 255));
    }

    public static uint Mix(uint a, uint b, float t)
    {
        int ar = (int)(a >> 16) & 0xff, ag = (int)(a >> 8) & 0xff, ab = (int)a & 0xff;
        int br = (int)(b >> 16) & 0xff, bg = (int)(b >> 8) & 0xff, bb = (int)b & 0xff;
        return (uint)((int)(ar + (br - ar) * t) << 16 | (int)(ag + (bg - ag) * t) << 8 | (int)(ab + (bb - ab) * t));
    }

    // ------------------------------------------------------------------ fallbacks

    private static readonly int[] CubeLevels = [0, 95, 135, 175, 215, 255];

    /// <summary>RGB of an xterm-256 index.</summary>
    public static uint Rgb256(int i)
    {
        if (i < 16) return Ansi16Rgb[i];
        if (i >= 232) { int v = 8 + (i - 232) * 10; return (uint)(v << 16 | v << 8 | v); }
        i -= 16;
        return (uint)(CubeLevels[i / 36] << 16 | CubeLevels[i / 6 % 6] << 8 | CubeLevels[i % 6]);
    }

    /// <summary>Nearest xterm-256 entry (cube 16-231 or grey ramp 232-255).</summary>
    public static int To256(uint rgb)
    {
        int r = (int)(rgb >> 16) & 0xff, g = (int)(rgb >> 8) & 0xff, b = (int)rgb & 0xff;
        static int Lvl(int v) => v < 48 ? 0 : v < 115 ? 1 : (v - 35) / 40;
        int ci = 16 + 36 * Lvl(r) + 6 * Lvl(g) + Lvl(b);
        int grey = Math.Clamp((int)Math.Round(((r + g + b) / 3.0 - 8) / 10.0), 0, 23);
        int gi = 232 + grey;
        return Dist(rgb, Rgb256(gi)) < Dist(rgb, Rgb256(ci)) ? gi : ci;
    }

    /// <summary>The 16 ANSI colours as Spectre defines them (black, maroon, green, olive, navy, purple, teal, silver, grey,
    /// red, lime, yellow, blue, fuchsia, aqua, white).</summary>
    public static readonly uint[] Ansi16Rgb =
    [
        0x000000, 0x800000, 0x008000, 0x808000, 0x000080, 0x800080, 0x008080, 0xc0c0c0,
        0x808080, 0xff0000, 0x00ff00, 0xffff00, 0x0000ff, 0xff00ff, 0x00ffff, 0xffffff,
    ];

    /// <summary>Nearest of the 16 ANSI colours, with a hue penalty between saturated colours (so grass maps to green, not olive).</summary>
    public static int To16(uint rgb)
    {
        int best = 0;
        long bd = long.MaxValue;
        var (h, sat) = HueSat(rgb);
        for (int i = 0; i < 16; i++)
        {
            long d = Dist(rgb, Ansi16Rgb[i]);
            var (h2, s2) = HueSat(Ansi16Rgb[i]);
            if (sat > 0.25 && s2 > 0.25)
            {
                double dh = Math.Abs(h - h2);
                dh = Math.Min(dh, 360 - dh);
                d += (long)(dh * dh * 20);
            }
            else if (sat > 0.35 && s2 < 0.1) d += 30_000; // a clearly coloured block shouldn't turn grey
            if (d < bd) { bd = d; best = i; }
        }
        return best;
    }

    private static (double Hue, double Sat) HueSat(uint rgb)
    {
        double r = ((rgb >> 16) & 0xff) / 255.0, g = ((rgb >> 8) & 0xff) / 255.0, b = (rgb & 0xff) / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), c = max - min;
        if (max <= 0 || c <= 0) return (0, 0);
        double h = max == r ? 60 * (((g - b) / c) % 6) : max == g ? 60 * ((b - r) / c + 2) : 60 * ((r - g) / c + 4);
        return (h < 0 ? h + 360 : h, c / max);
    }

    /// <summary>One step darker in the 16-colour set (bright → normal → grey/black).</summary>
    public static int Darker16(int i) => i switch
    {
        15 => 7, 7 => 8, 8 => 0, >= 9 and <= 14 => i - 8, 0 => 0, _ => i == 4 ? 0 : 8,
    };

    public static int Quantize(uint rgb, IsoColours c) => c == IsoColours.Ansi16 ? To16(rgb) : To256(rgb);

    /// <summary>Distinct top/left/right palette indices for a block's base colours.</summary>
    public static byte[] FaceIndices(uint top, uint side, IsoColours c)
    {
        int t = Quantize(top, c);
        int l = Quantize(Scale(side, LeftShade), c);
        if (l == t) l = Darker(l, Scale(side, LeftShade), c, t);
        int r = Quantize(Scale(side, RightShade), c);
        if (r == l || r == t) r = Darker(r, Scale(side, RightShade), c, l, t);
        return [(byte)t, (byte)l, (byte)r];
    }

    private static int Darker(int idx, uint rgb, IsoColours c, int avoid1, int avoid2 = -1)
    {
        int start = idx;
        idx = c == IsoColours.Ansi16 ? StepDarker16(idx, avoid1, avoid2) : StepDarker256(idx, rgb, avoid1, avoid2);
        if (idx != avoid1 && idx != avoid2) return idx;
        // Still clashing (e.g. black): the nearest other entry.
        return Nearest(rgb, c, start, avoid1, avoid2);
    }

    private static int StepDarker16(int idx, int avoid1, int avoid2)
    {
        for (int k = 0; k < 4 && (idx == avoid1 || idx == avoid2); k++) idx = Darker16(idx);
        return idx;
    }

    private static int StepDarker256(int idx, uint rgb, int avoid1, int avoid2)
    {
        float f = 0.8f;
        for (int k = 0; k < 6 && (idx == avoid1 || idx == avoid2); k++, f *= 0.8f) idx = To256(Scale(rgb, f));
        return idx;
    }

    /// <summary>The palette entry nearest <paramref name="rgb"/> other than the two to avoid (<paramref name="start"/> if none is nearer).</summary>
    private static int Nearest(uint rgb, IsoColours c, int start, int avoid1, int avoid2)
    {
        bool ansi16 = c == IsoColours.Ansi16;
        int n = ansi16 ? 16 : 256, best = start;
        long bd = long.MaxValue;
        for (int i = ansi16 ? 0 : 16; i < n; i++)
        {
            if (i == avoid1 || i == avoid2) continue;
            long d = Dist(rgb, ansi16 ? Ansi16Rgb[i] : Rgb256(i));
            if (d < bd) { bd = d; best = i; }
        }
        return best;
    }

    private static long Dist(uint a, uint b)
    {
        int dr = (int)((a >> 16) & 0xff) - (int)((b >> 16) & 0xff);
        int dg = (int)((a >> 8) & 0xff) - (int)((b >> 8) & 0xff);
        int db = (int)(a & 0xff) - (int)(b & 0xff);
        return 3L * dr * dr + 4L * dg * dg + 2L * db * db;
    }

    // ------------------------------------------------------------------ background

    public static uint VoidTop(bool nether) => nether ? 0x2a0b0au : 0x121722u;
    public static uint VoidBottom(bool nether) => nether ? 0x110303u : 0x06070bu;
    public static uint VoidGrid(bool nether) => nether ? 0x3d1513u : 0x1b2232u;

    // ------------------------------------------------------------------ materials

    private static IsoMat?[] Build()
    {
        var m = new IsoMat?[256];
        void S(int id, uint top, uint side, char gt, char gs, MatTex tex = MatTex.Plain, MatFlags f = MatFlags.None, uint accent = 0) =>
            m[id] = new IsoMat(id, top, side, gt, gs, tex, f, accent);
        void Same(int id, uint c, char g, MatTex tex = MatTex.Noise, MatFlags f = MatFlags.None) => S(id, c, c, g, g, tex, f);

        // Generic fallback first: any other solid block is light grey, anything see-through a thin sprite.
        for (int id = 1; id < 256; id++)
        {
            var info = Blocks.Get(id);
            if (info.Solid) Same(id, 0xa8a4b0, '+');
            else S(id, 0xc8c0a0, 0x7a6a50, '\'', '\'', MatTex.Plain, MatFlags.Small);
        }

        S(Blocks.Grass, 0x5da83a, 0x8b5a2b, '"', '|', MatTex.Grass, accent: 0x4f9631);
        Same(Blocks.Dirt, 0x8b5a2b, '.');
        S(Blocks.Farmland, 0x5a3a1c, 0x7d5126, '=', '|', MatTex.Planks);
        Same(Blocks.Stone, 0x8c8c8c, '#');
        Same(Blocks.Cobblestone, 0x7d7d7d, '%', MatTex.Speckle);
        Same(Blocks.MossyCobblestone, 0x6e7e5c, '%', MatTex.Speckle);
        Same(Blocks.Bedrock, 0x3c3c3c, 'B', MatTex.Speckle);
        Same(Blocks.Sand, 0xdbd29e, ',');
        Same(Blocks.Sandstone, 0xd5c88f, ',', MatTex.Planks);
        Same(Blocks.Gravel, 0x8a807b, ':', MatTex.Speckle);
        Same(Blocks.Clay, 0x9ea3b2, 'c');
        Same(Blocks.Planks, 0xb38c55, '=', MatTex.Planks);
        S(Blocks.WoodenStairs, 0xb38c55, 0xa07c48, '=', '=', MatTex.Planks);
        S(Blocks.Log, 0xb8955e, 0x5c4326, 'O', 'H', MatTex.Log, accent: 0x8a6a3e);
        Same(Blocks.Leaves, 0x3d8c2c, '*', MatTex.Leaves);
        S(Blocks.Glass, 0xcdeeff, 0xcdeeff, '+', '+', MatTex.Plain, MatFlags.Outline);
        S(Blocks.GlassPane, 0xcdeeff, 0xcdeeff, '+', '+', MatTex.Plain, MatFlags.Outline);
        S(Blocks.Water, 0x3b76e6, 0x2d5fd0, '~', '~', MatTex.Water, MatFlags.Translucent);
        S(Blocks.FlowingWater, 0x3b76e6, 0x2d5fd0, '~', '~', MatTex.Water, MatFlags.Translucent);
        S(Blocks.Ice, 0x9cc0f4, 0x86a8e0, '_', '_', MatTex.Plain, MatFlags.Translucent);
        S(Blocks.Lava, 0xff8418, 0xf06a0c, '!', '!', MatTex.Lava, MatFlags.Emissive, accent: 0xffd84a);
        S(Blocks.FlowingLava, 0xff8418, 0xf06a0c, '!', '!', MatTex.Lava, MatFlags.Emissive, accent: 0xffd84a);
        S(Blocks.Chest, 0xab6f2e, 0x9a6127, 'C', 'c', MatTex.Chest, accent: 0xe2c24e);
        S(Blocks.LockedChest, 0xab6f2e, 0x9a6127, 'C', 'c', MatTex.Chest, accent: 0xe2c24e);
        S(Blocks.Furnace, 0x8a8a8a, 0x7a7a7a, 'F', 'f', MatTex.Furnace, accent: 0x262626);
        S(Blocks.LitFurnace, 0x8a8a8a, 0x7a7a7a, 'F', 'f', MatTex.LitFurnace, MatFlags.None, accent: 0xff9a2e);
        S(Blocks.Workbench, 0xa3733c, 0x8a5e33, 'W', 'w', MatTex.Bench, accent: 0x5e3d1d);
        S(Blocks.Bookshelf, 0xb38c55, 0x7b4a2a, '=', 'b', MatTex.Planks);
        Same(Blocks.Bricks, 0x9b4b3b, 'r', MatTex.Planks);
        Same(Blocks.StoneBricks, 0x7f7f7f, '#', MatTex.Planks);
        S(Blocks.Tnt, 0xd8d0c0, 0xcc3222, 'T', 't');
        S(Blocks.Cactus, 0x3a8a34, 0x2c7a2a, 'k', 'k', MatTex.Planks);
        Same(Blocks.SnowBlock, 0xf0f6ff, 's');
        S(Blocks.Pumpkin, 0xd88a22, 0xc87818, 'p', 'p', MatTex.Planks);
        S(Blocks.JackOLantern, 0xd88a22, 0xe8a030, 'j', 'j', MatTex.Planks, MatFlags.Emissive);
        S(Blocks.Melon, 0x77a632, 0x6a9a2c, 'm', 'm', MatTex.Planks);
        Same(Blocks.Wool, 0xe6e6e6, 'w');
        Same(Blocks.Sponge, 0xcac454, 'o');
        Same(Blocks.Obsidian, 0x241a38, 'X');
        Same(Blocks.Netherrack, 0x7d3030, 'n', MatTex.Speckle);
        Same(Blocks.SoulSand, 0x5c4636, 'u');
        S(Blocks.Glowstone, 0xf2d47a, 0xe6c060, 'g', 'g', MatTex.Speckle, MatFlags.Emissive);
        Same(Blocks.IronBlock, 0xdadada, 'I', MatTex.Plain);
        Same(Blocks.GoldBlock, 0xf2d443, 'G', MatTex.Plain);
        Same(Blocks.DiamondBlock, 0x74e0dc, 'D', MatTex.Plain);
        Same(Blocks.LapisBlock, 0x2c4fa8, 'L', MatTex.Plain);
        S(Blocks.CoalOre, 0x8c8c8c, 0x8c8c8c, '#', '#', MatTex.Speckle, accent: 0x202020);
        S(Blocks.IronOre, 0x8c8c8c, 0x8c8c8c, '#', '#', MatTex.Speckle, accent: 0xd8b090);
        S(Blocks.GoldOre, 0x8c8c8c, 0x8c8c8c, '#', '#', MatTex.Speckle, accent: 0xf0e040);
        S(Blocks.DiamondOre, 0x8c8c8c, 0x8c8c8c, '#', '#', MatTex.Speckle, accent: 0x70f0f0);
        S(Blocks.RedstoneOre, 0x8c8c8c, 0x8c8c8c, '#', '#', MatTex.Speckle, accent: 0xd02020);
        S(Blocks.LitRedstoneOre, 0x8c8c8c, 0x8c8c8c, '#', '#', MatTex.Speckle, accent: 0xff3030);
        S(Blocks.LapisOre, 0x8c8c8c, 0x8c8c8c, '#', '#', MatTex.Speckle, accent: 0x3050c0);
        S(Blocks.Portal, 0x9a4ad8, 0x7a32b8, '@', '@', MatTex.Noise, MatFlags.Translucent | MatFlags.Emissive);

        // Thin and flat things.
        S(Blocks.Torch, 0xffd860, 0x8a6a3a, 'i', 'i', MatTex.Plain, MatFlags.Small | MatFlags.Emissive);
        S(Blocks.RedstoneTorch, 0xff3020, 0x8a6a3a, 'i', 'i', MatTex.Plain, MatFlags.Small | MatFlags.Emissive);
        S(Blocks.RedstoneTorchOff, 0x802020, 0x8a6a3a, 'i', 'i', MatTex.Plain, MatFlags.Small);
        S(Blocks.Fire, 0xffb030, 0xff6010, '^', '^', MatTex.Lava, MatFlags.Small | MatFlags.Emissive, accent: 0xffe060);
        S(Blocks.Sapling, 0x5ab040, 0x3c8a2c, 'y', 'y', MatTex.Plain, MatFlags.Small);
        S(Blocks.TallGrass, 0x6ab84a, 0x4a9a34, '\'', '\'', MatTex.Plain, MatFlags.Small);
        S(Blocks.DeadBush, 0x9a7a40, 0x7a5a30, '\'', '\'', MatTex.Plain, MatFlags.Small);
        S(Blocks.Dandelion, 0xf8e030, 0x3c8a2c, '*', '|', MatTex.Plain, MatFlags.Small);
        S(Blocks.Rose, 0xe02a2a, 0x3c8a2c, '*', '|', MatTex.Plain, MatFlags.Small);
        S(Blocks.BrownMushroom, 0x9a7050, 0xd8c8b0, 'm', 'm', MatTex.Plain, MatFlags.Small);
        S(Blocks.RedMushroom, 0xd03030, 0xd8c8b0, 'm', 'm', MatTex.Plain, MatFlags.Small);
        S(Blocks.SugarCane, 0x8ad060, 0x6ab048, '|', '|', MatTex.Plain, MatFlags.Small);
        S(Blocks.Crops, 0xc8b040, 0x6a9a2c, '"', '|', MatTex.Plain, MatFlags.Small);
        S(Blocks.Fence, 0xa07c48, 0x8a6a3a, '|', '|', MatTex.Plain, MatFlags.Small);
        S(Blocks.FenceGate, 0xa07c48, 0x8a6a3a, '|', '|', MatTex.Plain, MatFlags.Small);
        S(Blocks.WoodenDoor, 0xa07c48, 0x8a6a3a, 'd', 'd', MatTex.Plain, MatFlags.Small);
        S(Blocks.IronDoor, 0xc8c8c8, 0xa0a0a0, 'd', 'd', MatTex.Plain, MatFlags.Small);
        S(Blocks.Ladder, 0xa07c48, 0x8a6a3a, 'H', 'H', MatTex.Plain, MatFlags.Small);
        S(Blocks.SignPost, 0xb38c55, 0x8a6a3a, 'P', 'P', MatTex.Plain, MatFlags.Small);
        S(Blocks.WallSign, 0xb38c55, 0x8a6a3a, 'P', 'P', MatTex.Plain, MatFlags.Small);
        S(Blocks.Rail, 0x9a9a9a, 0x6a5030, '=', '=', MatTex.Plain, MatFlags.Flat);
        S(Blocks.PoweredRail, 0xd8b040, 0x6a5030, '=', '=', MatTex.Plain, MatFlags.Flat);
        S(Blocks.DetectorRail, 0xa08080, 0x6a5030, '=', '=', MatTex.Plain, MatFlags.Flat);
        S(Blocks.RedstoneWire, 0xb01818, 0xb01818, '-', '-', MatTex.Plain, MatFlags.Flat);
        S(Blocks.StonePressurePlate, 0x9a9a9a, 0x9a9a9a, '_', '_', MatTex.Plain, MatFlags.Flat);
        S(Blocks.WoodenPressurePlate, 0xb38c55, 0xb38c55, '_', '_', MatTex.Plain, MatFlags.Flat);
        S(Blocks.SnowLayer, 0xf0f6ff, 0xf0f6ff, '_', '_', MatTex.Plain, MatFlags.Flat);
        S(Blocks.Trapdoor, 0xa07c48, 0xa07c48, '_', '_', MatTex.Planks, MatFlags.Flat);
        m[0] = null;
        return m;
    }
}
