/**
 * Nest-native auth (docs/web/api.md §2): CSRF pre-sessions, password + TOTP login with lockout, logout, `me`, and TOTP
 * enrolment. While no user exists (first-run setup mode, see setup.service.ts) login answers 409. Errors are generic: a wrong password, an unknown user, a missing or wrong TOTP code and a disabled account all
 * answer the same 401 "invalid credentials", so the login form should always offer the code field.
 */
import type { IncomingMessage, ServerResponse } from 'node:http';
import type { StatementSync } from 'node:sqlite';
import {
  STEP_UP_MS,
  type CsrfResponse,
  type LoginRequest,
  type LoginResponse,
  type Me,
  type StepUpRequest,
  type StepUpResponse,
  type TotpEnrollRequest,
  type TotpEnrollResponse,
  type TotpVerifyRequest,
  type TotpVerifyResponse,
} from '@mineclaw/contracts';
import type { AuditService } from '../audit/audit.service.js';
import type { Clock } from '../common/clock.js';
import { Errors } from '../common/errors.js';
import { header, type RequestContext } from '../common/http.js';
import type { Logger } from '../common/logger.js';
import type { WebDb } from '../db/web-db.js';
import type { PasswordVerifier } from './passwords.js';
import { LoginLimiter, WindowLimiter } from './rate-limit.js';
import type { Session, SessionService } from './sessions.js';
import { newTotpSecret, otpauthUrl, verifyTotp } from './totp.js';
import { credFingerprint, normalizeUsername, type UserRecord, type UsersStore } from './users-store.js';

export interface AuthState {
  /** The request's session (anonymous pre-session or signed in). */
  session?: Session;
  /** Set only for a signed-in session whose user still exists, is enabled and has the same password. */
  user?: UserRecord;
}

const TOTP_PENDING_MS = 10 * 60_000;

/** What a sign-in or step-up proved: the TOTP step it used (stored against replays), or null without TOTP. */
interface Proof {
  totpStep: number | null;
}

/** One login attempt: its audit base, the audited username (null when unknown) and its lockout keys. */
interface LoginAttempt {
  base: { ip: string; userAgent: string | undefined; method: string; route: string; requestId: string };
  auditName: string | null;
  username: string;
  keys: string[];
  ip: string;
}

/** The audit summary of a failed step-up (never the code or the password). */
function stepUpFailure(user: UserRecord, body: StepUpRequest): string {
  if (!user.totp) return 'step-up: wrong password';
  return body.code ? 'step-up: wrong code' : 'step-up: a code is required';
}

export class AuthService {
  private readonly attempts: WindowLimiter;
  private readonly preSessions: WindowLimiter;
  private readonly q: Record<'pendingGet' | 'pendingPut' | 'pendingDel' | 'usedGet' | 'usedPut', StatementSync>;

  constructor(
    private readonly users: UsersStore,
    readonly sessions: SessionService,
    private readonly limiter: LoginLimiter,
    private readonly passwords: PasswordVerifier,
    private readonly audit: AuditService,
    db: WebDb,
    private readonly clock: Clock,
    private readonly log: Logger,
    ipAttemptsPerMinute: number,
  ) {
    this.attempts = new WindowLimiter(clock, ipAttemptsPerMinute, 60_000);
    this.preSessions = new WindowLimiter(clock, 60, 60_000);
    this.q = {
      pendingGet: db.db.prepare('select secret, expires_at from totp_pending where username = ?'),
      pendingPut: db.db.prepare(
        'insert into totp_pending (username, secret, expires_at) values (?, ?, ?) on conflict(username) do update set secret = excluded.secret, expires_at = excluded.expires_at',
      ),
      pendingDel: db.db.prepare('delete from totp_pending where username = ?'),
      usedGet: db.db.prepare('select step from totp_used where username = ?'),
      usedPut: db.db.prepare(
        'insert into totp_used (username, step) values (?, ?) on conflict(username) do update set step = excluded.step',
      ),
    };
  }

  /** Resolves the session and user of a request; stale sessions (user removed, disabled, password reset) are ended. */
  resolve(req: IncomingMessage, ctx: RequestContext): AuthState {
    const session = this.sessions.read(req, ctx.https);
    if (!session) return {};
    if (session.username === null) return { session };
    const user = this.users.get(session.username);
    if (!user || user.disabled || session.cred !== credFingerprint(user)) {
      this.sessions.destroy(session);
      return {};
    }
    this.sessions.touch(session);
    ctx.username = user.username;
    return { session, user };
  }

  me(state: Required<AuthState>): Me {
    const s = state.session;
    return {
      username: state.user.username,
      player: state.user.player ?? null,
      roles: [...state.user.roles],
      totpEnrolled: Boolean(state.user.totp),
      sessionCreatedAt: new Date(s.createdAt).toISOString(),
      idleExpiresAt: new Date(Math.min(s.lastSeenAt + s.idleMs, s.absoluteExpiresAt)).toISOString(),
      absoluteExpiresAt: new Date(s.absoluteExpiresAt).toISOString(),
      stepUpUntil: this.sessions.steppedUp(s, STEP_UP_MS) ? new Date(s.stepUpAt! + STEP_UP_MS).toISOString() : null,
    };
  }

  /** Whether the request's session has a fresh step-up (routes with `stepUp` need one). */
  steppedUp(state: AuthState): boolean {
    return this.sessions.steppedUp(state.session, STEP_UP_MS);
  }

  /**
   * POST /api/auth/step-up: re-confirms the signed-in user for `STEP_UP_MS`. With TOTP enrolled only the current code counts
   * (a password is refused, so a stolen session + password is not enough); without TOTP the password. Failures count toward
   * the same lockout as login; every attempt is audited (never the code or the password).
   */
  async stepUp(
    req: IncomingMessage,
    ctx: RequestContext,
    state: Required<AuthState>,
    body: StepUpRequest,
  ): Promise<StepUpResponse> {
    const user = state.user;
    const wait = this.attempts.hit(ctx.ip);
    if (wait > 0) throw Errors.rateLimited(wait, 'too many attempts');
    const k = LoginLimiter.keys(ctx.ip, user.username);
    const keys = [k.ip, k.user];
    const lockedMs = this.limiter.lockedFor(keys);
    if (lockedMs > 0) throw Errors.locked(lockedMs);
    const base = {
      ip: ctx.ip,
      userAgent: header(req, 'user-agent'),
      username: user.username,
      player: user.player,
      method: 'POST',
      route: 'auth.stepUp',
      requestId: ctx.requestId,
    };
    const proof = await this.stepUpProof(user, body);
    if (!proof) {
      this.limiter.fail(keys);
      this.audit.record({ ...base, kind: 'step_up', ok: false, status: 401, summary: stepUpFailure(user, body) });
      throw Errors.invalidCredentials();
    }
    if (proof.totpStep !== null) this.q.usedPut.run(user.username, proof.totpStep);
    this.limiter.reset([k.user]);
    const now = this.clock.now();
    this.sessions.setStepUp(state.session, now);
    this.audit.record({
      ...base,
      kind: 'step_up',
      ok: true,
      status: 200,
      summary: `step-up (${user.totp ? 'TOTP' : 'password'})`,
    });
    return { ok: true, stepUpUntil: new Date(now + STEP_UP_MS).toISOString() };
  }

  /** With TOTP enrolled only a fresh code counts; without, the password. Null when neither proves the user. */
  private async stepUpProof(user: UserRecord, body: StepUpRequest): Promise<Proof | null> {
    if (user.totp) {
      const step = body.code ? this.freshTotpStep(user.username, user.totp.secret, body.code) : null;
      return step === null ? null : { totpStep: step };
    }
    if (!body.password) return null;
    return (await this.passwords.verify(user.passwordHash, body.password)) ? { totpStep: null } : null;
  }

  /** The TOTP step a code proves, when it is newer than the user's last used step (no replays); null otherwise. */
  private freshTotpStep(username: string, secret: string, code: string | undefined): number | null {
    const step = code ? verifyTotp(secret, code, this.clock.now()) : null;
    const last = (this.q.usedGet.get(username) as { step: number } | undefined)?.step ?? -1;
    return step !== null && step > last ? step : null;
  }

  /** GET /api/auth/csrf: the token of the current session, creating an anonymous pre-session when there is none. */
  csrf(res: ServerResponse, req: IncomingMessage, ctx: RequestContext, state: AuthState): CsrfResponse {
    if (state.session) return { csrfToken: state.session.csrf };
    const wait = this.preSessions.hit(ctx.ip);
    if (wait > 0) throw Errors.rateLimited(wait);
    const { id, session } = this.sessions.createAnonymous({ ip: ctx.ip, userAgent: header(req, 'user-agent') });
    this.sessions.setCookie(res, id, session, ctx.https);
    return { csrfToken: session.csrf };
  }

  async login(
    res: ServerResponse,
    req: IncomingMessage,
    ctx: RequestContext,
    state: AuthState,
    body: LoginRequest,
  ): Promise<LoginResponse> {
    const username = normalizeUsername(body.username);
    const ua = header(req, 'user-agent');
    const base = { ip: ctx.ip, userAgent: ua, method: 'POST', route: 'auth.login', requestId: ctx.requestId };
    const auditName = this.users.get(username) ? username : null;

    const wait = this.attempts.hit(ctx.ip);
    if (wait > 0) throw Errors.rateLimited(wait, 'too many login attempts');
    // Setup mode (no users at all): sign-in is off until the first account is created at /setup.
    if (this.users.setupNeeded()) throw Errors.conflict('first-run setup is not complete; open /setup');

    const k = LoginLimiter.keys(ctx.ip, username);
    const keys = [k.ip, k.user];
    const attempt: LoginAttempt = { base, auditName, username, keys, ip: ctx.ip };
    this.refuseLockedLogin(attempt);

    const user = this.users.get(username);
    const proof = await this.loginProof(user, body);
    if (!proof || !user) this.failLogin(attempt);

    this.limiter.reset(keys);
    if (proof.totpStep !== null) this.q.usedPut.run(user.username, proof.totpStep);
    // Rotate: the pre-session (or an older session) is gone; a fresh id and CSRF token are issued.
    this.sessions.destroy(state.session);
    const { id, session } = this.sessions.createUser(user.username, credFingerprint(user), {
      ip: ctx.ip,
      userAgent: ua,
    });
    this.sessions.setCookie(res, id, session, ctx.https);
    ctx.username = user.username;
    this.audit.record({
      ...base,
      kind: 'login',
      ok: true,
      username: user.username,
      player: user.player,
      status: 200,
      summary: 'signed in',
    });
    return { user: this.me({ session, user }), csrfToken: session.csrf };
  }

  /** A locked IP or username is refused (audited as a failed login) before any password check. */
  private refuseLockedLogin(a: LoginAttempt): void {
    const lockedMs = this.limiter.lockedFor(a.keys);
    if (lockedMs <= 0) return;
    this.audit.record({
      ...a.base,
      kind: 'login_failed',
      ok: false,
      username: a.auditName,
      status: 423,
      summary: `locked out (${a.username})`,
    });
    throw Errors.locked(lockedMs);
  }

  /**
   * The password (checked even for an unknown user, against a dummy hash), an enabled account, and with TOTP enrolled a
   * fresh code. Null when any of them fails.
   */
  private async loginProof(user: UserRecord | undefined, body: LoginRequest): Promise<Proof | null> {
    const ok = await this.passwords.verify(user?.passwordHash, body.password);
    if (!ok || !user || user.disabled) return null;
    if (!user.totp) return { totpStep: null };
    const step = this.freshTotpStep(user.username, user.totp.secret, body.totp);
    return step === null ? null : { totpStep: step };
  }

  /** Counts the failure toward the lockout, audits it (and a new lockout), and answers the generic 401. */
  private failLogin(a: LoginAttempt): never {
    const { newlyLocked, lockMs } = this.limiter.fail(a.keys);
    this.audit.record({
      ...a.base,
      kind: 'login_failed',
      ok: false,
      username: a.auditName,
      status: 401,
      summary: `failed login as "${a.username}"`,
    });
    if (newlyLocked.length > 0) {
      this.audit.record({
        ...a.base,
        kind: 'lockout',
        ok: false,
        username: a.auditName,
        status: 423,
        summary: `lockout ${Math.round(lockMs / 1000)} s on ${newlyLocked.map((x) => x.split(':')[0]).join(' + ')} (${a.username})`,
      });
      this.log.warn({ ip: a.ip, lockMs }, 'login lockout');
    }
    throw Errors.invalidCredentials();
  }

  logout(res: ServerResponse, req: IncomingMessage, ctx: RequestContext, state: Required<AuthState>): { ok: true } {
    this.sessions.destroy(state.session);
    this.sessions.clearCookie(res, ctx.https);
    this.audit.record({
      kind: 'logout',
      ok: true,
      ip: ctx.ip,
      userAgent: header(req, 'user-agent'),
      username: state.user.username,
      player: state.user.player,
      method: 'POST',
      route: 'auth.logout',
      status: 200,
      summary: 'signed out',
      requestId: ctx.requestId,
    });
    return { ok: true };
  }

  async totpEnroll(
    req: IncomingMessage,
    ctx: RequestContext,
    state: Required<AuthState>,
    body: TotpEnrollRequest,
  ): Promise<TotpEnrollResponse> {
    const user = state.user;
    const k = LoginLimiter.keys(ctx.ip, user.username);
    const keys = [k.ip, k.user];
    const lockedMs = this.limiter.lockedFor(keys);
    if (lockedMs > 0) throw Errors.locked(lockedMs);
    const base = {
      ip: ctx.ip,
      userAgent: header(req, 'user-agent'),
      username: user.username,
      player: user.player,
      method: 'POST',
      route: 'auth.totpEnroll',
      requestId: ctx.requestId,
    };
    if (!(await this.passwords.verify(user.passwordHash, body.password))) {
      this.limiter.fail(keys);
      this.audit.record({
        ...base,
        kind: 'totp_enroll',
        ok: false,
        status: 401,
        summary: 'TOTP enrolment: wrong password',
      });
      throw Errors.invalidCredentials();
    }
    const secret = newTotpSecret();
    const expiresAt = this.clock.now() + TOTP_PENDING_MS;
    this.q.pendingPut.run(user.username, secret, expiresAt);
    this.audit.record({ ...base, kind: 'totp_enroll', ok: true, status: 200, summary: 'TOTP enrolment started' });
    return { otpauthUrl: otpauthUrl(secret, user.username), secret, expiresAt: new Date(expiresAt).toISOString() };
  }

  totpVerify(
    req: IncomingMessage,
    ctx: RequestContext,
    state: Required<AuthState>,
    body: TotpVerifyRequest,
  ): TotpVerifyResponse {
    const user = state.user;
    const k = LoginLimiter.keys(ctx.ip, user.username);
    const keys = [k.ip, k.user];
    const lockedMs = this.limiter.lockedFor(keys);
    if (lockedMs > 0) throw Errors.locked(lockedMs);
    const base = {
      ip: ctx.ip,
      userAgent: header(req, 'user-agent'),
      username: user.username,
      player: user.player,
      method: 'POST',
      route: 'auth.totpVerify',
      requestId: ctx.requestId,
    };
    const pending = this.q.pendingGet.get(user.username) as { secret: string; expires_at: number } | undefined;
    const now = this.clock.now();
    const step = pending && pending.expires_at > now ? verifyTotp(pending.secret, body.code, now) : null;
    if (!pending || step === null) {
      if (pending) this.limiter.fail(keys);
      this.audit.record({
        ...base,
        kind: 'totp_verify',
        ok: false,
        status: 200,
        summary: pending ? 'wrong TOTP code' : 'no pending TOTP enrolment',
      });
      return { ok: false, totpEnrolled: Boolean(user.totp) };
    }
    this.users.update(user.username, (u) => ({
      ...u,
      totp: { secret: pending.secret, enrolledAt: new Date(now).toISOString() },
    }));
    this.q.pendingDel.run(user.username);
    this.q.usedPut.run(user.username, step);
    this.limiter.reset([k.user]);
    this.audit.record({ ...base, kind: 'totp_verify', ok: true, status: 200, summary: 'TOTP activated' });
    return { ok: true, totpEnrolled: true };
  }

  /** For long-lived streams: is the session still signed in (not expired, logged out, user removed or password reset)? */
  stillSignedIn(idHash: string): boolean {
    const s = this.sessions.byHash(idHash);
    if (!s?.username) return false;
    const u = this.users.get(s.username);
    return Boolean(u && !u.disabled && s.cred === credFingerprint(u));
  }
}
