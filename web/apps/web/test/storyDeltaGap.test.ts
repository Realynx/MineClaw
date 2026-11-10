/**
 * The colony state stays true to the bot while whole reads, stream frames and catch-ups cross each other
 * (features/story/colonySync.ts). A delta says only what changed after its `since`, so the page merges it only into a state
 * that holds everything up to there. Every request here is answered by hand, so the order of events is the test's own.
 */
import { StoryColonyState, type StoryDesignation, type StoryStorage } from '@mineclaw/contracts';
import { QueryClientProvider, type QueryClient } from '@tanstack/react-query';
import { act, renderHook } from '@testing-library/react';
import { createElement, type ReactNode } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { setFetch } from '@/lib/api/client';
import { makeQueryClient } from '@/lib/api/query';
import { applyLiveEvent } from '@/lib/live/dispatch';
import { catchingUp, catchUp, colonyKey, startsAhead } from '@/features/story/colonySync';
import { useColony } from '@/features/story/useStory';

interface Call {
  url: string;
  reply(body: unknown, status?: number): void;
}

/** The page's fetch, answered by hand: a request waits until the test replies to it. */
function network(): Call[] {
  const calls: Call[] = [];
  setFetch(
    (input) =>
      new Promise<Response>((resolve) => {
        const url = typeof input === 'string' ? input : input instanceof URL ? input.href : input.url;
        calls.push({ url, reply: (body, status = 200) => resolve(new Response(JSON.stringify(body), { status })) });
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

/** Waits for what a reply sets off (it polls the check; it does not measure time). */
const until = (check: () => void) => vi.waitFor(check, { timeout: 4000, interval: 5 });

const whole = (rev: number, rows: Partial<StoryColonyState> = {}): StoryColonyState =>
  StoryColonyState.parse({ rev, at: rev, since: null, full: true, ...rows });
const delta = (since: number, rev: number, rows: Partial<StoryColonyState> = {}): StoryColonyState =>
  StoryColonyState.parse({ rev, at: rev, since, full: false, ...rows });
/** A `story` frame as the bridge publishes it: the delta after its own cursor, with the colony id. */
const frame = (id: string, since: number, rev: number, rows: Partial<StoryColonyState> = {}) => ({
  ...delta(since, rev, rows),
  id,
});

/** Chest A with a priority (the setting that flips back when a delta is lost). */
const chest = (priority: string): StoryStorage => ({
  chest: [1, 64, 1],
  priority,
  filter: { allow: [], deny: [] },
  contents: [],
});
const mark = (id: string): StoryDesignation => ({ id, kind: 'mine', block: [2, 64, 2] });

const urlOf = (id: string, since?: number) =>
  since === undefined ? `/api/story/colonies/${id}` : `/api/story/colonies/${id}?since=${since}`;
const urls = (calls: Call[]) => calls.map((c) => c.url);

function colony(qc: QueryClient, id: string) {
  const held = () => qc.getQueryData<StoryColonyState>(colonyKey(id))!;
  return {
    held,
    priority: () => held().storages?.[0]?.priority,
    marks: () => held().designations.map((d) => d.id),
  };
}

/** The page is in the background for the hook's own 3 s catch-up, so only the requests under test are made. */
function hideTab() {
  Object.defineProperty(document, 'visibilityState', { configurable: true, get: () => 'hidden' });
}

beforeEach(hideTab);
afterEach(() => {
  delete (document as { visibilityState?: string }).visibilityState;
  for (const qc of clients.splice(0)) qc.clear();
  setFetch((...args) => globalThis.fetch(...args));
});

describe('a whole read against the stream', () => {
  it('pins the library: a read that resolves replaces what was merged into its query while it was on its way', async () => {
    const qc = client();
    let answer!: (state: { rev: number }) => void;
    const read = qc.fetchQuery({
      queryKey: ['pin'],
      queryFn: () => new Promise<{ rev: number }>((resolve) => (answer = resolve)),
    });
    qc.setQueryData(['pin'], { rev: 15 });
    answer({ rev: 10 });
    await read;
    expect(qc.getQueryData(['pin'])).toEqual({ rev: 10 });
  });

  it('a whole read that comes back older than the cache keeps what the frames merged meanwhile, with one delta', async () => {
    const id = 'gap-refetch';
    const qc = client();
    const calls = network();
    const c = colony(qc, id);
    qc.setQueryData(colonyKey(id), whole(10, { storages: [chest('normal')] }));
    renderHook(() => useColony(id), {
      wrapper: ({ children }: { children: ReactNode }) => createElement(QueryClientProvider, { client: qc }, children),
    });
    expect(calls).toHaveLength(0);

    // The minute's full refetch (a stream resync does the same) asks for the whole state again.
    act(() => void qc.invalidateQueries({ queryKey: colonyKey(id) }));
    expect(urls(calls)).toEqual([urlOf(id)]);
    // While it is on its way a frame changes the chest.
    act(() => applyLiveEvent(qc, 'story', frame(id, 10, 15, { storages: [chest('critical')] })));
    expect(c.held().rev).toBe(15);

    // The whole state was read before that change. It is not put over the cache: a delta from its revision is asked first.
    await act(async () => {
      calls[0]!.reply(whole(10, { storages: [chest('normal')] }));
      await until(() => expect(calls).toHaveLength(2));
    });
    expect(calls[1]!.url).toBe(urlOf(id, 10));
    expect(c.priority()).toBe('critical');

    // The next frame continues from the cache's revision, so it merges.
    act(() => applyLiveEvent(qc, 'story', frame(id, 15, 18)));
    expect(c.held().rev).toBe(18);

    await act(async () => {
      calls[1]!.reply(delta(10, 18, { storages: [chest('critical')] }));
      await until(() => expect(qc.isFetching({ queryKey: colonyKey(id) })).toBe(0));
    });
    expect(calls).toHaveLength(2);
    expect(c.held().rev).toBeGreaterThanOrEqual(18);
    expect(c.priority()).toBe('critical');
  });
});

describe('a story frame', () => {
  it('tells a delta that starts after the held revision from one that continues it', () => {
    expect(startsAhead({ rev: 10 }, { since: 11, full: false })).toBe(true);
    expect(startsAhead({ rev: 10 }, { since: 10, full: false })).toBe(false);
    expect(startsAhead({ rev: 10 }, { since: 4, full: false })).toBe(false);
    // A whole state replaces; a delta without `since` (an older bot) cannot be checked and merges as before.
    expect(startsAhead({ rev: 10 }, { since: null, full: true })).toBe(false);
    expect(startsAhead({ rev: 10 }, { since: 20, full: true })).toBe(false);
    expect(startsAhead({ rev: 10 }, {})).toBe(false);
  });

  it('that starts at or before the cached revision is merged, and no request is made', () => {
    const id = 'gap-none';
    const qc = client();
    const calls = network();
    const c = colony(qc, id);
    qc.setQueryData(colonyKey(id), whole(10, { storages: [chest('normal')] }));

    applyLiveEvent(qc, 'story', frame(id, 10, 12, { storages: [chest('high')] }));
    expect([c.held().rev, c.priority()]).toEqual([12, 'high']);
    applyLiveEvent(qc, 'story', frame(id, 8, 14, { storages: [chest('low')] }));
    expect([c.held().rev, c.priority()]).toEqual([14, 'low']);
    expect(calls).toHaveLength(0);
  });

  it('for a colony that is not loaded is dropped, and no request is made', () => {
    const id = 'gap-unloaded';
    const qc = client();
    const calls = network();
    applyLiveEvent(qc, 'story', frame(id, 10, 12, { storages: [chest('high')] }));
    expect(qc.getQueryData(colonyKey(id))).toBeUndefined();
    expect(calls).toHaveLength(0);
  });

  it('that starts after the cached revision is not merged: one catch-up from the cached revision closes the gap', async () => {
    const id = 'gap-ahead';
    const qc = client();
    const calls = network();
    const c = colony(qc, id);
    qc.setQueryData(colonyKey(id), whole(10, { storages: [chest('normal')] }));

    // Revisions 11 to 15 never arrived here.
    applyLiveEvent(qc, 'story', frame(id, 15, 18, { designations: [mark('late')] }));
    expect(c.held().rev).toBe(10);
    expect(c.marks()).toEqual([]);
    expect(urls(calls)).toEqual([urlOf(id, 10)]);

    // Frames keep coming while the catch-up is on its way: they wait for it, and no second request is made.
    applyLiveEvent(qc, 'story', frame(id, 18, 20, { designations: [mark('later')] }));
    expect(c.held().rev).toBe(10);
    expect(calls).toHaveLength(1);

    // The reply was read before the last frame was published: that frame goes in after it.
    calls[0]!.reply(delta(10, 18, { storages: [chest('critical')], designations: [mark('late')] }));
    await until(() => expect(c.held().rev).toBe(20));
    expect(c.priority()).toBe('critical');
    expect(c.marks()).toEqual(['late', 'later']);
    expect(calls).toHaveLength(1);
    expect(catchingUp(qc, id)).toBe(false);

    // The stream continues from there without another read.
    applyLiveEvent(qc, 'story', frame(id, 20, 21));
    expect(c.held().rev).toBe(21);
    expect(calls).toHaveLength(1);
  });

  it('that is still ahead after the catch-up (the one before it never arrived) makes it read once more', async () => {
    const id = 'gap-twice';
    const qc = client();
    const calls = network();
    const c = colony(qc, id);
    qc.setQueryData(colonyKey(id), whole(10));

    applyLiveEvent(qc, 'story', frame(id, 15, 18));
    applyLiveEvent(qc, 'story', frame(id, 25, 28, { designations: [mark('far')] }));
    expect(urls(calls)).toEqual([urlOf(id, 10)]);

    calls[0]!.reply(delta(10, 20));
    await until(() => expect(calls).toHaveLength(2));
    expect(calls[1]!.url).toBe(urlOf(id, 20));
    expect(c.held().rev).toBe(20);
    expect(c.marks()).toEqual([]);

    calls[1]!.reply(delta(20, 28, { designations: [mark('far')] }));
    await until(() => expect(c.held().rev).toBe(28));
    expect(c.marks()).toEqual(['far']);
    expect(calls).toHaveLength(2);
    expect(catchingUp(qc, id)).toBe(false);
  });
});

describe('a catch-up', () => {
  it('asked for five times while one is on the wire makes two requests in all', async () => {
    const id = 'gap-single';
    const qc = client();
    const calls = network();
    const c = colony(qc, id);
    qc.setQueryData(colonyKey(id), whole(10));

    const first = catchUp(qc, id);
    const more = Array.from({ length: 5 }, () => catchUp(qc, id));
    expect(urls(calls)).toEqual([urlOf(id, 10)]);

    calls[0]!.reply(delta(10, 11));
    await first;
    expect(urls(calls)).toEqual([urlOf(id, 10), urlOf(id, 11)]);

    calls[1]!.reply(delta(11, 12));
    await Promise.all(more);
    expect(calls).toHaveLength(2);
    expect(c.held().rev).toBe(12);
    expect(catchingUp(qc, id)).toBe(false);
  });

  it('whose reply would skip revisions (the cache stepped back meanwhile) reads again from what is held', async () => {
    const id = 'gap-stepback';
    const qc = client();
    const calls = network();
    const c = colony(qc, id);
    qc.setQueryData(colonyKey(id), whole(15, { storages: [chest('critical')] }));

    const done = catchUp(qc, id);
    expect(urls(calls)).toEqual([urlOf(id, 15)]);
    // An older whole state replaces the cache while the catch-up is on its way.
    qc.setQueryData(colonyKey(id), whole(12, { storages: [chest('normal')] }));

    // This reply says nothing about revisions 13 to 15: merged, the chest would keep its old priority under revision 18.
    calls[0]!.reply(delta(15, 18));
    await until(() => expect(calls).toHaveLength(2));
    expect(calls[1]!.url).toBe(urlOf(id, 12));
    expect([c.held().rev, c.priority()]).toEqual([12, 'normal']);

    calls[1]!.reply(delta(12, 18, { storages: [chest('critical')] }));
    await done;
    expect([c.held().rev, c.priority()]).toEqual([18, 'critical']);
  });
});
