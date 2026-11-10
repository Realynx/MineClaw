import type { WorldEntities } from '@mineclaw/contracts';
import { normalizeEntities, type WorldSource } from '@mineclaw/world-viewer';
import { EntityStore } from '@mineclaw/world-viewer/testing';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createWorldSource, SEED_RETRY_MS, SEED_TIMEOUT_MS } from '@/features/world/WorldSource';
import { api } from '@/lib/api/client';
import type * as ApiClient from '@/lib/api/client';
import { makeQueryClient } from '@/lib/api/query';
import { applyLiveEvent, liveKeys, resyncLive } from '@/lib/live/dispatch';

/**
 * The Controller has no observer on the entity query: the viewer reads the cache the stream writes. A page that joins a
 * running stream first gets a delta, and a resync after a gap cannot refetch an inactive query. Either way the viewer must
 * end up with the whole set, never with a delta presented as the set.
 */
vi.mock('@/lib/api/client', async (original) => ({ ...(await original<typeof ApiClient>()), api: vi.fn() }));
beforeEach(() => vi.useFakeTimers());
afterEach(() => {
  vi.useRealTimers();
  vi.mocked(api).mockReset();
});

const T0 = 1_700_000_000_000;
const bot = (x: number) => ({
  name: 'maid_chan_101',
  short: '101',
  main: false,
  dim: 0,
  x,
  y: 64,
  z: 0,
  hp: 20,
  food: 20,
  claims: [] as string[],
  live: true,
  ageSeconds: 0,
});
const mob = (id: number, x = id) => ({ kind: 'mob', dim: 0, id, x, y: 64, z: 0 });
const frame = (rev: number, entities: ReturnType<typeof mob>[], more: Partial<WorldEntities> = {}): WorldEntities => ({
  at: T0 + rev * 100,
  rev,
  full: false,
  bots: [bot(rev)],
  entities,
  removed: [],
  ...more,
});

/** One viewer: its store, read the way the runtime's entity poller reads (`since` = the store's revision). */
function viewer(source: WorldSource) {
  const store = new EntityStore(() => 0);
  return {
    store,
    read: async () => store.apply(normalizeEntities(await source.entities(store.rev > 0 ? store.rev : undefined))),
    xs: () => Object.fromEntries(store.entities(null).map((e) => [e.id, e.dto.x])),
  };
}

function setup() {
  const qc = makeQueryClient();
  const view = viewer(createWorldSource(qc));
  return { qc, ...view };
}

/** Lets a REST reply that is already settled run its handlers (the seed is never awaited by a read). */
async function settle() {
  for (let i = 0; i < 20; i++) await Promise.resolve();
}

/** A REST read that never answers until its signal is aborted. */
function hungRequest() {
  return (_route: unknown, init?: { signal?: AbortSignal }) =>
    new Promise<never>((_done, fail) =>
      init?.signal?.addEventListener('abort', () => fail(new DOMException('aborted', 'AbortError'))),
    );
}

describe('the Controller always gets the whole entity set', () => {
  it('joins with a delta, then lays the older snapshot under it', async () => {
    const { qc, read, xs } = setup();
    vi.mocked(api).mockResolvedValueOnce(frame(10, [mob(1), mob(2), mob(3)], { full: true }));
    applyLiveEvent(qc, 'entities', frame(11, [mob(1, 7)]));
    await read();
    expect(xs()).toEqual({ 1: 7 }); // the stream's frame at once, as a delta
    expect(api).toHaveBeenCalledWith('world.entities', expect.objectContaining({ query: { since: 0 } }));
    await settle();
    await read();
    expect(xs()).toEqual({ 1: 7, 2: 2, 3: 3 });
    expect(qc.getQueryData<WorldEntities>(liveKeys.entities)?.full).toBe(true);
  });

  it('a snapshot newer than the delta is the set, and later deltas merge into it', async () => {
    const { qc, read, xs, store } = setup();
    vi.mocked(api).mockResolvedValueOnce(frame(12, [mob(1, 7), mob(2), mob(3), mob(4)], { full: true }));
    applyLiveEvent(qc, 'entities', frame(11, [mob(1, 7)]));
    await read();
    await settle();
    await read();
    expect(xs()).toEqual({ 1: 7, 2: 2, 3: 3, 4: 4 });
    applyLiveEvent(qc, 'entities', frame(13, [], { removed: [[0, 4]] }));
    await read();
    expect(xs()).toEqual({ 1: 7, 2: 2, 3: 3 });
    expect(store.rev).toBe(13);
    expect(api).toHaveBeenCalledOnce();
  });

  it('the snapshot came first: a delta after it does not shrink the set', async () => {
    const { qc, read, xs } = setup();
    // The viewer read before any stream frame: the plain REST reply, not cached.
    vi.mocked(api).mockResolvedValueOnce(frame(10, [mob(1), mob(2), mob(3)], { full: true }));
    await read();
    expect(xs()).toEqual({ 1: 1, 2: 2, 3: 3 });
    vi.mocked(api).mockResolvedValueOnce(frame(11, [mob(1, 7), mob(2), mob(3)], { full: true }));
    applyLiveEvent(qc, 'entities', frame(11, [mob(1, 7)]));
    await read();
    expect(xs()).toEqual({ 1: 7, 2: 2, 3: 3 });
  });

  it('a removal that arrives while the snapshot is on its way is honoured', async () => {
    const { qc, read, xs } = setup();
    let reply!: (value: WorldEntities) => void;
    vi.mocked(api).mockImplementationOnce(() => new Promise<WorldEntities>((done) => (reply = done)));
    applyLiveEvent(qc, 'entities', frame(11, [mob(1, 7)]));
    await read();
    expect(api).toHaveBeenCalledOnce();
    applyLiveEvent(qc, 'entities', frame(12, [], { removed: [[0, 2]] }));
    reply(frame(10, [mob(1), mob(2), mob(3)], { full: true }));
    await settle();
    await read();
    expect(xs()).toEqual({ 1: 7, 3: 3 });
    expect(qc.getQueryData<WorldEntities>(liveKeys.entities)?.rev).toBe(12);
  });

  it('a resync reseeds the set without any observer, so removals missed in the gap go', async () => {
    const { qc, read, xs } = setup();
    applyLiveEvent(qc, 'entities', frame(10, [mob(1), mob(2), mob(3)], { full: true }));
    await read();
    expect(xs()).toEqual({ 1: 1, 2: 2, 3: 3 });
    expect(api).not.toHaveBeenCalled();
    vi.mocked(api).mockResolvedValueOnce(frame(10, [mob(1), mob(2)], { full: true }));
    await resyncLive(qc);
    await read();
    await settle();
    await read();
    expect(xs()).toEqual({ 1: 1, 2: 2 });
    expect(api).toHaveBeenCalledOnce();
  });

  it('bodies keep moving on the stream while the snapshot is still out', async () => {
    const { qc, read, store } = setup();
    applyLiveEvent(qc, 'entities', frame(10, [mob(1)], { full: true }));
    await read();
    vi.mocked(api).mockImplementation(hungRequest());
    await resyncLive(qc);
    for (const rev of [11, 12, 13]) {
      applyLiveEvent(qc, 'entities', frame(rev, []));
      await read(); // never waits for the seed
      expect(store.bots()[0]?.dto.x).toBe(rev);
    }
    expect(api).toHaveBeenCalledOnce();
  });

  it('a seed that never answers is given up, and the next try waits', async () => {
    const { qc, read } = setup();
    applyLiveEvent(qc, 'entities', frame(10, [mob(1)], { full: true }));
    await read();
    vi.mocked(api).mockImplementation(hungRequest());
    await resyncLive(qc);
    await read();
    expect(api).toHaveBeenCalledOnce();
    await vi.advanceTimersByTimeAsync(SEED_TIMEOUT_MS);
    await read();
    expect(api).toHaveBeenCalledOnce(); // given up, so the retry waits
    await vi.advanceTimersByTimeAsync(SEED_RETRY_MS);
    await read();
    expect(api).toHaveBeenCalledTimes(2);
  });

  it('a seeded set carries no events: the stream already delivered them', async () => {
    const { qc, read } = setup();
    const swing = { id: 1, dim: 0, kind: 'swing', at: T0 };
    vi.mocked(api).mockResolvedValueOnce(frame(10, [mob(1)], { full: true, events: [swing] }));
    applyLiveEvent(qc, 'entities', frame(11, [mob(1, 7)], { events: [swing] }));
    await read();
    await settle();
    const seeded = qc.getQueryData<WorldEntities>(liveKeys.entities)!;
    expect(seeded.full).toBe(true);
    expect(seeded.events ?? []).toEqual([]);
    // A snapshot newer than the cache: its own last second of events is not replayed either.
    vi.mocked(api).mockResolvedValueOnce(frame(20, [mob(1)], { full: true, events: [swing] }));
    await resyncLive(qc);
    await read();
    await settle();
    expect(qc.getQueryData<WorldEntities>(liveKeys.entities)?.events ?? []).toEqual([]);
  });

  it('while the snapshot cannot be had, deltas stay deltas and the next try waits', async () => {
    const { qc, read, xs } = setup();
    applyLiveEvent(qc, 'entities', frame(10, [mob(1), mob(2), mob(3)], { full: true }));
    await read();
    vi.mocked(api).mockRejectedValue(new Error('offline'));
    await resyncLive(qc);
    applyLiveEvent(qc, 'entities', frame(11, [mob(1, 7)]));
    await read();
    expect(xs()).toEqual({ 1: 7, 2: 2, 3: 3 });
    applyLiveEvent(qc, 'entities', frame(12, [mob(2, 8)]));
    await read();
    expect(xs()).toEqual({ 1: 7, 2: 8, 3: 3 });
    expect(api).toHaveBeenCalledOnce();
    vi.advanceTimersByTime(SEED_RETRY_MS);
    vi.mocked(api).mockResolvedValueOnce(frame(12, [mob(1, 7), mob(2, 8)], { full: true }));
    await read();
    expect(api).toHaveBeenCalledTimes(2);
    await settle();
    await read();
    expect(xs()).toEqual({ 1: 7, 2: 8 });
  });
});
