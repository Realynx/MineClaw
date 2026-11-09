import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { beginTakeoverCapture, takeoverCapture, type TakeoverCapture } from '@/features/control/captureHandoff';

const f = vi.hoisted(() => ({ fullscreen: vi.fn<(host: HTMLElement) => Promise<boolean>>() }));
vi.mock('@/features/story/controllerInput', () => ({ requestControllerFullscreen: f.fullscreen }));
const owned: TakeoverCapture[] = [];
const root = document.documentElement;
let locked: Element | null = null, fullscreen: Element | null = null;
const change = (element: Element | null) => { locked = element; document.dispatchEvent(new Event('pointerlockchange')); };
const flush = async () => { await Promise.resolve(); await Promise.resolve(); await Promise.resolve(); };
const start = (pawn = 'pawn') => {
  const navigate = vi.fn();
  beginTakeoverCapture(pawn, navigate);
  const capture = takeoverCapture(pawn)!;
  owned.push(capture);
  return { navigate, capture };
};

beforeEach(() => {
  vi.useFakeTimers(); locked = null; fullscreen = null;
  Object.defineProperty(document, 'pointerLockElement', { configurable: true, get: () => locked });
  Object.defineProperty(document, 'fullscreenElement', { configurable: true, get: () => fullscreen });
  Object.defineProperty(document, 'exitPointerLock', { configurable: true, value: vi.fn(() => change(null)) });
  Object.defineProperty(document, 'exitFullscreen', { configurable: true, value: vi.fn(() => { fullscreen = null; return Promise.resolve(); }) });
  Object.defineProperty(root, 'requestPointerLock', { configurable: true, value: vi.fn(() => { change(root); return Promise.resolve(); }) });
  f.fullscreen.mockImplementation((host) => { fullscreen = host; return Promise.resolve(true); });
});
afterEach(() => {
  owned.splice(0).forEach((capture) => capture.release());
  for (const key of ['pointerLockElement', 'fullscreenElement', 'exitPointerLock', 'exitFullscreen']) Reflect.deleteProperty(document, key);
  Reflect.deleteProperty(root, 'requestPointerLock');
  document.body.innerHTML = ''; vi.clearAllMocks(); vi.useRealTimers();
});

describe('takeover capture across navigation', () => {
  it('captures the persistent document in the click, then transfers to a ready canvas without another gesture', async () => {
    const { capture, navigate } = start();
    expect(f.fullscreen).toHaveBeenCalledExactlyOnceWith(root);
    expect(navigate).not.toHaveBeenCalled();
    await flush();
    expect(locked).toBe(root);
    expect(navigate).toHaveBeenCalledOnce();
    const canvas = document.createElement('canvas'); document.body.append(canvas);
    canvas.requestPointerLock = vi.fn(() => { change(canvas); return Promise.resolve(); });
    expect(takeoverCapture('different-pawn')).toBeNull();
    expect(await capture.transfer(canvas, () => true)).toBe(true);
    expect(locked).toBe(canvas);
    expect(takeoverCapture('pawn')).toBeNull();
    vi.advanceTimersByTime(20_000);
    expect(locked).toBe(canvas);
    capture.release();
    expect(locked).toBeNull(); expect(fullscreen).toBeNull();
  });

  it.each(['Escape', 'blur', 'unlock'])('does not recapture after %s during route loading', async (kind) => {
    const { capture } = start(); await flush();
    if (kind === 'Escape') window.dispatchEvent(new KeyboardEvent('keydown', { code: 'Escape' }));
    else if (kind === 'blur') window.dispatchEvent(new Event('blur'));
    else change(null);
    const canvas = document.createElement('canvas'); document.body.append(canvas);
    const request = vi.fn(() => Promise.resolve()); canvas.requestPointerLock = request;
    expect(await capture.transfer(canvas, () => true)).toBe(false);
    expect(request).not.toHaveBeenCalled();
    expect(locked).toBeNull(); expect(fullscreen).toBeNull();
  });

  it('navigates to the explicit capture fallback if the browser blocks fullscreen', async () => {
    f.fullscreen.mockResolvedValueOnce(false);
    const { navigate } = start(); await flush();
    expect(locked).toBeNull();
    expect(navigate).toHaveBeenCalledOnce();
    expect(takeoverCapture('pawn')).toBeNull();
  });

  it('cannot capture after cancellation while fullscreen is still pending', async () => {
    let finish!: (value: boolean) => void;
    f.fullscreen.mockImplementationOnce(() => new Promise((resolve) => { finish = resolve; }));
    const { capture, navigate } = start(); capture.release();
    fullscreen = root; finish(true); await flush();
    expect(locked).toBeNull();
    expect(fullscreen).toBeNull(); expect(navigate).not.toHaveBeenCalled();
  });

  it('releases an abandoned handoff and does not transfer to an ended session', async () => {
    const { capture } = start(); await flush();
    const canvas = document.createElement('canvas'); document.body.append(canvas);
    canvas.requestPointerLock = vi.fn(() => Promise.resolve());
    expect(await capture.transfer(canvas, () => false)).toBe(false);
    vi.advanceTimersByTime(15_000);
    expect(locked).toBeNull(); expect(fullscreen).toBeNull();
    expect(takeoverCapture('pawn')).toBeNull();
  });

  it('does not let an old view cleanup release a replacement capture', async () => {
    const first = start('first'); await flush();
    const second = start('second'); await flush();
    first.capture.release();
    expect(locked).toBe(root); expect(fullscreen).toBe(root);
    expect(takeoverCapture('second')).toBe(second.capture);
    const canvas = document.createElement('canvas'); document.body.append(canvas);
    canvas.requestPointerLock = vi.fn(() => { change(canvas); return Promise.resolve(); });
    expect(await second.capture.transfer(canvas, () => true)).toBe(true);
    first.capture.release();
    expect(locked).toBe(canvas);
  });

  it('retains capture through a suspense view replacement and releases on the final unmount', async () => {
    const { capture } = start(); await flush();
    const oldUnmount = capture.mount(); oldUnmount();
    vi.advanceTimersByTime(50);
    const newUnmount = capture.mount();
    vi.advanceTimersByTime(100);
    expect(locked).toBe(root);
    newUnmount(); vi.advanceTimersByTime(100);
    expect(locked).toBeNull(); expect(fullscreen).toBeNull();
  });
});
