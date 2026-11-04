/**
 * The world pane's keys (WorldPane.tsx listens on the window and hands each key here): F3 toggles the HUD anywhere; with the
 * world focused, fullscreen or holding the mouse, F full screen, T / Enter / "/" the chat bar (or, while it is open, T / Enter
 * / Esc close it), E the container under the crosshair, Tab the HUD, 1–9 pick a bot in view (Shift adds), Esc frees the mouse
 * and then leaves full screen. Typing in a field, a modifier, or Enter / Space on a focused button or link is left alone.
 */
import { closesOpenBar, isTyping } from './directives';

/** What the keys do, on the pane. */
export interface WorldKeyActions {
  /** The stage (the world and its overlays): the keys work while it has focus, is fullscreen or holds the mouse. */
  stage: HTMLElement | null;
  chatOpen: () => boolean;
  toggleHud: () => void;
  openChat: () => void;
  closeChat: () => void;
  toggleFullscreen: () => void;
  /** E in free cam: the container under the crosshair opens (or closes, if it is the open one); anything else closes it. */
  toggleAimedContainer: () => void;
  /** 1–9: the nth bot in view (Shift: added to the selection). */
  selectNumber: (n: number, add: boolean) => void;
}

/** The document state the keys read (the real `document` in the app). */
export type WorldKeyDoc = Pick<Document, 'activeElement' | 'fullscreenElement' | 'pointerLockElement' | 'exitPointerLock' | 'exitFullscreen'>;

export function onWorldKey(e: KeyboardEvent, k: WorldKeyActions, doc: WorldKeyDoc = document): void {
  if (e.key === 'F3') return press(e, k.toggleHud);
  if (!worldHasKeys(e, k.stage, doc)) return;
  if (k.chatOpen()) {
    // The bar is open but its input doesn't have the keys (disabled, or a button has focus): T / Enter / Esc close it.
    if (closesOpenBar(e)) press(e, k.closeChat);
    return;
  }
  KEYMAP.find((r) => r.when(e, doc))?.run(e, k, doc);
}

function press(e: KeyboardEvent, action: () => void) {
  e.preventDefault();
  action();
}

/** The keys are the world's: not typing, the stage focused / fullscreen / holding the mouse, no modifier, not on a control. */
function worldHasKeys(e: KeyboardEvent, el: HTMLElement | null, doc: WorldKeyDoc): boolean {
  if (isTyping(e.target) || !inWorld(el, doc) || e.altKey || e.ctrlKey || e.metaKey) return false;
  return !((e.key === 'Enter' || e.key === ' ') && (e.target as HTMLElement | null)?.closest?.('button, a')); // a focused control
}

function inWorld(el: HTMLElement | null, doc: WorldKeyDoc): boolean {
  if (!el) return false;
  return el.contains(doc.activeElement) || doc.fullscreenElement === el || (!!doc.pointerLockElement && el.contains(doc.pointerLockElement));
}

/** Esc: free the mouse first, then leave full screen (when the stage is what is fullscreen). */
function escape(k: WorldKeyActions, doc: WorldKeyDoc) {
  if (doc.pointerLockElement) doc.exitPointerLock();
  else if (doc.fullscreenElement === k.stage) void doc.exitFullscreen().catch(() => undefined);
}

interface KeyRule {
  when: (e: KeyboardEvent, doc: WorldKeyDoc) => boolean;
  run: (e: KeyboardEvent, k: WorldKeyActions, doc: WorldKeyDoc) => void;
}

/** The world's keys, first match wins. */
const KEYMAP: readonly KeyRule[] = [
  { when: (e) => e.code === 'KeyF' && !e.shiftKey, run: (e, k) => press(e, k.toggleFullscreen) },
  { when: (e) => e.code === 'KeyT' || e.key === 'Enter' || e.key === '/', run: (e, k) => press(e, k.openChat) },
  { when: (e, doc) => e.code === 'KeyE' && !!doc.pointerLockElement && !e.shiftKey, run: (_e, k) => k.toggleAimedContainer() },
  { when: (e) => e.key === 'Tab', run: (e, k) => press(e, k.toggleHud) },
  { when: (e) => /^Digit[1-9]$/.test(e.code), run: (e, k) => k.selectNumber(Number(e.code.slice(5)), e.shiftKey) },
  { when: (e) => e.key === 'Escape', run: (_e, k, doc) => escape(k, doc) },
];
