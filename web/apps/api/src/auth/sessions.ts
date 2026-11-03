/**
 * Server-side sessions in web.db. The browser holds only an opaque 256-bit id; the db stores its SHA-256, so a leaked db
 * file does not yield live cookies.
 *
 * Cookie: `__Host-mcx` (HttpOnly, Secure, SameSite=Lax, Path=/) when the request is HTTPS (directly or via a trusted proxy).
 * Over plain HTTP on the LAN browsers refuse `__Host-` cookies without Secure, so the cookie is `mcx` there (same flags minus
 * Secure). Only the name matching the request's scheme is read, so a cookie planted over HTTP is never honoured over HTTPS.
 *
 * Timeouts: user sessions 12 h idle / 7 d absolute (config); anonymous pre-sessions (CSRF before login) 2 h / 24 h.
 * The id rotates on login (the pre-session is deleted and a new id is issued).
 */
import { createHash, randomBytes, timingSafeEqual } from 'node:crypto';
import type { IncomingMessage, ServerResponse } from 'node:http';
import type { StatementSync } from 'node:sqlite';
import type { Clock } from '../common/clock.js';
import { appendSetCookie, parseCookies, serializeCookie } from '../common/http.js';
import type { WebDb } from '../db/web-db.js';

export const SECURE_COOKIE = '__Host-mcx';
export const PLAIN_COOKIE = 'mcx';
const PRE_IDLE_MS = 2 * 3600_000;
const PRE_ABSOLUTE_MS = 24 * 3600_000;
const TOUCH_EVERY_MS = 60_000;

export interface Session {
  idHash: string;
  username: string | null;
  cred: string | null;
  csrf: string;
  createdAt: number;
  lastSeenAt: number;
  idleMs: number;
  absoluteExpiresAt: number;
  ip: string | null;
  userAgent: string | null;
  /** When the user last re-confirmed (step-up) in this session; null = never. */
  stepUpAt: number | null;
}

interface Row {
  id_hash: string;
  username: string | null;
  cred: string | null;
  csrf: string;
  created_at: number;
  last_seen_at: number;
  idle_ms: number;
  absolute_expires_at: number;
  ip: string | null;
  user_agent: string | null;
  step_up_at: number | null;
}

export interface SessionConfig {
  idleHours: number;
  absoluteDays: number;
  cookieSecure: 'auto' | 'always' | 'never';
}

export function hashId(id: string): string {
  return createHash('sha256').update(id).digest('hex');
}

export function newToken(bytes = 32): string {
  return randomBytes(bytes).toString('base64url');
}

export function tokensEqual(a: string, b: string): boolean {
  const x = Buffer.from(a);
  const y = Buffer.from(b);
  return x.length === y.length && timingSafeEqual(x, y);
}

function fromRow(r: Row): Session {
  return {
    idHash: r.id_hash,
    username: r.username,
    cred: r.cred,
    csrf: r.csrf,
    createdAt: r.created_at,
    lastSeenAt: r.last_seen_at,
    idleMs: r.idle_ms,
    absoluteExpiresAt: r.absolute_expires_at,
    ip: r.ip,
    userAgent: r.user_agent,
    stepUpAt: r.step_up_at ?? null,
  };
}

export class SessionService {
  private readonly q: Record<'get' | 'insert' | 'delete' | 'touch' | 'deleteUser' | 'gc' | 'setCred' | 'setStepUp', StatementSync>;

  constructor(
    db: WebDb,
    private readonly clock: Clock,
    private readonly cfg: SessionConfig,
  ) {
    const d = db.db;
    this.q = {
      get: d.prepare('select * from sessions where id_hash = ?'),
      insert: d.prepare(
        `insert into sessions (id_hash, username, cred, csrf, created_at, last_seen_at, idle_ms, absolute_expires_at, ip, user_agent)
         values (?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`,
      ),
      delete: d.prepare('delete from sessions where id_hash = ?'),
      touch: d.prepare('update sessions set last_seen_at = ? where id_hash = ?'),
      deleteUser: d.prepare('delete from sessions where username = ?'),
      gc: d.prepare('delete from sessions where absolute_expires_at <= ? or last_seen_at + idle_ms <= ?'),
      setCred: d.prepare('update sessions set cred = ? where id_hash = ?'),
      setStepUp: d.prepare('update sessions set step_up_at = ? where id_hash = ?'),
    };
  }

  get userIdleMs(): number {
    return this.cfg.idleHours * 3600_000;
  }
  get userAbsoluteMs(): number {
    return this.cfg.absoluteDays * 86400_000;
  }

  secureFor(https: boolean): boolean {
    return this.cfg.cookieSecure === 'always' ? true : this.cfg.cookieSecure === 'never' ? false : https;
  }

  cookieName(https: boolean): string {
    return this.secureFor(https) ? SECURE_COOKIE : PLAIN_COOKIE;
  }

  /** The live session of this request, or undefined (expired rows are deleted on sight). */
  read(req: IncomingMessage, https: boolean): Session | undefined {
    const id = parseCookies(req.headers.cookie).get(this.cookieName(https));
    if (!id || id.length < 32 || id.length > 128) return undefined;
    return this.byHash(hashId(id));
  }

  /** A live session by its stored hash (expired rows are deleted on sight). */
  byHash(idHash: string): Session | undefined {
    const row = this.q.get.get(idHash) as Row | undefined;
    if (!row) return undefined;
    const s = fromRow(row);
    const now = this.clock.now();
    if (now >= s.absoluteExpiresAt || now >= s.lastSeenAt + s.idleMs) {
      this.q.delete.run(s.idHash);
      return undefined;
    }
    return s;
  }

  /** Slides the idle timeout (written at most once a minute). */
  touch(s: Session): void {
    const now = this.clock.now();
    if (now - s.lastSeenAt < TOUCH_EVERY_MS) return;
    this.q.touch.run(now, s.idHash);
    s.lastSeenAt = now;
  }

  private create(
    username: string | null,
    cred: string | null,
    meta: { ip: string; userAgent?: string },
  ): { id: string; session: Session } {
    const id = newToken(32);
    const now = this.clock.now();
    const user = username !== null;
    const s: Session = {
      idHash: hashId(id),
      username,
      cred,
      csrf: newToken(32),
      createdAt: now,
      lastSeenAt: now,
      idleMs: user ? this.userIdleMs : PRE_IDLE_MS,
      absoluteExpiresAt: now + (user ? this.userAbsoluteMs : PRE_ABSOLUTE_MS),
      ip: meta.ip,
      userAgent: meta.userAgent?.slice(0, 300) ?? null,
      stepUpAt: null,
    };
    this.q.insert.run(
      s.idHash,
      s.username,
      s.cred,
      s.csrf,
      s.createdAt,
      s.lastSeenAt,
      s.idleMs,
      s.absoluteExpiresAt,
      s.ip,
      s.userAgent,
    );
    return { id, session: s };
  }

  createAnonymous(meta: { ip: string; userAgent?: string }): { id: string; session: Session } {
    return this.create(null, null, meta);
  }

  createUser(
    username: string,
    cred: string,
    meta: { ip: string; userAgent?: string },
  ): { id: string; session: Session } {
    return this.create(username, cred, meta);
  }

  setCred(s: Session, cred: string): void {
    this.q.setCred.run(cred, s.idHash);
    s.cred = cred;
  }

  /** Records a successful step-up (re-confirmation) for this session. */
  setStepUp(s: Session, at: number): void {
    this.q.setStepUp.run(at, s.idHash);
    s.stepUpAt = at;
  }

  /** Whether this session stepped up within `windowMs`. */
  steppedUp(s: Session | undefined, windowMs: number): boolean {
    return Boolean(s?.stepUpAt && this.clock.now() - s.stepUpAt < windowMs && this.clock.now() >= s.stepUpAt);
  }

  destroy(s: Session | undefined): void {
    if (s) this.q.delete.run(s.idHash);
  }

  destroyUser(username: string): void {
    this.q.deleteUser.run(username);
  }

  gc(): void {
    const now = this.clock.now();
    this.q.gc.run(now, now);
  }

  setCookie(res: ServerResponse, id: string, s: Session, https: boolean): void {
    const secure = this.secureFor(https);
    const maxAge = s.username !== null ? (s.absoluteExpiresAt - this.clock.now()) / 1000 : undefined;
    appendSetCookie(res, serializeCookie(this.cookieName(https), id, { secure, maxAgeSeconds: maxAge }));
  }

  clearCookie(res: ServerResponse, https: boolean): void {
    appendSetCookie(
      res,
      serializeCookie(this.cookieName(https), '', { secure: this.secureFor(https), maxAgeSeconds: 0 }),
    );
  }
}
