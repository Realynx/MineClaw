/**
 * The redstone kind in the Design Studio's chrome: the Kind select offers it, a saved circuit offers no build, the clear
 * question names it, the hotbar and the block drawer start on its parts, and the side panel says when the circuit is drawn
 * for another version than the server runs (a warning, never a refusal) and mounts the debugger with its keys.
 */
import { MOCK_DESIGNER_PALETTE, mockDesignerPalette, type DesignerSession } from '@mineclaw/contracts';
import type { Diagnostic, Pos } from '@mineclaw/redstone';
import { fakeSimulations } from '@mineclaw/redstone/testing';
import { act, cleanup, fireEvent, render, screen, within } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { setPlotMode } from '@/features/studio/canvas';
import { PaletteDrawer } from '@/features/studio/BlockBar';
import type { PaletteEntry } from '@/features/studio/palette';
import { RedstonePanel } from '@/features/studio/redstone/RedstonePanel';
import { REDSTONE_CATEGORY, redstoneHotbar } from '@/features/studio/redstone/redstonePalette';
import { REBUILD_MS, RedstoneSimController } from '@/features/studio/redstone/RedstoneSimController';
import type { RedstoneSim } from '@/features/studio/redstone/useRedstoneSim';
import { KindDialog, SavedBar, StudioHeader } from '@/features/studio/StudioChrome';
import { Studio, type StudioApi } from '@/features/studio/studio';

const debuggerMount = vi.hoisted(() => ({
  panel: vi.fn<(p: { tab: string; selected: unknown }) => void>(),
  keys: vi.fn<(target: HTMLElement, api: unknown, opts: { inWorld: () => boolean }) => () => void>(),
  unbind: vi.fn(),
}));
vi.mock('@/features/studio/redstone/debugger', () => ({
  DebuggerPanel: (p: { tab: string; selected: unknown }) => {
    debuggerMount.panel(p);
    return <div data-testid="debugger" data-tab={p.tab} />;
  },
  bindDebuggerKeys: (target: HTMLElement, api: unknown, opts: { inWorld: () => boolean }) => {
    debuggerMount.keys(target, api, opts);
    return debuggerMount.unbind;
  },
}));

const session = (over: Partial<DesignerSession> = {}): DesignerSession => ({
  id: 7,
  owner: 'poofimafox',
  model: 'main',
  designId: null,
  name: 'not gate',
  rev: 0,
  blocks: [[2, 0, 2, 69, 5]],
  anchor: null,
  messages: [],
  costUsd: 0,
  calls: 0,
  version: 'b1.8.1',
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
  // Unmounted here, before the mocks are cleared, so a panel's unbinding is counted in its own test.
  cleanup();
  for (const s of studios.splice(0)) s.dispose();
  setPlotMode({ kind: 'build' });
  vi.clearAllMocks();
});

function header(s: DesignerSession, changeKind = vi.fn()) {
  const studio = new Studio(s, api, 60_000);
  studios.push(studio);
  render(
    <StudioHeader
      snap={studio.snapshot()}
      session={s}
      version={s.version as 'b1.8.1' | '1.12.2'}
      locked={false}
      changeVersion={vi.fn()}
      changeKind={changeKind}
      saveName=""
      setSaveName={vi.fn()}
      saveSummary=""
      setSaveSummary={vi.fn()}
      canSave={false}
      saving={false}
      doSave={vi.fn()}
      revisions={[]}
    />,
  );
  return changeKind;
}

describe('the Kind select', () => {
  it('offers Redstone beside Build and Mine, and shows a redstone design as one', () => {
    header(session());
    const select = screen.getByTestId<HTMLSelectElement>('kind-select');
    expect(Array.from(select.options).map((o) => [o.value, o.text])).toEqual([
      ['build', 'Build'],
      ['mine', 'Mine'],
      ['redstone', 'Redstone'],
    ]);
    expect(select.value).toBe('redstone');
  });

  it('asks the page to switch a build to redstone', () => {
    const changeKind = header(session({ kind: 'build' }));
    fireEvent.change(screen.getByTestId('kind-select'), { target: { value: 'redstone' } });
    expect(changeKind).toHaveBeenCalledExactlyOnceWith('redstone');
  });
});

describe('a saved design’s bar', () => {
  const saved = { designId: 12, rev: 3, text: '' };
  const bar = (redstone?: boolean) =>
    render(<SavedBar saved={saved} mineRange={null} canChat mainBot="alice" askBot={vi.fn()} redstone={redstone} />);

  it('offers a build in the Controller and by the bot for a build design', () => {
    bar();
    expect(screen.getByRole('link', { name: 'Build in Controller' })).toHaveAttribute('href', '/story?design=12');
    expect(screen.getByRole('button', { name: 'Ask the bot to build it' })).toBeEnabled();
    expect(screen.queryByTestId('redstone-build-note')).not.toBeInTheDocument();
  });

  it('offers neither for a redstone design, and still opens it in Designs', () => {
    bar(true);
    const shown = screen.getByTestId('saved-bar');
    expect(shown).toHaveTextContent('Design #12 r3');
    expect(within(shown).queryByRole('link', { name: 'Build in Controller' })).not.toBeInTheDocument();
    expect(within(shown).queryByRole('button', { name: 'Ask the bot to build it' })).not.toBeInTheDocument();
    expect(screen.getByTestId('redstone-build-note')).toHaveTextContent("Pawns can't build circuits yet");
    expect(screen.getByRole('link', { name: 'Open in Designs' })).toHaveAttribute('href', '/designs?id=12');
  });
});

describe('the clear question', () => {
  // Only a switch to or from a mine clears; the bot asks for those alone.
  it('names the redstone circuit a mine turns into, and keeps the words for a build and a mine', () => {
    const props = { blocks: 4, dug: 9, busy: false, onConfirm: vi.fn(), onCancel: vi.fn() };
    const { rerender } = render(<KindDialog kind="redstone" {...props} />);
    expect(screen.getByTestId('kind-dialog')).toHaveTextContent(
      'Switch this design to a redstone circuit? The canvas is cleared (4 blocks, 9 dug cells): a redstone circuit stands on flat grass.',
    );
    rerender(<KindDialog kind="build" {...props} />);
    expect(screen.getByTestId('kind-dialog')).toHaveTextContent(
      'Switch this design to a build? The canvas is cleared (4 blocks, 9 dug cells): a build stands on flat grass.',
    );
    rerender(<KindDialog kind="mine" {...props} />);
    expect(screen.getByTestId('kind-dialog')).toHaveTextContent('a mine is dug into generated terrain at absolute y');
  });
});

describe('the parts a redstone design starts with', () => {
  const ids = (slots: Array<PaletteEntry | null>) => slots.map((e) => (e ? `${e.id}:${e.meta}` : null));

  it('puts the version’s circuit parts on the hotbar, most used first', () => {
    // b1.8.1 has no comparator, lamp, redstone block or observer.
    expect(ids(redstoneHotbar(mockDesignerPalette('b1.8.1')))).toEqual([
      '1:0',
      '55:0',
      '76:5',
      '93:0',
      '69:5',
      '77:1',
      '33:1',
      '29:1',
      '70:0',
    ]);
    expect(ids(redstoneHotbar(mockDesignerPalette('1.12.2')))).toEqual([
      '1:0',
      '55:0',
      '76:5',
      '93:0',
      '149:0',
      '69:5',
      '77:1',
      '123:0',
      '33:1',
    ]);
  });

  it('fills what a small palette lacks with its other entries, then leaves slots empty', () => {
    const palette: PaletteEntry[] = [
      { id: 4, meta: 0, name: 'Cobblestone', facing: false, cat: 'Building', spec: 'cobblestone' },
      { id: 55, meta: 0, name: 'Redstone Wire', facing: false, cat: REDSTONE_CATEGORY, spec: 'redstone_wire' },
    ];
    expect(ids(redstoneHotbar(palette))).toEqual(['55:0', '4:0', null, null, null, null, null, null, null]);
  });

  it('opens the block drawer on the Redstone category, and on every block when the palette has none', () => {
    const palette: PaletteEntry[] = mockDesignerPalette('1.12.2');
    const drawer = (initialCategory?: string, entries = palette) =>
      render(
        <PaletteDrawer
          palette={entries}
          version="1.12.2"
          current={null}
          onChoose={vi.fn()}
          onClose={vi.fn()}
          sheet={null}
          initialCategory={initialCategory}
        />,
      );
    const selected = () =>
      within(screen.getByRole('tablist', { name: 'Categories' }))
        .getAllByRole('tab')
        .filter((t) => t.getAttribute('aria-selected') === 'true')
        .map((t) => t.textContent?.split(' ')[0]);

    const first = drawer(REDSTONE_CATEGORY);
    expect(selected()).toEqual([REDSTONE_CATEGORY]);
    const parts = palette.filter((e) => e.cat === REDSTONE_CATEGORY);
    expect(parts.length).toBeGreaterThan(0);
    expect(screen.getByTestId('palette-count')).toHaveTextContent(String(parts.length));
    first.unmount();

    const build = drawer();
    expect(selected()).toEqual(['All']);
    expect(screen.getByTestId('palette-count')).toHaveTextContent(String(palette.length));
    build.unmount();

    const noParts = palette.filter((e) => e.cat !== REDSTONE_CATEGORY);
    drawer(REDSTONE_CATEGORY, noParts);
    expect(selected()).toEqual(['All']);
    expect(screen.getByTestId('palette-count')).toHaveTextContent(String(noParts.length));
  });
});

describe('the redstone side panel', () => {
  const FLOATING: Diagnostic = { at: [3, 0, 2], code: 'floating', text: 'dust with nothing under it' };

  function panel(o: { serverVersion?: string | null; selected?: Pos | null; version?: 'b1.8.1' | '1.12.2' } = {}) {
    const version = o.version ?? 'b1.8.1';
    const studio = new Studio(session({ version }), api, 60_000);
    studios.push(studio);
    const fake = fakeSimulations({ 0: {} }, { diagnostics: [FLOATING] });
    const clock = { t: 0 };
    const controller = new RedstoneSimController({
      studio,
      version,
      create: fake.create,
      display: { cells: vi.fn() },
      now: () => clock.t,
    });
    const stage = { current: document.createElement('div') };
    const sim = (selected: Pos | null = o.selected ?? null): RedstoneSim => ({
      api: controller,
      live: false,
      failure: null,
      selected,
      inspect: vi.fn(),
      aimed: () => null,
      jump: vi.fn(),
    });
    const view = render(<RedstonePanel sim={sim()} serverVersion={o.serverVersion} stage={stage} />);
    /** One animation frame, `ms` later. */
    const frame = (ms: number) => {
      clock.t += ms;
      act(() => controller.frame());
    };
    return { ...view, controller, fake, sim, stage, frame };
  }

  it('warns, and only warns, when the circuit is drawn for another version than the server runs', () => {
    const other = panel({ version: '1.12.2', serverVersion: 'b1.8.1' });
    expect(screen.getByTestId('redstone-version-warning')).toHaveTextContent(
      'Drawn and simulated for 1.12.2; the server runs b1.8.1.',
    );
    // A warning is not a refusal: the simulation still starts.
    fireEvent.click(screen.getByRole('button', { name: 'Simulate' }));
    expect(other.controller.snapshot().mode).toBe('running');
    expect(screen.getByTestId('redstone-mode')).toHaveAttribute('data-mode', 'running');
    other.unmount();

    panel({ version: 'b1.8.1', serverVersion: 'b1.8.1' }).unmount();
    panel({ serverVersion: null });
    expect(screen.queryByTestId('redstone-version-warning')).not.toBeInTheDocument();
  });

  it('mounts the debugger on its Simulate tab and binds its keys until the panel goes', () => {
    const h = panel();
    expect(screen.getByTestId('debugger')).toHaveAttribute('data-tab', 'simulate');
    expect(screen.queryByRole('tab', { name: 'Library' })).not.toBeInTheDocument();
    expect(debuggerMount.keys).toHaveBeenCalledExactlyOnceWith(document.body, h.controller, expect.anything());
    // The keys apply while nothing has the focus, and not while a text field has it.
    const { inWorld } = debuggerMount.keys.mock.calls[0]![2];
    expect(inWorld()).toBe(true);
    const field = document.createElement('input');
    document.body.append(field);
    field.focus();
    expect(inWorld()).toBe(false);
    field.remove();

    fireEvent.click(screen.getByRole('tab', { name: 'Probes' }));
    expect(screen.getByTestId('debugger')).toHaveAttribute('data-tab', 'probes');
    h.unmount();
    expect(debuggerMount.unbind).toHaveBeenCalledOnce();
  });

  it('opens the Inspector when a cell is picked, and lists the simulator’s checks', () => {
    const h = panel();
    expect(screen.queryByTestId('redstone-checks')).not.toBeInTheDocument();
    // The edit-mode simulation is built a moment after the canvas last changed.
    h.frame(REBUILD_MS);
    expect(screen.getByTestId('redstone-checks')).toHaveTextContent('1 to check');
    expect(screen.getByTestId('redstone-checks')).toHaveTextContent('3, 0, 2 dust with nothing under it');
    h.rerender(<RedstonePanel sim={h.sim([3, 0, 2])} stage={h.stage} />);
    expect(screen.getByTestId('debugger')).toHaveAttribute('data-tab', 'inspector');
    expect(debuggerMount.panel).toHaveBeenLastCalledWith(expect.objectContaining({ selected: [3, 0, 2] }));
  });

  it('stops a live simulation with Stop and edit', () => {
    const h = panel();
    act(() => h.controller.start());
    expect(screen.getByTestId('redstone-mode')).toHaveAttribute('data-mode', 'running');
    fireEvent.click(screen.getByRole('button', { name: /Stop and edit/ }));
    expect(h.controller.snapshot().mode).toBe('edit');
    expect(screen.getByRole('button', { name: /Simulate/ })).toBeInTheDocument();
  });
});
