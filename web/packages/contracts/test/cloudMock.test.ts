import { describe, expect, it } from 'vitest';
import * as C from '../src/index.js';
import { memoryCloudMock, type CloudMockNode } from '../src/testing.js';

/** 2 roots → 8 L2 → 32 L1 → 320 leaves. */
function tree(): CloudMockNode[] {
  const out: CloudMockNode[] = [];
  for (let i = 0; i < 320; i++)
    out.push({
      id: i,
      layer: 0,
      parents: [400 + Math.floor(i / 10)],
      title: `memory ${i}`,
      kind: i % 3 ? 'episode' : 'fact',
    });
  for (let j = 0; j < 32; j++)
    out.push({ id: 400 + j, layer: 1, parents: [500 + Math.floor(j / 4)], title: `L1 ${j}`, kind: 'summary' });
  for (let k = 0; k < 8; k++)
    out.push({
      id: 500 + k,
      layer: 2,
      parents: [600 + Math.floor(k / 4)],
      title: `L2 ${k}`,
      kind: 'summary',
      terms: [`topic${k}`, 'x'],
    });
  for (let m = 0; m < 2; m++) out.push({ id: 600 + m, layer: 3, parents: [], title: `root ${m}`, kind: 'summary' });
  return out;
}

describe('memoryCloudMock', () => {
  it('matches the contract, with parallel columns and the bridge cluster rule', () => {
    const c = memoryCloudMock(tree(), { extraLeaves: 500, extraFrom: 10_000 });
    expect(() => C.MemoryCloudResponse.parse(c)).not.toThrow();
    const n = c.points.id.length;
    expect(n).toBe(820);
    expect(c.points.xy).toHaveLength(2 * n);
    for (const k of ['cluster', 'kind', 'title', 'parents', 'inserted'] as const) expect(c.points[k]).toHaveLength(n);
    expect(c.clusterLayer).toBe(2); // L3 has 2 nodes (< 6), L2 has 8
    expect(c.clusters).toHaveLength(8);
    expect(c.clusters.map((x) => x.size).reduce((a, b) => a + b, 0)).toBe(n);
    expect(c.clusters[0]!.size).toBeGreaterThanOrEqual(c.clusters[7]!.size);
    expect(c.clusters.find((x) => x.node === 503)?.label).toBe('topic3 · x');
    for (const v of c.points.xy) expect(v).toBeGreaterThanOrEqual(0);
    expect(c.points.inserted.filter(Boolean)).toHaveLength(500);
  });

  it('is deterministic', () => {
    expect(memoryCloudMock(tree(), { extraLeaves: 50 })).toEqual(memoryCloudMock(tree(), { extraLeaves: 50 }));
  });
});
