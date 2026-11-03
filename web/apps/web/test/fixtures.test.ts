/**
 * The mock server's fixtures must satisfy the contracts: every JSON route resolver is called (with its sample params, or none)
 * and its body parsed with the route's response schema; every SSE frame the simulation emits is parsed with SSE_EVENTS.
 */
import { LoginResponse, Me, ROUTES, SetupRequest, SetupStatus, SSE_EVENTS, type RouteDef, type RouteId, type SseEventName } from '@mineclaw/contracts';
import { beforeEach, describe, expect, it } from 'vitest';
import { mockSetupMode, mockSignIn, resolvers, SAMPLE_CALLS, type Ctx } from '@/mocks/api';
import { tick } from '@/mocks/data/live';

// Answer errors by design (phase 2 / unused; setup.create is 409 once users exist, so it has its own test below).
const SKIP = new Set<RouteId>(['groom.preview', 'bots.tile', 'setup.create']);

/** Bodies and params for the write routes (the shared SAMPLE_CALLS cover the reads). */
const WRITES: Partial<Record<RouteId, Ctx>> = {
  'auth.login': { params: {}, query: {}, body: { username: 'robin', password: 'robin' } },
  'auth.totpEnroll': { params: {}, query: {}, body: { password: 'fox' } },
  'auth.totpVerify': { params: {}, query: {}, body: { code: '123456' } },
  'admin.rebuildStart': { params: {}, query: {}, body: { scope: 'kind', kind: 'fact' } },
  'admin.rebuildCancel': { params: { jobId: 'job-1' }, query: {}, body: {} },
  'bots.chat': { params: { bot: 'maid_chan' }, query: {}, body: { text: 'hi', requestId: 'abcdefgh1234' } },
};

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
    const sample: Ctx = WRITES[id] ?? SAMPLE_CALLS[id] ?? { params: {}, query: {}, body: {} };
    const ctx: Ctx = {
      params: r.params ? (r.params.parse(sample.params) as Record<string, unknown>) : {},
      query: r.query ? (r.query.parse(sample.query) as Record<string, unknown>) : {},
      body: r.body ? (r.body.parse(sample.body ?? {})) : sample.body,
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
