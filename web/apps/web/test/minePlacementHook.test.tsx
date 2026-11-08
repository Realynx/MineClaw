import type { DesignerSession } from '@mineclaw/contracts';
import { MOCK_DESIGNER_PALETTE } from '@mineclaw/contracts';
import type { WorldViewerHandle } from '@mineclaw/world-viewer';
import { act, cleanup, renderHook } from '@testing-library/react';
import type { DragEvent } from 'react';
import { PerspectiveCamera, Raycaster, Vector2, Vector3 } from 'three';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { keyOf, setPlotMode, type V3 } from '@/features/studio/canvas';
import { defaultModule, encodeModule, MINE_MODULE_MIME, type MineModuleSpec } from '@/features/studio/mine/modules';
import type { Overlay3D } from '@/features/studio/overlay3d';
import { Studio, type StudioApi } from '@/features/studio/studio';
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
function mount() {
  const studio = new Studio(session(), api, 60_000);
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
  const pickHandle = vi.fn(() => null);
  const notice = vi.fn();
  const props = {
    studio, viewer, mod: { Raycaster, Vector2 } as typeof ViewerMod,
    overlay: { setMinePlacement, pickHandle } as unknown as Overlay3D,
    ready: true, canEdit: true, selecting: true, notice,
  };
  const hook = renderHook((p: typeof props) => useMinePlacement(p), { initialProps: props });
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
  return { ...hook, props, studio, surface, focus, setMinePlacement, pickHandle, notice, pointer, place, drag };
}

describe('mine module pointer authoring', () => {
  it('previews at the chosen mining level and commits a click before ordinary canvas tools handle it', () => {
    const h = mount();
    const ordinaryTool = vi.fn();
    h.surface.addEventListener('pointerdown', ordinaryTool);
    act(() => { h.result.current.arm(tunnel); });
    expect(h.focus).toHaveBeenCalledWith(31, 11, 31, { distance: 40, pitch: 55 });
    h.pointer('pointermove', [24, 11, 28]);
    expect(h.result.current.hover).toEqual([24, 11, 28]);
    expect(h.setMinePlacement).toHaveBeenLastCalledWith(expect.arrayContaining([[22, 11, 28], [26, 12, 28]]));
    expect(h.studio.canvas.dug.size).toBe(0);
    const previewCalls = h.setMinePlacement.mock.calls.length;
    h.pointer('pointermove', [24, 11, 28]);
    expect(h.setMinePlacement).toHaveBeenCalledTimes(previewCalls);

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

  it('moves an overlapping layout through gizmo deltas, retaining the other tunnel and undoing the whole gesture', () => {
    const h = mount();
    const first = h.place(tunnel, [20, 30, 20]);
    const second = h.place({ ...tunnel, rotation: 1 }, [20, 30, 20]);
    expect(second.id).not.toBe(first.id);
    expect(h.studio.canvas.dug.size).toBe(18);
    const original = new Set(h.studio.canvas.dug);
    act(() => { expect(h.result.current.moveSelected([1, 0, 0], 'axis-drag')).toBe(true); });
    act(() => { expect(h.result.current.moveSelected([1, 0, 0], 'axis-drag')).toBe(true); });
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

  it('moves a piece with edited dimensions as one replacement rather than adding a duplicate', () => {
    const h = mount();
    const item = h.place(tunnel, [20, 30, 20]);
    const original = new Set(h.studio.canvas.dug);
    act(() => { h.result.current.move(item.id); });
    act(() => { h.result.current.updatePlacing({ ...tunnel, length: 9 }); });
    h.pointer('pointerdown', [40, 30, 40]);
    expect(h.result.current.modules).toHaveLength(1);
    expect(h.result.current.modules[0]).toMatchObject({ id: item.id, at: [40, 30, 40], spec: { length: 9 } });
    expect(h.studio.canvas.dug.size).toBe(18);
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
    expect(h.result.current.hover).toBeNull();
    expect(h.setMinePlacement).toHaveBeenLastCalledWith(null);
    h.pointer('pointerdown', [0, 11, 30]);
    expect(h.result.current.modules).toHaveLength(0);
    expect(h.studio.snapshot().canUndo).toBe(false);
    expect(h.result.current.placing).toEqual(tunnel);
    expect(h.notice).toHaveBeenLastCalledWith(expect.stringContaining('does not fit'));

    act(() => { h.result.current.arm({ ...defaultModule('stairs'), depth: 12 }); });
    h.pointer('pointerdown', [30, 11, 30]);
    expect(h.result.current.modules).toHaveLength(0);
    const item = h.place(tunnel, [30, 11, 30]);
    const before = new Set(h.studio.canvas.dug);
    act(() => { expect(h.result.current.moveSelected([80, 0, 0])).toBe(true); });
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

  it('clears a preview on pointer leave and cancels an armed move with Escape without changing the plan', () => {
    const h = mount();
    const item = h.place(tunnel, [20, 21, 20]);
    const before = new Set(h.studio.canvas.dug);
    act(() => { h.result.current.setPlacementY(40); });
    act(() => { h.result.current.move(item.id); });
    expect(h.result.current.placementY).toBe(21);
    expect(h.focus).toHaveBeenLastCalledWith(20, 21, 20, { distance: 40, pitch: 55 });
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

  it('cancels native drag placement after a rejected drop, without cancelling ordinary click placement on dragend', () => {
    const h = mount();
    act(() => { h.result.current.arm(tunnel, null, { drag: true }); });
    expect(h.focus).not.toHaveBeenCalled();
    h.pointer('pointermove', [30, 11, 30]);
    expect(h.result.current.hover).toEqual([30, 11, 30]);
    const invalid = h.drag([0, 11, 30]);
    act(() => { h.result.current.drop(invalid as unknown as DragEvent); });
    expect(h.notice).toHaveBeenLastCalledWith(expect.stringContaining('does not fit'));
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
});
