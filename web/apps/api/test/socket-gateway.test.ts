import { afterAll, beforeAll, expect, it } from 'vitest';
import WebSocket, { WebSocketServer } from 'ws';
import { socketText } from '../src/live/socket-data.js';
import { SignatureVerifier } from '../src/bridge/signing.js';
import { fakeBridge, fetchLogin, json, KEY, ORIGIN, sleep, startApp, type TestApp } from './helpers.js';

let app: TestApp, bridge: Awaited<ReturnType<typeof fakeBridge>>, player: Awaited<ReturnType<typeof fetchLogin>>;
const upstreams = new Set<WebSocket>(), frames: unknown[] = [], identities: unknown[] = [];
const server = new WebSocketServer({ noServer: true, perMessageDeflate: false });
// Literal supervisor wire data: the control relay is transparent, unlike the viewer event normalizer.
// Do not derive this with toSnake(envelope): its opaque `data` would leave camelCase test payloads unchanged.
const controlState = { x: 225, y: 65, z: 379, yaw: 90, pitch: 0, dim: 0, hp: 20, food: 18, held_slot: 2, on_ground: true, window_id: 0 };
const stateFrame = { type: 'state', sampled_at_ms: 1_794_182_403_123, data: {
  session: 'lease', body: 'maid_chan_105', expires_at: '2026-11-09T02:20:03Z', input_timeout_ms: 350, lease_timeout_ms: 3000,
  state: controlState, action: { seq: 1, pending: false, ok: true, message: 'action released' },
} };
const actionFrame = { type: 'action', request_id: 'click', data: { ok: true, message: 'inventory', pending: false,
  state: controlState, inventory: { window_id: 0, kind: 'inventory', title: '', slot_count: 45, container_slots: 0,
    main_start: 9, hotbar_start: 36, slots: [{ slot: 38, id: 5, meta: 0, count: 12 }], cursor: null },
} };
beforeAll(async () => {
  bridge = await fakeBridge((_req, res) => json(res, 200, { ok: true, supervisor: 'up' }));
  const verifier = new SignatureVerifier(KEY);
  bridge.server.on('upgrade', (req, socket, head) => {
    const verified = verifier.verify({ method: 'GET', rawTarget: req.url!, headers: req.headers });
    if (!verified.ok) { socket.destroy(); return; }
    identities.push({ path: req.url, identity: verified.identity, cookie: req.headers.cookie, protocol: req.headers['sec-websocket-protocol'] });
    server.handleUpgrade(req, socket, head, (ws) => {
      upstreams.add(ws); ws.on('close', () => upstreams.delete(ws));
      if (req.url === '/v1/live/socket') ws.send(JSON.stringify({ event: 'bots', id: 1, data: { bots: [] } }));
      ws.on('message', (raw) => {
        const frame = JSON.parse(socketText(raw)) as { type: string };
        frames.push(frame);
        if (frame.type === 'attach') ws.send(JSON.stringify(stateFrame));
        if (frame.type === 'action') ws.send(JSON.stringify(actionFrame));
        // Deliberately withhold input replies. Subsequent input frames must still arrive.
      });
    });
  });
  app = await startApp({ bridge: { url: bridge.url }, web: { validateResponses: 'off' } }, { hub: { sessionCheckMs: 30, lingerMs: 20 } });
  player = await fetchLogin(app.url, 'sky');
});
afterAll(async () => { await app.close(); for (const ws of upstreams) ws.terminate(); server.close(); await bridge.close(); });

function connect(path: string, options: { cookie?: string; origin?: string; csrf?: string } = {}) {
  const control = path.includes('/control/');
  return new WebSocket(app.url.replace('http:', 'ws:') + path,
    control ? ['mineclaw.control.v1', `csrf.${options.csrf ?? player.csrf}`] : ['mineclaw.live.v1'],
    { headers: { Origin: options.origin ?? ORIGIN, Cookie: options.cookie ?? player.cookie } });
}
function opened(ws: WebSocket): Promise<void> { return new Promise((resolve, reject) => { ws.once('open', () => resolve()); ws.once('error', reject); }); }
function refused(ws: WebSocket): Promise<number> {
  return new Promise((resolve, reject) => { ws.once('unexpected-response', (_req, res) => { res.resume(); ws.terminate(); resolve(res.statusCode!); }); ws.on('error', () => {}); ws.once('open', () => reject(new Error('unexpected upgrade'))); });
}

it('requires login, same-site origin, Story role and CSRF before control upgrade', async () => {
  const path = '/api/bodies/maid_chan_105/control/socket';
  expect(await refused(connect(path, { cookie: '' }))).toBe(401);
  expect(await refused(connect(path, { origin: 'https://untrusted.example' }))).toBe(403);
  expect(await refused(connect(path, { csrf: 'wrong' }))).toBe(403);
  const viewer = await fetchLogin(app.url, 'vera');
  expect(await refused(connect(path, viewer))).toBe(403);
  expect(await refused(connect(path + '?session=secret'))).toBe(400);
});

it('forwards signed ordered frames immediately without awaiting input acknowledgements', async () => {
  const ws = connect('/api/bodies/maid_chan_105/control/socket');
  const received: unknown[] = []; ws.on('message', (raw) => received.push(JSON.parse(socketText(raw))));
  await opened(ws);
  expect(ws.protocol).toBe('mineclaw.control.v1');
  ws.send(JSON.stringify({ type: 'attach', session: 'lease' }));
  ws.send(JSON.stringify({ type: 'input', session: 'lease', seq: 1 }));
  ws.send(JSON.stringify({ type: 'input', session: 'lease', seq: 2 }));
  ws.send(JSON.stringify({ type: 'action', request_id: 'click', session: 'lease', seq: 1 }));
  await expect.poll(() => frames.length).toBe(4);
  expect(frames).toEqual([{ type: 'attach', session: 'lease' }, { type: 'input', session: 'lease', seq: 1 }, { type: 'input', session: 'lease', seq: 2 }, { type: 'action', request_id: 'click', session: 'lease', seq: 1 }]);
  expect(identities.at(-1)).toEqual({ path: '/v1/sup/bodies/maid_chan_105/control/socket', identity: { user: 'sky', player: 'sky_mc', roles: 'story,viewer' }, cookie: undefined, protocol: undefined });
  await expect.poll(() => received.length).toBe(2);
  expect(received).toEqual([stateFrame, actionFrame]);
  ws.close();
  await expect.poll(() => upstreams.size).toBe(0);
});

it('fans out one upstream viewer socket, filters topics and resumes missed events', async () => {
  const first = connect('/api/live/socket?topics=entities');
  const second = connect('/api/live/socket?topics=feed');
  const a: Array<{ event: string; id: number; data: unknown }> = [], b: typeof a = [];
  first.on('message', (raw) => a.push(JSON.parse(socketText(raw)))); second.on('message', (raw) => b.push(JSON.parse(socketText(raw))));
  await Promise.all([opened(first), opened(second)]);
  await expect.poll(() => app.api.services.hub.upstreamState).toBe('connected');
  expect(identities.filter((entry) => (entry as { path: string }).path === '/v1/live/socket')).toHaveLength(1);
  // Exercise the actual upstream parser: 64-bit column hashes must not round during JSON conversion.
  const blocks = connect('/api/live/socket?topics=blocks'), blockFrames: typeof a = [];
  blocks.on('message', (raw) => blockFrames.push(JSON.parse(socketText(raw)))); await opened(blocks);
  for (const upstream of upstreams) upstream.send('{"event":"blocks","id":71,"data":{"rev":71,"some_key":true,"cols":[{"hash":18446744073709551615}]}}');
  await expect.poll(() => blockFrames.find((frame) => frame.event === 'blocks')?.data).toEqual({ rev: 71, someKey: true, cols: [{ hash: '18446744073709551615' }] });
  app.api.services.hub.publish('entities', { rev: 7, bots: [], entities: [] });
  app.api.services.hub.publish('feed', { last: 2, lines: [] });
  await expect.poll(() => a.some((frame) => frame.event === 'entities')).toBe(true);
  expect(a.some((frame) => frame.event === 'feed')).toBe(false);
  await expect.poll(() => b.some((frame) => frame.event === 'feed')).toBe(true);
  const last = a.find((frame) => frame.event === 'entities')!.id;
  first.close();
  app.api.services.hub.publish('entities', { rev: 8, bots: [], entities: [] });
  const resumed = connect(`/api/live/socket?topics=entities&lastEventId=${last}`), replay: typeof a = [];
  resumed.on('message', (raw) => replay.push(JSON.parse(socketText(raw)))); await opened(resumed);
  await expect.poll(() => replay.some((frame) => (frame.data as { rev?: number }).rev === 8)).toBe(true);
  second.close(); resumed.close(); blocks.close();
  await sleep(50);
});
