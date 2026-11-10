/** The combinational components: their tables are the right ones, and the engine proves them in every placement. */
import { describe, expect, it } from 'vitest';
import { LIBRARY } from '../../src/library/index.js';
import type { ComponentDef } from '../../src/library/types.js';
import { listed, prove } from './proof.js';

type Truth = Extract<ComponentDef['spec'], { kind: 'truth' }>;

/** What each table must say, written apart from the definitions so a slip in one shows up against the other. */
const MEANS: Readonly<Record<string, (...x: number[]) => number[]>> = {
  'gate.not': (a) => [1 - a],
  'gate.or': (a, b) => [a | b],
  'gate.nor': (a, b) => [1 - (a | b)],
  'gate.and': (a, b) => [a & b],
  'gate.nand': (a, b) => [1 - (a & b)],
  'gate.xor': (a, b) => [a ^ b],
  'gate.xnor': (a, b) => [1 - (a ^ b)],
  'arithmetic.half-adder': (a, b) => [a ^ b, a & b],
  'arithmetic.full-adder': (a, b, c) => [a ^ b ^ c, a + b + c >= 2 ? 1 : 0],
  'routing.mux2': (sel, a, b) => [sel ? b : a],
  'arithmetic.subtractor': (a, b) => [Math.max(a - b, 0)],
  'arithmetic.is-stronger': (a, b) => [a > b ? 1 : 0],
};

const TRUTH = LIBRARY.filter((d): d is ComponentDef & { spec: Truth } => d.spec.kind === 'truth');

it('has a table for every combinational component', () => {
  expect(TRUTH.map((d) => d.id).sort()).toEqual(Object.keys(MEANS).sort());
});

describe.each(TRUTH.map((d) => [d.id, d] as const))('%s', (id, def) => {
  it('lists every input combination once, with the outputs its name means', () => {
    const { inputs, outputs, rows, analog = [] } = def.spec;
    // An analog input walks a few levels, the same ones in every row; a binary one is 0 and 1.
    const levels = inputs.map((name, i) => (analog.includes(name) ? new Set(rows.map(([ins]) => ins[i])).size : 2));
    for (const [i, name] of inputs.entries())
      if (analog.includes(name))
        expect(rows.some(([ins]) => ins[i] === 0) && rows.some(([ins]) => ins[i] === 15)).toBe(true);
    expect(rows).toHaveLength(levels.reduce((a, b) => a * b, 1));
    expect(new Set(rows.map(([ins]) => ins.join(','))).size).toBe(rows.length);
    for (const [ins, outs] of rows) {
      expect(ins).toHaveLength(inputs.length);
      expect(outs).toEqual(MEANS[id]!(...ins));
      expect(outs).toHaveLength(outputs.length);
    }
  });

  it.each(listed(def).map(([v]) => v))(
    'holds in %s in every placement, from every other row, with every feeder',
    (v) => {
      expect(prove(def, v)).toEqual([]);
    },
  );
});
