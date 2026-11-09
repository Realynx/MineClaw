import { isTyping } from '@/features/world/directives';

export const CONTROLLER_TOOLS = [
  { id: 'inspect', label: 'Inspect' },
  { id: 'edit', label: 'Place / remove blocks' },
  { id: 'blocks', label: 'All Blocks' },
  { id: 'rotate', label: 'Rotate block' },
] as const;
export type ControllerTool = typeof CONTROLLER_TOOLS[number]['id'];
export type ToolWheel = { x: number; y: number; selected: ControllerTool | null };

/** Clockwise from the top; releasing Q in the centre cancels instead of changing tools. */
export function wheelSelection(x: number, y: number): ControllerTool | null {
  if (Math.hypot(x, y) < 28) return null;
  const angle = (Math.atan2(y, x) + Math.PI / 2 + Math.PI * 2) % (Math.PI * 2);
  return CONTROLLER_TOOLS[Math.round(angle / (Math.PI / 2)) % 4]!.id;
}

export function canCaptureController(): boolean {
  return !isTyping(document.activeElement)
    && !document.querySelector('[role="dialog"]:not([data-state="closed"]), [data-testid="fly-editor"], [data-controller-menu] [aria-expanded="true"]');
}

const fullscreenRequests = new WeakMap<HTMLElement, Promise<boolean>>();
const KEYBOARD_NOTICE = 'Browser shortcut protection is unavailable. Avoid Ctrl+W while controlling this view.';

/** Start during the click/key gesture, before React switches camera modes. Concurrent capture shares this request. */
export function requestControllerFullscreen(host: HTMLElement): Promise<boolean> {
  if (document.fullscreenElement?.contains(host)) return Promise.resolve(true);
  const pending = fullscreenRequests.get(host);
  if (pending) return pending;
  let request: Promise<boolean>;
  try {
    request = host.requestFullscreen
      ? Promise.resolve(host.requestFullscreen({ navigationUI: 'hide' })).then(() => true, () => false)
      : Promise.resolve(false);
  } catch { request = Promise.resolve(false); }
  fullscreenRequests.set(host, request);
  void request.finally(() => { if (fullscreenRequests.get(host) === request) fullscreenRequests.delete(host); });
  return request;
}

/** Fullscreen must precede pointer lock so Chromium can capture reserved movement shortcuts such as Ctrl+W. */
export async function requestControllerCapture(
  target: { surface: HTMLElement; lock: () => void },
  fullscreenHost: HTMLElement,
  stillActive: () => boolean = () => true,
): Promise<string | null> {
  if (!await requestControllerFullscreen(fullscreenHost))
    return 'Full screen was blocked. Click the world and allow full screen to protect movement keys from browser shortcuts.';
  if (!stillActive() || target.surface.isConnected === false) return null;
  try { target.lock(); }
  catch { return 'Mouse capture was blocked. Click the world to try again.'; }
  return (navigator as Navigator & { keyboard?: { lock?: unknown } }).keyboard?.lock ? null : KEYBOARD_NOTICE;
}

/** Reserved browser shortcuts need optional Keyboard Lock plus user-entered Fullscreen API mode. */
export function bindControllerKeyboard(active: () => boolean, notice?: (message: string | null) => void): () => void {
  const keyboard = (navigator as Navigator & { keyboard?: { lock: (codes: string[]) => Promise<void>; unlock: () => void } }).keyboard;
  if (!keyboard?.lock) return () => {};
  let requested = false, disposed = false, revision = 0;
  const release = () => {
    revision++;
    if (requested) { requested = false; keyboard.unlock(); }
  };
  const sync = () => {
    if (disposed || !active() || !document.fullscreenElement) { release(); return; }
    if (requested) return;
    requested = true;
    const started = ++revision;
    try {
      void keyboard.lock(['KeyW', 'KeyA', 'KeyS', 'KeyD']).then(() => {
        if (started !== revision && !requested) keyboard.unlock();
        else if (started === revision) notice?.(null);
      }, () => { if (started === revision) { requested = false; notice?.(KEYBOARD_NOTICE); } });
    } catch { requested = false; notice?.(KEYBOARD_NOTICE); }
  };
  document.addEventListener('pointerlockchange', sync);
  document.addEventListener('fullscreenchange', sync);
  window.addEventListener('blur', release);
  window.addEventListener('focus', sync);
  sync();
  return () => {
    disposed = true; release();
    document.removeEventListener('pointerlockchange', sync);
    document.removeEventListener('fullscreenchange', sync);
    window.removeEventListener('blur', release);
    window.removeEventListener('focus', sync);
  };
}

/** The wheel retains mouse lock, routing relative motion to its cursor until Q is released. */
export function bindControllerInput(options: {
  active: () => boolean;
  editable: () => boolean;
  editing: () => boolean;
  pause: (paused: boolean) => void;
  wheel: (state: ToolWheel | null) => void;
  choose: (tool: ControllerTool) => void;
  release: () => void;
  notice?: (message: string | null) => void;
}) {
  const releaseKeyboard = bindControllerKeyboard(options.active, options.notice);
  let wheel: ToolWheel | null = null;
  const consume = (event: Event) => { event.preventDefault(); event.stopImmediatePropagation(); };
  const finish = (commit: boolean) => {
    if (!wheel) return;
    const selected = wheel.selected;
    wheel = null;
    options.pause(false);
    options.wheel(null);
    if (commit && selected) options.choose(selected);
  };
  const down = (event: KeyboardEvent) => {
    if (event.metaKey || event.altKey) return;
    if (wheel && event.code === 'Escape') {
      consume(event); finish(false); options.release(); return;
    }
    if (!options.active() || isTyping(event.target)) return;
    if (event.code === 'KeyQ') {
      consume(event);
      if (!wheel && !event.repeat) {
        wheel = { x: 0, y: 0, selected: null };
        options.pause(true); options.wheel(wheel);
      }
    } else if (wheel) consume(event);
    else if (options.editable() && (event.code === 'KeyE' || event.code === 'KeyB' || event.code === 'KeyR' && options.editing())) {
      consume(event);
      if (!event.repeat) options.choose(event.code === 'KeyR' ? 'rotate' : 'blocks');
    }
  };
  const up = (event: KeyboardEvent) => {
    if (event.code === 'KeyQ' && wheel) { consume(event); finish(options.active()); }
  };
  const move = (event: PointerEvent) => {
    if (!wheel) return;
    consume(event);
    const x = wheel.x + event.movementX, y = wheel.y + event.movementY;
    const scale = Math.min(1, 140 / Math.max(1, Math.hypot(x, y)));
    wheel = { x: x * scale, y: y * scale, selected: wheelSelection(x, y) };
    options.wheel(wheel);
  };
  const block = (event: Event) => { if (wheel) consume(event); };
  const cancel = () => finish(false);
  const lock = () => { if (!options.active()) finish(false); };
  window.addEventListener('keydown', down, true);
  window.addEventListener('keyup', up, true);
  window.addEventListener('pointermove', move, true);
  window.addEventListener('pointerdown', block, true);
  window.addEventListener('pointerup', block, true);
  window.addEventListener('wheel', block, { capture: true, passive: false });
  window.addEventListener('blur', cancel);
  document.addEventListener('pointerlockchange', lock);
  return () => {
    releaseKeyboard();
    finish(false);
    window.removeEventListener('keydown', down, true);
    window.removeEventListener('keyup', up, true);
    window.removeEventListener('pointermove', move, true);
    window.removeEventListener('pointerdown', block, true);
    window.removeEventListener('pointerup', block, true);
    window.removeEventListener('wheel', block, true);
    window.removeEventListener('blur', cancel);
    document.removeEventListener('pointerlockchange', lock);
  };
}
