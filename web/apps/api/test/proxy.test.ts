import { createServer, request as httpRequest, type IncomingHttpHeaders, type Server } from 'node:http';
import type { AddressInfo } from 'node:net';
import { connect } from 'node:net';
import request from 'supertest';
import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import { startApp, type TestApp } from './helpers.js';

let next: Server;
let nextUrl: string;
let seen: IncomingHttpHeaders[] = [];
const nextSockets: Array<{ destroy(): void }> = [];
let t: TestApp;

beforeAll(async () => {
  next = createServer((req, res) => {
    seen.push(req.headers);
    res.writeHead(200, {
      'Content-Type': 'text/html; charset=utf-8',
      'Content-Security-Policy': "script-src 'unsafe-eval'",
      'X-Frame-Options': 'SAMEORIGIN',
      'X-Powered-By': 'Next.js',
    });
    res.end(`<!doctype html><script nonce="${String(req.headers['x-nonce'])}">1</script>${req.url}`);
  });
  next.on('upgrade', (req, socket) => {
    seen.push(req.headers);
    nextSockets.push(socket);
    socket.write('HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n\r\n');
    socket.on('data', (d: Buffer) => socket.write(Buffer.concat([Buffer.from('echo:'), d])));
  });
  await new Promise<void>((r) => next.listen(0, '127.0.0.1', r));
  nextUrl = `http://127.0.0.1:${(next.address() as AddressInfo).port}`;
  t = await startApp({ web: { proxyNext: true, nextUrl } });
});
afterAll(async () => {
  await t.close();
  for (const s of nextSockets) s.destroy();
  next.closeAllConnections();
  await new Promise<void>((r) => next.close(() => r()));
});

describe('prod reverse proxy to Next', () => {
  it('proxies pages with a per-request CSP nonce shared with Next, and Nest security headers win', async () => {
    seen = [];
    const r = await request(t.url)
      .get('/live/maid_chan?tab=chat')
      .set('x-middleware-subrequest', 'middleware:middleware')
      .set('x-nonce', 'forged');
    expect(r.status).toBe(200);
    const csp = String(r.headers['content-security-policy']);
    const nonce = /'nonce-([^']+)'/.exec(csp)?.[1];
    expect(nonce).toBeTruthy();
    expect(csp).toContain("'strict-dynamic'");
    expect(csp).toContain("frame-ancestors 'none'");
    expect(csp).not.toContain('unsafe-eval');
    expect(r.text).toContain(`nonce="${nonce}"`);
    expect(r.text).toContain('/live/maid_chan?tab=chat');
    expect(r.headers['x-frame-options']).toBe('DENY');
    expect(r.headers['referrer-policy']).toBe('same-origin');
    expect(r.headers['x-powered-by']).toBeUndefined();
    expect(r.headers['strict-transport-security']).toBeUndefined(); // plain HTTP
    const h = seen[0]!;
    expect(h['x-nonce']).toBe(nonce);
    expect(h['content-security-policy']).toBe(csp);
    expect(h['x-middleware-subrequest']).toBeUndefined();
    expect(h['x-forwarded-for']).toBe('127.0.0.1');
    expect(h['x-forwarded-proto']).toBe('http');
  });

  it('nonces differ per request', async () => {
    const a = String((await request(t.url).get('/')).headers['content-security-policy']);
    const b = String((await request(t.url).get('/')).headers['content-security-policy']);
    expect(a).not.toBe(b);
  });

  it('never proxies /api', async () => {
    seen = [];
    const r = await request(t.url).get('/api/does-not-exist');
    expect(r.status).toBe(404);
    expect(r.body.code).toBe('not_found');
    expect(r.headers['content-security-policy']).toBe(
      "default-src 'none'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'",
    );
    expect(seen).toHaveLength(0);
  });

  it('proxies WebSocket upgrades outside /api and refuses them under /api', async () => {
    const port = Number(new URL(t.url).port);
    const talk = (path: string) =>
      new Promise<string>((resolve) => {
        const s = connect(port, '127.0.0.1', () => {
          s.write(
            `GET ${path} HTTP/1.1\r\nHost: x\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\nSec-WebSocket-Version: 13\r\n\r\n`,
          );
        });
        let buf = '';
        s.on('data', (d: Buffer) => {
          buf += d.toString();
          if (buf.includes('\r\n\r\n') && !buf.includes('echo:')) s.write('ping');
          if (buf.includes('echo:ping')) {
            s.destroy();
            resolve(buf);
          }
        });
        s.on('close', () => resolve(buf));
        s.on('error', () => resolve(buf));
      });
    const ok = await talk('/_next/webpack-hmr');
    expect(ok).toMatch(/^HTTP\/1\.1 101/);
    expect(ok).toContain('echo:ping');
    const refused = await talk('/api/live/stream');
    expect(refused).toContain('HTTP/1.1 404');
    expect(refused).not.toContain('101 Switching Protocols');
  });

  it('answers a small 502 page when Next is down', async () => {
    const down = await startApp({ web: { proxyNext: true, nextUrl: 'http://127.0.0.1:9' } });
    try {
      const r = await new Promise<{ status: number; body: string }>((resolve) => {
        httpRequest(`${down.url}/`, (res) => {
          let body = '';
          res.on('data', (d: Buffer) => (body += d.toString()));
          res.on('end', () => resolve({ status: res.statusCode ?? 0, body }));
        }).end();
      });
      expect(r.status).toBe(502);
      expect(r.body).toContain('unavailable');
      expect(r.body).not.toContain('127.0.0.1');
    } finally {
      await down.close();
    }
  });
});
