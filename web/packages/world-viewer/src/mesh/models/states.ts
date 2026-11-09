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
  return isStairs(id) || isFence(id) || isFenceGate(id) || isPane(id) || isDoor(id) || [139, 106, 175, 104, 105, 55, 54, 130, 146, 2, 3, 110, 51, 26, 144, 140, 176, 177].includes(id);
}

const SKULLS = ['skeleton', 'wither', 'zombie', 'player', 'creeper', 'dragon'];

/** Skull names the wire may send for ours. */
const SKULL_ALIASES = new Map([
  ['wither_skeleton', 'wither'],
  ['char', 'player'],
  ['steve', 'player'],
  ['human', 'player'],
  ['ender_dragon', 'dragon'],
  ['enderdragon', 'dragon'],
]);

/** TileEntitySkull type (0 skeleton … 5 dragon, or a name) → our name; skeleton when unknown. */
export function skullType(v: unknown): string {
  if (typeof v === 'number') return SKULLS[v] ?? 'skeleton';
  if (typeof v !== 'string') return 'skeleton';
  const n = v.replace(/^minecraft:/, '').toLowerCase().replace(/_(skull|head)$/, '');
  const alias = SKULL_ALIASES.get(n);
  if (alias) return alias;
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
  if (typeof item === 'string') return potFromName(item);
  const named = typeof item === 'object' && !Array.isArray(item) ? (item as Record<string, unknown>).id : undefined;
  if (typeof named === 'string') return potContents(named);
  const [id, meta] = potItem(item);
  return potFromId(id, meta);
}

/** A pot item name ("minecraft:red_flower", "poppy") or a numeric id string. */
function potFromName(item: string): string {
  const n = item.replace(/^minecraft:/, '').toLowerCase();
  if (/^\d+$/.test(n)) return potFromId(+n, 0);
  const c = POT_NAMES[n] ?? n;
  return POT_CONTENTS.has(c) ? c : 'empty';
}

/** [id, meta] of an [id, meta] pair, a bare id or an {id|item, meta|damage} object; [0, 0] for anything else. */
function potItem(item: unknown): [number, number] {
  if (Array.isArray(item)) return [+item[0] || 0, +item[1] || 0];
  if (typeof item === 'number') return [item, 0];
  if (typeof item !== 'object') return [0, 0];
  const o = item as Record<string, unknown>;
  return [Number(o.id ?? o.item) || 0, Number(o.meta ?? o.damage) || 0];
}

/** Pot plants whose look doesn't depend on the item's metadata. */
const POT_BY_ID = new Map([
  [37, 'dandelion'],
  [39, 'mushroom_brown'],
  [40, 'mushroom_red'],
  [32, 'dead_bush'],
  [81, 'cactus'],
]);

function potFromId(id: number, meta: number): string {
  if (id === 38) return POT_FLOWERS[meta] ?? 'rose';
  if (id === 6) return WOOD[(meta & 7) % 6] + '_sapling';
  if (id === 31) return meta === 2 ? 'fern' : 'empty';
  return POT_BY_ID.get(id) ?? 'empty';
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

/** One resolveState call: the block, its neighbourhood (null = none: neighbour properties take their defaults) and the
 * properties being filled. */
class Resolve {
  readonly p: Record<string, string> = {};
  readonly nm: string;

  constructor(
    readonly id: number,
    readonly meta: number,
    readonly nb: Nb | null,
    readonly solidFull: (id: number, meta: number) => boolean,
  ) {
    this.nm = name(id);
  }

  bk(dx: number, dy: number, dz: number): number {
    return this.nb ? this.nb.id(dx, dy, dz) : 0;
  }

  mt(dx: number, dy: number, dz: number): number {
    return this.nb ? this.nb.meta(dx, dy, dz) : 0;
  }

  /** A solid full cube that fences, panes, walls and wire attach to (never an attach exception). */
  solid(dx: number, dy: number, dz: number): boolean {
    const b = this.bk(dx, dy, dz);
    return b !== 0 && !attachException(b) && this.solidFull(b, this.mt(dx, dy, dz));
  }

  snowy(): string {
    return B(this.bk(0, 1, 0) === 78 || this.bk(0, 1, 0) === 80);
  }

  be(): BlockEntityDto | undefined {
    return this.nb?.be?.();
  }

  S(file: string, key: string | null = null): StateRef {
    return { file, props: this.p, key, builtin: false };
  }

  /** The file named after the block. */
  named(): StateRef {
    return this.S(this.nm);
  }

  builtin(): StateRef {
    return { file: this.nm, props: this.p, key: null, builtin: true };
  }
}

type Resolver = (r: Resolve) => StateRef;

/** Resolver per block id (undefined = the block's own file, no properties). */
const RESOLVERS: Array<Resolver | undefined> = [];
const on = (ids: readonly number[], fn: Resolver) => {
  for (const id of ids) RESOLVERS[id] = fn;
};
const span = (from: number, to: number) => Array.from({ length: to - from + 1 }, (_, i) => from + i);
/** A resolver that only fills properties and names the block's own file. */
const props =
  (fill: (r: Resolve) => void): Resolver =>
  (r) => {
    fill(r);
    return r.named();
  };
const halfOf = (meta: number) => ((meta & 8) !== 0 ? 'top' : 'bottom');
const flag = (meta: number, bit: number) => B((meta & bit) !== 0);

// Plain variants: the file named by the metadata.
on([1], (r) => r.S(['stone', 'granite', 'smooth_granite', 'diorite', 'smooth_diorite', 'andesite', 'smooth_andesite'][r.meta % 7]));
on([5], (r) => r.S(WOOD[r.meta % 6] + '_planks'));
on([12], (r) => r.S((r.meta & 1) === 1 ? 'red_sand' : 'sand'));
on([18], (r) => r.S(WOOD[r.meta & 3] + '_leaves'));
on([161], (r) => r.S(WOOD[4 + (r.meta & 1)] + '_leaves'));
on([24], (r) => r.S(['sandstone', 'chiseled_sandstone', 'smooth_sandstone'][r.meta % 3]));
on([179], (r) => r.S(['red_sandstone', 'chiseled_red_sandstone', 'smooth_red_sandstone'][r.meta % 3]));
on([31], (r) => r.S(['dead_bush', 'tall_grass', 'fern'][r.meta % 3]));
on([32], (r) => r.S('dead_bush'));
on([37], (r) => r.S('dandelion'));
on([38], (r) => r.S(FLOWERS[r.meta % 9]));
on([80], (r) => r.S('snow'));
on([97], (r) => r.S(['stone', 'cobblestone', 'stone_brick', 'mossy_brick', 'cracked_brick', 'chiseled_brick'][r.meta % 6] + '_monster_egg'));
on([98], (r) => r.S(['stonebrick', 'mossy_stonebrick', 'cracked_stonebrick', 'chiseled_stonebrick'][r.meta & 3]));
on([168], (r) => r.S(['prismarine', 'prismarine_bricks', 'dark_prismarine'][r.meta % 3]));
on([155], (r) => {
  if (r.meta === 1) return r.S('chiseled_quartz_block');
  if (r.meta >= 2 && r.meta <= 4) return r.S('quartz_column', `axis=${['y', 'x', 'z'][r.meta - 2]}`);
  return r.S('quartz_block');
});

// Dyed blocks.
for (const [id, suffix] of [[35, '_wool'], [171, '_carpet'], [159, '_stained_hardened_clay'], [95, '_stained_glass'], [251, '_concrete'], [252, '_concrete_powder']] as const)
  on([id], (r) => r.S(COLORS[r.meta & 15] + suffix));
on(span(235, 250), (r) => {
  r.p.facing = hFacing(r.meta);
  return r.S(COLORS[r.id - 235] + '_glazed_terracotta');
});
on(span(219, 234), (r) => {
  r.p.facing = facing(r.meta & 7);
  r.p.color = COLORS[r.id - 219];
  return r.builtin();
});

// Slabs: seamless doubles take the "all" variant.
on([43], (r) => r.S(STONE_SLAB[r.meta & 7] + '_double_slab', (r.meta & 8) !== 0 ? 'all' : 'normal'));
on([181], (r) => r.S('red_sandstone_double_slab', (r.meta & 8) !== 0 ? 'all' : 'normal'));
on([125], (r) => r.S(WOOD[(r.meta & 7) % 6] + '_double_slab'));
on([204], (r) => r.S('purpur_double_slab'));
on([44], (r) => {
  r.p.half = halfOf(r.meta);
  return r.S(STONE_SLAB[r.meta & 7] + '_slab');
});
on([126], (r) => {
  r.p.half = halfOf(r.meta);
  return r.S(WOOD[(r.meta & 7) % 6] + '_slab');
});
on([182, 205], (r) => {
  r.p.half = halfOf(r.meta);
  return r.S(r.id === 182 ? 'red_sandstone_slab' : 'purpur_slab');
});

// Terrain, wood and plants.
on([2, 110], props((r) => (r.p.snowy = r.snowy())));
on([3], (r) => {
  if (r.meta % 3 !== 2) return r.S(r.meta % 3 === 1 ? 'coarse_dirt' : 'dirt');
  r.p.snowy = r.snowy();
  return r.S('podzol');
});
on([6], (r) => {
  r.p.stage = String((r.meta >> 3) & 1);
  return r.S(WOOD[(r.meta & 7) % 6] + '_sapling');
});
on([17, 162], (r) => {
  r.p.axis = ['y', 'x', 'z', 'none'][(r.meta & 12) >> 2];
  return r.S(WOOD[r.id === 162 ? Math.min(5, (r.meta & 3) + 4) : r.meta & 3] + '_log');
});
on([170, 202, 216], props((r) => (r.p.axis = (r.meta & 12) === 4 ? 'x' : (r.meta & 12) === 8 ? 'z' : 'y')));
on([19], props((r) => (r.p.wet = B((r.meta & 1) === 1))));
on([59, 141, 142], props((r) => (r.p.age = String(r.meta & 7))));
on([207, 115, 212], props((r) => (r.p.age = String(r.meta & 3))));
on([200], props((r) => (r.p.age = String(Math.min(5, r.meta)))));
on([60], props((r) => (r.p.moisture = String(r.meta & 7))));
on([78], props((r) => (r.p.layers = String((r.meta & 7) + 1))));
on([92], props((r) => (r.p.bites = String(Math.min(6, r.meta)))));
on([118], props((r) => (r.p.level = String(r.meta & 3))));
on([99, 100], props((r) => (r.p.variant = MUSHROOM[r.meta & 15])));
on([127], props((r) => {
  r.p.facing = hFacing(r.meta & 3);
  r.p.age = String(Math.min(2, (r.meta & 15) >> 2));
}));
on([104, 105], stem);
on([106], props(vine));
on([175], doublePlant);
on([51], fire);

// Facings and switches.
on([23, 158, 198], props((r) => (r.p.facing = r.id === 198 ? facing(r.meta) : facing(r.meta & 7))));
on([29, 33], props((r) => {
  r.p.facing = facing(r.meta & 7);
  r.p.extended = flag(r.meta, 8);
}));
on([34], props((r) => {
  r.p.facing = facing(r.meta & 7);
  r.p.type = (r.meta & 8) !== 0 ? 'sticky' : 'normal';
  r.p.short = 'false';
}));
on([154], props((r) => {
  r.p.facing = facing(r.meta & 7) === 'up' ? 'down' : facing(r.meta & 7);
  r.p.enabled = B((r.meta & 8) === 0);
}));
on([218], props((r) => {
  r.p.facing = facing(r.meta & 7);
  r.p.powered = flag(r.meta, 8);
}));
on([137, 210, 211], props((r) => {
  r.p.facing = facing(r.meta & 7);
  r.p.conditional = flag(r.meta, 8);
}));
on([50, 75, 76], props((r) => (r.p.facing = ({ 1: 'east', 2: 'west', 3: 'south', 4: 'north' } as Record<number, string>)[r.meta] ?? 'up')));
on([61, 62, 65], props((r) => (r.p.facing = horizontal(r.meta))));
on([86, 91], props((r) => (r.p.facing = hFacing(r.meta))));
on([90], props((r) => (r.p.axis = (r.meta & 3) === 2 ? 'z' : 'x')));
on([120], props((r) => {
  r.p.eye = flag(r.meta, 4);
  r.p.facing = hFacing(r.meta & 3);
}));
on([145], props((r) => {
  r.p.facing = hFacing(r.meta & 3);
  r.p.damage = ['intact', 'slightly_damaged', 'very_damaged'][(r.meta >> 2) % 3];
}));
on([96, 167], props((r) => {
  r.p.facing = ['north', 'south', 'west', 'east'][r.meta & 3];
  r.p.open = flag(r.meta, 4);
  r.p.half = halfOf(r.meta);
}));
on([117], props((r) => {
  r.p.has_bottle_0 = flag(r.meta, 1);
  r.p.has_bottle_1 = flag(r.meta, 2);
  r.p.has_bottle_2 = flag(r.meta, 4);
}));
on([255], props((r) => (r.p.mode = ['save', 'load', 'corner', 'data'][r.meta & 3])));

// Redstone.
on([27, 28, 157], props((r) => {
  r.p.shape = RAIL[(r.meta & 7) % 6];
  r.p.powered = flag(r.meta, 8);
}));
on([66], props((r) => (r.p.shape = RAIL[r.meta % 10])));
on([69], props((r) => {
  r.p.facing = LEVER[r.meta & 7];
  r.p.powered = flag(r.meta, 8);
}));
on([77, 143], props((r) => {
  r.p.facing = BUTTON[r.meta & 7];
  r.p.powered = flag(r.meta, 8);
}));
on([70, 72], props((r) => (r.p.powered = B(r.meta === 1))));
on([147, 148], props((r) => (r.p.power = String(r.meta & 15))));
on([93, 94], props((r) => {
  r.p.facing = hFacing(r.meta);
  r.p.delay = String(1 + ((r.meta >> 2) & 3));
  r.p.locked = 'false';
}));
on([149, 150], props((r) => {
  r.p.facing = hFacing(r.meta);
  r.p.mode = (r.meta & 4) !== 0 ? 'subtract' : 'compare';
  r.p.powered = flag(r.meta, 8);
}));
on([131], props((r) => {
  r.p.facing = hFacing(r.meta & 3);
  r.p.attached = flag(r.meta, 4);
  r.p.powered = flag(r.meta, 8);
}));
on([55], props((r) => {
  for (const f of HORIZ) r.p[f] = wire(r, f);
  r.p.power = String(r.meta & 15); // the model ignores it (the mapper drops it); the tint reads it
}));

// Connections to the neighbours.
on([85, 113, 188, 189, 190, 191, 192], props(fence));
on([101, 102], props(pane));
on([160], (r) => {
  pane(r);
  return r.S(COLORS[r.meta & 15] + '_stained_glass_pane');
});
on([139], (r) => {
  wall(r);
  return r.S((r.meta === 1 ? 'mossy_cobblestone' : 'cobblestone') + '_wall');
});
on([107, 183, 184, 185, 186, 187], props(fenceGate));
on([64, 71, 193, 194, 195, 196, 197], props(door));
on([53, 67, 108, 109, 114, 128, 134, 135, 136, 156, 163, 164, 180, 203], props((r) => {
  r.p.facing = stairFacing(r.meta);
  r.p.half = stairHalf(r.meta);
  r.p.shape = r.nb ? stairShape(r.nb, r.meta) : 'straight';
}));

// Built-in geometry (our own drawing), some with block-entity properties.
on([8, 9, 10, 11, 119, 209, 166, 36, 217], (r) => r.builtin());
on([54, 130, 146], chest);
on([26], (r) => {
  r.p.facing = hFacing(r.meta & 3);
  r.p.part = (r.meta & 8) !== 0 ? 'head' : 'foot';
  const c = dyeIndex(r.be()?.color);
  r.p.color = COLORS[c >= 0 ? c : 14];
  return r.builtin();
});
on([63, 176], (r) => {
  r.p.rotation = String(r.meta & 15);
  if (r.id === 176) bannerLayer(r);
  return r.builtin();
});
on([68, 177], (r) => {
  r.p.facing = horizontal(r.meta);
  if (r.id === 177) bannerLayer(r);
  return r.builtin();
});
on([144], (r) => {
  r.p.facing = facing(r.meta & 7);
  const be = r.be();
  r.p.type = skullType(be?.type);
  if (r.p.facing === 'up') r.p.rotation = String((be?.rotation ?? 0) & 15);
  return r.builtin();
});
on([140], props((r) => {
  r.p.contents = potContents(r.be()?.item);
  r.p.legacy_data = '0';
}));

/**
 * Resolves (id, meta) to a blockstate reference. `nb` null = no world (static classification: neighbour properties take
 * their defaults). `solidFull(id, meta)` = a solid full cube neighbour (fence/pane/wall attachments, wire climbs).
 */
export function resolveState(id: number, meta: number, nb: Nb | null, solidFull: (id: number, meta: number) => boolean): StateRef {
  const r = new Resolve(id, meta, nb, solidFull);
  const fn = RESOLVERS[id];
  return fn ? fn(r) : r.named();
}

/** Standing banners and wall banners with a block entity are drawn by the block-entity layer (patterns, sway). */
function bannerLayer(r: Resolve): void {
  if (r.be()?.kind === 'banner') r.p.layer = 'true';
}

/** BlockFire.getActualState: flip/alt by position, sides toward flammable neighbours unless it sits on solid ground. */
function fire(r: Resolve): StateRef {
  const { p } = r;
  fireParity(p, r.nb);
  const below = r.bk(0, -1, 0);
  const ground = below !== 0 && r.solidFull(below, r.mt(0, -1, 0));
  p.upper = '0';
  for (const d of HORIZ) {
    const [dx, dz] = step(d);
    p[d] = B(!ground && FLAMMABLE.has(r.bk(dx, 0, dz)));
  }
  p.up = B(!ground && FLAMMABLE.has(r.bk(0, 1, 0)));
  return r.named();
}

/** The fire's position-hashed flip / alt textures. */
function fireParity(p: Record<string, string>, nb: Nb | null): void {
  const x = nb?.wx ?? 0, y = nb?.wy ?? 0, z = nb?.wz ?? 0;
  p.flip = B(((Math.floor(x) + Math.floor(y) + Math.floor(z)) & 1) === 1);
  p.alt = B((((x >> 1) + (y >> 1) + (z >> 1)) & 1) === 1);
}

function chest(r: Resolve): StateRef {
  const { p, nb, id } = r;
  p.kind = id === 130 ? 'ender' : id === 146 ? 'trapped' : 'normal';
  const mate = nb && id !== 130 ? chestPartner(nb, id) : null;
  if (nb && mate) {
    p.facing = facing(pairFacing(nb, mate));
    p.pair = `${mate[0]},${mate[1]}`;
  } else p.facing = horizontal(r.meta);
  if (['chest', 'trapped_chest', 'ender_chest'].includes(r.be()?.kind ?? '')) p.liveLid = 'true';
  return r.builtin();
}

/** The double plant's upper half takes its variant from the lower half. */
function doublePlant(r: Resolve): StateRef {
  const upper = (r.meta & 8) !== 0;
  const variant = upper ? (r.bk(0, -1, 0) === 175 ? r.mt(0, -1, 0) & 7 : 0) : r.meta & 7;
  r.p.half = upper ? 'upper' : 'lower';
  return r.S(DOUBLE_PLANTS[variant % 6]);
}

/** Stems: ripe ones bend toward their fruit (and lose the age). */
function stem(r: Resolve): StateRef {
  const { p, meta } = r;
  p.age = String(meta & 7);
  const f = (meta & 7) === 7 ? stemFruitSide(r, r.id === 104 ? 86 : 103) : 'up';
  p.facing = f;
  if (f !== 'up') delete p.age;
  return r.named();
}

function stemFruitSide(r: Resolve, fruit: number): string {
  for (const d of HORIZ) {
    const [dx, dz] = step(d);
    if (r.bk(dx, 0, dz) === fruit) return d;
  }
  return 'up';
}

function vine(r: Resolve): void {
  const { p, meta } = r;
  p.south = flag(meta, 1);
  p.west = flag(meta, 2);
  p.north = flag(meta, 4);
  p.east = flag(meta, 8);
  p.up = B(r.solid(0, 1, 0));
}

function fence(r: Resolve): void {
  for (const f of HORIZ) {
    const [dx, dz] = step(f);
    const b = r.bk(dx, 0, dz);
    const same = r.id === 113 ? b === 113 : isWoodFence(b);
    r.p[f] = B(same || isFenceGate(b) || r.solid(dx, 0, dz));
  }
}

function pane(r: Resolve): void {
  for (const f of HORIZ) {
    const [dx, dz] = step(f);
    r.p[f] = B(isPane(r.bk(dx, 0, dz)) || r.solid(dx, 0, dz));
  }
}

function wallLinks(r: Resolve, dx: number, dz: number): boolean {
  return r.bk(dx, 0, dz) === 139 || isFenceGate(r.bk(dx, 0, dz)) || r.solid(dx, 0, dz);
}

/** Wall sides, and the post unless it runs straight with nothing above. */
function wall(r: Resolve): void {
  const { p } = r;
  const n = wallLinks(r, 0, -1), e = wallLinks(r, 1, 0), s = wallLinks(r, 0, 1), w = wallLinks(r, -1, 0);
  const straight = (n && !e && s && !w) || (!n && e && !s && w);
  p.north = B(n);
  p.east = B(e);
  p.south = B(s);
  p.west = B(w);
  p.up = B(!straight || r.bk(0, 1, 0) !== 0);
}

/** Fence gates sink into walls they stand between. */
function fenceGate(r: Resolve): void {
  const { p } = r;
  const f = hFacing(r.meta);
  p.facing = f;
  p.open = flag(r.meta, 4);
  const zAxis = f === 'north' || f === 'south';
  p.in_wall = B(zAxis ? r.bk(-1, 0, 0) === 139 || r.bk(1, 0, 0) === 139 : r.bk(0, 0, -1) === 139 || r.bk(0, 0, 1) === 139);
}

/** Door halves combined: facing/open from the lower half, hinge/power from the upper. */
function door(r: Resolve): void {
  const { p, meta, id } = r;
  const upper = (meta & 8) !== 0;
  const lower = upper ? (r.bk(0, -1, 0) === id ? r.mt(0, -1, 0) : 0) : meta;
  const top = upper ? meta : r.bk(0, 1, 0) === id ? r.mt(0, 1, 0) : 8;
  p.half = upper ? 'upper' : 'lower';
  p.facing = rotateYCCW(hFacing(lower & 3));
  p.open = flag(lower, 4);
  p.hinge = (top & 1) !== 0 ? 'right' : 'left';
  p.powered = flag(top, 2);
}

/** Redstone wire toward `f`: a side link, a climb up the solid block, or none. */
function wire(r: Resolve, f: string): string {
  const [dx, dz] = step(f);
  if (wireAttracts(r.bk(dx, 0, dz), r.mt(dx, 0, dz), f)) return 'side';
  const sideSolid = r.solid(dx, 0, dz);
  if (!sideSolid && r.bk(dx, -1, dz) === 55) return 'side';
  if (sideSolid && !r.solid(0, 1, 0) && r.bk(dx, 1, dz) === 55) return 'up';
  return 'none';
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
  const [fx, fz] = step(f);
  const f1 = cornerStairs(nb, f, half, fx, fz, true);
  if (f1) return f1 === rotateYCCW(f) ? 'outer_left' : 'outer_right';
  const f2 = cornerStairs(nb, f, half, -fx, -fz, false);
  if (f2) return f2 === rotateYCCW(f) ? 'inner_left' : 'inner_right';
  return 'straight';
}

const xAxis = (d: string) => d === 'east' || d === 'west';

/** The facing of the same-half stairs at (dx, dz) when they turn across `f` and the stairs on the side they turn to
 * (behind them when `outer`, in front otherwise) don't match ours; null when they make no corner. */
function cornerStairs(nb: Nb, f: string, half: string, dx: number, dz: number, outer: boolean): string | null {
  const id = nb.id(dx, 0, dz), m = nb.meta(dx, 0, dz);
  if (!isStairs(id) || stairHalf(m) !== half) return null;
  const f1 = stairFacing(m);
  if (xAxis(f1) === xAxis(f)) return null;
  return stairsDiffer(nb, f, half, outer ? opposite(f1) : f1) ? f1 : null;
}

/** Whatever stands toward `dir` is not stairs facing `f` on the same half. */
function stairsDiffer(nb: Nb, f: string, half: string, dir: string): boolean {
  const [dx, dz] = step(dir);
  const id = nb.id(dx, 0, dz), m = nb.meta(dx, 0, dz);
  return !isStairs(id) || stairFacing(m) !== f || stairHalf(m) !== half;
}

export function cacheKey(s: StateRef): string {
  const keys = Object.keys(s.props).sort();
  return s.file + '[' + keys.map((k) => k + '=' + s.props[k]).join(',') + (s.key ? ';' + s.key : '') + ']';
}
