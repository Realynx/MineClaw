/**
 * Logic gates. Every input starts with a repeater on the box's face, which reads bare dust, so a wire may reach a port
 * from any side (rule L2). A torch is the only inverter; a block between two signals is an OR of both.
 */
import { both, draw, input, output } from './draw.js';
import type { ComponentDef } from './types.js';

/** The truth table of a one-output gate, every input combination in counting order. */
function truth(inputs: string[], settleTicks: number, f: (...x: number[]) => number): ComponentDef['spec'] {
  const rows: Array<readonly [number[], number[]]> = [];
  for (let n = 0; n < 1 << inputs.length; n++) {
    const ins = inputs.map((_, i) => (n >> (inputs.length - 1 - i)) & 1);
    rows.push([ins, [f(...ins)]]);
  }
  return { kind: 'truth', inputs, outputs: ['Y'], rows, settleTicks };
}

/** A repeater into a block with a torch on its far side. */
const NOT = draw([['...', 'E#}', '...']], [input('A', 'west', [-1, 0, 1]), output('Y', 'east', [3, 0, 1])]);

/** Two repeaters into one dust. */
const OR = draw(
  [['E+W', '.+.']],
  [input('A', 'west', [-1, 0, 0]), input('B', 'east', [3, 0, 0]), output('Y', 'south', [1, 0, 2])],
);

/** Two repeaters into one block, with a torch on it. The empty row keeps the powered block off the north face (L6). */
const NOR = draw(
  [['...', 'E#W', '.v.']],
  [input('A', 'west', [-1, 0, 1]), input('B', 'east', [3, 0, 1]), output('Y', 'south', [1, 0, 3])],
);

/** NOT A and NOT B on one dust: NAND. The dust points into a block whose torch inverts it again: AND. */
const AND = draw(
  [['.......', 'E#}+{#W', '...+...', '...#...', '...v...']],
  [input('A', 'west', [-1, 0, 1]), input('B', 'east', [7, 0, 1]), output('Y', 'south', [3, 0, 5])],
);

const NAND = draw(
  [['.......', 'E#}+{#W', '...+...']],
  [input('A', 'west', [-1, 0, 1]), input('B', 'east', [7, 0, 1]), output('Y', 'south', [3, 0, 3])],
);

/**
 * Report 8.3's XOR: P = A AND NOT B and Q = B AND NOT A on one dust. Block KP is powered from the side by NOT A and from
 * the top by B, KQ the other way round; the A run passes north, the B run south, and the output leaves over the B run.
 * The stone caps stop the dust beside KP and KQ from climbing onto the dust on top of them.
 */
const XOR_LAYERS = [
  [
    '.++++++++++......',
    '.+........+......',
    'E++#}+#}+{#+{#++W',
    '......+.#......+.',
    '......++++++++++.',
    '........#........',
    '........+........',
  ],
  [
    '.................',
    '.................',
    '.....#+...+#.....',
    '........+........',
    '........#........',
    '........+........',
    '.................',
  ],
  [
    '.................',
    '.................',
    '.................',
    '.................',
    '........+........',
    '.................',
    '.................',
  ],
];

const XOR = draw(XOR_LAYERS, [
  input('A', 'west', [-1, 0, 2]),
  input('B', 'east', [17, 0, 2]),
  output('Y', 'south', [8, 0, 7]),
]);

/** The XOR with its output dust pointing into one more block, and a torch on that block. */
const blank = '.................';
const XNOR = draw(
  [
    [...XOR_LAYERS[0]!, '........#........', '........v........'],
    [...XOR_LAYERS[1]!, blank, blank],
    [...XOR_LAYERS[2]!, blank, blank],
  ],
  [input('A', 'west', [-1, 0, 2]), input('B', 'east', [17, 0, 2]), output('Y', 'south', [8, 0, 9])],
);

export const LOGIC: readonly ComponentDef[] = [
  {
    id: 'gate.not',
    name: 'NOT',
    category: 'logic',
    description: 'Inverter: Y is on while A is off. A torch on the block a repeater powers.',
    layouts: both(NOT),
    spec: truth(['A'], 4, (a) => 1 - a),
  },
  {
    id: 'gate.or',
    name: 'OR',
    category: 'logic',
    description: 'Y is on while A or B is on. Two repeaters into one dust, so neither input feeds the other.',
    layouts: both(OR),
    spec: truth(['A', 'B'], 2, (a, b) => a | b),
  },
  {
    id: 'gate.nor',
    name: 'NOR',
    category: 'logic',
    description: 'Y is on while A and B are both off. Two repeaters into one block with a torch on it.',
    layouts: both(NOR),
    spec: truth(['A', 'B'], 4, (a, b) => 1 - (a | b)),
  },
  {
    id: 'gate.and',
    name: 'AND',
    category: 'logic',
    description: 'Y is on while A and B are both on: NOT (NOT A OR NOT B), three torches.',
    layouts: both(AND),
    spec: truth(['A', 'B'], 6, (a, b) => a & b),
  },
  {
    id: 'gate.nand',
    name: 'NAND',
    category: 'logic',
    description: 'Y is off only while A and B are both on: NOT A OR NOT B, two torches on one dust.',
    layouts: both(NAND),
    spec: truth(['A', 'B'], 4, (a, b) => 1 - (a & b)),
  },
  {
    id: 'gate.xor',
    name: 'XOR',
    category: 'logic',
    description:
      'Y is on while exactly one of A and B is on: (A AND NOT B) OR (B AND NOT A). The output leaves over the B wire on a bridge.',
    layouts: both(XOR),
    spec: truth(['A', 'B'], 6, (a, b) => a ^ b),
  },
  {
    id: 'gate.xnor',
    name: 'XNOR',
    category: 'logic',
    description: 'Y is on while A and B are equal: the XOR with a torch after it.',
    layouts: both(XNOR),
    spec: truth(['A', 'B'], 8, (a, b) => 1 - (a ^ b)),
  },
];
