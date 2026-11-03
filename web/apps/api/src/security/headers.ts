/**
 * Security headers (architecture.md › Headers). Helmet sets the static ones; this adds per request:
 * - a strict CSP: `/api` responses get `default-src 'none'`; proxied Next pages get a nonce policy (see `pageCsp`);
 * - HSTS only when the request is HTTPS (directly or via a trusted proxy), never on the plain-HTTP LAN.
 *
 * Next coordination (prod, Nest in front): for every proxied page request Nest generates a nonce and sends Next the
 * request headers `x-nonce: <nonce>` and `content-security-policy: <policy>`; Next's app router applies the nonce from that
 * request header to its own inline/bootstrap scripts. The same policy goes back to the browser. Pages must render dynamically
 * (a prerendered static page carries no nonce); `Web:Csp:Scripts=unsafe-inline` is the documented fallback.
 */
import type { IncomingMessage, ServerResponse } from 'node:http';
import { randomBytes } from 'node:crypto';
import helmet from 'helmet';
import { maybeContext } from '../common/http.js';

export interface CspOptions {
  scripts: 'nonce' | 'unsafe-inline';
  reportOnly: boolean;
}

export const API_CSP = "default-src 'none'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'";

export function pageCsp(nonce: string, opts: CspOptions, https: boolean): string {
  const script = opts.scripts === 'nonce' ? `'self' 'nonce-${nonce}' 'strict-dynamic'` : `'self' 'unsafe-inline'`;
  const directives = [
    "default-src 'self'",
    `script-src ${script}`,
    // Tailwind/Next inject style attributes and <style> tags; inline styles only (no inline scripts).
    "style-src 'self' 'unsafe-inline'",
    "img-src 'self' data: blob:",
    "font-src 'self' data:",
    "connect-src 'self'",
    "worker-src 'self' blob:",
    "manifest-src 'self'",
    "object-src 'none'",
    "base-uri 'self'",
    "form-action 'self'",
    "frame-ancestors 'none'",
  ];
  if (https) directives.push('upgrade-insecure-requests');
  return directives.join('; ');
}

export function newNonce(): string {
  return randomBytes(16).toString('base64');
}

export const HSTS_VALUE = 'max-age=15552000';

type Mw = (req: IncomingMessage, res: ServerResponse, next: (err?: unknown) => void) => void;

export function securityHeaders(opts: CspOptions): Mw {
  const base = helmet({
    contentSecurityPolicy: false,
    strictTransportSecurity: false,
    xFrameOptions: { action: 'deny' },
    referrerPolicy: { policy: 'same-origin' },
    crossOriginEmbedderPolicy: false,
    crossOriginOpenerPolicy: { policy: 'same-origin' },
    crossOriginResourcePolicy: { policy: 'same-origin' },
    xPoweredBy: false,
  });
  const cspHeader = opts.reportOnly ? 'Content-Security-Policy-Report-Only' : 'Content-Security-Policy';
  return (req, res, next) => {
    base(req, res, (err?: unknown) => {
      if (err) return next(err);
      const ctx = maybeContext(req);
      const https = ctx?.https ?? false;
      res.setHeader('Permissions-Policy', 'camera=(), microphone=(), geolocation=(), payment=(), usb=()');
      if (https) res.setHeader('Strict-Transport-Security', HSTS_VALUE);
      const path = req.url ?? '/';
      if (path === '/api' || path.startsWith('/api/') || path.startsWith('/api?')) {
        res.setHeader(cspHeader, API_CSP);
      } else {
        const nonce = newNonce();
        if (ctx) ctx.nonce = nonce;
        res.setHeader(cspHeader, pageCsp(nonce, opts, https));
      }
      next();
    });
  };
}

export const CSP_RESPONSE_HEADER = (opts: CspOptions) =>
  opts.reportOnly ? 'content-security-policy-report-only' : 'content-security-policy';
