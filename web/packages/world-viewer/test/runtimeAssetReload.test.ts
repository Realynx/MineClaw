import { expect, it, vi } from 'vitest';
import type { AssetBundle } from '../src/assets/bundle';
import { WorldRuntime } from '../src/render/runtime';

const load = vi.hoisted(() => vi.fn());
vi.mock('../src/assets/bundle', () => ({ loadBundle: load }));
vi.mock('../src/render/blockAtlas', () => ({ BlockAtlas: class { constructor(readonly bundle: AssetBundle) {} bind() {} dispose() {} } }));
const deferred = <T>() => { let resolve!: (value: T) => void; const promise = new Promise<T>((r) => { resolve = r; }); return { promise, resolve }; };
const tick = async () => { for (let i = 0; i < 5; i++) await Promise.resolve(); };

it('keeps a replaced same-version atlas, sky, and entities when an older load finishes last', async () => {
  const old = deferred<AssetBundle>(), fresh = { version: '1.12.2' } as AssetBundle, stale = { ...fresh };
  load.mockReturnValueOnce(old.promise).mockReturnValueOnce(old.promise).mockReturnValueOnce(old.promise).mockResolvedValue(fresh);
  // Exercise the real asynchronous loading methods without requiring WebGL or a worker in this race regression.
  const runtime = Object.assign(Object.create(WorldRuntime.prototype), {
    disposed: false, assetGeneration: 0, version: '1.12.2', atlasFor: null, entityBundle: null, blockAtlas: null,
    sky: { setBundle: vi.fn() }, entities: { setVersion: vi.fn(), setBundle: vi.fn() }, blockEntityLayer: { setBundle: vi.fn() },
    uniforms: {}, opts: { onError: vi.fn() }, configure: vi.fn(async () => {}),
  });
  runtime.loadSkyBundle('1.12.2'); runtime.loadEntityBundle('1.12.2'); const pending = runtime.loadBlockAtlas('1.12.2');
  runtime.assetGeneration++; runtime.entityBundle = null;
  runtime.loadSkyBundle('1.12.2'); runtime.loadEntityBundle('1.12.2'); await runtime.loadBlockAtlas('1.12.2');
  old.resolve(stale); await pending; await tick();
  expect(runtime.blockAtlas.bundle).toBe(fresh);
  expect(runtime.sky.setBundle.mock.calls).toEqual([[fresh]]);
  expect(runtime.entities.setBundle.mock.calls).toEqual([[fresh]]);
  expect(runtime.blockEntityLayer.setBundle.mock.calls).toEqual([[fresh]]);
});
