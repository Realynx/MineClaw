import { mkdtempSync, readFileSync, readdirSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import request from 'supertest';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { ROUTES } from '@mineclaw/contracts';
import { HumanSkins, trustedSkinUrl } from '../src/skins/human-skins.js';
import { login, ORIGIN, PASSWORD, startApp } from './helpers.js';

const dirs: string[] = [], services: HumanSkins[] = [];
afterEach(() => {
  for (const service of services.splice(0)) service.close();
  for (const dir of dirs.splice(0)) rmSync(dir, { recursive: true, force: true });
});
const temp = () => { const dir = mkdtempSync(join(tmpdir(), 'mc-skins-')); dirs.push(dir); return dir; };
const png = () => {
  const b = Buffer.alloc(33);
  Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]).copy(b);
  b.write('IHDR', 12); b.writeUInt32BE(64, 16); b.writeUInt32BE(64, 20);
  return b;
};
function fixture(url = 'http://textures.minecraft.net/texture/' + 'a'.repeat(64), image = png()) {
  const bridge = { json: vi.fn(() => Promise.resolve({ status: 200, headers: {}, text: JSON.stringify({ bots: [{ name: 'maid_chan' }], entities: [] }) })) };
  const fetcher = vi.fn<typeof fetch>((input) => {
    const target = typeof input === 'string' ? input : input instanceof URL ? input.href : input.url;
    if (target.startsWith('https://api.mojang.com/')) return Promise.resolve(Response.json({ id: 'b'.repeat(32) }));
    if (target.startsWith('https://sessionserver.mojang.com/')) return Promise.resolve(Response.json({ properties: [{ name: 'textures', value: Buffer.from(JSON.stringify({ textures: { SKIN: { url, metadata: { model: 'slim' } } } })).toString('base64') }] }));
    return Promise.resolve(new Response(image));
  });
  const dir = temp();
  const service = new HumanSkins(dir, bridge, fetcher); services.push(service);
  return { service, bridge, fetcher, dir };
}

describe('human-only memory skin cache', () => {
  it('defaults off, warms known players, deduplicates concurrent joins and failures until restart', async () => {
    const { service, bridge, fetcher, dir } = fixture();
    service.start(); service.observeWorld({ entities: [{ kind: 'player', name: 'NewPlayer' }] });
    expect(fetcher).not.toHaveBeenCalled(); expect(bridge.json).not.toHaveBeenCalled();
    service.save(true);
    service.observeWorld({ entities: [{ kind: 'player', name: 'NEWPLAYER' }, { kind: 'mob', name: 'NotAPlayer' }] });
    await vi.waitFor(() => expect(service.settings().cached).toBe(2));
    expect(fetcher).toHaveBeenCalledTimes(6);
    const feed = service.feed();
    expect(feed.skins.map((s) => s.name).sort()).toEqual(['maid_chan', 'newplayer']);
    expect(feed.skins[0]?.slim).toBe(true);
    expect(service.feed({ epoch: feed.epoch, since: feed.revision }).skins).toEqual([]);
    service.save(false); expect(service.feed().skins).toEqual([]);
    service.save(true);
    await new Promise((resolve) => setTimeout(resolve, 20));
    expect(fetcher).toHaveBeenCalledTimes(6);
    expect(readdirSync(dir)).toEqual(['human-skins.json']);
    expect(JSON.parse(readFileSync(join(dir, 'human-skins.json'), 'utf8'))).toEqual({ enabled: true });
    service.close();
    const restarted = new HumanSkins(dir, bridge, fetcher); services.push(restarted);
    expect(restarted.settings()).toEqual({ enabled: true, cached: 0, unavailable: 0 });
    expect(restarted.epoch).not.toBe(feed.epoch);
    restarted.start();
    await vi.waitFor(() => expect(restarted.settings().cached).toBe(1));
    expect(fetcher).toHaveBeenCalledTimes(9);
  });

  it('caches failures once and rejects foreign URLs, redirects and invalid or oversized images', async () => {
    for (const url of ['https://evil.example/texture/' + 'a'.repeat(64), 'https://textures.minecraft.net.evil.example/texture/' + 'a'.repeat(64), 'https://textures.minecraft.net/texture/' + 'a'.repeat(64) + '?x=1']) expect(() => trustedSkinUrl(url)).toThrow();
    expect(trustedSkinUrl('http://textures.minecraft.net/texture/' + 'a'.repeat(64))).toMatch(/^https:/);
    for (const [url, image] of [
      ['https://evil.example/texture/' + 'a'.repeat(64), png()],
      ['https://textures.minecraft.net/texture/' + 'a'.repeat(64), Buffer.alloc(65537)],
      ['https://textures.minecraft.net/texture/' + 'a'.repeat(64), Buffer.alloc(33)],
    ] as const) {
      const { service, fetcher } = fixture(url, image);
      service.save(true);
      await vi.waitFor(() => expect(service.settings().unavailable).toBe(1));
      const calls = fetcher.mock.calls.length;
      service.observeWorld({ bots: [{ name: 'MAID_CHAN' }] });
      expect(fetcher).toHaveBeenCalledTimes(calls);
      expect(service.feed().skins).toEqual([]);
      for (const call of fetcher.mock.calls) expect(call[1]?.redirect).toBe('error');
    }
  });

  it('keeps the cosmetics route native, authenticated and separate from AI vision and entity responses', async () => {
    expect(ROUTES['world.humanSkins'].bridge).toBeNull();
    expect(ROUTES['vision.render'].bridge).not.toBeNull();
    const t = await startApp();
    try {
      expect((await request(t.url).get('/api/human-skins')).status).toBe(401);
      const viewer = await login(t.url, 'vera');
      const feed = await viewer.agent.get('/api/human-skins');
      expect(feed.status).toBe(200); expect(feed.headers['cache-control']).toBe('no-store');
      expect(feed.body).toMatchObject({ enabled: false, revision: 0, skins: [] });
      expect((await viewer.agent.get('/api/admin/human-skins')).status).toBe(403);
      const admin = await login(t.url, 'fox');
      const save = () => admin.agent.post('/api/admin/human-skins').set('Origin', ORIGIN).set('X-CSRF-Token', admin.csrf).send({ enabled: true, reason: 'enable human cosmetics' });
      expect((await save()).body.code).toBe('step_up_required');
      expect((await admin.agent.post('/api/auth/step-up').set('Origin', ORIGIN).set('X-CSRF-Token', admin.csrf).send({ password: PASSWORD })).status).toBe(200);
      expect((await admin.agent.post('/api/admin/human-skins').set('Origin', ORIGIN).send({ enabled: true, reason: 'test' })).body.code).toBe('csrf');
      expect((await save()).body.enabled).toBe(true);
      const audit = await admin.agent.get('/api/audit?kind=admin_settings');
      expect(JSON.stringify(audit.body)).toContain('enable human cosmetics');
      expect(JSON.stringify(audit.body)).not.toContain('textures.minecraft.net');
    } finally { await t.close(); }
  });
});
