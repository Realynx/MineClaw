/**
 * Clocks and pulse shapers. Times are game ticks (a repeater set to one redstone tick delays 2).
 *
 * A torch burns out on its eighth turn-off within 100 game ticks in b1.8.1 (60 in 1.12.2), so neither clock turns a
 * torch off more often than once every 20 game ticks.
 */
import { both, draw, input, output, repeater } from './draw.js';
import type { ComponentDef } from './types.js';

/** A torch whose own output comes back to its block through an 8 game tick repeater: 10 on, 10 off. */
const REPEATER_CLOCK = draw([['.....', '.#}++', '.8.+.', '.+++.']], [output('Y', 'east', [5, 0, 1])], {
  '8': repeater('north', 8),
});

/**
 * Five torches in a ring, each on a block the one before powers: an odd ring never settles. Every torch starts lit, so
 * a bare ring starts with several waves running round it, which in 1.12.2 burn torches out. The 8 game tick repeater
 * at (6,0,2) swallows any pulse or gap shorter than itself, so only one wave survives. 18 on, 18 off.
 */
const TORCH_CLOCK = draw(
  [['........', '.#}+#}+.', '.+....8.', '.+....#.', '.^....v+', '.#+{#++.', '........']],
  [output('Y', 'east', [8, 0, 4])],
  { '8': repeater('south', 8) },
);

/**
 * Y = I AND NOT (I delayed): the torch at (5,0,1) lights when the NOT torch before it goes out and goes out again when
 * the delayed copy arrives. The delay must be at least 6 game ticks (report 8.3): at 4 the delayed copy and the torch's
 * own tick fall on one game tick, the repeater runs first, and no pulse comes out. At 6 the pulse is 4 game ticks in
 * 1.12.2 but 2 in b1.8.1, where a torch that lights is updated by its own change and checks its block again 2 game ticks
 * later, on the tick the delayed copy arrives. At 8 it is 6 in both.
 */
const shaper = (delay: 6 | 8) =>
  draw([['......', 'E#}+#}', '.d..+.', '.++++.']], [input('I', 'west', [-1, 0, 1]), output('Y', 'east', [6, 0, 1])], {
    d: repeater('south', delay),
  });

/** Y = NOT I AND (I delayed): the torch at (4,0,1) lights when I falls and goes out when NOT (I delayed) catches up. */
const FALLING = draw(
  [['.....', 'E#+#}', '.S.+.', '.#}+.', '.....']],
  [input('I', 'west', [-1, 0, 1]), output('Y', 'east', [5, 0, 1])],
);

/**
 * A chain of 4 game tick repeaters, each tapped by a repeater into one output dust. Every copy overlaps the next as long
 * as the input pulse is at least 4 game ticks, so the output stays on 12 game ticks longer than the input.
 */
const EXTENDER = draw(
  [['E+e+e+e+.', '.S.S.S.S.', '.++++++++']],
  [input('I', 'west', [-1, 0, 0]), output('Y', 'east', [9, 0, 2])],
);

/** Waits `n` game ticks and expects Y. */
const at = (n: number, y: 0 | 1) => ({ wait: n, expect: { Y: y } });
/** Y read on each of the next `n` game ticks. */
const every = (n: number, y: 0 | 1) => Array.from({ length: n }, () => at(1, y));

export const TIMING: readonly ComponentDef[] = [
  {
    id: 'timing.repeater-clock',
    name: 'Repeater clock',
    category: 'timing',
    description: 'Runs by itself: Y is on for 10 game ticks and off for 10. Set the repeater longer to slow it down.',
    layouts: both(REPEATER_CLOCK),
    spec: { kind: 'clock', output: 'Y', periodTicks: 20, highTicks: 10 },
  },
  {
    id: 'timing.torch-clock',
    name: 'Five-torch clock',
    category: 'timing',
    description:
      'Five torches in a ring and one 8 game tick repeater: Y is on for 18 game ticks and off for 18. The repeater swallows the extra waves a ring of torches can start with.',
    layouts: both(TORCH_CLOCK),
    spec: { kind: 'clock', output: 'Y', periodTicks: 36, highTicks: 18 },
  },
  {
    id: 'timing.pulse-limiter',
    name: 'Pulse limiter',
    category: 'timing',
    description:
      'Shortens every pulse on I to at most 6 game ticks; a shorter pulse comes out as long as it went in. Y starts 5 game ticks after I rises.',
    layouts: both(shaper(8)),
    spec: {
      kind: 'sequence',
      minPulseTicks: 4,
      steps: [
        { set: { I: 0 }, ...at(20, 0) },
        { set: { I: 1 }, ...at(1, 0) },
        ...every(4, 0),
        ...every(6, 1),
        ...every(30, 0),
        { set: { I: 0 }, ...at(1, 0) },
        ...every(20, 0),
        { pulse: { port: 'I', ticks: 4 }, ...at(1, 0) },
        ...every(4, 1),
        ...every(20, 0),
      ],
    },
  },
  {
    id: 'timing.pulse-extender',
    name: 'Pulse extender',
    category: 'timing',
    description:
      'Keeps Y on 12 game ticks longer than each pulse on I, starting 3 game ticks after I rises. Pulses must be at least 4 game ticks.',
    layouts: both(EXTENDER),
    spec: {
      kind: 'sequence',
      minPulseTicks: 4,
      steps: [
        { set: { I: 0 }, ...at(30, 0) },
        { pulse: { port: 'I', ticks: 4 }, ...at(0, 1) },
        ...every(15, 1),
        ...every(10, 0),
        { pulse: { port: 'I', ticks: 10 }, ...at(0, 1) },
        ...every(15, 1),
        ...every(10, 0),
      ],
    },
  },
  {
    id: 'timing.rising-edge',
    name: 'Rising edge detector',
    category: 'timing',
    description:
      'A pulse on Y 5 game ticks after I turns on, nothing when it turns off. The pulse is 4 game ticks in 1.12.2 and 2 in b1.8.1, where a torch that lights checks its block again 2 game ticks later.',
    layouts: both(shaper(6)),
    spec: {
      kind: 'sequence',
      minPulseTicks: 4,
      steps: [
        { set: { I: 0 }, ...at(20, 0) },
        { set: { I: 1 }, ...at(1, 0) },
        ...every(4, 0),
        ...every(2, 1),
        at(3, 0),
        ...every(20, 0),
        { set: { I: 0 }, ...at(1, 0) },
        ...every(30, 0),
        { set: { I: 1 }, ...at(1, 0) },
        ...every(4, 0),
        ...every(2, 1),
        at(3, 0),
        ...every(10, 0),
      ],
    },
  },
  {
    id: 'timing.falling-edge',
    name: 'Falling edge detector',
    category: 'timing',
    description:
      'A pulse on Y 3 game ticks after I turns off, nothing when it turns on. I must have been on for at least 6 game ticks. The pulse is 4 game ticks in 1.12.2 and 2 in b1.8.1.',
    layouts: both(FALLING),
    spec: {
      kind: 'sequence',
      minPulseTicks: 6,
      steps: [
        { set: { I: 0 }, ...at(20, 0) },
        { set: { I: 1 }, ...at(1, 0) },
        ...every(30, 0),
        { set: { I: 0 }, ...at(1, 0) },
        ...every(2, 0),
        ...every(2, 1),
        at(3, 0),
        ...every(20, 0),
        { pulse: { port: 'I', ticks: 6 }, ...at(1, 0) },
        ...every(2, 0),
        ...every(2, 1),
        at(3, 0),
        ...every(20, 0),
      ],
    },
  },
];
