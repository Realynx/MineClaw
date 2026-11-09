/**
 * The mock server's fixtures must satisfy the contracts: every JSON route resolver is called (with its sample params, or none)
 * and its body parsed with the route's response schema; every SSE frame the simulation emits is parsed with SSE_EVENTS.
 */
import { LoginResponse, Me, PawnControlSession, ROUTES, SetupRequest, SetupStatus, SSE_EVENTS, type RouteDef, type RouteId, type SseEventName } from '@mineclaw/contracts';
import { beforeEach, describe, expect, it } from 'vitest';
import { mockSetupMode, mockSignIn, resolvers, SAMPLE_CALLS, type Ctx } from '@/mocks/api';
import { tick } from '@/mocks/data/live';
import { seedStory, storySim } from '@/mocks/data/story';

// Answer errors by design (phase 2 / unused; setup.create is 409 once users exist, so it has its own test below).
const SKIP = new Set<RouteId>(['groom.preview', 'bots.tile', 'setup.create', 'story.directive']);

/** Bodies and params for the write routes (the shared SAMPLE_CALLS cover the reads). */
const WRITES: Partial<Record<RouteId, Ctx>> = {
  'auth.login': { params: {}, query: {}, body: { username: 'robin', password: 'robin' } },
  'auth.totpEnroll': { params: {}, query: {}, body: { password: 'fox' } },
  'auth.totpVerify': { params: {}, query: {}, body: { code: '123456' } },
  'admin.rebuildStart': { params: {}, query: {}, body: { scope: 'kind', kind: 'fact' } },
  'admin.rebuildCancel': { params: { jobId: 'job-1' }, query: {}, body: {} },
  'bots.chat': { params: { bot: 'maid_chan' }, query: {}, body: { text: 'hi', requestId: 'abcdefgh1234' } },
  ...storyWrites(),
};

function bedPos(): { x: number; y: number; z: number } {
  seedStory();
  const p = storySim.state('story-1').beds?.[0]?.pos ?? [0, 0, 0];
  return { x: p[0], y: p[1], z: p[2] };
}

function benchPos(): number[] {
  seedStory();
  return storySim.state('story-1').workbenches?.[0]?.pos ?? [0, 0, 0];
}

function controlSample(id: RouteId): Ctx | undefined {
  if (!id.startsWith('bodies.control')) return;
  const params = { body: 'maid_chan_70' }, query = {};
  const acquire = { params, query, body: { requestId: 'fixture-control' } };
  if (id === 'bodies.controlAcquire') return acquire;
  const { session } = PawnControlSession.parse(resolvers['bodies.controlAcquire']!(acquire));
  const body = id === 'bodies.controlInput'
    ? { session, seq: 1, forward: 0, strafe: 0, yaw: 180, pitch: 0, jump: false, sneak: false, sprint: false }
    : id === 'bodies.controlAction' ? { session, seq: 1, kind: 'inventory' } : { session };
  return { params, query, body };
}

/** Story Mode writes against the seeded mock colony (story-1, first pawn maid_s1_dara). */
function storyWrites(): Partial<Record<RouteId, Ctx>> {
  const p = { id: 'story-1' };
  const pawns = ['maid_s1_dara'];
  const w = (body: unknown): Ctx => ({ params: p, query: {}, body });
  return {
    'story.create': { params: {}, query: {}, body: { name: 'Second', pawns: 2 } },
    'story.release': w({}),
    'story.pause': w({ paused: false }),
    'story.home': w({ home: { x: 4, y: 64, z: -3 } }),
    'story.time': { params: {}, query: {}, body: { time: 6000 } },
    'story.timeRange': { params: {}, query: {}, body: { range: null } },
    'story.beds': w({ op: 'assign', bed: bedPos(), pawn: 'maid_s1_dara' }),
    'story.priorities': w({ pawns, work: { chop: 1 } }),
    'story.roles': w({ op: 'update', role: { id: 'farmer', priorities: { farm: 2 } } }),
    'story.schedule': w({ pawns, hours: { '0': 'work' } }),
    'story.draft': w({ pawns, drafted: true }),
    'story.orders': w({ pawns, order: { kind: 'move', target: { x: 0, y: 70, z: 0 } } }),
    'story.zones': w({ op: 'add', kind: 'farm', area: [8, 66, -6, 13, 66, -1] }),
    'story.designations': w({ op: 'add', kind: 'chop', area: [-10, 50, -14, 8, 90, 8] }),
    'story.builds': w({ op: 'add', items: [{ kind: 'wall', area: [0, 70, 0, 4, 70, 4], material: 'cobblestone', height: 3 }] }),
    'story.storage': w({ op: 'assign_area', area: [1, 60, -5, 7, 80, -3] }),
    'story.bills': w({ op: 'add', bench: benchPos(), bill: { recipe: 'sticks', mode: 'until', target: 64 } }),
    'story.workbenches': w({ op: 'designate', pos: benchPos() }),
    'story.options': w({ autoPlaceBenches: true }),
    'story.policies': w({ op: 'set', policy: { name: 'Miners', tools: ['pickaxe'], armor: 'none' } }),
    'story.materialRules': w({ op: 'set', rule: { material: 'iron_ore', tier: 'iron', enchant: 'fortune' } }),
    'story.gear': w({ op: 'arm', pawns }),
    'story.pawns': w({ op: 'add', count: 1 }),
    'story.resume': w({}),
    'story.online': w({ pawns: null }),
    'story.directives': { params: {}, query: {}, body: { bots: ['maid_chan'], directive: { kind: 'text', text: 'hi' }, requestId: 'r-12345678' } },
  };
}

describe('mock fixtures satisfy the contracts', () => {
  beforeEach(() => mockSignIn('fox'));
  const ids = (Object.keys(ROUTES) as RouteId[]).filter((id) => {
    const r: RouteDef = ROUTES[id];
    return r.responseKind === 'json' && !!resolvers[id] && !SKIP.has(id);
  });

  it('covers every JSON route', () => {
    const missing = (Object.keys(ROUTES) as RouteId[]).filter((id) => ROUTES[id].responseKind === 'json' && !resolvers[id]);
    expect(missing).toEqual([]);
  });

  it.each(ids)('%s', (id) => {
    const r: RouteDef = ROUTES[id];
    const sample: Ctx = controlSample(id) ?? WRITES[id] ?? SAMPLE_CALLS[id] ?? { params: {}, query: {}, body: {} };
    const ctx: Ctx = {
      params: r.params ? (r.params.parse(sample.params) as Record<string, unknown>) : {},
      query: r.query ? (r.query.parse(sample.query) as Record<string, unknown>) : {},
      body: r.body ? r.body.parse(sample.body ?? {}) : sample.body,
    };
    const out = resolvers[id]!(ctx);
    const res = r.response!.safeParse(out);
    if (!res.success) throw new Error(`${id}: ${JSON.stringify(res.error.issues.slice(0, 3), null, 2)}`);
  });

  it('first-run setup: no users → setup.create signs in → login, enrol and verify work for the new account', () => {
    mockSetupMode(true);
    try {
      expect(SetupStatus.parse(resolvers['setup.status']!({ params: {}, query: {}, body: {} }))).toEqual({
        needed: true,
        allowedHere: true,
      });
      const body = SetupRequest.parse({ username: 'Owner', password: 'a long first password', player: 'owner_mc', roles: ['viewer', 'admin'] });
      const created = LoginResponse.parse(resolvers['setup.create']!({ params: {}, query: {}, body }));
      expect(created.user).toMatchObject({ username: 'owner', player: 'owner_mc', roles: ['admin', 'viewer'], totpEnrolled: false });
      expect(() => resolvers['setup.create']!({ params: {}, query: {}, body })).toThrow(/already complete/);
      resolvers['auth.totpEnroll']!({ params: {}, query: {}, body: { password: 'a long first password' } });
      expect(resolvers['auth.totpVerify']!({ params: {}, query: {}, body: { code: '123456' } })).toEqual({ ok: true, totpEnrolled: true });
      expect(Me.parse(resolvers['auth.me']!({ params: {}, query: {}, body: {} })).totpEnrolled).toBe(true);
    } finally {
      mockSetupMode(false);
    }
  });

  it('every simulated SSE frame matches its event schema', () => {
    for (let i = 0; i < 60; i++) {
      for (const [event, data] of tick()) {
        const schema = SSE_EVENTS[event as SseEventName];
        expect(schema, event).toBeDefined();
        const res = schema.safeParse(data);
        if (!res.success) throw new Error(`${event}: ${JSON.stringify(res.error.issues.slice(0, 3))}`);
      }
    }
  });
});
