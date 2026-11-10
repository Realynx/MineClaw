/**
 * The debugger's keys against a stand-in controller: the proposed bindings, each taken from the browser (default
 * prevented) ahead of the page's own listeners, only while the world has the focus; the toggles on the aimed cell; held
 * keys; and bindings changed in this browser, kept across reloads, with no two actions on one key.
 */
import type { Pos } from '@mineclaw/redstone';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { bindDebuggerKeys } from '@/features/studio/redstone/debugger';
import {
  actionFor,
  chordLabel,
  chordOf,
  DEFAULT_KEYS,
  keyMap,
  KEYS_STORAGE,
  loadKeyMap,
  reloadKeyMap,
  resetKeys,
  setKey,
} from '@/features/studio/redstone/debugger/debuggerKeys';
import { fakeController } from './redstoneDebuggerFixture';

const CELL: Pos = [4, 0, 2];

const unbinders: Array<() => void> = [];
beforeEach(() => {
  localStorage.clear();
  reloadKeyMap();
});
afterEach(() => {
  unbinders.splice(0).forEach((off) => off());
  document.body.innerHTML = '';
});

function bind(o: { inWorld?: boolean; aimed?: Pos | null } = {}) {
  const fake = fakeController();
  const state = { inWorld: o.inWorld ?? true, aimed: o.aimed === undefined ? CELL : o.aimed };
  const stage = document.createElement('div');
  stage.tabIndex = 0;
  document.body.appendChild(stage);
  unbinders.push(bindDebuggerKeys(document.body, fake.api, { inWorld: () => state.inWorld, aimed: () => state.aimed }));
  /** Presses a key on the stage, as the browser would, and says whether the browser would still act on it. */
  const press = (key: string, mods: KeyboardEventInit = {}) => {
    const e = new KeyboardEvent('keydown', { key, bubbles: true, cancelable: true, ...mods });
    stage.dispatchEvent(e);
    return e;
  };
  return { ...fake, state, stage, press };
}

describe('the proposed keys', () => {
  it('run, step, run until a change and reset, each kept from the browser', () => {
    const h = bind();
    const presses = [
      h.press('F8'),
      h.press('F10'),
      h.press('F10', { shiftKey: true }),
      h.press('F8', { shiftKey: true }),
      h.press('Backspace', { ctrlKey: true, shiftKey: true }),
    ];
    expect(presses.map((e) => e.defaultPrevented)).toEqual([true, true, true, true, true]);
    expect(h.api.start).toHaveBeenCalledTimes(1);
    expect(h.api.stepRedstoneTick).toHaveBeenCalledTimes(1);
    expect(h.api.stepGameTick).toHaveBeenCalledTimes(1);
    expect(h.api.runUntilChange).toHaveBeenCalledTimes(1);
    expect(h.api.reset).toHaveBeenCalledTimes(1);
  });

  it('F8 pauses a running circuit', () => {
    const h = bind();
    h.set({ mode: 'running' });
    h.press('F8');
    expect(h.api.pause).toHaveBeenCalledTimes(1);
    expect(h.api.start).not.toHaveBeenCalled();
  });

  it('F9 and Shift+F9 toggle a breakpoint and a probe on the aimed cell', () => {
    const h = bind();
    h.set({ breakpoints: [{ id: 'b1', at: [0, 0, 0], on: 'rising', enabled: true }] });
    h.press('F9');
    expect(h.api.setBreakpoint).toHaveBeenCalledWith({ id: 'b2', at: CELL, on: 'change', enabled: true });
    h.set({ breakpoints: [{ id: 'b2', at: CELL, on: 'change', enabled: true }] });
    h.press('F9');
    expect(h.api.removeBreakpoint).toHaveBeenCalledWith('b2');

    h.press('F9', { shiftKey: true });
    expect(h.api.addProbe).toHaveBeenCalledWith(CELL);
    h.set({ probes: [{ id: 'p1', at: CELL, label: 'P1', color: 1 }] });
    h.press('F9', { shiftKey: true });
    expect(h.api.removeProbe).toHaveBeenCalledWith('p1');
  });

  it('with no aimed cell the toggles do nothing, but the browser still does not get the key', () => {
    const h = bind({ aimed: null });
    expect(h.press('F9').defaultPrevented).toBe(true);
    h.press('F9', { shiftKey: true });
    expect(h.api.setBreakpoint).not.toHaveBeenCalled();
    expect(h.api.addProbe).not.toHaveBeenCalled();
  });

  it('a Cmd chord counts as Ctrl', () => {
    const h = bind();
    h.press('Backspace', { metaKey: true, shiftKey: true });
    expect(h.api.reset).toHaveBeenCalledTimes(1);
  });
});

describe('when the keys apply', () => {
  it('not while the focus is elsewhere: the key is left to the page and the browser', () => {
    const h = bind({ inWorld: false });
    const e = h.press('F8');
    expect(e.defaultPrevented).toBe(false);
    expect(h.api.start).not.toHaveBeenCalled();
  });

  it('other keys and other chords are left alone', () => {
    const h = bind();
    const plain = [h.press('Backspace'), h.press('F8', { altKey: true }), h.press('F11'), h.press('w')];
    expect(plain.map((e) => e.defaultPrevented)).toEqual([false, false, false, false]);
    expect(
      Object.values(h.api).some((fn) => fn !== h.api.subscribe && fn !== h.api.snapshot && fn.mock.calls.length > 0),
    ).toBe(false);
  });

  it('is ahead of the page: a key it takes reaches neither the stage nor anything above it', () => {
    const h = bind();
    const seen = vi.fn();
    h.stage.addEventListener('keydown', seen);
    const outer = vi.fn();
    window.addEventListener('keydown', outer);
    h.press('F10');
    window.removeEventListener('keydown', outer);
    expect(h.api.stepRedstoneTick).toHaveBeenCalledTimes(1);
    // Taken on its way down to the stage, so the studio's own key rules never act on it too.
    expect(seen).not.toHaveBeenCalled();
    expect(outer).not.toHaveBeenCalled();
  });

  it('a held step repeats, a held toggle does not', () => {
    const h = bind();
    h.press('F10', { repeat: true });
    h.press('F10', { repeat: true, shiftKey: true });
    h.press('F8', { repeat: true });
    h.press('F9', { repeat: true });
    h.press('Backspace', { repeat: true, ctrlKey: true, shiftKey: true });
    expect(h.api.stepRedstoneTick).toHaveBeenCalledTimes(1);
    expect(h.api.stepGameTick).toHaveBeenCalledTimes(1);
    expect([h.api.start, h.api.setBreakpoint, h.api.reset].map((f) => f.mock.calls.length)).toEqual([0, 0, 0]);
  });

  it('stops when unbound', () => {
    const h = bind();
    unbinders.splice(0).forEach((off) => off());
    expect(h.press('F8').defaultPrevented).toBe(false);
    expect(h.api.start).not.toHaveBeenCalled();
  });
});

describe('changed keys', () => {
  it('a new key works at once, the old one is free, and the change is kept in this browser', () => {
    const h = bind();
    setKey('runPause', { key: 'p' });
    expect(h.press('F8').defaultPrevented).toBe(false);
    h.press('P', { shiftKey: false });
    expect(h.api.start).toHaveBeenCalledTimes(1);
    expect(JSON.parse(localStorage.getItem(KEYS_STORAGE)!)).toEqual({ runPause: { key: 'P' } });

    // As after a reload.
    reloadKeyMap();
    expect(keyMap().runPause).toEqual({ key: 'P' });
    h.press('p');
    expect(h.api.start).toHaveBeenCalledTimes(2);
  });

  it('a key another action has is swapped, so no two actions share one', () => {
    setKey('runPause', { key: 'F10' });
    expect(keyMap().runPause).toEqual({ key: 'F10' });
    expect(keyMap().stepRedstone).toEqual({ key: 'F8' });
    reloadKeyMap();
    expect(keyMap().stepRedstone).toEqual({ key: 'F8' });
    expect(actionFor(keyMap(), { key: 'F10' })).toBe('runPause');
  });

  it('going back to the usual keys clears what was kept', () => {
    setKey('reset', { key: 'R', ctrl: true, alt: true });
    resetKeys();
    expect(keyMap()).toEqual(DEFAULT_KEYS);
    expect(localStorage.getItem(KEYS_STORAGE)).toBeNull();
  });

  it('damaged saved keys fall back to the usual ones', () => {
    const at = (text: string) => ({ getItem: () => text, setItem: () => undefined, removeItem: () => undefined });
    expect(loadKeyMap(at('not json'))).toEqual(DEFAULT_KEYS);
    expect(loadKeyMap(at(JSON.stringify({ runPause: { key: 'F10' } })))).toEqual(DEFAULT_KEYS);
    expect(loadKeyMap(at(JSON.stringify({ runPause: { key: 'Escape' }, reset: 5 })))).toEqual(DEFAULT_KEYS);
    expect(loadKeyMap(at(JSON.stringify({ toggleProbe: { key: 'F7' } }))).toggleProbe).toEqual({ key: 'F7' });
  });

  it('names and reads chords', () => {
    expect(chordLabel(DEFAULT_KEYS.reset)).toBe('Ctrl+Shift+Backspace');
    expect(chordLabel(DEFAULT_KEYS.stepGame)).toBe('Shift+F10');
    expect(chordLabel({ key: ' ' })).toBe('Space');
    expect(chordOf(new KeyboardEvent('keydown', { key: 'a', ctrlKey: true }))).toEqual({ key: 'A', ctrl: true });
    expect(chordOf(new KeyboardEvent('keydown', { key: 'Shift', shiftKey: true }))).toBeNull();
  });
});
