/** The mock bridge's special routes, straight at the bridge (signed): roles, refusals and reply kinds stay as the e2e expect. */
import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import { signedHeaders, type BridgeIdentity } from '../src/bridge/signing.js';
import { startMockBridge, type MockBridge } from '../src/mock/mock-bridge.js';

const KEY = 'k'.repeat(32);
const FOX: BridgeIdentity = { user: 'fox', player: 'poofimafox', roles: 'admin,commands,design,story,viewer' };
const VERA: BridgeIdentity = { user: 'vera', player: '', roles: 'viewer' };

let bridge: MockBridge;
beforeAll(async () => {
  bridge = await startMockBridge(KEY, { streamIntervalMs: 1_000_000 });
});
afterAll(async () => {
  await bridge.close();
});

async function call(
  method: string,
  target: string,
  identity: BridgeIdentity,
  body?: string,
  extra: Record<string, string> = {},
): Promise<{ status: number; headers: Headers; json: () => unknown; bytes: Buffer }> {
  const buf = body === undefined ? undefined : Buffer.from(body);
  const h = signedHeaders(KEY, { method, pathAndQuery: target, body: buf, identity }, Date.now());
  const res = await fetch(bridge.url + target, {
    method,
    headers: { ...h, ...(buf ? { 'content-type': 'application/json' } : {}), ...extra },
    body: buf,
  });
  const bytes = Buffer.from(await res.arrayBuffer());
  return { status: res.status, headers: res.headers, json: () => JSON.parse(bytes.toString('utf8')) as unknown, bytes };
}

describe('mock bridge routes', () => {
  it('refuses unsigned requests and unknown paths; health answers', async () => {
    expect((await fetch(`${bridge.url}/v1/health`)).status).toBe(401);
    expect((await call('GET', '/v1/health', VERA)).json()).toEqual({ ok: true, version: 'mock', supervisor: 'up' });
    expect((await call('GET', '/v1/nope', VERA)).status).toBe(404);
  });

  it('gates admin paths, Story writes and Design Studio writes by role', async () => {
    const vera = await call('GET', '/v1/sup/admin/audit', VERA);
    expect([vera.status, vera.json()]).toEqual([403, { error: 'admin role required', code: 'forbidden' }]);
    const create = await call('POST', '/v1/sup/designer/sessions', VERA, '{}');
    expect([create.status, create.json()]).toEqual([403, { error: 'design role required', code: 'forbidden' }]);
    expect((await call('GET', '/v1/sup/designer/models', VERA)).status).toBe(200);
    const made = await call('POST', '/v1/sup/designer/sessions', FOX, '{}');
    expect(made.status).toBe(200);
    expect((made.json() as { id: number }).id).toBeGreaterThan(0);
  });

  it('memory cloud arrangements and distances', async () => {
    expect((await call('GET', '/v1/memory-activity/cloud?arrangement=tsne', VERA)).json()).toEqual({
      error: 'arrangement must be one of mds|umap|pca|tree',
      code: 'bad_request',
    });
    expect((await call('GET', '/v1/memory-activity/cloud/distance?a=x&b=1', VERA)).status).toBe(400);
    expect((await call('GET', '/v1/memory-activity/cloud/distance?a=1&b=99999999', VERA)).status).toBe(404);
    expect((await call('GET', '/v1/memory-activity/cloud/distance?a=1&b=2', VERA)).status).toBe(200);
  });

  it('chat: a player only, no player/allow_commands in the body, 202 with the feed seq', async () => {
    expect((await call('POST', '/v1/sup/viewer/chat', VERA, '{"text":"hi"}')).status).toBe(403);
    expect((await call('POST', '/v1/sup/viewer/chat', FOX, '{"text":"hi","player":"x"}')).status).toBe(400);
    const ok = await call('POST', '/v1/sup/viewer/chat', FOX, '{"text":"hi","bot":"maid_chan"}');
    const body = ok.json() as { accepted: boolean; id: number; feed_seq: number };
    expect([ok.status, body.accepted, body.id === body.feed_seq]).toEqual([202, true, true]);
  });

  it('world chunks revalidate by ETag; block entities put a sign and a bed in the first column', async () => {
    const first = await call('GET', '/v1/sup/world/chunks?dim=0&cols=0,0', VERA);
    expect(first.headers.get('content-type')).toBe('application/octet-stream');
    const etag = first.headers.get('etag') ?? '';
    expect(
      (await call('GET', '/v1/sup/world/chunks?dim=0&cols=0,0', VERA, undefined, { 'if-none-match': etag })).status,
    ).toBe(304);
    const be = (await call('GET', '/v1/sup/world/blockentities?dim=0&cols=2,3;4,5', VERA)).json() as {
      columns: Array<{ cx: number; cz: number; entities: Array<{ kind: string; x: number }> }>;
    };
    expect(be.columns.map((c) => [c.cx, c.cz, c.entities.map((e) => `${e.kind}@${e.x}`).join(',')])).toEqual([
      [2, 3, 'sign@36,bed@38'],
    ]);
  });

  it('containers: a box over 512 wide is refused', async () => {
    const wide = await call('GET', '/v1/sup/world/containers?dim=0&x0=0&z0=0&x1=1000&z1=10', VERA);
    expect(wide.json()).toEqual({ error: 'containers box is at most 512 blocks wide', code: 'bad_request' });
  });
});
