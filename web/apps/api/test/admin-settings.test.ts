/** Administration routes: the admin role, a fresh step-up for writes, CSRF, the forwarded identity, and no secret in the audit. */
import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import { STEP_UP_MS } from '@mineclaw/contracts';
import { totpCode } from '../src/auth/totp.js';
import { ManualClock } from '../src/common/clock.js';
import { fakeBridge, json, login, ORIGIN, PASSWORD, startApp, type RecordedRequest, type Session, type TestApp } from './helpers.js';

const SECRET = 'JBSWY3DPEHPK3PXPJBSWY3DPEHPK3PXP';
const API_KEY = 'sk-ant-api03-never-in-the-audit-ABCD1234';

const SETTINGS = {
  node: { id: 'local', name: 'maid_chan', version: '1.0', supervisor_url: 'http://127.0.0.1:8765', reachable: true },
  groups: [{ id: 'models', title: 'Models & providers', description: 'x' }],
  settings: [
    {
      key: 'Agent:Llm:ApiKey', group: 'models', section: 'Anthropic', label: 'Anthropic API key', description: 'x', type: 'secret', apply: 'restart',
      secret: true, editable: true, secret_state: { set: true, hint: '…1234' }, source: 'config', overridden: false, pending_restart: false,
    },
  ],
  allowed_claude_models: ['claude-opus-5-5', 'claude-sonnet-5-5', 'claude-haiku-5-5'],
  restart_pending: [],
  override: { path: '/var/lib/mineclaw/settings.override.json', exists: false, backups: 0 },
};

let bridge: Awaited<ReturnType<typeof fakeBridge>>;
let t: TestApp;
const clock = new ManualClock(Date.UTC(2026, 10, 3, 10, 0, 15));

function sent(path: string): RecordedRequest[] {
  return bridge.requests.filter((r) => r.target.split('?')[0] === path);
}

beforeAll(async () => {
  bridge = await fakeBridge((req, res) => {
    const path = req.target.split('?')[0];
    if (path === '/v1/admin/nodes/local/settings' && req.method === 'GET') return json(res, 200, SETTINGS);
    if (path === '/v1/admin/nodes/local/settings' && req.method === 'POST') {
      const b = JSON.parse(req.body.toString('utf8')) as { changes: Array<{ key: string }> };
      if (b.changes.some((c) => c.key === 'Agent:Llm:MainModel'))
        return json(res, 400, {
          error: 'Agent:Llm:MainModel: not allowed',
          code: 'bad_request',
          details: [{ path: 'Agent:Llm:MainModel', message: "'claude-3-opus' is not allowed" }],
        });
      return json(res, 200, { applied: [], restart_required: ['Agent:Llm:ApiKey'], backup: null });
    }
    if (path === '/v1/admin/nodes/local/restart') return json(res, 409, { error: 'not safe to restart now: maid_chan_101 is outside at night', code: 'conflict' });
    if (path === '/v1/admin/nodes/local/settings/test') return json(res, 200, { ok: true, message: 'key accepted', latency_ms: 120 });
    return json(res, 404, { error: 'not found', code: 'not_found' });
  });
  t = await startApp(
    { bridge: { url: bridge.url } },
    { clock, users: [{ username: 'otto', roles: ['admin', 'viewer'], player: null, totp: { secret: SECRET, enrolledAt: '2026-11-01T00:00:00Z' } }] },
  );
});

afterAll(async () => {
  await t.close();
  await bridge.close();
});

const save = (s: Session, body: object = { changes: [{ key: 'Agent:Llm:ApiKey', value: API_KEY }], reason: 'rotate the key' }) =>
  s.agent.post('/api/admin/nodes/local/settings').set('Origin', ORIGIN).set('X-CSRF-Token', s.csrf).send(body);

const stepUp = (s: Session, body: object) => s.agent.post('/api/auth/step-up').set('Origin', ORIGIN).set('X-CSRF-Token', s.csrf).send(body);

describe('administration', () => {
  it('needs a session and the admin role', async () => {
    const anon = await (await import('supertest')).default(t.url).get('/api/admin/nodes/local/settings');
    expect(anon.status).toBe(401);
    const robin = await login(t.url, 'robin');
    expect((await robin.agent.get('/api/admin/nodes/local/settings')).status).toBe(403);
    expect((await robin.agent.get('/api/admin/nodes')).status).toBe(403);
    expect((await save(robin)).status).toBe(403);
    const story = await login(t.url, 'sky');
    expect((await story.agent.get('/api/admin/nodes/local/settings')).status).toBe(403);
    expect(sent('/v1/admin/nodes/local/settings')).toHaveLength(0);

    const fox = await login(t.url, 'fox');
    const r = await fox.agent.get('/api/admin/nodes/local/settings');
    expect(r.status).toBe(200);
    expect(r.body.settings[0].secretState).toEqual({ set: true, hint: '…1234' });
    expect(r.body.allowedClaudeModels).toContain('claude-opus-5-5');
    expect(sent('/v1/admin/nodes/local/settings')[0]!.identity.roles).toContain('admin');
    expect((await fox.agent.get('/api/admin/nodes/BAD!/settings')).status).toBe(400);
  });

  it('writes need a fresh step-up, CSRF and Origin', async () => {
    const fox = await login(t.url, 'fox');
    const before = bridge.requests.length;
    const stale = await save(fox);
    expect(stale.status).toBe(403);
    expect(stale.body.code).toBe('step_up_required');
    expect((await fox.agent.post('/api/admin/nodes/local/restart').set('Origin', ORIGIN).set('X-CSRF-Token', fox.csrf).send({ reason: 'apply' })).body.code).toBe(
      'step_up_required',
    );
    expect(bridge.requests.length).toBe(before);

    // Without TOTP the password confirms; a wrong one is refused like a login.
    expect((await stepUp(fox, { password: 'wrong password!' })).status).toBe(401);
    expect((await stepUp(fox, { password: PASSWORD, code: '123456' })).status).toBe(400);
    const ok = await stepUp(fox, { password: PASSWORD });
    expect(ok.status).toBe(200);
    expect(Date.parse(ok.body.stepUpUntil)).toBe(clock.now() + STEP_UP_MS);
    expect((await fox.agent.get('/api/auth/me')).body.stepUpUntil).toBe(ok.body.stepUpUntil);

    // CSRF and Origin still apply.
    expect((await fox.agent.post('/api/admin/nodes/local/settings').set('Origin', ORIGIN).send({ changes: [], reason: 'x' })).body.code).toBe('csrf');
    expect(
      (await fox.agent.post('/api/admin/nodes/local/settings').set('Origin', 'http://evil.example').set('X-CSRF-Token', fox.csrf).send({ changes: [], reason: 'x' }))
        .body.code,
    ).toBe('csrf');

    const saved = await save(fox);
    expect(saved.status).toBe(200);
    expect(saved.body.restartRequired).toEqual(['Agent:Llm:ApiKey']);
    const fwd = sent('/v1/admin/nodes/local/settings').at(-1)!;
    expect(fwd.method).toBe('POST');
    expect(JSON.parse(fwd.body.toString('utf8'))).toEqual({ changes: [{ key: 'Agent:Llm:ApiKey', value: API_KEY }], reason: 'rotate the key' });
    expect(fwd.identity.user).toBe('fox');

    // Per-key validation errors pass through as details.
    const bad = await save(fox, { changes: [{ key: 'Agent:Llm:MainModel', value: 'claude-3-opus' }], reason: 'try it' });
    expect(bad.status).toBe(400);
    expect(bad.body.details).toEqual([{ path: 'Agent:Llm:MainModel', message: "'claude-3-opus' is not allowed" }]);

    // A refused restart is a 409 with the bot's reason.
    const restart = await fox.agent.post('/api/admin/nodes/local/restart').set('Origin', ORIGIN).set('X-CSRF-Token', fox.csrf).send({ reason: 'apply' });
    expect(restart.status).toBe(409);
    expect(restart.body.error).toContain('outside at night');

    // The step-up lapses after five minutes.
    clock.advance(STEP_UP_MS + 1000);
    expect((await save(fox)).body.code).toBe('step_up_required');
    expect((await fox.agent.get('/api/auth/me')).body.stepUpUntil).toBeNull();
  });

  it('with TOTP only the current code steps up, once', async () => {
    const otto = await login(t.url, 'otto', { totp: totpCode(SECRET, clock.now() - 30_000) });
    expect((await stepUp(otto, { password: PASSWORD })).status).toBe(401);
    const code = totpCode(SECRET, clock.now());
    expect((await stepUp(otto, { code })).status).toBe(200);
    expect((await stepUp(otto, { code })).status).toBe(401); // replayed
    const test = await otto.agent.post('/api/admin/nodes/local/settings/test').set('Origin', ORIGIN).set('X-CSRF-Token', otto.csrf).send({ kind: 'anthropic', apiKey: API_KEY });
    expect(test.status).toBe(200);
    expect(test.body.ok).toBe(true);
  });

  it('never writes a secret to the web audit', async () => {
    const fox = await login(t.url, 'fox');
    const audit = await fox.agent.get('/api/audit?limit=200');
    expect(audit.status).toBe(200);
    const text = JSON.stringify(audit.body);
    expect(text).toContain('admin_settings');
    expect(text).toContain('Agent:Llm:ApiKey');
    expect(text).toContain('step_up');
    expect(text).not.toContain(API_KEY);
    expect(text).not.toContain(PASSWORD);
  });
});
