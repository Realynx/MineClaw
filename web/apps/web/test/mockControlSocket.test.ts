import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { WebSocketHandlerConnection } from 'msw';
import { toCamel } from '@mineclaw/contracts';
import { surface } from '@mineclaw/world-viewer/testing';
import { connectMockControl } from '@/mocks/controlSocket';
import { mockSignIn, pawnControl, resolvers } from '@/mocks/api';
import { advanceControlBody, type ControlBody, type ControlCollision } from '@/features/control/controlMotion';
import { InputLog, TickClock } from '@/features/control/controlReconcile';
import { idleInput } from '@/features/control/playerInput';

const LINK_KEYS = ['mcx-mock-control-lag', 'mcx-mock-control-jitter'];

beforeEach(() => { vi.useFakeTimers(); mockSignIn('fox'); });
afterEach(() => { vi.useRealTimers(); pawnControl.current = null; for (const key of LINK_KEYS) sessionStorage.removeItem(key); });

function socket(body = 'maid_chan_70', csrf?: string) {
  const target = new EventTarget();
  const sent: Array<Record<string, unknown>> = [];
  const wire: Array<Record<string, unknown>> = [];
  const arrived: number[] = [];
  const close = vi.fn(() => target.dispatchEvent(new Event('close')));
  const client = Object.assign(target, { id: 'test', url: new URL(`ws://localhost/api/bodies/${body}/control/socket`), close,
    send: (data: string) => {
      const frame = JSON.parse(data) as Record<string, unknown>;
      wire.push(frame); arrived.push(Date.now());
      sent.push(toCamel<Record<string, unknown>>(frame, { opaque: new Set<string>() }));
    } });
  const token = resolvers['auth.csrf']!({ params: {}, query: {}, body: null }) as { csrfToken: string };
  connectMockControl({ client: client as unknown as WebSocketHandlerConnection['client'], info: { protocols: ['mineclaw.control.v1', `csrf.${csrf ?? token.csrfToken}`] } });
  return { sent, wire, arrived, close, send: (data: unknown) => target.dispatchEvent(new MessageEvent('message', { data: JSON.stringify(data) })) };
}

type Lease = ReturnType<typeof pawnControl.acquire>;
/** The pawn as the newest frame on the socket describes it. */
const pose = (ws: ReturnType<typeof socket>) => (ws.sent.at(-1)!.data as Lease).state;
const keys = (session: string, seq: number, held: Partial<ReturnType<typeof idleInput>> = {}) => {
  const { hotbar: _hotbar, ...input } = { ...idleInput(), ...held };
  return { type: 'input', session, seq, ...input };
};

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

  it('shares the HTTP lease, moves the same pawn on its game tick, and returns its inventory with action correlation', () => {
    const lease = pawnControl.acquire('maid_chan_70'), ws = socket();
    ws.send({ type: 'attach', session: lease.session });
    expect(ws.sent[0]).toMatchObject({ type: 'state', data: lease });
    vi.advanceTimersByTime(50);
    ws.send({ type: 'input', session: lease.session, seq: 1, forward: 1, strafe: 0, yaw: 0, pitch: 0, jump: false, sneak: false, sprint: true, hotbar: 2 });
    // An input moves nothing by itself: like the real pawn, this one applies its keys on the next game tick.
    const waiting = pawnControl.current!.state;
    expect(waiting.z).toBeCloseTo(lease.state.z, 12); expect(waiting.heldSlot).toBe(0);
    vi.advanceTimersByTime(50);
    const moved = pose(ws);
    expect(moved.z).toBeGreaterThan(lease.state.z + 0.1); expect(moved.heldSlot).toBe(2);
    expect(moved).toEqual(pawnControl.current!.state);
    ws.send({ type: 'action', session: lease.session, seq: 1, request_id: 'inventory-1', kind: 'inventory' });
    expect(ws.sent.at(-1)).toMatchObject({ type: 'action', requestId: 'inventory-1', data: { ok: true, inventory: lease.inventory } });
    ws.close();
    expect(pawnControl.current).toBeNull();
    expect(pawnControl.release(lease.session).ok).toBe(true); // socket close and HTTP release can race
  });

  it('steps the pawn once per 50 ms tick on the newest input, and says which input each tick used and how long it waited', () => {
    // The walk below stays on two level cells of the mock island.
    expect(surface(0, 16)).toBe(surface(0, 15));
    const lease = pawnControl.acquire('maid_chan_70'), ws = socket(), start = lease.state;
    ws.send({ type: 'attach', session: lease.session });
    // No tick has run yet: nothing is acknowledged and the pawn says it is stopped.
    expect(start).toMatchObject({ y: surface(0, 15) + 1, onGround: true, stopped: true });
    for (const field of ['tick', 'ackSeq', 'ackWaitMs', 'vx', 'vy', 'vz']) expect(start).not.toHaveProperty(field);
    expect(lease.reach).toBe(4.25);
    vi.advanceTimersByTime(50);
    const first = pose(ws);
    expect(first).toMatchObject({ ackSeq: -1, ackWaitMs: 50, vx: 0, vy: 0, vz: 0, onGround: true, stopped: false });
    expect(first.z).toBeCloseTo(start.z, 12);
    // Two inputs inside one tick: nothing moves when they arrive, and the tick uses the newer one.
    vi.advanceTimersByTime(20);
    ws.send(keys(lease.session, 1, { forward: 1 }));
    ws.send(keys(lease.session, 2, { forward: 1, sprint: true }));
    expect(pawnControl.current!.state).toEqual(first);
    vi.advanceTimersByTime(30);
    const second = pose(ws);
    expect(second).toMatchObject({ tick: first.tick! + 1, ackSeq: 2, ackWaitMs: 30, onGround: true, stopped: false });
    // A sprint from rest: the first tick moves 0.13 x 0.98 blocks, and ground friction keeps 0.546 of that as speed.
    expect(second.z - first.z).toBeCloseTo(0.1274, 6);
    expect(second.vz).toBeCloseTo(0.1274 * 0.546, 6);
    // No newer input: the next tick acknowledges the same one, which has now waited a tick longer.
    vi.advanceTimersByTime(50);
    const third = pose(ws);
    expect(third).toMatchObject({ tick: first.tick! + 2, ackSeq: 2, ackWaitMs: 80, stopped: false });
    // The pawn is where the browser's own motion code puts it when stepped one whole tick at a time.
    const floor: ControlCollision = () => ({ loaded: true, boxes: [{ minX: -8, maxX: 8, minY: start.y - 1, maxY: start.y, minZ: 8, maxZ: 24 }] });
    const body: ControlBody = { x: start.x, y: start.y, z: start.z, dim: 0, vx: 0, vy: 0, vz: 0, onGround: true, height: 1.8, jumpCooldown: 0 };
    advanceControlBody(body, idleInput(180), 50, floor, { version: 'b1.8.1' });
    for (let tick = 0; tick < 2; tick++) advanceControlBody(body, { ...idleInput(), forward: 1, sprint: true }, 50, floor, { version: 'b1.8.1' });
    expect(third.z).toBeCloseTo(body.z, 12); expect(third.vz).toBeCloseTo(body.vz, 12);
    expect(third.z - second.z).toBeGreaterThan(second.z - first.z);
    // The envelope still carries when the state was sampled.
    expect(ws.wire.at(-1)).toMatchObject({ type: 'state', sampled_at_ms: Date.now(), data: { reach: 4.25, state: { ack_seq: 2, ack_wait_ms: 80, on_ground: true } } });
  });

  it('runs the ticks that fell due while nobody asked, and starts a new schedule instead of replaying a long gap', () => {
    const lease = pawnControl.acquire('maid_chan_70');
    vi.advanceTimersByTime(50);
    const first = pawnControl.snapshot(lease.session).state;
    // Two more ticks fall due, at 100 and 150 ms.
    vi.advanceTimersByTime(120);
    const early = pawnControl.snapshot(lease.session).state;
    expect(early).toMatchObject({ tick: first.tick! + 2, ackSeq: -1, ackWaitMs: 150, stopped: false });
    // An input by HTTP waits for the same tick as one by socket.
    pawnControl.input({ session: lease.session, seq: 4, forward: 1, strafe: 0, yaw: 0, pitch: 0, jump: false, sneak: false, sprint: false });
    vi.advanceTimersByTime(30);
    const used = pawnControl.snapshot(lease.session).state;
    expect(used).toMatchObject({ tick: early.tick! + 1, ackSeq: 4, ackWaitMs: 30 });
    // Forty ticks are due after two silent seconds. One runs, now, and the schedule carries on from it.
    vi.advanceTimersByTime(2000);
    const late = pawnControl.snapshot(lease.session).state;
    expect(late).toMatchObject({ tick: used.tick! + 1, ackSeq: 4, ackWaitMs: 2030, stopped: true });
    vi.advanceTimersByTime(49);
    expect(pawnControl.snapshot(lease.session).state.tick).toBe(late.tick);
    vi.advanceTimersByTime(1);
    expect(pawnControl.snapshot(lease.session).state.tick).toBe(late.tick! + 1);
  });

  it('counts keys older than the input timeout as released, and says the pawn has stopped', () => {
    const lease = pawnControl.acquire('maid_chan_70'), ws = socket();
    ws.send({ type: 'attach', session: lease.session });
    ws.send(keys(lease.session, 1, { forward: 1 }));
    vi.advanceTimersByTime(300);
    const walking = pose(ws);
    expect(walking).toMatchObject({ ackSeq: 1, ackWaitMs: 300, stopped: false });
    expect(walking.vz).toBeGreaterThan(0.1);
    vi.advanceTimersByTime(50);
    const released = pose(ws);
    expect(released).toMatchObject({ tick: walking.tick! + 1, ackSeq: 1, ackWaitMs: 350, stopped: true, yaw: 0 });
    expect(released.vz).toBeLessThan(walking.vz! * 0.6);
    vi.advanceTimersByTime(500);
    expect(pose(ws).vz).toBeCloseTo(0, 3);
    expect(pose(ws).stopped).toBe(true);
    ws.send(keys(lease.session, 2, { forward: 1 }));
    vi.advanceTimersByTime(50);
    expect(pose(ws)).toMatchObject({ ackSeq: 2, ackWaitMs: 50, stopped: false });
  });

  it("walks on the mock world's ground: a one-block rise stops the pawn, and a jump takes it up", () => {
    expect(surface(0, 14)).toBe(surface(0, 15) + 1);
    const lease = pawnControl.acquire('maid_chan_70'), ws = socket(), floor = lease.state.y;
    ws.send({ type: 'attach', session: lease.session });
    // North, straight at the rise. The pawn is 0.6 wide, so it stops with its centre 0.3 short of the block.
    ws.send(keys(lease.session, 1, { forward: 1, yaw: 180 }));
    vi.advanceTimersByTime(300);
    expect(pose(ws)).toMatchObject({ y: floor, vz: 0, onGround: true });
    expect(pose(ws).z).toBeCloseTo(15.3, 9);
    ws.send(keys(lease.session, 2, { forward: 1, yaw: 180, jump: true }));
    vi.advanceTimersByTime(100);
    expect(pose(ws).onGround).toBe(false);
    expect(pose(ws).y).toBeGreaterThan(floor + 0.7);
    vi.advanceTimersByTime(900);
    expect(pose(ws).onGround).toBe(true);
    expect(pose(ws).y).toBeCloseTo(floor + 1, 9);
    expect(pose(ws).z).toBeLessThan(15.3);
  });

  it('delivers frames late when a slow link is switched on: half the lag each way, in order', () => {
    sessionStorage.setItem('mcx-mock-control-lag', '100');
    const lease = pawnControl.acquire('maid_chan_70'), ws = socket(), t0 = Date.now();
    ws.send({ type: 'attach', session: lease.session });
    vi.advanceTimersByTime(99);
    expect(ws.wire).toHaveLength(0);
    // The attach took 50 ms to reach the pawn and its answer 50 ms to come back.
    vi.advanceTimersByTime(1);
    expect(ws.wire).toHaveLength(1);
    expect(ws.wire[0]).toMatchObject({ type: 'state', sampled_at_ms: t0 + 50, data: { inventory: { window_id: 0 } } });
    // An input sent now reaches the pawn at 150 and is used by the tick at 200, whose state is here at 250.
    ws.send(keys(lease.session, 1, { forward: 1 }));
    vi.advanceTimersByTime(149);
    expect(pose(ws)).toMatchObject({ ackSeq: -1 });
    expect(ws.wire.at(-1)).toMatchObject({ sampled_at_ms: t0 + 150 });
    vi.advanceTimersByTime(1);
    expect(pose(ws)).toMatchObject({ ackSeq: 1, ackWaitMs: 50 });
    expect(ws.wire.at(-1)).toMatchObject({ sampled_at_ms: t0 + 200 });
    expect(ws.arrived.at(-1)).toBe(t0 + 250);
    // Every frame took the same 50 ms, so none overtook another.
    ws.wire.forEach((frame, i) => expect(ws.arrived[i]! - (frame.sampled_at_ms as number)).toBe(50));
    // Ending the control also travels the link: the error frame first, then the close.
    ws.send({ type: 'action', session: 'wrong', seq: 1, request_id: 'bad', kind: 'inventory' });
    vi.advanceTimersByTime(99);
    expect(ws.close).not.toHaveBeenCalled();
    vi.advanceTimersByTime(1);
    expect(ws.wire.at(-1)).toMatchObject({ type: 'error', message: 'Wrong control session' });
    expect(ws.close).toHaveBeenCalledWith(1008, 'Control ended');
    const count = ws.wire.length; vi.advanceTimersByTime(500);
    expect(ws.wire).toHaveLength(count);
  });

  it('acknowledges inputs so that the browser can place the pawn ticks on its own clock, through a slow link', () => {
    sessionStorage.setItem('mcx-mock-control-lag', '100');
    const lease = pawnControl.acquire('maid_chan_70'), ws = socket(), clock = new TickClock(), log = new InputLog();
    ws.send({ type: 'attach', session: lease.session });
    vi.advanceTimersByTime(20);
    let read = 0, placed = 0;
    for (let seq = 1; seq <= 30; seq++) {
      log.record(seq, Date.now());
      ws.send(keys(lease.session, seq, { forward: 1 }));
      vi.advanceTimersByTime(50);
      for (; read < ws.sent.length; read++) {
        const state = (ws.sent[read]!.data as Lease).state;
        clock.observe(log.at(state.ackSeq ?? -1), state.ackWaitMs, state.tick, state.stopped);
        const at = state.tick === undefined ? null : clock.timeOf(state.tick);
        if (at === null) continue;
        // In this test a state is sampled the moment its tick runs. An input needs 50 ms to reach the pawn, so the
        // tick sits 50 ms before that on the browser's clock, plus the half tick. How long the state frame took to
        // come back does not come into it.
        expect(at).toBe((ws.wire[read]!.sampled_at_ms as number) - 50 + 25);
        placed++;
      }
    }
    expect(placed).toBeGreaterThan(20);
  });

  it('keeps frames in order when jitter makes some of them slower than the ones behind them', () => {
    sessionStorage.setItem('mcx-mock-control-jitter', '200');
    const lease = pawnControl.acquire('maid_chan_70'), ws = socket();
    ws.send({ type: 'attach', session: lease.session });
    for (let seq = 1; seq <= 40; seq++) { ws.send(keys(lease.session, seq, { forward: 1 })); vi.advanceTimersByTime(50); }
    const sampled = ws.wire.map((frame) => frame.sampled_at_ms as number);
    const delays = sampled.map((at, i) => ws.arrived[i]! - at);
    expect(sampled.length).toBeGreaterThan(30);
    expect(sampled).toEqual([...sampled].sort((a, b) => a - b));
    expect(Math.min(...delays)).toBeGreaterThanOrEqual(0);
    expect(Math.max(...delays) - Math.min(...delays)).toBeGreaterThan(50);
    // Inputs are jittered too and stay in order, so the acknowledged input never goes back.
    const acks = ws.sent.map((frame) => (frame.data as Lease).state.ackSeq ?? -1);
    expect(acks).toEqual([...acks].sort((a, b) => a - b));
    expect(acks.at(-1)).toBeGreaterThan(25);
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
