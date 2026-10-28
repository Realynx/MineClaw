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

    /// <param name="fancy">Fancy graphics: alpha-cutout leaves (tiles 52/132) instead of the opaque fast ones (53/133).</param>
    public static TileRef For(int id, int meta, Face f, bool fancy = false)
    {
        bool top = f == Face.Up, bottom = f == Face.Down, side = !top && !bottom;
        switch (id)
        {
            case Blocks.Stone: return new(1);
            case Blocks.Grass: return top ? new(0, GrassTint) : bottom ? new(2) : new(3, 0, GrassSideOverlay, GrassTint);
            case Blocks.Dirt: return new(2);
            case Blocks.Cobblestone: return new(16);
            case Blocks.Planks: return new(4);
            case Blocks.Sapling: return new((meta & 3) switch { 1 => 63, 2 => 79, _ => 15 });
            case Blocks.Bedrock: return new(17);
            case Blocks.Sand: return new(18);
            case Blocks.Gravel: return new(19);
            case Blocks.GoldOre: return new(32);
            case Blocks.IronOre: return new(33);
            case Blocks.CoalOre: return new(34);
            case Blocks.Log: return top || bottom ? new(21) : new((meta & 3) switch { 1 => 116, 2 => 117, _ => 20 });
            case Blocks.Leaves: return new(((meta & 3) == 1 ? 132 : 52) + (fancy ? 0 : 1), (meta & 3) switch { 1 => SpruceTint, 2 => BirchTint, _ => FoliageTint });
            case Blocks.Sponge: return new(48);
            case Blocks.Glass: return new(49);
            case Blocks.LapisOre: return new(160);
            case Blocks.LapisBlock: return new(144);
            case 23: return top ? new(62) : side && FrontOf(id, meta) == f ? new(46) : new(45); // dispenser
            case Blocks.Sandstone: return top ? new(176) : bottom ? new(208) : new(192);
            case 25: return new(74); // note block
            case Blocks.PoweredRail: return new((meta & 8) != 0 ? 179 : 163);
            case Blocks.DetectorRail: return new(195);
            case 30: return new(11); // cobweb
            case Blocks.TallGrass: return (meta & 3) switch { 0 => new(55), 2 => new(56, GrassTint), _ => new(39, GrassTint) };
            case Blocks.DeadBush: return new(55);
            case Blocks.Wool: return new(WoolTiles[meta & 15]);
            case Blocks.Dandelion: return new(13);
            case Blocks.Rose: return new(12);
            case Blocks.BrownMushroom: return new(29);
            case Blocks.RedMushroom: return new(28);
            case Blocks.GoldBlock: return new(23);
            case Blocks.IronBlock: return new(22);
            case Blocks.DoubleSlab:
            case Blocks.Slab:
                return (meta & 7) switch
                {
                    1 => top || bottom ? new(176) : new(192),
                    2 => new(4),
                    3 => new(16),
                    _ => top || bottom ? new(6) : new(5),
                };
            case Blocks.Bricks: return new(7);
            case Blocks.Tnt: return top ? new(9) : bottom ? new(10) : new(8);
            case Blocks.Bookshelf: return top || bottom ? new(4) : new(35);
            case Blocks.MossyCobblestone: return new(36);
            case Blocks.Obsidian: return new(37);
            case Blocks.Torch: return new(80);
            case Blocks.Fire: return new(31);
            case 52: return new(65); // spawner
            case Blocks.WoodenStairs: return new(4);
            case Blocks.Chest:
            case Blocks.LockedChest:
                return top || bottom ? new(25) : f == Face.South ? new(27) : new(26);
            case Blocks.RedstoneWire: return new(164);
            case Blocks.DiamondOre: return new(50);
            case Blocks.DiamondBlock: return new(24);
            case Blocks.Workbench: return top ? new(43) : bottom ? new(4) : f is Face.North or Face.West ? new(60) : new(59);
            case Blocks.Crops: return new(88 + Math.Clamp(meta, 0, 7));
            case Blocks.Farmland: return top ? new(meta > 0 ? 86 : 87) : new(2);
            case Blocks.Furnace: return top || bottom ? new(62) : FrontOf(id, meta) == f ? new(44) : new(45);
            case Blocks.LitFurnace: return top || bottom ? new(62) : FrontOf(id, meta) == f ? new(61) : new(45);
            case Blocks.SignPost:
            case Blocks.WallSign: return new(4);
            case Blocks.WoodenDoor: return new((meta & 8) != 0 ? 81 : 97);
            case Blocks.IronDoor: return new((meta & 8) != 0 ? 82 : 98);
            case Blocks.Ladder: return new(83);
            case Blocks.Rail: return new(meta >= 6 ? 112 : 128);
            case Blocks.CobblestoneStairs: return new(16);
            case Blocks.Lever: return new(96);
            case Blocks.StonePressurePlate: return new(1);
            case Blocks.WoodenPressurePlate: return new(4);
            case Blocks.RedstoneOre:
            case Blocks.LitRedstoneOre: return new(51);
            case Blocks.RedstoneTorchOff: return new(115);
            case Blocks.RedstoneTorch: return new(99);
            case 77: return new(1); // button
            case Blocks.SnowLayer: return new(66);
            case Blocks.Ice: return new(67);
            case Blocks.SnowBlock: return new(66);
            case Blocks.Cactus: return top ? new(69) : bottom ? new(71) : new(70);
            case Blocks.Clay: return new(72);
            case Blocks.SugarCane: return new(73);
            case 84: return top ? new(75) : new(74); // jukebox
            case Blocks.Fence: return new(4);
            case Blocks.Pumpkin: return top || bottom ? new(102) : FrontOf(id, meta) == f ? new(119) : new(118);
            case Blocks.JackOLantern: return top || bottom ? new(102) : FrontOf(id, meta) == f ? new(120) : new(118);
            case Blocks.Netherrack: return new(103);
            case Blocks.SoulSand: return new(104);
            case Blocks.Glowstone: return new(105);
            case Blocks.Trapdoor: return new(84);
            case Blocks.StoneBricks: return new((meta & 3) switch { 1 => 100, 2 => 101, _ => 54 });
            case 101: return new(85); // iron bars
            case Blocks.GlassPane: return new(49);
            case Blocks.Melon: return top || bottom ? new(137) : new(136);
            case Blocks.FenceGate: return new(4);
            case 108: return new(7);  // brick stairs
            case 109: return new(54); // stone brick stairs
            case 106: return new(143, FoliageTint); // vine
            default: return TileRef.None;
        }
    }

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
