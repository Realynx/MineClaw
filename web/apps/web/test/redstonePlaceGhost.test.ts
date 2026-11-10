/**
 * Placing a Library component with the mouse not locked: the controller's `previewPlace` (what a placement would do,
 * writing nothing) and the ghost bound on the stage. The ghost's outline and ports follow the mouse at most once per
 * frame in the overlay only, red with the reason where the placement would be refused; a plain left click places it
 * (one undo step) without the view seeing the press, a right click or Escape stops placing, and the mouse locked, the
 * middle button or a modifier leave the press to the view.
 */
import { MOCK_DESIGNER_PALETTE, type DesignerSession } from '@mineclaw/contracts';
import type { Pos } from '@mineclaw/redstone';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { setPlotMode, type Cell, type V3 } from '@/features/studio/canvas';
import type { PlacementRay } from '@/features/studio/components/levelRay';
import type { PortArrow } from '@/features/studio/overlay3d';
import type { StorageLike } from '@/features/studio/redstone/componentData';
import { RedstoneComponentController } from '@/features/studio/redstone/RedstoneComponentController';
import { bindPlaceGhost, type PlaceRequest } from '@/features/studio/redstone/redstoneInput';
import { Studio, type StudioApi } from '@/features/studio/studio';

const DUST = 55;
const NO_OUTLET = 'No output port near there to join: place it by its corner instead.';

const session = (): DesignerSession => ({
  id: 12,
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

/** A redstone design with one NOT at (10, 0, 10): input west at x 9, output east at (13, 0, 11). */
function board() {
  const studio = new Studio(session(), api, 60_000);
  const controller = new RedstoneComponentController(studio, memoryStorage());
  cleanups.push(() => {
    controller.dispose();
    studio.dispose();
  });
  controller.place('gate.not', [10, 0, 10]);
  const blocks = () => sorted(studio.canvas.cells());
  return { studio, controller, blocks };
}

describe('previewPlace', () => {
  it('says where a placement would go and that it may, and writes nothing', () => {
    const b = board();
    const before = b.blocks();
    const p = b.controller.previewPlace('gate.not', [30, 0, 30])!;
    expect(p).toMatchObject({ at: [30, 0, 30], joint: null, reason: null });
    // A NOT's two ports, both open out there.
    expect(p.ports.joined).toEqual([]);
    expect(p.ports.open).toHaveLength(2);
    expect(b.blocks()).toEqual(before);
    expect(b.studio.snapshot().undoLabel).toBe('place NOT');
    expect(b.controller.snapshot().components).toHaveLength(1);
  });

  it('gives place’s own reason where it would be refused, and place still refuses with it', () => {
    const b = board();
    const close = b.controller.previewPlace('gate.not', [12, 0, 12])!;
    expect(close.reason).toMatch(/would touch NOT: keep one free cell between components/);
    expect(b.controller.place('gate.not', [12, 0, 12])).toBeNull();
    expect(b.controller.snapshot().error).toBe(close.reason);
    const off = b.controller.previewPlace('gate.not', [63, 0, 63])!;
    expect(off.reason).toBe('NOT does not fit on the plot there.');
    expect(b.controller.previewPlace('gate.nothing', [30, 0, 30])).toBeNull();
  });

  it('snaps onto an output port near the cell, the shared cell joined; with none in reach it says so', () => {
    const b = board();
    const p = b.controller.previewPlace('gate.not', [14, 0, 11], { snap: true })!;
    expect(p).toMatchObject({ at: [14, 0, 10], joint: [13, 0, 11], reason: null });
    expect(p.ports.joined).toEqual([[13, 0, 11]]);
    const far = b.controller.previewPlace('gate.not', [40, 0, 40], { snap: true })!;
    expect(far.reason).toBe(NO_OUTLET);
    expect(b.controller.place('gate.not', [40, 0, 40], { snap: true })).toBeNull();
    expect(b.controller.snapshot().error).toBe(NO_OUTLET);
    // The snapped placement itself lands where the preview said, with the joint's dust.
    expect(b.controller.place('gate.not', [14, 0, 11], { snap: true })).toMatchObject({ at: [14, 0, 10] });
    expect(b.studio.canvas.id(13, 0, 11)).toBe(DUST);
  });
});

/** The ghost bound on a stage over a real controller, frames run by hand, the mouse looking straight down. */
function ghost(req: Partial<PlaceRequest> = {}, over: { level?: number; editable?: boolean } = {}) {
  const b = board();
  const stage = document.createElement('div');
  const surface = document.createElement('canvas');
  stage.append(surface);
  document.body.append(stage);
  // What comes after on the surface: the engine, whose unlocked left press locks the mouse.
  const engine = vi.fn();
  surface.addEventListener('pointerdown', engine);
  const overlay = {
    setMinePlacementGeometry: vi.fn((_positions: Float32Array | null, _at?: V3, _refused?: boolean) => undefined),
    setComponentPorts: vi.fn((_open: readonly Pos[] | null, _joined?: readonly Pos[]) => undefined),
    setPortArrows: vi.fn((_ports: readonly PortArrow[] | null, _at?: Pos, _joined?: readonly Pos[]) => undefined),
    setMinePlacement: vi.fn(),
  };
  const frames: Array<() => void> = [];
  const deps = {
    refused: vi.fn(),
    placed: vi.fn(),
    cancel: vi.fn(),
    rest: vi.fn(),
    notice: vi.fn(),
  };
  // Client (x, y) is straight above cell (x, y) of the plot.
  const ray = (x: number, y: number): PlacementRay => ({
    origin: { x: x + 0.5, y: 300, z: y + 0.5 },
    direction: { x: 0, y: -1, z: 0 },
  });
  const level = vi.fn(() => over.level ?? 0);
  const win = new EventTarget() as unknown as Window;
  const unbind = bindPlaceGhost(
    stage,
    { def: 'gate.not', turns: 0, mirror: null, snap: false, ...req },
    {
      controller: b.controller,
      overlay,
      ray,
      level,
      surface: () => surface,
      canEdit: () => over.editable ?? true,
      ...deps,
      schedule: (run) => void frames.push(run),
    },
    win,
  );
  cleanups.push(() => {
    unbind();
    stage.remove();
  });
  const event = (type: string, x: number, y: number, init: PointerEventInit = {}) => {
    const e = new PointerEvent(type, {
      bubbles: true,
      cancelable: true,
      pointerId: 1,
      clientX: x,
      clientY: y,
      ...init,
    });
    surface.dispatchEvent(e);
    return e;
  };
  const frame = () => {
    for (const run of frames.splice(0)) run();
  };
  const lock = () => Object.defineProperty(document, 'pointerLockElement', { configurable: true, value: surface });
  const key = (init: KeyboardEventInit) => {
    const e = new KeyboardEvent('keydown', { cancelable: true, ...init });
    win.dispatchEvent(e);
    return e;
  };
  return { ...b, stage, surface, engine, overlay, frames, deps, level, win, unbind, event, frame, lock, key };
}

describe('the placing ghost', () => {
  it('follows the mouse once per frame, at the latest position, in the overlay only', () => {
    const g = ghost();
    const before = g.blocks();
    for (const x of [30, 31, 32]) g.event('pointermove', x, 40);
    expect(g.frames).toHaveLength(1);
    expect(g.overlay.setMinePlacementGeometry).not.toHaveBeenCalled();
    g.frame();
    expect(g.overlay.setMinePlacementGeometry).toHaveBeenCalledOnce();
    const [positions, at, red] = g.overlay.setMinePlacementGeometry.mock.calls[0]!;
    expect(positions).toBeInstanceOf(Float32Array);
    expect(at).toEqual([32, 0, 40]);
    expect(red).toBe(false);
    expect(g.deps.refused).toHaveBeenLastCalledWith(null);
    expect(g.overlay.setComponentPorts).toHaveBeenCalledOnce();
    expect(g.overlay.setPortArrows.mock.calls[0]![0]).toEqual(g.controller.placeShape('gate.not')!.ports);
    expect(g.overlay.setPortArrows.mock.calls[0]![1]).toEqual([32, 0, 40]);
    // The same cell again shows nothing new; another moves the outline worked out once.
    g.event('pointermove', 32.4, 40.2);
    g.frame();
    expect(g.overlay.setMinePlacementGeometry).toHaveBeenCalledOnce();
    g.event('pointermove', 34, 40);
    g.frame();
    expect(g.overlay.setMinePlacementGeometry.mock.calls[1]![0]).toBe(positions);
    expect(g.blocks()).toEqual(before);
    expect(g.studio.snapshot().undoLabel).toBe('place NOT');
  });

  it('stands on the level it is given', () => {
    const g = ghost({}, { level: 3 });
    g.event('pointermove', 30, 30);
    g.frame();
    expect(g.overlay.setMinePlacementGeometry.mock.calls[0]![1]).toEqual([30, 3, 30]);
  });

  it('is red with the reason where the placement would be refused', () => {
    const g = ghost();
    g.event('pointermove', 12, 12);
    g.frame();
    expect(g.overlay.setMinePlacementGeometry.mock.calls[0]![2]).toBe(true);
    expect(g.deps.refused).toHaveBeenLastCalledWith(expect.stringMatching(/would touch NOT/));
  });

  it('places on a plain left click, one undo step, and the view never sees the press (so it does not lock)', () => {
    const g = ghost();
    g.event('pointermove', 30, 30);
    g.frame();
    const down = g.event('pointerdown', 30, 30);
    expect(down.defaultPrevented).toBe(true);
    expect(g.engine).not.toHaveBeenCalled();
    expect(g.deps.placed).toHaveBeenCalledExactlyOnceWith('c2');
    expect(g.controller.snapshot().components.find((c) => c.id === 'c2')!.at).toEqual([30, 0, 30]);
    expect(g.studio.snapshot().undoLabel).toBe('place NOT');
    g.studio.undo();
    expect(g.controller.snapshot().components.map((c) => c.id)).toEqual(['c1']);
  });

  it('snaps the ghost and the click onto an output port when asked, laying the joint’s dust', () => {
    const g = ghost({ snap: true });
    g.event('pointermove', 14, 11);
    g.frame();
    expect(g.overlay.setMinePlacementGeometry.mock.calls[0]![1]).toEqual([14, 0, 10]);
    expect(g.overlay.setComponentPorts.mock.calls[0]![1]).toEqual([[13, 0, 11]]);
    g.event('pointerdown', 14, 11);
    expect(g.controller.snapshot().components[1]!.at).toEqual([14, 0, 10]);
    expect(g.studio.canvas.id(13, 0, 11)).toBe(DUST);
  });

  it('places nothing on a refused spot and says why; nothing while editing is not allowed', () => {
    const g = ghost();
    const before = g.blocks();
    expect(g.event('pointerdown', 12, 12).defaultPrevented).toBe(true);
    expect(g.deps.placed).not.toHaveBeenCalled();
    expect(g.deps.notice).toHaveBeenCalledExactlyOnceWith(expect.stringMatching(/would touch NOT/));
    expect(g.blocks()).toEqual(before);

    const ro = ghost({}, { editable: false });
    expect(ro.event('pointerdown', 30, 30).defaultPrevented).toBe(true);
    expect(ro.deps.placed).not.toHaveBeenCalled();
    expect(ro.controller.snapshot().components).toHaveLength(1);
  });

  it('stops placing on a right click or Escape, but leaves Escape to the text being typed', () => {
    const g = ghost();
    expect(g.event('pointerdown', 30, 30, { button: 2 }).defaultPrevented).toBe(true);
    expect(g.engine).not.toHaveBeenCalled();
    expect(g.deps.cancel).toHaveBeenCalledOnce();
    expect(g.key({ key: 'Escape' }).defaultPrevented).toBe(true);
    expect(g.deps.cancel).toHaveBeenCalledTimes(2);
    // Escape in a text box (the panel's typed corner) is the box's.
    const typing = new KeyboardEvent('keydown', { key: 'Escape', cancelable: true });
    Object.defineProperty(typing, 'target', { value: document.createElement('input') });
    g.win.dispatchEvent(typing);
    expect(typing.defaultPrevented).toBe(false);
    expect(g.deps.cancel).toHaveBeenCalledTimes(2);
    expect(g.key({ key: 'a' }).defaultPrevented).toBe(false);
    expect(g.deps.placed).not.toHaveBeenCalled();
  });

  it('leaves the middle button, a modified press and the locked mouse to the view', () => {
    const g = ghost();
    for (const init of [{ button: 1 }, { altKey: true }, { ctrlKey: true }, { shiftKey: true }, { metaKey: true }])
      expect(g.event('pointerdown', 30, 30, init).defaultPrevented).toBe(false);
    g.lock();
    expect(g.event('pointerdown', 30, 30).defaultPrevented).toBe(false);
    expect(g.event('pointerdown', 30, 30, { button: 2 }).defaultPrevented).toBe(false);
    expect(g.engine).toHaveBeenCalledTimes(7);
    expect(g.deps.placed).not.toHaveBeenCalled();
    expect(g.deps.cancel).not.toHaveBeenCalled();
  });

  it('hides when the mouse leaves the view, locks, or placing ends, and the overlay rests', () => {
    const g = ghost();
    g.event('pointermove', 30, 30);
    g.frame();
    g.stage.dispatchEvent(new PointerEvent('pointerleave'));
    expect(g.overlay.setMinePlacement).toHaveBeenLastCalledWith(null);
    expect(g.deps.refused).toHaveBeenLastCalledWith(null);
    expect(g.deps.rest).toHaveBeenCalledOnce();

    g.event('pointermove', 31, 30);
    g.frame();
    g.lock();
    g.event('pointermove', 32, 30);
    expect(g.deps.rest).toHaveBeenCalledTimes(2);
    // A frame already asked for shows nothing once locked.
    g.frame();
    expect(g.overlay.setMinePlacementGeometry).toHaveBeenCalledTimes(2);

    Object.defineProperty(document, 'pointerLockElement', { configurable: true, value: null });
    g.event('pointermove', 33, 30);
    g.frame();
    g.unbind();
    expect(g.deps.rest).toHaveBeenCalledTimes(3);
    // Unbound: presses and moves go to the view, and no frame is asked for.
    expect(g.event('pointerdown', 30, 30).defaultPrevented).toBe(false);
    g.event('pointermove', 34, 30);
    expect(g.frames).toHaveLength(0);
  });

  it('leaves the controls drawn over the view to themselves: no placing, no cancelling, and the ghost hides there', () => {
    const g = ghost();
    const button = document.createElement('button');
    g.stage.append(button);
    const clicked = vi.fn();
    button.addEventListener('pointerdown', clicked);
    const on = (type: string, init: PointerEventInit = {}) => {
      const e = new PointerEvent(type, {
        bubbles: true,
        cancelable: true,
        pointerId: 1,
        clientX: 30,
        clientY: 30,
        ...init,
      });
      button.dispatchEvent(e);
      return e;
    };
    g.event('pointermove', 30, 30);
    g.frame();
    expect(g.overlay.setMinePlacementGeometry).toHaveBeenCalledOnce();
    // Over the button the ghost goes (it would stand behind it), and no frame is asked for.
    on('pointermove');
    expect(g.overlay.setMinePlacement).toHaveBeenLastCalledWith(null);
    expect(g.deps.rest).toHaveBeenCalledOnce();
    expect(g.frames).toHaveLength(0);
    expect(on('pointerdown').defaultPrevented).toBe(false);
    expect(on('pointerdown', { button: 2 }).defaultPrevented).toBe(false);
    expect(clicked).toHaveBeenCalledTimes(2);
    expect(g.deps.placed).not.toHaveBeenCalled();
    expect(g.deps.cancel).not.toHaveBeenCalled();
    expect(g.controller.snapshot().components).toHaveLength(1);
  });

  it('binds nothing for a definition the design cannot place', () => {
    const g = ghost({ def: 'gate.nothing' });
    expect(g.event('pointerdown', 30, 30).defaultPrevented).toBe(false);
    g.event('pointermove', 30, 30);
    expect(g.frames).toHaveLength(0);
  });
});
