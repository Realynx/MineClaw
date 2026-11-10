/** The `Simulation` contract on top of the engine: stepping calls, reset, the shown cells, and what waits for later. */
import { describe, expect, it } from 'vitest';
import { GROUND_ID, createSimulation, type Pos } from '../src/index.js';
import {
  DUST,
  LATCH,
  LEVER,
  READS_WEST,
  REPEATER,
  STICKS_EAST,
  STONE,
  VERSIONS,
  build,
  cell,
  toggle,
} from './circuit.js';

const LEVER_AT: Pos = [-1, 0, 0];
/** A lever feeding a 4 game tick repeater, with dust in front. */
const cells = [
  cell([-2, 0, 0], STONE),
  cell(LEVER_AT, LEVER, STICKS_EAST),
  cell([0, 0, 0], REPEATER, READS_WEST | 4),
  cell([1, 0, 0], DUST),
  cell([2, 0, 0], DUST),
];

describe.each(VERSIONS)('the simulation in %s', (v) => {
  it('starts settled at tick 0 and runs until a shown change or until idle', () => {
    const sim = build(v, cells);
    expect([sim.tick, sim.idle, sim.historyDepth]).toEqual([0, true, 0]);
    expect(sim.run(50, 'idle')).toMatchObject({ tick: 0, ranTicks: 0, changed: [], idle: true });

    sim.input(toggle(LEVER_AT));
    expect(sim.idle).toBe(false);
    // The lever moves on the first tick.
    expect(sim.run(50, 'change')).toMatchObject({
      tick: 1,
      ranTicks: 1,
      changed: [[-1, 0, 0, LEVER, STICKS_EAST | 8]],
    });
    const on = sim.run(50, 'change');
    expect(on).toMatchObject({ tick: 4, ranTicks: 3, idle: true });
    // Sorted by y, z, x.
    expect(on.changed).toEqual([
      [0, 0, 0, REPEATER + 1, READS_WEST | 4],
      [1, 0, 0, DUST, 15],
      [2, 0, 0, DUST, 14],
    ]);
    expect(sim.displayCells()).toEqual([[-1, 0, 0, LEVER, STICKS_EAST | 8], ...on.changed]);
    expect(sim.stepRedstoneTick()).toMatchObject({ tick: 6, ranTicks: 2, changed: [] });
  });

  it('reports what is waiting for a cell, soonest first', () => {
    const sim = build(v, cells);
    sim.input(toggle(LEVER_AT));
    sim.stepGameTick();
    expect(sim.pendingAt([0, 0, 0])).toEqual([{ dueTick: 4, priority: v === '1.12.2' ? -1 : 0 }]);
    expect(sim.pendingAt([9, 9, 9])).toEqual([]);
  });

  it('resets to tick 0 exactly as a fresh build settles, and reports the cells that went back', () => {
    const fresh = build(v, LATCH.cells);
    const sim = build(v, LATCH.cells);
    sim.input(toggle(LATCH.s));
    sim.run(20, 'idle');
    sim.input(toggle(LATCH.s));
    sim.run(20, 'idle');
    sim.input(toggle(LATCH.r));
    sim.run(20, 'idle');
    const moved = sim.displayCells();
    const back = sim.reset();
    expect([back.tick, back.ranTicks, back.events, back.hits]).toEqual([0, 0, [], []]);
    expect(sim.displayCells()).toEqual(fresh.displayCells());
    for (const [x, y, z, id, meta] of back.changed) expect(fresh.stateAt([x, y, z])).toBe((id << 4) | meta);
    expect(back.changed.length).toBeGreaterThan(0);
    expect(moved).not.toEqual(fresh.displayCells());
    expect(sim.pendingAt(LATCH.t1)).toEqual(fresh.pendingAt(LATCH.t1));
  });

  it('gives the same lists for the same script', () => {
    const script = (sim: ReturnType<typeof build>) => {
      const out: unknown[] = [];
      for (let t = 1; t <= 60; t++) {
        if (t % 9 === 1) sim.input(toggle(t % 2 ? LATCH.s : LATCH.r));
        out.push(sim.stepGameTick());
      }
      return out;
    };
    expect(script(build(v, LATCH.cells))).toEqual(script(build(v, LATCH.cells)));
  });

  it('reads the ground and anything outside the design', () => {
    const sim = createSimulation({ version: v, cells, ground: { topY: -1 } });
    expect(sim.stateAt([0, -1, 0])).toBe(GROUND_ID << 4);
    expect(sim.stateAt([500, -3, 0])).toBe(GROUND_ID << 4);
    expect(sim.stateAt([500, 3, 0])).toBe(0);
    expect(sim.sample([1, 0, 0])).toEqual({ tick: 0, power: 0, powered: false, state: DUST << 4 });
    expect(sim.inputAt([500, 0, 0])).toBeNull();
    expect(sim.lockedAt([0, 0, 0])).toBe(false);
  });

  it('keeps the design it was given, whatever the caller does to its arrays later', () => {
    const own = cells.map((c) => [...c] as [number, number, number, number, number]);
    const sim = createSimulation({ version: v, cells: own, ground: { topY: -1 } });
    own[2]![3] = STONE;
    expect(sim.stateAt([0, 0, 0]) >> 4).toBe(REPEATER);
  });
});
