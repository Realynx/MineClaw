import type { IncomingMessage } from 'node:http';
import { exportJWK, generateKeyPair, SignJWT, createLocalJWKSet } from 'jose';
import request from 'supertest';
import { afterEach, describe, expect, it } from 'vitest';
import { normalizeIp, TrustedProxies } from '../src/security/client-ip.js';
import { csrfSession, ORIGIN, startApp, type TestApp } from './helpers.js';

function req(peer: string, headers: Record<string, string> = {}, encrypted = false): IncomingMessage {
  return { socket: { remoteAddress: peer, encrypted }, headers } as unknown as IncomingMessage;
}

describe('TrustedProxies', () => {
  const tp = new TrustedProxies({ trustProxy: true, trustedProxies: ['127.0.0.1', '10.1.2.0/24', '::1'] });

  it('ignores forwarded headers from untrusted peers', () => {
    expect(tp.resolve(req('192.168.1.50', { 'cf-connecting-ip': '1.2.3.4', 'x-forwarded-proto': 'https' }))).toEqual({
      ip: '192.168.1.50',
      https: false,
    });
  });

  it('honours CF-Connecting-IP and X-Forwarded-Proto from a trusted peer (incl. v4-mapped and CIDR)', () => {
    expect(
      tp.resolve(req('::ffff:127.0.0.1', { 'cf-connecting-ip': '203.0.113.9', 'x-forwarded-proto': 'https' })),
    ).toEqual({ ip: '203.0.113.9', https: true });
    expect(tp.resolve(req('10.1.2.7', { 'cf-connecting-ip': '2001:db8::1' }))).toEqual({
      ip: '2001:db8::1',
      https: false,
    });
  });

  it('takes the rightmost untrusted X-Forwarded-For hop', () => {
    expect(tp.resolve(req('127.0.0.1', { 'x-forwarded-for': '6.6.6.6, 203.0.113.9, 10.1.2.5' })).ip).toBe(
      '203.0.113.9',
    );
  });

  it('is off unless TrustProxy is set', () => {
    const off = new TrustedProxies({ trustProxy: false, trustedProxies: ['127.0.0.1'] });
    expect(off.resolve(req('127.0.0.1', { 'cf-connecting-ip': '203.0.113.9' })).ip).toBe('127.0.0.1');
    expect(normalizeIp('::ffff:10.1.2.73')).toBe('10.1.2.73');
  });

  it('rejects malformed entries at startup', () => {
    expect(() => new TrustedProxies({ trustProxy: true, trustedProxies: ['cloudflared'] })).toThrow(/TrustedProxies/);
  });
});

let t: TestApp | undefined;
afterEach(async () => {
  await t?.close();
  t = undefined;
});

async function failedLoginIp(app: TestApp, headers: Record<string, string>): Promise<string> {
  const s = await csrfSession(app.url);
  let r = s.agent.post('/api/auth/login').set('Origin', ORIGIN).set('X-CSRF-Token', s.csrf);
  for (const [k, v] of Object.entries(headers)) r = r.set(k, v);
  await r.send({ username: 'fox', password: 'wrong-password!' });
  return app.api.services.audit.list({ offset: 0, limit: 1 }).items[0]!.ip;
}

describe('client IP in the app', () => {
  it('uses CF-Connecting-IP for audit and lockout keys only from a trusted proxy; HSTS only over HTTPS', async () => {
    t = await startApp({ web: { trustProxy: true, trustedProxies: ['127.0.0.1', '::1'] } });
    expect(await failedLoginIp(t, { 'CF-Connecting-IP': '203.0.113.9' })).toBe('203.0.113.9');
    const https = await request(t.url).get('/api/health').set('X-Forwarded-Proto', 'https');
    expect(https.headers['strict-transport-security']).toBe('max-age=15552000');
    const plain = await request(t.url).get('/api/health');
    expect(plain.headers['strict-transport-security']).toBeUndefined();
  });

  it('ignores the headers when the peer is not trusted', async () => {
    t = await startApp({ web: { trustProxy: true, trustedProxies: ['10.9.9.9'] } });
    expect(await failedLoginIp(t, { 'CF-Connecting-IP': '203.0.113.9' })).toBe('127.0.0.1');
    const r = await request(t.url).get('/api/health').set('X-Forwarded-Proto', 'https');
    expect(r.headers['strict-transport-security']).toBeUndefined();
  });
});

describe('Cloudflare Access gate', () => {
  it('requires a valid Access JWT (aud + issuer) everywhere except /api/health', async () => {
    const { publicKey, privateKey } = await generateKeyPair('RS256');
    const jwk = { ...(await exportJWK(publicKey)), kid: 'k1', alg: 'RS256' };
    const jwks = createLocalJWKSet({ keys: [jwk] });
    t = await startApp({ web: { cloudflareAccess: { teamDomain: 'example-team', aud: 'aud-123' } } }, { cfAccessJwks: jwks });
    const token = (aud: string, iss = 'https://example-team.cloudflareaccess.com') =>
      new SignJWT({ email: 'fox@example.com' })
        .setProtectedHeader({ alg: 'RS256', kid: 'k1' })
        .setAudience(aud)
        .setIssuer(iss)
        .setIssuedAt()
        .setExpirationTime('5m')
        .sign(privateKey);

    expect((await request(t.url).get('/api/health')).status).toBe(200);
    expect((await request(t.url).get('/api/auth/csrf')).status).toBe(403);
    expect(
      (
        await request(t.url)
          .get('/api/auth/csrf')
          .set('Cf-Access-Jwt-Assertion', await token('other'))
      ).status,
    ).toBe(403);
    expect(
      (
        await request(t.url)
          .get('/api/auth/csrf')
          .set('Cf-Access-Jwt-Assertion', await token('aud-123', 'https://evil.cloudflareaccess.com'))
      ).status,
    ).toBe(403);
    const good = await token('aud-123');
    expect((await request(t.url).get('/api/auth/csrf').set('Cf-Access-Jwt-Assertion', good)).status).toBe(200);
    expect((await request(t.url).get('/api/auth/csrf').set('Cookie', `CF_Authorization=${good}`)).status).toBe(200);
  });
});
