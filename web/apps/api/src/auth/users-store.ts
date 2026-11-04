/**
 * The users file (`Web:UsersFile`, default /etc/mineclaw-web/users.json, mode 0600): username, argon2id hash, roles, linked
 * Minecraft player and optional TOTP secret. Managed by the `users` CLI; the server writes it only for the first-run setup
 * (`createFirst`: the first account, while the file has no users) and to activate a verified TOTP enrolment. There is no
 * self-signup. Writes are atomic (temp file + rename), and the server's read-modify-writes hold an exclusive lock file
 * (`.users.json.lock`, O_EXCL) so two of them never interleave. The server re-reads the file when it changes, so CLI edits
 * (roles, removal, password resets) apply without a restart.
 */
import { createHash } from 'node:crypto';
import {
  chmodSync,
  closeSync,
  existsSync,
  mkdirSync,
  openSync,
  readFileSync,
  renameSync,
  rmSync,
  statSync,
  writeFileSync,
  writeSync,
} from 'node:fs';
import { dirname, join, basename } from 'node:path';
import { PLAYER_PATTERN, Role, USERNAME_PATTERN } from '@mineclaw/contracts';
import { z } from 'zod';

export const USERNAME_RE = USERNAME_PATTERN;
export const PLAYER_RE = PLAYER_PATTERN;

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

const LOCK_STALE_MS = 30_000;
const LOCK_WAIT_MS = 3_000;

/** Thrown by `createFirst` when the file already holds a user (setup is closed). */
export class UsersExistError extends Error {
  constructor() {
    super('the users file already has users');
    this.name = 'UsersExistError';
  }
}

function sleepSync(ms: number): void {
  Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, ms);
}

/**
 * Runs `fn` while holding `<dir>/.<file>.lock` (created with O_EXCL). `fn` must be synchronous, so the whole
 * read-check-write is one step for this process too. A lock older than 30 s is a crashed writer's and is broken.
 */
export function withUsersFileLock<T>(path: string, fn: () => T): T {
  const dir = dirname(path);
  if (!existsSync(dir)) mkdirSync(dir, { recursive: true, mode: 0o700 });
  const lock = join(dir, `.${basename(path)}.lock`);
  const deadline = Date.now() + LOCK_WAIT_MS;
  let fd: number | undefined;
  while (fd === undefined) {
    try {
      fd = openSync(lock, 'wx', 0o600);
    } catch (e) {
      if ((e as NodeJS.ErrnoException).code !== 'EEXIST') throw e;
      try {
        if (Date.now() - statSync(lock).mtimeMs > LOCK_STALE_MS) rmSync(lock, { force: true });
      } catch {
        // released meanwhile
      }
      if (Date.now() > deadline) throw new Error(`users file is locked (${lock})`, { cause: e });
      sleepSync(20);
    }
  }
  try {
    writeSync(fd, `${process.pid}
`);
    return fn();
  } finally {
    closeSync(fd);
    rmSync(lock, { force: true });
  }
}

export interface UsersStoreLog {
  warn(obj: object, msg: string): void;
  error(obj: object, msg: string): void;
}

/** mtime and size, or 'missing' (a missing file = no users). */
function fileStamp(path: string): string {
  try {
    const st = statSync(path);
    return `${st.mtimeMs}:${st.size}`;
  } catch {
    return 'missing';
  }
}

export class UsersStore {
  private data: UsersFileData = { version: 1, users: [] };
  /** False until the file has been read successfully once (a missing file counts as read: no users). */
  private loaded = false;
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
    const stamp = fileStamp(this.path);
    if (!force && stamp === this.stamp) return;
    this.load(stamp);
  }

  /** Reads the file; a bad file keeps the last good copy. An empty or missing file opens first-run setup. */
  private load(stamp: string): void {
    try {
      this.data = readUsersFile(this.path);
      this.loaded = true;
      this.stamp = stamp;
      if (this.data.users.length === 0)
        this.log?.warn(
          { path: this.path },
          stamp === 'missing'
            ? 'users file not found: first-run setup is open at /setup (LAN only)'
            : 'users file has no users: first-run setup is open at /setup (LAN only)',
        );
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

  /**
   * First-run setup mode: the users file is missing or valid with no users. A file that has never parsed is not "empty"
   * (it may hold users we cannot read), so a broken file never opens setup.
   */
  setupNeeded(): boolean {
    this.reload();
    return this.loaded && this.data.users.length === 0;
  }

  /**
   * Atomic create-if-empty for the first-run setup: under the lock, re-reads the file on disk and adds `record` only if it
   * holds no users; otherwise throws `UsersExistError`. Of any number of concurrent setups exactly one wins.
   */
  createFirst(record: UserRecord): UserRecord {
    const next = UserRecord.parse(record);
    withUsersFileLock(this.path, () => {
      const data = readUsersFile(this.path);
      if (data.users.length > 0) throw new UsersExistError();
      writeUsersFile(this.path, { ...data, users: [next] });
    });
    this.reload(true);
    return next;
  }

  /** Read-modify-write against the file on disk (not the cached copy), under the lock. */
  update(username: string, fn: (u: UserRecord) => UserRecord): UserRecord {
    const next = withUsersFileLock(this.path, () => {
      const data = readUsersFile(this.path);
      const name = normalizeUsername(username);
      const i = data.users.findIndex((u) => u.username === name);
      if (i < 0) throw new Error(`no such user "${name}"`);
      const updated = UserRecord.parse({ ...fn(data.users[i]!), updatedAt: new Date().toISOString() });
      data.users[i] = updated;
      writeUsersFile(this.path, data);
      return updated;
    });
    this.reload(true);
    return next;
  }
}
