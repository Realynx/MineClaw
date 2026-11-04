import { describe, expect, it } from 'vitest';
import {
  ARRANGEMENTS,
  CLUSTER_PALETTE,
  DEFAULT_ARRANGEMENT,
  OTHER_COLOR,
  alignXy,
  drawnDistance,
  easeInOut,
  fidelityBadge,
  isArrangement,
  linkAlpha,
  niceScale,
  paneFrame,
  paneRects,
  parseCompare,
  zoomSizeFactor,
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

describe('cloud arrangements, fidelity and scale', () => {
  it('parses the picked arrangement and the compared ones from the URL', () => {
    expect(isArrangement('mds')).toBe(true);
    expect(isArrangement('tsne')).toBe(false);
    expect(DEFAULT_ARRANGEMENT).toBe('mds');
    expect(parseCompare('mds,umap')).toEqual(['mds', 'umap']);
    expect(parseCompare('umap,umap,pca,x,tree,mds')).toEqual(['umap', 'pca', 'tree', 'mds']);
    expect(parseCompare('mds')).toBeNull();
    expect(parseCompare(null)).toBeNull();
    expect(ARRANGEMENTS.map((a) => a.id)).toEqual(['mds', 'umap', 'pca', 'tree']);
  });

  it('badges distances true to embeddings only when correlation is strong and stress low', () => {
    const f = (pearson: number, stress: number) =>
      ({ metric: 'cosine', pearson, spearman: pearson, stress, unitDistance: 1.3, pairs: 20_000 }) as const;
    const good = fidelityBadge('mds', f(0.86, 0.12))!;
    expect(good.text).toBe('Distances: true to embeddings');
    expect(good.detail).toBe('r = 0.86, stress 0.12');
    expect(good.tone).toBe('ok');
    expect(good.tooltip).toMatch(/1024-number embedding/);
    expect(good.tooltip).toMatch(/20,000 memory pairs/);
    expect(fidelityBadge('umap', f(0.66, 0.35))!.text).toBe('Distances: roughly to scale');
    expect(fidelityBadge('umap', f(0.66, 0.35))!.tooltip).toMatch(/read neighbours, not gaps/);
    expect(fidelityBadge('umap', f(0.4, 0.6))!.text).toBe('Distances: not to scale');
    expect(fidelityBadge('mds', null)).toBeNull();
    const pca = fidelityBadge('pca', { ...f(0.67, 0.38), varianceExplained: [0.17, 0.07] })!;
    expect(pca.tooltip).toMatch(/17 % and 7 %/);
  });

  it('picks a round scale bar near the target length in cosine distance', () => {
    // 1 unit = 1.35 cosine distance drawn 600 px wide: 110 px ≈ 0.2475 → 0.2
    const s = niceScale(1.35, 600)!;
    expect(s.value).toBe(0.2);
    expect(s.label).toBe('0.2 cosine distance');
    expect(s.px).toBeCloseTo((0.2 / 1.35) * 600);
    // zoomed in 10×: 0.02
    expect(niceScale(1.35, 6000)!.value).toBe(0.02);
    expect(niceScale(1.35, 6000)!.label).toBe('0.02 cosine distance');
    expect(niceScale(0, 600)).toBeNull();
    // drawn distance on the arrangement's scale
    expect(drawnDistance([0, 0, 0.3, 0.4], 0, 1, 2)).toBeCloseTo(1);
  });

  it('fades links when zoomed out or dense, a little stronger zoomed in, never loud', () => {
    const at1 = linkAlpha(1, 3000, 'dark');
    expect(at1).toBeGreaterThan(0.05);
    expect(at1).toBeLessThan(0.1);
    expect(linkAlpha(0.5, 3000, 'dark')).toBeLessThan(at1 / 2);
    expect(linkAlpha(1, 24_000, 'dark')).toBeLessThan(at1);
    expect(linkAlpha(8, 3000, 'dark')).toBeGreaterThan(at1);
    expect(linkAlpha(60, 100, 'light')).toBeLessThanOrEqual(0.16);
  });

  it('splits the canvas into panes right of the legend and fits the unit square in each', () => {
    const one = paneRects(1200, 800, 1, 290, 720);
    expect(one).toEqual([{ x: 290, y: 0, w: 910, h: 800 }]);
    expect(paneFrame(one[0]!)).toEqual({ base: 752, ox: 290 + (910 - 752) / 2, oy: 24 });
    const two = paneRects(1200, 800, 2, 290, 720);
    expect(two).toHaveLength(2);
    expect(two[1]!.x).toBeGreaterThan(two[0]!.x + two[0]!.w);
    expect(paneRects(600, 900, 2, 290, 720)[1]!.y).toBeGreaterThan(0); // narrow: stacked, no legend room
    const four = paneRects(1200, 800, 4, 290, 720);
    expect(four.map((r) => [r.x > 290 + 400, r.y > 0])).toEqual([
      [false, false],
      [true, false],
      [false, true],
      [true, true],
    ]);
  });

  it('aligns another arrangement to the primary point order and eases calmly', () => {
    expect(alignXy([1, 2], [1, 2], [0.1, 0.2, 0.3, 0.4])).toEqual([0.1, 0.2, 0.3, 0.4]);
    expect(alignXy([1, 2, 3], [2, 1], [0.3, 0.4, 0.1, 0.2])).toEqual([0.1, 0.2, 0.3, 0.4, -9, -9]);
    expect(easeInOut(0)).toBe(0);
    expect(easeInOut(0.5)).toBeCloseTo(0.5);
    expect(easeInOut(1)).toBe(1);
    expect(easeInOut(0.1)).toBeLessThan(0.1);
    expect(zoomSizeFactor(1)).toBe(1);
    expect(zoomSizeFactor(4)).toBeGreaterThan(1);
    expect(pointLook(0, 4).size / pointLook(0, 1).size).toBeCloseTo(zoomSizeFactor(4));
  });

  it('mock arrangements share the points and the default is the distance layout', () => {
    const mds = memoryCloud();
    const umap = memoryCloud('umap');
    expect(mds.arrangement).toBe('mds');
    expect(umap.points.id).toEqual(mds.points.id);
    expect(umap.points.xy).not.toEqual(mds.points.xy);
  });
});
