/** UI exercise fixture. Real gameplay and physics belong to MinecraftClient. */
import type { PawnControlActionRequest, PawnControlInputRequest, PawnControlSession } from '@mineclaw/contracts';

export class MockPawnControl {
  current: PawnControlSession | null = null;
  private last = 0;
  private released: { session: string; state: PawnControlSession['state'] } | null = null;
  acquire(body: string) {
    this.last = Date.now();
    this.current = {
      session: crypto.randomUUID(), body, expiresAt: new Date(this.last + 3000).toISOString(), inputTimeoutMs: 350, leaseTimeoutMs: 3000,
      state: { x: 0.5, y: 69, z: 15.5, yaw: 180, pitch: 0, dim: 0, hp: 20, food: 20, heldSlot: 0, onGround: true },
      inventory: { windowId: 0, kind: 'inventory', title: 'Inventory', slotCount: 46, containerSlots: 9, mainStart: 9, hotbarStart: 36,
        slots: [{ slot: 36, id: 257, meta: 0, count: 1 }, { slot: 37, id: 5, meta: 0, count: 32 }, { slot: 38, id: 349, meta: 0, count: 3 }], cursor: null },
    };
    return structuredClone(this.current);
  }
  input(request: PawnControlInputRequest) {
    const current = this.require(request.session), now = Date.now();
    const dt = Math.min(0.35, (now - this.last) / 1000), yaw = request.yaw * Math.PI / 180;
    this.last = now;
    const speed = request.sneak ? 1.3 : request.sprint ? 5.6 : 4.3;
    current.state.x += (-Math.sin(yaw) * request.forward + Math.cos(yaw) * request.strafe) * speed * dt;
    current.state.z += (Math.cos(yaw) * request.forward + Math.sin(yaw) * request.strafe) * speed * dt;
    Object.assign(current.state, { yaw: request.yaw, pitch: request.pitch, heldSlot: request.hotbar ?? current.state.heldSlot });
    current.expiresAt = new Date(now + 3000).toISOString();
    return structuredClone(current);
  }
  action(request: PawnControlActionRequest) {
    const current = this.require(request.session), inv = current.inventory!;
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
  snapshot(session: string) { return structuredClone(this.require(session)); }
  private require(session: string) {
    if (!this.current || this.current.session !== session) throw new Error('Control session ended');
    return this.current;
  }
}
