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
      case '/v1/sup/admin/memory':
        return json(res, 400, { ok: false, message: 'refused: protected fact', reembedded: false });
      case '/v1/sup/admin/backup':
        return json(res, 500, { error: 'disk full at /var/lib/mineclaw' });
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
