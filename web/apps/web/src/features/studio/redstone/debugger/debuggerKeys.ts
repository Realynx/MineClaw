/**
 * The debugger's keys: F8 run or pause, F10 step a redstone tick, Shift+F10 step a game tick, Shift+F8 run until the next
 * change, F9 a breakpoint and Shift+F9 a probe on the aimed cell, Ctrl+Shift+Backspace back to tick 0.
 *
 * Browsers own some of these (Firefox: F9 Reader View, F10 the menu bar, Shift+F10 the context menu), so a key the
 * debugger takes is never passed on, and every binding can be changed; the changes are kept in this browser. The listener
 * captures, because Ctrl-modified keys never reach the studio's own key rules and the browser must not see them first.
 */
import type { Breakpoint, Pos } from '@mineclaw/redstone';
import { useSyncExternalStore } from 'react';
import type { SimControllerApi } from '../simController.types';

export type DebuggerAction =
  'runPause' | 'stepRedstone' | 'stepGame' | 'runUntilChange' | 'toggleBreakpoint' | 'toggleProbe' | 'reset';

/** A key with its modifiers. `key` is `KeyboardEvent.key`, a letter in upper case; `ctrl` is Ctrl, or Cmd on a Mac. */
export interface KeyChord {
  key: string;
  shift?: boolean;
  ctrl?: boolean;
  alt?: boolean;
}

export type KeyMap = Readonly<Record<DebuggerAction, KeyChord>>;

export const ACTIONS: ReadonlyArray<{ id: DebuggerAction; label: string }> = [
  { id: 'runPause', label: 'Run or pause' },
  { id: 'stepRedstone', label: 'Step a redstone tick' },
  { id: 'stepGame', label: 'Step a game tick' },
  { id: 'runUntilChange', label: 'Run until something changes' },
  { id: 'toggleBreakpoint', label: 'Breakpoint on the aimed cell' },
  { id: 'toggleProbe', label: 'Probe on the aimed cell' },
  { id: 'reset', label: 'Back to tick 0' },
];

export const DEFAULT_KEYS: KeyMap = {
  runPause: { key: 'F8' },
  stepRedstone: { key: 'F10' },
  stepGame: { key: 'F10', shift: true },
  runUntilChange: { key: 'F8', shift: true },
  toggleBreakpoint: { key: 'F9' },
  toggleProbe: { key: 'F9', shift: true },
  reset: { key: 'Backspace', ctrl: true, shift: true },
};

export const KEYS_STORAGE = 'mineclaw.redstone.debuggerKeys.v1';

/** Keys a binding cannot use: they belong to leaving a field, the focus, or are modifiers on their own. */
const RESERVED = new Set([
  'Escape',
  'Tab',
  'Shift',
  'Control',
  'Alt',
  'Meta',
  'AltGraph',
  'CapsLock',
  'Dead',
  'Unidentified',
]);

const ACTION_IDS = ACTIONS.map((a) => a.id);

/** A key as the map stores it: one letter in upper case, so Shift does not change it. */
const normalKey = (key: string): string => (key.length === 1 ? key.toUpperCase() : key);

/** The chord a key press makes, or null for a key no binding can use. */
export function chordOf(
  e: Pick<KeyboardEvent, 'key' | 'shiftKey' | 'ctrlKey' | 'metaKey' | 'altKey'>,
): KeyChord | null {
  if (!e.key || RESERVED.has(e.key)) return null;
  const chord: KeyChord = { key: normalKey(e.key) };
  if (e.shiftKey) chord.shift = true;
  if (e.ctrlKey || e.metaKey) chord.ctrl = true;
  if (e.altKey) chord.alt = true;
  return chord;
}

export const sameChord = (a: KeyChord, b: KeyChord): boolean =>
  normalKey(a.key) === normalKey(b.key) && !!a.shift === !!b.shift && !!a.ctrl === !!b.ctrl && !!a.alt === !!b.alt;

/** How a chord is written: `Ctrl+Shift+Backspace`. */
export function chordLabel(c: KeyChord): string {
  const key = c.key === ' ' ? 'Space' : c.key;
  return [c.ctrl ? 'Ctrl' : '', c.alt ? 'Alt' : '', c.shift ? 'Shift' : '', key].filter(Boolean).join('+');
}

export function actionFor(map: KeyMap, chord: KeyChord): DebuggerAction | null {
  return ACTION_IDS.find((id) => sameChord(map[id], chord)) ?? null;
}

function readChord(v: unknown): KeyChord | null {
  if (typeof v !== 'object' || v === null) return null;
  const o = v as Record<string, unknown>;
  if (typeof o.key !== 'string' || !o.key || RESERVED.has(o.key)) return null;
  const chord: KeyChord = { key: normalKey(o.key) };
  if (o.shift === true) chord.shift = true;
  if (o.ctrl === true) chord.ctrl = true;
  if (o.alt === true) chord.alt = true;
  return chord;
}

type KeyStorage = Pick<Storage, 'getItem' | 'setItem' | 'removeItem'>;

function storage(): KeyStorage | null {
  try {
    return typeof localStorage === 'undefined' ? null : localStorage;
  } catch {
    return null;
  }
}

/**
 * The map this browser keeps: the defaults with the saved changes over them. Saved data that would give two actions one
 * key (only a damaged entry can) is ignored as a whole.
 */
export function loadKeyMap(from: KeyStorage | null = storage()): KeyMap {
  let saved: Record<string, unknown> = {};
  try {
    const raw: unknown = JSON.parse(from?.getItem(KEYS_STORAGE) ?? '{}');
    if (typeof raw === 'object' && raw !== null && !Array.isArray(raw)) saved = raw as Record<string, unknown>;
  } catch {
    saved = {};
  }
  const map = { ...DEFAULT_KEYS } as Record<DebuggerAction, KeyChord>;
  for (const id of ACTION_IDS) map[id] = readChord(saved[id]) ?? map[id];
  const clash = ACTION_IDS.some((id, i) => ACTION_IDS.slice(i + 1).some((other) => sameChord(map[id], map[other])));
  return clash ? DEFAULT_KEYS : map;
}

let current: KeyMap | null = null;
const listeners = new Set<() => void>();

/** The map in use. */
export function keyMap(): KeyMap {
  current ??= loadKeyMap();
  return current;
}

function publish(next: KeyMap): void {
  current = next;
  try {
    const s = storage();
    const changed = ACTION_IDS.filter((id) => !sameChord(next[id], DEFAULT_KEYS[id]));
    if (changed.length === 0) s?.removeItem(KEYS_STORAGE);
    else s?.setItem(KEYS_STORAGE, JSON.stringify(Object.fromEntries(changed.map((id) => [id, next[id]]))));
  } catch {
    // Without storage the change holds until the page is left.
  }
  for (const l of listeners) l();
}

/** Binds `action` to `chord`. An action that had the chord takes this one's old key instead, so no two share one. */
export function setKey(action: DebuggerAction, chord: KeyChord): void {
  const map = keyMap();
  const other = actionFor(map, chord);
  if (other === action) return;
  const next = { ...map, [action]: { ...chord, key: normalKey(chord.key) } };
  if (other) next[other] = map[action];
  publish(next);
}

export function resetKeys(): void {
  publish(DEFAULT_KEYS);
}

/** Forgets the map read from storage, so the next read loads it again (tests, another tab's change). */
export function reloadKeyMap(): void {
  current = null;
  for (const l of listeners) l();
}

const subscribeKeys = (fn: () => void) => {
  listeners.add(fn);
  return () => listeners.delete(fn);
};

/** The map in use, re-rendering when it changes. */
export function useKeyMap(): KeyMap {
  return useSyncExternalStore(subscribeKeys, keyMap, keyMap);
}

/** The words for an action with its key, for a button's title: `Run or pause (F8)`. */
export function withKey(map: KeyMap, action: DebuggerAction, text: string): string {
  return `${text} (${chordLabel(map[action])})`;
}

const samePos = (a: Pos, b: Pos) => a[0] === b[0] && a[1] === b[1] && a[2] === b[2];

/** The next free id of the form `prefix` + number. */
export function nextId(prefix: string, ids: readonly string[]): string {
  const rx = new RegExp(`^${prefix}(\\d+)$`);
  const n = 1 + Math.max(0, ...ids.map((id) => Number(rx.exec(id)?.[1] ?? 0)));
  return `${prefix}${n}`;
}

/** A breakpoint on the cell: the one there goes, or a new one ("any change") is set. */
export function toggleBreakpoint(api: SimControllerApi, at: Pos): void {
  const snap = api.snapshot();
  const there = snap.breakpoints.find((b) => samePos(b.at, at));
  if (there) return api.removeBreakpoint(there.id);
  const b: Breakpoint = {
    id: nextId(
      'b',
      snap.breakpoints.map((x) => x.id),
    ),
    at: [at[0], at[1], at[2]],
    on: 'change',
    enabled: true,
  };
  api.setBreakpoint(b);
}

/** A probe on the cell: the one there goes, or a new one is added. */
export function toggleProbe(api: SimControllerApi, at: Pos): void {
  const there = api.snapshot().probes.find((p) => samePos(p.at, at));
  if (there) api.removeProbe(there.id);
  else api.addProbe(at);
}

/** Does what the action says. */
export function runAction(action: DebuggerAction, api: SimControllerApi, aimed: () => Pos | null): void {
  switch (action) {
    case 'runPause':
      return api.snapshot().mode === 'running' ? api.pause() : api.start();
    case 'stepRedstone':
      return api.stepRedstoneTick();
    case 'stepGame':
      return api.stepGameTick();
    case 'runUntilChange':
      return api.runUntilChange();
    case 'reset':
      return api.reset();
    case 'toggleBreakpoint': {
      const at = aimed();
      return at ? toggleBreakpoint(api, at) : undefined;
    }
    case 'toggleProbe': {
      const at = aimed();
      return at ? toggleProbe(api, at) : undefined;
    }
  }
}

/** Held down, only the steps repeat; a toggle would flip back and forth. */
const REPEATS: ReadonlySet<DebuggerAction> = new Set(['stepRedstone', 'stepGame']);

export interface DebuggerKeyOptions {
  /** Whether the keys apply now: the stage has the focus and no text field does. */
  inWorld: () => boolean;
  /** The cell under the pointer now (a breakpoint or a probe goes on it). */
  aimed: () => Pos | null;
}

/** Binds the debugger's keys on `target` and returns the function that removes them. */
export function bindDebuggerKeys(target: HTMLElement, api: SimControllerApi, opts: DebuggerKeyOptions): () => void {
  const onKey = (e: KeyboardEvent) => {
    if (e.defaultPrevented || e.isComposing) return;
    const chord = chordOf(e);
    const action = chord ? actionFor(keyMap(), chord) : null;
    if (!action || !opts.inWorld()) return;
    e.preventDefault();
    e.stopPropagation();
    if (e.repeat && !REPEATS.has(action)) return;
    runAction(action, api, opts.aimed);
  };
  target.addEventListener('keydown', onKey, true);
  return () => target.removeEventListener('keydown', onKey, true);
}
