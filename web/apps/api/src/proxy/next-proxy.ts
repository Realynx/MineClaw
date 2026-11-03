/**
 * Prod reverse proxy (architecture.md › Topology): Nest is the only listener on 0.0.0.0:8080; every request outside `/api`
 * goes to Next standalone on `Web:NextUrl` (127.0.0.1:3000), WebSocket upgrades included.
 *
 * - Request headers to Next: `x-nonce` and `content-security-policy` (Next applies the nonce to its scripts), plus
 *   X-Forwarded-For/Proto/Host. Any client-sent `x-middleware-*` and `x-nonce` headers are stripped first (CVE-2025-29927).
 * - Response: Nest's security headers win; Next's own CSP / frame / HSTS / referrer headers are dropped.
 * - Next down → a small 502 page (no upstream details).
 */
import type { IncomingMessage, ServerResponse } from 'node:http';
import type { Socket } from 'node:net';
import { createProxyMiddleware, type RequestHandler } from 'http-proxy-middleware';
import { maybeContext } from '../common/http.js';
import type { Logger } from '../common/logger.js';
import { CSP_RESPONSE_HEADER, type CspOptions } from '../security/headers.js';

const STRIP_FROM_NEXT = [
  'content-security-policy',
  'content-security-policy-report-only',
  'x-frame-options',
  'strict-transport-security',
  'referrer-policy',
  'x-powered-by',
];

export function isApiPath(url: string | undefined): boolean {
  const u = url ?? '/';
  return u === '/api' || u.startsWith('/api/') || u.startsWith('/api?');
}

function stripClientHeaders(req: IncomingMessage): void {
  for (const name of Object.keys(req.headers)) {
    if (name.startsWith('x-middleware-') || name === 'x-nonce' || name === 'x-mcx-user' || name === 'x-mcx-roles')
      delete req.headers[name];
  }
}

export interface NextProxy {
  middleware: (req: IncomingMessage, res: ServerResponse, next: (err?: unknown) => void) => void;
  upgrade: (req: IncomingMessage, socket: Socket, head: Buffer) => void;
}

export function createNextProxy(nextUrl: string, csp: CspOptions, log: Logger): NextProxy {
  const proxy: RequestHandler<IncomingMessage, ServerResponse> = createProxyMiddleware<IncomingMessage, ServerResponse>(
    {
      target: nextUrl,
      changeOrigin: false,
      xfwd: true,
      ws: false,
      pathFilter: (path) => !isApiPath(path),
      on: {
        proxyReq: (proxyReq, req, res) => {
          const ctx = maybeContext(req);
          if (ctx?.nonce) {
            proxyReq.setHeader('x-nonce', ctx.nonce);
            const policy = res.getHeader(CSP_RESPONSE_HEADER(csp));
            if (typeof policy === 'string') proxyReq.setHeader('content-security-policy', policy);
          }
          if (ctx) {
            proxyReq.setHeader('x-forwarded-for', ctx.ip);
            proxyReq.setHeader('x-forwarded-proto', ctx.https ? 'https' : 'http');
          }
        },
        proxyRes: (proxyRes) => {
          for (const h of STRIP_FROM_NEXT) delete proxyRes.headers[h];
        },
        error: (err, _req, res) => {
          log.warn({ err: err.message }, 'Next is unreachable');
          if ('setHeader' in res && !res.headersSent) {
            const r = res;
            r.statusCode = 502;
            r.setHeader('Content-Type', 'text/html; charset=utf-8');
            r.setHeader('Cache-Control', 'no-store');
            r.end(
              '<!doctype html><title>MineClaw</title><p>The web UI is starting or unavailable. Try again in a moment.</p>',
            );
          } else if ('destroy' in res) {
            res.destroy();
          }
        },
      },
    },
  );
  return {
    middleware: (req, res, next) => {
      if (isApiPath(req.url)) return next();
      stripClientHeaders(req);
      proxy(req, res, next).catch((e: unknown) => next(e));
    },
    upgrade: (req, socket, head) => {
      if (isApiPath(req.url)) {
        socket.destroy();
        return;
      }
      stripClientHeaders(req);
      proxy.upgrade(req, socket, head);
    },
  };
}
