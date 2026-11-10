/**
 * Power between cells. A part gives weak power to the cells beside it and strong power to some of them; a full solid
 * block passes on only the strong power it receives. `toward` is always the direction from the cell that gives the
 * power to the cell that reads it.
 *
 * Levels are 0..15 in both versions. b1.8.1 only ever asks "any power?", so its parts read a level as on or off.
 * While `w.dustHidden` is set dust gives nothing, which is how dust reads its own input without feeding itself.
 */
import type { World } from './components/handler.js';
import { opposite, type Dir } from './orientation.js';

/** Whether the cell holds a full solid block that passes power on; the ground is one. */
export function isSolid(w: World, at: number): boolean {
  const state = w.cells[at]!;
  return state < 0 || w.profile.conducts[state >> 4] === 1;
}

/** The weak power the part at `at` gives the cell on its `toward` side. */
export function weakFrom(w: World, at: number, toward: Dir): number {
  const state = w.cells[at]!;
  if (state <= 0) return 0;
  const weak = w.handlers[state >> 4]?.weak;
  return weak ? weak(w, at, state, toward) : 0;
}

/** The strong power the part at `at` gives the cell on its `toward` side. */
export function strongFrom(w: World, at: number, toward: Dir): number {
  const state = w.cells[at]!;
  if (state <= 0) return 0;
  const strong = w.handlers[state >> 4]?.strong;
  return strong ? strong(w, at, state, toward) : 0;
}

/** The strong power the cell receives from its six neighbours: what a full solid block there passes on. */
export function strongInto(w: World, at: number): number {
  let power = 0;
  for (let d = 0; d < 6; d++) {
    const p = strongFrom(w, at + w.step[d]!, opposite(d));
    if (p > power) {
      if (p >= 15) return 15;
      power = p;
    }
  }
  return power;
}

/** What the cell at `at` gives the reader on its `toward` side: a solid block what it passes on, a part its weak power. */
export function powerFrom(w: World, at: number, toward: Dir): number {
  return isSolid(w, at) ? strongInto(w, at) : weakFrom(w, at, toward);
}

/** The strongest power reaching the cell from its six neighbours. */
export function powerInto(w: World, at: number): number {
  let power = 0;
  for (let d = 0; d < 6; d++) {
    const p = powerFrom(w, at + w.step[d]!, opposite(d));
    if (p > power) {
      if (p >= 15) return 15;
      power = p;
    }
  }
  return power;
}
