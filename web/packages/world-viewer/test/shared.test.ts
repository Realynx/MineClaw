import { describe, expect, it } from 'vitest';
import { InlineMeshHost } from '../src/mesh/host';
import { WorldCache } from '../src/world/cache';
import { WorldSync } from '../src/world/sync';
import { BlockDeltaFeed } from '../src/world/deltas';
import { SectionManager, type Centre } from '../src/render/sections';
import { createFixtureSource, surface } from '../src/fixtures/world';
import { createChunkMaterials, createChunkUniforms } from '../src/render/chunkMaterial';
import { storeWith } from './helpers';

const tick = () => new Promise((r) => setTimeout(r, 0));
let clock = 0;

async function world(radius = 3) {
  const src = createFixtureSource({ radius, now: () => 0 });
  const host = new InlineMeshHost();
  await host.configure('b1.8.1', null);
  const cache = new WorldCache();
  const sections = new SectionManager(host, cache, createChunkMaterials(createChunkUniforms()));
  const sync = new WorldSync(src, host, cache, {
    onApplied: (r) => sections.markDirty(r.dirty),
    onDropped: (_d, cols, dirty) => {
      sections.dropColumns(cols);
      sections.markDirty(dirty);
    },
    onTouched: (_d, dirty) => sections.markDirty(dirty),
    onInfo: () => undefined,
    onError: (r, e) => {
      throw new Error(`${r}: ${String(e)}`);
    },
  });
  (sync as unknown as { running: boolean }).running = true;
  sync.setInterest({ dim: 0, x: 0, z: 0, r: radius * 16 });
  await sync.tick(0);
  return { src, host, cache, sections, sync };
}

/** Runs section upkeep for the given views until nothing is queued or in flight. */
async function settle(sections: SectionManager, centres: Centre[]) {
  // Dirty sections out of every view's range stay queued (meshed if a view comes near), so "settled" = nothing in flight
  // and no new builds for a few rounds.
  let last = -1, still = 0;
  for (let i = 0; i < 2000; i++) {
    sections.maintain(centres, (clock += 1000));
    await tick();
    const total = [...sections.builds.values()].reduce((x, y) => x + y, 0);
    if (!sections.busy && total === last) {
      if (++still >= 3) return;
    } else still = 0;
    last = total;
  }
  throw new Error('sections never settled');
}

describe('shared world: one mesh per section for any number of views', () => {
  it('N views over overlapping regions mesh each section exactly once', async () => {
    const { sections, cache } = await world(3);
    const views: Centre[] = [
      { x: 0, y: 64, z: 0, distance: 32 },
      { x: 10, y: 64, z: 6, distance: 32 },
      { x: -8, y: 70, z: 12, distance: 24 },
      { x: 4, y: 60, z: -4, distance: 40 },
    ];
    await settle(sections, views);
    // Every section of every column in range of any view was meshed, and none twice.
    expect(sections.builds.size).toBeGreaterThan(100);
    expect([...sections.builds.values()].every((n) => n === 1)).toBe(true);
    // A geometry exists once per non-empty pass: the counts don't grow with the number of views.
    const geos = sections.geometries, bytes = sections.gpuBytes;
    await settle(sections, [...views, ...views, { x: 2, y: 64, z: 2, distance: 16 }]);
    expect(sections.geometries).toBe(geos);
    expect(sections.gpuBytes).toBe(bytes);
    expect([...sections.builds.values()].every((n) => n === 1)).toBe(true);
    expect(cache.count(0)).toBe(49);
  });

  it('sections far from every view are evicted, and come back meshed once more when a view returns', async () => {
    const { sections } = await world(4);
    const a: Centre = { x: -48, y: 64, z: -48, distance: 24 };
    const b: Centre = { x: 48, y: 64, z: 48, distance: 24 };
    await settle(sections, [a, b]);
    const n = sections.count;
    await settle(sections, [a]);
    expect(sections.count).toBeLessThan(n);
    await settle(sections, [a, b]);
    expect(Math.max(...sections.builds.values())).toBe(2);
  });
});

describe('block deltas', () => {
  it('a change re-meshes exactly its section, plus the neighbours across the boundaries it sits on', async () => {
    const store = await storeWith([[4, 64, 4, 1]], { extra: [[1, 0], [0, 1], [1, 1], [-1, 0]] });
    const p = (b: [number, number, number, number, number]) => store.patch(0, [b]).dirty.map((d) => d.join(',')).sort();
    expect(p([5, 70, 5, 1, 0])).toEqual(['0,4,0']);
    expect(p([15, 70, 5, 1, 0])).toEqual(['0,4,0', '1,4,0']);
    expect(p([0, 70, 5, 1, 0])).toEqual(['-1,4,0', '0,4,0']);
    expect(p([5, 64, 5, 2, 0])).toEqual(['0,3,0', '0,4,0']);
    expect(p([15, 79, 15, 1, 0])).toEqual(['0,4,0', '0,4,1', '0,5,0', '0,5,1', '1,4,0', '1,4,1', '1,5,0', '1,5,1']);
    // Same block again: nothing changes, nothing to re-mesh.
    expect(p([15, 79, 15, 1, 0])).toEqual([]);
    const miss = store.patch(0, [[200, 64, 200, 1, 0]]);
    expect(miss.missing).toEqual([[12, 12]]);
    expect(store.blockAt(0, 15, 79, 15)!.id).toBe(1);
  });

  it('a burst of changes in one frame re-meshes each touched section once', async () => {
    const { src, host, cache, sections, sync } = await world(2);
    const view: Centre[] = [{ x: 8, y: 64, z: 8, distance: 32 }];
    await settle(sections, view);
    const before = new Map(sections.builds);
    const feed = new BlockDeltaFeed(src, host, cache, sync, { onDirty: (_d, dirty) => sections.markDirty(dirty) });
    feed.push({ rev: 1, full: true, blocks: {} });
    // A bot digs a 3×3×6 shaft in section (0, 3, 0), spread over several replies, then an explosion next to it.
    const y0 = surface(5, 5) - 6;
    let rev = 1;
    for (let y = y0; y < y0 + 6; y++) {
      const blocks: Array<[number, number, number, number, number]> = [];
      for (let x = 4; x < 7; x++) for (let z = 4; z < 7; z++) blocks.push([x, y, z, 0, 0]);
      feed.push({ rev: ++rev, full: false, blocks: { 0: blocks } });
    }
    feed.push({ rev: ++rev, full: false, blocks: { 0: [[5, y0, 5, 4, 0], [5, y0, 5, 0, 0]] } }); // later entry wins
    await feed.flush();
    await settle(sections, view);
    const touched = [...sections.builds].filter(([k, n]) => n !== before.get(k));
    expect(touched.length).toBeGreaterThan(0);
    expect(touched.every(([k, n]) => n === (before.get(k) ?? 0) + 1)).toBe(true);
    const sys = new Set(touched.map(([k]) => Number(k.split(',')[1])));
    for (const sy of sys) expect(sy === y0 >> 4 || sy === (y0 + 5) >> 4 || sy === (y0 >> 4) - 1 || sy === ((y0 + 5) >> 4) + 1).toBe(true);
    expect(host.engine.store.blockAt(0, 5, y0, 5)!.id).toBe(0);
    expect(feed.applied).toBeGreaterThan(50);
  });

  it('full replies after the first, reconnects and bulk "loaded" columns fall back to the index hash-diff refetch', async () => {
    const { src, host, cache, sync } = await world(2);
    const feed = new BlockDeltaFeed(src, host, cache, sync, { onDirty: () => undefined });
    feed.push({ rev: 3, full: true, blocks: {} }); // the first read is always full: nothing to refetch
    await feed.flush();
    expect(feed.fallbacks).toBe(0);
    const index0 = src.calls.index, cols0 = src.calls.columns;
    await sync.tick(100); // inside the index period: nothing happens
    expect(src.calls.index).toBe(index0);
    feed.push({ rev: 9, full: true, blocks: {} }); // bot restarted / ring overflow
    await feed.flush();
    expect(feed.fallbacks).toBe(1);
    await sync.tick(200);
    expect(src.calls.index).toBe(index0 + 1);
    expect(src.calls.columns).toBe(cols0); // hashes unchanged: nothing refetched
    // A bulk update of a held column: it is refetched even though the index hash looks the same to the cache.
    feed.push({ rev: 10, full: false, blocks: {}, columns: [{ dim: 0, cx: 1, cz: 1, kind: 'loaded' }] });
    await feed.flush();
    await sync.tick(300);
    expect(src.calls.columns).toBe(cols0 + 1);
    // Unloaded: kept, drawn as remembered.
    feed.push({ rev: 11, full: false, blocks: {}, columns: [{ dim: 0, cx: 0, cz: 0, kind: 'unloaded' }] });
    await feed.flush();
    expect(cache.get(0, 0, 0)!.live).toBe(false);
    expect(host.engine.store.get(0, 0, 0)!.live).toBe(false);
    feed.fallback(); // the SSE stream reconnected
    await feed.flush();
    expect(feed.fallbacks).toBe(2);
  });

  it('polls the fixture source and applies its build-plan changes', async () => {
    let t = 0;
    const src = createFixtureSource({ radius: 2, now: () => t, buildRate: 2 });
    const host = new InlineMeshHost();
    const cache = new WorldCache();
    const sync = new WorldSync(src, host, cache, { onApplied: () => undefined, onDropped: () => undefined, onTouched: () => undefined, onInfo: () => undefined, onError: () => undefined });
    (sync as unknown as { running: boolean }).running = true;
    sync.setInterest({ dim: 0, x: -14, z: -14, r: 32 });
    await sync.tick(0);
    const feed = new BlockDeltaFeed(src, host, cache, sync, { onDirty: () => undefined });
    feed.push(await src.blocks!());
    t = 3000;
    feed.push(await src.blocks!(feed.rev));
    await feed.flush();
    expect(feed.applied).toBe(6);
  });
});
