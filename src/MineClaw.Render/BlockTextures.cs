using MineClaw.Data;

namespace MineClaw.Render;

/// <summary>Block faces in Minecraft's side order: 0 down, 1 up, 2 north (−z), 3 south (+z), 4 west (−x), 5 east (+x).</summary>
public enum Face : byte { Down = 0, Up = 1, North = 2, South = 3, West = 4, East = 5 }

/// <summary>
/// b1.8.1 block id + metadata + face → <c>terrain.png</c> tile index (the vanilla <c>Block.a(side, meta)</c> values), the
/// biome tint for grey tiles (fixed default colours: grass 0x91BD59, foliage 0x48B518, spruce 0x619961, birch 0x80A755) and
/// an optional overlay (the grass side fringe). −1 = no tile: draw the flat palette colour (air, animated liquids, unknown).
/// </summary>
public static class BlockTextures
{
    public const uint GrassTint = 0x91bd59, FoliageTint = 0x48b518, SpruceTint = 0x619961, BirchTint = 0x80a755;
    public const int GrassSideOverlay = 38;

    public readonly record struct TileRef(int Tile, uint Tint = 0, int Overlay = -1, uint OverlayTint = 0)
    {
        public static readonly TileRef None = new(-1);
        public bool IsNone => Tile < 0;
    }

    /// <summary>Wool colour tiles by metadata (white … black).</summary>
    private static readonly int[] WoolTiles = [64, 210, 194, 178, 162, 146, 130, 114, 225, 209, 193, 177, 161, 145, 129, 113];

    /// <summary>Facing of a furnace/pumpkin/dispenser front from its metadata (2 N, 3 S, 4 W, 5 E; pumpkins 0 S,1 W,2 N,3 E).</summary>
    private static Face FrontOf(int id, int meta) => id is Blocks.Pumpkin or Blocks.JackOLantern
        ? (meta & 3) switch { 0 => Face.South, 1 => Face.West, 2 => Face.North, _ => Face.East }
        : meta switch { 2 => Face.North, 3 => Face.South, 4 => Face.West, 5 => Face.East, _ => Face.South };

    /// <summary>One block's face → tile rule (meta, face, fancy leaves).</summary>
    private delegate TileRef TileRule(int meta, Face f, bool fancy);

    private static bool IsEnd(Face f) => f is Face.Up or Face.Down;

    /// <summary>Blocks with one tile on every face.</summary>
    private static readonly (int Id, int Tile)[] Plain =
    [
        (Blocks.Stone, 1), (Blocks.Dirt, 2), (Blocks.Cobblestone, 16), (Blocks.Planks, 4), (Blocks.Bedrock, 17), (Blocks.Sand, 18), (Blocks.Gravel, 19),
        (Blocks.GoldOre, 32), (Blocks.IronOre, 33), (Blocks.CoalOre, 34), (Blocks.Sponge, 48), (Blocks.Glass, 49), (Blocks.LapisOre, 160),
        (Blocks.LapisBlock, 144), (25, 74) /* note block */, (Blocks.DetectorRail, 195), (30, 11) /* cobweb */, (Blocks.DeadBush, 55),
        (Blocks.Dandelion, 13), (Blocks.Rose, 12), (Blocks.BrownMushroom, 29), (Blocks.RedMushroom, 28), (Blocks.GoldBlock, 23), (Blocks.IronBlock, 22),
        (Blocks.Bricks, 7), (Blocks.MossyCobblestone, 36), (Blocks.Obsidian, 37), (Blocks.Torch, 80), (Blocks.Fire, 31), (52, 65) /* spawner */,
        (Blocks.WoodenStairs, 4), (Blocks.RedstoneWire, 164), (Blocks.DiamondOre, 50), (Blocks.DiamondBlock, 24), (Blocks.SignPost, 4),
        (Blocks.WallSign, 4), (Blocks.Ladder, 83), (Blocks.CobblestoneStairs, 16), (Blocks.Lever, 96), (Blocks.StonePressurePlate, 1),
        (Blocks.WoodenPressurePlate, 4), (Blocks.RedstoneOre, 51), (Blocks.LitRedstoneOre, 51), (Blocks.RedstoneTorchOff, 115),
        (Blocks.RedstoneTorch, 99), (77, 1) /* button */, (Blocks.SnowLayer, 66), (Blocks.Ice, 67), (Blocks.SnowBlock, 66), (Blocks.Clay, 72),
        (Blocks.SugarCane, 73), (Blocks.Fence, 4), (Blocks.Netherrack, 103), (Blocks.SoulSand, 104), (Blocks.Glowstone, 105), (Blocks.Trapdoor, 84),
        (101, 85) /* iron bars */, (Blocks.GlassPane, 49), (Blocks.FenceGate, 4), (108, 7) /* brick stairs */, (109, 54) /* stone brick stairs */,
    ];

    /// <summary>Blocks with one tile on the top and bottom and another on the sides.</summary>
    private static readonly (int Id, int Ends, int Side)[] Pillars = [(Blocks.Bookshelf, 4, 35), (Blocks.Melon, 137, 136)];

    /// <summary>Blocks with their own top, bottom and side tiles.</summary>
    private static readonly (int Id, int Top, int Bottom, int Side)[] Layered =
        [(Blocks.Sandstone, 176, 208, 192), (Blocks.Tnt, 9, 10, 8), (Blocks.Cactus, 69, 71, 70)];

    /// <summary>Blocks with a front face (by meta): ends, front and the other sides.</summary>
    private static readonly (int Id, int Ends, int Front, int Side)[] Fronted =
    [
        (Blocks.Furnace, 62, 44, 45), (Blocks.LitFurnace, 62, 61, 45), (Blocks.Pumpkin, 102, 119, 118), (Blocks.JackOLantern, 102, 120, 118),
    ];

    /// <summary>The rule per block id (null = no tile).</summary>
    private static readonly TileRule?[] Rules = BuildRules();

    private static TileRule?[] BuildRules()
    {
        var r = new TileRule?[256];
        foreach (var (id, tile) in Plain) { var t = new TileRef(tile); r[id] = (_, _, _) => t; }
        foreach (var (id, ends, side) in Pillars) r[id] = (_, f, _) => IsEnd(f) ? new(ends) : new(side);
        foreach (var (id, top, bottom, side) in Layered) r[id] = (_, f, _) => f == Face.Up ? new(top) : f == Face.Down ? new(bottom) : new(side);
        foreach (var (id, ends, front, side) in Fronted) r[id] = (m, f, _) => IsEnd(f) ? new(ends) : FrontOf(id, m) == f ? new(front) : new(side);
        r[Blocks.Grass] = (_, f, _) => f == Face.Up ? new(0, GrassTint) : f == Face.Down ? new(2) : new(3, 0, GrassSideOverlay, GrassTint);
        r[Blocks.Sapling] = (m, _, _) => new((m & 3) switch { 1 => 63, 2 => 79, _ => 15 });
        r[Blocks.Log] = (m, f, _) => IsEnd(f) ? new(21) : new((m & 3) switch { 1 => 116, 2 => 117, _ => 20 });
        r[Blocks.Leaves] = (m, _, fancy) => new(((m & 3) == 1 ? 132 : 52) + (fancy ? 0 : 1), (m & 3) switch { 1 => SpruceTint, 2 => BirchTint, _ => FoliageTint });
        r[23] = (m, f, _) => f == Face.Up ? new(62) : f != Face.Down && FrontOf(23, m) == f ? new(46) : new(45); // dispenser
        r[Blocks.PoweredRail] = (m, _, _) => new((m & 8) != 0 ? 179 : 163);
        r[Blocks.TallGrass] = (m, _, _) => (m & 3) switch { 0 => new(55), 2 => new(56, GrassTint), _ => new(39, GrassTint) };
        r[Blocks.Wool] = (m, _, _) => new(WoolTiles[m & 15]);
        r[Blocks.DoubleSlab] = r[Blocks.Slab] = Slab;
        r[Blocks.Chest] = r[Blocks.LockedChest] = (_, f, _) => IsEnd(f) ? new(25) : f == Face.South ? new(27) : new(26);
        r[Blocks.Workbench] = (_, f, _) => f == Face.Up ? new(43) : f == Face.Down ? new(4) : f is Face.North or Face.West ? new(60) : new(59);
        r[Blocks.Crops] = (m, _, _) => new(88 + Math.Clamp(m, 0, 7));
        r[Blocks.Farmland] = (m, f, _) => f == Face.Up ? new(m > 0 ? 86 : 87) : new(2);
        r[Blocks.WoodenDoor] = (m, _, _) => new((m & 8) != 0 ? 81 : 97);
        r[Blocks.IronDoor] = (m, _, _) => new((m & 8) != 0 ? 82 : 98);
        r[Blocks.Rail] = (m, _, _) => new(m >= 6 ? 112 : 128);
        r[84] = (_, f, _) => f == Face.Up ? new(75) : new(74); // jukebox
        r[Blocks.StoneBricks] = (m, _, _) => new((m & 3) switch { 1 => 100, 2 => 101, _ => 54 });
        r[106] = (_, _, _) => new(143, FoliageTint); // vine
        return r;
    }

    private static TileRef Slab(int meta, Face f, bool fancy) => (meta & 7) switch
    {
        1 => IsEnd(f) ? new(176) : new(192),
        2 => new(4),
        3 => new(16),
        _ => IsEnd(f) ? new(6) : new(5),
    };

    /// <param name="fancy">Fancy graphics: alpha-cutout leaves (tiles 52/132) instead of the opaque fast ones (53/133).</param>
    public static TileRef For(int id, int meta, Face f, bool fancy = false) =>
        (uint)id < (uint)Rules.Length && Rules[id] is { } rule ? rule(meta, f, fancy) : TileRef.None;

    /// <summary>Animated or special blocks drawn from the palette even when an atlas is loaded (their tiles are placeholders).</summary>
    public static bool UsePalette(int id) => id is Blocks.Water or Blocks.FlowingWater or Blocks.Lava or Blocks.FlowingLava or Blocks.Portal;

    /// <summary>Blocks that emit light and render at full brightness (vanilla light values ≥ 13 that matter here).</summary>
    public static bool Emissive(int id) => id is Blocks.Torch or Blocks.Glowstone or Blocks.Lava or Blocks.FlowingLava or Blocks.Fire
        or Blocks.JackOLantern or Blocks.LitFurnace or Blocks.Portal or Blocks.RedstoneTorch or Blocks.LitRedstoneOre;

    /// <summary>Multiplies a grey texel by a tint colour.</summary>
    public static uint Tinted(uint texel, uint tint)
    {
        if (tint == 0) return texel;
        uint r = ((texel >> 16) & 0xff) * ((tint >> 16) & 0xff) / 255, g = ((texel >> 8) & 0xff) * ((tint >> 8) & 0xff) / 255,
            b = (texel & 0xff) * (tint & 0xff) / 255;
        return (texel & 0xff000000) | r << 16 | g << 8 | b;
    }

    /// <summary>Texel of a block face at (u, v) with tint and overlay applied (0xAARRGGBB), or null without a tile.</summary>
    public static uint? Texel(TextureAtlas atlas, int id, int meta, Face f, float u, float v, bool fancy = false) =>
        Texel(atlas, For(id, meta, f, fancy), id, u, v);

    /// <summary>Texel of a resolved tile reference (tint + overlay applied), or null without a tile.</summary>
    public static uint? Texel(TextureAtlas atlas, TileRef t, int id, float u, float v)
    {
        if (t.IsNone || UsePalette(id)) return null;
        uint p = Tinted(atlas.Sample(t.Tile, u, v), t.Tint);
        if (t.Overlay >= 0)
        {
            uint o = atlas.Sample(t.Overlay, u, v);
            if (o >> 24 >= 128) p = Tinted(o, t.OverlayTint) | 0xff000000;
        }
        return p;
    }
}
