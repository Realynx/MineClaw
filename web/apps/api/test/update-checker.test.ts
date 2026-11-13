import { mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { compareReleaseVersions } from '@mineclaw/contracts';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { ManualClock } from '../src/common/clock.js';
import { createLogger } from '../src/common/logger.js';
import {
  CACHE_FILE,
  pickLatest,
  shellWord,
  toRelease,
  UpdateChecker,
  upgradeCommand,
  type UpdateSettings,
} from '../src/system/update-checker.js';
import { installKindOf, readVersionInfo } from '../src/version.js';

const dirs: string[] = [];
const checkers: UpdateChecker[] = [];
afterEach(() => {
  for (const c of checkers.splice(0)) c.close();
  for (const d of dirs.splice(0)) rmSync(d, { recursive: true, force: true });
});

function temp(): string {
  const d = mkdtempSync(join(tmpdir(), 'mcx-upd-'));
  dirs.push(d);
  return d;
}

const gh = (tag: string, extra: Record<string, unknown> = {}) => ({
  tag_name: tag,
  name: `MineClaw ${tag}`,
  body: `## Changes in ${tag}\n- fixed things`,
  html_url: `https://github.com/Realynx/MineClaw/releases/tag/${tag}`,
  draft: false,
  prerelease: false,
  published_at: '2026-11-10T12:00:00Z',
  ...extra,
});

function reply(status: number, body: unknown, headers: Record<string, string> = {}): Response {
  return new Response(status === 304 ? null : JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json', ...headers },
  });
}

function make(opts: { settings?: Partial<UpdateSettings>; current?: string; fetch?: typeof fetch; dataDir?: string } = {}) {
  const dataDir = opts.dataDir ?? temp();
  const clock = new ManualClock(Date.UTC(2026, 10, 12, 10, 0, 0));
  const fetcher = vi.fn<typeof fetch>(opts.fetch ?? (() => Promise.resolve(reply(200, [gh('v1.2.0')]))));
  const checker = new UpdateChecker({
    settings: { enabled: true, repo: 'Realynx/MineClaw', intervalHours: 12, prereleases: false, dir: join(dataDir, 'update'), ...opts.settings },
    dataDir,
    version: { version: opts.current ?? '1.1.0', commit: 'abc123', built: null },
    installKind: 'systemd',
    env: {},
    log: createLogger('silent'),
    clock,
    fetch: fetcher,
  });
  checkers.push(checker);
  return { checker, fetcher, clock, dataDir };
}

describe('release versions', () => {
  it('orders versions with prereleases before their release', () => {
    expect(compareReleaseVersions('1.2.0', '1.10.0')).toBe(-1);
    expect(compareReleaseVersions('2.0.0', '1.99.99')).toBe(1);
    expect(compareReleaseVersions('1.2.0-rc.1', '1.2.0')).toBe(-1);
    expect(compareReleaseVersions('1.2.0-rc.2', '1.2.0-rc.10')).toBe(-1);
    expect(compareReleaseVersions('1.2.0-alpha', '1.2.0-alpha.1')).toBe(-1);
    expect(compareReleaseVersions('1.2.0-1', '1.2.0-alpha')).toBe(-1);
    expect(compareReleaseVersions('v1.2.3', '1.2.3')).toBe(0);
    expect(compareReleaseVersions('1.2', '1.2.0')).toBeNull();
  });

  it('maps GitHub releases: drafts and bad tags skipped, notes truncated, links pinned to github.com', () => {
    expect(toRelease(gh('v1.0.0', { draft: true }), 'a/b')).toBeNull();
    expect(toRelease(gh('nightly'), 'a/b')).toBeNull();
    expect(toRelease(gh('v1.0.0-rc.1'), 'a/b')?.prerelease).toBe(true);
    const long = toRelease(gh('v1.0.0', { body: 'x'.repeat(40_000), html_url: 'javascript:alert(1)' }), 'a/b')!;
    expect(long.notes.length).toBe(16_000);
    expect(long.notesTruncated).toBe(true);
    expect(long.url).toBe('https://github.com/a/b/releases/tag/v1.0.0');
  });

  it('picks the newest release, prereleases only when allowed', () => {
    const rs = ['v1.0.0', 'v1.3.0-rc.1', 'v1.2.0', 'v1.1.5'].map((t) => toRelease(gh(t), 'a/b')!);
    expect(pickLatest(rs, false)?.version).toBe('1.2.0');
    expect(pickLatest(rs, true)?.version).toBe('1.3.0-rc.1');
    expect(pickLatest([], true)).toBeNull();
  });

  it('builds docker and helm commands from the environment', () => {
    expect(upgradeCommand('docker', '1.2.0', {})).toBe(
      "cd /opt/mineclaw-docker && sed -i '/^MINECLAW_VERSION=/d' .env && echo MINECLAW_VERSION=1.2.0 >> .env && docker compose pull && docker compose up -d",
    );
    expect(upgradeCommand('docker', '1.2.0', { MINECLAW_COMPOSE_DIR: '/srv/my stack' })).toMatch(/^cd '\/srv\/my stack' && /);
    expect(
      upgradeCommand('helm', '1.2.0', { MINECLAW_HELM_RELEASE: 'mc', MINECLAW_HELM_NAMESPACE: 'games' }),
    ).toBe('helm upgrade mc oci://ghcr.io/realynx/charts/mineclaw --version 1.2.0 -n games --reuse-values');
    expect(upgradeCommand('systemd', '1.2.0', {})).toBeUndefined();
    expect(shellWord("it's")).toBe(`'it'\\''s'`);
  });
});

describe('version info', () => {
  it('env > RELEASE.json > package.json, commit from RELEASE.json', () => {
    const d = temp();
    const release = join(d, 'RELEASE.json');
    const pkg = join(d, 'package.json');
    writeFileSync(pkg, JSON.stringify({ version: '0.1.0' }));
    writeFileSync(release, JSON.stringify({ id: '20261112-1000', commit: 'abc1234', built: '2026-11-12T10:00:00Z' }));
    expect(readVersionInfo({ env: {}, releaseFile: release, packageFile: pkg })).toEqual({
      version: '0.1.0',
      commit: 'abc1234',
      built: '2026-11-12T10:00:00Z',
    });
    writeFileSync(release, JSON.stringify({ version: '1.4.0', commit: 'f'.repeat(40), built: null }));
    expect(readVersionInfo({ env: {}, releaseFile: release, packageFile: pkg }).version).toBe('1.4.0');
    expect(readVersionInfo({ env: { MINECLAW_VERSION: 'v1.5.0' }, releaseFile: release, packageFile: pkg }).version).toBe('1.5.0');
    expect(readVersionInfo({ env: {}, releaseFile: join(d, 'none.json'), packageFile: join(d, 'none.json') }).version).toBe('0.0.0');
  });

  it('install kind from MINECLAW_INSTALL_KIND', () => {
    expect(installKindOf({ MINECLAW_INSTALL_KIND: 'docker' })).toBe('docker');
    expect(installKindOf({ MINECLAW_INSTALL_KIND: 'Helm' })).toBe('helm');
    expect(installKindOf({ MINECLAW_INSTALL_KIND: 'bogus' })).toBe('manual');
    expect(installKindOf({})).toBe('manual');
  });
});

describe('update checker', () => {
  it('disabled: no network, nothing reported', async () => {
    const { checker, fetcher } = make({ settings: { enabled: false } });
    checker.start();
    await checker.check();
    await expect(checker.forceCheck()).rejects.toMatchObject({ status: 400 });
    expect(fetcher).not.toHaveBeenCalled();
    expect(checker.status()).toMatchObject({ enabled: false, latest: null, outdated: false, update: { mode: 'none' } });
  });

  it('checks GitHub with the right headers and reports an outdated install', async () => {
    const { checker, fetcher, dataDir } = make();
    await checker.check();
    expect(fetcher).toHaveBeenCalledOnce();
    const [url, init] = fetcher.mock.calls[0]!;
    expect(url).toBe('https://api.github.com/repos/Realynx/MineClaw/releases?per_page=20');
    expect(init?.headers).toMatchObject({ 'User-Agent': 'mineclaw-update-check', Accept: 'application/vnd.github+json' });
    const s = checker.status();
    expect(s).toMatchObject({ enabled: true, current: '1.1.0', outdated: true, lastError: null, update: { mode: 'trigger' } });
    expect(s.latest?.version).toBe('1.2.0');
    expect(s.checkedAt).toBe('2026-11-12T10:00:00.000Z');
    const cache = JSON.parse(readFileSync(join(dataDir, CACHE_FILE), 'utf8')) as { releases: unknown[] };
    expect(cache.releases).toHaveLength(1);
  });

  it('revalidates with the ETag and keeps the cached releases on 304; the cache survives a restart', async () => {
    let n = 0;
    const { checker, fetcher, clock, dataDir } = make({
      fetch: (_url, init) => {
        n++;
        if (n === 1) return Promise.resolve(reply(200, [gh('v1.2.0')], { ETag: '"abc"' }));
        expect((init?.headers as Record<string, string>)['If-None-Match']).toBe('"abc"');
        return Promise.resolve(reply(304, null, { ETag: '"abc"' }));
      },
    });
    await checker.check();
    clock.advance(3600_000);
    await checker.check();
    expect(fetcher).toHaveBeenCalledTimes(2);
    expect(checker.status().latest?.version).toBe('1.2.0');
    expect(checker.status().checkedAt).toBe('2026-11-12T11:00:00.000Z');
    checker.close();
    const again = make({ dataDir, fetch: () => Promise.reject(new Error('offline')) });
    expect(again.checker.status().latest?.version).toBe('1.2.0');
  });

  it('never runs two requests at once', async () => {
    let release!: (r: Response) => void;
    const { checker, fetcher } = make({ fetch: () => new Promise<Response>((r) => (release = r)) });
    const a = checker.check();
    const b = checker.check();
    expect(fetcher).toHaveBeenCalledOnce();
    release(reply(200, [gh('v1.2.0')]));
    await Promise.all([a, b]);
    expect(checker.status().latest?.version).toBe('1.2.0');
  });

  it('backs off on a spent rate limit and on Retry-After, forced checks included', async () => {
    const { checker, fetcher, clock } = make({
      fetch: () =>
        Promise.resolve(
          reply(403, { message: 'API rate limit exceeded' }, {
            'X-RateLimit-Remaining': '0',
            'X-RateLimit-Reset': String(Date.UTC(2026, 10, 12, 10, 30, 0) / 1000),
          }),
        ),
    });
    await checker.check();
    expect(checker.status().lastError).toMatch(/rate limit/);
    await checker.check();
    expect(fetcher).toHaveBeenCalledOnce();
    await expect(checker.forceCheck()).rejects.toMatchObject({ status: 429 });
    clock.advance(31 * 60_000);
    await checker.check();
    expect(fetcher).toHaveBeenCalledTimes(2);

    const ra = make({ fetch: () => Promise.resolve(reply(429, {}, { 'Retry-After': '120' })) });
    await ra.checker.check();
    expect(ra.checker.rateLimitedUntil).toBe(ra.clock.now() + 120_000);
    await ra.checker.check();
    expect(ra.fetcher).toHaveBeenCalledOnce();
  });

  it('a forced check runs at most once a minute and returns the fresh status', async () => {
    const { checker, fetcher, clock } = make();
    const s = await checker.forceCheck();
    expect(s.latest?.version).toBe('1.2.0');
    await expect(checker.forceCheck()).rejects.toMatchObject({ status: 429 });
    clock.advance(61_000);
    await checker.forceCheck();
    expect(fetcher).toHaveBeenCalledTimes(2);
  });

  it('skips prereleases unless enabled; not outdated when current is newer', async () => {
    const list = [gh('v1.3.0-rc.1', { prerelease: true }), gh('v1.2.0')];
    const off = make({ fetch: () => Promise.resolve(reply(200, list)) });
    await off.checker.check();
    expect(off.checker.status().latest?.version).toBe('1.2.0');
    const on = make({ settings: { prereleases: true }, fetch: () => Promise.resolve(reply(200, list)) });
    await on.checker.check();
    expect(on.checker.status().latest?.version).toBe('1.3.0-rc.1');
    const ahead = make({ current: '1.3.0', fetch: () => Promise.resolve(reply(200, list)) });
    await ahead.checker.check();
    expect(ahead.checker.status().outdated).toBe(false);
  });

  it('a failure is reported as lastError, not thrown', async () => {
    const { checker } = make({ fetch: () => Promise.reject(new TypeError('fetch failed')) });
    await checker.check();
    expect(checker.status().lastError).toBe('GitHub could not be reached (fetch failed)');
    const bad = make({ fetch: () => Promise.resolve(new Response('<html>', { status: 200 })) });
    await bad.checker.check();
    expect(bad.checker.status().lastError).toBe('GitHub sent invalid JSON');
  });

  it('reads the updater status and refuses a request while one is pending or running', async () => {
    const { checker, dataDir } = make();
    await checker.check();
    const dir = join(dataDir, 'update');
    expect(() => checker.requestUpdate('1.2.0', 'fox')).toThrow(/not installed/);
    mkdirSync(dir);
    writeFileSync(join(dir, 'status.json'), JSON.stringify({ state: 'running', version: '1.2.0', from: '1.1.0', log: Array.from({ length: 80 }, (_, i) => `line ${i}`) }));
    expect(checker.status().update.status?.log).toHaveLength(50);
    expect(() => checker.requestUpdate('1.2.0', 'fox')).toThrow(/already running/);
    writeFileSync(join(dir, 'status.json'), JSON.stringify({ state: 'failed', version: '1.2.0', message: 'checksum mismatch' }));
    expect(() => checker.requestUpdate('1.1.9', 'fox')).toThrow(/latest known release/);
    expect(() => checker.requestUpdate('1.2.0; rm -rf /', 'fox')).toThrow(/release version/);
    const s = checker.requestUpdate('1.2.0', 'fox');
    expect(s.update.request).toMatchObject({ version: '1.2.0', requestedBy: 'fox' });
    expect(JSON.parse(readFileSync(join(dir, 'request.json'), 'utf8'))).toEqual({
      version: '1.2.0',
      requestedBy: 'fox',
      requestedAt: '2026-11-12T10:00:00.000Z',
    });
    expect(() => checker.requestUpdate('1.2.0', 'fox')).toThrow(/already waiting/);
  });
});
