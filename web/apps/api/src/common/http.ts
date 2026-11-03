/** Small HTTP helpers shared by the dispatcher, auth and the proxy (no Express types needed). */
import type { IncomingMessage } from 'node:http';
import { Errors } from './errors.js';

/** Per-request state attached by the context middleware (also set for WebSocket upgrades). */
export interface RequestContext {
  requestId: string;
  /** Client IP (CF-Connecting-IP / X-Forwarded-For only from a trusted proxy). */
  ip: string;
  /** HTTPS as the browser sees it (TLS socket, or X-Forwarded-Proto https from a trusted proxy). */
  https: boolean;
  /** CSP nonce for proxied Next pages. */
  nonce?: string;
  /** Filled once the session is resolved (for logs). */
  username?: string;
}

const contexts = new WeakMap<IncomingMessage, RequestContext>();

export function setContext(req: IncomingMessage, ctx: RequestContext): void {
  contexts.set(req, ctx);
}

export function contextOf(req: IncomingMessage): RequestContext {
  const ctx = contexts.get(req);
  if (!ctx) throw new Error('request context missing (context middleware not installed)');
  return ctx;
}

export function maybeContext(req: IncomingMessage): RequestContext | undefined {
  return contexts.get(req);
}

/** Reads the whole body up to `limit` bytes (413 beyond it). */
export async function readBody(req: IncomingMessage, limit: number): Promise<Buffer> {
  const declared = Number(req.headers['content-length'] ?? NaN);
  if (Number.isFinite(declared) && declared > limit) {
    req.resume();
    throw Errors.tooLarge();
  }
  const chunks: Buffer[] = [];
  let size = 0;
  for await (const chunk of req) {
    const buf = chunk as Buffer;
    size += buf.length;
    if (size > limit) {
      req.resume();
      throw Errors.tooLarge();
    }
    chunks.push(buf);
  }
  return Buffer.concat(chunks, size);
}

/** Last value wins for repeated keys; values stay strings (schemas coerce). */
export function parseQuery(url: string | undefined): Record<string, string> {
  const q = (url ?? '').indexOf('?');
  if (q < 0) return {};
  const out: Record<string, string> = {};
  for (const [k, v] of new URLSearchParams(url!.slice(q + 1))) out[k] = v;
  return out;
}

export function pathOf(url: string | undefined): string {
  const u = url ?? '/';
  const q = u.indexOf('?');
  return q < 0 ? u : u.slice(0, q);
}

export function parseCookies(header: string | undefined): Map<string, string> {
  const out = new Map<string, string>();
  if (!header) return out;
  for (const part of header.split(';')) {
    const eq = part.indexOf('=');
    if (eq <= 0) continue;
    const name = part.slice(0, eq).trim();
    let value = part.slice(eq + 1).trim();
    if (value.startsWith('"') && value.endsWith('"')) value = value.slice(1, -1);
    if (!out.has(name)) out.set(name, value);
  }
  return out;
}

export interface CookieOptions {
  maxAgeSeconds?: number;
  secure: boolean;
  httpOnly?: boolean;
}

export function serializeCookie(name: string, value: string, o: CookieOptions): string {
  const parts = [`${name}=${value}`, 'Path=/', 'SameSite=Lax'];
  if (o.httpOnly !== false) parts.push('HttpOnly');
  if (o.secure) parts.push('Secure');
  if (o.maxAgeSeconds !== undefined) parts.push(`Max-Age=${Math.max(0, Math.floor(o.maxAgeSeconds))}`);
  return parts.join('; ');
}

export function header(req: IncomingMessage, name: string): string | undefined {
  const v = req.headers[name.toLowerCase()];
  return Array.isArray(v) ? v[0] : v;
}

/** Appends a Set-Cookie without clobbering earlier ones. */
export function appendSetCookie(
  res: { getHeader(n: string): unknown; setHeader(n: string, v: string[]): unknown },
  cookie: string,
): void {
  const prev = res.getHeader('Set-Cookie');
  const list = Array.isArray(prev) ? (prev as string[]) : typeof prev === 'string' ? [prev] : [];
  res.setHeader('Set-Cookie', [...list, cookie]);
}
