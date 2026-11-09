import type { PawnControlState } from '@mineclaw/contracts';

type Position = Pick<PawnControlState, 'x' | 'y' | 'z' | 'dim'>;
type Sample = Position & { at: number };

/** Short interpolation of confirmed positions only. A missing update never predicts walking through a wall. */
export class ControlCamera {
  private samples: Sample[] = [];
  private serverOffset: number | null = null;
  private receivedAt = 0;
  constructor(private readonly delayMs = 75) {}
  clear(): void { this.samples = []; this.serverOffset = null; }

  push(position: Position, receivedAt: number, sampledAtMs?: number): void {
    const at = sampledAtMs ?? receivedAt;
    const last = this.samples.at(-1);
    if (last && (position.dim !== last.dim || at < last.at || receivedAt - this.receivedAt > 350
      || (sampledAtMs !== undefined) !== (this.serverOffset !== null)
      || Math.hypot(position.x - last.x, position.y - last.y, position.z - last.z) > 4)) this.clear();
    // Preserve sample spacing when several frames arrive together. The smallest observed offset removes transport
    // queuing delay from the clock mapping without assuming the browser and server wall clocks agree.
    this.serverOffset = sampledAtMs === undefined ? null : Math.min(this.serverOffset ?? Infinity, receivedAt - sampledAtMs);
    this.receivedAt = receivedAt;
    const sample = { x: position.x, y: position.y, z: position.z, dim: position.dim, at };
    if (this.samples.at(-1)?.at === at) this.samples[this.samples.length - 1] = sample;
    else this.samples.push(sample);
    if (this.samples.length > 24) this.samples.shift();
  }

  position(now: number): Position | null {
    const first = this.samples[0];
    if (!first) return null;
    const target = now - (this.serverOffset ?? 0) - this.delayMs;
    while (this.samples.length > 2 && this.samples[1]!.at <= target) this.samples.shift();
    const a = this.samples[0]!, b = this.samples[1];
    if (!b || target <= a.at) return a;
    const t = Math.min(1, (target - a.at) / Math.max(1, b.at - a.at));
    return { x: a.x + (b.x - a.x) * t, y: a.y + (b.y - a.y) * t, z: a.z + (b.z - a.z) * t, dim: b.dim };
  }
}
