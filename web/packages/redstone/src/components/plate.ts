/**
 * Pressure plates. The user stands in for whatever would be on the plate: a stone or wooden plate is held down or let
 * go, a weighted plate (1.12.2) is set to a level 0..15. A plate that is up goes down at once; a plate that is down
 * looks again every 20 game ticks (a weighted one every 10), so it comes up, or takes a new level, at its next look and
 * not the moment the user lets go. Down, it weakly powers every cell around it and strongly powers the block under it;
 * a change tells its six neighbours and the six of that block.
 */
import { DOWN } from '../orientation.js';
import type { ComponentHandler, World } from './handler.js';

const STONE = 70;
const WOODEN = 72;
const LIGHT_WEIGHTED = 147;
const HEAVY_WEIGHTED = 148;

const isWeighted = (id: number): boolean => id === LIGHT_WEIGHTED || id === HEAVY_WEIGHTED;
/** The power a plate gives: a weighted plate's meta is its level, a plain plate's meta says whether it is down. */
const strengthOf = (state: number): number => (isWeighted(state >> 4) ? state & 15 : (state & 15) !== 0 ? 15 : 0);

/** The plate looks at what is on it: it takes that state, and keeps looking while something is. */
function look(w: World, at: number, state: number): void {
  const id = state >> 4;
  const held = w.level(at);
  const meta = isWeighted(id) ? held : held > 0 ? 1 : 0;
  if ((state & 15) !== meta) {
    w.set(at, (id << 4) | meta);
    w.notifyAround(at, id);
    w.notifyAround(at + w.step[DOWN]!, id);
  }
  if (held > 0) w.schedule(at, isWeighted(id) ? 10 : 20);
}

export const plate: ComponentHandler = {
  name: 'Pressure plate',
  ids: (v) => (v === '1.12.2' ? [STONE, WOODEN, LIGHT_WEIGHTED, HEAVY_WEIGHTED] : [STONE, WOODEN]),
  normalize: (state) => (state >> 4) << 4,
  supported: (w, at) => w.profile.holdsPlate(w.cells[at + w.step[DOWN]!]!),
  weak: (_w, _at, state) => strengthOf(state),
  strong: (_w, _at, state, toward) => (toward === DOWN ? strengthOf(state) : 0),
  // A weighted plate that starts with a level on it (CellExtra.level) is down from the start.
  start: (w, at, state) => {
    if (w.level(at) > 0) look(w, at, state);
  },
  tick: (w, at, state) => {
    if (strengthOf(state) > 0) look(w, at, state);
  },
  inputKind: (state) => (isWeighted(state >> 4) ? 'level' : 'plate'),
  input: (w, at, state, e) => {
    if (isWeighted(state >> 4)) {
      if (e.kind !== 'level') return;
      w.setLevel(at, e.level);
    } else {
      if (e.kind !== 'hold') return;
      w.setLevel(at, e.on ? 15 : 0);
    }
    // Like something stepping on: a plate that is up notices at once, one that is down at its next look.
    if (strengthOf(state) === 0 && w.level(at) > 0) look(w, at, state);
  },
  power: (_w, _at, state) => strengthOf(state),
};
