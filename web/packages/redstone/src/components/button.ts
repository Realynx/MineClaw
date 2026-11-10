/**
 * The button: pressed by hand, it lets go by itself, a stone one after 20 game ticks and a wooden one (1.12.2) after
 * 30. Pressing a pressed button does nothing. Pressed, it powers like a lever that is on. b1.8.1 has it on walls only;
 * 1.12.2 also on a floor and under a block. Nothing but the user and the timer works it here: no arrow holds a wooden
 * button down.
 */
import { buttonSupport, opposite } from '../orientation.js';
import type { ComponentHandler, World } from './handler.js';

const STONE = 77;
const WOODEN = 143;
const PRESSED = 8;

function change(w: World, at: number, next: number): void {
  const id = next >> 4;
  w.set(at, next);
  w.notifyAround(at, id);
  w.notifyAround(at + w.step[buttonSupport(next & 15)]!, id);
}

export const button: ComponentHandler = {
  name: 'Button',
  ids: (v) => (v === '1.12.2' ? [STONE, WOODEN] : [STONE]),
  valid: (state, v) => (v === '1.12.2' ? (state & 7) <= 5 : (state & 7) >= 1 && (state & 7) <= 4),
  normalize: (state) => state & ~PRESSED,
  supported: (w, at, state) => {
    const dir = buttonSupport(state & 15);
    return w.profile.holdsOnFace(w.cells[at + w.step[dir]!]!, opposite(dir));
  },
  weak: (_w, _at, state) => ((state & PRESSED) !== 0 ? 15 : 0),
  strong: (_w, _at, state, toward) => ((state & PRESSED) !== 0 && toward === buttonSupport(state & 15) ? 15 : 0),
  tick: (w, at, state) => {
    if ((state & PRESSED) !== 0) change(w, at, state & ~PRESSED);
  },
  inputKind: () => 'button',
  input: (w, at, state, e) => {
    if (e.kind !== 'press' || (state & PRESSED) !== 0) return;
    change(w, at, state | PRESSED);
    w.schedule(at, state >> 4 === WOODEN ? 30 : 20);
  },
  power: (_w, _at, state) => ((state & PRESSED) !== 0 ? 15 : 0),
};
