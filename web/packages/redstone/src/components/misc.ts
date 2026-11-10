/**
 * The smaller parts of engine core B, and the manual sources (report simplification 5).
 *
 * Sinks:
 * - Note block: plays (a `played` event) when its power rises. b1.8.1 needs strong power and an update from a power-source
 *   block; 1.12.2 any power.
 * - TNT: primed (a `primed` event) and removed when it is powered, put in place powered included; nothing explodes.
 *   b1.8.1 notices only an update from a power-source block.
 * - Hopper (1.12.2): disabled (meta bit 8) while powered; the change tells no neighbour.
 *
 * Sources and containers (1.12.2 unless said otherwise):
 * - Redstone block: weak power 15 on every side, no strong power.
 * - Containers (chests, furnaces, hoppers, dispensers...): the user sets the fill level a comparator reads (`level`).
 * - Daylight sensors: the user sets the level they give (`level`), weak on every side.
 * - Trapped chest: the user sets how many are looking in (`level`): weak power on every side, strong into the block below,
 *   and the same number is the fill a comparator reads.
 * - Detector rail (both versions): held down or let go by the user (`hold`), like a plate: weak 15 on every side and
 *   strong into the block below while down.
 */
import { DOWN } from '../orientation.js';
import { powerInto, strongInto } from '../power.js';
import { containerChanged } from './comparator.js';
import type { ComponentHandler, Store, World } from './handler.js';
import { poweredAt, replace } from './util.js';

const range = (a: number, b: number): number[] => Array.from({ length: b - a + 1 }, (_, i) => a + i);

// ---------------------------------------------------------------------------------------------- note block

const NOTE_BLOCK = 25;

/** The note blocks that were powered when they last looked (the game keeps it beside the block). */
class Powered implements Store {
  private set = new Set<number>();

  has(at: number): boolean {
    return this.set.has(at);
  }

  put(at: number, on: boolean): void {
    if (on) this.set.add(at);
    else this.set.delete(at);
  }

  save(): Set<number> {
    return new Set(this.set);
  }

  load(saved: unknown): void {
    this.set = new Set(saved as Set<number>);
  }
}

const notesOf = (w: World): Powered => w.state('note.powered', () => new Powered());
/** b1.8.1 asks for strong power, 1.12.2 for any. */
const notePower = (w: World, at: number): number => (w.profile.modern ? powerInto(w, at) : strongInto(w, at));

export const noteBlock: ComponentHandler = {
  name: 'Note block',
  ids: () => [NOTE_BLOCK],
  init: (w) => void notesOf(w),
  // Built powered it has not played; it plays when its power next rises.
  start: (w, at) => notesOf(w).put(at, notePower(w, at) > 0),
  update: (w, at, _state, _from, sourceId) => {
    if (!w.profile.modern && !(sourceId > 0 && w.profile.source[sourceId] === 1)) return;
    const on = notePower(w, at) > 0;
    const notes = notesOf(w);
    if (notes.has(at) === on) return;
    if (on) w.event('played', at);
    notes.put(at, on);
  },
  power: notePower,
};

// ---------------------------------------------------------------------------------------------- TNT

const TNT = 46;

function prime(w: World, at: number): void {
  if (!poweredAt(w, at)) return;
  w.event('primed', at);
  replace(w, at, 0);
}

export const tnt: ComponentHandler = {
  name: 'TNT',
  ids: () => [TNT],
  start: prime,
  placed: prime,
  update: (w, at, _state, _from, sourceId) => {
    if (w.profile.modern || (sourceId > 0 && w.profile.source[sourceId] === 1)) prime(w, at);
  },
  power: (w, at) => powerInto(w, at),
};

// ---------------------------------------------------------------------------------------------- containers

const HOPPER = 154;
const DISABLED = 8;

/** A user-set fill level changed: the comparators that read it hear of it. */
function setFill(w: World, at: number, state: number, level: number): void {
  w.setLevel(at, level);
  containerChanged(w, at, state >> 4);
}

const fillInput: Pick<ComponentHandler, 'inputKind' | 'input' | 'fill'> = {
  inputKind: () => 'level',
  input: (w, at, state, e) => {
    if (e.kind === 'level') setFill(w, at, state, e.level);
  },
  fill: (w, at) => w.level(at),
};

function hopperLook(w: World, at: number, state: number): void {
  const disabled = poweredAt(w, at);
  if (((state & DISABLED) !== 0) !== disabled) w.set(at, disabled ? state | DISABLED : state & ~DISABLED);
}

export const hopper: ComponentHandler = {
  name: 'Hopper',
  ids: (v) => (v === '1.12.2' ? [HOPPER] : []),
  normalize: (state) => state & ~DISABLED,
  update: hopperLook,
  ...fillInput,
  power: (w, at) => powerInto(w, at),
};

/** Chests, furnaces, the jukebox, the brewing stand, the cauldron and shulker boxes: a fill level and nothing else. */
export const container: ComponentHandler = {
  name: 'Container',
  ids: (v) => (v === '1.12.2' ? [54, 61, 62, 84, 117, 118, ...range(219, 234)] : []),
  ...fillInput,
};

// ---------------------------------------------------------------------------------------------- manual sources

const REDSTONE_BLOCK = 152;

export const redstoneBlock: ComponentHandler = {
  name: 'Redstone block',
  ids: (v) => (v === '1.12.2' ? [REDSTONE_BLOCK] : []),
  weak: () => 15,
  power: () => 15,
};

const DAYLIGHT = 151;
const DAYLIGHT_INVERTED = 178;

/** The level the user gave a sensor becomes its state, and its neighbours hear of it. */
function sense(w: World, at: number, state: number): void {
  const level = w.level(at);
  if ((state & 15) !== level) replace(w, at, (state & ~15) | level);
}

export const daylightSensor: ComponentHandler = {
  name: 'Daylight sensor',
  ids: (v) => (v === '1.12.2' ? [DAYLIGHT, DAYLIGHT_INVERTED] : []),
  // Its meta is the level it gives: run-time state, which the user's level (`CellExtra.level`) sets.
  normalize: (state) => state & ~15,
  start: sense,
  weak: (_w, _at, state) => state & 15,
  inputKind: () => 'level',
  input: (w, at, state, e) => {
    if (e.kind !== 'level') return;
    w.setLevel(at, e.level);
    sense(w, at, state);
  },
  power: (_w, _at, state) => state & 15,
};

const TRAPPED_CHEST = 146;

export const trappedChest: ComponentHandler = {
  name: 'Trapped chest',
  ids: (v) => (v === '1.12.2' ? [TRAPPED_CHEST] : []),
  weak: (w, at) => w.level(at),
  strong: (w, at, _state, toward) => (toward === DOWN ? w.level(at) : 0),
  inputKind: () => 'level',
  input: (w, at, state, e) => {
    if (e.kind !== 'level' || w.level(at) === Math.max(0, Math.min(15, Math.floor(e.level)))) return;
    w.setLevel(at, e.level);
    w.notifyAround(at, TRAPPED_CHEST);
    w.notifyAround(at + w.step[DOWN]!, TRAPPED_CHEST);
    containerChanged(w, at, state >> 4);
  },
  fill: (w, at) => w.level(at),
  power: (w, at) => w.level(at),
};

const DETECTOR_RAIL = 28;
const RAIL_ON = 8;

const railOutput = (state: number): number => ((state & RAIL_ON) !== 0 ? 15 : 0);

export const detectorRail: ComponentHandler = {
  name: 'Detector rail',
  ids: () => [DETECTOR_RAIL],
  normalize: (state) => state & ~RAIL_ON,
  supported: (w, at) => w.profile.holdsDiode(w.cells[at + w.step[DOWN]!]!),
  weak: (_w, _at, state) => railOutput(state),
  strong: (_w, _at, state, toward) => (toward === DOWN ? railOutput(state) : 0),
  inputKind: () => 'plate',
  input: (w, at, state, e) => {
    if (e.kind !== 'hold' || ((state & RAIL_ON) !== 0) === e.on) return;
    w.set(at, e.on ? state | RAIL_ON : state & ~RAIL_ON);
    w.notifyAround(at, DETECTOR_RAIL);
    w.notifyAround(at + w.step[DOWN]!, DETECTOR_RAIL);
  },
  power: (_w, _at, state) => railOutput(state),
};
