/** UI exercise fixture: the pawn moves on a 50 ms game tick, like the real one. Real gameplay belongs to MinecraftClient. */
import type { PawnControlActionRequest, PawnControlInputRequest, PawnControlSession, PawnControlState } from '@mineclaw/contracts';
import { surface } from '@mineclaw/world-viewer/testing';
import { advanceControlBody, type ControlBody, type ControlBox, type ControlCollision } from '@/features/control/controlMotion';
import { idleInput, type PlayerInput } from '@/features/control/playerInput';

const TICK_MS = 50;
const START = { x: 0.5, y: surface(0, 15) + 1, z: 15.5 };
const standing = (): ControlBody => ({ ...START, dim: 0, vx: 0, vy: 0, vz: 0, onGround: true, height: 1.8, jumpCooldown: 0 });

/** The mock world's ground under a box: every cell is a solid column up to the island's surface, so the pawn meets the
 * same slopes as the browser's own prediction. Trees, water and whatever was built are not in it: it walks through them. */
const ground: ControlCollision = (box) => {
  const boxes: ControlBox[] = [];
  for (let x = Math.floor(box.minX); x <= Math.floor(box.maxX); x++)
    for (let z = Math.floor(box.minZ); z <= Math.floor(box.maxZ); z++)
      boxes.push({ minX: x, maxX: x + 1, minY: 0, maxY: surface(x, z) + 1, minZ: z, maxZ: z + 1 });
  return { loaded: true, boxes };
};

export class MockPawnControl {
  current: PawnControlSession | null = null;
  private released: { session: string; state: PawnControlSession['state'] } | null = null;
  private body = standing();
  /** The newest input received, its `seq` (negative: none yet) and when it arrived. */
  private keys: PlayerInput = idleInput();
  private keysSeq = -1;
  private keysAt = 0;
  /** Game ticks run so far. Like the real client's counter it carries on from one takeover to the next. */
  private ticks = 0;
  /** Tick n falls due at `epoch + n * TICK_MS`. */
  private epoch = 0;
  acquire(body: string) {
    const now = Date.now();
    this.body = standing();
    this.keys = idleInput(180); this.keysSeq = -1; this.keysAt = now;
    this.epoch = now - this.ticks * TICK_MS;
    this.current = {
      session: crypto.randomUUID(), body, expiresAt: new Date(now + 3000).toISOString(), inputTimeoutMs: 350, leaseTimeoutMs: 3000,
      // No game tick has run for this takeover yet, so there are no tick fields and the pawn says it is stopped.
      state: { ...START, yaw: 180, pitch: 0, dim: 0, hp: 20, food: 20, heldSlot: 0, onGround: true, stopped: true },
      inventory: { windowId: 0, kind: 'inventory', title: 'Inventory', slotCount: 46, containerSlots: 9, mainStart: 9, hotbarStart: 36,
        slots: [{ slot: 36, id: 257, meta: 0, count: 1 }, { slot: 37, id: 5, meta: 0, count: 32 }, { slot: 38, id: 349, meta: 0, count: 3 }], cursor: null },
      reach: 4.25,
    };
    return structuredClone(this.current);
  }
  /** Keeps the input for the next game tick. Nothing moves here: the real client applies its keys on the tick too. */
  input(request: PawnControlInputRequest) {
    const current = this.require(request.session), now = Date.now();
    // Ticks that fell due before this input arrived ran on the older one.
    this.catchUp(now);
    const { session: _session, seq, hotbar = this.keys.hotbar, ...keys } = request;
    this.keys = { ...keys, hotbar }; this.keysSeq = seq; this.keysAt = now;
    current.expiresAt = new Date(now + 3000).toISOString();
    return structuredClone(current);
  }
  action(request: PawnControlActionRequest) {
    const current = this.require(request.session), inv = current.inventory!;
    this.catchUp(Date.now());
    if (request.kind === 'inventory_click' && request.slot !== undefined && request.slot >= 0) {
      const i = inv.slots.findIndex((s) => s.slot === request.slot), previous = i < 0 ? null : inv.slots.splice(i, 1)[0]!;
      if (inv.cursor) inv.slots.push({ ...inv.cursor, slot: request.slot });
      inv.cursor = previous;
    }
    return structuredClone({ ok: true, message: '', state: current.state, inventory: inv });
  }
  release(session: string) {
    if (this.released?.session === session) return structuredClone({ ok: true, state: this.released.state });
    const current = this.require(session); this.current = null;
    this.released = { session, state: structuredClone(current.state) };
    return { ok: true, state: current.state };
  }
  snapshot(session: string) {
    const current = this.require(session);
    this.catchUp(Date.now());
    return structuredClone(current);
  }
  private require(session: string) {
    if (!this.current || this.current.session !== session) throw new Error('Control session ended');
    return this.current;
  }
  /** Runs the game ticks that have fallen due. Like the real tick loop it keeps a fixed 50 ms schedule, and when it is
   * far behind (a throttled tab) it starts a new schedule instead of replaying the backlog. */
  private catchUp(now: number): void {
    if (!this.current) return;
    let due = Math.floor((now - this.epoch) / TICK_MS) - this.ticks;
    if (due > 10) { this.epoch = now - (this.ticks + 1) * TICK_MS; due = 1; }
    while (due-- > 0) this.step(this.epoch + (this.ticks + 1) * TICK_MS);
  }
  /** One game tick, due at `at`: the newest input moves the body, and the state becomes that tick's sample. */
  private step(at: number): void {
    const { state, inputTimeoutMs } = this.current!, waited = at - this.keysAt;
    // The deadman: keys older than the input timeout count as released. The look stays.
    const stopped = waited >= inputTimeoutMs;
    const keys = stopped ? idleInput(this.keys.yaw, this.keys.pitch, this.keys.hotbar) : this.keys;
    // The mock world is a b1.8.1 world (mocks/data/live.ts), so the pawn moves by its rules.
    advanceControlBody(this.body, keys, TICK_MS, ground, { version: 'b1.8.1' });
    const { x, y, z, vx, vy, vz, onGround } = this.body;
    const sample: Partial<PawnControlState> = { x, y, z, vx, vy, vz, onGround, yaw: keys.yaw, pitch: keys.pitch,
      heldSlot: keys.hotbar, tick: ++this.ticks, ackSeq: this.keysSeq, ackWaitMs: waited, stopped };
    Object.assign(state, sample);
  }
}
