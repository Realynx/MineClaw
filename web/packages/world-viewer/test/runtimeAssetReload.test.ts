import { afterEach, expect, it, vi, type Mock } from 'vitest';
import type { AssetBundle } from '../src/assets/bundle';
import type { GameVersion } from '../src/world/blocks';
import { WorldRuntime } from '../src/render/runtime';

const load = vi.hoisted(() => vi.fn<(version: GameVersion, retryMissing?: boolean) => Promise<AssetBundle>>());
vi.mock('../src/assets/bundle', () => ({ loadBundle: load }));
vi.mock('../src/render/blockAtlas', () => ({ BlockAtlas: class { constructor(readonly bundle: AssetBundle) {} bind() {} dispose() {} } }));
const deferred = <T>() => { let resolve!: (value: T) => void; const promise = new Promise<T>((r) => { resolve = r; }); return { promise, resolve }; };
const tick = async () => { for (let i = 0; i < 5; i++) await Promise.resolve(); };
afterEach(() => { vi.useRealTimers(); load.mockReset(); });

interface BundleLayer { setBundle: Mock<(bundle: AssetBundle) => void> }
interface TestRuntime {
  loadSkyBundle(version: GameVersion): void;
  loadEntityBundle(version: GameVersion): void;
  loadBlockAtlas(version: GameVersion): Promise<void>;
  assetGeneration: number;
  assetsFailed: boolean;
  atlasFor: GameVersion | null;
  entityBundle: GameVersion | null;
  blockAtlas: { bundle: AssetBundle } | null;
  sky: BundleLayer;
  entities: BundleLayer & { setVersion: Mock<(version: GameVersion) => void> };
  blockEntityLayer: BundleLayer;
  configure: Mock<() => Promise<void>>;
  setMeshHold: Mock<(held: boolean) => void>;
  retryAssets: Mock<() => void>;
}

/** Invoke the real loading methods without allocating a WebGL context or worker. */
function testRuntime(): TestRuntime {
  const layer = (): BundleLayer => ({ setBundle: vi.fn<(bundle: AssetBundle) => void>() });
  const prototype = Object.create(WorldRuntime.prototype) as Pick<TestRuntime, 'loadSkyBundle' | 'loadEntityBundle' | 'loadBlockAtlas'>;
  return Object.assign(prototype, {
    disposed: false, hidden: false, paused: false, assetGeneration: 0, assetsFailed: false, assetRetryDelay: 1000,
    version: '1.12.2', atlasFor: null, entityBundle: null, blockAtlas: null,
    sky: layer(), entities: { ...layer(), setVersion: vi.fn<(version: GameVersion) => void>() }, blockEntityLayer: layer(),
    uniforms: {}, opts: { onError: vi.fn<(route: string, error: unknown) => void>() },
    configure: vi.fn(() => Promise.resolve()), setMeshHold: vi.fn<(held: boolean) => void>(),
    // Arrow methods belong to real instances. This harness observes timer dispatch; the browser harness tests recovery.
    retryAssets: vi.fn<() => void>(),
  });
}

it('schedules recovery after a failed local atlas read and releases the temporary mesh hold', async () => {
  vi.useFakeTimers();
  const fresh = { version: '1.12.2' } as AssetBundle;
  load.mockRejectedValueOnce(new Error('Storage was interrupted')).mockResolvedValue(fresh);
  const runtime = testRuntime();
  await runtime.loadBlockAtlas('1.12.2');
  expect(runtime.assetsFailed).toBe(true);
  expect(runtime.atlasFor).toBeNull();
  expect(runtime.setMeshHold).toHaveBeenCalledWith(false);
  await vi.advanceTimersByTimeAsync(1000);
  expect(runtime.retryAssets).toHaveBeenCalledOnce();
  await runtime.loadBlockAtlas('1.12.2');
  expect(runtime.blockAtlas?.bundle).toBe(fresh);
  expect(runtime.blockEntityLayer.setBundle).toHaveBeenCalledWith(fresh);
});

it('keeps a replaced same-version atlas, sky, and entities when an older load finishes last', async () => {
  const old = deferred<AssetBundle>(), fresh = { version: '1.12.2' } as AssetBundle, stale = { ...fresh };
  load.mockReturnValueOnce(old.promise).mockReturnValueOnce(old.promise).mockReturnValueOnce(old.promise).mockResolvedValue(fresh);
  // Exercise the real asynchronous loading methods without requiring WebGL or a worker in this race regression.
  const runtime = testRuntime();
  runtime.loadSkyBundle('1.12.2'); runtime.loadEntityBundle('1.12.2'); const pending = runtime.loadBlockAtlas('1.12.2');
  runtime.assetGeneration++; runtime.entityBundle = null;
  runtime.loadSkyBundle('1.12.2'); runtime.loadEntityBundle('1.12.2'); await runtime.loadBlockAtlas('1.12.2');
  old.resolve(stale); await pending; await tick();
  expect(runtime.blockAtlas?.bundle).toBe(fresh);
  expect(runtime.sky.setBundle.mock.calls).toEqual([[fresh]]);
  expect(runtime.entities.setBundle.mock.calls).toEqual([[fresh]]);
  expect(runtime.blockEntityLayer.setBundle.mock.calls).toEqual([[fresh]]);
});
