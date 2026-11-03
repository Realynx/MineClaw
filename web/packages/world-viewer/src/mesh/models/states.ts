// The 1.12.2 (id, meta) → blockstate mapping, ported in our own words from the C# BlockStates1122 (itself from the
// client's Block.getStateFromMeta per block class, BlockModelShapes' state mappers and getActualState):
// - metadata properties per block class (stair facing/half, slab halves and seamless doubles, log axes, torch/door/trapdoor/
//   button/lever/rail/crop/… facings and states);
// - the file named by the variant ("birch_log", "red_wool", "stone_brick_slab"), ignored properties dropped;
// - built-in blocks (chests, signs, beds, skulls, banners, shulker boxes, fluids) drawn by our own geometry;
// - neighbour-dependent properties: stair shapes (inner/outer corners), fence/pane/wall/bar connections, the wall's post,
//   fence gates in walls, door halves combined (facing/open from the lower, hinge/power from the upper), the double plant's
//   upper half, snowy grass/mycelium/podzol, the vine's top, stems bending to their fruit, redstone wire sides and climbs,
//   fire on flammable neighbours, chest pairs.

import type { BlockEntityDto } from '../../types';
import { dyeIndex } from '../../wire/normalize';
import { BLOCK_NAMES_1122 } from '../../world/blocks';

/** Neighbourhood access around the block being resolved (dx, dy, dz relative). */
export interface Nb {
  id(dx: number, dy: number, dz: number): number;
  meta(dx: number, dy: number, dz: number): number;
  /** World position (position-hashed variants). */
  readonly wx: number;
  readonly wy: number;
  readonly wz: number;
  /** The block entity at the origin cell, if its column sent one (bed colour, skull type/rotation, pot contents, banner). */
  be?(): BlockEntityDto | undefined;
}

export interface StateRef {
  file: string;
  props: Record<string, string>;
  /** An exact variant key to prefer ("all"). */
  key: string | null;
  builtin: boolean;
}

export const WOOD = ['oak', 'spruce', 'birch', 'jungle', 'acacia', 'dark_oak'];
export const COLORS = ['white', 'orange', 'magenta', 'light_blue', 'yellow', 'lime', 'pink', 'gray', 'silver', 'cyan', 'purple', 'blue', 'brown', 'green', 'red', 'black'];
const STONE_SLAB = ['stone', 'sandstone', 'wood_old', 'cobblestone', 'brick', 'stone_brick', 'nether_brick', 'quartz'];
const RAIL = ['north_south', 'east_west', 'ascending_east', 'ascending_west', 'ascending_north', 'ascending_south', 'south_east', 'south_west', 'north_west', 'north_east'];
const LEVER = ['down_x', 'east', 'west', 'south', 'north', 'up_z', 'up_x', 'down_z'];
const BUTTON = ['down', 'east', 'west', 'south', 'north', 'up', 'up', 'up'];
const MUSHROOM = ['all_inside', 'north_west', 'north', 'north_east', 'west', 'center', 'east', 'south_west', 'south', 'south_east', 'stem', 'all_inside', 'all_inside', 'all_inside', 'all_outside', 'all_stem'];
const FLOWERS = ['poppy', 'blue_orchid', 'allium', 'houstonia', 'red_tulip', 'orange_tulip', 'white_tulip', 'pink_tulip', 'oxeye_daisy'];
const DOUBLE_PLANTS = ['sunflower', 'syringa', 'double_grass', 'double_fern', 'double_rose', 'paeonia'];
const HORIZ = ['north', 'east', 'south', 'west'] as const;

/** EnumFacing.byIndex: 0 down, 1 up, 2 north, 3 south, 4 west, 5 east. */
export const facing = (i: number): string => ['down', 'up', 'north', 'south', 'west', 'east'][Math.abs(i) % 6];
/** EnumFacing.byHorizontalIndex: 0 south, 1 west, 2 north, 3 east. */
export const hFacing = (i: number): string => ['south', 'west', 'north', 'east'][i & 3];
const horizontal = (i: number) => (i % 6 < 2 ? 'north' : facing(i));
const B = (b: boolean) => (b ? 'true' : 'false');

export const isStairs = (id: number) => [53, 67, 108, 109, 114, 128, 134, 135, 136, 156, 163, 164, 180, 203].includes(id);
export const isWoodFence = (id: number) => id === 85 || (id >= 188 && id <= 192);
export const isFence = (id: number) => isWoodFence(id) || id === 113;
export const isFenceGate = (id: number) => id === 107 || (id >= 183 && id <= 187);
export const isPane = (id: number) => id === 101 || id === 102 || id === 160;
export const isDoor = (id: number) => id === 64 || id === 71 || (id >= 193 && id <= 197);
/** Block.isExceptionBlockForAttaching (+ pistons): never a fence/pane/wall attachment even when full. */
const attachException = (id: number) => [18, 161, 20, 95, 138, 118, 86, 91, 103, 166, 29, 33, 34, 96, 167].includes(id) || (id >= 219 && id <= 234);

export function step(f: string): [number, number] {
  return f === 'north' ? [0, -1] : f === 'south' ? [0, 1] : f === 'west' ? [-1, 0] : f === 'east' ? [1, 0] : [0, 0];
}
const rotateYCCW = (f: string) => ({ north: 'west', west: 'south', south: 'east', east: 'north' })[f] ?? f;
const opposite = (f: string) => ({ north: 'south', south: 'north', west: 'east', east: 'west', up: 'down', down: 'up' })[f] ?? f;
export const stairFacing = (meta: number) => facing(5 - (meta & 3));
export const stairHalf = (meta: number) => ((meta & 4) !== 0 ? 'top' : 'bottom');

function name(id: number): string {
  return BLOCK_NAMES_1122[id] || 'air';
}

/** Blocks whose state needs the neighbours or the block entity (never cached per id/meta alone). */
export function worldDependent(id: number): boolean {
  return isStairs(id) || isFence(id) || isFenceGate(id) || isPane(id) || isDoor(id) || [139, 106, 175, 104, 105, 55, 54, 146, 2, 3, 110, 51, 26, 144, 140, 176, 177].includes(id);
}

const SKULLS = ['skeleton', 'wither', 'zombie', 'player', 'creeper', 'dragon'];

/** TileEntitySkull type (0 skeleton … 5 dragon, or a name) → our name; skeleton when unknown. */
export function skullType(v: unknown): string {
  if (typeof v === 'number') return SKULLS[v] ?? 'skeleton';
  if (typeof v !== 'string') return 'skeleton';
  const n = v.replace(/^minecraft:/, '').toLowerCase().replace(/_(skull|head)$/, '');
  if (n === 'wither_skeleton') return 'wither';
  if (n === 'char' || n === 'steve' || n === 'human') return 'player';
  if (n === 'ender_dragon' || n === 'enderdragon') return 'dragon';
  return SKULLS.includes(n) ? n : /^\d+$/.test(n) ? (SKULLS[+n] ?? 'skeleton') : 'skeleton';
}

const POT_FLOWERS = ['rose', 'blue_orchid', 'allium', 'houstonia', 'red_tulip', 'orange_tulip', 'white_tulip', 'pink_tulip', 'oxeye_daisy'];
const POT_NAMES: Record<string, string> = {
  red_flower: 'rose', poppy: 'rose', yellow_flower: 'dandelion', sapling: 'oak_sapling', deadbush: 'dead_bush', tallgrass: 'fern',
  brown_mushroom: 'mushroom_brown', red_mushroom: 'mushroom_red', azure_bluet: 'houstonia', orchid: 'blue_orchid', daisy: 'oxeye_daisy',
};

/** BlockFlowerPot contents from the pot's item ([id, meta] (1.12.2 numbering), {id, meta} or a name); "empty" when none. */
export function potContents(item: unknown): string {
  if (item == null || item === false) return 'empty';
  let id = 0, meta = 0;
  if (typeof item === 'string') {
    const n = item.replace(/^minecraft:/, '').toLowerCase();
    if (/^\d+$/.test(n)) id = +n;
    else {
      const c = POT_NAMES[n] ?? n;
      return POT_CONTENTS.has(c) ? c : 'empty';
    }
  } else if (Array.isArray(item)) {
    id = +item[0] || 0;
    meta = +item[1] || 0;
  } else if (typeof item === 'number') id = item;
  else if (typeof item === 'object') {
    const o = item as Record<string, unknown>;
    if (typeof o.id === 'string') return potContents(o.id);
    id = Number(o.id ?? o.item) || 0;
    meta = Number(o.meta ?? o.damage) || 0;
  }
  switch (id) {
    case 38: return POT_FLOWERS[meta] ?? 'rose';
    case 37: return 'dandelion';
    case 6: return WOOD[(meta & 7) % 6] + '_sapling';
    case 39: return 'mushroom_brown';
    case 40: return 'mushroom_red';
    case 32: return 'dead_bush';
    case 31: return meta === 2 ? 'fern' : 'empty';
    case 81: return 'cactus';
    default: return 'empty';
  }
}

const POT_CONTENTS = new Set(['rose', 'blue_orchid', 'allium', 'houstonia', 'red_tulip', 'orange_tulip', 'white_tulip', 'pink_tulip', 'oxeye_daisy', 'dandelion', 'oak_sapling', 'spruce_sapling', 'birch_sapling', 'jungle_sapling', 'acacia_sapling', 'dark_oak_sapling', 'mushroom_red', 'mushroom_brown', 'dead_bush', 'fern', 'cactus']);

/** Flammable neighbours fire climbs onto (Blocks.FIRE.setFireInfo encouragement > 0). */
const FLAMMABLE = new Set([5, 17, 18, 35, 46, 47, 53, 85, 106, 107, 125, 126, 134, 135, 136, 161, 162, 163, 164, 170, 171, 173, 175, 31, 32, 37, 38, 183, 184, 185, 186, 187, 188, 189, 190, 191, 192]);

/** Chest pairing (ChestPairs: the one horizontal same-kind neighbour, mutually). (dx, dz) of the partner, or null. */
export function chestPartner(nb: Nb, id: number): [number, number] | null {
  if (id !== 54 && id !== 146) return null;
  const only = (ox: number, oz: number): [number, number] | null => {
    let found: [number, number] | null = null;
    for (const [dx, dz] of [[-1, 0], [1, 0], [0, -1], [0, 1]] as const) {
      if (nb.id(ox + dx, 0, oz + dz) !== id) continue;
      if (found) return null;
      found = [dx, dz];
    }
    return found;
  };
  const o = only(0, 0);
  if (!o) return null;
  const back = only(o[0], o[1]);
  return back && back[0] === -o[0] && back[1] === -o[1] ? o : null;
}

/** LargeChest.PairFacing: the west/north half's facing unless it points along the pair (then the other's, else S / E). */
export function pairFacing(nb: Nb, partner: [number, number]): number {
  const alongX = partner[0] !== 0;
  const masterHere = partner[0] > 0 || partner[1] > 0;
  const mf = (masterHere ? nb.meta(0, 0, 0) : nb.meta(partner[0], 0, partner[1])) & 7;
  const of = (masterHere ? nb.meta(partner[0], 0, partner[1]) : nb.meta(0, 0, 0)) & 7;
  const across = (f: number) => (alongX ? f === 2 || f === 3 : f === 4 || f === 5);
  if (across(mf)) return mf;
  if (across(of)) return of;
  return alongX ? 3 : 5;
}

/**
 * Resolves (id, meta) to a blockstate reference. `nb` null = no world (static classification: neighbour properties take
 * their defaults). `solidFull(id, meta)` = a solid full cube neighbour (fence/pane/wall attachments, wire climbs).
 */
export function resolveState(id: number, meta: number, nb: Nb | null, solidFull: (id: number, meta: number) => boolean): StateRef {
  const p: Record<string, string> = {};
  const nm = name(id);
  const S = (file: string, key: string | null = null): StateRef => ({ file, props: p, key, builtin: false });
  const builtin = (): StateRef => ({ file: nm, props: p, key: null, builtin: true });
  const bk = (dx: number, dy: number, dz: number) => (nb ? nb.id(dx, dy, dz) : 0);
  const mt = (dx: number, dy: number, dz: number) => (nb ? nb.meta(dx, dy, dz) : 0);
  const solid = (dx: number, dy: number, dz: number) => {
    const b = bk(dx, dy, dz);
    return b !== 0 && !attachException(b) && solidFull(b, mt(dx, dy, dz));
  };
  const snowy = () => B(bk(0, 1, 0) === 78 || bk(0, 1, 0) === 80);

  switch (id) {
    case 1: return S(['stone', 'granite', 'smooth_granite', 'diorite', 'smooth_diorite', 'andesite', 'smooth_andesite'][meta % 7]);
    case 2:
    case 110:
      p.snowy = snowy();
      return S(nm);
    case 3:
      if (meta % 3 === 2) {
        p.snowy = snowy();
        return S('podzol');
      }
      return S(meta % 3 === 1 ? 'coarse_dirt' : 'dirt');
    case 5: return S(WOOD[meta % 6] + '_planks');
    case 6:
      p.stage = String((meta >> 3) & 1);
      return S(WOOD[(meta & 7) % 6] + '_sapling');
    case 8: case 9: case 10: case 11: return builtin();
    case 12: return S((meta & 1) === 1 ? 'red_sand' : 'sand');
    case 17:
    case 162:
      p.axis = ['y', 'x', 'z', 'none'][(meta & 12) >> 2];
      return S(WOOD[id === 162 ? Math.min(5, (meta & 3) + 4) : meta & 3] + '_log');
    case 18: return S(WOOD[meta & 3] + '_leaves');
    case 161: return S(WOOD[4 + (meta & 1)] + '_leaves');
    case 19:
      p.wet = B((meta & 1) === 1);
      return S(nm);
    case 23:
    case 158:
      p.facing = facing(meta & 7);
      return S(nm);
    case 24: return S(['sandstone', 'chiseled_sandstone', 'smooth_sandstone'][meta % 3]);
    case 179: return S(['red_sandstone', 'chiseled_red_sandstone', 'smooth_red_sandstone'][meta % 3]);
    case 27: case 28: case 157:
      p.shape = RAIL[(meta & 7) % 6];
      p.powered = B((meta & 8) !== 0);
      return S(nm);
    case 66:
      p.shape = RAIL[meta % 10];
      return S(nm);
    case 29: case 33:
      p.facing = facing(meta & 7);
      p.extended = B((meta & 8) !== 0);
      return S(nm);
    case 34:
      p.facing = facing(meta & 7);
      p.type = (meta & 8) !== 0 ? 'sticky' : 'normal';
      p.short = 'false';
      return S(nm);
    case 31: return S(['dead_bush', 'tall_grass', 'fern'][meta % 3]);
    case 32: return S('dead_bush');
    case 35: return S(COLORS[meta & 15] + '_wool');
    case 171: return S(COLORS[meta & 15] + '_carpet');
    case 159: return S(COLORS[meta & 15] + '_stained_hardened_clay');
    case 95: return S(COLORS[meta & 15] + '_stained_glass');
    case 251: return S(COLORS[meta & 15] + '_concrete');
    case 252: return S(COLORS[meta & 15] + '_concrete_powder');
    case 160:
      pane();
      return S(COLORS[meta & 15] + '_stained_glass_pane');
    case 101: case 102:
      pane();
      return S(nm);
    case 37: return S('dandelion');
    case 38: return S(FLOWERS[meta % 9]);
    case 43: return S(STONE_SLAB[meta & 7] + '_double_slab', (meta & 8) !== 0 ? 'all' : 'normal');
    case 44:
      p.half = (meta & 8) !== 0 ? 'top' : 'bottom';
      return S(STONE_SLAB[meta & 7] + '_slab');
    case 125: return S(WOOD[(meta & 7) % 6] + '_double_slab');
    case 126:
      p.half = (meta & 8) !== 0 ? 'top' : 'bottom';
      return S(WOOD[(meta & 7) % 6] + '_slab');
    case 181: return S('red_sandstone_double_slab', (meta & 8) !== 0 ? 'all' : 'normal');
    case 182: case 205:
      p.half = (meta & 8) !== 0 ? 'top' : 'bottom';
      return S(id === 182 ? 'red_sandstone_slab' : 'purpur_slab');
    case 204: return S('purpur_double_slab');
    case 50: case 75: case 76:
      p.facing = ({ 1: 'east', 2: 'west', 3: 'south', 4: 'north' } as Record<number, string>)[meta] ?? 'up';
      return S(nm);
    case 51: {
      // BlockFire.getActualState: flip/alt by position, sides toward flammable neighbours unless it sits on solid ground.
      const f = (x: number) => Math.floor(x);
      p.flip = B(((f(nb?.wx ?? 0) + f(nb?.wy ?? 0) + f(nb?.wz ?? 0)) & 1) === 1);
      p.alt = B(((((nb?.wx ?? 0) >> 1) + ((nb?.wy ?? 0) >> 1) + ((nb?.wz ?? 0) >> 1)) & 1) === 1);
      const below = bk(0, -1, 0);
      const ground = below !== 0 && solidFull(below, mt(0, -1, 0));
      p.upper = '0';
      for (const d of HORIZ) {
        const [dx, dz] = step(d);
        p[d] = B(!ground && FLAMMABLE.has(bk(dx, 0, dz)));
      }
      p.up = B(!ground && FLAMMABLE.has(bk(0, 1, 0)));
      return S(nm);
    }
    case 54: case 130: case 146: {
      const kind = id === 130 ? 'ender' : id === 146 ? 'trapped' : 'normal';
      p.kind = kind;
      const mate = nb && id !== 130 ? chestPartner(nb, id) : null;
      if (nb && mate) {
        p.facing = facing(pairFacing(nb, mate));
        p.pair = `${mate[0]},${mate[1]}`;
      } else p.facing = horizontal(meta);
      return builtin();
    }
    case 26: {
      p.facing = hFacing(meta & 3);
      p.part = (meta & 8) !== 0 ? 'head' : 'foot';
      const c = dyeIndex(nb?.be?.()?.color);
      p.color = COLORS[c >= 0 ? c : 14];
      return builtin();
    }
    case 63: case 176:
      p.rotation = String(meta & 15);
      if (id === 176 && nb?.be?.()?.kind === 'banner') p.layer = 'true'; // drawn by the block-entity layer
      return builtin();
    case 68: case 177:
      p.facing = horizontal(meta);
      if (id === 177 && nb?.be?.()?.kind === 'banner') p.layer = 'true'; // drawn by the block-entity layer (patterns, sway)
      return builtin();
    case 144: {
      p.facing = facing(meta & 7);
      const be = nb?.be?.();
      p.type = skullType(be?.type);
      if (p.facing === 'up') p.rotation = String((be?.rotation ?? 0) & 15);
      return builtin();
    }
    case 140:
      p.contents = potContents(nb?.be?.()?.item);
      p.legacy_data = '0';
      return S(nm);
    case 119: case 209: case 166: case 36: case 217:
      return builtin();
    case 55:
      for (const f of HORIZ) p[f] = wire(f);
      p.power = String(meta & 15); // the model ignores it (the mapper drops it); the tint reads it
      return S(nm);
    case 59: case 141: case 142:
      p.age = String(meta & 7);
      return S(nm);
    case 207: case 115:
      p.age = String(meta & 3);
      return S(nm);
    case 212:
      p.age = String(meta & 3);
      return S(nm);
    case 60:
      p.moisture = String(meta & 7);
      return S(nm);
    case 61: case 62: case 65:
      p.facing = horizontal(meta);
      return S(nm);
    case 64: case 71: case 193: case 194: case 195: case 196: case 197:
      door();
      return S(nm);
    case 69:
      p.facing = LEVER[meta & 7];
      p.powered = B((meta & 8) !== 0);
      return S(nm);
    case 70: case 72:
      p.powered = B(meta === 1);
      return S(nm);
    case 147: case 148:
      p.power = String(meta & 15);
      return S(nm);
    case 77: case 143:
      p.facing = BUTTON[meta & 7];
      p.powered = B((meta & 8) !== 0);
      return S(nm);
    case 78:
      p.layers = String((meta & 7) + 1);
      return S(nm);
    case 80: return S('snow');
    case 85: case 113: case 188: case 189: case 190: case 191: case 192:
      fence();
      return S(nm);
    case 86: case 91:
      p.facing = hFacing(meta);
      return S(nm);
    case 90:
      p.axis = (meta & 3) === 2 ? 'z' : 'x';
      return S(nm);
    case 92:
      p.bites = String(Math.min(6, meta));
      return S(nm);
    case 93: case 94:
      p.facing = hFacing(meta);
      p.delay = String(1 + ((meta >> 2) & 3));
      p.locked = 'false';
      return S(nm);
    case 149: case 150:
      p.facing = hFacing(meta);
      p.mode = (meta & 4) !== 0 ? 'subtract' : 'compare';
      p.powered = B((meta & 8) !== 0);
      return S(nm);
    case 96: case 167:
      p.facing = ['north', 'south', 'west', 'east'][meta & 3];
      p.open = B((meta & 4) !== 0);
      p.half = (meta & 8) !== 0 ? 'top' : 'bottom';
      return S(nm);
    case 97: return S(['stone', 'cobblestone', 'stone_brick', 'mossy_brick', 'cracked_brick', 'chiseled_brick'][meta % 6] + '_monster_egg');
    case 98: return S(['stonebrick', 'mossy_stonebrick', 'cracked_stonebrick', 'chiseled_stonebrick'][meta & 3]);
    case 99: case 100:
      p.variant = MUSHROOM[meta & 15];
      return S(nm);
    case 104: case 105: {
      p.age = String(meta & 7);
      let f = 'up';
      if ((meta & 7) === 7)
        for (const d of HORIZ) {
          const [dx, dz] = step(d);
          if (bk(dx, 0, dz) === (id === 104 ? 86 : 103)) {
            f = d;
            break;
          }
        }
      p.facing = f;
      if (f !== 'up') delete p.age;
      return S(nm);
    }
    case 106:
      p.south = B((meta & 1) !== 0);
      p.west = B((meta & 2) !== 0);
      p.north = B((meta & 4) !== 0);
      p.east = B((meta & 8) !== 0);
      p.up = B(solid(0, 1, 0));
      return S(nm);
    case 107: case 183: case 184: case 185: case 186: case 187: {
      const f = hFacing(meta);
      p.facing = f;
      p.open = B((meta & 4) !== 0);
      const zAxis = f === 'north' || f === 'south';
      p.in_wall = B(zAxis ? bk(-1, 0, 0) === 139 || bk(1, 0, 0) === 139 : bk(0, 0, -1) === 139 || bk(0, 0, 1) === 139);
      return S(nm);
    }
    case 117:
      p.has_bottle_0 = B((meta & 1) !== 0);
      p.has_bottle_1 = B((meta & 2) !== 0);
      p.has_bottle_2 = B((meta & 4) !== 0);
      return S(nm);
    case 118:
      p.level = String(meta & 3);
      return S(nm);
    case 120:
      p.eye = B((meta & 4) !== 0);
      p.facing = hFacing(meta & 3);
      return S(nm);
    case 127:
      p.facing = hFacing(meta & 3);
      p.age = String(Math.min(2, (meta & 15) >> 2));
      return S(nm);
    case 131:
      p.facing = hFacing(meta & 3);
      p.attached = B((meta & 4) !== 0);
      p.powered = B((meta & 8) !== 0);
      return S(nm);
    case 139:
      wall();
      return S((meta === 1 ? 'mossy_cobblestone' : 'cobblestone') + '_wall');
    case 145:
      p.facing = hFacing(meta & 3);
      p.damage = ['intact', 'slightly_damaged', 'very_damaged'][(meta >> 2) % 3];
      return S(nm);
    case 154:
      p.facing = facing(meta & 7) === 'up' ? 'down' : facing(meta & 7);
      p.enabled = B((meta & 8) === 0);
      return S(nm);
    case 155:
      if (meta === 1) return S('chiseled_quartz_block');
      if (meta >= 2 && meta <= 4) return S('quartz_column', `axis=${['y', 'x', 'z'][meta - 2]}`);
      return S('quartz_block');
    case 168: return S(['prismarine', 'prismarine_bricks', 'dark_prismarine'][meta % 3]);
    case 170: case 202: case 216:
      p.axis = (meta & 12) === 4 ? 'x' : (meta & 12) === 8 ? 'z' : 'y';
      return S(nm);
    case 175: {
      const upper = (meta & 8) !== 0;
      const variant = upper ? (bk(0, -1, 0) === 175 ? mt(0, -1, 0) & 7 : 0) : meta & 7;
      p.half = upper ? 'upper' : 'lower';
      return S(DOUBLE_PLANTS[variant % 6]);
    }
    case 198:
      p.facing = facing(meta);
      return S(nm);
    case 200:
      p.age = String(Math.min(5, meta));
      return S(nm);
    case 218:
      p.facing = facing(meta & 7);
      p.powered = B((meta & 8) !== 0);
      return S(nm);
    case 255:
      p.mode = ['save', 'load', 'corner', 'data'][meta & 3];
      return S(nm);
    case 137: case 210: case 211:
      p.facing = facing(meta & 7);
      p.conditional = B((meta & 8) !== 0);
      return S(nm);
  }
  if (id >= 219 && id <= 234) {
    p.facing = facing(meta & 7);
    p.color = COLORS[id - 219];
    return builtin();
  }
  if (id >= 235 && id <= 250) {
    p.facing = hFacing(meta);
    return S(COLORS[id - 235] + '_glazed_terracotta');
  }
  if (isStairs(id)) {
    p.facing = stairFacing(meta);
    p.half = stairHalf(meta);
    p.shape = nb ? stairShape(nb, meta) : 'straight';
    return S(nm);
  }
  return S(nm);

  function fence(): void {
    for (const f of HORIZ) {
      const [dx, dz] = step(f);
      const b = bk(dx, 0, dz);
      const same = id === 113 ? b === 113 : isWoodFence(b);
      p[f] = B(same || isFenceGate(b) || solid(dx, 0, dz));
    }
  }
  function pane(): void {
    for (const f of HORIZ) {
      const [dx, dz] = step(f);
      p[f] = B(isPane(bk(dx, 0, dz)) || solid(dx, 0, dz));
    }
  }
  function wall(): void {
    const wl = (dx: number, dz: number) => bk(dx, 0, dz) === 139 || isFenceGate(bk(dx, 0, dz)) || solid(dx, 0, dz);
    const n = wl(0, -1), e = wl(1, 0), s = wl(0, 1), w = wl(-1, 0);
    const straight = (n && !e && s && !w) || (!n && e && !s && w);
    p.north = B(n);
    p.east = B(e);
    p.south = B(s);
    p.west = B(w);
    p.up = B(!straight || bk(0, 1, 0) !== 0);
  }
  function door(): void {
    const upper = (meta & 8) !== 0;
    const lower = upper ? (bk(0, -1, 0) === id ? mt(0, -1, 0) : 0) : meta;
    const top = upper ? meta : bk(0, 1, 0) === id ? mt(0, 1, 0) : 8;
    p.half = upper ? 'upper' : 'lower';
    p.facing = rotateYCCW(hFacing(lower & 3));
    p.open = B((lower & 4) !== 0);
    p.hinge = (top & 1) !== 0 ? 'right' : 'left';
    p.powered = B((top & 2) !== 0);
  }
  function wire(f: string): string {
    const [dx, dz] = step(f);
    if (wireAttracts(bk(dx, 0, dz), mt(dx, 0, dz), f)) return 'side';
    const sideSolid = solid(dx, 0, dz);
    if (!sideSolid && bk(dx, -1, dz) === 55) return 'side';
    if (sideSolid && !solid(0, 1, 0) && bk(dx, 1, dz) === 55) return 'up';
    return 'none';
  }
}

function wireAttracts(id: number, meta: number, dir: string): boolean {
  if ([55, 75, 76, 69, 70, 72, 77, 143, 147, 148, 151, 178, 152, 131, 149, 150].includes(id)) return true;
  if (id === 93 || id === 94) {
    const f = hFacing(meta);
    return f === dir || opposite(f) === dir;
  }
  if (id === 218) return facing(meta & 7) === dir;
  return false;
}

/** BlockStairs.getStairsShape: outer corner from the stairs in front (its facing across ours, not matched on the side it
 * turns to), inner corner from the stairs behind. */
export function stairShape(nb: Nb, meta: number): string {
  const f = stairFacing(meta), half = stairHalf(meta);
  const isDifferent = (dir: string) => {
    const [dx, dz] = step(dir);
    const id = nb.id(dx, 0, dz), m = nb.meta(dx, 0, dz);
    return !isStairs(id) || stairFacing(m) !== f || stairHalf(m) !== half;
  };
  const xAxis = (d: string) => d === 'east' || d === 'west';
  const [fx, fz] = step(f);
  const front = nb.id(fx, 0, fz), fm = nb.meta(fx, 0, fz);
  if (isStairs(front) && stairHalf(fm) === half) {
    const f1 = stairFacing(fm);
    if (xAxis(f1) !== xAxis(f) && isDifferent(opposite(f1))) return f1 === rotateYCCW(f) ? 'outer_left' : 'outer_right';
  }
  const back = nb.id(-fx, 0, -fz), bm = nb.meta(-fx, 0, -fz);
  if (isStairs(back) && stairHalf(bm) === half) {
    const f2 = stairFacing(bm);
    if (xAxis(f2) !== xAxis(f) && isDifferent(f2)) return f2 === rotateYCCW(f) ? 'inner_left' : 'inner_right';
  }
  return 'straight';
}

export function cacheKey(s: StateRef): string {
  const keys = Object.keys(s.props).sort();
  return s.file + '[' + keys.map((k) => k + '=' + s.props[k]).join(',') + (s.key ? ';' + s.key : '') + ']';
}
