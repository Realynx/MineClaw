import { describe, expect, it, vi } from 'vitest';
import { runKeyAction } from '@/features/story/mode/keys';
import type { Story } from '@/features/story/mode/commands';
import { nextLevel, type KeyAction } from '@/features/story/tools';

/** A screen state whose setters record what they were given (functional updates applied to `prev`). */
function fake(over: Partial<Record<string, unknown>> = {}) {
  const calls: string[] = [];
  const prev: Record<string, unknown> = {
    setView: 'top',
    setHelp: false,
    setLayers: 2,
    setTab: null,
    setTool: { kind: 'select' },
    setBuild: { height: 3, facing: 's', material: 'oak_planks' },
    setHeightModes: { mine: 'surface', deconstruct: 'down' },
  };
  const setter = (name: string) =>
    vi.fn((v: unknown) => {
      const value = typeof v === 'function' ? (v as (p: unknown) => unknown)(prev[name]) : v;
      calls.push(`${name}(${JSON.stringify(value)})`);
    });
  const fn = (name: string) =>
    vi.fn((...a: unknown[]) => void calls.push(`${name}(${a.map((x) => JSON.stringify(x)).join(',')})`));
  const c = {
    state: { pawns: [{ name: 'a', drafted: false }], home: [10, 64, 20] },
    split: { pawns: ['a'], ai: [] },
    selection: { bodies: [], chests: [], bench: null },
    tool: { kind: 'select' },
    heightTool: null,
    layers: 2,
    lv: 64,
    build: { height: 3 },
    xrayOn: false,
    menu: null,
    help: false,
    chat: false,
    tab: null,
    matShown: [
      { key: '5:0', label: 'Planks', unlocked: true },
      { key: '4:0', label: 'Cobble', unlocked: true },
    ],
    matCurrent: '5:0',
    matRecent: [{ key: '4:0', label: 'Cobble', unlocked: true }],
    picking: { current: null },
    lastDraft: { current: null },
    map: { current: { cancelDrag: () => false, focus: fn('focus') } },
    fullscreen: { toggle: fn('fullscreen') },
    say: fn('say'),
    draft: fn('draft'),
    togglePause: fn('togglePause'),
    setSpeed: fn('setSpeed'),
    focusBody: fn('focusBody'),
    chooseMaterial: fn('chooseMaterial'),
    ...Object.fromEntries(
      [
        'setView',
        'setXray',
        'setHelp',
        'setChat',
        'setLevel',
        'setBuild',
        'setLayers',
        'setHeightModes',
        'setTool',
        'setTab',
        'setMenu',
        'setSelection',
        'setPickHint',
      ].map((n) => [n, setter(n)]),
    ),
    ...over,
  } as unknown as Story;
  return { c, calls };
}

const key = (o: Partial<KeyboardEvent> = {}) =>
  ({ shiftKey: false, stopPropagation: vi.fn(), ...o }) as unknown as KeyboardEvent;

function run(a: KeyAction, over?: Partial<Record<string, unknown>>, e = key()) {
  const { c, calls } = fake(over);
  runKeyAction(c, a, e);
  return calls;
}

describe('Story key handlers (each action → what it changes)', () => {
  it('runs the simple actions', () => {
    expect(run('pause')).toEqual(['togglePause()']);
    expect(run('speed:fast')).toEqual(['setSpeed("fast")']);
    expect(run('chat')).toEqual(['setChat(true)']);
    expect(run('view')).toEqual(['setView("angled")']);
    expect(run('xray')).toEqual(['setXray("on")']);
    expect(run('xray', { xrayOn: true })).toEqual(['setXray("off")']);
    expect(run('help')).toEqual(['setHelp(true)']);
    expect(run('fullscreen')).toEqual(['fullscreen()']);
    expect(run('levelUp')).toEqual([`setLevel(${nextLevel(64, 3, 1, false)})`]);
    expect(run('levelUp', {}, key({ shiftKey: true }))).toEqual([`setLevel(${nextLevel(64, 3, 1, true)})`]);
    expect(run('levelDown')).toEqual([`setLevel(${nextLevel(64, 3, -1, false)})`]);
    expect(run('rotateCw')).toEqual(['setBuild({"height":3,"facing":"w","material":"oak_planks"})']);
    expect(run('rotateCcw')).toEqual(['setBuild({"height":3,"facing":"e","material":"oak_planks"})']);
    expect(run('heightUp')).toEqual(['setBuild({"height":4,"facing":"s","material":"oak_planks"})']);
    expect(run('heightDown')).toEqual(['setBuild({"height":2,"facing":"s","material":"oak_planks"})']);
  });

  it('picks up and puts down tools, opens and closes tabs', () => {
    expect(run('tool:chop')).toEqual(['setTool({"kind":"designate","id":"chop"})']);
    const { c, calls } = fake();
    (c as unknown as { setTool: (f: unknown) => void }).setTool = vi.fn((f: unknown) => {
      calls.push(JSON.stringify((f as (p: unknown) => unknown)({ kind: 'designate', id: 'chop' })));
    });
    runKeyAction(c, 'tool:chop', key());
    expect(calls).toEqual(['{"kind":"select"}']);
    expect(run('tab:work')).toEqual(['setTab("work")']);
  });

  it('cycles and picks materials and names them', () => {
    expect(run('materialNext')).toEqual([
      'chooseMaterial({"key":"4:0","label":"Cobble","unlocked":true})',
      'say("Cobble")',
    ]);
    expect(run('recent:1')).toEqual([
      'chooseMaterial({"key":"4:0","label":"Cobble","unlocked":true})',
      'say("Cobble")',
    ]);
    expect(run('recent:3')).toEqual([]);
  });

  it('sets height modes and layers only with a height tool', () => {
    expect(run('height:level')).toEqual([]);
    expect(run('height:layers', { heightTool: 'mine' })).toEqual([
      'setHeightModes({"mine":"layers","deconstruct":"down"})',
      'say("Layers: 2")',
    ]);
    expect(run('layersUp')).toEqual(['setLayers(3)']);
    expect(run('layersDown', { heightTool: 'deconstruct' })).toEqual([
      'setLayers(1)',
      'setHeightModes({"mine":"surface","deconstruct":"layers"})',
    ]);
  });

  it('drafts the selected pawns, and a quick second R flips the last request', () => {
    const { c, calls } = fake();
    runKeyAction(c, 'draft', key());
    runKeyAction(c, 'draft', key());
    expect(calls).toEqual(['draft(["a"],true)', 'draft(["a"],false)']);
  });

  it('Esc closes the topmost thing first', () => {
    expect(run('escape', { menu: {} })).toEqual(['setMenu(null)']);
    expect(run('escape', { help: true, chat: true })).toEqual(['setHelp(false)']);
    expect(run('escape', { chat: true })).toEqual(['setChat(false)']);
    expect(run('escape', { tool: { kind: 'build', id: 'wall' }, tab: 'work' })).toEqual(['setTool({"kind":"select"})']);
    expect(run('escape', { tab: 'work' })).toEqual(['setTab(null)']);
    expect(run('escape')).toEqual(['setSelection({"bodies":[],"chests":[],"bench":null})']);
    const pick = vi.fn();
    expect(run('escape', { picking: { current: pick }, menu: {} })).toEqual(['setPickHint(false)']);
    expect(pick).toHaveBeenCalledWith(null);
    const stop = vi.fn();
    const e = key({ stopPropagation: stop });
    expect(run('escape', { menu: {}, map: { current: { cancelDrag: () => true } } }, e)).toEqual([]);
    expect(stop).toHaveBeenCalled();
  });

  it('Home focuses the selected body, else the colony home', () => {
    expect(run('focus', { selection: { bodies: ['a'], chests: [], bench: null } })).toEqual(['focusBody("a")']);
    expect(run('focus')).toEqual(['focus(10,20,64)']);
  });
});
