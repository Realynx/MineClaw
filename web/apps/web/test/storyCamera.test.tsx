import { act, fireEvent, render, screen } from '@testing-library/react';
import { forwardRef, useEffect, useImperativeHandle, useState } from 'react';
import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import type { WorldViewerHandle, WorldViewerProps } from '@mineclaw/world-viewer';
import { ControlCluster } from '@/features/story/StoryHud';
import { StoryMap, type StoryMapProps } from '@/features/story/StoryMap';
import { useStoryKeys } from '@/features/story/mode/keys';
import type { Story } from '@/features/story/mode/commands';

const viewer = vi.hoisted(() => ({
  mounts: 0,
  mode: '',
  focus: vi.fn(),
  engine: {},
  source: {},
  locked: false,
  aimClick: undefined as WorldViewerProps['onAimClick'],
  resolveAim: undefined as WorldViewerProps['resolveAim'],
}));
vi.mock('@/features/story/chestBadges', () => ({ useContainerBadges: () => ({ current: new Map() }) }));
vi.mock('@/features/world/integration', () => ({
  useWorldHost: () => ({ ready: true, source: viewer.source, mod: { WorldViewer: MockViewer } }),
}));
const MockViewer = forwardRef<WorldViewerHandle, WorldViewerProps>(function MockViewer(props, ref) {
  viewer.mode = props.cameraMode ?? '';
  viewer.aimClick = props.onAimClick;
  viewer.resolveAim = props.resolveAim;
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
  viewer.engine = {
    surface: { focus: viewer.focus },
    rig: { target: { x: 8, y: 64, z: 12 }, isoZoom: 44, flyPos: { set: vi.fn() } },
  };
  vi.stubGlobal('requestAnimationFrame', () => 1);
  vi.stubGlobal('cancelAnimationFrame', () => undefined);
});
afterEach(() => vi.unstubAllGlobals());

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

it('switches the same mounted viewer to free cam and back, giving mouse control to the viewer only while flying', () => {
  const props = mapProps();
  const { rerender } = render(<StoryMap {...props} />);
  const canvas = screen.getByLabelText<HTMLCanvasElement>(/Colony map/);
  expect(viewer.mode).toBe('top');
  expect(props.keys.current.size).toBe(0);
  rerender(<StoryMap {...props} view="fly" />);
  expect(viewer.mode).toBe('fly');
  expect(canvas.style.pointerEvents).toBe('none');
  expect(canvas.tabIndex).toBe(-1);
  expect(viewer.focus).toHaveBeenCalledOnce();
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
  const matrix = { elements: new Array<number>(16).fill(0) };
  viewer.engine = {
    surface: { focus: viewer.focus },
    rig: { target, isoZoom: 44, flyPos: { set: position } },
    camera: { matrixWorldInverse: matrix, projectionMatrix: matrix, matrixWorld: matrix, projectionMatrixInverse: matrix },
    setBotLabels: vi.fn(),
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
  rerender(<StoryMap {...props} view="fly" home={[100, 63, 200]} />);
  frame();
  expect(position).toHaveBeenLastCalledWith(100.5, 76, 216.5);
  expect(viewer.mode).toBe('fly');
  expect(viewer.mounts).toBe(1);
  rerender(<StoryMap {...props} view="top" />);
  frame();
  expect(setBotBadges).toHaveBeenLastCalledWith([]);
});

it('has an always-available free camera button, and flight keys cannot pause the colony or rotate a held tool', () => {
  const pause = vi.fn(),
    build = vi.fn();
  function Controls() {
    const [view, setView] = useState<'top' | 'angled' | 'fly'>('top');
    const c = {
      view,
      setView,
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
  fireEvent.click(screen.getByRole('button', { name: 'Edit plan' }));
  expect(screen.queryByRole('option', { name: /Preserve repair/ })).not.toBeInTheDocument();
  viewer.locked = true;
  act(() => viewer.aimClick!({ kind: 'block', x: 0, y: 64, z: 3, id: 4, meta: 0, face: 2, name: 'Cobble', distance: 3, plan: 'repair' }, { shift: false, ctrl: false, button: 0 }));
  expect(edit).toHaveBeenCalledExactlyOnceWith([[0, 64, 3, 0, 0]], undefined);
});
