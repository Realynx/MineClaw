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
  if (e.key === 'F3') {
    e.preventDefault();
    k.toggleHud();
    return;
  }
  if (isTyping(e.target)) return;
  const el = k.stage;
  const inWorld = !!el && (el.contains(doc.activeElement) || doc.fullscreenElement === el || (!!doc.pointerLockElement && el.contains(doc.pointerLockElement)));
  if (!inWorld || e.altKey || e.ctrlKey || e.metaKey) return;
  if ((e.key === 'Enter' || e.key === ' ') && (e.target as HTMLElement | null)?.closest?.('button, a')) return; // a focused control
  if (k.chatOpen()) {
    // The bar is open but its input doesn't have the keys (disabled, or a button has focus): T / Enter / Esc close it.
    if (closesOpenBar(e)) {
      e.preventDefault();
      k.closeChat();
    }
    return;
  }
  if (e.code === 'KeyF' && !e.shiftKey) {
    e.preventDefault();
    k.toggleFullscreen();
  } else if (e.code === 'KeyT' || e.key === 'Enter' || e.key === '/') {
    e.preventDefault();
    k.openChat();
  } else if (e.code === 'KeyE' && doc.pointerLockElement && !e.shiftKey) {
    k.toggleAimedContainer();
  } else if (e.key === 'Tab') {
    e.preventDefault();
    k.toggleHud();
  } else if (/^Digit[1-9]$/.test(e.code)) {
    k.selectNumber(Number(e.code.slice(5)), e.shiftKey);
  } else if (e.key === 'Escape') {
    if (doc.pointerLockElement) doc.exitPointerLock();
    else if (doc.fullscreenElement === el) void doc.exitFullscreen().catch(() => undefined);
  }
}
