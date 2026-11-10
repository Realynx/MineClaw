/** Redstone dust: levels, steps, what it powers and what it links to, per version (report 4.2, C14.2). */
import { describe, expect, it } from 'vitest';
import type { Pos } from '../src/api.js';
import {
  DUST,
  FLOOR,
  GLOWSTONE,
  LEVER,
  LIGHT_PLATE,
  ON,
  READS_EAST,
  READS_SOUTH,
  REPEATER,
  SLAB,
  STICKS_WEST,
  STONE,
  TORCH,
  TORCH_ON_FLOOR,
  VERSIONS,
  build,
  cell,
  toggle,
} from './circuit.js';

const dust = (x: number, y = 0, z = 0) => cell([x, y, z], DUST);
const leverOn = (at: Pos) => cell(at, LEVER, FLOOR | ON);

describe.each(VERSIONS)('dust in %s', (v) => {
  it('loses one level per cell: 15 down to 0 over 16 cells, and follows its source', () => {
    const line = Array.from({ length: 17 }, (_, i) => dust(i + 1));
    const sim = build(v, [leverOn([0, 0, 0]), ...line]);
    const levels = () => line.map((c) => sim.powerAt([c[0], c[1], c[2]]));
    expect(levels()).toEqual([15, 14, 13, 12, 11, 10, 9, 8, 7, 6, 5, 4, 3, 2, 1, 0, 0]);

    sim.input(toggle([0, 0, 0]));
    const off = sim.stepGameTick();
    expect(levels().every((l) => l === 0)).toBe(true);
    // The lever and the 15 dust cells that had a level.
    expect(off.changed).toHaveLength(16);
    // The work of a tick grows with what changed, not with the size of the design: no wall clock needed.
    expect(sim.engine.stats.updates).toBeLessThanOrEqual(10 * off.changed.length);

    sim.input(toggle([0, 0, 0]));
    sim.stepGameTick();
    expect(levels()[0]).toBe(15);
    expect(levels()[14]).toBe(1);
  });

  it('climbs a block and steps down past an open side, but not under a block', () => {
    const steps = [leverOn([0, 0, 0]), dust(1), cell([2, 0, 0], STONE), dust(2, 1), dust(3)];
    const sim = build(v, steps);
    expect([sim.powerAt([1, 0, 0]), sim.powerAt([2, 1, 0]), sim.powerAt([3, 0, 0])]).toEqual([15, 14, 13]);

    const capped = build(v, [...steps, cell([1, 1, 0], STONE)]);
    expect([capped.powerAt([1, 0, 0]), capped.powerAt([2, 1, 0]), capped.powerAt([3, 0, 0])]).toEqual([15, 0, 0]);
  });

  it('powers the block it points at and the block below it, a dot all four sides, a bent end none', () => {
    const line = build(v, [
      leverOn([0, 0, 0]),
      dust(1),
      dust(2),
      dust(3),
      cell([4, 0, 0], STONE),
      cell([2, 0, 1], STONE),
    ]);
    expect(line.powerAt([4, 0, 0])).toBe(13);
    expect(line.powerAt([2, 0, 1])).toBe(0);
    expect(line.powerAt([2, -1, 0])).toBe(14);

    const bent = build(v, [
      leverOn([0, 0, 0]),
      dust(1),
      dust(2),
      dust(2, 0, 1),
      cell([3, 0, 0], STONE),
      cell([2, 0, -1], STONE),
      cell([2, 0, 2], STONE),
    ]);
    expect(bent.powerAt([3, 0, 0])).toBe(0);
    expect(bent.powerAt([2, 0, -1])).toBe(0);
    expect(bent.powerAt([2, 0, 2])).toBe(13);

    // A dot on a block a repeater powers: nothing beside it to link to.
    const dot = build(v, [
      cell([0, 0, 0], STONE),
      cell([1, 0, 0], REPEATER, READS_EAST),
      leverOn([2, 0, 0]),
      dust(0, 1),
      cell([1, 1, 0], STONE),
      cell([-1, 1, 0], STONE),
      cell([0, 1, 1], STONE),
      cell([0, 1, -1], STONE),
    ]);
    expect(dot.powerAt([0, 1, 0])).toBe(15);
    for (const side of [
      [1, 1, 0],
      [-1, 1, 0],
      [0, 1, 1],
      [0, 1, -1],
    ] as const)
      expect(dot.powerAt(side)).toBe(15);
  });

  it('does not power other dust through a block', () => {
    const sim = build(v, [leverOn([0, 0, 0]), dust(1), cell([2, 0, 0], STONE), dust(3)]);
    expect(sim.powerAt([2, 0, 0])).toBe(15);
    expect(sim.powerAt([3, 0, 0])).toBe(0);
  });

  it('stands on the ground, which conducts like any solid block', () => {
    const sim = build(v, [leverOn([0, 0, 0]), dust(1, -1)], { dug: [[1, -1, 0]] });
    expect(sim.diagnostics()).toEqual([]);
    expect(sim.powerAt([0, -1, 0])).toBe(15);
    expect(sim.powerAt([1, -1, 0])).toBe(15);

    const floating = build(v, [dust(0)], { dug: [[0, -1, 0]] });
    expect(floating.diagnostics().map((d) => [d.code, d.at])).toEqual([['floating', [0, 0, 0]]]);
  });
});

describe('dust links per version', () => {
  // Dust on a block a repeater powers, with a lit torch one step down past its open east side. b1.8.1 links to a power
  // source on that diagonal (C14.2), so the dust points west only; 1.12.2 links diagonally to dust only, so it is a dot.
  const cells = [
    cell([0, 0, 0], STONE),
    cell([0, 0, 1], REPEATER, READS_SOUTH),
    leverOn([0, 0, 2]),
    cell([1, 0, 0], TORCH, TORCH_ON_FLOOR),
    dust(0, 1),
    cell([-1, 1, 0], STONE),
    cell([0, 1, -1], STONE),
  ];

  it('b1.8.1 links to a torch one step down, which turns the dust toward the far side', () => {
    const sim = build('b1.8.1', cells);
    expect(sim.powerAt([0, 1, 0])).toBe(15);
    expect(sim.powerAt([-1, 1, 0])).toBe(15);
    expect(sim.powerAt([0, 1, -1])).toBe(0);
  });

  it('1.12.2 does not, so the same dust powers every side', () => {
    const sim = build('1.12.2', cells);
    expect(sim.powerAt([-1, 1, 0])).toBe(15);
    expect(sim.powerAt([0, 1, -1])).toBe(15);
  });
});

describe('1.12.2 dust', () => {
  it.each([
    ['glowstone', GLOWSTONE, 0],
    ['a top slab', SLAB, 8],
  ])('on %s steps down to lower dust but is not read back', (_name, id, meta) => {
    const support = cell([1, 0, 0], id, meta);
    // Powered from below: the lower dust feeds the upper one.
    const fromBelow = build('1.12.2', [leverOn([-1, 0, 0]), dust(0), support, dust(1, 1)]);
    expect(fromBelow.diagnostics()).toEqual([]);
    expect([fromBelow.powerAt([0, 0, 0]), fromBelow.powerAt([1, 1, 0])]).toEqual([15, 14]);

    // Powered from above: nothing comes down.
    const fromAbove = build('1.12.2', [
      dust(0),
      support,
      dust(1, 1),
      cell([2, 1, 0], LEVER, STICKS_WEST | ON),
      cell([3, 1, 0], STONE),
    ]);
    expect([fromAbove.powerAt([1, 1, 0]), fromAbove.powerAt([0, 0, 0])]).toEqual([15, 0]);
  });

  it('takes an analog input level; b1.8.1 has no part that gives one', () => {
    const plate = cell([0, 0, 0], LIGHT_PLATE);
    const sim = build('1.12.2', [plate, dust(1), dust(2)], { extras: [{ at: [0, 0, 0], level: 6 }] });
    expect([sim.powerAt([1, 0, 0]), sim.powerAt([2, 0, 0])]).toEqual([6, 5]);

    const beta = build('b1.8.1', [plate, dust(1)], { extras: [{ at: [0, 0, 0], level: 6 }] });
    expect(beta.diagnostics().map((d) => d.code)).toEqual(['notInVersion']);
    expect(beta.powerAt([1, 0, 0])).toBe(0);
  });
});
