/**
 * Small things the parts of engine core B share: how a block that changed tells its neighbours, and the two power tests
 * the sinks use.
 */
import { UP } from '../orientation.js';
import { powerInto } from '../power.js';
import { INERT } from '../grid.js';
import type { World } from './handler.js';

/** The block id of a state; 0 for air, the ground and a part left out of the simulation. */
export const idOf = (state: number): number => (state > 0 && state < INERT ? state >> 4 : 0);

/** The game's "is this block powered": the strongest power any of its six neighbours gives it is above 0. */
export const poweredAt = (w: World, at: number): boolean => powerInto(w, at) > 0;

/** Powered here or in the cell above: the reach of a dispenser, a dropper and a door. */
export const poweredHereOrAbove = (w: World, at: number): boolean => poweredAt(w, at) || poweredAt(w, at + w.step[UP]!);

/**
 * Writes a new block and tells its six neighbours, as the game's "set the block, update the neighbours" does. b1.8.1
 * names the new block as the source, 1.12.2 the old one.
 */
export function replace(w: World, at: number, next: number): void {
  const old = w.cells[at]!;
  if (old === next) return;
  w.set(at, next);
  w.notifyAround(at, w.profile.modern ? idOf(old) : idOf(next));
}
