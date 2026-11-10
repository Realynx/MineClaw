import { PerspectiveCamera, Raycaster, Vector2, Vector3 } from 'three';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { setPlotMode, type Selection, type V3 } from '@/features/studio/canvas';
import { Overlay3D } from '@/features/studio/overlay3d';
import type { Studio } from '@/features/studio/studio';
import type { InputState } from '@/features/studio/studioInput';
import { bindStudioPointer, type StudioPointerActions } from '@/features/studio/studioPointer';

const cleanups: Array<() => void> = [];
afterEach(() => {
  for (const cleanup of cleanups.splice(0)) cleanup();
  Object.defineProperty(document, 'pointerLockElement', { configurable: true, value: null });
  setPlotMode({ kind: 'build' });
});

function mount(mine = true) {
  setPlotMode(mine ? { kind: 'mine', terrain: { state: () => 16 } } : { kind: 'build' });
  const surface = document.createElement('canvas');
  surface.tabIndex = 0;
  document.body.append(surface);
  const captured = new Set<number>();
  const capture = vi.fn((id: number) => { captured.add(id); });
  const release = vi.fn((id: number) => { captured.delete(id); });
  surface.setPointerCapture = capture;
  surface.hasPointerCapture = vi.fn((id: number) => captured.has(id));
  surface.releasePointerCapture = release;
  vi.spyOn(surface, 'getBoundingClientRect').mockReturnValue({ left: 0, top: 0, width: 800, height: 600 } as DOMRect);
  const overlay = new Overlay3D(64);
  const pick = vi.spyOn(overlay, 'pickHandle').mockReturnValue(null);
  const studio = {
    selection: null as Selection | null,
    setSelection(selection: Selection | null) { this.selection = selection; overlay.setSelection(selection); },
  };
  const camera = new PerspectiveCamera(60, 800 / 600, 0.1, 300);
  camera.position.set(0, 0, 20);
  camera.lookAt(0, 0, 0);
  camera.updateMatrixWorld();
  const state = { tool: 'build', drag: null, pasting: false } as InputState;
  const translate = (delta: V3) => {
    if (!studio.selection) return;
    studio.setSelection({
      ...studio.selection,
      min: studio.selection.min.map((v, i) => v + delta[i]!) as V3,
      max: studio.selection.max.map((v, i) => v + delta[i]!) as V3,
    });
  };
  let actions: StudioPointerActions = {
    canEdit: true,
    down: vi.fn(() => Promise.resolve()),
    up: vi.fn(() => Promise.resolve()),
    cancel: vi.fn(),
    move: vi.fn(translate),
    setDrag: vi.fn(),
    error: vi.fn(),
  };
  const unbind = bindStudioPointer({ surface, camera, studio: studio as unknown as Studio, overlay,
    mod: { Vector2, Vector3, Raycaster }, mine, state: () => state, actions: () => actions });
  const pointer = (type: string, init: PointerEventInit = {}) => {
    const event = new PointerEvent(type, { bubbles: true, cancelable: true, pointerId: 1, button: 0, clientX: 100, clientY: 100, ...init });
    (type === 'pointerdown' ? surface : window).dispatchEvent(event);
    return event;
  };
  const select = () => {
    studio.setSelection({ min: [0, 0, 0], max: [4, 1, 0] });
    camera.position.copy(overlay.gizmo.position).add(new Vector3(0, 0, 20));
    camera.lookAt(overlay.gizmo.position);
    camera.updateMatrixWorld();
  };
  cleanups.push(() => { unbind(); overlay.dispose(); surface.remove(); });
  return { surface, overlay, camera, studio, state, pick, pointer, select, translate, capture, release,
    actions: () => actions, replaceActions: (next: StudioPointerActions) => { actions = next; }, unbind };
}

describe('studio cursor and immersive pointer binding', () => {
  it('reserves middle and Alt-left for mine camera navigation, including over a selected gizmo', async () => {
    const h = mount();
    h.select(); h.pick.mockReturnValue('x');
    const downstream = vi.fn();
    h.surface.addEventListener('pointerdown', downstream);
    expect(h.pointer('pointerdown', { button: 1 }).defaultPrevented).toBe(false);
    expect(h.pointer('pointerdown', { altKey: true }).defaultPrevented).toBe(false);
    await Promise.resolve();
    expect(h.pick).not.toHaveBeenCalled();
    expect(h.actions().down).not.toHaveBeenCalled();
    expect(h.actions().move).not.toHaveBeenCalled();
    expect(downstream).toHaveBeenCalledTimes(2);
  });

  it('authors mine clicks at the cursor and serializes a quick release behind an asynchronous press', async () => {
    const h = mount();
    let resolve!: () => void;
    const pending = new Promise<void>((r) => { resolve = r; });
    const down = vi.fn(() => pending);
    h.replaceActions({ ...h.actions(), down });
    expect(h.pointer('pointerdown', { clientX: 230, clientY: 180, shiftKey: true, metaKey: true }).defaultPrevented).toBe(true);
    expect(h.pointer('pointerup', { clientX: 270, clientY: 200 }).defaultPrevented).toBe(true);
    await Promise.resolve();
    expect(down).toHaveBeenCalledWith(0, { shift: true, ctrl: true }, { x: 230, y: 180 }, expect.any(AbortSignal));
    expect(h.actions().up).not.toHaveBeenCalled();
    resolve();
    await vi.waitFor(() => expect(h.actions().up).toHaveBeenCalledWith(0, { shift: false, ctrl: false }, { x: 270, y: 200 }, expect.any(AbortSignal)));
    expect(h.capture).toHaveBeenCalledWith(1);
    expect(h.release).toHaveBeenCalledWith(1);
  });

  it('keeps builder clicks delegated while unlocked and sends locked clicks to authoring without cursor coordinates', async () => {
    const h = mount(false);
    const viewer = vi.fn();
    h.surface.addEventListener('pointerdown', viewer);
    expect(h.pointer('pointerdown').defaultPrevented).toBe(false);
    await Promise.resolve();
    expect(viewer).toHaveBeenCalledOnce();
    expect(h.actions().down).not.toHaveBeenCalled();
    Object.defineProperty(document, 'pointerLockElement', { configurable: true, value: h.surface });
    expect(h.pointer('pointerdown', { button: 2 }).defaultPrevented).toBe(true);
    h.pointer('pointerup', { button: 2 });
    await vi.waitFor(() => expect(h.actions().up).toHaveBeenCalledOnce());
    expect(h.actions().down).toHaveBeenCalledWith(2, { shift: false, ctrl: false }, undefined, expect.any(AbortSignal));
    expect(viewer).toHaveBeenCalledOnce();
    expect(h.capture).not.toHaveBeenCalled();
  });

  it('blocks editing when permission is absent while retaining ordinary selection', async () => {
    const h = mount();
    h.select(); h.pick.mockReturnValue('x');
    h.replaceActions({ ...h.actions(), canEdit: false });
    expect(h.pointer('pointerdown').defaultPrevented).toBe(false);
    await Promise.resolve();
    expect(h.actions().down).not.toHaveBeenCalled();
    expect(h.pick).not.toHaveBeenCalled();
    h.state.tool = 'select';
    expect(h.pointer('pointerdown').defaultPrevented).toBe(true);
    h.pointer('pointerup');
    await vi.waitFor(() => expect(h.actions().up).toHaveBeenCalledOnce());
    h.state.pasting = true;
    expect(h.pointer('pointerdown').defaultPrevented).toBe(false);
    expect(h.actions().down).toHaveBeenCalledOnce();
  });

  it('leaves the wheel to mine zoom without resizing the selected component or clearing its mask', () => {
    const h = mount();
    h.select(); h.state.tool = 'select';
    const selection = { ...h.studio.selection!, mask: new Set([1, 2]) };
    h.studio.setSelection(selection);
    const zoom = vi.fn();
    h.surface.addEventListener('wheel', zoom);
    const event = new WheelEvent('wheel', { deltaY: -1, bubbles: true, cancelable: true });
    h.surface.dispatchEvent(event);
    expect(event.defaultPrevented).toBe(false);
    expect(zoom).toHaveBeenCalledOnce();
    expect(h.studio.selection).toBe(selection);
    expect(h.actions().setDrag).not.toHaveBeenCalled();
  });

  it('uses current callbacks throughout a gizmo drag and groups every accepted movement together', () => {
    const h = mount();
    h.select(); h.pick.mockReturnValue('x');
    const firstMove = h.actions().move;
    h.pointer('pointerdown');
    h.pointer('pointermove', { clientX: 126 });
    expect(h.studio.selection?.min).toEqual([1, 0, 0]);
    const nextMove = vi.fn<StudioPointerActions['move']>(h.translate);
    h.replaceActions({ ...h.actions(), move: nextMove });
    h.pointer('pointermove', { clientX: 152 });
    h.pointer('pointermove', { pointerId: 2, clientX: 178 });
    h.pointer('pointerup');
    h.pointer('pointermove', { clientX: 178 });
    expect(h.studio.selection?.min).toEqual([2, 0, 0]);
    expect(firstMove).toHaveBeenCalledOnce();
    expect(nextMove).toHaveBeenCalledOnce();
    expect(vi.mocked(firstMove).mock.calls[0]![1]).toBe(nextMove.mock.calls[0]![1]);
    expect(h.actions().down).not.toHaveBeenCalled();
    expect(h.actions().up).not.toHaveBeenCalled();
  });

  it('keeps the camera fixed during an axis drag and cancels if pointer capture is lost', () => {
    const h = mount();
    h.select(); h.pick.mockReturnValue('x');
    const zoom = vi.fn();
    h.surface.addEventListener('wheel', zoom);
    h.pointer('pointerdown');
    const wheel = new WheelEvent('wheel', { deltaY: -1, bubbles: true, cancelable: true });
    h.surface.dispatchEvent(wheel);
    expect(wheel.defaultPrevented).toBe(true);
    expect(zoom).not.toHaveBeenCalled();
    h.surface.dispatchEvent(new PointerEvent('lostpointercapture', { pointerId: 1 }));
    h.pointer('pointermove', { clientX: 152 });
    expect(h.actions().move).not.toHaveBeenCalled();
    expect(h.actions().cancel).toHaveBeenCalledOnce();
    h.surface.dispatchEvent(new WheelEvent('wheel', { deltaY: -1 }));
    expect(zoom).toHaveBeenCalledOnce();
  });

  it('does not count a rejected move toward the drag offset, so returning inside bounds moves from the actual position', () => {
    const h = mount();
    h.select(); h.pick.mockReturnValue('x');
    const move = vi.fn((delta: V3) => { if (delta[0] <= 1) h.translate(delta); });
    h.replaceActions({ ...h.actions(), move });
    h.pointer('pointerdown');
    h.pointer('pointermove', { clientX: 152 });
    expect(h.studio.selection?.min).toEqual([0, 0, 0]);
    h.pointer('pointermove', { clientX: 126 });
    expect(h.studio.selection?.min).toEqual([1, 0, 0]);
    expect(move.mock.calls.map(([delta]) => delta)).toEqual([[2, 0, 0], [1, 0, 0]]);
  });

  it('ends a gizmo on pointer cancellation or window blur and skips an authoring press cancelled before it starts', async () => {
    const h = mount();
    h.select(); h.pick.mockReturnValue('x');
    h.pointer('pointerdown');
    h.pointer('pointercancel');
    h.pointer('pointermove', { clientX: 126 });
    expect(h.actions().move).not.toHaveBeenCalled();
    h.pointer('pointerdown');
    window.dispatchEvent(new Event('blur'));
    h.pointer('pointermove', { clientX: 126 });
    expect(h.actions().move).not.toHaveBeenCalled();
    expect(h.actions().cancel).toHaveBeenCalledTimes(2);
    expect(h.release).toHaveBeenCalledTimes(2);
    h.pick.mockReturnValue(null);
    h.pointer('pointerdown');
    h.pointer('pointercancel');
    await Promise.resolve(); await Promise.resolve();
    expect(h.actions().down).not.toHaveBeenCalled();
  });

  it('previews a mine component drag, refuses positions the preview rejects, and commits one move on release', () => {
    const h = mount();
    h.select(); h.pick.mockReturnValue('x');
    const dragPreview = vi.fn((delta: V3) => delta[0] <= 2);
    const dragCommit = vi.fn();
    const dragCancel = vi.fn();
    const gesture = vi.fn();
    h.replaceActions({ ...h.actions(), dragStart: () => true, dragPreview, dragCommit, dragCancel, gesture });
    h.pointer('pointerdown');
    expect(gesture).toHaveBeenLastCalledWith(true);
    h.pointer('pointermove', { clientX: 126 });
    h.pointer('pointermove', { clientX: 127 });
    h.pointer('pointermove', { clientX: 152 });
    h.pointer('pointermove', { clientX: 178 });
    expect(dragPreview.mock.calls.map(([delta]) => delta)).toEqual([[1, 0, 0], [2, 0, 0], [3, 0, 0]]);
    expect(h.actions().move).not.toHaveBeenCalled();
    expect(dragCommit).not.toHaveBeenCalled();
    expect(h.studio.selection?.min).toEqual([0, 0, 0]);
    h.pointer('pointerup', { clientX: 178 });
    expect(dragCommit).toHaveBeenCalledOnce();
    expect(dragCommit).toHaveBeenCalledWith([2, 0, 0]);
    expect(dragCancel).not.toHaveBeenCalled();
    expect(gesture).toHaveBeenLastCalledWith(false);
    expect(h.release).toHaveBeenCalledWith(1);
  });

  it('cancels a previewed drag with Escape, a lost pointer or a lost window, and never commits it', () => {
    const h = mount();
    h.select(); h.pick.mockReturnValue('x');
    const dragCommit = vi.fn();
    const dragCancel = vi.fn();
    h.replaceActions({ ...h.actions(), dragStart: () => true, dragPreview: () => true, dragCommit, dragCancel });
    const other = vi.fn();
    window.addEventListener('keydown', other);
    h.pointer('pointerdown');
    h.pointer('pointermove', { clientX: 152 });
    const escape = new KeyboardEvent('keydown', { key: 'Escape', cancelable: true, bubbles: true });
    window.dispatchEvent(escape);
    expect(escape.defaultPrevented).toBe(true);
    expect(other).not.toHaveBeenCalled();
    expect(dragCancel).toHaveBeenCalledOnce();
    h.pointer('pointermove', { clientX: 178 });
    h.pointer('pointerup');
    expect(dragCommit).not.toHaveBeenCalled();
    // Escape without a drag is left to the page.
    const idle = new KeyboardEvent('keydown', { key: 'Escape', cancelable: true, bubbles: true });
    window.dispatchEvent(idle);
    expect(idle.defaultPrevented).toBe(false);
    expect(other).toHaveBeenCalledOnce();
    window.removeEventListener('keydown', other);
    h.pointer('pointerdown');
    h.pointer('pointercancel');
    h.pointer('pointerdown');
    window.dispatchEvent(new Event('blur'));
    expect(dragCancel).toHaveBeenCalledTimes(3);
    expect(dragCommit).not.toHaveBeenCalled();
  });

  it('keeps the per-step path when the press declines a preview (a raw selection, or build mode)', () => {
    const h = mount();
    h.select(); h.pick.mockReturnValue('x');
    const dragPreview = vi.fn(() => true);
    const dragCommit = vi.fn();
    h.replaceActions({ ...h.actions(), dragStart: () => false, dragPreview, dragCommit });
    h.pointer('pointerdown');
    h.pointer('pointermove', { clientX: 126 });
    h.pointer('pointerup');
    expect(h.actions().move).toHaveBeenCalledOnce();
    expect(h.studio.selection?.min).toEqual([1, 0, 0]);
    expect(dragPreview).not.toHaveBeenCalled();
    expect(dragCommit).not.toHaveBeenCalled();
  });

  it('aborts the signal of an in-flight asynchronous press when the window loses focus', async () => {
    const h = mount();
    let resolve!: () => void;
    const pending = new Promise<void>((r) => { resolve = r; });
    const down = vi.fn<StudioPointerActions['down']>(() => pending);
    h.replaceActions({ ...h.actions(), down });
    h.pointer('pointerdown');
    await Promise.resolve();
    const signal = down.mock.calls[0]![3]!;
    expect(signal.aborted).toBe(false);
    window.dispatchEvent(new Event('blur'));
    expect(signal.aborted).toBe(true);
    resolve();
    await Promise.resolve();
    expect(h.actions().cancel).toHaveBeenCalledOnce();
    expect(h.actions().up).not.toHaveBeenCalled();
  });
});
