/** What the engine reports about a design it cannot simulate faithfully: floating parts, parts a version lacks, parts not
 * modelled yet, and a tick that never settles. */
import { describe, expect, it } from 'vitest';
import type { Diagnostic } from '../src/api.js';
import { RedstoneSimulation } from '../src/simulation.js';
import {
  COMPARATOR,
  DUST,
  FLOOR,
  LAMP,
  LEVER,
  ON,
  POWERED_RAIL,
  READS_NORTH,
  READS_WEST,
  REPEATER,
  STONE,
  STONE_BUTTON,
  TORCH,
  TORCH_ON_FLOOR,
  TORCH_ON_WEST,
  VERSIONS,
  WOODEN_BUTTON,
  build,
  cell,
  toggle,
} from './circuit.js';

const brief = (list: readonly Diagnostic[]) => list.map((d) => [d.code, d.at.join()]);

describe.each(VERSIONS)('diagnostics in %s', (v) => {
  it('reports dust, a torch and a repeater with nothing under them, and leaves them out', () => {
    const sim = new RedstoneSimulation({
      version: v,
      cells: [
        cell([0, 0, 0], LEVER, FLOOR | ON),
        cell([1, 0, 0], DUST),
        cell([2, 0, 0], TORCH, TORCH_ON_FLOOR),
        cell([3, 0, 0], REPEATER, READS_WEST),
        cell([4, 0, 0], TORCH, TORCH_ON_WEST),
      ],
    });
    // Without ground the floor lever floats too; the wall torch hangs on a repeater, which is no block to hang on.
    expect(brief(sim.diagnostics())).toEqual([
      ['floating', '0,0,0'],
      ['floating', '1,0,0'],
      ['floating', '2,0,0'],
      ['floating', '3,0,0'],
      ['floating', '4,0,0'],
    ]);
    expect(sim.diagnostics()[1]!.text).toMatch(/^Redstone dust has nothing to hold it/);
    // Left out: shown as placed, giving and taking no power, never ticking.
    expect([sim.powerAt([0, 0, 0]), sim.powerAt([1, 0, 0]), sim.powerAt([2, 0, 0])]).toEqual([0, 0, 0]);
    expect(sim.stateAt([2, 0, 0])).toBe((TORCH << 4) | TORCH_ON_FLOOR);
    expect(sim.displayCells()).toEqual([]);
    expect(sim.idle).toBe(true);

    // The same parts on the plot's ground are fine.
    expect(build(v, [cell([1, 0, 0], DUST), cell([2, 0, 0], TORCH, TORCH_ON_FLOOR)]).diagnostics()).toEqual([]);
  });

  it('flags the parts that are not simulated and stay inert', () => {
    // A powered rail has redstone behaviour the engine does not model.
    const sim = build(v, [
      cell([0, 0, 0], POWERED_RAIL, 0),
      cell([2, 0, 0], TORCH, 0),
      cell([3, 0, 0], LEVER, FLOOR | ON),
      cell([4, 0, 0], DUST),
    ]);
    expect(brief(sim.diagnostics())).toEqual([
      ['unsupported', '0,0,0'],
      ['unsupported', '2,0,0'],
    ]);
    expect(sim.diagnostics()[1]!.text).toMatch(/state the game does not have/);
    expect(sim.powerAt([2, 0, 0])).toBe(0);
    expect(sim.inputAt([0, 0, 0])).toBeNull();
  });

  it('has no unstable tick with the default budget', () => {
    const sim = build(v, runawayCells);
    sim.input(toggle([0, 0, 0]));
    sim.run(20, 'idle');
    expect(sim.diagnostics()).toEqual([]);
    expect(sim.powerAt([5, 0, 1])).toBe(15);
  });

  it('cuts short a tick that takes more updates than its budget, reports it and stops stepping', () => {
    const sim = build(v, runawayCells, { updateLimit: 50 });
    expect(sim.diagnostics()).toEqual([]);
    sim.input(toggle([0, 0, 0]));
    const r = sim.run(20, 'idle');
    expect(r.ranTicks).toBe(1);
    const [d] = sim.diagnostics();
    expect(d?.code).toBe('unstable');
    expect(d?.text).toMatch(/More than 50 updates in one game tick/);
    // The busiest cell is one of the repeaters beside the wire.
    expect(d?.at[2]).toBe(1);
  });
});

/** A floor lever, a 20-cell wire and a repeater beside every dust cell reading from it. */
const runawayCells = [
  cell([0, 0, 0], LEVER, FLOOR),
  ...Array.from({ length: 20 }, (_, i) => cell([i + 1, 0, 0], DUST)),
  ...Array.from({ length: 20 }, (_, i) => cell([i + 1, 0, 1], REPEATER, READS_NORTH)),
];

describe('parts per version', () => {
  const cells = [
    cell([0, 2, 0], STONE),
    cell([0, 1, 0], LEVER, 0 | ON),
    cell([2, 0, 0], STONE_BUTTON, FLOOR),
    cell([3, 0, 0], STONE),
    cell([4, 0, 0], WOODEN_BUTTON, 1),
    cell([6, 0, 0], COMPARATOR, 0),
    cell([8, 0, 0], LAMP, 0),
  ];

  it('b1.8.1 has no ceiling lever, floor button, wooden button, comparator or lamp', () => {
    const list = build('b1.8.1', cells).diagnostics();
    expect(brief(list)).toEqual([
      ['notInVersion', '2,0,0'],
      ['notInVersion', '4,0,0'],
      ['notInVersion', '6,0,0'],
      ['notInVersion', '8,0,0'],
      ['notInVersion', '0,1,0'],
    ]);
    expect(list[0]!.text).toBe('Beta 1.8.1 does not have button placed this way, so it is left out of the simulation.');
    expect(list[1]!.text).toBe('Beta 1.8.1 does not have this block.');
  });

  it('1.12.2 has them all, and simulates them', () => {
    const sim = build('1.12.2', cells);
    expect(brief(sim.diagnostics())).toEqual([]);
    // The ceiling lever strongly powers the block above it.
    expect(sim.powerAt([0, 2, 0])).toBe(15);
  });
});
