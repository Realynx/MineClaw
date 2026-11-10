/**
 * Placed redstone components under the mouse, with the mouse not locked: a plain left press on one selects it without
 * locking the view (the engine never sees that press) and may drag it; a press on empty space, a gizmo handle, another
 * button or a modifier is left alone. A drag previews the outline, its ports and the selection box in the overlay at most
 * once per frame (the canvas is untouched), snaps onto a port, stays red with the controller's reason on a refused spot,
 * commits one move (one undo step) on release, and Escape puts it back. The keys turn, mirror, remove and duplicate.
 */
import { MOCK_DESIGNER_PALETTE, type DesignerSession } from '@mineclaw/contracts';
import type { Pos } from '@mineclaw/redstone';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { setPlotMode, type Cell, type V3 } from '@/features/studio/canvas';
import { DRAG_THRESHOLD_PX } from '@/features/studio/components/dragSession';
import { blockedBefore, type PlacementRay } from '@/features/studio/components/levelRay';
import type { StorageLike } from '@/features/studio/redstone/componentData';
import { RedstoneComponentController } from '@/features/studio/redstone/RedstoneComponentController';
import {
  BUSY_MOVE,
  bindComponentPointer,
  componentKeyAction,
  RedstoneComponentDrag,
  type ComponentHit,
  type ComponentKeys,
  type ComponentPointer,
} from '@/features/studio/redstone/redstoneInput';
import { componentUnder } from '@/features/studio/redstone/useRedstoneComponents';
import { Studio, type StudioApi } from '@/features/studio/studio';

const DUST = 55;

const session = (over: Partial<DesignerSession> = {}): DesignerSession => ({
  id: 11,
  owner: 'poofimafox',
  model: 'main',
  designId: null,
  name: 'board',
  rev: 0,
  blocks: [],
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

const api: StudioApi = {
  message: () => Promise.reject(new Error('n/a')),
  canvas: () => Promise.resolve({ rev: 1, applied: 0 }),
  save: () => Promise.reject(new Error('n/a')),
};

function memoryStorage(): StorageLike {
  const map = new Map<string, string>();
  return { getItem: (k) => map.get(k) ?? null, setItem: (k, v) => void map.set(k, v) };
}

const cleanups: Array<() => void> = [];
afterEach(() => {
  for (const cleanup of cleanups.splice(0)) cleanup();
  Object.defineProperty(document, 'pointerLockElement', { configurable: true, value: null });
  setPlotMode({ kind: 'build' });
});

const sorted = (cells: Cell[]): Cell[] =>
  cells.sort((a, b) => a[1] - b[1] || a[2] - b[2] || a[0] - b[0] || a[3] - b[3]);

// ------------------------------------------------------------------ the press

describe('a press on the stage', () => {
  function mount(over: Partial<ComponentPointer> = {}, at: ComponentHit | null = component) {
    const stage = document.createElement('div');
    const surface = document.createElement('canvas');
    surface.tabIndex = 0;
    stage.append(surface);
    document.body.append(stage);
    // What comes after on the surface: the engine (whose unlocked left press locks the mouse) and the studio's rules.
    const engine = vi.fn();
    surface.addEventListener('pointerdown', engine);
    const pointer = {
      ready: vi.fn(() => true),
      selecting: vi.fn(() => false),
      hit: vi.fn((_at: { x: number; y: number } | null) => at),
      select: vi.fn(),
      press: vi.fn(),
      focus: vi.fn(),
      surface: vi.fn(() => surface),
      ...over,
    };
    const unbind = bindComponentPointer(stage, pointer);
    cleanups.push(() => {
      unbind();
      stage.remove();
    });
    const lock = () => Object.defineProperty(document, 'pointerLockElement', { configurable: true, value: surface });
    const down = (init: PointerEventInit = {}) => {
      const event = new PointerEvent('pointerdown', {
        bubbles: true,
        cancelable: true,
        pointerId: 3,
        button: 0,
        clientX: 40,
        clientY: 50,
        ...init,
      });
      surface.dispatchEvent(event);
      return event;
    };
    return { stage, surface, engine, pointer, unbind, lock, down };
  }
  const component: ComponentHit = { kind: 'component', id: 'c1', origin: [10, 0, 10], grab: [11, 0, 12] };

  it('on a component selects it and may drag it, and the view never sees it (so it does not lock)', () => {
    const h = mount();
    expect(h.down().defaultPrevented).toBe(true);
    expect(h.engine).not.toHaveBeenCalled();
    expect(h.pointer.hit).toHaveBeenCalledExactlyOnceWith({ x: 40, y: 50 });
    expect(h.pointer.select).toHaveBeenCalledExactlyOnceWith('c1');
    expect(h.pointer.focus).toHaveBeenCalledOnce();
    expect(h.pointer.press).toHaveBeenCalledExactlyOnceWith({
      pointerId: 3,
      x: 40,
      y: 50,
      id: 'c1',
      origin: [10, 0, 10],
      grab: [11, 0, 12],
    });
  });

  it('on a component it cannot be dragged from still selects it', () => {
    const h = mount({}, { ...component, grab: null });
    expect(h.down().defaultPrevented).toBe(true);
    expect(h.pointer.select).toHaveBeenCalledWith('c1');
    expect(h.pointer.press).not.toHaveBeenCalled();
  });

  it('on empty space or a gizmo handle goes on to the view: the fly view still locks on click', () => {
    for (const at of [null, { kind: 'handle' } as const]) {
      const h = mount({}, at);
      expect(h.down().defaultPrevented).toBe(false);
      expect(h.engine).toHaveBeenCalledOnce();
      expect(h.pointer.select).not.toHaveBeenCalled();
      expect(h.pointer.press).not.toHaveBeenCalled();
    }
  });

  it('leaves the other buttons, the modifiers and a live simulation to the view, without asking what is there', () => {
    const h = mount();
    for (const init of [
      { button: 1 },
      { button: 2 },
      { altKey: true },
      { ctrlKey: true },
      { metaKey: true },
      { shiftKey: true },
    ])
      expect(h.down(init).defaultPrevented).toBe(false);
    const live = mount({ ready: vi.fn(() => false) });
    expect(live.down().defaultPrevented).toBe(false);
    for (const x of [h, live]) {
      expect(x.pointer.hit).not.toHaveBeenCalled();
      expect(x.pointer.select).not.toHaveBeenCalled();
    }
    expect(h.engine).toHaveBeenCalledTimes(6);
    expect(live.engine).toHaveBeenCalledOnce();
  });

  it('with the mouse locked: the Select tool selects the component under the crosshair, and nothing drags', () => {
    const select = mount({ selecting: vi.fn(() => true) });
    select.lock();
    expect(select.down().defaultPrevented).toBe(true);
    expect(select.pointer.hit).toHaveBeenCalledExactlyOnceWith(null);
    expect(select.pointer.select).toHaveBeenCalledWith('c1');
    expect(select.pointer.press).not.toHaveBeenCalled();
    expect(select.pointer.focus).not.toHaveBeenCalled();

    // Another tool keeps its locked click (building, breaking) on a component's cells as before.
    const build = mount();
    build.lock();
    expect(build.down().defaultPrevented).toBe(false);
    expect(build.pointer.hit).not.toHaveBeenCalled();
    expect(build.engine).toHaveBeenCalledOnce();
  });

  it('leaves a press on a control drawn over the view (a toolbar, the hotbar) to that control', () => {
    const h = mount();
    const button = document.createElement('button');
    h.stage.append(button);
    const clicked = vi.fn();
    button.addEventListener('pointerdown', clicked);
    const event = new PointerEvent('pointerdown', { bubbles: true, cancelable: true, pointerId: 3, button: 0 });
    button.dispatchEvent(event);
    expect(event.defaultPrevented).toBe(false);
    expect(clicked).toHaveBeenCalledOnce();
    expect(h.pointer.hit).not.toHaveBeenCalled();
    expect(h.pointer.select).not.toHaveBeenCalled();
    expect(h.pointer.focus).not.toHaveBeenCalled();
    expect(h.pointer.press).not.toHaveBeenCalled();
    // Nor before the view is up.
    const early = mount({ surface: vi.fn(() => null) });
    expect(early.down().defaultPrevented).toBe(false);
    expect(early.pointer.select).not.toHaveBeenCalled();
  });

  it('is gone once unbound', () => {
    const h = mount();
    h.unbind();
    expect(h.down().defaultPrevented).toBe(false);
    expect(h.pointer.select).not.toHaveBeenCalled();
  });
});

// ------------------------------------------------------------------ the drag

/** A redstone design with two NOTs, a drag bound to its own window, frames run by hand, and the mouse looking down. */
function board() {
  const studio = new Studio(session(), api, 60_000);
  const controller = new RedstoneComponentController(studio, memoryStorage());
  cleanups.push(() => {
    controller.dispose();
    studio.dispose();
  });
  controller.place('gate.not', [10, 0, 10]);
  controller.place('gate.not', [30, 0, 30]);
  const overlay = {
    setMinePlacementGeometry: vi.fn((_positions: Float32Array | null, _at?: V3, _refused?: boolean) => undefined),
    setComponentPorts: vi.fn((_open: readonly Pos[] | null, _joined?: readonly Pos[]) => undefined),
    setMinePlacement: vi.fn(),
    setSelection: vi.fn(),
  };
  const frames: Array<() => void> = [];
  const deps = {
    refused: vi.fn(),
    moved: vi.fn(),
    rest: vi.fn(),
    notice: vi.fn(),
  };
  let editable = true;
  // Client (x, y) is straight above cell (x, y) of the plot: the ray meets any level there.
  const ray = (x: number, y: number): PlacementRay => ({
    origin: { x: x + 0.5, y: 300, z: y + 0.5 },
    direction: { x: 0, y: -1, z: 0 },
  });
  const drag = new RedstoneComponentDrag({
    controller,
    overlay,
    ray,
    canEdit: () => editable,
    ...deps,
    schedule: (run) => void frames.push(run),
  });
  const win = new EventTarget();
  drag.bind(win as unknown as Window);
  cleanups.push(() => drag.unbind());
  const pointer = (type: string, x: number, y: number) =>
    win.dispatchEvent(new PointerEvent(type, { clientX: x, clientY: y, pointerId: 1, cancelable: true }));
  const frame = () => {
    for (const run of frames.splice(0)) run();
  };
  /** A press on the second NOT at cell (31, 31), as the stage hands it over. */
  const grab = () => drag.press({ pointerId: 1, x: 31, y: 31, id: 'c2', origin: [30, 0, 30], grab: [31, 0, 31] });
  const blocks = () => sorted(studio.canvas.cells());
  return {
    studio,
    controller,
    overlay,
    deps,
    drag,
    frames,
    pointer,
    frame,
    grab,
    blocks,
    win,
    setEditable: (v: boolean) => (editable = v),
  };
}

describe('dragging a placed component', () => {
  it('stays a click under the threshold: nothing is shown and nothing moves', () => {
    const b = board();
    const before = b.blocks();
    b.grab();
    b.pointer('pointermove', 31 + DRAG_THRESHOLD_PX - 1, 31);
    b.pointer('pointerup', 31 + DRAG_THRESHOLD_PX - 1, 31);
    expect(b.frames).toHaveLength(0);
    expect(b.overlay.setMinePlacementGeometry).not.toHaveBeenCalled();
    expect(b.blocks()).toEqual(before);
    expect(b.studio.snapshot().undoLabel).toBe('place NOT');
  });

  it('previews once per frame, at the latest pointer position, in the overlay only', () => {
    const b = board();
    const before = b.blocks();
    b.grab();
    for (const x of [37, 38, 41, 45]) b.pointer('pointermove', x, 40);
    expect(b.frames).toHaveLength(1);
    expect(b.overlay.setMinePlacementGeometry).not.toHaveBeenCalled();
    b.frame();
    // The corner moves by what the pointer moved over the level: 14 east and 9 south of the press.
    const at: V3 = [44, 0, 39];
    expect(b.overlay.setMinePlacementGeometry).toHaveBeenCalledOnce();
    const [positions, shown, red] = b.overlay.setMinePlacementGeometry.mock.calls[0]!;
    expect(positions).toBeInstanceOf(Float32Array);
    expect(shown).toEqual(at);
    expect(red).toBe(false);
    expect(b.deps.refused).toHaveBeenLastCalledWith(null);
    expect(b.overlay.setComponentPorts).toHaveBeenCalledOnce();
    const box = b.controller.placedOf('c2')!.box;
    expect(b.overlay.setSelection).toHaveBeenLastCalledWith({
      min: at,
      max: [at[0] + box.max[0] - box.min[0], box.max[1] - box.min[1], at[2] + box.max[2] - box.min[2]],
    });
    // The outline was prepared once for the drag: the next frame only moves it.
    b.pointer('pointermove', 46, 40);
    b.frame();
    expect(b.overlay.setMinePlacementGeometry.mock.calls[1]![0]).toBe(positions);
    // Nothing reached the canvas, the undo list or the bot.
    expect(b.blocks()).toEqual(before);
    expect(b.studio.snapshot().undoLabel).toBe('place NOT');
  });

  it('commits one move on release: one undo step, the selection follows, and the overlay rests', () => {
    const b = board();
    b.grab();
    b.pointer('pointermove', 45, 40);
    b.frame();
    b.pointer('pointerup', 45, 40);
    expect(b.controller.snapshot().components.find((c) => c.id === 'c2')!.at).toEqual([44, 0, 39]);
    expect(b.deps.moved).toHaveBeenCalledExactlyOnceWith('c2');
    expect(b.overlay.setMinePlacement).toHaveBeenCalledWith(null);
    expect(b.deps.rest).toHaveBeenCalledOnce();
    expect(b.deps.refused).toHaveBeenLastCalledWith(null);
    expect(b.studio.snapshot().undoLabel).toBe('move NOT');
    b.studio.undo();
    expect(b.controller.snapshot().components.find((c) => c.id === 'c2')!.at).toEqual([30, 0, 30]);
    expect(b.studio.snapshot().undoLabel).toBe('place NOT');
  });

  it('puts the piece back on Escape, a lost pointer or a release where it began', () => {
    for (const end of ['escape', 'cancel', 'home'] as const) {
      const b = board();
      const before = b.blocks();
      b.grab();
      b.pointer('pointermove', 45, 40);
      b.frame();
      if (end === 'escape') {
        const key = new KeyboardEvent('keydown', { key: 'Escape', cancelable: true });
        b.win.dispatchEvent(key);
        expect(key.defaultPrevented).toBe(true);
      } else if (end === 'cancel') b.pointer('pointercancel', 45, 40);
      else {
        b.pointer('pointermove', 31, 31);
        b.frame();
        b.pointer('pointerup', 31, 31);
      }
      b.pointer('pointerup', 45, 40);
      expect(b.drag.active).toBe(false);
      expect(b.overlay.setMinePlacement).toHaveBeenLastCalledWith(null);
      expect(b.deps.rest).toHaveBeenCalledOnce();
      expect(b.deps.moved).not.toHaveBeenCalled();
      expect(b.blocks()).toEqual(before);
      expect(b.studio.snapshot().undoLabel).toBe('place NOT');
    }
  });

  it('shows a refused spot red with the reason, and its release moves nothing and says why', () => {
    const b = board();
    const before = b.blocks();
    b.grab();
    // On top of the first NOT (corner 10, 0, 10): too close, and no port join in reach.
    b.pointer('pointermove', 11, 11);
    b.frame();
    expect(b.overlay.setMinePlacementGeometry.mock.calls[0]![1]).toEqual([10, 0, 10]);
    const reason = b.controller.check('c2', [10, 0, 10]);
    expect(reason).toMatch(/would touch NOT/);
    expect(b.overlay.setMinePlacementGeometry.mock.calls[0]![2]).toBe(true);
    expect(b.deps.refused).toHaveBeenLastCalledWith(reason);
    b.pointer('pointerup', 11, 11);
    expect(b.deps.notice).toHaveBeenCalledExactlyOnceWith(reason);
    expect(b.deps.moved).not.toHaveBeenCalled();
    expect(b.blocks()).toEqual(before);
    expect(b.studio.snapshot().undoLabel).toBe('place NOT');
  });

  it('snaps its input onto the other NOT’s output, shows the port joined, and the move lays the joint’s dust', () => {
    const b = board();
    // The pointer asks for corner (15, 0, 11), a cell or so off the join east of the first NOT (output at x 13).
    const snap = b.controller.snapMoveCorner('c2', [15, 0, 11])!;
    expect(snap).not.toBeNull();
    expect(snap.at).not.toEqual([15, 0, 11]);
    const [jx, jy, jz] = snap.joint;
    expect(b.studio.canvas.id(jx, jy, jz)).not.toBe(DUST);
    b.grab();
    b.pointer('pointermove', 16, 12);
    b.frame();
    expect(b.overlay.setMinePlacementGeometry.mock.calls[0]![1]).toEqual(snap.at);
    expect(b.overlay.setMinePlacementGeometry.mock.calls[0]![2]).toBe(false);
    const [open, joined] = b.overlay.setComponentPorts.mock.calls[0]!;
    expect(joined).toContainEqual(snap.joint);
    expect(open).not.toContainEqual(snap.joint);
    b.pointer('pointerup', 16, 12);
    expect(b.controller.snapshot().components.find((c) => c.id === 'c2')!.at).toEqual(snap.at);
    expect(b.studio.canvas.id(jx, jy, jz)).toBe(DUST);
    expect(b.controller.portsOf('c2').some((p) => p.link?.to === 'port' && p.link.id === 'c1')).toBe(true);
    // The dust and the move are one step.
    b.studio.undo();
    expect(b.studio.canvas.id(jx, jy, jz)).not.toBe(DUST);
    expect(b.controller.snapshot().components.find((c) => c.id === 'c2')!.at).toEqual([30, 0, 30]);
  });

  it('turns and mirrors the piece in hand, and the release keeps the turn even where the drag began', () => {
    const b = board();
    expect(b.drag.turn(1)).toBe(false);
    b.grab();
    b.pointer('pointermove', 45, 40);
    b.frame();
    const first = b.overlay.setMinePlacementGeometry.mock.calls[0]![0];
    expect(b.drag.turn(1)).toBe(true);
    // Shown again at once, on the same corner, with the turned outline.
    expect(b.overlay.setMinePlacementGeometry).toHaveBeenCalledTimes(2);
    expect(b.overlay.setMinePlacementGeometry.mock.calls[1]![1]).toEqual([44, 0, 39]);
    expect(b.overlay.setMinePlacementGeometry.mock.calls[1]![0]).not.toBe(first);
    expect(b.drag.mirror('x')).toBe(true);
    b.pointer('pointermove', 31, 31);
    b.frame();
    b.pointer('pointerup', 31, 31);
    const c2 = b.controller.snapshot().components.find((c) => c.id === 'c2')!;
    expect(c2).toMatchObject({ at: [30, 0, 30], turns: 1, mirror: 'z' });
    expect(b.studio.snapshot().undoLabel).toBe('move NOT');
    b.studio.undo();
    expect(b.controller.snapshot().components.find((c) => c.id === 'c2')).toMatchObject({ turns: 0, mirror: null });
  });

  it('moves nothing when editing stopped during the drag (a simulation started, the page went busy)', () => {
    const b = board();
    const before = b.blocks();
    b.grab();
    b.pointer('pointermove', 45, 40);
    b.frame();
    b.setEditable(false);
    b.pointer('pointerup', 45, 40);
    expect(b.blocks()).toEqual(before);
    expect(b.deps.moved).not.toHaveBeenCalled();
    // The piece went back, and the user hears why.
    expect(b.deps.notice).toHaveBeenCalledExactlyOnceWith(BUSY_MOVE);

    // And a press does not even start a drag while editing is not allowed.
    b.grab();
    b.pointer('pointermove', 45, 40);
    expect(b.drag.moved).toBe(false);
    expect(b.frames).toHaveLength(0);
  });
});

describe('which component a press picks', () => {
  const COBBLE = 4;
  /** Straight down onto cell (x, z) from high above the plot. */
  const down = (x: number, z: number): PlacementRay => ({
    origin: { x: x + 0.5, y: 300, z: z + 0.5 },
    direction: { x: 0, y: -1, z: 0 },
  });

  it('is the box the ray meets first, unless a block outside it stands in front', () => {
    const b = board();
    const solid = (x: number, y: number, z: number) => b.studio.canvas.id(x, y, z) !== 0;
    expect(componentUnder(down(31, 31), b.controller, solid)?.id).toBe('c2');
    expect(componentUnder(down(20, 20), b.controller, solid)).toBeNull();
    // A loose block above the gate: the press is that block's, and goes on to the view.
    b.studio.edit([[31, 5, 31, COBBLE, 0]], 'place', 'cobble');
    expect(componentUnder(down(31, 31), b.controller, solid)).toBeNull();
    // Beside the ray it hides nothing; the ground under the gate is behind it.
    expect(componentUnder(down(30, 30), b.controller, solid)?.id).toBe('c2');
    expect(componentUnder(down(11, 11), b.controller, solid)?.id).toBe('c1');
  });

  it('walks only the cells the ray crosses before the distance given', () => {
    // A redstone plot (canvas y 0 is world 64).
    board();
    const seen: string[] = [];
    const solid = (x: number, y: number, z: number) => {
      seen.push(`${x},${y},${z}`);
      return x === 5 && y === 0 && z === 5;
    };
    // Along +x on level 0 of row z 5, from outside the plot: the block at x 5 lies before 10 cells in, not before 4.
    const east: PlacementRay = { origin: { x: -3.5, y: 64.5, z: 5.5 }, direction: { x: 1, y: 0, z: 0 } };
    expect(blockedBefore(east, 13, solid)).toBe(true);
    expect(seen).toEqual(['0,0,5', '1,0,5', '2,0,5', '3,0,5', '4,0,5', '5,0,5']);
    expect(blockedBefore(east, 8, solid)).toBe(false);
    // Missing the plot, or meeting it only after the distance, walks nothing.
    seen.length = 0;
    expect(blockedBefore({ origin: { x: -3.5, y: 64.5, z: 5.5 }, direction: { x: -1, y: 0, z: 0 } }, 50, solid)).toBe(
      false,
    );
    expect(blockedBefore(east, 2, solid)).toBe(false);
    expect(seen).toEqual([]);
  });
});

describe('dragging a selected component by a gizmo handle', () => {
  const atOf = (b: ReturnType<typeof board>, id: string) =>
    b.controller.snapshot().components.find((c) => c.id === id)!.at;

  it('previews each step in the overlay only, and the release is one move and one undo step', () => {
    const b = board();
    const before = b.blocks();
    const move = vi.spyOn(b.controller, 'move');
    expect(b.drag.gizmoStart('c2')).toBe(true);
    expect(b.drag.holdingGizmo).toBe(true);
    for (const n of [1, 2, 3, 4, 5]) expect(b.drag.gizmoPreview([n, 0, 0])).toBe(true);
    expect(b.overlay.setMinePlacementGeometry).toHaveBeenLastCalledWith(expect.any(Float32Array), [35, 0, 30], false);
    // The outline was worked out once: each step only moves it.
    const calls = b.overlay.setMinePlacementGeometry.mock.calls;
    expect(calls[4]![0]).toBe(calls[0]![0]);
    expect(b.overlay.setComponentPorts).toHaveBeenCalledTimes(5);
    // Nothing reached the canvas, the undo list or the bot while the handle was held.
    expect(move).not.toHaveBeenCalled();
    expect(b.blocks()).toEqual(before);
    expect(b.studio.snapshot().undoLabel).toBe('place NOT');
    b.drag.gizmoCommit([5, 0, 0]);
    expect(move).toHaveBeenCalledOnce();
    expect(atOf(b, 'c2')).toEqual([35, 0, 30]);
    expect(b.deps.moved).toHaveBeenCalledExactlyOnceWith('c2');
    expect(b.deps.rest).toHaveBeenCalledOnce();
    expect(b.drag.holdingGizmo).toBe(false);
    expect(b.studio.snapshot().undoLabel).toBe('move NOT');
    b.studio.undo();
    expect(atOf(b, 'c2')).toEqual([30, 0, 30]);
    expect(b.studio.snapshot().undoLabel).toBe('place NOT');
  });

  it('shows a refused step red with the reason and moves nothing for it; its release says why', () => {
    const b = board();
    const before = b.blocks();
    const move = vi.spyOn(b.controller, 'move');
    b.drag.gizmoStart('c2');
    // Onto the first NOT (corner 10, 0, 10).
    const reason = b.controller.check('c2', [10, 0, 10]);
    expect(reason).toMatch(/would touch NOT/);
    for (let i = 0; i < 3; i++) expect(b.drag.gizmoPreview([-20, 0, -20])).toBe(true);
    expect(b.overlay.setMinePlacementGeometry).toHaveBeenLastCalledWith(expect.any(Float32Array), [10, 0, 10], true);
    expect(b.deps.refused).toHaveBeenLastCalledWith(reason);
    expect(b.deps.notice).not.toHaveBeenCalled();
    b.drag.gizmoCommit([-20, 0, -20]);
    expect(b.deps.notice).toHaveBeenCalledExactlyOnceWith(reason);
    expect(move).not.toHaveBeenCalled();
    expect(b.blocks()).toEqual(before);
    expect(b.deps.refused).toHaveBeenLastCalledWith(null);
  });

  it('puts the piece back on cancel or a release where it began, and holds nothing while editing is not allowed', () => {
    const b = board();
    const before = b.blocks();
    b.drag.gizmoStart('c2');
    b.drag.gizmoPreview([2, 0, 0]);
    b.drag.gizmoCancel();
    expect(b.drag.holdingGizmo).toBe(false);
    expect(b.overlay.setMinePlacement).toHaveBeenLastCalledWith(null);
    expect(b.deps.rest).toHaveBeenCalledOnce();
    expect(b.drag.gizmoPreview([3, 0, 0])).toBe(false);

    b.drag.gizmoStart('c2');
    b.drag.gizmoPreview([2, 0, 0]);
    b.drag.gizmoCommit([0, 0, 0]);
    expect(b.drag.holdingGizmo).toBe(false);
    // A simulation starting (the page cancels every drag) lets go of a held handle too.
    b.drag.gizmoStart('c2');
    b.drag.cancel();
    expect(b.drag.holdingGizmo).toBe(false);
    expect(b.blocks()).toEqual(before);
    expect(b.studio.snapshot().undoLabel).toBe('place NOT');
    expect(b.deps.moved).not.toHaveBeenCalled();

    b.setEditable(false);
    expect(b.drag.gizmoStart('c2')).toBe(false);
    expect(b.drag.gizmoStart('nope')).toBe(false);
  });

  it('says why when editing stopped while the handle was held', () => {
    const b = board();
    b.drag.gizmoStart('c2');
    b.drag.gizmoPreview([4, 0, 0]);
    b.setEditable(false);
    b.drag.gizmoCommit([4, 0, 0]);
    expect(atOf(b, 'c2')).toEqual([30, 0, 30]);
    expect(b.deps.notice).toHaveBeenCalledExactlyOnceWith(BUSY_MOVE);
  });

  it('keeps the piece a handle holds when a mouse press would start a drag of its body', () => {
    const b = board();
    b.drag.gizmoStart('c2');
    b.grab();
    b.pointer('pointermove', 45, 40);
    expect(b.drag.moved).toBe(false);
    expect(b.drag.holdingGizmo).toBe(true);
  });
});

// ------------------------------------------------------------------ the keys

describe('the keys on a selected component', () => {
  function keys(over: Partial<ComponentKeys> = {}, dragging = false) {
    const k = {
      selected: vi.fn((): string | null => 'c1'),
      canEdit: vi.fn(() => true),
      inWorld: vi.fn(() => true),
      drag: {
        moved: dragging,
        cancel: vi.fn(),
        turn: vi.fn((_by: number) => true),
        mirror: vi.fn((_axis: 'x' | 'z') => true),
      },
      turn: vi.fn((_id: string, _by: number) => undefined),
      mirror: vi.fn((_id: string, _axis: 'x' | 'z') => undefined),
      remove: vi.fn((_id: string) => undefined),
      duplicate: vi.fn((_id: string) => undefined),
    };
    const run = (init: KeyboardEventInit) => {
      const act = componentKeyAction(new KeyboardEvent('keydown', init), { ...k, ...over });
      act?.();
      return act;
    };
    return { k, run };
  }

  it('R and ] turn it clockwise, Shift+R and [ counter-clockwise, M mirrors it across x and Shift+M across z', () => {
    const { k, run } = keys();
    run({ code: 'KeyR', key: 'r' });
    run({ key: ']', code: 'BracketRight' });
    run({ code: 'KeyR', key: 'R', shiftKey: true });
    run({ key: '[', code: 'BracketLeft' });
    expect(k.turn.mock.calls).toEqual([
      ['c1', 1],
      ['c1', 1],
      ['c1', 3],
      ['c1', 3],
    ]);
    run({ code: 'KeyM', key: 'm' });
    run({ code: 'KeyM', key: 'M', shiftKey: true });
    expect(k.mirror.mock.calls).toEqual([
      ['c1', 'x'],
      ['c1', 'z'],
    ]);
  });

  it('Delete removes it and Ctrl+D duplicates it', () => {
    const { k, run } = keys();
    run({ key: 'Delete' });
    run({ key: 'Backspace' });
    run({ key: 'd', ctrlKey: true });
    run({ key: 'd', metaKey: true });
    expect(k.remove.mock.calls).toEqual([['c1'], ['c1']]);
    expect(k.duplicate.mock.calls).toEqual([['c1'], ['c1']]);
  });

  it('while dragging, the turn and mirror keys change the piece in hand, Delete puts it back first, Escape cancels', () => {
    const { k, run } = keys({}, true);
    run({ code: 'KeyR', key: 'r' });
    run({ key: '[' });
    run({ code: 'KeyM', key: 'm' });
    expect(k.drag.turn.mock.calls).toEqual([[1], [3]]);
    expect(k.drag.mirror).toHaveBeenCalledExactlyOnceWith('x');
    expect(k.turn).not.toHaveBeenCalled();
    expect(k.mirror).not.toHaveBeenCalled();
    run({ key: 'Delete' });
    expect(k.drag.cancel).toHaveBeenCalledOnce();
    expect(k.remove).toHaveBeenCalledWith('c1');
    expect(run({ key: 'Escape' })).not.toBeNull();
    expect(k.drag.cancel).toHaveBeenCalledTimes(2);
  });

  it('leaves other keys, Escape without a drag, typing, the focus elsewhere and no selection to the page', () => {
    expect(keys().run({ key: 'a', code: 'KeyA' })).toBeNull();
    expect(keys().run({ key: 'Escape' })).toBeNull();
    expect(keys().run({ key: 'z', ctrlKey: true })).toBeNull();
    expect(keys().run({ code: 'KeyR', key: 'r', altKey: true })).toBeNull();
    expect(keys({ inWorld: vi.fn(() => false) }).run({ code: 'KeyR', key: 'r' })).toBeNull();
    expect(keys({ selected: vi.fn(() => null) }).run({ key: 'Delete' })).toBeNull();
    const input = document.createElement('input');
    document.body.append(input);
    cleanups.push(() => input.remove());
    const typing = keys();
    const seen = vi.fn((e: KeyboardEvent) => componentKeyAction(e, typing.k));
    input.addEventListener('keydown', seen);
    input.dispatchEvent(new KeyboardEvent('keydown', { key: 'Delete', bubbles: true }));
    expect(seen).toHaveReturnedWith(null);
  });

  it('takes the key and does nothing while editing is not allowed, so the page never edits the cells as raw ones', () => {
    const { k, run } = keys({ canEdit: vi.fn(() => false) });
    for (const init of [{ code: 'KeyR', key: 'r' }, { key: 'Delete' }, { key: 'd', ctrlKey: true }, { code: 'KeyM' }])
      expect(run(init)).not.toBeNull();
    for (const fn of [k.turn, k.mirror, k.remove, k.duplicate]) expect(fn).not.toHaveBeenCalled();
  });

  it('on a real controller: a turn, a mirror, a copy beside it and a removal are one undo step each', () => {
    const b = board();
    let selected: string | null = 'c1';
    const act = (init: KeyboardEventInit) =>
      componentKeyAction(new KeyboardEvent('keydown', init), {
        selected: () => selected,
        canEdit: () => true,
        inWorld: () => true,
        drag: b.drag,
        turn: (id, by) => void b.controller.turn(id, by),
        mirror: (id, axis) => void b.controller.mirror(id, axis),
        remove: (id) => void b.controller.remove(id),
        duplicate: (id) => {
          selected = b.controller.duplicate(id)?.id ?? selected;
        },
      })?.();
    act({ code: 'KeyR', key: 'r' });
    expect(b.controller.snapshot().components[0]).toMatchObject({ id: 'c1', turns: 1 });
    expect(b.studio.snapshot().undoLabel).toBe('turn NOT');
    act({ code: 'KeyM', key: 'm' });
    expect(b.studio.snapshot().undoLabel).toBe('mirror NOT');
    act({ key: 'd', ctrlKey: true });
    expect(selected).toBe('c3');
    expect(b.controller.snapshot().components).toHaveLength(3);
    act({ key: 'Delete' });
    expect(b.controller.snapshot().components.map((c) => c.id)).toEqual(['c1', 'c2']);
    b.studio.undo();
    expect(b.controller.snapshot().components).toHaveLength(3);
  });
});
