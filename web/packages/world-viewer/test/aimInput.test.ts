import { afterEach, expect, it, vi } from 'vitest';
import { WorldViewerEngine } from '../src/render/engine';
import type { AimTarget } from '../src/render/aim';

afterEach(() => vi.unstubAllGlobals());

it('consumes pointer-lock acquisition and forwards both edit buttons only while the same freecam remains locked', async () => {
  const doc: { pointerLockElement: EventTarget | null; activeElement: EventTarget | null } = {
    pointerLockElement: null,
    activeElement: null,
  };
  vi.stubGlobal('document', doc);
  vi.stubGlobal('window', new EventTarget());
  const surface = Object.assign(new EventTarget(), {
    focus: () => {
      doc.activeElement = surface;
    },
    requestPointerLock: () => {
      doc.pointerLockElement = surface;
    },
    hasPointerCapture: () => false,
  });
  const callback = vi.fn();
  let resolve!: (target: AimTarget | null) => void;
  const aimNow = vi.fn(
    () =>
      new Promise<AimTarget | null>((r) => {
        resolve = r;
      }),
  );
  const engine = Object.assign(Object.create(WorldViewerEngine.prototype) as WorldViewerEngine, {
    surface,
    cleanups: [],
    keys: new Set<string>(),
    disposed: false,
    rig: { mode: 'fly' },
    opts: { onAimClick: callback },
    aimNow,
  });
  (engine as unknown as { bindInput(): void }).bindInput();
  const event = (type: string, button: number) =>
    surface.dispatchEvent(Object.assign(new Event(type), { button, clientX: 0, clientY: 0, pointerId: 1 }));
  event('pointerdown', 0);
  event('pointerup', 0);
  expect(aimNow).not.toHaveBeenCalled();
  event('pointerdown', 2);
  event('pointerup', 2);
  resolve(null);
  await Promise.resolve();
  expect(callback).toHaveBeenCalledExactlyOnceWith(null, { shift: undefined, ctrl: undefined, button: 2 });
  event('pointerdown', 0);
  event('pointerup', 0);
  doc.pointerLockElement = null;
  resolve(null);
  await Promise.resolve();
  expect(callback).toHaveBeenCalledOnce();
});
