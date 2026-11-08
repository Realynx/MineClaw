import { fireEvent, render, screen } from '@testing-library/react';
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
}));
vi.mock('@/features/story/chestBadges', () => ({ useContainerBadges: () => ({ current: new Map() }) }));
vi.mock('@/features/world/integration', () => ({
  useWorldHost: () => ({ ready: true, source: viewer.source, mod: { WorldViewer: MockViewer } }),
}));
const MockViewer = forwardRef<WorldViewerHandle, WorldViewerProps>(function MockViewer(props, ref) {
  viewer.mode = props.cameraMode ?? '';
  viewer.aimClick = props.onAimClick;
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
  viewer.engine = {
    surface: { focus: viewer.focus },
    rig: { target, isoZoom: 44, flyPos: { set: position } },
    setBotLabels: vi.fn(),
    setLevelCut: vi.fn(),
    setHolograms: vi.fn(),
  };
  const props = mapProps();
  const { rerender } = render(<StoryMap {...props} view="fly" home={null} />);
  vi.spyOn(screen.getByLabelText<HTMLCanvasElement>(/Colony map/), 'getContext').mockReturnValue(null);
  frame(); // engine exists, but the asynchronous colony position has not arrived
  expect(position).toHaveBeenLastCalledWith(0, 12, 16);
  rerender(<StoryMap {...props} view="fly" home={[100, 63, 200]} />);
  frame();
  expect(position).toHaveBeenLastCalledWith(100.5, 76, 216.5);
  expect(viewer.mode).toBe('fly');
  expect(viewer.mounts).toBe(1);
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
