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
afterEach(() => {
  vi.unstubAllGlobals();
  vi.unstubAllEnvs();
  vi.restoreAllMocks();
  Reflect.deleteProperty(document, 'fullscreenElement');
  Reflect.deleteProperty(navigator, 'keyboard');
  Reflect.deleteProperty(window, '__story');
});

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

type FrameEngine = Parameters<NonNullable<WorldViewerProps['onBeforeFrame']>>[0];

/** A viewer engine as the map's frame callbacks use it. Its rig starts where the real one does: the world origin, at
 * zoom 48. */
function fakeEngine(bots: unknown[] = []) {
  const target = {
    x: 0,
    y: 64,
    z: 0,
    set(x: number, y: number, z: number) {
      this.x = x;
      this.y = y;
      this.z = z;
    },
  };
  const matrix = { elements: new Array<number>(16).fill(0) };
  const engine = {
    surface: { focus: viewer.focus },
    lock: viewer.lock,
    rig: { target, isoZoom: 48, flyPos: { set: vi.fn() }, transitioning: false, beginTransition: vi.fn() },
    camera: {
      matrixWorldInverse: matrix,
      projectionMatrix: matrix,
      matrixWorld: matrix,
      projectionMatrixInverse: matrix,
    },
    setBotLabels: vi.fn(),
    setBotBadges: vi.fn(),
    setLevelCut: vi.fn(),
    setHolograms: vi.fn(),
    runtime: { store: { bots: () => bots, entities: () => [] }, host: { scan: () => Promise.resolve([]) } },
  };
  viewer.engine = engine;
  return engine;
}

/** The viewer's call before it poses the camera. False = the map holds the frame: the viewer draws nothing. */
function before(now = 16): boolean {
  let drawn = true;
  act(() => {
    drawn = viewer.beforeFrame!(viewer.engine as FrameEngine, now, 0) !== false;
  });
  return drawn;
}

/** One viewer frame: the after-frame callback runs only when the frame is drawn. True when it was. */
function frame(now = 16): boolean {
  if (!before(now)) return false;
  act(() => viewer.afterFrame!(viewer.engine as FrameEngine, now, 0));
  return true;
}

/** The map mounted with a 2D overlay that draws nothing (the frame callbacks are what is under test). */
function mount(props: StoryMapProps, ref?: React.Ref<StoryMapHandle>) {
  const page = render(<StoryMap ref={ref} {...props} />);
  vi.spyOn(screen.getByLabelText<HTMLCanvasElement>(/Colony map/), 'getContext').mockReturnValue(null);
  return {
    ...page,
    show: (next: Partial<StoryMapProps>) => page.rerender(<StoryMap ref={ref} {...props} {...next} />),
  };
}

it('holds every frame until the first home position arrives, then opens free cam on the colony', () => {
  const engine = fakeEngine();
  const props = mapProps();
  props.state = {
    pawns: [{ name: 'maid_chan_109', display: 'Nickname', job: { type: 'mine', label: 'Mining quarry' } }],
  } as unknown as StoryMapProps['state'];
  const { show } = mount({ ...props, view: 'fly', home: null });
  const aimed = engine.rig.flyPos.set.mock.calls.length; // choosing free cam aims it once, from the rig as it is
  expect(before()).toBe(false); // the engine exists, but the asynchronous colony position has not arrived
  expect(engine.rig.target).toMatchObject({ x: 0, y: 64, z: 0 });
  expect(engine.rig.flyPos.set.mock.calls).toHaveLength(aimed);
  expect(engine.setBotBadges).not.toHaveBeenCalled();
  expect(engine.setBotLabels).not.toHaveBeenCalled();
  show({ view: 'fly', home: [100, 63, 200] });
  expect(before()).toBe(true);
  expect(engine.rig.target).toMatchObject({ x: 100.5, y: 64, z: 200.5 });
  expect(engine.rig.isoZoom).toBe(44);
  expect(engine.rig.flyPos.set).toHaveBeenLastCalledWith(100.5, 76, 216.5);
  expect(engine.setBotBadges).toHaveBeenLastCalledWith([
    expect.objectContaining({ text: '109', activity: 'Mining quarry' }),
  ]);
  expect(engine.setBotLabels).toHaveBeenLastCalledWith(false);
  expect(viewer.mode).toBe('fly');
  expect(viewer.mounts).toBe(1);
  show({ view: 'top', home: [100, 63, 200] });
  before();
  expect(engine.setBotBadges).toHaveBeenLastCalledWith([]);
  expect(engine.setBotLabels).toHaveBeenLastCalledWith(false);
});

it('places the camera on the colony in its very first frame when the home is known at mount', () => {
  const engine = fakeEngine();
  mount({ ...mapProps(), home: [100, 63, 200] });
  expect(before()).toBe(true);
  expect(engine.rig.target).toMatchObject({ x: 100.5, y: 64, z: 200.5 });
  expect(engine.rig.isoZoom).toBe(44);
  expect(engine.rig.beginTransition).not.toHaveBeenCalled(); // placed, not glided to: nothing was drawn before
  expect(engine.rig.flyPos.set).not.toHaveBeenCalled();
});

it('gives a new engine object the picture the map had, where it was panned and zoomed to', () => {
  const first = fakeEngine();
  mount({ ...mapProps(), home: [100, 63, 200] });
  expect(frame()).toBe(true);
  first.rig.target.x = 140.25;
  first.rig.isoZoom = 60;
  expect(frame(32)).toBe(true);
  const next = fakeEngine();
  expect(before(48)).toBe(true);
  expect(next.rig.target).toMatchObject({ x: 140.25, y: 64, z: 200.5 });
  expect(next.rig.isoZoom).toBe(60);
  expect(next.setBotLabels).toHaveBeenLastCalledWith(false); // and its per-engine setup
});

it('ends a glide begun before the first drawn frame: that frame is at the spot, not on its way from the origin', () => {
  const engine = fakeEngine();
  engine.rig.transitioning = true; // the view was switched while the map waited: the rig glides from its default camera
  mount({ ...mapProps(), home: [100, 63, 200] });
  before(500);
  // A glide whose time has run out: the rig's next pose is the wanted one.
  expect(engine.rig.beginTransition).toHaveBeenCalledExactlyOnceWith(engine.camera, 499, 1);
});

it('with no colony, waits six seconds for the main bot or the player, then lets the map show where the rig is', () => {
  const now = vi.spyOn(performance, 'now').mockReturnValue(1_000);
  const engine = fakeEngine();
  const onSettled = vi.fn();
  const ref = createRef<StoryMapHandle>();
  mount({ ...mapProps(), home: null, settle: true, onSettled }, ref);
  expect(before()).toBe(false);
  now.mockReturnValue(6_999);
  expect(before()).toBe(false);
  expect(ref.current?.center()).toBeNull();
  now.mockReturnValue(7_000);
  expect(before()).toBe(true);
  expect(engine.rig.target).toMatchObject({ x: 0, y: 64, z: 0 });
  expect(engine.rig.isoZoom).toBe(44);
  expect(onSettled).not.toHaveBeenCalled();
  expect(ref.current?.center()).toEqual([0, 64, 0]);
  now.mockRestore();
});

it('with no colony, settles over the main bot as soon as the viewer knows it', () => {
  const bots: unknown[] = [];
  const engine = fakeEngine(bots);
  const onSettled = vi.fn();
  mount({ ...mapProps(), home: null, settle: true, onSettled });
  expect(before()).toBe(false);
  bots.push({ dim: 0, dto: { main: true }, x: 40.7, y: 70, z: -12.3 });
  expect(before()).toBe(true);
  expect(engine.rig.target).toMatchObject({ x: 40.5, z: -12.5 });
  expect(engine.rig.isoZoom).toBe(44);
  expect(onSettled).toHaveBeenCalledExactlyOnceWith(69); // the floor under the bot: the working level starts there
});

it('glides to the new colony when the colony changes, and stays put when a home moves inside one colony', () => {
  const engine = fakeEngine();
  const { show } = mount({ ...mapProps(), colony: 'story-1', home: [100, 63, 200] });
  frame();
  show({ colony: 'story-1', home: [300, 63, 400] }); // "move home": the same colony
  frame(32);
  expect(engine.rig.target).toMatchObject({ x: 100.5, z: 200.5 });
  show({ colony: 'story-2', home: null, loading: true }); // switched: the new colony's state is still on its way
  expect(frame(48)).toBe(true); // drawn where it was: never held again, and no loading line over the picture
  expect(screen.queryByRole('status')).not.toBeInTheDocument();
  expect(engine.rig.target).toMatchObject({ x: 100.5, z: 200.5 });
  expect(engine.rig.beginTransition).not.toHaveBeenCalled();
  show({ colony: 'story-2', home: [-50, 70, 60] });
  frame(64);
  expect(engine.rig.beginTransition).toHaveBeenCalledExactlyOnceWith(engine.camera, 64, 500);
  expect(engine.rig.target).toMatchObject({ x: -49.5, y: 64, z: 60.5 });
  frame(80);
  expect(engine.rig.beginTransition).toHaveBeenCalledOnce();
});

it('has no centre before the map is placed', () => {
  fakeEngine();
  const ref = createRef<StoryMapHandle>();
  const { show } = mount({ ...mapProps(), home: null }, ref);
  expect(before()).toBe(false);
  expect(ref.current?.center()).toBeNull(); // "move home here" and a new colony never take the world origin
  show({ home: [100, 63, 200] });
  before();
  expect(ref.current?.center()).toEqual([100, 64, 200]);
});

it('opens on the view remembered for the colony before its home arrives, and the home does not override it', () => {
  const engine = fakeEngine();
  const initialView = { colony: 'story-1', x: 216.5, z: 400.5, zoom: 60 };
  const { show } = mount({ ...mapProps(), colony: 'story-1', home: null, loading: true, initialView });
  expect(screen.queryByText('Loading the colony…')).not.toBeInTheDocument(); // it knows where to look already
  expect(before()).toBe(true);
  expect(engine.rig.target).toMatchObject({ x: 216.5, y: 64, z: 400.5 });
  expect(engine.rig.isoZoom).toBe(60);
  show({ colony: 'story-1', home: [100, 63, 200], loading: false, initialView });
  frame(32);
  expect(engine.rig.target).toMatchObject({ x: 216.5, z: 400.5 });
  expect(engine.rig.isoZoom).toBe(60);
});

it('ignores a view remembered for another colony and opens on the home', () => {
  const engine = fakeEngine();
  const initialView = { colony: 'story-9', x: 216.5, z: 400.5, zoom: 60 };
  const { show } = mount({ ...mapProps(), colony: 'story-1', home: null, loading: true, initialView });
  expect(screen.getByRole('status')).toHaveTextContent('Loading the colony…');
  expect(before()).toBe(false);
  show({ colony: 'story-1', home: [100, 63, 200], loading: false, initialView });
  before();
  expect(engine.rig.target).toMatchObject({ x: 100.5, y: 64, z: 200.5 });
  expect(engine.rig.isoZoom).toBe(44);
});

it('says the colony is loading when the remembered colony turns out to be gone before anything was drawn', () => {
  fakeEngine();
  const initialView = { colony: 'story-9', x: 216.5, z: 400.5, zoom: 60 };
  const { show } = mount({ ...mapProps(), colony: 'story-9', home: null, loading: true, initialView });
  expect(screen.queryByRole('status')).not.toBeInTheDocument();
  show({ colony: 'story-1', home: null, loading: true, initialView: null }); // forgotten: the list's choice is loading
  expect(screen.getByRole('status')).toHaveTextContent('Loading the colony…');
  expect(before()).toBe(false);
});

it('says the colony is loading until the map knows where to look, and never once it does', () => {
  fakeEngine();
  const { show } = mount({ ...mapProps(), home: null, loading: true });
  expect(screen.getByRole('status')).toHaveTextContent('Loading the colony…');
  expect(before()).toBe(false);
  expect(screen.getByRole('status')).toHaveTextContent('Loading the colony…');
  show({ home: [100, 63, 200], loading: true }); // the home is here; the frame that places the camera ends the wait
  expect(before()).toBe(true);
  expect(screen.queryByRole('status')).not.toBeInTheDocument();
});

it('shows no loading line when the home is known at mount, nor while a colony-less map settles', () => {
  fakeEngine();
  const known = mount({ ...mapProps(), home: [100, 63, 200], loading: true });
  expect(screen.queryByRole('status')).not.toBeInTheDocument();
  known.unmount();
  mount({ ...mapProps(), home: null, settle: true });
  expect(before()).toBe(false); // waiting for the main bot: the found-a-colony card is the page's word on it
  expect(screen.queryByRole('status')).not.toBeInTheDocument();
});

it('takes a focus before anything was drawn as the place to open on', () => {
  const engine = fakeEngine();
  const ref = createRef<StoryMapHandle>();
  mount({ ...mapProps(), home: null, loading: true }, ref);
  expect(before()).toBe(false);
  act(() => ref.current!.focus(40, -20));
  expect(screen.queryByRole('status')).not.toBeInTheDocument();
  expect(engine.rig.target).toMatchObject({ x: 0, y: 64, z: 0 }); // the camera moves in its frame, not before
  expect(before()).toBe(true);
  expect(engine.rig.target).toMatchObject({ x: 40.5, y: 64, z: -19.5 });
  expect(engine.rig.beginTransition).not.toHaveBeenCalled(); // nothing to glide from
  act(() => ref.current!.focus(60, 10, 70));
  expect(engine.rig.beginTransition).toHaveBeenCalledOnce(); // once drawn, a focus glides as before
  expect(engine.rig.target).toMatchObject({ x: 60.5, y: 70, z: 10.5 });
});

it('reports the view for the page to remember: at most every two seconds while it changes, and once on leaving', () => {
  const engine = fakeEngine();
  const onView = vi.fn();
  const { unmount } = mount({ ...mapProps(), home: [100, 63, 200], onView });
  frame(3_000);
  expect(onView).toHaveBeenCalledExactlyOnceWith({ x: 100.5, z: 200.5, zoom: 44 });
  frame(6_000); // unchanged: nothing to say
  expect(onView).toHaveBeenCalledOnce();
  engine.rig.target.x = 120.5;
  frame(6_016);
  expect(onView).toHaveBeenLastCalledWith({ x: 120.5, z: 200.5, zoom: 44 });
  engine.rig.target.z = 180.5;
  engine.rig.isoZoom = 80;
  frame(7_000); // changed again, but the last report is only a second old
  expect(onView).toHaveBeenCalledTimes(2);
  unmount();
  expect(onView).toHaveBeenCalledTimes(3);
  expect(onView).toHaveBeenLastCalledWith({ x: 120.5, z: 180.5, zoom: 80 });
});

it('reports no view from a map that never found its place, nor while still on the colony it was switched from', () => {
  const engine = fakeEngine();
  const onView = vi.fn();
  const unplaced = mount({ ...mapProps(), home: null, onView });
  expect(before(3_000)).toBe(false);
  unplaced.unmount();
  expect(onView).not.toHaveBeenCalled();
  const switched = mount({ ...mapProps(), colony: 'story-1', home: [100, 63, 200], onView });
  frame(3_000);
  expect(onView).toHaveBeenCalledOnce();
  switched.show({ colony: 'story-2', home: null });
  engine.rig.target.x = 120.5; // panned while the new colony loads: still the old colony's ground
  frame(6_000);
  switched.unmount();
  expect(onView).toHaveBeenCalledOnce();
});

it('after a switch to a colony with no home, glides over its main bot and remembers the view again', () => {
  const bots: unknown[] = [];
  const engine = fakeEngine(bots);
  const onView = vi.fn();
  const { show, unmount } = mount({ ...mapProps(), colony: 'story-1', home: [100, 63, 200], onView });
  frame(3_000);
  expect(onView).toHaveBeenCalledOnce();
  show({ colony: 'story-2', home: null, loading: true });
  frame(3_016); // its state is on its way: stays put
  expect(engine.rig.beginTransition).not.toHaveBeenCalled();
  bots.push({ dim: 0, dto: { main: true }, x: 40.7, y: 70, z: -12.3 });
  show({ colony: 'story-2', home: null, settle: true }); // here, with no home
  frame(6_000);
  expect(engine.rig.beginTransition).toHaveBeenCalledExactlyOnceWith(engine.camera, 6_000, 500);
  expect(engine.rig.target).toMatchObject({ x: 40.5, z: -12.5 });
  expect(onView).toHaveBeenLastCalledWith({ x: 40.5, z: -12.5, zoom: 44 });
  engine.rig.target.x = 60.5;
  frame(6_016);
  unmount();
  expect(onView).toHaveBeenLastCalledWith({ x: 60.5, z: -12.5, zoom: 44 });
});

it('after a switch to a colony with no home and no body to settle on, stays put and remembers the view again', () => {
  const engine = fakeEngine();
  const onView = vi.fn();
  const { show } = mount({ ...mapProps(), colony: 'story-1', home: [100, 63, 200], onView });
  frame(3_000);
  show({ colony: 'story-2', home: null, settle: true });
  engine.rig.target.x = 120.5;
  frame(6_000);
  expect(engine.rig.beginTransition).not.toHaveBeenCalled();
  expect(engine.rig.target).toMatchObject({ x: 120.5, z: 200.5 });
  expect(onView).toHaveBeenLastCalledWith({ x: 120.5, z: 200.5, zoom: 44 });
});

it('tells the mock debug hook which column the first drawn frame looked at', () => {
  vi.stubEnv('NEXT_PUBLIC_MOCK', '1');
  fakeEngine();
  const { show } = mount({ ...mapProps(), home: null });
  const story = () => (window as unknown as { __story: { firstFrame(): [number, number] | null } }).__story;
  expect(frame()).toBe(false);
  expect(story().firstFrame()).toBeNull();
  show({ home: [400, 63, 400] });
  expect(frame()).toBe(true);
  expect(story().firstFrame()).toEqual([400, 400]);
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
