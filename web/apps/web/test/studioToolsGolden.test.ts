import { afterEach, describe, expect, it } from 'vitest';
import { Canvas, connected, EARTH, setPlotMode, type Cell, type V3 } from '@/features/studio/canvas';
import { MineTerrain } from '@/features/studio/mine/mineTerrain';
import { searchPalette } from '@/features/studio/palette';
import { boxCells, brush, faceArea, type BoxMode, type BrushMode, type BrushShape } from '@/features/studio/tools';
import { MOCK_DESIGNER_PALETTE } from '@mineclaw/contracts';

/** A small deterministic stream (mulberry32). */
function rng(seed: number) {
  let a = seed | 0;
  return (n: number) => {
    a = (a + 0x6d2b79f5) | 0;
    let t = Math.imul(a ^ (a >>> 15), 1 | a);
    t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
    return ((t ^ (t >>> 14)) >>> 0) % n;
  };
}

const hash = (rows: ReadonlyArray<readonly number[]>): string => {
  let h = 2166136261;
  for (const r of rows) for (const v of r) h = Math.imul(h ^ (v & 0xffff), 16777619) >>> 0;
  return `${rows.length}:${h}`;
};

/** A lumpy structure around (20, 0, 20): stone, dirt, planks, a few chests, doors and beds, and some dug ground. */
function sample(): Canvas {
  const r = rng(7);
  const c = new Canvas();
  const ids = [1, 3, 5, 4, 17];
  const cells: Cell[] = [];
  for (let i = 0; i < 900; i++) cells.push([12 + r(16), -4 + r(12), 12 + r(16), ids[r(ids.length)]!, r(3)]);
  c.apply(cells);
  c.apply([
    [25, 0, 25, 54, 2],
    [26, 0, 25, 54, 3],
    [25, 0, 27, 54, 4],
    [30, 0, 30, 64, 1],
    [32, 0, 30, 26, 0],
    [14, -2, 14, 0, 0],
    [14, -3, 14, 0, 0],
  ]);
  return c;
}

afterEach(() => setPlotMode({ kind: 'build' }));

describe('studio tools (golden, pinned before they were split)', () => {
  it('boxes in every mode', () => {
    const out: string[] = [];
    const boxes: Array<[V3, V3]> = [
      [
        [0, 0, 0],
        [0, 0, 0],
      ],
      [
        [3, 1, 2],
        [7, 4, 9],
      ],
      [
        [9, 5, 1],
        [2, -3, 4],
      ],
    ];
    for (const [a, b] of boxes)
      for (const mode of ['fill', 'hollow', 'walls', 'outline'] as BoxMode[]) out.push(`${mode} ${hash(boxCells(a, b, 5, 2, mode))}`);
    expect(out).toMatchSnapshot();
  });

  it('brushes in every mode, shape and size, digging or not', () => {
    const c = sample();
    const out: string[] = [];
    const modes: BrushMode[] = ['add', 'remove', 'paint', 'replace', 'smooth'];
    const shapes: BrushShape[] = ['sphere', 'cube'];
    for (const centre of [
      [20, 2, 20],
      [14, -2, 15],
      [0, 0, 0],
    ] as V3[])
      for (const mode of modes)
        for (const shape of shapes)
          for (const size of [1, 2, 3, 5])
            for (const dig of [false, true])
              out.push(`${centre.join(",")} ${mode} ${shape} ${size} ${dig} ${hash(brush(c, centre, size, shape, mode, 98, 1, 3, dig))}`);
    expect(out).toMatchSnapshot();
  });

  it('face areas from every face, same type or any', () => {
    const c = sample();
    const out: string[] = [];
    const r = rng(3);
    for (let i = 0; i < 25; i++) {
      const hit: V3 = [12 + r(16), -4 + r(12), 12 + r(16)];
      for (let face = 0; face < 7; face++)
        for (const same of [true, false]) out.push(`${hit.join(",")} ${face} ${same} ${hash(faceArea(c, hit, face, same, 300))}`);
    }
    expect(out).toMatchSnapshot();
  });

  it('applies edits (chests pair, doors and beds stay whole, earth digs and fills) the same', () => {
    const c = sample();
    const r = rng(11);
    const out: string[] = [];
    const ids = [0, 54, 64, 26, 1, EARTH, 146, 71];
    for (let i = 0; i < 60; i++) {
      const ch = c.apply([[18 + r(14), -3 + r(6), 18 + r(14), ids[r(ids.length)]!, r(16)]], i % 5 !== 0);
      out.push(`${hash(ch.forward)} ${hash(ch.backward)}`);
    }
    out.push(hash(c.cells()), hash(c.holes()));
    expect(out).toMatchSnapshot();
  });

  it('floods connected blocks the same', () => {
    const c = sample();
    const r = rng(5);
    const out: string[] = [];
    for (let i = 0; i < 20; i++) {
      const at: V3 = [12 + r(16), -4 + r(12), 12 + r(16)];
      out.push(`${at.join(",")} ${connected(c, at, true).size} ${connected(c, at, false).size}`);
    }
    expect(out).toMatchSnapshot();
  });

  it('a mine plot: brushes and edits over the terrain', () => {
    const t = new MineTerrain('12345', 'b1.8.1');
    setPlotMode({ kind: 'mine', terrain: t });
    const c = new Canvas();
    const out: string[] = [];
    for (const mode of ['add', 'remove', 'paint', 'replace', 'smooth'] as BrushMode[])
      for (const dig of [false, true]) out.push(`${mode} ${dig} ${hash(brush(c, [20, 60, 20], 3, 'sphere', mode, 1, 0, 1, dig))}`);
    const ch = c.apply([
      [20, 3, 20, 0, 0],
      [20, 1, 20, 0, 0],
      [21, 60, 21, 0, 0],
      [21, 60, 21, EARTH, 0],
    ]);
    out.push(hash(ch.forward), hash(ch.backward));
    expect(out).toMatchSnapshot();
  });

  it('searches the palette the same', () => {
    const out = ['', 'stone', 'oak plank', '5:2', '17', 'red', 'wool white', 'zzz', 'st', 'col', 'door, wood'].map((q) =>
      searchPalette(MOCK_DESIGNER_PALETTE, q)
        .map((e) => `${e.id}:${e.meta}`)
        .join(' '),
    );
    expect(out).toMatchSnapshot();
  });
});
