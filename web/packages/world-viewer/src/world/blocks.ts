// Per-version block knowledge for the mesher's fallback path (no atlas, or an id the atlas doesn't list): shape, render layer,
// culling flags and a flat colour palette. b1.8.1 ids are a subset of 1.12.2's numbering except 95 (locked chest vs stained
// glass), so one table keyed by id serves both with a b1.8.1 override; metadata meanings that differ are branched on the
// version where they matter (slab tops, plank/log/stone variants exist only in 1.12.2). Colours follow MineClaw.Render's
// IsoPalette (b1.8.1) and the vanilla 1.12.2 map colours, so a texture-less picture still reads right.

export type GameVersion = 'b1.8.1' | '1.12.2';

export const enum Shape {
  None = 0,
  Cube = 1,
  Cross = 2,
  Torch = 3,
  Fluid = 4,
  Slab = 5,
  /** A full-width box of a height from the state (farmland, snow layers, beds, carpets, plates, rails…). */
  Partial = 6,
  Fence = 7,
  Pane = 8,
  Wall = 9,
  Door = 10,
  /** A plane on one wall of the cell (ladders, vines, wall signs). */
  WallPlane = 11,
  Stairs = 12,
  Chest = 13,
  /** Cactus: sides inset by 1/16. */
  Inset = 14,
  /** A small centred box (buttons, levers, pots, skulls). */
  Small = 15,
  /** Standing sign / banner: a post with a board. */
  Post = 16,
  Trapdoor = 17,
  Gate = 18,
  /** Exactly the atlas entry's quads. */
  Quads = 19,
}

export const enum Layer {
  Opaque = 0,
  Cutout = 1,
  Translucent = 2,
}

/** Full opaque cube: hides neighbour faces, occludes AO. */
export const F_OPAQUE = 1;
/** Faces against the same id are skipped (glass, ice, water, fast leaves). */
export const F_SELF_CULL = 2;
export const F_EMISSIVE = 4;
/** Biome-tinted in flat mode (grass top, leaves, vines). */
export const F_BIOME_GRASS = 8;
export const F_BIOME_FOLIAGE = 16;
export const F_WATER = 32;
export const F_LAVA = 64;
/** Leaves: counts as cover for the x-ray "is the body hidden" test. */
export const F_LEAVES = 128;

export const BLOCK_NAMES_1122: string[] = (() => {
  const n = new Array<string>(256).fill('');
  const list =
    'air stone grass dirt cobblestone planks sapling bedrock flowing_water water flowing_lava lava sand gravel gold_ore iron_ore coal_ore log leaves sponge glass lapis_ore lapis_block dispenser sandstone noteblock bed golden_rail detector_rail sticky_piston web tallgrass deadbush piston piston_head wool piston_extension yellow_flower red_flower brown_mushroom red_mushroom gold_block iron_block double_stone_slab stone_slab brick_block tnt bookshelf mossy_cobblestone obsidian torch fire mob_spawner oak_stairs chest redstone_wire diamond_ore diamond_block crafting_table wheat farmland furnace lit_furnace standing_sign wooden_door ladder rail stone_stairs wall_sign lever stone_pressure_plate iron_door wooden_pressure_plate redstone_ore lit_redstone_ore unlit_redstone_torch redstone_torch stone_button snow_layer ice snow cactus clay reeds jukebox fence pumpkin netherrack soul_sand glowstone portal lit_pumpkin cake unpowered_repeater powered_repeater stained_glass trapdoor monster_egg stonebrick brown_mushroom_block red_mushroom_block iron_bars glass_pane melon_block pumpkin_stem melon_stem vine fence_gate brick_stairs stone_brick_stairs mycelium waterlily nether_brick nether_brick_fence nether_brick_stairs nether_wart enchanting_table brewing_stand cauldron end_portal end_portal_frame end_stone dragon_egg redstone_lamp lit_redstone_lamp double_wooden_slab wooden_slab cocoa sandstone_stairs emerald_ore ender_chest tripwire_hook tripwire emerald_block spruce_stairs birch_stairs jungle_stairs command_block beacon cobblestone_wall flower_pot carrots potatoes wooden_button skull anvil trapped_chest light_weighted_pressure_plate heavy_weighted_pressure_plate unpowered_comparator powered_comparator daylight_detector redstone_block quartz_ore hopper quartz_block quartz_stairs activator_rail dropper stained_hardened_clay stained_glass_pane leaves2 log2 acacia_stairs dark_oak_stairs slime barrier iron_trapdoor prismarine sea_lantern hay_block carpet hardened_clay coal_block packed_ice double_plant standing_banner wall_banner daylight_detector_inverted red_sandstone red_sandstone_stairs double_stone_slab2 stone_slab2 spruce_fence_gate birch_fence_gate jungle_fence_gate dark_oak_fence_gate acacia_fence_gate spruce_fence birch_fence jungle_fence dark_oak_fence acacia_fence spruce_door birch_door jungle_door acacia_door dark_oak_door end_rod chorus_plant chorus_flower purpur_block purpur_pillar purpur_stairs purpur_double_slab purpur_slab end_bricks beetroots grass_path end_gateway repeating_command_block chain_command_block frosted_ice magma nether_wart_block red_nether_brick bone_block structure_void observer'.split(
      ' ',
    );
  list.forEach((s, i) => (n[i] = s));
  const dyes = 'white orange magenta light_blue yellow lime pink gray silver cyan purple blue brown green red black'.split(' ');
  dyes.forEach((d, i) => {
    n[219 + i] = `${d}_shulker_box`;
    n[235 + i] = `${d}_glazed_terracotta`;
  });
  n[251] = 'concrete';
  n[252] = 'concrete_powder';
  n[255] = 'structure_block';
  return n;
})();

export function blockName(version: GameVersion, id: number): string {
  if (version === 'b1.8.1') {
    if (id === 95) return 'locked_chest';
    if (id > 109) return `block_${id}`;
  }
  return BLOCK_NAMES_1122[id] || `block_${id}`;
}

/** Wool / stained glass / carpet colours by dye metadata. */
export const DYE: number[] = [0xe9ecec, 0xf07613, 0xbd44b3, 0x3aafd9, 0xf8c627, 0x70b919, 0xed8dac, 0x3e4447, 0x8e8e86, 0x158991, 0x792aac, 0x35399d, 0x724728, 0x546d1b, 0xa12722, 0x141519];
const TERRACOTTA: number[] = [0xd1b1a1, 0xa15325, 0x95586c, 0x716c89, 0xba8523, 0x677534, 0xa14e4e, 0x392a23, 0x876a61, 0x565b5b, 0x764656, 0x4a3b5b, 0x4d3323, 0x4c532a, 0x8e3c2e, 0x251610];
const PLANKS: number[] = [0xa2834f, 0x735531, 0xc0af79, 0xa0734d, 0xa85a32, 0x42291a];
const LOG_SIDE: number[] = [0x6b5532, 0x3b2912, 0xd7d3c8, 0x55451a];
const LOG2_SIDE: number[] = [0x696259, 0x3e3020];
const STONE: number[] = [0x8c8c8c, 0x9a6b58, 0x9f6d59, 0xbcbcbc, 0xc0c0c2, 0x888889, 0x848685];

export const GRASS_TINT = 0x91bd59;
export const FOLIAGE_TINT = 0x48b518;

export interface BlockDef {
  shape: Shape;
  layer: Layer;
  flags: number;
  top: number;
  side: number;
  /** Constant alpha for translucent flat faces. */
  alpha: number;
}

function def(shape: Shape, top: number, side = top, layer: Layer = Layer.Opaque, flags = 0, alpha = 1): BlockDef {
  return { shape, layer, flags, top, side, alpha };
}

type Table = Array<BlockDef | null>;

function baseTable(): Table {
  const t: Table = new Array(256).fill(null);
  const cube = (id: number, top: number, side = top, flags = 0) => (t[id] = def(Shape.Cube, top, side, Layer.Opaque, F_OPAQUE | flags));
  const set = (ids: number[], d: () => BlockDef) => ids.forEach((id) => (t[id] = d()));

  // Generic: unknown solid ids grey cubes (overwritten below where known).
  for (let id = 1; id < 256; id++) cube(id, 0xa8a4b0);

  cube(1, 0x8c8c8c);
  cube(2, 0x5da83a, 0x8b5a2b, F_BIOME_GRASS);
  cube(3, 0x8b5a2b);
  cube(4, 0x7d7d7d);
  cube(5, 0xb38c55);
  t[6] = def(Shape.Cross, 0x5ab040, 0x3c8a2c, Layer.Cutout);
  cube(7, 0x3c3c3c);
  set([8, 9], () => def(Shape.Fluid, 0x3b76e6, 0x2d5fd0, Layer.Translucent, F_SELF_CULL | F_WATER, 0.62));
  set([10, 11], () => def(Shape.Fluid, 0xff8418, 0xf06a0c, Layer.Opaque, F_SELF_CULL | F_LAVA | F_EMISSIVE));
  cube(12, 0xdbd29e);
  cube(13, 0x8a807b);
  cube(14, 0x9a9070);
  cube(15, 0x9a8c84);
  cube(16, 0x6e6e6e);
  cube(17, 0xb8955e, 0x5c4326);
  t[18] = def(Shape.Cube, 0x3d8c2c, 0x3d8c2c, Layer.Cutout, F_SELF_CULL | F_BIOME_FOLIAGE | F_LEAVES);
  cube(19, 0xcac454);
  t[20] = def(Shape.Cube, 0xcdeeff, 0xcdeeff, Layer.Translucent, F_SELF_CULL, 0.35);
  cube(21, 0x5a6a90);
  cube(22, 0x2c4fa8);
  cube(23, 0x7a7a7a);
  cube(24, 0xd5c88f);
  cube(25, 0x6a4a34);
  t[26] = def(Shape.Partial, 0xa02828, 0xb38c55);
  set([27, 28, 66, 157], () => def(Shape.Partial, 0x9a9a9a, 0x6a5030, Layer.Cutout));
  cube(29, 0x9a8c6a, 0x7d7d7d);
  t[30] = def(Shape.Cross, 0xe8e8e8, 0xe8e8e8, Layer.Cutout);
  t[31] = def(Shape.Cross, 0x6ab84a, 0x4a9a34, Layer.Cutout, F_BIOME_GRASS);
  t[32] = def(Shape.Cross, 0x9a7a40, 0x7a5a30, Layer.Cutout);
  cube(33, 0xb38c55, 0x7d7d7d);
  t[34] = def(Shape.Slab, 0xb38c55, 0x7d7d7d);
  cube(35, 0xe6e6e6);
  t[36] = def(Shape.None, 0, 0);
  t[37] = def(Shape.Cross, 0xf8e030, 0x3c8a2c, Layer.Cutout);
  t[38] = def(Shape.Cross, 0xe02a2a, 0x3c8a2c, Layer.Cutout);
  t[39] = def(Shape.Cross, 0x9a7050, 0xd8c8b0, Layer.Cutout);
  t[40] = def(Shape.Cross, 0xd03030, 0xd8c8b0, Layer.Cutout);
  cube(41, 0xf2d443);
  cube(42, 0xdadada);
  cube(43, 0xa8a8a8, 0x9a9a9a);
  t[44] = def(Shape.Slab, 0xa8a8a8, 0x9a9a9a);
  cube(45, 0x9b4b3b);
  cube(46, 0xd8d0c0, 0xcc3222);
  cube(47, 0xb38c55, 0x7b4a2a);
  cube(48, 0x6e7e5c);
  cube(49, 0x241a38);
  t[50] = def(Shape.Torch, 0xffd860, 0x8a6a3a, Layer.Cutout, F_EMISSIVE);
  t[51] = def(Shape.Cross, 0xffb030, 0xff6010, Layer.Cutout, F_EMISSIVE);
  t[52] = def(Shape.Cube, 0x2a3a4a, 0x2a3a4a, Layer.Cutout);
  set([53, 67, 108, 109, 114, 128, 134, 135, 136, 156, 163, 164, 180, 203], () => def(Shape.Stairs, 0xb38c55, 0xa07c48));
  t[54] = def(Shape.Chest, 0xab6f2e, 0x9a6127);
  t[55] = def(Shape.Partial, 0xb01818, 0xb01818, Layer.Cutout);
  cube(56, 0x7aa0a0);
  cube(57, 0x74e0dc);
  cube(58, 0xa3733c, 0x8a5e33);
  t[59] = def(Shape.Cross, 0xc8b040, 0x6a9a2c, Layer.Cutout);
  t[60] = def(Shape.Partial, 0x5a3a1c, 0x7d5126);
  cube(61, 0x8a8a8a, 0x7a7a7a);
  cube(62, 0x8a8a8a, 0x7a7a7a, F_EMISSIVE);
  t[63] = def(Shape.Post, 0xb38c55, 0x8a6a3a);
  t[64] = def(Shape.Door, 0xa07c48, 0x8a6a3a, Layer.Cutout);
  t[65] = def(Shape.WallPlane, 0xa07c48, 0x8a6a3a, Layer.Cutout);
  t[68] = def(Shape.WallPlane, 0xb38c55, 0x8a6a3a);
  t[69] = def(Shape.Small, 0x7d7d7d, 0x6a5030);
  set([70, 147, 148], () => def(Shape.Partial, 0x9a9a9a));
  t[147] = def(Shape.Partial, 0xf2d443);
  t[148] = def(Shape.Partial, 0xdadada);
  t[71] = def(Shape.Door, 0xc8c8c8, 0xa0a0a0, Layer.Cutout);
  t[72] = def(Shape.Partial, 0xb38c55);
  cube(73, 0x8c7070);
  cube(74, 0xa06060, 0xa06060, F_EMISSIVE);
  t[75] = def(Shape.Torch, 0x802020, 0x8a6a3a, Layer.Cutout);
  t[76] = def(Shape.Torch, 0xff3020, 0x8a6a3a, Layer.Cutout, F_EMISSIVE);
  set([77, 143], () => def(Shape.Small, 0x8c8c8c));
  t[143] = def(Shape.Small, 0xb38c55);
  t[78] = def(Shape.Partial, 0xf0f6ff);
  t[79] = def(Shape.Cube, 0x9cc0f4, 0x86a8e0, Layer.Translucent, F_SELF_CULL, 0.7);
  cube(80, 0xf0f6ff);
  t[81] = def(Shape.Inset, 0x3a8a34, 0x2c7a2a);
  cube(82, 0x9ea3b2);
  t[83] = def(Shape.Cross, 0x8ad060, 0x6ab048, Layer.Cutout);
  cube(84, 0x6a4a34, 0x5a3a24);
  set([85, 113, 188, 189, 190, 191, 192], () => def(Shape.Fence, 0xa07c48, 0x8a6a3a));
  t[113] = def(Shape.Fence, 0x2c1418, 0x2c1418);
  cube(86, 0xd88a22, 0xc87818);
  cube(87, 0x7d3030);
  cube(88, 0x5c4636);
  cube(89, 0xf2d47a, 0xe6c060, F_EMISSIVE);
  t[90] = def(Shape.Cube, 0x9a4ad8, 0x7a32b8, Layer.Translucent, F_SELF_CULL | F_EMISSIVE, 0.6);
  cube(91, 0xd88a22, 0xe8a030, F_EMISSIVE);
  t[92] = def(Shape.Partial, 0xf0e8e0, 0xc89060);
  set([93, 94, 149, 150], () => def(Shape.Partial, 0xa8a4a0, 0x9a9a9a));
  t[95] = def(Shape.Cube, 0xffffff, 0xffffff, Layer.Translucent, F_SELF_CULL, 0.45); // 1.12.2 stained glass (b1.8.1: chest)
  t[96] = def(Shape.Trapdoor, 0xa07c48, 0xa07c48, Layer.Cutout);
  cube(97, 0x8c8c8c);
  cube(98, 0x7f7f7f);
  cube(99, 0x8d6a52);
  cube(100, 0xb52a26);
  t[101] = def(Shape.Pane, 0x8a8a8a, 0x8a8a8a, Layer.Cutout);
  t[102] = def(Shape.Pane, 0xcdeeff, 0xcdeeff, Layer.Translucent, 0, 0.35);
  cube(103, 0x77a632, 0x6a9a2c);
  set([104, 105], () => def(Shape.Cross, 0x7a9a3a, 0x7a9a3a, Layer.Cutout));
  t[106] = def(Shape.WallPlane, 0x3d8c2c, 0x3d8c2c, Layer.Cutout, F_BIOME_FOLIAGE);
  set([107, 183, 184, 185, 186, 187], () => def(Shape.Gate, 0xa07c48, 0x8a6a3a));
  cube(110, 0x6f6265, 0x8b5a2b);
  t[111] = def(Shape.Partial, 0x208030, 0x208030, Layer.Cutout);
  cube(112, 0x2c1418);
  t[115] = def(Shape.Cross, 0x8a1e22, 0x8a1e22, Layer.Cutout);
  t[116] = def(Shape.Partial, 0xa02828, 0x241a38);
  t[117] = def(Shape.Small, 0x7a7a6a);
  cube(118, 0x3a3a3a);
  t[119] = def(Shape.Partial, 0x0a0a14, 0x0a0a14, Layer.Opaque, F_EMISSIVE);
  t[120] = def(Shape.Partial, 0x5a7a68, 0xdcdca0);
  cube(121, 0xdcdca0);
  t[122] = def(Shape.Small, 0x1a0a20);
  cube(123, 0x7a4a24);
  cube(124, 0xe8b070, 0xe8b070, F_EMISSIVE);
  cube(125, 0xb38c55);
  t[126] = def(Shape.Slab, 0xb38c55);
  t[127] = def(Shape.Small, 0x9a6030);
  cube(129, 0x7aa08a);
  t[130] = def(Shape.Chest, 0x1e2a2a, 0x1e2a2a);
  t[131] = def(Shape.Small, 0x8a8a8a);
  t[132] = def(Shape.Partial, 0xd8d8d8, 0xd8d8d8, Layer.Cutout);
  cube(133, 0x50d080);
  cube(137, 0xb08a70);
  t[138] = def(Shape.Cube, 0x70e0dc, 0x70e0dc, Layer.Cutout, F_EMISSIVE);
  t[139] = def(Shape.Wall, 0x7d7d7d);
  t[140] = def(Shape.Small, 0x7a3a24);
  set([141, 142, 207], () => def(Shape.Cross, 0x6ab048, 0x4a9a34, Layer.Cutout));
  t[144] = def(Shape.Small, 0xc8c8b8);
  cube(145, 0x444444);
  t[146] = def(Shape.Chest, 0xab6f2e, 0x9a6127);
  set([151, 178], () => def(Shape.Partial, 0xd8c8a0, 0xb38c55));
  cube(152, 0xaa1c0c);
  cube(153, 0x8a7070);
  cube(154, 0x444444);
  cube(155, 0xece6dc);
  cube(158, 0x7a7a7a);
  cube(159, 0x985e43);
  t[160] = def(Shape.Pane, 0xffffff, 0xffffff, Layer.Translucent, 0, 0.45);
  t[161] = def(Shape.Cube, 0x3d8c2c, 0x3d8c2c, Layer.Cutout, F_SELF_CULL | F_BIOME_FOLIAGE | F_LEAVES);
  cube(162, 0xa85a32, 0x696259);
  t[165] = def(Shape.Cube, 0x78c868, 0x78c868, Layer.Translucent, F_SELF_CULL, 0.7);
  t[166] = def(Shape.None, 0, 0);
  t[167] = def(Shape.Trapdoor, 0xc8c8c8, 0xc8c8c8, Layer.Cutout);
  cube(168, 0x63a090);
  cube(169, 0xd8e8e0, 0xd8e8e0, F_EMISSIVE);
  cube(170, 0xb89a20, 0xa68a1c);
  t[171] = def(Shape.Partial, 0xe6e6e6);
  cube(172, 0x985e43);
  cube(173, 0x161616);
  cube(174, 0x8cb4f0);
  t[175] = def(Shape.Cross, 0x6ab84a, 0x4a9a34, Layer.Cutout, F_BIOME_GRASS);
  t[176] = def(Shape.Post, 0xe6e6e6, 0xb38c55);
  t[177] = def(Shape.WallPlane, 0xe6e6e6, 0xe6e6e6);
  cube(179, 0xa95821);
  cube(181, 0xa95821);
  t[182] = def(Shape.Slab, 0xa95821);
  set([193, 194, 195, 196, 197], () => def(Shape.Door, 0xa07c48, 0x8a6a3a, Layer.Cutout));
  t[198] = def(Shape.Torch, 0xf0f0e8, 0xf0f0e8, Layer.Cutout, F_EMISSIVE);
  cube(199, 0x5a3a5a);
  cube(200, 0x9a7a9a);
  cube(201, 0xa97ca9);
  cube(202, 0xab80ab);
  cube(204, 0xa97ca9);
  t[205] = def(Shape.Slab, 0xa97ca9);
  cube(206, 0xdcdca0);
  t[208] = def(Shape.Partial, 0x9a8048, 0x8b5a2b);
  t[209] = def(Shape.Cube, 0x0a0a14, 0x0a0a14, Layer.Opaque, F_OPAQUE | F_EMISSIVE);
  cube(210, 0x6a4ab0);
  cube(211, 0x5a9a8a);
  t[212] = def(Shape.Cube, 0x9cc0f4, 0x86a8e0, Layer.Translucent, F_SELF_CULL, 0.7);
  cube(213, 0x8a3a10, 0x8a3a10, F_EMISSIVE);
  cube(214, 0x7a0a0a);
  cube(215, 0x4a0808);
  cube(216, 0xe0dcc8);
  t[217] = def(Shape.None, 0, 0);
  cube(218, 0x5a5a5a);
  for (let i = 0; i < 16; i++) {
    cube(219 + i, DYE[i]);
    cube(235 + i, TERRACOTTA[i]);
  }
  cube(251, 0xcfd5d6);
  cube(252, 0xe1e3e3);
  cube(255, 0x5a4a5a);
  for (const id of [253, 254]) cube(id, 0xa8a4b0);
  // Fix the cubes that were set as cubes above but are not opaque.
  for (const id of [18, 20, 52, 79, 90, 95, 138, 161, 165, 212]) if (t[id]) t[id]!.flags &= ~F_OPAQUE;
  t[0] = def(Shape.None, 0, 0);
  return t;
}

const TABLE_1122 = baseTable();
const TABLE_B181: Table = (() => {
  const t = baseTable().slice();
  t[95] = def(Shape.Chest, 0xab6f2e, 0x9a6127); // locked chest
  // b1.8.1 ice/leaves/glass behave the same; ids above 109 do not exist (left as grey cubes in case of a modded server).
  return t;
})();

export function tableFor(version: GameVersion): Table {
  return version === 'b1.8.1' ? TABLE_B181 : TABLE_1122;
}

/** Slab variants (b1.8.1: 0 stone, 1 sandstone, 2 wood, 3 cobble; 1.12.2 adds 4 brick, 5 stone brick, 6 nether brick, 7 quartz). */
const SLAB: number[] = [0xa8a8a8, 0xd5c88f, 0xb38c55, 0x7d7d7d, 0x9b4b3b, 0x7f7f7f, 0x2c1418, 0xece6dc];

/** A face colour that depends on the state: (table entry, meta, top face, 1.12.2). */
type Variant = (d: BlockDef, meta: number, up: boolean, modern: boolean) => number;

const planks: Variant = (d, meta, _up, modern) => (modern ? PLANKS[(meta & 7) % 6] : d.top);
const dyed: Variant = (_d, meta) => DYE[meta & 15];
const stainedGlass: Variant = (d, meta, _up, modern) => (modern ? DYE[meta & 15] : d.top);
const topOnly: Variant = (d) => d.top;

/** Dirt: 1.12.2 coarse dirt (1) and podzol (2). */
const dirt: Variant = (d, meta, _up, modern) => {
  if (!modern) return d.top;
  const v = meta & 3;
  return v === 2 ? 0x5a3d1e : v === 1 ? 0x77553b : d.top;
};

/** Leaves: spruce (1) and birch (2). */
const leaves: Variant = (d, meta) => {
  const v = meta & 3;
  return v === 1 ? 0x3f6a3f : v === 2 ? 0x5f8a3e : d.top;
};

/** The meta-dependent variants, by id; ids absent use the table's top / side colour. */
const VARIANTS: Array<Variant | undefined> = (() => {
  const t = new Array<Variant | undefined>(256);
  const set = (ids: number[], f: Variant) => ids.forEach((id) => (t[id] = f));
  set([1], (d, meta, _up, modern) => (modern ? (STONE[meta % 7] ?? STONE[0]) : d.top));
  set([3], dirt);
  set([5, 125, 126], planks);
  set([12], (d, meta, _up, modern) => (modern && meta === 1 ? 0xa95821 : d.top)); // red sand
  set([17], (d, meta, up, modern) => (up ? (modern ? PLANKS[meta & 3] : d.top) : LOG_SIDE[meta & 3]));
  set([162], (_d, meta, up) => (up ? PLANKS[4 + (meta & 1)] : LOG2_SIDE[meta & 1]));
  set([18], leaves);
  set([161], () => 0x3d8c2c);
  set([35, 171], dyed);
  set([95, 160], stainedGlass);
  set([159], (_d, meta) => TERRACOTTA[meta & 15]);
  set([251], (_d, meta) => scaleRgb(DYE[meta & 15], 0.85)); // concrete
  set([252], (_d, meta) => scaleRgb(DYE[meta & 15], 1.0)); // concrete powder
  set([43, 44], (_d, meta) => SLAB[meta & 7]);
  set([24, 179], topOnly);
  return t;
})();

/** Flat colour of a state's face (0xRRGGBB): the meta-dependent variants first, then the table. */
export function flatColour(version: GameVersion, id: number, meta: number, up: boolean): number {
  const d = tableFor(version)[id];
  if (!d) return 0x808080;
  const variant = VARIANTS[id];
  if (variant) return variant(d, meta, up, version === '1.12.2');
  return up ? d.top : d.side;
}

export function scaleRgb(rgb: number, k: number): number {
  const r = Math.min(255, Math.round(((rgb >> 16) & 255) * k));
  const g = Math.min(255, Math.round(((rgb >> 8) & 255) * k));
  const b = Math.min(255, Math.round((rgb & 255) * k));
  return (r << 16) | (g << 8) | b;
}

/** True for the chest kinds that pair into double chests (ChestPairs.IsPairable: 54 with 54, trapped 146 with 146). */
export function pairsAsChest(version: GameVersion, id: number): boolean {
  return id === 54 || (version === '1.12.2' && id === 146);
}
