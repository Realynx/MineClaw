import type { DesignerSession } from '@mineclaw/contracts';
import { MOCK_DESIGNER_PALETTE } from '@mineclaw/contracts';
import type { WorldViewerHandle } from '@mineclaw/world-viewer';
import { act, cleanup, renderHook } from '@testing-library/react';
import { StrictMode, type DragEvent } from 'react';
import { PerspectiveCamera, Raycaster, Vector2, Vector3 } from 'three';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { keyOf, plotMode, setPlotMode, type V3 } from '@/features/studio/canvas';
import { defaultModule, encodeModule, joinedPortCells, MINE_MODULE_MIME, moduleCells, modulePorts, openPortCells, snapModuleAt, type MineModuleSpec } from '@/features/studio/mine/modules';
import type { Overlay3D } from '@/features/studio/overlay3d';
import { Studio, type StudioApi } from '@/features/studio/studio';
import { studioKey, type InputState, type KeyActions } from '@/features/studio/studioInput';
import type * as ViewerMod from '@/features/studio/studio-viewer';
import { useMinePlacement } from '@/features/studio/useMinePlacement';

const studios: Studio[] = [];
const surfaces: HTMLCanvasElement[] = [];
const tunnel: MineModuleSpec = { ...defaultModule('tunnel'), length: 5 };
const session = (): DesignerSession => ({
  id: 901, owner: 'test', model: 'main', designId: null, name: 'Mine placement', rev: 0,
  blocks: [], anchor: null, messages: [], costUsd: 0, calls: 0, version: '1.12.2',
  palette: MOCK_DESIGNER_PALETTE, plot: { size: 64, height: 128, depth: 0 }, revisions: [],
  kind: 'mine', seed: '12345',
});
const api: StudioApi = {
  message: () => Promise.reject(new Error('unused')),
  canvas: () => Promise.resolve({ rev: 1, applied: 0 }),
  save: () => Promise.reject(new Error('unused')),
};

beforeEach(() => localStorage.clear());
afterEach(() => {
  cleanup();
  for (const studio of studios.splice(0)) studio.dispose();
  for (const surface of surfaces.splice(0)) surface.remove();
  setPlotMode({ kind: 'build' });
});

/** Real authoring state and camera math; only the renderer and its DOM rectangle are substituted. */
function mount(over: Partial<DesignerSession> = {}, strict = false) {
  const studio = new Studio({ ...session(), ...over }, api, 60_000);
  studios.push(studio);
  const surface = document.createElement('canvas');
  surface.tabIndex = 0;
  document.body.append(surface);
  surfaces.push(surface);
  const rect = { left: 100, top: 50, width: 800, height: 600 };
  vi.spyOn(surface, 'getBoundingClientRect').mockReturnValue({
    ...rect, right: 900, bottom: 650, x: 100, y: 50, toJSON: () => ({}),
  });
  const camera = new PerspectiveCamera(60, rect.width / rect.height, 0.1, 300);
  camera.position.set(32, 90, 80);
  camera.lookAt(32, 20, 32);
  camera.updateMatrixWorld();
  const focus = vi.fn();
  const viewer = { current: { engine: () => ({ surface, camera, focus }) } as unknown as WorldViewerHandle };
  const setMinePlacement = vi.fn();
  const setMinePlacementGeometry = vi.fn();
  const setMinePorts = vi.fn();
  const setSelection = vi.fn();
  const pickHandle = vi.fn(() => null);
  const notice = vi.fn();
  const activateSelection = vi.fn();
  const onWorkLevel = vi.fn();
  const props = {
    studio, viewer, mod: { Raycaster, Vector2 } as typeof ViewerMod,
    overlay: { setMinePlacement, setMinePlacementGeometry, setMinePorts, setSelection, pickHandle } as unknown as Overlay3D,
    ready: true, canEdit: true, selecting: true, notice, activateSelection, onWorkLevel,
  };
  const hook = renderHook((p: typeof props) => useMinePlacement(p), { initialProps: props, ...(strict ? { wrapper: StrictMode } : {}) });
  const client = (at: V3) => {
    // Aim inside the cell to avoid floating point ambiguity at exact voxel boundaries.
    const ndc = new Vector3(at[0] + 0.5, at[1], at[2] + 0.5).project(camera);
    return { clientX: rect.left + (ndc.x + 1) * rect.width / 2, clientY: rect.top + (1 - ndc.y) * rect.height / 2 };
  };
  const pointer = (type: string, at: V3, init: PointerEventInit = {}) => {
    const event = new PointerEvent(type, { ...client(at), button: 0, bubbles: true, cancelable: true, ...init });
    act(() => { surface.dispatchEvent(event); });
    return event;
  };
  const place = (spec: MineModuleSpec, at: V3) => {
    act(() => { hook.result.current.setPlacementY(at[1]); });
    act(() => { hook.result.current.arm(spec); });
    pointer('pointerdown', at);
    return hook.result.current.modules.at(-1)!;
  };
  const drag = (at: V3, raw = encodeModule(tunnel)) => ({
    ...client(at), preventDefault: vi.fn(), stopPropagation: vi.fn(),
    dataTransfer: { types: [MINE_MODULE_MIME], dropEffect: 'none', getData: vi.fn(() => raw) },
  });
  return { ...hook, props, studio, surface, camera, focus, setMinePlacement, setMinePlacementGeometry, setMinePorts, setSelection, pickHandle, notice, activateSelection, onWorkLevel, pointer, place, drag };
}

describe('mine module pointer authoring', () => {
  it('previews at the chosen mining level and commits a click before ordinary canvas tools handle it', () => {
    const h = mount();
    const ordinaryTool = vi.fn();
    h.surface.addEventListener('pointerdown', ordinaryTool);
    act(() => { h.result.current.arm(tunnel); });
    expect(h.focus).not.toHaveBeenCalled();
    h.pointer('pointermove', [24, 11, 28]);
    expect(h.result.current.hover).toEqual([24, 11, 28]);
    expect(h.setMinePlacementGeometry).toHaveBeenLastCalledWith(expect.any(Float32Array), [24, 11, 28], false);
    expect(h.studio.canvas.dug.size).toBe(0);
    const previewCalls = h.setMinePlacementGeometry.mock.calls.length;
    h.pointer('pointermove', [24, 11, 28]);
    expect(h.setMinePlacementGeometry).toHaveBeenCalledTimes(previewCalls);

    expect(h.pointer('pointerdown', [24, 11, 28]).defaultPrevented).toBe(true);
    expect(ordinaryTool).not.toHaveBeenCalled();
    expect(h.result.current.modules).toHaveLength(1);
    expect(h.result.current.modules[0]!.at).toEqual([24, 11, 28]);
    expect(h.studio.canvas.dug.size).toBe(10);
    expect(h.studio.canvas.dug.has(keyOf(26, 12, 28))).toBe(true);
    expect(h.studio.selection?.mask?.size).toBe(10);
    expect(h.result.current.selectedId).toBe(h.result.current.modules[0]!.id);
    expect(h.result.current.placing).toBeNull();
    expect(h.setMinePlacement).toHaveBeenLastCalledWith(null);
  });

  it('accepts a dragged module without arming and projects its drop onto the selected Y, not the terrain surface', () => {
    const h = mount();
    act(() => { h.result.current.setPlacementY(27); });
    const event = h.drag([18, 27, 40]);
    act(() => { h.result.current.dragOver(event as unknown as DragEvent); });
    expect(event.preventDefault).toHaveBeenCalledOnce();
    expect(event.dataTransfer.dropEffect).toBe('copy');
    act(() => { h.result.current.drop(event as unknown as DragEvent); });
    expect(event.dataTransfer.getData).toHaveBeenCalledWith(MINE_MODULE_MIME);
    expect(event.stopPropagation).toHaveBeenCalledOnce();
    expect(h.result.current.modules[0]!.at).toEqual([18, 27, 40]);
    expect(h.studio.canvas.dug.has(keyOf(16, 27, 40))).toBe(true);
    expect(h.focus).not.toHaveBeenCalled();
    const malformed = h.drag([40, 27, 40], '{broken');
    act(() => { h.result.current.drop(malformed as unknown as DragEvent); });
    expect(h.result.current.modules).toHaveLength(1);
    expect(malformed.preventDefault).not.toHaveBeenCalled();
  });

  it('moves an overlapping layout with one gizmo drag, retaining the other tunnel and undoing the whole gesture', () => {
    const h = mount();
    const first = h.place(tunnel, [20, 30, 20]);
    const second = h.place({ ...tunnel, rotation: 1 }, [20, 30, 20]);
    expect(second.id).not.toBe(first.id);
    expect(h.studio.canvas.dug.size).toBe(18);
    const original = new Set(h.studio.canvas.dug);
    const edits = vi.spyOn(h.studio, 'editWithHistory');
    const pending = h.studio.snapshot().pending;
    const box = h.studio.selection!;
    act(() => { expect(h.result.current.dragStart()).toBe(true); });
    act(() => { expect(h.result.current.dragPreview([1, 0, 0])).toBe(true); });
    act(() => { expect(h.result.current.dragPreview([2, 0, 0])).toBe(true); });
    // Only the outline and the handles travel: no cells, no layout change, nothing queued for the bot.
    expect(h.setMinePlacementGeometry).toHaveBeenLastCalledWith(expect.any(Float32Array), [22, 30, 20], false);
    expect(h.setSelection).toHaveBeenLastCalledWith({ min: [box.min[0] + 2, box.min[1], box.min[2]], max: [box.max[0] + 2, box.max[1], box.max[2]] });
    expect(h.result.current.modules[1]!.at).toEqual([20, 30, 20]);
    expect(h.studio.canvas.dug).toEqual(original);
    expect(h.studio.snapshot().pending).toBe(pending);
    expect(edits).not.toHaveBeenCalled();
    act(() => { h.result.current.dragCommit([2, 0, 0]); });
    expect(edits).toHaveBeenCalledOnce();
    expect(h.setMinePlacement).toHaveBeenLastCalledWith(null);
    expect(h.result.current.modules[1]!.at).toEqual([22, 30, 20]);
    expect(h.studio.canvas.authoredState(keyOf(20, 30, 18))).toBe(-1);
    expect(h.studio.canvas.dug.has(keyOf(20, 30, 20))).toBe(true);
    expect(h.studio.canvas.dug.has(keyOf(22, 30, 18))).toBe(true);
    expect(h.studio.selection?.min).toEqual([22, 30, 18]);
    const moved = new Set(h.studio.canvas.dug);

    act(() => { h.studio.undo(); });
    expect(h.result.current.modules[1]!.at).toEqual([20, 30, 20]);
    expect(h.studio.canvas.dug).toEqual(original);
    expect(h.studio.selection?.min).toEqual([20, 30, 18]);
    expect(h.result.current.selectedId).toBe(second.id);
    act(() => { h.studio.redo(); });
    expect(h.result.current.modules[1]!.at).toEqual([22, 30, 20]);
    expect(h.studio.canvas.dug).toEqual(moved);
    expect(h.studio.selection?.min).toEqual([22, 30, 18]);
  });

  it('cancels a gizmo drag without an edit, draws an outline off the plot red with the reason, and only previews a selected component', () => {
    const h = mount();
    const item = h.place(tunnel, [20, 30, 20]);
    const original = new Set(h.studio.canvas.dug);
    const box = h.studio.selection;
    const edits = vi.spyOn(h.studio, 'editWithHistory');
    act(() => { expect(h.result.current.dragStart()).toBe(true); });
    act(() => { expect(h.result.current.dragPreview([3, 0, 0])).toBe(true); });
    expect(h.result.current.refused).toBeNull();
    // Off the plot: the outline goes there in red, and the reason is what the tooltip shows.
    act(() => { expect(h.result.current.dragPreview([plotMode().size, 0, 0])).toBe(true); });
    expect(h.setMinePlacementGeometry).toHaveBeenLastCalledWith(expect.any(Float32Array), [20 + plotMode().size, 30, 20], true);
    expect(h.result.current.refused).toContain('whole layout must fit');
    act(() => { expect(h.result.current.dragPreview([3, 0, 0])).toBe(true); });
    expect(h.setMinePlacementGeometry).toHaveBeenLastCalledWith(expect.any(Float32Array), [23, 30, 20], false);
    expect(h.result.current.refused).toBeNull();
    act(() => { expect(h.result.current.dragPreview([0, -40, 0])).toBe(true); });
    act(() => { h.result.current.dragCancel(); });
    expect(h.result.current.refused).toBeNull();
    expect(h.setMinePlacement).toHaveBeenLastCalledWith(null);
    expect(h.setSelection).toHaveBeenLastCalledWith(box);
    expect(h.result.current.modules).toEqual([item]);
    expect(h.studio.canvas.dug).toEqual(original);
    expect(edits).not.toHaveBeenCalled();
    // Released where it cannot stand: nothing moves, and the notice says why.
    act(() => { expect(h.result.current.dragStart()).toBe(true); });
    act(() => { h.result.current.dragPreview([0, -40, 0]); });
    act(() => { h.result.current.dragCommit([0, -40, 0]); });
    expect(h.result.current.modules).toEqual([item]);
    expect(h.studio.canvas.dug).toEqual(original);
    expect(h.notice).toHaveBeenLastCalledWith(expect.stringContaining('whole layout must fit'));
    expect(h.result.current.refused).toBeNull();
    // A release after the cancel, or with no travel, changes nothing.
    act(() => { h.result.current.dragCommit([3, 0, 0]); });
    act(() => { expect(h.result.current.dragStart()).toBe(true); h.result.current.dragCommit([0, 0, 0]); });
    expect(edits).not.toHaveBeenCalled();

    // A raw selection, a placement in progress or a read-only page keep the per-step path.
    act(() => { h.result.current.arm(tunnel); });
    expect(h.result.current.dragStart()).toBe(false);
    act(() => { h.result.current.cancel(); h.result.current.select(item.id); });
    h.rerender({ ...h.props, canEdit: false });
    expect(h.result.current.dragStart()).toBe(false);
    h.rerender(h.props);
    act(() => { h.studio.setSelection({ min: [0, 30, 0], max: [3, 31, 3] }); });
    expect(h.result.current.dragStart()).toBe(false);
  });

  it('turns a held arrow key into one undo step, summing the presses of one frame, while a raw selection moves as before', () => {
    vi.useFakeTimers({ toFake: ['requestAnimationFrame', 'cancelAnimationFrame', 'performance'] });
    try {
      const h = mount();
      const item = h.place(tunnel, [20, 30, 20]);
      const original = new Set(h.studio.canvas.dug);
      const moveSel = vi.fn();
      const actions = {
        studio: h.studio, state: () => ({ drag: null }) as unknown as InputState, inWorld: () => true, moveSel,
        nudgeSel: (d: V3) => h.result.current.nudgeSelected(d),
      } as unknown as KeyActions;
      const press = (key = 'ArrowRight') => {
        const event = new KeyboardEvent('keydown', { key, repeat: true, cancelable: true });
        act(() => { studioKey(event, actions); });
        expect(event.defaultPrevented).toBe(true);
      };
      const edits = vi.spyOn(h.studio, 'editWithHistory');
      press(); press();
      expect(edits).not.toHaveBeenCalled();
      act(() => { vi.advanceTimersByTime(20); });
      expect(edits).toHaveBeenCalledOnce();
      expect(h.result.current.modules[0]!.at).toEqual([22, 30, 20]);
      for (let i = 0; i < 3; i++) { press(); act(() => { vi.advanceTimersByTime(40); }); }
      expect(edits).toHaveBeenCalledTimes(4);
      expect(h.result.current.modules[0]!.at).toEqual([25, 30, 20]);
      // A pause starts the next step.
      act(() => { vi.advanceTimersByTime(700); });
      press('ArrowUp');
      act(() => { vi.advanceTimersByTime(20); });
      expect(h.result.current.modules[0]!.at).toEqual([25, 30, 19]);
      act(() => { h.studio.undo(); });
      expect(h.result.current.modules[0]!.at).toEqual([25, 30, 20]);
      act(() => { h.studio.undo(); });
      expect(h.result.current.modules).toEqual([item]);
      expect(h.studio.canvas.dug).toEqual(original);
      expect(moveSel).not.toHaveBeenCalled();
      act(() => { h.studio.setSelection({ min: [0, 30, 0], max: [3, 31, 3] }); });
      press();
      expect(moveSel).toHaveBeenCalledWith([1, 0, 0]);
    } finally {
      vi.useRealTimers();
    }
  });

  it('refreshes selected bounds after edits and undo, and selects underground layouts with a real camera ray', () => {
    const h = mount();
    const item = h.place(tunnel, [30, 25, 30]);
    act(() => { h.result.current.update(item.id, { ...tunnel, length: 9 }); });
    expect(h.studio.selection?.min).toEqual([26, 25, 30]);
    expect(h.studio.selection?.mask?.size).toBe(18);
    act(() => { h.studio.undo(); });
    expect(h.studio.selection?.min).toEqual([28, 25, 30]);
    expect(h.studio.selection?.mask?.size).toBe(10);

    act(() => { h.studio.setSelection(null); });
    expect(h.result.current.selectedId).toBeNull();
    act(() => { expect(h.result.current.moveSelected([1, 0, 0])).toBe(false); });
    h.pointer('pointerdown', [30, 26.5, 30]);
    expect(h.result.current.selectedId).toBe(item.id);
    expect(h.pickHandle).toHaveBeenCalledWith(expect.any(Raycaster));
    expect(h.studio.selection?.mask?.size).toBe(10);
  });

  it('Move cancels an armed different component and translates the selected original without creating a copy', () => {
    const h = mount();
    const item = h.place(tunnel, [20, 30, 20]);
    const original = new Set(h.studio.canvas.dug);
    act(() => { h.result.current.arm(defaultModule('chamber')); });
    h.pointer('pointermove', [40, 30, 40]);
    act(() => { h.result.current.move(item.id); });
    expect(h.result.current.placing).toBeNull();
    expect(h.result.current.hover).toBeNull();
    expect(h.result.current.selectedId).toBe(item.id);
    expect(h.setMinePlacement).toHaveBeenLastCalledWith(null);
    expect(h.activateSelection).toHaveBeenCalled();
    expect(h.focus).not.toHaveBeenCalled();
    h.pointer('pointerdown', [40, 30, 40]);
    expect(h.result.current.modules).toEqual([item]);
    act(() => { h.result.current.moveSelected([4, 0, 0]); });
    expect(h.result.current.modules).toHaveLength(1);
    expect(h.result.current.modules[0]).toMatchObject({ id: item.id, at: [24, 30, 20], spec: tunnel });
    expect(h.studio.canvas.dug.size).toBe(10);
    expect(h.studio.canvas.authoredState(keyOf(20, 30, 20))).toBe(-1);
    expect(document.activeElement).toBe(h.surface);

    act(() => { h.studio.undo(); });
    expect(h.result.current.modules).toEqual([item]);
    expect(h.studio.canvas.dug).toEqual(original);
    expect(h.studio.selection?.mask?.size).toBe(10);
  });

  it('rotates and deletes selected layouts only while the canvas has focus, retaining normal undo', () => {
    const h = mount();
    const item = h.place(tunnel, [20, 30, 20]);
    const key = (value: string) => {
      const event = new KeyboardEvent('keydown', { key: value, cancelable: true });
      act(() => { window.dispatchEvent(event); });
      return event;
    };
    const input = document.createElement('input');
    document.body.append(input);
    input.focus();
    expect(key('Delete').defaultPrevented).toBe(false);
    expect(key(']').defaultPrevented).toBe(false);
    expect(h.result.current.modules).toEqual([item]);
    input.remove();

    h.surface.focus();
    expect(key(']').defaultPrevented).toBe(true);
    expect(h.result.current.modules[0]!.spec.rotation).toBe(1);
    expect(h.studio.selection?.min).toEqual([20, 30, 18]);
    expect(key('Delete').defaultPrevented).toBe(true);
    expect(h.result.current.modules).toHaveLength(0);
    expect(h.result.current.selectedId).toBeNull();
    expect(h.studio.canvas.dug.size).toBe(0);
    act(() => { h.studio.undo(); });
    expect(h.result.current.modules).toHaveLength(1);
    expect(h.result.current.modules[0]!.id).toBe(item.id);
    expect(h.result.current.modules[0]!.spec.rotation).toBe(1);
    expect(h.studio.canvas.dug.size).toBe(10);
  });

  it('rejects footprints crossing plot bounds or the bottom, and leaves an invalid gizmo move unchanged', () => {
    const h = mount();
    act(() => { h.result.current.arm(tunnel); });
    h.pointer('pointermove', [0, 11, 30]);
    // The outline stays where the pointer is, red, and the reason is ready before the click.
    expect(h.result.current.hover).not.toBeNull();
    expect(h.setMinePlacementGeometry).toHaveBeenLastCalledWith(expect.any(Float32Array), h.result.current.hover, true);
    expect(h.result.current.refused).toContain('whole layout must fit');
    h.pointer('pointerdown', [0, 11, 30]);
    expect(h.result.current.modules).toHaveLength(0);
    expect(h.studio.snapshot().canUndo).toBe(false);
    expect(h.result.current.placing).toEqual(tunnel);
    expect(h.notice).toHaveBeenLastCalledWith(h.result.current.refused);
    h.pointer('pointermove', [30, 11, 30]);
    expect(h.result.current.refused).toBeNull();
    expect(h.setMinePlacementGeometry).toHaveBeenLastCalledWith(expect.any(Float32Array), [30, 11, 30], false);

    act(() => { h.result.current.arm({ ...defaultModule('stairs'), depth: 12 }); });
    act(() => { h.result.current.setPlacementY(11); });
    h.pointer('pointerdown', [30, 11, 30]);
    expect(h.result.current.modules).toHaveLength(0);
    const item = h.place(tunnel, [30, 11, 30]);
    const before = new Set(h.studio.canvas.dug);
    act(() => { expect(h.result.current.moveSelected([plotMode().size, 0, 0])).toBe(true); });
    expect(h.result.current.modules[0]).toEqual(item);
    expect(h.studio.canvas.dug).toEqual(before);
    expect(h.notice).toHaveBeenLastCalledWith(expect.stringContaining('whole layout must fit'));
  });

  it('blocks every mutation when edit permission is revoked, including a placement armed beforehand', () => {
    const h = mount();
    const item = h.place(tunnel, [20, 25, 20]);
    act(() => { h.result.current.arm(tunnel); });
    h.rerender({ ...h.props, canEdit: false });
    const before = new Set(h.studio.canvas.dug);
    h.pointer('pointerdown', [40, 25, 40]);
    const event = h.drag([40, 25, 40]);
    act(() => {
      h.result.current.drop(event as unknown as DragEvent);
      h.result.current.dragOver(event as unknown as DragEvent);
      h.result.current.update(item.id, { ...tunnel, length: 11 });
      h.result.current.remove(item.id);
      expect(h.result.current.moveSelected([1, 0, 0])).toBe(true);
      h.result.current.cancel();
    });
    act(() => { h.result.current.arm(tunnel); });
    expect(h.result.current.placing).toBeNull();
    expect(event.preventDefault).not.toHaveBeenCalled();
    expect(h.result.current.modules).toEqual([item]);
    expect(h.studio.canvas.dug).toEqual(before);
  });

  it('drags a component by its body across its level: only the outline travels, and the release is one move', () => {
    const h = mount();
    const item = h.place(tunnel, [24, 11, 28]);
    const original = new Set(h.studio.canvas.dug);
    const edits = vi.spyOn(h.studio, 'editWithHistory');
    h.pointer('pointerdown', [24, 11, 28]);
    expect(h.result.current.selectedId).toBe(item.id);
    // Under the threshold nothing starts; then the outline follows the pointer on the piece's own level.
    h.pointer('pointermove', [24, 11, 28]);
    expect(h.result.current.modules[0]!.at).toEqual([24, 11, 28]);
    h.pointer('pointermove', [27, 11, 31]);
    h.pointer('pointermove', [30, 11, 34]);
    expect(h.setMinePlacementGeometry).toHaveBeenLastCalledWith(expect.any(Float32Array), [30, 11, 34], false);
    expect(h.studio.canvas.dug).toEqual(original);
    expect(edits).not.toHaveBeenCalled();
    h.pointer('pointerup', [30, 11, 34]);
    expect(edits).toHaveBeenCalledOnce();
    expect(h.result.current.modules[0]!.at).toEqual([30, 11, 34]);
    expect(h.result.current.selectedId).toBe(item.id);
    expect(h.setMinePlacement).toHaveBeenLastCalledWith(null);
    act(() => { h.studio.undo(); });
    expect(h.result.current.modules[0]!.at).toEqual([24, 11, 28]);
    expect(h.studio.canvas.dug).toEqual(original);
  });

  it('a body drag snaps to an open port of another piece, a plain click only selects, and Escape puts the piece back', () => {
    const h = mount();
    const first = h.place(tunnel, [10, 11, 28]);
    const second = h.place(tunnel, [30, 11, 44]);
    const near: V3 = [first.at[0] + 6, 11, first.at[2] + 1];
    const snapped = snapModuleAt(tunnel, near, [first]);
    expect(snapped).not.toEqual(near);
    h.pointer('pointerdown', [30, 11, 44]);
    h.pointer('pointermove', near);
    expect(h.setMinePlacementGeometry).toHaveBeenLastCalledWith(expect.any(Float32Array), snapped, false);
    // The first piece's openings are marked, and the one the dragged piece joins is marked as joined; its own are not.
    const [open, joined] = h.setMinePorts.mock.lastCall as [V3[], V3[]];
    expect(open).toEqual(openPortCells([first]));
    expect(joined).toHaveLength(1);
    expect(open).toContainEqual(joined[0]);
    expect(joined).toEqual(joinedPortCells(tunnel, snapped, [first]));
    h.pointer('pointerup', near);
    expect(h.result.current.modules.find((m) => m.id === second.id)!.at).toEqual(snapped);

    // A click on the first selects it and moves nothing.
    const before = h.result.current.modules;
    h.pointer('pointerdown', [10, 11, 28]);
    h.pointer('pointerup', [10, 11, 28]);
    expect(h.result.current.selectedId).toBe(first.id);
    expect(h.result.current.modules).toBe(before);

    // Escape during a body drag cancels it, before the page's Escape could clear the selection.
    const edits = vi.spyOn(h.studio, 'editWithHistory');
    h.pointer('pointerdown', [10, 11, 28]);
    h.pointer('pointermove', [16, 11, 20]);
    const escape = new KeyboardEvent('keydown', { key: 'Escape', cancelable: true });
    expect(h.result.current.handlesKey(escape)).toBe(true);
    act(() => { window.dispatchEvent(escape); });
    expect(escape.defaultPrevented).toBe(true);
    expect(h.setMinePlacement).toHaveBeenLastCalledWith(null);
    h.pointer('pointerup', [16, 11, 20]);
    expect(edits).not.toHaveBeenCalled();
    expect(h.result.current.modules.find((m) => m.id === first.id)!.at).toEqual([10, 11, 28]);
    expect(h.result.current.selectedId).toBe(first.id);
  });

  it('marks the openings a piece in hand can join, and the one it snaps to', () => {
    const h = mount();
    const first = h.place(tunnel, [10, 11, 28]);
    act(() => { h.result.current.arm(tunnel); });
    h.pointer('pointermove', [40, 11, 40]);
    expect(h.setMinePorts).toHaveBeenLastCalledWith(openPortCells([first]), []);
    const near: V3 = [first.at[0] + 6, 11, first.at[2] + 1];
    h.pointer('pointermove', near);
    const snapped = snapModuleAt(tunnel, near, [first]);
    expect(h.result.current.hover).toEqual(snapped);
    expect(h.setMinePorts).toHaveBeenLastCalledWith(openPortCells([first]), joinedPortCells(tunnel, snapped, [first]));
    expect(h.setMinePorts.mock.lastCall![1]).toHaveLength(1);
  });

  it('clears a preview on pointer leave and cancels an armed creation with Escape without changing the plan', () => {
    const h = mount();
    const item = h.place(tunnel, [20, 21, 20]);
    const before = new Set(h.studio.canvas.dug);
    act(() => { h.result.current.arm(tunnel); });
    expect(h.result.current.placementY).toBe(21);
    expect(h.focus).not.toHaveBeenCalled();
    h.pointer('pointermove', [40, 21, 40]);
    expect(h.result.current.hover).toEqual([40, 21, 40]);
    h.pointer('pointerleave', [40, 21, 40]);
    expect(h.result.current.hover).toBeNull();
    h.pointer('pointermove', [40, 21, 40]);
    const escape = new KeyboardEvent('keydown', { key: 'Escape', cancelable: true });
    act(() => { window.dispatchEvent(escape); });
    expect(escape.defaultPrevented).toBe(true);
    expect(h.result.current.placing).toBeNull();
    expect(h.result.current.hover).toBeNull();
    expect(h.setMinePlacement).toHaveBeenLastCalledWith(null);
    h.pointer('pointerdown', [40, 21, 40]);
    expect(h.result.current.modules).toEqual([item]);
    expect(h.studio.canvas.dug).toEqual(before);
    h.unmount();
    const afterUnmount = new KeyboardEvent('keydown', { key: 'Escape', cancelable: true });
    window.dispatchEvent(afterUnmount);
    expect(afterUnmount.defaultPrevented).toBe(false);
  });

  it('gives a piece in hand [ ] and R, leaves the selected component to neither the arrows nor Delete, and cancels on a right click', () => {
    const h = mount();
    const item = h.place(tunnel, [20, 30, 20]);
    const before = new Set(h.studio.canvas.dug);
    const key = (init: KeyboardEventInit) => {
      const event = new KeyboardEvent('keydown', { cancelable: true, ...init });
      act(() => { window.dispatchEvent(event); });
      return event;
    };
    act(() => { h.result.current.arm(tunnel); });
    h.pointer('pointermove', [40, 30, 40]);
    const drawn = h.setMinePlacementGeometry.mock.calls.length;
    expect(key({ key: ']' }).defaultPrevented).toBe(true);
    expect(h.result.current.placing!.rotation).toBe(1);
    // Turned where the pointer is, without waiting for it to move.
    expect(h.setMinePlacementGeometry.mock.calls.length).toBeGreaterThan(drawn);
    key({ key: 'r', code: 'KeyR' });
    expect(h.result.current.placing!.rotation).toBe(2);
    key({ key: 'R', code: 'KeyR', shiftKey: true });
    key({ key: '[' });
    expect(h.result.current.placing!.rotation).toBe(0);
    for (const name of ['ArrowRight', 'PageUp', 'Delete', 'Backspace']) expect(key({ key: name }).defaultPrevented).toBe(true);
    expect(h.result.current.modules).toEqual([item]);
    expect(h.studio.canvas.dug).toEqual(before);

    // The page's own key handling leaves every one of those keys to the piece in hand.
    const moveSel = vi.fn(), applyTransform = vi.fn(), turnFacing = vi.fn(), edit = vi.spyOn(h.studio, 'edit');
    const actions = {
      studio: h.studio, state: () => ({ drag: null, pasting: false, under: null }) as unknown as InputState, inWorld: () => true,
      moveSel, applyTransform, turnFacing, handlesKey: (e: KeyboardEvent) => h.result.current.handlesKey(e),
    } as unknown as KeyActions;
    for (const init of [{ key: 'ArrowRight' }, { key: ']' }, { key: 'Delete' }, { key: 'r', code: 'KeyR' }, { key: 'Escape' }]) {
      act(() => { studioKey(new KeyboardEvent('keydown', { cancelable: true, ...init }), actions); });
    }
    expect(moveSel).not.toHaveBeenCalled();
    expect(applyTransform).not.toHaveBeenCalled();
    expect(turnFacing).not.toHaveBeenCalled();
    expect(edit).not.toHaveBeenCalled();
    expect(h.result.current.placing).not.toBeNull();

    const ordinaryTool = vi.fn();
    h.surface.addEventListener('pointerdown', ordinaryTool);
    expect(h.pointer('pointerdown', [40, 30, 40], { button: 2 }).defaultPrevented).toBe(true);
    expect(ordinaryTool).not.toHaveBeenCalled();
    expect(h.result.current.placing).toBeNull();
    expect(h.setMinePlacement).toHaveBeenLastCalledWith(null);
    expect(h.result.current.modules).toEqual([item]);
    // With nothing in hand, ] turns the selected component again.
    h.surface.focus();
    key({ key: ']' });
    expect(h.result.current.modules[0]!.spec.rotation).toBe(1);
  });

  it('never lets the page key handler act before the component Delete, even when the page listens first', () => {
    let actions: KeyActions | null = null;
    const page = (e: KeyboardEvent) => { if (actions) studioKey(e, actions); };
    window.addEventListener('keydown', page, true);
    try {
      const h = mount();
      actions = {
        studio: h.studio, state: () => ({ drag: null, pasting: false, under: null }) as unknown as InputState, inWorld: () => true,
        moveSel: vi.fn(), applyTransform: vi.fn(), handlesKey: (e: KeyboardEvent) => h.result.current.handlesKey(e),
      } as unknown as KeyActions;
      const item = h.place(tunnel, [20, 30, 20]);
      const edit = vi.spyOn(h.studio, 'edit');
      const event = new KeyboardEvent('keydown', { key: 'Delete', cancelable: true });
      act(() => { window.dispatchEvent(event); });
      expect(event.defaultPrevented).toBe(true);
      expect(edit).not.toHaveBeenCalled();
      expect(h.result.current.modules).toHaveLength(0);
      expect(h.studio.canvas.dug.size).toBe(0);
      act(() => { h.studio.undo(); });
      expect(h.result.current.modules).toEqual([item]);
      expect(h.studio.canvas.dug.size).toBe(10);
    } finally {
      window.removeEventListener('keydown', page, true);
    }
  });

  it('refuses a raw move, turn or mirror over component cells and says to select the component', () => {
    const h = mount();
    h.place(tunnel, [20, 30, 20]);
    expect(h.result.current.refuseRaw({ min: [0, 30, 0], max: [3, 31, 3] })).toBe(false);
    expect(h.notice).not.toHaveBeenCalledWith('Select the component to move it.');
    expect(h.result.current.refuseRaw({ min: [19, 29, 19], max: [21, 31, 21] })).toBe(true);
    expect(h.notice).toHaveBeenLastCalledWith('Select the component to move it.');
  });

  it('cancels native drag placement after a rejected drop, without cancelling ordinary click placement on dragend', () => {
    const h = mount();
    act(() => { h.result.current.arm(tunnel, null, { drag: true }); });
    expect(h.focus).not.toHaveBeenCalled();
    h.pointer('pointermove', [30, 11, 30]);
    expect(h.result.current.hover).toEqual([30, 11, 30]);
    const invalid = h.drag([0, 11, 30]);
    act(() => { h.result.current.drop(invalid as unknown as DragEvent); });
    expect(h.notice).toHaveBeenLastCalledWith(expect.stringContaining('whole layout must fit'));
    act(() => { window.dispatchEvent(new Event('dragend')); });
    expect(h.result.current.placing).toBeNull();
    expect(h.result.current.hover).toBeNull();
    expect(h.setMinePlacement).toHaveBeenLastCalledWith(null);
    h.pointer('pointerdown', [30, 11, 30]);
    expect(h.result.current.modules).toHaveLength(0);
    expect(h.studio.snapshot().canUndo).toBe(false);

    act(() => { h.result.current.arm(tunnel); });
    act(() => { window.dispatchEvent(new Event('dragend')); });
    expect(h.result.current.placing).toEqual(tunnel);
  });

  it('leaves Alt-left and middle-button camera gestures alone while placing or selecting components', () => {
    const h = mount();
    const item = h.place(tunnel, [30, 25, 30]);
    act(() => { h.studio.setSelection(null); });
    expect(h.pointer('pointerdown', [30, 26.5, 30], { altKey: true }).defaultPrevented).toBe(false);
    expect(h.pointer('pointerdown', [30, 26.5, 30], { button: 1 }).defaultPrevented).toBe(false);
    expect(h.result.current.selectedId).toBeNull();
    act(() => { h.result.current.arm(tunnel); });
    expect(h.pointer('pointerdown', [40, 25, 40], { altKey: true }).defaultPrevented).toBe(false);
    expect(h.pointer('pointerdown', [40, 25, 40], { button: 1 }).defaultPrevented).toBe(false);
    h.pointer('pointermove', [40, 25, 40], { altKey: true });
    expect(h.result.current.hover).toBeNull();
    expect(h.result.current.modules).toEqual([item]);
    expect(h.result.current.placing).toEqual(tunnel);
  });

  it('moves a component to typed coordinates as an undoable edit and refuses invalid or read-only positions', () => {
    const h = mount();
    const item = h.place(tunnel, [20, 25, 20]);
    const original = new Set(h.studio.canvas.dug);
    act(() => { h.result.current.position(item.id, [35, 30, 40]); });
    expect(h.result.current.modules[0]!.at).toEqual([35, 30, 40]);
    expect(h.studio.selection?.min).toEqual([33, 30, 40]);
    expect(h.result.current.selectedId).toBe(item.id);
    act(() => { h.studio.undo(); });
    expect(h.result.current.modules).toEqual([item]);
    expect(h.studio.canvas.dug).toEqual(original);
    act(() => { h.result.current.position(item.id, [0, 25, 20]); });
    expect(h.result.current.modules).toEqual([item]);
    expect(h.notice).toHaveBeenLastCalledWith(expect.stringContaining('whole layout must fit'));
    h.rerender({ ...h.props, canEdit: false });
    act(() => { h.result.current.position(item.id, [35, 30, 40]); });
    expect(h.result.current.modules).toEqual([item]);
    expect(h.studio.canvas.dug).toEqual(original);
  });

  it('frames a component without editing it, cancels pending placement, and offers F or numpad decimal while read-only', () => {
    const h = mount();
    const item = h.place(tunnel, [30, 25, 30]);
    const original = new Set(h.studio.canvas.dug);
    act(() => { h.result.current.arm(tunnel); });
    act(() => { h.result.current.frame(item.id); });
    expect(h.result.current.placing).toBeNull();
    expect(h.result.current.selectedId).toBe(item.id);
    expect(h.focus).toHaveBeenLastCalledWith(30, 25.5, 30, expect.anything());
    expect(document.activeElement).toBe(h.surface);
    h.focus.mockClear();
    h.rerender({ ...h.props, canEdit: false });
    for (const code of ['KeyF', 'NumpadDecimal']) {
      const event = new KeyboardEvent('keydown', { code, key: code === 'KeyF' ? 'f' : '.', cancelable: true });
      act(() => { window.dispatchEvent(event); });
      expect(event.defaultPrevented).toBe(true);
    }
    expect(h.focus).toHaveBeenCalledTimes(2);
    expect(h.result.current.modules).toEqual([item]);
    expect(h.studio.canvas.dug).toEqual(original);
    const input = document.createElement('input');
    document.body.append(input); input.focus();
    const typing = new KeyboardEvent('keydown', { code: 'KeyF', key: 'f', cancelable: true });
    act(() => { window.dispatchEvent(typing); });
    expect(typing.defaultPrevented).toBe(false);
    expect(h.focus).toHaveBeenCalledTimes(2);
    input.remove();
  });

  it('places automatic access on the rotated surface entrance without moving the camera, then continues at its lower exit', () => {
    const h = mount();
    h.camera.position.set(30.5, 110, 30.5);
    h.camera.up.set(0, 0, -1);
    h.camera.lookAt(30.5, 50, 30.5);
    h.camera.updateMatrixWorld();
    const spec = { ...defaultModule('spiral'), rotation: 1, depth: 12 };
    act(() => { h.result.current.arm(spec); });
    expect(h.result.current.placementSurface).toBe(true);
    expect(h.focus).not.toHaveBeenCalled();
    const surfaceY = h.studio.terrain!.surface(30, 30) + 1;
    h.pointer('pointermove', [30, surfaceY, 30]);
    const at = h.result.current.hover!;
    const entry = moduleCells(spec, at).entry;
    expect(entry[1]).toBe(h.studio.terrain!.surface(entry[0], entry[2]) + 1);
    expect(at[1]).toBeGreaterThan(50);
    h.pointer('pointerdown', [30, surfaceY, 30]);
    expect(h.result.current.modules[0]!.at).toEqual(at);
    act(() => { h.result.current.arm(tunnel); });
    expect(h.result.current.placementSurface).toBe(false);
    expect(h.result.current.placementY).toBe(Math.min(...modulePorts(spec, at).map((port) => port.at[1])));
    expect(h.focus).not.toHaveBeenCalled();
  });

  it('keeps explicit mining heights across new access pieces and lets Surface override connection context', () => {
    const h = mount();
    const item = h.place(tunnel, [30, 27, 30]);
    act(() => { h.result.current.arm(defaultModule('stairs')); });
    expect(h.result.current.placementMode).toBe('fixed');
    expect(h.result.current.placementY).toBe(27);
    expect(h.result.current.placementSurface).toBe(false);
    act(() => { h.result.current.setPlacementMode('surface'); });
    expect(h.result.current.placementSurface).toBe(true);
    act(() => { h.result.current.cancel(); h.result.current.select(item.id); });
    act(() => { h.result.current.arm(defaultModule('spiral'), null, { drag: true }); });
    expect(h.result.current.placementSurface).toBe(true);
    act(() => { h.result.current.setPlacementY(16); });
    expect(h.result.current.placementMode).toBe('fixed');
    expect(h.result.current.placementY).toBe(16);
    act(() => { h.result.current.cancel(); });
    act(() => { h.result.current.arm(defaultModule('spiral')); });
    expect(h.result.current.placementY).toBe(16);
    expect(h.result.current.placementSurface).toBe(false);
    expect(h.focus).not.toHaveBeenCalled();
  });

  it('copies and pastes component configuration and original height with a new identity and normal undo', () => {
    const h = mount();
    const spec = { ...defaultModule('branches'), rotation: 3, length: 13, width: 2, height: 3, branchLength: 9, spacing: 7 };
    const original = h.place(spec, [30, 30, 30]);
    const key = (key: string) => {
      const event = new KeyboardEvent('keydown', { key, ctrlKey: true, cancelable: true });
      act(() => { window.dispatchEvent(event); });
      return event;
    };
    expect(key('c').defaultPrevented).toBe(true);
    expect(h.result.current.clipboard).toEqual({ spec, level: 30 });
    act(() => { h.result.current.update(original.id, { ...spec, length: 19 }); h.result.current.select(null); });
    expect(key('v').defaultPrevented).toBe(true);
    expect(h.activateSelection).toHaveBeenCalled();
    expect(h.result.current.placing).toEqual(spec);
    expect(h.result.current.placementY).toBe(30);
    h.pointer('pointerdown', [45, 30, 50]);
    expect(h.result.current.modules).toHaveLength(2);
    expect(h.result.current.modules[1]!.id).not.toBe(original.id);
    expect(h.result.current.modules[1]!.spec).toEqual(spec);
    expect(h.result.current.modules[0]!.spec.length).toBe(19);
    act(() => { h.studio.undo(); });
    expect(h.result.current.modules).toHaveLength(1);
    expect(h.result.current.modules[0]!.id).toBe(original.id);
    expect(h.focus).not.toHaveBeenCalled();
  });

  it('duplicates composite components as editable groups and leaves typing and read-only paste alone', () => {
    const h = mount();
    const first = h.place(tunnel, [30, 25, 30]);
    const second = h.place(defaultModule('spiral'), [40, 30, 30]);
    const before = new Set(h.studio.canvas.dug);
    act(() => { h.result.current.combine([first.id, second.id]); });
    expect(h.result.current.modules).toHaveLength(1);
    const combined = h.result.current.modules[0]!;
    expect(combined.spec.kind).toBe('custom');
    expect(h.studio.canvas.dug).toEqual(before);
    const duplicateKey = new KeyboardEvent('keydown', { key: 'd', metaKey: true, cancelable: true });
    act(() => { window.dispatchEvent(duplicateKey); });
    expect(duplicateKey.defaultPrevented).toBe(true);
    expect(h.result.current.placing).toEqual(combined.spec);
    expect(h.result.current.placing).not.toBe(combined.spec);
    expect(h.result.current.clipboard!.spec.parts).not.toBe(combined.spec.parts);
    expect(h.result.current.placementY).toBe(combined.at[1]);
    h.pointer('pointerdown', [50, combined.at[1], 50]);
    expect(h.result.current.modules).toHaveLength(2);
    expect(h.result.current.modules[1]!.spec).toEqual(combined.spec);
    expect(h.result.current.modules[1]!.id).not.toBe(combined.id);
    const rotate = new KeyboardEvent('keydown', { key: ']', cancelable: true });
    act(() => { window.dispatchEvent(rotate); });
    expect(h.result.current.modules[1]!.spec.rotation).toBe(1);
    expect(h.result.current.modules[1]!.spec.parts).toEqual(combined.spec.parts);
    expect(h.result.current.modules[0]!.spec.rotation).toBe(0);
    expect(h.result.current.modules[1]!.spec.kind).toBe('custom');
    const input = document.createElement('input'); document.body.append(input); input.focus();
    const typing = new KeyboardEvent('keydown', { key: 'v', ctrlKey: true, cancelable: true });
    act(() => { window.dispatchEvent(typing); });
    expect(typing.defaultPrevented).toBe(false);
    expect(h.result.current.placing).toBeNull();
    input.remove(); h.surface.focus();
    h.rerender({ ...h.props, canEdit: false });
    act(() => { h.result.current.paste(); h.result.current.duplicate(); h.result.current.combine(h.result.current.modules.map((m) => m.id)); });
    expect(h.result.current.placing).toBeNull();
    expect(h.result.current.modules).toHaveLength(2);
  });

  it('reopens the layout this browser kept, says so through the notice, and returns the note', () => {
    const first = mount();
    const item = first.place(tunnel, [20, 30, 20]);
    const dug = first.studio.canvas.holes();
    first.unmount();
    const h = mount({ dug });
    expect(h.result.current.modules).toEqual([item]);
    expect(h.result.current.restoreNote).toBe('Recovered unsaved component changes from this browser. Save the design to keep them.');
    // A notice that stays until dismissed: the page would otherwise replace it with the next passing one.
    expect(h.notice).toHaveBeenCalledWith(h.result.current.restoreNote, { tone: 'warn', sticky: true });
    expect(h.studio.snapshot().dirty).toBe(true);
    // Nothing to say when nothing was kept.
    h.unmount();
    localStorage.clear();
    const fresh = mount();
    expect(fresh.result.current.restoreNote).toBeNull();
    expect(fresh.notice).not.toHaveBeenCalled();
  });

  it('in a development build, the second controller React builds and drops never writes its layout over the kept one', () => {
    const first = mount();
    const item = first.place(tunnel, [20, 30, 20]);
    const dug = first.studio.canvas.holes();
    first.unmount();
    const h = mount({ dug }, true);
    expect(h.result.current.modules).toEqual([item]);
    const added = h.place(tunnel, [40, 30, 40]);
    // A hand edit inside the first component: the dropped controller still holds it, and must not answer.
    act(() => { h.studio.edit([[21, 30, 20, 50, 5]], 'place', 'manual torch'); });
    const kept = localStorage.getItem('mineclaw.mine-modules.v2:test:901')!;
    const copy = JSON.parse(kept) as { modules: Array<{ id: string }>; overrides: number[][] };
    expect(copy.modules.map((m) => m.id)).toEqual([item.id, added.id]);
    // The kept one follows the canvas again after React's trial unmount, and stops for good on the real one.
    expect(copy.overrides).toEqual([[keyOf(21, 30, 20), 50 * 16 + 5]]);
    h.unmount();
    h.studio.edit([[22, 30, 20, 50, 5]], 'place', 'another torch');
    expect(localStorage.getItem('mineclaw.mine-modules.v2:test:901')).toBe(kept);
  });

  it('authors and selects components beyond the old 64-block boundary', () => {
    const h = mount();
    expect(plotMode().size).toBe(256);
    h.camera.position.set(200.5, 110, 200.5);
    h.camera.up.set(0, 0, -1);
    h.camera.lookAt(200.5, 20, 200.5);
    h.camera.updateMatrixWorld();
    const item = h.place(tunnel, [200, 20, 200]);
    expect(item.at).toEqual([200, 20, 200]);
    expect(h.studio.canvas.dug.has(keyOf(202, 21, 200))).toBe(true);
    act(() => { h.result.current.select(null); });
    h.pointer('pointerdown', [200, 21.5, 200]);
    expect(h.result.current.selectedId).toBe(item.id);
  });

  it('Ctrl+G combines the ticked pieces and Ctrl+Shift+G ungroups the selected group, each one undo step', () => {
    const h = mount();
    const a = h.place(tunnel, [20, 30, 20]), b = h.place(tunnel, [20, 30, 30]);
    const dug = new Set(h.studio.canvas.dug);
    const key = (init: KeyboardEventInit, target: EventTarget = window) => {
      const event = new KeyboardEvent('keydown', { key: 'g', ctrlKey: true, cancelable: true, bubbles: true, ...init });
      act(() => { target.dispatchEvent(event); });
      return event;
    };
    // Nothing ticked yet: the checklist opens with the selected piece and says what to do.
    expect(key({}).defaultPrevented).toBe(true);
    expect(h.result.current.checked).toEqual([b.id]);
    expect(h.notice).toHaveBeenLastCalledWith('Tick at least two components under Combine, then press Ctrl+G again.');
    expect(h.result.current.modules).toEqual([a, b]);
    // Ticked in the checklist, with a checkbox focused: Ctrl+G combines them.
    act(() => { h.result.current.setChecked([b.id, a.id]); });
    const box = document.createElement('input');
    box.type = 'checkbox';
    document.body.append(box);
    expect(key({}, box).defaultPrevented).toBe(true);
    box.remove();
    const group = h.result.current.modules[0]!;
    expect(h.result.current.modules).toHaveLength(1);
    expect(group.spec.kind).toBe('custom');
    expect(h.result.current.checked).toBeNull();
    expect(h.result.current.selectedId).toBe(group.id);
    expect(h.studio.canvas.dug).toEqual(dug);
    // Typing in a text field keeps the browser's own Ctrl+Shift+G.
    const input = document.createElement('input');
    document.body.append(input);
    expect(key({ key: 'G', shiftKey: true }, input).defaultPrevented).toBe(false);
    input.remove();
    expect(h.result.current.modules).toEqual([group]);
    expect(key({ key: 'G', shiftKey: true }).defaultPrevented).toBe(true);
    expect(h.result.current.modules).toMatchObject([{ spec: a.spec, at: a.at }, { spec: b.spec, at: b.at }]);
    expect(h.result.current.selectedId).toBeNull();
    expect(h.studio.canvas.dug).toEqual(dug);
    act(() => { h.studio.undo(); });
    expect(h.result.current.modules).toEqual([group]);
    act(() => { h.studio.undo(); });
    expect(h.result.current.modules).toEqual([a, b]);
    // A plain component has nothing to ungroup: the key is not taken.
    act(() => { h.result.current.select(a.id); });
    expect(key({ key: 'G', shiftKey: true }).defaultPrevented).toBe(false);
  });

  it('ungroups from the inspector and removes a row that is not selected without dropping the selection', () => {
    const h = mount();
    const a = h.place(tunnel, [20, 30, 20]), b = h.place(tunnel, [20, 30, 30]), c = h.place(tunnel, [40, 30, 40]);
    act(() => { h.result.current.combine([a.id, b.id]); });
    const group = h.result.current.modules[1]!;
    act(() => { h.result.current.ungroup(group.id); });
    expect(h.result.current.modules).toMatchObject([{ id: c.id }, { at: a.at }, { at: b.at }]);
    expect(h.notice).toHaveBeenLastCalledWith('Ungrouped into 2 components. Select one to edit it, or undo to group them again.');
    act(() => { h.result.current.select(c.id); });
    act(() => { h.result.current.remove(h.result.current.modules[1]!.id); });
    expect(h.result.current.modules).toHaveLength(2);
    expect(h.result.current.selectedId).toBe(c.id);
    act(() => { h.result.current.remove(c.id); });
    expect(h.result.current.selectedId).toBeNull();
  });

  it('works at a chosen level: the view cut follows, PgUp / PgDn step it with nothing selected, and a paste lands there', () => {
    const h = mount();
    act(() => { h.result.current.setPlacementY(16); });
    expect(h.result.current.workLevel).toBe(16);
    expect(h.onWorkLevel).toHaveBeenLastCalledWith(16);
    const moveSel = vi.fn();
    const actions = {
      studio: h.studio, state: () => ({ drag: null }) as unknown as InputState, inWorld: () => true, moveSel,
      stepLevel: (by: number) => h.result.current.stepLevel(by), handlesKey: (e: KeyboardEvent) => h.result.current.handlesKey(e),
    } as unknown as KeyActions;
    const press = (init: KeyboardEventInit) => {
      const event = new KeyboardEvent('keydown', { cancelable: true, ...init });
      act(() => { studioKey(event, actions); });
      return event;
    };
    expect(press({ key: 'PageUp' }).defaultPrevented).toBe(true);
    expect(h.result.current.workLevel).toBe(17);
    expect(h.onWorkLevel).toHaveBeenLastCalledWith(17);
    press({ key: 'PageDown', shiftKey: true });
    expect(h.result.current.workLevel).toBe(12);
    // A piece in hand goes up with the level, where the pointer is (the hook's own key listener takes PgUp then).
    act(() => { h.result.current.arm(tunnel); });
    h.pointer('pointermove', [30, 12, 30]);
    const up = new KeyboardEvent('keydown', { key: 'PageUp', cancelable: true });
    expect(h.result.current.handlesKey(up)).toBe(true);
    act(() => { window.dispatchEvent(up); });
    expect(h.result.current.placementY).toBe(13);
    expect(h.result.current.hover?.[1]).toBe(13);
    act(() => { h.result.current.cancel(); });
    // Automatic height has no working level, and the first press starts from the height shown.
    act(() => { h.result.current.setPlacementMode('auto'); });
    expect(h.result.current.workLevel).toBeNull();
    act(() => { h.result.current.setPlacementMode('fixed'); });
    expect(h.onWorkLevel).toHaveBeenLastCalledWith(13);

    // A paste goes to the working level; a duplicate stays at its original's height.
    const item = h.place(tunnel, [30, 30, 30]);
    act(() => { h.result.current.copy(item.id); h.result.current.setPlacementY(20); });
    act(() => { h.result.current.paste(); });
    expect(h.result.current.placementY).toBe(20);
    expect(h.notice).toHaveBeenLastCalledWith('Place a copy at Y20. Its settings are preserved; click to place or Escape to cancel.');
    act(() => { h.result.current.cancel(); h.result.current.duplicate(item.id); });
    expect(h.result.current.placementY).toBe(30);
    act(() => { h.result.current.cancel(); h.result.current.setPlacementMode('auto'); });
    act(() => { h.result.current.paste(); });
    expect(h.result.current.placementY).toBe(30);
    act(() => { h.result.current.cancel(); });

    // With a component selected, PgUp moves the selection and leaves the level alone.
    act(() => { h.result.current.select(item.id); });
    press({ key: 'PageUp' });
    expect(moveSel).toHaveBeenCalledWith([0, 1, 0]);
    expect(h.result.current.workLevel).toBeNull();
  });

  it('steps the working level with PgUp / PgDn while a piece is in hand and the last one placed is still selected', () => {
    const h = mount();
    const item = h.place(tunnel, [20, 16, 20]);
    expect(h.result.current.selectedId).toBe(item.id);
    expect(h.studio.selection).not.toBeNull();
    act(() => { h.result.current.arm(tunnel); });
    h.pointer('pointermove', [30, 16, 30]);
    const key = (init: KeyboardEventInit) => {
      const event = new KeyboardEvent('keydown', { cancelable: true, ...init });
      act(() => { window.dispatchEvent(event); });
      return event;
    };
    expect(key({ key: 'PageUp' }).defaultPrevented).toBe(true);
    expect(h.result.current.placementY).toBe(17);
    expect(h.result.current.hover?.[1]).toBe(17);
    key({ key: 'PageDown', shiftKey: true });
    expect(h.result.current.placementY).toBe(12);
    // The selected component stays where it is; the arrows still do nothing while the piece is in hand.
    expect(key({ key: 'ArrowRight' }).defaultPrevented).toBe(true);
    expect(h.result.current.modules).toEqual([item]);
  });

  it('looks straight down at the working level and frames the whole design', () => {
    const h = mount();
    act(() => { h.result.current.frameAll(); });
    expect(h.focus).not.toHaveBeenCalled();
    expect(h.notice).toHaveBeenLastCalledWith('Nothing to frame yet: place a layout or a preset first.');
    h.place(tunnel, [30, 20, 30]);
    h.place(tunnel, [40, 24, 34]);
    act(() => { h.result.current.setPlacementY(22); });
    type Focus = [number, number, number, { distance: number; pitch?: number }];
    act(() => { h.result.current.topView(); });
    const top = h.focus.mock.lastCall as Focus;
    expect(top.slice(0, 3)).toEqual([35, 22, 32]);
    expect(top[3].pitch).toBe(89.9);
    expect(top[3].distance).toBeGreaterThan(5);
    act(() => { h.result.current.frameAll(); });
    const all = h.focus.mock.lastCall as Focus;
    expect(all.slice(0, 3)).toEqual([35, 22.5, 32]);
    expect(all[3].pitch).toBeUndefined();
    expect(all[3].distance).toBeGreaterThan(top[3].distance);
    expect(document.activeElement).toBe(h.surface);
  });
});
