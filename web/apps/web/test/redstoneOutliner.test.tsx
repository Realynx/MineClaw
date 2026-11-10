/**
 * The redstone outliner on a real component controller: its rows (name or label, definition, blocks, joined ports), the
 * selection shared with the world, framing, the row and detail actions (duplicate, unlink, delete, rename, move, turn),
 * Ctrl/Shift ticks with Ctrl+G to combine and Ctrl+Shift+G to ungroup, the selected component's ports drawn in the world
 * with an arrow each way, and the sticky note when saved components were opened as plain blocks.
 */
import { MOCK_DESIGNER_PALETTE, type DesignerSession } from '@mineclaw/contracts';
import { LIBRARY, type Pos } from '@mineclaw/redstone';
import type { WorldViewerHandle } from '@mineclaw/world-viewer';
import { act, cleanup, fireEvent, render, screen, within } from '@testing-library/react';
import type { RefObject } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { plotMode, setPlotMode, type Cell, type V3 } from '@/features/studio/canvas';
import { Overlay3D, portArrowMarks, portArrowsAt } from '@/features/studio/overlay3d';
import { pictureDistance } from '@/features/studio/PictureControls';
import { placeLayout } from '@/features/studio/redstone/ports';
import { RedstoneComponentDrag } from '@/features/studio/redstone/redstoneInput';
import { RedstoneOutliner } from '@/features/studio/redstone/RedstoneOutliner';
import { useRedstoneComponents, type RedstoneComponents } from '@/features/studio/redstone/useRedstoneComponents';
import { Studio, type StudioApi } from '@/features/studio/studio';
import type { NoticeOptions } from '@/features/studio/studioSave';

const NOT = LIBRARY.find((d) => d.id === 'gate.not')!;
const TORCH = 76;

const session = (over: Partial<DesignerSession> = {}): DesignerSession => ({
  id: 21,
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

const studios: Studio[] = [];
beforeEach(() => localStorage.clear());
afterEach(() => {
  cleanup();
  for (const s of studios.splice(0)) s.dispose();
  setPlotMode({ kind: 'build' });
});

const sorted = (cells: Cell[]): Cell[] =>
  cells.sort((a, b) => a[1] - b[1] || a[2] - b[2] || a[0] - b[0] || a[3] - b[3]);
const notBlocks = (at: Pos): Cell[] =>
  placeLayout(NOT.layouts['1.12.2']!, { at, turns: 0, mirror: null }, '1.12.2').cells.filter((c) => c[3] > 0);

interface Mounted {
  studio: Studio;
  hook: { current: RedstoneComponents };
  notice: ReturnType<typeof vi.fn<(text: string | null, o?: NoticeOptions) => void>>;
  engine: { focus: ReturnType<typeof vi.fn>; camera: { aspect: number; fov: number }; surface: { focus: () => void } };
  rerender: (canEdit: boolean) => void;
}

/** The hook on a studio, with the outliner it feeds; `overlay` is the view's (a real one where the test reads it). */
function mount(o: { over?: Partial<DesignerSession>; overlay?: Overlay3D; canEdit?: boolean } = {}): Mounted {
  const studio = new Studio(session(o.over), api, 60_000);
  studios.push(studio);
  const engine = { focus: vi.fn(), camera: { aspect: 1.5, fov: 70 }, surface: { focus: vi.fn() } };
  const viewer = { current: { engine: () => engine } } as unknown as RefObject<WorldViewerHandle | null>;
  const stage = { current: null };
  const notice = vi.fn<(text: string | null, o?: NoticeOptions) => void>();
  const hook = {} as { current: RedstoneComponents };
  function Harness({ canEdit }: { canEdit: boolean }) {
    const components = useRedstoneComponents({
      enabled: true,
      studio,
      viewer,
      stage,
      live: false,
      overlay: o.overlay,
      canEdit,
      notice,
    });
    hook.current = components;
    return <RedstoneOutliner components={components} defs={components.controller?.defs() ?? []} />;
  }
  const view = render(<Harness canEdit={o.canEdit ?? true} />);
  return { studio, hook, notice, engine, rerender: (canEdit) => view.rerender(<Harness canEdit={canEdit} />) };
}

/** Two NOTs in a row, the second's input on the first's output: c1 at x 10, c2 at x 14. */
function chain(m: Mounted, label?: string) {
  const c = m.hook.current.controller!;
  act(() => {
    c.place('gate.not', [10, 0, 10]);
    c.place('gate.not', [14, 0, 11], { snap: true, ...(label ? { label } : {}) });
  });
  return c;
}

const list = () => screen.getByRole('listbox', { name: 'Redstone components' });
const row = (name: string) => within(list()).getByRole('option', { name });
const rows = () =>
  within(list())
    .getAllByRole('option')
    .map((r) => r.getAttribute('aria-label'));
const key = (target: Element, init: KeyboardEventInit) => act(() => void fireEvent.keyDown(target, init));

describe('the outliner rows', () => {
  it('list each component by its label or definition, with its blocks and how many of its ports are joined', () => {
    const m = mount();
    expect(screen.getByTestId('redstone-components')).toHaveTextContent('Nothing placed yet');
    chain(m, 'second');
    const blocks = notBlocks([0, 0, 0]).length;
    expect(rows()).toEqual(['NOT 1', 'second 2']);
    // The first's output feeds the second's input: one of two ports joined on each.
    expect(row('NOT 1')).toHaveTextContent(`${blocks} blocks · 1/2 ports joined`);
    expect(row('second 2')).toHaveTextContent(`NOT · ${blocks} blocks · 1/2 ports joined`);
    expect(row('NOT 1').title).toMatch(/←Y joined to second 2 A/);
    expect(row('NOT 1').title).toMatch(/→A open/);
  });

  it('select the component in the world and follow a pick there, showing its ports and what they join', () => {
    const m = mount();
    const c = chain(m);
    fireEvent.click(row('NOT 1'));
    expect(m.hook.current.selectedId).toBe('c1');
    expect(m.studio.selection).toEqual({ min: [10, 0, 10], max: [12, 0, 12] });
    const ports = within(screen.getByTestId('redstone-selected-component')).getAllByTestId('component-port');
    expect(ports.map((p) => [p.textContent, p.dataset.joined])).toEqual([
      ['inA open', 'false'],
      ['outY joined to NOT 2 A', 'true'],
    ]);
    // A press on the second in the world selects it here too.
    act(() => m.hook.current.select('c2'));
    expect(row('NOT 2')).toHaveAttribute('aria-selected', 'true');
    expect(row('NOT 1')).toHaveAttribute('aria-selected', 'false');
    // Another selection (the Select tool's box) is no component's.
    act(() => m.studio.setSelection({ min: [0, 0, 0], max: [1, 0, 1] }));
    expect(m.hook.current.selectedId).toBeNull();
    expect(screen.queryByTestId('redstone-selected-component')).not.toBeInTheDocument();
    expect(c.snapshot().components).toHaveLength(2);
  });

  it('frame a component on a double-click: the view centres on its box, on the plot’s base', () => {
    const m = mount();
    chain(m);
    fireEvent.doubleClick(row('NOT 1'));
    const box = { min: [10, 0, 10] as V3, max: [12, 0, 12] as V3 };
    expect(m.engine.focus).toHaveBeenCalledExactlyOnceWith(11, plotMode().baseY, 11, {
      distance: pictureDistance(box, 1.5, 70),
    });
    expect(m.engine.surface.focus).toHaveBeenCalledOnce();
    expect(m.hook.current.selectedId).toBe('c1');
  });
});

describe('the outliner actions', () => {
  it('duplicate beside it and select the copy, rename, move by the typed corner, and turn, each one undo step', () => {
    const m = mount();
    const c = chain(m);
    fireEvent.click(screen.getByRole('button', { name: 'Duplicate NOT 1' }));
    expect(rows()).toHaveLength(3);
    expect(m.studio.snapshot().undoLabel).toBe('duplicate NOT');
    expect(row('NOT 3')).toHaveAttribute('aria-selected', 'true');

    fireEvent.click(row('NOT 1'));
    const name = screen.getByRole('textbox', { name: 'Name of NOT 1' });
    fireEvent.change(name, { target: { value: 'inverter' } });
    fireEvent.blur(name);
    expect(rows()[0]).toBe('inverter 1');
    expect(m.studio.snapshot().undoLabel).toBe('rename NOT');

    const corner = screen.getByRole('textbox', { name: 'Corner of inverter 1' });
    fireEvent.change(corner, { target: { value: '40 0 40' } });
    fireEvent.blur(corner);
    expect(c.snapshot().components[0]!.at).toEqual([40, 0, 40]);
    expect(m.studio.selection).toEqual({ min: [40, 0, 40], max: [42, 0, 42] });

    fireEvent.click(screen.getByRole('button', { name: 'Turn inverter 1' }));
    expect(c.snapshot().components[0]!.turns).toBe(1);
    fireEvent.click(screen.getByRole('button', { name: 'Turn inverter 1 back' }));
    expect(c.snapshot().components[0]!.turns).toBe(0);
  });

  it('unlink keeps the blocks as plain cells; delete takes them away', () => {
    const m = mount();
    const c = chain(m);
    const cells = sorted(m.studio.canvas.cells());
    fireEvent.click(screen.getByRole('button', { name: 'Unlink NOT 2' }));
    expect(rows()).toEqual(['NOT 1']);
    expect(sorted(m.studio.canvas.cells())).toEqual(cells);
    fireEvent.click(row('NOT 1'));
    fireEvent.click(screen.getByRole('button', { name: 'Delete NOT 1' }));
    expect(c.snapshot().components).toHaveLength(0);
    expect(m.hook.current.selectedId).toBeNull();
    expect(m.studio.canvas.get(11, 0, 11)).toEqual([0, 0]);
    expect(screen.getByTestId('redstone-components')).toHaveTextContent('Nothing placed yet');
  });

  it('change nothing while editing is not allowed, but still select', () => {
    const m = mount({ canEdit: false });
    chain(m);
    for (const name of ['Duplicate NOT 1', 'Unlink NOT 1', 'Delete NOT 1'])
      expect(screen.getByRole('button', { name })).toBeDisabled();
    fireEvent.click(row('NOT 1'));
    expect(m.hook.current.selectedId).toBe('c1');
    expect(screen.getByRole('button', { name: 'Turn NOT 1' })).toBeDisabled();
    m.hook.current.duplicate('c1');
    m.hook.current.remove('c1');
    expect(m.hook.current.controller!.snapshot().components).toHaveLength(2);
  });
});

describe('combine and ungroup', () => {
  it('Ctrl-click ticks and Ctrl+G combine them into one of the design’s own; Ctrl+Shift+G gives the parts back', () => {
    const m = mount();
    const c = chain(m);
    const cells = sorted(m.studio.canvas.cells());
    fireEvent.click(row('NOT 1'), { ctrlKey: true });
    fireEvent.click(row('NOT 2'), { ctrlKey: true });
    const checklist = screen.getByRole('region', { name: 'Combine redstone components' });
    expect(within(checklist).getByRole('checkbox', { name: 'Combine NOT 1' })).toBeChecked();
    expect(within(checklist).getByRole('checkbox', { name: 'Combine NOT 2' })).toBeChecked();
    // The page leaves the key to the components (plain G picks a tool).
    expect(m.hook.current.handlesKey(new KeyboardEvent('keydown', { key: 'g', ctrlKey: true }))).toBe(true);

    key(row('NOT 1'), { key: 'g', ctrlKey: true });
    expect(rows()).toEqual(['combined 1']);
    expect(m.studio.snapshot().undoLabel).toBe('combine combined');
    expect(sorted(m.studio.canvas.cells())).toEqual(cells);
    expect(row('combined 1')).toHaveAttribute('aria-selected', 'true');
    expect(screen.queryByRole('region', { name: 'Combine redstone components' })).not.toBeInTheDocument();
    expect(m.notice).toHaveBeenLastCalledWith(
      'Combined 2 components into one. Move, turn or rename it, or ungroup it (Ctrl+Shift+G).',
    );
    expect(screen.getByRole('button', { name: 'Ungroup combined 1' })).toBeInTheDocument();

    key(document.body, { key: 'G', ctrlKey: true, shiftKey: true });
    expect(rows()).toEqual(['NOT 1', 'NOT 2']);
    expect(c.snapshot().customDefs).toHaveLength(0);
    expect(sorted(m.studio.canvas.cells())).toEqual(cells);
    expect(m.notice).toHaveBeenLastCalledWith('Ungrouped into 2 components. Undo groups them again.');
  });

  it('Ctrl+G with fewer than two ticked opens the checklist with the selected one and says what to do', () => {
    const m = mount();
    chain(m);
    fireEvent.click(row('NOT 1'));
    key(document.body, { key: 'g', ctrlKey: true });
    const checklist = screen.getByRole('region', { name: 'Combine redstone components' });
    expect(within(checklist).getByRole('checkbox', { name: 'Combine NOT 1' })).toBeChecked();
    expect(m.notice).toHaveBeenLastCalledWith('Tick at least two components under Combine, then press Ctrl+G again.');
    // From the checklist itself the key still works.
    const second = within(checklist).getByRole('checkbox', { name: 'Combine NOT 2' });
    fireEvent.click(second);
    key(second, { key: 'g', ctrlKey: true });
    expect(rows()).toEqual(['combined 1']);
  });

  it('leaves Ctrl+G to the text being typed, Ctrl+Shift+G on a library gate, and both while editing is not allowed', () => {
    const m = mount();
    const c = chain(m);
    fireEvent.click(row('NOT 1'));
    key(screen.getByRole('textbox', { name: 'Name of NOT 1' }), { key: 'g', ctrlKey: true });
    expect(screen.queryByRole('region', { name: 'Combine redstone components' })).not.toBeInTheDocument();
    const ungroup = new KeyboardEvent('keydown', { key: 'G', ctrlKey: true, shiftKey: true });
    expect(m.hook.current.handlesKey(ungroup)).toBe(false);
    key(document.body, { key: 'G', ctrlKey: true, shiftKey: true });
    expect(c.snapshot().components.map((x) => x.id)).toEqual(['c1', 'c2']);
    m.rerender(false);
    expect(m.hook.current.handlesKey(new KeyboardEvent('keydown', { key: 'g', ctrlKey: true }))).toBe(false);
  });
});

describe('ports in the world', () => {
  it('shows the selected and ticked components’ ports, amber where open and green where joined, with arrows', () => {
    const overlay = new Overlay3D(64);
    const m = mount({ overlay });
    chain(m);
    expect(overlay.portCounts).toEqual([0, 0]);
    fireEvent.click(row('NOT 1'));
    expect(overlay.portCounts).toEqual([1, 1]);
    expect(overlay.portArrowCounts).toEqual([1, 1]);
    fireEvent.click(row('NOT 2'), { ctrlKey: true });
    // Ticked: NOT 1 (selected) and NOT 2; their shared cell is joined on both.
    expect(overlay.portCounts).toEqual([2, 2]);
    expect(overlay.portArrowCounts).toEqual([2, 2]);
    act(() => m.hook.current.select(null));
    act(() => m.hook.current.setChecked(null));
    expect(overlay.portCounts).toEqual([0, 0]);
    expect(overlay.portArrowCounts).toEqual([0, 0]);
  });

  it('points an input’s arrow into its component and an output’s out of it', () => {
    // A west input at x 9 points east (+x); an east output at x 13 points east too, away from its box.
    const [input, output] = [
      portArrowMarks([{ at: [9, 0, 11], side: 'west', kind: 'in' }]),
      portArrowMarks([{ at: [13, 0, 11], side: 'east', kind: 'out' }]),
    ];
    const tipX = (m: Float32Array) => m[3]!;
    const backX = (m: Float32Array) => m[0]!;
    expect(tipX(input)).toBeGreaterThan(backX(input));
    expect(tipX(output)).toBeGreaterThan(backX(output));
    const north = portArrowMarks([{ at: [5, 0, 5], side: 'north', kind: 'out' }]);
    expect(north[5]!).toBeLessThan(north[2]!);
    expect(
      portArrowsAt([{ at: [-1, 0, 1], side: 'west', kind: 'in' }], [10, 0, 10], [[9, 0, 11]]).map((a) => [
        a.at,
        a.joined,
      ]),
    ).toEqual([[[9, 0, 11], true]]);
  });

  it('moves the dragged component’s arrows with its outline', () => {
    const m = mount();
    const c = chain(m);
    const overlay = {
      setMinePlacementGeometry: vi.fn(),
      setComponentPorts: vi.fn(),
      setPortArrows: vi.fn<Overlay3D['setPortArrows']>(),
      setMinePlacement: vi.fn(),
      setSelection: vi.fn(),
    };
    const frames: Array<() => void> = [];
    const drag = new RedstoneComponentDrag({
      controller: c,
      overlay,
      ray: (x, y) => ({ origin: { x: x + 0.5, y: 300, z: y + 0.5 }, direction: { x: 0, y: -1, z: 0 } }),
      canEdit: () => true,
      refused: vi.fn(),
      moved: vi.fn(),
      rest: vi.fn(),
      notice: vi.fn(),
      schedule: (run) => void frames.push(run),
    });
    const win = new EventTarget();
    drag.bind(win as unknown as Window);
    drag.press({ pointerId: 1, x: 15, y: 11, id: 'c2', origin: [14, 0, 10], grab: [15, 0, 11] });
    win.dispatchEvent(new PointerEvent('pointermove', { clientX: 35, clientY: 31, pointerId: 1 }));
    for (const run of frames.splice(0)) run();
    expect(overlay.setPortArrows).toHaveBeenCalledOnce();
    const [ports, at, joined] = overlay.setPortArrows.mock.calls[0]!;
    expect(at).toEqual([34, 0, 30]);
    expect(ports).toEqual(c.shapeOf('c2')!.ports);
    expect(joined).toEqual([]);
    drag.unbind();
  });
});

describe('the restore note', () => {
  const saved = JSON.stringify({
    redstone: 1,
    components: [
      { id: 'c1', def: 'gate.not', at: [10, 0, 10], turns: 0, mirror: null },
      { id: 'c2', def: 'gate.not', at: [20, 0, 10], turns: 0, mirror: null },
    ],
    customDefs: [],
  });

  it('stays on the page when saved components were opened as plain blocks', () => {
    const blocks = [...notBlocks([10, 0, 10]), ...notBlocks([20, 0, 10]).filter((b) => b[3] !== TORCH)];
    const m = mount({ over: { blocks, designId: 5, rev: 1, redstoneAuthoring: saved } });
    expect(rows()).toEqual(['NOT 1']);
    expect(m.notice).toHaveBeenCalledExactlyOnceWith(
      '1 of 2 saved components no longer matches the circuit and was opened as plain blocks. The saved revision still has it.',
      { tone: 'warn', sticky: true },
    );
  });

  it('says nothing when every saved component still matches', () => {
    const blocks = [...notBlocks([10, 0, 10]), ...notBlocks([20, 0, 10])];
    const m = mount({ over: { blocks, designId: 5, rev: 1, redstoneAuthoring: saved } });
    expect(rows()).toEqual(['NOT 1', 'NOT 2']);
    expect(m.notice).not.toHaveBeenCalled();
  });
});
