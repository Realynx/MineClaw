import { readFileSync } from 'node:fs';
import type { ServerResponse } from 'node:http';
import { createRequire } from 'node:module';
import { dirname, join } from 'node:path';
import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import { fakeBridge, fetchLogin, json, ORIGIN, readSse, sleep, startApp, type TestApp } from './helpers.js';

const require = createRequire(import.meta.url);
const live = JSON.parse(
  readFileSync(
    join(dirname(require.resolve('@mineclaw/contracts/package.json')), 'test', 'fixtures', 'live.json'),
    'utf8',
  ),
) as Record<string, Record<string, unknown>>;

const upstreams: ServerResponse[] = [];
const lastEventIds: Array<string | undefined> = [];
let closedUpstreams = 0;
let upId = 100;
let bridge: Awaited<ReturnType<typeof fakeBridge>>;
let t: TestApp;
let fox: { cookie: string; csrf: string };

function emit(event: string, data: unknown): void {
  const id = ++upId;
  for (const r of upstreams)
    if (!r.writableEnded) r.write(`event: ${event}\nid: ${id}\ndata: ${JSON.stringify(data)}\n\n`);
}

const entities = (rev: number) => ({
  ...live.worldEntities,
  rev,
  entities: [
    { kind: 'mob', name: 'creeper', dim: -1, x: 1, y: 70, z: 2, id: 9001 },
    { kind: 'item', name: 'cobblestone', dim: 0, x: 5, y: 64, z: 6, id: 3301 },
  ],
  removed: [
    [0, 1],
    [-1, 2],
  ],
});
const now = (bot: string) => ({ ...live.now, bot });
const feed = {
  lines: [
    { seq: 5001, at: '2026-11-02T10:00:00+00:00', bot: 'maid_chan', kind: 'said', from: 'maid_chan', text: 'hello' },
    {
      seq: 5002,
      at: '2026-11-02T10:00:01+00:00',
      bot: 'maid_chan_67',
      kind: 'said',
      from: 'maid_chan_67',
      text: 'sand',
    },
  ],
  last: 5002,
};

function open(query = '', headers: Record<string, string> = {}) {
  const ac = new AbortController();
  const p = fetch(`${t.url}/api/live/stream${query}`, {
    headers: { cookie: fox.cookie, 'Accept-Encoding': 'gzip, br', ...headers },
    signal: ac.signal,
  });
  return { ac, p };
}

async function waitFor(cond: () => boolean, ms = 3000): Promise<void> {
  const end = Date.now() + ms;
  while (!cond()) {
    if (Date.now() > end) throw new Error('timed out');
    await sleep(10);
  }
}

beforeAll(async () => {
  bridge = await fakeBridge((req, res) => {
    if (req.target === '/v1/live/stream') {
      lastEventIds.push(req.headers['last-event-id'] as string | undefined);
      res.writeHead(200, { 'Content-Type': 'text/event-stream' });
      res.write(': hello\n\n');
      upstreams.push(res);
      res.on('close', () => closedUpstreams++);
      return;
    }
    if (req.target === '/v1/health') return json(res, 200, { ok: true, supervisor: 'up' });
    json(res, 404, {});
  });
  t = await startApp(
    { bridge: { url: bridge.url } },
    {
      hub: { upstreamTransport: 'sse', lingerMs: 300, heartbeatMs: 250, sessionCheckMs: 150, ringSize: 8, backoffBaseMs: 50, backoffMaxMs: 100 },
    },
  );
  fox = await fetchLogin(t.url, 'fox');
});
afterAll(async () => {
  await t.close();
  await bridge.close();
});

describe('live stream fan-out', () => {
  it('serves SSE headers without compression or buffering', async () => {
    const { ac, p } = open();
    const res = await p;
    expect(res.status).toBe(200);
    expect(res.headers.get('content-type')).toBe('text/event-stream; charset=utf-8');
    expect(res.headers.get('cache-control')).toBe('no-cache, no-transform');
    expect(res.headers.get('x-accel-buffering')).toBe('no');
    expect(res.headers.get('content-encoding')).toBeNull();
    const frames = await readSse(res.body!, (f) => f.some((x) => x.event === 'status'));
    expect(frames[0]?.event).toBe('status');
    ac.abort();
  });

  it('holds ONE upstream for many browsers and filters per client', async () => {
    const a = open();
    const b = open('?bots=main&topics=now,feed');
    const c = open('?dim=-1&topics=entities');
    const [ra, rb, rc] = await Promise.all([a.p, b.p, c.p]);
    await waitFor(() => t.api.services.hub.upstreamState === 'connected' && t.api.services.hub.clientCount === 3);
    const reads = [ra, rb, rc].map((r) => readSse(r.body!, (f) => f.some((x) => x.event === 'feed'), 3000));
    await sleep(50);
    emit('entities', entities(7000));
    emit('now', now('maid_chan'));
    emit('now', now('maid_chan_67'));
    emit('bogus', { x: 1 }); // not in SSE_EVENTS: ignored
    emit('status', { upstream: 'down' }); // Nest-only: ignored from upstream
    emit('feed', feed);
    const [fa, fb, fc] = (await Promise.all(reads)).map((fs) => fs.filter((f) => f.event !== 'status'));

    expect(fa!.map((f) => f.event)).toEqual(['entities', 'now', 'now', 'feed']);
    expect((fa![0]!.data as { bots: Array<{ poseAge: number }> }).bots[0]!.poseAge).toBe(0.1); // camelCased
    expect(fb!.map((f) => f.event)).toEqual(['now', 'feed']);
    expect((fb![0]!.data as { bot: string }).bot).toBe('maid_chan'); // "main" resolved via the learned alias
    expect((fb![1]!.data as { lines: unknown[] }).lines).toHaveLength(1);
    const ce = fc![0]!.data as { entities: Array<{ dim: number }>; removed: number[][]; bots: unknown[] };
    expect(fc!.map((f) => f.event)).toEqual(['entities']);
    expect(ce.entities.map((e) => e.dim)).toEqual([-1]);
    expect(ce.removed).toEqual([[-1, 2]]);
    expect(ce.bots).toHaveLength(1); // bots always in full
    // ids are Nest's own and identical across clients
    expect(fa![3]!.id).toBe(fb![1]!.id);
    expect(t.api.services.hub.upstreamConnects).toBe(1);
    expect(bridge.requests.filter((r) => r.target === '/v1/live/stream')).toHaveLength(1);
    expect(bridge.requests.find((r) => r.target === '/v1/live/stream')?.identity).toEqual({
      user: '',
      player: '',
      roles: '',
    });
    a.ac.abort();
    b.ac.abort();
    c.ac.abort();
  });

  it('replays after Last-Event-ID, and asks for a resync when the id is too old', async () => {
    const keep = open();
    await keep.p;
    await waitFor(() => t.api.services.hub.upstreamState === 'connected');
    const base = t.api.services.hub.lastId;
    for (let i = 0; i < 3; i++) emit('now', now(`bot${i}`));
    await waitFor(() => t.api.services.hub.lastId === base + 3);
    const r = open('', { 'Last-Event-ID': String(base + 1) });
    const frames = await readSse((await r.p).body!, (f) => f.filter((x) => x.event === 'now').length >= 2);
    expect(frames.filter((f) => f.event === 'now').map((f) => (f.data as { bot: string }).bot)).toEqual([
      'bot1',
      'bot2',
    ]);
    r.ac.abort();

    for (let i = 0; i < 10; i++) emit('now', now(`filler${i}`)); // ring holds 8
    await waitFor(() => t.api.services.hub.lastId === base + 13);
    const old = open('', { 'Last-Event-ID': String(base + 1) });
    const of = await readSse((await old.p).body!, (f) => f.some((x) => x.event === 'status'));
    expect((of[0]!.data as { resync: boolean }).resync).toBe(true);
    old.ac.abort();
    keep.ac.abort();
  });

  it('sends `: ping` and a status frame on the heartbeat', async () => {
    const s = open();
    const res = await s.p;
    const reader = res.body!.getReader();
    let text = '';
    const end = Date.now() + 2000;
    while (!text.includes(': ping') && Date.now() < end) text += new TextDecoder().decode((await reader.read()).value);
    expect(text).toContain(': ping');
    expect(text).toContain('event: status');
    expect(text).toContain('"bot":"up"');
    s.ac.abort();
  });

  it('reconnects upstream with Last-Event-ID and tells browsers to resync', async () => {
    const s = open();
    const res = await s.p;
    await waitFor(() => t.api.services.hub.upstreamState === 'connected');
    const reading = readSse(
      res.body!,
      (f) => f.some((x) => x.event === 'status' && (x.data as { resync: boolean }).resync),
      4000,
    );
    await sleep(30);
    emit('now', now('before-drop'));
    await sleep(50);
    const before = upstreams.length;
    for (const u of upstreams) u.destroy();
    const frames = await reading;
    expect(upstreams.length).toBe(before + 1);
    expect(lastEventIds.at(-1)).toBe(String(upId));
    const states = frames.filter((f) => f.event === 'status').map((f) => (f.data as { upstream: string }).upstream);
    expect(states).toContain('reconnecting');
    expect(states.at(-1)).toBe('connected');
    s.ac.abort();
  });

  it('closes the upstream after the last browser leaves (linger), and reopens on demand', async () => {
    await waitFor(() => t.api.services.hub.clientCount === 0);
    const closedBefore = closedUpstreams;
    await waitFor(() => closedUpstreams > closedBefore, 3000);
    expect(t.api.services.hub.upstreamState).toBe('down');
    const s = open();
    await s.p;
    await waitFor(() => t.api.services.hub.upstreamState === 'connected');
    s.ac.abort();
  });

  it('ends the stream when the session ends', async () => {
    const other = await fetchLogin(t.url, 'robin');
    const res = await fetch(`${t.url}/api/live/stream`, { headers: { cookie: other.cookie } });
    const reader = res.body!.getReader();
    await reader.read();
    await fetch(`${t.url}/api/auth/logout`, {
      method: 'POST',
      headers: { cookie: other.cookie, Origin: ORIGIN, 'X-CSRF-Token': other.csrf, 'Content-Type': 'application/json' },
      body: '{}',
    });
    let done = false;
    const end = Date.now() + 2000;
    while (!done && Date.now() < end) done = (await reader.read()).done;
    expect(done).toBe(true);
  });

  it('requires a session', async () => {
    const r = await fetch(`${t.url}/api/live/stream`);
    expect(r.status).toBe(401);
  });
});
