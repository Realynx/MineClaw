import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import type { LocalPack } from '../src/assets/localStore';
import type { GameVersion } from '../src/world/blocks';

const read = vi.hoisted(() => vi.fn<(version: GameVersion) => Promise<LocalPack | null>>());
vi.mock('../src/assets/localStore', () => ({ ASSETS_CHANGED: 'assets-changed', readLocalPack: read }));
vi.mock('../src/assets/fallback', () => ({ fallbackBundle: vi.fn((version: GameVersion) => Promise.resolve({ version, fallback: true })) }));
const deferred = <T>() => { let resolve!: (value: T) => void; let reject!: (e: Error) => void; const promise = new Promise<T>((r, j) => { resolve = r; reject = j; }); return { promise, resolve, reject }; };
const pack = (name: string): LocalPack => ({ name, version: '1.12.2', importedAt: 0,
  pack: { version: '1.12.2', tileSize: 16, blocks: { columns: 32, rows: 1, tiles: {}, info: {}, sheets: {} },
    items: { columns: 32, rows: 1, tiles: {} }, entity: { width: 16, height: 16, rects: {} }, env: [], anims: [],
    models: {}, itemModels: {}, blockstates: {}, biomes: {} }, images: { blocks: new Blob([name]) }, sounds: {}, soundEvents: {} });
const tick = async () => { for (let i = 0; i < 5; i++) await Promise.resolve(); };

beforeEach(() => {
  vi.resetModules(); read.mockReset();
  vi.stubGlobal('window', new EventTarget());
  vi.stubGlobal('createImageBitmap', vi.fn(() => Promise.resolve({ close: vi.fn() })));
  vi.spyOn(URL, 'createObjectURL').mockImplementation(() => 'blob:' + Math.random());
  vi.spyOn(URL, 'revokeObjectURL').mockImplementation(() => {});
});
afterEach(() => { vi.restoreAllMocks(); vi.unstubAllGlobals(); });

it('retries a transient storage failure instead of remembering that the imported JAR is missing', async () => {
  read.mockRejectedValueOnce(new Error('IndexedDB temporarily unavailable')).mockResolvedValue(pack('installed'));
  const { loadBundle } = await import('../src/assets/bundle');
  await expect(loadBundle('1.12.2')).rejects.toThrow('temporarily unavailable');
  const restored = await loadBundle('1.12.2');
  expect(restored.fallback).not.toBe(true);
  expect(read).toHaveBeenCalledTimes(2);
  expect(await loadBundle('1.12.2')).toBe(restored);
});

it('rechecks a missing pack on window return without discarding a healthy cached import', async () => {
  read.mockResolvedValueOnce(null).mockResolvedValue(pack('installed in another window'));
  const { loadBundle } = await import('../src/assets/bundle');
  expect((await loadBundle('1.12.2')).fallback).toBe(true);
  const restored = await loadBundle('1.12.2', true);
  expect(restored.fallback).not.toBe(true);
  expect(await loadBundle('1.12.2', true)).toBe(restored);
  expect(read).toHaveBeenCalledTimes(2);
});

it('retains the same placeholder bundle when a focus recheck still finds no import', async () => {
  read.mockResolvedValue(null);
  const { loadBundle } = await import('../src/assets/bundle');
  const first = await loadBundle('1.12.2');
  expect(await loadBundle('1.12.2', true)).toBe(first);
  expect(read).toHaveBeenCalledTimes(2);
});

it('redirects an obsolete database read to the current imported pack', async () => {
  const old = deferred<LocalPack>(); read.mockReturnValueOnce(old.promise).mockResolvedValue(pack('new'));
  const { loadBundle, bundleUrls } = await import('../src/assets/bundle');
  const pending = loadBundle('1.12.2');
  window.dispatchEvent(new Event('assets-changed'));
  const fresh = await loadBundle('1.12.2'), urls = bundleUrls('1.12.2');
  old.resolve(pack('old'));
  expect(await pending).toBe(fresh);
  expect(bundleUrls('1.12.2')).toBe(urls);
  expect(createImageBitmap).toHaveBeenCalledOnce();
});

it.each([false, true])('discards stale decoded resources without replacing the new cache (failed=%s)', async (failed) => {
  const old = deferred<ImageBitmap>(), close = vi.fn<() => void>(), stale = { close } as unknown as ImageBitmap;
  read.mockResolvedValueOnce(pack('old')).mockResolvedValue(pack('new'));
  vi.mocked(createImageBitmap).mockReturnValueOnce(old.promise);
  const { loadBundle, bundleUrls } = await import('../src/assets/bundle');
  const pending = loadBundle('1.12.2'); await tick();
  window.dispatchEvent(new Event('assets-changed'));
  const fresh = await loadBundle('1.12.2'), urls = bundleUrls('1.12.2');
  if (failed) old.reject(new Error('old decode failed')); else old.resolve(stale);
  expect(await pending).toBe(fresh);
  expect(await loadBundle('1.12.2')).toBe(fresh);
  expect(bundleUrls('1.12.2')).toBe(urls);
  expect(close).toHaveBeenCalledTimes(failed ? 0 : 1);
});
