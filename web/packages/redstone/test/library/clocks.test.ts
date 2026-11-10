/** The clocks: period and high time in every placement, and they keep time without burning a torch out. */
import { describe, expect, it } from 'vitest';
import type { RedstoneVersion } from '../../src/api.js';
import { LIBRARY } from '../../src/library/index.js';
import { transformLayout } from '../../src/library/transform.js';
import type { ComponentDef } from '../../src/library/types.js';
import { createSimulation } from '../../src/simulation.js';
import { TRANSFORMS, listed, prove } from './proof.js';

type Clock = Extract<ComponentDef['spec'], { kind: 'clock' }>;
const CLOCKS = LIBRARY.filter((d): d is ComponentDef & { spec: Clock } => d.spec.kind === 'clock');
const DUST = 55;
const REPEATER = 93;

/** Runs a layout with dust on its output port; returns the output after every tick and every event's kind. */
function run(
  def: ComponentDef & { spec: Clock },
  v: RedstoneVersion,
  turns: number,
  mirror: 'x' | 'z' | null,
  ticks: number,
) {
  const layout = transformLayout(def.layouts[v]!, turns, mirror, v);
  const port = layout.ports.find((p) => p.name === def.spec.output)!;
  const sim = createSimulation({
    version: v,
    cells: [...layout.cells, [port.at[0], port.at[1], port.at[2], DUST, 0]],
    ground: { topY: -1 },
  });
  let levels = '';
  const events: string[] = [];
  for (let t = 0; t < ticks; t++) {
    events.push(...sim.stepGameTick().events.map((e) => e.kind));
    levels += sim.powerAt(port.at) > 0 ? 1 : 0;
  }
  return { levels, events };
}

it('has the two clocks of the plan', () => {
  expect(CLOCKS.map((d) => d.id)).toEqual(['timing.repeater-clock', 'timing.torch-clock']);
  expect(CLOCKS.map((d) => d.spec.periodTicks)).toEqual([20, 36]);
});

describe.each(CLOCKS.map((d) => [d.id, d] as const))('%s', (_id, def) => {
  it.each(listed(def).map(([v]) => v))('keeps its period and high time in %s in every placement', (v) => {
    expect(prove(def, v)).toEqual([]);
  });

  it.each(listed(def).map(([v]) => v))(
    'runs 2000 game ticks in %s in every placement without a torch burning out',
    (v) => {
      for (const { turns, mirror } of TRANSFORMS) {
        const { levels, events } = run(def, v, turns, mirror, 2000);
        expect(events).toEqual([]);
        const high = '1'.repeat(def.spec.highTicks);
        const low = '0'.repeat(def.spec.periodTicks - def.spec.highTicks);
        // After the first cycle, nothing but whole cycles.
        const from = levels.indexOf(`0${high}0`) + 1;
        expect(levels.slice(from, from + 1800)).toBe(
          (high + low).repeat(Math.ceil(1800 / def.spec.periodTicks)).slice(0, 1800),
        );
      }
    },
  );
});

it('a ring of five torches without the repeater does not keep one wave in 1.12.2: that is what the repeater is for', () => {
  const clock = CLOCKS.find((d) => d.id === 'timing.torch-clock')!;
  const layout = clock.layouts['1.12.2']!;
  // The repeater's place in the ring becomes plain dust.
  const bare: ComponentDef = {
    ...clock,
    spec: { kind: 'clock', output: 'Y', periodTicks: 20, highTicks: 10 },
    layouts: {
      '1.12.2': { ...layout, cells: layout.cells.map((c) => (c[3] === REPEATER ? [c[0], c[1], c[2], DUST, 0] : c)) },
    },
  };
  expect(prove(bare, '1.12.2').length).toBeGreaterThan(0);
});
