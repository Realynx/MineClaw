import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { PawnSkins, SkinLibraryEntry } from '@mineclaw/contracts';
import { checkSkinFile, labelFromFileName, looksSlim, readSkinHeader, toBase64, fromBase64 } from '@/features/story/skins/skinFile';
import { entryOf, refLabel, sameRef, skinOf, versionNote, visibleLibrary, wearers } from '@/features/story/skins/skinLibrary';
import { frontParts } from '@/features/story/skins/skinPreview';
import { mockSkinAssign, mockSkinDelete, mockSkinFeed, mockSkinsView, mockSkinUpload, resetMockSkins } from '@/mocks/skins';
import { fakeImageBitmap, skinPng } from './skinPng';

beforeEach(() => void vi.stubGlobal('createImageBitmap', fakeImageBitmap));
afterEach(() => void vi.unstubAllGlobals());

const bytes = (b: Buffer) => new Uint8Array(b);

describe('skin file validation', () => {
  it('accepts 64×32 and 64×64 PNGs and names the problem otherwise', () => {
    expect(readSkinHeader(bytes(skinPng({ height: 32 })))).toEqual({ ok: true, width: 64, height: 32 });
    expect(readSkinHeader(bytes(skinPng()))).toEqual({ ok: true, width: 64, height: 64 });
    expect(readSkinHeader(bytes(skinPng({ width: 128, height: 128 })))).toEqual({
      ok: false,
      error: 'Skins are 64×32 (b1.8.1) or 64×64 (1.12.2); this image is 128×128',
    });
    expect(readSkinHeader(new TextEncoder().encode('just some text that is long enough to read'))).toMatchObject({ ok: false, error: 'Not a PNG image' });
    expect(readSkinHeader(new Uint8Array(65 * 1024))).toMatchObject({ ok: false, error: 'Skin files can be at most 64 KiB' });
  });

  it('checks a picked file: type, size and header, and returns its base64 and a label', async () => {
    const png = skinPng({ height: 32 });
    const ok = await checkSkinFile(new File([bytes(png)], 'my knight.png', { type: 'image/png' }));
    expect(ok).toMatchObject({ height: 32, slim: false, label: 'my knight' });
    expect(fromBase64(ok.png)).toEqual(bytes(png));
    await expect(checkSkinFile(new File(['x'], 'a.jpg', { type: 'image/jpeg' }))).rejects.toThrow('Choose a PNG image');
    await expect(checkSkinFile(new File([new Uint8Array(70_000)], 'big.png', { type: 'image/png' }))).rejects.toThrow(/at most 64 KiB/);
    await expect(checkSkinFile(new File([bytes(skinPng({ width: 64, height: 48 }))], 'odd.png', { type: 'image/png' }))).rejects.toThrow(/64×48/);
  });

  it('guesses slim arms from the empty arm column and labels from file names', () => {
    const rgba = new Uint8ClampedArray(64 * 64 * 4).fill(255);
    expect(looksSlim(rgba, 64)).toBe(false);
    for (const [x, y] of [[54, 20], [55, 20], [54, 31]] as const) rgba[(y * 64 + x) * 4 + 3] = 0;
    expect(looksSlim(rgba, 64)).toBe(true);
    expect(looksSlim(rgba, 32)).toBe(false);
    expect(labelFromFileName('Cool_Skin (2).PNG')).toBe('Cool_Skin (2)');
    expect(labelFromFileName('.png')).toBe('Skin');
    expect(labelFromFileName('x'.repeat(40) + '.png')).toHaveLength(32);
    expect(toBase64(new Uint8Array([1, 2, 3]))).toBe('AQID');
  });
});

describe('library and selection helpers', () => {
  const entry = (id: string, label: string, createdAt: string): SkinLibraryEntry => ({
    id: id.repeat(24).slice(0, 24), label, slim: false, height: 64, png: 'AA==', source: 'upload', createdAt,
  });
  const a = entry('a', 'Knight', '2026-11-01'), b = entry('b', 'Archer', '2026-11-03'), c = entry('c', 'Miner', '2026-11-02');
  const skins: PawnSkins = {
    library: [a, b, c],
    assignments: { maid_chan: { kind: 'library', id: a.id }, kiwi: { kind: 'library', id: a.id }, sky: { kind: 'default', id: 'alex' } },
    lookup: false,
  };

  it("reads a pawn's skin case-insensitively and labels it", () => {
    expect(skinOf(skins, 'Maid_Chan')).toEqual({ kind: 'library', id: a.id });
    expect(skinOf(skins, 'nobody')).toBeNull();
    expect(refLabel(skins, skinOf(skins, 'sky'))).toBe('Alex');
    expect(refLabel(skins, skinOf(skins, 'kiwi'))).toBe('Knight');
    expect(refLabel(skins, null)).toBe('Default appearance');
    expect(refLabel(skins, { kind: 'library', id: 'f'.repeat(24) })).toBe('Removed skin');
    expect(entryOf(skins, { kind: 'default', id: 'steve' })).toBeUndefined();
  });

  it('compares refs, counts wearers and orders the library (current first, then newest), with search', () => {
    expect(sameRef(null, undefined)).toBe(true);
    expect(sameRef({ kind: 'default', id: 'steve' }, { kind: 'default', id: 'steve' })).toBe(true);
    expect(sameRef({ kind: 'default', id: 'steve' }, { kind: 'default', id: 'alex' })).toBe(false);
    expect(sameRef({ kind: 'library', id: a.id }, null)).toBe(false);
    expect(wearers(skins).get(a.id)).toBe(2);
    expect(visibleLibrary(skins, '', null).map((s) => s.label)).toEqual(['Archer', 'Miner', 'Knight']);
    expect(visibleLibrary(skins, '', { kind: 'library', id: a.id }).map((s) => s.label)).toEqual(['Knight', 'Archer', 'Miner']);
    expect(visibleLibrary(skins, 'MIN', null).map((s) => s.label)).toEqual(['Miner']);
    expect(versionNote(32, '1.12.2')).toMatch(/no outer layers/);
    expect(versionNote(64, 'b1.8.1')).toMatch(/top half/);
    expect(versionNote(64, '1.12.2')).toBeNull();
  });

  it('lays out the flat preview from the right texture regions for each layout', () => {
    const legacy = frontParts(32, true);
    expect(legacy.filter((p) => p.flip).map((p) => p.src)).toEqual([[44, 20, 4, 12], [4, 20, 4, 12]]);
    expect(legacy.every((p) => p.src[1] + p.src[3] <= 32)).toBe(true);
    const slim = frontParts(64, true);
    expect(slim.find((p) => p.src[0] === 44 && p.src[1] === 20)).toMatchObject({ src: [44, 20, 3, 12], dst: [1, 8] });
    expect(slim.find((p) => p.src[0] === 36 && p.src[1] === 52)).toMatchObject({ dst: [12, 8] });
    expect(slim.filter((p) => p.overlay)).toHaveLength(6);
    for (const p of [...legacy, ...slim, ...frontParts(64, false)]) {
      expect(p.dst[0] + p.src[2]).toBeLessThanOrEqual(16);
      expect(p.dst[1] + p.src[3]).toBeLessThanOrEqual(32);
    }
  });
});

describe('mock gateway skins', () => {
  afterEach(() => resetMockSkins());
  const fail = (status: number, message: string) => Object.assign(new Error(message), { status });

  it('uploads, assigns, feeds the world views with a new epoch per change, and deletes', () => {
    const png = skinPng().toString('base64');
    const skin = mockSkinUpload({ label: 'Knight', slim: true, png }, 'fox', fail);
    expect(mockSkinUpload({ label: 'Again', png }, 'fox', fail).id).toBe(skin.id);
    expect(() => mockSkinUpload({ label: 'Big', png: skinPng({ width: 128, height: 128 }).toString('base64') }, 'fox', fail)).toThrow(/128×128/);
    expect(mockSkinFeed(false, {})).toMatchObject({ enabled: false, skins: [] });
    mockSkinAssign(['Maid_Chan'], { kind: 'library', id: skin.id }, fail);
    const feed = mockSkinFeed(false, {});
    expect(feed.skins).toEqual([{ name: 'maid_chan', slim: false, height: 64, png }]);
    expect(mockSkinFeed(false, { epoch: feed.epoch }).skins).toEqual([]);
    mockSkinAssign(['maid_chan'], { kind: 'default', id: 'alex' }, fail);
    expect(mockSkinFeed(false, { epoch: feed.epoch }).skins).toEqual([{ name: 'maid_chan', slim: true, height: 64, png: '', base: 'alex' }]);
    mockSkinAssign(['maid_chan'], { kind: 'library', id: skin.id }, fail);
    mockSkinDelete(skin.id, fail);
    expect(mockSkinsView(false)).toEqual({ library: [], assignments: {}, lookup: false });
    expect(() => mockSkinDelete(skin.id, fail)).toThrow('No such skin');
  });
});
