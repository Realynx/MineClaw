/**
 * The update checker: reads the GitHub releases of `Web:Updates:Repo` (unauthenticated, conditional with If-None-Match),
 * keeps the newest eligible release in memory and in `<Web:DataDir>/update-cache.json`, and reports whether this install is
 * behind. A systemd install updates through a root-owned path unit: the web only writes `<Dir>/request.json` and reads the
 * updater's `<Dir>/status.json`. Docker and Helm installs get the command to run (the web has no docker socket or cluster RBAC).
 *
 * Network rules: never more than one request in flight; X-RateLimit-Remaining/Reset and Retry-After set a backoff that also
 * blocks forced checks; failures retry after 15 min, doubling up to the interval. Disabled = no network at all.
 */
import { randomUUID } from 'node:crypto';
import {
  closeSync,
  existsSync,
  fchmodSync,
  fsyncSync,
  mkdirSync,
  openSync,
  readFileSync,
  renameSync,
  statSync,
  unlinkSync,
  writeFileSync,
} from 'node:fs';
import { join } from 'node:path';
import {
  compareReleaseVersions,
  parseReleaseVersion,
  RELEASE_NOTES_MAX,
  RELEASE_VERSION_RE,
  UpdateRelease,
  UpdateRequestFile,
  UpdateRunStatus,
  type InstallKind,
  type SystemUpdateStatus,
  type SystemVersion,
  type UpdateMode,
} from '@mineclaw/contracts';
import { z } from 'zod';
import type { Clock } from '../common/clock.js';
import { Errors } from '../common/errors.js';
import type { Logger } from '../common/logger.js';
import { stripBom, type VersionInfo } from '../version.js';

export interface UpdateSettings {
  enabled: boolean;
  repo: string;
  intervalHours: number;
  prereleases: boolean;
  dir: string;
}

export interface UpdateCheckerOptions {
  settings: UpdateSettings;
  dataDir: string;
  version: VersionInfo;
  installKind: InstallKind;
  log: Logger;
  clock: Clock;
  /** MINECLAW_COMPOSE_DIR / MINECLAW_HELM_* for the shown commands. */
  env?: NodeJS.ProcessEnv;
  fetch?: typeof fetch;
  /** First check after start (default 60 s). */
  firstCheckDelayMs?: number;
}

export const CACHE_FILE = 'update-cache.json';
export const REQUEST_FILE = 'request.json';
export const STATUS_FILE = 'status.json';
export const FORCE_CHECK_MIN_MS = 60_000;
const FETCH_TIMEOUT_MS = 15_000;
const MAX_RESPONSE_CHARS = 8 * 1024 * 1024;
const FAILURE_RETRY_MS = 15 * 60_000;
const MAX_BACKOFF_MS = 24 * 3600_000;
const MAX_RELEASES = 20;
const DEFAULT_CHART = 'oci://ghcr.io/realynx/charts/mineclaw';
const DEFAULT_COMPOSE_DIR = '/opt/mineclaw-docker';

const CacheFile = z.object({
  repo: z.string(),
  etag: z.string().max(200).nullable(),
  checkedAt: z.string().nullable(),
  releases: z.array(UpdateRelease).max(MAX_RELEASES),
});
type CacheFile = z.infer<typeof CacheFile>;

/** A check that failed for a reason worth one log line (no stack). */
class CheckError extends Error {}

function clipText(v: unknown, max: number): string {
  return typeof v === 'string' ? (v.length > max ? v.slice(0, max) : v) : '';
}

/** One release from the GitHub API, or null when it is a draft or its tag is not a release version. */
export function toRelease(raw: unknown, repo: string): UpdateRelease | null {
  if (!raw || typeof raw !== 'object') return null;
  const r = raw as Record<string, unknown>;
  if (r.draft === true || typeof r.tag_name !== 'string') return null;
  const parsed = parseReleaseVersion(r.tag_name);
  if (!parsed) return null;
  const version = `${parsed.core.join('.')}${parsed.pre.length ? `-${parsed.pre.join('.')}` : ''}`;
  if (!RELEASE_VERSION_RE.test(version) || version.length > 64) return null;
  const body = typeof r.body === 'string' ? r.body : '';
  const htmlUrl = typeof r.html_url === 'string' ? r.html_url : '';
  const url =
    htmlUrl.startsWith(`https://github.com/`) && htmlUrl.length <= 500
      ? htmlUrl
      : `https://github.com/${repo}/releases/tag/${encodeURIComponent(r.tag_name)}`;
  const name = clipText(r.name, 200).trim() || `v${version}`;
  return {
    version,
    name,
    notes: body.length > RELEASE_NOTES_MAX ? body.slice(0, RELEASE_NOTES_MAX) : body,
    notesTruncated: body.length > RELEASE_NOTES_MAX,
    url,
    publishedAt: typeof r.published_at === 'string' ? r.published_at.slice(0, 64) : null,
    prerelease: r.prerelease === true || parsed.pre.length > 0,
  };
}

/** The newest release (by version), skipping prereleases unless allowed. */
export function pickLatest(releases: readonly UpdateRelease[], prereleases: boolean): UpdateRelease | null {
  let best: UpdateRelease | null = null;
  for (const r of releases) {
    if (r.prerelease && !prereleases) continue;
    if (!best || (compareReleaseVersions(r.version, best.version) ?? 0) > 0) best = r;
  }
  return best;
}

/** A word for a shell command: kept bare when it is plain, else single-quoted. */
export function shellWord(s: string): string {
  return /^[A-Za-z0-9_./:@=+-]+$/.test(s) ? s : `'${s.replace(/'/g, `'\\''`)}'`;
}

/** The upgrade command a docker or helm install runs for `version` (already a validated release version). */
export function upgradeCommand(kind: InstallKind, version: string, env: NodeJS.ProcessEnv): string | undefined {
  if (kind === 'docker') {
    const dir = env.MINECLAW_COMPOSE_DIR?.trim() || DEFAULT_COMPOSE_DIR;
    return (
      `cd ${shellWord(dir)} && sed -i '/^MINECLAW_VERSION=/d' .env && echo MINECLAW_VERSION=${version} >> .env` +
      ' && docker compose pull && docker compose up -d'
    );
  }
  if (kind === 'helm') {
    const release = env.MINECLAW_HELM_RELEASE?.trim() || 'mineclaw';
    const ns = env.MINECLAW_HELM_NAMESPACE?.trim() || 'default';
    const chart = env.MINECLAW_HELM_CHART?.trim() || DEFAULT_CHART;
    return `helm upgrade ${shellWord(release)} ${shellWord(chart)} --version ${version} -n ${shellWord(ns)} --reuse-values`;
  }
  return undefined;
}

function modeOf(kind: InstallKind): UpdateMode {
  return kind === 'systemd' ? 'trigger' : kind === 'docker' || kind === 'helm' ? 'command' : 'none';
}

/** Retry-After (seconds or an HTTP date) in ms from now; undefined when absent or unreadable. */
function retryAfterMs(h: string | null, now: number): number | undefined {
  if (!h) return undefined;
  const s = Number(h);
  if (Number.isFinite(s)) return Math.max(0, s * 1000);
  const at = Date.parse(h);
  return Number.isNaN(at) ? undefined : Math.max(0, at - now);
}

/** A small JSON file, or null when it is missing, too large or not JSON. */
function readSmallJson(path: string, max = 256 * 1024): unknown {
  try {
    if (!existsSync(path) || statSync(path).size > max) return null;
    return JSON.parse(stripBom(readFileSync(path, 'utf8'))) as unknown;
  } catch {
    return null;
  }
}

/** Writes through a temp file in the same directory and renames it into place (readers never see half a file). */
export function writeFileAtomic(path: string, data: string, mode: number): void {
  const temp = `${path}.${randomUUID()}.tmp`;
  try {
    const fd = openSync(temp, 'wx', mode);
    try {
      fchmodSync(fd, mode);
      writeFileSync(fd, data);
      fsyncSync(fd);
    } finally {
      closeSync(fd);
    }
    renameSync(temp, path);
  } catch (e) {
    try {
      if (existsSync(temp)) unlinkSync(temp);
    } catch {
      // nothing left to clean
    }
    throw e;
  }
}

export class UpdateChecker {
  private readonly s: UpdateSettings;
  private readonly fetchImpl: typeof fetch;
  private readonly env: NodeJS.ProcessEnv;
  private releases: UpdateRelease[] = [];
  private etag: string | null = null;
  private checkedAt: string | null = null;
  private lastError: string | null = null;
  private failures = 0;
  /** No request before this time (GitHub rate limit / Retry-After). */
  private backoffUntil = 0;
  private lastForcedAt = Number.NEGATIVE_INFINITY;
  private inflight: Promise<void> | null = null;
  private timer: NodeJS.Timeout | null = null;
  private readonly abort = new AbortController();
  private closed = false;

  constructor(private readonly o: UpdateCheckerOptions) {
    this.s = o.settings;
    this.fetchImpl = o.fetch ?? ((...args) => globalThis.fetch(...args));
    this.env = o.env ?? process.env;
    if (this.s.enabled) this.loadCache();
  }

  get enabled(): boolean {
    return this.s.enabled;
  }

  /** GitHub's request budget is spent until then (ms since epoch; 0 = none). */
  get rateLimitedUntil(): number {
    return this.backoffUntil;
  }

  version(): SystemVersion {
    const v = this.o.version;
    return { version: v.version, commit: v.commit, built: v.built, installKind: this.o.installKind };
  }

  /** Schedules the first check shortly after start, then one per interval. Does nothing when disabled. */
  start(): void {
    if (!this.s.enabled || this.closed || this.timer) return;
    this.schedule(this.o.firstCheckDelayMs ?? 60_000);
  }

  close(): void {
    this.closed = true;
    if (this.timer) clearTimeout(this.timer);
    this.timer = null;
    this.abort.abort();
  }

  /** One check (joins the one in flight). Never throws: failures land in `lastError`. */
  check(): Promise<void> {
    if (!this.s.enabled || this.closed) return Promise.resolve();
    this.inflight ??= this.run().finally(() => {
      this.inflight = null;
    });
    return this.inflight;
  }

  /** An admin's "check now": at most once a minute, and not while GitHub rate-limits us. */
  async forceCheck(): Promise<SystemUpdateStatus> {
    if (!this.s.enabled) throw Errors.badRequest(undefined, 'the update checker is disabled (Web:Updates:Enabled)');
    const now = this.o.clock.now();
    if (this.backoffUntil > now) throw Errors.rateLimited(this.backoffUntil - now, 'GitHub rate limit; try again later');
    const since = now - this.lastForcedAt;
    if (since < FORCE_CHECK_MIN_MS) throw Errors.rateLimited(FORCE_CHECK_MIN_MS - since, 'checked less than a minute ago');
    this.lastForcedAt = now;
    await this.check();
    this.reschedule();
    return this.status();
  }

  latest(): UpdateRelease | null {
    return this.s.enabled ? pickLatest(this.releases, this.s.prereleases) : null;
  }

  status(): SystemUpdateStatus {
    const current = this.o.version.version;
    const latest = this.latest();
    const outdated = !!latest && (compareReleaseVersions(latest.version, current) ?? 0) > 0;
    const kind = this.o.installKind;
    const mode: UpdateMode = this.s.enabled ? modeOf(kind) : 'none';
    const update: SystemUpdateStatus['update'] = { mode };
    if (mode === 'command' && latest) update.command = upgradeCommand(kind, latest.version, this.env);
    if (kind === 'systemd') {
      update.status = this.runStatus();
      update.request = this.pendingRequest();
    }
    return {
      enabled: this.s.enabled,
      current,
      latest,
      outdated,
      checkedAt: this.s.enabled ? this.checkedAt : null,
      lastError: this.s.enabled ? this.lastError : null,
      installKind: kind,
      update,
    };
  }

  /**
   * systemd only: asks the root updater for `version`, which must be the latest known release and newer than this one.
   * Writes `<Dir>/request.json` atomically (0640). 409 while a request is pending or an update is running.
   */
  requestUpdate(version: string, username: string): SystemUpdateStatus {
    if (!this.s.enabled) throw Errors.badRequest(undefined, 'the update checker is disabled (Web:Updates:Enabled)');
    if (this.o.installKind !== 'systemd')
      throw Errors.badRequest(undefined, 'this install updates with the shown command, not from the web');
    if (!RELEASE_VERSION_RE.test(version)) throw Errors.badRequest(undefined, 'not a release version');
    const latest = this.latest();
    if (!latest || latest.version !== version)
      throw Errors.badRequest(undefined, 'only the latest known release can be installed');
    if ((compareReleaseVersions(latest.version, this.o.version.version) ?? 0) <= 0)
      throw Errors.badRequest(undefined, 'this version is already installed');
    const dir = this.s.dir;
    if (!existsSync(dir)) throw Errors.conflict('the updater is not installed on this server (update directory missing)');
    if (this.runStatus()?.state === 'running') throw Errors.conflict('an update is already running');
    const requestPath = join(dir, REQUEST_FILE);
    if (existsSync(requestPath)) throw Errors.conflict('an update request is already waiting for the updater');
    const request = UpdateRequestFile.parse({
      version,
      requestedBy: username.slice(0, 64),
      requestedAt: new Date(this.o.clock.now()).toISOString(),
    });
    writeFileAtomic(requestPath, `${JSON.stringify(request)}\n`, 0o640);
    this.o.log.info({ version, from: this.o.version.version, user: username }, 'update requested');
    return this.status();
  }

  /** `<Dir>/status.json` from the root updater, normalised (null when absent or unreadable). */
  runStatus(): UpdateRunStatus | null {
    const raw = readSmallJson(join(this.s.dir, STATUS_FILE));
    if (!raw || typeof raw !== 'object') return null;
    const r = { ...(raw as Record<string, unknown>) };
    if (typeof r.message === 'string') r.message = clipText(r.message, 2000);
    if (Array.isArray(r.log)) r.log = r.log.filter((l) => typeof l === 'string').slice(-50).map((l: string) => clipText(l, 500));
    const parsed = UpdateRunStatus.safeParse(r);
    return parsed.success ? parsed.data : null;
  }

  private pendingRequest(): UpdateRequestFile | null {
    const parsed = UpdateRequestFile.safeParse(readSmallJson(join(this.s.dir, REQUEST_FILE), 4096));
    return parsed.success ? parsed.data : null;
  }

  private schedule(ms: number): void {
    if (this.closed) return;
    if (this.timer) clearTimeout(this.timer);
    this.timer = setTimeout(() => {
      this.timer = null;
      void this.check().then(() => this.reschedule());
    }, Math.max(1000, ms));
    this.timer.unref();
  }

  /** Next regular check: the interval, sooner after a failure, never before a rate-limit backoff ends. */
  private reschedule(): void {
    if (this.closed) return;
    const interval = this.s.intervalHours * 3600_000;
    const base = this.failures > 0 ? Math.min(interval, FAILURE_RETRY_MS * 2 ** (this.failures - 1)) : interval;
    this.schedule(Math.max(base, this.backoffUntil - this.o.clock.now()));
  }

  private async run(): Promise<void> {
    const now = this.o.clock.now();
    if (this.backoffUntil > now) return;
    try {
      const res = await this.request();
      this.noteRateLimit(res, now);
      if (res.status === 304) {
        await res.body?.cancel().catch(() => {});
        this.succeeded(now);
        return;
      }
      if (!res.ok) {
        await res.body?.cancel().catch(() => {});
        throw new CheckError(
          res.status === 403 || res.status === 429 ? `GitHub rate limit (HTTP ${res.status})` : `GitHub answered HTTP ${res.status}`,
        );
      }
      this.releases = await this.readReleases(res);
      this.etag = res.headers.get('etag')?.slice(0, 200) ?? null;
      this.succeeded(now);
    } catch (e) {
      if (this.closed) return;
      this.failures++;
      const message =
        e instanceof CheckError
          ? e.message
          : e instanceof Error && (e.name === 'TimeoutError' || e.name === 'AbortError')
            ? 'GitHub did not answer in time'
            : `GitHub could not be reached (${e instanceof Error ? e.message : String(e)})`;
      this.lastError = message.slice(0, 300);
      this.o.log.warn({ repo: this.s.repo, err: this.lastError, failures: this.failures }, 'update check failed');
    }
  }

  private request(): Promise<Response> {
    const headers: Record<string, string> = {
      'User-Agent': 'mineclaw-update-check',
      Accept: 'application/vnd.github+json',
      'X-GitHub-Api-Version': '2022-11-28',
    };
    if (this.etag) headers['If-None-Match'] = this.etag;
    return this.fetchImpl(`https://api.github.com/repos/${this.s.repo}/releases?per_page=${MAX_RELEASES}`, {
      headers,
      redirect: 'follow',
      signal: AbortSignal.any([this.abort.signal, AbortSignal.timeout(FETCH_TIMEOUT_MS)]),
    });
  }

  private async readReleases(res: Response): Promise<UpdateRelease[]> {
    const text = await res.text();
    if (text.length > MAX_RESPONSE_CHARS) throw new CheckError('GitHub sent an oversized release list');
    let data: unknown;
    try {
      data = JSON.parse(text) as unknown;
    } catch {
      throw new CheckError('GitHub sent invalid JSON');
    }
    if (!Array.isArray(data)) throw new CheckError('GitHub sent an unexpected release list');
    const out: UpdateRelease[] = [];
    for (const raw of data.slice(0, MAX_RELEASES)) {
      const r = toRelease(raw, this.s.repo);
      if (r) out.push(r);
    }
    return out;
  }

  private noteRateLimit(res: Response, now: number): void {
    let until = 0;
    const after = retryAfterMs(res.headers.get('retry-after'), now);
    if (after !== undefined) until = now + after;
    else if (res.headers.get('x-ratelimit-remaining') === '0') {
      const reset = Number(res.headers.get('x-ratelimit-reset'));
      if (Number.isFinite(reset) && reset > 0) until = reset * 1000 + 1000;
      else if (res.status === 403 || res.status === 429) until = now + 60 * 60_000;
    }
    if (until > now) {
      this.backoffUntil = Math.min(until, now + MAX_BACKOFF_MS);
      this.o.log.warn(
        { repo: this.s.repo, until: new Date(this.backoffUntil).toISOString() },
        'GitHub rate limit reached; update checks paused',
      );
    }
  }

  private succeeded(now: number): void {
    this.checkedAt = new Date(now).toISOString();
    this.lastError = null;
    this.failures = 0;
    this.saveCache();
  }

  private cachePath(): string {
    return join(this.o.dataDir, CACHE_FILE);
  }

  private loadCache(): void {
    const parsed = CacheFile.safeParse(readSmallJson(this.cachePath(), 4 * 1024 * 1024));
    if (!parsed.success || parsed.data.repo !== this.s.repo) return;
    this.releases = parsed.data.releases;
    this.etag = parsed.data.etag;
    this.checkedAt = parsed.data.checkedAt;
  }

  private saveCache(): void {
    const cache: CacheFile = { repo: this.s.repo, etag: this.etag, checkedAt: this.checkedAt, releases: this.releases };
    try {
      mkdirSync(this.o.dataDir, { recursive: true });
      writeFileAtomic(this.cachePath(), JSON.stringify(cache), 0o640);
    } catch (e) {
      this.o.log.warn({ err: e instanceof Error ? e.message : String(e) }, 'could not write the update cache');
    }
  }
}
