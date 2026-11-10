import { createHash, createHmac } from 'node:crypto';
import { describe, expect, it } from 'vitest';
import { bridgeSigningString, canonicalRoles, EMPTY_BODY_SHA256, NestConfig } from '../src/index.js';

const sha256 = (s: string) => createHash('sha256').update(s, 'utf8').digest('hex');
const hmac = (key: string, s: string) => createHmac('sha256', Buffer.from(key, 'utf8')).update(s, 'utf8').digest('hex');

/** The test vectors published in docs/web/api.md §3 (the C# bridge tests must reproduce them). */
const VECTOR_KEY = 'test-key-not-a-secret-0123456789abcdef';

describe('bridge signing', () => {
  it('empty body hash', () => {
    expect(sha256('')).toBe(EMPTY_BODY_SHA256);
  });

  it('roles are canonical', () => {
    expect(canonicalRoles(['viewer', 'admin', 'chat', 'admin'])).toBe('admin,chat,viewer');
  });

  it('GET vector', () => {
    const s = bridgeSigningString({
      method: 'get',
      pathAndQuery: '/v1/memory/facts?q=fox&offset=0&limit=50',
      timestamp: '1762077600',
      nonce: '00112233445566778899aabbccddeeff',
      bodySha256: EMPTY_BODY_SHA256,
      user: 'fox',
      player: 'poofimafox',
      roles: canonicalRoles(['viewer', 'chat', 'commands', 'admin']),
    });
    expect(s).toBe(
      'GET\n/v1/memory/facts?q=fox&offset=0&limit=50\n1762077600\n00112233445566778899aabbccddeeff\n' +
        `${EMPTY_BODY_SHA256}\nfox\npoofimafox\nadmin,chat,commands,viewer`,
    );
    expect(hmac(VECTOR_KEY, s)).toBe('675be9cf1726a3d05bd596235a6f55f84d52c0982a7f2c5450a6a221ebff20b5');
  });

  it('POST vector', () => {
    const body = '{"bot":"maid_chan","text":"claude, set it to day","request_id":"r-8f14e45fceea"}';
    const s = bridgeSigningString({
      method: 'POST',
      pathAndQuery: '/v1/sup/viewer/chat',
      timestamp: '1762077600',
      nonce: 'ffeeddccbbaa99887766554433221100',
      bodySha256: sha256(body),
      user: 'robin',
      player: 'kiwi',
      roles: canonicalRoles(['chat', 'viewer']),
    });
    expect(sha256(body)).toBe('1799cd6a496b955c3b09614f6ea849740d8a960cf4953323d89de775321e6da9');
    expect(hmac(VECTOR_KEY, s)).toBe('430af190755eb08dec07cd74e8a00c00a62afb0c27ca9c2be09c770e5573c611');
  });

  it('rejects newlines in identity headers', () => {
    expect(() =>
      bridgeSigningString({
        method: 'GET',
        pathAndQuery: '/v1/overview',
        timestamp: '1',
        nonce: 'n',
        bodySha256: EMPTY_BODY_SHA256,
        user: 'fox\nadmin',
        player: '',
        roles: '',
      }),
    ).toThrow();
  });

  it('Nest config defaults to loopback plain HTTP', () => {
    const c = NestConfig.parse({ web: { allowedOrigins: ['http://10.1.2.73:8080'] }, bridge: { key: 'x'.repeat(32) } });
    expect(c.bridge.url).toBe('http://127.0.0.1:8766');
    expect(c.web.trustedProxies).toEqual([]);
  });
});
