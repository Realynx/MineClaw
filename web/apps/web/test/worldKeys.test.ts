import { describe, expect, it } from 'vitest';
import { onWorldKey, type WorldKeyDoc } from '@/features/world/worldKeys';

/** Every key the world pane knows, and a few it does not, as (key, code). */
const KEYS: Array<[string, string]> = [
  ['F3', 'F3'],
  ['f', 'KeyF'],
  ['F', 'KeyF'],
  ['t', 'KeyT'],
  ['Enter', 'Enter'],
  ['/', 'Slash'],
  ['e', 'KeyE'],
  ['Tab', 'Tab'],
  ['1', 'Digit1'],
  ['9', 'Digit9'],
  ['0', 'Digit0'],
  ['Escape', 'Escape'],
  [' ', 'Space'],
  ['x', 'KeyX'],
  ['ArrowUp', 'ArrowUp'],
];

interface Scene {
  name: string;
  focused: boolean;
  fullscreen: boolean;
  locked: boolean;
  chat: boolean;
  target: 'stage' | 'input' | 'button';
  mods: Partial<Record<'shiftKey' | 'altKey' | 'ctrlKey' | 'metaKey', boolean>>;
}

const SCENES: Scene[] = [
  { name: 'outside', focused: false, fullscreen: false, locked: false, chat: false, target: 'stage', mods: {} },
  { name: 'focused', focused: true, fullscreen: false, locked: false, chat: false, target: 'stage', mods: {} },
  { name: 'focused+shift', focused: true, fullscreen: false, locked: false, chat: false, target: 'stage', mods: { shiftKey: true } },
  { name: 'focused+ctrl', focused: true, fullscreen: false, locked: false, chat: false, target: 'stage', mods: { ctrlKey: true } },
  { name: 'focused+alt', focused: false, fullscreen: true, locked: false, chat: false, target: 'stage', mods: { altKey: true } },
  { name: 'focused+meta', focused: true, fullscreen: false, locked: false, chat: false, target: 'stage', mods: { metaKey: true } },
  { name: 'fullscreen', focused: false, fullscreen: true, locked: false, chat: false, target: 'stage', mods: {} },
  { name: 'locked', focused: false, fullscreen: false, locked: true, chat: false, target: 'stage', mods: {} },
  { name: 'locked+shift', focused: false, fullscreen: false, locked: true, chat: false, target: 'stage', mods: { shiftKey: true } },
  { name: 'locked fullscreen', focused: false, fullscreen: true, locked: true, chat: false, target: 'stage', mods: {} },
  { name: 'chat open', focused: true, fullscreen: false, locked: false, chat: true, target: 'stage', mods: {} },
  { name: 'typing', focused: true, fullscreen: false, locked: false, chat: false, target: 'input', mods: {} },
  { name: 'on a button', focused: true, fullscreen: false, locked: false, chat: false, target: 'button', mods: {} },
];

function run(scene: Scene, key: string, code: string): string {
  const calls: string[] = [];
  const stage = document.createElement('div');
  const inner = document.createElement('div');
  const input = document.createElement('input');
  const button = document.createElement('button');
  stage.append(inner, input, button);
  const outside = document.createElement('div');
  const doc: WorldKeyDoc = {
    activeElement: scene.focused ? inner : outside,
    fullscreenElement: scene.fullscreen ? stage : null,
    pointerLockElement: scene.locked ? inner : null,
    exitPointerLock: () => void calls.push('exitPointerLock'),
    exitFullscreen: () => {
      calls.push('exitFullscreen');
      return Promise.resolve();
    },
  };
  let prevented = false;
  const target = scene.target === 'input' ? input : scene.target === 'button' ? button : inner;
  const e = {
    key,
    code,
    target,
    shiftKey: false,
    altKey: false,
    ctrlKey: false,
    metaKey: false,
    ...scene.mods,
    preventDefault: () => {
      prevented = true;
    },
  } as unknown as KeyboardEvent;
  onWorldKey(
    e,
    {
      stage,
      chatOpen: () => scene.chat,
      toggleHud: () => calls.push('hud'),
      openChat: () => calls.push('openChat'),
      closeChat: () => calls.push('closeChat'),
      toggleFullscreen: () => calls.push('fullscreen'),
      toggleAimedContainer: () => calls.push('container'),
      selectNumber: (n, add) => calls.push(`select ${n}${add ? '+' : ''}`),
    },
    doc,
  );
  return `${prevented ? 'prevented ' : ''}${calls.join(',') || '-'}`;
}

describe('world pane keys (every key in every state, pinned before the keymap became a table)', () => {
  it('does the same for each key and state', () => {
    const out: Record<string, string> = {};
    for (const s of SCENES) for (const [key, code] of KEYS) out[`${s.name} | ${key}`] = run(s, key, code);
    expect(out).toMatchSnapshot();
  });
});
