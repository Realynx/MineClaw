/** The generic bridge forward against a recording fake bridge: casing, identity, chat injection, binary streaming, errors. */
import { readFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import { dirname, join } from 'node:path';
import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import busboy from 'busboy';
import { ShardExportRequest } from '@mineclaw/contracts';
import { generate } from '../src/mock/generate.js';
import {
  fakeBridge,
  fetchLogin,
  json,
  login,
  ORIGIN,
  sleep,
  startApp,
  type RecordedRequest,
  type Session,
  type TestApp,
} from './helpers.js';

const require = createRequire(import.meta.url);
const fixtures = join(dirname(require.resolve('@mineclaw/contracts/package.json')), 'test', 'fixtures');
const liveText = readFileSync(join(fixtures, 'live.json'), 'utf8');
const live = JSON.parse(liveText) as Record<string, unknown>;
const worldIndexRaw = live.worldIndexRaw as string;

let releaseStream: (() => void) | undefined;
let bridge: Awaited<ReturnType<typeof fakeBridge>>;
let t: TestApp;
let fox: Session;
let robin: Session;
let cora: Session;
let foxCookie: string;

function last(path: string): RecordedRequest {
  const r = [...bridge.requests].reverse().find((x) => x.target.split('?')[0] === path);
  if (!r) throw new Error(`no bridge request to ${path}`);
  return r;
}

beforeAll(async () => {
  bridge = await fakeBridge((req, res) => {
    const path = req.target.split('?')[0];
    switch (path) {
      case '/v1/sup/bodies/b1/script':
        return json(res, 200, live.bodyScript);
      case '/v1/sup/world/index':
        res.writeHead(200, { 'Content-Type': 'application/json' }).end(worldIndexRaw);
        return;
      case '/v1/memory-activity/heat':
        return json(res, 200, live.memoryHeat);
      case '/v1/sup/viewer/chat':
        return json(res, 200, { accepted: true, id: 2042, feed_seq: 2042 });
      case '/v1/sup/viewer/now':
        return json(res, 200, { bot: 'x' }); // missing `at`: contract mismatch
      case '/v1/sup/viewer/bodies/maid_chan_67/inventory':
        return json(res, 200, live.inventory);
      case '/v1/sup/viewer/bodies/maid_chan_102/inventory':
        return json(res, 200, live.inventoryOffline);
      case '/v1/sup/world/containers': {
        const q = new URLSearchParams(req.target.split('?')[1] ?? '');
        if (q.has('x0')) return json(res, 200, live.containerList);
        if (q.get('x') === '20') return json(res, 200, live.containerUnknown);
        if (q.get('x') === '9') return json(res, 404, { error: 'no container at 9,64,9' });
        return json(res, 200, live.containerDouble);
      }
      case '/v1/sup/viewer/bodies/ghost/inventory':
        return json(res, 404, { error: 'no such body', code: 'not_found' });
      case '/v1/sup/admin/memory':
        return json(res, 400, { ok: false, message: 'refused: protected fact', reembedded: false });
      case '/v1/sup/admin/backup':
        return json(res, 500, { error: 'disk full at /var/lib/mineclaw' });
      case '/v1/sup/story/colonies/story-1/builds':
        return json(res, 200, { ids: ['b1'], build_id: 'b1' });
      case '/v1/sup/world/chunks': {
        if (req.headers['if-none-match'] === '"c1"') {
          res.writeHead(304, { ETag: '"c1"' }).end();
          return;
        }
        res.writeHead(200, {
          'Content-Type': 'application/octet-stream',
          'Content-Length': '8',
          ETag: '"c1"',
          'X-Seen': '7',
          'Set-Cookie': 'evil=1',
        });
        res.write(Buffer.from('MCWC'));
        releaseStream = () => res.end(Buffer.from([1, 0, 0, 0]));
        return;
      }
      case '/v1/shards/export':
        res
          .writeHead(200, {
            'Content-Type': 'application/zip',
            'Content-Disposition': 'attachment; filename="x-20261102-1000.mcshard"',
          })
          .end(Buffer.from('PK\x03\x04zip'));
        return;
      case '/v1/shards/import/preview':
        return json(res, 200, JSON.parse(readFileSync(join(fixtures, 'tools.json'), 'utf8')).shardImportPreview);
      default:
        return json(res, 404, { error: 'not found' });
    }
  });
  t = await startApp({ bridge: { url: bridge.url }, web: { chatRate: { burst: 2, perMinute: 1 } } });
  fox = await login(t.url, 'fox');
  robin = await login(t.url, 'robin');
  cora = await login(t.url, 'cora');
  foxCookie = (await fetchLogin(t.url, 'fox')).cookie;
});
afterAll(async () => {
  await t.close();
  await bridge.close();
});

describe('JSON forwarding', () => {
  it('forwards exact freecam plan edits and their build identity without changing metadata', async () => {
    const cells = [[1, 64, 2, 53, 3], [1, 65, 2, 0, 0]];
    const response = await fox.agent.post('/api/story/colonies/story-1/builds')
      .set('Origin', ORIGIN).set('X-CSRF-Token', fox.csrf)
      .send({ op: 'edit', buildId: 'b1', cells }).expect(200);
    expect(response.body).toMatchObject({ ids: ['b1'], buildId: 'b1' });
    expect(JSON.parse(last('/v1/sup/story/colonies/story-1/builds').body.toString()))
      .toEqual({ op: 'edit', build_id: 'b1', cells });
    await robin.agent.post('/api/story/colonies/story-1/builds')
      .set('Origin', ORIGIN).set('X-CSRF-Token', robin.csrf)
      .send({ op: 'edit', cells }).expect(403);
  });

  it('camelCases replies but keeps opaque maps (params, args, blackboard) intact', async () => {
    const r = await fox.agent.get('/api/bodies/b1/script').expect(200);
    expect(r.body.snapshot.params).toEqual({ n: 80, store_at: 'home' });
    expect(r.body.snapshot.machine.blackboard).toEqual({ last_chest: [10, 64, 3], mined_total: 640 });
    expect(r.body.snapshot.checkpointsPassed).toBe(2);
    expect(r.body.snapshot.checkpoints_passed).toBeUndefined();
    expect(r.headers['cache-control']).toBe('no-store');
  });

  it('keeps i64 world hashes exact as strings', async () => {
    const r = await fox.agent.get('/api/world/index?x=0&z=0').expect(200);
    expect(r.body.columns[0].hash).toBe('-7046029254386353131');
  });

  it('forwards the parsed query in snake_case with schema defaults and the identity of the session', async () => {
    await robin.agent.get('/api/memory-activity/heat?windowS=600&brain=main').expect(200);
    const req = last('/v1/memory-activity/heat');
    const q = new URL(req.target, 'http://x').searchParams;
    expect(q.get('window_s')).toBe('600');
    expect(q.get('brain')).toBe('main');
    expect(req.identity).toEqual({ user: 'robin', player: 'kiwi', roles: 'chat,viewer' });
    expect(req.headers['x-mcx-request-id']).toMatch(/^[0-9a-f-]{36}$/);
  });

  it('validates responses in test mode: a contract mismatch is a 502', async () => {
    const r = await fox.agent.get('/api/bots/maid_chan/now');
    expect(r.status).toBe(502);
    expect(r.body.code).toBe('upstream_error');
  });

  it('passes a MemoryEditResult refusal through as 400 with that body', async () => {
    const r = await fox.agent
      .post('/api/admin/memory')
      .set('Origin', ORIGIN)
      .set('X-CSRF-Token', fox.csrf)
      .send({ op: 'update', kind: 'fact', id: '12', fields: { text: 'x' } });
    expect(r.status).toBe(400);
    expect(r.body).toEqual({ ok: false, message: 'refused: protected fact', reembedded: false });
    const sent = JSON.parse(last('/v1/sup/admin/memory').body.toString()) as Record<string, unknown>;
    expect(sent).toMatchObject({ op: 'update', kind: 'fact', id: '12', fields: { text: 'x' } });
    const audit = t.api.services.audit.list({ offset: 0, limit: 5, kind: 'memory_edit' });
    expect(audit.items[0]).toMatchObject({ ok: false, status: 400, username: 'fox' });
  });

  it('a body inventory: viewer role, GET, camelCased, armour and off hand intact; unknown bodies 404', async () => {
    const r = await robin.agent.get('/api/bots/maid_chan_67/inventory').expect(200);
    expect(r.body.heldSlot).toBe(0);
    expect(r.body.held_slot).toBeUndefined();
    expect(r.body.version).toBe('1.12.2');
    expect(r.body.slots[0]).toMatchObject({ slot: 0, id: 276, count: 1, enchanted: true, durability: { max: 1561, left: 1351 } });
    expect(r.body.armor).toHaveLength(4);
    expect(r.body.armor[0]).toMatchObject({ slot: 39, name: 'diamond_helmet' });
    expect(r.body.armor[1]).toBeNull();
    expect(r.body.offhand).toMatchObject({ slot: 40, name: 'shield' });
    expect(r.headers['cache-control']).toBe('no-store');
    const req = last('/v1/sup/viewer/bodies/maid_chan_67/inventory');
    expect(req.method).toBe('GET');
    expect(req.identity).toEqual({ user: 'robin', player: 'kiwi', roles: 'chat,viewer' });
    const off = await fox.agent.get('/api/bots/maid_chan_102/inventory').expect(200);
    expect(off.body).toMatchObject({ online: false, slots: [], offhand: null });
    const missing = await fox.agent.get('/api/bots/ghost/inventory');
    expect(missing.status).toBe(404);
    expect(missing.body.code).toBe('not_found');
    // Read-only: no write verb on the route, and a session is required.
    expect([404, 405]).toContain((await fox.agent.post('/api/bots/maid_chan_67/inventory').set('Origin', ORIGIN).set('X-CSRF-Token', fox.csrf).send({})).status);
    expect((await fetch(`${t.url}/api/bots/maid_chan_67/inventory`)).status).toBe(401);
  });

  it('containers: viewer role, GET, camelCased, query forwarded snake_case; a box lists; bad queries never reach the bridge', async () => {
    const r = await robin.agent.get('/api/world/container?dim=0&x=3&y=74&z=-4').expect(200);
    expect(r.body).toMatchObject({ kind: 'double_chest', size: 54, source: 'memory', seenBy: 'maid_chan_102', exact: true });
    expect(r.body.seen_by).toBeUndefined();
    expect(r.body.slots[1]).toMatchObject({ slot: 13, name: 'stone_axe', durability: { max: 131, left: 100 } });
    expect(r.body.cells).toEqual([
      [2, 74, -4],
      [3, 74, -4],
    ]);
    const req = last('/v1/sup/world/containers');
    expect(req.method).toBe('GET');
    expect(req.target).toBe('/v1/sup/world/containers?dim=0&x=3&y=74&z=-4');
    expect(req.identity).toEqual({ user: 'robin', player: 'kiwi', roles: 'chat,viewer' });
    const unknown = await cora.agent.get('/api/world/container?x=20&y=70&z=-32').expect(200);
    expect(unknown.body).toMatchObject({ source: 'unknown', slots: [] });
    expect(unknown.body.seenAt ?? null).toBeNull();
    const none = await fox.agent.get('/api/world/container?x=9&y=64&z=9');
    expect(none.status).toBe(404);
    expect(none.body.code).toBe('not_found');
    const box = await fox.agent.get('/api/world/containers?x0=-16&z0=-40&x1=32&z1=8&limit=50').expect(200);
    expect(box.body.containers[0]).toMatchObject({ kind: 'double_chest', used: 3, total: 67, seenBy: 'maid_chan_102' });
    expect(last('/v1/sup/world/containers').target).toBe('/v1/sup/world/containers?dim=0&x0=-16&z0=-40&x1=32&z1=8&limit=50');
    const before = bridge.requests.length;
    expect((await fox.agent.get('/api/world/container?x=3&z=-4')).status).toBe(400);
    expect((await fox.agent.get('/api/world/containers?x0=0&z0=0&x1=1&z1=1&limit=900')).status).toBe(400);
    expect(bridge.requests.length).toBe(before);
    expect((await fetch(`${t.url}/api/world/container?x=3&y=74&z=-4`)).status).toBe(401);
    expect([404, 405]).toContain((await fox.agent.post('/api/world/container').set('Origin', ORIGIN).set('X-CSRF-Token', fox.csrf).send({})).status);
  });

  it('never leaks upstream details on 5xx', async () => {
    const r = await fox.agent.post('/api/admin/backup').set('Origin', ORIGIN).set('X-CSRF-Token', fox.csrf).send({});
    expect(r.status).toBe(502);
    expect(JSON.stringify(r.body)).not.toMatch(/var|disk/);
  });
});

describe('chat', () => {
  it('sends as the session player only; a player in the body is refused', async () => {
    const smuggle = await robin.agent
      .post('/api/bots/maid_chan/chat')
      .set('Origin', ORIGIN)
      .set('X-CSRF-Token', robin.csrf)
      .send({ text: 'hi', requestId: 'r-12345678', player: 'poofimafox' });
    expect(smuggle.status).toBe(400);
    const allow = await robin.agent
      .post('/api/bots/maid_chan/chat')
      .set('Origin', ORIGIN)
      .set('X-CSRF-Token', robin.csrf)
      .send({ text: 'hi', requestId: 'r-12345678', allowCommands: true });
    expect(allow.status).toBe(400);

    const ok = await robin.agent
      .post('/api/bots/maid_chan/chat')
      .set('Origin', ORIGIN)
      .set('X-CSRF-Token', robin.csrf)
      .send({ text: '  claude, set it to day ', requestId: 'r-8f14e45fceea' });
    expect(ok.status).toBe(202);
    expect(ok.body).toEqual({ accepted: true, id: 2042, feedSeq: 2042 });
    const req = last('/v1/sup/viewer/chat');
    expect(JSON.parse(req.body.toString())).toEqual({
      bot: 'maid_chan',
      text: 'claude, set it to day',
      request_id: 'r-8f14e45fceea',
    });
    expect(req.identity).toEqual({ user: 'robin', player: 'kiwi', roles: 'chat,viewer' });
  });

  it('carries the commands role for the bridge to arm grants', async () => {
    await cora.agent
      .post('/api/bots/maid_chan/chat')
      .set('Origin', ORIGIN)
      .set('X-CSRF-Token', cora.csrf)
      .send({ text: 'day please', requestId: 'r-cora0001' })
      .expect(202);
    expect(last('/v1/sup/viewer/chat').identity).toEqual({
      user: 'cora',
      player: 'cora_mc',
      roles: 'chat,commands,viewer',
    });
  });

  it('rate limits per user and audits every line', async () => {
    const statuses: number[] = [];
    for (let i = 0; i < 3; i++) {
      statuses.push(
        (
          await fox.agent
            .post('/api/bots/maid_chan/chat')
            .set('Origin', ORIGIN)
            .set('X-CSRF-Token', fox.csrf)
            .send({ text: `line ${i}`, requestId: `r-fox0000${i}` })
        ).status,
      );
    }
    expect(statuses).toEqual([202, 202, 429]);
    const audit = t.api.services.audit.list({ offset: 0, limit: 10, kind: 'chat', user: 'fox' });
    expect(audit.items.map((i) => [i.summary.split(' [')[0], i.ok, i.status])).toEqual([
      ['line 2', false, 429],
      ['line 1', true, 202],
      ['line 0', true, 202],
    ]);
    expect(audit.items[0]?.player).toBe('poofimafox');
    expect(audit.items[0]?.target).toBe('maid_chan');
  });
});

describe('binary streaming', () => {
  it('streams MCWC bytes as they arrive (no buffering) with ETag passthrough and revalidation', async () => {
    const res = await fetch(`${t.url}/api/world/chunks?cols=0,0`, { headers: { cookie: foxCookie } });
    expect(res.status).toBe(200);
    expect(res.headers.get('etag')).toBe('"c1"');
    expect(res.headers.get('x-seen')).toBe('7');
    expect(res.headers.get('set-cookie')).toBeNull();
    expect(res.headers.get('cache-control')).toBe('private, no-cache');
    const reader = res.body!.getReader();
    const first = await reader.read();
    expect(Buffer.from(first.value).toString()).toBe('MCWC'); // arrived while the upstream is still open
    await sleep(20);
    releaseStream!();
    const rest = await reader.read();
    expect([...rest.value!]).toEqual([1, 0, 0, 0]);
    const again = await fetch(`${t.url}/api/world/chunks?cols=0,0`, {
      headers: { cookie: foxCookie, 'If-None-Match': '"c1"' },
    });
    expect(again.status).toBe(304);
    expect(again.headers.get('etag')).toBe('"c1"');
  });

  it('streams the shard export zip with its Content-Disposition and audits it', async () => {
    const r = await robin.agent
      .post('/api/shards/export')
      .set('Origin', ORIGIN)
      .set('X-CSRF-Token', robin.csrf)
      .send(generate(ShardExportRequest) as object)
      .buffer(true)
      .parse((res, cb) => {
        const chunks: Buffer[] = [];
        res.on('data', (c: Buffer) => chunks.push(c));
        res.on('end', () => cb(null, Buffer.concat(chunks)));
      });
    if (r.status !== 200) throw new Error(JSON.stringify(r.body));
    expect(r.headers['content-type']).toBe('application/zip');
    expect(r.headers['content-disposition']).toMatch(/\.mcshard"/);
    expect((r.body as Buffer).subarray(0, 2).toString()).toBe('PK');
    expect(t.api.services.audit.list({ offset: 0, limit: 5, kind: 'shard_export' }).total).toBe(1);
  });
});

describe('multipart shard upload', () => {
  it('validates options, re-encodes them in snake_case and passes the file bytes unchanged', async () => {
    const file = Buffer.from([0x50, 0x4b, 3, 4, 0, 255, 10, 13]);
    const r = await fox.agent
      .post('/api/shards/import/preview')
      .set('Origin', ORIGIN)
      .set('X-CSRF-Token', fox.csrf)
      .field('options', JSON.stringify({ includeRules: true, maxUsd: 1 }))
      .attach('shard', file, 'my pack.mcshard');
    expect(r.status).toBe(200);
    const req = last('/v1/shards/import/preview');
    const parts = await parseMultipart(req);
    expect(JSON.parse(parts.options!)).toEqual({
      mode: 'merge',
      conflict: 'keep-newer',
      include_rules: true,
      include_world: false,
      max_usd: 1,
    });
    expect(parts.file).toEqual(file);
    expect(parts.filename).toBe('my pack.mcshard');
  });

  it('rejects bad options and a missing file with 400', async () => {
    const bad = await fox.agent
      .post('/api/shards/import/preview')
      .set('Origin', ORIGIN)
      .set('X-CSRF-Token', fox.csrf)
      .field('options', '{"evil":1}')
      .attach('shard', Buffer.from('x'), 'a.mcshard');
    expect(bad.status).toBe(400);
    const none = await fox.agent
      .post('/api/shards/import/preview')
      .set('Origin', ORIGIN)
      .set('X-CSRF-Token', fox.csrf)
      .field('options', '{}');
    expect(none.status).toBe(400);
    const json2 = await fox.agent
      .post('/api/shards/import/preview')
      .set('Origin', ORIGIN)
      .set('X-CSRF-Token', fox.csrf)
      .send({});
    expect(json2.status).toBe(400);
  });
});

function parseMultipart(req: RecordedRequest): Promise<{ options?: string; file?: Buffer; filename?: string }> {
  return new Promise((resolve, reject) => {
    const out: { options?: string; file?: Buffer; filename?: string } = {};
    const bb = busboy({ headers: req.headers });
    bb.on('field', (n, v) => {
      if (n === 'options') out.options = v;
    });
    bb.on('file', (_n, s, info) => {
      const c: Buffer[] = [];
      out.filename = info.filename;
      s.on('data', (d: Buffer) => c.push(d));
      s.on('end', () => (out.file = Buffer.concat(c)));
    });
    bb.on('close', () => resolve(out));
    bb.on('error', reject);
    bb.end(req.body);
  });
}
