/** Timestamped spectator motion. A short history absorbs delivery jitter without repeatedly restarting a glide. */
export interface MotionPoint { at: number; x: number; y: number; z: number; yaw: number }
export type MotionPose = [number, number, number, number];
const CORRECT_MS = 100;
/** Bodies are drawn one update interval plus this slack behind the stream: a frame may be that late before a body has to wait. */
const SLACK_MS = 150;
const MIN_DELAY = 200;
const MAX_STEADY_DELAY = 400;
/** A body that had to wait for a late frame may be drawn this far behind; the delay then slews back, 15 ms per update. */
const MAX_DELAY = 1000;
/** History kept: the longest delay and a few updates more (it was eight updates, 0.7 s at 10 Hz). */
const HISTORY_MS = MAX_DELAY + 300;
const MAX_POINTS = 32;
/** A continued walk is taken back after this much silence, over SETTLE_MS. */
const SETTLE_AFTER_MS = 1000;
const SETTLE_MS = 300;
/** After a second without updates, a body further than this (Manhattan) from its new place appears there instead of gliding. */
export const RESET_SNAP = 2;
/** A shorter step gives no direction to resume along. */
const MIN_STEP = 0.001;
/** A missed stretch is replayed at no less than this share of the body's own pace. */
const REPLAY_FLOOR = 0.5;
/** Updates this close together followed each other: the step between them has no silence in it and shows the body's pace. */
const STEP_MS = 250;
/** A slower step (blocks per ms) is a shuffle: it tells nothing about how fast the body walks. */
const SHUFFLE_PACE = 0.0005;
/** A walk in blocks per ms: the pace assumed of a body that has only stood or shuffled. */
const WALK_PACE = 0.0043;
/** A shorter wait is not worth moving the playout for (and not a pause anyone sees). */
const MIN_HOLD_MS = 1;
/** Frames may get here this much faster than the one the stream clock is anchored on before that anchor counts as stale. */
export const ANCHOR_SLACK_MS = 120;
/** A stale anchor found this soon after it was taken is a bad first frame: everything starts over. Later, the clock is moved under the running playout. */
export const ANCHOR_FRESH_MS = 1000;

/** The entity stream as the browser got it over the last ten seconds (the frame-rate overlay's motion line). */
export interface MotionFigures {
  /** Applied frames per second. */
  rate: number;
  /** The longest step between the sender's stamps of two frames that followed each other (ms; a silence of a second or more is no step). */
  src: number;
  /** The longest time between the arrivals of two such frames (ms). */
  arr: number;
  /** How far behind the stream the bots are drawn now (ms). */
  buf: number;
  /** Frames for which a bot had to wait. */
  pauses: number;
}

const WINDOW_S = 10;

/**
 * Figures over the last ten seconds, kept per second in a fixed ring (no allocation per update): counts add up, gaps keep
 * their longest. A second leaves the window as a whole.
 */
export class RecentFigures {
  private readonly seconds = new Float64Array(WINDOW_S).fill(-Infinity);
  private readonly values: Float64Array;
  private since = -1;

  constructor(private readonly channels: number) {
    this.values = new Float64Array(WINDOW_S * channels);
  }

  add(now: number, channel: number, n = 1): void {
    this.values[this.slot(now) + channel] += n;
  }

  max(now: number, channel: number, v: number): void {
    const i = this.slot(now) + channel;
    if (v > this.values[i]) this.values[i] = v;
  }

  sum(now: number, channel: number): number {
    const s = Math.floor(now / 1000);
    let total = 0;
    for (let i = 0; i < WINDOW_S; i++)
      if (this.seconds[i] > s - WINDOW_S && this.seconds[i] <= s) total += this.values[i * this.channels + channel];
    return total;
  }

  most(now: number, channel: number): number {
    const s = Math.floor(now / 1000);
    let most = 0;
    for (let i = 0; i < WINDOW_S; i++)
      if (this.seconds[i] > s - WINDOW_S && this.seconds[i] <= s)
        most = Math.max(most, this.values[i * this.channels + channel]);
    return most;
  }

  /** Per second over the window, or over the time since the first figure when that is shorter (at least a second). */
  rate(now: number, channel: number): number {
    if (this.since < 0) return 0;
    const start = Math.max(this.since, (Math.floor(now / 1000) - WINDOW_S + 1) * 1000);
    return (this.sum(now, channel) * 1000) / Math.max(1000, now - start);
  }

  private slot(now: number): number {
    if (this.since < 0) this.since = now;
    const s = Math.floor(now / 1000),
      i = ((s % WINDOW_S) + WINDOW_S) % WINDOW_S;
    if (this.seconds[i] !== s) {
      this.seconds[i] = s;
      this.values.fill(0, i * this.channels, (i + 1) * this.channels);
    }
    return i * this.channels;
  }
}

/** The playout delay for a stream that updates every `dur` ms (250 ms at 10 Hz). */
export function delayFor(dur: number): number {
  return Math.max(MIN_DELAY, Math.min(MAX_STEADY_DELAY, dur + SLACK_MS));
}

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

  /** How far behind the stream the body is drawn now (ms). */
  get behind(): number {
    return this.delay;
  }

  /** Returns the milliseconds the body was set back because the playout had run dry before this point came (0 when it had not). */
  push(point: MotionPoint, now: number, reset = false, delay = this.delay): number {
    const previous = this.points.at(-1)!;
    if (point.at < previous.at && !reset) return 0;
    const drawn = this.at(now),
      was = this.delay;
    this.delay = reset ? delay : this.delay + Math.max(-15, Math.min(15, delay - this.delay));
    const distance = Math.abs(point.x - previous.x) + Math.abs(point.y - previous.y) + Math.abs(point.z - previous.z);
    this.correction = [0, 0, 0, 0];
    this.received = now;
    if (distance > 8) {
      this.points = [point];
      this.vx = this.vz = 0;
      return 0;
    }
    let next = false;
    if (reset || point.at - previous.at > 1000) {
      // A fresh start (nothing heard for a second, a tab that was away): a body that went far meanwhile appears where it
      // is. A glide there would cross the distance in a dash, through walls.
      if (Math.abs(point.x - drawn[0]) + Math.abs(point.y - drawn[1]) + Math.abs(point.z - drawn[2]) > RESET_SNAP) {
        this.points = [point];
        this.vx = this.vz = 0;
        return 0;
      }
      // A quiet stream's first movement gets a normal short glide, never the whole idle interval.
      this.points = [{ at: point.at - this.delay, x: drawn[0], y: drawn[1], z: drawn[2], yaw: drawn[3] }, point];
    } else if (point.at === previous.at) this.points[this.points.length - 1] = point;
    else {
      this.points.push(point);
      next = true;
    }
    // Kept by time, not by count: the delay a late frame leaves must still find the updates it plays.
    while (this.points.length > 2 && (this.points[1].at < point.at - HISTORY_MS || this.points.length > MAX_POINTS))
      this.points.shift();
    this.updateVelocity();
    const resumed = next && now - this.delay > previous.at && this.resume(previous, point, drawn, now);
    const target = this.raw(now);
    this.correction = [
      drawn[0] - target[0],
      drawn[1] - target[1],
      drawn[2] - target[2],
      angle(target[3], drawn[3], 1) - target[3],
    ];
    this.corrected = now;
    // A resume that only gives back what this update's own slew took is no wait.
    return resumed && this.delay - was >= MIN_HOLD_MS ? this.delay - was : 0;
  }

  /**
   * The stream clock was moved by `shift` ms (its anchor had gone stale: every point was stamped that much too late).
   * The history moves with it and the delay takes the difference up, so the body stays where it is drawn; the delay
   * then slews back like any other.
   */
  rebase(shift: number, now: number): void {
    const drawn = this.at(now);
    for (const p of this.points) p.at += shift;
    this.delay = Math.min(MAX_DELAY, this.delay - shift);
    const target = this.raw(now);
    this.correction = [drawn[0] - target[0], drawn[1] - target[1], drawn[2] - target[2], angle(target[3], drawn[3], 1) - target[3]];
    this.corrected = now;
  }

  at(now: number): MotionPose {
    // A read may be earlier than the last push (a frame's instant is fixed before the frame's updates arrive): the blend
    // is then whole, never more.
    const p = this.raw(now),
      k = Math.max(0, Math.min(1, 1 - (now - this.corrected) / CORRECT_MS));
    return [
      p[0] + this.correction[0] * k,
      p[1] + this.correction[1] * k,
      p[2] + this.correction[2] * k,
      p[3] + this.correction[3] * k,
    ];
  }

  /**
   * The playout ran past `previous` before `point` came, so the body waits at (or was continued a little past) `previous`
   * while the stream is already at `point`. Catching up in the blend would be the stop-and-dash of a late frame, so the
   * playout is set back to where the body is drawn on the way to `point`: it walks on from there at its own pace, later,
   * and the delay slews back over the next updates.
   *
   * The stream is silent while nothing changes, so the first step after a quiet moment arrives the same way, as one
   * stretch over the whole silence, although the body only moved at its end. Replayed over that time it would be slow
   * motion, most of a second late. So a stretch is never replayed slower than REPLAY_FLOOR of the body's own pace, and
   * what that leaves is blended as before.
   */
  private resume(previous: MotionPoint, point: MotionPoint, drawn: MotionPose, now: number): boolean {
    const sx = point.x - previous.x,
      sy = point.y - previous.y,
      sz = point.z - previous.z;
    const length = Math.hypot(sx, sy, sz);
    if (length < MIN_STEP) return false;
    const k = Math.max(
      0,
      Math.min(
        1,
        ((drawn[0] - previous.x) * sx + (drawn[1] - previous.y) * sy + (drawn[2] - previous.z) * sz) /
          (length * length),
      ),
    );
    const left = (1 - k) * Math.min(point.at - previous.at, length / (this.pace() * REPLAY_FLOOR));
    const wanted = Math.min(MAX_DELAY, now - (point.at - left));
    if (wanted - this.delay < MIN_HOLD_MS) return false;
    this.delay = wanted;
    return true;
  }

  /** The body's own pace (blocks per ms): its fastest stored step between updates that followed each other, the newest step aside. */
  private pace(): number {
    let pace = 0;
    for (let i = this.points.length - 2; i > 0; i--) {
      const a = this.points[i - 1],
        b = this.points[i],
        dt = b.at - a.at;
      if (dt >= 20 && dt <= STEP_MS) pace = Math.max(pace, Math.hypot(b.x - a.x, b.y - a.y, b.z - a.z) / dt);
    }
    return pace < SHUFFLE_PACE ? WALK_PACE : pace;
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
    const settle = Math.max(0, 1 - Math.max(0, now - this.received - SETTLE_AFTER_MS) / SETTLE_MS);
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
