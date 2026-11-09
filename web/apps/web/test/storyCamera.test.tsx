import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { createRef, forwardRef, useEffect, useImperativeHandle, useState } from 'react';
import { PerspectiveCamera } from 'three';
import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import type { WorldViewerHandle, WorldViewerProps } from '@mineclaw/world-viewer';
import { ControlCluster } from '@/features/story/StoryHud';
import { StoryMap, type StoryMapHandle, type StoryMapProps } from '@/features/story/StoryMap';
import { useStoryKeys } from '@/features/story/mode/keys';
import type { Story } from '@/features/story/mode/commands';
import type * as ChestBadges from '@/features/story/chestBadges';

const viewer = vi.hoisted(() => ({
  mounts: 0,
  mode: '',
  focus: vi.fn(),
  lock: vi.fn(),
  engine: {},
  source: {},
  locked: false,
  aimClick: undefined as WorldViewerProps['onAimClick'],
  resolveAim: undefined as WorldViewerProps['resolveAim'],
  beforeFrame: undefined as WorldViewerProps['onBeforeFrame'],
  afterFrame: undefined as WorldViewerProps['onAfterFrame'],
}));
vi.mock('@/features/story/chestBadges', async (importOriginal) => ({
  ...await importOriginal<typeof ChestBadges>(),
  useContainerBadges: () => ({ current: new Map() }),
}));
vi.mock('@/features/story/StoryAudio', () => ({ StoryAudio: () => null }));
vi.mock('@/features/world/integration', () => ({
  useWorldHost: () => ({ ready: true, source: viewer.source, mod: { WorldViewer: MockViewer } }),
}));
const MockViewer = forwardRef<WorldViewerHandle, WorldViewerProps>(function MockViewer(props, ref) {
  viewer.mode = props.cameraMode ?? '';
  viewer.aimClick = props.onAimClick;
  viewer.resolveAim = props.resolveAim;
  viewer.beforeFrame = props.onBeforeFrame;
  viewer.afterFrame = props.onAfterFrame;
  useEffect(() => {
    viewer.mounts++;
  }, []);
  useImperativeHandle(ref, () => ({ engine: () => viewer.engine, locked: () => viewer.locked }) as WorldViewerHandle);
  return <div data-testid="fake-viewer" data-camera={props.cameraMode} />;
});

beforeEach(() => {
  viewer.mounts = 0;
  viewer.locked = false;
  viewer.focus.mockClear();
  viewer.lock.mockClear();
  viewer.engine = {
    surface: { focus: viewer.focus }, lock: viewer.lock,
    rig: { target: { x: 8, y: 64, z: 12 }, isoZoom: 44, flyPos: { set: vi.fn() } },
  };
  vi.stubGlobal('requestAnimationFrame', () => 1);
  vi.stubGlobal('cancelAnimationFrame', () => undefined);
  Object.defineProperty(document, 'fullscreenElement', { configurable: true, get: () => document.body });
  Object.defineProperty(navigator, 'keyboard', { configurable: true, value: { lock: vi.fn(async () => {}), unlock: vi.fn() } });
});
afterEach(() => { vi.unstubAllGlobals(); Reflect.deleteProperty(document, 'fullscreenElement'); Reflect.deleteProperty(navigator, 'keyboard'); });

const mapProps = (): StoryMapProps => ({
  state: undefined,
  selection: { bodies: [], chests: [], bench: null },
  tool: { kind: 'select' },
  build: { height: 3, facing: 'n', material: 'cobblestone', roof: 'flat', overhang: false },
  mineFilter: 'all',
  heightMode: 'surface',
  layers: 1,
  level: 63,
  cutY: 67,
  view: 'top',
  keys: { current: new Set(['KeyW']) },
  home: [0, 63, 0],
  onSelect: vi.fn(),
  onSelectSame: vi.fn(),
  onTool: vi.fn(),
  onContext: vi.fn(),
  onDropTool: vi.fn(),
});

it('switches the same mounted viewer to free cam and back, giving mouse control to the viewer only while flying', async () => {
  const props = mapProps();
  const { rerender } = render(<StoryMap {...props} />);
  const canvas = screen.getByLabelText<HTMLCanvasElement>(/Colony map/);
  expect(viewer.mode).toBe('top');
  expect(props.keys.current.size).toBe(0);
  rerender(<StoryMap {...props} view="fly" />);
  expect(viewer.mode).toBe('fly');
  expect(canvas.style.pointerEvents).toBe('none');
  expect(canvas.tabIndex).toBe(-1);
  await waitFor(() => expect(viewer.lock).toHaveBeenCalledOnce());
  const rig = (viewer.engine as { rig: { flyPos: { set: ReturnType<typeof vi.fn> }; target: object; isoZoom: number } })
    .rig;
  expect(rig.flyPos.set).toHaveBeenCalledWith(8, 76, 28);
  expect(rig.target).toEqual({ x: 8, y: 64, z: 12 });
  expect(rig.isoZoom).toBe(44);
  rerender(<StoryMap {...props} view="top" />);
  expect(viewer.mode).toBe('top');
  expect(canvas.style.pointerEvents).toBe('');
  expect(canvas.tabIndex).toBe(0);
  expect(document.activeElement).toBe(canvas);
  expect(viewer.mounts).toBe(1);
});

it('moves free cam to the colony when its first home position arrives after the viewer', () => {
  const frames = new Map<number, FrameRequestCallback>();
  let frameId = 0;
  vi.stubGlobal('requestAnimationFrame', (cb: FrameRequestCallback) => {
    frames.set(++frameId, cb);
    return frameId;
  });
  vi.stubGlobal('cancelAnimationFrame', (id: number) => frames.delete(id));
  const frame = () => {
    const pending = [...frames.values()];
    frames.clear();
    for (const cb of pending) cb(16);
    viewer.beforeFrame?.(viewer.engine as Parameters<NonNullable<WorldViewerProps['onBeforeFrame']>>[0], 16, 0);
  };
  const target = {
    x: 0,
    y: 0,
    z: 0,
    set(x: number, y: number, z: number) {
      this.x = x;
      this.y = y;
      this.z = z;
    },
  };
  const position = vi.fn();
  const setBotBadges = vi.fn();
  const setBotLabels = vi.fn();
  const matrix = { elements: new Array<number>(16).fill(0) };
  viewer.engine = {
    surface: { focus: viewer.focus }, lock: viewer.lock,
    rig: { target, isoZoom: 44, flyPos: { set: position } },
    camera: { matrixWorldInverse: matrix, projectionMatrix: matrix, matrixWorld: matrix, projectionMatrixInverse: matrix },
    setBotLabels,
    setBotBadges,
    setLevelCut: vi.fn(),
    setHolograms: vi.fn(),
  };
  const props = mapProps();
  props.state = { pawns: [{ name: 'maid_chan_109', display: 'Nickname', job: { type: 'mine', label: 'Mining quarry' } }] } as unknown as StoryMapProps['state'];
  const { rerender } = render(<StoryMap {...props} view="fly" home={null} />);
  vi.spyOn(screen.getByLabelText<HTMLCanvasElement>(/Colony map/), 'getContext').mockReturnValue(null);
  frame(); // engine exists, but the asynchronous colony position has not arrived
  expect(position).toHaveBeenLastCalledWith(0, 12, 16);
  expect(setBotBadges).toHaveBeenLastCalledWith([expect.objectContaining({ text: '109', activity: 'Mining quarry' })]);
  expect(setBotLabels).toHaveBeenLastCalledWith(false);
  rerender(<StoryMap {...props} view="fly" home={[100, 63, 200]} />);
  frame();
  expect(position).toHaveBeenLastCalledWith(100.5, 76, 216.5);
  expect(viewer.mode).toBe('fly');
  expect(viewer.mounts).toBe(1);
  rerender(<StoryMap {...props} view="top" />);
  frame();
  expect(setBotBadges).toHaveBeenLastCalledWith([]);
  expect(setBotLabels).toHaveBeenLastCalledWith(false);
});

it('routes bare-world clicks through fullscreen before the engine can lock and leaves open editor menus alone', async () => {
  const props = mapProps();
  const page = render(<StoryMap {...props} />);
  const surface = screen.getByTestId('fake-viewer');
  (viewer.engine as { surface: HTMLElement }).surface = surface;
  page.rerender(<StoryMap {...props} view="fly" />);
  await waitFor(() => expect(viewer.lock).toHaveBeenCalledOnce());
  viewer.lock.mockClear();
  let finish!: () => void;
  Object.defineProperty(document, 'fullscreenElement', { configurable: true, get: () => null });
  const fullscreen = vi.fn(() => new Promise<void>((resolve) => { finish = resolve; }));
  Object.defineProperty(screen.getByTestId('story-map'), 'requestFullscreen', { value: fullscreen });
  const bypass = vi.fn(); surface.addEventListener('pointerdown', bypass);
  fireEvent.pointerDown(surface, { button: 0 });
  expect(fullscreen).toHaveBeenCalledOnce(); expect(bypass).not.toHaveBeenCalled(); expect(viewer.lock).not.toHaveBeenCalled();
  await act(async () => { finish(); await Promise.resolve(); });
  expect(viewer.lock).toHaveBeenCalledOnce();
  viewer.lock.mockClear();
  const dialog = document.createElement('div'); dialog.setAttribute('role', 'dialog'); document.body.append(dialog);
  fireEvent.pointerDown(surface, { button: 0 });
  expect(viewer.lock).not.toHaveBeenCalled(); expect(bypass).not.toHaveBeenCalled();
  dialog.remove(); surface.removeEventListener('pointerdown', bypass);
});

it('has an always-available free camera button, and flight keys cannot pause the colony or rotate a held tool', () => {
  const pause = vi.fn(),
    build = vi.fn();
  function Controls() {
    const [view, setView] = useState<'top' | 'angled' | 'fly'>('top');
    const c = {
      view,
      setView,
      fullscreen: { el: null },
      keys: { current: new Set() },
      tool: { kind: 'build' },
      heightTool: null,
      matTool: null,
      setBuild: build,
      togglePause: pause,
    } as unknown as Story;
    useStoryKeys(c);
    return (
      <ControlCluster
        name="Watch"
        paused={false}
        canWrite
        onPause={pause}
        view={view}
        onView={() => setView((v) => (v === 'top' ? 'angled' : 'top'))}
        onFly={() => setView((v) => (v === 'fly' ? 'top' : 'fly'))}
        level={63}
        ground={63}
        height={3}
        xray={false}
        onLevel={vi.fn()}
        onXray={vi.fn()}
        onHelp={vi.fn()}
      />
    );
  }
  render(<Controls />);
  fireEvent.click(screen.getByRole('button', { name: 'Switch to free camera' }));
  expect(screen.getByRole('button', { name: 'Return to the top-down map' })).toHaveAttribute('aria-pressed', 'true');
  for (const code of ['Space', 'KeyQ', 'KeyE', 'KeyW', 'PageUp', 'Escape']) fireEvent.keyDown(window, { code });
  expect(pause).not.toHaveBeenCalled();
  expect(build).not.toHaveBeenCalled();
  fireEvent.keyDown(window, { code: 'KeyV' });
  expect(screen.getByRole('button', { name: 'Switch to free camera' })).toHaveAttribute('aria-pressed', 'false');
  fireEvent.keyDown(window, { code: 'KeyV', shiftKey: true });
  expect(screen.getByRole('button', { name: 'Return to the top-down map' })).toBeTruthy();
  fireEvent.click(screen.getByRole('button', { name: 'Return to the top-down map' }));
  expect(screen.getByRole('button', { name: 'Switch to the angled view' })).toBeTruthy();
});

it.each([54, 146, 58, 61, 62])(
  'opens the shared furniture inspector for freecam block %s while a build tool is held',
  (id) => {
    const props = mapProps();
    props.tool = { kind: 'build', id: 'block' };
    props.state = { storages: [{ chest: [2, 64, 3], doubleWith: [3, 64, 3] }] } as StoryMapProps['state'];
    const { rerender } = render(<StoryMap {...props} view="fly" />);
    const target = { kind: 'block' as const, id, x: 3, y: 64, z: 3, meta: 0, face: 2, name: 'Furniture', distance: 4 };
    const unlock = vi.fn();
    Object.defineProperty(document, 'exitPointerLock', { configurable: true, value: unlock });
    viewer.aimClick!(target, { shift: false, ctrl: false });
    expect(props.onSelect).not.toHaveBeenCalled(); // initial unlocked click cannot inspect or place
    viewer.locked = true;
    const position = (viewer.engine as { rig: { flyPos: { set: ReturnType<typeof vi.fn> } } }).rig.flyPos.set;
    const seeded = position.mock.calls.length;
    viewer.aimClick!(target, { shift: false, ctrl: false });
    expect(props.onSelect).toHaveBeenCalledExactlyOnceWith(
      id === 54 || id === 146
        ? { bodies: [], chests: [[2, 64, 3]], bench: null }
        : { bodies: [], chests: [], bench: [3, 64, 3] },
    );
    expect(unlock).toHaveBeenCalledOnce();
    expect(position.mock.calls).toHaveLength(seeded); // unlocking does not reset the free camera's pose
    expect(props.onTool).not.toHaveBeenCalled();
    expect(props.onContext).not.toHaveBeenCalled();
    rerender(<StoryMap {...props} view="top" />);
    viewer.aimClick!(target, { shift: false, ctrl: false });
    expect(props.onSelect).toHaveBeenCalledOnce(); // an old asynchronous ray reply cannot act after switching views
  },
);

it('shows the crosshair only for this map while freecam owns pointer lock', () => {
  const props = mapProps();
  const { rerender } = render(<StoryMap {...props} view="fly" />);
  expect(screen.queryByTestId('crosshair')).not.toBeInTheDocument();
  Object.defineProperty(document, 'pointerLockElement', {
    configurable: true,
    value: screen.getByTestId('fake-viewer'),
  });
  fireEvent(document, new Event('pointerlockchange'));
  expect(screen.getByTestId('crosshair')).toBeInTheDocument();
  rerender(<StoryMap {...props} view="top" />);
  expect(screen.queryByTestId('crosshair')).not.toBeInTheDocument();
  rerender(<StoryMap {...props} view="fly" />);
  Object.defineProperty(document, 'pointerLockElement', { configurable: true, value: document.body });
  fireEvent(document, new Event('pointerlockchange'));
  expect(screen.queryByTestId('crosshair')).not.toBeInTheDocument();
  Object.defineProperty(document, 'pointerLockElement', { configurable: true, value: null });
  fireEvent(document, new Event('pointerlockchange'));
});

it('edits only in explicit mode, stacks on optimistic holograms and leaves ordinary inspection unchanged', async () => {
  const props = mapProps();
  let saved!: (reply: { buildId: string; rev: number }) => void;
  const edit = vi.fn().mockImplementationOnce(() => new Promise((resolve) => { saved = resolve; })).mockResolvedValue({ buildId: 'new', rev: 8 });
  props.state = { id: 'colony', version: '1.12.2', rev: 1, builds: [], ghosts: { rev: 1, blocks: [] } } as unknown as StoryMapProps['state'];
  render(<StoryMap {...props} view="fly" canEdit onEditPlan={edit} />);
  const block = { kind: 'block' as const, x: 0, y: 64, z: 3, id: 4, meta: 0, face: 2, name: 'Cobble', distance: 3 };
  expect(screen.queryByRole('button', { name: 'Edit plan' })).not.toBeInTheDocument();
  fireEvent.click(screen.getByRole('button', { name: 'Freecam tools' }));
  fireEvent.click(screen.getByRole('button', { name: 'Edit plan' }));
  act(() => viewer.aimClick!(block, { shift: false, ctrl: false, button: 2 }));
  expect(edit).not.toHaveBeenCalled(); // acquisition click never edits
  viewer.locked = true;
  act(() => viewer.aimClick!(block, { shift: false, ctrl: false, button: 2 }));
  expect(edit).toHaveBeenCalledExactlyOnceWith([[0, 64, 2, 4, 0]], undefined);
  const pending = viewer.resolveAim!(block, { origin: [0.5, 64.5, 0], direction: [0, 0, 1], max: 50 });
  expect(pending).toMatchObject({ z: 2, face: 2 });
  act(() => viewer.aimClick!(pending, { shift: false, ctrl: false, button: 2 }));
  expect(edit).toHaveBeenCalledTimes(1); // waits for this new plan's id
  await act(async () => { saved({ buildId: 'new', rev: 7 }); await Promise.resolve(); });
  expect(edit).toHaveBeenLastCalledWith([[0, 64, 1, 4, 0]], 'new');
  expect(props.onSelect).not.toHaveBeenCalled();
  expect(props.onTool).not.toHaveBeenCalled();
  fireEvent.click(screen.getByRole('button', { name: 'Inspect' }));
  act(() => viewer.aimClick!({ ...block, id: 54 }, { shift: false, ctrl: false, button: 2 }));
  expect(props.onSelect).not.toHaveBeenCalled();
  act(() => viewer.aimClick!({ ...block, id: 54 }, { shift: false, ctrl: false, button: 0 }));
  expect(props.onSelect).toHaveBeenCalledOnce();
});

it('does not attach edits or additions to transient preserve repair items', () => {
  const props = mapProps();
  const edit = vi.fn().mockResolvedValue({ buildId: 'new', rev: 2 });
  props.state = {
    id: 'colony', version: '1.12.2', rev: 1,
    builds: [{ id: 'repair', kind: 'preserve', label: 'Preserve repair' }],
    ghosts: { rev: 1, blocks: [[0, 64, 3, 4, 0, 'repair', 'pending']] },
  } as unknown as StoryMapProps['state'];
  render(<StoryMap {...props} view="fly" canEdit onEditPlan={edit} />);
  fireEvent.click(screen.getByRole('button', { name: 'Freecam tools' }));
  fireEvent.click(screen.getByRole('button', { name: 'Edit plan' }));
  expect(screen.queryByRole('option', { name: /Preserve repair/ })).not.toBeInTheDocument();
  viewer.locked = true;
  act(() => viewer.aimClick!({ kind: 'block', x: 0, y: 64, z: 3, id: 4, meta: 0, face: 2, name: 'Cobble', distance: 3, plan: 'repair' }, { shift: false, ctrl: false, button: 0 }));
  expect(edit).toHaveBeenCalledExactlyOnceWith([[0, 64, 3, 0, 0]], undefined);
});

it('hides freecam tools while looking around and opens blocks with B while editing', () => {
  const props = mapProps();
  props.state = { id: 'colony', version: 'b1.8.1', rev: 1, builds: [] } as unknown as StoryMapProps['state'];
  render(<StoryMap {...props} view="fly" canEdit onEditPlan={vi.fn()} />);
  fireEvent.click(screen.getByRole('button', { name: 'Freecam tools' }));
  fireEvent.click(screen.getByRole('button', { name: 'Edit plan' }));
  Object.defineProperty(document, 'pointerLockElement', { configurable: true, value: screen.getByTestId('fake-viewer') });
  viewer.locked = true;
  fireEvent(document, new Event('pointerlockchange'));
  expect(screen.queryByTestId('fly-editor')).not.toBeInTheDocument();
  expect(screen.queryByRole('button', { name: 'Freecam tools' })).not.toBeInTheDocument();
  const key = new KeyboardEvent('keydown', { code: 'KeyB', key: 'b', cancelable: true, bubbles: true });
  fireEvent(window, key);
  expect(key.defaultPrevented).toBe(true);
  expect(screen.getByRole('dialog', { name: 'Block palette' })).toBeInTheDocument();
  Object.defineProperty(document, 'pointerLockElement', { configurable: true, value: null });
  viewer.locked = false;
  fireEvent(document, new Event('pointerlockchange'));
  fireEvent.click(screen.getByRole('button', { name: 'Close the palette' }));
  expect(screen.getByRole('button', { name: 'Freecam tools' })).toBeInTheDocument();
  expect(screen.queryByTestId('fly-editor')).not.toBeInTheDocument();
});

it.each(['cancel', 'tool', 'view', 'level', 'unchanged'] as const)('a deferred release pick respects %s before issuing an angled build', async (change) => {
  const now = vi.spyOn(performance, 'now').mockReturnValue(0); // no unrelated throttled hover pick
  const camera = new PerspectiveCamera(60, 800 / 600, 0.1, 500);
  camera.position.set(0.5, 90, 30.5); camera.lookAt(0.5, 64, 0.5); camera.updateMatrixWorld();
  let resolve!: (hit: { x: number; y: number; z: number; id: number; meta: number; face: number }) => void;
  const pick = vi.fn(() => new Promise<{ x: number; y: number; z: number; id: number; meta: number; face: number }>((done) => { resolve = done; }));
  viewer.engine = {
    camera, pickCut: () => null, lock: viewer.lock,
    surface: { focus: viewer.focus },
    rig: { target: { x: 0, y: 64, z: 0 }, isoZoom: 44, flyPos: { set: vi.fn() } },
    runtime: { host: { pick, scan: () => Promise.resolve([]) }, store: { entities: () => [], bots: () => [] } },
  };
  const ref = createRef<StoryMapHandle>(), props = { ...mapProps(), tool: { kind: 'build', id: 'block' } as const };
  const rendered = render(<StoryMap ref={ref} {...props} view="angled" canEdit />);
  const canvas = screen.getByLabelText<HTMLCanvasElement>(/Colony map/);
  Object.defineProperties(canvas, { clientWidth: { value: 800 }, clientHeight: { value: 600 }, setPointerCapture: { value: vi.fn() } });
  vi.spyOn(canvas, 'getContext').mockReturnValue(null);
  viewer.afterFrame?.(viewer.engine as Parameters<NonNullable<WorldViewerProps['onAfterFrame']>>[0], 0, 0);
  fireEvent.pointerDown(canvas, { pointerId: 1, clientX: 400, clientY: 300, button: 0, buttons: 1 });
  fireEvent.pointerUp(canvas, { pointerId: 1, clientX: 450, clientY: 300, button: 0, buttons: 0 });
  expect(pick).toHaveBeenCalledOnce(); expect(props.onTool).not.toHaveBeenCalled();
  if (change === 'cancel') act(() => { expect(ref.current?.cancelDrag()).toBe(true); });
  if (change === 'tool') rendered.rerender(<StoryMap ref={ref} {...props} tool={{ kind: 'select' }} view="angled" canEdit />);
  if (change === 'view') rendered.rerender(<StoryMap ref={ref} {...props} view="top" canEdit />);
  if (change === 'level') rendered.rerender(<StoryMap ref={ref} {...props} view="angled" level={64} canEdit />);
  await act(async () => { resolve({ x: 9, y: 70, z: 11, id: 1, meta: 0, face: 1 }); await Promise.resolve(); });
  if (change === 'unchanged') expect(props.onTool).toHaveBeenCalledExactlyOnceWith(expect.objectContaining({ b: [9, 71, 11], moved: true }), false);
  else expect(props.onTool).not.toHaveBeenCalled();
  now.mockRestore();
});
