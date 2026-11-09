import { afterEach, describe, expect, it, vi } from 'vitest';
import { bindControllerInput, bindControllerKeyboard, canCaptureController, requestControllerCapture, requestControllerFullscreen, wheelSelection } from '@/features/story/controllerInput';
import { toggleControllerFly } from '@/features/story/mode/camera';
import type { Story } from '@/features/story/mode/commands';

const cleanups: Array<() => void> = [];
afterEach(() => { cleanups.splice(0).forEach((f) => f()); vi.restoreAllMocks(); Reflect.deleteProperty(navigator, 'keyboard'); Reflect.deleteProperty(document, 'fullscreenElement'); document.body.innerHTML = ''; });
function setup() {
  let active = true;
  const choose = vi.fn(), pause = vi.fn(), wheel = vi.fn(), release = vi.fn(() => { active = false; });
  cleanups.push(bindControllerInput({ active: () => active, editable: () => true, editing: () => true, choose, pause, wheel, release }));
  const key = (type: string, code: string, target: EventTarget = window, repeat = false) => {
    const e = new KeyboardEvent(type, { code, repeat, bubbles: true, cancelable: true }); target.dispatchEvent(e); return e;
  };
  const move = (x: number, y: number) => {
    const e = new Event('pointermove', { bubbles: true, cancelable: true });
    Object.assign(e, { movementX: x, movementY: y }); window.dispatchEvent(e); return e;
  };
  return { choose, pause, wheel, release, key, move, deactivate: () => { active = false; } };
}

describe('Controller tool wheel', () => {
  it('holds Q without acting, uses relative mouse movement, and selects only on release', () => {
    const s = setup();
    expect(s.key('keydown', 'KeyQ').defaultPrevented).toBe(true);
    s.key('keydown', 'KeyQ', window, true);
    expect(s.pause).toHaveBeenCalledTimes(1);
    expect(s.move(90, 0).defaultPrevented).toBe(true);
    expect(s.choose).not.toHaveBeenCalled();
    expect(s.wheel).toHaveBeenLastCalledWith({ x: 90, y: 0, selected: 'edit' });
    expect(s.key('keyup', 'KeyQ').defaultPrevented).toBe(true);
    expect(s.choose).toHaveBeenCalledExactlyOnceWith('edit');
    expect(s.pause).toHaveBeenLastCalledWith(false);
  });
  it.each(['Escape', 'blur', 'pointerlockchange'])('cancels on %s without accidental selection', (kind) => {
    const s = setup(); s.key('keydown', 'KeyQ'); s.move(0, 90);
    if (kind === 'Escape') s.key('keydown', kind);
    else if (kind === 'blur') window.dispatchEvent(new Event(kind));
    else { s.deactivate(); document.dispatchEvent(new Event(kind)); }
    s.key('keyup', 'KeyQ');
    expect(s.choose).not.toHaveBeenCalled();
    expect(s.pause).toHaveBeenLastCalledWith(false);
  });
  it('blocks clicks while choosing and leaves ordinary GUI input alone', () => {
    const s = setup(); s.key('keydown', 'KeyQ');
    const click = new Event('pointerdown', { cancelable: true }); window.dispatchEvent(click);
    expect(click.defaultPrevented).toBe(true);
    s.key('keyup', 'KeyQ');
    expect(s.choose).not.toHaveBeenCalled();
    const input = document.createElement('input'); document.body.append(input); input.focus();
    expect(s.key('keydown', 'KeyE', input).defaultPrevented).toBe(false);
    expect(s.choose).not.toHaveBeenCalled();
    input.blur(); s.deactivate();
    expect(s.key('keydown', 'KeyE').defaultPrevented).toBe(false);
  });
  it('E opens All Blocks while immersive and does not also move or repeat-open', () => {
    const s = setup();
    expect(s.key('keydown', 'KeyE').defaultPrevented).toBe(true);
    s.key('keydown', 'KeyE', window, true);
    expect(s.choose).toHaveBeenCalledExactlyOnceWith('blocks');
  });
  it('maps sectors and reserves a centre cancellation zone', () => {
    expect(([[0, -90], [90, 0], [0, 90], [-90, 0], [10, 10]] as const).map(([x, y]) => wheelSelection(x, y)))
      .toEqual(['inspect', 'edit', 'blocks', 'rotate', null]);
  });
  it('leaves Meta and Alt tool-letter shortcuts to the browser', () => {
    const s = setup();
    for (const code of ['KeyQ', 'KeyE', 'KeyR']) for (const modifier of ['metaKey', 'altKey']) {
      const event = new KeyboardEvent('keydown', { code, [modifier]: true, bubbles: true, cancelable: true });
      window.dispatchEvent(event); expect(event.defaultPrevented).toBe(false);
    }
    expect(s.choose).not.toHaveBeenCalled(); expect(s.pause).not.toHaveBeenCalled();
  });
  it('does not automatically capture over GUI editing or dialogs', () => {
    expect(canCaptureController()).toBe(true);
    const input = document.createElement('input'); document.body.append(input); input.focus();
    expect(canCaptureController()).toBe(false); input.remove();
    const dialog = document.createElement('div'); dialog.setAttribute('role', 'dialog'); document.body.append(dialog);
    expect(canCaptureController()).toBe(false); dialog.remove();
    expect(canCaptureController()).toBe(true);
    const menu = document.createElement('div'); menu.dataset.controllerMenu = 'sound';
    const toggle = document.createElement('button'); toggle.setAttribute('aria-expanded', 'true'); menu.append(toggle); document.body.append(menu);
    expect(canCaptureController()).toBe(false);
    toggle.setAttribute('aria-expanded', 'false'); expect(canCaptureController()).toBe(true);
  });
});

describe('Controller reserved shortcut capture', () => {
  it('starts fullscreen in the Fly gesture and waits for it before capturing the mouse', async () => {
    let finish!: () => void;
    const host = document.createElement('div'), surface = document.createElement('canvas');
    host.append(surface); document.body.append(host);
    const fullscreen = vi.fn(() => new Promise<void>((resolve) => { finish = resolve; }));
    Object.defineProperty(host, 'requestFullscreen', { value: fullscreen });
    const lock = vi.fn(), setView = vi.fn();
    Object.defineProperty(navigator, 'keyboard', { configurable: true, value: { lock: vi.fn() } });
    toggleControllerFly({ view: 'top', fullscreen: { el: host }, setView } as unknown as Story);
    expect(fullscreen).toHaveBeenCalledExactlyOnceWith({ navigationUI: 'hide' });
    expect(setView).toHaveBeenCalledOnce();
    const capture = requestControllerCapture({ surface, lock }, host);
    expect(lock).not.toHaveBeenCalled();
    expect(fullscreen).toHaveBeenCalledOnce();
    finish();
    expect(await capture).toBeNull();
    expect(lock).toHaveBeenCalledOnce();
  });

  it('leaves GUI editing in place and reports blocked fullscreen without unsafe mouse capture', async () => {
    const host = document.createElement('div'), surface = document.createElement('canvas'), input = document.createElement('input');
    host.append(surface, input); document.body.append(host); input.focus();
    const fullscreen = vi.fn(() => Promise.reject(new Error('denied'))), lock = vi.fn(), setView = vi.fn();
    Object.defineProperty(host, 'requestFullscreen', { value: fullscreen });
    toggleControllerFly({ view: 'top', fullscreen: { el: host }, setView } as unknown as Story);
    expect(fullscreen).not.toHaveBeenCalled();
    expect(setView).toHaveBeenCalledOnce();
    input.blur();
    expect(await requestControllerCapture({ surface, lock }, host)).toMatch(/Full screen was blocked/);
    expect(lock).not.toHaveBeenCalled();
  });

  it('does not recapture after leaving fly while fullscreen is pending', async () => {
    const host = document.createElement('div'), surface = document.createElement('canvas');
    host.append(surface); document.body.append(host);
    Object.defineProperty(document, 'fullscreenElement', { configurable: true, get: () => host });
    expect(await requestControllerFullscreen(host)).toBe(true);
    const lock = vi.fn();
    expect(await requestControllerCapture({ surface, lock }, host, () => false)).toBeNull();
    expect(lock).not.toHaveBeenCalled();
    expect(await requestControllerCapture({ surface, lock }, host)).toMatch(/Avoid Ctrl\+W/);
    expect(lock).toHaveBeenCalledOnce();
  });

  it('requests only movement keys while both immersive controls and user fullscreen are active', async () => {
    let fullscreen: Element | null = null, active = false;
    const keyboard = { lock: vi.fn(async () => {}), unlock: vi.fn() };
    Object.defineProperty(navigator, 'keyboard', { configurable: true, value: keyboard });
    Object.defineProperty(document, 'fullscreenElement', { configurable: true, get: () => fullscreen });
    cleanups.push(bindControllerKeyboard(() => active));
    active = true; document.dispatchEvent(new Event('pointerlockchange'));
    expect(keyboard.lock).not.toHaveBeenCalled();
    fullscreen = document.body; document.dispatchEvent(new Event('fullscreenchange')); await Promise.resolve();
    expect(keyboard.lock).toHaveBeenCalledExactlyOnceWith(['KeyW', 'KeyA', 'KeyS', 'KeyD']);
    active = false; document.dispatchEvent(new Event('pointerlockchange'));
    expect(keyboard.unlock).toHaveBeenCalledOnce();
    active = true; document.dispatchEvent(new Event('pointerlockchange'));
    window.dispatchEvent(new Event('blur'));
    expect(keyboard.unlock).toHaveBeenCalledTimes(2);
    Reflect.deleteProperty(navigator, 'keyboard');
  });

  it('releases late lock requests after immersive controls end and tolerates permission refusal', async () => {
    let finish!: () => void, active = true;
    const keyboard = { lock: vi.fn(() => new Promise<void>((r) => { finish = r; })), unlock: vi.fn() };
    Object.defineProperty(navigator, 'keyboard', { configurable: true, value: keyboard });
    Object.defineProperty(document, 'fullscreenElement', { configurable: true, get: () => document.body });
    cleanups.push(bindControllerKeyboard(() => active));
    active = false; document.dispatchEvent(new Event('pointerlockchange'));
    finish(); await Promise.resolve();
    expect(keyboard.unlock).toHaveBeenCalledTimes(2);
    keyboard.lock.mockRejectedValue(new Error('permission denied'));
    active = true; document.dispatchEvent(new Event('pointerlockchange')); await Promise.resolve();
    expect(keyboard.lock).toHaveBeenCalledTimes(2);
    Reflect.deleteProperty(navigator, 'keyboard');
  });

  it('shows a notice if the browser denies keyboard lock, then clears it on a successful retry', async () => {
    const notice = vi.fn(), keyboard = { lock: vi.fn().mockRejectedValueOnce(new Error('denied')).mockResolvedValue(undefined), unlock: vi.fn() };
    Object.defineProperty(navigator, 'keyboard', { configurable: true, value: keyboard });
    Object.defineProperty(document, 'fullscreenElement', { configurable: true, get: () => document.body });
    cleanups.push(bindControllerKeyboard(() => true, notice));
    await Promise.resolve();
    expect(notice).toHaveBeenLastCalledWith(expect.stringContaining('Avoid Ctrl+W'));
    window.dispatchEvent(new Event('focus')); await Promise.resolve();
    expect(notice).toHaveBeenLastCalledWith(null);
  });
});
