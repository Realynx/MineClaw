/**
 * History (report 4.4, 4.9): stepping back lands on exactly the state the clock passed through, cell by cell, with the
 * pending ticks, the burnout log, a piston's move and a comparator's level; what came after is gone; a reset is a fresh
 * settle; and the same script gives the same lists again.
 */
import { describe, expect, it } from 'vitest';
import type { InputEvent, Pos, RedstoneVersion, StepResult } from '../src/api.js';
import type { RedstoneSimulation } from '../src/simulation.js';
import {
  CHEST,
  COMPARATOR,
  DUST,
  FLOOR,
  LEVER,
  READS_WEST,
  STICKS_WEST,
  STICKY_PISTON,
  STONE,
  TORCH,
  TORCH_OFF,
  TORCH_ON_WEST,
  VERSIONS,
  build,
  cell,
  idAt,
  toggle,
} from './circuit.js';

const EAST = 5;

/** The burnout inverter of torch.test: torch T on block K, lever A powers K, lever B only nudges T. */
const T: Pos = [1, 0, 0];
const A: Pos = [-1, 0, 0];
const B: Pos = [2, 0, 0];
/** A sticky piston facing east with a block in front and a floor lever beside it. */
const P: Pos = [0, 0, 10];
const PL: Pos = [0, 0, 9];
/** 1.12.2: a chest read by a comparator, with dust in front. */
const CH: Pos = [-1, 0, 20];
const CMP: Pos = [0, 0, 20];

function design(v: RedstoneVersion) {
  const cells = [
    cell([0, 0, 0], STONE),
    cell(T, TORCH, TORCH_ON_WEST),
    cell(A, LEVER, STICKS_WEST),
    cell(B, LEVER, STICKS_WEST),
    cell([3, 0, 0], STONE),
    cell(P, STICKY_PISTON, EAST),
    cell(PL, LEVER, FLOOR),
    cell([1, 0, 10], STONE),
    cell([2, 0, 10], DUST),
  ];
  if (v === '1.12.2') cells.push(cell(CH, CHEST), cell(CMP, COMPARATOR, READS_WEST), cell([1, 0, 20], DUST));
  return cells;
}

/** Eight 4 game tick pulses on A burn the torch out on tick 58; the piston and the chest move in between. */
function script(v: RedstoneVersion): Record<number, InputEvent[]> {
  const out: Record<number, InputEvent[]> = {};
  const add = (tick: number, e: InputEvent) => (out[tick] ??= []).push(e);
  for (let k = 0; k < 8; k++) {
    add(1 + 8 * k, toggle(A));
    add(5 + 8 * k, toggle(A));
  }
  add(3, toggle(PL));
  add(31, toggle(PL));
  add(70, toggle(PL));
  add(72, toggle(PL));
  if (v === '1.12.2') {
    add(12, { kind: 'level', at: CH, level: 7 });
    add(40, { kind: 'level', at: CH, level: 3 });
    add(41, { kind: 'level', at: CH, level: 12 });
  }
  return out;
}

/**
 * Everything in the engine that changes, small enough to compare and to print: the clock, the queued inputs, every
 * store (pending ticks, the burnout log, piston moves, comparator levels...) and every cell that differs from the design.
 */
function snap(sim: RedstoneSimulation) {
  const k = sim.engine.save();
  const design = sim.engine.grid.design;
  const cells: Array<[number, number]> = [];
  for (let i = 0; i < k.cells.length; i++) if (k.cells[i] !== design[i]) cells.push([i, k.cells[i]!]);
  return { tick: k.tick, queue: k.queue, stores: k.stores, cells };
}

type Snap = ReturnType<typeof snap>;

/** Plays the script to `until`, keeping a snapshot after every tick (index = tick). */
function forward(sim: RedstoneSimulation, until: number, inputs: Record<number, InputEvent[]>) {
  const saved: Snap[] = [snap(sim)];
  const results: StepResult[] = [];
  while (sim.tick < until) {
    for (const e of inputs[sim.tick + 1] ?? []) sim.input(e);
    results.push(sim.stepGameTick());
    saved[sim.tick] = snap(sim);
  }
  return { saved, results };
}

describe.each(VERSIONS)('history in %s', (v) => {
  it('steps back one game tick at a time through exactly the states it passed', () => {
    const sim = build(v, design(v));
    const { saved } = forward(sim, 120, script(v));
    // The script did burn the torch out and move the piston.
    expect(idAt(sim, T)).toBe(TORCH_OFF);
    expect(snap(sim)).not.toEqual(saved[0]);
    for (let t = 119; t >= 0; t--) {
      const back = sim.stepBack();
      expect([back.tick, back.ranTicks, back.events, back.hits]).toEqual([t, 0, [], []]);
      expect(snap(sim), `tick ${t}`).toEqual(saved[t]);
    }
    expect(sim.historyDepth).toBe(0);
    expect(sim.stepBack()).toMatchObject({ tick: 0, changed: [] });
  });

  it('steps back many game ticks at once, and the changed cells are the difference', () => {
    const sim = build(v, design(v));
    const { saved } = forward(sim, 120, script(v));
    const twin = build(v, design(v));
    forward(twin, 33, script(v));
    const shownBefore = sim.displayCells();
    const back = sim.stepBack(87);
    expect(back.tick).toBe(33);
    expect(snap(sim)).toEqual(saved[33]);
    expect(sim.displayCells()).toEqual(twin.displayCells());
    for (const [x, y, z, id, meta] of back.changed) expect(twin.stateAt([x, y, z])).toBe((id << 4) | meta);
    const changedAt = new Set(back.changed.map(([x, y, z]) => `${x},${y},${z}`));
    for (const [x, y, z, id, meta] of shownBefore)
      if (!changedAt.has(`${x},${y},${z}`)) expect(twin.stateAt([x, y, z])).toBe((id << 4) | meta);
    expect(sim.pendingAt(T)).toEqual(twin.pendingAt(T));
  });

  it('plays the same ticks again after a rewind when given the same inputs', () => {
    const sim = build(v, design(v));
    const inputs = script(v);
    const { saved, results } = forward(sim, 100, inputs);
    sim.stepBack(100);
    const again = forward(sim, 100, inputs);
    expect(again.results).toEqual(results);
    for (let t = 0; t <= 100; t++) expect(again.saved[t], `tick ${t}`).toEqual(saved[t]);
  });

  it('drops the inputs and samples after the tick it went back to, queued ones included', () => {
    const sim = build(v, design(v));
    sim.watch([T]);
    forward(sim, 40, script(v));
    expect(sim.trace(T, 0, 100).map((s) => s.tick)).toEqual(Array.from({ length: 41 }, (_, i) => i));
    sim.input(toggle(A));
    sim.stepBack(10);
    expect(sim.tick).toBe(30);
    expect(sim.trace(T, 0, 100).map((s) => s.tick)).toEqual(Array.from({ length: 31 }, (_, i) => i));
    // Lever A was switched on at tick 33 and off at 37; after the rewind neither happens, nor the queued toggle.
    sim.run(30, 'breakpoint');
    expect(sim.stateAt(A) & 8).toBe(0);
    expect(sim.powerAt(T)).toBe(15);
    const samples = sim.trace(T, 31, 60);
    expect(samples.map((s) => s.tick)).toEqual(Array.from({ length: 30 }, (_, i) => 31 + i));
    expect(samples.every((s) => s.power === 15 && s.powered)).toBe(true);
  });

  it('a rewind of nothing still drops the queued inputs', () => {
    const sim = build(v, design(v));
    sim.input(toggle(A));
    expect(sim.stepBack(0)).toMatchObject({ tick: 0, changed: [], idle: true });
    sim.stepGameTick();
    expect(sim.stateAt(A) & 8).toBe(0);
  });

  it('resets to exactly a fresh settle and forgets the history', () => {
    const sim = build(v, design(v));
    forward(sim, 90, script(v));
    expect(sim.historyDepth).toBe(90);
    sim.input(toggle(B));
    const back = sim.reset();
    expect([back.tick, back.ranTicks, back.events, back.hits]).toEqual([0, 0, [], []]);
    expect(snap(sim)).toEqual(snap(build(v, design(v))));
    expect(sim.historyDepth).toBe(0);
    expect(sim.idle).toBe(true);
  });

  it('gives identical lists on a rerun, rewinds included', () => {
    const run = () => {
      const sim = build(v, design(v));
      const inputs = script(v);
      const out: StepResult[] = [];
      // Every 25th call steps back 7 and goes on with a redstone tick; the inputs of the ticks run again are given again.
      for (let call = 1; call <= 100; call++) {
        for (const e of inputs[sim.tick + 1] ?? []) sim.input(e);
        out.push(sim.stepGameTick());
        if (call % 25 === 0) out.push(sim.stepBack(7), sim.stepRedstoneTick());
      }
      out.push(sim.reset(), sim.run(60, 'idle'));
      return out;
    };
    const one = run();
    expect(one.some((r) => r.changed.length > 0)).toBe(true);
    expect(run()).toEqual(one);
  });

  it('reaches back no further than its depth, keyframes or not', () => {
    const sim = build(v, design(v), { historyTicks: 30 });
    const { saved } = forward(sim, 100, script(v));
    expect(sim.historyDepth).toBe(30);
    expect(sim.stepBack(500).tick).toBe(70);
    expect(snap(sim)).toEqual(saved[70]);
    expect(sim.historyDepth).toBeLessThanOrEqual(30);
    sim.stepBack(sim.historyDepth);
    expect(snap(sim)).toEqual(saved[sim.tick]);
  });

  it('keeps no history at a depth of 0', () => {
    const sim = build(v, design(v), { historyTicks: 0 });
    const { saved } = forward(sim, 20, script(v));
    expect(sim.historyDepth).toBe(0);
    expect(sim.stepBack(5).tick).toBe(20);
    expect(snap(sim)).toEqual(saved[20]);
  });
});

describe('what the history covers', () => {
  it('keeps a piston in mid-move and a comparator level across a rewind (1.12.2)', () => {
    const v = '1.12.2';
    const sim = build(v, design(v));
    const { saved } = forward(sim, 45, script(v));
    // The chest goes to 3 at the start of tick 40 and to 12 at the start of 41: the comparator's tick, asked for on
    // 40, runs on 41 and reads 12, so 3 never shows. Before that it put out the 7 of tick 12.
    expect(sim.powerAt([1, 0, 20])).toBe(12);
    sim.stepBack(4);
    expect(sim.powerAt([1, 0, 20])).toBe(12);
    sim.stepBack(1);
    expect(sim.powerAt([1, 0, 20])).toBe(7);
    // Tick 4: the push started on tick 4 is still in flight.
    sim.stepBack(36);
    expect(sim.tick).toBe(4);
    expect(snap(sim)).toEqual(saved[4]);
    expect(sim.idle).toBe(false);
  });

  it('keeps the b1.8.1 torch out after its burnout when stepping back over it', () => {
    const sim = build('b1.8.1', design('b1.8.1'));
    forward(sim, 70, script('b1.8.1'));
    expect(idAt(sim, T)).toBe(TORCH_OFF);
    sim.stepBack(12);
    expect(idAt(sim, T)).toBe(TORCH_OFF);
    sim.stepBack(2);
    expect(sim.tick).toBe(56);
    expect(idAt(sim, T)).toBe(TORCH);
  });
});
