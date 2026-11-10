/**
 * Memory. A latch powers up in either state (report 4.1, simplification 9), so every sequence starts by resetting it.
 */
import { both, draw, input, output, repeater } from './draw.js';
import type { ComponentDef } from './types.js';

/**
 * Report 8.3's RS NOR latch: two torches, each on a block the other one's dust powers. The dust beside each torch keeps
 * exactly one link so it points into the other block, which is why Q and NOT Q are tapped beside the torches.
 */
const RS = draw(
  [['..+...', 'E#}+#W', '.+..v.', '.++++.', '..+...']],
  [
    input('R', 'west', [-1, 0, 1]),
    input('S', 'east', [6, 0, 1]),
    output('Q', 'north', [2, 0, -1]),
    output('NQ', 'south', [2, 0, 5]),
  ],
);

/**
 * A gated D latch is a 2:1 multiplexer whose unselected input is its own output: Q = E ? D : Q. Block KA (4,0,3) holds
 * Q while E is off: NOT Q comes from the torch on (1,0,3), which reads Q through the repeater at (1,0,4). E reaches KA by
 * dust from the block at (6,0,1), not by repeaters, so when E falls KA lets go 2 game ticks before the torch on KB
 * (8,0,3) does and Q never drops for a tick.
 */
const D = draw(
  [['......S......', '....++#}+....', '....+...+....', '.#}+#}+{#+{#W', '.N....+......', '+++++++......']],
  [input('E', 'north', [6, 0, -1]), input('D', 'east', [13, 0, 3]), output('Q', 'west', [-1, 0, 5])],
);

/**
 * A T flip-flop from torches and repeaters only, so it works without repeater locks: the D latch's hold half, with its
 * set half fed by Q itself. Q = (NOT E AND Q) OR (E AND NOT Qd AND NOT R), where Qd is Q 12 game ticks late.
 * - T's rising edge comes out of the 8 game tick pulse limiter (rows 1 to 3) and the 8 game tick repeater at (6,0,4)
 *   stretches it to an E pulse of at least 8 game ticks on block EB (6,0,5).
 * - E powers KA (4,0,7) by dust, so the hold torch on it lets go of Q; the torch on EB (NOT E) stops powering KB
 *   (8,0,7), so the set torch on KB lights unless Qd or R powers KB. Qd comes from Q through the repeaters at (7,0,9)
 *   and (8,0,8), 12 game ticks in all: longer than the E pulse, so Q has flipped once and E is over before Qd catches up.
 * - The NOT Q torch on (1,0,7) keeps KA powered while Q is off, as in the D latch.
 * - R powers E through the repeater at (7,0,2) and KB through two paths: the 8 game tick repeater at (9,0,7) reaches KB
 *   before NOT E goes out, and the 6 and 8 game tick repeaters at (9,0,9) and (8,0,8) hold KB until NOT E is back. Without
 *   the slow path a reset while Q is already off flashes Q on; without the fast one KB goes dark for 2 game ticks and only
 *   the order inside one game tick keeps the set torch out.
 * - The row of air at z 10 keeps the dust at (8,0,9), which points south in b1.8.1, off the box's face.
 */
const T_FLIP_FLOP = draw(
  [
    [
      '............',
      'E#}+#}+.....',
      '.d..+.+W++++',
      '.++++.+....+',
      '......x....+',
      '....++#}+..+',
      '....+...+..+',
      '.#}+#}+{#f++',
      '.N....+.q..+',
      '+++++++e+r++',
      '............',
    ],
  ],
  [input('T', 'west', [-1, 0, 1]), input('R', 'east', [12, 0, 6]), output('Q', 'west', [-1, 0, 9])],
  {
    d: repeater('south', 8),
    x: repeater('south', 8),
    q: repeater('north', 8),
    f: repeater('west', 8),
    r: repeater('west', 6),
  },
);

const pulse = (port: string, ticks = 4): { port: string; ticks: number } => ({ port, ticks });

export const MEMORY: readonly ComponentDef[] = [
  {
    id: 'memory.rs-latch',
    name: 'RS latch',
    category: 'memory',
    description:
      'A pulse on S turns Q on, a pulse on R turns it off; NQ is always the opposite. Pulses must be at least 4 game ticks: in 1.12.2 a 2 game tick pulse ends before the torch it should turn off has ticked.',
    layouts: both(RS),
    spec: {
      kind: 'sequence',
      minPulseTicks: 4,
      steps: [
        { pulse: pulse('R'), wait: 10, expect: { Q: 0, NQ: 1 } },
        { pulse: pulse('S'), wait: 10, expect: { Q: 1, NQ: 0 } },
        { pulse: pulse('S'), wait: 10, expect: { Q: 1, NQ: 0 } },
        { pulse: pulse('R'), wait: 10, expect: { Q: 0, NQ: 1 } },
        { pulse: pulse('R'), wait: 10, expect: { Q: 0, NQ: 1 } },
        { pulse: pulse('S'), wait: 10, expect: { Q: 1, NQ: 0 } },
        { set: { S: 1 }, wait: 10, expect: { Q: 1, NQ: 0 } },
        { set: { S: 0 }, wait: 10, expect: { Q: 1, NQ: 0 } },
        { set: { R: 1 }, wait: 10, expect: { Q: 0, NQ: 1 } },
        { set: { R: 0 }, wait: 10, expect: { Q: 0, NQ: 1 } },
      ],
    },
  },
  {
    id: 'memory.d-latch',
    name: 'D latch',
    category: 'memory',
    description:
      'While E is on, Q follows D; when E turns off, Q keeps D as it was. D may change on the very tick E turns on or off. An E pulse must last at least 4 game ticks.',
    layouts: both(D),
    spec: {
      kind: 'sequence',
      minPulseTicks: 4,
      steps: [
        { set: { D: 0 }, wait: 10 },
        { pulse: pulse('E'), wait: 12, expect: { Q: 0 } },
        { set: { D: 1 }, wait: 12, expect: { Q: 0 } },
        { pulse: pulse('E'), wait: 12, expect: { Q: 1 } },
        { set: { D: 0 }, wait: 12, expect: { Q: 1 } },
        { pulse: pulse('E'), wait: 12, expect: { Q: 0 } },
        { pulse: pulse('E'), wait: 12, expect: { Q: 0 } },
        // D changes on the tick E turns on, and again on the tick E turns off.
        { set: { D: 1 }, pulse: pulse('E'), wait: 0 },
        { set: { D: 0 }, wait: 12, expect: { Q: 1 } },
        { pulse: pulse('E'), wait: 0 },
        { set: { D: 1 }, wait: 12, expect: { Q: 0 } },
        // Held open, Q follows D.
        { set: { E: 1 }, wait: 12, expect: { Q: 1 } },
        { set: { D: 0 }, wait: 12, expect: { Q: 0 } },
        { set: { D: 1 }, wait: 12, expect: { Q: 1 } },
        { set: { E: 0 }, wait: 12, expect: { Q: 1 } },
        { set: { D: 0 }, wait: 12, expect: { Q: 1 } },
      ],
    },
  },
  {
    id: 'memory.t-flip-flop',
    name: 'T flip-flop',
    category: 'memory',
    description:
      'Q flips each time T turns on: it turns on 18 game ticks after the rise and off 16 after; turning T off does nothing. A pulse on R turns Q off 12 game ticks later, and a T pulse that comes and goes while R is on is ignored. T must stay on for at least 4 game ticks and off for at least 8, a rise may come no sooner than 18 game ticks after the one before, and T may rise 8 game ticks after R turns off. Torches and repeaters only, so it works in both versions.',
    layouts: both(T_FLIP_FLOP),
    spec: {
      kind: 'sequence',
      minPulseTicks: 4,
      steps: [
        { pulse: pulse('R'), wait: 30, expect: { Q: 0 } },
        { pulse: pulse('T'), wait: 20, expect: { Q: 1 } },
        { pulse: pulse('T'), wait: 20, expect: { Q: 0 } },
        { pulse: pulse('T'), wait: 20, expect: { Q: 1 } },
        // R turns Q off, and leaves an off Q off.
        { pulse: pulse('R'), wait: 30, expect: { Q: 0 } },
        { pulse: pulse('R'), wait: 30, expect: { Q: 0 } },
        { pulse: pulse('T', 10), wait: 20, expect: { Q: 1 } },
        // T held on flips Q once; turning it off does nothing.
        { set: { T: 1 }, wait: 40, expect: { Q: 0 } },
        { set: { T: 0 }, wait: 40, expect: { Q: 0 } },
        // Rises 18 game ticks apart, 4 on and 14 off, then 10 on and 8 off: each one counts.
        { pulse: pulse('T'), wait: 14 },
        { pulse: pulse('T'), wait: 14 },
        { pulse: pulse('T'), wait: 30, expect: { Q: 1 } },
        { pulse: pulse('T', 10), wait: 8 },
        { pulse: pulse('T', 10), wait: 30, expect: { Q: 1 } },
        // A T pulse while R is held is ignored.
        { set: { R: 1 }, wait: 20, expect: { Q: 0 } },
        { pulse: pulse('T'), wait: 20, expect: { Q: 0 } },
        { set: { R: 0 }, wait: 30, expect: { Q: 0 } },
        // T may rise 8 game ticks after R turns off.
        { pulse: pulse('T'), wait: 20, expect: { Q: 1 } },
        { pulse: pulse('R'), wait: 8 },
        { pulse: pulse('T'), wait: 30, expect: { Q: 1 } },
      ],
    },
  },
];
