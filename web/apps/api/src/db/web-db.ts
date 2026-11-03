/**
 * web.db (SQLite via node:sqlite): sessions, login failure counters, pending TOTP enrolments, TOTP replay guard and the
 * web audit log. The file is created 0600 in `Web:DataDir`. WAL mode; every statement is prepared once.
 */
import { chmodSync, existsSync, mkdirSync } from 'node:fs';
import { dirname } from 'node:path';
import { DatabaseSync } from 'node:sqlite';

const SCHEMA_VERSION = 2;

const MIGRATIONS: Record<number, string> = {
  1: `
    create table sessions (
      id_hash text primary key,
      username text,
      cred text,
      csrf text not null,
      created_at integer not null,
      last_seen_at integer not null,
      idle_ms integer not null,
      absolute_expires_at integer not null,
      ip text,
      user_agent text
    );
    create index sessions_username on sessions(username);
    create index sessions_expiry on sessions(absolute_expires_at);

    create table login_failures (
      key text primary key,
      failures integer not null,
      locked_until integer not null default 0,
      last_at integer not null
    );

    create table totp_pending (
      username text primary key,
      secret text not null,
      expires_at integer not null
    );

    create table totp_used (
      username text primary key,
      step integer not null
    );

    create table web_audit (
      id integer primary key autoincrement,
      at integer not null,
      username text,
      player text,
      ip text not null,
      user_agent text,
      kind text not null,
      method text,
      route text,
      target text,
      ok integer not null,
      status integer,
      summary text not null,
      request_id text
    );
    create index web_audit_kind on web_audit(kind, id);
    create index web_audit_user on web_audit(username, id);
  `,
  // Step-up (re-entered TOTP or password) for admin settings writes: when this session last confirmed its user.
  2: `
    alter table sessions add column step_up_at integer;
  `,
};

export class WebDb {
  readonly db: DatabaseSync;

  constructor(path: string) {
    if (path !== ':memory:') {
      const dir = dirname(path);
      if (!existsSync(dir)) mkdirSync(dir, { recursive: true, mode: 0o700 });
    }
    const fresh = path === ':memory:' || !existsSync(path);
    this.db = new DatabaseSync(path);
    if (fresh && path !== ':memory:') {
      try {
        chmodSync(path, 0o600);
      } catch {
        // best effort (Windows dev)
      }
    }
    this.db.exec(
      'pragma journal_mode = wal; pragma synchronous = normal; pragma foreign_keys = on; pragma busy_timeout = 3000;',
    );
    this.migrate();
  }

  private migrate(): void {
    const row = this.db.prepare('pragma user_version').get() as { user_version: number } | undefined;
    let version = row?.user_version ?? 0;
    while (version < SCHEMA_VERSION) {
      const next = version + 1;
      const sql = MIGRATIONS[next];
      if (!sql) throw new Error(`missing web.db migration ${next}`);
      this.db.exec('begin');
      try {
        this.db.exec(sql);
        this.db.exec(`pragma user_version = ${next}`);
        this.db.exec('commit');
      } catch (e) {
        this.db.exec('rollback');
        throw e;
      }
      version = next;
    }
  }

  close(): void {
    if (this.db.isOpen) this.db.close();
  }
}
