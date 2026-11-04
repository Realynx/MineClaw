import { describe, expect, it } from 'vitest';
import * as C from '../src/index.js';
import { cloudMockDistance, memoryCloudMock, type CloudMockNode } from '../src/testing.js';

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

  it('fakes every arrangement over the same points, with fidelity and the arrangement list', () => {
    const mds = memoryCloudMock(tree());
    expect(mds.arrangement).toBe('mds');
    expect(mds.fidelity?.pearson).toBeGreaterThan(0.8);
    expect(mds.arrangements.map((a) => a.id)).toEqual(['mds', 'umap', 'pca', 'tree']);
    for (const a of ['umap', 'pca', 'tree'] as const) {
      const c = memoryCloudMock(tree(), { arrangement: a });
      expect(() => C.MemoryCloudResponse.parse(c)).not.toThrow();
      expect(c.arrangement).toBe(a);
      expect(c.points.id).toEqual(mds.points.id);
      expect(c.points.xy).not.toEqual(mds.points.xy);
      expect(c.clusters.map((x) => x.node)).toEqual(mds.clusters.map((x) => x.node));
    }
    expect(memoryCloudMock(tree(), { arrangement: 'pca' }).fidelity?.varianceExplained).toHaveLength(2);
  });

  it('answers a symmetric mock distance near the drawn one', () => {
    const mds = memoryCloudMock(tree());
    const [a, b] = [mds.points.id[3]!, mds.points.id[200]!];
    const d = cloudMockDistance(mds, a, b)!;
    expect(() => C.CloudDistanceResponse.parse(d)).not.toThrow();
    expect(cloudMockDistance(mds, b, a)!.distance).toBe(d.distance);
    expect(cloudMockDistance(mds, a, a)!.distance).toBe(0);
    expect(cloudMockDistance(mds, a, 99_999)).toBeNull();
    const drawn = Math.hypot(mds.points.xy[6]! - mds.points.xy[400]!, mds.points.xy[7]! - mds.points.xy[401]!) * 1.35;
    expect(d.distance).toBeGreaterThan(drawn * 0.84);
    expect(d.distance).toBeLessThan(drawn * 1.16);
  });
});
