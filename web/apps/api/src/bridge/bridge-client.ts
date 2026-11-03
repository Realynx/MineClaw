/**
 * The signed bridge client (`Bridge:Url`, loopback plain HTTP). One keep-alive agent for request/response calls and a
 * separate non-pooled agent for the long-lived SSE stream. Every request carries the HMAC headers and the identity headers
 * of the session's user (or none for Nest's own calls). Errors are mapped to `ApiError` here; upstream URLs, the key and
 * stack traces never reach the browser.
 */
import { Agent, request as httpRequest, type IncomingHttpHeaders, type IncomingMessage } from 'node:http';
import { BRIDGE_HEADERS } from '@mineclaw/contracts';
import { ApiHttpError, Errors } from '../common/errors.js';
import type { Logger } from '../common/logger.js';
import { signedHeaders, type BridgeIdentity } from './signing.js';

export interface BridgeClientConfig {
  url: string;
  key: string;
  timeoutMs: number;
}

export interface BridgeCall {
  method: 'GET' | 'POST';
  /** Request target as it goes on the wire (`/v1/...?...`), exactly what is signed. */
  target: string;
  identity: BridgeIdentity;
  body?: Buffer;
  contentType?: string;
  /** Unsigned extra headers (Accept, If-None-Match, Last-Event-ID). */
  headers?: Record<string, string>;
  requestId?: string;
  /** Time to response headers (and idle time after). null = none (SSE). Default `Bridge:TimeoutMs`. */
  timeoutMs?: number | null;
  /** Max silence on the response body once it started. Default: same as `timeoutMs`. */
  idleTimeoutMs?: number | null;
  stream?: boolean;
  signal?: AbortSignal;
}

export interface BridgeJson {
  status: number;
  headers: IncomingHttpHeaders;
  text: string;
}

export const MAX_JSON_BYTES = 64 * 1024 * 1024;

export function isLoopbackHost(host: string): boolean {
  const h = host.replace(/^\[|\]$/g, '').toLowerCase();
  return h === 'localhost' || h === '::1' || /^127\./.test(h);
}

export class BridgeClient {
  private readonly base: URL;
  private readonly pool = new Agent({ keepAlive: true, keepAliveMsecs: 10_000, maxSockets: 64, maxFreeSockets: 16 });
  private readonly streamAgent = new Agent({ keepAlive: false, maxSockets: 8 });

  constructor(
    private readonly cfg: BridgeClientConfig,
    private readonly log: Logger,
  ) {
    this.base = new URL(cfg.url);
    if (this.base.protocol !== 'http:') throw new Error('Bridge:Url must be plain http:// on loopback');
    if (!isLoopbackHost(this.base.hostname)) {
      log.warn({ host: this.base.hostname }, 'Bridge:Url is not loopback; the bridge only accepts loopback sources');
    }
  }

  /** Resolves with the upstream response once its headers arrive. Connection failures and timeouts become 503. */
  request(call: BridgeCall): Promise<IncomingMessage> {
    const timeoutMs = call.timeoutMs === undefined ? this.cfg.timeoutMs : call.timeoutMs;
    const headers: Record<string, string> = {
      ...signedHeaders(
        this.cfg.key,
        { method: call.method, pathAndQuery: call.target, body: call.body, identity: call.identity },
        Date.now(),
      ),
      ...(call.headers ?? {}),
      Host: this.base.host,
    };
    if (call.requestId) headers[BRIDGE_HEADERS.requestId] = call.requestId;
    if (call.body) {
      headers['Content-Length'] = String(call.body.length);
      headers['Content-Type'] = call.contentType ?? 'application/json';
    }
    return new Promise<IncomingMessage>((resolve, reject) => {
      let settled = false;
      const req = httpRequest({
        protocol: 'http:',
        hostname: this.base.hostname.replace(/^\[|\]$/g, ''),
        port: this.base.port || 80,
        method: call.method,
        path: call.target,
        headers,
        agent: call.stream ? this.streamAgent : this.pool,
        signal: call.signal,
      });
      let timer: NodeJS.Timeout | undefined;
      if (timeoutMs !== null && timeoutMs > 0) {
        timer = setTimeout(() => {
          req.destroy(new Error('bridge timeout'));
        }, timeoutMs);
      }
      req.on('response', (res) => {
        if (timer) clearTimeout(timer);
        settled = true;
        const idle = call.idleTimeoutMs === undefined ? timeoutMs : call.idleTimeoutMs;
        if (idle !== null && idle > 0) {
          res.setTimeout(idle, () => res.destroy(new Error('bridge idle timeout')));
        }
        resolve(res);
      });
      req.on('error', (err) => {
        if (timer) clearTimeout(timer);
        if (settled) return;
        settled = true;
        if (err.name === 'AbortError') {
          reject(err);
          return;
        }
        this.log.warn({ err: err.message, target: call.target.split('?')[0] }, 'bridge request failed');
        reject(Errors.upstreamUnavailable());
      });
      if (call.body) req.end(call.body);
      else req.end();
    });
  }

  /** A JSON round trip: status, headers and the body text (callers parse with `parseBridgeJson`). */
  async json(call: BridgeCall): Promise<BridgeJson> {
    const res = await this.request(call);
    const text = await readText(res, MAX_JSON_BYTES);
    return { status: res.statusCode ?? 502, headers: res.headers, text };
  }
}

export async function readText(res: IncomingMessage, limit: number): Promise<string> {
  const chunks: Buffer[] = [];
  let size = 0;
  try {
    for await (const chunk of res) {
      const b = chunk as Buffer;
      size += b.length;
      if (size > limit) {
        res.destroy();
        throw Errors.upstreamError(502, 'upstream response too large');
      }
      chunks.push(b);
    }
  } catch (e) {
    if (e instanceof ApiHttpError) throw e;
    throw Errors.upstreamUnavailable();
  }
  return Buffer.concat(chunks, size).toString('utf8');
}

function retryAfterMs(headers: IncomingHttpHeaders, body: Record<string, unknown> | undefined): number {
  const fromBody = body?.retry_after_ms ?? body?.retryAfterMs;
  if (typeof fromBody === 'number' && fromBody >= 0) return fromBody;
  const h = headers['retry-after'];
  const s = Number(Array.isArray(h) ? h[0] : h);
  return Number.isFinite(s) && s >= 0 ? s * 1000 : 1000;
}

function safeMessage(body: Record<string, unknown> | undefined, fallback: string): string {
  const m = body?.error;
  if (typeof m !== 'string' || m.length === 0) return fallback;
  // Bridge messages are short and ours, but never echo anything that looks like a URL or a path on disk.
  if (/https?:\/\/|[A-Za-z]:\\|\/etc\/|\/var\//.test(m)) return fallback;
  return m.length > 300 ? `${m.slice(0, 299)}…` : m;
}

/** Maps a non-2xx bridge reply to the public `ApiError` (api.md §1 table). */
export function mapUpstreamError(
  status: number,
  headers: IncomingHttpHeaders,
  text: string,
  log: Logger,
): ApiHttpError {
  let body: Record<string, unknown> | undefined;
  try {
    const parsed = JSON.parse(text) as unknown;
    if (parsed && typeof parsed === 'object' && !Array.isArray(parsed)) body = parsed as Record<string, unknown>;
  } catch {
    body = undefined;
  }
  const code = typeof body?.code === 'string' ? body.code : undefined;
  switch (status) {
    case 400:
      return Errors.badRequest(undefined, safeMessage(body, 'invalid request'));
    case 401:
      // The bridge rejected our signature: a config problem on our side, never the browser's.
      log.error({ status }, 'bridge rejected the request signature (check Bridge:Key and clocks)');
      return Errors.upstreamError(502);
    case 403:
      return Errors.forbidden(safeMessage(body, 'forbidden'));
    case 404:
      return Errors.notFound(safeMessage(body, 'not found'));
    case 409:
      return Errors.conflict(safeMessage(body, 'conflict'));
    case 413:
      return Errors.tooLarge();
    case 429:
      return Errors.rateLimited(retryAfterMs(headers, body), safeMessage(body, 'slow down'));
    case 501:
      return Errors.upstreamError(501, 'not implemented yet');
    case 503:
      return code === 'busy' ? Errors.busy(retryAfterMs(headers, body)) : Errors.upstreamUnavailable();
    default:
      if (status >= 400 && status < 500)
        return new ApiHttpError(status, { error: safeMessage(body, 'request failed'), code: 'bad_request' });
      log.warn({ status }, 'bridge error');
      return Errors.upstreamError(502);
  }
}
