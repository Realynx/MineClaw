/** Timestamped spectator motion. A short history absorbs delivery jitter without repeatedly restarting a glide. */
export interface MotionPoint { at: number; x: number; y: number; z: number; yaw: number }
export type MotionPose = [number, number, number, number];
const CORRECT_MS = 100;

function angle(a: number, b: number, k: number): number {
  return a + (((b - a) % 360 + 540) % 360 - 180) * k;
}

export class SnapshotMotion {
  private points: MotionPoint[];
  private received: number;
  private correction: MotionPose = [0, 0, 0, 0];
  private corrected = 0;
  private vx = 0;
  private vz = 0;

  constructor(point: MotionPoint, now: number, private delay = 250) { this.points = [point]; this.received = now; }

  push(point: MotionPoint, now: number, reset = false, delay = this.delay): void {
    const previous = this.points.at(-1)!;
    if (point.at < previous.at && !reset) return;
    const drawn = this.at(now);
    this.delay = reset ? delay : this.delay + Math.max(-15, Math.min(15, delay - this.delay));
    const distance = Math.abs(point.x - previous.x) + Math.abs(point.y - previous.y) + Math.abs(point.z - previous.z);
    this.correction = [0, 0, 0, 0];
    this.received = now;
    if (distance > 8) { this.points = [point]; this.vx = this.vz = 0; return; }
    if (reset || point.at - previous.at > 1000) {
      // A quiet stream's first movement gets a normal short glide, never the whole idle interval.
      this.points = [{ at: point.at - this.delay, x: drawn[0], y: drawn[1], z: drawn[2], yaw: drawn[3] }, point];
    } else if (point.at === previous.at) this.points[this.points.length - 1] = point;
    else this.points.push(point);
    if (this.points.length > 8) this.points.shift();
    this.updateVelocity();
    const target = this.raw(now);
    this.correction = [drawn[0] - target[0], drawn[1] - target[1], drawn[2] - target[2], angle(target[3], drawn[3], 1) - target[3]];
    this.corrected = now;
  }

  at(now: number): MotionPose {
    const p = this.raw(now), k = Math.max(0, 1 - (now - this.corrected) / CORRECT_MS);
    return [p[0] + this.correction[0] * k, p[1] + this.correction[1] * k, p[2] + this.correction[2] * k, p[3] + this.correction[3] * k];
  }

  private raw(now: number): MotionPose {
    const at = now - this.delay, first = this.points[0], last = this.points.at(-1)!;
    if (at <= first.at) return [first.x, first.y, first.z, first.yaw];
    for (let i = 1; i < this.points.length; i++) {
      const b = this.points[i], a = this.points[i - 1];
      if (at > b.at) continue;
      const k = Math.max(0, Math.min(1, (at - a.at) / Math.max(1, b.at - a.at)));
      return [a.x + (b.x - a.x) * k, a.y + (b.y - a.y) * k, a.z + (b.z - a.z) * k, angle(a.yaw, b.yaw, k)];
    }
    // Continue only an established straight, level walk, by at most 100 ms / 0.4 blocks. Never predict jumps/falls.
    // If the stream falls silent, settle back to its actual last point; predictions cannot leave a pawn in a wall.
    const settle = Math.max(0, 1 - Math.max(0, now - this.received - 450) / 150);
    const extra = Math.min(100, Math.max(0, at - last.at)) * settle;
    return [last.x + this.vx * extra, last.y, last.z + this.vz * extra, last.yaw];
  }

  private updateVelocity(): void {
    this.vx = this.vz = 0;
    if (this.points.length < 3) return;
    const [a, b, c] = this.points.slice(-3);
    const dt = c.at - b.at, oldDt = b.at - a.at;
    if (dt < 20 || dt > 500 || oldDt < 20 || oldDt > 500 || Math.abs(c.y - b.y) > 0.01 || Math.abs(b.y - a.y) > 0.01) return;
    const vx = (c.x - b.x) / dt, vz = (c.z - b.z) / dt;
    const ox = (b.x - a.x) / oldDt, oz = (b.z - a.z) / oldDt;
    const speed = Math.hypot(vx, vz), oldSpeed = Math.hypot(ox, oz);
    if (speed < 0.0001 || speed > 0.008 || oldSpeed < 0.0001 || speed / oldSpeed < 0.75 || speed / oldSpeed > 1.25 || (vx * ox + vz * oz) / (speed * oldSpeed) < 0.95) return;
    const scale = Math.min(1, 0.004 / speed);
    this.vx = vx * scale;
    this.vz = vz * scale;
  }
}
