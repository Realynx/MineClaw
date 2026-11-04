import { afterEach, describe, expect, it } from 'vitest';
import { Canvas, setPlotMode, type Cell } from '@/features/studio/canvas';
import { MineTerrain } from '@/features/studio/mine/mineTerrain';
import { StudioWorld } from '@/features/studio/studioWorld';

function bytesHash(buf: ArrayBuffer): string {
  const b = new Uint8Array(buf);
  let h = 2166136261;
  for (let i = 0; i < b.length; i++) h = Math.imul(h ^ b[i]!, 16777619) >>> 0;
  return `${b.length}:${h}`;
}

/** A few blocks on and under the grass, glowstone and torches (light), and dug ground, across two columns. */
function canvas(): Canvas {
  const c = new Canvas();
  const cells: Cell[] = [];
  for (let x = 10; x < 22; x++) for (let y = 0; y < 5; y++) cells.push([x, y, 14, x % 3 === 0 ? 20 : 4, 0]);
  cells.push([12, 6, 12, 89, 0], [30, 0, 30, 50, 5], [31, 2, 31, 91, 1], [5, -2, 5, 1, 0], [40, 10, 40, 5, 2]);
  c.apply(cells);
  c.apply([
    [8, -1, 8, 0, 0],
    [8, -2, 8, 0, 0],
    [8, -3, 8, 0, 0],
    [17, -1, 33, 0, 0],
  ]);
  return c;
}

const COLS: Array<[number, number]> = [
  [0, 0],
  [1, 0],
  [2, 2],
  [-2, 1],
  [5, 5],
  [6, 0],
];

afterEach(() => setPlotMode({ kind: 'build' }));

describe('the studio world encoding (golden, pinned before encode was split)', () => {
  it('encodes the grassland plot the same in both versions', async () => {
    const out: string[] = [];
    for (const v of ['b1.8.1', '1.12.2'] as const) {
      const w = new StudioWorld(v, canvas(), () => 1000);
      out.push(bytesHash(await w.source.chunks(0, COLS)));
      out.push(bytesHash(await w.source.chunks(1, [[0, 0]])));
    }
    expect(out).toMatchSnapshot();
  });

  it('encodes a mine plot over its terrain the same', async () => {
    const t = new MineTerrain('12345', 'b1.8.1');
    setPlotMode({ kind: 'mine', terrain: t });
    const c = new Canvas();
    c.apply([
      [20, 60, 20, 0, 0],
      [20, 59, 20, 0, 0],
      [21, 40, 21, 0, 0],
      [22, 70, 22, 4, 0],
    ]);
    const out: string[] = [];
    for (const v of ['b1.8.1', '1.12.2'] as const) out.push(bytesHash(await new StudioWorld(v, c, () => 1000, t).source.chunks(0, COLS)));
    expect(out).toMatchSnapshot();
  });
});
