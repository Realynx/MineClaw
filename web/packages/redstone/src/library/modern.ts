/**
 * Components built on what only 1.12.2 has: repeater locking and the comparator. A repeater is locked while a powered
 * repeater or comparator points into its side, and a locked repeater ignores both its updates and its ticks. A
 * comparator in compare mode with nothing at its sides passes the level it reads on unchanged, 2 game ticks later, so
 * these layouts use comparators where a wire must carry a signal strength: dust would lose a level per cell.
 */
import { draw, input, output } from './draw.js';
import type { ComponentDef } from './types.js';

const COMPARATOR = 149;
const SUBTRACT = 4;

/**
 * Comparators by the side they read from, in compare mode (`<` reads east, `>` reads west, `d` reads north, `u` reads
 * south), and one in subtract mode that reads west (`s`) or north (`S`).
 */
const COMPARATORS = {
  '>': [COMPARATOR, 1],
  '<': [COMPARATOR, 3],
  d: [COMPARATOR, 2],
  u: [COMPARATOR, 0],
  s: [COMPARATOR, 1 | SUBTRACT],
  S: [COMPARATOR, 2 | SUBTRACT],
} as const;

/**
 * The data repeater at (1,0,0) passes D on to Q while it is free. The repeater at (1,0,1) points into its side: while H
 * is on it locks the data repeater, which then keeps Q whatever D does. The dust at (0,0,0) keeps the D port two cells
 * from the H port, so the wires on the two never touch.
 */
const LOCK_CELL = draw(
  [['+E', '.N']],
  [input('D', 'west', [-1, 0, 0]), input('H', 'south', [1, 0, 2]), output('Q', 'east', [2, 0, 0])],
);

/**
 * Report 8.3's T flip-flop: master repeater M (2,0,2) and slave repeater S (4,0,2), each locked from the south. While T
 * is low the torch at (4,0,4) locks S and M follows NOT Q, which the torch on KN (5,0,2) feeds back round the north
 * edge. When T rises M's lock closes first and the T torch lets S go 2 game ticks later, so S takes M's held value and
 * Q flips once. When T falls S's lock (priority -3, it faces another repeater) closes on the same game tick M opens.
 * M only takes the new NOT Q after its lock has opened and the torch on KN has switched, and the next rise must not
 * close the lock again on that very tick: rises have to be at least 9 game ticks apart and T has to stay off for at
 * least 3 (report 8.3 said 4 on and 4 off is enough; the engine shows that loses every second pulse).
 * The input repeater at (2,0,6) keeps a bent wire on T from giving the dust at (3,0,5) a second link.
 */
const LOCK_T = draw(
  [['++++++.', '+....^.', '++E+E#+', '..N.N..', '..+.^..', '..++#..', '..N....']],
  [input('T', 'south', [2, 0, 7]), output('Q', 'east', [7, 0, 2])],
);

/**
 * Y = A - B, never below 0. The main comparator at (1,0,1) subtracts what its north side reads from its rear. The two
 * comparators in front of it only pass A and B on unchanged, which keeps the ports apart without losing a level.
 */
const SUBTRACTOR = draw(
  [['.d', '>s']],
  [input('A', 'west', [-1, 0, 1]), input('B', 'north', [1, 0, -1]), output('Y', 'east', [2, 0, 1])],
  COMPARATORS,
);

/** The subtractor with a repeater after it: Y is on exactly when A - B is above 0. */
const IS_STRONGER = draw(
  [['.d.', '>sE']],
  [input('A', 'west', [-1, 0, 1]), input('B', 'north', [1, 0, -1]), output('Y', 'east', [3, 0, 1])],
  COMPARATORS,
);

/**
 * Four comparators in a ring, joined at the corners by single dust cells (a comparator reads bare dust at its level, so a
 * corner loses nothing): whatever level goes round keeps going round. D comes in through the comparator at (0,0,0) onto
 * the corner (0,0,1), whose dust takes the stronger of D and the ring, so the ring keeps the strongest D it has seen. R
 * locks the subtracting comparator at (2,0,2) to 0 from the side through the repeater at (3,0,2), which empties the
 * ring. The comparator at (2,0,4) reads the ring for Y.
 */
const HOLD = draw(
  [['d...', '+>+.', 'u.SW', '+<+.', '..d.']],
  [input('D', 'north', [0, 0, -1]), input('R', 'east', [4, 0, 2]), output('Y', 'south', [2, 0, 5])],
  COMPARATORS,
);

/** The levels the analog tables walk: off, the weakest, two between and the strongest. */
const LEVELS = [0, 1, 6, 11, 15] as const;
const table = (f: (a: number, b: number) => number): Array<readonly [number[], number[]]> =>
  LEVELS.flatMap((a) => LEVELS.map((b) => [[a, b], [f(a, b)]] as const));

const pulse = (port: string, ticks = 4): { port: string; ticks: number } => ({ port, ticks });

export const MODERN: readonly ComponentDef[] = [
  {
    id: 'memory.lock-cell',
    name: 'Repeater-lock memory cell',
    category: 'memory',
    description:
      'While H is off, Q follows D 2 game ticks later; while H is on, Q keeps its value whatever D does. D may change on the very tick H turns on (Q keeps the old value) or off (Q takes the new one). 1.12.2 only: b1.8.1 repeaters cannot be locked.',
    layouts: { '1.12.2': LOCK_CELL },
    spec: {
      kind: 'sequence',
      minPulseTicks: 4,
      steps: [
        { set: { D: 1 }, wait: 6, expect: { Q: 1 } },
        { set: { D: 0 }, wait: 6, expect: { Q: 0 } },
        { set: { D: 1 }, wait: 6, expect: { Q: 1 } },
        { set: { H: 1 }, wait: 6, expect: { Q: 1 } },
        { set: { D: 0 }, wait: 6, expect: { Q: 1 } },
        { set: { H: 0 }, wait: 6, expect: { Q: 0 } },
        { set: { H: 1 }, wait: 6, expect: { Q: 0 } },
        { set: { D: 1 }, wait: 6, expect: { Q: 0 } },
        { set: { D: 0 }, wait: 6, expect: { Q: 0 } },
        { set: { D: 1 }, wait: 6, expect: { Q: 0 } },
        { set: { H: 0 }, wait: 6, expect: { Q: 1 } },
        // D changes on the tick H turns on: the old value is kept.
        { set: { D: 0, H: 1 }, wait: 8, expect: { Q: 1 } },
        // D changes on the tick H turns off: the new value is taken.
        { set: { D: 1 }, wait: 6, expect: { Q: 1 } },
        { set: { D: 0, H: 0 }, wait: 8, expect: { Q: 0 } },
        // D changes with a 4 game tick H pulse: Q takes it once H has let go.
        { set: { D: 1 }, pulse: pulse('H'), wait: 6, expect: { Q: 1 } },
      ],
    },
  },
  {
    id: 'memory.lock-t-flip-flop',
    name: 'Repeater-lock T flip-flop',
    category: 'memory',
    description:
      'Q flips each time T turns on, 8 game ticks later; turning T off does nothing. T must stay on for at least 4 game ticks and off for at least 4, and a pulse may start no sooner than 10 game ticks after the one before. It powers up with Q off in the simulator; in the world the order the parts are placed in decides. 1.12.2 only: b1.8.1 repeaters cannot be locked.',
    layouts: { '1.12.2': LOCK_T },
    spec: {
      kind: 'sequence',
      minPulseTicks: 4,
      steps: [
        { pulse: pulse('T'), wait: 12, expect: { Q: 1 } },
        { pulse: pulse('T'), wait: 12, expect: { Q: 0 } },
        { pulse: pulse('T'), wait: 12, expect: { Q: 1 } },
        { pulse: pulse('T'), wait: 12, expect: { Q: 0 } },
        // Pulses 10 game ticks apart still count one by one, whether 4 on and 6 off or 6 on and 4 off.
        { pulse: pulse('T'), wait: 6 },
        { pulse: pulse('T', 6), wait: 4 },
        { pulse: pulse('T'), wait: 12, expect: { Q: 1 } },
        { set: { T: 1 }, wait: 12, expect: { Q: 0 } },
        { set: { T: 0 }, wait: 12, expect: { Q: 0 } },
        { pulse: pulse('T', 10), wait: 12, expect: { Q: 1 } },
      ],
    },
  },
  {
    id: 'arithmetic.subtractor',
    name: 'Comparator subtractor',
    category: 'arithmetic',
    description:
      'Y = A - B as signal strengths, never below 0, 4 game ticks after an input changes. 1.12.2 only: b1.8.1 has no comparator.',
    layouts: { '1.12.2': SUBTRACTOR },
    spec: {
      kind: 'truth',
      inputs: ['A', 'B'],
      outputs: ['Y'],
      rows: table((a, b) => Math.max(a - b, 0)),
      settleTicks: 6,
      analog: ['A', 'B', 'Y'],
    },
  },
  {
    id: 'arithmetic.is-stronger',
    name: 'Is stronger',
    category: 'arithmetic',
    description:
      'Y is on while signal A is stronger than signal B, 6 game ticks after an input changes. 1.12.2 only: b1.8.1 has no comparator.',
    layouts: { '1.12.2': IS_STRONGER },
    spec: {
      kind: 'truth',
      inputs: ['A', 'B'],
      outputs: ['Y'],
      rows: table((a, b) => (a > b ? 1 : 0)),
      settleTicks: 8,
      analog: ['A', 'B'],
    },
  },
  {
    id: 'memory.signal-hold',
    name: 'Signal strength hold',
    category: 'memory',
    description:
      'Y keeps the strongest level D has had since the last pulse on R, which empties it. D must hold a new level for at least 8 game ticks (one turn of the ring), and an R pulse must last at least 8. Y follows 8 game ticks after D. 1.12.2 only: b1.8.1 has no comparator.',
    layouts: { '1.12.2': HOLD },
    spec: {
      kind: 'sequence',
      minPulseTicks: 8,
      analog: ['D', 'Y'],
      steps: [
        { pulse: pulse('R', 10), wait: 12, expect: { Y: 0 } },
        { set: { D: 9 }, wait: 12, expect: { Y: 9 } },
        { set: { D: 0 }, wait: 12, expect: { Y: 9 } },
        { set: { D: 5 }, wait: 12, expect: { Y: 9 } },
        { set: { D: 0 }, wait: 16, expect: { Y: 9 } },
        { set: { D: 13 }, wait: 12, expect: { Y: 13 } },
        { set: { D: 0 }, wait: 12, expect: { Y: 13 } },
        { pulse: pulse('R', 10), wait: 12, expect: { Y: 0 } },
        { set: { D: 1 }, wait: 12, expect: { Y: 1 } },
        { set: { D: 0 }, wait: 12, expect: { Y: 1 } },
        { set: { D: 15 }, wait: 12, expect: { Y: 15 } },
        { set: { D: 0 }, pulse: pulse('R', 8), wait: 12, expect: { Y: 0 } },
        // R while D is held: the ring fills again with D once R lets go.
        { set: { D: 7 }, pulse: pulse('R', 8), wait: 12, expect: { Y: 7 } },
        { set: { D: 0 }, wait: 12, expect: { Y: 7 } },
      ],
    },
  },
];
