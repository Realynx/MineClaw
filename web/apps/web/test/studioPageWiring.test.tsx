/**
 * The Design Studio page's own wiring, rendered whole: only the 3D viewer is replaced (a canvas element and an engine with a
 * real camera), so the real Studio, mine controller, overlay, studio world, key and pointer bindings and chrome run as on the
 * page. The pieces themselves are tested in their own files; this one checks that the page connects them.
 */
import type * as ReactQuery from '@tanstack/react-query';
import type { DesignerSession } from '@mineclaw/contracts';
import { MOCK_DESIGNER_PALETTE } from '@mineclaw/contracts';
import { act, cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { type PerspectiveCamera, Raycaster, Vector2, Vector3 } from 'three';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type * as Client from '@/lib/api/client';
import type * as Query from '@/lib/api/query';
import { selectAll, setPlotMode, type V3 } from '@/features/studio/canvas';
import type { MinePreview } from '@/features/studio/mine/minePreview';
import { Overlay3D } from '@/features/studio/overlay3d';
import type { Studio } from '@/features/studio/studio';
import { mineHint } from '@/features/studio/StudioShortcuts';

const nav = vi.hoisted(() => ({ params: new URLSearchParams(), replace: vi.fn() }));
const client = vi.hoisted(() => ({ api: vi.fn() }));
/** The fake engine the viewer stub hands out (set once the stub mounts). */
const view = vi.hoisted(() => ({ engine: null as null | Record<string, unknown> }));

vi.mock('next/navigation', () => ({
  useSearchParams: () => nav.params,
  useRouter: () => ({ replace: nav.replace, push: vi.fn(), back: vi.fn() }),
  usePathname: () => '/designs/studio',
}));
vi.mock('@/lib/api/client', async (original) => ({ ...(await original<typeof Client>()), api: client.api }));
vi.mock('@/lib/api/query', async (original) => ({
  ...(await original<typeof Query>()),
  useApi: () => ({ data: undefined, isPending: true, error: null }),
}));
vi.mock('@/lib/auth', () => ({ useAuth: () => ({ me: { username: 'fox' }, can: () => true, canChat: false }) }));
vi.mock('@tanstack/react-query', async (original) => ({
  ...(await original<typeof ReactQuery>()),
  useQueryClient: () => ({ invalidateQueries: vi.fn() }),
}));
vi.mock('@/features/studio/studio-viewer', async () => {
  const React = await import('react');
  const three = await import('three');
  const { StudioWorld } = await import('@/features/studio/studioWorld');
  const { Overlay3D } = await import('@/features/studio/overlay3d');
  const camera = new three.PerspectiveCamera(60, 4 / 3, 0.1, 300);
  camera.position.set(32, 90, 80);
  camera.lookAt(32, 20, 32);
  camera.updateMatrixWorld();
  const WorldViewer = React.forwardRef<unknown, Record<string, unknown>>(function WorldViewer(_p, ref) {
    const surface = React.useRef<HTMLCanvasElement>(null);
    React.useImperativeHandle(ref, () => ({
      engine: () => {
        if (!surface.current) return null;
        view.engine ??= {
          runtime: { scene: { add: () => undefined } },
          rig: { orbitYaw: 0, flyYaw: 0 },
          camera,
          surface: surface.current,
          focus: vi.fn(),
          setLevelCut: vi.fn(),
          aimNow: () => Promise.resolve(null),
          aimAtPointer: () => Promise.resolve(null),
          capturePng: () => Promise.reject(new Error('no frame')),
        };
        return view.engine;
      },
    }));
    return React.createElement('canvas', { ref: surface, tabIndex: 0, 'data-testid': 'viewer-surface' });
  });
  return {
    StudioWorld,
    Overlay3D,
    WorldViewer,
    WorldViewerProvider: ({ children }: { children: unknown }) => children,
    loadBundle: () => new Promise(() => undefined),
    blockIconAtlasAsync: () => new Promise(() => undefined),
    blockIconUrl: () => null,
    ASSETS_CHANGED: 'mineclaw:test-assets',
    Raycaster: three.Raycaster,
    Vector2: three.Vector2,
    Vector3: three.Vector3,
  };
});

import { StudioPage } from '@/features/studio/StudioPage';

const mineSession = (over: Partial<DesignerSession> = {}): DesignerSession => ({
  id: 41,
  owner: 'fox',
  model: 'main',
  designId: null,
  name: 'new design',
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
  ...over,
});

type Route = (body: unknown) => unknown;
let routes: Record<string, Route> = {};
let canvasRev = 0;

beforeEach(() => {
  localStorage.clear();
  vi.stubEnv('NEXT_PUBLIC_MOCK', '1');
  canvasRev = 0;
  routes = {
    'designer.canvas': () => Promise.resolve({ rev: ++canvasRev, applied: 0 }),
  };
  client.api.mockImplementation((name: string, o?: { body?: unknown }) => {
    const route = routes[name];
    return route ? route(o?.body) : new Promise<never>(() => undefined);
  });
});
afterEach(async () => {
  // Unmounting disposes the studio, which sends the edits still waiting once: let that reach this test's fake API.
  cleanup();
  for (let i = 0; i < 40; i++) await Promise.resolve();
  vi.restoreAllMocks();
  client.api.mockReset();
  nav.replace.mockClear();
  nav.params = new URLSearchParams();
  view.engine = null;
  vi.unstubAllEnvs();
  delete (window as { __studio?: unknown }).__studio;
  setPlotMode({ kind: 'build' });
});

/** Opens `?session=41` on the given session and waits for the viewer to be up. */
async function open(session: DesignerSession, query = `session=${session.id}`) {
  nav.params = new URLSearchParams(query);
  routes['designer.get'] = () => Promise.resolve(session);
  routes['designer.create'] = () => Promise.resolve(session);
  const r = render(<StudioPage />);
  // A ceiling, not a wait: the first page of a run also loads three.js and the studio modules.
  await screen.findByTestId('studio', {}, { timeout: 10_000 });
  await waitFor(() => expect(screen.getByTestId('stage-hint')).toBeInTheDocument(), { timeout: 10_000 });
  const studio = (window as unknown as { __studio: { studio: Studio } }).__studio.studio;
  return { ...r, studio };
}

const calls = (name: string) => client.api.mock.calls.filter((c) => c[0] === name);
const bodyOf = (call: unknown[]) => (call[1] as { body: Record<string, unknown> }).body;
/** Lets the promise chains of the studio's wire run (no timers involved). */
const ticks = () =>
  act(async () => {
    for (let i = 0; i < 40; i++) await Promise.resolve();
  });
const DUG: V3 = [30, 20, 30];
const dig = (studio: Studio, at: V3 = DUG) =>
  act(() => {
    studio.edit([[...at, 0, 0]], 'test', 'dug a test cell');
  });
const focusWorld = () => act(() => screen.getByTestId('viewer-surface').focus());
const press = (key: string, init: KeyboardEventInit = {}) =>
  act(() => {
    fireEvent.keyDown(document.activeElement ?? document.body, { key, ...init });
  });
/** "Use" on a preset: its components are placed (one edit). */
async function usePreset(studio: Studio) {
  fireEvent.click(screen.getByTestId('preset-1.12.2-diamond-y11'));
  await waitFor(() => expect(screen.getAllByRole('option').length).toBeGreaterThan(1));
  expect(studio.canvas.dug.size).toBeGreaterThan(0);
}
const holes = (studio: Studio) => [...studio.canvas.holes()].sort().join(' ');

describe('the studio page', () => {
  it('opens a mine with the viewer up: the start steps, the hint line for nothing selected and the ? button', async () => {
    const { studio } = await open(mineSession());
    expect(studio.mine).toBe(true);
    expect(screen.getByTestId('mine-start')).toBeInTheDocument();
    expect(screen.getByTestId('stage-hint')).toHaveTextContent(mineHint('idle'));
    expect(screen.getByTestId('shortcuts-button')).toBeInTheDocument();
  });
});

describe('sync wiring (WP1)', () => {
  it('sends waiting edits at once when the page is hidden, not after the debounce', async () => {
    const { studio } = await open(mineSession());
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] });
    try {
      dig(studio);
      await ticks();
      expect(calls('designer.canvas')).toHaveLength(0);
      act(() => {
        window.dispatchEvent(new Event('pagehide'));
      });
      await ticks();
      expect(calls('designer.canvas')).toHaveLength(1);
    } finally {
      vi.useRealTimers();
    }
  });

  it.each([
    [
      'the seed',
      "Couldn't change the seed",
      () => {
        const seed = screen.getByTestId('mine-seed');
        fireEvent.change(seed, { target: { value: '999' } });
        fireEvent.submit(seed.closest('form')!);
      },
    ],
    [
      'the kind',
      "Couldn't switch to build",
      () => {
        fireEvent.change(screen.getByTestId('kind-select'), { target: { value: 'build' } });
      },
    ],
    [
      'the version',
      "Couldn't change the version",
      () => {
        fireEvent.change(screen.getByTestId('version-select'), { target: { value: 'b1.8.1' } });
      },
    ],
    [
      'a preset',
      "Couldn't tag it preferred for 1.12.2",
      () => {
        fireEvent.click(screen.getByTestId('preset-1.12.2-diamond-y11'));
      },
    ],
  ])('changing %s waits for unsent edits and says so instead of going ahead', async (_what, said, change) => {
    routes['designer.canvas'] = () => Promise.reject(Object.assign(new Error('the bot is away'), { status: 503 }));
    routes['designer.version'] = () => Promise.resolve({ version: '1.12.2', target: '1.12.2', changed: false });
    const { studio } = await open(mineSession());
    dig(studio);
    act(change);
    const alert = await screen.findByTestId('studio-alert');
    expect(alert).toHaveTextContent(said);
    expect(alert).toHaveTextContent('The latest edits have not reached the designer');
    expect(calls('designer.version')).toHaveLength(0);
  });

  it('the sync chip says the bot refused the edits, and Retry sends them again', async () => {
    routes['designer.canvas'] = () => Promise.reject(Object.assign(new Error('no such session'), { status: 404 }));
    const { studio } = await open(mineSession());
    dig(studio);
    await waitFor(() => expect(screen.getByTestId('sync-chip')).toHaveAttribute('data-sync', 'blocked'));
    expect(calls('designer.canvas')).toHaveLength(1);
    routes['designer.canvas'] = () => Promise.resolve({ rev: ++canvasRev, applied: 1 });
    fireEvent.click(within(screen.getByTestId('sync-chip')).getByRole('button', { name: 'Retry' }));
    await waitFor(() => expect(screen.getByTestId('sync-chip')).toHaveAttribute('data-sync', 'idle'));
    expect(calls('designer.canvas')).toHaveLength(2);
  });
});

describe('save wiring (WP3)', () => {
  const saved = mineSession({ designId: 7, name: 'Strip floor', designRevision: 1 });
  const reply = (rev: number) => ({
    designId: 7,
    rev,
    name: 'Strip floor',
    blocks: 0,
    created: false,
    text: `Saved #7 r${rev}`,
  });

  it('Ctrl+S saves from anywhere on the page, without the picture when none could be taken', async () => {
    let rev = 1;
    routes['designer.save'] = () => Promise.resolve(reply(++rev));
    const { studio } = await open(saved);
    dig(studio);
    routes['designer.get'] = () => Promise.resolve({ ...saved, rev: canvasRev, dug: [DUG] });
    expect(screen.getByTestId('unsaved-chip')).toBeInTheDocument();
    act(() => {
      fireEvent.keyDown(screen.getByTestId('mine-seed'), { key: 's', ctrlKey: true });
    });
    await waitFor(() => expect(calls('designer.save')).toHaveLength(1));
    expect(bodyOf(calls('designer.save')[0]!)).toMatchObject({ name: 'Strip floor' });
    expect(bodyOf(calls('designer.save')[0]!)).not.toHaveProperty('previewPng');
    const notice = await screen.findByTestId('studio-notice');
    expect(notice).toHaveTextContent('Saved. The picture could not be taken; use Update picture.');
    expect(notice).toHaveAttribute('data-tone', 'warn');
    await waitFor(() => expect(screen.queryByTestId('unsaved-chip')).toBeNull());
  });

  it('the picture shows the plan as it is at the save, not the one still waiting for the canvas to be still', async () => {
    routes['designer.save'] = () => Promise.resolve(reply(2));
    const shown = vi.spyOn(Overlay3D.prototype, 'setMinePreview');
    const { studio } = await open(saved);
    let pictured: MinePreview | null | undefined;
    view.engine!.capturePng = () => {
      pictured = shown.mock.calls.at(-1)?.[0];
      return Promise.resolve('data:image/png;base64,AA');
    };
    dig(studio);
    routes['designer.get'] = () => Promise.resolve({ ...saved, rev: canvasRev, dug: [DUG] });
    act(() => {
      fireEvent.keyDown(window, { key: 's', ctrlKey: true });
    });
    await waitFor(() => expect(calls('designer.save')).toHaveLength(1));
    expect(bodyOf(calls('designer.save')[0]!)).toHaveProperty('previewPng', 'data:image/png;base64,AA');
    expect(pictured?.planned).toContainEqual(DUG);
  });

  it('offers one "Resync and save again" when the saved canvas lacks cells of this page', async () => {
    let rev = 1;
    routes['designer.save'] = () => Promise.resolve(reply(++rev));
    const { studio } = await open(saved);
    dig(studio);
    // The bot's copy read back after the save does not have the dug cell.
    routes['designer.get'] = () => Promise.resolve({ ...saved, rev: canvasRev, dug: [] });
    act(() => {
      fireEvent.keyDown(window, { key: 's', ctrlKey: true });
    });
    const alert = await screen.findByTestId('studio-alert');
    expect(alert).toHaveTextContent('Saved r2, but 1 cell on this page is not in it.');
    const before = calls('designer.canvas').length;
    fireEvent.click(within(alert).getByRole('button', { name: 'Resync and save again' }));
    await waitFor(() => expect(calls('designer.save')).toHaveLength(2));
    expect(bodyOf(calls('designer.save')[1]!)).toMatchObject({ summary: 'Resynced cells the previous save missed' });
    const resent = calls('designer.canvas')
      .slice(before)
      .flatMap((c) => ((bodyOf(c).ops ?? []) as Array<{ cells: number[][] }>).flatMap((e) => e.cells));
    expect(resent).toContainEqual([...DUG, 0, 0]);
  });

  it('a save that goes through takes away the failure an earlier save left on the line', async () => {
    routes['designer.save'] = () => Promise.reject(new Error('the designer is away'));
    const { studio } = await open(saved);
    dig(studio);
    routes['designer.get'] = () => Promise.resolve({ ...saved, rev: canvasRev, dug: [DUG] });
    act(() => {
      fireEvent.keyDown(window, { key: 's', ctrlKey: true });
    });
    const alert = await screen.findByTestId('studio-alert');
    expect(alert).toHaveTextContent('the designer is away');
    routes['designer.save'] = () => Promise.resolve(reply(2));
    act(() => {
      fireEvent.keyDown(window, { key: 's', ctrlKey: true });
    });
    await waitFor(() => expect(calls('designer.save')).toHaveLength(2));
    await waitFor(() => expect(screen.queryByTestId('studio-alert')).toBeNull());
  });
});

describe('unsaved work in an earlier session (WP3 item 8)', () => {
  it('a dirty session is remembered for its design, and "Open in studio" offers to resume it', async () => {
    const earlier = mineSession({ id: 41, designId: 7, name: 'Strip floor', designRevision: 1 });
    const first = await open(earlier);
    dig(first.studio);
    await waitFor(() => expect(localStorage.getItem('mineclaw.studio.unsaved:fox:7')).toBe('41'));
    first.unmount();
    view.engine = null;

    const fresh = mineSession({ id: 42, designId: 7, name: 'Strip floor', designRevision: 1 });
    await open(fresh, 'id=7');
    expect(calls('designer.create').at(-1)?.[1]).toEqual({ body: { designId: 7 } });
    const alert = await screen.findByTestId('studio-alert');
    expect(alert).toHaveTextContent('You have unsaved work on this design in an earlier session.');
    const assign = vi.spyOn(window.location, 'assign').mockImplementation(() => undefined);
    fireEvent.click(within(alert).getByRole('button', { name: 'Resume it' }));
    expect(assign).toHaveBeenCalledWith('/designs/studio?session=41&id=7');
    // The new session is clean: it does not replace the earlier one as the work to resume.
    expect(localStorage.getItem('mineclaw.studio.unsaved:fox:7')).toBe('41');
  });

  it('a session that is saved clean is forgotten, and reopening a session never offers anything', async () => {
    localStorage.setItem('mineclaw.studio.unsaved:fox:7', '41');
    routes['designer.save'] = () =>
      Promise.resolve({ designId: 7, rev: 2, name: 'Strip floor', blocks: 0, created: false, text: 'Saved #7 r2' });
    const session = mineSession({ id: 41, designId: 7, name: 'Strip floor', designRevision: 1 });
    const { studio } = await open(session, 'session=41&id=7');
    expect(screen.queryByTestId('studio-alert')).toBeNull();
    dig(studio);
    routes['designer.get'] = () => Promise.resolve({ ...session, rev: canvasRev, dug: [DUG] });
    act(() => {
      fireEvent.keyDown(window, { key: 's', ctrlKey: true });
    });
    await waitFor(() => expect(localStorage.getItem('mineclaw.studio.unsaved:fox:7')).toBeNull());
  });

  it('"Save as new design" lets go of the old design’s offer: that work is saved as the new design', async () => {
    routes['designer.save'] = () =>
      Promise.resolve({ designId: 9, rev: 1, name: 'Strip floor', blocks: 0, created: true, text: 'Saved #9 r1' });
    const session = mineSession({ id: 41, designId: 7, name: 'Strip floor', designRevision: 1 });
    const { studio } = await open(session, 'session=41&id=7');
    dig(studio);
    await waitFor(() => expect(localStorage.getItem('mineclaw.studio.unsaved:fox:7')).toBe('41'));
    routes['designer.get'] = () => Promise.resolve({ ...session, designId: 9, rev: canvasRev, dug: [DUG] });
    fireEvent.click(screen.getByRole('button', { name: 'Save as new design' }));
    await waitFor(() => expect(calls('designer.save')).toHaveLength(1));
    expect(bodyOf(calls('designer.save')[0]!)).toMatchObject({ asNew: true });
    await waitFor(() => expect(localStorage.getItem('mineclaw.studio.unsaved:fox:7')).toBeNull());
    expect(localStorage.getItem('mineclaw.studio.unsaved:fox:9')).toBeNull();
  });
});

describe('mine keys and panel wiring (WP5 to WP7)', () => {
  it('a held arrow on a selected component is one move and one undo step', async () => {
    const { studio } = await open(mineSession());
    await usePreset(studio);
    fireEvent.click(screen.getAllByRole('option')[0]!);
    const placed = holes(studio);
    focusWorld();
    press('ArrowRight');
    press('ArrowRight');
    press('ArrowRight');
    await waitFor(() => expect(holes(studio)).not.toBe(placed));
    act(() => {
      studio.undo();
    });
    expect(holes(studio)).toBe(placed);
  });

  it('a raw turn over component cells is refused, and the whole-design turn is hidden while components exist', async () => {
    const { studio } = await open(mineSession());
    expect(screen.getByRole('toolbar', { name: 'Whole design' })).toBeInTheDocument();
    await usePreset(studio);
    act(() => studio.setSelection(null));
    expect(screen.queryByRole('toolbar', { name: 'Whole design' })).toBeNull();
    const placed = holes(studio);
    act(() => studio.setSelection(selectAll(studio.canvas)));
    focusWorld();
    press(']');
    expect(await screen.findByTestId('studio-notice')).toHaveTextContent('Select the component to move it.');
    expect(holes(studio)).toBe(placed);
  });

  it('while a piece is in hand, Delete leaves a box selection alone and the tooltip and hint list the keys', async () => {
    const { studio } = await open(mineSession());
    // Dug by hand, with no component selected since the viewer came up: the page's key listener is the first to hear keys,
    // so only the hook's handlesKey keeps its Delete from clearing the selection's plan.
    dig(studio);
    dig(studio, [31, 20, 30]);
    fireEvent.click(screen.getAllByRole('button', { name: /^Place / })[0]!);
    expect(screen.getByTestId('mine-placement-tip')).toHaveTextContent(
      'Click to place · [ ] or R turn it · Right-click or Esc cancels',
    );
    expect(screen.getByTestId('stage-hint')).toHaveTextContent(mineHint('placing'));
    act(() => studio.setSelection(selectAll(studio.canvas)));
    focusWorld();
    press('Delete');
    expect(studio.canvas.dug.size).toBe(2);
  });

  it('the working level moves the view cut, and PgUp steps it when nothing is selected', async () => {
    await open(mineSession());
    const stage = screen.getByTestId('studio');
    expect(stage).toHaveAttribute('data-under', '');
    const chip = screen.getByTitle(/^diamond ore: preferred feet Y ?\d+$/i);
    const y = Number(/(\d+)$/.exec(chip.title)![1]);
    fireEvent.click(chip);
    expect(within(screen.getByTestId('work-level')).getByText(`Y${y}`)).toBeInTheDocument();
    expect(stage).toHaveAttribute('data-under', String(y + 2));
    focusWorld();
    press('PageUp');
    expect(stage).toHaveAttribute('data-under', String(y + 3));
    expect(within(screen.getByTestId('work-level')).getByText(`Y${y + 1}`)).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Top view' }));
    expect(view.engine!.focus).toHaveBeenLastCalledWith(
      expect.any(Number),
      y + 1,
      expect.any(Number),
      expect.objectContaining({ pitch: 89.9 }),
    );
  });

  it('dragging a selected component by its handle moves only the outline, then commits one move on release', async () => {
    const { studio } = await open(mineSession());
    await usePreset(studio);
    fireEvent.click(screen.getAllByRole('option')[0]!);
    const { overlay } = (window as unknown as { __studio: { overlay: Overlay3D } }).__studio;
    const surface = screen.getByTestId('viewer-surface');
    vi.spyOn(surface, 'getBoundingClientRect').mockReturnValue(new DOMRect(0, 0, 800, 600));
    // Look at the handles from the side, as studioGizmo.test.ts does.
    const camera = view.engine!.camera as PerspectiveCamera;
    const aim = overlay.gizmo.position.clone();
    camera.position.copy(aim).add(new Vector3(0, 0, 40));
    camera.lookAt(aim);
    camera.updateMatrixWorld();
    const client = (p: Vector3) => {
      const ndc = p.clone().project(camera);
      return { clientX: (ndc.x + 1) * 400, clientY: (1 - ndc.y) * 300 };
    };
    const picks = (at: { clientX: number; clientY: number }) => {
      const ray = new Raycaster();
      ray.setFromCamera(new Vector2(at.clientX / 400 - 1, 1 - at.clientY / 300), camera);
      return overlay.pickHandle(ray);
    };
    const grip = [0.75, 1, 1.5, 2]
      .map((t) => client(aim.clone().add(new Vector3(t * overlay.gizmo.scale.x, 0, 0))))
      .find((at) => picks(at) === 'x');
    expect(grip).toBeDefined();
    const along = client(aim.clone().add(new Vector3(overlay.gizmo.scale.x * 2 + 6, 0, 0)));
    const placed = holes(studio);
    act(() => {
      fireEvent.pointerDown(surface, { ...grip!, button: 0, pointerId: 7 });
    });
    act(() => {
      fireEvent.pointerMove(window, { ...along, buttons: 1, pointerId: 7 });
    });
    expect(holes(studio)).toBe(placed);
    act(() => {
      fireEvent.pointerUp(window, { ...along, button: 0, pointerId: 7 });
    });
    expect(holes(studio)).not.toBe(placed);
    act(() => {
      studio.undo();
    });
    expect(holes(studio)).toBe(placed);
  });

  it('the dig estimate waits while a pointer is held on the view', async () => {
    const { studio } = await open(mineSession());
    const stats = screen.getByTestId('mine-stats');
    const before = stats.textContent;
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] });
    try {
      act(() => {
        fireEvent.pointerDown(screen.getByTestId('viewer-surface'), {
          clientX: 10,
          clientY: 10,
          button: 0,
          pointerId: 3,
        });
      });
      for (let x = 30; x < 36; x++) dig(studio, [x, 20, 30]);
      act(() => {
        vi.advanceTimersByTime(1000);
      });
      expect(stats.textContent).toBe(before);
      act(() => {
        fireEvent.pointerUp(window, { clientX: 10, clientY: 10, button: 0, pointerId: 3 });
      });
      act(() => {
        vi.advanceTimersByTime(300);
      });
      expect(stats.textContent).not.toBe(before);
    } finally {
      vi.useRealTimers();
    }
  });

  it('the start steps leave once the mine has components', async () => {
    const { studio } = await open(mineSession());
    await usePreset(studio);
    expect(screen.queryByTestId('mine-start')).toBeNull();
  });

  it('? opens the shortcut sheet outside the stage, and the button does too', async () => {
    await open(mineSession());
    focusWorld();
    press('?');
    const sheet = await screen.findByTestId('studio-shortcuts');
    expect(screen.getByTestId('studio-stage')).not.toContainElement(sheet);
    act(() => {
      fireEvent.keyDown(sheet, { key: 'Escape' });
    });
    await waitFor(() => expect(screen.queryByTestId('studio-shortcuts')).toBeNull());
    fireEvent.click(screen.getByTestId('shortcuts-button'));
    expect(await screen.findByTestId('studio-shortcuts')).toBeInTheDocument();
  });
});
