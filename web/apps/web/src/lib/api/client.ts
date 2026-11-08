/**
 * The typed API client. Every call goes through a route id from `ROUTES` (@mineclaw/contracts), so the path, method and
 * response schema always come from the contract. Responses are validated:
 * - in development, a contract mismatch throws (so fixtures and servers that drift are caught at once);
 * - in production, a mismatch is logged and the raw body is returned (responses are forward compatible: a newer bot may add
 *   fields, and stripping unknown keys must never break the page).
 *
 * CSRF (docs/web/api.md §2): the token from `GET /api/auth/csrf` is sent as `X-CSRF-Token` on every non-GET, login included.
 * Login returns a new token (the session rotates). A 403 `csrf` refreshes the token once and retries.
 */
import { ApiError as ApiErrorSchema, apiPath, CsrfResponse, ROUTES, type RouteDef, type RouteId } from '@mineclaw/contracts';
import type { z } from 'zod';
import type { RouteTypes } from './route-types.gen';

/** Exact types per route (generated from ROUTES; see scripts/gen-route-types.mjs). */
export type RouteResponse<K extends RouteId> = RouteTypes[K]['response'];
export type RouteQueryInput<K extends RouteId> = RouteTypes[K]['query'];
export type RouteBody<K extends RouteId> = RouteTypes[K]['body'];

const DEV = process.env.NODE_ENV !== 'production';

export class ApiError extends Error {
  readonly status: number;
  readonly code: string | undefined;
  readonly retryAfterMs: number | undefined;
  readonly details: Array<{ path: string; message: string }> | undefined;
  /** The raw body (e.g. a `MemoryEditResult` refusal is passed through as a 400 with that body). */
  readonly body: unknown;

  constructor(status: number, message: string, init: Partial<Pick<ApiError, 'code' | 'retryAfterMs' | 'details' | 'body'>>) {
    super(message);
    this.name = 'ApiError';
    this.status = status;
    this.code = init.code;
    this.retryAfterMs = init.retryAfterMs;
    this.details = init.details;
    this.body = init.body;
  }

  get isUnauthorized(): boolean {
    return this.status === 401;
  }
  get isForbidden(): boolean {
    return this.status === 403 && this.code !== 'csrf';
  }
  get isRateLimited(): boolean {
    return this.status === 429;
  }
  get isUnavailable(): boolean {
    return this.status === 503 || this.status === 502 || this.status === 0;
  }
}

/** Raised in development when a response does not match its contract schema. */
export class ContractError extends Error {
  constructor(
    readonly route: string,
    readonly issues: unknown,
  ) {
    super(`response of ${route} does not match the contract`);
    this.name = 'ContractError';
  }
}

// ---------- CSRF ----------

let csrfToken: string | null = null;
let csrfInflight: Promise<string> | null = null;

export function setCsrfToken(token: string | null): void {
  csrfToken = token;
}

export function getCsrfToken(): string | null {
  return csrfToken;
}

/** Fetches (once) the CSRF token for the current, possibly anonymous, session. */
export async function ensureCsrf(force = false): Promise<string> {
  if (csrfToken && !force) return csrfToken;
  csrfInflight ??= (async () => {
    try {
      const res = await rawFetch(apiPath('auth.csrf'), { method: 'GET' });
      const data = (await readJson(res));
      if (!res.ok) throw toApiError(res, data);
      const parsed = CsrfResponse.parse(data);
      csrfToken = parsed.csrfToken;
      return parsed.csrfToken;
    } finally {
      csrfInflight = null;
    }
  })();
  return csrfInflight;
}

// ---------- session-expiry hook ----------

type UnauthorizedListener = (route: RouteId) => void;
const unauthorizedListeners = new Set<UnauthorizedListener>();

/** The auth provider listens here: a 401 on a non-public route means the session expired. */
export function onUnauthorized(fn: UnauthorizedListener): () => void {
  unauthorizedListeners.add(fn);
  return () => unauthorizedListeners.delete(fn);
}

// ---------- fetch plumbing ----------

/** Swappable for tests. */
let fetchImpl: typeof fetch = (...args) => globalThis.fetch(...args);
export function setFetch(f: typeof fetch): void {
  fetchImpl = f;
}

function rawFetch(url: string, init: RequestInit): Promise<Response> {
  return fetchImpl(url, { credentials: 'same-origin', ...init });
}

async function readJson(res: Response): Promise<unknown> {
  const text = await res.text();
  if (!text) return null;
  try {
    return JSON.parse(text) as unknown;
  } catch {
    return { error: text.slice(0, 200) };
  }
}

function retryAfter(res: Response, body: { retryAfterMs?: number } | null): number | undefined {
  if (body?.retryAfterMs !== undefined) return body.retryAfterMs;
  const h = res.headers.get('Retry-After');
  if (!h) return undefined;
  const s = Number(h);
  if (Number.isFinite(s)) return s * 1000;
  const at = Date.parse(h);
  return Number.isNaN(at) ? undefined : Math.max(0, at - Date.now());
}

function toApiError(res: Response, body: unknown): ApiError {
  const parsed = ApiErrorSchema.safeParse(body);
  const err = parsed.success ? parsed.data : null;
  const message = err?.error ?? (res.statusText || `HTTP ${res.status}`);
  return new ApiError(res.status, message, {
    code: err?.code,
    retryAfterMs: retryAfter(res, err),
    details: err?.details,
    body,
  });
}

export interface CallOptions<K extends RouteId> {
  params?: Record<string, string | number>;
  query?: RouteQueryInput<K>;
  body?: RouteBody<K>;
  /** multipart routes: the `shard` file (the `options` part is `body`). */
  file?: Blob;
  signal?: AbortSignal;
}

function buildInit<K extends RouteId>(route: RouteDef, opts: CallOptions<K>): RequestInit {
  const headers: Record<string, string> = { Accept: route.responseKind === 'json' ? 'application/json' : '*/*' };
  let body: BodyInit | undefined;
  if (route.method !== 'GET') {
    if (route.bodyKind === 'multipart') {
      const form = new FormData();
      if (opts.file) form.append('shard', opts.file);
      form.append('options', JSON.stringify(opts.body ?? {}));
      body = form;
    } else {
      headers['Content-Type'] = 'application/json';
      body = JSON.stringify(opts.body ?? {});
    }
  }
  return { method: route.method, headers, body, signal: opts.signal };
}

async function send<K extends RouteId>(id: K, opts: CallOptions<K>): Promise<Response> {
  const route: RouteDef = ROUTES[id];
  const url = apiPath(id, opts.params ?? {}, opts.query);
  const checkOnline = () => {
    if (route.method !== 'GET' && typeof navigator !== 'undefined' && navigator.onLine === false)
      throw new ApiError(0, 'You are offline. Reconnect, then send the command again.', { code: 'offline' });
  };
  const attempt = async (): Promise<Response> => {
    checkOnline();
    const init = buildInit(route, opts);
    if (id === 'world.humanSkins') init.cache = 'no-store';
    if (route.method !== 'GET') {
      const token = await ensureCsrf();
      (init.headers as Record<string, string>)['X-CSRF-Token'] = token;
    }
    checkOnline();
    try {
      return await rawFetch(url, init);
    } catch (e) {
      if (e instanceof DOMException && e.name === 'AbortError') throw e;
      throw new ApiError(0, 'The server could not be reached.', { code: 'upstream_unavailable' });
    }
  };
  let res = await attempt();
  if (res.status === 403 && route.method !== 'GET') {
    const body = await readJson(res.clone());
    if ((body as { code?: string } | null)?.code === 'csrf') {
      await ensureCsrf(true);
      res = await attempt();
    }
  }
  if (res.status === 401 && route.access !== 'public') {
    for (const l of unauthorizedListeners) l(id);
  }
  return res;
}

function validate<K extends RouteId>(id: K, schema: z.ZodType, data: unknown): RouteResponse<K> {
  const result = schema.safeParse(data);
  if (result.success) return result.data as RouteResponse<K>;
  if (DEV) {
    console.error(`[api] ${id}: response does not match the contract`, result.error.issues, data);
    throw new ContractError(id, result.error.issues);
  }
  console.warn(`[api] ${id}: response does not match the contract; using it as is`, result.error.issues);
  return data as RouteResponse<K>;
}

/** Calls a JSON route and returns its parsed, typed response. Throws `ApiError` on non-2xx. */
export async function api<K extends RouteId>(id: K, opts: CallOptions<K> = {}): Promise<RouteResponse<K>> {
  const route: RouteDef = ROUTES[id];
  const res = await send(id, opts);
  const data = await readJson(res);
  if (!res.ok) throw toApiError(res, data);
  if (!route.response) return data as RouteResponse<K>;
  // Some bot writes answer 400 with the domain result; callers read it from ApiError.body.
  return validate(id, route.response, data);
}

/** Calls a binary route (png/zip/binary) and returns the blob plus the pass-through headers. */
export async function apiBlob<K extends RouteId>(
  id: K,
  opts: CallOptions<K> = {},
): Promise<{ blob: Blob; headers: Headers; filename: string | null }> {
  const res = await send(id, opts);
  if (!res.ok) throw toApiError(res, await readJson(res));
  const cd = res.headers.get('Content-Disposition');
  const filename = cd ? (/filename="?([^";]+)"?/.exec(cd)?.[1] ?? null) : null;
  return { blob: await res.blob(), headers: res.headers, filename };
}

/** The URL of a GET binary route, for <img src>. */
export function apiUrl<K extends RouteId>(
  id: K,
  params: Record<string, string | number> = {},
  query?: RouteQueryInput<K>,
): string {
  return apiPath(id, params, query);
}

/** Human text for any error thrown by the client. */
export function errorText(e: unknown): string {
  if (e instanceof ApiError) return apiErrorText(e);
  if (e instanceof ContractError) return 'The server answered in an unexpected format.';
  return e instanceof Error ? e.message : String(e);
}

/** What an HTTP status means to the reader (some depend on the error code); any other status shows the server's message. */
const STATUS_TEXT: Partial<Record<number, string | ((code: string | undefined) => string)>> = {
  0: 'The server could not be reached. Check your connection.',
  401: 'Your session has ended. Sign in again.',
  403: (code) => (code === 'csrf' ? 'Security token expired. Reload the page.' : 'You do not have access to this.'),
  404: 'Not found.',
  429: 'Too many requests. Wait a moment and try again.',
  501: 'Not available yet on this server.',
  503: (code) => (code === 'busy' ? 'The renderer is busy. Try again shortly.' : 'The bot is not reachable right now.'),
};

function apiErrorText(e: ApiError): string {
  if (e.code === 'offline') return e.message;
  const t = STATUS_TEXT[e.status];
  if (t === undefined) return e.message;
  return typeof t === 'function' ? t(e.code) : t;
}
