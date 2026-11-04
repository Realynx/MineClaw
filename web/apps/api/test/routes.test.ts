/**
 * Every route in the contracts table × every access level, against the in-process mock bridge with strict response
 * validation: callers without the role get 401/403 before anything reaches the bridge; callers with it get a 2xx whose
 * body matches the contract (the dispatcher turns a mismatch into 502). New routes are covered automatically: inputs are
 * generated from their schemas (overrides below only where a generated sample would not be meaningful).
 */
import { ROUTES, ROUTE_IDS, apiPath, hasRole, type RouteDef, type RouteId } from '@mineclaw/contracts';
import request from 'supertest';
import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import { generate } from '../src/mock/generate.js';
import { CAMP, topAt } from '../src/mock/terrain.js';
import { csrfSession, login, ORIGIN, PASSWORD, PERSONAS, startApp, type Session, type TestApp } from './helpers.js';

/** Routes whose success path has side effects or needs its own flow; tested in auth/setup/sse tests. Gating is still checked. */
const OWN_FLOW: ReadonlySet<RouteId> = new Set([
  'setup.create',
  'auth.login',
  'auth.logout',
  'auth.totpEnroll',
  'auth.totpVerify',
  'auth.stepUp',
  'live.stream',
]);

const OVERRIDES: Partial<
  Record<RouteId, { params?: Record<string, unknown>; query?: Record<string, unknown>; body?: unknown }>
> = {
  'memory.get': { params: { kind: 'facts', id: '12' } },
  'bots.chat': { body: { text: 'claude, set it to day', requestId: 'r-8f14e45fceea' } },
  'vision.render': { query: { see: '{"mode":"eye"}' } },
  'auth.stepUp': { body: { password: 'correct horse battery staple' } },
  'admin.settingsSave': { params: { node: 'local' }, body: { changes: [{ key: 'Supervisor:Viewer:PerMinute', value: 20 }], reason: 'route table test' } },
  // Story Mode: the mock bridge's colony `story-1` (Mock Hollow, founded at start) and its first pawn.
  ...storyOverrides(),
};

function storyOverrides(): typeof OVERRIDES {
  const id = { id: 'story-1' };
  const pawns = ['maid_s1_dara'];
  return {
    'story.recipes': { query: { workbench: 'crafting_table' } },
    'story.create': { body: { name: 'Second Hollow', pawns: 2 } },
    'story.get': { params: id, query: {} },
    'story.release': { params: id, body: {} },
    'story.resume': { params: id, body: {} },
    'story.online': { params: id, body: { pawns: null } },
    'story.directive': { params: { id: 'dir-r-story0001' } },
    'story.pause': { params: id, body: { paused: false } },
    'story.home': { params: id, body: { home: { x: 4, y: 64, z: -3 } } },
    'story.time': { body: { time: 6000 } },
    // the first of the camp's two beds (mock/terrain.ts)
    'story.beds': { params: id, body: { op: 'assign', bed: { x: CAMP[0] - 3, y: topAt(CAMP[0] - 3, CAMP[2] + 3) + 1, z: CAMP[2] + 3 }, pawn: 'maid_s1_dara' } },
    'story.priorities': { params: id, body: { pawns, work: { chop: 1, haul: 2 } } },
    'story.schedule': { params: id, body: { pawns, hours: { '0': 'work', '20': 'sleep' } } },
    'story.draft': { params: id, body: { pawns, drafted: false } },
    'story.orders': { params: id, body: { pawns, order: { kind: 'move', target: { x: 20, y: 64, z: 12 } }, queue: true } },
    'story.zones': { params: id, body: { op: 'add', kind: 'farm', area: [12, 63, 18, 19, 63, 23] } },
    'story.designations': { params: id, body: { op: 'add', kind: 'chop', area: [26, 60, 2, 50, 75, 20] } },
    'story.builds': { params: id, body: { op: 'add', items: [{ kind: 'wall', area: [0, 64, 0, 4, 64, 4], material: 'cobblestone', height: 3 }] } },
    'story.storage': { params: id, body: { op: 'assign_area', area: [14, 60, 0, 30, 70, 10] } },
    'story.bills': { params: id, body: { op: 'add', bench: [17, 63, 7], bill: { recipe: 'sticks', mode: 'until', target: 64, minSkill: 2 } } },
    'story.directives': { body: { bots: ['maid_chan'], directive: { kind: 'text', text: 'come here' }, requestId: 'r-story0001' } },
    'story.workbenches': { params: id, body: { op: 'designate', pos: [17, 63, 7] } },
    'story.options': { params: id, body: { autoCraftTools: true, spareTools: { count: 1, kinds: ['pickaxe'] } } },
    'story.architect': { params: id },
    'story.policies': { params: id, body: { op: 'set', policy: { name: 'Miners', tools: ['pickaxe', 'shovel'], armor: 'none' } } },
    'story.materialRules': { params: id, body: { op: 'set', rule: { material: 'iron_ore', tier: 'iron', enchant: 'fortune' } } },
    'story.gear': { params: id, body: { op: 'arm', pawns } },
  };
}

function toQuery(v: unknown): Record<string, string> {
  const out: Record<string, string> = {};
  for (const [k, x] of Object.entries((v ?? {}) as Record<string, unknown>)) {
    if (typeof x === 'string' || typeof x === 'number' || typeof x === 'boolean') out[k] = String(x);
  }
  return out;
}

export function sampleInput(id: RouteId): { path: string; body?: unknown } {
  const r: RouteDef = ROUTES[id];
  const o = OVERRIDES[id] ?? {};
  const params = o.params ?? (r.params ? (generate(r.params) as Record<string, unknown>) : {});
  const query = o.query ?? (r.query ? toQuery(generate(r.query)) : undefined);
  if (r.params) expect(r.params.safeParse(params).success, `${id}: sample params`).toBe(true);
  if (r.query)
    expect(r.query.safeParse(query ?? {}).success, `${id}: sample query ${JSON.stringify(query)}`).toBe(true);
  const body =
    r.method === 'POST' ? (o.body ?? (r.body && r.bodyKind !== 'multipart' ? generate(r.body) : {})) : undefined;
  if (r.body && r.bodyKind !== 'multipart') expect(r.body.safeParse(body).success, `${id}: sample body`).toBe(true);
  return { path: apiPath(id, params, query), body };
}

type Who = 'anon' | keyof typeof PERSONAS;
const WHO: Who[] = ['anon', 'viewer', 'chat', 'commands', 'admin', 'story'];

let t: TestApp;
const sessions = new Map<Who, Session>();

beforeAll(async () => {
  t = await startApp({ bridge: { mock: true } }, { mockIntervalMs: 50 });
  sessions.set('anon', await csrfSession(t.url));
  for (const w of WHO.filter((x) => x !== 'anon')) sessions.set(w, await login(t.url, PERSONAS[w].username));
});
afterAll(async () => {
  await t.close();
});

async function send(id: RouteId, s: Session) {
  const r: RouteDef = ROUTES[id];
  const { path, body } = sampleInput(id);
  if (r.method === 'GET') return s.agent.get(path);
  const req = s.agent.post(path).set('Origin', ORIGIN).set('X-CSRF-Token', s.csrf);
  if (r.bodyKind === 'multipart')
    return req.field('options', '{"mode":"merge"}').attach('shard', Buffer.from('PK fake shard'), 'pack.mcshard');
  return req.send(body as object);
}

const steppedUp = new Set<Who>();
const cases: Array<[RouteId, Who]> = [];
for (const id of ROUTE_IDS) for (const w of WHO) cases.push([id, w]);

describe('role gating per route table', () => {
  it('covers every route', () => {
    expect(new Set(cases.map((c) => c[0])).size).toBe(ROUTE_IDS.length);
  });

  it.each(cases)('%s as %s', async (id, who) => {
    const route: RouteDef = ROUTES[id];
    const roles = who === 'anon' ? [] : PERSONAS[who].roles;
    const allowed = route.access === 'public' || (who !== 'anon' && hasRole(roles, route.access));
    if (id === 'live.stream') {
      // SSE: gating via a plain request; the body is never read for the allowed case.
      if (allowed) return;
    }
    if (allowed && OWN_FLOW.has(id)) return;
    if (allowed && route.stepUp && !steppedUp.has(who)) {
      // Admin settings writes need a fresh step-up first (admin-settings.test.ts covers the flow); it lasts 5 minutes.
      steppedUp.add(who);
      const s = sessions.get(who)!;
      const stale = await send(id, s);
      expect(stale.status).toBe(403);
      expect(stale.body.code).toBe('step_up_required');
      await s.agent.post('/api/auth/step-up').set('Origin', ORIGIN).set('X-CSRF-Token', s.csrf).send({ password: PASSWORD }).expect(200);
    }
    const res = await send(id, sessions.get(who)!);
    if (!allowed) {
      expect(res.status, JSON.stringify(res.body)).toBe(who === 'anon' ? 401 : 403);
      expect(res.body.code).toBe(who === 'anon' ? 'unauthorized' : 'forbidden');
      return;
    }
    expect(res.status, `${id}: ${JSON.stringify(res.body).slice(0, 400)}`).toBeGreaterThanOrEqual(200);
    expect(res.status).toBeLessThan(300);
    if (route.status) expect(res.status).toBe(route.status);
    if (route.responseKind === 'json') {
      expect(res.headers['content-type']).toMatch(/application\/json/);
      expect(route.response!.safeParse(res.body).success).toBe(true);
    }
  });

  it('the chat route refuses a user without a linked player', async () => {
    const s = await login(t.url, PERSONAS.chatNoPlayer.username);
    const r = await s.agent
      .post('/api/bots/maid_chan/chat')
      .set('Origin', ORIGIN)
      .set('X-CSRF-Token', s.csrf)
      .send({ text: 'hi', requestId: 'r-12345678' });
    expect(r.status).toBe(403);
  });

  it('unknown /api paths are JSON 404s', async () => {
    const r = await request(t.url).get('/api/nope');
    expect(r.status).toBe(404);
    expect(r.body).toEqual({ error: 'not found', code: 'not_found' });
  });
});

describe('CSRF and Origin', () => {
  const post = (s: Session) => s.agent.post('/api/admin/backup');

  it('accepts the session token with an allowed Origin', async () => {
    const s = sessions.get('admin')!;
    expect((await post(s).set('Origin', ORIGIN).set('X-CSRF-Token', s.csrf).send({})).status).toBe(200);
  });

  it.each([
    ['no token', (s: Session) => post(s).set('Origin', ORIGIN).send({})],
    ['wrong token', (s: Session) => post(s).set('Origin', ORIGIN).set('X-CSRF-Token', 'x'.repeat(43)).send({})],
    ['no Origin', (s: Session) => post(s).set('X-CSRF-Token', s.csrf).send({})],
    [
      'foreign Origin',
      (s: Session) => post(s).set('Origin', 'https://evil.example').set('X-CSRF-Token', s.csrf).send({}),
    ],
    [
      'another session token',
      (s: Session) => post(s).set('Origin', ORIGIN).set('X-CSRF-Token', sessions.get('viewer')!.csrf).send({}),
    ],
  ])('rejects %s with 403 csrf', async (_label, fn) => {
    const r = await fn(sessions.get('admin')!);
    expect(r.status).toBe(403);
    expect(r.body.code).toBe('csrf');
  });

  it('login needs the pre-session CSRF token too', async () => {
    const r = await request(t.url)
      .post('/api/auth/login')
      .set('Origin', ORIGIN)
      .send({ username: 'fox', password: 'x' });
    expect(r.status).toBe(403);
    expect(r.body.code).toBe('csrf');
  });

  it('GETs need no token', async () => {
    expect((await sessions.get('viewer')!.agent.get('/api/overview')).status).toBe(200);
  });

  it('request bodies are strict: unknown keys are a 400', async () => {
    const s = sessions.get('admin')!;
    const r = await s.agent
      .post('/api/admin/backup')
      .set('Origin', ORIGIN)
      .set('X-CSRF-Token', s.csrf)
      .send({ who: 'mallory' });
    expect(r.status).toBe(400);
    expect(r.body.code).toBe('bad_request');
  });
});
