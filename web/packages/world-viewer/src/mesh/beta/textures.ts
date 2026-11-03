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

/** BlockMushroomCap: which faces show the cap (meta 1..9 = the 3×3 cap pieces, 10 stem, 14 all cap, 15 all stem). */
function mushroom(cap: number, meta: number, f: number): TileRef {
  if (meta === 10 && f > 1) return ref(141);
  if (meta === 15) return ref(141);
  if (meta === 14) return ref(cap);
  if (meta >= 1 && meta <= 9 && f === 1) return ref(cap);
  if (meta >= 1 && meta <= 3 && f === 2) return ref(cap);
  if (meta >= 7 && meta <= 9 && f === 3) return ref(cap);
  if ((meta === 1 || meta === 4 || meta === 7) && f === 4) return ref(cap);
  if ((meta === 3 || meta === 6 || meta === 9) && f === 5) return ref(cap);
  return ref(142);
}

/** `fancy`: alpha-cutout leaves (52/132) rather than the opaque fast ones (53/133). */
export function tileFor(id: number, meta: number, f: number, fancy = true): TileRef {
  const top = f === 1, bottom = f === 0, side = !top && !bottom;
  switch (id) {
    case 1: return ref(1);
    case 2: return top ? ref(0, GRASS_TINT) : bottom ? ref(2) : ref(3, 0, GRASS_SIDE_OVERLAY, GRASS_TINT);
    case 3: return ref(2);
    case 4: return ref(16);
    case 5: return ref(4);
    case 6: return ref([15, 63, 79, 15][meta & 3]);
    case 7: return ref(17);
    case 12: return ref(18);
    case 13: return ref(19);
    case 14: return ref(32);
    case 15: return ref(33);
    case 16: return ref(34);
    case 17: return top || bottom ? ref(21) : ref([20, 116, 117, 20][meta & 3]);
    case 18: return ref(((meta & 3) === 1 ? 132 : 52) + (fancy ? 0 : 1), [FOLIAGE_TINT, SPRUCE_TINT, BIRCH_TINT, FOLIAGE_TINT][meta & 3]);
    case 19: return ref(48);
    case 20: return ref(49);
    case 21: return ref(160);
    case 22: return ref(144);
    case 23: return top ? ref(62) : side && frontOf(id, meta) === f ? ref(46) : ref(45);
    case 24: return top ? ref(176) : bottom ? ref(208) : ref(192);
    case 25: return ref(74);
    case 27: return ref((meta & 8) !== 0 ? 179 : 163);
    case 28: return ref(195);
    case 30: return ref(11);
    case 31: return (meta & 3) === 0 ? ref(55) : (meta & 3) === 2 ? ref(56, GRASS_TINT) : ref(39, GRASS_TINT);
    case 32: return ref(55);
    case 35: return ref(WOOL[meta & 15]);
    case 37: return ref(13);
    case 38: return ref(12);
    case 39: return ref(29);
    case 40: return ref(28);
    case 41: return ref(23);
    case 42: return ref(22);
    case 43:
    case 44:
      switch (meta & 7) {
        case 1: return top || bottom ? ref(176) : ref(192);
        case 2: return ref(4);
        case 3: return ref(16);
        case 4: return ref(7);
        case 5: return ref(54);
        default: return top || bottom ? ref(6) : ref(5);
      }
    case 45: return ref(7);
    case 46: return top ? ref(9) : bottom ? ref(10) : ref(8);
    case 47: return top || bottom ? ref(4) : ref(35);
    case 48: return ref(36);
    case 49: return ref(37);
    case 50: return ref(80);
    case 51: return ref(31);
    case 52: return ref(65);
    case 53: return ref(4);
    case 54:
    case 95: return top || bottom ? ref(25) : f === 3 ? ref(27) : ref(26);
    case 55: return ref(164);
    case 56: return ref(50);
    case 57: return ref(24);
    case 58: return top ? ref(43) : bottom ? ref(4) : f === 2 || f === 4 ? ref(60) : ref(59);
    case 59: return ref(88 + Math.max(0, Math.min(7, meta)));
    case 60: return top ? ref(meta > 0 ? 86 : 87) : ref(2);
    case 61: return top || bottom ? ref(62) : frontOf(id, meta) === f ? ref(44) : ref(45);
    case 62: return top || bottom ? ref(62) : frontOf(id, meta) === f ? ref(61) : ref(45);
    case 63:
    case 68: return ref(4);
    case 64: return ref((meta & 8) !== 0 ? 81 : 97);
    case 71: return ref((meta & 8) !== 0 ? 82 : 98);
    case 65: return ref(83);
    case 66: return ref(meta >= 6 ? 112 : 128);
    case 67: return ref(16);
    case 69: return ref(96);
    case 70: return ref(1);
    case 72: return ref(4);
    case 73:
    case 74: return ref(51);
    case 75: return ref(115);
    case 76: return ref(99);
    case 77: return ref(1);
    case 78: return ref(66);
    case 79: return ref(67);
    case 80: return ref(66);
    case 81: return top ? ref(69) : bottom ? ref(71) : ref(70);
    case 82: return ref(72);
    case 83: return ref(73);
    case 84: return top ? ref(75) : ref(74);
    case 85: return ref(4);
    case 86: return top || bottom ? ref(102) : frontOf(id, meta) === f ? ref(119) : ref(118);
    case 87: return ref(103);
    case 88: return ref(104);
    case 89: return ref(105);
    case 90: return ref(14);
    case 91: return top || bottom ? ref(102) : frontOf(id, meta) === f ? ref(120) : ref(118);
    case 92: return top ? ref(121) : bottom ? ref(124) : ref(122);
    case 96: return ref(84);
    case 97: return ref([1, 16, 54][meta % 3] ?? 1);
    case 98: return ref([54, 100, 101, 54][meta & 3]);
    case 99: return mushroom(126, meta, f);
    case 100: return mushroom(125, meta, f);
    case 101: return ref(85);
    case 102: return ref(49);
    case 103: return top || bottom ? ref(137) : ref(136);
    case 107: return ref(4);
    case 108: return ref(7);
    case 109: return ref(54);
    case 106: return ref(143, FOLIAGE_TINT);
    default: return NO_TILE;
  }
}

/** Blocks drawn at full brightness (vanilla light values ≥ 13 that matter here). */
export function emissiveBeta(id: number): boolean {
  return [50, 89, 10, 11, 51, 91, 62, 90, 76, 74, 94].includes(id);
}
