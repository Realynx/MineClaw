/**
 * The repeater: it reads the cell behind it (a part's power, a powered block, or bare dust at any level) and puts 15
 * out in front, 2, 4, 6 or 8 game ticks later. Its tick always turns an off repeater on, and asks for the turn-off
 * itself when the input has gone by then, so a pulse shorter than the delay comes out a full delay long.
 *
 * 1.12.2 only:
 * - Locking: while a powered repeater or comparator points into its side it neither asks for a tick nor acts on one.
 *   The lock is looked at when the tick runs, not when it was asked for.
 * - Priority among ticks of the same game tick: -3 when it faces another repeater or comparator that does not face
 *   back at it, else -2 when it is turning off, else -1; the turn-off it asks for itself is -1.
 * - It asks for a tick only while none of its own is still due on the tick being run.
 *
 * Who hears a change: 1.12.2 tells the cell in front and that cell's other five neighbours, nothing beside or behind
 * itself. b1.8.1 tells the cells around each of its six neighbours and then the neighbours, and a repeater is not a
 * power-source block there, which the parts that only listen to power sources care about.
 */
import { DOWN, EAST, NORTH, SOUTH, UP, WEST, diodeRear, leftOf, opposite, rightOf, type Dir } from '../orientation.js';
import { powerFrom, strongFrom } from '../power.js';
import { isWire } from '../wire.js';
import type { ComponentHandler, World } from './handler.js';

const OFF = 93;
const ON = 94;
const COMPARATOR_OFF = 149;
const COMPARATOR_ON = 150;

/** The order b1.8.1 walks the six neighbours when a repeater tells the cells around every one of them. */
const RING_BETA: readonly Dir[] = [EAST, WEST, SOUTH, NORTH, DOWN, UP];

const isDiode = (id: number): boolean => id === OFF || id === ON || id === COMPARATOR_OFF || id === COMPARATOR_ON;
const delayOf = (meta: number): number => ((meta >> 2) + 1) * 2;
/** What the repeater gives the cell on its `toward` side: 15 out of its front when it is on. */
const output = (state: number, toward: Dir): number =>
  state >> 4 === ON && toward === opposite(diodeRear(state & 15)) ? 15 : 0;

/** Whether the cell behind the repeater powers it. */
function fed(w: World, at: number, meta: number): boolean {
  const rear = diodeRear(meta);
  const from = at + w.step[rear]!;
  if (powerFrom(w, from, opposite(rear)) > 0) return true;
  const behind = w.cells[from]!;
  return isWire(behind) && (behind & 15) > 0;
}

function locksFrom(w: World, at: number, side: Dir): boolean {
  const from = at + w.step[side]!;
  return isDiode(w.cells[from]! >> 4) && strongFrom(w, from, opposite(side)) > 0;
}

function locked(w: World, at: number, state: number): boolean {
  if (!w.profile.modern) return false;
  const rear = diodeRear(state & 15);
  return locksFrom(w, at, leftOf(rear)) || locksFrom(w, at, rightOf(rear));
}

/** 1.12.2: whether the cell in front holds a repeater or comparator that does not put out into this one. */
function facesDiode(w: World, at: number, meta: number): boolean {
  const front = opposite(diodeRear(meta));
  const ahead = w.cells[at + w.step[front]!]!;
  return isDiode(ahead >> 4) && diodeRear(ahead & 15) !== front;
}

function turn(w: World, at: number, next: number): void {
  const id = next >> 4;
  w.set(at, next);
  if (w.profile.modern) {
    const rear = diodeRear(next & 15);
    const front = at + w.step[opposite(rear)]!;
    w.notify(front, at, id);
    w.notifyAroundExcept(front, id, rear);
    return;
  }
  for (const d of RING_BETA) w.notifyAround(at + w.step[d]!, id);
  w.notifyAround(at, id);
}

export const repeater: ComponentHandler = {
  name: 'Repeater',
  ids: () => [OFF, ON],
  normalize: (state) => (OFF << 4) | (state & 15),
  supported: (w, at) => w.profile.holdsDiode(w.cells[at + w.step[DOWN]!]!),
  weak: (_w, _at, state, toward) => output(state, toward),
  strong: (_w, _at, state, toward) => output(state, toward),
  update: (w, at, state) => {
    const meta = state & 15;
    const on = state >> 4 === ON;
    if (locked(w, at, state) || on === fed(w, at, meta)) return;
    if (!w.profile.modern) w.schedule(at, delayOf(meta));
    else if (!w.tickPendingNow(at)) w.schedule(at, delayOf(meta), facesDiode(w, at, meta) ? -3 : on ? -2 : -1);
  },
  tick: (w, at, state) => {
    if (locked(w, at, state)) return;
    const meta = state & 15;
    const want = fed(w, at, meta);
    if (state >> 4 === ON) {
      if (!want) turn(w, at, (OFF << 4) | meta);
      return;
    }
    turn(w, at, (ON << 4) | meta);
    if (!want) w.schedule(at, delayOf(meta), -1);
  },
  // 1.12.2 counts a powered and an unpowered repeater as one block for its scheduled ticks; b1.8.1 does not.
  tickKey: (id, v) => (v === '1.12.2' ? OFF : id),
  power: (_w, _at, state) => (state >> 4 === ON ? 15 : 0),
  locked,
};
