import { existsSync, mkdtempSync, rmSync, statSync, utimesSync, writeFileSync } from 'node:fs';
import type { IncomingMessage } from 'node:http';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import request from 'supertest';
import { afterEach, describe, expect, it } from 'vitest';
import { totpCode } from '../src/auth/totp.js';
import { readUsersFile, UsersExistError, UsersStore } from '../src/auth/users-store.js';
import { isLocalAddress, notDirectLan, TrustedProxies } from '../src/security/client-ip.js';
import { csrfSession, ORIGIN, startApp, type Session, type TestApp } from './helpers.js';

const FIRST = {
  username: 'Fox',
  password: 'a long first password',
  player: 'poofimafox',
  roles: ['admin', 'chat', 'commands', 'viewer'],
};

let t: TestApp | undefined;
afterEach(async () => {
  await t?.close();
  t = undefined;
});

function post(s: Session, body: object = FIRST, headers: Record<string, string> = {}) {
  let r = s.agent.post('/api/setup').set('Origin', ORIGIN).set('X-CSRF-Token', s.csrf);
  for (const [k, v] of Object.entries(headers)) r = r.set(k, v);
  return r.send(body);
}

function setupAudit(app: TestApp) {
  return app.api.services.audit.list({ offset: 0, limit: 50, kind: 'setup' }).items;
}

describe('setup status', () => {
  it('is needed with no users file, and allowed from loopback', async () => {
    t = await startApp({}, { users: null });
    const r = await request(t.url).get('/api/setup').expect(200);
    expect(r.body).toEqual({ needed: true, allowedHere: true });
  });

  it('is not needed once any user exists', async () => {
    t = await startApp();
    expect((await request(t.url).get('/api/setup')).body).toEqual({ needed: false, allowedHere: false });
  });

  it('is needed for a valid file with no users, but never for a file that does not parse', () => {
    const dir = mkdtempSync(join(tmpdir(), 'mcx-users-'));
    try {
      const empty = join(dir, 'empty.json');
      writeFileSync(empty, '{"version":1,"users":[]}\n');
      expect(new UsersStore(empty).setupNeeded()).toBe(true);
      // Broken from the start: setup stays closed (the file may hold users we cannot read).
      const broken = join(dir, 'broken.json');
      writeFileSync(broken, '{ not json');
      expect(new UsersStore(broken).setupNeeded()).toBe(false);
      expect(new UsersStore(join(dir, 'missing.json')).setupNeeded()).toBe(true);
    } finally {
      rmSync(dir, { recursive: true, force: true });
    }
  });
});

describe('page redirects', () => {
  it('sends every page to /setup in setup mode, and /setup to /login afterwards', async () => {
    t = await startApp({}, { users: null });
    for (const path of ['/', '/live', '/login', '/overview?x=1', '/live/maid_chan']) {
      const r = await request(t.url).get(path);
      expect(r.status, path).toBe(302);
      expect(r.headers.location).toBe('/setup');
      expect(r.headers['cache-control']).toBe('no-store');
    }
    // Not pages: assets, files and the API pass through.
    for (const path of ['/setup', '/_next/static/chunks/app.js', '/mockServiceWorker.js', '/api/health']) {
      expect((await request(t.url).get(path)).status, path).not.toBe(302);
    }
    expect((await request(t.url).post('/live')).status).not.toBe(302);

    const s = await csrfSession(t.url);
    expect((await post(s)).status).toBe(200);
    const after = await request(t.url).get('/setup');
    expect(after.status).toBe(302);
    expect(after.headers.location).toBe('/login');
    expect((await request(t.url).get('/setup/anything')).headers.location).toBe('/login');
    expect((await request(t.url).get('/live')).status).not.toBe(302);
  });
});

describe('POST /api/setup', () => {
  it('creates the first account (0600 file), signs it in, and audits it', async () => {
    t = await startApp({}, { users: null });
    const s = await csrfSession(t.url);
    const r = await post(s);
    expect(r.status, JSON.stringify(r.body)).toBe(200);
    expect(r.body.user).toMatchObject({
      username: 'fox',
      player: 'poofimafox',
      roles: ['admin', 'chat', 'commands', 'viewer'],
      totpEnrolled: false,
    });
    expect(r.body.csrfToken).not.toBe(s.csrf); // the session rotated
    const me = await s.agent.get('/api/auth/me').expect(200);
    expect(me.body.username).toBe('fox');

    const file = readUsersFile(t.usersFile);
    expect(file.users).toHaveLength(1);
    expect(file.users[0]).toMatchObject({ username: 'fox', player: 'poofimafox', totp: null, disabled: false });
    expect(file.users[0]!.passwordHash.startsWith('$argon2id$')).toBe(true);
    if (process.platform !== 'win32') expect(statSync(t.usersFile).mode & 0o777).toBe(0o600);

    const audit = setupAudit(t);
    expect(audit).toHaveLength(1);
    expect(audit[0]).toMatchObject({ kind: 'setup', ok: true, username: 'fox', player: 'poofimafox', status: 200 });
    expect(audit[0]!.summary).toMatch(/created "fox"/);

    expect((await request(t.url).get('/api/setup')).body.needed).toBe(false);
  });

  it('has exactly one winner when two setups race', async () => {
    t = await startApp({}, { users: null });
    const a = await csrfSession(t.url);
    const b = await csrfSession(t.url);
    const [ra, rb] = await Promise.all([post(a), post(b, { ...FIRST, username: 'mallory', player: null })]);
    expect([ra.status, rb.status].sort()).toEqual([200, 409]);
    const loser = ra.status === 409 ? ra : rb;
    expect(loser.body.code).toBe('conflict');
    const file = readUsersFile(t.usersFile);
    expect(file.users).toHaveLength(1);
    expect(file.users[0]!.username).toBe(ra.status === 200 ? 'fox' : 'mallory');
    expect(setupAudit(t).map((e) => e.ok).sort()).toEqual([false, true]);
  });

  it('is closed for good once a user exists (409, audited)', async () => {
    t = await startApp();
    const s = await csrfSession(t.url);
    const r = await post(s, { ...FIRST, username: 'mallory' });
    expect(r.status).toBe(409);
    expect(readUsersFile(t.usersFile).users.some((u) => u.username === 'mallory')).toBe(false);
    expect(setupAudit(t)[0]).toMatchObject({ ok: false, status: 409 });
  });

  it('needs the CSRF token and an allowed Origin like every write', async () => {
    t = await startApp({}, { users: null });
    const s = await csrfSession(t.url);
    const noToken = await s.agent.post('/api/setup').set('Origin', ORIGIN).send(FIRST);
    expect(noToken.status).toBe(403);
    expect(noToken.body.code).toBe('csrf');
    const evil = await s.agent
      .post('/api/setup')
      .set('Origin', 'https://evil.example')
      .set('X-CSRF-Token', s.csrf)
      .send(FIRST);
    expect(evil.status).toBe(403);
    expect(readUsersFile(t.usersFile).users).toHaveLength(0);
  });

  it('validates the body: password length, username and player rules, at least one role', async () => {
    t = await startApp({}, { users: null });
    const s = await csrfSession(t.url);
    for (const bad of [
      { ...FIRST, password: 'short' },
      { ...FIRST, username: 'no spaces' },
      { ...FIRST, player: 'way_too_long_for_minecraft' },
      { ...FIRST, roles: [] },
      { ...FIRST, roles: ['root'] },
      { ...FIRST, extra: 1 },
    ]) {
      const r = await post(s, bad);
      expect(r.status, JSON.stringify(bad)).toBe(400);
    }
    expect((await post(s, { ...FIRST, player: null, roles: ['viewer'] })).status).toBe(200);
  });

  it('is rate limited per IP', async () => {
    t = await startApp({ web: { setup: { attemptsPerMinute: 2 } } }, { users: null });
    const s = await csrfSession(t.url);
    // Malformed bodies are 400s before the handler; refused attempts count.
    expect((await post(s, FIRST, { 'Cf-Ray': 'x' })).status).toBe(403);
    expect((await post(s, FIRST, { 'Cf-Ray': 'x' })).status).toBe(403);
    const r = await post(s);
    expect(r.status).toBe(429);
    expect(r.body.code).toBe('rate_limited');
  });
});

describe('setup refuses proxied requests', () => {
  it('refuses a request from a trusted proxy (cloudflared) and audits it', async () => {
    t = await startApp({ web: { trustProxy: true, trustedProxies: ['127.0.0.1', '::1'] } }, { users: null });
    expect((await request(t.url).get('/api/setup')).body).toEqual({ needed: true, allowedHere: false });
    const s = await csrfSession(t.url);
    const r = await post(s, FIRST, { 'CF-Connecting-IP': '203.0.113.9' });
    expect(r.status).toBe(403);
    expect(r.body.code).toBe('forbidden');
    expect(readUsersFile(t.usersFile).users).toHaveLength(0);
    const audit = setupAudit(t)[0]!;
    expect(audit).toMatchObject({ ok: false, status: 403, ip: '203.0.113.9' });
    expect(audit.summary).toMatch(/trusted proxy/);
  });

  it('refuses Cloudflare headers even when TrustProxy is off', async () => {
    t = await startApp({}, { users: null });
    const s = await csrfSession(t.url);
    expect((await post(s, FIRST, { 'Cf-Ray': '8a1b2c3d4e5f-AMS' })).status).toBe(403);
    expect((await s.agent.get('/api/setup').set('CF-Connecting-IP', '203.0.113.9')).body.allowedHere).toBe(false);
  });

  it('Web:Setup:AllowProxied lets it through', async () => {
    t = await startApp(
      { web: { trustProxy: true, trustedProxies: ['127.0.0.1', '::1'], setup: { allowProxied: true } } },
      { users: null },
    );
    const s = await csrfSession(t.url);
    expect((await post(s, FIRST, { 'CF-Connecting-IP': '203.0.113.9' })).status).toBe(200);
  });
});

describe('sign-in around setup', () => {
  it('login is off in setup mode and works with the new account afterwards', async () => {
    t = await startApp({}, { users: null });
    const pre = await csrfSession(t.url);
    const off = await pre.agent
      .post('/api/auth/login')
      .set('Origin', ORIGIN)
      .set('X-CSRF-Token', pre.csrf)
      .send({ username: 'fox', password: FIRST.password });
    expect(off.status).toBe(409);

    expect((await post(pre)).status).toBe(200);
    const s = await csrfSession(t.url);
    const r = await s.agent
      .post('/api/auth/login')
      .set('Origin', ORIGIN)
      .set('X-CSRF-Token', s.csrf)
      .send({ username: 'fox', password: FIRST.password });
    expect(r.status).toBe(200);
    expect(r.body.user.roles).toEqual(['admin', 'chat', 'commands', 'viewer']);
  });

  it('the optional TOTP step: enrol + verify right after setup with the existing routes; login then needs the code', async () => {
    t = await startApp({}, { users: null });
    const pre = await csrfSession(t.url);
    const created = await post(pre);
    const s: Session = { agent: pre.agent, csrf: created.body.csrfToken as string };
    const enroll = await s.agent
      .post('/api/auth/totp/enroll')
      .set('Origin', ORIGIN)
      .set('X-CSRF-Token', s.csrf)
      .send({ password: FIRST.password });
    expect(enroll.status).toBe(200);
    expect(enroll.body.otpauthUrl).toMatch(/^otpauth:\/\/totp\/MineClaw:fox\?/);
    const verify = await s.agent
      .post('/api/auth/totp/verify')
      .set('Origin', ORIGIN)
      .set('X-CSRF-Token', s.csrf)
      .send({ code: totpCode(enroll.body.secret as string, Date.now()) });
    expect(verify.body).toEqual({ ok: true, totpEnrolled: true });
    // Still signed in (TOTP activation does not change the password fingerprint).
    expect((await s.agent.get('/api/auth/me')).body.totpEnrolled).toBe(true);
    if (process.platform !== 'win32') expect(statSync(t.usersFile).mode & 0o777).toBe(0o600);

    const s2 = await csrfSession(t.url);
    const noCode = await s2.agent
      .post('/api/auth/login')
      .set('Origin', ORIGIN)
      .set('X-CSRF-Token', s2.csrf)
      .send({ username: 'fox', password: FIRST.password });
    expect(noCode.status).toBe(401);
  });
});

describe('UsersStore.createFirst', () => {
  const record = (username: string) => ({
    username,
    passwordHash: '$argon2id$v=19$m=19456,t=2,p=1$c2FsdHNhbHQ$aGFzaGhhc2hoYXNoaGFzaA',
    roles: ['admin' as const],
    player: null,
    totp: null,
    disabled: false,
  });

  it('re-reads the file under the lock: a second store (another process) that still thinks it is empty loses', () => {
    const dir = mkdtempSync(join(tmpdir(), 'mcx-users-'));
    try {
      const file = join(dir, 'users.json');
      const a = new UsersStore(file);
      const b = new UsersStore(file);
      expect(a.setupNeeded() && b.setupNeeded()).toBe(true);
      a.createFirst(record('fox'));
      expect(() => b.createFirst(record('mallory'))).toThrow(UsersExistError);
      expect(readUsersFile(file).users.map((u) => u.username)).toEqual(['fox']);
      expect(existsSync(join(dir, '.users.json.lock'))).toBe(false);
    } finally {
      rmSync(dir, { recursive: true, force: true });
    }
  });

  it('breaks a stale lock left by a crashed writer', () => {
    const dir = mkdtempSync(join(tmpdir(), 'mcx-users-'));
    try {
      const file = join(dir, 'users.json');
      const lock = join(dir, '.users.json.lock');
      writeFileSync(lock, '12345\n');
      const old = new Date(Date.now() - 60_000);
      utimesSync(lock, old, old);
      new UsersStore(file).createFirst(record('fox'));
      expect(readUsersFile(file).users).toHaveLength(1);
    } finally {
      rmSync(dir, { recursive: true, force: true });
    }
  });
});

describe('notDirectLan', () => {
  const req = (peer: string, headers: Record<string, string> = {}) =>
    ({ socket: { remoteAddress: peer }, headers }) as unknown as IncomingMessage;
  const off = new TrustedProxies({ trustProxy: false, trustedProxies: [] });
  const on = new TrustedProxies({ trustProxy: true, trustedProxies: ['10.1.2.1'] });

  it('accepts direct LAN, Tailscale and loopback clients (and the dev rewrite proxy on loopback)', () => {
    expect(notDirectLan(req('10.0.1.20'), off)).toBeNull();
    expect(notDirectLan(req('::ffff:192.168.1.5'), off)).toBeNull();
    expect(notDirectLan(req('100.101.102.103'), off)).toBeNull();
    expect(notDirectLan(req('fd7a:115c:a1e0::1'), off)).toBeNull();
    expect(notDirectLan(req('127.0.0.1', { 'x-forwarded-for': '127.0.0.1' }), off)).toBeNull();
  });

  it('refuses trusted proxies, Cloudflare headers, unknown proxies and public addresses', () => {
    expect(notDirectLan(req('10.1.2.1'), on)).toMatch(/trusted proxy/);
    expect(notDirectLan(req('127.0.0.1', { 'cf-connecting-ip': '1.2.3.4' }), off)).toMatch(/Cloudflare/);
    expect(notDirectLan(req('10.0.1.20', { 'x-forwarded-for': '1.2.3.4' }), off)).toMatch(/proxy/);
    expect(notDirectLan(req('203.0.113.9'), off)).toMatch(/LAN/);
    expect(isLocalAddress('8.8.8.8')).toBe(false);
    expect(isLocalAddress('172.20.0.1')).toBe(true);
    expect(isLocalAddress('172.32.0.1')).toBe(false);
  });
});
