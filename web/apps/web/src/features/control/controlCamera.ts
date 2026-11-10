import type { PawnControlState } from '@mineclaw/contracts';
import { advanceControlBody, type ControlBody, type ControlCollision, type ControlRules } from './controlMotion';
import {
  decaySpring,
  InputLog,
  matchTime,
  OFFSET_TAU_MS,
  PoseHistory,
  RECONCILE_LIMITS,
  reconcile,
  restSpring,
  TickClock,
  type Pose,
  type Spring,
  type Vec3,
} from './controlReconcile';
import { idleInput, type PlayerInput } from './playerInput';

type Position = Pick<PawnControlState, 'x' | 'y' | 'z' | 'dim'>;
/** A state frame as the camera reads it: the position is required, everything else may be missing (an older server). */
export type ControlSample = Position & Partial<PawnControlState>;

/** What the takeover's stats line shows: how far the prediction is from the pawn and why. */
export interface ControlCameraStats {
  /** Smoothed time from the moment a state frame describes to the frame that uses it, in ms. */
  latencyMs: number;
  /** The visual offset now, and its 95th percentile over the last 120 frames, in blocks. */
  offset: number;
  offsetP95: number;
  /** Jumps to the pawn (more than 4 blocks off, a dimension change or a server teleport). */
  snaps: number;
  /** Times the stream fell so far behind that the prediction was eased to a stop. */
  holds: number;
  /** State frames dropped because they repeated a tick already seen. */
  duplicates: number;
  /** Frames in which the prediction had no terrain to move through. */
  terrainMisses: number;
  /** State frames used, and how many of them were matched by their tick rather than by their arrival time. */
  samples: number;
  ticked: number;
}

const SUBSTEP_MS = 10;
/** A longer gap between calls is not integrated: the body waits and the next state frame corrects it. */
const MAX_ADVANCE_MS = 1000;
/** A stream this much later than usual eases the prediction from full speed to a stop. */
const HOLD_FROM_MS = 300;
const HOLD_UNTIL_MS = 500;
const QUEUE_LIMIT = 64;
const LATENCY_SMOOTHING = 0.1;
/** A rise this large inside one substep is a step up: the picture eases up instead of popping. */
const STEP_RISE = 0.2;
/** onGround is taken from the server after this many disagreements in a row. */
const GROUND_VOTES = 2;
const OFFSET_WINDOW = 120;

interface Queued {
  sample: ControlSample;
  at: number;
}

const EPS = 1e-7;

/**
 * How far down (a negative number, at most `shift.y`) the body may be shifted before its feet meet the top of a solid
 * box, once its horizontal shift is applied. Boxes the body is already inside are ignored, so stale terrain can hold
 * the body up for as long as it is stale but never trap it.
 */
function groundClip(body: ControlBody, shift: Vec3, query: ControlCollision): number {
  const minX = body.x + shift.x - 0.3,
    maxX = body.x + shift.x + 0.3;
  const minZ = body.z + shift.z - 0.3,
    maxZ = body.z + shift.z + 0.3;
  const terrain = query({ minX, minY: body.y + shift.y, minZ, maxX, maxY: body.y + body.height, maxZ }, body.dim);
  if (!terrain.loaded) return shift.y;
  let dy = shift.y;
  for (const b of terrain.boxes) {
    if (maxX <= b.minX + EPS || minX >= b.maxX - EPS || maxZ <= b.minZ + EPS || minZ >= b.maxZ - EPS) continue;
    if (body.y >= b.maxY - EPS) dy = Math.max(dy, b.maxY - body.y);
  }
  return dy;
}

const sameInput = (a: PlayerInput, b: PlayerInput) =>
  a.forward === b.forward &&
  a.strafe === b.strafe &&
  a.yaw === b.yaw &&
  a.pitch === b.pitch &&
  a.jump === b.jump &&
  a.sneak === b.sneak &&
  a.sprint === b.sprint &&
  a.hotbar === b.hotbar;

/**
 * The controlled pawn's camera: a local simulation that moves at once on a key press, drawn at `body + offset`.
 * Each state frame is compared with what the simulation held at the moment that frame describes (its tick, placed on the
 * local clock by the input it acknowledges). The body takes the whole difference at once and the visual offset the
 * opposite, which then decays, so a correction never overshoots and never moves the picture in one frame. See
 * controlReconcile.ts for the arithmetic.
 *
 * Only `setInput` (to the event's time) and `position` (to the frame's time) advance the body. `push` only queues, so a
 * frame stamped by its message handler cannot interleave with the integration of a render frame.
 */
export class ControlCamera {
  private body: ControlBody | null = null;
  private input: PlayerInput = idleInput();
  private query: ControlCollision | null = null;
  /** The browser stopped the keys (inventory open, mouse released): hold the body and follow the frames. */
  private paused = false;
  /** The newest frame said the pawn does not follow the keys (deadman, server stop, riding): the same. */
  private serverStopped = false;
  private terrainKnown = false;
  /** The local time the body has been integrated to. */
  private at = 0;
  private readonly history = new PoseHistory();
  private offset: Spring = restSpring();
  private readonly clock = new TickClock();
  private readonly log = new InputLog();
  private queue: Queued[] = [];
  private newestTick: number | undefined;
  private teleports: number | undefined;
  private impulses: number | undefined;
  /** The version, food, effects and item use the prediction moves by (one object, changed in place). */
  private readonly rules: ControlRules = { food: 20 };
  private groundVotes = 0;
  /** The moment the newest used frame describes, and how long after that moment a frame is usually used. */
  private newestMatch: number | null = null;
  private expectedLag = 0;
  private holding = false;
  private readonly offsets = new Float32Array(OFFSET_WINDOW);
  private offsetCount = 0;
  private offsetNext = 0;
  private counters = { snaps: 0, holds: 0, duplicates: 0, terrainMisses: 0, samples: 0, ticked: 0 };

  clear(): void {
    this.body = this.query = null;
    this.input = idleInput();
    this.paused = this.serverStopped = this.terrainKnown = this.holding = false;
    this.at = this.expectedLag = this.groundVotes = this.offsetCount = this.offsetNext = 0;
    this.history.clear();
    this.offset = restSpring();
    this.clock.reset();
    this.log.clear();
    this.queue = [];
    this.newestTick = this.teleports = this.impulses = undefined;
    this.rules.version = this.rules.effects = this.rules.usingItem = undefined;
    this.rules.food = 20;
    this.newestMatch = null;
    this.counters = { snaps: 0, holds: 0, duplicates: 0, terrainMisses: 0, samples: 0, ticked: 0 };
  }

  /** Queues a state frame for the next `position`. A frame repeating the tick of the one before it is dropped. */
  push(sample: ControlSample, receivedAt: number): void {
    if (sample.tick !== undefined) {
      if (sample.tick === this.newestTick) {
        this.counters.duplicates++;
        return;
      }
      this.newestTick = sample.tick;
    }
    if (this.queue.length >= QUEUE_LIMIT) this.queue.shift();
    this.queue.push({ sample, at: receivedAt });
  }

  /** The input frame `seq` left at local time `at`: an acknowledgement of it places the server's ticks on this clock. */
  sent(seq: number, at: number): void {
    this.log.record(seq, at);
  }

  /** Called at the input event, so a key transition cannot retroactively move the previous frame's interval. */
  setInput(input: PlayerInput, now: number, stop = false): void {
    this.advance(now);
    this.input = { ...input };
    if (stop && this.body) this.body.vx = this.body.vy = this.body.vz = 0;
    this.paused = stop;
  }

  /** `version` is the world's game version (the movement rules differ); absent keeps the one given last. */
  position(now: number, input?: PlayerInput, query?: ControlCollision, version?: string | null): Position | null {
    if (query) this.query = query;
    if (version) this.rules.version = version;
    if (input && !sameInput(input, this.input)) this.setInput(input, now);
    this.advance(now);
    this.useQueued();
    const body = this.body;
    if (!body) return null;
    const o = this.offset;
    this.offsets[this.offsetNext] = Math.hypot(o.x, o.y, o.z);
    this.offsetNext = (this.offsetNext + 1) % OFFSET_WINDOW;
    if (this.offsetCount < OFFSET_WINDOW) this.offsetCount++;
    return { x: body.x + o.x, y: body.y + o.y, z: body.z + o.z, dim: body.dim };
  }

  /** Whether the predicted body stands on the ground (false before the first frame). */
  onGround(): boolean {
    return this.body?.onGround ?? false;
  }

  stats(): ControlCameraStats {
    const recent = Array.from(this.offsets.subarray(0, this.offsetCount)).sort((a, b) => a - b);
    const o = this.offset;
    return {
      latencyMs: this.expectedLag,
      offset: Math.hypot(o.x, o.y, o.z),
      offsetP95: recent.length ? recent[Math.min(recent.length - 1, Math.floor(recent.length * 0.95))]! : 0,
      ...this.counters,
    };
  }

  /** Integrates the body to `now` in substeps of at most 10 ms, remembering each pose, and decays the offset. */
  private advance(now: number): void {
    if (!(now > this.at)) return;
    const from = Math.max(this.at, now - MAX_ADVANCE_MS);
    decaySpring(this.offset, now - this.at, OFFSET_TAU_MS, this.offset);
    this.at = now;
    const body = this.body;
    if (!body) return;
    const simulate = !this.paused && !this.serverStopped;
    let missed = false;
    for (let t = from; t < now;) {
      const ms = Math.min(SUBSTEP_MS, now - t);
      t += ms;
      const dt = ms * this.timeScale(t);
      if (simulate && dt > 0) {
        const y = body.y;
        if (this.query && advanceControlBody(body, this.input, dt, this.query, this.rules)) {
          this.terrainKnown = true;
          if (body.y - y >= STEP_RISE) this.offset.y -= body.y - y;
        } else {
          this.terrainKnown = false;
          missed = true;
        }
      }
      this.history.push(t, body);
    }
    if (missed) this.counters.terrainMisses++;
  }

  /** 1 while frames arrive as usual; eases to 0 when the newest one is 300 to 500 ms later than usual. */
  private timeScale(t: number): number {
    if (this.newestMatch === null) return 1;
    const late = t - this.newestMatch - this.expectedLag;
    if (late <= HOLD_FROM_MS) {
      this.holding = false;
      return 1;
    }
    if (!this.holding) {
      this.holding = true;
      this.counters.holds++;
    }
    if (late >= HOLD_UNTIL_MS) return 0;
    const u = (late - HOLD_FROM_MS) / (HOLD_UNTIL_MS - HOLD_FROM_MS);
    return 1 - u * u * (3 - 2 * u);
  }

  private useQueued(): void {
    if (!this.queue.length) return;
    const now = this.at;
    let newest: number | null = null;
    for (const { sample, at } of this.queue) newest = this.use(sample, at, now);
    this.queue.length = 0;
    if (newest === null) return;
    const lag = now - newest;
    this.expectedLag =
      this.newestMatch === null ? lag : this.expectedLag + (lag - this.expectedLag) * LATENCY_SMOOTHING;
    this.newestMatch = newest;
  }

  /** Reconciles one state frame with the body; returns the local time the frame describes. */
  private use(s: ControlSample, receivedAt: number, now: number): number {
    this.counters.samples++;
    this.clock.observe(s.ackSeq === undefined ? undefined : this.log.at(s.ackSeq), s.ackWaitMs, s.tick, s.stopped);
    const ticked = s.tick !== undefined && this.clock.timeOf(s.tick) !== null;
    if (ticked) this.counters.ticked++;
    const described = matchTime(this.clock, s.tick, receivedAt);
    const teleported = s.teleports !== undefined && this.teleports !== undefined && s.teleports !== this.teleports;
    const pushed = s.impulses !== undefined && this.impulses !== undefined && s.impulses !== this.impulses;
    if (s.teleports !== undefined) this.teleports = s.teleports;
    if (s.impulses !== undefined) this.impulses = s.impulses;
    if (s.food !== undefined) this.rules.food = s.food;
    if (s.effects !== undefined) this.rules.effects = s.effects;
    if (s.usingItem !== undefined) this.rules.usingItem = s.usingItem;
    this.serverStopped = s.stopped === true;
    const body = this.body;
    if (!body) {
      this.rebuild(s, now);
      return described;
    }
    // While the body is held, or cannot move for lack of terrain, its past says nothing: compare with it as it is.
    const following = this.paused || this.serverStopped || !this.terrainKnown;
    const predicted: Pose = following ? body : (this.history.at(Math.min(described, now)) ?? body);
    const r = reconcile(predicted, s, this.offset, { ...RECONCILE_LIMITS, force: teleported || s.dim !== body.dim });
    if (r.snapped) {
      this.counters.snaps++;
      this.rebuild(s, now);
      return described;
    }
    const shift = r.shift;
    if (shift.y < 0 && this.query) {
      // A late sample can still say "lower" after the body has landed (the ±25 ms of the tick clock is most of a block
      // at the end of a long fall). Shifts are otherwise unchecked, but this one must not sink the body into the ground:
      // the part that would is dropped, from the offset too, and the body stays standing where it is.
      const allowed = groundClip(body, shift, this.query);
      r.offset.y -= allowed - shift.y;
      shift.y = allowed;
    }
    this.offset = r.offset;
    body.x += shift.x;
    body.y += shift.y;
    body.z += shift.z;
    // The velocity is taken from the server only while the body is not predicting (so it resumes at the pawn's speed),
    // or when something outside pushed the pawn. A general "velocity differs" rule would cancel a jump the server has
    // not applied yet.
    let dv: Vec3 | undefined;
    const velocity = s.vx !== undefined && s.vy !== undefined && s.vz !== undefined;
    if (following && (velocity || this.serverStopped)) {
      body.vx = s.vx ?? 0;
      body.vy = s.vy ?? 0;
      body.vz = s.vz ?? 0;
    } else if (pushed && velocity) {
      dv = { x: (s.vx ?? 0) - predicted.vx, y: (s.vy ?? 0) - predicted.vy, z: (s.vz ?? 0) - predicted.vz };
      body.vx += dv.x;
      body.vy += dv.y;
      body.vz += dv.z;
    }
    if (shift.x || shift.y || shift.z || dv) this.history.shift(shift, dv);
    if (s.onGround !== undefined && s.onGround !== predicted.onGround) {
      if (++this.groundVotes >= GROUND_VOTES) {
        body.onGround = s.onGround;
        this.groundVotes = 0;
      }
    } else this.groundVotes = 0;
    return described;
  }

  /** Puts the body where the frame says, with its velocity, and forgets the past it no longer belongs to. */
  private rebuild(s: ControlSample, now: number): void {
    this.body = {
      x: s.x,
      y: s.y,
      z: s.z,
      dim: s.dim,
      vx: s.vx ?? 0,
      vy: s.vy ?? 0,
      vz: s.vz ?? 0,
      onGround: s.onGround ?? true,
      height: this.body?.height ?? 1.8,
      jumpCooldown: 0,
    };
    this.offset = restSpring();
    this.groundVotes = 0;
    this.history.clear();
    this.history.push(now, this.body);
  }
}
