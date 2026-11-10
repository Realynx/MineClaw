import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, expect, it, vi } from 'vitest';
import { blockName } from '@mineclaw/world-viewer';
import { setViewerQuality } from '@mineclaw/world-viewer/settings';
import { PawnControlView } from '@/features/control/PawnControlView';

const cameraStats = vi.hoisted(() => ({
  latencyMs: 141.6, offset: 0.034, offsetP95: 0.081, snaps: 1, holds: 0, duplicates: 2, terrainMisses: 0, samples: 41, ticked: 40,
}));
const f = vi.hoisted(() => ({
  engine: null as null | { surface: HTMLCanvasElement },
  bind: vi.fn(() => vi.fn()), capture: vi.fn(() => Promise.resolve(null)),
  handoff: null as null | { mount: ReturnType<typeof vi.fn>; transfer: ReturnType<typeof vi.fn>; release: ReturnType<typeof vi.fn> },
  viewer: null as null | { onBeforeFrame?: (engine: unknown, now: number) => void; layers?: unknown;
    resolveAim?: (physical: unknown, ray: { origin: number[]; direction: number[]; max: number }) => unknown },
  audio: null as null | { play?: { current: ((events: unknown[]) => void) | null }; heard?: { current: ((events: unknown[]) => unknown[]) | null } },
  events: null as null | { onBroke?: (broke: { count: number; x: number; y: number; z: number; id: number; meta: number }) => void },
  control: {
    status: 'active', state: { dim: 0, hp: 20, food: 20 }, inventory: null as null | { windowId: number; hotbarStart: number; slots: unknown[] }, inventoryAt: 0, error: '',
    endReason: null as null | string,
    input: { current: { forward: 0, strafe: 0, yaw: 0, pitch: 0, jump: false, sneak: false, sprint: false, hotbar: 0 } },
    pose: { current: null as null | Record<string, unknown> }, action: vi.fn(), end: vi.fn(), retry: vi.fn(),
    camera: { current: { position: vi.fn(() => ({ x: 10.5, y: 64, z: -3.5, dim: 0 })), stats: vi.fn(() => cameraStats), onGround: vi.fn(() => true) } },
    reach: { current: undefined as number | undefined },
    aimInput: { current: { seq: 0, pending: false } },
  },
}));
vi.mock('next/navigation', () => ({ useRouter: () => ({ push: vi.fn() }) }));
vi.mock('@/features/world/integration', async () => {
  const React = await import('react');
  const mod = { WorldViewer: React.forwardRef((props, ref) => {
    f.viewer = props;
    React.useImperativeHandle(ref, () => ({ engine: () => f.engine }));
    return null;
  }), itemName: () => undefined };
  const source = {};
  return { useWorldHost: () => ({ mod, source }) };
});
vi.mock('@/features/world/InWorld', () => ({ Crosshair: () => null }));
vi.mock('@/features/live/inventory/InventoryPanel', () => ({ useBodyInventory: () => ({ dataUpdatedAt: 0 }), useItemIcons: () => ({}) }));
vi.mock('@/features/story/StoryAudio', () => ({ StoryAudio: (props: NonNullable<typeof f.audio>) => { f.audio = props; return null; } }));
vi.mock('@/features/story/controllerInput', () => ({ bindControllerKeyboard: () => () => {}, requestControllerCapture: f.capture }));
vi.mock('@/features/control/usePawnControl', async (original) => ({
  ...await original<object>(),
  usePawnControl: (_body: string, events: NonNullable<typeof f.events>) => { f.events = events; return f.control; },
}));
vi.mock('@/features/control/playerInput', async (original) => ({ ...await original<object>(), bindPlayerInput: f.bind }));
vi.mock('@/features/control/captureHandoff', () => ({ takeoverCapture: () => f.handoff }));
vi.mock('@/features/control/PlayerInventory', () => ({ PlayerInventory: () => null, PlayerSlot: () => null }));

afterEach(() => {
  cleanup(); vi.useRealTimers(); vi.clearAllMocks(); f.engine = null; f.handoff = null; f.viewer = null; f.audio = null; f.events = null;
  Object.assign(f.control.input.current, { pitch: 0, sneak: false, hotbar: 0 }); f.control.reach.current = undefined;
  Object.assign(f.control, { status: 'active', state: { dim: 0, hp: 20, food: 20 }, inventory: null, inventoryAt: 0, error: '', endReason: null });
  f.control.pose.current = null; f.control.aimInput.current = { seq: 0, pending: false };
  f.control.camera.current.position.mockImplementation(() => ({ x: 10.5, y: 64, z: -3.5, dim: 0 }));
});

/** A view engine with what the takeover's frame uses; `changed` plays the world's block change feed. */
function viewEngine() {
  const blockListeners = new Set<(dim: number, cells: Array<[number, number, number]>) => void>();
  return {
    surface: document.createElement('canvas'),
    rig: { flyPos: { set: vi.fn() }, flyYaw: 0, flyPitch: 0 }, runtime: { version: '1.12.2' as string | null, blockListeners },
    playerCollision: vi.fn(), setPlayerReach: vi.fn(), setBreak: vi.fn(), blockBurst: vi.fn(), overrideCollision: vi.fn(), setHologramPreview: vi.fn(),
    blockAt: vi.fn((): { id: number; meta: number } | null => ({ id: 1, meta: 0 })), aim: vi.fn((): unknown => null),
    changed: (cells: Array<[number, number, number]>) => { for (const l of blockListeners) l(0, cells); },
  };
}

it('does not allow pointer capture until the actual canvas has its input listeners', async () => {
  vi.useFakeTimers();
  render(<PawnControlView body="maid_chan_70" returnTo="/story" />);
  const play = screen.getByRole('button', { name: 'Click to play' });
  expect(play).toBeDisabled();
  fireEvent.click(play);
  expect(f.capture).not.toHaveBeenCalled();
  act(() => { vi.advanceTimersByTime(30); });
  expect(play).toBeDisabled();
  f.engine = { surface: document.createElement('canvas') };
  act(() => { vi.advanceTimersByTime(30); });
  expect(f.bind).toHaveBeenCalledOnce();
  expect(f.bind).toHaveBeenCalledWith(f.engine.surface, expect.any(Object));
  expect(play).toBeEnabled();
  await act(async () => { fireEvent.click(play); await Promise.resolve(); });
  expect(f.capture).toHaveBeenCalledWith(f.engine, screen.getByTestId('pawn-control'), expect.any(Function));
});

it('transfers the original click only after listeners are bound to the actual canvas', async () => {
  vi.useFakeTimers();
  const order: string[] = [];
  f.handoff = { mount: vi.fn(() => () => {}), transfer: vi.fn(() => { order.push('transfer'); return Promise.resolve(true); }), release: vi.fn() };
  f.bind.mockImplementationOnce(() => { order.push('bind'); return vi.fn(); });
  render(<PawnControlView body="maid_chan_70" returnTo="/story" />);
  expect(f.handoff.transfer).not.toHaveBeenCalled();
  f.engine = { surface: document.createElement('canvas') };
  await act(async () => { vi.advanceTimersByTime(30); await Promise.resolve(); });
  expect(order).toEqual(['bind', 'transfer']);
  expect(f.handoff.transfer).toHaveBeenCalledWith(f.engine.surface, expect.any(Function));
  expect(screen.queryByRole('button', { name: 'Click to play' })).not.toBeInTheDocument();
  expect(f.capture).not.toHaveBeenCalled();
});

it('puts the eye where the world\'s game version puts it and gives the viewer the reach of the session', () => {
  render(<PawnControlView body="maid_chan_70" returnTo="/story" />);
  const engine = viewEngine();
  const eyeAbove = (version: string | null, sneak: boolean) => {
    engine.runtime.version = version; f.control.input.current.sneak = sneak;
    f.viewer!.onBeforeFrame!(engine, 16);
    const [x, y, z] = engine.rig.flyPos.set.mock.lastCall as number[];
    expect([x, z]).toEqual([10.5, -3.5]);
    return y! - 64;
  };
  expect(eyeAbove('1.12.2', false)).toBeCloseTo(1.62, 9);
  expect(eyeAbove('1.12.2', true)).toBeCloseTo(1.54, 9);
  // Beta has no lower sneaking eye, and its server aims from 1.62 whether the pawn sneaks or not.
  expect(eyeAbove('b1.8.1', false)).toBeCloseTo(1.62, 9);
  expect(eyeAbove('b1.8.1', true)).toBeCloseTo(1.62, 9);
  // A viewer that has not set itself up yet counts as 1.12.2, as it does for the audio listener.
  expect(eyeAbove(null, true)).toBeCloseTo(1.54, 9);
  // No reach from an older server: the viewer keeps its own default.
  expect(engine.setPlayerReach).toHaveBeenLastCalledWith(undefined);
  f.control.reach.current = 4.5;
  f.control.input.current.pitch = 90;
  f.viewer!.onBeforeFrame!(engine, 32);
  expect(engine.setPlayerReach).toHaveBeenLastCalledWith(4.5);
  expect(engine.rig.flyPitch).toBe(90); // the wire pitch as it is; the rig keeps a heading when it draws
});

it('shows the stats line of the camera only while the frame rate overlay is on', () => {
  vi.useFakeTimers();
  render(<PawnControlView body="maid_chan_70" returnTo="/story" />);
  expect(screen.queryByTestId('control-stats')).not.toBeInTheDocument();
  try {
    act(() => setViewerQuality({ perfOverlay: true }));
    expect(screen.getByTestId('control-stats')).toHaveTextContent(
      'control 142 ms · offset 0.03 (p95 0.08) · snaps 1 · holds 0 · repeated 2 · no terrain 0 · by tick 40/41');
    f.control.camera.current.stats.mockReturnValue({ ...cameraStats, snaps: 2, holds: 1 });
    act(() => { vi.advanceTimersByTime(500); });
    expect(screen.getByTestId('control-stats')).toHaveTextContent('snaps 2 · holds 1');
  } finally {
    act(() => setViewerQuality({ perfOverlay: false }));
  }
  expect(screen.queryByTestId('control-stats')).not.toBeInTheDocument();
});

it("draws the pawn's crack, holds it full after the break until the world shows the block gone, then bursts it", () => {
  vi.useFakeTimers({ toFake: ['performance', 'Date'] }); // the hold measures time
  render(<PawnControlView body="maid_chan_70" returnTo="/story" />);
  expect(f.viewer!.layers).toEqual({ notes: false, bubbles: true, changes: false }); // no yellow glow over own digs
  const engine = viewEngine(), play = vi.fn();
  f.audio!.play!.current = play;
  const frame = () => f.viewer!.onBeforeFrame!(engine, performance.now());
  const dig = { x: 3, y: 64, z: -2, face: 4, id: 1, meta: 0 };
  f.control.pose.current = { ...f.control.state, dig: { ...dig, progress: 0.5 }, tick: 100 };
  frame();
  expect(engine.setBreak).toHaveBeenLastCalledWith({ x: 3, y: 64, z: -2, progress: 0.5 });
  expect(engine.blockBurst).toHaveBeenCalledExactlyOnceWith(3, 64, -2, 1, 0, 'block_hit', 4);
  expect(play).toHaveBeenCalledExactlyOnceWith([
    expect.objectContaining({ kind: 'sound', sound: 'block.stone.hit', x: 3.5, y: 64.5, z: -1.5, dim: 0 }),
  ]);
  const [[hit]] = play.mock.lastCall as [[{ at: number }]];
  expect(hit.at).toBe(Date.now()); // Unix ms, for the player's age filter
  frame(); // the same state frame again: no second chip
  expect(engine.blockBurst).toHaveBeenCalledOnce();
  const broke = { count: 1, ...dig };
  f.events!.onBroke!(broke);
  // The prediction may walk into the dug cell before the world's change arrives.
  expect(engine.overrideCollision).toHaveBeenCalledExactlyOnceWith(3, 64, -2, 0, 0, 1000);
  f.control.pose.current = { ...f.control.state, tick: 103 };
  frame();
  expect(engine.setBreak).toHaveBeenLastCalledWith({ x: 3, y: 64, z: -2, progress: 1 });
  expect(engine.blockBurst).toHaveBeenCalledOnce();
  engine.changed([[3, 64, -2]]);
  frame();
  expect(engine.setBreak).toHaveBeenLastCalledWith(null);
  expect(engine.blockBurst).toHaveBeenLastCalledWith(3, 64, -2, 1, 0, 'block_break');
  expect(play).toHaveBeenLastCalledWith([expect.objectContaining({ sound: 'block.stone.break', volume: 1, pitch: 0.8 })]);
  frame();
  expect(engine.blockBurst).toHaveBeenCalledTimes(2);
  // Other pawns' clients report the same break (effect 2001) into the world stream, at their own time: heard once.
  const echo = { kind: 'sound', sound: 'block.stone.break', volume: 1, pitch: 0.8, dim: 0, x: 3.5, y: 64.5, z: -1.5, at: Date.now() + 300 };
  expect(f.audio!.heard!.current!([echo])).toEqual([]);
});

it("plays the pawn's own break once when the world stream reports it first", () => {
  vi.useFakeTimers({ toFake: ['performance', 'Date'] });
  render(<PawnControlView body="maid_chan_70" returnTo="/story" />);
  const engine = viewEngine(), play = vi.fn();
  f.audio!.play!.current = play;
  const frame = () => f.viewer!.onBeforeFrame!(engine, performance.now());
  const dig = { x: 3, y: 64, z: -2, face: 4, id: 1, meta: 0 };
  f.control.pose.current = { ...f.control.state, dig: { ...dig, progress: 0.9 }, tick: 100 };
  frame();
  play.mockClear();
  const stream = { kind: 'sound', sound: 'block.stone.break', dim: 0, x: 3.5, y: 64.5, z: -1.5, at: Date.now() };
  expect(f.audio!.heard!.current!([stream])).toEqual([stream]);
  f.events!.onBroke!({ count: 1, ...dig });
  f.control.pose.current = { ...f.control.state, tick: 101 };
  engine.changed([[3, 64, -2]]);
  frame();
  expect(engine.blockBurst).toHaveBeenLastCalledWith(3, 64, -2, 1, 0, 'block_break'); // the particles still burst
  expect(play).not.toHaveBeenCalled();
  // Past the echo window the same cell sounds again.
  vi.advanceTimersByTime(1600);
  expect(f.audio!.heard!.current!([{ ...stream, at: Date.now() }])).toHaveLength(1);
});

it('clears the crack when control ends and when the view goes', () => {
  const page = render(<PawnControlView body="maid_chan_70" returnTo="/story" />);
  const engine = viewEngine();
  f.control.pose.current = { ...f.control.state, dig: { x: 3, y: 64, z: -2, face: 1, id: 1, meta: 0, progress: 0.5 }, tick: 100 };
  f.viewer!.onBeforeFrame!(engine, performance.now());
  expect(engine.setBreak).toHaveBeenLastCalledWith(expect.objectContaining({ progress: 0.5 }));
  f.control.status = 'ended';
  f.viewer!.onBeforeFrame!(engine, performance.now());
  expect(engine.setBreak).toHaveBeenLastCalledWith(null);
  f.control.status = 'active';
  f.viewer!.onBeforeFrame!(engine, performance.now());
  expect(engine.setBreak).toHaveBeenLastCalledWith(expect.objectContaining({ progress: 0.5 }));
  expect(engine.runtime.blockListeners.size).toBe(1);
  page.unmount();
  expect(engine.setBreak).toHaveBeenLastCalledWith(null);
  expect(engine.runtime.blockListeners.size).toBe(0);
});

it('sounds a footstep on the block under the feet every 1.67 blocks walked', () => {
  render(<PawnControlView body="maid_chan_70" returnTo="/story" />);
  const engine = viewEngine(), play = vi.fn();
  f.audio!.play!.current = play;
  engine.blockAt.mockReturnValue({ id: 2, meta: 0 });
  let x = 10.5;
  f.control.camera.current.position.mockImplementation(() => ({ x, y: 64, z: -3.5, dim: 0 }));
  const walk = (frames: number) => {
    for (let i = 0; i < frames; i++) {
      f.viewer!.onBeforeFrame!(engine, performance.now());
      x += 0.15;
    }
  };
  walk(12);
  expect(play).not.toHaveBeenCalled();
  walk(1);
  expect(engine.blockAt).toHaveBeenLastCalledWith(12, 63, -4);
  expect(play).toHaveBeenCalledExactlyOnceWith([expect.objectContaining({ sound: 'block.grass.step', y: 64, z: -3.5 })]);
  // Sneaking on the ground is silent.
  f.control.input.current.sneak = true;
  walk(30);
  expect(play).toHaveBeenCalledOnce();
});

it('stands the prediction on a block the server placed, and sounds it', async () => {
  vi.useFakeTimers();
  f.control.inventory = { windowId: 0, hotbarStart: 36, slots: [{ slot: 37, id: 5, meta: 2, count: 9 }] };
  f.control.inventoryAt = 1;
  f.control.input.current.hotbar = 1;
  render(<PawnControlView body="maid_chan_70" returnTo="/story" />);
  const engine = viewEngine(), play = vi.fn();
  f.audio!.play!.current = play;
  f.engine = engine;
  act(() => { vi.advanceTimersByTime(30); });
  f.viewer!.onBeforeFrame!(engine, performance.now());
  const input = (f.bind.mock.lastCall as unknown as [HTMLElement, { action: (kind: string) => void }])[1];
  engine.aim.mockReturnValue({ kind: 'block', x: 10, y: 63, z: -6, id: 1, meta: 0, face: 3, name: 'stone', distance: 2 });
  // Sent, but the game server did not fill the cell (the body still stood in it on the server's side): nothing changes.
  f.control.action.mockResolvedValueOnce({ ok: true, message: 'used block' });
  await act(async () => { input.action('use'); await Promise.resolve(); });
  expect(f.control.action).toHaveBeenLastCalledWith({ kind: 'use' });
  expect(engine.overrideCollision).not.toHaveBeenCalled();
  expect(play).not.toHaveBeenCalled();
  f.control.action.mockResolvedValueOnce({ ok: true, message: 'placed block', code: 'placed' });
  await act(async () => { input.action('use'); await Promise.resolve(); });
  expect(engine.overrideCollision).toHaveBeenCalledExactlyOnceWith(10, 63, -5, 5, 2, 1000);
  expect(play).toHaveBeenCalledExactlyOnceWith([expect.objectContaining({ sound: 'block.wood.place', x: 10.5, y: 63.5, z: -4.5 })]);
  // Another pawn nearby hears the same placement and reports it into the world stream: it is not played again.
  const echo = { kind: 'sound', sound: 'block.wood.place', dim: 0, x: 10.5, y: 63.5, z: -4.5, at: Date.now() + 200 };
  const elsewhere = { ...echo, x: 12.5 };
  expect(f.audio!.heard!.current!([echo, elsewhere])).toEqual([elsewhere]);
  // Refused by the server, or a click that uses the block: the prediction keeps the world as it is.
  f.control.action.mockResolvedValueOnce({ ok: false, message: 'Control input timed out.' });
  await act(async () => { input.action('use'); await Promise.resolve(); });
  engine.aim.mockReturnValue({ kind: 'block', x: 10, y: 63, z: -6, id: 54, meta: 0, face: 3, name: 'chest', distance: 2 });
  f.control.action.mockResolvedValueOnce({ ok: true, message: 'opened inventory' });
  await act(async () => { input.action('use'); await Promise.resolve(); });
  expect(engine.overrideCollision).toHaveBeenCalledOnce();
  expect(play).toHaveBeenCalledOnce();
});

it('names the ending on the ended screen, with Take over again', () => {
  f.control.status = 'ended';
  f.control.endReason = 'died';
  f.control.error = 'Your pawn died. Take control again once it has respawned.';
  const view = render(<PawnControlView body="maid_chan_70" returnTo="/story" />);
  expect(screen.getByRole('heading', { name: 'Your pawn died' })).toBeInTheDocument();
  expect(screen.getByText('Your pawn died. Take control again once it has respawned.')).toBeInTheDocument();
  expect(screen.queryByRole('status')).not.toBeInTheDocument(); // said once, on the card
  fireEvent.click(screen.getByRole('button', { name: 'Take over again' }));
  expect(f.control.retry).toHaveBeenCalledOnce();
  for (const [reason, heading] of [['expired', 'Control expired'], ['replaced', 'Control moved to another window'], ['offline', 'The pawn went offline']] as const) {
    f.control.endReason = reason;
    view.rerender(<PawnControlView body="maid_chan_70" returnTo="/story" />);
    expect(screen.getByRole('heading', { name: heading })).toBeInTheDocument();
  }
  // An ending the server did not name keeps the plain banner.
  f.control.endReason = null;
  f.control.error = 'The control connection closed. Take control again.';
  view.rerender(<PawnControlView body="maid_chan_70" returnTo="/story" />);
  expect(screen.queryByRole('heading')).not.toBeInTheDocument();
  expect(screen.getByRole('status')).toHaveTextContent('The control connection closed.');
  expect(screen.getByRole('button', { name: 'Take over again' })).toBeInTheDocument();
});

it('picks the aimed block from the hotbar on a middle click', () => {
  vi.useFakeTimers();
  f.control.inventory = { windowId: 0, hotbarStart: 36, slots: [{ slot: 36, id: 1, meta: 0, count: 3 }, { slot: 39, id: 5, meta: 2, count: 9 }] };
  f.control.inventoryAt = 1;
  render(<PawnControlView body="maid_chan_70" returnTo="/story" />);
  const engine = viewEngine();
  f.engine = engine;
  act(() => { vi.advanceTimersByTime(30); });
  f.viewer!.onBeforeFrame!(engine, performance.now());
  const pick = (f.bind.mock.lastCall as unknown as [HTMLElement, { pick: () => number | null }])[1].pick;
  engine.aim.mockReturnValue({ kind: 'block', x: 10, y: 63, z: -6, id: 5, meta: 2, face: 1, name: 'planks', distance: 2 });
  expect(pick()).toBe(3);
  engine.aim.mockReturnValue({ kind: 'block', x: 10, y: 63, z: -6, id: 4, meta: 0, face: 1, name: 'cobblestone', distance: 2 });
  expect(pick()).toBeNull();
  engine.aim.mockReturnValue({ kind: 'entity', id: 7, distance: 2 });
  expect(pick()).toBeNull();
});

it('shows the cell a right click would fill while a block is held, and tells the viewer only of changes', () => {
  f.control.inventory = { windowId: 0, hotbarStart: 36, slots: [{ slot: 37, id: 5, meta: 2, count: 9 }, { slot: 38, id: 280, meta: 0, count: 1 }] };
  f.control.inventoryAt = 1;
  f.control.input.current.hotbar = 1;
  render(<PawnControlView body="maid_chan_70" returnTo="/story" />);
  const engine = viewEngine();
  engine.aim.mockReturnValue({ kind: 'block', x: 10, y: 63, z: -6, id: 1, meta: 0, face: 3, name: 'stone', distance: 2 });
  f.viewer!.onBeforeFrame!(engine, 16);
  expect(engine.setHologramPreview).toHaveBeenCalledExactlyOnceWith([10, 63, -5, 5, 2], 'control:10,63,-5,5,2');
  f.viewer!.onBeforeFrame!(engine, 32);
  expect(engine.setHologramPreview).toHaveBeenCalledOnce();
  // A stick is no block.
  f.control.input.current.hotbar = 2;
  f.viewer!.onBeforeFrame!(engine, 48);
  expect(engine.setHologramPreview).toHaveBeenLastCalledWith(null, undefined);
  f.control.input.current.hotbar = 1;
  f.viewer!.onBeforeFrame!(engine, 64);
  expect(engine.setHologramPreview).toHaveBeenCalledTimes(3);
  engine.aim.mockReturnValue(null);
  f.viewer!.onBeforeFrame!(engine, 80);
  expect(engine.setHologramPreview).toHaveBeenLastCalledWith(null, undefined);
  // Ending control takes the ghost away.
  engine.aim.mockReturnValue({ kind: 'block', x: 10, y: 63, z: -6, id: 1, meta: 0, face: 3, name: 'stone', distance: 2 });
  f.viewer!.onBeforeFrame!(engine, 96);
  expect(engine.setHologramPreview).toHaveBeenCalledTimes(5);
  f.control.status = 'ended';
  f.viewer!.onBeforeFrame!(engine, 112);
  expect(engine.setHologramPreview).toHaveBeenLastCalledWith(null, undefined);
});

it("outlines the server's aim once it has applied the look and keys the player sees, and the view's own pick until then", () => {
  render(<PawnControlView body="maid_chan_70" returnTo="/story" />);
  const engine = viewEngine();
  f.engine = engine;
  engine.blockAt.mockReturnValue({ id: 31, meta: 1 });
  const ray = { origin: [10.5, 65.62, -3.5], direction: [0, 0, -1], max: 4.25 };
  const local = { kind: 'block', x: 10, y: 65, z: -6, id: 1, meta: 0, face: 3, name: 'stone', distance: 2.5 };
  const resolve = (physical: unknown) => f.viewer!.resolveAim!(physical, ray);
  // A server that does not report its aim: the view's pick.
  f.control.pose.current = { ...f.control.state, tick: 10, ackSeq: 4 };
  expect(resolve(local)).toBe(local);
  // The frame applied the newest look: the server's block, as the view has it.
  f.control.aimInput.current = { seq: 4, pending: false };
  f.control.pose.current = { ...f.control.state, tick: 10, ackSeq: 4, aim: { x: 10, y: 65, z: -5, face: 3 } };
  expect(resolve(local)).toEqual({ kind: 'block', x: 10, y: 65, z: -5, id: 31, meta: 1, face: 3, name: blockName('1.12.2', 31), distance: 0.5 });
  expect(engine.blockAt).toHaveBeenLastCalledWith(10, 65, -5);
  // An older frame, or a look not sent yet: the view's pick.
  f.control.pose.current = { ...f.control.state, tick: 9, ackSeq: 3, aim: { x: 10, y: 65, z: -5, face: 3 } };
  expect(resolve(local)).toBe(local);
  f.control.pose.current = { ...f.control.state, tick: 10, ackSeq: 4, aim: { x: 10, y: 65, z: -5, face: 3 } };
  f.control.aimInput.current = { seq: 4, pending: true };
  expect(resolve(local)).toBe(local);
  f.control.aimInput.current = { seq: 4, pending: false };
  // The same cell as the view's pick: the side the server's ray enters by.
  f.control.pose.current = { ...f.control.state, tick: 10, ackSeq: 5, aim: { x: 10, y: 65, z: -6, face: 2 } };
  expect(resolve(local)).toEqual({ ...local, face: 2 });
  // Nothing within the pawn's reach: no outline. A bot under the crosshair stays outlined.
  f.control.pose.current = { ...f.control.state, tick: 10, ackSeq: 5, aim: null };
  expect(resolve(local)).toBeNull();
  const bot = { kind: 'bot', name: 'maid_chan_71', x: 10.5, y: 64, z: -5.5, distance: 1.5 };
  expect(resolve(bot)).toBe(bot);
  // A block the view does not have: its own pick is the better guess.
  f.control.pose.current = { ...f.control.state, tick: 10, ackSeq: 5, aim: { x: 10, y: 65, z: -5, face: 3 } };
  engine.blockAt.mockReturnValue(null);
  expect(resolve(local)).toBe(local);
});

it('sends clicks without a target or a face, and marks the repeats of a held attack', async () => {
  vi.useFakeTimers();
  render(<PawnControlView body="maid_chan_70" returnTo="/story" />);
  const engine = viewEngine();
  f.engine = engine;
  act(() => { vi.advanceTimersByTime(30); });
  const input = (f.bind.mock.lastCall as unknown as [HTMLElement, { action: (kind: string, wholeStack?: boolean, repeat?: boolean) => void }])[1];
  engine.aim.mockReturnValue({ kind: 'block', x: 10, y: 63, z: -6, id: 1, meta: 0, face: 3, name: 'stone', distance: 2 });
  f.control.action.mockResolvedValue({ ok: true, message: 'dug block' });
  await act(async () => { input.action('attack'); input.action('attack', undefined, true); input.action('drop', true); await Promise.resolve(); });
  expect(f.control.action.mock.calls).toEqual([[{ kind: 'attack' }], [{ kind: 'attack', repeat: true }], [{ kind: 'drop', wholeStack: true }]]);
});

it("draws the pawn's armour and air, which the state frame carries, in the status bar", () => {
  Object.assign(f.control.state, { hp: 13, armor: 7, air: 150 });
  render(<PawnControlView body="maid_chan_70" returnTo="/story" />);
  expect(screen.getByRole('img', { name: 'Health 13 / 20' })).toBeInTheDocument();
  expect(screen.getByRole('img', { name: 'Armour 7 / 20' })).toBeInTheDocument();
  expect(screen.getByRole('img', { name: 'Air 5 / 10' })).toBeInTheDocument();
});
