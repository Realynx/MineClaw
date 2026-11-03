import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { api, ApiError, ContractError, onUnauthorized, setCsrfToken, setFetch } from '@/lib/api/client';

const json = (status: number, body: unknown, headers: Record<string, string> = {}) =>
  new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json', ...headers } });

const me = {
  username: 'fox',
  player: 'poofimafox',
  roles: ['viewer', 'chat'],
  totpEnrolled: false,
  sessionCreatedAt: '2026-11-02T10:00:00+00:00',
  idleExpiresAt: '2026-11-02T22:00:00+00:00',
  absoluteExpiresAt: '2026-11-09T10:00:00+00:00',
};

describe('API client CSRF', () => {
  let calls: Array<{ url: string; init: RequestInit }>;
  let respond: (url: string, init: RequestInit) => Response;

  beforeEach(() => {
    calls = [];
    setCsrfToken(null);
    setFetch((input, init) => {
      const url = typeof input === 'string' ? input : input instanceof URL ? input.href : input.url;
      calls.push({ url, init: init ?? {} });
      return Promise.resolve(respond(url, init ?? {}));
    });
  });
  afterEach(() => setCsrfToken(null));

  const header = (init: RequestInit, name: string) => (init.headers as Record<string, string> | undefined)?.[name];

  it('sends no CSRF header on GET', async () => {
    respond = () => json(200, me);
    await api('auth.me');
    expect(calls).toHaveLength(1);
    expect(header(calls[0]!.init, 'X-CSRF-Token')).toBeUndefined();
    expect(calls[0]!.init.credentials).toBe('same-origin');
  });

  it('fetches the token once, then sends it as X-CSRF-Token on every non-GET (login included)', async () => {
    respond = (url) => (url === '/api/auth/csrf' ? json(200, { csrfToken: 'tok-aaaaaaaaaaaaaaaa' }) : json(200, { ok: true }));
    await api('auth.logout', { body: {} });
    await api('auth.logout', { body: {} });
    expect(calls.map((c) => c.url)).toEqual(['/api/auth/csrf', '/api/auth/logout', '/api/auth/logout']);
    expect(header(calls[1]!.init, 'X-CSRF-Token')).toBe('tok-aaaaaaaaaaaaaaaa');
    expect(header(calls[2]!.init, 'X-CSRF-Token')).toBe('tok-aaaaaaaaaaaaaaaa');
    expect(calls[1]!.init.method).toBe('POST');
  });

  it('uses the rotated token after login', async () => {
    setCsrfToken('tok-after-login-000000');
    respond = () => json(200, { ok: true });
    await api('auth.logout', { body: {} });
    expect(calls).toHaveLength(1);
    expect(header(calls[0]!.init, 'X-CSRF-Token')).toBe('tok-after-login-000000');
  });

  it('refreshes the token and retries once on a 403 csrf', async () => {
    setCsrfToken('tok-stale-000000000000');
    let posts = 0;
    respond = (url) => {
      if (url === '/api/auth/csrf') return json(200, { csrfToken: 'tok-fresh-000000000000' });
      posts++;
      return posts === 1 ? json(403, { error: 'csrf', code: 'csrf' }) : json(200, { ok: true });
    };
    await expect(api('auth.logout', { body: {} })).resolves.toEqual({ ok: true });
    expect(calls.map((c) => c.url)).toEqual(['/api/auth/logout', '/api/auth/csrf', '/api/auth/logout']);
    expect(header(calls[2]!.init, 'X-CSRF-Token')).toBe('tok-fresh-000000000000');
  });

  it('reports 401 on a protected route (session expiry) but not on public ones', async () => {
    const seen: string[] = [];
    const off = onUnauthorized((r) => seen.push(r));
    respond = () => json(401, { error: 'Not signed in', code: 'unauthorized' });
    await expect(api('auth.me')).rejects.toBeInstanceOf(ApiError);
    setCsrfToken('tok-x-0000000000000000');
    await expect(api('auth.login', { body: { username: 'a', password: 'b' } })).rejects.toMatchObject({ status: 401 });
    off();
    expect(seen).toEqual(['auth.me']);
  });

  it('reads Retry-After on 429', async () => {
    setCsrfToken('tok-x-0000000000000000');
    respond = () => json(429, { error: 'slow down', code: 'rate_limited' }, { 'Retry-After': '7' });
    const err = await api('bots.chat', { params: { bot: 'maid_chan' }, body: { text: 'hi', requestId: 'abcdefgh' } }).catch((e: unknown) => e);
    expect(err).toBeInstanceOf(ApiError);
    expect((err as ApiError).isRateLimited).toBe(true);
    expect((err as ApiError).retryAfterMs).toBe(7000);
  });

  it('validates responses against the contract in development', async () => {
    const spy = vi.spyOn(console, 'error').mockImplementation(() => undefined);
    respond = () => json(200, { username: 'fox' });
    await expect(api('auth.me')).rejects.toBeInstanceOf(ContractError);
    spy.mockRestore();
  });

  it('builds paths and query strings from the route table', async () => {
    respond = () => json(200, { kind: 'facts', total: 0, offset: 0, limit: 50, items: [] });
    await api('memory.list', { params: { kind: 'facts' }, query: { q: 'fox melons', limit: 50 } });
    expect(calls[0]!.url).toBe('/api/memory/facts?q=fox%20melons&limit=50');
  });
});
