/**
 * The repeater: delays, pulse stretching, what it reads, and 1.12.2's locking, priorities and notification reach
 * (C14.6). Also the RS latch whose 2 game tick input pulse works in b1.8.1 and fails in 1.12.2 (report 8.3).
 */
import { describe, expect, it, vi } from 'vitest';
import type { Pos } from '../src/api.js';
import {
  DUST,
  FLOOR,
  LATCH,
  LEVER,
  READS_SOUTH,
  READS_WEST,
  REPEATER,
  STICKS_EAST,
  STICKS_NORTH,
  STONE,
  VERSIONS,
  build,
  cell,
  edges,
  play,
  toggle,
} from './circuit.js';

const R: Pos = [0, 0, 0];
const LEVER_AT: Pos = [-1, 0, 0];
const OUT: Pos = [1, 0, 0];
/** A lever on a block to its west feeds a repeater at R that reads west; dust in front shows its output. */
const line = (meta: number) => [
  cell([-2, 0, 0], STONE),
  cell(LEVER_AT, LEVER, STICKS_EAST),
  cell(R, REPEATER, meta),
  cell(OUT, DUST),
];
const delayMeta = (gameTicks: number) => READS_WEST | ((gameTicks / 2 - 1) << 2);

describe.each(VERSIONS)('the repeater in %s', (v) => {
  it.each([2, 4, 6, 8])('turns on and off %i game ticks after its input', (delay) => {
    const sim = build(v, line(delayMeta(delay)));
    const power = play(sim, 60, { 1: [toggle(LEVER_AT)], 21: [toggle(LEVER_AT)] }, (s) => s.powerAt(OUT));
    expect(edges(power, true)).toEqual([delay]);
    expect(edges(power, false)).toEqual([20 + delay]);
  });

  it('stretches a 2 game tick pulse to its delay', () => {
    const sim = build(v, line(delayMeta(8)));
    const power = play(sim, 40, { 1: [toggle(LEVER_AT)], 3: [toggle(LEVER_AT)] }, (s) => s.powerAt(OUT));
    expect(edges(power, true)).toEqual([8]);
    expect(edges(power, false)).toEqual([16]);
  });

  it('reads bare dust behind it, even dust that does not point at it', () => {
    // A north-south line passes behind the repeater: that dust links north and south, so it points neither way.
    const sim = build(v, [
      cell([-1, 0, -2], LEVER, FLOOR | 8),
      cell([-1, 0, -1], DUST),
      cell([-1, 0, 0], DUST),
      cell([-1, 0, 1], DUST),
      cell(R, REPEATER, READS_WEST),
      cell(OUT, DUST),
    ]);
    expect(sim.powerAt([-1, 0, 0])).toBe(14);
    expect(sim.powerAt(OUT)).toBe(15);
  });

  it('settles the RS latch the same way, then a 4 game tick pulse sets it', () => {
    const sim = build(v, LATCH.cells);
    play(sim, 30, { 1: [toggle(LATCH.r)], 11: [toggle(LATCH.r)] }, () => 0);
    expect([sim.powerAt(LATCH.q), sim.powerAt(LATCH.qBar) > 0]).toEqual([0, true]);
    play(sim, 60, { 31: [toggle(LATCH.s)], 35: [toggle(LATCH.s)] }, () => 0);
    expect([sim.powerAt(LATCH.q), sim.powerAt(LATCH.qBar)]).toEqual([15, 0]);
    play(sim, 90, { 61: [toggle(LATCH.r)], 65: [toggle(LATCH.r)] }, () => 0);
    expect(sim.powerAt(LATCH.q)).toBe(0);
  });
});

/**
 * Report 8.3 expected a 2 game tick S pulse to set the latch in b1.8.1. It does not, there either: when T2 goes out its
 * own update queues a tick for it (C14.1: mc-dev writes the new block id before the old torch tells its neighbours),
 * and that tick runs before T1's on the same game tick, so T2 lights again first. The versions still differ in what
 * the pulse does: b1.8.1's T2 is queued before the repeater's turn-off and goes out for 2 game ticks; 1.12.2 runs the
 * turn-off (priority -2) before T2's tick (0), so T2 never goes out. Both need 4 game ticks (`minPulseTicks: 4`).
 */
describe('a 2 game tick pulse into the RS latch (report 8.3)', () => {
  const pulse = (v: 'b1.8.1' | '1.12.2') => {
    const sim = build(v, LATCH.cells);
    play(sim, 30, { 1: [toggle(LATCH.r)], 11: [toggle(LATCH.r)] }, () => 0);
    expect(sim.powerAt(LATCH.q)).toBe(0);
    const t2 = play(sim, 60, { 31: [toggle(LATCH.s)], 33: [toggle(LATCH.s)] }, (s) => s.powerAt(LATCH.t2));
    expect(sim.powerAt(LATCH.q)).toBe(0);
    return [edges(t2, false), edges(t2, true)];
  };

  it('b1.8.1: T2 is queued before the repeater turns off, goes out for 2 game ticks, and lights again', () => {
    expect(pulse('b1.8.1')).toEqual([[34], [36]]);
  });

  it('1.12.2: the turn-off (priority -2) runs before T2 (0) on the same tick, so T2 never goes out', () => {
    expect(pulse('1.12.2')).toEqual([[], []]);
  });
});

describe('locking and priorities', () => {
  // Repeater A at R reads west and waits 4 game ticks; repeater B south of it points into A's side.
  const B: Pos = [0, 0, 1];
  const LB: Pos = [0, 0, 2];
  const lock = [
    ...line(delayMeta(4)),
    cell(B, REPEATER, READS_SOUTH),
    cell(LB, LEVER, STICKS_NORTH),
    cell([0, 0, 3], STONE),
  ];

  it('1.12.2: a lock that arrives after the tick was asked for still holds, because it is checked when the tick runs', () => {
    const sim = build('1.12.2', lock);
    sim.input(toggle(LEVER_AT));
    sim.input(toggle(LB));
    sim.stepGameTick();
    expect(sim.pendingAt(R)).toEqual([{ dueTick: 4, priority: -1 }]);
    // B faces a repeater that does not face back: -3.
    expect(sim.pendingAt(B)).toEqual([{ dueTick: 2, priority: -3 }]);

    const power = play(sim, 20, {}, (s) => s.powerAt(OUT));
    expect([...power.values()].every((p) => p === 0)).toBe(true);
    expect(sim.lockedAt(R)).toBe(true);
    expect(sim.lockedCells()).toEqual([R]);
    expect(sim.pendingAt(R)).toEqual([]);

    // Unlocked, it hears the change and follows its input after its delay.
    const after = play(sim, 40, { 21: [toggle(LB)] }, (s) => s.powerAt(OUT));
    expect(edges(after, true)).toEqual([26]);
    expect(sim.lockedCells()).toEqual([]);
  });

  it('1.12.2: a repeater turning off asks with -2', () => {
    const sim = build('1.12.2', line(delayMeta(2)));
    play(sim, 10, { 1: [toggle(LEVER_AT)] }, () => 0);
    sim.input(toggle(LEVER_AT));
    sim.stepGameTick();
    expect(sim.pendingAt(R)).toEqual([{ dueTick: 12, priority: -2 }]);
  });

  it('b1.8.1 has no locking and no priorities', () => {
    const sim = build('b1.8.1', lock);
    sim.input(toggle(LEVER_AT));
    sim.input(toggle(LB));
    sim.stepGameTick();
    expect(sim.pendingAt(R)).toEqual([{ dueTick: 4, priority: 0 }]);
    expect(sim.pendingAt(B)).toEqual([{ dueTick: 2, priority: 0 }]);
    const power = play(sim, 20, {}, (s) => s.powerAt(OUT));
    expect(edges(power, true)).toEqual([4]);
    expect(sim.lockedCells()).toEqual([]);
  });
});

describe('who hears a repeater change (report 4.2a, C14.6)', () => {
  /** The cells told about the repeater's own change on the tick it turns on. */
  const heard = (v: 'b1.8.1' | '1.12.2') => {
    const sim = build(v, [cell([-2, 0, 0], STONE), cell(LEVER_AT, LEVER, STICKS_EAST), cell(R, REPEATER, READS_WEST)]);
    sim.input(toggle(LEVER_AT));
    sim.stepGameTick();
    const engine = sim.engine;
    const spy = vi.spyOn(engine, 'notify');
    sim.stepGameTick();
    const cells = new Set(spy.mock.calls.map(([at]) => engine.pos(at).join()));
    spy.mockRestore();
    return cells;
  };

  it("1.12.2 tells the cell in front and that cell's other neighbours, nothing beside it", () => {
    expect([...heard('1.12.2')].sort()).toEqual(
      [
        [1, 0, 0],
        [2, 0, 0],
        [1, -1, 0],
        [1, 1, 0],
        [1, 0, -1],
        [1, 0, 1],
      ]
        .map((p) => p.join())
        .sort(),
    );
  });

  it('b1.8.1 tells the cells around all six of its neighbours, the ones beside it included', () => {
    const cells = heard('b1.8.1');
    for (const p of [
      [0, 0, 1],
      [0, 0, -1],
      [-1, 0, 0],
      [1, 0, 0],
      [0, 1, 0],
      [-1, 0, 1],
    ])
      expect(cells.has(p.join())).toBe(true);
  });
});
