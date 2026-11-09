import { act, renderHook } from '@testing-library/react';
import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { toSnake, type PawnControlActionResponse, type PawnControlSession } from '@mineclaw/contracts';
import { usePawnControl } from '@/features/control/usePawnControl';

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
  frame(value: unknown) { this.onmessage?.(new MessageEvent('message', { data: JSON.stringify(toSnake(value)) })); }
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
