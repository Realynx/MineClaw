/**
 * The page's own reads and writes of a colony's state (features/story/colonySync.ts, useStory.ts): catch-ups run one at a
 * time and a caller only settles with a read that started after its call, a whole read never rewinds the cache, the first
 * load recovers the frames that came before it, the 3 s tick waits for a read that is still on the wire, and a write leaves
 * the Architect palette to the state's own key. Every request is answered by hand: nothing here waits on a clock.
 */
import { StoryColonyState, type RouteId, type StoryDesignation } from '@mineclaw/contracts';
import { QueryClientProvider, type QueryClient } from '@tanstack/react-query';
import { act, renderHook } from '@testing-library/react';
import { createElement, type ReactNode } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { setCsrfToken, setFetch } from '@/lib/api/client';
import { makeQueryClient, qk } from '@/lib/api/query';
import { applyLiveEvent } from '@/lib/live/dispatch';
import { catchingUp, catchUp, colonyKey, readColony, writeRefetchesArchitect } from '@/features/story/colonySync';
import { useColony, useStoryWrite } from '@/features/story/useStory';

interface Call {
  url: string;
  method: string;
  reply(body: unknown, status?: number): void;
}

/** The page's fetch, answered by hand: a request waits until the test replies to it. */
function network(): Call[] {
  const calls: Call[] = [];
  setFetch(
    (input, init) =>
      new Promise<Response>((resolve) => {
        const url = typeof input === 'string' ? input : input instanceof URL ? input.href : input.url;
        calls.push({
          url,
          method: init?.method ?? 'GET',
          reply: (body, status = 200) => resolve(new Response(JSON.stringify(body), { status })),
        });
      }),
  );
  return calls;
}

const clients: QueryClient[] = [];
function client(): QueryClient {
  const qc = makeQueryClient();
  clients.push(qc);
  return qc;
}

const provider =
  (qc: QueryClient) =>
  ({ children }: { children: ReactNode }) =>
    createElement(QueryClientProvider, { client: qc }, children);

/** Waits for what a reply sets off (it polls the check; it does not measure time). */
const until = (check: () => void) => vi.waitFor(check, { timeout: 4000, interval: 5 });

/** A promise's state without waiting for it. */
function watch(p: Promise<unknown>): { state: 'pending' | 'done' | 'failed' } {
  const w: { state: 'pending' | 'done' | 'failed' } = { state: 'pending' };
  p.then(
    () => (w.state = 'done'),
    () => (w.state = 'failed'),
  );
  return w;
}

const whole = (rev: number, rows: Partial<StoryColonyState> = {}): StoryColonyState =>
  StoryColonyState.parse({ rev, at: rev, since: null, full: true, ...rows });
const delta = (since: number, rev: number, rows: Partial<StoryColonyState> = {}): StoryColonyState =>
  StoryColonyState.parse({ rev, at: rev, since, full: false, ...rows });
const mark = (id: string): StoryDesignation => ({ id, kind: 'mine', block: [2, 64, 2] });

const urlOf = (id: string, since?: number) =>
  since === undefined ? `/api/story/colonies/${id}` : `/api/story/colonies/${id}?since=${since}`;
const urls = (calls: Call[]) => calls.map((c) => c.url);
const held = (qc: QueryClient, id: string) => qc.getQueryData<StoryColonyState>(colonyKey(id))!;
const marks = (qc: QueryClient, id: string) => held(qc, id).designations.map((d) => d.id);
/** The colony query's read as the page runs it (`useColony`'s `queryFn`), without a component. */
const load = (qc: QueryClient, id: string) =>
  qc.fetchQuery({ queryKey: colonyKey(id), queryFn: ({ signal }) => readColony(qc, id, signal), staleTime: 0 });

afterEach(() => {
  vi.useRealTimers();
  for (const qc of clients.splice(0)) qc.clear();
  setCsrfToken(null);
  setFetch((...args) => globalThis.fetch(...args));
});

describe('catchUp', () => {
  it('runs one read at a time: callers that arrive meanwhile share the one read that follows, and wait for it', async () => {
    const id = 'state-single';
    const qc = client();
    const calls = network();
    qc.setQueryData(colonyKey(id), whole(10));

    const first = catchUp(qc, id);
    const later = [catchUp(qc, id), catchUp(qc, id), catchUp(qc, id)];
    const seen = later.map(watch);
    expect(urls(calls)).toEqual([urlOf(id, 10)]);

    // The read on the wire may have left before a later caller's write was applied: its reply is not their answer.
    calls[0]!.reply(delta(10, 11));
    await first;
    expect(seen.map((w) => w.state)).toEqual(['pending', 'pending', 'pending']);
    // Their read starts now, from what the first one brought.
    expect(urls(calls)).toEqual([urlOf(id, 10), urlOf(id, 11)]);
    expect(catchingUp(qc, id)).toBe(true);

    calls[1]!.reply(delta(11, 12, { designations: [mark('d12')] }));
    await Promise.all(later);
    expect(calls).toHaveLength(2);
    expect(marks(qc, id)).toEqual(['d12']);
    expect(catchingUp(qc, id)).toBe(false);
  });

  it('that fails rejects its own caller only: the read that follows still runs', async () => {
    const id = 'state-fail';
    const qc = client();
    const calls = network();
    qc.setQueryData(colonyKey(id), whole(10));

    const first = watch(catchUp(qc, id));
    const second = catchUp(qc, id);
    calls[0]!.reply({ error: 'bridge or bot unavailable', code: 'upstream_unavailable' }, 503);
    await until(() => expect(first.state).toBe('failed'));
    expect(urls(calls)).toEqual([urlOf(id, 10), urlOf(id, 10)]);

    calls[1]!.reply(delta(10, 11));
    await second;
    expect(held(qc, id).rev).toBe(11);
    expect(catchingUp(qc, id)).toBe(false);
  });

  it('with nothing cached reads the whole state, once', async () => {
    const id = 'state-empty';
    const qc = client();
    const calls = network();

    const first = catchUp(qc, id);
    const second = catchUp(qc, id);
    expect(urls(calls)).toEqual([urlOf(id)]);
    calls[0]!.reply(whole(7, { designations: [mark('d7')] }));
    await first;
    expect(held(qc, id)).toMatchObject({ rev: 7, full: true });
    expect(marks(qc, id)).toEqual(['d7']);

    // The caller that arrived meanwhile gets a read that started after its call: now a delta.
    expect(urls(calls)).toEqual([urlOf(id), urlOf(id, 7)]);
    calls[1]!.reply(delta(7, 7));
    await second;
    expect(calls).toHaveLength(2);
  });

  it('with nothing cached leaves the read to the query while that is loading', async () => {
    const id = 'state-loading';
    const qc = client();
    const calls = network();

    const loading = load(qc, id);
    expect(urls(calls)).toEqual([urlOf(id)]);
    await catchUp(qc, id);
    expect(calls).toHaveLength(1);

    calls[0]!.reply(whole(7));
    await loading;
    expect(held(qc, id).rev).toBe(7);
  });
});

describe('the whole read', () => {
  it('at revision 10 while the cache is at 12 keeps the revision-12 designation and asks for the delta since 10', async () => {
    const id = 'state-rewind';
    const qc = client();
    const calls = network();
    qc.setQueryData(colonyKey(id), whole(12, { designations: [mark('d12')] }));

    const reading = load(qc, id);
    expect(urls(calls)).toEqual([urlOf(id)]);
    calls[0]!.reply(whole(10));
    await until(() => expect(calls).toHaveLength(2));
    expect(calls[1]!.url).toBe(urlOf(id, 10));
    // Nothing is put over the cache while the delta is on its way.
    expect(marks(qc, id)).toEqual(['d12']);

    calls[1]!.reply(delta(10, 12, { designations: [mark('d12')] }));
    await reading;
    expect(calls).toHaveLength(2);
    expect(held(qc, id).rev).toBe(12);
    expect(marks(qc, id)).toEqual(['d12']);
  });

  it('asks again when a frame moved the cache on while its delta was on the way, so the cache is never put back', async () => {
    const id = 'state-chase';
    const qc = client();
    const calls = network();
    qc.setQueryData(colonyKey(id), whole(12, { designations: [mark('d12')] }));

    const reading = load(qc, id);
    calls[0]!.reply(whole(10));
    await until(() => expect(urls(calls)).toEqual([urlOf(id), urlOf(id, 10)]));
    applyLiveEvent(qc, 'story', { ...delta(12, 14, { designations: [mark('d14')] }), id });
    expect(marks(qc, id)).toEqual(['d12', 'd14']);

    // The delta was read before that frame's change: the result would still be older than the cache.
    calls[1]!.reply(delta(10, 12, { designations: [mark('d12')] }));
    await until(() => expect(urls(calls)).toEqual([urlOf(id), urlOf(id, 10), urlOf(id, 12)]));
    expect(marks(qc, id)).toEqual(['d12', 'd14']);

    calls[2]!.reply(delta(12, 14, { designations: [mark('d14')] }));
    await reading;
    expect(held(qc, id).rev).toBe(14);
    expect(marks(qc, id)).toEqual(['d12', 'd14']);
    expect(calls).toHaveLength(3);
  });

  it('stops asking when the bot has nothing newer than its own state (it restarted behind the cache)', async () => {
    const id = 'state-behind';
    const qc = client();
    const calls = network();
    qc.setQueryData(colonyKey(id), whole(100, { designations: [mark('before')] }));

    const reading = load(qc, id);
    calls[0]!.reply(whole(98, { designations: [mark('after')] }));
    await until(() => expect(urls(calls)).toEqual([urlOf(id), urlOf(id, 98)]));
    calls[1]!.reply(delta(98, 98));
    await reading;
    // The bot's own state stands: nothing it can send explains the cache's higher revision.
    expect(calls).toHaveLength(2);
    expect(held(qc, id).rev).toBe(98);
    expect(marks(qc, id)).toEqual(['after']);
  });

  it('at or past the cached revision replaces the cache as before: it is the backstop for what a delta missed', async () => {
    const id = 'state-backstop';
    const qc = client();
    const calls = network();
    qc.setQueryData(colonyKey(id), whole(12, { designations: [mark('gone'), mark('kept')] }));

    const reading = load(qc, id);
    calls[0]!.reply(whole(12, { designations: [mark('kept')] }));
    await reading;
    expect(calls).toHaveLength(1);
    expect(marks(qc, id)).toEqual(['kept']);
    expect(catchingUp(qc, id)).toBe(false);
  });

  it('the first time is followed by one catch-up, so a frame published before it is not lost', async () => {
    const id = 'state-first';
    const qc = client();
    const calls = network();

    const loading = load(qc, id);
    // The stream delivers a frame while the first state is on its way: there is nothing to merge it into.
    applyLiveEvent(qc, 'story', { ...delta(10, 12, { designations: [mark('d12')] }), id });
    expect(qc.getQueryData(colonyKey(id))).toBeUndefined();

    // The whole state was read before that frame's change.
    calls[0]!.reply(whole(11));
    await loading;
    expect(urls(calls)).toEqual([urlOf(id), urlOf(id, 11)]);
    expect(marks(qc, id)).toEqual([]);

    calls[1]!.reply(delta(11, 12, { designations: [mark('d12')] }));
    await until(() => expect(marks(qc, id)).toEqual(['d12']));
    expect(held(qc, id).rev).toBe(12);
    expect(calls).toHaveLength(2);
    expect(catchingUp(qc, id)).toBe(false);
  });

  it('through the page hook: the first load of a colony asks for the whole state, then one catch-up', async () => {
    const id = 'state-hook';
    const qc = client();
    const calls = network();
    Object.defineProperty(document, 'visibilityState', { configurable: true, get: () => 'hidden' });
    try {
      renderHook(() => useColony(id), { wrapper: provider(qc) });
      await until(() => expect(urls(calls)).toEqual([urlOf(id)]));
      await act(async () => {
        calls[0]!.reply(whole(11));
        await until(() => expect(urls(calls)).toEqual([urlOf(id), urlOf(id, 11)]));
      });
      expect(held(qc, id).rev).toBe(11);
      await act(async () => {
        calls[1]!.reply(delta(11, 12));
        await until(() => expect(held(qc, id).rev).toBe(12));
      });
      expect(calls).toHaveLength(2);
    } finally {
      delete (document as { visibilityState?: string }).visibilityState;
    }
  });
});

describe("the page's 3 s catch-up of a quiet stream", () => {
  it('skips while an earlier one is still on the wire, and queues nothing behind it', async () => {
    const id = 'state-tick';
    const qc = client();
    const calls = network();
    vi.useFakeTimers({ toFake: ['setInterval', 'clearInterval'] });
    qc.setQueryData(colonyKey(id), whole(10));
    renderHook(() => useColony(id), { wrapper: provider(qc) });
    expect(calls).toHaveLength(0);

    vi.advanceTimersByTime(3000);
    expect(urls(calls)).toEqual([urlOf(id, 10)]);
    // The bot is slow: two more ticks pass before it answers.
    vi.advanceTimersByTime(6000);
    expect(calls).toHaveLength(1);

    await act(async () => {
      calls[0]!.reply(delta(10, 11));
      await until(() => expect(held(qc, id).rev).toBe(11));
    });
    expect(calls).toHaveLength(1);
    expect(catchingUp(qc, id)).toBe(false);

    vi.advanceTimersByTime(3000);
    expect(urls(calls)).toEqual([urlOf(id, 10), urlOf(id, 11)]);
  });
});

describe('a colony write and the Architect palette', () => {
  const ARCHITECT: RouteId[] = ['story.workbenches', 'story.options', 'story.builds', 'story.storage'];

  it('only a state without architectKey needs the write to refetch the palette', () => {
    for (const route of ARCHITECT) {
      expect(writeRefetchesArchitect(route, whole(10, { architectKey: 77 })), route).toBe(false);
      expect(writeRefetchesArchitect(route, whole(10)), route).toBe(true);
      expect(writeRefetchesArchitect(route, whole(10, { architectKey: null })), route).toBe(true);
      expect(writeRefetchesArchitect(route, undefined), route).toBe(true);
    }
    // A write that does not change what the colony can make never does.
    expect(writeRefetchesArchitect('story.designations', whole(10))).toBe(false);
    expect(writeRefetchesArchitect('story.orders', undefined)).toBe(false);
  });

  it.each([
    { name: 'with architectKey in the cache does not invalidate the palette', architectKey: 77, invalidated: false },
    { name: 'without the key (an older bot) invalidates it', architectKey: null, invalidated: true },
  ])('a story.builds write $name', async ({ architectKey, invalidated }) => {
    const id = `state-architect-${invalidated ? 'old' : 'keyed'}`;
    const qc = client();
    const calls = network();
    setCsrfToken('test');
    qc.setQueryData(colonyKey(id), whole(10, { architectKey }));
    const invalidate = vi.spyOn(qc, 'invalidateQueries');
    const { result } = renderHook(() => useStoryWrite(id), { wrapper: provider(qc) });

    const sent = result.current('story.builds', { op: 'pause', ids: ['b1'] });
    await until(() => expect(calls).toHaveLength(1));
    expect([calls[0]!.method, calls[0]!.url]).toEqual(['POST', `/api/story/colonies/${id}/builds`]);
    calls[0]!.reply({ ok: true, rev: 11 });
    await expect(sent).resolves.toEqual({ ok: true, rev: 11 });

    // The state is caught up either way: one read, started after the write was answered.
    expect(urls(calls).slice(1)).toEqual([urlOf(id, 10)]);
    const palette = { queryKey: qk('story.architect', { id }) };
    if (invalidated) expect(invalidate).toHaveBeenCalledWith(palette);
    else expect(invalidate).not.toHaveBeenCalled();
    calls[1]!.reply(delta(10, 11));
    await until(() => expect(catchingUp(qc, id)).toBe(false));
  });
});
