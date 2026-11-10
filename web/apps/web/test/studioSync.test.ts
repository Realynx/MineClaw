/**
 * The Design Studio's canvas sync (studio.ts): the edits reach the bot in requests that fit the bridge's body cap, one at a
 * time and in order with the anchor, a message and a save; a failed request is tried again or stops the sync; a save and a
 * message never go ahead of edits that could not be sent; and the bot's corrections never undo a newer edit.
 */
import {
  DESIGNER_CANVAS_BATCH_BYTES,
  DESIGNER_CANVAS_BODY_CAP,
  DesignerCanvasRequest,
  type DesignerCanvasResponse,
  type DesignerMessageRequest,
  type DesignerMessageResponse,
  type DesignerSaveRequest,
  type DesignerSaveResponse,
  type DesignerSession,
  MOCK_DESIGNER_PALETTE,
  toSnake,
} from '@mineclaw/contracts';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { type Cell, keyOf, setPlotMode } from '@/features/studio/canvas';
import { MineModuleController } from '@/features/studio/mine/MineModuleController';
import { defaultModule } from '@/features/studio/mine/modules';
import { flushWhenHidden, Studio, type StudioApi } from '@/features/studio/studio';
import { ApiError } from '@/lib/api/client';

const session = (over: Partial<DesignerSession> = {}): DesignerSession => ({
  id: 7,
  owner: 'poofimafox',
  model: 'main',
  designId: null,
  name: 'new design',
  rev: 0,
  blocks: [],
  anchor: null,
  messages: [],
  costUsd: 0,
  calls: 0,
  version: 'b1.8.1',
  palette: MOCK_DESIGNER_PALETTE,
  plot: { size: 64, height: 48, depth: 16 },
  revisions: [],
  ...over,
});

const mine = (): DesignerSession =>
  session({ version: '1.12.2', kind: 'mine', seed: '12345', plot: { size: 256, height: 128, depth: 0 } });

/** `n` different cells of a mine, each as long as a cell gets in JSON (21 bytes with its comma): three digits everywhere. */
function longest(n: number, from = 0): Cell[] {
  return Array.from({ length: n }, (_, j): Cell => {
    const i = from + j;
    return [100 + (i % 156), 100 + (Math.floor(i / 24_336) % 28), 100 + (Math.floor(i / 156) % 156), 252, 15];
  });
}

/**
 * The size of a canvas body as the bot reads it: the bridge adds who is designing and writes the body again, turning quotes,
 * plus signs, angle brackets, ampersands and everything outside ASCII inside strings into 6-byte escapes.
 */
function upstreamBytes(body: DesignerCanvasRequest): number {
  const escaped = (text: string) => text.replace(/[^\x20-\x7e]|["+<>&'`\\]/g, 'uXXXXX');
  const upstream = toSnake({ ...body, player: 'x', who: 'x (web)' });
  return JSON.stringify(upstream, (_key, value: unknown) => (typeof value === 'string' ? escaped(value) : value))
    .length;
}

function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((r) => (resolve = r));
  return { promise, resolve };
}

/** A refusal as the API client throws it, reduced to what the studio reads off it: a status and a message. */
const refuse = (status: number, message: string, more: object = {}): Promise<never> =>
  Promise.reject(Object.assign(new Error(message), { status, ...more }));

type Call =
  | { route: 'canvas'; body: DesignerCanvasRequest }
  | { route: 'message'; body: DesignerMessageRequest }
  | { route: 'save'; body: DesignerSaveRequest };
type CanvasCall = Extract<Call, { route: 'canvas' }>;

const canvasCalls = (calls: Call[]): CanvasCall[] => calls.filter((c): c is CanvasCall => c.route === 'canvas');
const sentCells = (calls: Call[]): Cell[] => canvasCalls(calls).flatMap((c) => c.body.ops.flatMap((o) => o.cells));

/** "canvas a+b" (its edits' labels), "canvas anchor 1,0,1", "canvas no anchor", "message hi", "save hut". */
function what(c: Call): string {
  if (c.route === 'message') return `message ${c.body.text}`;
  if (c.route === 'save') return `save ${c.body.name}`;
  if (c.body.anchor) return `canvas anchor ${c.body.anchor.join(',')}`;
  return c.body.clearAnchor ? 'canvas no anchor' : `canvas ${c.body.ops.map((o) => o.label).join('+')}`;
}

const messageReply = (ops: DesignerMessageResponse['ops'] = []): DesignerMessageResponse => ({
  reply: 'Done.',
  ops,
  rev: 9,
  costUsd: 0.01,
  sessionCostUsd: 0.01,
  model: 'claude-opus-5-5',
  calls: 1,
  usage: { input: 1, output: 1, cacheRead: 0, cacheWrite: 0 },
  message: { seq: 2, role: 'assistant', text: 'Done.', at: 1 },
  userMessage: { seq: 1, role: 'user', text: 'asked', at: 1 },
});

/** A designer API that records every request in order and counts how many are out at once. */
function fakeApi(
  on: {
    canvas?: (body: DesignerCanvasRequest, nth: number) => Promise<DesignerCanvasResponse>;
    message?: (body: DesignerMessageRequest) => Promise<DesignerMessageResponse>;
    save?: (body: DesignerSaveRequest) => Promise<DesignerSaveResponse>;
  } = {},
) {
  const calls: Call[] = [];
  let out = 0;
  let most = 0;
  let canvases = 0;
  let rev = 0;
  const tracked = <T>(run: () => Promise<T>): Promise<T> => {
    most = Math.max(most, ++out);
    return run().finally(() => {
      out--;
    });
  };
  /** The bot's answer to a canvas request it applied. */
  const ok = (body: DesignerCanvasRequest): DesignerCanvasResponse => ({
    rev: ++rev,
    applied: body.ops.reduce((n, o) => n + o.cells.length, 0),
  });
  const api: StudioApi = {
    canvas: (_id, body) => {
      calls.push({ route: 'canvas', body });
      const nth = ++canvases;
      return tracked(() => (on.canvas ? on.canvas(body, nth) : Promise.resolve(ok(body))));
    },
    message: (_id, body) => {
      calls.push({ route: 'message', body });
      return tracked(() => (on.message ? on.message(body) : Promise.resolve(messageReply())));
    },
    save: (_id, body) => {
      calls.push({ route: 'save', body });
      const saved = { designId: 5, rev: 1, name: body.name, blocks: 1, created: true, text: 'saved' };
      return tracked(() => (on.save ? on.save(body) : Promise.resolve(saved)));
    },
  };
  return { api, calls, ok, most: () => most };
}

/** Lets every promise that can settle do so; no time passes. */
const idle = () => vi.advanceTimersByTimeAsync(0);

beforeEach(() => {
  vi.useFakeTimers();
});
afterEach(() => {
  vi.useRealTimers();
  setPlotMode({ kind: 'build' });
});

describe('requests that fit the body cap', () => {
  it('no canvas body exceeds the cap: a 60 000-cell edit arrives in order, in requests the bridge and the bot accept', async () => {
    const sizes: number[] = [];
    const f = fakeApi({
      canvas: (body) => {
        sizes.push(upstreamBytes(body));
        return Promise.resolve(f.ok(body));
      },
    });
    const s = new Studio(mine(), f.api, 60_000);
    const cells = longest(60_000);
    expect(s.edit(cells, 'é'.repeat(40), "“+<&>'” 採掘 ".repeat(30))).toHaveLength(60_000);
    expect(s.snapshot().pending).toBe(60_000);
    await s.flush();
    expect(sizes.length).toBeGreaterThan(1);
    expect(Math.max(...sizes)).toBeLessThan(DESIGNER_CANVAS_BODY_CAP);
    // A full request uses what it may: the estimate does not waste the room under the cap.
    expect(sizes[0]).toBeGreaterThan(DESIGNER_CANVAS_BATCH_BYTES * 0.9);
    for (const c of f.calls) expect(DesignerCanvasRequest.safeParse(c.body).success).toBe(true);
    expect(sentCells(f.calls)).toEqual(cells);
    expect(s.snapshot()).toMatchObject({ pending: 0, sync: 'idle', syncError: null });
    expect(f.most()).toBe(1);
    s.dispose();
  });

  it('edits made in one flush window share requests up to that size and the contract’s 64 edits, in the order made', async () => {
    const f = fakeApi();
    const s = new Studio(mine(), f.api, 60_000);
    // A 1 000-cell floor moved 100 times before the first flush, then 200 single cells.
    const moves = Array.from({ length: 100 }, (_, i) => longest(1000, i * 1000));
    moves.forEach((cells, i) => s.edit(cells, 'mine-layout', `move ${i}`));
    const clicks = longest(200, 100_000).map((c) => [c]);
    for (const cells of clicks) s.edit(cells, 'place', 'placed');
    await s.flush();
    const sent = canvasCalls(f.calls);
    for (const c of sent) {
      expect(upstreamBytes(c.body)).toBeLessThan(DESIGNER_CANVAS_BODY_CAP);
      expect(DesignerCanvasRequest.safeParse(c.body).success).toBe(true);
    }
    expect(sent.flatMap((c) => c.body.ops.map((o) => o.label))).toEqual([
      ...moves.map((_, i) => `move ${i}`),
      ...clicks.map(() => 'placed'),
    ]);
    expect(sentCells(f.calls)).toEqual([...moves.flat(), ...clicks.flat()]);
    // 300 edits in far fewer requests: about nine floors, or 64 edits, in each.
    expect(sent.length).toBeLessThan(20);
    expect(s.snapshot()).toMatchObject({ pending: 0, sync: 'idle' });
    s.dispose();
  });

  it('a 413 shrinks the requests and the queue still drains; later edits are cut to the smaller size', async () => {
    let refusals = 0;
    const arrived: Cell[] = [];
    const f = fakeApi({
      canvas: (body) => {
        if (upstreamBytes(body) > 100_000) {
          refusals++;
          return refuse(413, 'payload too large');
        }
        for (const o of body.ops) for (const c of o.cells) arrived.push(c);
        return Promise.resolve(f.ok(body));
      },
    });
    const s = new Studio(mine(), f.api, 60_000);
    const first = longest(30_000);
    s.edit(first, 'mine-layout', 'a floor');
    await s.flush();
    expect(refusals).toBe(1);
    expect(s.snapshot()).toMatchObject({ pending: 0, sync: 'idle', syncError: null });
    const second = longest(20_000, 30_000);
    s.edit(second, 'mine-layout', 'another floor');
    await s.flush();
    expect(refusals).toBe(1);
    expect(arrived).toEqual([...first, ...second]);
    s.dispose();
  });

  it('a branch mine too large for one request, then moved before the first flush, reaches the bot whole', async () => {
    // The bot as the bridge guards it: a body over the cap is answered 413 and none of it is applied.
    const bot = new Map<number, number>();
    const sizes: number[] = [];
    const f = fakeApi({
      canvas: (body) => {
        sizes.push(upstreamBytes(body));
        if (sizes.at(-1)! > DESIGNER_CANVAS_BODY_CAP) return refuse(413, 'payload too large');
        for (const o of body.ops)
          for (const c of o.cells) bot.set(keyOf(c[0], c[1], c[2]), c[3] < 0 ? -1 : c[3] * 16 + c[4]);
        return Promise.resolve(f.ok(body));
      },
    });
    const s = new Studio(mine(), f.api, 60_000);
    const c = new MineModuleController(s, null);
    const floor = c.add({ ...defaultModule('branches'), length: 192, branchLength: 96, spacing: 3 }, [128, 11, 128])!;
    expect(s.snapshot().pending).toBeGreaterThan(15_000);
    // Dragged two blocks and dropped a level within one flush window: every step is an edit of its own.
    expect(c.move(floor.id, [129, 11, 128], 'drag')).toBe(true);
    expect(c.move(floor.id, [130, 11, 128], 'drag')).toBe(true);
    expect(c.move(floor.id, [130, 10, 128])).toBe(true);
    await s.flush();
    expect(sizes.length).toBeGreaterThan(3);
    expect(Math.max(...sizes)).toBeLessThan(DESIGNER_CANVAS_BODY_CAP);
    expect(s.snapshot()).toMatchObject({ pending: 0, sync: 'idle', syncError: null });
    // The bot's canvas is the page's: every cell it was told about holds its last state, and it was told every dug cell.
    expect([...bot].filter(([k, state]) => s.canvas.authoredState(k) !== state)).toEqual([]);
    expect([...s.canvas.dug].filter((k) => bot.get(k) !== 0)).toEqual([]);
    expect(s.canvas.dug.size).toBeGreaterThan(15_000);
    c.dispose();
    s.dispose();
  });

  it('stops when even the smallest request is answered 413, and loses nothing', async () => {
    let capped = true;
    const arrived: Cell[] = [];
    const f = fakeApi({
      canvas: (body) => {
        if (capped) return refuse(413, 'payload too large');
        for (const o of body.ops) for (const c of o.cells) arrived.push(c);
        return Promise.resolve(f.ok(body));
      },
    });
    const s = new Studio(mine(), f.api, 10);
    const cells = longest(5000);
    s.edit(cells, 'mine-layout', 'a floor');
    await vi.advanceTimersByTimeAsync(10);
    // Five tries, each half the size of the one before, down to the smallest.
    const sizes = canvasCalls(f.calls).map((c) => upstreamBytes(c.body));
    expect(sizes).toHaveLength(5);
    expect(sizes.every((size, i) => i === 0 || size < sizes[i - 1]!)).toBe(true);
    expect(sizes[4]).toBeLessThan(16_000);
    expect(s.snapshot()).toMatchObject({ pending: 5000, sync: 'blocked', syncError: 'payload too large' });
    await vi.advanceTimersByTimeAsync(600_000);
    expect(f.calls).toHaveLength(5);
    capped = false;
    await s.retrySync();
    expect(arrived).toEqual(cells);
    expect(s.snapshot()).toMatchObject({ pending: 0, sync: 'idle', syncError: null });
    s.dispose();
  });
});

describe('one request at a time', () => {
  it('in the order asked: edits, the anchor, a save, then an edit made meanwhile', async () => {
    const gates: Array<(reply: DesignerCanvasResponse) => void> = [];
    const saving = deferred<DesignerSaveResponse>();
    const f = fakeApi({
      canvas: () => {
        const gate = deferred<DesignerCanvasResponse>();
        gates.push(gate.resolve);
        return gate.promise;
      },
      save: () => saving.promise,
    });
    const s = new Studio(session(), f.api, 10);
    s.edit([[1, 0, 1, 4, 0]], 'place', 'a');
    await vi.advanceTimersByTimeAsync(10);
    expect(f.calls).toHaveLength(1);
    // A second edit while the first request is out: its debounce, and a flush, must not start another request.
    s.edit([[2, 0, 2, 4, 0]], 'place', 'b');
    await vi.advanceTimersByTimeAsync(10);
    void s.flush();
    const anchored = s.setAnchor([1, 0, 1]);
    const saved = s.save('hut');
    await idle();
    expect(f.calls).toHaveLength(1);
    gates[0]!({ rev: 1, applied: 1 });
    await idle();
    expect(f.calls).toHaveLength(2);
    gates[1]!({ rev: 2, applied: 1 });
    await idle();
    expect(f.calls).toHaveLength(3);
    gates[2]!({ rev: 3, applied: 0 });
    await anchored;
    await idle();
    expect(f.calls).toHaveLength(4);
    // An edit made while the save is out waits for it, and stays unsaved.
    s.edit([[3, 0, 3, 4, 0]], 'place', 'c');
    await vi.advanceTimersByTimeAsync(10);
    expect(f.calls).toHaveLength(4);
    saving.resolve({ designId: 5, rev: 1, name: 'hut', blocks: 2, created: true, text: 'saved' });
    await saved;
    await idle();
    expect(f.calls).toHaveLength(5);
    gates[3]!({ rev: 4, applied: 1 });
    await idle();
    expect(f.calls.map(what)).toEqual(['canvas a', 'canvas b', 'canvas anchor 1,0,1', 'save hut', 'canvas c']);
    expect(f.most()).toBe(1);
    expect(s.snapshot()).toMatchObject({ pending: 0, sync: 'idle', rev: 4, designId: 5, dirty: true });
    s.dispose();
  });

  it('a message waits for the queue: the waiting edits go first through the canvas route and the message carries none', async () => {
    const f = fakeApi();
    const s = new Studio(session(), f.api, 60_000);
    s.edit([[1, 0, 1, 4, 0]], 'place', 'a');
    s.edit([[2, 0, 2, 4, 0]], 'place', 'b');
    const sent = s.send('add a door');
    // Busy from the first moment, while its edits are still going out: a second Enter sends nothing.
    expect(s.snapshot().busy).toBe(true);
    expect(await s.send('add a door')).toBeNull();
    await sent;
    expect(f.calls.map(what)).toEqual(['canvas a+b', 'message add a door']);
    expect(f.calls[1]!.body).toEqual({ text: 'add a door', canvas: { rev: 1 } });
    expect(f.most()).toBe(1);
    s.dispose();
  });

  it('an edit made while a reply is being applied goes out in the same drain', async () => {
    const f = fakeApi();
    const s = new Studio(session(), f.api, 60_000);
    // Something listening (the mine components, the page) answers the first batch's arrival with an edit of its own.
    let answered = false;
    s.subscribe(() => {
      if (answered || s.snapshot().pending !== 0) return;
      answered = true;
      s.edit([[2, 0, 2, 4, 0]], 'place', 'b');
    });
    s.edit([[1, 0, 1, 4, 0]], 'place', 'a');
    await s.flush();
    expect(f.calls.map(what)).toEqual(['canvas a', 'canvas b']);
    expect(s.snapshot()).toMatchObject({ pending: 0, sync: 'idle' });
    s.dispose();
  });

  it('a message is not sent ahead of edits that could not be', async () => {
    const f = fakeApi({ canvas: () => refuse(503, 'upstream unavailable') });
    const s = new Studio(session(), f.api, 60_000);
    s.edit([[1, 0, 1, 4, 0]], 'place', 'a');
    await expect(s.send('add a door')).rejects.toThrow(
      'The latest edits have not reached the designer (upstream unavailable). The message was not sent.',
    );
    expect(f.calls.map(what)).toEqual(['canvas a']);
    expect(s.snapshot()).toMatchObject({ busy: false, messages: [], pending: 1, sync: 'retrying' });
    expect(s.snapshot().error).toContain('The message was not sent.');
    // The failed batch keeps its own retry: the refused message does not start another try at once.
    await idle();
    expect(f.calls).toHaveLength(1);
    s.dispose();
  });

  it('a save during a message is refused at once instead of waiting behind the model', async () => {
    const answer = deferred<DesignerMessageResponse>();
    const f = fakeApi({ message: () => answer.promise });
    const s = new Studio(session({ blocks: [[1, 0, 1, 4, 0]] }), f.api, 60_000);
    const sent = s.send('add a door');
    await idle();
    await expect(s.save('hut')).rejects.toThrow('The assistant is still answering. Nothing was saved.');
    answer.resolve(messageReply());
    await sent;
    await s.save('hut');
    expect(f.calls.map(what)).toEqual(['message add a door', 'save hut']);
    s.dispose();
  });

  it('the anchor follows the edits in a request of its own and is sent again after a failure', async () => {
    let anchorTries = 0;
    const f = fakeApi({
      canvas: (body) =>
        body.ops.length === 0 && ++anchorTries <= 2 ? refuse(503, 'upstream unavailable') : Promise.resolve(f.ok(body)),
    });
    const s = new Studio(session(), f.api, 60_000);
    s.edit([[1, 0, 1, 4, 0]], 'place', 'a');
    await expect(s.setAnchor([1, 0, 1])).rejects.toThrow('The latest edits have not reached the designer');
    expect(f.calls.map(what)).toEqual(['canvas a', 'canvas anchor 1,0,1']);
    expect(s.snapshot()).toMatchObject({ anchor: [1, 0, 1], pending: 0, sync: 'retrying' });
    // A save now would keep the bot's old anchor: refused, like one with unsent cells.
    await expect(s.save('hut')).rejects.toThrow('Nothing was saved.');
    expect(f.calls.map(what)).toEqual(['canvas a', 'canvas anchor 1,0,1', 'canvas anchor 1,0,1']);
    await vi.advanceTimersByTimeAsync(4000);
    expect(f.calls.map(what)).toEqual(['canvas a', ...Array<string>(3).fill('canvas anchor 1,0,1')]);
    expect(f.calls[3]!.body).toEqual({ rev: 1, ops: [], anchor: [1, 0, 1] });
    expect(s.snapshot()).toMatchObject({ anchor: [1, 0, 1], sync: 'idle', syncError: null });
    await s.setAnchor(null);
    expect(f.calls[4]!.body).toEqual({ rev: 2, ops: [], clearAnchor: true });
    expect(s.snapshot()).toMatchObject({ anchor: null, sync: 'idle' });
    s.dispose();
  });
});

describe('a failed canvas request', () => {
  it('a 4xx blocks instead of spinning: nothing more is sent until retrySync', async () => {
    let gone = true;
    const f = fakeApi({ canvas: (body) => (gone ? refuse(404, 'no such session') : Promise.resolve(f.ok(body))) });
    const s = new Studio(session(), f.api, 10);
    s.edit([[1, 0, 1, 4, 0]], 'place', 'a');
    await vi.advanceTimersByTimeAsync(10);
    expect(f.calls).toHaveLength(1);
    expect(s.snapshot()).toMatchObject({ pending: 1, sync: 'blocked', syncError: 'no such session' });
    s.edit([[2, 0, 2, 4, 0]], 'place', 'b');
    await vi.advanceTimersByTimeAsync(600_000);
    await s.flush();
    expect(f.calls).toHaveLength(1);
    expect(vi.getTimerCount()).toBe(0);
    await expect(s.settle('Nothing was saved.')).rejects.toThrow(
      'The latest edits have not reached the designer (no such session). Nothing was saved.',
    );
    gone = false;
    await s.retrySync();
    expect(f.calls.map(what)).toEqual(['canvas a', 'canvas a+b']);
    expect(s.snapshot()).toMatchObject({ pending: 0, sync: 'idle', syncError: null });
    s.dispose();
  });

  it.each<[string, unknown]>([
    ['a 503', Object.assign(new Error('upstream unavailable'), { status: 503 })],
    ['a 502', Object.assign(new Error('bad gateway'), { status: 502 })],
    ['a 429', Object.assign(new Error('the session is busy'), { status: 429 })],
    // The bot's own "busy": its session lock was held past a second by another request (DesignerRoutes.Locked).
    ['a 409', Object.assign(new Error('the session is busy (a message is still running)'), { status: 409 })],
    ['a 408', Object.assign(new Error('request timeout'), { status: 408 })],
    ['no connection', new ApiError(0, 'The server could not be reached.', { code: 'upstream_unavailable' })],
    ['an error without a status', new Error('boom')],
    ['a plain object', { status: 503, message: 'upstream unavailable' }],
  ])('%s is tried again after a wait, not given up on', async (_name, failure) => {
    let down = true;
    const f = fakeApi({
      // eslint-disable-next-line @typescript-eslint/prefer-promise-reject-errors -- whatever a client may throw
      canvas: (body) => (down ? Promise.reject(failure) : Promise.resolve(f.ok(body))),
    });
    const s = new Studio(session(), f.api, 10);
    s.edit([[1, 0, 1, 4, 0]], 'place', 'a');
    await vi.advanceTimersByTimeAsync(10);
    expect(f.calls).toHaveLength(1);
    expect(s.snapshot()).toMatchObject({ pending: 1, sync: 'retrying' });
    expect(s.snapshot().syncError).toBeTruthy();
    down = false;
    await vi.advanceTimersByTimeAsync(1999);
    expect(f.calls).toHaveLength(1);
    await vi.advanceTimersByTimeAsync(1);
    expect(f.calls).toHaveLength(2);
    expect(s.snapshot()).toMatchObject({ pending: 0, sync: 'idle', syncError: null });
    s.dispose();
  });

  it('waits 2, 4, 8 … s between tries and at least as long as the server asked; edits made meanwhile wait with it', async () => {
    const f = fakeApi({
      canvas: (body, nth) => {
        if (nth <= 2) return refuse(503, 'upstream unavailable');
        if (nth === 3) return refuse(429, 'slow down', { retryAfterMs: 20_000 });
        return Promise.resolve(f.ok(body));
      },
    });
    const s = new Studio(session(), f.api, 10);
    s.edit([[1, 0, 1, 4, 0]], 'place', 'a');
    await vi.advanceTimersByTimeAsync(10);
    expect(f.calls).toHaveLength(1);
    // Its own debounce does not hurry the retry.
    s.edit([[2, 0, 2, 4, 0]], 'place', 'b');
    await vi.advanceTimersByTimeAsync(1999);
    expect(f.calls).toHaveLength(1);
    await vi.advanceTimersByTimeAsync(1);
    expect(f.calls).toHaveLength(2);
    await vi.advanceTimersByTimeAsync(3999);
    expect(f.calls).toHaveLength(2);
    await vi.advanceTimersByTimeAsync(1);
    expect(f.calls).toHaveLength(3);
    expect(s.snapshot()).toMatchObject({ pending: 2, sync: 'retrying', syncError: 'slow down' });
    // The third wait would be 8 s; the server asked for 20.
    await vi.advanceTimersByTimeAsync(19_999);
    expect(f.calls).toHaveLength(3);
    await vi.advanceTimersByTimeAsync(1);
    expect(f.calls.map(what)).toEqual(['canvas a', 'canvas a+b', 'canvas a+b', 'canvas a+b']);
    expect(s.snapshot()).toMatchObject({ pending: 0, sync: 'idle', syncError: null });
    s.dispose();
  });

  it('an explicit flush tries at once instead of waiting for the retry', async () => {
    let down = true;
    const f = fakeApi({ canvas: (body) => (down ? refuse(503, 'upstream unavailable') : Promise.resolve(f.ok(body))) });
    const s = new Studio(session(), f.api, 10);
    s.edit([[1, 0, 1, 4, 0]], 'place', 'a');
    await vi.advanceTimersByTimeAsync(10);
    expect(s.snapshot().sync).toBe('retrying');
    down = false;
    await s.flush();
    expect(f.calls).toHaveLength(2);
    expect(s.snapshot().sync).toBe('idle');
    expect(vi.getTimerCount()).toBe(0);
    s.dispose();
  });

  it('save refuses when edits could not be sent, and saves once they have been', async () => {
    let down = true;
    const f = fakeApi({
      canvas: (body) =>
        down ? Promise.reject(new ApiError(503, 'upstream unavailable', {})) : Promise.resolve(f.ok(body)),
    });
    const s = new Studio(session(), f.api, 60_000);
    s.edit([[1, 0, 1, 4, 0]], 'place', 'a');
    await expect(s.save('hut')).rejects.toThrow(
      'The latest edits have not reached the designer (The bot is not reachable right now). Nothing was saved.',
    );
    expect(f.calls.map(what)).toEqual(['canvas a']);
    expect(s.dirty).toBe(true);
    expect(s.snapshot()).toMatchObject({ designId: null, pending: 1, sync: 'retrying' });
    down = false;
    await s.save('hut');
    expect(f.calls.map(what)).toEqual(['canvas a', 'canvas a', 'save hut']);
    expect(s.dirty).toBe(false);
    s.dispose();
  });

  it('settle says why the edits have not arrived, and passes when a message alone is running', async () => {
    const answer = deferred<DesignerMessageResponse>();
    const f = fakeApi({ message: () => answer.promise });
    const s = new Studio(session(), f.api, 60_000);
    await s.settle();
    const sent = s.send('hello');
    await idle();
    await s.settle();
    s.edit([[1, 0, 1, 4, 0]], 'place', 'a');
    await expect(s.settle()).rejects.toThrow(
      'The latest edits have not reached the designer (a message to the assistant is still running).',
    );
    answer.resolve(messageReply());
    await sent;
    await s.settle();
    expect(f.calls.map(what)).toEqual(['message hello', 'canvas a']);
    s.dispose();
  });
});

describe("the bot's corrections", () => {
  it('a late correction never overrides a newer edit', async () => {
    const first = deferred<DesignerCanvasResponse>();
    const f = fakeApi({ canvas: (body, nth) => (nth === 1 ? first.promise : Promise.resolve(f.ok(body))) });
    const walls: Cell[] = [
      [10, 1, 10, 4, 0],
      [12, 1, 12, 4, 0],
    ];
    const s = new Studio(session({ blocks: walls }), f.api, 60_000);
    // Two wall torches go out facing away from their walls: the bot will turn both.
    s.edit(
      [
        [9, 1, 10, 50, 1],
        [11, 1, 12, 50, 1],
      ],
      'place',
      'placed torches',
    );
    const sending = s.flush();
    await idle();
    // One is taken away again while that request is still out.
    s.edit([[9, 1, 10, 0, 0]], 'remove', 'removed a torch');
    first.resolve({
      rev: 2,
      applied: 2,
      fixed: [
        [9, 1, 10, 50, 2],
        [11, 1, 12, 50, 2],
      ],
    });
    await sending;
    expect(s.canvas.get(9, 1, 10)).toEqual([0, 0]);
    expect(s.canvas.get(11, 1, 12)).toEqual([50, 2]);
    expect(f.calls.map(what)).toEqual(['canvas placed torches', 'canvas removed a torch']);
    expect(canvasCalls(f.calls)[1]!.body.ops[0]!.cells).toEqual([[9, 1, 10, 0, 0]]);
    s.dispose();
  });

  it('corrections that come with a reply follow the same rule, and edits made during the turn go after it', async () => {
    const answer = deferred<DesignerMessageResponse>();
    const f = fakeApi({ message: () => answer.promise });
    const torches: Cell[] = [
      [9, 1, 10, 50, 1],
      [11, 1, 12, 50, 1],
    ];
    const s = new Studio(session({ blocks: torches }), f.api, 10);
    const sent = s.send('tidy the torches');
    await idle();
    s.edit([[9, 1, 10, 0, 0]], 'remove', 'removed a torch');
    // Held while the bot's session is busy with the message.
    await vi.advanceTimersByTimeAsync(10_000);
    expect(f.calls.map(what)).toEqual(['message tidy the torches']);
    const fixed: Cell[] = [
      [9, 1, 10, 50, 2],
      [11, 1, 12, 50, 2],
    ];
    answer.resolve(messageReply([{ kind: 'fix', label: 'turned 2 wall torches around', by: 'ai', cells: fixed }]));
    await sent;
    await idle();
    expect(s.canvas.get(9, 1, 10)).toEqual([0, 0]);
    expect(s.canvas.get(11, 1, 12)).toEqual([50, 2]);
    expect(f.calls.map(what)).toEqual(['message tidy the torches', 'canvas removed a torch']);
    s.dispose();
  });
});

describe('a page going away', () => {
  it('sends the waiting edits at once when it is hidden or closing', async () => {
    const f = fakeApi();
    const s = new Studio(session(), f.api, 60_000);
    const doc = Object.assign(new EventTarget(), { visibilityState: 'visible' });
    const win = new EventTarget();
    const off = flushWhenHidden(s, doc as unknown as Document, win as unknown as Window);
    s.edit([[1, 0, 1, 4, 0]], 'place', 'a');
    // Shown again: the debounce is enough.
    doc.dispatchEvent(new Event('visibilitychange'));
    await idle();
    expect(f.calls).toHaveLength(0);
    doc.visibilityState = 'hidden';
    doc.dispatchEvent(new Event('visibilitychange'));
    await idle();
    expect(f.calls.map(what)).toEqual(['canvas a']);
    s.edit([[2, 0, 2, 4, 0]], 'place', 'b');
    win.dispatchEvent(new Event('pagehide'));
    await idle();
    expect(f.calls.map(what)).toEqual(['canvas a', 'canvas b']);
    off();
    s.edit([[3, 0, 3, 4, 0]], 'place', 'c');
    win.dispatchEvent(new Event('pagehide'));
    doc.dispatchEvent(new Event('visibilitychange'));
    await idle();
    expect(f.calls).toHaveLength(2);
    s.dispose();
    expect(vi.getTimerCount()).toBe(0);
  });

  it('a studio let go sends what waits for the debounce once and does not try again; subscribed again, it is live', async () => {
    let down = true;
    const f = fakeApi({ canvas: (body) => (down ? refuse(503, 'upstream unavailable') : Promise.resolve(f.ok(body))) });
    const s = new Studio(session(), f.api, 400);
    s.edit([[1, 0, 1, 4, 0]], 'place', 'a');
    s.dispose();
    await idle();
    expect(f.calls.map(what)).toEqual(['canvas a']);
    await vi.advanceTimersByTimeAsync(600_000);
    expect(f.calls).toHaveLength(1);
    expect(vi.getTimerCount()).toBe(0);
    // React's development build disposes a live studio and subscribes again: failures are retried then, as before.
    const off = s.subscribe(() => undefined);
    s.edit([[2, 0, 2, 4, 0]], 'place', 'b');
    await vi.advanceTimersByTimeAsync(400);
    expect(f.calls.map(what)).toEqual(['canvas a', 'canvas a+b']);
    down = false;
    await vi.advanceTimersByTimeAsync(2000);
    expect(f.calls.map(what)).toEqual(['canvas a', 'canvas a+b', 'canvas a+b']);
    expect(s.snapshot()).toMatchObject({ pending: 0, sync: 'idle' });
    off();
    s.dispose();
  });

  it('a studio let go within the debounce still gets its edits to the bot', async () => {
    const f = fakeApi();
    const s = new Studio(session(), f.api, 400);
    s.edit([[1, 0, 1, 4, 0]], 'place', 'a');
    s.dispose();
    await idle();
    expect(f.calls.map(what)).toEqual(['canvas a']);
    expect(s.snapshot()).toMatchObject({ pending: 0, sync: 'idle' });
  });
});
