/**
 * The mock's colony-write hook (`window.__mockStory`, mocks/storyHold.ts), through the page's own API client and the real MSW
 * route handlers: a held write is neither applied nor answered until it is released, and a refused one reaches the page as
 * Nest relays the bot's refusal (a busy colony: 503, code `busy`, `retryAfterMs`) and is not applied.
 */
import { type RouteId } from '@mineclaw/contracts';
import { getResponse, HttpHandler } from 'msw';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { api, ApiError, setCsrfToken, setFetch } from '@/lib/api/client';
import { mockSignIn, resolvers } from '@/mocks/api';
import { seedStory, storySim } from '@/mocks/data/story';
import { handlers } from '@/mocks/handlers';
import { isColonyWrite, mockStory, type MockStoryHook } from '@/mocks/storyHold';

// msw builds its live-stream handler only where the EventSource API exists (the test DOM has none); no test opens a stream.
vi.hoisted(() => vi.stubGlobal('EventSource', class {}));

const ORIGIN = 'http://localhost:3000';
const routes = handlers.filter((h): h is HttpHandler => h instanceof HttpHandler);
const COLONY = { id: 'story-1' };

/** The mock's own delay before it answers (250 ms for a write); the hook's wait has no clock of its own. */
const settle = () => vi.advanceTimersByTimeAsync(1000);

const mineMarks = () => storySim.state(COLONY.id).designations.filter((d) => d.kind === 'mine').length;
let nextCell = 0;
/** A Mine designation on a cell no test used before (the mock director is shared by the file). */
const mine = () =>
  api('story.designations', {
    params: COLONY,
    body: { op: 'add', kind: 'mine', blocks: [[300 + nextCell++, 60, 300]] },
  });

/** A promise's state without waiting for it. */
function watch<T>(p: Promise<T>): { state: 'pending' | 'done' | 'failed'; error?: unknown } {
  const w: { state: 'pending' | 'done' | 'failed'; error?: unknown } = { state: 'pending' };
  p.then(
    () => (w.state = 'done'),
    (e: unknown) => Object.assign(w, { state: 'failed', error: e }),
  );
  return w;
}

beforeEach(() => {
  vi.useFakeTimers();
  mockSignIn('fox');
  seedStory();
  setCsrfToken((resolvers['auth.csrf']!({ params: {}, query: {}, body: null }) as { csrfToken: string }).csrfToken);
  setFetch(async (input, init) => {
    const path = typeof input === 'string' ? input : input instanceof URL ? input.href : input.url;
    const res = await getResponse(routes, new Request(new URL(path, ORIGIN), init), { baseUrl: ORIGIN });
    if (!res) throw new TypeError(`the mock has no route for ${path}`);
    return res;
  });
});

afterEach(() => {
  mockStory.release();
  vi.useRealTimers();
  setCsrfToken(null);
  setFetch((...args) => globalThis.fetch(...args));
});

describe('window.__mockStory', () => {
  it('is the hook the mock handlers obey', () => {
    expect((window as unknown as { __mockStory?: MockStoryHook }).__mockStory).toBe(mockStory);
  });

  it('holds a colony write: not applied and not answered until release, then both, with the revision', async () => {
    const before = mineMarks();
    mockStory.hold();
    const reply = mine();
    const seen = watch(reply);
    await settle();
    expect(seen.state).toBe('pending');
    expect(mineMarks()).toBe(before);
    // Reads are not held: the page still sees the colony while its command waits.
    const read = watch(api('story.get', { params: COLONY }));
    await settle();
    expect(read.state).toBe('done');
    expect(seen.state).toBe('pending');
    mockStory.release();
    const out = await reply;
    expect(out.count).toBe(1);
    expect(mineMarks()).toBe(before + 1);
    expect(out.rev).toBe(storySim.state(COLONY.id).rev);
  });

  it('releases held writes in the order they came', async () => {
    mockStory.hold();
    const first = mine();
    const firstSeen = watch(first);
    await settle();
    const second = mine();
    const secondSeen = watch(second);
    await settle();
    expect([firstSeen.state, secondSeen.state]).toEqual(['pending', 'pending']);
    mockStory.release();
    const [a, b] = await Promise.all([first, second]);
    expect(a.rev!).toBeLessThan(b.rev!);
  });

  it('refuses the next colony write as a busy colony reaches the page, applies nothing, and lets the one after through', async () => {
    const before = mineMarks();
    mockStory.failNext(503, 'busy: the colony did not take the change');
    const refused = watch(mine());
    await settle();
    expect(refused.state).toBe('failed');
    const e = refused.error as ApiError;
    expect(e).toBeInstanceOf(ApiError);
    expect(e).toMatchObject({ status: 503, code: 'busy', retryAfterMs: 1000, message: 'busy; retry shortly' });
    expect(e.body).toEqual({ error: 'busy; retry shortly', code: 'busy', retryAfterMs: 1000 });
    expect(mineMarks()).toBe(before);
    const again = watch(mine());
    await settle();
    expect(again.state).toBe('done');
    expect(mineMarks()).toBe(before + 1);
  });

  it('answers a busy refusal with a Retry-After header, as Nest does', async () => {
    mockStory.failNext(503, 'Busy');
    const csrf = (resolvers['auth.csrf']!({ params: {}, query: {}, body: null }) as { csrfToken: string }).csrfToken;
    const request = new Request(`${ORIGIN}/api/story/colonies/story-1/pause`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', 'X-CSRF-Token': csrf },
      body: JSON.stringify({ paused: true }),
    });
    const reply = getResponse(routes, request, { baseUrl: ORIGIN });
    await settle();
    const res = (await reply)!;
    expect(res.status).toBe(503);
    expect(res.headers.get('Retry-After')).toBe('1');
    expect(storySim.state(COLONY.id).paused).toBe(false);
  });

  it("refuses with the bot's own message for a 4xx, and without it when the outcome is unknown", async () => {
    const refusal = async (status: number, message: string) => {
      mockStory.failNext(status, message);
      const seen = watch(mine());
      await settle();
      const e = seen.error as ApiError;
      return { status: e.status, code: e.code, message: e.message, retryAfterMs: e.retryAfterMs };
    };
    const before = mineMarks();
    expect(await refusal(400, 'no')).toEqual({
      status: 400,
      code: 'bad_request',
      message: 'no',
      retryAfterMs: undefined,
    });
    expect(await refusal(404, 'no colony story-9')).toMatchObject({
      status: 404,
      code: 'not_found',
      message: 'no colony story-9',
    });
    expect(await refusal(503, 'no world view yet')).toMatchObject({
      status: 503,
      code: 'upstream_unavailable',
      message: 'bridge or bot unavailable',
    });
    expect(await refusal(500, 'boom')).toMatchObject({
      status: 502,
      code: 'upstream_error',
      message: 'upstream error',
    });
    expect(mineMarks()).toBe(before);
    expect(() => mockStory.failNext(200, 'fine')).toThrow(RangeError);
  });

  it('a refusal armed while a write is held refuses that write when it is released', async () => {
    const before = mineMarks();
    mockStory.hold();
    const seen = watch(mine());
    await settle();
    mockStory.failNext(503, 'busy');
    expect(seen.state).toBe('pending');
    mockStory.release();
    await settle();
    expect(seen.state).toBe('failed');
    expect(seen.error).toMatchObject({ status: 503, code: 'busy' });
    expect(mineMarks()).toBe(before);
  });

  it('one refusal hits one colony write: a request the mock rejects by itself, a read and a directive do not use it up', async () => {
    mockStory.failNext(400, 'the armed refusal');
    const invalid = watch(
      api('story.designations', { params: COLONY, body: { op: 'add', kind: 'mine', oops: true } as never }),
    );
    const read = watch(api('story.get', { params: COLONY }));
    const directive = watch(
      api('story.directives', {
        body: { bots: ['maid_chan'], directive: { kind: 'text', text: 'hi' }, requestId: 'r-12345678' },
      }),
    );
    await settle();
    expect(invalid.error).toMatchObject({ status: 400, message: 'Invalid request' });
    expect(read.state).toBe('done');
    expect(directive.state).toBe('done');
    const hit = watch(mine());
    await settle();
    expect(hit.error).toMatchObject({ status: 400, message: 'the armed refusal' });
  });

  it("covers the colony's action routes only", () => {
    const held: RouteId[] = [
      'story.designations',
      'story.zones',
      'story.builds',
      'story.orders',
      'story.draft',
      'story.storage',
      'story.bills',
      'story.workbenches',
      'story.beds',
      'story.options',
      'story.pause',
      'story.home',
    ];
    const free: RouteId[] = [
      'story.get',
      'story.list',
      'story.architect',
      'story.create',
      'story.time',
      'story.timeRange',
      'story.directives',
      'bots.chat',
    ];
    for (const id of held) expect(isColonyWrite(id), id).toBe(true);
    for (const id of free) expect(isColonyWrite(id), id).toBe(false);
  });
});
