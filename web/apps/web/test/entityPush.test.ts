import type { WorldEntities } from '@mineclaw/contracts';
import { QueryClient } from '@tanstack/react-query';
import { normalizeEntities } from '@mineclaw/world-viewer';
import { EntityStore } from '@mineclaw/world-viewer/testing';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { createWorldSource } from '@/features/world/WorldSource';
import { api } from '@/lib/api/client';
import type * as ApiClient from '@/lib/api/client';
import { makeQueryClient } from '@/lib/api/query';
import { applyLiveEvent, liveKeys } from '@/lib/live/dispatch';

// No network: a REST read in this file would mean the cached set was incomplete.
vi.mock('@/lib/api/client', async (original) => ({
  ...(await original<typeof ApiClient>()),
  api: vi.fn(() => Promise.reject(new Error('offline'))),
}));
afterEach(() => {
  vi.useRealTimers();
  vi.mocked(api).mockClear();
});

/**
 * Live 11-04 "players are moving around slowly": how often a walking bot's drawn target moves in the browser, fed by the shared
 * stream's `entities` frames at the bridge's 5 Hz. A frame where only bots moved keeps the entity revision (bots aren't part of
 * it); such frames were dropped by the cache merge, and the viewer polled the cache on its own 200 ms beat. Now every frame is
 * merged, pushed to the viewer (`WorldSource.onEntities`) and read once (numbered frames).
 */
describe('entity frames reach the viewer at the stream pace', () => {
  const bot = (x: number) => ({ name: 'maid_chan_101', short: '101', main: false, dim: 0, x, y: 64, z: 0, hp: 20, food: 20, claims: [] as string[], live: true, ageSeconds: 0 });

  it('a bot that walks while every mob stands still moves on every frame', async () => {
    const qc = new QueryClient();
    const source = createWorldSource(qc);
    let now = 0;
    const store = new EntityStore(() => now);
    const targets: number[] = [];
    let reads = 0;
    const read = async () => {
      reads++;
      store.apply(normalizeEntities(await source.entities(store.rev > 0 ? store.rev : undefined)));
      const b = store.bots()[0];
      if (b && (targets.length === 0 || targets[targets.length - 1] !== b.dto.x)) targets.push(b.dto.x);
    };
    let pending: Promise<void> = Promise.resolve();
    const off = source.onEntities!(() => {
      pending = pending.then(read);
    });
    // First frame: full; then 10 s of 5 Hz frames at the same entity revision (nothing but the bot moved).
    const frames = 50;
    for (let i = 0; i <= frames; i++) {
      now = i * 200;
      const f: WorldEntities = { at: 1_700_000_000_000 + now, rev: 42, full: i === 0, bots: [bot(i * 0.86)], entities: [], removed: [] };
      applyLiveEvent(qc, 'entities', f);
      await pending;
      // The viewer's backstop poll in between reads the same frame: nothing changes.
      if (i % 5 === 2) {
        await read();
      }
    }
    off();
    const perSecond = (targets.length - 1) / (frames * 0.2);
    // eslint-disable-next-line no-console -- the measured rate, for the record
    console.log(`bot targets applied: ${targets.length - 1} in ${frames * 0.2} s = ${perSecond.toFixed(1)}/s (frames 5/s), ${reads} reads`);
    expect(perSecond).toBeGreaterThanOrEqual(4.9);
    expect(store.intervalMs).toBeCloseTo(200, 0); // the glide follows the frames, not the reads in between
  });

  it('six minutes after a full frame, a delta does not shrink the set (the cache is never dropped)', async () => {
    vi.useFakeTimers();
    const qc = makeQueryClient();
    const mob = (id: number, x: number) => ({ kind: 'mob', dim: 0, id, x, y: 64, z: 0 });
    const t0 = 1_700_000_000_000;
    applyLiveEvent(qc, 'entities', {
      at: t0,
      rev: 1,
      full: true,
      bots: [bot(0)],
      entities: [mob(1, 0), mob(2, 5), mob(3, 9)],
      removed: [],
    });
    await vi.advanceTimersByTimeAsync(6 * 60_000);
    applyLiveEvent(qc, 'entities', {
      at: t0 + 360_000,
      rev: 2,
      full: false,
      bots: [bot(1)],
      entities: [mob(1, 1)],
      removed: [],
    });
    const store = new EntityStore(() => 360_000);
    store.apply(normalizeEntities(await createWorldSource(qc).entities(undefined)));
    const keys = store.entities(null).map((e) => e.key);
    expect(keys.sort()).toEqual(['0:1', '0:2', '0:3']);
    expect(api).not.toHaveBeenCalled();
  });

  it('stores each frame as merged, without a deep comparison against the previous one', () => {
    const qc = makeQueryClient();
    applyLiveEvent(qc, 'entities', { at: 1, rev: 1, full: true, bots: [bot(0)], entities: [], removed: [] });
    const next: WorldEntities = { at: 2, rev: 1, full: false, bots: [bot(0)], entities: [], removed: [] };
    applyLiveEvent(qc, 'entities', next);
    // With structural sharing the equal bot list would be swapped back for the previous frame's.
    expect(qc.getQueryData<WorldEntities>(liveKeys.entities)?.bots).toBe(next.bots);
  });
});
