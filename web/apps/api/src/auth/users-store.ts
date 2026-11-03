/**
 * The users file (`Web:UsersFile`, default /etc/mineclaw-web/users.json, mode 0600): username, argon2id hash, roles, linked
 * Minecraft player and optional TOTP secret. Managed by the `users` CLI (no self-signup); the server only writes it to
 * activate a verified TOTP enrolment. Writes are atomic (temp file + rename). The server re-reads it when it changes, so CLI
 * edits (roles, removal, password resets) apply without a restart.
 */
import { createHash } from 'node:crypto';
import { chmodSync, existsSync, mkdirSync, readFileSync, renameSync, statSync, writeFileSync } from 'node:fs';
import { dirname, join, basename } from 'node:path';
import { Role } from '@mineclaw/contracts';
import { z } from 'zod';

export const USERNAME_RE = /^[a-z0-9][a-z0-9_.-]{0,63}$/;
export const PLAYER_RE = /^[A-Za-z0-9_]{1,16}$/;

export const UserRecord = z.object({
  username: z.string().regex(USERNAME_RE),
  passwordHash: z.string().startsWith('$argon2id$'),
  roles: z.array(Role).min(1),
  player: z.string().regex(PLAYER_RE).nullish(),
  totp: z.object({ secret: z.string().regex(/^[A-Z2-7]{16,}$/), enrolledAt: z.string() }).nullish(),
  disabled: z.boolean().default(false),
  createdAt: z.string().optional(),
  updatedAt: z.string().optional(),
});
export type UserRecord = z.infer<typeof UserRecord>;

export const UsersFileSchema = z.object({
  version: z.literal(1).default(1),
  users: z.array(UserRecord).default([]),
});
export type UsersFileData = z.infer<typeof UsersFileSchema>;

export function normalizeUsername(name: string): string {
  return name.trim().toLowerCase();
}

/** A short fingerprint of the password hash: sessions remember it, so a password reset ends every session of that user. */
export function credFingerprint(user: Pick<UserRecord, 'passwordHash'>): string {
  return createHash('sha256').update(user.passwordHash).digest('hex').slice(0, 24);
}

export function readUsersFile(path: string): UsersFileData {
  if (!existsSync(path)) return { version: 1, users: [] };
  const raw = JSON.parse(readFileSync(path, 'utf8')) as unknown;
  const data = UsersFileSchema.parse(raw);
  const seen = new Set<string>();
  for (const u of data.users) {
    if (seen.has(u.username)) throw new Error(`users file: duplicate user "${u.username}"`);
    seen.add(u.username);
  }
  return data;
}

export function writeUsersFile(path: string, data: UsersFileData): void {
  const dir = dirname(path);
  if (!existsSync(dir)) mkdirSync(dir, { recursive: true, mode: 0o700 });
  const tmp = join(dir, `.${basename(path)}.${process.pid}.${Date.now()}.tmp`);
  const text = `${JSON.stringify(UsersFileSchema.parse(data), null, 2)}\n`;
  writeFileSync(tmp, text, { mode: 0o600 });
  try {
    chmodSync(tmp, 0o600);
  } catch {
    // Windows dev
  }
  renameSync(tmp, path);
}

export interface UsersStoreLog {
  warn(obj: object, msg: string): void;
  error(obj: object, msg: string): void;
}

export class UsersStore {
  private data: UsersFileData = { version: 1, users: [] };
  private stamp = '';
  private checkedAt = 0;

  constructor(
    readonly path: string,
    private readonly log?: UsersStoreLog,
    private readonly recheckMs = 1000,
  ) {
    this.reload(true);
  }

  private reload(force = false): void {
    const now = Date.now();
    if (!force && now - this.checkedAt < this.recheckMs) return;
    this.checkedAt = now;
    let stamp = 'missing';
    try {
      const st = statSync(this.path);
      stamp = `${st.mtimeMs}:${st.size}`;
    } catch {
      // missing file = no users
    }
    if (!force && stamp === this.stamp) return;
    try {
      this.data = readUsersFile(this.path);
      this.stamp = stamp;
      if (stamp === 'missing') this.log?.warn({ path: this.path }, 'users file not found: nobody can sign in');
    } catch (e) {
      // Keep the last good copy; a half-edited file must not lock everyone out or let anyone in.
      this.log?.error({ path: this.path, err: (e as Error).message }, 'users file invalid; keeping the previous copy');
      this.stamp = stamp;
    }
  }

  /** Forces the next lookup to re-read the file. */
  invalidate(): void {
    this.checkedAt = 0;
  }

  get(username: string): UserRecord | undefined {
    this.reload();
    const name = normalizeUsername(username);
    return this.data.users.find((u) => u.username === name);
  }

  list(): UserRecord[] {
    this.reload();
    return [...this.data.users];
  }

  /** Read-modify-write against the file on disk (not the cached copy). */
  update(username: string, fn: (u: UserRecord) => UserRecord): UserRecord {
    const data = readUsersFile(this.path);
    const name = normalizeUsername(username);
    const i = data.users.findIndex((u) => u.username === name);
    if (i < 0) throw new Error(`no such user "${name}"`);
    const next = UserRecord.parse({ ...fn(data.users[i]!), updatedAt: new Date().toISOString() });
    data.users[i] = next;
    writeUsersFile(this.path, data);
    this.reload(true);
    return next;
  }
}
