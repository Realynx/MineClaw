import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { WebSocketHandlerConnection } from 'msw';
import { toCamel } from '@mineclaw/contracts';
import { connectMockControl } from '@/mocks/controlSocket';
import { mockSignIn, pawnControl, resolvers } from '@/mocks/api';

beforeEach(() => { vi.useFakeTimers(); mockSignIn('fox'); });
afterEach(() => { vi.useRealTimers(); pawnControl.current = null; });

function socket(body = 'maid_chan_70', csrf?: string) {
  const target = new EventTarget();
  const sent: Array<Record<string, unknown>> = [];
  const wire: Array<Record<string, unknown>> = [];
  const close = vi.fn(() => target.dispatchEvent(new Event('close')));
  const client = Object.assign(target, { id: 'test', url: new URL(`ws://localhost/api/bodies/${body}/control/socket`), close,
    send: (data: string) => {
      const frame = JSON.parse(data) as Record<string, unknown>;
      wire.push(frame);
      sent.push(toCamel<Record<string, unknown>>(frame, { opaque: new Set<string>() }));
    } });
  const token = resolvers['auth.csrf']!({ params: {}, query: {}, body: null }) as { csrfToken: string };
  connectMockControl({ client: client as unknown as WebSocketHandlerConnection['client'], info: { protocols: ['mineclaw.control.v1', `csrf.${csrf ?? token.csrfToken}`] } });
  return { sent, wire, close, send: (data: unknown) => target.dispatchEvent(new MessageEvent('message', { data: JSON.stringify(data) })) };
}

describe('native mock control socket', () => {
  it('emits supervisor-shaped snake_case state and action payloads without opaque data conversion', () => {
    const lease = pawnControl.acquire('maid_chan_70'), ws = socket();
    ws.send({ type: 'attach', session: lease.session });
    const state = ws.wire[0]!.data as Record<string, unknown>;
    expect(ws.wire[0]).toMatchObject({ type: 'state', sampled_at_ms: Date.now() });
    expect(state).toMatchObject({ expires_at: lease.expiresAt, input_timeout_ms: 350, lease_timeout_ms: 3000,
      state: { held_slot: 0, on_ground: true },
      inventory: { window_id: 0, slot_count: 46, container_slots: 9, main_start: 9, hotbar_start: 36, cursor: null }, action: null });
    expect(state).not.toHaveProperty('expiresAt');
    expect(state.state).not.toHaveProperty('heldSlot');
    expect(state.inventory).not.toHaveProperty('windowId');
    vi.advanceTimersByTime(50);
    expect(ws.wire.at(-1)).toMatchObject({ data: { inventory: null, action: null } });
    ws.send({ type: 'action', session: lease.session, seq: 1, request_id: 'inventory-wire', kind: 'inventory' });
    expect(ws.wire.at(-1)).toMatchObject({ type: 'action', request_id: 'inventory-wire', data: { ok: true,
      state: { held_slot: 0, on_ground: true }, inventory: { window_id: 0, main_start: 9, hotbar_start: 36, cursor: null } } });
    ws.close();
  });

  it('shares the HTTP lease, advances the same pawn, and returns its inventory with action correlation', () => {
    const lease = pawnControl.acquire('maid_chan_70'), ws = socket();
    ws.send({ type: 'attach', session: lease.session });
    expect(ws.sent[0]).toMatchObject({ type: 'state', data: lease });
    vi.advanceTimersByTime(50);
    ws.send({ type: 'input', session: lease.session, seq: 1, forward: 1, strafe: 0, yaw: 0, pitch: 0, jump: false, sneak: false, sprint: true, hotbar: 2 });
    vi.advanceTimersByTime(50);
    const pose = (ws.sent.at(-1)!.data as typeof lease).state;
    expect(pose.z).toBeGreaterThan(lease.state.z); expect(pose.heldSlot).toBe(2);
    expect(pose).toEqual(pawnControl.current!.state);
    ws.send({ type: 'action', session: lease.session, seq: 1, request_id: 'inventory-1', kind: 'inventory' });
    expect(ws.sent.at(-1)).toMatchObject({ type: 'action', requestId: 'inventory-1', data: { ok: true, inventory: lease.inventory } });
    ws.close();
    expect(pawnControl.current).toBeNull();
    expect(pawnControl.release(lease.session).ok).toBe(true); // socket close and HTTP release can race
  });

  it('rejects wrong CSRF, body, or lease rather than creating a second fake control state', () => {
    const lease = pawnControl.acquire('maid_chan_70');
    expect(socket('maid_chan_70', 'wrong').close).toHaveBeenCalledWith(1008, 'Control connection refused');
    const other = socket('maid_chan_71'); other.send({ type: 'attach', session: lease.session });
    expect(other.close).toHaveBeenCalled();
    expect(pawnControl.current?.session).toBe(lease.session);
    const fresh = pawnControl.acquire('maid_chan_70'), ws = socket();
    ws.send({ type: 'attach', session: fresh.session });
    ws.send({ type: 'action', session: 'wrong', seq: 1, request_id: 'bad', kind: 'inventory' });
    expect(ws.close).toHaveBeenCalled();
  });

  it('lets a silent lease expire instead of renewing it merely by streaming state', () => {
    const lease = pawnControl.acquire('maid_chan_70'), ws = socket();
    ws.send({ type: 'attach', session: lease.session });
    vi.advanceTimersByTime(3100);
    expect(ws.close).toHaveBeenCalledWith(1008, 'Control ended');
    expect(pawnControl.current).toBeNull();
    const count = ws.sent.length; vi.advanceTimersByTime(1000);
    expect(ws.sent).toHaveLength(count);
  });
});
