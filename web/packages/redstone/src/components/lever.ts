/**
 * The lever: on or off by hand, with no delay. On, it weakly powers every cell around it and strongly powers the block
 * it sits on. A flip tells its six neighbours and the six neighbours of that block. b1.8.1 has it on walls and floors;
 * 1.12.2 also under a block.
 */
import { leverSupport, opposite } from '../orientation.js';
import type { ComponentHandler } from './handler.js';

const LEVER = 69;
const ON = 8;

export const lever: ComponentHandler = {
  name: 'Lever',
  ids: () => [LEVER],
  valid: (state, v) => v === '1.12.2' || ((state & 7) >= 1 && (state & 7) <= 6),
  supported: (w, at, state) => {
    const dir = leverSupport(state & 15);
    return w.profile.holdsOnFace(w.cells[at + w.step[dir]!]!, opposite(dir));
  },
  weak: (_w, _at, state) => ((state & ON) !== 0 ? 15 : 0),
  strong: (_w, _at, state, toward) => ((state & ON) !== 0 && toward === leverSupport(state & 15) ? 15 : 0),
  inputKind: () => 'lever',
  input: (w, at, state, e) => {
    if (e.kind !== 'toggle') return;
    w.set(at, state ^ ON);
    w.notifyAround(at, LEVER);
    w.notifyAround(at + w.step[leverSupport(state & 15)]!, LEVER);
  },
  power: (_w, _at, state) => ((state & ON) !== 0 ? 15 : 0),
};
