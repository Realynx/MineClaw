/** Auth, sessions, CSRF, TOTP and the web audit log (all Nest-native; nothing here reaches the bridge). */
import { z } from 'zod';
import { IsoDateTime, QInt } from './common.js';

/** The roles (architecture.md, Security model). */
export const Role = z.enum([
  'viewer',
  'chat',
  'commands',
  'admin',
  /** Design Studio writes (sessions, messages, saves); `admin` implies it. */
  'design',
  /** Story Mode writes (colonies, orders, builds, storage, bills, directives); `admin` implies it. */
  'story',
]);
export type Role = z.infer<typeof Role>;

/**
 * What a route requires:
 * - `public`: no session (login, csrf, health).
 * - `authenticated`: any signed-in user (logout, me, totp).
 * - a `Role`: see `hasRole`.
 */
export const RouteAccess = z.union([z.literal('public'), z.literal('authenticated'), Role]);
export type RouteAccess = z.infer<typeof RouteAccess>;

/**
 * Role semantics (resolves an ambiguity in architecture.md): every signed-in user can read (`viewer` is implied by any role);
 * `chat`, `commands` and `admin` are independent grants and never imply each other. `commands` only has an effect together
 * with `chat` (it lets the user's viewer chat lines arm CommandGate request grants). `design` (Design Studio writes) and
 * `story` (Story Mode writes) are the grants `admin` implies.
 */
export function hasRole(userRoles: readonly Role[], required: RouteAccess): boolean {
  if (required === 'public') return true;
  if (required === 'authenticated' || required === 'viewer') return userRoles.length > 0;
  if (required === 'design') return userRoles.includes('design') || userRoles.includes('admin');
  if (required === 'story') return userRoles.includes('story') || userRoles.includes('admin');
  return userRoles.includes(required);
}

/** The signed-in user. */
export const Me = z.object({
  username: z.string(),
  player: z.string().nullish().describe('linked Minecraft player (poofimafox, kiwi); null = cannot chat'),
  roles: z.array(Role),
  totpEnrolled: z.boolean(),
  sessionCreatedAt: IsoDateTime,
  idleExpiresAt: IsoDateTime.describe('12 h idle timeout, slides on activity'),
  absoluteExpiresAt: IsoDateTime.describe('7 d absolute timeout'),
});
export type Me = z.infer<typeof Me>;

/** GET /api/auth/csrf: the token to send as `X-CSRF-Token` on every non-GET. Creates an anonymous pre-session if needed. */
export const CsrfResponse = z.object({ csrfToken: z.string().min(16) });
export type CsrfResponse = z.infer<typeof CsrfResponse>;

/** POST /api/auth/login. `totp` is required when the user has TOTP enrolled. Errors are generic ("invalid credentials"). */
export const LoginRequest = z
  .object({
    username: z.string().min(1).max(64),
    password: z.string().min(1).max(256),
    totp: z
      .string()
      .regex(/^\d{6}$/)
      .optional(),
  })
  .strict();
export type LoginRequest = z.infer<typeof LoginRequest>;

/** Login rotates the session id (new `__Host-mcx` cookie) and the CSRF token. */
export const LoginResponse = z.object({ user: Me, csrfToken: z.string().min(16) });
export type LoginResponse = z.infer<typeof LoginResponse>;

export const LogoutResponse = z.object({ ok: z.literal(true) });
export type LogoutResponse = z.infer<typeof LogoutResponse>;

/** POST /api/auth/totp/enroll: re-authenticates with the password, returns a pending secret (not active until verified). */
export const TotpEnrollRequest = z.object({ password: z.string().min(1).max(256) }).strict();
export type TotpEnrollRequest = z.infer<typeof TotpEnrollRequest>;

export const TotpEnrollResponse = z.object({
  otpauthUrl: z.string().startsWith('otpauth://totp/').describe('shown once as a QR code; never logged'),
  secret: z.string().describe('base32, for manual entry; shown once'),
  expiresAt: IsoDateTime.describe('the pending enrolment lapses if not verified by then'),
});
export type TotpEnrollResponse = z.infer<typeof TotpEnrollResponse>;

/** POST /api/auth/totp/verify: activates the pending secret. */
export const TotpVerifyRequest = z
  .object({
    code: z.string().regex(/^\d{6}$/),
  })
  .strict();
export type TotpVerifyRequest = z.infer<typeof TotpVerifyRequest>;

export const TotpVerifyResponse = z.object({ ok: z.boolean(), totpEnrolled: z.boolean() });
export type TotpVerifyResponse = z.infer<typeof TotpVerifyResponse>;

/** Usernames: lower case, 1-64 chars of a-z 0-9 _ . - (the users file and the CLI use the same rule). */
export const USERNAME_PATTERN = /^[a-z0-9][a-z0-9_.-]{0,63}$/;
/** Minecraft player names: 1-16 chars of A-Z a-z 0-9 _. */
export const PLAYER_PATTERN = /^[A-Za-z0-9_]{1,16}$/;
/** One password rule for the setup page and the users CLI. */
export const PASSWORD_MIN_LENGTH = 12;
export const PASSWORD_MAX_LENGTH = 256;

/**
 * GET /api/setup: first-run setup. `needed` is true while the users file holds no users; the app is then in setup mode
 * (every page goes to `/setup`, sign-in is off). `allowedHere` says whether this request may complete it: only a direct
 * LAN or loopback client, never one through a trusted proxy or a tunnel, unless `Web:Setup:AllowProxied`.
 */
export const SetupStatus = z.object({
  needed: z.boolean(),
  allowedHere: z.boolean().describe('false through cloudflared / a trusted proxy or from a non-LAN address'),
});
export type SetupStatus = z.infer<typeof SetupStatus>;

/** POST /api/setup: creates the first account (atomically: only while there are no users) and signs it in. */
export const SetupRequest = z
  .object({
    username: z
      .string()
      .trim()
      .toLowerCase()
      .regex(USERNAME_PATTERN, 'usernames are 1-64 characters of a-z 0-9 _ . - and start with a letter or digit'),
    password: z
      .string()
      .min(PASSWORD_MIN_LENGTH, `at least ${PASSWORD_MIN_LENGTH} characters`)
      .max(PASSWORD_MAX_LENGTH),
    player: z
      .string()
      .regex(PLAYER_PATTERN, 'Minecraft names are 1-16 characters of A-Z a-z 0-9 _')
      .nullish()
      .describe('linked Minecraft player; null = cannot chat'),
    roles: z.array(Role).min(1).max(6),
  })
  .strict();
export type SetupRequest = z.infer<typeof SetupRequest>;

/** What the web audit log records (web.db). */
export const WebAuditKind = z.enum([
  'setup',
  'login',
  'login_failed',
  'logout',
  'lockout',
  'totp_enroll',
  'totp_verify',
  'chat',
  'memory_edit',
  'rebuild',
  'rebuild_cancel',
  'backup',
  'compact',
  'shard_import',
  'shard_import_preview',
  'shard_export',
  'admin_other',
  'design',
  'story',
]);
export type WebAuditKind = z.infer<typeof WebAuditKind>;

export const WebAuditEntry = z.object({
  id: z.number().int(),
  at: IsoDateTime,
  username: z.string().nullish().describe('null for a failed login with an unknown user'),
  player: z.string().nullish(),
  ip: z.string(),
  userAgent: z.string().nullish(),
  kind: WebAuditKind,
  method: z.string().nullish(),
  route: z.string().nullish().describe('the route id from the route table'),
  target: z.string().nullish().describe('bot, memory kind/id, job id, shard name…'),
  ok: z.boolean(),
  status: z.number().int().nullish().describe('HTTP status returned'),
  summary: z.string().describe('one line, e.g. the chat text (truncated to 500 chars) or "update fact 12"'),
  requestId: z.string().nullish(),
});
export type WebAuditEntry = z.infer<typeof WebAuditEntry>;

/** GET /api/audit (admin): newest first. */
export const WebAuditQuery = z.object({
  offset: QInt.min(0).default(0),
  limit: QInt.min(1).max(500).default(100),
  user: z.string().optional(),
  kind: WebAuditKind.optional(),
  since: IsoDateTime.optional(),
  ok: z.enum(['true', 'false']).optional(),
});
export type WebAuditQuery = z.infer<typeof WebAuditQuery>;

export const WebAuditPage = z.object({
  total: z.number().int().nonnegative(),
  offset: z.number().int().nonnegative(),
  limit: z.number().int().positive(),
  items: z.array(WebAuditEntry),
});
export type WebAuditPage = z.infer<typeof WebAuditPage>;
