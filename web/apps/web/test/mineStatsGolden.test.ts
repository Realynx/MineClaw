import { describe, expect, it } from 'vitest';
import { mineStats, xrayCells } from '@/features/studio/mine/mineStats';
import { MineTerrain } from '@/features/studio/mine/mineTerrain';
import { spiral, strip } from '@/features/studio/mine/mineTemplates';

const hash = (rows: ReadonlyArray<readonly number[]>): number => {
  let h = 2166136261;
  for (const r of rows) for (const v of r) h = Math.imul(h ^ v, 16777619) >>> 0;
  return h;
};

describe('mine stats and the X-ray layer (golden, pinned before they were split)', () => {
  it('estimates a dig the same', () => {
    const out: unknown[] = [];
    for (const [seed, v] of [
      ['12345', 'b1.8.1'],
      ['hello', '1.12.2'],
    ] as const) {
      const t = new MineTerrain(seed, v);
      const dug = [
        ...spiral({ x: 10, z: 10, top: 70, bottom: 5, size: 5 }).cells,
        ...strip({ x: 2, y: 12, z: 40, length: 50, torchEvery: 4 }).cells,
      ].filter((c) => c[3] === 0);
      out.push(mineStats(t, dug.map(([x, y, z]) => [x, y, z] as const)));
      out.push(mineStats(t, []));
    }
    expect(JSON.parse(JSON.stringify(out))).toMatchSnapshot();
  });

  it('lists the same X-ray cells', () => {
    const out: string[] = [];
    for (const [seed, v] of [
      ['12345', 'b1.8.1'],
      ['hello', '1.12.2'],
    ] as const) {
      const t = new MineTerrain(seed, v);
      for (const [w, max] of [
        [null, 6000],
        [null, 50],
        [{ lo: 5, hi: 20 }, 6000],
        [{ lo: -3, hi: 200 }, 300],
        [{ lo: 30, hi: 10 }, 6000],
      ] as const) {
        const cells = xrayCells(t, w, max);
        out.push(`${cells.length}:${hash(cells)}`);
      }
    }
    expect(out).toMatchSnapshot();
  });
});
