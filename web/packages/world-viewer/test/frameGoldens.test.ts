// Behaviour pin for the per-frame code (docs/dev/complexity.md, WP12): a seeded scene on a fake clock, stepped frame by frame
// through every camera mode and the inputs that branch the frame (keys, wheel, mouse look, follow, level cut, x-ray, marker,
// highlighted bots, a lightning event, the nether, viewer settings, a copy-mode view and a hive-card thumbnail). Every frame
// records what the runtime did: the renderer's calls with each camera's matrices, the world uniforms and the scene's
// visibility at each draw, the arguments handed to every layer, each view's ViewSetup and the status report. The digest
// hashes those records in memory (no files, so line endings never matter). GOLDEN_DUMP=<file> writes them out to diff.

import { writeFileSync } from 'node:fs';
import * as THREE from 'three';
import { afterAll, beforeAll, describe, expect, it, vi } from 'vitest';
import type { RuntimeView } from '../src/render/runtime';
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

const { WorldRuntime, newViewId, MESH_TAIL_MS } = await import('../src/render/runtime');
const { WorldViewerEngine } = await import('../src/render/engine');
const { FrameTiming } = await import('../src/render/frameTiming');
const { ThumbnailView } = await import('../src/render/thumbnails');
const { SectionManager } = await import('../src/render/sections');
const { resetViewerQuality, setViewerQuality, viewerStatus } = await import('../src/render/viewerQuality');
const { DEFAULT_PREVIEW_FPS, setPreviewFps } = await import('../src/render/previewRate');
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
  setPreviewFps(10); // This rendering fixture pins a 10 FPS thumbnail, independent of the user's preview ceiling.
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
  setPreviewFps(DEFAULT_PREVIEW_FPS);
  resetViewerQuality();
  vi.restoreAllMocks();
});

const flush = async () => {
  for (let i = 0; i < 40; i++) await Promise.resolve();
};

/** Manual frame harness: stop network timers without hiding the views whose rendering this fixture exercises. */
function stopNetwork(rt: InstanceType<typeof WorldRuntime>): void {
  rt.sync.stop();
  rt.deltas.stop();
  const polls = rt as unknown as { entityPoller: { stop(): void }; overlayPoller: { stop(): void } };
  polls.entityPoller.stop();
  polls.overlayPoller.stop();
}

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
  stopNetwork(rt);

  rt.store.apply({ rev: 1, full: true, bots: bots(0), entities: [
    { kind: 'mob', id: 1, dim: 0, x: 6, y: 70, z: 4, type: 54 },
    { kind: 'player', id: 2, dim: 0, x: 2, y: 70, z: -3, name: 'Steve' },
    { kind: 'item', id: 3, dim: 0, x: 5, y: 70, z: 5, item: 1, count: 1 },
  ] });
  // The world: one index + chunk pass around the views, meshed on the inline host.
  rt.step(clock, 0);
  // Pin the initial world to the primary region. Multi-view loading now yields after two batches; this golden concerns
  // rendering/cameras, not transport scheduling, so finish the original single-view download before recording frames.
  rt.sync.setInterest(engine.interest()!);
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
    // Compared with a83da944, exactly four blocking readPixels calls disappeared. Every remaining trace line is identical.
    // This fixture is a legacy source without sender timestamps; timestamped movement is covered by motion.test.ts.
    expect(log).not.toContain('readPixels');
    expect(a).toBe('1d5aef9f00097ed876e9a2e3ee54e863');
  }, 30000);

  it('pans before the world frame and projects the overlay after that same camera is rendered', () => {
    const src = createFixtureSource({ mobs: 0 });
    const rt = new WorldRuntime(src, { inlineMesher: true, quality: 'gpu', textures: false, pixelRatio: 1 });
    const events: string[] = [];
    let rendered: number[] | undefined;
    const engine = new WorldViewerEngine(container(800, 500), {
      source: src, runtime: rt, cameraMode: 'top', pixelRatio: 1,
      onBeforeFrame: (view) => { events.push('input'); view.rig.target.x = 40; },
      onAfterFrame: (view) => {
        events.push('overlay');
        expect([...view.camera.matrixWorldInverse.elements, ...view.camera.projectionMatrix.elements]).toEqual(rendered);
        expect(rt.canvas.style.visibility).toBe('visible');
      },
    });
    stopNetwork(rt);
    hooks.render = (_scene, cam) => {
      if (cam !== engine.camera) return;
      events.push('world');
      rendered = [...engine.camera.matrixWorldInverse.elements, ...engine.camera.projectionMatrix.elements];
    };
    try {
      expect(rt.canvas.style.visibility).toBe('hidden');
      rt.step(clock + 100, 0.016);
      expect(events[0]).toBe('input');
      expect(events.at(-1)).toBe('overlay');
      expect(events).toContain('world');
      expect(engine.camera.position.x).toBeCloseTo(40);
    } finally { hooks.render = undefined; engine.dispose(); }
  });

  it('retains a suspended engine camera and holograms while releasing its active view and input', () => {
    const cancelPending = vi.fn();
    const src = { ...createFixtureSource({ mobs: 0 }), cancelPending };
    const rt = new WorldRuntime(src, { inlineMesher: true, textures: false });
    const engine = new WorldViewerEngine(container(800, 500), { source: src, runtime: rt, cameraMode: 'fly' });
    stopNetwork(rt);
    engine.rig.flyPos.set(233, 71, 407);
    engine.rig.flyYaw = 81;
    engine.setHolograms([{ x: 230, y: 64, z: 403, id: 4, meta: 0, state: 'todo', build: 'one' }], 'one');
    const holograms = rt.hologramsOf(engine.id);
    engine.suspend();
    expect(rt.viewCount).toBe(0);
    expect(cancelPending).toHaveBeenCalledOnce();
    expect(engine.frame(rt, clock, 0)).toBeNull();
    expect(rt.hologramsOf(engine.id)).toBe(holograms);
    engine.resume();
    stopNetwork(rt);
    expect(rt.viewCount).toBe(1);
    expect(rt.isPrimary(engine)).toBe(true);
    expect(engine.rig.flyPos.toArray()).toEqual([233, 71, 407]);
    expect(engine.rig.flyYaw).toBe(81);
    expect(rt.hologramsOf(engine.id)).toBe(holograms);
    engine.dispose();
    expect(rt.isDisposed).toBe(true);
  });

  it('hides the followed pawn only in the takeover view of a shared runtime', () => {
    const src = createFixtureSource({ mobs: 0 });
    const rt = new WorldRuntime(src, { inlineMesher: true, textures: false });
    const normal = new WorldViewerEngine(container(800, 500), { source: src, runtime: rt, follow: 'Alice' });
    copyCanvasNext = true;
    const player = new WorldViewerEngine(container(800, 500), { source: src, runtime: rt, follow: 'Alice', controls: 'player', cameraMode: 'fly' });
    stopNetwork(rt);
    rt.store.apply({ rev: 1, full: true, bots: bots(0), entities: [] });
    const show = vi.spyOn(rt.entities, 'showFor');
    try {
      expect(normal.frame(rt, clock, 0)).not.toHaveProperty('hiddenBot');
      expect(player.frame(rt, clock, 0)).toHaveProperty('hiddenBot', 'Alice');
      rt.step(clock, 0);
      const options = show.mock.calls.map((call) => call[2]);
      expect(options.map((o) => o.hiddenBot)).toEqual(['Alice', undefined]); // copies render before the primary surface
      expect(options[1]).not.toHaveProperty('hiddenBot');
    } finally { player.dispose(); normal.dispose(); }
  });
});

// ------------------------------------------------------------------------------------------------------------ first frame

/** One frame on the fake clock. */
async function step(rt: InstanceType<typeof WorldRuntime>, ms = 16): Promise<void> {
  clock += ms;
  rt.step(clock, ms / 1000);
  await flush();
}

/** One world pass by hand (the loops are stopped): index the next due interest and download what it lacks. */
async function syncOnce(rt: InstanceType<typeof WorldRuntime>): Promise<void> {
  const sync = rt.sync as unknown as { running: boolean; tick(n: number): Promise<void> };
  sync.running = true;
  await sync.tick(EPOCH + clock);
  sync.running = false;
  await flush();
}

const newRuntime = (src: ReturnType<typeof createFixtureSource>) =>
  new WorldRuntime(src, { inlineMesher: true, quality: 'gpu', textures: false, pixelRatio: 1 });

/**
 * What an earlier page leaves in the shared runtime: the 6 x 7 columns the fixture has around chunk (14, 0), 224 blocks
 * from the origin, all meshed for a top-down view that has since gone. Its cover is wider than the pictures of the
 * views the tests add, so those find every section they want already there.
 */
async function leftBehind() {
  const src = createFixtureSource({ radius: 16, mobs: 0, now: () => EPOCH + clock });
  const rt = newRuntime(src);
  const earlier: RuntimeView = {
    id: newViewId(),
    interest: () => ({ dim: 0, x: 224, z: 0, r: 48 }),
    centre: () => ({ dim: 0, x: 224, y: 64, z: 0, distance: 32, cover: 64 }),
    frame: () => null,
    overlayDim: () => null,
    selectedBots: () => [],
  };
  rt.addView(earlier);
  stopNetwork(rt);
  rt.sync.setInterest({ dim: 0, x: 224, z: 0, r: 48 });
  await syncOnce(rt);
  const sections = rt.sectionsFor(0);
  for (let i = 0; i < 100 && (i === 0 || sections.busy || sections.queued > 0); i++) await step(rt, 500);
  expect(sections.busy).toBe(false);
  expect(sections.queued).toBe(0);
  const count = sections.count;
  expect(count).toBe(6 * 7 * 8);
  const builds = new Map(sections.builds);
  rt.removeView(earlier);
  return { src, rt, sections, count, builds };
}

/** A WebGL2 context with the timer-query extension: every query is ready at once and reports `ms`. */
function timerGl(ms: number) {
  return {
    QUERY_RESULT_AVAILABLE: 1,
    QUERY_RESULT: 2,
    getExtension: vi.fn(() => ({ TIME_ELAPSED_EXT: 3, GPU_DISJOINT_EXT: 4 })),
    createQuery: vi.fn(() => ({})),
    beginQuery: vi.fn(),
    endQuery: vi.fn(),
    deleteQuery: vi.fn(),
    getQueryParameter: vi.fn((_query: unknown, p: number) => (p === 1 ? true : ms * 1e6)),
    getParameter: vi.fn(() => false),
  };
}

// A new engine's camera sits at the rig's defaults (the world origin) until its first frame poses it, and the app may
// hold frames (onBeforeFrame returning false) until it knows where the camera goes. Until then the view must not count:
// world synced, or sections kept, around the origin are the wrong map and evict what is held for the right one.
describe('a view asks for nothing until its camera is posed', () => {
  it('a held view draws nothing, stays hidden and asks for no world', async () => {
    const src = createFixtureSource({ mobs: 0 });
    const index = vi.spyOn(src, 'index');
    const rt = newRuntime(src);
    const before = vi.fn((): false => false);
    const engine = new WorldViewerEngine(container(800, 500), {
      source: src,
      runtime: rt,
      cameraMode: 'top',
      pixelRatio: 1,
      onBeforeFrame: before,
    });
    stopNetwork(rt);
    const cameras: THREE.Camera[] = [];
    hooks.render = (_scene, cam) => void cameras.push(cam);
    try {
      await step(rt);
      await step(rt);
      expect(before).toHaveBeenCalledTimes(2); // asked again every frame
      expect(engine.posed).toBe(false);
      expect(engine.interest()).toBeNull();
      expect(engine.centre()).toBeNull();
      expect(cameras.includes(engine.camera)).toBe(false);
      expect(rt.canvas.style.visibility).toBe('hidden');
      await syncOnce(rt);
      expect(index).not.toHaveBeenCalled();
    } finally {
      hooks.render = undefined;
      engine.dispose();
    }
  });

  it('the first frame that is not held poses the camera, shows the canvas and asks for the world there', async () => {
    const src = createFixtureSource({ mobs: 0 });
    const index = vi.spyOn(src, 'index');
    const rt = newRuntime(src);
    let hold = true;
    const engine = new WorldViewerEngine(container(800, 500), {
      source: src,
      runtime: rt,
      cameraMode: 'top',
      pixelRatio: 1,
      onBeforeFrame: (view) => {
        if (hold) return false;
        view.rig.target.set(400.5, 65, 400.5);
      },
    });
    stopNetwork(rt);
    const drawnAt: number[][] = [];
    hooks.render = (_scene, cam) => {
      if (cam === engine.camera) drawnAt.push(engine.camera.position.toArray());
    };
    try {
      await step(rt);
      hold = false;
      await step(rt);
      expect(drawnAt).toHaveLength(1);
      expect(drawnAt[0][0]).toBeCloseTo(400.5);
      expect(Math.abs(drawnAt[0][2] - 400.5)).toBeLessThan(1); // the top camera is tilted 0.1 degree off vertical
      expect(rt.canvas.style.visibility).toBe('visible');
      expect(engine.posed).toBe(true);
      expect(engine.interest()).toMatchObject({ dim: 0, x: 400.5, z: 400.5 });
      expect(engine.centre()).toMatchObject({ dim: 0, x: 400.5, y: 65, z: 400.5 });
      // Upkeep runs before the views' frames: the sync hears of the place in the next step, and first asks for it.
      await step(rt);
      await syncOnce(rt);
      expect(index).toHaveBeenCalledTimes(1);
      expect(index.mock.calls[0].slice(0, 3)).toEqual([0, 400, 400]);
    } finally {
      hooks.render = undefined;
      engine.dispose();
    }
  });

  it('stays posed through a later held frame and through a page that hides and shows it again', async () => {
    const src = createFixtureSource({ mobs: 0 });
    const rt = newRuntime(src);
    let hold = false;
    const engine = new WorldViewerEngine(container(800, 500), {
      source: src,
      runtime: rt,
      cameraMode: 'top',
      pixelRatio: 1,
      onBeforeFrame: (view) => {
        if (hold) return false;
        view.rig.target.set(400.5, 65, 400.5);
      },
    });
    stopNetwork(rt);
    let drawn = 0;
    hooks.render = (_scene, cam) => {
      if (cam === engine.camera) drawn++;
    };
    try {
      await step(rt);
      expect(drawn).toBe(1);
      hold = true;
      await step(rt);
      expect(drawn).toBe(1);
      expect(engine.posed).toBe(true);
      expect(engine.interest()).toMatchObject({ x: 400.5, z: 400.5 });
      engine.suspend();
      engine.resume();
      stopNetwork(rt);
      expect(engine.posed).toBe(true);
      expect(engine.centre()).toMatchObject({ x: 400.5, y: 65, z: 400.5 });
    } finally {
      hooks.render = undefined;
      engine.dispose();
    }
  });

  it('neither a held view nor its first frames at its place evict the sections the runtime holds there', async () => {
    const { src, rt, sections, count, builds } = await leftBehind();
    let spot: [number, number, number] | null = null;
    const engine = new WorldViewerEngine(container(800, 500), {
      source: src,
      runtime: rt,
      cameraMode: 'top',
      drawDistance: 32,
      pixelRatio: 1,
      onBeforeFrame: (view) => {
        if (!spot) return false;
        view.rig.target.set(...spot);
      },
    });
    stopNetwork(rt);
    try {
      for (let i = 0; i < 3; i++) await step(rt, 500);
      expect(sections.count).toBe(count);
      spot = [224.5, 65, 0.5];
      for (let i = 0; i < 2; i++) await step(rt, 500);
      expect(engine.centre()).toMatchObject({ x: 224.5, z: 0.5 });
      expect(sections.count).toBe(count);
      expect(sections.queued).toBe(0);
      expect(new Map(sections.builds)).toEqual(builds); // nothing was meshed again
    } finally {
      engine.dispose();
    }
  });

  it('a follow view that finds its bot in its first frame keeps the sections held around that bot', async () => {
    const { src, rt, sections, count, builds } = await leftBehind();
    const pawn = { ...bots(0)[0], name: 'Pawn', short: 'p1', main: false, x: 224, y: 70, z: 0 };
    rt.store.apply({ rev: 1, full: true, bots: [pawn], entities: [] });
    const engine = new WorldViewerEngine(container(800, 500), {
      source: src,
      runtime: rt,
      follow: 'Pawn',
      drawDistance: 32,
      pixelRatio: 1,
    });
    stopNetwork(rt);
    try {
      await step(rt, 500);
      expect(sections.count).toBe(count);
      expect(engine.centre()).toMatchObject({ x: 224, z: 0 });
      await step(rt, 500);
      expect(sections.count).toBe(count);
      expect(new Map(sections.builds)).toEqual(builds);
    } finally {
      engine.dispose();
    }
  });

  it('a free camera the app places in its first frame (the takeover view) keeps them too', async () => {
    const { src, rt, sections, count, builds } = await leftBehind();
    const engine = new WorldViewerEngine(container(800, 500), {
      source: src,
      runtime: rt,
      cameraMode: 'fly',
      drawDistance: 32,
      pixelRatio: 1,
      onBeforeFrame: (view) => void view.rig.flyPos.set(224.5, 71.5, 0.5),
    });
    stopNetwork(rt);
    try {
      await step(rt, 500);
      expect(sections.count).toBe(count);
      expect(engine.centre()).toMatchObject({ x: 224.5, y: 71.5, z: 0.5 });
      await step(rt, 500);
      expect(sections.count).toBe(count);
      expect(new Map(sections.builds)).toEqual(builds);
    } finally {
      engine.dispose();
    }
  });

  it('does not judge the frame rate by a frame its primary view held (GPU timer queries)', async () => {
    const src = createFixtureSource({ mobs: 0 });
    const rt = newRuntime(src);
    const gl = timerGl(25);
    const timing = new FrameTiming(gl as unknown as WebGL2RenderingContext);
    (rt as unknown as { frameTiming: InstanceType<typeof FrameTiming> }).frameTiming = timing;
    let hold = true;
    const engine = new WorldViewerEngine(container(800, 500), {
      source: src,
      runtime: rt,
      cameraMode: 'top',
      pixelRatio: 1,
      onBeforeFrame: () => (hold ? false : undefined),
    });
    stopNetwork(rt);
    try {
      await step(rt);
      await step(rt);
      expect(gl.createQuery).toHaveBeenCalled(); // a held frame was timed …
      expect(gl.deleteQuery).toHaveBeenCalledTimes(gl.createQuery.mock.calls.length); // … its measurement dropped,
      expect(gl.getQueryParameter).not.toHaveBeenCalled(); // never read,
      expect(rt.quality.state.frameMs).toBe(0); // so the levers saw nothing
      hold = false;
      await step(rt);
      await step(rt); // the GPU's answer for a drawn frame is read on the next one
      expect(rt.quality.state.frameMs).toBe(25);
    } finally {
      engine.dispose();
    }
  });
});

// ------------------------------------------------------------------------------------------------------------ readouts

// What a page polls between frames: the overlay's motion line and the map's "updating" note. Neither goes through the
// viewer status the frame golden records.
describe('readouts a page polls', () => {
  it('the longest gap between two drawn frames over the last ten seconds', async () => {
    const src = createFixtureSource({ mobs: 0 });
    const rt = newRuntime(src);
    const engine = new WorldViewerEngine(container(800, 500), {
      source: src,
      runtime: rt,
      cameraMode: 'top',
      pixelRatio: 1,
      onBeforeFrame: (view) => void view.rig.target.set(0.5, 65, 0.5),
    });
    stopNetwork(rt);
    try {
      await step(rt);
      expect(rt.motionStatus(clock).frame).toBe(0); // one frame is no gap
      await step(rt);
      expect(rt.motionStatus(clock).frame).toBe(16);
      await step(rt, 180); // a long task held the page
      await step(rt);
      expect(rt.motionStatus(clock)).toMatchObject({ frame: 180, rate: 0, pauses: 0 });
      expect(rt.motionStatus(clock + 11_000).frame).toBe(0);
      expect(viewerStatus(clock)).not.toHaveProperty('frame');
    } finally {
      engine.dispose();
    }
  });

  it('a held frame or a new primary view is not a gap between drawn frames', async () => {
    const src = createFixtureSource({ mobs: 0 });
    const rt = newRuntime(src);
    let hold = false;
    const options = {
      source: src,
      runtime: rt,
      cameraMode: 'top' as const,
      pixelRatio: 1,
      onBeforeFrame: (view: { rig: { target: THREE.Vector3 } }) =>
        hold ? false : void view.rig.target.set(0.5, 65, 0.5),
    };
    const first = new WorldViewerEngine(container(800, 500), options);
    stopNetwork(rt);
    rt.retain(); // the runtime outlives the first page, as the shared one does
    let second: InstanceType<typeof WorldViewerEngine> | null = null;
    try {
      await step(rt);
      await step(rt);
      expect(rt.motionStatus(clock).frame).toBe(16);
      hold = true; // waiting for the colony's place
      for (let i = 0; i < 4; i++) await step(rt, 500);
      hold = false;
      await step(rt);
      expect(rt.motionStatus(clock).frame).toBe(16);
      // The page changes in one commit: the old view goes and the new one comes, with no frame between.
      first.dispose();
      second = new WorldViewerEngine(container(800, 500), options);
      stopNetwork(rt);
      await step(rt, 2000);
      await step(rt);
      expect(rt.motionStatus(clock).frame).toBe(16);
    } finally {
      if (!second) first.dispose();
      second?.dispose();
      rt.release();
    }
  });

  it('every reader of a frame sees a body at the frame’s instant, not at the time it reads', async () => {
    const src = createFixtureSource({ mobs: 0 });
    const rt = newRuntime(src);
    const engine = new WorldViewerEngine(container(800, 500), {
      source: src,
      runtime: rt,
      cameraMode: 'top',
      pixelRatio: 1,
      onBeforeFrame: (view) => void view.rig.target.set(0.5, 65, 0.5),
    });
    stopNetwork(rt);
    const begin = vi.spyOn(rt.store, 'beginFrame');
    const update = vi.spyOn(rt.entities, 'update');
    try {
      rt.store.apply({ rev: 1, full: true, bots: bots(0), entities: [] });
      clock += 200;
      rt.store.apply({ rev: 2, full: true, bots: bots(1), entities: [] }); // Alice glides from x 4 to 4.5
      await step(rt);
      expect(begin).toHaveBeenCalledTimes(1);
      expect(begin.mock.invocationCallOrder[0]).toBeLessThan(update.mock.invocationCallOrder[0]);
      const drawn = rt.store.bot('Alice')!.x;
      expect(drawn).toBeGreaterThan(4);
      expect(drawn).toBeLessThan(4.5);
      clock += 50; // later in the same frame (outlines, overlay marks): the body has not moved
      expect(rt.store.bot('Alice')!.x).toBe(drawn);
      expect(rt.store.bots().find((b) => b.name === 'Alice')!.x).toBe(drawn);
      await step(rt); // the next frame moves on
      expect(begin).toHaveBeenCalledTimes(2);
      expect(rt.store.bot('Alice')!.x).toBeGreaterThan(drawn);
    } finally {
      engine.dispose();
    }
  });

  it('the map is updating until the view’s sweep is over and its sections are meshed', async () => {
    const src = createFixtureSource({ radius: 2, mobs: 0, now: () => EPOCH + clock });
    const rt = newRuntime(src);
    const engine = new WorldViewerEngine(container(800, 500), {
      source: src,
      runtime: rt,
      cameraMode: 'top',
      drawDistance: 32,
      pixelRatio: 1,
      onBeforeFrame: (view) => void view.rig.target.set(8.5, 65, 8.5),
    });
    stopNetwork(rt);
    try {
      await step(rt);
      await step(rt); // the sync hears of the view's place
      expect(rt.syncStatus()).toMatchObject({ swept: false, updating: true });
      await syncOnce(rt);
      expect(rt.syncStatus().swept).toBe(true);
      expect(rt.syncStatus()).toMatchObject({ updating: true }); // sections to mesh
      const sections = rt.sectionsFor(0);
      for (let i = 0; i < 100 && (sections.busy || sections.queued > 0); i++) await step(rt, 500);
      expect(rt.syncStatus()).toEqual({ swept: true, sectionsQueued: 0, updating: false });
      rt.sync.reindexNow(); // the tab came back
      expect(rt.syncStatus()).toMatchObject({ swept: false, updating: true });
      await syncOnce(rt);
      expect(rt.syncStatus().updating).toBe(false); // nothing had changed
      // A block changed during live play: a re-mesh, not catching up.
      sections.markDirty([[0, 4, 0]]);
      expect(rt.syncStatus()).toEqual({ swept: true, sectionsQueued: 1, updating: false });
      // A catch-up whose meshing live changes keep from ever running dry still ends.
      rt.sync.reindexNow();
      expect(rt.syncStatus().updating).toBe(true);
      await syncOnce(rt);
      sections.hold = true;
      expect(rt.syncStatus()).toMatchObject({ swept: true, sectionsQueued: 1, updating: true });
      expect(rt.syncStatus(clock + MESH_TAIL_MS - 1).updating).toBe(true);
      expect(rt.syncStatus(clock + MESH_TAIL_MS).updating).toBe(false);
    } finally {
      engine.dispose();
    }
  });

  it('sections left dirty out of every view’s range do not keep the map updating', async () => {
    const src = createFixtureSource({ radius: 2, mobs: 0, now: () => EPOCH + clock });
    const rt = newRuntime(src);
    const at = { x: 8.5, z: 8.5 };
    const engine = new WorldViewerEngine(container(800, 500), {
      source: src,
      runtime: rt,
      cameraMode: 'top',
      drawDistance: 32,
      pixelRatio: 1,
      onBeforeFrame: (view) => void view.rig.target.set(at.x, 65, at.z),
    });
    stopNetwork(rt);
    try {
      await step(rt);
      await step(rt);
      expect(rt.syncStatus().updating).toBe(true);
      await syncOnce(rt); // the columns are in, not meshed yet
      const sections = rt.sectionsFor(0);
      expect(sections.queued).toBeGreaterThan(0);
      sections.hold = true; // meshing (a software renderer) is slower than the pan
      at.x = at.z = 4000; // the view moves on before they are meshed
      await step(rt);
      await step(rt, 500);
      sections.hold = false;
      await step(rt, 500);
      expect(sections.queued).toBeGreaterThan(0); // still dirty, never scheduled
      expect(rt.syncStatus()).toEqual({ swept: true, sectionsQueued: 0, updating: false });
    } finally {
      engine.dispose();
    }
  });
});
