/**
 * One pipeline for every route in the contracts `ROUTES` table:
 *   session → access (`hasRole`) → CSRF + Origin (non-GET) → validate params/query/body (zod) → handler
 * Handlers: Nest-native (auth, audit, health), the SSE hub (`live.stream`), or the generic signed bridge forward, which
 * converts bodies/query to snake_case, streams binary replies unchanged, camelCases JSON replies with
 * `parseBridgeJson` + `toCamel`, validates them in dev/test, and records the route's audit kind.
 */
import type { IncomingMessage, ServerResponse } from 'node:http';
import {
  ROUTES,
  ViewMeta,
  bridgePath,
  hasRole,
  parseBridgeJson,
  toCamel,
  toSnake,
  type RouteDef,
  type RouteId,
  type WebAuditKind,
} from '@mineclaw/contracts';
import type { z } from 'zod';
import type { AuditService } from '../audit/audit.service.js';
import { clip } from '../audit/audit.service.js';
import type { AuthService, AuthState } from '../auth/auth.service.js';
import type { SetupService } from '../auth/setup.service.js';
import type { TokenBucket } from '../auth/rate-limit.js';
import { tokensEqual } from '../auth/sessions.js';
import type { UserRecord } from '../auth/users-store.js';
import { mapUpstreamError, readText, MAX_JSON_BYTES, type BridgeClient } from '../bridge/bridge-client.js';
import type { BridgeHealth } from '../bridge/bridge-health.js';
import { identityOf } from '../bridge/signing.js';
import { ApiHttpError, Errors, sendApiError, sendJson } from '../common/errors.js';
import { contextOf, header, parseQuery, readBody, type RequestContext } from '../common/http.js';
import type { Logger } from '../common/logger.js';
import type { LiveHub } from '../live/live-hub.js';
import { VERSION } from '../version.js';
import { encodeShardUpload, parseShardUpload, type ShardUpload } from './multipart.js';

export type RoutedRequest = IncomingMessage & { params?: Record<string, string | string[]> };

export interface DispatcherDeps {
  auth: AuthService;
  setup: SetupService;
  audit: AuditService;
  bridge: BridgeClient;
  health: BridgeHealth;
  hub: LiveHub;
  chatLimiter: TokenBucket;
  log: Logger;
  allowedOrigins: readonly string[];
  bodyLimitBytes: number;
  validateResponses: 'off' | 'warn' | 'strict';
}

interface Input {
  params: Record<string, unknown>;
  query: Record<string, unknown> | undefined;
  body: unknown;
  upload?: ShardUpload;
}

type Signed = Required<AuthState>;

/** Headers passed through from binary bridge replies (api.md §1, Binary responses). */
export const BINARY_PASSTHROUGH = [
  'content-type',
  'content-length',
  'etag',
  'x-sha256',
  'x-tokens',
  'x-width',
  'x-height',
  'x-seen',
  'retry-after',
  'content-disposition',
] as const;

/** Binary GET replies that carry an ETag (MCWC chunks, renders) may be kept and revalidated with If-None-Match. */
const REVALIDATE = 'private, no-cache';

function cacheControl(id: RouteId, binary: boolean, etag: unknown): string {
  return binary && etag ? REVALIDATE : 'no-store';
}

/** Alternate JSON reply schemas for binary routes (`vision.render?meta=1` returns `ViewMeta`). */
const ALT_JSON: Partial<Record<RouteId, z.ZodType>> = { 'vision.render': ViewMeta };

function issues(e: z.ZodError): Array<{ path: string; message: string }> {
  return e.issues.slice(0, 20).map((i) => ({ path: i.path.map(String).join('.'), message: i.message }));
}

function normOrigin(o: string): string {
  return o.trim().replace(/\/+$/, '').toLowerCase();
}

export class RouteDispatcher {
  private readonly origins: Set<string>;
  private readonly native: Partial<
    Record<
      RouteId,
      (
        req: IncomingMessage,
        res: ServerResponse,
        ctx: RequestContext,
        state: AuthState,
        input: Input,
      ) => Promise<unknown>
    >
  >;

  constructor(private readonly d: DispatcherDeps) {
    this.origins = new Set(d.allowedOrigins.map(normOrigin));
    const signed = (state: AuthState): Signed => state as Signed;
    this.native = {
      health: async () => {
        const h = await d.health.check();
        return { ok: true, version: VERSION, bridge: h.up ? 'up' : 'down' };
      },
      'setup.status': (req) => Promise.resolve(d.setup.status(req)),
      'setup.create': (req, res, ctx, state, input) => d.setup.create(res, req, ctx, state, input.body as never),
      'auth.csrf': (req, res, ctx, state) => Promise.resolve(d.auth.csrf(res, req, ctx, state)),
      'auth.login': (req, res, ctx, state, input) => d.auth.login(res, req, ctx, state, input.body as never),
      'auth.logout': (req, res, ctx, state) => Promise.resolve(d.auth.logout(res, req, ctx, signed(state))),
      'auth.me': (_req, _res, _ctx, state) => Promise.resolve(d.auth.me(signed(state))),
      'auth.totpEnroll': (req, _res, ctx, state, input) =>
        d.auth.totpEnroll(req, ctx, signed(state), input.body as never),
      'auth.totpVerify': (req, _res, ctx, state, input) =>
        Promise.resolve(d.auth.totpVerify(req, ctx, signed(state), input.body as never)),
      'audit.list': (_req, _res, _ctx, _state, input) => Promise.resolve(d.audit.list(input.query as never)),
    };
    for (const id of Object.keys(ROUTES) as RouteId[]) {
      const r: RouteDef = ROUTES[id];
      if (r.bridge === null && !this.native[id]) throw new Error(`route ${id} is Nest-native but has no handler`);
    }
  }

  async dispatch(id: RouteId, req: RoutedRequest, res: ServerResponse): Promise<void> {
    const route: RouteDef = ROUTES[id];
    const ctx = contextOf(req);
    try {
      const state = this.d.auth.resolve(req, ctx);
      this.checkAccess(route, state);
      if (route.method !== 'GET') this.checkCsrf(req, state);
      const input = await this.parseInput(route, req);
      if (route.bridge === null) {
        const out = await this.native[id]!(req, res, ctx, state, input);
        this.validate(id, route.response, out);
        sendJson(res, route.status ?? 200, out);
        return;
      }
      const signed = state as Signed;
      if (id === 'live.stream') {
        this.d.hub.attach(req, res, input.query, {
          username: signed.user.username,
          stillValid: () => this.d.auth.stillSignedIn(signed.session.idHash),
        });
        return;
      }
      await this.forward(id, route, req, res, ctx, signed, input);
    } catch (e) {
      this.fail(res, e, id);
    }
  }

  private fail(res: ServerResponse, e: unknown, id: RouteId): void {
    if (e instanceof ApiHttpError) {
      sendApiError(res, e);
      return;
    }
    this.d.log.error({ route: id, err: e instanceof Error ? e.message : String(e) }, 'unhandled route error');
    sendApiError(res, Errors.internal());
  }

  private checkAccess(route: RouteDef, state: AuthState): void {
    if (route.access === 'public') return;
    if (!state.user) throw Errors.unauthorized();
    if (!hasRole(state.user.roles, route.access)) throw Errors.forbidden();
  }

  /** Every non-GET: `X-CSRF-Token` equals the session's token and `Origin` is in `Web:AllowedOrigins`. */
  private checkCsrf(req: IncomingMessage, state: AuthState): void {
    const origin = header(req, 'origin');
    if (!origin || !this.origins.has(normOrigin(origin))) throw Errors.csrf();
    const token = header(req, 'x-csrf-token');
    if (!state.session || !token || !tokensEqual(token, state.session.csrf)) throw Errors.csrf();
  }

  private async parseInput(route: RouteDef, req: RoutedRequest): Promise<Input> {
    const rawParams: Record<string, string> = {};
    for (const [k, v] of Object.entries(req.params ?? {})) rawParams[k] = Array.isArray(v) ? v.join('/') : v;
    let params: Record<string, unknown> = rawParams;
    if (route.params) {
      const p = route.params.safeParse(rawParams);
      if (!p.success) throw Errors.badRequest(issues(p.error));
      params = p.data as Record<string, unknown>;
    }
    let query: Record<string, unknown> | undefined;
    if (route.query) {
      const q = route.query.safeParse(parseQuery(req.url));
      if (!q.success) throw Errors.badRequest(issues(q.error));
      query = q.data as Record<string, unknown>;
    }
    if (route.method === 'GET' || !route.body) return { params, query, body: undefined };

    if (route.bodyKind === 'multipart') {
      const upload = await parseShardUpload(req);
      const o = route.body.safeParse(upload.options);
      if (!o.success) throw Errors.badRequest(issues(o.error).map((i) => ({ ...i, path: `options.${i.path}` })));
      return { params, query, body: o.data, upload };
    }

    const raw = await readBody(req, this.d.bodyLimitBytes);
    let json: unknown = {};
    if (raw.length > 0) {
      const type = header(req, 'content-type') ?? '';
      if (!/^application\/json\b/i.test(type)) throw Errors.badRequest(undefined, 'expected application/json');
      try {
        json = JSON.parse(raw.toString('utf8')) as unknown;
      } catch {
        throw Errors.badRequest(undefined, 'body is not valid JSON');
      }
    }
    const b = route.body.safeParse(json);
    if (!b.success) throw Errors.badRequest(issues(b.error));
    return { params, query, body: b.data };
  }

  private validate(id: RouteId, schema: z.ZodType | null | undefined, data: unknown): void {
    if (this.d.validateResponses === 'off' || !schema) return;
    const r = schema.safeParse(data);
    if (r.success) return;
    this.d.log.warn({ route: id, issues: issues(r.error) }, 'response does not match the contract');
    if (this.d.validateResponses === 'strict')
      throw Errors.upstreamError(502, 'upstream response did not match the contract');
  }

  /** Bridge body for a route (snake_case). Chat sends `{bot, text, request_id}`: never a player, never allow_commands. */
  private bridgeBody(id: RouteId, input: Input): { body?: Buffer; contentType?: string } {
    if (input.upload) {
      const enc = encodeShardUpload(toSnake(input.body), input.upload.file, input.upload.filename);
      return { body: enc.body, contentType: enc.contentType };
    }
    if (input.body === undefined) return {};
    if (id === 'bots.chat') {
      const b = input.body as { text: string; requestId: string };
      const payload = { bot: input.params.bot, text: b.text, request_id: b.requestId };
      return { body: Buffer.from(JSON.stringify(payload), 'utf8'), contentType: 'application/json' };
    }
    return { body: Buffer.from(JSON.stringify(toSnake(input.body)), 'utf8'), contentType: 'application/json' };
  }

  private auditTarget(input: Input): string | null {
    const parts = Object.values(input.params).map(String);
    const b = input.body as Record<string, unknown> | undefined;
    if (b && typeof b === 'object') {
      for (const k of ['kind', 'id', 'scope', 'node']) {
        const v = b[k];
        if (typeof v === 'string' || typeof v === 'number') parts.push(`${k}=${v}`);
      }
    }
    if (input.upload) parts.push(`${input.upload.filename} (${input.upload.file.length} B)`);
    return parts.length ? parts.join(' ') : null;
  }

  private auditSummary(id: RouteId, input: Input): string {
    if (id === 'bots.chat') return (input.body as { text: string }).text;
    const body = input.body === undefined ? '' : ` ${JSON.stringify(input.body)}`;
    return clip(`${id}${body}`);
  }

  private async forward(
    id: RouteId,
    route: RouteDef,
    req: IncomingMessage,
    res: ServerResponse,
    ctx: RequestContext,
    s: Signed,
    input: Input,
  ): Promise<void> {
    const auditKind: WebAuditKind | null = route.audit;
    const user: UserRecord = s.user;
    const audit = (ok: boolean, status: number, note?: string) => {
      if (!auditKind) return;
      this.d.audit.record({
        kind: auditKind,
        ok,
        status,
        ip: ctx.ip,
        userAgent: header(req, 'user-agent'),
        username: user.username,
        player: user.player,
        method: route.method,
        route: id,
        target: this.auditTarget(input),
        summary: note ? `${this.auditSummary(id, input)} [${note}]` : this.auditSummary(id, input),
        requestId: ctx.requestId,
      });
    };

    try {
      if (id === 'bots.chat') {
        if (!user.player) throw Errors.forbidden('no Minecraft player is linked to this account');
        const wait = this.d.chatLimiter.take(user.username);
        if (wait > 0) throw Errors.rateLimited(wait, 'too many chat lines; slow down');
      }

      const target = bridgePath(id, input.params, input.query);
      const { body, contentType } = this.bridgeBody(id, input);
      const extra: Record<string, string> = {};
      const inm = header(req, 'if-none-match');
      if (route.method === 'GET' && inm) extra['If-None-Match'] = inm;
      const upstream = await this.d.bridge.request({
        method: route.bridge!.method,
        target,
        identity: identityOf(user),
        body,
        contentType,
        headers: extra,
        requestId: ctx.requestId,
      });
      const status = upstream.statusCode ?? 502;

      if (status === 304) {
        upstream.resume();
        res.statusCode = 304;
        const etag = upstream.headers.etag;
        if (etag) res.setHeader('ETag', etag);
        res.setHeader('Cache-Control', cacheControl(id, route.responseKind !== 'json', etag ?? inm));
        res.end();
        audit(true, 304);
        return;
      }

      if (status < 200 || status >= 300) {
        const text = await readText(upstream, 256 * 1024);
        if (id === 'admin.memoryEdit' && status === 400) {
          // A supervisor MemoryEditResult refusal passes through as 400 with that body (api.md §1).
          try {
            const parsed = toCamel(parseBridgeJson(text));
            if (route.response?.safeParse(parsed).success) {
              sendJson(res, 400, parsed);
              audit(false, 400, 'refused');
              return;
            }
          } catch {
            // fall through to the generic mapping
          }
        }
        throw mapUpstreamError(status, upstream.headers, text, this.d.log);
      }

      const type = String(upstream.headers['content-type'] ?? '');
      const jsonReply = route.responseKind === 'json' || /^application\/(problem\+)?json\b/i.test(type);
      if (jsonReply) {
        const text = await readText(upstream, MAX_JSON_BYTES);
        let data: unknown;
        try {
          data = toCamel(parseBridgeJson(text));
        } catch {
          throw Errors.upstreamError(502, 'upstream sent invalid JSON');
        }
        this.validate(id, route.responseKind === 'json' ? route.response : ALT_JSON[id], data);
        if (id === 'bots.list') this.d.hub.learn(data);
        const outStatus = route.status ?? status;
        if (route.method === 'GET' && upstream.headers.etag) res.setHeader('ETag', upstream.headers.etag);
        sendJson(res, outStatus, data, cacheControl(id, false, upstream.headers.etag));
        if (id === 'bots.chat') {
          const accepted = (data as { accepted?: unknown }).accepted === true;
          const note = (data as { note?: unknown }).note;
          audit(accepted, outStatus, accepted ? undefined : typeof note === 'string' ? note : 'not accepted');
        } else {
          audit(true, outStatus);
        }
        return;
      }

      // Binary (MCWC chunks, PNGs, shard zips): headers through, bytes streamed unchanged, never buffered.
      res.statusCode = route.status ?? status;
      for (const h of BINARY_PASSTHROUGH) {
        const v = upstream.headers[h];
        if (v !== undefined) res.setHeader(h, v);
      }
      res.setHeader('Cache-Control', cacheControl(id, true, upstream.headers.etag));
      res.setHeader('X-Content-Type-Options', 'nosniff');
      await new Promise<void>((resolve) => {
        const done = () => resolve();
        upstream.on('error', () => {
          res.destroy();
          done();
        });
        res.on('close', () => {
          if (!upstream.complete) upstream.destroy();
          done();
        });
        res.on('finish', done);
        upstream.pipe(res);
      });
      audit(res.writableFinished, res.statusCode);
    } catch (e) {
      if (e instanceof ApiHttpError) audit(false, e.status, e.body.code);
      throw e;
    }
  }
}
