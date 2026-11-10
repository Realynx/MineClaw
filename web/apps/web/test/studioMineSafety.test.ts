/**
 * Safe selection keys in a mine (C11): Delete removes the plan inside a selection and never digs, Escape drops the selection,
 * a click without a drag selects nothing, the whole design's turn and mirror are hidden once the mine has components, and a
 * refused outline draws red. Build mode keeps its own Delete (studio.test.ts, studioUnderground.test.ts).
 */
import { MOCK_DESIGNER_PALETTE, type DesignerSession } from '@mineclaw/contracts';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { createElement } from 'react';
import type * as THREE from 'three';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { Canvas, keyOf, plotMode, setPlotMode, type Selection, type V3 } from '@/features/studio/canvas';
import { MINE_PREVIEW_COLORS } from '@/features/studio/mine/minePreview';
import { Overlay3D, portMarks, REFUSED_COLOR } from '@/features/studio/overlay3d';
import { TransformBar } from '@/features/studio/StudioChrome';
import { Studio, type StudioApi } from '@/features/studio/studio';
import { studioKey, studioRelease, type InputState, type KeyActions } from '@/features/studio/studioInput';
import { clearPlan, deleteSelection, erase, type Hit } from '@/features/studio/tools';

const studios: Studio[] = [];
const api: StudioApi = {
  canvas: () => Promise.resolve({ rev: 1, applied: 1 }),
  message: () => Promise.reject(new Error('unused')),
  save: () => Promise.reject(new Error('unused')),
};
function mine(): Studio {
  const s = new Studio(
    {
      id: 31,
      owner: 'test',
      model: 'main',
      designId: null,
      name: 'mine',
      rev: 0,
      blocks: [],
      anchor: null,
      messages: [],
      costUsd: 0,
      calls: 0,
      version: '1.12.2',
      palette: MOCK_DESIGNER_PALETTE,
      plot: { size: 64, height: 128, depth: 0 },
      revisions: [],
      kind: 'mine',
      seed: '12345',
    } satisfies DesignerSession,
    api,
    60_000,
  );
  studios.push(s);
  return s;
}
afterEach(() => {
  cleanup();
  for (const s of studios.splice(0)) s.dispose();
  setPlotMode({ kind: 'build' });
});

const state = (over: Partial<InputState> = {}) =>
  ({ drag: null, pasting: false, under: null, tool: 'select', ...over }) as InputState;
function keys(studio: Studio, over: Partial<KeyActions> = {}): KeyActions {
  return {
    studio,
    state: () => state(),
    inWorld: () => true,
    moveSel: vi.fn(),
    applyTransform: vi.fn(),
    setPasting: vi.fn(),
    setDrag: vi.fn(),
    ...over,
  } as unknown as KeyActions;
}
const press = (a: KeyActions, init: KeyboardEventInit) => {
  const e = new KeyboardEvent('keydown', { cancelable: true, ...init });
  studioKey(e, a);
  return e;
};
/** A dug corridor, a placed torch on it and a dug cell elsewhere. */
function plan(s: Studio): void {
  s.edit(
    [
      [10, 20, 10, 0, 0],
      [11, 20, 10, 0, 0],
      [12, 20, 10, 0, 0],
      [11, 21, 10, 50, 5],
      [40, 20, 40, 0, 0],
    ],
    'dig',
    'plan',
  );
}

describe('Delete in a mine', () => {
  it('removes the plan inside a box selection and digs nothing new', () => {
    const s = mine();
    plan(s);
    s.setSelection({ min: [9, 19, 9], max: [11, 22, 11] });
    const edit = vi.spyOn(s, 'edit');
    expect(press(keys(s), { key: 'Delete' }).defaultPrevented).toBe(true);
    // Only the plan's own cells, each back to untouched terrain: before, the whole 3×4×3 box was dug.
    expect(edit.mock.calls[0]![0]).toEqual([
      [11, 21, 10, -1, 0],
      [10, 20, 10, -1, 0],
      [11, 20, 10, -1, 0],
    ]);
    expect([...s.canvas.dug].sort()).toEqual([keyOf(12, 20, 10), keyOf(40, 20, 40)].sort());
    expect(s.canvas.size).toBe(0);
    s.undo();
    expect(s.canvas.dug.size).toBe(4);
    expect(s.canvas.get(11, 21, 10)).toEqual([50, 5]);
  });

  it('Ctrl+A then Delete clears the plan without a single dig', () => {
    const s = mine();
    plan(s);
    const a = keys(s);
    expect(press(a, { key: 'a', ctrlKey: true }).defaultPrevented).toBe(true);
    expect(s.selection).not.toBeNull();
    const edit = vi.spyOn(s, 'edit');
    press(a, { key: 'Delete' });
    const cells = edit.mock.calls[0]![0];
    expect(cells).toHaveLength(5);
    expect(cells.every((c) => c[3] === -1)).toBe(true);
    expect(s.canvas.dug.size).toBe(0);
    expect(s.canvas.size).toBe(0);
  });

  it('the selection bar Delete follows the same rule', () => {
    const s = mine();
    plan(s);
    const sel: Selection = { min: [9, 19, 9], max: [12, 22, 11] };
    render(
      createElement(TransformBar, {
        studio: s,
        sel,
        version: '1.12.2',
        hasClip: false,
        applyTransform: vi.fn(),
        moveSel: vi.fn(),
        doCopy: vi.fn(),
        startPaste: vi.fn(),
      }),
    );
    fireEvent.click(screen.getByRole('button', { name: 'Delete [Delete]' }));
    expect([...s.canvas.dug]).toEqual([keyOf(40, 20, 40)]);
    expect(s.canvas.size).toBe(0);
  });

  it('clearPlan visits only the plan; build mode keeps erase', () => {
    setPlotMode({ kind: 'mine', terrain: { state: () => 16 } });
    const c = new Canvas();
    c.apply([
      [5, 20, 5, 0, 0],
      [6, 20, 5, 4, 0],
    ]);
    const box: Selection = { min: [0, 1, 0], max: [plotMode().size - 1, 100, plotMode().size - 1] };
    expect(clearPlan(c, box)).toEqual([
      [6, 20, 5, -1, 0],
      [5, 20, 5, -1, 0],
    ]);
    expect(deleteSelection(c, box, true)).toEqual(clearPlan(c, box));
    setPlotMode({ kind: 'build' });
    const b = new Canvas();
    b.apply([[1, 0, 1, 4, 0]]);
    const sel: Selection = { min: [0, -2, 0], max: [2, 0, 2] };
    expect(deleteSelection(b, sel, true)).toEqual(erase(b, sel, true));
    expect(deleteSelection(b, sel)).toEqual(erase(b, sel));
  });
});

describe('selection keys and clicks in a mine', () => {
  it('Escape drops the selection in a mine and keeps it on a build plot', () => {
    const s = mine();
    s.setSelection({ min: [1, 20, 1], max: [3, 21, 3] });
    press(keys(s), { key: 'Escape' });
    expect(s.selection).toBeNull();
    setPlotMode({ kind: 'build' });
    const sel = { min: [1, 0, 1] as V3, max: [3, 1, 3] as V3 };
    const fake = { selection: sel as Selection | null, setSelection: vi.fn() };
    press(keys(fake as unknown as Studio), { key: 'Escape' });
    expect(fake.setSelection).not.toHaveBeenCalled();
  });

  it('a click without a drag selects nothing, Shift keeps the selection, and a drag still selects its box', () => {
    const s = mine();
    const hit = (p: V3): Hit => ({ hit: p, place: p, face: 1 });
    const kept: Selection = { min: [1, 20, 1], max: [3, 21, 3] };
    s.setSelection(kept);
    studioRelease(s, state(), { button: 0, a: [5, 20, 5], dy: 0 }, hit([5, 20, 5]), 0, true);
    expect(s.selection).toBe(kept);
    studioRelease(s, state(), { button: 0, a: [5, 20, 5], dy: 0 }, hit([5, 20, 5]), 0, false);
    expect(s.selection).toBeNull();
    studioRelease(s, state(), { button: 0, a: [5, 20, 5], dy: 1 }, hit([5, 20, 5]), 0, false);
    expect(s.selection).toEqual({ min: [5, 20, 5], max: [5, 21, 5] });
    studioRelease(s, state(), { button: 0, a: [5, 20, 5], dy: 0 }, hit([7, 20, 6]), 0, false);
    expect(s.selection).toEqual({ min: [5, 20, 5], max: [7, 20, 6] });
  });
});

describe('the whole design bar and the refused outline', () => {
  const bar = (over: { sel: Selection | null; whole?: boolean; hasClip?: boolean }) =>
    render(
      createElement(TransformBar, {
        studio: {} as Studio,
        version: '1.12.2',
        hasClip: false,
        applyTransform: vi.fn(),
        moveSel: vi.fn(),
        doCopy: vi.fn(),
        startPaste: vi.fn(),
        ...over,
      }),
    );

  it('hides the whole design turn and mirror when told to, keeping paste and the selection bar', () => {
    bar({ sel: null });
    expect(screen.getByRole('toolbar', { name: 'Whole design' })).toBeTruthy();
    cleanup();
    bar({ sel: null, whole: false });
    expect(screen.queryByRole('button', { name: /Rotate|Mirror/ })).toBeNull();
    cleanup();
    bar({ sel: null, whole: false, hasClip: true });
    expect(screen.getByRole('button', { name: 'Paste [Ctrl+V]' })).toBeTruthy();
    expect(screen.queryByRole('button', { name: /Rotate/ })).toBeNull();
    cleanup();
    bar({ sel: { min: [1, 20, 1], max: [2, 20, 2] }, whole: false });
    expect(screen.getByRole('toolbar', { name: 'Selection' })).toBeTruthy();
    expect(screen.getAllByRole('button', { name: /Rotate|Mirror/ })).toHaveLength(4);
  });

  it('draws a refused placement outline red and the next accepted one violet again', () => {
    setPlotMode({ kind: 'mine', terrain: { state: () => 0 } });
    const overlay = new Overlay3D(256);
    const lines = overlay.group.getObjectByName('mine-placement') as THREE.LineSegments<
      THREE.BufferGeometry,
      THREE.LineBasicMaterial
    >;
    const outline = new Float32Array([0, 0, 0, 1, 0, 0]);
    overlay.setMinePlacementGeometry(outline, [3, 20, 3], true);
    expect(lines.visible).toBe(true);
    expect(lines.material.color.getHex()).toBe(REFUSED_COLOR);
    overlay.setMinePlacementGeometry(outline, [4, 20, 3]);
    expect(lines.material.color.getHex()).toBe(MINE_PREVIEW_COLORS.placement);
    overlay.setMinePlacementGeometry(outline, [5, 20, 3], true);
    overlay.setMinePlacement(null);
    expect(lines.material.color.getHex()).toBe(MINE_PREVIEW_COLORS.placement);
    overlay.dispose();
  });

  it('marks the openings a piece can join, the joined ones apart, and drops them with the outline and from a picture', async () => {
    setPlotMode({ kind: 'mine', terrain: { state: () => 0 } });
    const overlay = new Overlay3D(256);
    overlay.setMinePorts(
      [
        [3, 20, 3],
        [9, 20, 3],
      ],
      [[9, 20, 3]],
    );
    expect(overlay.portCounts).toEqual([2, 1]);
    const marks = portMarks([[9, 20, 3]]);
    expect(marks).toHaveLength(24);
    // A square on the cell's floor, inside its edges.
    for (let i = 0; i < marks.length; i += 3) {
      expect(marks[i]).toBeGreaterThan(9);
      expect(marks[i]).toBeLessThan(10);
      expect(marks[i + 1]).toBeCloseTo(20.03);
    }
    let during: [number, number] | null = null;
    const visible = () => ['mine-ports', 'mine-ports-joined'].map((n) => overlay.group.getObjectByName(n)!.visible);
    await overlay.withoutGuides(() => {
      during = [Number(visible()[0]), Number(visible()[1])];
      return Promise.resolve();
    }, true);
    expect(during).toEqual([0, 0]);
    expect(visible()).toEqual([true, true]);
    overlay.setMinePorts([[3, 20, 3]]);
    expect(overlay.portCounts).toEqual([1, 0]);
    overlay.setMinePlacement(null);
    expect(overlay.portCounts).toEqual([0, 0]);
    overlay.dispose();
  });
});
