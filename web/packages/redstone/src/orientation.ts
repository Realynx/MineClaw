/**
 * Directions, and how a block's meta turns and mirrors with its block: the one table rotation, mirroring and the engine
 * share. The rule per block family is the Design Studio's own (the web app's `features/studio/facing.ts`, which matches
 * the bot's `DesignFacing`), so a component the library turns reads the same as a selection the studio turns. The web
 * app holds the test that compares the two tables.
 *
 * A family's facing is in its own sense: the wall a torch hangs on, the way a lever or button sticks out, the side a
 * repeater or comparator reads from, the way stairs rise.
 */
import type { RedstoneVersion, Side } from './api.js';

/** A direction as the engine counts it: also the order both game versions number a block's six sides. */
export type Dir = number;
export const DOWN = 0;
export const UP = 1;
export const NORTH = 2;
export const SOUTH = 3;
export const WEST = 4;
export const EAST = 5;

/** The four sides in the order the engine walks them. */
export const HORIZONTAL: readonly Dir[] = [NORTH, SOUTH, WEST, EAST];

/** Both games tell a block's six neighbours about a change in this order: west, east, down, up, north, south. */
export const UPDATE_ORDER: readonly Dir[] = [WEST, EAST, DOWN, UP, NORTH, SOUTH];

export const opposite = (d: Dir): Dir => d ^ 1;

/** The two sides at a right angle to a horizontal direction. */
export const leftOf = (d: Dir): Dir => (d === NORTH ? WEST : d === WEST ? SOUTH : d === SOUTH ? EAST : NORTH);
export const rightOf = (d: Dir): Dir => opposite(leftOf(d));

/** Clockwise seen from above: north, east, south, west. */
const SIDES: readonly Side[] = ['north', 'east', 'south', 'west'];
const DIR_OF: Readonly<Record<Side, Dir>> = { north: NORTH, east: EAST, south: SOUTH, west: WEST };
const SIDE_OF: ReadonlyArray<Side | null> = [null, null, 'north', 'south', 'west', 'east'];

export const dirOfSide = (s: Side): Dir => DIR_OF[s];
/** The side a horizontal direction is; null for up and down. */
export const sideOfDir = (d: Dir): Side | null => SIDE_OF[d] ?? null;

/** A side after `quarterTurns` clockwise quarter turns seen from above (north becomes east). */
export function turnSide(s: Side, quarterTurns: number): Side {
  return SIDES[(((SIDES.indexOf(s) + quarterTurns) % 4) + 4) % 4]!;
}

/** A side mirrored across x (east and west swap) or across z (north and south swap). */
export function mirrorSide(s: Side, axis: 'x' | 'z'): Side {
  if (axis === 'x') return s === 'east' ? 'west' : s === 'west' ? 'east' : s;
  return s === 'north' ? 'south' : s === 'south' ? 'north' : s;
}

// ---------------------------------------------------------------------------------------------- families

type Family =
  | 'stairs'
  | 'log'
  | 'pillar'
  | 'quartz'
  | 'door'
  | 'bed'
  | 'torch'
  | 'front'
  | 'horizontal'
  | 'trapdoor'
  | 'button'
  | 'lever'
  | 'rail'
  | 'poweredrail'
  | 'rotation16'
  | 'vine';

const range = (a: number, b: number): number[] => Array.from({ length: b - a + 1 }, (_, i) => a + i);

/** Every block whose meta holds a direction, by family. A block that is not listed keeps its meta when it turns. */
const FAMILY_IDS: Readonly<Record<Family, readonly number[]>> = {
  stairs: [53, 67, 108, 109, 114, 128, 134, 135, 136, 156, 163, 164, 180, 203],
  log: [17, 162],
  pillar: [170, 202, 216],
  quartz: [155],
  door: [64, 71, 193, 194, 195, 196, 197],
  bed: [26],
  torch: [50, 75, 76],
  // Ladders, wall signs and banners, chests, furnaces, pistons, dispensers, droppers, observers, end rods, shulker
  // boxes, hoppers and wall skulls all keep a front in metas 2..5.
  front: [65, 68, 177, 54, 61, 62, 130, 146, 23, 29, 33, 158, 198, 218, ...range(219, 234), 154, 144],
  // Pumpkins, repeaters, comparators, cocoa, tripwire hooks, anvils, glazed terracotta and fence gates.
  horizontal: [86, 91, 93, 94, 127, 131, 145, 149, 150, ...range(235, 250), 107, 183, 184, 185, 186, 187],
  trapdoor: [96, 167],
  button: [77, 143],
  lever: [69],
  rail: [66],
  poweredrail: [27, 28, 157],
  rotation16: [63, 176],
  vine: [106],
};

const FAMILY = new Map<number, Family>();
for (const [family, ids] of Object.entries(FAMILY_IDS) as Array<[Family, readonly number[]]>)
  for (const id of ids) FAMILY.set(id, family);

/** One family's meta and facing: `get` is null for a meta without a horizontal facing (a floor torch). */
interface Codec {
  get(meta: number): Side | null;
  set(meta: number, s: Side): number;
}

/** The facing in the low two bits. */
const low2 = (sides: readonly Side[]): Codec => ({
  get: (m) => sides[m & 3]!,
  set: (m, s) => (m & ~3) | sides.indexOf(s),
});

/** The facing in metas `first`..`first + 3` of the low three bits; bit 8 is kept. */
const span = (sides: readonly Side[], first: number): Codec => ({
  get: (m) => ((m & 7) >= first && (m & 7) <= first + 3 ? sides[(m & 7) - first]! : null),
  set: (m, s) => ((m & 7) >= first && (m & 7) <= first + 3 ? (m & 8) | (sides.indexOf(s) + first) : m),
});

const SWNE: readonly Side[] = ['south', 'west', 'north', 'east'];
/** A torch's metas 1..4: the wall it hangs on. */
const TORCH_WALLS: readonly Side[] = ['west', 'east', 'north', 'south'];
/** A lever's or button's metas 1..4: the way it sticks out (its block is behind it). */
const STICKS_OUT: readonly Side[] = ['east', 'west', 'south', 'north'];

const CODEC: Readonly<Partial<Record<Family, Codec>>> = {
  stairs: low2(['east', 'west', 'south', 'north']),
  door: low2(['east', 'south', 'west', 'north']),
  bed: low2(SWNE),
  horizontal: low2(SWNE),
  trapdoor: low2(['south', 'north', 'east', 'west']),
  torch: {
    get: (m) => (m >= 1 && m <= 4 ? TORCH_WALLS[m - 1]! : null),
    set: (m, s) => (m >= 1 && m <= 4 ? TORCH_WALLS.indexOf(s) + 1 : m),
  },
  front: span(['north', 'south', 'west', 'east'], 2),
  button: span(STICKS_OUT, 1),
  lever: span(STICKS_OUT, 1),
};

/** The meta with its facing moved by `move`; unchanged for a block or a meta without one. */
function withMoved(id: number, meta: number, move: (s: Side) => Side): number {
  const family = FAMILY.get(id);
  const codec = family && CODEC[family];
  const side = codec?.get(meta);
  return codec && side ? codec.set(meta, move(side)) : meta;
}

// ---------------------------------------------------------------------------------------------- turning

/** A log's or pillar's axis after a quarter turn: x (4) and z (8) swap, upright stays. */
function swapAxis(meta: number): number {
  const axis = meta & 12;
  if (axis === 4) return (meta & ~12) | 8;
  return axis === 8 ? (meta & ~12) | 4 : meta;
}

/** A floor or ceiling lever lies along x or along z; a quarter turn swaps the two. */
const LEVER_FLAT: Readonly<Record<number, number>> = { 5: 6, 6: 5, 0: 7, 7: 0 };

/** Rail shapes one quarter turn on: straight N-S and E-W swap, slopes go E, S, W, N, curves SE, SW, NW, NE. */
const RAIL_NEXT: Readonly<Record<number, number>> = { 0: 1, 1: 0, 2: 5, 5: 3, 3: 4, 4: 2, 6: 7, 7: 8, 8: 9, 9: 6 };

function turnRail(shape: number, q: number): number {
  let s = shape;
  for (let i = 0; i < q; i++) s = RAIL_NEXT[s] ?? s;
  return s;
}

/** A vine's side bits (south, west, north, east) moved round by `q` quarter turns. */
function turnVine(meta: number, q: number): number {
  let m = meta & 15;
  for (let i = 0; i < q; i++) m = ((m << 1) | (m >> 3)) & 15;
  return m;
}

/** What an odd number of quarter turns does to a family whose meta holds an axis, not a facing; undefined: no rule. */
function axisTurn(family: Family, meta: number, v: RedstoneVersion): number | undefined {
  switch (family) {
    case 'log':
      // b1.8.1 logs are always upright: the bits are the wood.
      return v === '1.12.2' ? swapAxis(meta) : undefined;
    case 'pillar':
      return swapAxis(meta);
    case 'quartz':
      return meta === 3 ? 4 : meta === 4 ? 3 : meta;
    case 'lever':
      return (meta & 7) in LEVER_FLAT ? (meta & 8) | LEVER_FLAT[meta & 7]! : undefined;
    default:
      return undefined;
  }
}

/** Families whose meta is a rotation or a shape; undefined: the family turns by its facing. */
function shapeTurn(family: Family, meta: number, q: number): number | undefined {
  switch (family) {
    case 'rotation16':
      return (meta + q * 4) & 15;
    case 'rail':
      return turnRail(meta, q);
    case 'poweredrail':
      return (meta & 8) | turnRail(meta & 7, q);
    case 'vine':
      return turnVine(meta, q);
    default:
      return undefined;
  }
}

export function rotateMeta(id: number, meta: number, quarterTurns: number, v: RedstoneVersion): number {
  const q = ((quarterTurns % 4) + 4) % 4;
  if (q === 0) return meta;
  const family = FAMILY.get(id);
  if (family) {
    const special = (q % 2 === 1 ? axisTurn(family, meta, v) : undefined) ?? shapeTurn(family, meta, q);
    if (special !== undefined) return special;
  }
  return withMoved(id, meta, (s) => turnSide(s, q));
}

// ---------------------------------------------------------------------------------------------- mirroring

/** Curved rails mirrored across x and across z. */
const CURVE_MIRROR: Readonly<Record<number, readonly [number, number]>> = {
  6: [7, 9],
  7: [6, 8],
  8: [9, 7],
  9: [8, 6],
};

/** A rail shape mirrored: slopes east and west flip across x, north and south across z, curves swap. */
function mirrorRailShape(shape: number, axis: 'x' | 'z'): number {
  if ((shape === 2 || shape === 3) && axis === 'x') return shape ^ 1;
  if ((shape === 4 || shape === 5) && axis === 'z') return shape ^ 1;
  return CURVE_MIRROR[shape]?.[axis === 'x' ? 0 : 1] ?? shape;
}

export function mirrorMeta(id: number, meta: number, axis: 'x' | 'z', _v: RedstoneVersion): number {
  switch (FAMILY.get(id)) {
    case 'rotation16':
      return axis === 'x' ? (16 - meta) & 15 : (8 - meta) & 15;
    case 'rail':
      return mirrorRailShape(meta & 15, axis);
    case 'poweredrail':
      return (meta & ~7) | mirrorRailShape(meta & 7, axis);
    case 'vine': {
      // Across x the west (2) and east (8) bits swap; across z south (1) and north (4).
      const m = meta & 15;
      return axis === 'x' ? (m & 5) | ((m & 2) << 2) | ((m & 8) >> 2) : (m & 10) | ((m & 1) << 2) | ((m & 4) >> 2);
    }
    default:
      return withMoved(id, meta, (s) => mirrorSide(s, axis));
  }
}

// ---------------------------------------------------------------------------------------------- what the engine reads

/**
 * Where a redstone torch's block is, as the direction from the torch to it: a wall for metas 1..4, the floor for 5.
 * -1 for a meta the game does not have.
 */
export function torchSupport(meta: number): Dir {
  if (meta === 5) return DOWN;
  const wall = CODEC.torch!.get(meta);
  return wall ? dirOfSide(wall) : -1;
}

/** The side a repeater or comparator reads from; it puts out on the opposite side. */
export function diodeRear(meta: number): Dir {
  return dirOfSide(SWNE[meta & 3]!);
}

/**
 * Where a lever's block is, as the direction from the lever to it: behind it for the wall metas 1..4, below for the
 * floor metas 5 and 6, above for the ceiling metas 0 and 7 (1.12.2 only).
 */
export function leverSupport(meta: number): Dir {
  const m = meta & 7;
  if (m === 5 || m === 6) return DOWN;
  if (m === 0 || m === 7) return UP;
  return opposite(dirOfSide(STICKS_OUT[m - 1]!));
}

/**
 * Where a button's block is, as the direction from the button to it: behind it for the wall metas 1..4; in 1.12.2 also
 * below for 5 and above for 0. -1 for a meta the game does not have.
 */
export function buttonSupport(meta: number): Dir {
  const m = meta & 7;
  if (m === 5) return DOWN;
  if (m === 0) return UP;
  return m <= 4 ? opposite(dirOfSide(STICKS_OUT[m - 1]!)) : -1;
}

/** The way a piston, dispenser, dropper or observer points: the low three bits are the direction itself. */
export function facing6(meta: number): Dir {
  const m = meta & 7;
  return m <= 5 ? m : -1;
}
