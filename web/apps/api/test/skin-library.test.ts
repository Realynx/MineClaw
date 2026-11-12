import { mkdtempSync, readFileSync, readdirSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import request from 'supertest';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { ROUTES, SKIN_LIBRARY_MAX } from '@mineclaw/contracts';
import { ApiHttpError } from '../src/common/errors.js';
import { HumanSkins } from '../src/skins/human-skins.js';
import { SkinLibrary } from '../src/skins/skin-library.js';
import { sanitizeSkinPng, SkinPngError } from '../src/skins/skin-png.js';
import { login, ORIGIN, startApp } from './helpers.js';
import { skinPng } from './skin-fixtures.js';

const dirs: string[] = [];
afterEach(() => {
  for (const dir of dirs.splice(0)) rmSync(dir, { recursive: true, force: true });
});
const temp = () => {
  const dir = mkdtempSync(join(tmpdir(), 'mc-pawn-skins-'));
  dirs.push(dir);
  return dir;
};
const status = (fn: () => unknown) => {
  try {
    fn();
  } catch (e) {
    return e instanceof ApiHttpError ? e.status : -1;
  }
  return 0;
};

describe('skin PNG checks', () => {
  it('accepts 64×32 and 64×64 skins and strips metadata chunks', () => {
    expect(sanitizeSkinPng(skinPng({ height: 32 })).height).toBe(32);
    const tagged = skinPng({ text: 'made in an editor, at /home/someone' });
    const clean = sanitizeSkinPng(tagged);
    expect(clean.height).toBe(64);
    expect(clean.png.length).toBeLessThan(tagged.length);
    expect(clean.png.includes(Buffer.from('tEXt'))).toBe(false);
    expect(clean.png.equals(sanitizeSkinPng(clean.png).png)).toBe(true);
  });

  it('refuses other sizes, non-PNGs, damaged and oversized files', () => {
    const bad = [
      skinPng({ width: 32, height: 32 }),
      skinPng({ width: 64, height: 128 }),
      Buffer.from('GIF89a not a png at all, padded out to some length'),
      Buffer.alloc(65 * 1024),
    ];
    const flipped = skinPng();
    flipped[45] = flipped[45]! ^ 0xff; // inside IDAT: the chunk checksum no longer matches
    bad.push(flipped, skinPng().subarray(0, 60));
    for (const b of bad) expect(() => sanitizeSkinPng(b)).toThrow(SkinPngError);
    expect(() => sanitizeSkinPng(skinPng({ width: 64, height: 16 }))).toThrow(/64×32.*64×64.*64×16/);
  });
});

describe('pawn skin library', () => {
  it('stores each image once, assigns pawns, and keeps both across a restart', () => {
    const dir = temp();
    const lib = new SkinLibrary(dir);
    const a = lib.add({ label: 'Knight', slim: false, png: skinPng(), source: 'upload', by: 'sky' });
    const again = lib.add({ label: 'Knight 2', slim: true, png: skinPng({ text: 'same pixels' }), source: 'upload' });
    expect(again.id).toBe(a.id);
    expect(again.label).toBe('Knight 2');
    expect(again.createdBy).toBe('sky');
    const legacy = lib.add({ label: 'Old', slim: true, png: skinPng({ height: 32, rgba: [1, 2, 3, 255] }), source: 'upload' });
    expect(legacy.slim).toBe(false); // 64×32 has no slim layout
    lib.assign(['Maid_Chan', 'kiwi_bot'], { kind: 'library', id: a.id });
    lib.assign(['Steve_Fan'], { kind: 'default', id: 'alex' });
    expect(lib.view(false).assignments).toEqual({
      maid_chan: { kind: 'library', id: a.id },
      kiwi_bot: { kind: 'library', id: a.id },
      steve_fan: { kind: 'default', id: 'alex' },
    });
    expect(readdirSync(join(dir, 'skins')).sort()).toEqual([`${a.id}.png`, `${legacy.id}.png`, 'pawn-skins.json'].sort());
    expect(readFileSync(join(dir, 'skins', 'pawn-skins.json'), 'utf8')).not.toContain('iVBOR'); // no PNG data in the index

    const restarted = new SkinLibrary(dir);
    expect(restarted.view(true)).toEqual({ ...lib.view(false), lookup: true });
    expect(restarted.assigned()).toEqual(
      expect.arrayContaining([
        { name: 'steve_fan', slim: true, height: 64, png: '', base: 'alex' },
        expect.objectContaining({ name: 'maid_chan', slim: true, height: 64 }),
      ]),
    );
  });

  it('deleting a worn skin puts its pawns back on the default appearance; unknown skins are refused', () => {
    const lib = new SkinLibrary(temp());
    const a = lib.add({ label: 'A', slim: false, png: skinPng(), source: 'upload' });
    lib.assign(['one'], { kind: 'library', id: a.id });
    const gen = lib.generation;
    lib.remove(a.id);
    expect(lib.generation).toBeGreaterThan(gen);
    expect(lib.view(false)).toMatchObject({ library: [], assignments: {} });
    expect(status(() => lib.remove(a.id))).toBe(404);
    expect(status(() => lib.assign(['one'], { kind: 'library', id: a.id }))).toBe(404);
    expect(status(() => lib.add({ label: 'x', slim: false, png: Buffer.from('nope'), source: 'upload' }))).toBe(400);
    lib.assign(['one'], { kind: 'default', id: 'steve' });
    lib.assign(['ONE'], null);
    expect(lib.view(false).assignments).toEqual({});
  });

  it('caps the library and survives a damaged index or a missing PNG', () => {
    const dir = temp();
    const lib = new SkinLibrary(dir);
    for (let i = 0; i < SKIN_LIBRARY_MAX; i++) lib.add({ label: `s${i}`, slim: false, png: skinPng({ rgba: [i, 0, 0, 255] }), source: 'upload' });
    expect(status(() => lib.add({ label: 'one more', slim: false, png: skinPng({ rgba: [0, 0, 1, 255] }), source: 'upload' }))).toBe(409);
    const first = lib.view(false).library[0]!;
    rmSync(join(dir, 'skins', `${first.id}.png`));
    expect(new SkinLibrary(dir).view(false).library).toHaveLength(SKIN_LIBRARY_MAX - 1);
    writeFileSync(join(dir, 'skins', 'pawn-skins.json'), '{ not json');
    expect(new SkinLibrary(dir).view(false).library).toEqual([]);
  });
});

describe('pawn skins in the human world-view feed', () => {
  const bridge = { json: vi.fn(() => Promise.resolve({ status: 200, headers: {}, text: '{"bots":[],"entities":[]}' })) };

  it('serves assignments with the official setting off, restarts the feed on every change, and wins over official skins', async () => {
    const dir = temp();
    const fetcher = vi.fn<typeof fetch>(() => Promise.reject(new Error('offline')));
    const skins = new HumanSkins(dir, bridge, fetcher);
    try {
      expect(skins.feed()).toMatchObject({ enabled: false, skins: [] });
      const a = skins.library.add({ label: 'A', slim: true, png: skinPng(), source: 'upload' });
      skins.library.assign(['Maid_Chan'], { kind: 'library', id: a.id });
      const first = skins.feed();
      expect(first.enabled).toBe(true);
      expect(first.skins).toEqual([{ name: 'maid_chan', slim: true, height: 64, png: a.png }]);
      // Same epoch: nothing new to send.
      expect(skins.feed({ epoch: first.epoch, since: first.revision }).skins).toEqual([]);
      skins.library.assign(['maid_chan'], { kind: 'default', id: 'steve' });
      const second = skins.feed({ epoch: first.epoch, since: first.revision });
      expect(second.epoch).not.toBe(first.epoch);
      expect(second.skins).toEqual([{ name: 'maid_chan', slim: false, height: 64, png: '', base: 'steve' }]);
      skins.library.assign(['maid_chan'], null);
      expect(skins.feed({ epoch: second.epoch })).toMatchObject({ enabled: false, skins: [] });
      expect(fetcher).not.toHaveBeenCalled(); // pawn skins never reach out to the profile service
      await expect(skins.lookup('kiwi')).rejects.toMatchObject({ status: 409 });
      expect(fetcher).not.toHaveBeenCalled();
      skins.save(true);
      await expect(skins.lookup('kiwi')).rejects.toMatchObject({ status: 404 });
    } finally {
      skins.close();
    }
  });
});

describe('skin routes', () => {
  it('lists for viewers, writes for the story role only, checks CSRF, sizes and formats, and audits without image data', async () => {
    expect(['skins.list', 'skins.upload', 'skins.delete', 'skins.assign', 'skins.lookup'].map((id) => ROUTES[id as 'skins.list'].bridge)).toEqual([null, null, null, null, null]);
    const t = await startApp();
    try {
      expect((await request(t.url).get('/api/skins')).status).toBe(401);
      const viewer = await login(t.url, 'vera');
      expect((await viewer.agent.get('/api/skins')).body).toEqual({ library: [], assignments: {}, lookup: false });
      const png = skinPng({ text: 'private editor path' }).toString('base64');
      const post = (s: typeof viewer, path: string, body: unknown) =>
        s.agent.post(path).set('Origin', ORIGIN).set('X-CSRF-Token', s.csrf).send(body as object);
      expect((await post(viewer, '/api/skins', { label: 'Knight', png })).status).toBe(403);

      const story = await login(t.url, 'sky');
      expect((await story.agent.post('/api/skins').set('Origin', ORIGIN).send({ label: 'Knight', png })).body.code).toBe('csrf');
      const up = await post(story, '/api/skins', { label: 'Knight', slim: true, png });
      expect(up.status).toBe(200);
      const id = up.body.skin.id as string;
      expect(up.body.skin).toMatchObject({ label: 'Knight', slim: true, height: 64, source: 'upload', createdBy: 'sky' });
      expect(Buffer.from(up.body.skin.png, 'base64').includes(Buffer.from('private editor path'))).toBe(false);

      // Format and size: not a skin size, not a PNG, over 64 KiB, and over the schema's base64 bound.
      const wrongSize = await post(story, '/api/skins', { label: 'Big', png: skinPng({ width: 128, height: 128 }).toString('base64') });
      expect(wrongSize.status).toBe(400);
      expect(wrongSize.body.error).toMatch(/64×32.*64×64/);
      expect((await post(story, '/api/skins', { label: 'Txt', png: Buffer.from('hello').toString('base64') })).status).toBe(400);
      expect((await post(story, '/api/skins', { label: 'Huge', png: 'A'.repeat(87384) })).body.error).toMatch(/64 KiB/);
      expect((await post(story, '/api/skins', { label: 'Huger', png: 'A'.repeat(87388) })).status).toBe(400);
      expect((await post(story, '/api/skins', { label: '', png })).status).toBe(400);

      const assigned = await post(story, '/api/skins/assign', { pawns: ['Maid_Chan'], skin: { kind: 'library', id } });
      expect(assigned.body.assignments).toEqual({ maid_chan: { kind: 'library', id } });
      expect((await post(story, '/api/skins/assign', { pawns: ['maid_chan'], skin: { kind: 'library', id: 'f'.repeat(24) } })).status).toBe(404);
      expect((await post(story, '/api/skins/assign', { pawns: ['bad name!'], skin: null })).status).toBe(400);
      const feed = await viewer.agent.get('/api/human-skins');
      expect(feed.body.enabled).toBe(true);
      expect(feed.body.skins).toEqual([{ name: 'maid_chan', slim: true, height: 64, png: up.body.skin.png }]);

      expect((await post(story, '/api/skins/lookup', { name: 'kiwi' })).status).toBe(409);
      expect((await post(viewer, '/api/skins/delete', { id })).status).toBe(403);
      expect((await post(story, '/api/skins/delete', { id })).body).toEqual({ library: [], assignments: {}, lookup: false });
      expect((await viewer.agent.get('/api/human-skins')).body).toMatchObject({ enabled: false, skins: [] });

      const admin = await login(t.url, 'fox');
      const audit = JSON.stringify((await admin.agent.get('/api/audit?kind=story')).body);
      expect(audit).toContain('Skin uploaded: Knight');
      expect(audit).toContain('Skin for Maid_Chan: library');
      expect(audit).not.toContain(png.slice(0, 40));
    } finally {
      await t.close();
    }
  });
});
