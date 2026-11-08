import { afterEach, describe, expect, it, vi } from 'vitest';
import * as THREE from 'three';
import { WorldViewerEngine } from '../src/render/engine';
import { CameraRig } from '../src/render/cameraRig';
import type { AimRay, AimTarget } from '../src/render/aim';

afterEach(() => vi.unstubAllGlobals());

function setup(controls: 'default' | 'editor' | 'controller' = 'editor') {
  const doc = { pointerLockElement: null as EventTarget | null, activeElement: null as EventTarget | null, exitPointerLock: vi.fn() };
  const win = new EventTarget();
  vi.stubGlobal('document', doc);
  vi.stubGlobal('window', win);
  const captures = new Set<number>();
  const surface = Object.assign(new EventTarget(), {
    focus: () => { doc.activeElement = surface; },
    requestPointerLock: vi.fn(() => { doc.pointerLockElement = surface; }),
    setPointerCapture: (id: number) => captures.add(id),
    hasPointerCapture: (id: number) => captures.has(id),
    releasePointerCapture: (id: number) => captures.delete(id),
    getBoundingClientRect: () => ({ left: 10, top: 20, width: 800, height: 600 }),
    clientHeight: 600,
  });
  const rig = new CameraRig();
  rig.target.set(20, 12, 20);
  rig.orbitDist = 40;
  const camera = new THREE.PerspectiveCamera(70, 4 / 3, 0.05, 500);
  rig.apply(camera, 0, { keys: new Set() }, 0);
  const pickAt = vi.fn();
  const onAim = vi.fn();
  const resolveAim = vi.fn<(physical: AimTarget | null, ray: AimRay) => AimTarget | null>((t) => t);
  const pick = vi.fn().mockResolvedValue(null);
  const engine: WorldViewerEngine = Object.assign(Object.create(WorldViewerEngine.prototype) as WorldViewerEngine, {
    surface, camera, rig, pickAt,
    cleanups: [], keys: new Set<string>(), disposed: false,
    opts: { controls, onAim, resolveAim },
    runtime: { viewerQuality: { drawDistance: 128 }, host: { pick }, version: '1.12.2' },
    drawCap: null, layers: { bots: false }, dim: 0, levelCut: null,
    aimRequest: 0, aimed: null, pointerAt: null, cameraCollision: true,
  });
  const internal = engine as unknown as { bindInput(): void; collide(): Promise<void>; updateAim(now: number): void; keys: Set<string> };
  internal.bindInput();
  const event = (type: string, props: Record<string, unknown> = {}) => {
    const e = Object.assign(new Event(type, { cancelable: true }), { button: 0, clientX: 400, clientY: 300, pointerId: 1, shiftKey: false, altKey: false, ...props });
    surface.dispatchEvent(e);
    return e;
  };
  return { engine, internal, event, surface, captures, rig, camera, pickAt, pick, onAim, resolveAim, win };
}

describe('editor navigation', () => {
  it('leaves ordinary left-click and left-drag to selection without moving or locking the camera', () => {
    const { engine, event, surface, rig, pickAt } = setup();
    const start = [rig.orbitYaw, rig.orbitPitch, rig.orbitDist, ...rig.target.toArray()];
    event('pointerdown'); event('pointerup');
    expect(pickAt).toHaveBeenCalledOnce();
    event('pointerdown'); event('pointermove', { clientX: 470, clientY: 345 }); event('pointerup');
    engine.lock();
    expect(surface.requestPointerLock).not.toHaveBeenCalled();
    expect([rig.orbitYaw, rig.orbitPitch, rig.orbitDist, ...rig.target.toArray()]).toEqual(start);
    expect(pickAt).toHaveBeenCalledOnce();
  });

  it.each([{ button: 1 }, { button: 0, altKey: true }])('orbits with %o and consumes the gesture before tools', (gesture) => {
    const { event, surface, rig, captures, pickAt } = setup();
    const tools = vi.fn();
    surface.addEventListener('pointerdown', tools, { capture: true });
    surface.addEventListener('pointermove', tools, { capture: true });
    surface.addEventListener('pointerup', tools, { capture: true });
    const yaw = rig.orbitYaw;
    event('pointerdown', gesture);
    expect(captures.has(1)).toBe(true);
    event('pointermove', { ...gesture, clientX: 450, clientY: 320 });
    event('pointerup', gesture);
    expect(rig.orbitYaw).toBe(yaw + 15);
    expect(rig.orbitPitch).toBe(36);
    expect(captures.size).toBe(0);
    expect(tools).not.toHaveBeenCalled();
    expect(pickAt).not.toHaveBeenCalled();
  });

  it('pans in the screen plane with Shift and dollies with the wheel, without changing fly speed', () => {
    const { event, rig, camera } = setup();
    const start = rig.target.clone();
    const yaw = rig.orbitYaw;
    event('pointerdown', { button: 1, shiftKey: true });
    event('pointermove', { button: 1, shiftKey: true, clientX: 460, clientY: 330 });
    event('pointerup', { button: 1, shiftKey: true });
    const delta = rig.target.clone().sub(start);
    expect(delta.length()).toBeGreaterThan(0);
    expect(delta.dot(camera.getWorldDirection(new THREE.Vector3()))).toBeCloseTo(0);
    expect(rig.orbitYaw).toBe(yaw);
    event('wheel', { deltaY: -100 });
    expect(rig.orbitDist).toBeCloseTo(40 / 1.12);
    expect(rig.speed).toBe(12);
  });

  it('releases cancelled gestures and ignores fly movement keys or attempts to enable fly mode', async () => {
    const { event, rig, captures, win, engine, internal, pick } = setup();
    event('pointerdown', { button: 1 });
    event('pointercancel', { button: 1 });
    const yaw = rig.orbitYaw;
    event('pointermove', { clientX: 500 });
    expect(captures.size).toBe(0);
    expect(rig.orbitYaw).toBe(yaw);
    win.dispatchEvent(Object.assign(new Event('keydown'), { code: 'KeyW' }));
    expect(internal.keys.size).toBe(0);
    engine.setCameraMode('fly');
    expect(engine.getCameraMode()).toBe('follow');
    rig.collideDist = 2;
    await internal.collide();
    expect(rig.collideDist).toBe(Infinity);
    expect(pick).not.toHaveBeenCalled();
  });

  it('preserves click-to-lock for the normal freecam profile', () => {
    const { event, surface, rig, pickAt } = setup('default');
    rig.mode = 'fly';
    event('pointerdown'); event('pointerup');
    expect(surface.requestPointerLock).toHaveBeenCalledOnce();
    expect(pickAt).not.toHaveBeenCalled();
  });
});

describe('Controller immersive input', () => {
  it('requires this view to own pointer lock and suppresses Ctrl shortcuts only while flying', () => {
    const { engine, surface, win, rig, internal } = setup('controller');
    rig.mode = 'fly';
    surface.focus();
    const key = (code: string, ctrlKey = false) => {
      const e = Object.assign(new Event('keydown', { cancelable: true }), { code, ctrlKey });
      win.dispatchEvent(e); return e;
    };
    expect(key('KeyW', true).defaultPrevented).toBe(false);
    expect(internal.keys.size).toBe(0);
    engine.lock();
    expect(key('KeyW', true).defaultPrevented).toBe(true);
    expect(internal.keys.has('KeyW')).toBe(true);
    key('KeyQ'); key('KeyE');
    expect(internal.keys.has('KeyQ')).toBe(false);
    expect(internal.keys.has('KeyE')).toBe(false);
    engine.setCameraMode('top');
    expect(key('KeyS', true).defaultPrevented).toBe(false);
  });

  it('pauses movement, looking, and picking while the tool wheel owns input', () => {
    const { engine, win, event, rig, internal, pickAt } = setup('controller');
    rig.mode = 'fly'; engine.lock();
    win.dispatchEvent(Object.assign(new Event('keydown'), { code: 'KeyW' }));
    expect(internal.keys.has('KeyW')).toBe(true);
    engine.setInputPaused(true);
    const yaw = rig.flyYaw;
    event('pointerdown'); event('pointermove', { movementX: 100, movementY: 40 }); event('pointerup');
    win.dispatchEvent(Object.assign(new Event('keydown'), { code: 'Space' }));
    expect(rig.flyYaw).toBe(yaw);
    expect(internal.keys.size).toBe(0);
    expect(pickAt).not.toHaveBeenCalled();
    engine.setInputPaused(false);
    event('pointermove', { movementX: 100, movementY: 0 });
    expect(rig.flyYaw).toBe((yaw + 15) % 360);
  });

  it('uses Ctrl/Space vertically and reserves Q/E only in Controller', () => {
    const rig = new CameraRig(); rig.mode = 'fly';
    const y = rig.flyPos.y;
    rig.desired(1, { keys: new Set(['ControlLeft']), controller: true });
    expect(rig.flyPos.y).toBe(y - 12);
    rig.desired(1, { keys: new Set(['Space']), controller: true });
    expect(rig.flyPos.y).toBe(y);
    rig.desired(1, { keys: new Set(['KeyQ', 'KeyE']), controller: true });
    expect(rig.flyPos.y).toBe(y);
    rig.desired(1, { keys: new Set(['KeyQ']) });
    expect(rig.flyPos.y).toBe(y - 12);
    rig.desired(1, { keys: new Set(['KeyE']) });
    expect(rig.flyPos.y).toBe(y);
  });
});

describe('editor cursor picking and framing', () => {
  it('updates hover without pointer lock and discards an in-flight hover after the cursor leaves', async () => {
    const { engine, internal, event, onAim, pick, resolveAim } = setup();
    const target: AimTarget = { kind: 'block', x: 20, y: 12, z: 20, id: 1, meta: 0, face: 1, distance: 20, name: 'Stone' };
    resolveAim.mockReturnValue(target);
    event('pointermove');
    internal.updateAim(1000);
    await vi.waitFor(() => expect(onAim).toHaveBeenCalledWith(target));
    let resolve!: (hit: null) => void;
    pick.mockImplementationOnce(() => new Promise<null>((r) => { resolve = r; }));
    const pending = engine.aimAtPointer(200, 100);
    event('pointerleave');
    onAim.mockClear();
    resolve(null);
    await pending;
    expect(onAim).not.toHaveBeenCalled();
    expect(engine.aim()).toBeNull();
  });

  it('uses the cursor ray, even when a capture-phase tool consumes the click, and preserves application aim resolution', async () => {
    const { engine, event, surface, camera, resolveAim } = setup();
    surface.addEventListener('pointerdown', (e) => e.stopImmediatePropagation(), { capture: true });
    event('pointerdown', { clientX: 710, clientY: 120 });
    await engine.aimNow();
    const ray = resolveAim.mock.lastCall?.[1];
    expect(ray).toBeDefined();
    const expected = new THREE.Raycaster();
    expected.setFromCamera(new THREE.Vector2(0.75, 2 / 3), camera);
    expect(ray?.direction).toEqual(expected.ray.direction.toArray());
    expect(ray?.direction).not.toEqual(camera.getWorldDirection(new THREE.Vector3()).toArray());
    // A queued click may finish after the cursor moved. Picking its old position must not replace the live hover position.
    await engine.aimAtPointer(200, 100);
    await engine.aimNow();
    expect(resolveAim.mock.lastCall?.[1].direction).toEqual(ray?.direction);
    event('pointerleave');
    resolveAim.mockClear();
    expect(await engine.aimNow()).toBeNull();
    expect(resolveAim).not.toHaveBeenCalled();
  });

  it('focuses the requested point and framing distance in orbit mode', () => {
    const { engine, rig, camera } = setup();
    engine.focus(30, 16, 25, { distance: 28, pitch: 55 });
    rig.apply(camera, 0, { keys: new Set() }, performance.now() + 1000);
    expect(rig.target.toArray()).toEqual([30.5, 16.5, 25.5]);
    expect(camera.position.distanceTo(rig.target)).toBeCloseTo(28);
    expect(rig.orbitPitch).toBe(55);
    const direction = camera.getWorldDirection(new THREE.Vector3());
    expect(direction.dot(rig.target.clone().sub(camera.position).normalize())).toBeCloseTo(1);
  });

  it('keeps the ordinary follow camera framing unchanged when it focuses a point', () => {
    const { engine, rig } = setup('default');
    engine.focus(30, 16, 25, { distance: 28, pitch: 55 });
    expect(rig.target.toArray()).toEqual([30.5, 16.5, 25.5]);
    expect(rig.orbitDist).toBe(40);
    expect(rig.orbitPitch).toBe(30);
  });
});
