// The shared runtime applies the viewer settings to the views it draws, live (no remount): a stand-in WebGL renderer, the
// fixture world and a scripted view, stepped by hand.
import * as THREE from 'three';
import { afterAll, afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import type { RuntimeView, ViewSetup } from '../src/render/runtime';

vi.mock('three', async (orig) => {
  const real = await orig<typeof THREE>();
  class FakeRenderer {
    readonly domElement = { style: {}, width: 300, height: 150, tabIndex: 0, remove: () => undefined } as unknown as HTMLCanvasElement;
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
      return { getExtension: () => null, getParameter: () => 'fake', RENDERER: 0, readPixels: () => undefined, RGBA: 0, UNSIGNED_BYTE: 0 };
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
const { resetViewerQuality, setViewerQuality, viewerStatus } = await import('../src/render/viewerQuality');
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
    layers: { bots: true, entities: true, weather: true, staleness: false, changes: false },
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

describe('WorldRuntime under the viewer settings', () => {
  it('cancels reads and skips all frame upkeep after its last view leaves, resuming on return', () => {
    const cancelPending = vi.fn();
    const rt = new WorldRuntime({ ...createFixtureSource({ mobs: 0 }), cancelPending }, { inlineMesher: true, textures: false });
    const view = topView(rt);
    const entityUpdates = vi.spyOn(rt.entities, 'update');
    const blockFetch = vi.spyOn(rt.blockEntities, 'tick');
    rt.removeView(view);
    expect(cancelPending).toHaveBeenCalledOnce();
    rt.step(1000, 0.016);
    expect(entityUpdates).not.toHaveBeenCalled();
    expect(blockFetch).not.toHaveBeenCalled();
    topView(rt);
    rt.step(1016, 0.016);
    expect(entityUpdates).toHaveBeenCalledOnce();
    expect(blockFetch).toHaveBeenCalledOnce();
    rt.dispose();
  });

  it('applies a change on its next frame: profile levers, AO, fog, entity reach, drops and the status report', () => {
    const rt = new WorldRuntime(createFixtureSource({ mobs: 0 }), { inlineMesher: true, quality: 'software', textures: false });
    const view = topView(rt);
    // Two mobs and a drop near the look-at point, one mob 90 blocks out (inside the 117-block picture).
    rt.store.apply({
      rev: 1,
      full: true,
      bots: [],
      entities: [
        { kind: 'mob', id: 1, dim: 0, x: 3, y: 64, z: 3, type: 54 },
        { kind: 'mob', id: 2, dim: 0, x: -5, y: 64, z: 2, type: 54 },
        { kind: 'item', id: 3, dim: 0, x: 1, y: 64, z: 1, item: 1, count: 1 },
        { kind: 'mob', id: 4, dim: 0, x: 90, y: 64, z: 0, type: 54 },
      ],
    });
    let t = 1000;
    const step = () => rt.step((t += 16), 0.016);
    step();
    // Same as ground (the default): the whole picture, across the ground from the look-at point.
    expect(rt.memory().entityRadius).toBe(117);
    expect(rt.entities.instances).toBe(4);
    expect(rt.uniforms.uAo.value).toBe(1);
    expect(rt.uniforms.uFogRange.value.x).toBeLessThan(1000);
    expect(rt.quality.state.profile.maxDraw).toBe(128);

    setViewerQuality({ entityDistance: 32, drops: false, ambientOcclusion: false, fog: false, drawDistance: 64, renderScale: 0.5, adaptive: false, entityModelDistance: 8 });
    step();
    expect(rt.memory().entityRadius).toBe(32);
    expect(rt.entities.instances).toBe(2); // the far mob and the drop are gone
    expect(rt.uniforms.uAo.value).toBe(0);
    expect(rt.uniforms.uFogRange.value.x).toBeGreaterThan(1e6);
    expect(rt.quality.state.profile).toMatchObject({ maxDraw: 64, pixelRatioCap: 0.5, entityDetailDistance: 8 });
    expect(rt.quality.enabled).toBe(false);
    expect(rt.entities.detailDistance).toBe(8);
    // Render scale: half a pixel per CSS pixel.
    expect(rt.primarySize({ clientWidth: 1000, clientHeight: 600 } as HTMLElement)).toEqual([500, 300]);

    const status = viewerStatus(t);
    expect(status).toMatchObject({ software: true, entityRadius: 32, entities: 2, profile: 'software' });
    expect(view.setup.cover).toBe(117);
    rt.dispose();
  });

  it('labels, bubbles and silhouettes follow their toggles', () => {
    const rt = new WorldRuntime(createFixtureSource({ mobs: 0 }), { inlineMesher: true, quality: 'gpu', textures: false });
    topView(rt);
    const showFor = vi.spyOn(rt.entities, 'showFor');
    const bubbles = vi.spyOn(rt.bubbles, 'showFor');
    rt.step(1000, 0.016);
    expect(showFor.mock.lastCall?.[2]).toMatchObject({ labels: true, drops: true });
    expect(bubbles.mock.lastCall?.[0].mode).toBe('full');
    setViewerQuality({ labels: false, bubbles: false, xray: false });
    rt.step(1016, 0.016);
    expect(showFor.mock.lastCall?.[2]).toMatchObject({ labels: false, ghosts: null });
    expect(bubbles.mock.lastCall?.[0].mode).toBe('off');
    rt.dispose();
  });
});
