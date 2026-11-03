import { describe, expect, it } from 'vitest';
import {
  CLUSTER_PALETTE,
  OTHER_COLOR,
  buildGrid,
  cloudBrightness,
  cloudParents,
  clusterColor,
  lassoSelect,
  legendEntries,
  legendKey,
  matchesQuery,
  nearest,
  pointLook,
  toggleLegend,
  visibleMask,
} from '@/features/memory-activity/cloud/cloudStyle';
import { memoryCloud } from '@/mocks/data/cloud';

const MIN = 60_000;

describe('cloud colour', () => {
  it('follows the cluster in fixed slots per theme, folding the ninth on into "other"', () => {
    expect(clusterColor(0, 'light')).toBe(CLUSTER_PALETTE.light[0]);
    expect(clusterColor(7, 'dark')).toBe(CLUSTER_PALETTE.dark[7]);
    expect(clusterColor(8, 'dark')).toBe(OTHER_COLOR.dark);
    expect(clusterColor(-1, 'light')).toBe(OTHER_COLOR.light);
    expect(legendKey(3)).toBe(3);
    expect(legendKey(12)).toBe(-1);
  });
});

describe('cloud brightness', () => {
  const t = 1_000_000_000;
  it('is the heat decay, bright when just used', () => {
    expect(cloudBrightness({ at: t, brain: 'main' }, t, 120, 15 * MIN)).toBeCloseTo(1);
    expect(cloudBrightness({ at: t - 120_000, brain: 'main' }, t, 120, 15 * MIN)).toBeCloseTo(0.5);
  });
  it('keeps a faint tail across the window, then goes dark', () => {
    const tenMin = cloudBrightness({ at: t - 10 * MIN, brain: 'main' }, t, 120, 15 * MIN);
    expect(tenMin).toBeGreaterThan(0.05);
    expect(tenMin).toBeLessThan(0.2);
    expect(cloudBrightness({ at: t - 16 * MIN, brain: 'main' }, t, 120, 15 * MIN)).toBe(0);
    expect(cloudBrightness(undefined, t, 120, 15 * MIN)).toBe(0);
    // in replay, an access after the scrub time does not count yet
    expect(cloudBrightness({ at: t + 1000, brain: 'main' }, t, 120, 15 * MIN)).toBe(0);
  });
  it('maps to alpha, size and a halo; unused memories stay visible but recede', () => {
    const dim = pointLook(0, 1);
    const hot = pointLook(1, 1);
    expect(dim.alpha).toBeGreaterThan(0.2);
    expect(hot.alpha).toBeGreaterThan(dim.alpha);
    expect(hot.size).toBeGreaterThan(dim.size);
    expect(dim.halo).toBe(0);
    expect(hot.halo).toBeGreaterThan(0);
    expect(pointLook(1, 1, { dimmed: true }).alpha).toBeLessThan(0.2);
    expect(pointLook(0, 1, { selected: true }).alpha).toBe(1);
    expect(pointLook(0, 8).size).toBeGreaterThan(pointLook(0, 0.5).size);
  });
});

describe('cloud legend', () => {
  const cloud = memoryCloud();
  it('lists the mock clusters largest first, every point accounted for', () => {
    const rows = legendEntries(cloud, 'dark');
    expect(rows).toHaveLength(8);
    expect(rows.reduce((s, r) => s + r.size, 0)).toBe(cloud.points.id.length);
    expect(rows[0]!.size).toBeGreaterThanOrEqual(rows[7]!.size);
    expect(cloud.points.id.length).toBe(3000);
  });
  it('toggles hide and show, Alt shows one alone, again shows all', () => {
    const all = [0, 1, 2, 3];
    const h1 = toggleLegend(new Set(), 2, all);
    expect([...h1]).toEqual([2]);
    expect([...toggleLegend(h1, 2, all)]).toEqual([]);
    const solo = toggleLegend(new Set(), 1, all, true);
    expect([...solo].sort()).toEqual([0, 2, 3]);
    expect([...toggleLegend(solo, 1, all, true)]).toEqual([]);
    const mask = visibleMask(cloud, new Set([0]));
    const hiddenCount = cloud.points.cluster.filter((c) => c === 0).length;
    expect(mask.reduce((s, v) => s + v, 0)).toBe(cloud.points.id.length - hiddenCount);
  });
});

describe('cloud geometry and search', () => {
  const xy = [0.1, 0.1, 0.5, 0.5, 0.52, 0.5, 0.9, 0.9];
  const grid = buildGrid(xy, 8);
  it('finds the nearest visible point within a radius', () => {
    expect(nearest(grid, xy, 0.51, 0.5, 0.05)).toBeGreaterThanOrEqual(1);
    expect(nearest(grid, xy, 0.3, 0.3, 0.05)).toBe(-1);
    expect(nearest(grid, xy, 0.52, 0.5, 0.05, Uint8Array.from([1, 1, 0, 1]))).toBe(1);
  });
  it('lassos the points inside a polygon', () => {
    expect(
      lassoSelect(grid, xy, [
        [0.4, 0.4],
        [0.6, 0.4],
        [0.6, 0.6],
        [0.4, 0.6],
      ]),
    ).toEqual([1, 2]);
  });
  it('matches every word, or an exact #id', () => {
    expect(matchesQuery('Built the cobble generator with Fox', 4, 'cobble fox')).toBe(true);
    expect(matchesQuery('Built the cobble generator with Fox', 4, 'cobble robin')).toBe(false);
    expect(matchesQuery('anything', 42, '#42')).toBe(true);
    expect(matchesQuery('anything', 42, '')).toBe(false);
  });
  it('parents cover leaves and summaries (heat propagation)', () => {
    const p = cloudParents(memoryCloud());
    expect(p.get(0)?.length).toBeGreaterThan(0);
    expect(p.size).toBeGreaterThan(3000);
  });
});
