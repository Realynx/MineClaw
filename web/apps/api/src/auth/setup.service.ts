/**
 * First-run setup (docs/web/api.md §2, Setup). While the users file holds no users the app is in setup mode: pages go to
 * `/setup` (see `setupGate`), sign-in is off, and `POST /api/setup` creates the first account and signs it in.
 *
 * Whoever completes it first becomes that account, so it is fenced:
 * - only a direct LAN/loopback client (`notDirectLan`), never through cloudflared or another proxy, unless
 *   `Web:Setup:AllowProxied`;
 * - CSRF + Origin like every write (the dispatcher checks them before this runs);
 * - rate limited per IP (`Web:Setup:AttemptsPerMinute`);
 * - atomic: `UsersStore.createFirst` re-reads the file under its lock, so of two simultaneous setups one wins and the other
 *   gets 409;
 * - audited (`setup`), the refusals too.
 */
import type { IncomingMessage, ServerResponse } from 'node:http';
import { canonicalRoles, type LoginResponse, type Role, type SetupRequest, type SetupStatus } from '@mineclaw/contracts';
import type { AuditService } from '../audit/audit.service.js';
import type { Clock } from '../common/clock.js';
import { Errors } from '../common/errors.js';
import { header, type RequestContext } from '../common/http.js';
import type { Logger } from '../common/logger.js';
import { notDirectLan, type TrustedProxies } from '../security/client-ip.js';
import type { AuthService, AuthState } from './auth.service.js';
import { hashPassword } from './passwords.js';
import { WindowLimiter } from './rate-limit.js';
import type { SessionService } from './sessions.js';
import { credFingerprint, UsersExistError, type UsersStore } from './users-store.js';

export interface SetupConfig {
  allowProxied: boolean;
  attemptsPerMinute: number;
}

/** Pages a browser loads (no `/api`, no `/_next` assets, no files with an extension). */
function isPagePath(path: string): boolean {
  if (path === '/api' || path.startsWith('/api/') || path.startsWith('/_next/')) return false;
  const last = path.slice(path.lastIndexOf('/') + 1);
  return !last.includes('.');
}

export function isSetupPath(path: string): boolean {
  return path === '/setup' || path.startsWith('/setup/');
}

/** In setup mode every page goes to `/setup`; once a user exists `/setup` goes to `/login`; null stays. */
function setupRedirect(needed: boolean, path: string): string | null {
  if (needed) return isSetupPath(path) ? null : '/setup';
  return isSetupPath(path) ? '/login' : null;
}

export class SetupService {
  private readonly attempts: WindowLimiter;

  constructor(
    private readonly users: UsersStore,
    private readonly sessions: SessionService,
    private readonly auth: AuthService,
    private readonly audit: AuditService,
    private readonly trusted: TrustedProxies,
    private readonly cfg: SetupConfig,
    clock: Clock,
    private readonly log: Logger,
  ) {
    this.attempts = new WindowLimiter(clock, cfg.attemptsPerMinute, 60_000);
  }

  needed(): boolean {
    return this.users.setupNeeded();
  }

  /** Why this request may not complete the setup (null = it may). */
  private refusal(req: IncomingMessage): string | null {
    return this.cfg.allowProxied ? null : notDirectLan(req, this.trusted);
  }

  status(req: IncomingMessage): SetupStatus {
    const needed = this.needed();
    return { needed, allowedHere: needed && this.refusal(req) === null };
  }

  async create(
    res: ServerResponse,
    req: IncomingMessage,
    ctx: RequestContext,
    state: AuthState,
    body: SetupRequest,
  ): Promise<LoginResponse> {
    const ua = header(req, 'user-agent');
    const base = { ip: ctx.ip, userAgent: ua, method: 'POST', route: 'setup.create', requestId: ctx.requestId };
    const refuse = (status: number, summary: string) =>
      this.audit.record({ ...base, kind: 'setup', ok: false, username: null, status, summary });

    const wait = this.attempts.hit(ctx.ip);
    if (wait > 0) throw Errors.rateLimited(wait, 'too many setup attempts');

    const why = this.refusal(req);
    if (why) {
      refuse(403, `setup refused: ${why}`);
      this.log.warn({ ip: ctx.ip, why }, 'first-run setup refused');
      throw Errors.forbidden('first-run setup can only be completed from the local network');
    }
    // Cheap check first so a closed setup costs no argon2 hash; the authoritative one is createFirst's, under the lock.
    if (!this.needed()) {
      refuse(409, 'setup refused: already set up');
      throw Errors.conflict('setup is already complete');
    }

    const now = new Date().toISOString();
    const passwordHash = await hashPassword(body.password);
    let user;
    try {
      user = this.users.createFirst({
        username: body.username,
        passwordHash,
        roles: canonicalRoles(body.roles).split(',') as Role[],
        player: body.player ?? null,
        totp: null,
        disabled: false,
        createdAt: now,
        updatedAt: now,
      });
    } catch (e) {
      if (e instanceof UsersExistError) {
        refuse(409, `setup refused: already set up (lost the race as "${body.username}")`);
        throw Errors.conflict('setup is already complete');
      }
      throw e;
    }

    // Signed in like a login: the pre-session goes, a fresh id and CSRF token are issued.
    this.sessions.destroy(state.session);
    const { id, session } = this.sessions.createUser(user.username, credFingerprint(user), { ip: ctx.ip, userAgent: ua });
    this.sessions.setCookie(res, id, session, ctx.https);
    ctx.username = user.username;
    this.audit.record({
      ...base,
      kind: 'setup',
      ok: true,
      username: user.username,
      player: user.player,
      status: 200,
      summary: `first-run setup: created "${user.username}" (${user.roles.join(',')}${user.player ? `, player ${user.player}` : ''}) and signed in`,
    });
    this.log.warn({ ip: ctx.ip, user: user.username }, 'first-run setup complete');
    return { user: this.auth.me({ session, user }), csrfToken: session.csrf };
  }

  /**
   * Express middleware in front of the Next proxy: in setup mode every page redirects to `/setup`; once a user exists
   * `/setup` redirects to `/login`. Checked on every request (the users file is re-read when it changes).
   */
  gate() {
    return (req: IncomingMessage, res: ServerResponse, next: () => void) => {
      if (req.method !== 'GET' && req.method !== 'HEAD') return next();
      const path = (req.url ?? '/').split('?')[0] ?? '/';
      if (!isPagePath(path)) return next();
      const to = setupRedirect(this.needed(), path);
      if (!to) return next();
      res.statusCode = 302;
      res.setHeader('Location', to);
      res.setHeader('Cache-Control', 'no-store');
      res.end();
    };
  }
}
