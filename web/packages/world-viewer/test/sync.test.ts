import { describe, expect, it } from 'vitest';
import { WorldCache, indexRect, planBatches } from '../src/world/cache';
import { WorldSync } from '../src/world/sync';
import { InlineMeshHost } from '../src/mesh/host';
import { createFixtureSource, PLAN } from '../src/fixtures/world';
import type { WorldIndexColumn, WorldSource } from '../src/types';
import { meshSection } from '../src/mesh/mesher';

const col = (cx: number, cz: number, hash: string | number, extra: Partial<WorldIndexColumn> = {}): WorldIndexColumn => ({ cx, cz, hash, seen: 1, by: 'a', live: true, ...extra });

describe('WorldCache.reconcile (hash-diff)', () => {
  it('wants new and changed columns, touches same-hash ones, drops unlisted ones inside the rectangle', () => {
    const c = new WorldCache();
    let r = c.reconcile(0, -1, -1, 1, 1, [col(0, 0, '1'), col(1, 0, '2')]);
    expect(r.want).toEqual([[0, 0], [1, 0]]);
    c.applyStored(0, [{ cx: 0, cz: 0, hash: '1', seen: 1, by: 'a', live: true, height: 128, missing: false }, { cx: 1, cz: 0, hash: '2', seen: 1, by: 'a', live: true, height: 128, missing: false }], 10);
    r = c.reconcile(0, -1, -1, 1, 1, [col(0, 0, '1', { seen: 5, live: false }), col(1, 0, '3')]);
    expect(r.want).toEqual([[1, 0]]);
    expect(r.touched).toEqual([{ cx: 0, cz: 0, seen: 5, by: 'a', live: false }]);
    r = c.reconcile(0, -1, -1, 1, 1, [col(1, 0, '2')]);
    expect(r.dropped).toEqual([[0, 0]]);
    expect(c.get(0, 0, 0)).toBeUndefined();
    // Outside the rectangle nothing is dropped.
    c.applyStored(0, [{ cx: 9, cz: 9, hash: '7', seen: 1, by: null, live: true, height: 128, missing: false }], 0);
    expect(c.reconcile(0, -1, -1, 1, 1, [col(1, 0, '2')]).dropped).toEqual([]);
  });

  it('a hash above 2^53 sent as a JSON number still matches the exact i64 from the payload', () => {
    const c = new WorldCache();
    const exact = '-7046029254386353131';
    c.applyStored(0, [{ cx: 0, cz: 0, hash: exact, seen: 1, by: 'a', live: true, height: 128, missing: false }], 0);
    const lossy = JSON.parse(`[${exact}]`)[0] as number;
    expect(c.reconcile(0, 0, 0, 0, 0, [col(0, 0, lossy)]).want).toEqual([]);
  });

  it('missing columns in a reply are removed; trim keeps the nearest', () => {
    const c = new WorldCache();
    c.applyStored(0, [0, 1, 2, 3, 4].map((i) => ({ cx: i, cz: 0, hash: `${i}`, seen: 1, by: null, live: true, height: 128, missing: false })), 0);
    c.applyStored(0, [{ cx: 4, cz: 0, hash: '0', seen: 1, by: null, live: true, height: 0, missing: true }], 0);
    expect(c.count(0)).toBe(4);
    expect(c.trim(0, 0, 0, 2).sort()).toEqual([[2, 0], [3, 0]]);
  });

  it('plans nearest-first batches of 48, at most 8 per pass; the rectangle follows HiveSync', () => {
    const want: Array<[number, number]> = [];
    for (let x = -15; x <= 15; x++) for (let z = -15; z <= 15; z++) want.push([x, z]);
    const b = planBatches(want, 0, 0);
    expect(b).toHaveLength(8);
    expect(b[0]).toHaveLength(48);
    expect(b[0][0]).toEqual([0, 0]);
    const d = (p: [number, number]) => p[0] ** 2 + p[1] ** 2;
    expect(Math.max(...b[0].map(d))).toBeLessThanOrEqual(Math.min(...b[1].map(d)));
    expect(indexRect(-1, 17, 16)).toEqual([-2, 0, 0, 2]);
  });
});

describe('WorldSync index-then-fetch', () => {
  function rig(source: WorldSource) {
    const host = new InlineMeshHost();
    const cache = new WorldCache();
    const dirty: number[] = [];
    const sync = new WorldSync(source, host, cache, {
      onApplied: (r) => dirty.push(r.dirty.length),
      onDropped: () => undefined,
      onTouched: () => undefined,
      onInfo: () => undefined,
      onError: (r, e) => {
        throw new Error(`${r}: ${String(e)}`);
      },
    });
    (sync as unknown as { running: boolean }).running = true;
    return { host, cache, sync, dirty };
  }

  it('downloads every column once, then only the columns whose hash changed', async () => {
    let t = 0;
    const src = createFixtureSource({ radius: 3, now: () => t, buildRate: 0 });
    const { host, cache, sync } = rig(src);
    sync.setInterest({ dim: 0, x: 0, z: 0, r: 64 });
    await sync.tick(0);
    expect(src.calls.index).toBe(1);
    expect(src.calls.columns).toBe(49);
    expect(cache.count(0)).toBe(49);
    expect(host.engine.store.cols.size).toBe(49);

    t = 1000;
    await sync.tick(1000);
    expect(src.calls.index).toBe(2);
    expect(src.calls.columns).toBe(49); // nothing changed: no chunk request

    // The build plan places blocks: only the plan's column(s) are refetched.
    src.world.setPlaced(PLAN.length);
    const planCols = new Set(PLAN.map((b) => `${b[0] >> 4},${b[2] >> 4}`));
    t = 2000;
    await sync.tick(2000);
    expect(src.calls.columns).toBe(49 + planCols.size);
    const [x, y, z] = PLAN[0];
    expect(host.engine.store.blockAt(0, x, y, z)!.id).toBe(PLAN[0][3]);
  });

  it('respects the index period and re-indexes at once when the interest moves 16+ blocks', async () => {
    const src = createFixtureSource({ radius: 2, now: () => 0 });
    const { sync } = rig(src);
    sync.setInterest({ dim: 0, x: 0, z: 0, r: 32 });
    await sync.tick(0);
    await sync.tick(300);
    expect(src.calls.index).toBe(1);
    sync.setInterest({ dim: 0, x: 20, z: 0, r: 32 });
    await sync.tick(400);
    expect(src.calls.index).toBe(2);
  });

  it('indexes several interests round-robin (thumbnail views)', async () => {
    const src = createFixtureSource({ radius: 4, now: () => 0 });
    const { sync } = rig(src);
    sync.setInterests({ a: { dim: 0, x: -40, z: 0, r: 16 }, b: { dim: 0, x: 40, z: 0, r: 16 } });
    await sync.tick(0);
    await sync.tick(10);
    expect(src.calls.index).toBe(2);
    await sync.tick(20);
    expect(src.calls.index).toBe(2);
  });

  it('backs off after a failed index and retries later', async () => {
    let fail = true;
    const src = createFixtureSource({ radius: 1, now: () => 0 });
    const flaky: WorldSource = { ...src, index: (...a) => (fail ? Promise.reject(new Error('503')) : src.index(...a)) };
    const host = new InlineMeshHost();
    const errors: string[] = [];
    const sync = new WorldSync(flaky, host, new WorldCache(), { onApplied: () => undefined, onDropped: () => undefined, onTouched: () => undefined, onInfo: () => undefined, onError: (r) => errors.push(r) });
    (sync as unknown as { running: boolean }).running = true;
    sync.setInterest({ dim: 0, x: 0, z: 0, r: 16 });
    await sync.tick(Date.now());
    expect(errors).toEqual(['index']);
    fail = false;
    await sync.tick(Date.now());
    expect(errors).toEqual(['index']); // still backing off: no call
    expect(sync.indexBackoff.waiting(Date.now())).toBe(true);
    await sync.tick(Date.now() + 20_000);
    expect(host.engine.store.cols.size).toBeGreaterThan(0);
  });
});

describe('fixture world', () => {
  it.each(['b1.8.1', '1.12.2'] as const)('%s fixture payloads decode and mesh', async (version) => {
    const src = createFixtureSource({ version, radius: 1, now: () => 0 });
    const host = new InlineMeshHost();
    await host.configure(version, null, false);
    const res = await host.apply(await src.chunks(0, [[0, 0], [1, 0], [0, 1], [-1, -1]]));
    expect(res.columns.every((c) => !c.missing && c.height === (version === '1.12.2' ? 256 : 128))).toBe(true);
    let q = 0;
    for (let sy = 0; sy < (version === '1.12.2' ? 16 : 8); sy++) q += meshSection(host.engine.store, 0, 0, sy, 0, host.engine.table).quads;
    expect(q).toBeGreaterThan(200);
    const idx = await src.index(0, 0, 0, 16);
    expect(idx.version).toBe(version);
    expect(idx.columns.length).toBeGreaterThan(0);
  });
});
