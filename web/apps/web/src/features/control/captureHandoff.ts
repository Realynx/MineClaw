import { requestControllerFullscreen } from '@/features/story/controllerInput';

export interface TakeoverCapture {
  mount(): () => void;
  transfer(surface: HTMLElement, active: () => boolean): Promise<boolean>;
  release(): void;
}

let pending: { pawn: string; capture: TakeoverCapture } | null = null;
let current: TakeoverCapture | null = null;

/** The document survives route changes; transfer its existing lock only after the pawn's input is ready. */
export function takeoverCapture(pawn: string): TakeoverCapture | null {
  return pending?.pawn === pawn ? pending.capture : null;
}

/** Called in the original click, while fullscreen and pointer lock still have user activation. */
export function beginTakeoverCapture(pawn: string, navigate: () => void): void {
  current?.release();
  const root = document.documentElement;
  const ownsFullscreen = document.fullscreenElement !== root;
  let cancelled = false, transferred = false, acquiring = true;
  let mounts = 0;
  let unmountTimer: ReturnType<typeof setTimeout> | null = null;
  let surface: HTMLElement | null = null;
  const detach = () => {
    clearTimeout(timer);
    if (unmountTimer !== null) clearTimeout(unmountTimer);
    document.removeEventListener('pointerlockchange', changed);
    document.removeEventListener('visibilitychange', hidden);
    window.removeEventListener('keydown', escape, true);
    window.removeEventListener('blur', release);
    if (pending?.capture === capture) pending = null;
  };
  const release = () => {
    cancelled = true;
    detach();
    if (current !== capture) return;
    if (document.pointerLockElement === root || surface && document.pointerLockElement === surface) document.exitPointerLock();
    if (ownsFullscreen && document.fullscreenElement === root) void document.exitFullscreen?.().catch(() => {});
  };
  const changed = () => {
    if (!acquiring && !transferred && document.pointerLockElement !== root && (!surface || document.pointerLockElement !== surface)) release();
  };
  const hidden = () => { if (document.hidden) release(); };
  const escape = (event: KeyboardEvent) => { if (event.code === 'Escape') release(); };
  const timer = setTimeout(release, 15_000);
  const capture: TakeoverCapture = {
    mount() {
      mounts++;
      if (unmountTimer !== null) clearTimeout(unmountTimer);
      return () => {
        mounts--;
        // Route suspense/Strict Mode may replace the first view before its canvas mounts.
        unmountTimer = setTimeout(() => { if (mounts === 0) release(); }, 100);
      };
    },
    async transfer(target, active) {
      if (cancelled || transferred || document.pointerLockElement !== root || !active()) return false;
      surface = target;
      try {
        target.focus();
        await target.requestPointerLock();
        if (cancelled || !active() || !target.isConnected) { release(); return false; }
        transferred = document.pointerLockElement === target;
        if (transferred) detach();
        else release();
        return transferred;
      } catch { release(); return false; }
    },
    release,
  };
  current = capture;
  pending = { pawn, capture };
  document.addEventListener('pointerlockchange', changed);
  document.addEventListener('visibilitychange', hidden);
  window.addEventListener('keydown', escape, true);
  window.addEventListener('blur', release);
  void (async () => {
    try {
      // Fullscreen may consume transient activation. Issue both in the click, with pointer lock first.
      const mouse = Promise.resolve(root.requestPointerLock()).then(() => true, () => false);
      const [locked, full] = await Promise.all([mouse, requestControllerFullscreen(root)]);
      if (cancelled) { release(); return; }
      if (!locked || !full) throw new Error('Immersive capture unavailable');
      acquiring = false;
      if (document.pointerLockElement !== root) throw new Error('Pointer lock unavailable');
    } catch {
      // Browser restrictions retain the ordinary route and its explicit Click to play recovery.
      if (cancelled) return;
      release();
    }
    navigate();
  })();
}
