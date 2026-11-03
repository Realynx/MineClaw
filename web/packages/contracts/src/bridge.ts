/**
 * Nest ↔ bridge request signing (architecture.md, Security model › Nest ↔ bridge) and the Nest config schema.
 * Pure helpers only (no crypto here, so the package stays browser-safe): Nest computes the HMAC with node:crypto, the bridge
 * with System.Security.Cryptography. Test vectors are in docs/web/api.md §3 and test/bridge.test.ts.
 */
import { z } from 'zod';
import type { Role } from './auth.js';

export const BRIDGE_HEADERS = {
  ts: 'X-MCX-Ts',
  nonce: 'X-MCX-Nonce',
  sig: 'X-MCX-Sig',
  user: 'X-MCX-User',
  player: 'X-MCX-Player',
  roles: 'X-MCX-Roles',
  requestId: 'X-MCX-Request-Id',
} as const;

/** Max clock skew the bridge accepts, and the nonce replay window. */
export const BRIDGE_MAX_SKEW_S = 30;
export const BRIDGE_NONCE_TTL_S = 300;

export interface SigningParts {
  /** Upper-case HTTP method. */
  method: string;
  /** The request target exactly as sent on the wire: path plus `?query` when there is one (no scheme/host, no fragment). */
  pathAndQuery: string;
  /** Unix seconds, decimal (the `X-MCX-Ts` header value). */
  timestamp: string;
  /** 32 lowercase hex chars (16 random bytes; the `X-MCX-Nonce` value). */
  nonce: string;
  /** Lowercase hex SHA-256 of the exact body bytes (of the empty string when there is no body). */
  bodySha256: string;
  /** `X-MCX-User`; "" when absent. */
  user: string;
  /** `X-MCX-Player`; "" when absent. */
  player: string;
  /** `X-MCX-Roles`: `canonicalRoles(...)`; "" when absent. */
  roles: string;
}

/** SHA-256 of the empty body, lowercase hex. */
export const EMPTY_BODY_SHA256 = 'e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855';

/** Roles header value: lowercase, de-duplicated, sorted, comma-joined, no spaces ("admin,chat,viewer"). */
export function canonicalRoles(roles: readonly Role[]): string {
  return [...new Set(roles.map((r) => r.toLowerCase()))].sort().join(',');
}

/**
 * The string the HMAC covers. Eight lines joined by "\n" (no trailing newline):
 *   METHOD \n PATH?QUERY \n TIMESTAMP \n NONCE \n SHA256(BODY) \n USER \n PLAYER \n ROLES
 * Signature = lowercase hex HMAC-SHA256(key = UTF-8 bytes of `Bridge:Key`, message = UTF-8 bytes of this string).
 */
export function bridgeSigningString(p: SigningParts): string {
  for (const v of [p.user, p.player, p.roles]) {
    if (/[\r\n]/.test(v)) throw new Error('identity headers must not contain CR or LF');
  }
  return [p.method.toUpperCase(), p.pathAndQuery, p.timestamp, p.nonce, p.bodySha256, p.user, p.player, p.roles].join(
    '\n',
  );
}

/**
 * Nest's runtime config (keys only; values come from `/etc/mineclaw-web/*.json` or env `MINECLAW_WEB_Section__Key` on
 * CT 114, `.env.local` in dev). `Bridge:Key` is a secret: never logged, never committed.
 */
export const NestConfig = z.object({
  web: z.object({
    port: z.number().int().default(8080),
    bind: z.string().default('0.0.0.0'),
    nextUrl: z.string().default('http://127.0.0.1:3000'),
    allowedOrigins: z.array(z.string()).min(1),
    trustProxy: z.boolean().default(false),
    trustedProxies: z
      .array(z.string())
      .default([])
      .describe('source IPs/CIDRs of cloudflared whose CF-Connecting-IP is honoured'),
    cloudflareAccess: z.object({ teamDomain: z.string(), aud: z.string() }).nullish(),
    usersFile: z.string().default('/etc/mineclaw-web/users.json'),
    dbPath: z.string().default('/var/lib/mineclaw-web/web.db'),
    sessionIdleHours: z.number().default(12),
    sessionAbsoluteDays: z.number().default(7),
  }),
  bridge: z.object({
    url: z.string().default('http://127.0.0.1:8766').describe('loopback only; plain HTTP (no TLS on loopback)'),
    key: z.string().min(32).describe('HMAC key (secret)'),
    timeoutMs: z.number().int().default(15000),
  }),
});
export type NestConfig = z.infer<typeof NestConfig>;
