import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import type { LocalPack } from '../src/assets/localStore';

const read = vi.hoisted(() => vi.fn());
vi.mock('../src/assets/localStore', () => ({ ASSETS_CHANGED: 'assets-changed', readLocalPack: read }));
const deferred = <T>() => { let resolve!: (value: T) => void; let reject!: (e: Error) => void; const promise = new Promise<T>((r, j) => { resolve = r; reject = j; }); return { promise, resolve, reject }; };
const pack = (name: string): LocalPack => ({ name, version: '1.12.2', importedAt: 0,
  pack: { version: '1.12.2', tileSize: 16, blocks: { columns: 32, rows: 1, tiles: {}, info: {}, sheets: {} },
    items: { columns: 32, rows: 1, tiles: {} }, entity: { width: 16, height: 16, rects: {} }, env: [], anims: [],
    models: {}, itemModels: {}, blockstates: {}, biomes: {} }, images: { blocks: new Blob([name]) }, sounds: {}, soundEvents: {} });
const tick = async () => { for (let i = 0; i < 5; i++) await Promise.resolve(); };

beforeEach(() => {
  vi.resetModules(); read.mockReset();
  vi.stubGlobal('window', new EventTarget());
  vi.stubGlobal('createImageBitmap', vi.fn(async () => ({ close: vi.fn() })));
  vi.spyOn(URL, 'createObjectURL').mockImplementation(() => 'blob:' + Math.random());
  vi.spyOn(URL, 'revokeObjectURL').mockImplementation(() => {});
});
afterEach(() => { vi.restoreAllMocks(); vi.unstubAllGlobals(); });

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
  const old = deferred<ImageBitmap>(), stale = { close: vi.fn() } as unknown as ImageBitmap;
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
  expect(stale.close).toHaveBeenCalledTimes(failed ? 0 : 1);
});
