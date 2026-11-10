/** The redstone torch: inversion, what it powers, burnout per version, and when it asks for its tick (C14.1). */
import { describe, expect, it } from 'vitest';
import type { InputEvent, Pos } from '../src/api.js';
import {
  DUST,
  LATCH,
  LEVER,
  STICKS_WEST,
  STONE,
  TORCH,
  TORCH_OFF,
  TORCH_ON_WEST,
  VERSIONS,
  build,
  cell,
  edges,
  idAt,
  play,
  toggle,
} from './circuit.js';

/** A torch at (1,0,0) hanging on K (0,0,0); lever A at (-1,0,0) powers K; lever B at (2,0,0) only nudges the torch. */
const T: Pos = [1, 0, 0];
const A: Pos = [-1, 0, 0];
const B: Pos = [2, 0, 0];
const inverter = [
  cell([0, 0, 0], STONE),
  cell(T, TORCH, TORCH_ON_WEST),
  // Sticks out west from K, so it strongly powers K.
  cell(A, LEVER, STICKS_WEST),
  cell(B, LEVER, STICKS_WEST),
  cell([3, 0, 0], STONE),
];

/** Eight pulses of 4 game ticks on lever A, one every 8: the torch turns off on ticks 2, 10, ..., 58. */
const EIGHT_PULSES: Record<number, InputEvent[]> = {};
for (let k = 0; k < 8; k++) {
  EIGHT_PULSES[1 + 8 * k] = [toggle(A)];
  EIGHT_PULSES[5 + 8 * k] = [toggle(A)];
}

describe.each(VERSIONS)('the torch in %s', (v) => {
  it('inverts its block with a 2 game tick delay', () => {
    const sim = build(v, inverter);
    expect(sim.powerAt(T)).toBe(15);
    const power = play(sim, 30, { 1: [toggle(A)], 10: [toggle(A)] }, (s) => s.powerAt(T));
    // An input lands between two ticks; the torch answers on the second tick after it.
    expect(edges(power, false)).toEqual([2]);
    expect(edges(power, true)).toEqual([11]);
    expect(idAt(sim, T)).toBe(TORCH);
  });

  it('powers every side but its block, and strongly only the block above', () => {
    const sim = build(v, [
      cell([0, 0, 0], STONE),
      cell(T, TORCH, TORCH_ON_WEST),
      cell([1, 1, 0], STONE),
      cell([2, 0, 0], STONE),
      cell([3, 0, 0], DUST),
      cell([1, 0, 1], DUST),
      cell([1, 0, -1], DUST),
      cell([-1, 0, 0], DUST),
    ]);
    expect([sim.powerAt([1, 0, 1]), sim.powerAt([1, 0, -1])]).toEqual([15, 15]);
    // Above: strong power, which the block passes on.
    expect(sim.powerAt([1, 1, 0])).toBe(15);
    // Beside: weak power only, which a block does not pass on.
    expect(sim.powerAt([2, 0, 0])).toBe(0);
    expect(sim.powerAt([3, 0, 0])).toBe(0);
    // Its own block gets nothing.
    expect(sim.powerAt([0, 0, 0])).toBe(0);
    expect(sim.powerAt([-1, 0, 0])).toBe(0);
  });

  it('burns out on the 8th turn-off inside the window, not the 7th', () => {
    const sim = build(v, inverter);
    const events: Array<{ kind: string; tick: number }> = [];
    const power = new Map<number, number>();
    while (sim.tick < 100) {
      for (const e of EIGHT_PULSES[sim.tick + 1] ?? []) sim.input(e);
      const r = sim.stepGameTick();
      events.push(...r.events.map((e) => ({ kind: e.kind, tick: e.tick })));
      power.set(sim.tick, sim.powerAt(T));
    }
    expect(edges(power, false)).toEqual([2, 10, 18, 26, 34, 42, 50, 58]);
    // It relit after each of the first seven, and not after the eighth although its block is unpowered again.
    expect(edges(power, true)).toEqual([6, 14, 22, 30, 38, 46, 54]);
    expect(events).toEqual([{ kind: 'burnout', tick: 58 }]);
    expect(sim.powerAt([0, 0, 0])).toBe(0);
    expect(idAt(sim, T)).toBe(TORCH_OFF);
  });

  it('settles a latch the same way every time: one torch lit, the other out', () => {
    const one = build(v, LATCH.cells);
    const two = build(v, LATCH.cells);
    expect(one.displayCells()).toEqual(two.displayCells());
    expect([idAt(one, LATCH.t1), idAt(one, LATCH.t2)].sort()).toEqual([TORCH_OFF, TORCH]);
    expect(one.idle).toBe(true);
  });
});

describe('burnout recovery per version', () => {
  const burnOut = (v: '1.12.2' | 'b1.8.1') => {
    const sim = build(v, inverter);
    play(sim, 64, EIGHT_PULSES, () => 0);
    expect(idAt(sim, T)).toBe(TORCH_OFF);
    return sim;
  };

  it('1.12.2 tries again 160 game ticks after the burnout and relights', () => {
    const sim = burnOut('1.12.2');
    expect(sim.pendingAt(T)).toEqual([{ dueTick: 58 + 160, priority: 0 }]);
    const power = play(sim, 240, {}, (s) => s.powerAt(T));
    expect(edges(power, true)).toEqual([218]);
  });

  it('b1.8.1 stays out with nothing waiting until a neighbour update after the log has aged', () => {
    const sim = burnOut('b1.8.1');
    play(sim, 150, {}, () => 0);
    expect(idAt(sim, T)).toBe(TORCH_OFF);
    expect(sim.pendingAt(T)).toEqual([]);
    expect(sim.idle).toBe(true);
    // Lever B touches the torch but not its block: the update alone relights it.
    const power = play(sim, 170, { 160: [toggle(B)] }, (s) => s.powerAt(T));
    expect(edges(power, true)).toEqual([161]);
  });
});

describe('when a torch asks for its tick (C14.1)', () => {
  const run = (v: '1.12.2' | 'b1.8.1', nudge: boolean) => {
    const sim = build(v, inverter);
    const inputs: Record<number, InputEvent[]> = nudge ? { 1: [toggle(B)], 2: [toggle(A)] } : { 2: [toggle(A)] };
    return edges(
      play(sim, 10, inputs, (s) => s.powerAt(T)),
      false,
    );
  };

  it('b1.8.1 answers in 1 game tick when an unrelated update already queued its tick', () => {
    expect(run('b1.8.1', false)).toEqual([3]);
    expect(run('b1.8.1', true)).toEqual([2]);
  });

  it('1.12.2 queues nothing for an update that does not change its input', () => {
    expect(run('1.12.2', false)).toEqual([3]);
    expect(run('1.12.2', true)).toEqual([3]);
  });
});
