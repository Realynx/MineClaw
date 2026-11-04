import { describe, expect, it } from 'vitest';
import { MineTerrain } from '@/features/studio/mine/mineTerrain';

/** FNV-1a over a chunk's states. */
function chunkHash(t: MineTerrain, cx: number, cz: number): number {
  let h = 2166136261;
  for (const v of t.chunk(cx, cz)) h = Math.imul(h ^ v, 16777619) >>> 0;
  return h;
}

const SEEDS = ['0', '1', '-7', '999999', 'abc', '-9223372036854775808'];
const VERSIONS = ['b1.8.1', '1.12.2'];
const CHUNKS: Array<[number, number]> = [
  [0, 0],
  [3, 1],
  [-1, -2],
  [-5, 4],
];

describe('the mine terrain generator over many seeds (golden, pinned before the generator was split)', () => {
  it('generates the same chunks', () => {
    const out: Record<string, number[]> = {};
    for (const seed of SEEDS)
      for (const v of VERSIONS) {
        const t = new MineTerrain(seed, v);
        out[`${seed}|${v}`] = CHUNKS.map(([cx, cz]) => chunkHash(t, cx, cz));
      }
    expect(out).toMatchSnapshot();
  });
});
