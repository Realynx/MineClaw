import { afterEach, describe, expect, it } from 'vitest';
import { ManualClock } from '../src/common/clock.js';
import { readUsersFile, writeUsersFile } from '../src/auth/users-store.js';
import { hashPassword } from '../src/auth/passwords.js';
import { totpCode } from '../src/auth/totp.js';
import { csrfSession, login, ORIGIN, PASSWORD, startApp, type TestApp } from './helpers.js';

const SECRET = 'JBSWY3DPEHPK3PXPJBSWY3DPEHPK3PXP';
let t: TestApp | undefined;
afterEach(async () => {
  await t?.close();
  t = undefined;
});

function setCookieNames(headers: Record<string, unknown>): string[] {
  const sc = headers['set-cookie'];
  return (Array.isArray(sc) ? (sc as string[]) : []).map((c) => c.split('=')[0]!);
}

describe('login', () => {
  it('signs in, returns Me and a new CSRF token, and rotates the session id', async () => {
    t = await startApp();
    const s = await csrfSession(t.url);
    const pre = await s.agent.get('/api/auth/csrf');
    expect(pre.body.csrfToken).toBe(s.csrf); // same pre-session
    const r = await s.agent
      .post('/api/auth/login')
      .set('Origin', ORIGIN)
      .set('X-CSRF-Token', s.csrf)
      .send({ username: 'Fox', password: PASSWORD });
    expect(r.status).toBe(200);
    expect(r.body.user).toMatchObject({
      username: 'fox',
      player: 'poofimafox',
      roles: ['admin', 'chat', 'commands', 'viewer'],
      totpEnrolled: false,
    });
    expect(r.body.csrfToken).not.toBe(s.csrf);
    expect(setCookieNames(r.headers)).toEqual(['mcx']);
    const cookie = String((r.headers['set-cookie'] as unknown as string[])[0]);
    expect(cookie).toMatch(/HttpOnly/);
    expect(cookie).toMatch(/SameSite=Lax/);
    expect(cookie).not.toMatch(/Secure/); // plain HTTP LAN mode
    const me = await s.agent.get('/api/auth/me').expect(200);
    expect(me.body.username).toBe('fox');
    // the old pre-session row is gone
    const rows = t.api.services.db.db.prepare('select count(*) as n from sessions where username is null').get() as {
      n: number;
    };
    expect(rows.n).toBe(0);
  });

  it('answers the same generic 401 for a wrong password and an unknown user', async () => {
    t = await startApp();
    const s = await csrfSession(t.url);
    const a = await s.agent
      .post('/api/auth/login')
      .set('Origin', ORIGIN)
      .set('X-CSRF-Token', s.csrf)
      .send({ username: 'fox', password: 'nope-nope-nope' });
    const b = await s.agent
      .post('/api/auth/login')
      .set('Origin', ORIGIN)
      .set('X-CSRF-Token', s.csrf)
      .send({ username: 'ghost', password: 'nope-nope-nope' });
    expect(a.status).toBe(401);
    expect(b.status).toBe(401);
    expect(a.body).toEqual(b.body);
    expect(a.body).toEqual({ error: 'invalid credentials', code: 'unauthorized' });
    const audit = t.api.services.audit.list({ offset: 0, limit: 10 });
    expect(audit.items.filter((i) => i.kind === 'login_failed')).toHaveLength(2);
    expect(audit.items.find((i) => i.summary.includes('ghost'))?.username).toBeNull();
  });

  it('rejects a disabled user generically', async () => {
    t = await startApp({}, { users: [{ username: 'fox', disabled: true }] });
    const s = await csrfSession(t.url);
    const r = await s.agent
      .post('/api/auth/login')
      .set('Origin', ORIGIN)
      .set('X-CSRF-Token', s.csrf)
      .send({ username: 'fox', password: PASSWORD });
    expect(r.status).toBe(401);
  });
});

describe('TOTP', () => {
  it('requires a valid code once enrolled, accepts ±1 step, and refuses replays', async () => {
    const clock = new ManualClock(Date.UTC(2026, 10, 2, 10, 0, 15));
    t = await startApp(
      {},
      { clock, users: [{ username: 'fox', totp: { secret: SECRET, enrolledAt: '2026-11-01T00:00:00Z' } }] },
    );
    const s = await csrfSession(t.url);
    const send = (totp?: string) =>
      s.agent
        .post('/api/auth/login')
        .set('Origin', ORIGIN)
        .set('X-CSRF-Token', s.csrf)
        .send({ username: 'fox', password: PASSWORD, ...(totp ? { totp } : {}) });
    expect((await send()).status).toBe(401);
    expect((await send('000000')).status).toBe(401);
    const previous = totpCode(SECRET, clock.now() - 30_000);
    const ok = await send(previous);
    expect(ok.status).toBe(200);
    expect(ok.body.user.totpEnrolled).toBe(true);
    const s2 = await csrfSession(t.url);
    const replay = await s2.agent
      .post('/api/auth/login')
      .set('Origin', ORIGIN)
      .set('X-CSRF-Token', s2.csrf)
      .send({ username: 'fox', password: PASSWORD, totp: previous });
    expect(replay.status).toBe(401);
    const tooOld = totpCode(SECRET, clock.now() - 90_000);
    const r3 = await s2.agent
      .post('/api/auth/login')
      .set('Origin', ORIGIN)
      .set('X-CSRF-Token', s2.csrf)
      .send({ username: 'fox', password: PASSWORD, totp: tooOld });
    expect(r3.status).toBe(401);
    const current = await s2.agent
      .post('/api/auth/login')
      .set('Origin', ORIGIN)
      .set('X-CSRF-Token', s2.csrf)
      .send({ username: 'fox', password: PASSWORD, totp: totpCode(SECRET, clock.now()) });
    expect(current.status).toBe(200);
  });

  it('enrolls through the API: password re-check, pending secret, verify writes the users file', async () => {
    const clock = new ManualClock();
    t = await startApp({}, { clock });
    const s = await login(t.url, 'robin');
    const bad = await s.agent
      .post('/api/auth/totp/enroll')
      .set('Origin', ORIGIN)
      .set('X-CSRF-Token', s.csrf)
      .send({ password: 'wrong-password!' });
    expect(bad.status).toBe(401);
    const e = await s.agent
      .post('/api/auth/totp/enroll')
      .set('Origin', ORIGIN)
      .set('X-CSRF-Token', s.csrf)
      .send({ password: PASSWORD });
    expect(e.status).toBe(200);
    expect(e.body.otpauthUrl).toMatch(/^otpauth:\/\/totp\/MineClaw:robin\?secret=/);
    expect(readUsersFile(t.usersFile).users.find((u) => u.username === 'robin')?.totp).toBeNull();
    const wrong = await s.agent
      .post('/api/auth/totp/verify')
      .set('Origin', ORIGIN)
      .set('X-CSRF-Token', s.csrf)
      .send({ code: '000000' });
    expect(wrong.body).toEqual({ ok: false, totpEnrolled: false });
    const v = await s.agent
      .post('/api/auth/totp/verify')
      .set('Origin', ORIGIN)
      .set('X-CSRF-Token', s.csrf)
      .send({ code: totpCode(e.body.secret as string, clock.now()) });
    expect(v.body).toEqual({ ok: true, totpEnrolled: true });
    expect(readUsersFile(t.usersFile).users.find((u) => u.username === 'robin')?.totp?.secret).toBe(e.body.secret);
    // the session survives activation
    expect((await s.agent.get('/api/auth/me')).body.totpEnrolled).toBe(true);
    const kinds = t.api.services.audit.list({ offset: 0, limit: 20 }).items.map((i) => `${i.kind}:${i.ok}`);
    expect(kinds).toEqual(
      expect.arrayContaining(['totp_enroll:false', 'totp_enroll:true', 'totp_verify:false', 'totp_verify:true']),
    );
  });
});

describe('lockout', () => {
  it('locks a user after 5 failures with exponential backoff, even for the right password, and audits it', async () => {
    const clock = new ManualClock();
    t = await startApp(
      {
        web: {
          login: {
            userThreshold: 5,
            ipThreshold: 100,
            lockoutBaseSeconds: 30,
            lockoutMaxSeconds: 3600,
            ipAttemptsPerMinute: 1000,
          },
        },
      },
      { clock },
    );
    const s = await csrfSession(t.url);
    const attempt = (password: string) =>
      s.agent
        .post('/api/auth/login')
        .set('Origin', ORIGIN)
        .set('X-CSRF-Token', s.csrf)
        .send({ username: 'fox', password });
    for (let i = 0; i < 4; i++) expect((await attempt('wrong-password!')).status).toBe(401);
    expect((await attempt('wrong-password!')).status).toBe(401); // 5th: locks
    const locked = await attempt(PASSWORD);
    expect(locked.status).toBe(423);
    expect(locked.body.code).toBe('locked');
    expect(locked.headers['retry-after']).toBe('30');
    clock.advance(31_000);
    expect((await attempt('wrong-password!')).status).toBe(401); // 6th failure: 60 s now
    expect((await attempt(PASSWORD)).status).toBe(423);
    clock.advance(59_000);
    expect((await attempt(PASSWORD)).status).toBe(423);
    clock.advance(2_000);
    expect((await attempt(PASSWORD)).status).toBe(200);
    const kinds = t.api.services.audit.list({ offset: 0, limit: 50, kind: 'lockout' });
    expect(kinds.total).toBe(2);
  });

  it('locks per IP across usernames, and also locks unknown usernames (no enumeration)', async () => {
    t = await startApp({
      web: {
        login: {
          userThreshold: 2,
          ipThreshold: 4,
          lockoutBaseSeconds: 30,
          lockoutMaxSeconds: 3600,
          ipAttemptsPerMinute: 1000,
        },
      },
    });
    const s = await csrfSession(t.url);
    const attempt = (username: string, password = 'wrong-password!') =>
      s.agent.post('/api/auth/login').set('Origin', ORIGIN).set('X-CSRF-Token', s.csrf).send({ username, password });
    await attempt('ghost');
    await attempt('ghost');
    expect((await attempt('ghost')).status).toBe(423); // an unknown name locks like a real one
    await attempt('ghost2');
    await attempt('ghost3'); // 4th failure from this IP
    expect((await attempt('robin', PASSWORD)).status).toBe(423); // the IP key is locked now
  });

  it('rate limits raw login attempts per IP with 429', async () => {
    t = await startApp({
      web: {
        login: {
          userThreshold: 100,
          ipThreshold: 100,
          lockoutBaseSeconds: 30,
          lockoutMaxSeconds: 3600,
          ipAttemptsPerMinute: 3,
        },
      },
    });
    const s = await csrfSession(t.url);
    const statuses: number[] = [];
    for (let i = 0; i < 4; i++) {
      statuses.push(
        (
          await s.agent
            .post('/api/auth/login')
            .set('Origin', ORIGIN)
            .set('X-CSRF-Token', s.csrf)
            .send({ username: 'x', password: 'wrong-password!' })
        ).status,
      );
    }
    expect(statuses).toEqual([401, 401, 401, 429]);
  });
});

describe('sessions', () => {
  it('expires after 12 h idle, slides with activity, and ends at 7 d absolute', async () => {
    const clock = new ManualClock();
    t = await startApp({}, { clock });
    const s = await login(t.url, 'vera');
    clock.advance(11 * 3600_000);
    expect((await s.agent.get('/api/auth/me')).status).toBe(200); // slides
    clock.advance(11 * 3600_000);
    expect((await s.agent.get('/api/auth/me')).status).toBe(200);
    clock.advance(12 * 3600_000 + 1000);
    expect((await s.agent.get('/api/auth/me')).status).toBe(401);

    const s2 = await login(t.url, 'vera');
    for (let h = 0; h < 7 * 24; h += 10) {
      clock.advance(10 * 3600_000);
      const r = await s2.agent.get('/api/auth/me');
      if (h + 10 >= 7 * 24) expect(r.status).toBe(401);
      else expect(r.status).toBe(200);
    }
  });

  it('logout ends the session and clears the cookie', async () => {
    t = await startApp();
    const s = await login(t.url, 'vera');
    const r = await s.agent.post('/api/auth/logout').set('Origin', ORIGIN).set('X-CSRF-Token', s.csrf).send({});
    expect(r.status).toBe(200);
    expect(String((r.headers['set-cookie'] as unknown as string[])[0])).toMatch(/Max-Age=0/);
    expect((await s.agent.get('/api/auth/me')).status).toBe(401);
  });

  it('a password reset or removal in the users file ends existing sessions; role edits apply live', async () => {
    t = await startApp();
    const s = await login(t.url, 'robin');
    expect((await s.agent.get('/api/admin/schema')).status).toBe(403);
    const data = readUsersFile(t.usersFile);
    data.users.find((u) => u.username === 'robin')!.roles = ['admin', 'viewer'];
    writeUsersFile(t.usersFile, data);
    t.api.services.users.invalidate();
    expect((await s.agent.get('/api/auth/me')).body.roles).toEqual(['admin', 'viewer']);
    data.users.find((u) => u.username === 'robin')!.passwordHash = await hashPassword('another password 123');
    writeUsersFile(t.usersFile, data);
    t.api.services.users.invalidate();
    expect((await s.agent.get('/api/auth/me')).status).toBe(401);
  });

  it('uses __Host-mcx with Secure only for HTTPS via a trusted proxy', async () => {
    t = await startApp({ web: { trustProxy: true, trustedProxies: ['127.0.0.1', '::1'] } });
    const s = await csrfSession(t.url);
    const r = await s.agent.get('/api/auth/csrf').set('X-Forwarded-Proto', 'https');
    const cookie = String((r.headers['set-cookie'] as unknown as string[])[0]);
    expect(cookie.startsWith('__Host-mcx=')).toBe(true);
    expect(cookie).toMatch(/; Secure/);
    expect(cookie).toMatch(/Path=\//);
    expect(cookie).not.toMatch(/Domain=/);
  });
});
