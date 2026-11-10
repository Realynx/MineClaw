import { createServer } from 'node:http';
import type { AddressInfo } from 'node:net';
import { describe, expect, it } from 'vitest';
import { BridgeClient, mapUpstreamError } from '../src/bridge/bridge-client.js';
import { sha256Hex, sign, signedHeaders, SignatureVerifier } from '../src/bridge/signing.js';
import { ApiHttpError } from '../src/common/errors.js';
import { createLogger } from '../src/common/logger.js';

const KEY = 'test-key-not-a-secret-0123456789abcdef';
const log = createLogger('silent');

describe('HMAC signing (docs/web/api.md §3 test vectors)', () => {
  it('GET vector', () => {
    const sig = sign(KEY, {
      method: 'GET',
      pathAndQuery: '/v1/memory/facts?q=fox&offset=0&limit=50',
      timestamp: '1762077600',
      nonce: '00112233445566778899aabbccddeeff',
      bodySha256: sha256Hex(undefined),
      user: 'fox',
      player: 'poofimafox',
      roles: 'admin,chat,commands,viewer',
    });
    expect(sig).toBe('675be9cf1726a3d05bd596235a6f55f84d52c0982a7f2c5450a6a221ebff20b5');
  });

  it('POST vector (body hash included)', () => {
    const body = Buffer.from('{"bot":"maid_chan","text":"claude, set it to day","request_id":"r-8f14e45fceea"}');
    expect(sha256Hex(body)).toBe('1799cd6a496b955c3b09614f6ea849740d8a960cf4953323d89de775321e6da9');
    const sig = sign(KEY, {
      method: 'post',
      pathAndQuery: '/v1/sup/viewer/chat',
      timestamp: '1762077600',
      nonce: 'ffeeddccbbaa99887766554433221100',
      bodySha256: sha256Hex(body),
      user: 'robin',
      player: 'kiwi',
      roles: 'chat,viewer',
    });
    expect(sig).toBe('430af190755eb08dec07cd74e8a00c00a62afb0c27ca9c2be09c770e5573c611');
  });

  it('signedHeaders reproduces the vector headers', () => {
    const h = signedHeaders(
      KEY,
      {
        method: 'GET',
        pathAndQuery: '/v1/memory/facts?q=fox&offset=0&limit=50',
        identity: { user: 'fox', player: 'poofimafox', roles: 'admin,chat,commands,viewer' },
      },
      1762077600_000,
      '00112233445566778899aabbccddeeff',
    );
    expect(h).toMatchObject({
      'X-MCX-Ts': '1762077600',
      'X-MCX-Nonce': '00112233445566778899aabbccddeeff',
      'X-MCX-Sig': '675be9cf1726a3d05bd596235a6f55f84d52c0982a7f2c5450a6a221ebff20b5',
      'X-MCX-User': 'fox',
      'X-MCX-Player': 'poofimafox',
      'X-MCX-Roles': 'admin,chat,commands,viewer',
    });
  });

  it('the verifier rejects tampering, skew and replays', () => {
    const now = 1762077600_000;
    const v = new SignatureVerifier(KEY, () => now);
    const identity = { user: 'robin', player: 'kiwi', roles: 'chat,viewer' };
    const body = Buffer.from('{"a":1}');
    const mk = (n: string) => signedHeaders(KEY, { method: 'POST', pathAndQuery: '/v1/x?a=1', body, identity }, now, n);
    const lower = (h: Record<string, string>) =>
      Object.fromEntries(Object.entries(h).map(([k, x]) => [k.toLowerCase(), x]));
    const h1 = lower(mk('0'.repeat(32)));
    expect(v.verify({ method: 'POST', rawTarget: '/v1/x?a=1', headers: h1, body }).ok).toBe(true);
    expect(v.verify({ method: 'POST', rawTarget: '/v1/x?a=1', headers: h1, body }).ok).toBe(false); // replay
    const h2 = lower(mk('1'.repeat(32)));
    expect(v.verify({ method: 'POST', rawTarget: '/v1/x?a=2', headers: h2, body }).ok).toBe(false);
    expect(
      v.verify({ method: 'POST', rawTarget: '/v1/x?a=1', headers: { ...h2, 'x-mcx-player': 'poofimafox' }, body }).ok,
    ).toBe(false);
    expect(v.verify({ method: 'POST', rawTarget: '/v1/x?a=1', headers: h2, body: Buffer.from('{"a":2}') }).ok).toBe(
      false,
    );
    const late = new SignatureVerifier(KEY, () => now + 31_000);
    expect(late.verify({ method: 'POST', rawTarget: '/v1/x?a=1', headers: lower(mk('2'.repeat(32))), body })).toEqual({
      ok: false,
      reason: 'clock skew',
    });
  });
});

describe('bridge client', () => {
  it('signs the exact wire target and maps a dead bridge to 503', async () => {
    const seen: string[] = [];
    const verifier = new SignatureVerifier(KEY);
    const server = createServer((req, res) => {
      const ok = verifier.verify({ method: req.method!, rawTarget: req.url!, headers: req.headers }).ok;
      seen.push(`${req.url} ${ok}`);
      res.writeHead(200, { 'Content-Type': 'application/json' }).end('{"ok":true}');
    });
    await new Promise<void>((r) => server.listen(0, '127.0.0.1', r));
    const port = (server.address() as AddressInfo).port;
    const c = new BridgeClient({ url: `http://127.0.0.1:${port}`, key: KEY, timeoutMs: 2000 }, log);
    const r = await c.json({
      method: 'GET',
      target: '/v1/search?q=a%20b&mode=hybrid',
      identity: { user: '', player: '', roles: '' },
    });
    expect(r.status).toBe(200);
    expect(seen).toEqual(['/v1/search?q=a%20b&mode=hybrid true']);
    await new Promise<void>((r2) => server.close(() => r2()));
    const err = await c
      .json({ method: 'GET', target: '/v1/health', identity: { user: '', player: '', roles: '' } })
      .catch((e: unknown) => e);
    expect(err).toBeInstanceOf(ApiHttpError);
    expect((err as ApiHttpError).status).toBe(503);
    expect((err as ApiHttpError).body.code).toBe('upstream_unavailable');
  });

  it.each([
    [400, '{"error":"bad kind"}', 400, 'bad_request', 'bad kind'],
    [401, '{"error":"bad signature"}', 502, 'upstream_error', 'upstream error'],
    [403, '{"error":"admin role required"}', 403, 'forbidden', 'admin role required'],
    [404, '', 404, 'not_found', 'not found'],
    [413, '', 413, 'payload_too_large', 'payload too large'],
    [429, '{"error":"slow","retry_after_ms":2500}', 429, 'rate_limited', 'slow'],
    [500, '{"error":"System.NullReferenceException at C:\\\\src\\\\x.cs"}', 502, 'upstream_error', 'upstream error'],
    [501, '', 501, 'upstream_error', 'not implemented yet'],
    [503, '{"error":"render gate","code":"busy"}', 503, 'busy', 'busy; retry shortly'],
    [503, '', 503, 'upstream_unavailable', 'bridge or bot unavailable'],
  ])('maps bridge %i to %i %s', (status, body, outStatus, code, message) => {
    const e = mapUpstreamError(status, { 'retry-after': '3' }, body, log);
    expect(e.status).toBe(outStatus);
    expect(e.body.code).toBe(code);
    expect(e.body.error).toBe(message);
    if (status === 429) expect(e.body.retryAfterMs).toBe(2500);
  });
});
