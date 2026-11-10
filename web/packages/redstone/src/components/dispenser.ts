/**
 * Dispensers and droppers. They hold nothing here: firing is a `fired` event. Both are powered from their own cell or
 * the cell above it, and fire 4 game ticks after they notice, if still powered then (b1.8.1) or in any case (1.12.2).
 *
 * - b1.8.1 (dispensers only): it notices only an update from a power-source block, and it has no "triggered" flag, so
 *   every such update while it is powered fires it again.
 * - 1.12.2: a "triggered" flag (meta bit 8) is set when it notices power and cleared when the power goes, so it fires
 *   once per rising edge. The flag changes tell no neighbour.
 *
 * In 1.12.2 a comparator reads their fill level (`level` input); b1.8.1 has no comparator.
 */
import type { ComponentHandler, World } from './handler.js';
import { containerChanged } from './comparator.js';
import { poweredHereOrAbove } from './util.js';
import { powerInto } from '../power.js';
import { UP } from '../orientation.js';

const DISPENSER = 23;
const DROPPER = 158;
const TRIGGERED = 8;
const DELAY = 4;

const powerOf = (w: World, at: number): number => Math.max(powerInto(w, at), powerInto(w, at + w.step[UP]!));

export const dispenser: ComponentHandler = {
  name: 'Dispenser',
  ids: (v) => (v === '1.12.2' ? [DISPENSER, DROPPER] : [DISPENSER]),
  valid: (state) => (state & 7) <= 5,
  normalize: (state) => state & ~TRIGGERED,
  // Building one next to power does not fire it: it notices at its next update, as in the game.
  start: () => undefined,
  update: (w, at, state, _from, sourceId) => {
    const powered = poweredHereOrAbove(w, at);
    if (!w.profile.modern) {
      if (powered && sourceId > 0 && w.profile.source[sourceId] === 1) w.schedule(at, DELAY);
      return;
    }
    const triggered = (state & TRIGGERED) !== 0;
    if (powered && !triggered) {
      w.schedule(at, DELAY);
      w.set(at, state | TRIGGERED);
    } else if (!powered && triggered) w.set(at, state & ~TRIGGERED);
  },
  tick: (w, at) => {
    if (w.profile.modern || poweredHereOrAbove(w, at)) w.event('fired', at);
  },
  inputKind: (_state, v) => (v === '1.12.2' ? 'level' : null),
  input: (w, at, state, e) => {
    if (e.kind !== 'level' || !w.profile.modern) return;
    w.setLevel(at, e.level);
    containerChanged(w, at, state >> 4);
  },
  fill: (w, at) => w.level(at),
  power: powerOf,
};
