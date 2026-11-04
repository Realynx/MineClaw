/**
 * Mock Nest: one resolver per route id in ROUTES. Resolvers return a JSON body (validated by the fixture tests against the
 * route's response schema), a Response (binary, errors), or throw MockError. Auth, CSRF, roles and chat rate limiting
 * behave like the real server so the UI's error paths can be exercised.
 */
import {
  hasRole,
  ROUTES,
  STEP_UP_MS,
  type CloudArrangement,
  type MemoryKindKey,
  type Me,
  type Role,
  type RouteId,
} from '@mineclaw/contracts';
import { blockSheetSvg, iso, nowIso } from './util';
import * as admin from './data/administration';
import * as live from './data/live';
import * as mem from './data/memory';
import * as misc from './data/misc';
import * as cloud from './data/cloud';
import * as raptor from './data/raptor';
import * as scripts from './data/scripts';
import { designer } from './data/designer';
import { seedStory, storySim } from './data/story';

export class MockError extends Error {
  constructor(
    readonly status: number,
    message: string,
    readonly code?: string,
    readonly extra: Record<string, unknown> = {},
    readonly headers: Record<string, string> = {},
  ) {
    super(message);
  }
}

export interface Ctx {
  params: Record<string, unknown>;
  query: Record<string, unknown>;
  body: unknown;
  file?: File | null;
}

type Resolver = (ctx: Ctx) => unknown;

// ---------- auth ----------

interface MockUser {
  username: string;
  password: string;
  player: string | null;
  roles: Role[];
  totp: string | null;
}

const USERS: MockUser[] = [
  {
    username: 'fox',
    password: 'fox',
    player: 'poofimafox',
    roles: ['viewer', 'chat', 'commands', 'admin'],
    totp: '123456',
  },
  { username: 'robin', password: 'robin', player: 'kiwi', roles: ['viewer', 'chat'], totp: null },
  { username: 'guest', password: 'guest', player: null, roles: ['viewer'], totp: null },
];

/**
 * First-run setup mode: `sessionStorage['mcx-mock-setup']` set (any value; the e2e sets '1') starts the mock with NO users,
 * like an empty users file. The accounts created through /setup are kept in that key as JSON, so they survive reloads.
 */
const SETUP_KEY = 'mcx-mock-setup';
let memorySetup: MockUser[] | null = null;

function setupStore(): MockUser[] | null {
  try {
    const raw = sessionStorage.getItem(SETUP_KEY);
    if (raw === null) return null;
    const v = JSON.parse(raw) as unknown;
    return Array.isArray(v) ? (v as MockUser[]) : [];
  } catch {
    return memorySetup;
  }
}

function saveSetupUsers(list: MockUser[]): void {
  memorySetup = list;
  try {
    sessionStorage.setItem(SETUP_KEY, JSON.stringify(list));
  } catch {
    /* node test env */
  }
}

/** For tests: start (true) or leave (false) first-run setup mode. */
export function mockSetupMode(on: boolean): void {
  memorySetup = on ? [] : null;
  try {
    if (on) sessionStorage.setItem(SETUP_KEY, '[]');
    else sessionStorage.removeItem(SETUP_KEY);
  } catch {
    /* node test env */
  }
}

function users(): MockUser[] {
  return setupStore() ?? USERS;
}

/** Persists a changed `users()` list (setup-mode users live in sessionStorage; the built-in ones in memory). */
function persistUsers(list: MockUser[]): void {
  if (list !== USERS) saveSetupUsers(list);
}

const SESSION_KEY = 'mcx-mock-session';
let memorySession: { username: string; created: number } | null = null;
let csrf = randomToken();

function randomToken(): string {
  return Array.from({ length: 32 }, () => Math.floor(Math.random() * 16).toString(16)).join('');
}

function readSession(): { username: string; created: number } | null {
  try {
    const raw = sessionStorage.getItem(SESSION_KEY);
    return raw ? (JSON.parse(raw) as { username: string; created: number }) : null;
  } catch {
    return memorySession;
  }
}

function writeSession(s: { username: string; created: number } | null): void {
  memorySession = s;
  try {
    if (s) sessionStorage.setItem(SESSION_KEY, JSON.stringify(s));
    else sessionStorage.removeItem(SESSION_KEY);
  } catch {
    /* node test env */
  }
}

/** For tests (and a dev convenience): sign in as a user without the form. */
export function mockSignIn(username: string | null): void {
  writeSession(username ? { username, created: Date.now() } : null);
  writeStepUp(null);
}

// Step-up (re-entered TOTP or password) for the Administration writes: valid for STEP_UP_MS, per session.
const STEP_UP_KEY = 'mcx-mock-stepup';
let memoryStepUp: number | null = null;

function readStepUp(): number | null {
  try {
    const raw = sessionStorage.getItem(STEP_UP_KEY);
    return raw ? Number(raw) : null;
  } catch {
    return memoryStepUp;
  }
}

function writeStepUp(until: number | null): void {
  memoryStepUp = until;
  try {
    if (until) sessionStorage.setItem(STEP_UP_KEY, String(until));
    else sessionStorage.removeItem(STEP_UP_KEY);
  } catch {
    /* node test env */
  }
}

/** For tests: grant (or drop) a fresh step-up without the dialog. */
export function mockStepUp(on: boolean): void {
  writeStepUp(on ? Date.now() + STEP_UP_MS : null);
}

function stepUpFresh(): boolean {
  const until = readStepUp();
  return until !== null && until > Date.now();
}

function me(): Me | null {
  const s = readSession();
  const u = s && users().find((x) => x.username === s.username);
  if (!s || !u) return null;
  return {
    username: u.username,
    player: u.player,
    roles: u.roles,
    totpEnrolled: !!u.totp,
    sessionCreatedAt: iso(s.created),
    idleExpiresAt: iso(Date.now() + 12 * 3600_000),
    absoluteExpiresAt: iso(s.created + 7 * 86400_000),
    stepUpUntil: stepUpFresh() ? iso(readStepUp()!) : null,
  };
}

export function currentUser(): Me | null {
  return me();
}

export function checkAccess(id: RouteId, csrfHeader: string | null, method: string): void {
  const route = ROUTES[id];
  if (method !== 'GET' && csrfHeader !== csrf) throw new MockError(403, 'Security token missing or expired', 'csrf');
  if (route.access === 'public') return;
  const user = me();
  if (!user) throw new MockError(401, 'Not signed in', 'unauthorized');
  if (!hasRole(user.roles, route.access)) throw new MockError(403, 'You do not have access to this', 'forbidden');
  if (route.stepUp && !stepUpFresh()) throw new MockError(403, 'confirm it is you first (step-up)', 'step_up_required');
}

function adminCall<T>(f: () => T): T {
  try {
    return f();
  } catch (e) {
    if (e instanceof admin.AdminMockError)
      throw new MockError(e.status, e.message, e.code, e.details ? { details: e.details } : {});
    throw e;
  }
}

function who(): string {
  return `${me()?.username ?? 'unknown'} (web)`;
}

function checkNode(params: Record<string, unknown>): void {
  if (params.node !== admin.NODE.id) throw new MockError(404, `no node ${String(params.node)}`, 'not_found');
}

const chatTimes: number[] = [];

// ---------- resolvers ----------

const num = (v: unknown) => Number(v);

export const resolvers: Partial<Record<RouteId, Resolver>> = {
  health: () => ({ ok: true, version: 'mock-0.1.0', bridge: 'up' }),
  'auth.csrf': () => ({ csrfToken: csrf }),
  'setup.status': () => ({ needed: users().length === 0, allowedHere: true }),
  'setup.create': ({ body }) => {
    const b = body as { username: string; password: string; player?: string | null; roles: Role[] };
    if (users().length > 0) throw new MockError(409, 'setup is already complete', 'conflict');
    const roles = [...new Set(b.roles)].sort();
    saveSetupUsers([{ username: b.username, password: b.password, player: b.player ?? null, roles, totp: null }]);
    writeSession({ username: b.username, created: Date.now() });
    csrf = randomToken();
    return { user: me(), csrfToken: csrf };
  },
  'auth.login': ({ body }) => {
    const b = body as { username: string; password: string; totp?: string };
    if (users().length === 0) throw new MockError(409, 'first-run setup is not complete; open /setup', 'conflict');
    const u = users().find((x) => x.username === b.username && x.password === b.password);
    if (!u) throw new MockError(401, 'Invalid credentials', 'unauthorized');
    if (u.totp && !b.totp)
      throw new MockError(401, 'Enter the code from your authenticator', 'unauthorized', {
        details: [{ path: 'totp', message: 'required' }],
      });
    if (u.totp && b.totp !== u.totp) throw new MockError(401, 'Invalid credentials', 'unauthorized');
    writeSession({ username: u.username, created: Date.now() });
    writeStepUp(null);
    csrf = randomToken();
    return { user: me(), csrfToken: csrf };
  },
  'auth.logout': () => {
    writeSession(null);
    writeStepUp(null);
    csrf = randomToken();
    return { ok: true };
  },
  'auth.me': () => me(),
  'auth.totpEnroll': ({ body }) => {
    const u = users().find((x) => x.username === me()?.username);
    if (!u || u.password !== (body as { password: string }).password)
      throw new MockError(401, 'Invalid credentials', 'unauthorized');
    return {
      otpauthUrl: `otpauth://totp/MineClaw:${u.username}?secret=JBSWY3DPEHPK3PXP&issuer=MineClaw`,
      secret: 'JBSWY3DPEHPK3PXP',
      expiresAt: iso(Date.now() + 600_000),
    };
  },
  'auth.totpVerify': ({ body }) => {
    const list = users();
    const u = list.find((x) => x.username === me()?.username);
    if (!u) throw new MockError(401, 'Not signed in', 'unauthorized');
    if ((body as { code: string }).code !== '123456')
      throw new MockError(400, 'That code did not match', 'bad_request');
    u.totp = '123456';
    persistUsers(list);
    return { ok: true, totpEnrolled: true };
  },
  'auth.stepUp': ({ body }) => {
    const b = body as { code?: string; password?: string };
    const u = users().find((x) => x.username === me()?.username);
    if (!u) throw new MockError(401, 'Not signed in', 'unauthorized');
    const ok = u.totp
      ? b.code !== undefined && b.code === u.totp
      : b.password !== undefined && b.password === u.password;
    if (!ok) throw new MockError(401, 'invalid credentials', 'unauthorized');
    mockStepUp(true);
    return { ok: true, stepUpUntil: iso(readStepUp()!) };
  },
  'audit.list': ({ query }) => {
    let items = misc.webAudit;
    if (query.user) items = items.filter((e) => e.username === query.user);
    if (query.kind) items = items.filter((e) => e.kind === query.kind);
    if (query.ok) items = items.filter((e) => String(e.ok) === query.ok);
    const offset = num(query.offset);
    const limit = num(query.limit);
    return { total: items.length, offset, limit, items: items.slice(offset, offset + limit) };
  },

  overview: () => misc.overview(),
  'memory.kinds': () => mem.kinds(),
  'memory.list': ({ params, query }) => mem.list(params.kind as MemoryKindKey, query as never),
  'memory.get': ({ params }) => {
    const d = mem.get(params.kind as MemoryKindKey, String(params.id));
    if (!d) throw new MockError(404, 'Not found', 'not_found');
    return d;
  },
  search: ({ query }) => {
    const q = String(query.q);
    const hits = raptor.searchHits(q, num(query.k));
    return {
      query: q,
      mode: query.mode === 'lexical' ? 'lexical' : 'hybrid',
      modeNote: query.mode === 'lexical' ? 'lexical: forced (FTS only)' : 'hybrid: FTS + vectors + RAPTOR',
      embedded: query.mode !== 'lexical',
      elapsedMs: 38 + q.length,
      hits,
      treeHits: hits.slice(0, 5).flatMap((h, i) => [
        {
          list: 'collapsed' as const,
          index: h.raptorLeaf ?? 0,
          layer: 0,
          score: 0.81 - i * 0.03,
          preview: h.snippet.slice(0, 80),
        },
        {
          list: 'traversal' as const,
          index: h.raptorPath[1]?.index ?? 0,
          layer: h.raptorPath[1]?.layer ?? 2,
          score: 0.74 - i * 0.04,
          preview: h.raptorPath[1]?.label ?? '',
        },
      ]),
    };
  },
  'raptor.graph': ({ query }) =>
    raptor.graph(
      query.layout as never,
      query.colorBy as never,
      query.focus === undefined ? undefined : num(query.focus),
    ),
  'raptor.leaves': ({ params }) => raptor.leavesResponse(num(params.id)),
  'raptor.node': ({ params }) => {
    const d = raptor.nodeDetail(num(params.id)) ?? cloud.extraDetail(num(params.id));
    if (!d) throw new MockError(404, 'No such node', 'not_found');
    return d;
  },
  personality: ({ query }) => misc.personality(String(query.scope)),
  'shards.packs': () => misc.packs(),
  'shards.exportPreview': ({ body }) => misc.exportPreview((body as { name: string }).name),
  'shards.export': ({ body }) => {
    const name = (body as { name: string }).name.replace(/[^a-z0-9-]+/gi, '-').toLowerCase();
    const bytes = new Uint8Array([0x50, 0x4b, 0x05, 0x06, ...new Array<number>(18).fill(0)]);
    return new Response(bytes, {
      headers: {
        'Content-Type': 'application/zip',
        'Content-Disposition': `attachment; filename="${name}-20261102-1015.mcshard"`,
      },
    });
  },
  'shards.importPreview': () => misc.importPreview(),
  'scripts.list': ({ query }) => ({ at: nowIso(), source: query.source, scripts: scripts.library() }),
  'scripts.diff': ({ params, query }) =>
    scripts.diff(String(params.name), num(query.a), num(query.b), num(query.context)),
  'scripts.lint': ({ params, query }) => {
    const l = scripts.lint(String(params.name), query.v === undefined ? undefined : num(query.v));
    if (!l) throw new MockError(404, 'No such script', 'not_found');
    return l;
  },
  'scripts.get': ({ params, query }) => {
    const d = scripts.detail(String(params.name), query.v === undefined ? undefined : num(query.v));
    if (!d) throw new MockError(404, 'No such script', 'not_found');
    return d;
  },
  'designs.list': ({ query }) => misc.designList(query.q as string | undefined),
  'designs.picture': ({ params }) =>
    new Response(blockSheetSvg(num(params.id), 640, 400, `design #${String(params.id)}`), {
      headers: { 'Content-Type': 'image/svg+xml' },
    }),
  'designs.diff': ({ params, query }) => {
    const d = misc.designDetail(num(params.id));
    return misc.designDiff(
      num(params.id),
      (query.a as string | undefined) ?? d?.defaultDiff.a ?? '1',
      (query.b as string | undefined) ?? d?.defaultDiff.b ?? '2',
    );
  },
  'designs.get': ({ params }) => {
    const d = misc.designDetail(num(params.id));
    if (!d) throw new MockError(404, 'No such design', 'not_found');
    return d;
  },

  // The Design Studio: the shared fake designer (a little house, a door, a roof) over an in-memory canvas.
  'designer.models': () => designer.models(),
  'designer.create': ({ body }) =>
    designer.create(body as Parameters<typeof designer.create>[0], me()?.player ?? me()?.username ?? 'poofimafox'),
  'designer.version': ({ params, body }) =>
    designer.version(num(params.id), body as Parameters<typeof designer.version>[1]),
  'designer.get': ({ params }) => designer.get(num(params.id)),
  'designer.message': ({ params, body }) =>
    designer.message(num(params.id), body as Parameters<typeof designer.message>[1]),
  'designer.canvas': ({ params, body }) =>
    designer.canvas(num(params.id), body as Parameters<typeof designer.canvas>[1]),
  'designer.save': ({ params, body }) => {
    try {
      return designer.save(
        num(params.id),
        body as Parameters<typeof designer.save>[1],
        me()?.player ?? me()?.username ?? 'poofimafox',
      );
    } catch (e) {
      throw new MockError(400, e instanceof Error ? e.message : 'save failed', 'bad_request');
    }
  },

  'memoryActivity.heat': ({ query }) => live.memoryHeat(query.brain as string | undefined, num(query.windowS), raptor.STATE_KEY),
  'memoryActivity.cloud': ({ query }) => cloud.memoryCloud((query.arrangement as CloudArrangement | undefined) ?? 'mds'),
  'memoryActivity.cloudDistance': ({ query }) => {
    const d = cloud.cloudDistance(num(query.a), num(query.b));
    if (!d) throw new MockError(404, 'No such memory', 'not_found');
    return d;
  },
  'memoryActivity.list': ({ query }) => live.memoryActivitySince(num(query.since), query.brain as string | undefined),

  'admin.schema': () => misc.editSchema,
  'admin.memoryEdit': ({ body }) => {
    const b = body as { op: string; kind: string; id?: string; fields: Record<string, string | null> };
    if (b.op !== 'add' && b.id === '999')
      throw new MockError(400, 'refused', undefined, { ok: false, message: 'refused: id not found' });
    return {
      ok: true,
      message: `${b.op === 'add' ? 'Added' : b.op === 'delete' ? 'Deleted' : 'Updated'} ${b.kind}${b.id ? ` ${b.id}` : ''}`,
      id: b.id ?? '901',
      before: b.op === 'add' ? null : '{"text":"previous value"}',
      after: b.op === 'delete' ? null : JSON.stringify(b.fields),
      reembedded: b.op !== 'delete',
    };
  },
  'admin.rebuildEstimate': ({ body }) => {
    const b = body as { scope: string; kind?: string };
    return b.kind === 'lesson'
      ? {
          summaries: 0,
          inputTokens: 0,
          outputTokens: 0,
          embeddingTokens: 0,
          usd: 0,
          model: 'claude-haiku-5-5',
          note: 'nothing is dirty',
        }
      : {
          summaries: 6,
          inputTokens: 18400,
          outputTokens: 1300,
          embeddingTokens: 900,
          usd: 0.034,
          model: 'claude-haiku-5-5',
          note: '6 summaries in 2 sections',
        };
  },
  'admin.rebuildStart': () => {
    const id = `job-${Date.now().toString(36)}`;
    misc.startJob(id);
    return { jobId: id };
  },
  'admin.rebuildCancel': ({ params }) => {
    misc.cancelJob(String(params.jobId));
    return { ok: true, message: null };
  },
  'admin.rebuildJob': ({ params }) => misc.job(String(params.jobId)),
  'admin.backup': () => ({ path: `memory-backup-${new Date().toISOString().replace(/\D/g, '').slice(0, 14)}.db` }),
  'admin.audit': ({ query }) => ({ entries: misc.adminAudit.slice(0, num(query.max)) }),
  'admin.compact': ({ body }) => misc.compaction((body as { dryRun?: boolean }).dryRun !== false),
  'admin.shardImport': () => {
    misc.startJob('job-import-1');
    return misc.importResult();
  },
  'admin.nodes': () => admin.nodes(),
  'admin.settings': ({ params }) => {
    checkNode(params);
    return admin.settings();
  },
  'admin.settingsSave': ({ params, body }) => {
    checkNode(params);
    const b = body as { changes: Parameters<typeof admin.save>[0]; reason: string };
    return adminCall(() => admin.save(b.changes, b.reason, who()));
  },
  'admin.settingsTest': ({ params, body }) => {
    checkNode(params);
    return admin.test(body as Parameters<typeof admin.test>[0], who());
  },
  'admin.ollamaModels': ({ params, query }) => {
    checkNode(params);
    return admin.ollamaModels(query.endpoint as string | undefined);
  },
  'admin.restartSafety': ({ params }) => {
    checkNode(params);
    return admin.safety();
  },
  'admin.restart': ({ params, body }) => {
    checkNode(params);
    return adminCall(() => admin.restart(body as { reason: string; force?: boolean }, who()));
  },
  'admin.settingsAudit': ({ params, query }) => {
    checkNode(params);
    return admin.audit(num(query.max));
  },
  'groom.preview': () => {
    throw new MockError(501, 'Groom preview is not available on this server yet', 'upstream_error');
  },

  'bots.list': () => ({
    at: nowIso(),
    bots: live.BOTS.map((b) => ({
      name: b.name,
      short: b.short,
      main: b.main,
      dim: b.dim,
      x: b.pos[0],
      y: b.pos[1],
      z: b.pos[2],
      hp: b.hp,
      food: b.food,
      job: b.task,
      brain: b.brain,
      ageSeconds: 0.4,
      claims: b.claims,
    })),
  }),
  'hive.tree': () => live.hiveTree(),
  status: ({ query }) => live.statusResponse(query.bot as string | undefined),
  events: ({ query }) => live.eventsSince(num(query.since)),
  locate: ({ query }) => {
    const q = String(query.q).toLowerCase();
    const b = live.BOTS.find((x) => x.name === q || x.short === q);
    if (b) return { name: b.name, dim: b.dim, x: b.pos[0], y: b.pos[1], z: b.pos[2] };
    if (q === 'home') return { name: 'home', dim: 0, x: 0, y: 64, z: 0 };
    throw new MockError(404, `no place called ${q}`, 'not_found');
  },
  'world.index': ({ query }) => live.worldIndex(num(query.dim), num(query.x), num(query.z)),
  'world.chunks': ({ query }) => {
    const cols = String(query.cols)
      .split(';')
      .map((p) => p.split(',').map(Number) as [number, number]);
    return new Response(live.mcwc(num(query.dim), cols), { headers: { 'Content-Type': 'application/octet-stream' } });
  },
  'world.entities': ({ query }) => live.entitiesSince(query.since === undefined ? undefined : num(query.since)),
  'world.overlays': ({ query }) => live.overlays(num(query.dim)),
  'world.blockEntities': () => ({ columns: [] }),
  'world.container': ({ query }) => live.container(num(query.dim), num(query.x), num(query.y), num(query.z)),
  'world.containers': ({ query }) =>
    live.containers(
      num(query.dim),
      {
        x0: num(query.x0),
        z0: num(query.z0),
        x1: num(query.x1),
        z1: num(query.z1),
        ...(query.y0 === undefined ? {} : { y0: num(query.y0) }),
        ...(query.y1 === undefined ? {} : { y1: num(query.y1) }),
      },
      query.limit === undefined ? undefined : num(query.limit),
    ),
  'world.blocks': ({ query }) => ({
    at: Date.now(),
    rev: Math.max(1, num(query.since)),
    full: num(query.since) === 0,
    more: false,
    blocks: {},
    columns: [],
  }),
  'bots.tile': () => {
    throw new MockError(410, 'Server tiles are not used by this client', 'not_found');
  },
  'bots.now': ({ params }) => {
    const b = live.findBot(String(params.bot));
    if (!b) throw new MockError(404, 'No such bot', 'not_found');
    return live.botNow(b);
  },
  'bots.inventory': ({ params }) => {
    const b = live.findBot(String(params.bot));
    if (!b) throw new MockError(404, 'No such body', 'not_found');
    return live.bodyInventory(b);
  },
  'bots.chat': ({ params, body }) => {
    const b = live.findBot(String(params.bot));
    if (!b) throw new MockError(404, 'No such bot', 'not_found');
    const user = me();
    if (!user?.player) throw new MockError(403, 'No linked player', 'forbidden');
    const now = Date.now();
    while (chatTimes.length && now - chatTimes[0]! > 10_000) chatTimes.shift();
    if (chatTimes.length >= 5) {
      const wait = 10_000 - (now - chatTimes[0]!);
      throw new MockError(
        429,
        'Slow down a little',
        'rate_limited',
        { retryAfterMs: wait },
        { 'Retry-After': String(Math.ceil(wait / 1000)) },
      );
    }
    chatTimes.push(now);
    const req = body as { text: string; requestId: string };
    const line = live.acceptChat(b, req.text, req.requestId, user.player);
    // Contract: `id` echoes the request id; `feedSeq` is the viewer line's feed seq.
    return { accepted: true, id: req.requestId, feedSeq: line.seq, note: null };
  },
  feed: ({ query }) => live.feedSince(num(query.since)),
  'blueprints.list': () => ({
    designs: misc
      .designList()
      .items.map((d) => ({
        id: d.id,
        name: d.name,
        status: d.status,
        requester: d.requester,
        rev: d.rev,
        updated: d.updated,
      })),
  }),
  'blueprints.get': ({ params }) => ({
    id: num(params.id),
    bill: { cobblestone: 240, glass: 12 },
    blocks: mockDesignBlocks(),
  }),
  'buildplans.get': ({ params }) => ({ id: num(params.id), parts: [] }),
  'bodies.scripts': () => live.bodyScripts(),
  'bodies.script': ({ params, query }) => live.bodyScript(String(params.body), query.code === true),
  'bodies.controller': ({ params }) => live.controller(String(params.body)),
  'vision.render': ({ params }) =>
    new Response(blockSheetSvg(String(params.body).length * 3, 1024, 640, 'render'), {
      headers: { 'Content-Type': 'image/svg+xml' },
    }),
  'vision.png': ({ params }) =>
    new Response(blockSheetSvg(num(params.id), 1024, 640, `sheet #${String(params.id)}`), {
      headers: { 'Content-Type': 'image/svg+xml', 'X-Tokens': '870', 'X-Width': '1024', 'X-Height': '640' },
    }),
  'vision.sheet': ({ params }) =>
    live.visionList(String(params.body)).sheets.find((s) => s.id === num(params.id)) ??
    live.visionList(String(params.body)).sheets[0],
  'vision.list': ({ params }) => live.visionList(String(params.body)),
  'brains.context': ({ params, query }) => live.brainContext(String(params.brain), num(query.since)),
  'brains.turn': ({ params }) => live.brainTurn(String(params.brain), num(params.n)),
  ...storyResolvers(),
};

/** A 5×5 cobblestone hut, 3 high with a doorway and a plank roof (the blocks a design page's "Build in Story Mode" places). */
function mockDesignBlocks(): Array<[number, number, number, number, number]> {
  const out: Array<[number, number, number, number, number]> = [];
  for (let x = 0; x < 5; x++)
    for (let z = 0; z < 5; z++) {
      const edge = x === 0 || x === 4 || z === 0 || z === 4;
      for (let y = 0; y < 3; y++) if (edge && !(x === 2 && z === 0 && y < 2)) out.push([x, y, z, 4, 0]);
      out.push([x, 3, z, 5, 0]);
    }
  return out;
}

/** Story Mode: the shared mock director (mocks/data/story.ts); its errors become API errors. */
function storyResolvers(): Partial<Record<RouteId, Resolver>> {
  const run = (f: () => unknown) => {
    seedStory();
    try {
      return f();
    } catch (e) {
      const status = (e as { status?: number }).status ?? 500;
      throw new MockError(
        status,
        e instanceof Error ? e.message : 'story error',
        status === 404 ? 'not_found' : 'bad_request',
      );
    }
  };
  const id = (c: Ctx) => String(c.params.id);
  const b = (c: Ctx) => c.body as never;
  return {
    'story.list': () => run(() => storySim.list()),
    'story.recipes': ({ query }) => run(() => storySim.recipes(query.workbench as string | undefined)),
    'story.create': (c) => run(() => storySim.create(b(c))),
    'story.get': (c) => run(() => storySim.state(id(c), c.query.since === undefined ? undefined : num(c.query.since))),
    'story.release': (c) => run(() => storySim.release(id(c))),
    'story.resume': (c) => run(() => storySim.resume(id(c))),
    'story.online': (c) => run(() => storySim.online(id(c), b(c))),
    'story.pawns': (c) => run(() => storySim.pawns(id(c), b(c))),
    'story.directives.list': () => run(() => ({ directives: storySim.directives() })),
    'story.directive': (c) => run(() => storySim.directiveDetail(id(c))),
    'story.beds': (c) => run(() => storySim.beds(id(c), b(c))),
    'story.time': (c) => run(() => storySim.time(b(c))),
    'story.home': (c) => run(() => storySim.home(id(c), b(c))),
    'story.pause': (c) => run(() => storySim.pause(id(c), (c.body as { paused: boolean }).paused)),
    'story.priorities': (c) => run(() => storySim.priorities(id(c), b(c))),
    'story.schedule': (c) => run(() => storySim.schedule(id(c), b(c))),
    'story.draft': (c) => run(() => storySim.draft(id(c), b(c))),
    'story.orders': (c) => run(() => storySim.orders(id(c), b(c))),
    'story.zones': (c) => run(() => storySim.zones(id(c), b(c))),
    'story.designations': (c) => run(() => storySim.designations(id(c), b(c))),
    'story.builds': (c) => run(() => storySim.builds(id(c), b(c))),
    'story.storage': (c) => run(() => storySim.storage(id(c), b(c))),
    'story.bills': (c) => run(() => storySim.bills(id(c), b(c))),
    'story.workbenches': (c) => run(() => storySim.workbenches(id(c), b(c))),
    'story.options': (c) => run(() => storySim.options(id(c), b(c))),
    'story.architect': (c) => run(() => storySim.architect(id(c))),
    'story.directives': (c) => run(() => storySim.directive(b(c))),
  };
}

/** Sample calls for the fixture tests: route id → params + query (+ body) that exercise the resolver. */
export const SAMPLE_CALLS: Partial<Record<RouteId, Ctx>> = {
  'memory.list': { params: { kind: 'facts' }, query: {}, body: null },
  'memory.get': { params: { kind: 'claims', id: '3' }, query: {}, body: null },
  search: { params: {}, query: { q: 'melons' }, body: null },
  'raptor.node': { params: { id: 481 }, query: {}, body: null },
  'raptor.leaves': { params: { id: 481 }, query: {}, body: null },
  'scripts.get': { params: { name: 'cobble_loop' }, query: {}, body: null },
  'scripts.diff': { params: { name: 'cobble_loop' }, query: { a: 6, b: 7 }, body: null },
  'scripts.lint': { params: { name: 'cobble_loop' }, query: {}, body: null },
  'designs.get': { params: { id: 12 }, query: {}, body: null },
  'designs.diff': { params: { id: 12 }, query: {}, body: null },
  'designer.get': { params: { id: 1 }, query: {}, body: null },
  'designer.message': { params: { id: 1 }, query: {}, body: { text: 'build a little house', canvas: { rev: 0 } } },
  'designer.canvas': {
    params: { id: 1 },
    query: {},
    body: { rev: 0, ops: [{ kind: 'place', cells: [[1, 0, 1, 4, 0]] }] },
  },
  'designer.save': { params: { id: 1 }, query: {}, body: { name: 'mock hut' } },
  'designer.version': { params: { id: 1 }, query: {}, body: { version: '1.12.2' } },
  'bots.now': { params: { bot: 'maid_chan' }, query: {}, body: null },
  'bots.inventory': { params: { bot: 'maid_chan_67' }, query: {}, body: null },
  'bodies.script': { params: { body: 'maid_chan' }, query: { code: '1' }, body: null },
  'bodies.controller': { params: { body: 'maid_chan_68' }, query: {}, body: null },
  'vision.list': { params: { body: 'maid_chan' }, query: {}, body: null },
  'vision.sheet': { params: { body: 'maid_chan', id: 59 }, query: {}, body: null },
  'brains.context': { params: { brain: 'main' }, query: {}, body: null },
  'brains.turn': { params: { brain: 'main', n: 104 }, query: {}, body: null },
  'world.index': { params: {}, query: { x: 4, z: 6 }, body: null },
  'world.blockEntities': { params: {}, query: { dim: 0, cols: '0,0;1,0' }, body: null },
  'world.container': { params: {}, query: { dim: 0, x: 3, y: 74, z: -4 }, body: null },
  'world.containers': { params: {}, query: { dim: 0, x0: -16, z0: -40, x1: 32, z1: 8 }, body: null },
  'world.overlays': { params: {}, query: {}, body: null },
  'admin.rebuildJob': { params: { jobId: 'job-x' }, query: {}, body: null },
  'admin.memoryEdit': { params: {}, query: {}, body: { op: 'update', kind: 'fact', id: '3', fields: { text: 'x' } } },
  'admin.rebuildEstimate': { params: {}, query: {}, body: { scope: 'kind', kind: 'fact' } },
  'admin.compact': { params: {}, query: {}, body: { dryRun: true } },
  'auth.stepUp': { params: {}, query: {}, body: { code: '123456' } },
  'admin.settings': { params: { node: 'local' }, query: {}, body: null },
  'admin.settingsSave': {
    params: { node: 'local' },
    query: {},
    body: { changes: [{ key: 'Supervisor:Viewer:PerMinute', value: 10 }], reason: 'fixture test' },
  },
  'admin.settingsTest': {
    params: { node: 'local' },
    query: {},
    body: { kind: 'ollama', endpoint: 'http://10.1.2.50:11434' },
  },
  'admin.ollamaModels': { params: { node: 'local' }, query: {}, body: null },
  'admin.restartSafety': { params: { node: 'local' }, query: {}, body: null },
  'admin.restart': { params: { node: 'local' }, query: {}, body: { reason: 'fixture test' } },
  'admin.settingsAudit': { params: { node: 'local' }, query: {}, body: null },
  'shards.exportPreview': {
    params: {},
    query: {},
    body: { name: 'kiwi', selection: { mode: 'person', people: ['kiwi'] } },
  },
  locate: { params: {}, query: { q: 'home' }, body: null },
  'blueprints.get': { params: { id: 12 }, query: {}, body: null },
  'buildplans.get': { params: { id: 12 }, query: {}, body: null },
  'story.get': { params: { id: 'story-1' }, query: {}, body: null },
  'story.recipes': { params: {}, query: { workbench: 'crafting_table' }, body: null },
  'story.architect': { params: { id: 'story-1' }, query: {}, body: null },
};
