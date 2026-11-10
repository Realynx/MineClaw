/**
 * What differs between the two game versions outside a single part's own rules: which blocks conduct, which blocks dust
 * links to, what holds a part up, how many scheduled ticks one game tick runs, and when a redstone torch burns out.
 *
 * Block states are the grid's (`id << 4 | meta`, the ground below zero, `INERT` set on a part that is left out), so the
 * ground holds everything and a left-out part holds nothing.
 */
import type { RedstoneVersion } from './api.js';
import { FENCES_B181, LAST_ID_B181, NOT_CUBES_B181, PARTS_B181, POWER_SOURCES_B181 } from './data/conductors.b181.js';
import {
  FENCES_1122,
  NEVER_HOLD_1122,
  NOT_FULL_CUBES_1122,
  NO_BODY_1122,
  PARTS_1122,
  POWER_SOURCES_1122,
  SLABS_1122,
  STAIRS_1122,
  TRANSLUCENT_1122,
} from './data/conductors.1122.js';
import { DOWN, EAST, NORTH, SOUTH, UP, WEST, type Dir } from './orientation.js';

export interface Profile {
  readonly version: RedstoneVersion;
  /** 1.12.2. */
  readonly modern: boolean;
  /** The highest block id the version has. */
  readonly lastId: number;
  /** 1 for a block id that is a full solid cube: it passes strong power on and stops dust stepping past it. */
  readonly conducts: Uint8Array;
  /** 1 for a block id dust links to on any side (the game's power-source blocks). */
  readonly source: Uint8Array;
  /** 1 for a block id with redstone behaviour of its own. */
  readonly part: Uint8Array;
  /** The most scheduled ticks one game tick runs; the rest wait for the next. */
  readonly ticksPerGameTick: number;
  /** A torch burns out on its `limit`th turn-off within `window` game ticks; 1.12.2 tries again `retry` ticks later. */
  readonly burnout: { readonly limit: number; readonly window: number; readonly retry: number };

  /** Whether the block can have a repeater or comparator on it. */
  holdsDiode(state: number): boolean;
  /** Whether the block can have dust on it. */
  holdsDust(state: number): boolean;
  /** Whether the block can have a pressure plate on it. */
  holdsPlate(state: number): boolean;
  /** Whether the block can have a torch standing on it. */
  holdsTorch(state: number): boolean;
  /** Whether a part can hang on the block's `face` (the direction from the block to the part). */
  holdsOnFace(state: number, face: Dir): boolean;
}

function flags(ids: readonly number[]): Uint8Array {
  const out = new Uint8Array(256);
  for (const id of ids) out[id] = 1;
  return out;
}

function beta(): Profile {
  const conducts = new Uint8Array(256);
  for (let id = 1; id <= LAST_ID_B181; id++) conducts[id] = 1;
  for (const id of NOT_CUBES_B181) conducts[id] = 0;
  const fence = flags(FENCES_B181);
  const cube = (state: number): boolean => state < 0 || conducts[state >> 4] === 1;
  const cubeOrFence = (state: number): boolean => cube(state) || fence[state >> 4] === 1;
  return {
    version: 'b1.8.1',
    modern: false,
    lastId: LAST_ID_B181,
    conducts,
    source: flags(POWER_SOURCES_B181),
    part: flags(PARTS_B181),
    ticksPerGameTick: 1000,
    burnout: { limit: 8, window: 100, retry: 0 },
    holdsDiode: cube,
    holdsDust: cube,
    holdsPlate: cubeOrFence,
    holdsTorch: cubeOrFence,
    // Walls and the floor only: this version has no part that hangs under a block.
    holdsOnFace: (state, face) => face !== DOWN && cube(state),
  };
}

/** The way 1.12.2 stairs rise, by the low two bits: the tall side is a full face. */
const STAIRS_RISE: readonly Dir[] = [EAST, WEST, SOUTH, NORTH];

function modern(): Profile {
  const notFull = flags(NOT_FULL_CUBES_1122);
  const noBody = flags(NO_BODY_1122);
  const translucent = flags(TRANSLUCENT_1122);
  const source = flags(POWER_SOURCES_1122);
  const fence = flags(FENCES_1122);
  const slab = flags(SLABS_1122);
  const stairs = flags(STAIRS_1122);
  const neverHolds = flags(NEVER_HOLD_1122);

  const full = new Uint8Array(256);
  const conducts = new Uint8Array(256);
  for (let id = 1; id < 256; id++) {
    full[id] = notFull[id] === 0 && noBody[id] === 0 ? 1 : 0;
    conducts[id] = full[id] === 1 && translucent[id] === 0 && source[id] === 0 ? 1 : 0;
  }

  /** The game's "top solid": what dust, repeaters and plates look for under them. */
  const topSolid = (state: number): boolean => {
    if (state < 0) return true;
    const id = state >> 4;
    const meta = state & 15;
    if (full[id] === 1) return translucent[id] === 0;
    if (slab[id] === 1) return (meta & 8) !== 0;
    if (stairs[id] === 1) return (meta & 4) !== 0;
    if (id === 78) return (meta & 7) === 7;
    if (id === 154) return true;
    if (id === 29 || id === 33) return (meta & 8) === 0 || (meta & 7) === DOWN;
    return id === 34 && (meta & 7) === UP;
  };

  /** Whether the block's `face` is a whole flat face. */
  const faceSolid = (state: number, face: Dir): boolean => {
    const id = state >> 4;
    const meta = state & 15;
    if (full[id] === 1) return true;
    if (slab[id] === 1) return face === ((meta & 8) !== 0 ? UP : DOWN);
    if (stairs[id] === 1) {
      if (face === UP || face === DOWN) return face === ((meta & 4) !== 0 ? UP : DOWN);
      return face === STAIRS_RISE[meta & 3];
    }
    return false;
  };

  return {
    version: '1.12.2',
    modern: true,
    lastId: 255,
    conducts,
    source,
    part: flags(PARTS_1122),
    ticksPerGameTick: 65536,
    burnout: { limit: 8, window: 60, retry: 160 },
    holdsDiode: topSolid,
    holdsDust: (state) => topSolid(state) || state >> 4 === 89,
    holdsPlate: (state) => topSolid(state) || fence[state >> 4] === 1,
    holdsTorch: (state) => {
      const id = state >> 4;
      // A jack o'lantern and an end gateway hold no torch; a fence, a wall and glass do.
      if (id === 91 || id === 209) return false;
      return topSolid(state) || fence[id] === 1 || id === 20 || id === 95 || id === 139;
    },
    holdsOnFace: (state, face) => {
      if (state < 0) return true;
      const id = state >> 4;
      if (neverHolds[id] === 1) return false;
      // A hopper's rim holds a lever or a button; a piston holds one on top only.
      if (id === 154) return face === UP;
      if (id === 29 || id === 33 || id === 34) return face === UP && topSolid(state);
      return faceSolid(state, face);
    },
  };
}

const BETA = beta();
const MODERN = modern();

export function profileOf(v: RedstoneVersion): Profile {
  return v === '1.12.2' ? MODERN : BETA;
}
