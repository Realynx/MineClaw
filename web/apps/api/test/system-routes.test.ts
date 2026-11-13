import { existsSync, mkdirSync, readFileSync, rmSync, statSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { login, ORIGIN, startApp, type TestApp } from './helpers.js';

const apps: TestApp[] = [];
afterEach(async () => {
  for (const a of apps.splice(0)) await a.close();
});

const releases = [
  { tag_name: 'v1.2.0', name: 'MineClaw 1.2.0', body: '## Fixes\n- **faster** paths', html_url: 'https://github.com/Realynx/MineClaw/releases/tag/v1.2.0', draft: false, prerelease: false, published_at: '2026-11-10T12:00:00Z' },
  { tag_name: 'v1.1.0', name: 'MineClaw 1.1.0', body: '', html_url: 'https://github.com/Realynx/MineClaw/releases/tag/v1.1.0', draft: false, prerelease: false, published_at: '2026-10-01T12:00:00Z' },
];

async function boot(kind: string | undefined, env: Record<string, string> = {}, enabled = true) {
  const fetcher = vi.fn<typeof fetch>(() => Promise.resolve(Response.json(releases)));
  const app = await startApp(
    { web: { updates: { enabled } } },
    {
      system: {
        fetch: fetcher,
        env: { ...(kind ? { MINECLAW_INSTALL_KIND: kind } : {}), ...env },
        version: { version: '1.1.0', commit: 'abc1234', built: '2026-10-01T12:00:00Z' },
        firstCheckDelayMs: 3600_000,
      },
    },
  );
  apps.push(app);
  return { app, fetcher, dir: join(app.dir, 'update') };
}

describe('system routes', () => {
  it('version and status need a session; any role may read them', async () => {
    const { app } = await boot('systemd');
    const anon = await fetch(`${app.url}/api/system/version`);
    expect(anon.status).toBe(401);
    expect((await fetch(`${app.url}/api/system/update`)).status).toBe(401);
    const vera = await login(app.url, 'vera');
    const v = await vera.agent.get('/api/system/version').expect(200);
    expect(v.body).toEqual({ version: '1.1.0', commit: 'abc1234', built: '2026-10-01T12:00:00Z', installKind: 'systemd' });
    const s = await vera.agent.get('/api/system/update').expect(200);
    expect(s.body).toMatchObject({ enabled: true, current: '1.1.0', latest: null, outdated: false, update: { mode: 'trigger', status: null, request: null } });
  });

  it('a forced check is admin-only, needs CSRF and is rate-limited', async () => {
    const { app, fetcher } = await boot('systemd');
    const vera = await login(app.url, 'vera');
    await vera.agent.post('/api/system/update/check').set('Origin', ORIGIN).set('X-CSRF-Token', vera.csrf).expect(403);
    const fox = await login(app.url, 'fox');
    await fox.agent.post('/api/system/update/check').set('Origin', ORIGIN).expect(403);
    const r = await fox.agent.post('/api/system/update/check').set('Origin', ORIGIN).set('X-CSRF-Token', fox.csrf).expect(200);
    expect(r.body).toMatchObject({ outdated: true, latest: { version: '1.2.0', notes: '## Fixes\n- **faster** paths' } });
    await fox.agent.post('/api/system/update/check').set('Origin', ORIGIN).set('X-CSRF-Token', fox.csrf).expect(429);
    expect(fetcher).toHaveBeenCalledOnce();
  });

  it('an update request writes request.json once, is audited, and 409s while pending or running', async () => {
    const { app, dir } = await boot('systemd');
    const fox = await login(app.url, 'fox');
    const post = (body: unknown, csrf = fox.csrf) =>
      fox.agent.post('/api/system/update').set('Origin', ORIGIN).set('X-CSRF-Token', csrf).send(body as object);
    await fox.agent.post('/api/system/update/check').set('Origin', ORIGIN).set('X-CSRF-Token', fox.csrf).expect(200);
    mkdirSync(dir, { recursive: true });

    const vera = await login(app.url, 'vera');
    await vera.agent.post('/api/system/update').set('Origin', ORIGIN).set('X-CSRF-Token', vera.csrf).send({ version: '1.2.0' }).expect(403);
    await post({ version: '1.2.0' }, 'wrong-token').expect(403);
    await post({ version: 'latest' }).expect(400);
    await post({ version: '1.2.0', force: true }).expect(400);
    await post({ version: '1.1.0' }).expect(400);
    expect(existsSync(join(dir, 'request.json'))).toBe(false);

    const ok = await post({ version: '1.2.0' }).expect(200);
    expect(ok.body.update.request).toMatchObject({ version: '1.2.0', requestedBy: 'fox' });
    const file = JSON.parse(readFileSync(join(dir, 'request.json'), 'utf8')) as Record<string, unknown>;
    expect(Object.keys(file).sort()).toEqual(['requestedAt', 'requestedBy', 'version']);
    if (process.platform !== 'win32') expect(statSync(join(dir, 'request.json')).mode & 0o777).toBe(0o640);
    const pending = await post({ version: '1.2.0' }).expect(409);
    expect(pending.body.code).toBe('conflict');

    // The root updater picked it up and is running.
    writeFileSync(join(dir, 'status.json'), JSON.stringify({ state: 'running', version: '1.2.0', from: '1.1.0', startedAt: '2026-11-12T10:00:00Z', log: ['downloading'] }));
    rmSync(join(dir, 'request.json'));
    await post({ version: '1.2.0' }).expect(409);
    const s = await fox.agent.get('/api/system/update').expect(200);
    expect(s.body.update).toMatchObject({ mode: 'trigger', request: null, status: { state: 'running', log: ['downloading'] } });

    const audit = await fox.agent.get('/api/audit?kind=system_update').expect(200);
    const items = audit.body.items as Array<{ ok: boolean; target: string; summary: string }>;
    expect(items.filter((i) => i.ok)).toHaveLength(1);
    expect(items.find((i) => i.ok)?.summary).toBe('Update from v1.1.0 to v1.2.0 requested');
    expect(items.some((i) => !i.ok && /already/.test(i.summary))).toBe(true);
  });

  it('docker and helm installs get a command and cannot trigger', async () => {
    const { app } = await boot('docker', { MINECLAW_COMPOSE_DIR: '/srv/mineclaw' });
    const fox = await login(app.url, 'fox');
    const r = await fox.agent.post('/api/system/update/check').set('Origin', ORIGIN).set('X-CSRF-Token', fox.csrf).expect(200);
    expect(r.body.update).toEqual({
      mode: 'command',
      command: "cd /srv/mineclaw && sed -i '/^MINECLAW_VERSION=/d' .env && echo MINECLAW_VERSION=1.2.0 >> .env && docker compose pull && docker compose up -d",
    });
    await fox.agent.post('/api/system/update').set('Origin', ORIGIN).set('X-CSRF-Token', fox.csrf).send({ version: '1.2.0' }).expect(400);

    const helm = await boot('helm', { MINECLAW_HELM_RELEASE: 'mc', MINECLAW_HELM_NAMESPACE: 'games' });
    const h = await login(helm.app.url, 'fox');
    const hr = await h.agent.post('/api/system/update/check').set('Origin', ORIGIN).set('X-CSRF-Token', h.csrf).expect(200);
    expect(hr.body.update.command).toBe('helm upgrade mc oci://ghcr.io/realynx/charts/mineclaw --version 1.2.0 -n games --reuse-values');
  });

  it('disabled: no network, mode none, check refused', async () => {
    const { app, fetcher } = await boot(undefined, {}, false);
    const fox = await login(app.url, 'fox');
    const s = await fox.agent.get('/api/system/update').expect(200);
    expect(s.body).toMatchObject({ enabled: false, installKind: 'manual', latest: null, update: { mode: 'none' } });
    await fox.agent.post('/api/system/update/check').set('Origin', ORIGIN).set('X-CSRF-Token', fox.csrf).expect(400);
    expect(fetcher).not.toHaveBeenCalled();
  });
});
