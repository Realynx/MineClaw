// Behaviour pin for the per-frame code (docs/dev/complexity.md, WP12): a seeded scene on a fake clock, stepped frame by frame
// through every camera mode and the inputs that branch the frame (keys, wheel, mouse look, follow, level cut, x-ray, marker,
// highlighted bots, a lightning event, the nether, viewer settings, a copy-mode view and a hive-card thumbnail). Every frame
// records what the runtime did: the renderer's calls with each camera's matrices, the world uniforms and the scene's
// visibility at each draw, the arguments handed to every layer, each view's ViewSetup and the status report. The digest
// hashes those records in memory (no files, so line endings never matter). GOLDEN_DUMP=<file> writes them out to diff.

import { writeFileSync } from 'node:fs';
import * as THREE from 'three';
import { afterAll, beforeAll, describe, expect, it, vi } from 'vitest';
import { Digest, rng } from './digest';

const hooks: { render?: (scene: THREE.Scene, camera: THREE.Camera) => void; call?: (s: string) => void } = {};

vi.mock('three', async (orig) => {
  const real = await orig<typeof THREE>();
  class FakeRenderer {
    readonly domElement = {
      style: {},
      width: 300,
      height: 150,
      tabIndex: 0,
      remove: () => undefined,
      addEventListener: () => undefined,
      removeEventListener: () => undefined,
    } as unknown as HTMLCanvasElement;
    readonly info = { render: { calls: 0, triangles: 0 } };
    readonly renderLists = { dispose: () => undefined };
    setPixelRatio() {}
    setScissorTest() {}
    setScissor(x: number, y: number, w: number, h: number) {
      hooks.call?.(`scissor ${x} ${y} ${w} ${h}`);
    }
    setViewport(x: number, y: number, w: number, h: number) {
      hooks.call?.(`viewport ${x} ${y} ${w} ${h}`);
    }
    setSize(w: number, h: number) {
      hooks.call?.(`size ${w} ${h}`);
      (this.domElement as { width: number }).width = w;
      (this.domElement as { height: number }).height = h;
    }
    getContext() {
      return {
        getExtension: () => null,
        getParameter: () => 'fake',
        RENDERER: 0,
        readPixels: () => hooks.call?.('readPixels'),
        RGBA: 0,
        UNSIGNED_BYTE: 0,
      };
    }
    render(scene: THREE.Scene, camera: THREE.Camera) {
      hooks.render?.(scene, camera);
    }
    dispose() {}
    forceContextLoss() {}
  }
  return { ...real, WebGLRenderer: FakeRenderer };
});

const { WorldRuntime } = await import('../src/render/runtime');
const { WorldViewerEngine } = await import('../src/render/engine');
const { ThumbnailView } = await import('../src/render/thumbnails');
const { SectionManager } = await import('../src/render/sections');
const { resetViewerQuality, setViewerQuality, viewerStatus } = await import('../src/render/viewerQuality');
const { createFixtureSource } = await import('../src/testing');

const EPOCH = 1_700_000_000_000;
let clock = 1000;

// ------------------------------------------------------------------------------------------------------------ records

/** A stable, compact rendering of a value: vectors, colours, matrices and cameras by their numbers; key order kept. */
function ser(v: unknown, depth = 0): unknown {
  if (v === undefined) return '~';
  if (v === null || typeof v === 'boolean' || typeof v === 'string') return v;
  if (typeof v === 'number') return Object.is(v, -0) ? '-0' : Number.isNaN(v) ? 'NaN' : v === Infinity ? 'Inf' : v === -Infinity ? '-Inf' : v;
  if (typeof v === 'function') return 'fn';
  if (depth > 5) return '…';
  const o = v as Record<string, unknown> & { isCamera?: boolean };
  if (o.isCamera) return camera(o as unknown as THREE.PerspectiveCamera);
  if (o instanceof THREE.Frustum) return o.planes.map((p) => [...p.normal.toArray(), p.constant]);
  if (o instanceof THREE.Vector2 || o instanceof THREE.Vector3 || o instanceof THREE.Vector4 || o instanceof THREE.Quaternion || o instanceof THREE.Matrix4 || o instanceof THREE.Color)
    return (o as unknown as { toArray(): number[] }).toArray().map((x) => ser(x));
  if (o instanceof THREE.Texture) return 'tex';
  if (o instanceof THREE.Object3D) return `obj:${o.name}:${o.visible}`;
  if (ArrayBuffer.isView(o)) return `bytes:${(o as unknown as ArrayLike<number>).length}`;
  if (o instanceof Map) return [...o].map(([k, x]) => [ser(k, depth + 1), ser(x, depth + 1)]);
  if (o instanceof Set) return [...o].map((x) => ser(x, depth + 1));
  if (Array.isArray(o)) return o.map((x) => ser(x, depth + 1));
  const out: Record<string, unknown> = {};
  for (const k of Object.keys(o)) out[k] = ser(o[k], depth + 1);
  return out;
}

function camera(c: THREE.PerspectiveCamera): unknown {
  return {
    pos: ser(c.position),
    quat: ser(c.quaternion),
    fov: c.fov,
    aspect: c.aspect,
    near: c.near,
    far: c.far,
    proj: ser(c.projectionMatrix),
    world: ser(c.matrixWorld),
  };
}

const log: string[] = [];
const rec = (label: string, v: unknown) => log.push(`${label} ${JSON.stringify(ser(v))}`);

/** Records every call of `obj[name]` (arguments as they were at the call, and optionally the result). */
function tap<T extends object>(obj: T, name: keyof T & string, label: string, result = false, skip = 0): void {
  const orig = (obj[name] as unknown as (...a: unknown[]) => unknown).bind(obj);
  (obj as Record<string, unknown>)[name] = (...a: unknown[]) => {
    rec(`${label}(`, a.slice(skip));
    const r = orig(...a);
    if (result) rec(`${label}=`, r);
    return r;
  };
}

// ------------------------------------------------------------------------------------------------------------ stand-ins

function fakeCanvas(w: number, h: number, name: string) {
  const canvas = {
    width: w,
    height: h,
    tabIndex: 0,
    style: {},
    clientWidth: w,
    clientHeight: h,
    remove: () => undefined,
    focus: () => undefined,
    addEventListener: () => undefined,
    removeEventListener: () => undefined,
    getContext: () => ctx,
  };
  const ctx = { canvas, drawImage: (...a: unknown[]) => rec(`${name}.drawImage`, a.slice(1)) };
  return canvas as unknown as HTMLCanvasElement;
}

function container(w: number, h: number): HTMLElement {
  return { appendChild: () => undefined, clientWidth: w, clientHeight: h } as unknown as HTMLElement;
}

const random = rng(4242);
let copyCanvasNext = false;

beforeAll(() => {
  const g = globalThis as Record<string, unknown>;
  g.requestAnimationFrame = () => 0;
  g.cancelAnimationFrame = () => undefined;
  g.fetch = () => Promise.reject(new Error('no assets in this test'));
  g.ResizeObserver = class {
    observe() {}
    disconnect() {}
  };
  g.window = { addEventListener: () => undefined, removeEventListener: () => undefined };
  const doc = (g.document ??= {}) as Record<string, unknown>;
  // Labels and icons get no 2D context (as in the other runtime tests); the copy-mode engine's canvas does.
  doc.createElement = () =>
    copyCanvasNext ? ((copyCanvasNext = false), fakeCanvas(320, 200, 'copy')) : { width: 0, height: 0, getContext: () => null };
  doc.addEventListener ??= () => undefined;
  doc.removeEventListener ??= () => undefined;
  doc.visibilityState = 'visible';
  vi.spyOn(performance, 'now').mockImplementation(() => clock);
  vi.spyOn(Date, 'now').mockImplementation(() => EPOCH + clock);
  vi.spyOn(Math, 'random').mockImplementation(random);
});
afterAll(() => {
  resetViewerQuality();
  vi.restoreAllMocks();
});

const flush = async () => {
  for (let i = 0; i < 40; i++) await Promise.resolve();
};

function bots(t: number) {
  return [
    { name: 'Alice', short: 'main', main: true, dim: 0 as const, x: 4 + t * 0.5, y: 70, z: 2 + t * 0.25, yaw: 30 * t, pitch: 10, hp: 20, food: 20 },
    { name: 'Bob', short: '67', main: false, dim: 0 as const, x: -6, y: 69, z: 8 - t * 0.5, yaw: 200, pitch: -5, hp: 12, food: 18 },
    { name: 'Nethy', short: '68', main: false, dim: -1 as const, x: 1, y: 40, z: 1, hp: 20, food: 20 },
  ];
}

async function run(): Promise<string> {
  log.length = 0;
  clock = 1000;
  const src = createFixtureSource({ mobs: 0, rain: 0.8, thunder: 0.4, now: () => EPOCH + clock });
  const rt = new WorldRuntime(src, { inlineMesher: true, quality: 'gpu', textures: false, pixelRatio: 1 });
  const engine = new WorldViewerEngine(container(800, 500), { source: src, runtime: rt, follow: 'Alice', pixelRatio: 1 });
  copyCanvasNext = true;
  const copy = new WorldViewerEngine(container(320, 200), { source: src, runtime: rt, follow: 'Bob', cameraMode: 'iso', pixelRatio: 1 });
  const thumb = new ThumbnailView(rt, fakeCanvas(240, 150, 'thumb'), 'Bob', { fps: 10 });
  rt.addView(thumb);
  // The network loops stop before they ever run (their timers would interleave with the column inflation, which is a real
  // stream); the world is fetched by hand below.
  (rt as unknown as { pause(): void }).pause();

  rt.store.apply({ rev: 1, full: true, bots: bots(0), entities: [
    { kind: 'mob', id: 1, dim: 0, x: 6, y: 70, z: 4, type: 54 },
    { kind: 'player', id: 2, dim: 0, x: 2, y: 70, z: -3, name: 'Steve' },
    { kind: 'item', id: 3, dim: 0, x: 5, y: 70, z: 5, item: 1, count: 1 },
  ] });
  // The world: one index + chunk pass around the views, meshed on the inline host.
  rt.step(clock, 0);
  // Pin the initial world to the primary region. Multi-view loading now yields after two batches; this golden concerns
  // rendering/cameras, not transport scheduling, so finish the original single-view download before recording frames.
  rt.sync.setInterest(engine.interest());
  const sync = rt.sync as unknown as { running: boolean; tick(n: number): Promise<void> };
  sync.running = true;
  await sync.tick(EPOCH + clock);
  sync.running = false;
  await flush();
  log.length = 0;

  // Taps: the arguments every layer receives each frame.
  hooks.call = (s) => log.push(s);
  hooks.render = (scene, cam) => {
    rec('render.camera', cam);
    rec('render.uniforms', rt.uniforms);
    rec('render.scene', { bg: scene.background, children: scene.children.map((c) => `${c.type}:${c.name}:${c.visible}`), cap: rt.materials.cap.visible });
  };
  tap(rt.entities, 'showFor', 'entities.showFor');
  tap(rt.entities, 'update', 'entities.update');
  tap(rt.bubbles, 'showFor', 'bubbles.showFor');
  tap(rt.sky, 'update', 'sky.update');
  tap(rt.weather, 'update', 'weather.update', true);
  tap(rt.aids, 'showAt', 'aids.showAt');
  tap(rt.flashes, 'showFor', 'flashes.showFor');
  tap(rt.overlays, 'setVisible', 'overlays.setVisible');
  tap(rt.blockEntityLayer, 'update', 'be.update', false, 1);
  tap(rt.blockEntityLayer, 'showFor', 'be.showFor');
  tap(rt.sync, 'setInterests', 'sync.setInterests');
  tap(engine, 'frame', 'engine.frame', true, 1);
  tap(copy, 'frame', 'copy.frame', true, 1);
  tap(thumb, 'frame', 'thumb.frame', true, 1);
  const proto = SectionManager.prototype as unknown as Record<string, (...a: unknown[]) => unknown>;
  const showAround = proto.showAround, maintain = proto.maintain;
  proto.showAround = function (this: unknown, ...a: unknown[]) {
    rec('sections.showAround', a);
    return showAround.apply(this, a);
  };
  proto.maintain = function (this: unknown, ...a: unknown[]) {
    rec('sections.maintain', a);
    return maintain.apply(this, a);
  };

  let n = 0;
  const keys = (engine as unknown as { keys: Set<string> }).keys;
  const frame = async (label: string, dt = 16) => {
    clock += dt;
    log.push(`---- frame ${n++} ${label}`);
    rt.step(clock, dt / 1000);
    rec('status', viewerStatus(clock));
    rec('sky', [rt.skyOf(engine.id), rt.skyOf(copy.id), rt.skyOf(thumb.id)]);
    rec('rig', engine.rig);
    rec('copyRig', copy.rig);
    await flush();
  };

  try {
    for (let i = 0; i < 4; i++) {
      rt.store.apply({ rev: 2 + i, full: true, bots: bots(i + 1), entities: [] });
      await frame(`follow ${i}`, 16 + i * 7);
    }
    engine.rig.look(40, -12);
    engine.rig.wheel(1);
    await frame('follow look');
    rt.store.apply({ rev: 10, full: false, bots: bots(5), entities: [], events: [{ kind: 'lightning', at: EPOCH + clock, x: 4, y: 70, z: 2 }] });
    await frame('lightning');
    await frame('lightning fades', 33);

    engine.setCameraMode('fly');
    keys.add('KeyW');
    keys.add('ShiftLeft');
    await frame('fly W fast', 20);
    keys.clear();
    keys.add('KeyA');
    keys.add('Space');
    engine.rig.look(-30, 25);
    await frame('fly A up', 700);
    keys.clear();
    keys.add('KeyS');
    keys.add('KeyD');
    keys.add('PageDown');
    engine.setXRay('on');
    await frame('fly S D down x-ray on', 300);
    keys.clear();
    keys.add('ArrowUp');
    keys.add('KeyQ');
    engine.setHighlighted(['Bob', 'Nobody']);
    engine.setMarker([3, 68, -2]);
    await frame('fly arrows marker');
    keys.clear();

    engine.setCameraMode('iso');
    engine.rig.rotateIso(1);
    keys.add('KeyD');
    keys.add('ArrowDown');
    await frame('iso glide', 200);
    keys.clear();
    keys.add('ArrowLeft');
    keys.add('KeyW');
    engine.rig.wheel(-1);
    await frame('iso pan', 500);
    keys.clear();
    engine.rig.pan(30, -20, 500);
    engine.setLevelCut({ y: 72, r: 24, dimBelow: 66 });
    await frame('iso level cut', 400);

    engine.setCameraMode('top');
    keys.add('KeyS');
    keys.add('ArrowRight');
    await frame('top glide', 300);
    keys.clear();
    engine.setLevelCut(null);
    engine.setXRay('off');
    engine.rig.wheel(-1);
    await frame('top x-ray off', 700);
    for (let i = 0; i < 6; i++) engine.rig.wheel(1);
    await frame('top zoomed out');

    engine.setCameraMode('follow');
    engine.setXRay('auto');
    engine.setMarker(null);
    engine.setHighlighted([]);
    engine.focus(10, 66, -4, { distance: 6, pitch: 40 });
    await frame('focus glide', 300);
    engine.setFollow('Alice');
    await frame('refollow', 900);

    setViewerQuality({ fog: false, weather: false, labels: false, bubbles: false, xray: false, holograms: false, entityDistance: 24 });
    engine.setLayers({ bots: true, entities: false, weather: false, staleness: true, changes: true, aids: true, cutaway: false, claims: true });
    engine.setBotLabels(false);
    await frame('viewer settings');
    resetViewerQuality();
    engine.setLayers({});

    engine.setDim(-1);
    engine.setFollow('Nethy');
    await frame('nether', 50);
    engine.setDim(0);
    engine.setFollow('Alice');
    rt.setWorldTime(0, 13000, EPOCH + clock, true);
    await frame('night frozen', 120);
    for (let i = 0; i < 3; i++) await frame(`settle ${i}`, 140);
  } finally {
    proto.showAround = showAround;
    proto.maintain = maintain;
    hooks.call = hooks.render = undefined;
    thumb.dispose?.();
    copy.dispose();
    engine.dispose();
  }
  const d = new Digest();
  for (const l of log) d.add(l);
  return d.hex();
}

describe('frame golden', () => {
  it('pins the runtime render, the engine frame and the camera rig over a scripted session', async () => {
    const a = await run();
    if (process.env.GOLDEN_DUMP) writeFileSync(process.env.GOLDEN_DUMP, log.join('\n'));
    const frames = log.filter((l) => l.startsWith('---- frame')).length;
    expect(frames).toBe(25);
    expect(log.filter((l) => l.startsWith('render.camera')).length).toBeGreaterThan(40);
    // Only the interactive-view priority metadata changes; rendering and camera output match the previous trace.
    expect(a).toBe('6e8cbb9a3730a4553df245508d947bac');
  }, 30000);
});
