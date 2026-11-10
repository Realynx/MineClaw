/**
 * The comparator (1.12.2 only). It reads the cell behind it like a repeater does (a part's power, a powered block, bare
 * dust at its level) but keeps the level; a container behind it, or behind one solid block when the rear power is under
 * 15, gives its fill level instead. Its sides read dust at its level, a redstone block as 15 and any other power source by
 * the strong power it gives the comparator.
 *
 * Compare mode puts out the rear level while it is at least the stronger side, else nothing; subtract mode puts out rear
 * minus side, never below 0. The output changes 2 game ticks after the inputs do, with priority -1 when it faces a
 * repeater or comparator that does not face back, else 0, and it never asks while a tick of its own is still due this
 * tick. It is never locked. The level it holds is kept beside the block (the game's tile entity); the block shows
 * powered (meta bit 8) and the mode (bit 4).
 *
 * Who hears a change: the cell in front and that cell's other five neighbours, as for the 1.12.2 repeater.
 */
import { DOWN, HORIZONTAL, diodeRear, leftOf, opposite, rightOf, type Dir } from '../orientation.js';
import { isSolid, powerFrom, strongFrom } from '../power.js';
import { isWire } from '../wire.js';
import type { ComponentHandler, Store, World } from './handler.js';

const OFF = 149;
const ON = 150;
const REPEATER_OFF = 93;
const REPEATER_ON = 94;
const REDSTONE_BLOCK = 152;
const SUBTRACT = 4;
const POWERED = 8;
const DELAY = 2;

/** What each comparator holds, by cell; a cell that is not listed holds 0. */
class Outputs implements Store {
  private map = new Map<number, number>();

  get(at: number): number {
    return this.map.get(at) ?? 0;
  }

  set(at: number, level: number): void {
    if (level > 0) this.map.set(at, level);
    else this.map.delete(at);
  }

  save(): Map<number, number> {
    return new Map(this.map);
  }

  load(saved: unknown): void {
    this.map = new Map(saved as Map<number, number>);
  }
}

const outputsOf = (w: World): Outputs => w.state('comparator.outputs', () => new Outputs());

const isDiode = (id: number): boolean => id === REPEATER_OFF || id === REPEATER_ON || id === OFF || id === ON;
const isOn = (state: number): boolean => state >> 4 === ON || (state & POWERED) !== 0;

/** The fill level of a container at `at`, or null when the cell holds none. */
function containerLevel(w: World, at: number): number | null {
  const state = w.cells[at]!;
  if (state <= 0) return null;
  const fill = w.handlers[state >> 4]?.fill;
  return fill ? fill(w, at, state) : null;
}

/** The rear input: power, bare dust, or a container's level (also through one solid block). */
function rearOf(w: World, at: number, meta: number): number {
  const rear = diodeRear(meta);
  const from = at + w.step[rear]!;
  let level = powerFrom(w, from, opposite(rear));
  const behind = w.cells[from]!;
  if (level < 15 && isWire(behind)) level = Math.max(level, behind & 15);
  const own = containerLevel(w, from);
  if (own !== null) return own;
  if (level < 15 && isSolid(w, from)) {
    const further = containerLevel(w, from + w.step[rear]!);
    if (further !== null) return further;
  }
  return level;
}

/** One side input: dust at its level, a redstone block as 15, another power source by its strong power. */
function sideOf(w: World, at: number, side: Dir): number {
  const from = at + w.step[side]!;
  const state = w.cells[from]!;
  if (state <= 0) return 0;
  const id = state >> 4;
  if (id === REDSTONE_BLOCK) return 15;
  if (isWire(state)) return state & 15;
  return w.profile.source[id] === 1 ? strongFrom(w, from, opposite(side)) : 0;
}

function sidesOf(w: World, at: number, meta: number): number {
  const rear = diodeRear(meta);
  return Math.max(sideOf(w, at, leftOf(rear)), sideOf(w, at, rightOf(rear)));
}

/** Whether the comparator should show powered: the game's compare test, in either mode. */
function shouldBeOn(rear: number, side: number): boolean {
  if (rear >= 15) return true;
  if (rear === 0) return false;
  return side === 0 || rear >= side;
}

function outputOf(rear: number, side: number, meta: number): number {
  return (meta & SUBTRACT) !== 0 ? Math.max(rear - side, 0) : rear;
}

/** 1.12.2: whether the cell in front holds a repeater or comparator that does not put out into this one. */
function facesDiode(w: World, at: number, meta: number): boolean {
  const front = opposite(diodeRear(meta));
  const ahead = w.cells[at + w.step[front]!]!;
  return ahead > 0 && isDiode(ahead >> 4) && diodeRear(ahead & 15) !== front;
}

/** The level it gives the cell in front: the held level while it shows powered. */
function given(w: World, at: number, state: number, toward: Dir): number {
  return isOn(state) && toward === opposite(diodeRear(state & 15)) ? outputsOf(w).get(at) : 0;
}

/** Tells the cell in front and that cell's other neighbours. */
function tellFront(w: World, at: number, meta: number): void {
  const rear = diodeRear(meta);
  const front = at + w.step[opposite(rear)]!;
  w.notify(front, at, OFF);
  w.notifyAroundExcept(front, OFF, rear);
}

/** The scheduled tick: take the new level, show powered or not, and tell the cell in front. */
function act(w: World, at: number, state: number): void {
  const meta = state & 15;
  const rear = rearOf(w, at, meta);
  const side = sidesOf(w, at, meta);
  const next = outputOf(rear, side, meta);
  const outputs = outputsOf(w);
  const held = outputs.get(at);
  outputs.set(at, next);
  if (held === next && (meta & SUBTRACT) !== 0) return;
  const should = shouldBeOn(rear, side);
  const base = (OFF << 4) | (meta & 7);
  if (isOn(state) !== should) w.set(at, should ? base | POWERED : base);
  tellFront(w, at, meta);
}

/** A neighbour changed: ask for a tick when the level or the powered look would change. */
function look(w: World, at: number, state: number): void {
  if (w.tickPendingNow(at)) return;
  const meta = state & 15;
  const rear = rearOf(w, at, meta);
  const side = sidesOf(w, at, meta);
  if (outputOf(rear, side, meta) === outputsOf(w).get(at) && isOn(state) === shouldBeOn(rear, side)) return;
  w.schedule(at, DELAY, facesDiode(w, at, meta) ? -1 : 0);
}

export const comparator: ComponentHandler = {
  name: 'Comparator',
  ids: (v) => (v === '1.12.2' ? [OFF, ON] : []),
  // The powered bit and the old powered block are run-time state; the facing and the mode are the builder's.
  normalize: (state) => (OFF << 4) | (state & 7),
  supported: (w, at) => w.profile.holdsDiode(w.cells[at + w.step[DOWN]!]!),
  weak: given,
  strong: given,
  init: (w) => void outputsOf(w),
  update: (w, at, state) => look(w, at, state),
  tick: act,
  tickKey: () => OFF,
  power: (w, at, state) => (isOn(state) ? outputsOf(w).get(at) : 0),
};

/** Tells the comparators a container feeds that its level changed: beside it, or one solid block away. */
export function containerChanged(w: World, at: number, sourceId: number): void {
  for (const d of HORIZONTAL) {
    const next = at + w.step[d]!;
    const state = w.cells[next]!;
    if (state > 0 && (state >> 4 === OFF || state >> 4 === ON)) w.notify(next, at, sourceId);
    else if (isSolid(w, next)) {
      const further = next + w.step[d]!;
      const s = w.cells[further]!;
      if (s > 0 && (s >> 4 === OFF || s >> 4 === ON)) w.notify(further, at, sourceId);
    }
  }
}
