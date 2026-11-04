// Day and night: a view's sky light follows the world time the index (or another feed of the server clock, Story Mode's
// colony state) anchors, advanced at 20 ticks/s; a stopped daylight cycle holds. A stand-in WebGL renderer, the fixture world
// and a scripted top-down view (Story Mode's), stepped by hand.
import * as THREE from 'three';
import { afterAll, afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import type { RuntimeView, ViewSetup } from '../src/render/runtime';

vi.mock('three', async (orig) => {
  const real = await orig<typeof THREE>();
  class FakeRenderer {
    readonly domElement = {
      style: {},
      width: 300,
      height: 150,
      tabIndex: 0,
      remove: () => undefined,
    } as unknown as HTMLCanvasElement;
    readonly info = { render: { calls: 0, triangles: 0 } };
    readonly renderLists = { dispose: () => undefined };
    readonly renders: Array<{ camera: THREE.Camera }> = [];
    setPixelRatio() {}
    setScissorTest() {}
    setScissor() {}
    setViewport() {}
    setSize(w: number, h: number) {
      (this.domElement as { width: number }).width = w;
      (this.domElement as { height: number }).height = h;
    }
    getContext() {
      return {
        getExtension: () => null,
        getParameter: () => 'fake',
        RENDERER: 0,
        readPixels: () => undefined,
        RGBA: 0,
        UNSIGNED_BYTE: 0,
      };
    }
    render(_scene: THREE.Scene, camera: THREE.Camera) {
      this.renders.push({ camera });
    }
    dispose() {}
    forceContextLoss() {}
  }
  return { ...real, WebGLRenderer: FakeRenderer };
});

const { WorldRuntime } = await import('../src/render/runtime');
const { resetViewerQuality } = await import('../src/render/viewerQuality');
const { WorldCache } = await import('../src/world/cache');
const { normalizeIndex } = await import('../src/wire/normalize');
const { createFixtureSource } = await import('../src/testing');

beforeAll(() => {
  const g = globalThis as Record<string, unknown>;
  g.requestAnimationFrame ??= () => 0;
  g.cancelAnimationFrame ??= () => undefined;
  g.fetch = () => Promise.reject(new Error('no assets in this test'));
  const doc = (g.document ??= {}) as Record<string, unknown>;
  doc.createElement ??= () => ({ width: 0, height: 0, getContext: () => null });
  doc.addEventListener ??= () => undefined;
  doc.removeEventListener ??= () => undefined;
  doc.visibilityState ??= 'visible';
});
afterEach(() => resetViewerQuality());
afterAll(() => vi.restoreAllMocks());

/** A top-down view over (0, 64, 0) at zoom 112, as Story Mode zoomed all the way out. */
function topView(rt: InstanceType<typeof WorldRuntime>): RuntimeView & { setup: ViewSetup } {
  const camera = new THREE.PerspectiveCamera(12, 1.6, 100, 4000);
  camera.position.set(0, 64 + 56 / Math.tan((6 * Math.PI) / 180), 0);
  camera.up.set(0, 0, -1);
  camera.lookAt(0, 64, 0);
  camera.updateMatrixWorld();
  const setup: ViewSetup = {
    dim: 0,
    camera,
    centre: new THREE.Vector3(0, 64, 0),
    drawDistance: 112,
    cover: 117,
    width: 1280,
    height: 800,
    layers: { bots: true, entities: true, weather: false, staleness: false, changes: false }, // Story Mode's layers
    labels: true,
    overlays: false,
    selected: null,
    cones: [],
    floorY: 0,
    roof: null,
    ghosts: true,
    viewId: 77,
    target: 'primary',
  };
  const v = {
    id: 77,
    setup,
    interest: () => ({ dim: 0 as const, x: 0, z: 0, r: 140 }),
    centre: () => ({ dim: 0 as const, x: 0, y: 64, z: 0, distance: 112, cover: 117 }),
    frame: () => setup,
    overlayDim: () => null,
    selectedBots: () => [],
  };
  rt.addView(v);
  rt.attachPrimary(v, { appendChild: () => undefined, clientWidth: 1280, clientHeight: 800 } as unknown as HTMLElement);
  return v;
}

describe('day and night in a top-down view', () => {
  const skyAt = (rt: InstanceType<typeof WorldRuntime>, t: number) => {
    rt.step(t, 0.016);
    return rt.skyOf(77)!;
  };

  it('an index time darkens the view at midnight and lights it at noon; unknown time is full day', () => {
    const rt = new WorldRuntime(createFixtureSource({ mobs: 0 }), {
      inlineMesher: true,
      quality: 'software',
      textures: false,
    });
    topView(rt);
    expect(skyAt(rt, 1000).skySub).toBe(0); // nothing heard yet
    rt.cache.setWorldInfo(normalizeIndex({ dim: 0, columns: [], time: 18000 }), Date.now());
    expect(skyAt(rt, 1016).skySub).toBeGreaterThan(10.9); // midnight
    rt.cache.setWorldInfo(normalizeIndex({ dim: 0, columns: [], time: 6000 }), Date.now() + 1);
    expect(skyAt(rt, 1032).skySub).toBeLessThan(0.01); // noon
    // A full world time (days past) is the same time of day.
    rt.cache.setWorldInfo(normalizeIndex({ dim: 0, columns: [], time: 24000 * 5 + 18000 }), Date.now() + 2);
    expect(skyAt(rt, 1048).skySub).toBeGreaterThan(10.9);
    rt.dispose();
  });

  it('the fixture source path: its index replies carry the time the view lights by', async () => {
    const rt = new WorldRuntime(createFixtureSource({ mobs: 0, startTime: 18000, ticksPerSecond: 0 }), {
      inlineMesher: true,
      quality: 'software',
      textures: false,
    });
    topView(rt);
    rt.sync.setInterest({ dim: 0, x: 0, z: 0, r: 32 });
    await rt.sync.tick(Date.now());
    expect(rt.worldTimeAnchor(0)?.time).toBe(18000);
    expect(skyAt(rt, 1000).skySub).toBeGreaterThan(10.9);
    rt.dispose();
  });

  it('another feed of the server clock re-anchors it when newer (Story Mode), and a stopped cycle never advances', () => {
    const rt = new WorldRuntime(createFixtureSource({ mobs: 0 }), {
      inlineMesher: true,
      quality: 'software',
      textures: false,
    });
    topView(rt);
    const now = Date.now();
    rt.cache.setWorldInfo(normalizeIndex({ dim: 0, columns: [], time: 6000 }), now - 5000);
    rt.setWorldTime(0, 18000, now); // newer: midnight
    expect(skyAt(rt, 1000).skySub).toBeGreaterThan(10.9);
    rt.setWorldTime(0, 6000, now - 10_000); // older than the anchor: ignored
    expect(rt.worldTimeAnchor(0)?.time).toBe(18000);
    // 1.12.2, doDaylightCycle false: a negative time is the time, stopped.
    rt.cache.setWorldInfo(normalizeIndex({ dim: 0, columns: [], time: -13000 }), now + 1);
    expect(rt.worldTimeAnchor(0)).toMatchObject({ time: 13000, frozen: true });
    expect(rt.timeNow(0)).toBe(13000);
    rt.dispose();
  });
});

describe('the world time anchor', () => {
  it('advances at 20 ticks a second, for at most a minute', () => {
    const c = new WorldCache();
    expect(c.timeNow(0, 0)).toBeNull();
    c.setTime(0, 1000, 10_000);
    expect(c.timeNow(0, 10_000)).toBe(1000);
    expect(c.timeNow(0, 12_000)).toBe(1040);
    expect(c.timeNow(0, 10_000 + 120_000)).toBe(1000 + 1200); // capped: stale after a minute
    expect(c.timeNow(-1, 12_000)).toBeNull(); // per dimension
    c.setTime(0, 2000, 11_000, true);
    expect(c.timeNow(0, 50_000)).toBe(2000);
  });
});
