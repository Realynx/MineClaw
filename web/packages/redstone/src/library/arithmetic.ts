/**
 * Adders and the multiplexer. Any one-level XOR needs a crossing (report 8.3); these cross with a bridge: the wire that
 * crosses climbs onto two blocks and a third block that sits on the repeater it passes over. A repeater does not power
 * up, and dust ignores the power other dust gives a block, so the two signals stay apart.
 */
import { both, compose, draw, input, output } from './draw.js';
import type { ComponentDef } from './types.js';

/**
 * Half adder, C = A AND B and S = NOT (NOR(A, B) OR C).
 * - KN (4,0,4) is powered by A and B: its torch below KS gives NOR.
 * - KC (4,0,6) is powered by NOT A and NOT B: its torch (4,0,7) gives C.
 * - C runs east, crosses over B's repeater at (9,0,4) and comes back to KS (4,0,1) from the east; S is KS's torch.
 * Both outputs leave on the north face, so a full adder can chain two of these.
 */
const HALF_ADDER = draw(
  [
    [
      '....^..+...',
      '....#+++++.',
      '....+....+.',
      '....^....#.',
      'E#++#++#WWW',
      '.v.....v.#.',
      '.+++#+++.+.',
      '....v....+.',
      '....+++E++.',
    ],
    [
      '...........',
      '...........',
      '...........',
      '.........+.',
      '.........#.',
      '.........+.',
      '...........',
      '...........',
      '...........',
    ],
    [
      '...........',
      '...........',
      '...........',
      '...........',
      '.........+.',
      '...........',
      '...........',
      '...........',
      '...........',
    ],
  ],
  [
    input('A', 'west', [-1, 0, 4]),
    input('B', 'east', [11, 0, 4]),
    output('S', 'north', [4, 0, -1]),
    output('C', 'north', [7, 0, -1]),
  ],
);

const column = (x: number, z0: number, z1: number, ch = '+') =>
  Array.from({ length: z1 - z0 + 1 }, (_, i) => [x, 0, z0 + i, ch] as const);
const row = (z: number, x0: number, x1: number, ch = '+') =>
  Array.from({ length: x1 - x0 + 1 }, (_, i) => [x0 + i, 0, z, ch] as const);

/**
 * Full adder from two half adders. The first (A, B) sits at the bottom right; its S runs up into the second one's east
 * input and Cin into its west input. The first one's carry runs up the east side, past the second adder, and joins the
 * second carry behind a repeater each, so neither carry feeds back into the adder it came from.
 */
const FULL_ADDER = compose(
  [19, 3, 21],
  [
    { layout: HALF_ADDER, at: [0, 0, 3] },
    { layout: HALF_ADDER, at: [8, 0, 12] },
  ],
  [
    // S of the second adder.
    ...column(4, 0, 2),
    // Its carry, then the first carry's wire along z 1.
    [7, 0, 2, 'N'],
    ...column(7, 0, 1),
    ...row(1, 8, 11),
    [12, 0, 1, 'W'],
    ...row(1, 13, 15),
    ...column(15, 2, 10),
    [15, 0, 11, 'N'],
    // S of the first adder into the second one's east input.
    ...column(12, 7, 11),
    [11, 0, 7, '+'],
    // A into the first adder.
    [0, 0, 16, 'E'],
    ...row(16, 1, 7),
  ],
  [
    input('Cin', 'west', [-1, 0, 7]),
    input('A', 'west', [-1, 0, 16]),
    input('B', 'east', [19, 0, 16]),
    output('S', 'north', [4, 0, -1]),
    output('Cout', 'north', [7, 0, -1]),
  ],
);

/**
 * 2:1 multiplexer, Y = Sel ? B : A. KA (4,0,3) holds NOT A or Sel, KB (8,0,3) holds NOT B or NOT Sel; their torches meet
 * on Y. Sel reaches KA by dust from the block its repeater powers, and NOT Sel is the torch on that block.
 */
const MUX = draw(
  [['......S......', '....++#}+....', '....+...+....', 'E#}+#}+{#+{#W', '......+......']],
  [
    input('Sel', 'north', [6, 0, -1]),
    input('A', 'west', [-1, 0, 3]),
    input('B', 'east', [13, 0, 3]),
    output('Y', 'south', [6, 0, 5]),
  ],
);

/** Every input combination in counting order, with the outputs `f` gives. */
function table(inputs: string[], outputs: string[], settleTicks: number, f: (...x: number[]) => number[]) {
  const rows: Array<readonly [number[], number[]]> = [];
  for (let n = 0; n < 1 << inputs.length; n++) {
    const ins = inputs.map((_, i) => (n >> (inputs.length - 1 - i)) & 1);
    rows.push([ins, f(...ins)]);
  }
  return { kind: 'truth' as const, inputs, outputs, rows, settleTicks };
}

export const ARITHMETIC: readonly ComponentDef[] = [
  {
    id: 'arithmetic.half-adder',
    name: 'Half adder',
    category: 'arithmetic',
    description: 'Adds two bits: S = A XOR B, C = A AND B. Both outputs on the north face.',
    layouts: both(HALF_ADDER),
    spec: table(['A', 'B'], ['S', 'C'], 16, (a, b) => [a ^ b, a & b]),
  },
  {
    id: 'arithmetic.full-adder',
    name: 'Full adder',
    category: 'arithmetic',
    description: 'Adds three bits: S = A XOR B XOR Cin, Cout = at least two of them. Two half adders and an OR.',
    layouts: both(FULL_ADDER),
    spec: table(['A', 'B', 'Cin'], ['S', 'Cout'], 32, (a, b, c) => [a ^ b ^ c, (a & b) | (a & c) | (b & c)]),
  },
  {
    id: 'routing.mux2',
    name: '2:1 multiplexer',
    category: 'routing',
    description: 'Y follows A while Sel is off and B while Sel is on.',
    layouts: both(MUX),
    spec: table(['Sel', 'A', 'B'], ['Y'], 8, (s, a, b) => [s ? b : a]),
  },
];
