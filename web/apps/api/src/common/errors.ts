/** `ApiError` responses (docs/web/api.md §1, Errors). Auth errors are generic; upstream details never reach the browser. */
import type { ServerResponse } from 'node:http';
import type { ApiError } from '@mineclaw/contracts';

export type ApiErrorCode = NonNullable<ApiError['code']>;

export class ApiHttpError extends Error {
  constructor(
    readonly status: number,
    readonly body: ApiError,
    readonly headers: Record<string, string> = {},
  ) {
    super(body.error);
    this.name = 'ApiHttpError';
  }
}

function retry(ms: number): { headers: Record<string, string>; retryAfterMs: number } {
  const retryAfterMs = Math.max(0, Math.ceil(ms));
  return { headers: { 'Retry-After': String(Math.max(1, Math.ceil(retryAfterMs / 1000))) }, retryAfterMs };
}

export const Errors = {
  badRequest: (details?: ApiError['details'], error = 'invalid request') =>
    new ApiHttpError(400, { error, code: 'bad_request', ...(details ? { details } : {}) }),
  unauthorized: (error = 'not signed in') => new ApiHttpError(401, { error, code: 'unauthorized' }),
  invalidCredentials: () => new ApiHttpError(401, { error: 'invalid credentials', code: 'unauthorized' }),
  forbidden: (error = 'forbidden') => new ApiHttpError(403, { error, code: 'forbidden' }),
  csrf: () => new ApiHttpError(403, { error: 'request rejected (CSRF)', code: 'csrf' }),
  stepUpRequired: () =>
    new ApiHttpError(403, { error: 'confirm it is you first (step-up)', code: 'step_up_required' }),
  notFound: (error = 'not found') => new ApiHttpError(404, { error, code: 'not_found' }),
  conflict: (error = 'conflict') => new ApiHttpError(409, { error, code: 'conflict' }),
  tooLarge: (error = 'payload too large') => new ApiHttpError(413, { error, code: 'payload_too_large' }),
  locked: (ms: number) => {
    const r = retry(ms);
    return new ApiHttpError(
      423,
      { error: 'too many failed attempts; try again later', code: 'locked', retryAfterMs: r.retryAfterMs },
      r.headers,
    );
  },
  rateLimited: (ms: number, error = 'slow down') => {
    const r = retry(ms);
    return new ApiHttpError(429, { error, code: 'rate_limited', retryAfterMs: r.retryAfterMs }, r.headers);
  },
  upstreamError: (status = 502, error = 'upstream error') =>
    new ApiHttpError(status, { error, code: 'upstream_error' }),
  upstreamUnavailable: (error = 'bridge or bot unavailable') =>
    new ApiHttpError(503, { error, code: 'upstream_unavailable' }),
  busy: (ms: number) => {
    const r = retry(ms);
    return new ApiHttpError(
      503,
      { error: 'busy; retry shortly', code: 'busy', retryAfterMs: r.retryAfterMs },
      r.headers,
    );
  },
  internal: () => new ApiHttpError(500, { error: 'internal error', code: 'internal' }),
};

/** Writes an ApiError (no-store JSON). If headers already went out, the connection is cut instead. */
export function sendApiError(res: ServerResponse, err: ApiHttpError): void {
  if (res.headersSent) {
    res.destroy();
    return;
  }
  const body = JSON.stringify(err.body);
  res.statusCode = err.status;
  for (const [k, v] of Object.entries(err.headers)) res.setHeader(k, v);
  res.setHeader('Content-Type', 'application/json; charset=utf-8');
  res.setHeader('Cache-Control', 'no-store');
  res.setHeader('Content-Length', Buffer.byteLength(body));
  res.end(body);
}

export function sendJson(res: ServerResponse, status: number, body: unknown, cacheControl = 'no-store'): void {
  const text = JSON.stringify(body);
  res.statusCode = status;
  res.setHeader('Content-Type', 'application/json; charset=utf-8');
  res.setHeader('Cache-Control', cacheControl);
  res.setHeader('Content-Length', Buffer.byteLength(text));
  res.end(text);
}
