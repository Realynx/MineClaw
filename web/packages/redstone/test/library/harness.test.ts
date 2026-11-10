/** The tools the library is proven with: turning a layout, and the harness that plays a spec against the engine. */
import { describe, expect, it } from 'vitest';
import type { Cell } from '../../src/api.js';
import { compose, draw, input, output } from '../../src/library/draw.js';
import { LIBRARY } from '../../src/library/index.js';
import { transformLayout } from '../../src/library/transform.js';
import type { ComponentDef, ComponentLayout } from '../../src/library/types.js';
import { everyStep, harnessCells, verifyComponent, verifyWith } from '../../src/library/verify.js';
import { TRANSFORMS } from './proof.js';

const STONE = 1;
const DUST = 55;
const LEVER = 69;
const TORCH = 76;
const REPEATER = 93;

/** A repeater (outputs east) into stone with a torch on it: NOT, 3 x 1 x 3. */
const NOT: ComponentLayout = draw(
  [['...', 'E#}', '...']],
  [input('A', 'west', [-1, 0, 1]), output('Y', 'east', [3, 0, 1])],
);
const notDef = (layout: ComponentLayout, rows: Array<readonly [number[], number[]]>): ComponentDef => ({
  id: 'test.not',
  name: 'NOT',
  category: 'logic',
  description: '',
  layouts: { '1.12.2': layout },
  spec: { kind: 'truth', inputs: ['A'], outputs: ['Y'], rows, settleTicks: 4 },
});
const sorted = (cells: readonly Cell[]) => [...cells].sort((a, b) => a[1] - b[1] || a[2] - b[2] || a[0] - b[0]);

describe('drawing a layout', () => {
  it('reads rows west to east, north to south, one layer per height', () => {
    const l = draw(
      [
        ['E#', '.v'],
        ['+.', '..'],
      ],
      [],
    );
    expect(l.size).toEqual([2, 2, 2]);
    expect(l.cells).toEqual([
      [0, 0, 0, REPEATER, 1],
      [1, 0, 0, STONE, 0],
      [1, 0, 1, TORCH, 3],
      [0, 1, 0, DUST, 0],
    ]);
  });

  it('refuses an unknown character, a ragged row and two parts on one cell', () => {
    expect(() => draw([['?']], [])).toThrow(/'\?'/);
    expect(() => draw([['..', '.']], [])).toThrow(/row 1/);
    expect(() => compose([3, 1, 3], [{ layout: NOT, at: [0, 0, 0] }], [[1, 0, 1, '+']], [])).toThrow(/\(1,0,1\)/);
  });
});

describe('turning and mirroring a layout', () => {
  it('turns a quarter clockwise: north becomes east, the box turns with it, metas and ports follow', () => {
    const l = transformLayout(NOT, 1, null, '1.12.2');
    expect(l.size).toEqual([3, 1, 3]);
    // The repeater put out east at (0,0,1); now it puts out south at (1,0,0). The torch hung on its west block, now north.
    expect(l.cells).toEqual([
      [1, 0, 0, REPEATER, 2],
      [1, 0, 1, STONE, 0],
      [1, 0, 2, TORCH, 3],
    ]);
    expect(l.ports).toEqual([
      { name: 'A', kind: 'in', at: [1, 0, -1], side: 'north' },
      { name: 'Y', kind: 'out', at: [1, 0, 3], side: 'south' },
    ]);
  });

  it('swaps the box sides on an odd turn and keeps port cells just outside the box', () => {
    const wide = draw([['E#}+', '....']], [input('A', 'west', [-1, 0, 0]), output('Y', 'east', [4, 0, 0])]);
    const l = transformLayout(wide, 1, null, 'b1.8.1');
    expect(l.size).toEqual([2, 1, 4]);
    expect(l.ports.map((p) => [p.at, p.side])).toEqual([
      [[1, 0, -1], 'north'],
      [[1, 0, 4], 'south'],
    ]);
  });

  it('mirrors before it turns, and comes back to the start', () => {
    const xor = LIBRARY.find((d) => d.id === 'gate.xor')!.layouts['1.12.2']!;
    const back = (l: ComponentLayout) => ({ ...l, cells: sorted(l.cells) });
    expect(back(transformLayout(transformLayout(xor, 1, null, '1.12.2'), 3, null, '1.12.2'))).toEqual(back(xor));
    expect(back(transformLayout(transformLayout(xor, 0, 'x', '1.12.2'), 0, 'x', '1.12.2'))).toEqual(back(xor));
    expect(transformLayout(xor, 2, 'x', '1.12.2')).toEqual(transformLayout(xor, 0, 'z', '1.12.2'));
    const m = transformLayout(NOT, 0, 'x', '1.12.2');
    expect(m.cells).toEqual([
      [0, 0, 1, TORCH, 2],
      [1, 0, 1, STONE, 0],
      [2, 0, 1, REPEATER, 3],
    ]);
    expect(m.ports.map((p) => [p.at, p.side])).toEqual([
      [[3, 0, 1], 'east'],
      [[-1, 0, 1], 'west'],
    ]);
  });

  it('gives twelve different placements of an asymmetric layout', () => {
    const half = LIBRARY.find((d) => d.id === 'arithmetic.half-adder')!.layouts['b1.8.1']!;
    const seen = new Set(
      TRANSFORMS.map(({ turns, mirror }) => JSON.stringify(transformLayout(half, turns, mirror, 'b1.8.1'))),
    );
    // Mirror x turned twice is mirror z: eight placements are distinct.
    expect(seen.size).toBe(8);
  });
});

describe('the harness', () => {
  it('wires an input as dust, a stone further out and a lever on it; a spur goes left or right facing the box', () => {
    const a = NOT.ports[0]!;
    expect(harnessCells(a, 'straight')).toEqual([
      [-1, 0, 1, DUST, 0],
      [-2, 0, 1, STONE, 0],
      [-2, 1, 1, LEVER, 5],
    ]);
    // Facing east from the west port, left is north.
    expect(harnessCells(a, 'spurLeft')[3]).toEqual([-1, 0, 0, DUST, 0]);
    expect(harnessCells(a, 'spurRight')[3]).toEqual([-1, 0, 2, DUST, 0]);
    expect(harnessCells(NOT.ports[1]!, 'spurLeft')).toEqual([[3, 0, 1, DUST, 0]]);
  });

  it('walks a truth table through every step from one row to another, once each', () => {
    for (const n of [2, 4, 8]) {
      const walk = everyStep(n);
      expect(walk).toHaveLength(n * (n - 1) + 1);
      const steps = walk.slice(1).map((to, i) => `${walk[i]}>${to}`);
      expect(new Set(steps).size).toBe(n * (n - 1));
      expect(steps.every((s) => s.split('>')[0] !== s.split('>')[1])).toBe(true);
    }
  });

  it('passes a right table and names the row of a wrong one', () => {
    expect(
      verifyComponent(
        notDef(NOT, [
          [[0], [1]],
          [[1], [0]],
        ]),
        '1.12.2',
      ),
    ).toEqual({ ok: true, failures: [] });
    const wrong = verifyComponent(
      notDef(NOT, [
        [[0], [1]],
        [[1], [1]],
      ]),
      '1.12.2',
    );
    expect(wrong.ok).toBe(false);
    expect(wrong.failures[0]).toMatch(/^A=1 after A=0: Y is 0 4 game ticks later, expected 1$/);
  });

  it('fails a version without a layout, a port the spec does not have and a feeder that hits the layout', () => {
    expect(verifyComponent(notDef(NOT, [[[0], [1]]]), 'b1.8.1')).toEqual({
      ok: false,
      failures: ['test.not has no layout for b1.8.1'],
    });
    const renamed = { ...NOT, ports: [input('B', 'west', [-1, 0, 1]), NOT.ports[1]!] };
    expect(
      verifyComponent(
        notDef(renamed, [
          [[0], [1]],
          [[1], [0]],
        ]),
        '1.12.2',
      ).failures,
    ).toContain('the spec names input A, which has no port');
    // A stone where the north spur of A would go.
    const crowded = { ...NOT, cells: [...NOT.cells, [-1, 0, 0, STONE, 0] as Cell] };
    expect(
      verifyComponent(
        notDef(crowded, [
          [[0], [1]],
          [[1], [0]],
        ]),
        '1.12.2',
        'spurLeft',
      ).failures,
    ).toEqual(['the spurLeft feeder of A needs (-1,0,0), which the layout holds']);
  });

  it('fails a layout the engine reports on: a torch with nothing to hang on', () => {
    const floating = { ...NOT, cells: NOT.cells.filter((c) => c[3] !== STONE) };
    const r = verifyComponent(
      notDef(floating, [
        [[0], [1]],
        [[1], [0]],
      ]),
      '1.12.2',
    );
    expect(r.failures[0]).toMatch(/^floating at \(2,0,1\)/);
  });

  it('fails a sequence that pulses under its own minimum, and a clock with the wrong period', () => {
    const seq: ComponentDef = {
      ...notDef(NOT, []),
      spec: {
        kind: 'sequence',
        minPulseTicks: 4,
        steps: [{ pulse: { port: 'A', ticks: 2 }, wait: 10, expect: { Y: 1 } }],
      },
    };
    expect(verifyComponent(seq, '1.12.2').failures).toEqual(['step 1 pulses for 2 game ticks, under the 4 minimum']);
    const clock = LIBRARY.find((d) => d.id === 'timing.repeater-clock')!;
    const slow = { ...clock, spec: { kind: 'clock' as const, output: 'Y', periodTicks: 24, highTicks: 10 } };
    expect(verifyComponent(slow, '1.12.2').ok).toBe(false);
  });

  it('fails a layout the engine flags while the spec runs, even when its outputs read right', () => {
    const rows: Array<readonly [number[], number[]]> = [
      [[0], [1]],
      [[1], [0]],
    ];
    for (const v of ['b1.8.1', '1.12.2'] as const) {
      const def: ComponentDef = { ...notDef(NOT, rows), layouts: { [v]: NOT } };
      // A budget the built circuit stays under: the NOT is proven.
      expect(verifyWith(def, v, 'straight', { updateLimit: 10 })).toEqual({ ok: true, failures: [] });
      // One update less: building is still quiet and every row reads right, but a tick of the run is cut short.
      expect(
        verifyWith({ ...def, spec: { ...def.spec, rows: [] } } as ComponentDef, v, 'straight', { updateLimit: 9 }).ok,
      ).toBe(true);
      expect(verifyWith(def, v, 'straight', { updateLimit: 9 })).toEqual({
        ok: false,
        failures: [expect.stringMatching(/^unstable at \(2,0,1\): More than 9 updates in one game tick/)],
      });
    }
    // A run the engine stops early is a failure of its own.
    const b181: ComponentDef = { ...notDef(NOT, rows), layouts: { 'b1.8.1': NOT } };
    expect(verifyWith(b181, 'b1.8.1', 'straight', { updateLimit: 5 }).failures[0]).toBe(
      'the engine stopped after 2 of 4 game ticks, on tick 6',
    );
  });

  it('fails a torch that burns out', () => {
    // Eight pulses of 4 game ticks, one every 7: the NOT torch turns off for the eighth time within 56 game ticks.
    const steps = Array.from({ length: 8 }, () => ({ pulse: { port: 'A', ticks: 4 }, wait: 3 }));
    for (const v of ['b1.8.1', '1.12.2'] as const) {
      const def: ComponentDef = {
        ...notDef(NOT, []),
        layouts: { [v]: NOT },
        spec: { kind: 'sequence', minPulseTicks: 4, steps },
      };
      expect(verifyComponent(def, v).failures).toEqual([
        expect.stringMatching(/^the torch at \(2,0,1\) burned out on tick \d+$/),
      ]);
    }
  });
});
