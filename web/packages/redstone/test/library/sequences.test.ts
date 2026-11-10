/**
 * Memory and pulse shapers: their sequences are proven in every placement, and the timing traps of report 8.3 (C16) are
 * pinned per version, so a spec cannot quietly promise what the game does not do.
 */
import { describe, expect, it } from 'vitest';
import type { Pos, RedstoneVersion } from '../../src/api.js';
import { LIBRARY } from '../../src/library/index.js';
import { transformLayout } from '../../src/library/transform.js';
import type { ComponentDef, ComponentLayout } from '../../src/library/types.js';
import { harnessCells, verifyComponent } from '../../src/library/verify.js';
import { createSimulation } from '../../src/simulation.js';
import { TRANSFORMS, listed, prove } from './proof.js';

type Sequence = Extract<ComponentDef['spec'], { kind: 'sequence' }>;
const VERSIONS: readonly RedstoneVersion[] = ['b1.8.1', '1.12.2'];
const REPEATER = 93;

const SEQUENCES = LIBRARY.filter((d): d is ComponentDef & { spec: Sequence } => d.spec.kind === 'sequence');
const def = (id: string): ComponentDef => LIBRARY.find((d) => d.id === id)!;

/**
 * Plays a layout wired by the harness: `inputs[tick]` toggles those input levers on that tick, and the result is each
 * output's level after every tick, as a string of 0 and 1 starting at tick 1.
 */
function play(layout: ComponentLayout, v: RedstoneVersion, ticks: number, inputs: Record<number, string[]>) {
  const cells = [...layout.cells, ...layout.ports.flatMap((p) => harnessCells(p, 'straight'))];
  const sim = createSimulation({ version: v, cells, ground: { topY: -1 } });
  const out: Record<string, string> = {};
  const outward: Record<string, readonly [number, number]> = {
    north: [0, -1],
    south: [0, 1],
    west: [-1, 0],
    east: [1, 0],
  };
  for (let t = 1; t <= ticks; t++) {
    for (const name of inputs[t] ?? []) {
      const p = layout.ports.find((q) => q.name === name)!;
      const [dx, dz] = outward[p.side]!;
      sim.input({ kind: 'toggle', at: [p.at[0] + dx, p.at[1] + 1, p.at[2] + dz] });
    }
    sim.stepGameTick();
    for (const p of layout.ports)
      if (p.kind === 'out') out[p.name] = (out[p.name] ?? '') + (sim.powerAt(p.at) > 0 ? 1 : 0);
  }
  return out;
}

/** The length of every pulse in a played output, in order. */
const pulses = (levels: string): number[] => levels.match(/1+/g)?.map((s) => s.length) ?? [];

/** A layout with the repeater at `at` set to `gameTicks`. */
function withDelay(layout: ComponentLayout, at: Pos, gameTicks: number): ComponentLayout {
  const cells = layout.cells.map((c) =>
    c[0] === at[0] && c[1] === at[1] && c[2] === at[2] && c[3] === REPEATER
      ? ([c[0], c[1], c[2], c[3], (c[4] & 3) | ((gameTicks / 2 - 1) << 2)] as const)
      : c,
  );
  return { ...layout, cells };
}

/** The pulse shapers' delay repeater (timing.ts). */
const SHAPER_DELAY: Pos = [1, 0, 2];

describe.each(SEQUENCES.map((d) => [d.id, d] as const))('%s', (_id, d) => {
  it('pulses no input for less than its minimum', () => {
    for (const step of d.spec.steps)
      if (step.pulse) expect(step.pulse.ticks).toBeGreaterThanOrEqual(d.spec.minPulseTicks);
  });

  it('drives an input before its first expectation, so the start state never decides the result', () => {
    const first = d.spec.steps.findIndex((s) => s.expect);
    expect(d.spec.steps.slice(0, first + 1).some((s) => s.pulse || s.set)).toBe(true);
  });

  it.each(listed(d).map(([v]) => v))('holds in %s in every placement and with every feeder', (v) => {
    expect(prove(d, v)).toEqual([]);
  });
});

describe('memory starts with a reset', () => {
  it.each(VERSIONS)(
    'the RS latch powers up in either state across placements in %s, and the reset makes it Q = 0',
    (v) => {
      const latch = def('memory.rs-latch');
      const start = new Set<string>();
      for (const { turns, mirror } of TRANSFORMS) {
        const layout = transformLayout(latch.layouts[v]!, turns, mirror, v);
        start.add(play(layout, v, 1, {}).Q!);
      }
      expect([...start].sort()).toEqual(['0', '1']);
      expect(latch.spec.kind === 'sequence' && latch.spec.steps[0]).toMatchObject({
        pulse: { port: 'R' },
        expect: { Q: 0 },
      });
    },
  );
});

describe('the timing traps of report 8.3 (C16)', () => {
  it.each(VERSIONS)('an RS latch input of 2 game ticks is too short in %s; 4 sets it', (v) => {
    const latch = def('memory.rs-latch');
    const layout = latch.layouts[v]!;
    // R at tick 1 for 4 game ticks resets it; then S for 2, later S for 4.
    const q = play(layout, v, 60, { 1: ['R'], 5: ['R'], 21: ['S'], 23: ['S'], 41: ['S'], 45: ['S'] }).Q!;
    expect(q.slice(15, 40)).toBe('0'.repeat(25));
    expect(q.slice(55)).toBe('11111');
  });

  it('the latch spec states the 4 game tick minimum', () => {
    expect(def('memory.rs-latch').spec).toMatchObject({ kind: 'sequence', minPulseTicks: 4 });
  });

  // I AND NOT (I delayed), one rising edge after a long low: the delay repeater decides the pulse.
  it.each([
    ['b1.8.1', 4, []],
    ['1.12.2', 4, []],
    ['b1.8.1', 6, [2]],
    ['1.12.2', 6, [4]],
    ['b1.8.1', 8, [6]],
    ['1.12.2', 8, [6]],
  ] as const)('in %s a limiter delay of %i game ticks gives pulses %j', (v, delay, want) => {
    const layout = withDelay(def('timing.rising-edge').layouts[v]!, SHAPER_DELAY, delay);
    expect(pulses(play(layout, v, 40, { 1: ['I'] }).Y!)).toEqual(want);
  });

  it('the limiter uses 8 game ticks and the rising edge detector 6', () => {
    const delayAt = (id: string) => {
      const c = def(id).layouts['1.12.2']!.cells.find((x) => x[0] === 1 && x[2] === 2 && x[3] === REPEATER)!;
      return ((c[4] >> 2) + 1) * 2;
    };
    expect([delayAt('timing.pulse-limiter'), delayAt('timing.rising-edge')]).toEqual([8, 6]);
  });

  it.each([
    ['b1.8.1', [2, 2]],
    ['1.12.2', [4, 4]],
  ] as const)('the edge detectors give %s pulses of %j game ticks', (v, [rising, falling]) => {
    expect(pulses(play(def('timing.rising-edge').layouts[v]!, v, 40, { 1: ['I'] }).Y!)).toEqual([rising]);
    expect(pulses(play(def('timing.falling-edge').layouts[v]!, v, 60, { 1: ['I'], 21: ['I'] }).Y!)).toEqual([falling]);
  });

  it('a falling edge after only 4 game ticks on is lost in 1.12.2: a stale torch tick relights NOT (I delayed) early', () => {
    const v = '1.12.2';
    const four = play(def('timing.falling-edge').layouts[v]!, v, 40, { 1: ['I'], 5: ['I'] }).Y!;
    const six = play(def('timing.falling-edge').layouts[v]!, v, 40, { 1: ['I'], 7: ['I'] }).Y!;
    expect([pulses(four), pulses(six)]).toEqual([[], [4]]);
    expect(def('timing.falling-edge').spec).toMatchObject({ minPulseTicks: 6 });
  });

  it.each(VERSIONS)('a D latch E pulse of 2 game ticks is too short in %s; the spec asks for 4', (v) => {
    const d = def('memory.d-latch');
    const spec = d.spec as Sequence;
    const short: ComponentDef = {
      ...d,
      spec: {
        ...spec,
        minPulseTicks: 2,
        steps: spec.steps.map((s) => (s.pulse ? { ...s, pulse: { ...s.pulse, ticks: 2 } } : s)),
      },
    };
    expect(verifyComponent(short, v).ok).toBe(false);
    expect(spec.minPulseTicks).toBe(4);
  });
});

describe('the torch T flip-flop', () => {
  const tff = def('memory.t-flip-flop');
  /** The reset's two repeaters into KB (memory.ts): the fast one beside it and the first of the slow path. */
  const FAST_RESET: Pos = [9, 0, 7];
  const SLOW_RESET: Pos = [9, 0, 9];
  const without = (l: ComponentLayout, at: Pos): ComponentLayout => ({
    ...l,
    cells: l.cells.filter((c) => c[0] !== at[0] || c[1] !== at[1] || c[2] !== at[2]),
  });
  const changes = (levels: string): number => [...levels].filter((c, i) => c !== (levels[i - 1] ?? '0')).length;

  it.each(VERSIONS)(
    'turns on 18 game ticks after a rise, off 16 after, resets 12 after R and never flashes, in every placement in %s',
    (v) => {
      // R pulses at 1 and 121 (Q already off) and 181 (Q on); T rises at 41, 81 and 141; every pulse is 4 game ticks.
      const inputs = {
        1: ['R'],
        5: ['R'],
        41: ['T'],
        45: ['T'],
        81: ['T'],
        85: ['T'],
        121: ['R'],
        125: ['R'],
        141: ['T'],
        145: ['T'],
        181: ['R'],
        185: ['R'],
      };
      const want = '0'.repeat(57) + '1'.repeat(38) + '0'.repeat(62) + '1'.repeat(34) + '0'.repeat(29);
      for (const { turns, mirror } of TRANSFORMS)
        expect(play(transformLayout(tff.layouts[v]!, turns, mirror, v), v, 220, inputs).Q).toBe(want);
    },
  );

  it.each(VERSIONS)('needs the slow reset path: without it a reset flashes an off Q on in %s', (v) => {
    const inputs = { 1: ['R'], 5: ['R'], 41: ['R'], 45: ['R'] };
    expect(pulses(play(without(tff.layouts[v]!, SLOW_RESET), v, 80, inputs).Q!.slice(30))).toHaveLength(1);
    expect(play(tff.layouts[v]!, v, 80, inputs).Q).toBe('0'.repeat(80));
  });

  it.each(VERSIONS)(
    'keeps KB powered right through a reset in %s; without the fast path it goes dark for 2 game ticks',
    (v) => {
      // Without the fast path only a same-tick order saves Q: the slow path powers KB on the tick the set torch checks it.
      const KB: Pos = [8, 0, 7];
      const dark = (layout: ComponentLayout): number => {
        const cells = [...layout.cells, ...layout.ports.flatMap((p) => harnessCells(p, 'straight'))];
        const sim = createSimulation({ version: v, cells, ground: { topY: -1 } });
        const lever: Pos = [13, 1, 6];
        let ticks = 0;
        for (let t = 1; t <= 40; t++) {
          if (t === 1 || t === 5) sim.input({ kind: 'toggle', at: lever });
          sim.stepGameTick();
          if (sim.powerAt(KB) === 0) ticks++;
        }
        return ticks;
      };
      expect(dark(tff.layouts[v]!)).toBe(0);
      expect(dark(without(tff.layouts[v]!, FAST_RESET))).toBe(2);
    },
  );

  it.each(VERSIONS)('loses a rise that comes 12 game ticks after the one before (4 on, 8 off) in %s', (v) => {
    const q = play(tff.layouts[v]!, v, 100, { 1: ['R'], 5: ['R'], 41: ['T'], 45: ['T'], 53: ['T'], 57: ['T'] }).Q!;
    expect(changes(q)).not.toBe(2);
    expect(tff.spec).toMatchObject({ kind: 'sequence', minPulseTicks: 4 });
  });
});
