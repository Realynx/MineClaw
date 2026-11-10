/**
 * The redstone studio's input rules: what the Configure tool sets on a part (one undoable edit each, in a redstone design
 * only), key C, the cell the aim is on, and the pointer while a simulation is live (the click is the circuit's, and the
 * studio's own tools never see it).
 */
import { MOCK_DESIGNER_PALETTE, type DesignerCanvasRequest, type DesignerSession } from '@mineclaw/contracts';
import type { Pos } from '@mineclaw/redstone';
import type { AimTarget } from '@mineclaw/world-viewer';
import { PerspectiveCamera, Raycaster, Vector2, Vector3 } from 'three';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { setPlotMode, type Cell } from '@/features/studio/canvas';
import { Overlay3D } from '@/features/studio/overlay3d';
import { aimedCell, bindSimPointer, configured, type SimPointer } from '@/features/studio/redstone/redstoneInput';
import { Studio, type StudioApi } from '@/features/studio/studio';
import { studioKey, studioPress, type InputState, type KeyActions, type PressCtx } from '@/features/studio/studioInput';
import { bindStudioPointer, type StudioPointerActions } from '@/features/studio/studioPointer';
import type { Hit } from '@/features/studio/tools';

const REPEATER: Cell = [3, 0, 3, 93, 1];
const COMPARATOR: Cell = [4, 0, 3, 149, 2];
const LEVER: Cell = [5, 0, 3, 69, 5];
const STONE: Cell = [6, 0, 3, 1, 0];

const session = (over: Partial<DesignerSession> = {}): DesignerSession => ({
  id: 7,
  owner: 'poofimafox',
  model: 'main',
  designId: null,
  name: 'clock',
  rev: 0,
  blocks: [REPEATER, COMPARATOR, LEVER, STONE],
  anchor: null,
  messages: [],
  costUsd: 0,
  calls: 0,
  version: '1.12.2',
  palette: MOCK_DESIGNER_PALETTE,
  plot: { size: 64, height: 48, depth: 16 },
  revisions: [],
  kind: 'redstone',
  ...over,
});

const studios: Studio[] = [];
const cleanups: Array<() => void> = [];
afterEach(() => {
  for (const s of studios.splice(0)) s.dispose();
  for (const cleanup of cleanups.splice(0)) cleanup();
  Object.defineProperty(document, 'pointerLockElement', { configurable: true, value: null });
  setPlotMode({ kind: 'build' });
});

function open(over: Partial<DesignerSession> = {}) {
  const canvas = vi.fn((_id: number, _body: DesignerCanvasRequest) => Promise.resolve({ rev: 1, applied: 1 }));
  const api: StudioApi = { canvas, message: vi.fn(), save: vi.fn() };
  const studio = new Studio(session(over), api, 60_000);
  studios.push(studio);
  return { studio, canvas };
}

const state = (tool: InputState['tool']): InputState => ({
  tool,
  boxMode: 'fill',
  brushShape: 'sphere',
  brushSize: 3,
  brushMode: 'add',
  extrudeN: 1,
  slots: [],
  active: 0,
  clip: null,
  pasting: false,
  drag: null,
  version: '1.12.2',
  under: null,
});

/** The aim on a block's top face. */
const on = (c: Cell): Hit => ({ hit: [c[0], c[1], c[2]], place: [c[0], c[1] + 1, c[2]], face: 1 });

/** A press with the Configure tool, as the page hands it to the input rules. */
function press(studio: Studio, h: Hit, button: number) {
  const ctx = {
    studio,
    h,
    button,
    mods: { shift: false, ctrl: false },
    s: state('configure'),
    setDrag: vi.fn(),
    setPasting: vi.fn(),
    pick: vi.fn(),
    placeAt: vi.fn(),
    startBrush: vi.fn(),
    setNotice: vi.fn(),
    inspect: vi.fn(),
  } satisfies PressCtx;
  studioPress(ctx);
  return ctx;
}

describe('what the Configure tool makes of a block', () => {
  it('takes a repeater through its four delays and keeps the way it faces', () => {
    for (const facing of [0, 1, 2, 3]) {
      const metas = [facing];
      for (let i = 0; i < 4; i++) metas.push(configured(93, metas.at(-1)!)!.meta);
      expect(metas).toEqual([facing, facing | 4, facing | 8, facing | 12, facing]);
    }
    expect(configured(93, 1)!.label).toBe('set a repeater to 2 ticks');
    expect(configured(93, 13)!.label).toBe('set a repeater to 1 tick');
    expect(configured(94, 6)).toEqual({ meta: 10, label: 'set a repeater to 3 ticks' });
  });

  it('switches a comparator between compare and subtract, and a lever between off and on', () => {
    expect(configured(149, 2)).toEqual({ meta: 6, label: 'set a comparator to subtract' });
    expect(configured(149, 6)).toEqual({ meta: 2, label: 'set a comparator to compare' });
    expect(configured(150, 11)!.meta).toBe(15);
    expect(configured(69, 5)).toEqual({ meta: 13, label: 'switched a lever on' });
    expect(configured(69, 13)).toEqual({ meta: 5, label: 'switched a lever off' });
  });

  it('has nothing to set on anything else', () => {
    for (const [id, meta] of [
      [55, 0],
      [76, 5],
      [1, 0],
      [77, 1],
      [33, 1],
      [0, 0],
      [-1, 0],
    ] as const)
      expect(configured(id, meta)).toBeNull();
  });
});

describe('the Configure tool', () => {
  it('sets a part with one right click: one undo step each, and the bot hears of it like any edit', async () => {
    const { studio, canvas } = open();
    press(studio, on(REPEATER), 2);
    expect(studio.canvas.get(3, 0, 3)).toEqual([93, 5]);
    expect(studio.snapshot()).toMatchObject({ canUndo: true, undoLabel: 'set a repeater to 2 ticks' });
    press(studio, on(REPEATER), 2);
    press(studio, on(COMPARATOR), 2);
    press(studio, on(LEVER), 2);
    expect(studio.canvas.get(3, 0, 3)).toEqual([93, 9]);
    expect(studio.canvas.get(4, 0, 3)).toEqual([149, 6]);
    expect(studio.canvas.get(5, 0, 3)).toEqual([69, 13]);

    await studio.flush();
    expect(canvas.mock.calls[0]![1].ops).toEqual([
      { kind: 'configure', label: 'set a repeater to 2 ticks', cells: [[3, 0, 3, 93, 5]] },
      { kind: 'configure', label: 'set a repeater to 3 ticks', cells: [[3, 0, 3, 93, 9]] },
      { kind: 'configure', label: 'set a comparator to subtract', cells: [[4, 0, 3, 149, 6]] },
      { kind: 'configure', label: 'switched a lever on', cells: [[5, 0, 3, 69, 13]] },
    ]);

    // Four clicks, four steps back, each undoing exactly one of them.
    studio.undo();
    expect(studio.canvas.get(5, 0, 3)).toEqual([69, 5]);
    studio.undo();
    expect(studio.canvas.get(4, 0, 3)).toEqual([149, 2]);
    studio.undo();
    expect(studio.canvas.get(3, 0, 3)).toEqual([93, 5]);
    studio.undo();
    expect(studio.canvas.get(3, 0, 3)).toEqual([93, 1]);
    expect(studio.snapshot().canUndo).toBe(false);
    studio.redo();
    expect(studio.canvas.get(3, 0, 3)).toEqual([93, 5]);
    expect(studio.canvas.size).toBe(4);
  });

  it('says so when the block has nothing to set, and changes nothing', () => {
    const { studio } = open();
    const stone = press(studio, on(STONE), 2);
    expect(stone.setNotice).toHaveBeenCalledWith(expect.stringContaining('a repeater, a comparator or a lever'));
    // The ground and the open air have no block to set either.
    const ground = press(studio, { hit: null, place: [9, 0, 9], face: 1 }, 2);
    expect(ground.setNotice).not.toHaveBeenCalled();
    expect(studio.snapshot()).toMatchObject({ canUndo: false, pending: 0 });
  });

  it('shows a cell in the inspector with the left button, the ground under the aim too', () => {
    const { studio } = open();
    expect(press(studio, on(REPEATER), 0).inspect).toHaveBeenCalledWith([3, 0, 3]);
    expect(press(studio, { hit: null, place: [9, 0, 9], face: 1 }, 0).inspect).toHaveBeenCalledWith([9, -1, 9]);
    expect(press(studio, { hit: null, place: null, face: 1 }, 0).inspect).not.toHaveBeenCalled();
    expect(studio.snapshot().canUndo).toBe(false);
    // The middle button picks the block, whatever the tool.
    const middle = press(studio, on(REPEATER), 1);
    expect(middle.pick).toHaveBeenCalledTimes(1);
    expect(middle.inspect).not.toHaveBeenCalled();
  });

  it('does nothing in a design that is not a redstone one', () => {
    const { studio } = open({ kind: 'build' });
    const right = press(studio, on(REPEATER), 2);
    const left = press(studio, on(REPEATER), 0);
    expect(studio.canvas.get(3, 0, 3)).toEqual([93, 1]);
    expect(studio.snapshot().canUndo).toBe(false);
    expect(right.setNotice).not.toHaveBeenCalled();
    expect(left.inspect).not.toHaveBeenCalled();
  });

  it('is chosen with key C, and Ctrl+C still copies', () => {
    const { studio } = open();
    const setTool = vi.fn();
    const doCopy = vi.fn(() => null);
    const actions = {
      studio,
      state: () => state('build'),
      inWorld: () => true,
      setTool,
      doCopy,
    } as unknown as KeyActions;
    studioKey(new KeyboardEvent('keydown', { code: 'KeyC', key: 'c' }), actions);
    expect(setTool).toHaveBeenCalledExactlyOnceWith('configure');
    studioKey(new KeyboardEvent('keydown', { code: 'KeyC', key: 'c', ctrlKey: true }), actions);
    expect(setTool).toHaveBeenCalledTimes(1);
    expect(doCopy).toHaveBeenCalledTimes(1);
  });
});

describe('the cell the aim is on', () => {
  const block = (x: number, y: number, z: number): AimTarget => ({
    kind: 'block',
    x,
    y,
    z,
    id: 1,
    meta: 0,
    face: 1,
    name: 'stone',
    distance: 4,
  });

  it('is in canvas coordinates, counts the ground, and is nothing outside the plot', () => {
    expect(aimedCell(block(5, 64, 6))).toEqual([5, 0, 6]);
    expect(aimedCell(block(5, 63, 6))).toEqual([5, -1, 6]);
    expect(aimedCell(block(-1, 64, 6))).toBeNull();
    expect(aimedCell(block(5, 200, 6))).toBeNull();
    expect(aimedCell({ kind: 'bot', name: 'alice', x: 5, y: 64, z: 6, distance: 2 })).toBeNull();
    expect(aimedCell(null)).toBeNull();
  });
});

describe('the pointer while a simulation is live', () => {
  const CELL: Pos = [3, 0, 3];
  /** The aim is worked out in a few promise steps (no timers): this lets them all run. */
  const settled = async () => {
    for (let i = 0; i < 10; i++) await Promise.resolve();
  };

  function mount(over: Partial<SimPointer> = {}) {
    const stage = document.createElement('div');
    const surface = document.createElement('canvas');
    surface.tabIndex = 0;
    stage.append(surface);
    document.body.append(stage);
    // What comes after on the surface: the studio's own pointer rules and the viewer.
    const studioTools = vi.fn();
    surface.addEventListener('pointerdown', studioTools, true);
    const pointer = {
      live: vi.fn(() => true),
      aim: vi.fn(() => Promise.resolve<Pos | null>(CELL)),
      click: vi.fn((_at: Pos) => true),
      inspect: vi.fn(),
      ...over,
    };
    const unbind = bindSimPointer(stage, pointer);
    cleanups.push(() => {
      unbind();
      stage.remove();
    });
    const lock = (locked = true) =>
      Object.defineProperty(document, 'pointerLockElement', { configurable: true, value: locked ? surface : null });
    const down = (button = 0) => {
      const event = new PointerEvent('pointerdown', { bubbles: true, cancelable: true, pointerId: 1, button });
      surface.dispatchEvent(event);
      return event;
    };
    return { stage, surface, studioTools, pointer, unbind, lock, down };
  }

  it('gives a locked click to the circuit: the input under the crosshair is used, and the studio never sees it', async () => {
    const h = mount();
    h.lock();
    for (const button of [0, 2]) expect(h.down(button).defaultPrevented).toBe(true);
    await settled();
    expect(vi.mocked(h.pointer.click).mock.calls).toEqual([[CELL], [CELL]]);
    expect(h.pointer.inspect).not.toHaveBeenCalled();
    expect(h.studioTools).not.toHaveBeenCalled();
  });

  it('shows a block that is not an input in the inspector, and any block with the middle button', async () => {
    const h = mount({ click: vi.fn(() => false) });
    h.lock();
    h.down(2);
    await settled();
    expect(h.pointer.click).toHaveBeenCalledWith(CELL);
    expect(h.pointer.inspect).toHaveBeenCalledExactlyOnceWith(CELL);

    const input = mount();
    input.lock();
    expect(input.down(1).defaultPrevented).toBe(true);
    await settled();
    expect(input.pointer.click).not.toHaveBeenCalled();
    expect(input.pointer.inspect).toHaveBeenCalledExactlyOnceWith(CELL);
  });

  it('leaves the click alone in edit mode, with the mouse not locked, and for the extra buttons', async () => {
    const editing = mount({ live: vi.fn(() => false) });
    editing.lock();
    expect(editing.down().defaultPrevented).toBe(false);
    expect(editing.studioTools).toHaveBeenCalledTimes(1);

    // Not locked: the click is the one that locks the mouse, as when building.
    const unlocked = mount();
    unlocked.lock(false);
    expect(unlocked.down().defaultPrevented).toBe(false);
    expect(unlocked.studioTools).toHaveBeenCalledTimes(1);

    const side = mount();
    side.lock();
    expect(side.down(3).defaultPrevented).toBe(false);
    expect(side.studioTools).toHaveBeenCalledTimes(1);

    await settled();
    for (const h of [editing, unlocked, side]) {
      expect(h.pointer.aim).not.toHaveBeenCalled();
      expect(h.pointer.click).not.toHaveBeenCalled();
      expect(h.pointer.inspect).not.toHaveBeenCalled();
    }
  });

  it('does nothing with a click at nothing, or when the simulation ended while the aim was worked out', async () => {
    const nothing = mount({ aim: vi.fn(() => Promise.resolve(null)) });
    nothing.lock();
    expect(nothing.down().defaultPrevented).toBe(true);

    let live = true;
    const ended = mount({ live: vi.fn(() => live) });
    ended.lock();
    ended.down();
    live = false;

    const lost = mount({ aim: vi.fn(() => Promise.reject(new Error('the view is gone'))) });
    lost.lock();
    lost.down();

    await settled();
    for (const h of [nothing, ended, lost]) {
      expect(h.pointer.click).not.toHaveBeenCalled();
      expect(h.pointer.inspect).not.toHaveBeenCalled();
    }
  });

  it('is gone once unbound', async () => {
    const h = mount();
    h.lock();
    h.unbind();
    expect(h.down().defaultPrevented).toBe(false);
    await settled();
    expect(h.pointer.click).not.toHaveBeenCalled();
    expect(h.studioTools).toHaveBeenCalledTimes(1);
  });

  it('beside the studio’s own pointer rules: they edit until a simulation is live, then only the circuit is clicked', async () => {
    let live = false;
    const h = mount({ live: vi.fn(() => live) });
    const overlay = new Overlay3D(64);
    const actions: StudioPointerActions = {
      canEdit: true,
      down: vi.fn(() => Promise.resolve()),
      up: vi.fn(() => Promise.resolve()),
      cancel: vi.fn(),
      move: vi.fn(),
      setDrag: vi.fn(),
      error: vi.fn(),
    };
    const unbind = bindStudioPointer({
      surface: h.surface,
      camera: new PerspectiveCamera(60, 4 / 3, 0.1, 300),
      studio: { selection: null } as unknown as Studio,
      overlay,
      mod: { Vector2, Vector3, Raycaster },
      mine: false,
      state: () => state('build'),
      // The page folds the simulation into what the pointer may edit.
      actions: () => ({ ...actions, canEdit: !live }),
    });
    cleanups.push(() => {
      unbind();
      overlay.dispose();
    });
    const release = () =>
      window.dispatchEvent(new PointerEvent('pointerup', { bubbles: true, cancelable: true, pointerId: 1, button: 2 }));
    h.lock();

    h.down(2);
    release();
    await settled();
    expect(actions.down).toHaveBeenCalledTimes(1);
    expect(actions.up).toHaveBeenCalledTimes(1);
    expect(h.pointer.click).not.toHaveBeenCalled();

    live = true;
    expect(h.down(2).defaultPrevented).toBe(true);
    release();
    await settled();
    expect(actions.down).toHaveBeenCalledTimes(1);
    expect(actions.up).toHaveBeenCalledTimes(1);
    expect(h.pointer.click).toHaveBeenCalledExactlyOnceWith(CELL);
  });
});
