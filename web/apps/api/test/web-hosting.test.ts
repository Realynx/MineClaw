import { generateKeyPairSync } from 'node:crypto';
import { mkdtempSync, readFileSync, rmSync, writeFileSync, existsSync } from 'node:fs';
import { createServer } from 'node:net';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import request from 'supertest';
import { afterEach, describe, expect, it } from 'vitest';
import { WebTlsSettings } from '@mineclaw/contracts';
import { loadConfig } from '../src/config/config.js';
import { hostingPath, tlsMaterial, WebHostingSettings } from '../src/config/web-hosting.js';
import { KEY, login, ORIGIN, PASSWORD, startApp } from './helpers.js';

const certFile = resolve('test/fixtures/tls/localhost-cert.pem');
const keyFile = resolve('test/fixtures/tls/localhost-key.pem');
const tls = WebTlsSettings.parse({ enabled: true, hostname: 'localhost', certFile, keyFile });
const dirs: string[] = [];
const temp = () => {
  const dir = mkdtempSync(join(tmpdir(), 'mcx-tls-'));
  dirs.push(dir);
  return dir;
};
afterEach(() => {
  for (const dir of dirs.splice(0)) rmSync(dir, { recursive: true, force: true });
});

describe('HTTPS certificate validation', () => {
  it('accepts a valid matching certificate for its DNS name and IP SAN', () => {
    expect(tlsMaterial(tls, 8080)?.minVersion).toBe('TLSv1.2');
    expect(tlsMaterial({ ...tls, hostname: '127.0.0.1' }, 8080)).toBeDefined();
    expect(tlsMaterial({ ...tls, enabled: false, certFile: '', keyFile: '' }, 8080)).toBeUndefined();
  });
  it('rejects mismatched hostname, expiry, relative paths and colliding ports', () => {
    for (const changed of [
      { hostname: 'other.example.com' },
      { certFile: 'cert.pem' },
      { keyFile: 'key.pem' },
      { port: 8080 },
    ])
      expect(() => tlsMaterial({ ...tls, ...changed }, 8080)).toThrow('Invalid HTTPS configuration');
    expect(() => tlsMaterial(tls, 8080, Date.UTC(2100, 0, 1))).toThrow('Invalid HTTPS configuration');
  });
  it('rejects wrong and malformed keys without leaking file contents', () => {
    const path = join(temp(), 'key.pem');
    writeFileSync(
      path,
      generateKeyPairSync('rsa', { modulusLength: 2048 }).privateKey.export({ type: 'pkcs8', format: 'pem' }),
    );
    expect(() => tlsMaterial({ ...tls, keyFile: path }, 8080)).toThrow('Invalid HTTPS configuration');
    writeFileSync(path, 'SECRET-NOT-A-PRIVATE-KEY');
    try {
      tlsMaterial({ ...tls, keyFile: path }, 8080);
      throw new Error('accepted malformed key');
    } catch (error) {
      expect(JSON.stringify(error)).not.toContain('SECRET-NOT-A-PRIVATE-KEY');
    }
  });
});

it('saves atomically, preserves a backup, loads after restart and honors environment overrides', () => {
  const dir = temp();
  const configFile = join(dir, 'config.json');
  writeFileSync(configFile, JSON.stringify({ web: { allowedOrigins: [ORIGIN], dataDir: dir }, bridge: { key: KEY } }));
  const base = loadConfig({ argv: ['--config', configFile], env: {} }).config;
  const service = new WebHostingSettings(base);
  const saved = service.save(tls);
  expect(saved.running.enabled).toBe(false);
  expect(saved.pendingRestart).toBe(true);
  expect(saved.savedOrigin).toBe('https://localhost:8443');
  expect(readFileSync(hostingPath(dir), 'utf8')).not.toContain('BEGIN PRIVATE KEY');
  expect(loadConfig({ argv: ['--config', configFile], env: {} }).config.web.tls).toEqual(tls);
  expect(() => service.save({ ...tls, hostname: 'wrong.example' })).toThrow();
  expect(service.read().saved).toEqual(tls);
  service.save({ ...tls, enabled: false });
  expect(JSON.parse(readFileSync(hostingPath(dir) + '.bak', 'utf8')).tls).toEqual(tls);
  const overridden = loadConfig({
    argv: ['--config', configFile],
    env: { MINECLAW_WEB_Web__Tls__Enabled: 'true' },
  }).config;
  expect(overridden.web.tls.enabled).toBe(true);
  expect(new WebHostingSettings(overridden).read().editable).toBe(false);
  expect(() => new WebHostingSettings(overridden).save(tls)).toThrow('environment variables');
});

it('requires admin, CSRF, Origin and fresh step-up; audits saves without certificate material', async () => {
  const t = await startApp();
  try {
    expect((await request(t.url).get('/api/admin/hosting')).status).toBe(401);
    const viewer = await login(t.url, 'vera');
    expect((await viewer.agent.get('/api/admin/hosting')).status).toBe(403);
    const admin = await login(t.url, 'fox');
    const save = () =>
      admin.agent
        .post('/api/admin/hosting')
        .set('Origin', ORIGIN)
        .set('X-CSRF-Token', admin.csrf)
        .send({ tls, reason: 'install the app' });
    expect((await save()).body.code).toBe('step_up_required');
    expect(
      (
        await admin.agent
          .post('/api/auth/step-up')
          .set('Origin', ORIGIN)
          .set('X-CSRF-Token', admin.csrf)
          .send({ password: PASSWORD })
      ).status,
    ).toBe(200);
    expect(
      (await admin.agent.post('/api/admin/hosting').set('Origin', ORIGIN).send({ tls, reason: 'test' })).body.code,
    ).toBe('csrf');
    expect(
      (
        await admin.agent
          .post('/api/admin/hosting')
          .set('Origin', 'https://evil.example')
          .set('X-CSRF-Token', admin.csrf)
          .send({ tls, reason: 'test' })
      ).body.code,
    ).toBe('csrf');
    const result = await save();
    expect(result.status).toBe(200);
    expect(result.body.pendingRestart).toBe(true);
    expect(result.body.running.enabled).toBe(false);
    expect(existsSync(hostingPath(t.dir))).toBe(true);
    const audit = await admin.agent.get('/api/audit?kind=admin_settings');
    expect(JSON.stringify(audit.body)).toContain('install the app');
    expect(JSON.stringify(audit.body)).not.toContain(keyFile);
  } finally {
    await t.close();
  }
});

async function freePort(): Promise<number> {
  const server = createServer();
  await new Promise<void>((resolve) => server.listen(0, '127.0.0.1', resolve));
  const port = (server.address() as { port: number }).port;
  await new Promise<void>((resolve) => server.close(() => resolve()));
  return port;
}

it('serves the same API over trusted TLS with Secure cookies and the intended origin while HTTP health remains available', async () => {
  const port = await freePort();
  const t = await startApp({ web: { tls: { ...tls, port } } });
  try {
    const secure = request.agent(`https://127.0.0.1:${port}`).ca(readFileSync(certFile));
    const csrf = await secure.get('/api/auth/csrf');
    expect(csrf.status).toBe(200);
    expect(csrf.headers['strict-transport-security']).toBeDefined();
    expect(csrf.headers['set-cookie']?.[0]).toContain('__Host-mcx=');
    expect(csrf.headers['set-cookie']?.[0]).toContain('Secure');
    const logged = await secure
      .post('/api/auth/login')
      .set('Origin', `https://localhost:${port}`)
      .set('X-CSRF-Token', csrf.body.csrfToken)
      .send({ username: 'fox', password: PASSWORD });
    expect(logged.status).toBe(200);
    expect((await secure.get('/api/admin/hosting')).body.httpsOrigin).toBe(`https://localhost:${port}`);
    expect((await request(t.url).get('/api/auth/csrf')).headers['set-cookie']?.[0]).not.toContain('Secure');
  } finally {
    await t.close();
  }
});
