/**
 * The 1.12.2 components (report 4.5, 8.3): repeater locks and comparators. The timing minimums their specs state are
 * pinned, and so is why they are not listed for b1.8.1.
 */
import { describe, expect, it } from 'vitest';
import type { Pos } from '../../src/api.js';
import { LIBRARY } from '../../src/library/index.js';
import { MODERN } from '../../src/library/modern.js';
import type { ComponentDef } from '../../src/library/types.js';
import { harnessCells, verifyComponent } from '../../src/library/verify.js';
import { createSimulation } from '../../src/simulation.js';

type Sequence = Extract<ComponentDef['spec'], { kind: 'sequence' }>;
const def = (id: string): ComponentDef => MODERN.find((d) => d.id === id)!;
const DAYLIGHT = 151;

/** The def with every pulse of `port` set to `ticks` (and its minimum lowered to match). */
function pulsed(d: ComponentDef, port: string, ticks: number): ComponentDef {
  const spec = d.spec as Sequence;
  return {
    ...d,
    spec: {
      ...spec,
      minPulseTicks: Math.min(spec.minPulseTicks, ticks),
      steps: spec.steps.map((s) => (s.pulse?.port === port ? { ...s, pulse: { port, ticks } } : s)),
    },
  };
}

it('lists the five 1.12.2 components in the library, for 1.12.2 only', () => {
  expect(MODERN.map((d) => [d.id, Object.keys(d.layouts)])).toEqual(
    [
      'memory.lock-cell',
      'memory.lock-t-flip-flop',
      'arithmetic.subtractor',
      'arithmetic.is-stronger',
      'memory.signal-hold',
    ].map((id) => [id, ['1.12.2']]),
  );
  for (const d of MODERN) expect(LIBRARY).toContain(d);
});

// Their specs are proven in every placement with every feeder by truthTables and sequences, with the rest of the library.
describe.each(MODERN.map((d) => [d.id, d] as const))('%s', (_id, d) => {
  it('does not hold in b1.8.1, which has neither locks nor comparators', () => {
    const asBeta: ComponentDef = { ...d, layouts: { 'b1.8.1': d.layouts['1.12.2']! } };
    expect(verifyComponent(asBeta, 'b1.8.1').ok).toBe(false);
  });
});

describe('the analog harness', () => {
  it('drives an analog input with a daylight sensor and refuses b1.8.1', () => {
    const port = { name: 'A', kind: 'in' as const, at: [-1, 0, 1] as Pos, side: 'west' as const };
    expect(harnessCells(port, 'straight', true)).toEqual([
      [-1, 0, 1, 55, 0],
      [-2, 0, 1, DAYLIGHT, 0],
    ]);
    const sub = def('arithmetic.subtractor');
    expect(verifyComponent({ ...sub, layouts: { 'b1.8.1': sub.layouts['1.12.2']! } }, 'b1.8.1')).toEqual({
      ok: false,
      failures: ['arithmetic.subtractor has analog ports, which only 1.12.2 can drive'],
    });
  });

  it('reads an analog output by level, so a wrong level fails', () => {
    const sub = def('arithmetic.subtractor');
    const spec = sub.spec as Extract<ComponentDef['spec'], { kind: 'truth' }>;
    const wrong: ComponentDef = {
      ...sub,
      spec: { ...spec, rows: spec.rows.map(([ins, outs]) => [ins, outs.map((y) => (y === 0 ? 0 : y + 1))] as const) },
    };
    expect(verifyComponent(wrong, '1.12.2').ok).toBe(false);
  });
});

describe('the timing minimums the specs state', () => {
  it('the lock T flip-flop needs T on for 4 game ticks: 2 do not flip it', () => {
    const tff = def('memory.lock-t-flip-flop');
    expect(tff.spec).toMatchObject({ minPulseTicks: 4 });
    expect(verifyComponent(pulsed(tff, 'T', 2), '1.12.2').ok).toBe(false);
  });

  it('the lock T flip-flop needs rises 9 game ticks apart: 4 on and 4 off loses every second pulse', () => {
    const layout = def('memory.lock-t-flip-flop').layouts['1.12.2']!;
    const cells = [...layout.cells, ...layout.ports.flatMap((p) => harnessCells(p, 'straight'))];
    /** Q after every game tick for three pulses `on` long, `off` apart, the first switched on at tick 1. */
    const play = (on: number, off: number): string => {
      const sim = createSimulation({ version: '1.12.2', cells, ground: { topY: -1 } });
      const flips = new Set<number>();
      for (let k = 0, t = 1; k < 3; k++, t += on + off) flips.add(t).add(t + on);
      let q = '';
      for (let t = 1; t <= 60; t++) {
        if (flips.has(t)) sim.input({ kind: 'toggle', at: [2, 1, 8] });
        sim.stepGameTick();
        q += sim.powerAt([7, 0, 2]) > 0 ? 1 : 0;
      }
      return q;
    };
    const changes = (q: string): number => [...q].filter((c, i) => i > 0 && c !== q[i - 1]).length;
    expect([changes(play(4, 5)), changes(play(3, 6)), changes(play(6, 3))]).toEqual([3, 3, 3]);
    expect([changes(play(4, 4)), changes(play(3, 5)), changes(play(10, 2))]).toEqual([2, 2, 1]);
    // Q flips 8 game ticks after the lever.
    expect(play(4, 6).indexOf('1') + 1).toBe(8);
  });

  it('the signal hold needs an R pulse of 8 game ticks: 4 leave part of the ring full', () => {
    const hold = def('memory.signal-hold');
    expect(hold.spec).toMatchObject({ minPulseTicks: 8 });
    expect(verifyComponent(pulsed(hold, 'R', 8), '1.12.2').ok).toBe(true);
    expect(verifyComponent(pulsed(hold, 'R', 4), '1.12.2').ok).toBe(false);
  });

  /** The hold wired by the harness with straight feeders; `level` sets D, `y` reads Y. */
  function hold() {
    const layout = def('memory.signal-hold').layouts['1.12.2']!;
    const cells = [...layout.cells, ...layout.ports.flatMap((p) => harnessCells(p, 'straight', p.name === 'D'))];
    const sim = createSimulation({ version: '1.12.2', cells, ground: { topY: -1 } });
    const sensor: Pos = [0, 0, -2];
    return {
      sim,
      level: (n: number) => sim.input({ kind: 'level', at: sensor, level: n }),
      y: () => sim.powerAt([2, 0, 5]),
    };
  }

  it('the signal hold keeps its level, idle, long after D has gone', () => {
    const { sim, level, y } = hold();
    level(12);
    sim.run(12, 'breakpoint');
    level(0);
    sim.run(500, 'breakpoint');
    expect(y()).toBe(12);
    expect(sim.idle).toBe(true);
  });

  it('a D level held for only 4 game ticks is not kept steady: half the ring goes round empty', () => {
    const { sim, level, y } = hold();
    level(9);
    sim.run(4, 'breakpoint');
    level(0);
    sim.run(20, 'breakpoint');
    const seen = new Set<number>();
    for (let t = 0; t < 16; t++) {
      sim.stepGameTick();
      seen.add(y());
    }
    expect([...seen].sort((a, b) => a - b)).toEqual([0, 9]);
    expect(sim.idle).toBe(false);
  });
});
