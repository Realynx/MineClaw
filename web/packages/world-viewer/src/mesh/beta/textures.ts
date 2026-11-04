// b1.8.1 block id + metadata + face → terrain.png tile (the vanilla Block.getBlockTextureFromSideAndMetadata values), the
// fixed tints of grey tiles (grass 0x91BD59, foliage 0x48B518, spruce 0x619961, birch 0x80A755: b1.8.1 biome colours come
// from the world seed, not the chunk) and the grass side overlay. Ported from the C# BlockTextures (+ silverfish stone and
// huge mushroom faces, which it leaves to the palette). Tile -1 = no tile (flat palette colour).

export const GRASS_TINT = 0x91bd59, FOLIAGE_TINT = 0x48b518, SPRUCE_TINT = 0x619961, BIRCH_TINT = 0x80a755;
export const GRASS_SIDE_OVERLAY = 38;

/** The procedural tiles TextureFX animates every tick (water/lava still + flow, fire, portal). */
export const TILE_WATER_STILL = 205, TILE_WATER_FLOW = 206, TILE_LAVA_STILL = 237, TILE_LAVA_FLOW = 238, TILE_FIRE_A = 31, TILE_FIRE_B = 47, TILE_PORTAL = 14;

export interface TileRef {
  tile: number;
  tint: number;
  overlay: number;
  overlayTint: number;
}

const ref = (tile: number, tint = 0, overlay = -1, overlayTint = 0): TileRef => ({ tile, tint, overlay, overlayTint });
export const NO_TILE: TileRef = ref(-1);

const WOOL = [64, 210, 194, 178, 162, 146, 130, 114, 225, 209, 193, 177, 161, 145, 129, 113];

/** Furnace/dispenser fronts (2 N, 3 S, 4 W, 5 E) and pumpkins (0 S, 1 W, 2 N, 3 E) → face. */
function frontOf(id: number, meta: number): number {
  if (id === 86 || id === 91) return [3, 4, 2, 5][meta & 3];
  return meta >= 2 && meta <= 5 ? meta : 3;
}

/** BlockMushroomCap pieces 1..9 (the 3×3 cap: 1 NW … 9 SE) → the faces (bit per face) showing the cap: the top, plus
 * north for the first row, south for the last, west for the first column, east for the last. */
function capFaces(meta: number): number {
  if (meta < 1 || meta > 9) return 0;
  const row = Math.floor((meta - 1) / 3), col = (meta - 1) % 3;
  return (1 << 1) | (row === 0 ? 1 << 2 : 0) | (row === 2 ? 1 << 3 : 0) | (col === 0 ? 1 << 4 : 0) | (col === 2 ? 1 << 5 : 0);
}
const CAP_FACES = Array.from({ length: 16 }, (_, meta) => capFaces(meta));

/** BlockMushroomCap: which faces show the cap (meta 1..9 = the 3×3 cap pieces, 10 stem, 14 all cap, 15 all stem). */
function mushroom(cap: number, meta: number, f: number): TileRef {
  if (meta === 15 || (meta === 10 && f > 1)) return ref(141);
  if (meta === 14 || (CAP_FACES[meta] & (1 << f)) !== 0) return ref(cap);
  return ref(142);
}

type TileRule = (meta: number, f: number, fancy: boolean) => TileRef;

/** The terrain.png tile rule per block id (undefined = no tile). */
const TILE_RULES: Array<TileRule | undefined> = [];
const rule = (ids: readonly number[], fn: TileRule) => {
  for (const id of ids) TILE_RULES[id] = fn;
};

/** One tile on every face, whatever the metadata. */
const SINGLE: ReadonlyArray<readonly [number, number]> = [
  [1, 1], [3, 2], [4, 16], [5, 4], [7, 17], [12, 18], [13, 19], [14, 32], [15, 33], [16, 34], [19, 48], [20, 49], [21, 160],
  [22, 144], [25, 74], [28, 195], [30, 11], [32, 55], [37, 13], [38, 12], [39, 29], [40, 28], [41, 23], [42, 22], [45, 7],
  [48, 36], [49, 37], [50, 80], [51, 31], [52, 65], [53, 4], [55, 164], [56, 50], [57, 24], [63, 4], [68, 4], [65, 83],
  [67, 16], [69, 96], [70, 1], [72, 4], [73, 51], [74, 51], [75, 115], [76, 99], [77, 1], [78, 66], [79, 67], [80, 66],
  [82, 72], [83, 73], [85, 4], [87, 103], [88, 104], [89, 105], [90, 14], [96, 84], [101, 85], [102, 49], [107, 4],
  [108, 7], [109, 54],
];
for (const [id, tile] of SINGLE) rule([id], () => ref(tile));

/** [top, bottom, sides]. */
const BY_FACE: ReadonlyArray<readonly [number, number, number, number]> = [
  [24, 176, 208, 192], [46, 9, 10, 8], [47, 4, 4, 35], [81, 69, 71, 70], [84, 75, 74, 74], [92, 121, 124, 122], [103, 137, 137, 136],
];
for (const [id, top, bottom, side] of BY_FACE) rule([id], (_m, f) => ref(f === 1 ? top : f === 0 ? bottom : side));

/** Furnaces, dispensers, pumpkins and jack-o'-lanterns: [top, bottom, front, other sides]. */
const FRONTED: ReadonlyArray<readonly [number, number, number, number, number]> = [
  [23, 62, 45, 46, 45], [61, 62, 62, 44, 45], [62, 62, 62, 61, 45], [86, 102, 102, 119, 118], [91, 102, 102, 120, 118],
];
for (const [id, top, bottom, front, side] of FRONTED)
  rule([id], (meta, f) => ref(f === 1 ? top : f === 0 ? bottom : frontOf(id, meta) === f ? front : side));

rule([2], (_m, f) => (f === 1 ? ref(0, GRASS_TINT) : f === 0 ? ref(2) : ref(3, 0, GRASS_SIDE_OVERLAY, GRASS_TINT)));
rule([6], (meta) => ref([15, 63, 79, 15][meta & 3]));
rule([17], (meta, f) => (f === 1 || f === 0 ? ref(21) : ref([20, 116, 117, 20][meta & 3])));
rule([18], (meta, _f, fancy) => ref(((meta & 3) === 1 ? 132 : 52) + (fancy ? 0 : 1), [FOLIAGE_TINT, SPRUCE_TINT, BIRCH_TINT, FOLIAGE_TINT][meta & 3]));
rule([27], (meta) => ref((meta & 8) !== 0 ? 179 : 163));
rule([31], (meta) => ((meta & 3) === 0 ? ref(55) : (meta & 3) === 2 ? ref(56, GRASS_TINT) : ref(39, GRASS_TINT)));
rule([35], (meta) => ref(WOOL[meta & 15]));
rule([43, 44], slab);
rule([54, 95], (_m, f) => (f === 1 || f === 0 ? ref(25) : f === 3 ? ref(27) : ref(26)));
rule([58], (_m, f) => (f === 1 ? ref(43) : f === 0 ? ref(4) : f === 2 || f === 4 ? ref(60) : ref(59)));
rule([59], (meta) => ref(88 + Math.max(0, Math.min(7, meta))));
rule([60], (meta, f) => (f === 1 ? ref(meta > 0 ? 86 : 87) : ref(2)));
rule([64], (meta) => ref((meta & 8) !== 0 ? 81 : 97));
rule([71], (meta) => ref((meta & 8) !== 0 ? 82 : 98));
rule([66], (meta) => ref(meta >= 6 ? 112 : 128));
rule([97], (meta) => ref([1, 16, 54][meta % 3] ?? 1));
rule([98], (meta) => ref([54, 100, 101, 54][meta & 3]));
rule([99], (meta, f) => mushroom(126, meta, f));
rule([100], (meta, f) => mushroom(125, meta, f));
rule([106], () => ref(143, FOLIAGE_TINT));

/** Stone slabs by material (meta & 7): stone, sandstone, wood, cobblestone, brick, stone brick. */
function slab(meta: number, f: number): TileRef {
  const ends = f === 1 || f === 0;
  switch (meta & 7) {
    case 1: return ends ? ref(176) : ref(192);
    case 2: return ref(4);
    case 3: return ref(16);
    case 4: return ref(7);
    case 5: return ref(54);
    default: return ends ? ref(6) : ref(5);
  }
}

/** `fancy`: alpha-cutout leaves (52/132) rather than the opaque fast ones (53/133). */
export function tileFor(id: number, meta: number, f: number, fancy = true): TileRef {
  const fn = TILE_RULES[id];
  return fn ? fn(meta, f, fancy) : NO_TILE;
}

/** Blocks drawn at full brightness (vanilla light values ≥ 13 that matter here). */
export function emissiveBeta(id: number): boolean {
  return [50, 89, 10, 11, 51, 91, 62, 90, 76, 74, 94].includes(id);
}
