/**
 * The observer (1.12.2 only). It watches the cell its face points at: any change of that cell's state (whether or not
 * the change tells its neighbours) asks for a tick 2 game ticks later. The tick turns it on and asks for another, which
 * turns it off, so it gives a 2 game tick pulse of 15, weak and strong, out of its back only. Each of its own changes
 * tells the cell behind it and that cell's other five neighbours.
 *
 * Built into a design it does not fire on its own; moved into place by a piston it does, as in the game.
 */
import { facing6, opposite } from '../orientation.js';
import type { ComponentHandler, World } from './handler.js';

const OBSERVER = 218;
const POWERED = 8;
const DELAY = 2;

const isOn = (state: number): boolean => (state & POWERED) !== 0;

/** 15 out of its back while it is on. */
const output = (state: number, toward: number): number =>
  isOn(state) && toward === opposite(facing6(state & 15)) ? 15 : 0;

function tellBehind(w: World, at: number, state: number): void {
  const face = facing6(state & 15);
  const behind = at + w.step[opposite(face)]!;
  w.notify(behind, at, OBSERVER);
  w.notifyAroundExcept(behind, OBSERVER, face);
}

/** It saw a change: a tick in 2 game ticks, unless it is on (the scheduler drops a second request). */
const notice = (w: World, at: number, state: number): void => {
  if (!isOn(state)) w.schedule(at, DELAY);
};

function tick(w: World, at: number, state: number): void {
  const next = isOn(state) ? state & ~POWERED : state | POWERED;
  w.set(at, next);
  if (!isOn(state)) w.schedule(at, DELAY);
  tellBehind(w, at, next);
}

export const observer: ComponentHandler = {
  name: 'Observer',
  ids: (v) => (v === '1.12.2' ? [OBSERVER] : []),
  valid: (state) => (state & 7) <= 5,
  normalize: (state) => state & ~POWERED,
  start: () => undefined,
  weak: (_w, _at, state, toward) => output(state, toward),
  strong: (_w, _at, state, toward) => output(state, toward),
  observed: (w, at, state, changed) => {
    if (at + w.step[facing6(state & 15)]! === changed) notice(w, at, state);
  },
  tick,
  placed: (w, at, state) => {
    if (isOn(state)) tick(w, at, state);
    notice(w, at, w.cells[at]!);
  },
  power: (_w, _at, state) => (isOn(state) ? 15 : 0),
};
