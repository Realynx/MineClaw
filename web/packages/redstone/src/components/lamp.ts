/**
 * The redstone lamp (1.12.2 only): it lights the moment it is powered and goes out 4 game ticks after the power goes,
 * if it is still unpowered then. Its own change tells no neighbour (only the observers that watch it).
 */
import { powerInto } from '../power.js';
import type { ComponentHandler, World } from './handler.js';
import { poweredAt } from './util.js';

const OFF = 123;
const LIT = 124;
const DELAY = 4;

/** Put in place (at load or by a piston): it takes its state at once, either way. */
function settle(w: World, at: number, state: number): void {
  const powered = poweredAt(w, at);
  if ((state >> 4 === LIT) !== powered) w.set(at, (powered ? LIT : OFF) << 4);
}

export const lamp: ComponentHandler = {
  name: 'Redstone lamp',
  ids: (v) => (v === '1.12.2' ? [OFF, LIT] : []),
  normalize: () => OFF << 4,
  start: settle,
  placed: settle,
  update: (w, at, state) => {
    const lit = state >> 4 === LIT;
    const powered = poweredAt(w, at);
    if (lit && !powered) w.schedule(at, DELAY);
    else if (!lit && powered) w.set(at, LIT << 4);
  },
  tick: (w, at, state) => {
    if (state >> 4 === LIT && !poweredAt(w, at)) w.set(at, OFF << 4);
  },
  power: (w, at) => powerInto(w, at),
};
