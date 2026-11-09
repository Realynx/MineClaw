import type { ChestState, EntityEvent } from '../types';

const key = (dim: number, x: number, y: number, z: number) => `${dim},${x},${y},${z}`;

/** Last authoritative lid state per chest cell; duplicate observer packets never restart the animation. */
export class ChestLidStates {
  private readonly cells = new Map<string, { open: boolean; at: number; showUntil: number }>();
  private snapshot: readonly ChestState[] | null = null;
  private opened: Set<string> | null = null;

  reconcile(snapshot: readonly ChestState[] | null): void {
    if (snapshot === this.snapshot) return;
    this.snapshot = snapshot;
    this.opened = snapshot ? new Set(snapshot.filter((c) => c.openCount > 0).map((c) => key(c.dim, c.x, c.y, c.z))) : null;
  }

  receive(events: readonly EntityEvent[], now = 0): void {
    for (const e of events) {
      if (e.kind !== 'chest_open' && e.kind !== 'chest_close') continue;
      if (typeof e.x !== 'number' || typeof e.y !== 'number' || typeof e.z !== 'number') continue;
      const k = key(e.dim ?? 0, e.x, e.y, e.z), old = this.cells.get(k);
      if (old && e.at < old.at) continue;
      this.cells.delete(k);
      const open = e.kind === 'chest_open';
      // A quick deposit can open and close within one poll. Let its opening become visible before easing closed.
      const showUntil = open && (!old || e.at > old.at && !old.open) ? now + 300 : old?.showUntil ?? 0;
      this.cells.set(k, { open, at: e.at, showUntil });
      if (this.cells.size > 4096) this.cells.delete(this.cells.keys().next().value!);
    }
  }

  open(dim: number, x: number, y: number, z: number, pair: [number, number] | null, now = Infinity): boolean {
    const here = this.cells.get(key(dim, x, y, z));
    const mate = pair ? this.cells.get(key(dim, x + pair[0], y, z + pair[1])) : undefined;
    const latest = mate && (!here || mate.at > here.at) ? mate : here;
    const open = this.opened ? this.opened.has(key(dim, x, y, z)) || !!pair && this.opened.has(key(dim, x + pair[0], y, z + pair[1])) : !!latest?.open;
    return open || Math.max(here?.showUntil ?? 0, mate?.showUntil ?? 0) > now;
  }

  clear(): void { this.cells.clear(); this.snapshot = null; this.opened = null; }
  forget(key: string): void { this.cells.delete(key); }
}

/** Vanilla opens/closes by 0.1 per game tick and eases the rendered angle cubically. */
export function chestLidStep(amount: number, open: boolean, dt: number): number {
  return Math.max(0, Math.min(1, amount + (open ? 1 : -1) * Math.max(0, dt) * 2));
}
export const chestLidAngle = (amount: number) => -(1 - (1 - amount) ** 3) * Math.PI / 2;
