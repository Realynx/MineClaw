/**
 * The arithmetic of the takeover camera: how the browser's own prediction of the pawn is compared with the pawn's real
 * position and brought back to it without a visible pull. No DOM, no three and no clock: every time is an argument, in
 * milliseconds on one local clock.
 *
 * The picture is `body + offset`. The body is the local simulation. When the server's position for a tick differs from
 * what the simulation held at the matching moment, the body takes the whole difference at once and the opposite goes
 * into the offset, so nothing moves on screen at that instant; the offset then decays to zero. Correcting the body
 * little by little instead is what made the camera overshoot: each new sample measured an error the body had not
 * finished absorbing and applied it again.
 */

export interface Vec3 {
  x: number;
  y: number;
  z: number;
}

/** A visual offset in blocks, and its rate of change in blocks per ms. */
export interface Spring extends Vec3 {
  vx: number;
  vy: number;
  vz: number;
}

/** The simulated body at one moment. Velocity is in blocks per tick, as the simulation keeps it. */
export interface Pose extends Vec3 {
  vx: number;
  vy: number;
  vz: number;
  onGround: boolean;
}

/** One game tick. */
export const TICK_MS = 50;
/** Decay time of the visual offset: 59 % of it is gone after 100 ms, 91 % after 200 and 98 % after 300. */
export const OFFSET_TAU_MS = 50;
/** Errors up to `deadband` blocks are left alone. Beyond `snap` blocks the camera jumps instead of easing. */
export const RECONCILE_LIMITS = { deadband: 0.01, snap: 4 } as const;
/** A state frame without tick fields (an older server) is compared with the prediction this long before it arrived. */
export const MATCH_FALLBACK_MS = 75;

const CLOCK_WINDOW = 9;
/** An input that waited longer than this for its tick was held up by a stall and says nothing about the phase. */
const MAX_ACK_WAIT_MS = 200;
/** This many observations in a row, each further than `STRAY_MS` from the estimate, mean the ticks really moved. */
const STRAYS_TO_RESTART = 3;
const STRAY_MS = 100;
const INPUT_LOG_SIZE = 256;

export const restSpring = (): Spring => ({ x: 0, y: 0, z: 0, vx: 0, vy: 0, vz: 0 });

/**
 * Exact critically damped decay toward zero: x(t) = (x0 + (v0 + w * x0) * t) * e^(-w * t), with w = 1 / tauMs. Because
 * it is the exact solution, the result does not depend on how the time is cut into frames, and from rest it never
 * overshoots. `out` may be `s` itself, to decay in place.
 */
export function decaySpring(s: Spring, dtMs: number, tauMs: number, out: Spring = restSpring()): Spring {
  const t = dtMs > 0 ? dtMs : 0;
  const w = 1 / tauMs;
  const k = tauMs > 0 ? Math.exp(-w * t) : 0;
  if (k === 0) {
    // No decay time, or a step so long that nothing is left. Saying so also keeps an endless step from making NaN.
    out.x = out.y = out.z = out.vx = out.vy = out.vz = 0;
    return out;
  }
  const ax = s.vx + w * s.x;
  const ay = s.vy + w * s.y;
  const az = s.vz + w * s.z;
  out.x = (s.x + ax * t) * k;
  out.vx = (s.vx - w * ax * t) * k;
  out.y = (s.y + ay * t) * k;
  out.vy = (s.vy - w * ay * t) * k;
  out.z = (s.z + az * t) * k;
  out.vz = (s.vz - w * az * t) * k;
  return out;
}

export interface ReconcileOptions {
  deadband: number;
  snap: number;
  /** Snap whatever the distance: the pawn changed dimension or the server teleported it. */
  force?: boolean;
}

export interface Reconciled {
  /** What to add to the simulated body, and to every remembered pose of it. */
  shift: Vec3;
  offset: Spring;
  /** The camera must jump to the pawn: rebuild the body from the server's sample rather than shift it. */
  snapped: boolean;
}

/**
 * error = authority - predicted, where `predicted` is the simulation at the moment the authority's sample describes.
 * The simulation takes the whole error at once. The camera keeps its place because the opposite goes into the visual
 * offset; its rates are untouched, so position and speed on screen are continuous at the instant of the correction.
 */
export function reconcile(predicted: Vec3, authority: Vec3, offset: Spring, o: ReconcileOptions): Reconciled {
  const x = authority.x - predicted.x;
  const y = authority.y - predicted.y;
  const z = authority.z - predicted.z;
  const error = Math.hypot(x, y, z);
  if (o.force || error > o.snap) return { shift: { x, y, z }, offset: restSpring(), snapped: true };
  // Written this way round so that an error that is not a number changes nothing either.
  if (!(error > o.deadband)) return { shift: { x: 0, y: 0, z: 0 }, offset, snapped: false };
  return {
    shift: { x, y, z },
    offset: { ...offset, x: offset.x - x, y: offset.y - y, z: offset.z - z },
    snapped: false,
  };
}

/**
 * Maps a server tick to the local time whose predicted pose it must be compared with.
 *
 * The clock places a tick at the local time by which an input had to be sent for that tick to use it. An input sent at
 * `s` that then waited `w` ms at the server for tick `k` puts tick `k` at `s + w`, so the phase `s + w - 50 * k` is the
 * same for every tick, and the median of the last nine observations rides out late packets. The trip to the server is
 * inside that phase, which is right: the prediction applies an input the moment it is sent. Nothing here depends on
 * how long state frames take to come back, on frame timing or on wall clocks.
 */
export class TickClock {
  private readonly window = new Float64Array(CLOCK_WINDOW);
  private readonly sorted = new Float64Array(CLOCK_WINDOW);
  private readonly strays = new Float64Array(STRAYS_TO_RESTART);
  private count = 0;
  private next = 0;
  private strayCount = 0;
  private phase: number | null = null;

  reset(): void {
    this.count = this.next = this.strayCount = 0;
    this.phase = null;
  }

  /**
   * Learns from one state frame. `sentAt` is the local time at which the input the frame acknowledges was sent
   * (`InputLog.at(ackSeq)`). A frame that is `stopped`, acknowledges no input the log knows, or whose input waited
   * more than 200 ms for its tick teaches nothing.
   */
  observe(sentAt: number | undefined, ackWaitMs: number | undefined, tick: number | undefined, stopped = false): void {
    if (stopped || sentAt === undefined || ackWaitMs === undefined || tick === undefined) return;
    if (!(ackWaitMs >= 0 && ackWaitMs <= MAX_ACK_WAIT_MS)) return;
    const seen = sentAt + ackWaitMs - TICK_MS * tick;
    if (!Number.isFinite(seen)) return;
    if (this.phase !== null && Math.abs(seen - this.phase) > STRAY_MS) {
      // One or two strays are noise and must not move the estimate. Three in a row mean the server's ticks moved: its
      // tick loop drops a backlog after a long stall, and a new attachment starts a new sequence. Start over from
      // them instead of waiting for the window to turn over.
      this.strays[this.strayCount++] = seen;
      if (this.strayCount < STRAYS_TO_RESTART) return;
      this.count = this.next = 0;
      for (let i = 0; i < STRAYS_TO_RESTART; i++) this.keep(this.strays[i]!);
    } else this.keep(seen);
    this.strayCount = 0;
    this.phase = this.median();
  }

  /**
   * The local time to compare tick `tick` with, or null before the first observation. An input arrives anywhere inside
   * a tick and is then applied for the whole of it, so the pawn after a tick stands where the prediction stood up to
   * one tick after the tick's own time: half a tick on average. Adding that half tick centres the 0 to 50 ms that
   * cannot be known, so one start or stop leaves at most 25 ms of movement as error, with zero mean.
   */
  timeOf(tick: number): number | null {
    return this.phase === null ? null : this.phase + TICK_MS * tick + TICK_MS / 2;
  }

  private keep(phase: number): void {
    this.window[this.next] = phase;
    this.next = (this.next + 1) % CLOCK_WINDOW;
    if (this.count < CLOCK_WINDOW) this.count++;
  }

  private median(): number {
    const sorted = this.sorted;
    for (let i = 0; i < this.count; i++) {
      const value = this.window[i]!;
      let j = i;
      for (; j > 0 && sorted[j - 1]! > value; j--) sorted[j] = sorted[j - 1]!;
      sorted[j] = value;
    }
    const middle = this.count >> 1;
    return this.count % 2 ? sorted[middle]! : (sorted[middle - 1]! + sorted[middle]!) / 2;
  }
}

/** The local time to compare a state frame with: its tick's time when the clock knows it, else a fixed step back. */
export function matchTime(clock: TickClock, tick: number | undefined, receivedAt: number): number {
  const at = tick === undefined ? null : clock.timeOf(tick);
  return at ?? receivedAt - MATCH_FALLBACK_MS;
}

/** When each recent input frame was sent, by its `seq`. Remembers the last 256. */
export class InputLog {
  private readonly seqs = new Float64Array(INPUT_LOG_SIZE).fill(-1);
  private readonly times = new Float64Array(INPUT_LOG_SIZE);

  clear(): void {
    this.seqs.fill(-1);
  }

  record(seq: number, at: number): void {
    if (!Number.isInteger(seq) || seq < 0) return;
    const slot = seq % INPUT_LOG_SIZE;
    this.seqs[slot] = seq;
    this.times[slot] = at;
  }

  /** Undefined for an input that was never recorded or has been forgotten, and for the server's "none" (negative). */
  at(seq: number): number | undefined {
    if (!Number.isInteger(seq) || seq < 0) return undefined;
    const slot = seq % INPUT_LOG_SIZE;
    return this.seqs[slot] === seq ? this.times[slot] : undefined;
  }
}

interface TimedPose extends Pose {
  at: number;
}

/** The simulated body's recent past, one second by default, so a late sample can be compared with the right moment. */
export class PoseHistory {
  private readonly ring: TimedPose[];
  private head = 0;
  private count = 0;

  constructor(
    private readonly spanMs = 1000,
    capacity = 256,
  ) {
    this.ring = Array.from({ length: Math.max(2, capacity) }, () => ({
      at: 0,
      x: 0,
      y: 0,
      z: 0,
      vx: 0,
      vy: 0,
      vz: 0,
      onGround: false,
    }));
  }

  get size(): number {
    return this.count;
  }

  clear(): void {
    this.head = this.count = 0;
  }

  /**
   * Remembers the body as it is at `at`. A time that is not later than the newest entry replaces that entry: frames
   * and input events read the clock separately and may disagree by a moment, and the history must not run backwards.
   */
  push(at: number, p: Pose): void {
    let entry = this.count ? this.entry(this.count - 1) : null;
    if (!entry || at > entry.at) {
      if (this.count === this.ring.length) this.dropOldest();
      entry = this.entry(this.count++);
      entry.at = at;
      // Keep one entry at or before the start of the span, so a lookup there still has two entries to stand between.
      while (this.count > 2 && this.entry(1).at <= at - this.spanMs) this.dropOldest();
    }
    entry.x = p.x;
    entry.y = p.y;
    entry.z = p.z;
    entry.vx = p.vx;
    entry.vy = p.vy;
    entry.vz = p.vz;
    entry.onGround = p.onGround;
  }

  /**
   * The body at `time`: interpolated between the entries around it, the oldest entry for an earlier time and the
   * newest for a later one. Null while nothing has been pushed.
   */
  at(time: number): Pose | null {
    if (!this.count) return null;
    let later = this.count - 1;
    if (!(time < this.entry(later).at)) return copy(this.entry(later));
    // Lookups are for the recent past, so walk back from the newest entry.
    while (later > 0 && this.entry(later - 1).at > time) later--;
    if (later === 0) return copy(this.entry(0));
    const a = this.entry(later - 1);
    const b = this.entry(later);
    const f = (time - a.at) / (b.at - a.at);
    return {
      x: a.x + (b.x - a.x) * f,
      y: a.y + (b.y - a.y) * f,
      z: a.z + (b.z - a.z) * f,
      vx: a.vx + (b.vx - a.vx) * f,
      vy: a.vy + (b.vy - a.vy) * f,
      vz: a.vz + (b.vz - a.vz) * f,
      onGround: f < 0.5 ? a.onGround : b.onGround,
    };
  }

  /**
   * Moves every remembered pose by `d`, and its velocity by `dv` when given. A correction applied to the body must be
   * applied here too, or the next sample would be compared with a past that never had it and the same error would be
   * corrected twice.
   */
  shift(d: Vec3, dv?: Vec3): void {
    for (let n = 0; n < this.count; n++) {
      const entry = this.entry(n);
      entry.x += d.x;
      entry.y += d.y;
      entry.z += d.z;
      if (!dv) continue;
      entry.vx += dv.x;
      entry.vy += dv.y;
      entry.vz += dv.z;
    }
  }

  /** Entry `n`, counted from the oldest. */
  private entry(n: number): TimedPose {
    return this.ring[(this.head + n) % this.ring.length]!;
  }

  private dropOldest(): void {
    this.head = (this.head + 1) % this.ring.length;
    this.count--;
  }
}

function copy(p: Pose): Pose {
  return { x: p.x, y: p.y, z: p.z, vx: p.vx, vy: p.vy, vz: p.vz, onGround: p.onGround };
}
