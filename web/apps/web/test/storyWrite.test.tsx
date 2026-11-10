/**
 * One tracked colony write from click to confirmation (features/story/optimistic.ts `track`): the change is shown before the
 * request is answered, it stops being shown exactly when the colony's state reaches the answer's revision, a refusal puts
 * the old value back, an unknown outcome is shown until the state decides it or 10 s pass and is never sent again, and one
 * key has one request in flight. Every request is answered by hand and time moves only when the test moves it.
 */
import { StoryColonyState, type StoryStorage } from '@mineclaw/contracts';
import { QueryClientProvider, useQuery, type QueryClient } from '@tanstack/react-query';
import { act, renderHook } from '@testing-library/react';
import { createElement, type ReactNode } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { setCsrfToken, setFetch } from '@/lib/api/client';
import { makeQueryClient } from '@/lib/api/query';
import { colonyKey } from '@/features/story/colonySync';
import { noteStoryFrame } from '@/features/story/frames';
import { mergeColony } from '@/features/story/model';
import {
  BUSY_TEXT,
  FALLBACK_CATCH_UP_MS,
  patchStorageSet,
  storyCommands,
  storyWriteError,
  UNCONFIRMED_MS,
  UNCONFIRMED_TEXT,
  useOptimisticColony,
  writeCatchUp,
  type StorageSet,
} from '@/features/story/optimistic';
import { useStoryWrite } from '@/features/story/useStory';

interface Call {
  url: string;
  method: string;
  body: unknown;
  reply(body: unknown, status?: number): void;
  drop(): void;
}

/** The page's fetch, answered by hand. */
function network(): Call[] {
  const calls: Call[] = [];
  setFetch(
    (input, init) =>
      new Promise<Response>((resolve, reject) => {
        const url = typeof input === 'string' ? input : input instanceof URL ? input.href : input.url;
        calls.push({
          url,
          method: init?.method ?? 'GET',
          body: typeof init?.body === 'string' ? (JSON.parse(init.body) as unknown) : undefined,
          reply: (body, status = 200) => resolve(new Response(JSON.stringify(body), { status })),
          drop: () => reject(new TypeError('fetch failed')),
        });
      }),
  );
  return calls;
}

/** Lets answered requests run through the client (real macrotasks: the fake clock does not move). */
async function settle(): Promise<void> {
  await act(async () => {
    for (let i = 0; i < 6; i++) await new Promise<void>((r) => setImmediate(r));
  });
}

/** Moves the fake clock. */
async function wait(ms: number): Promise<void> {
  act(() => {
    vi.advanceTimersByTime(ms);
  });
  await settle();
}

const A: [number, number, number] = [1, 64, 1];
const chest = (priority: string, extra: Partial<StoryStorage> = {}) => ({
  chest: A,
  priority,
  filter: { allow: ['*'], deny: [] },
  ...extra,
});
const whole = (rev: number, priority = 'normal'): StoryColonyState =>
  StoryColonyState.parse({ rev, at: rev, since: null, full: true, storages: [chest(priority)] });
const delta = (since: number, rev: number, rows: Record<string, unknown> = {}): StoryColonyState =>
  StoryColonyState.parse({ rev, at: rev, since, full: false, ...rows });

const clients: QueryClient[] = [];
const provider =
  (qc: QueryClient) =>
  ({ children }: { children: ReactNode }) =>
    createElement(QueryClientProvider, { client: qc }, children);

/** The page's colony state as `useStoryData` shows it: the cached state (never fetched here) with the shown changes. */
function mount(id: string, rev = 10) {
  const qc = makeQueryClient();
  clients.push(qc);
  qc.setQueryData(colonyKey(id), whole(rev));
  const hook = renderHook(
    () => {
      const { data } = useQuery<StoryColonyState>({
        queryKey: colonyKey(id),
        queryFn: () => new Promise<StoryColonyState>(() => undefined),
        enabled: false,
      });
      return { server: data, ...useOptimisticColony(id, data) };
    },
    { wrapper: provider(qc) },
  );
  /** Merges a stream frame into the cache, as the stream does (the query tells the component on a zero timeout). */
  const frame = async (d: StoryColonyState) => {
    act(() => {
      qc.setQueryData(colonyKey(id), mergeColony(qc.getQueryData<StoryColonyState>(colonyKey(id)), d));
    });
    await wait(0);
  };
  const shown = () => (hook.result.current.state?.storages ?? [])[0]?.priority;
  const set = (patch: StorageSet) =>
    hook.result.current.track(
      {
        key: 'storage:1,64,1',
        apply: patchStorageSet([A], patch),
        merge: (a, b) => ({ ...a, ...b }),
        confirmed: (s) => (s.storages ?? []).some((x) => x.priority === patch.priority),
      },
      'story.storage',
      { op: 'set', chests: [A], ...patch },
    );
  return { qc, hook, frame, shown, set };
}

/** A promise's state without waiting for it. */
function watch(p: Promise<unknown>) {
  const w: { state: 'pending' | 'done' | 'failed'; value?: unknown; error?: unknown } = { state: 'pending' };
  p.then(
    (v) => Object.assign(w, { state: 'done', value: v }),
    (e: unknown) => Object.assign(w, { state: 'failed', error: e }),
  );
  return w;
}

const posts = (calls: Call[]) => calls.filter((c) => c.method === 'POST');
const reads = (calls: Call[]) => calls.filter((c) => c.method === 'GET').map((c) => c.url);

beforeEach(() => {
  vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout', 'Date'], now: 1_000_000 });
  setCsrfToken('test');
});

afterEach(() => {
  vi.useRealTimers();
  for (const qc of clients.splice(0)) qc.clear();
  setCsrfToken(null);
  setFetch((...args) => globalThis.fetch(...args));
});

describe('a tracked write', () => {
  it('shows the value before the request is answered; with rev and frames flowing it reads nothing, and stops at that rev', async () => {
    const id = 'write-rev';
    const calls = network();
    const { hook, frame, shown, set } = mount(id);
    noteStoryFrame(id);

    let sent!: ReturnType<typeof watch>;
    act(() => void (sent = watch(set({ priority: 'important' }))));
    expect(shown()).toBe('important'); // in the frame of the click
    await settle();
    expect(posts(calls).map((c) => c.body)).toEqual([{ op: 'set', chests: [A], priority: 'important' }]);
    expect(sent.state).toBe('pending');

    posts(calls)[0]!.reply({ chests: [A], rev: 12 });
    await settle();
    expect(sent).toMatchObject({ state: 'done', value: { chests: [A], rev: 12 } });
    expect(reads(calls)).toEqual([]);

    // A frame that stops short of the answer's revision does not carry the change yet: still shown.
    noteStoryFrame(id);
    await frame(delta(10, 11));
    expect(shown()).toBe('important');
    // The colony's own row at the answer's revision replaces it: the page shows the server's state itself.
    noteStoryFrame(id);
    await frame(delta(11, 12, { storages: [chest('important', { label: 'normalised' })] }));
    expect(hook.result.current.state).toBe(hook.result.current.server);
    expect(hook.result.current.state?.storages?.[0]?.label).toBe('normalised');
    await wait(FALLBACK_CATCH_UP_MS);
    expect(reads(calls)).toEqual([]);
  });

  it('with rev, makes one read after 1.5 s when the frames have not reached the answer', async () => {
    const id = 'write-fallback';
    const calls = network();
    const { set } = mount(id);
    noteStoryFrame(id);
    act(() => void set({ priority: 'low' }));
    await settle();
    posts(calls)[0]!.reply({ chests: [A], rev: 12 });
    await settle();
    await wait(FALLBACK_CATCH_UP_MS - 1);
    expect(reads(calls)).toEqual([]);
    await wait(1);
    expect(reads(calls)).toEqual([`/api/story/colonies/${id}?since=10`]);
  });

  it('without rev (an older bot), is shown until a read started after the answer has merged', async () => {
    const id = 'write-norev';
    const calls = network();
    const { hook, shown, set } = mount(id);
    noteStoryFrame(id); // frames flowing do not matter: the answer has no revision to wait for
    act(() => void set({ priority: 'preferred' }));
    await settle();
    expect(reads(calls)).toEqual([]);
    posts(calls)[0]!.reply({ chests: [A] });
    await settle();
    expect(reads(calls)).toEqual([`/api/story/colonies/${id}?since=10`]);
    expect(shown()).toBe('preferred');

    calls.find((c) => c.method === 'GET')!.reply(delta(10, 11, { storages: [chest('preferred')] }));
    await settle();
    expect(hook.result.current.state).toBe(hook.result.current.server);
    expect(shown()).toBe('preferred');
  });

  it.each([
    { name: '400', status: 400, body: { error: 'priority must be low|normal|preferred|important|critical' } },
    { name: '503 busy', status: 503, body: { error: 'busy; retry shortly', code: 'busy', retryAfterMs: 1000 } },
  ])(
    'refused with $name: the old value comes back, the caller hears why, nothing is sent again',
    async ({ status, body }) => {
      const id = `write-refused-${status}`;
      const calls = network();
      const { hook, shown, set } = mount(id);
      let sent!: ReturnType<typeof watch>;
      act(() => void (sent = watch(set({ priority: 'critical' }))));
      expect(shown()).toBe('critical');
      await settle();
      posts(calls)[0]!.reply(body, status);
      await settle();
      expect(sent.state).toBe('failed');
      expect(hook.result.current.state).toBe(hook.result.current.server);
      expect(shown()).toBe('normal');
      expect(storyWriteError(sent.error).text).toBe(status === 503 ? BUSY_TEXT : body.error);
      await wait(UNCONFIRMED_MS * 2);
      expect(posts(calls)).toHaveLength(1);
      expect(reads(calls)).toEqual([]);
    },
  );

  it('with an unknown outcome, keeps the value for 10 s, then follows the colony and says so; never resends', async () => {
    const id = 'write-unknown';
    const calls = network();
    const { hook, shown, set } = mount(id);
    let sent!: ReturnType<typeof watch>;
    act(() => void (sent = watch(set({ priority: 'low' }))));
    await settle();
    posts(calls)[0]!.drop();
    await settle();
    expect(shown()).toBe('low');
    expect(reads(calls)).toEqual([`/api/story/colonies/${id}?since=10`]);
    // The state read after the failure does not show the change: it may still arrive, so it stays shown.
    calls.find((c) => c.method === 'GET')!.reply(delta(10, 11));
    await settle();
    expect(shown()).toBe('low');
    await wait(UNCONFIRMED_MS - 1);
    expect(sent.state).toBe('pending');
    await wait(1);
    expect(sent.state).toBe('failed');
    expect((sent.error as Error).message).toBe(UNCONFIRMED_TEXT);
    expect(hook.result.current.state).toBe(hook.result.current.server);
    expect(shown()).toBe('normal');
    expect(posts(calls)).toHaveLength(1);
  });

  it('with an unknown outcome, is settled as applied by a later state that shows it', async () => {
    const id = 'write-unknown-applied';
    const calls = network();
    const { hook, frame, shown, set } = mount(id);
    let sent!: ReturnType<typeof watch>;
    act(() => void (sent = watch(set({ priority: 'low' }))));
    await settle();
    posts(calls)[0]!.reply({ error: 'bridge or bot unavailable', code: 'upstream_unavailable' }, 503);
    await settle();
    expect(shown()).toBe('low');
    await frame(delta(10, 11, { storages: [chest('low')] }));
    await settle();
    expect(sent.state).toBe('done');
    expect(hook.result.current.state).toBe(hook.result.current.server);
    await wait(UNCONFIRMED_MS);
    expect(shown()).toBe('low');
    expect(posts(calls)).toHaveLength(1);
  });

  it('with an unknown outcome and no `confirmed` of its own, is settled by a state that looks as the change makes it', async () => {
    const id = 'write-unknown-shows';
    const calls = network();
    const { hook, frame, shown } = mount(id);
    let sent!: ReturnType<typeof watch>;
    act(
      () =>
        void (sent = watch(
          hook.result.current.track(
            { key: 'storage:1,64,1', apply: patchStorageSet([A], { priority: 'low' }) },
            'story.storage',
            { op: 'set', chests: [A], priority: 'low' },
          ),
        )),
    );
    await settle();
    posts(calls)[0]!.drop();
    await settle();
    // A later state that does not have it decides nothing.
    await frame(delta(10, 11, { storages: [chest('normal', { label: 'Ores' })] }));
    expect(sent.state).toBe('pending');
    expect(shown()).toBe('low');
    // One that has it settles the change as applied: no "could not confirm" after 10 s.
    await frame(delta(11, 12, { storages: [chest('low', { label: 'Ores' })] }));
    expect(sent.state).toBe('done');
    expect(hook.result.current.state).toBe(hook.result.current.server);
    await wait(UNCONFIRMED_MS);
    expect(sent.state).toBe('done');
    expect(posts(calls)).toHaveLength(1);
  });

  it('three quick writes on one key send two requests, and the last value wins', async () => {
    const id = 'write-queue';
    const calls = network();
    const { shown, set } = mount(id);
    const sent: Array<ReturnType<typeof watch>> = [];
    act(() => {
      sent.push(watch(set({ priority: 'low' })));
      sent.push(watch(set({ priority: 'preferred', label: 'Ores' })));
      sent.push(watch(set({ priority: 'critical' })));
    });
    expect(shown()).toBe('critical');
    await settle();
    expect(posts(calls)).toHaveLength(1);

    posts(calls)[0]!.reply({ chests: [A], rev: 11 });
    await settle();
    expect(posts(calls)).toHaveLength(2);
    // The queued request carries both later changes: later fields win.
    expect(posts(calls)[1]!.body).toEqual({ op: 'set', chests: [A], priority: 'critical', label: 'Ores' });
    expect(shown()).toBe('critical');

    posts(calls)[1]!.reply({ chests: [A], rev: 12 });
    await settle();
    expect(sent.map((w) => w.state)).toEqual(['done', 'done', 'done']);
    expect(posts(calls)).toHaveLength(2);
  });

  it('a queued write that waited more than 10 s is dropped without being sent', async () => {
    const id = 'write-stale';
    const calls = network();
    const { shown, set } = mount(id);
    let queued!: ReturnType<typeof watch>;
    act(() => {
      void set({ priority: 'low' }).catch(() => undefined);
      queued = watch(set({ priority: 'critical' }));
    });
    await settle();
    await wait(UNCONFIRMED_MS + 1);
    posts(calls)[0]!.reply({ chests: [A], rev: 11 });
    await settle();
    expect(queued.state).toBe('failed');
    expect(posts(calls)).toHaveLength(1);
    expect(shown()).toBe('low'); // the first change stays shown until the state reaches rev 11
  });

  it('a refused write takes the write queued behind it on its key: nothing more is sent and the server value comes back', async () => {
    const id = 'write-refused-queued';
    const calls = network();
    const { hook, shown, set } = mount(id);
    let first!: ReturnType<typeof watch>;
    let second!: ReturnType<typeof watch>;
    act(() => {
      first = watch(set({ priority: 'low' }));
      // Built from what is shown (the refused value is in it), as the filter tree builds its body.
      second = watch(set({ priority: 'critical', label: 'Ores' }));
    });
    expect(shown()).toBe('critical');
    await settle();
    expect(posts(calls)).toHaveLength(1);
    posts(calls)[0]!.reply({ error: 'busy; retry shortly', code: 'busy', retryAfterMs: 1000 }, 503);
    await settle();
    expect(first.state).toBe('failed');
    expect(second.state).toBe('failed');
    expect(storyWriteError(second.error).text).toBe(BUSY_TEXT);
    expect(hook.result.current.state).toBe(hook.result.current.server);
    expect(shown()).toBe('normal');
    await wait(UNCONFIRMED_MS * 2);
    expect(posts(calls)).toHaveLength(1);
  });
});

describe('the command ring', () => {
  it('records each colony write: sent, answered, and confirmed when the state reaches its rev', async () => {
    const id = 'ring-rev';
    const calls = network();
    const { qc, frame } = mount(id);
    noteStoryFrame(id);
    const { result } = renderHook(() => useStoryWrite(id), { wrapper: provider(qc) });
    expect((window as unknown as { __storyCommands?: unknown }).__storyCommands).toBe(storyCommands);

    act(() => void result.current('story.storage', { op: 'set', chests: [A], priority: 'low' }));
    await settle();
    const rec = storyCommands.at(-1)!;
    expect(rec).toMatchObject({
      colony: id,
      route: 'story.storage',
      sentAt: 1_000_000,
      repliedAt: null,
      status: 'sending',
    });

    await wait(300);
    posts(calls)[0]!.reply({ chests: [A], rev: 12 });
    await settle();
    expect(rec).toMatchObject({ repliedAt: 1_000_300, status: 'ok', rev: 12, confirmedAt: null });
    // With `rev` while frames flow, a plain write reads nothing: the stream brings the change.
    expect(reads(calls)).toEqual([]);

    await wait(400);
    await frame(delta(10, 11));
    expect(rec.confirmedAt).toBeNull();
    const debug = vi.spyOn(console, 'debug').mockImplementation(() => undefined);
    await frame(delta(11, 12));
    expect(rec.confirmedAt).toBe(1_000_700);
    expect(debug).toHaveBeenCalledWith('[story] slow command', rec);
    debug.mockRestore();
    await wait(FALLBACK_CATCH_UP_MS);
    expect(reads(calls)).toEqual([]);
  });

  it('a plain write with rev reads once after 1.5 s when the frames have not reached it', async () => {
    const id = 'plain-fallback';
    const calls = network();
    const { qc } = mount(id);
    noteStoryFrame(id);
    const { result } = renderHook(() => useStoryWrite(id), { wrapper: provider(qc) });
    act(() => void result.current('story.storage', { op: 'set', chests: [A], priority: 'low' }));
    await settle();
    posts(calls)[0]!.reply({ chests: [A], rev: 12 });
    await settle();
    await wait(FALLBACK_CATCH_UP_MS - 1);
    expect(reads(calls)).toEqual([]);
    await wait(1);
    expect(reads(calls)).toEqual([`/api/story/colonies/${id}?since=10`]);
  });

  it('a plain write without rev starts one read, and that read is the one its pending marks settle by', async () => {
    const id = 'plain-norev';
    const calls = network();
    const { qc } = mount(id);
    noteStoryFrame(id);
    const { result } = renderHook(() => useStoryWrite(id), { wrapper: provider(qc) });
    let out: unknown;
    act(
      () =>
        void result.current('story.storage', { op: 'set', chests: [A], priority: 'low' }).then((o) => {
          out = o;
        }),
    );
    await settle();
    posts(calls)[0]!.reply({ chests: [A] });
    await settle();
    expect(reads(calls)).toEqual([`/api/story/colonies/${id}?since=10`]);
    const read = writeCatchUp(out);
    expect(read).toBeInstanceOf(Promise);
    // Asking for it again starts nothing more (release.ts settle used to start a second, follower read here).
    expect(writeCatchUp(out)).toBe(read);
    calls.find((c) => c.method === 'GET')!.reply(delta(10, 11));
    await settle();
    await expect(read).resolves.toBeUndefined();
    expect(reads(calls)).toHaveLength(1);
  });

  it('keeps the last 50 and marks a busy refusal as refused', async () => {
    const id = 'ring-busy';
    const calls = network();
    const { qc } = mount(id);
    const { result } = renderHook(() => useStoryWrite(id), { wrapper: provider(qc) });
    for (let i = 0; i < 51; i++) {
      act(
        () => void result.current('story.storage', { op: 'set', chests: [A], priority: 'low' }).catch(() => undefined),
      );
      await settle();
      posts(calls)[i]!.reply({ error: 'busy; retry shortly', code: 'busy', retryAfterMs: 1000 }, 503);
      await settle();
    }
    expect(storyCommands).toHaveLength(50);
    expect(storyCommands.at(-1)).toMatchObject({ colony: id, status: 'refused', http: 503 });
  });
});
