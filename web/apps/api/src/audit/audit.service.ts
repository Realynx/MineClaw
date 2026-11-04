/** The web audit log in web.db: every login (ok or failed), lockout, chat line and write. Read by `GET /api/audit`. */
import type { StatementSync } from 'node:sqlite';
import type { WebAuditEntry, WebAuditKind, WebAuditPage, WebAuditQuery } from '@mineclaw/contracts';
import type { Clock } from '../common/clock.js';
import type { WebDb } from '../db/web-db.js';

export interface AuditRecord {
  kind: WebAuditKind;
  ok: boolean;
  ip: string;
  username?: string | null;
  player?: string | null;
  userAgent?: string | null;
  method?: string | null;
  route?: string | null;
  target?: string | null;
  status?: number | null;
  summary: string;
  requestId?: string | null;
}

interface Row {
  id: number;
  at: number;
  username: string | null;
  player: string | null;
  ip: string;
  user_agent: string | null;
  kind: WebAuditKind;
  method: string | null;
  route: string | null;
  target: string | null;
  ok: number;
  status: number | null;
  summary: string;
  request_id: string | null;
}

export const AUDIT_SUMMARY_MAX = 500;

export function clip(text: string, max = AUDIT_SUMMARY_MAX): string {
  const oneLine = text.replace(/[\r\n]+/g, ' ');
  return oneLine.length > max ? `${oneLine.slice(0, max - 1)}…` : oneLine;
}

/** An optional column: SQL NULL when absent. */
function orNull<T>(v: T | null | undefined): T | null {
  return v ?? null;
}

export class AuditService {
  private readonly insert: StatementSync;

  constructor(
    private readonly db: WebDb,
    private readonly clock: Clock,
  ) {
    this.insert = db.db.prepare(
      `insert into web_audit (at, username, player, ip, user_agent, kind, method, route, target, ok, status, summary, request_id)
       values (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`,
    );
  }

  record(r: AuditRecord): void {
    this.insert.run(
      this.clock.now(),
      orNull(r.username),
      orNull(r.player),
      r.ip,
      orNull(r.userAgent?.slice(0, 300)),
      r.kind,
      orNull(r.method),
      orNull(r.route),
      r.target ? clip(r.target, 200) : null,
      r.ok ? 1 : 0,
      orNull(r.status),
      clip(r.summary),
      orNull(r.requestId),
    );
  }

  list(q: WebAuditQuery): WebAuditPage {
    const where: string[] = [];
    const args: Array<string | number> = [];
    if (q.user) {
      where.push('username = ?');
      args.push(q.user.toLowerCase());
    }
    if (q.kind) {
      where.push('kind = ?');
      args.push(q.kind);
    }
    if (q.since) {
      const t = Date.parse(q.since);
      if (Number.isFinite(t)) {
        where.push('at >= ?');
        args.push(t);
      }
    }
    if (q.ok) {
      where.push('ok = ?');
      args.push(q.ok === 'true' ? 1 : 0);
    }
    const w = where.length ? `where ${where.join(' and ')}` : '';
    const total = (this.db.db.prepare(`select count(*) as n from web_audit ${w}`).get(...args) as { n: number }).n;
    const rows = this.db.db
      .prepare(`select * from web_audit ${w} order by id desc limit ? offset ?`)
      .all(...args, q.limit, q.offset) as unknown as Row[];
    const items: WebAuditEntry[] = rows.map((r) => ({
      id: r.id,
      at: new Date(r.at).toISOString(),
      username: r.username,
      player: r.player,
      ip: r.ip,
      userAgent: r.user_agent,
      kind: r.kind,
      method: r.method,
      route: r.route,
      target: r.target,
      ok: r.ok === 1,
      status: r.status,
      summary: r.summary,
      requestId: r.request_id,
    }));
    return { total, offset: q.offset, limit: q.limit, items };
  }
}
