/**
 * Redstone dust. A dust cell's level is the stronger of the power it is given from outside and the level of the
 * strongest dust it reads, minus one. The engine solves that for a whole connected network at once (the game steps the
 * levels down cell by cell and ends at the same values), then tells the cells around every dust that changed, in y, z,
 * x order. The game tells them in an order of its own that circuits cannot rely on either; a circuit that does depend on
 * it is not guaranteed here.
 *
 * Which dust a cell reads (both versions): the dust beside it; the dust one step up on a neighbour that is a full solid
 * block, unless a full solid block sits on the cell itself; the dust one step down past a neighbour that is not a full
 * solid block. The last two are not mirror images, so dust on a block that does not conduct (1.12.2 glowstone, a top
 * slab) reads the dust below it and is not read back.
 *
 * What dust powers (both versions): the block under it, and the block it points at. It points at a side when it links
 * on the opposite side and on neither side at a right angle; dust with no link at all points at all four sides. What it
 * links to differs by version (`links...` below). Dust never powers other dust through a block.
 */
import type { ComponentHandler, World } from './components/handler.js';
import {
  DOWN,
  HORIZONTAL,
  UP,
  UPDATE_ORDER,
  diodeRear,
  facing6,
  leftOf,
  opposite,
  rightOf,
  type Dir,
} from './orientation.js';
import { isSolid, powerInto } from './power.js';

export const WIRE = 55;
const REPEATER_OFF = 93;
const REPEATER_ON = 94;
const OBSERVER = 218;

export const isWire = (state: number): boolean => state >> 4 === WIRE;

// ---------------------------------------------------------------------------------------------- links and output

/** 1.12.2: whether the dust at `at` links on side `h`. */
function linksModern(w: World, at: number, h: Dir): boolean {
  const { cells, step } = w;
  const n = at + step[h]!;
  const solid = isSolid(w, n);
  // Up a full solid block and down past an open side it links to dust only.
  if (solid && !isSolid(w, at + step[UP]!) && isWire(cells[n + step[UP]!]!)) return true;
  const state = cells[n]!;
  if (state > 0) {
    const id = state >> 4;
    // A repeater links at its front and its back; an observer at the face it puts out on.
    if (id === REPEATER_OFF || id === REPEATER_ON) {
      const rear = diodeRear(state & 15);
      if (rear === h || rear === opposite(h)) return true;
    } else if (id === OBSERVER) {
      if (facing6(state & 15) === h) return true;
    } else if (w.profile.source[id] === 1) return true;
  }
  return !solid && isWire(cells[n + step[DOWN]!]!);
}

/** b1.8.1: a block dust links to wherever it is: other dust and every power-source block (a torch lit or not). */
function sourceBeta(w: World, state: number): boolean {
  return state > 0 && w.profile.source[state >> 4] === 1;
}

/** b1.8.1: whether the dust at `at` links on side `h`. */
function linksBeta(w: World, at: number, h: Dir): boolean {
  const { cells, step } = w;
  const n = at + step[h]!;
  const state = cells[n]!;
  if (sourceBeta(w, state)) return true;
  // A repeater links only when it reads from the dust.
  const id = state >> 4;
  if ((id === REPEATER_OFF || id === REPEATER_ON) && diodeRear(state & 15) === opposite(h)) return true;
  // One step down past an open side and one step up a full solid block it links to power sources too, not only dust.
  if (!isSolid(w, n)) return sourceBeta(w, cells[n + step[DOWN]!]!);
  return !isSolid(w, at + step[UP]!) && sourceBeta(w, cells[n + step[UP]!]!);
}

/** Whether the dust at `at` powers the cell on its `toward` side. */
export function pointsAt(w: World, at: number, toward: Dir): boolean {
  const links = w.profile.modern ? linksModern : linksBeta;
  if (links(w, at, leftOf(toward)) || links(w, at, rightOf(toward))) return false;
  return links(w, at, opposite(toward)) || !links(w, at, toward);
}

function output(w: World, at: number, state: number, toward: Dir): number {
  if (w.dustHidden) return 0;
  const level = state & 15;
  if (level === 0 || toward === UP) return 0;
  return toward === DOWN || pointsAt(w, at, toward) ? level : 0;
}

// ---------------------------------------------------------------------------------------------- levels

/** The solver's working arrays, one set per world. Nothing in them outlives a solve. */
interface Scratch {
  /** The number of the solve that last visited each cell. */
  mark: Int32Array;
  /** The level each cell of the network gets. */
  next: Uint8Array;
  buckets: number[][];
  solves: number;
  /** How many solves are telling their neighbours right now (they can nest). */
  telling: number;
}

const SCRATCH = new WeakMap<World, Scratch>();

function scratchOf(w: World): Scratch {
  let s = SCRATCH.get(w);
  if (!s) {
    s = {
      mark: new Int32Array(w.cells.length),
      next: new Uint8Array(w.cells.length),
      buckets: Array.from({ length: 16 }, (): number[] => []),
      solves: 0,
      telling: 0,
    };
    SCRATCH.set(w, s);
  }
  return s;
}

/** The power the cell is given by everything that is not dust. b1.8.1 knows only on and off here. */
function external(w: World, at: number): number {
  w.dustHidden = true;
  const power = powerInto(w, at);
  w.dustHidden = false;
  return w.profile.modern || power === 0 ? power : 15;
}

/** The level the dust at `at` should have, given the dust around it as it is now. */
function wanted(w: World, at: number): number {
  let best = external(w, at);
  if (best >= 15) return 15;
  const { cells, step } = w;
  const open = !isSolid(w, at + step[UP]!);
  for (const h of HORIZONTAL) {
    const n = at + step[h]!;
    const beside = cells[n]!;
    if (isWire(beside)) best = Math.max(best, (beside & 15) - 1);
    if (!isSolid(w, n)) {
      const below = cells[n + step[DOWN]!]!;
      if (isWire(below)) best = Math.max(best, (below & 15) - 1);
    } else if (open) {
      const above = cells[n + step[UP]!]!;
      if (isWire(above)) best = Math.max(best, (above & 15) - 1);
    }
  }
  return best;
}

/** Works out the whole network the dust at `start` belongs to, then tells the cells around every dust that changed. */
function solve(w: World, start: number): void {
  const { cells, step } = w;
  const s = scratchOf(w);
  const { mark, next, buckets } = s;
  const stamp = ++s.solves;
  const up = step[UP]!;
  const down = step[DOWN]!;

  // The network: every dust that reads another or is read by it.
  const network = [start];
  mark[start] = stamp;
  const join = (at: number): void => {
    if (mark[at] !== stamp && isWire(cells[at]!)) {
      mark[at] = stamp;
      network.push(at);
    }
  };
  for (let i = 0; i < network.length; i++) {
    const at = network[i]!;
    const open = !isSolid(w, at + up);
    for (const h of HORIZONTAL) {
      const n = at + step[h]!;
      join(n);
      if (open) join(n + up);
      if (!isSolid(w, n)) join(n + down);
    }
  }

  // Levels from the strongest down: a cell at a level lifts every dust that reads it to one less.
  for (const bucket of buckets) bucket.length = 0;
  for (const at of network) {
    const level = external(w, at);
    next[at] = level;
    if (level > 1) buckets[level]!.push(at);
  }
  for (let level = 15; level > 1; level--) {
    const bucket = buckets[level]!;
    const lower = level - 1;
    const lift = (at: number): void => {
      if (mark[at] === stamp && next[at]! < lower) {
        next[at] = lower;
        if (lower > 1) buckets[lower]!.push(at);
      }
    };
    for (let i = 0; i < bucket.length; i++) {
      const at = bucket[i]!;
      if (next[at] !== level) continue;
      const open = !isSolid(w, at + up);
      const onSolid = isSolid(w, at + down);
      for (const h of HORIZONTAL) {
        const n = at + step[h]!;
        lift(n);
        if (open) lift(n + up);
        if (onSolid && !isSolid(w, n)) lift(n + down);
      }
    }
  }

  // Each change as one number, so sorting the numbers sorts the cells: cell, old level, new level.
  const changes: number[] = [];
  for (const at of network) {
    const old = cells[at]! & 15;
    const level = next[at]!;
    if (old === level) continue;
    changes.push(at * 256 + old * 16 + level);
    w.set(at, (WIRE << 4) | level);
  }
  if (changes.length === 0) return;
  changes.sort((a, b) => a - b);

  const modern = w.profile.modern;
  const tellAround = (center: number): void => {
    for (const d of UPDATE_ORDER) {
      const at = center + step[d]!;
      // Dust of this network has just been worked out from the world as it is.
      if (mark[at] !== stamp) w.notify(at, center, WIRE);
    }
  };
  s.telling++;
  try {
    for (const change of changes) {
      const at = Math.floor(change / 256);
      const old = (change >> 4) & 15;
      const level = change & 15;
      // b1.8.1 tells the neighbours only when dust comes on from nothing or falls to its last level or to nothing.
      if (!modern && old !== 0 && level > 1) continue;
      tellAround(at);
      for (const d of UPDATE_ORDER) tellAround(at + step[d]!);
    }
  } finally {
    s.telling--;
  }
}

/**
 * Whether a dust network is telling its neighbours of a change right now. In the game that happens cell by cell while
 * the network is half updated, so a part that acts at this moment may act differently there.
 */
export const dustTelling = (w: World): boolean => (SCRATCH.get(w)?.telling ?? 0) > 0;

/** A dust cell heard of a change: a network is solved only when this cell's own level no longer fits. */
function refresh(w: World, at: number): void {
  if (wanted(w, at) !== (w.cells[at]! & 15)) solve(w, at);
}

export const wire: ComponentHandler = {
  name: 'Redstone dust',
  ids: () => [WIRE],
  normalize: () => WIRE << 4,
  supported: (w, at) => w.profile.holdsDust(w.cells[at + w.step[DOWN]!]!),
  weak: output,
  strong: output,
  update: refresh,
  power: (_w, _at, state) => state & 15,
};
