/**
 * Doors, trapdoors and fence gates: they follow power at once, with no delay, by each version's rule.
 *
 * - b1.8.1 doors and trapdoors act only on an update from a power-source block (a repeater is not one there), and their
 *   change tells their neighbours (the game writes it with neighbour updates). A door is powered when either half is.
 *   Fence gates ignore power.
 * - 1.12.2 doors and trapdoors open on any update while powered and close only on an update from a power provider;
 *   a door keeps "powered" in its upper half. Fence gates follow power. None of them tells its neighbours.
 */
import type { RedstoneVersion } from '../api.js';
import { DOWN, UP, dirOfSide } from '../orientation.js';
import { powerInto } from '../power.js';
import type { ComponentHandler, World } from './handler.js';
import { poweredAt } from './util.js';

const WOODEN_DOOR = 64;
const IRON_DOOR = 71;
/** 1.12.2's spruce, birch, jungle, acacia and dark oak doors. */
const WOOD_DOORS_1122 = [193, 194, 195, 196, 197];
const TRAPDOOR = 96;
const IRON_TRAPDOOR = 167;
const GATES_1122 = [107, 183, 184, 185, 186, 187];

const UPPER = 8;
const OPEN = 4;
/** 1.12.2: the upper half's "powered" bit. */
const UPPER_POWERED = 2;
/** 1.12.2: a fence gate's "powered" bit. */
const GATE_POWERED = 8;

/** Whether the source of an update is a block that gives power: the only updates some parts listen to. */
const fromSource = (w: World, sourceId: number): boolean => sourceId > 0 && w.profile.source[sourceId] === 1;

const isUpper = (state: number): boolean => (state & UPPER) !== 0;

/** The door's power: either half powered counts. */
const doorPower = (w: World, lower: number): number => Math.max(powerInto(w, lower), powerInto(w, lower + w.step[UP]!));

/** b1.8.1: opens or closes both halves; each write tells its neighbours. */
function setDoorBeta(w: World, lower: number, state: number, open: boolean): void {
  if (((state & OPEN) !== 0) === open) return;
  const id = state >> 4;
  const meta = state & 15;
  const upper = lower + w.step[UP]!;
  if (w.cells[upper]! >> 4 === id) {
    w.set(upper, (id << 4) | ((meta ^ OPEN) | UPPER));
    w.notifyAround(upper, id);
  }
  w.set(lower, (id << 4) | (meta ^ OPEN));
  w.notifyAround(lower, id);
}

/** The lower half heard an update (or the upper half passed one on). */
function lowerHeard(w: World, lower: number, state: number, sourceId: number): void {
  const id = state >> 4;
  const powered = doorPower(w, lower) > 0;
  if (!w.profile.modern) {
    if (fromSource(w, sourceId)) setDoorBeta(w, lower, state, powered);
    return;
  }
  if (sourceId === id) return;
  const upper = lower + w.step[UP]!;
  const top = w.cells[upper]!;
  if (top >> 4 !== id) return;
  if (!(powered || fromSource(w, sourceId)) || ((top & UPPER_POWERED) !== 0) === powered) return;
  w.set(upper, powered ? top | UPPER_POWERED : top & ~UPPER_POWERED);
  if (((state & OPEN) !== 0) !== powered) w.set(lower, state ^ OPEN);
}

export const door: ComponentHandler = {
  name: 'Door',
  ids: (v) => (v === '1.12.2' ? [WOODEN_DOOR, IRON_DOOR, ...WOOD_DOORS_1122] : [WOODEN_DOOR, IRON_DOOR]),
  // 1.12.2 keeps "powered" in the upper half: that is run-time state.
  normalize: (state, v) => (v === '1.12.2' && isUpper(state) ? state & ~UPPER_POWERED : state),
  supported: (w, at, state) => {
    const id = state >> 4;
    if (isUpper(state)) {
      const below = w.cells[at + w.step[DOWN]!]!;
      return below >> 4 === id && !isUpper(below);
    }
    const above = w.cells[at + w.step[UP]!]!;
    return above >> 4 === id && isUpper(above) && w.profile.holdsDiode(w.cells[at + w.step[DOWN]!]!);
  },
  update: (w, at, state, _from, sourceId) => {
    if (!isUpper(state)) {
      lowerHeard(w, at, state, sourceId);
      return;
    }
    // The upper half passes the update on to the lower half (b1.8.1 only one from a power source).
    const lower = at + w.step[DOWN]!;
    const pass = w.profile.modern ? sourceId !== state >> 4 : fromSource(w, sourceId);
    if (pass) lowerHeard(w, lower, w.cells[lower]!, sourceId);
  },
  power: (w, at, state) => doorPower(w, isUpper(state) ? at + w.step[DOWN]! : at),
};

/** b1.8.1: the block a trapdoor hangs on, by the low two bits (the trapdoor's facing in the studio's table). */
const TRAPDOOR_WALL = ['south', 'north', 'east', 'west'] as const;

export const trapdoor: ComponentHandler = {
  name: 'Trapdoor',
  ids: (v) => (v === '1.12.2' ? [TRAPDOOR, IRON_TRAPDOOR] : [TRAPDOOR]),
  // 1.12.2 trapdoors hold on by themselves.
  supported: (w, at, state) => w.profile.modern || w.solid(at + w.step[dirOfSide(TRAPDOOR_WALL[state & 3]!)]!),
  update: (w, at, state, _from, sourceId) => {
    const powered = poweredAt(w, at);
    if (((state & OPEN) !== 0) === powered) return;
    if (!w.profile.modern) {
      if (!fromSource(w, sourceId)) return;
      w.set(at, state ^ OPEN);
      w.notifyAround(at, state >> 4);
    } else if (powered || fromSource(w, sourceId)) w.set(at, state ^ OPEN);
  },
  power: (w, at) => powerInto(w, at),
};

export const fenceGate: ComponentHandler = {
  name: 'Fence gate',
  // b1.8.1 fence gates have no redstone behaviour at all.
  ids: (v: RedstoneVersion) => (v === '1.12.2' ? GATES_1122 : []),
  normalize: (state) => state & ~GATE_POWERED,
  update: (w, at, state) => {
    const powered = poweredAt(w, at);
    if (((state & GATE_POWERED) !== 0) === powered) return;
    w.set(at, (state & ~(OPEN | GATE_POWERED)) | (powered ? OPEN | GATE_POWERED : 0));
  },
  power: (w, at) => powerInto(w, at),
};
