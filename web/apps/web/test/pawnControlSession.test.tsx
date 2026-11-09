import { act, renderHook } from '@testing-library/react';
import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import type { PawnControlActionResponse, PawnControlSession } from '@mineclaw/contracts';
import { usePawnControl } from '@/features/control/usePawnControl';

const { api } = vi.hoisted(() => ({ api: vi.fn() }));
vi.mock('@/lib/api/client', () => ({ api, getCsrfToken: () => 'csrf', errorText: (e: Error) => e.message }));
const session: PawnControlSession = {
  session: 'private-test-session', body: '102', expiresAt: '2026-11-08T12:00:03Z', inputTimeoutMs: 350, leaseTimeoutMs: 3000,
  state: { x: 1, y: 64, z: 2, yaw: 90, pitch: -10, dim: 0, hp: 20, food: 18, heldSlot: 2, onGround: true },
};
beforeEach(() => {
  vi.useFakeTimers(); api.mockReset();
  api.mockImplementation((id: string) => Promise.resolve(id === 'bodies.controlRelease' ? { ok: true, state: session.state } : session));
  Object.defineProperty(document, 'hidden', { configurable: true, value: false });
});
afterEach(() => { vi.useRealTimers(); });

it('uses immediate mouse look and at most one outstanding movement request', async () => {
  let finish: ((v: PawnControlSession) => void) | undefined;
  api.mockImplementation((id: string) => id === 'bodies.controlInput' ? new Promise<PawnControlSession>((resolve) => { finish = resolve; }) : Promise.resolve(session));
  const page = renderHook(() => usePawnControl('102'));
  await act(() => vi.advanceTimersByTimeAsync(1));
  expect(page.result.current.status).toBe('active');
  expect(page.result.current.input.current.yaw).toBe(90);
  page.result.current.input.current.forward = 1;
  await act(() => vi.advanceTimersByTimeAsync(500));
  expect(api.mock.calls.filter(([id]) => id === 'bodies.controlInput')).toHaveLength(1);
  await act(async () => { finish?.(session); await Promise.resolve(); });
  page.result.current.input.current.forward = 0;
  await act(() => vi.advanceTimersByTimeAsync(50));
  const calls = api.mock.calls.filter(([id]) => id === 'bodies.controlInput');
  expect(calls).toHaveLength(2);
  expect((calls[1]![1] as { body: { forward: number } }).body.forward).toBe(0);
  page.unmount();
  expect(api).toHaveBeenCalledWith('bodies.controlRelease', expect.objectContaining({ body: { session: session.session } }));
});

it('allows a slow input reply within the lease but ends control when the transport budget expires', async () => {
  const timeout = vi.spyOn(AbortSignal, 'timeout').mockImplementation((milliseconds) => {
    const controller = new AbortController();
    setTimeout(() => controller.abort(), milliseconds);
    return controller.signal;
  });
  let finish: ((reply: PawnControlSession) => void) | undefined;
  api.mockImplementation((id: string, options: { signal?: AbortSignal }) => id === 'bodies.controlInput'
    ? new Promise<PawnControlSession>((resolve, reject) => {
      finish = resolve;
      options.signal?.addEventListener('abort', () => reject(new Error('Input response timed out.')), { once: true });
    }) : Promise.resolve(session));
  const page = renderHook(() => usePawnControl('102'));
  try {
    await act(() => vi.advanceTimersByTimeAsync(51));
    expect(timeout).toHaveBeenLastCalledWith(session.leaseTimeoutMs - 250);
    page.result.current.input.current.forward = 1;
    await act(() => vi.advanceTimersByTimeAsync(1700));
    await act(async () => { finish?.(session); await Promise.resolve(); });
    expect(page.result.current.status).toBe('active');
    expect(page.result.current.error).toBe('');
    expect(api.mock.calls.filter(([id]) => id === 'bodies.controlInput')).toHaveLength(1);
    await act(() => vi.advanceTimersByTimeAsync(50));
    expect(api.mock.calls.filter(([id]) => id === 'bodies.controlInput')).toHaveLength(2);
    await act(() => vi.advanceTimersByTimeAsync(session.leaseTimeoutMs));
    expect(page.result.current.status).toBe('ended');
    expect(page.result.current.input.current.forward).toBe(0);
    expect(page.result.current.error).toBe('Input response timed out.');
    expect(api).toHaveBeenCalledWith('bodies.controlRelease', expect.objectContaining({ body: { session: session.session } }));
    await act(async () => { finish?.(session); await Promise.resolve(); });
    expect(page.result.current.status).toBe('ended');
  } finally { page.unmount(); timeout.mockRestore(); }
});

it('releases control when hidden, never automatically reacquires, and ignores late movement', async () => {
  const page = renderHook(() => usePawnControl('102'));
  await act(() => vi.advanceTimersByTimeAsync(1));
  await act(async () => {
    Object.defineProperty(document, 'hidden', { configurable: true, value: true });
    document.dispatchEvent(new Event('visibilitychange'));
    await Promise.resolve();
  });
  expect(page.result.current.status).toBe('ended');
  await act(() => vi.advanceTimersByTimeAsync(1000));
  expect(api.mock.calls.filter(([id]) => id === 'bodies.controlInput')).toHaveLength(0);
  expect(api.mock.calls.filter(([id]) => id === 'bodies.controlAcquire')).toHaveLength(1);
  page.unmount();
});

it('cleans up an acquisition which completed after leaving the page', async () => {
  let finish: ((v: PawnControlSession) => void) | undefined;
  api.mockImplementation((id: string) => id === 'bodies.controlAcquire' ? new Promise<PawnControlSession>((resolve) => { finish = resolve; }) : Promise.resolve(session));
  const page = renderHook(() => usePawnControl('102'));
  await act(() => vi.advanceTimersByTimeAsync(1));
  page.unmount();
  await act(async () => { finish?.(session); await Promise.resolve(); });
  expect(api).toHaveBeenCalledWith('bodies.controlRelease', expect.objectContaining({ body: { session: session.session } }));
});

it('lets cancel bypass a pending held action and does not queue repeated swings', async () => {
  const page = renderHook(() => usePawnControl('102'));
  await act(() => vi.advanceTimersByTimeAsync(1));
  api.mockImplementation((id: string, opts: { body: { kind?: string } }) =>
    id === 'bodies.controlAction' && opts.body.kind === 'attack' ? new Promise(() => {})
      : Promise.resolve(id === 'bodies.controlInput' ? session : { ok: true, message: '', state: session.state }));
  void page.result.current.action({ kind: 'attack' });
  void page.result.current.action({ kind: 'attack' });
  await act(async () => { await Promise.resolve(); });
  await act(async () => { await page.result.current.action({ kind: 'cancel' }); });
  expect(api.mock.calls.filter(([id]) => id === 'bodies.controlAction').map(([, args]) => (args as { body: { kind: string } }).body.kind)).toEqual(['attack', 'cancel']);
  page.unmount();
});

it('orders quick press/release HTTP requests and keeps a second click queued, including fresh inventory', async () => {
  const pending: Array<(reply: PawnControlActionResponse) => void> = [];
  const page = renderHook(() => usePawnControl('102'));
  await act(() => vi.advanceTimersByTimeAsync(1));
  api.mockImplementation((id: string) => id === 'bodies.controlAction'
    ? new Promise<PawnControlActionResponse>((resolve) => { pending.push(resolve); }) : Promise.resolve(session));
  void page.result.current.action({ kind: 'use' });
  void page.result.current.action({ kind: 'release' });
  void page.result.current.action({ kind: 'use' });
  void page.result.current.action({ kind: 'release' });
  await act(async () => { await Promise.resolve(); });
  const kinds = () => api.mock.calls.filter(([id]) => id === 'bodies.controlAction')
    .map(([, args]) => (args as { body: { kind: string } }).body.kind);
  expect(kinds()).toEqual(['use']);
  const inventory = { windowId: 7, kind: 'chest', title: 'Chest', slotCount: 63, containerSlots: 27, mainStart: 27, hotbarStart: 54, slots: [], cursor: null };
  await act(async () => { pending[0]?.({ ok: true, message: 'opened', state: session.state, inventory }); await Promise.resolve(); });
  expect(kinds()).toEqual(['use', 'release']);
  expect(page.result.current.inventory?.windowId).toBe(7);
  expect(page.result.current.inventoryAt).toBe(Date.now());
  await act(async () => { pending[1]?.({ ok: true, message: 'released', state: session.state }); await Promise.resolve(); });
  expect(kinds()).toEqual(['use', 'release', 'use']);
  await act(async () => { pending[2]?.({ ok: true, message: 'placed', state: session.state }); await Promise.resolve(); });
  expect(kinds()).toEqual(['use', 'release', 'use', 'release']);
  await act(async () => { pending[3]?.({ ok: true, message: 'released', state: session.state }); await Promise.resolve(); });
  page.unmount();
});

it('hard cancellation discards queued clicks and their delayed responses cannot replace inventory', async () => {
  let finish: ((reply: PawnControlActionResponse) => void) | undefined;
  const page = renderHook(() => usePawnControl('102'));
  await act(() => vi.advanceTimersByTimeAsync(1));
  api.mockImplementation((id: string, options: { body: { kind?: string } }) => id === 'bodies.controlAction' && options.body.kind === 'use'
    ? new Promise<PawnControlActionResponse>((resolve) => { finish = resolve; })
    : Promise.resolve(id === 'bodies.controlInput' ? session : { ok: true, message: '', state: session.state }));
  void page.result.current.action({ kind: 'use' });
  await act(async () => { await Promise.resolve(); });
  void page.result.current.action({ kind: 'release' });
  void page.result.current.action({ kind: 'use' });
  await act(async () => { await page.result.current.action({ kind: 'cancel' }); });
  await act(async () => {
    finish?.({ ok: true, message: 'opened old window', state: session.state,
      inventory: { windowId: 4, kind: 'chest', title: 'Old', slotCount: 63, containerSlots: 27, mainStart: 27, hotbarStart: 54, slots: [] } });
    await Promise.resolve();
  });
  expect(api.mock.calls.filter(([id]) => id === 'bodies.controlAction')
    .map(([, args]) => (args as { body: { kind: string } }).body.kind)).toEqual(['use', 'cancel']);
  expect(page.result.current.inventory).toBeNull();
  expect(page.result.current.inventoryAt).toBe(0);
  page.unmount();
});

it('flushes click look after the older heartbeat, without replaying released movement keys', async () => {
  const inputs: Array<(reply: PawnControlSession) => void> = [];
  api.mockImplementation((id: string) => id === 'bodies.controlInput'
    ? new Promise<PawnControlSession>((resolve) => { inputs.push(resolve); })
    : Promise.resolve(id === 'bodies.controlAction' ? { ok: true, message: '', state: session.state } : session));
  const page = renderHook(() => usePawnControl('102'));
  await act(() => vi.advanceTimersByTimeAsync(1));
  page.result.current.input.current.forward = 1;
  await act(() => vi.advanceTimersByTimeAsync(50));
  Object.assign(page.result.current.input.current, { yaw: 135, pitch: 12, hotbar: 4, sneak: true });
  void page.result.current.action({ kind: 'use' });
  await act(async () => { await Promise.resolve(); });
  // Keyup and another mouse movement happen before the queued click gets its turn.
  Object.assign(page.result.current.input.current, { forward: 0, jump: false, yaw: 180 });
  await act(async () => { await Promise.resolve(); });
  expect(api.mock.calls.filter(([id]) => id === 'bodies.controlInput')).toHaveLength(1);
  expect(api.mock.calls.filter(([id]) => id === 'bodies.controlAction')).toHaveLength(0);
  await act(async () => { inputs[0]?.(session); await Promise.resolve(); });
  const frames = api.mock.calls.filter(([id]) => id === 'bodies.controlInput');
  expect(frames).toHaveLength(2);
  expect((frames[1]![1] as { body: unknown }).body).toMatchObject({ seq: 2, yaw: 135, pitch: 12, hotbar: 4, sneak: true, forward: 0, jump: false });
  expect(api.mock.calls.filter(([id]) => id === 'bodies.controlAction')).toHaveLength(0);
  await act(async () => { inputs[1]?.({ ...session, state: { ...session.state, yaw: 135, pitch: 12 } }); await Promise.resolve(); });
  expect(api.mock.calls.filter(([id]) => id === 'bodies.controlAction')).toHaveLength(1);
  expect(page.result.current.pose.current?.yaw).toBe(135);
  expect(page.result.current.input.current.yaw).toBe(180);
  page.unmount();
});

it('hard cancellation while waiting for a movement acknowledgement drops the pending press', async () => {
  let finish: ((reply: PawnControlSession) => void) | undefined;
  api.mockImplementation((id: string) => id === 'bodies.controlInput'
    ? new Promise<PawnControlSession>((resolve) => { finish = resolve; })
    : Promise.resolve(id === 'bodies.controlAction' ? { ok: true, message: '', state: session.state } : session));
  const page = renderHook(() => usePawnControl('102'));
  await act(() => vi.advanceTimersByTimeAsync(51));
  void page.result.current.action({ kind: 'use' });
  await act(async () => { await Promise.resolve(); });
  await act(async () => { await page.result.current.action({ kind: 'cancel' }); });
  await act(async () => { finish?.(session); await Promise.resolve(); });
  expect(api.mock.calls.filter(([id]) => id === 'bodies.controlInput')).toHaveLength(1);
  expect(api.mock.calls.filter(([id]) => id === 'bodies.controlAction')
    .map(([, args]) => (args as { body: { kind: string } }).body.kind)).toEqual(['cancel']);
  page.unmount();
});

it('reads an opened chest independently of pending use and survives the following unlock cancel', async () => {
  let finishRead: ((reply: PawnControlActionResponse) => void) | undefined;
  const page = renderHook(() => usePawnControl('102'));
  await act(() => vi.advanceTimersByTimeAsync(1));
  api.mockImplementation((id: string, options: { body: { kind?: string } }) => {
    if (id === 'bodies.controlInput') return Promise.resolve(session);
    if (options.body.kind === 'use') return new Promise(() => {});
    if (options.body.kind === 'inventory') return new Promise<PawnControlActionResponse>((resolve) => { finishRead = resolve; });
    return Promise.resolve({ ok: true, message: '', state: session.state });
  });
  void page.result.current.action({ kind: 'use' });
  await act(async () => { await Promise.resolve(); });
  void page.result.current.action({ kind: 'inventory' });
  await act(async () => { await page.result.current.action({ kind: 'cancel' }); });
  const inventory = { windowId: 7, kind: 'chest', title: 'Chest', slotCount: 63, containerSlots: 27, mainStart: 27, hotbarStart: 54, slots: [] };
  await act(async () => { finishRead?.({ ok: true, message: '', state: session.state, inventory }); await Promise.resolve(); });
  expect(api.mock.calls.filter(([id]) => id === 'bodies.controlAction')
    .map(([, args]) => (args as { body: { kind: string } }).body.kind)).toEqual(['use', 'inventory', 'cancel']);
  expect(page.result.current.inventory?.windowId).toBe(7);
  page.unmount();
});

it('does not let an older inventory read overwrite a newer inventory click', async () => {
  let finishRead: ((reply: PawnControlActionResponse) => void) | undefined;
  const page = renderHook(() => usePawnControl('102'));
  await act(() => vi.advanceTimersByTimeAsync(1));
  const inventory = { windowId: 0, kind: 'inventory', title: 'Inventory', slotCount: 46, containerSlots: 0, mainStart: 9, hotbarStart: 36,
    slots: [], cursor: { id: 3, meta: 0, count: 12 } };
  api.mockImplementation((id: string, options: { body: { kind?: string } }) => id === 'bodies.controlAction' && options.body.kind === 'inventory'
    ? new Promise<PawnControlActionResponse>((resolve) => { finishRead = resolve; })
    : Promise.resolve({ ok: true, message: '', state: session.state, inventory }));
  void page.result.current.action({ kind: 'inventory' });
  await act(async () => { await page.result.current.action({ kind: 'inventory_click', windowId: 0, slot: 36 }); });
  await act(async () => { finishRead?.({ ok: true, message: '', state: session.state, inventory: { ...inventory, cursor: null } }); await Promise.resolve(); });
  expect(page.result.current.inventory?.cursor?.count).toBe(12);
  page.unmount();
});
