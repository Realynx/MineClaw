import { readFileSync } from 'node:fs';
import path from 'node:path';
import { act, renderHook } from '@testing-library/react';
import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { toSnake, type PawnControlActionResponse, type PawnControlSession } from '@mineclaw/contracts';
import { ControlCamera } from '@/features/control/controlCamera';
import { serverAim, usePawnControl } from '@/features/control/usePawnControl';

const { api } = vi.hoisted(() => ({ api: vi.fn() }));
vi.mock('@/lib/api/client', () => ({ api, getCsrfToken: () => 'csrf', errorText: (e: Error) => e.message }));
const session: PawnControlSession = {
  session: 'private-test-session', body: '102', expiresAt: '2026-11-08T12:00:03Z', inputTimeoutMs: 350, leaseTimeoutMs: 3000,
  state: { x: 1, y: 64, z: 2, yaw: 90, pitch: -10, dim: 0, hp: 20, food: 18, heldSlot: 2, onGround: true },
};
const inventory = { windowId: 7, kind: 'chest', title: 'Chest', slotCount: 63, containerSlots: 27, mainStart: 27, hotbarStart: 54, slots: [] };

class FakeSocket {
  static readonly OPEN = 1;
  static instances: FakeSocket[] = [];
  readyState = 0;
  bufferedAmount = 0;
  onopen: ((event: Event) => void) | null = null;
  onmessage: ((event: MessageEvent<unknown>) => void) | null = null;
  onerror: ((event: Event) => void) | null = null;
  onclose: ((event: Event) => void) | null = null;
  sent: string[] = [];
  constructor(readonly url: string, readonly protocols: string[]) { FakeSocket.instances.push(this); }
  send(value: string) { this.sent.push(value); }
  close() { this.readyState = 3; }
  open() { this.readyState = 1; this.onopen?.(new Event('open')); }
  frame(value: unknown) { this.onmessage?.(new MessageEvent('message', { data: JSON.stringify(toSnake(value, { opaque: new Set<string>() })) })); }
  reply(seq: number, data: Partial<PawnControlActionResponse> = {}) {
    this.frame({ type: 'action', requestId: String(seq), data: { ok: true, message: '', state: session.state, ...data } });
  }
  frames(type?: string) {
    const frames = this.sent.map((text) => JSON.parse(text) as Record<string, unknown>);
    return type ? frames.filter((frame) => frame.type === type) : frames;
  }
}

beforeEach(() => {
  vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout', 'setInterval', 'clearInterval', 'Date', 'performance'] });
  api.mockReset(); api.mockResolvedValue(session);
  FakeSocket.instances = []; vi.stubGlobal('WebSocket', FakeSocket);
  Object.defineProperty(document, 'hidden', { configurable: true, value: false });
});
afterEach(() => { vi.unstubAllGlobals(); vi.useRealTimers(); });

async function connect() {
  const page = renderHook(() => usePawnControl('102'));
  await act(() => vi.advanceTimersByTimeAsync(1));
  const socket = FakeSocket.instances[0]!;
  await act(async () => {
    socket.open(); socket.frame({ type: 'state', data: session, sampledAtMs: 1_000_000 });
    await Promise.resolve();
  });
  return { page, socket };
}

it('sends key transitions before the next heartbeat and bounds high-rate look without delaying key-up', async () => {
  const { page, socket } = await connect();
  page.result.current.input.current.forward = 1;
  page.result.current.inputChanged('movement');
  expect(socket.frames('input')).toHaveLength(1);
  expect(socket.frames('input')[0]).toMatchObject({ forward: 1 });
  for (let i = 0; i < 100; i++) {
    page.result.current.input.current.yaw = i;
    page.result.current.inputChanged('look');
  }
  expect(socket.frames('input')).toHaveLength(1);
  page.result.current.input.current.forward = 0;
  page.result.current.inputChanged('stop');
  expect(socket.frames('input')).toHaveLength(2);
  expect(socket.frames('input').at(-1)).toMatchObject({ forward: 0, yaw: 99 });
  await act(() => vi.advanceTimersByTimeAsync(30));
  expect(socket.frames('input')).toHaveLength(2); // pending look was cancelled by the immediate stop
  page.unmount();
});

it('attaches with CSRF protocols and keeps 20 Hz movement independent of response timing', async () => {
  const page = renderHook(() => usePawnControl('102'));
  await act(() => vi.advanceTimersByTimeAsync(1));
  const socket = FakeSocket.instances[0]!;
  expect(page.result.current.status).toBe('connecting');
  expect(socket.url).toMatch(/\/api\/bodies\/102\/control\/socket$/);
  expect(socket.url).not.toContain(session.session);
  expect(socket.protocols).toEqual(['mineclaw.control.v1', 'csrf.csrf']);
  await act(async () => { expect(await page.result.current.action({ kind: 'inventory' })).toBeNull(); });
  expect(page.result.current.error).toBe('');
  await act(async () => { socket.open(); await Promise.resolve(); });
  await act(() => vi.advanceTimersByTimeAsync(100));
  expect(socket.frames()).toEqual([{ type: 'attach', session: session.session }]);
  await act(async () => { socket.frame({ type: 'state', data: session }); await Promise.resolve(); });
  expect(page.result.current.status).toBe('active');
  page.result.current.input.current.forward = 1;
  await act(() => vi.advanceTimersByTimeAsync(500));
  const frames = socket.frames('input');
  expect(frames).toHaveLength(10);
  expect(frames.map((frame) => frame.seq)).toEqual([1, 2, 3, 4, 5, 6, 7, 8, 9, 10]);
  expect(frames.every((frame) => frame.forward === 1)).toBe(true);
  expect(api.mock.calls.filter(([id]) => id === 'bodies.controlInput')).toHaveLength(0);
  page.unmount();
  expect(socket.readyState).toBe(3);
  expect(api).toHaveBeenCalledWith('bodies.controlRelease', expect.objectContaining({ body: { session: session.session } }));
});

it('accepts a literal supervisor state frame with snake_case payload fields and null optional snapshots', async () => {
  const page = renderHook(() => usePawnControl('102'));
  await act(() => vi.advanceTimersByTimeAsync(1));
  const socket = FakeSocket.instances[0]!;
  await act(async () => {
    socket.open();
    // Keep this wire fixture literal: converting a camelCase fixture with the same helper would hide decoding mistakes.
    socket.onmessage?.(new MessageEvent('message', { data: JSON.stringify({
      type: 'state', sampled_at_ms: 1_794_241_000_000,
      data: {
        session: 'private-test-session', body: '102', expires_at: '2026-11-09T08:16:43.1234567+00:00',
        input_timeout_ms: 350, lease_timeout_ms: 3000,
        state: { x: 229, y: 64, z: 381, yaw: 90, pitch: -10, dim: 0, hp: 20, food: 18, held_slot: 6, on_ground: false, window_id: 0 },
        inventory: null, action: null,
      },
    }) }));
    await Promise.resolve();
  });
  expect(page.result.current.status).toBe('active');
  expect(page.result.current.error).toBe('');
  expect(page.result.current.state).toMatchObject({ x: 229, heldSlot: 6, onGround: false, windowId: 0 });
  expect(page.result.current.camera.current.position(performance.now())).toMatchObject({ x: 229, y: 64, z: 381, dim: 0 });
  await act(() => vi.advanceTimersByTimeAsync(50));
  expect(socket.frames('input')).toHaveLength(1);
  page.unmount();
});

it('decodes a literal supervisor action response including nested inventory layout fields', async () => {
  const { page, socket } = await connect();
  const reply = page.result.current.action({ kind: 'inventory' });
  await act(async () => {
    socket.onmessage?.(new MessageEvent('message', { data: JSON.stringify({
      type: 'action', request_id: '1', data: {
        ok: true, message: 'inventory', pending: false,
        state: { x: 1, y: 64, z: 2, yaw: 90, pitch: -10, dim: 0, hp: 20, food: 18, held_slot: 2, on_ground: true, window_id: 7 },
        inventory: { window_id: 7, kind: 'chest', title: 'Chest', slot_count: 63, container_slots: 27, main_start: 27, hotbar_start: 54,
          slots: [{ slot: 54, id: 5, meta: 1, count: 32 }], cursor: null },
      },
    }) }));
    await reply;
  });
  expect(page.result.current.status).toBe('active');
  expect(page.result.current.error).toBe('');
  expect(page.result.current.inventory).toEqual({ ...inventory, slots: [{ slot: 54, id: 5, meta: 1, count: 32 }], cursor: null });
  expect(page.result.current.inventoryAt).toBe(Date.now());
  page.unmount();
});

it('sends click aim immediately before the action and keeps moving while its acknowledgement is pending', async () => {
  const { page, socket } = await connect();
  Object.assign(page.result.current.input.current, { forward: 1, yaw: 135, pitch: 12, hotbar: 4, sneak: true });
  void page.result.current.action({ kind: 'use' });
  void page.result.current.action({ kind: 'use' });
  expect(socket.frames().slice(-2)).toMatchObject([
    { type: 'input', seq: 1, forward: 1, yaw: 135, pitch: 12, hotbar: 4, sneak: true },
    { type: 'action', seq: 1, request_id: '1', kind: 'use' },
  ]);
  page.result.current.input.current.forward = 0;
  await act(() => vi.advanceTimersByTimeAsync(250));
  expect(socket.frames('input')).toHaveLength(6);
  expect(socket.frames('input').at(-1)?.forward).toBe(0);
  expect(socket.frames('action')).toHaveLength(1);
  page.unmount();
});

it('keeps quick use/release/use/release in wire order without waiting for any acknowledgement', async () => {
  const { page, socket } = await connect();
  void page.result.current.action({ kind: 'use' });
  void page.result.current.action({ kind: 'release' });
  void page.result.current.action({ kind: 'use' });
  void page.result.current.action({ kind: 'release' });
  expect(socket.frames('action').map((frame) => frame.kind)).toEqual(['use', 'release', 'use', 'release']);
  await act(async () => { socket.reply(1, { inventory }); await Promise.resolve(); });
  expect(page.result.current.inventory?.windowId).toBe(7);
  expect(page.result.current.inventoryAt).toBe(Date.now());
  page.unmount();
});

it('cancel is immediate and an older action response cannot replace inventory', async () => {
  const { page, socket } = await connect();
  void page.result.current.action({ kind: 'use' });
  void page.result.current.action({ kind: 'cancel' });
  expect(socket.frames('action').map((frame) => frame.kind)).toEqual(['use', 'cancel']);
  await act(async () => { socket.reply(2); socket.reply(1, { inventory }); await Promise.resolve(); });
  expect(page.result.current.inventory).toBeNull();
  page.unmount();
});

it('inventory reading survives unlock cancellation and ignores a read preceding a newer inventory click', async () => {
  const { page, socket } = await connect();
  void page.result.current.action({ kind: 'use' });
  void page.result.current.action({ kind: 'inventory' });
  void page.result.current.action({ kind: 'cancel' });
  await act(async () => { socket.reply(3); socket.reply(2, { inventory }); await Promise.resolve(); });
  expect(page.result.current.inventory?.windowId).toBe(7);
  void page.result.current.action({ kind: 'inventory' });
  void page.result.current.action({ kind: 'inventory_click', windowId: 7, slot: 3 });
  await act(async () => {
    socket.reply(5, { inventory: { ...inventory, cursor: { id: 3, meta: 0, count: 12 } } });
    socket.reply(4, { inventory: { ...inventory, cursor: null } });
    await Promise.resolve();
  });
  expect(page.result.current.inventory?.cursor?.count).toBe(12);
  page.unmount();
});

it('continues sending during 1.7 s of delayed state then ends on stale updates within the lease budget', async () => {
  const { page, socket } = await connect();
  page.result.current.input.current.forward = 1;
  await act(() => vi.advanceTimersByTimeAsync(1700));
  expect(page.result.current.status).toBe('active');
  expect(socket.frames('input')).toHaveLength(34);
  await act(async () => { socket.frame({ type: 'state', data: session }); await Promise.resolve(); });
  await act(() => vi.advanceTimersByTimeAsync(2800));
  expect(page.result.current.status).toBe('ended');
  expect(page.result.current.input.current.forward).toBe(0);
  expect(page.result.current.error).toContain('timed out');
  expect(socket.readyState).toBe(3);
  page.unmount();
});

it.each(['buffer', 'closed', 'malformed', 'wrong-session'])('releases on a %s connection without an automatic reconnect', async (failure) => {
  const { page, socket } = await connect();
  await act(async () => {
    if (failure === 'buffer') { socket.bufferedAmount = 16_385; await vi.advanceTimersByTimeAsync(50); }
    else if (failure === 'closed') socket.onclose?.(new Event('close'));
    else if (failure === 'malformed') socket.onmessage?.(new MessageEvent('message', { data: '{bad' }));
    else socket.frame({ type: 'state', data: { ...session, session: 'different' } });
    await Promise.resolve();
  });
  expect(page.result.current.status).toBe('ended');
  expect(socket.readyState).toBe(3);
  await act(() => vi.advanceTimersByTimeAsync(1000));
  expect(FakeSocket.instances).toHaveLength(1);
  page.unmount();
});

it('releases when hidden and fences late states and action acknowledgements from a previous connection', async () => {
  const { page, socket } = await connect();
  void page.result.current.action({ kind: 'use' });
  const late = socket.onmessage;
  await act(async () => {
    Object.defineProperty(document, 'hidden', { configurable: true, value: true });
    document.dispatchEvent(new Event('visibilitychange')); await Promise.resolve();
  });
  expect(page.result.current.status).toBe('ended');
  Object.defineProperty(document, 'hidden', { configurable: true, value: false });
  await act(async () => { page.result.current.retry(); await Promise.resolve(); });
  await act(() => vi.advanceTimersByTimeAsync(1));
  const next = FakeSocket.instances[1]!;
  await act(async () => {
    next.open(); next.frame({ type: 'state', data: { ...session, state: { ...session.state, x: 20 } } });
    late?.(new MessageEvent('message', { data: JSON.stringify(toSnake({ type: 'state', data: session })) }));
    await Promise.resolve();
  });
  expect(page.result.current.status).toBe('active');
  expect(page.result.current.pose.current?.x).toBe(20);
  expect(page.result.current.error).toBe('');
  page.unmount();
});

it('releases an HTTP acquisition completed after leaving without opening a socket', async () => {
  let finish: ((reply: PawnControlSession) => void) | undefined;
  api.mockImplementation((id: string) => id === 'bodies.controlAcquire'
    ? new Promise<PawnControlSession>((resolve) => { finish = resolve; }) : Promise.resolve(session));
  const page = renderHook(() => usePawnControl('102'));
  await act(() => vi.advanceTimersByTimeAsync(1));
  page.unmount();
  await act(async () => { finish?.(session); await Promise.resolve(); });
  expect(FakeSocket.instances).toHaveLength(0);
  expect(api).toHaveBeenCalledWith('bodies.controlRelease', expect.objectContaining({ body: { session: session.session } }));
});

const lookedAway = 'Dig stopped because the aim, held item or control input changed.';
type Ending = { seq: number; ok: boolean; message: string; code?: string };
/** A state frame whose newest action has finished. */
const finished = (socket: FakeSocket, action: Ending) => act(async () => {
  socket.frame({ type: 'state', data: { ...session, action: { pending: false, ...action } } });
  await Promise.resolve();
});

it('shows no banner for a dig the player dropped by looking away, by code or by the message of an older server', async () => {
  const { page, socket } = await connect();
  // A real failure is shown, and a code this browser does not know changes nothing about that.
  await finished(socket, { seq: 1, ok: false, message: 'That block is unbreakable.', code: 'unbreakable' });
  expect(page.result.current.status).toBe('active');
  expect(page.result.current.error).toBe('That block is unbreakable.');
  await finished(socket, { seq: 2, ok: false, message: lookedAway, code: 'aim_changed' });
  expect(page.result.current.error).toBe('');
  await finished(socket, { seq: 3, ok: false, message: 'The server did not break that block.' });
  expect(page.result.current.error).toBe('The server did not break that block.');
  await finished(socket, { seq: 4, ok: false, message: lookedAway });
  expect(page.result.current.error).toBe('');
  await finished(socket, { seq: 5, ok: false, message: 'That block cannot be dug.' });
  await finished(socket, { seq: 6, ok: false, message: 'superseded', code: 'cancelled' });
  expect(page.result.current.error).toBe('');
  await finished(socket, { seq: 7, ok: false, message: 'That block cannot be dug.' });
  await finished(socket, { seq: 8, ok: false, message: 'action cancelled' });
  expect(page.result.current.error).toBe('');
  // A code this browser does not know counts as no code: the message still decides.
  await finished(socket, { seq: 9, ok: false, message: 'That block cannot be dug.' });
  await finished(socket, { seq: 10, ok: false, message: lookedAway, code: 'added_by_a_newer_server' });
  expect(page.result.current.error).toBe('');
  page.unmount();
});

it('shows no banner when the acknowledgement itself reports the dig as dropped', async () => {
  const { page, socket } = await connect();
  const attack = async (seq: number, reply: Partial<PawnControlActionResponse>) => {
    const sent = page.result.current.action({ kind: 'attack' });
    await act(async () => { socket.reply(seq, reply); await sent; });
  };
  await attack(1, { ok: false, message: 'That block is unbreakable.' });
  expect(page.result.current.error).toBe('That block is unbreakable.');
  await attack(2, { ok: false, message: lookedAway, code: 'aim_changed' });
  expect(page.result.current.error).toBe('');
  await attack(3, { ok: false, message: 'That block is unbreakable.' });
  await attack(4, { ok: false, message: lookedAway });
  expect(page.result.current.error).toBe('');
  page.unmount();
});

it('offers the reach of the session to the view, and none while the server does not send one', async () => {
  api.mockResolvedValue({ ...session, reach: 5 });
  const page = renderHook(() => usePawnControl('102'));
  await act(() => vi.advanceTimersByTimeAsync(1));
  expect(page.result.current.reach.current).toBe(5); // from the acquire reply, before the socket has said anything
  const socket = FakeSocket.instances[0]!;
  await act(async () => { socket.open(); socket.frame({ type: 'state', data: { ...session, reach: 4.5 } }); await Promise.resolve(); });
  expect(page.result.current.reach.current).toBe(4.5);
  await act(async () => { socket.frame({ type: 'state', data: session }); await Promise.resolve(); });
  expect(page.result.current.reach.current).toBeUndefined(); // an older server: the viewer keeps its own default
  page.unmount();
});

it('keeps every per-tick field of the literal wire frame through the socket', async () => {
  // The one hand-written snake_case frame that names every field of the wire; the contracts test parses the same file.
  const here = path.dirname(new URL(import.meta.url).pathname.replace(/^\/([A-Za-z]:)/, '$1'));
  const text = readFileSync(path.join(here, '..', '..', '..', 'packages', 'contracts', 'test', 'fixtures', 'pawn-control-frame.json'), 'utf8');
  const frame = JSON.parse(text) as { data: { session: string } };
  api.mockResolvedValue({ ...session, session: frame.data.session });
  const page = renderHook(() => usePawnControl('102'));
  await act(() => vi.advanceTimersByTimeAsync(1));
  const socket = FakeSocket.instances[0]!;
  await act(async () => { socket.open(); socket.onmessage?.(new MessageEvent('message', { data: text })); await Promise.resolve(); });
  expect(page.result.current.status).toBe('active');
  // Its last action is a dig dropped by looking away: not an error to show.
  expect(page.result.current.error).toBe('');
  expect(page.result.current.state).toEqual({
    x: 229.5, y: 64, z: 381.3, yaw: 270, pitch: 12.5, dim: 0, hp: 18, food: 17, heldSlot: 6, onGround: true, windowId: 0,
    tick: 48213, ackSeq: 912, ackWaitMs: 31.5, vx: 0.1178, vy: -0.0784, vz: -0.0012, teleports: 2, impulses: 1,
    usingItem: false, stopped: false, effects: { speed: 1, slowness: -1, jumpBoost: 0 },
    dig: { x: 231, y: 65, z: 381, face: 4, progress: 0.35, id: 1, meta: 0 }, aim: { x: 231, y: 64, z: 381, face: 4 },
    broke: { count: 3, x: 230, y: 65, z: 381, id: 17, meta: 2 }, armor: 7, air: 300,
  });
  expect(page.result.current.pose.current?.dig?.progress).toBe(0.35);
  expect(page.result.current.reach.current).toBe(4.25);
  expect(page.result.current.inventory?.slots).toHaveLength(2);
  page.unmount();
});

it('tells the camera when each input frame left and hands it every whole state frame', async () => {
  const sent = vi.spyOn(ControlCamera.prototype, 'sent');
  const pushed = vi.spyOn(ControlCamera.prototype, 'push');
  try {
    const { page, socket } = await connect();
    expect(sent).not.toHaveBeenCalled();
    page.result.current.input.current.forward = 1;
    page.result.current.inputChanged('movement');
    expect(socket.frames('input').at(-1)).toMatchObject({ seq: 1, forward: 1 });
    expect(sent).toHaveBeenLastCalledWith(1, performance.now());
    await act(() => vi.advanceTimersByTimeAsync(50)); // the heartbeat
    expect(socket.frames('input').at(-1)).toMatchObject({ seq: 2 });
    expect(sent).toHaveBeenLastCalledWith(2, expect.any(Number));
    // A click sends the input it was made with first, so the server applies the click with that aim.
    const attack = page.result.current.action({ kind: 'attack' });
    expect(socket.frames('input').at(-1)).toMatchObject({ seq: 3 });
    expect(sent).toHaveBeenCalledTimes(3);
    await act(async () => { socket.reply(1); await attack; });
    const state = { ...session.state, tick: 812, ackSeq: 2, ackWaitMs: 21.5, vx: 0.1, vy: -0.0784, vz: 0, stopped: false };
    await act(async () => { socket.frame({ type: 'state', data: { ...session, state } }); await Promise.resolve(); });
    expect(pushed).toHaveBeenLastCalledWith(state, performance.now());
    page.unmount();
  } finally {
    sent.mockRestore(); pushed.mockRestore();
  }
});

it("names the input frame that last moved the look or the keys, for the server's aim to answer for it", async () => {
  const { page, socket } = await connect();
  const aim = page.result.current.aimInput;
  expect(aim.current).toEqual({ seq: 0, pending: false });
  const tick = (ackSeq: number, aimAt: { x: number; y: number; z: number; face: number } | null) =>
    ({ ...session.state, tick: 900 + ackSeq, ackSeq, aim: aimAt });
  const stone = { x: 4, y: 64, z: 2, face: 1 };
  // The first frame of the takeover looks where the pawn already looked.
  expect(serverAim(tick(1, stone), aim.current)).toEqual(stone);
  page.result.current.input.current.yaw = 120;
  page.result.current.inputChanged('look');
  expect(socket.frames('input').at(-1)).toMatchObject({ seq: 1, yaw: 120 });
  expect(aim.current).toEqual({ seq: 1, pending: false });
  // A look inside the 25 ms bound waits for its frame; until it leaves, no frame can have applied it.
  page.result.current.input.current.yaw = 130;
  page.result.current.inputChanged('look');
  expect(aim.current.pending).toBe(true);
  expect(serverAim(tick(9, stone), aim.current)).toBeUndefined();
  await act(() => vi.advanceTimersByTimeAsync(25));
  expect(socket.frames('input').at(-1)).toMatchObject({ seq: 2, yaw: 130 });
  expect(aim.current).toEqual({ seq: 2, pending: false });
  expect(serverAim(tick(1, stone), aim.current)).toBeUndefined();
  expect(serverAim(tick(2, null), aim.current)).toBeNull();
  // The heartbeat and a hotbar change move neither the look nor the keys.
  await act(() => vi.advanceTimersByTimeAsync(50));
  page.result.current.inputChanged('hotbar');
  expect(socket.frames('input').length).toBeGreaterThan(3);
  expect(aim.current).toEqual({ seq: 2, pending: false });
  page.result.current.input.current.forward = 1;
  page.result.current.inputChanged('movement');
  expect(aim.current).toEqual({ seq: socket.frames('input').length, pending: false });
  // No frame of a server that does not report its aim, and none before the first tick, answers for it.
  expect(serverAim({ ...session.state, ackSeq: 99 }, aim.current)).toBeUndefined();
  expect(serverAim(session.state, { seq: 0, pending: false })).toBeUndefined();
  page.unmount();
});

it('reports a break once when the broke counter grows, however the action status of that frame reads', async () => {
  const onBroke = vi.fn();
  api.mockResolvedValue({ ...session, state: { ...session.state, broke: { count: 4, x: 0, y: 60, z: 0, id: 1, meta: 0 } } });
  const page = renderHook(() => usePawnControl('102', { onBroke }));
  await act(() => vi.advanceTimersByTimeAsync(1));
  const socket = FakeSocket.instances[0]!;
  const send = (state: Partial<PawnControlSession['state']>, action?: PawnControlSession['action']) => act(async () => {
    socket.frame({ type: 'state', data: { ...session, state: { ...session.state, ...state }, ...(action ? { action } : {}) } });
    await Promise.resolve();
  });
  await act(async () => { socket.open(); await Promise.resolve(); });
  const dig = { x: 3, y: 64, z: -2, face: 1, id: 1, meta: 0 };
  // Breaks counted before this takeover's first frame are not news.
  await send({ dig: { ...dig, progress: 0.6 }, broke: { count: 4, x: 0, y: 60, z: 0, id: 1, meta: 0 } });
  expect(onBroke).not.toHaveBeenCalled();
  // The next click is already pending in the frame that carries the break: the counter still tells.
  const block = { count: 5, x: 3, y: 64, z: -2, id: 1, meta: 0 };
  await send({ dig: { ...dig, progress: 1 }, broke: block }, { seq: 9, pending: true });
  expect(onBroke).toHaveBeenCalledOnce();
  expect(onBroke).toHaveBeenLastCalledWith(block);
  // The same counter again, with or without a finished dig status, is the same break.
  await send({ broke: block }, { seq: 9, pending: false, ok: true, message: 'dug', code: 'dug' });
  await send({ broke: block });
  await send({});
  expect(onBroke).toHaveBeenCalledOnce();
  // A finished dig reported without a grown counter breaks nothing.
  await send({ broke: block }, { seq: 10, pending: false, ok: true, message: 'dug', code: 'dug' });
  expect(onBroke).toHaveBeenCalledOnce();
  await send({ broke: { ...block, count: 6, y: 63 } });
  expect(onBroke).toHaveBeenCalledTimes(2);
  expect(onBroke).toHaveBeenLastCalledWith({ ...block, count: 6, y: 63 });
  page.unmount();
});

it('names the ending the server gives on its closing frame, and none for an ending it does not know', async () => {
  for (const [reason, named] of [['died', 'died'], ['expired', 'expired'], ['replaced', 'replaced'], ['kicked', null], [undefined, null]] as const) {
    FakeSocket.instances = [];
    const { page, socket } = await connect();
    expect(page.result.current.endReason).toBeNull();
    const message = `The takeover ended (${reason ?? 'no reason'}).`;
    await act(async () => { socket.frame({ type: 'error', message, ...(reason ? { reason } : {}) }); await Promise.resolve(); });
    expect(page.result.current).toMatchObject({ status: 'ended', error: message, endReason: named });
    expect(socket.readyState).toBe(3);
    // Taking over again starts without the old ending.
    await act(async () => { page.result.current.retry(); await vi.advanceTimersByTimeAsync(1); });
    expect(page.result.current.endReason).toBeNull();
    page.unmount();
  }
});

it('lets no state frame read before a click put back the inventory the click replied with', async () => {
  const { page, socket } = await connect();
  const counted = (count: number) => ({ ...inventory, windowId: 0, slots: [{ slot: 36, id: 4, meta: 0, count }] });
  const frame = (tick: number, count: number) => act(async () => {
    socket.frame({ type: 'state', data: { ...session, state: { ...session.state, tick }, inventory: counted(count) } });
    await Promise.resolve();
  });
  await frame(18, 64);
  expect(page.result.current.inventory?.slots[0]?.count).toBe(64);
  void page.result.current.action({ kind: 'inventory_click', windowId: 0, slot: 36 });
  await act(async () => { socket.reply(1, { inventory: counted(32), state: { ...session.state, tick: 20 } }); await Promise.resolve(); });
  expect(page.result.current.inventory?.slots[0]?.count).toBe(32);
  // Frames of the click's tick or before it were read before the click: their inventory is older than the reply.
  await frame(19, 64);
  await frame(20, 64);
  expect(page.result.current.inventory?.slots[0]?.count).toBe(32);
  // A later tick's inventory is news (a pickup, say).
  await frame(21, 40);
  expect(page.result.current.inventory?.slots[0]?.count).toBe(40);
  page.unmount();
});

it('keeps the inventory with the higher version, whichever tick or reply brought it', async () => {
  const { page, socket } = await connect();
  const counted = (count: number, version: number) => ({ ...inventory, windowId: 0, slots: [{ slot: 36, id: 4, meta: 0, count }], version });
  const frame = (tick: number, count: number, version: number) => act(async () => {
    socket.frame({ type: 'state', data: { ...session, state: { ...session.state, tick }, inventory: counted(count, version) } });
    await Promise.resolve();
  });
  await frame(18, 64, 5);
  void page.result.current.action({ kind: 'inventory_click', windowId: 0, slot: 36 });
  await act(async () => { socket.reply(1, { inventory: counted(32, 7), state: { ...session.state, tick: 20 } }); await Promise.resolve(); });
  expect(page.result.current.inventory?.slots[0]?.count).toBe(32);
  // A second slot update in the click's own tick: the frame of that tick is newer than the reply, and the server sends it once.
  await frame(20, 31, 8);
  expect(page.result.current.inventory?.slots[0]?.count).toBe(31);
  // A reply read before that frame cannot put the older inventory back.
  void page.result.current.action({ kind: 'inventory' });
  await act(async () => { socket.reply(2, { inventory: counted(32, 7) }); await Promise.resolve(); });
  expect(page.result.current.inventory?.slots[0]?.count).toBe(31);
  await frame(21, 32, 8);
  expect(page.result.current.inventory?.slots[0]?.count).toBe(31);
  page.unmount();
});
