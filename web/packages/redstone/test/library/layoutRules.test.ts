/**
 * Report 4.5's layout rules for every component, in every version it lists and every placement (L7). L2 (any feeder)
 * and the spec itself are proven by truthTables, sequences and clocks, which run every feeder in every placement.
 */
import { describe, expect, it } from 'vitest';
import type { RedstoneVersion } from '../../src/api.js';
import { LIBRARY } from '../../src/library/index.js';
import { transformLayout } from '../../src/library/transform.js';
import { createSimulation } from '../../src/simulation.js';
import { FEEDERS, TRANSFORMS, listed } from './proof.js';
import { PARTS, fits, l1, l3, l5, l6 } from './rules.js';

/** Sorted as `Object.keys(...).sort()` gives them. */
const BOTH: RedstoneVersion[] = ['1.12.2', 'b1.8.1'];

it('lists the components of WP4 in both versions and those of WP5 in 1.12.2', () => {
  expect(LIBRARY.map((d) => [d.id, Object.keys(d.layouts).sort()])).toEqual([
    ...[
      'gate.not',
      'gate.or',
      'gate.nor',
      'gate.and',
      'gate.nand',
      'gate.xor',
      'gate.xnor',
      'memory.rs-latch',
      'memory.d-latch',
      'memory.t-flip-flop',
      'timing.repeater-clock',
      'timing.torch-clock',
      'timing.pulse-limiter',
      'timing.pulse-extender',
      'timing.rising-edge',
      'timing.falling-edge',
      'arithmetic.half-adder',
      'arithmetic.full-adder',
      'routing.mux2',
    ].map((id) => [id, BOTH]),
    ...[
      'memory.lock-cell',
      'memory.lock-t-flip-flop',
      'arithmetic.subtractor',
      'arithmetic.is-stronger',
      'memory.signal-hold',
    ].map((id) => [id, ['1.12.2']]),
  ]);
});

it('gives every component its own id and name and a category that fits it', () => {
  expect(new Set(LIBRARY.map((d) => d.id)).size).toBe(LIBRARY.length);
  expect(new Set(LIBRARY.map((d) => d.name)).size).toBe(LIBRARY.length);
  for (const d of LIBRARY) expect(d.id.startsWith(d.category === 'logic' ? 'gate.' : `${d.category}.`)).toBe(true);
});

describe.each(LIBRARY.map((d) => [d.id, d] as const))('%s', (_id, def) => {
  it('names every port its spec drives or reads, and no port twice', () => {
    for (const [, layout] of listed(def)) {
      const names = layout.ports.map((p) => p.name);
      expect(new Set(names).size).toBe(names.length);
      const spec = def.spec;
      const ins =
        spec.kind === 'truth'
          ? spec.inputs
          : spec.kind === 'sequence'
            ? spec.steps.flatMap((s) => [...Object.keys(s.set ?? {}), ...(s.pulse ? [s.pulse.port] : [])])
            : [];
      const outs =
        spec.kind === 'truth'
          ? spec.outputs
          : spec.kind === 'sequence'
            ? spec.steps.flatMap((s) => Object.keys(s.expect ?? {}))
            : [spec.output];
      for (const n of ins) expect(layout.ports.find((p) => p.name === n)?.kind).toBe('in');
      for (const n of outs) expect(layout.ports.find((p) => p.name === n)?.kind).toBe('out');
    }
  });

  it.each(listed(def).map(([v]) => v))('keeps L1, L3, L5, L6 and fits 32 a side in %s, in every placement', (v) => {
    for (const { turns, mirror } of TRANSFORMS) {
      const layout = transformLayout(def.layouts[v]!, turns, mirror, v);
      const broken = [...l1(layout), ...fits(layout), ...l3(layout, FEEDERS), ...l5(layout, PARTS[v]), ...l6(layout)];
      expect(broken, `${turns} turns, mirror ${mirror ?? '-'}`).toEqual([]);
    }
  });

  it.each(listed(def).map(([v]) => v))(
    'stands on the plot with nothing floating or unknown in %s, in every placement (L4)',
    (v) => {
      for (const { turns, mirror } of TRANSFORMS) {
        const layout = transformLayout(def.layouts[v]!, turns, mirror, v);
        const sim = createSimulation({ version: v, cells: layout.cells, ground: { topY: -1 } });
        expect(sim.diagnostics(), `${turns} turns, mirror ${mirror ?? '-'}`).toEqual([]);
      }
    },
  );
});
