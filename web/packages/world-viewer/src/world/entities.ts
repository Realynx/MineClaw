// Timestamped stream frames use a short snapshot history; legacy sources glide over their measured update interval.
// Both paths snap teleports and reset after long silence. Bots and entities share the same clock; ages advance locally.

import type { BotDto, ChestState, Dim, EntityDelta, EntityDto, EntityEvent } from '../types';
import {
  ANCHOR_FRESH_MS,
  ANCHOR_SLACK_MS,
  delayFor,
  type MotionFigures,
  RecentFigures,
  RESET_SNAP,
  SnapshotMotion,
} from './motion';

interface Track {
  fromX: number; fromY: number; fromZ: number;
  toX: number; toY: number; toZ: number;
  fromYaw: number; toYaw: number;
  start: number;
  dur: number;
  motion?: SnapshotMotion;
}

interface EntityTrack extends Track {
  e: EntityDto;
  ageAt: number;
  ageMs: number;
}

interface BotTrack extends Track {
  b: BotDto;
  /** A sparse (AI hive) body: when its place last changed. */
  changed?: number;
}

export interface EntityView {
  key: string;
  id: number;
  dim: Dim;
  kind: string;
  name: string | null;
  type: number;
  item: number;
  count: number;
  x: number;
  y: number;
  z: number;
  yaw: number;
  /** Ticks since first seen, advanced locally (drops bob and spin). */
  ageTicks: number;
  flags: number;
  dto: EntityDto;
}

export interface BotView {
  name: string;
  dim: Dim;
  x: number;
  y: number;
  z: number;
  yaw: number;
  dto: BotDto;
}

const SNAP = 8;
/** A live body's pose age (s) up to which it dates the body's sample; the bot clamps it there, so more means "older, unknown". */
const MAX_POSE_AGE = 0.25;
/** A sparse body glides to each new place over the time since its last change, within these bounds (ms). */
const SPARSE_MIN_MS = 200;
const SPARSE_MAX_MS = 3000;
/** Channels of the ten-second figures. */
const FRAMES = 0,
  SRC = 1,
  ARR = 2,
  PAUSES = 3;
/** Frames further apart at the source than this are a silence or a fresh start, not a gap in a running stream. */
const STEP_LIMIT_MS = 1000;

function lerpAngle(a: number, b: number, k: number): number {
  let d = ((b - a) % 360 + 540) % 360 - 180;
  return a + d * k;
}

function at(t: Track, now: number): [number, number, number, number] {
  if (t.motion) return t.motion.at(now);
  const k = t.dur <= 0 ? 1 : Math.max(0, Math.min(1, (now - t.start) / t.dur));
  return [t.fromX + (t.toX - t.fromX) * k, t.fromY + (t.toY - t.fromY) * k, t.fromZ + (t.toZ - t.fromZ) * k, lerpAngle(t.fromYaw, t.toYaw, k)];
}

/** Returns the milliseconds the body was made to wait for this update (`SnapshotMotion.push`); 0 on the legacy path. */
function retarget(
  t: Track,
  x: number,
  y: number,
  z: number,
  yaw: number,
  now: number,
  dur: number,
  sampled?: number,
  reset = false,
): number {
  if (sampled !== undefined) {
    const point = { x, y, z, yaw, at: sampled };
    if (t.motion) return t.motion.push(point, now, reset, delayFor(dur));
    t.motion = new SnapshotMotion(point, now, delayFor(dur));
    return 0;
  }
  const [cx, cy, cz, cyaw] = at(t, now);
  const snap = Math.abs(x - cx) + Math.abs(y - cy) + Math.abs(z - cz) > SNAP;
  t.fromX = snap ? x : cx;
  t.fromY = snap ? y : cy;
  t.fromZ = snap ? z : cz;
  t.fromYaw = snap ? yaw : cyaw;
  t.toX = x;
  t.toY = y;
  t.toZ = z;
  t.toYaw = yaw;
  t.start = now;
  t.dur = dur;
  t.motion = undefined;
  return 0;
}

function track(x: number, y: number, z: number, yaw: number, now: number, dur: number, sampled?: number): Track {
  return {
    fromX: x,
    fromY: y,
    fromZ: z,
    toX: x,
    toY: y,
    toZ: z,
    fromYaw: yaw,
    toYaw: yaw,
    start: now,
    dur,
    ...(sampled !== undefined ? { motion: new SnapshotMotion({ x, y, z, yaw, at: sampled }, now, delayFor(dur)) } : {}),
  };
}

export class EntityStore {
  private readonly tracks = new Map<string, EntityTrack>();
  private readonly botTracks = new Map<string, BotTrack>();
  private lastUpdate = 0;
  private lastFrame: number | undefined;
  private sourceOffset: number | undefined;
  private anchoredAt = 0;
  private sourceAt: number | undefined;
  private sampled: number | undefined;
  private resetMotion = false;
  private frameAt: number | undefined;
  private readonly recent = new RecentFigures(4);
  intervalMs = 200;
  /** The revision to send as `since` next. */
  rev = 0;
  /** Bumped on every applied reply. */
  version = 0;
  /** Applied frames that came so late that a bot had to wait: its playout had run dry, and it went on from where it stood. */
  pauses = 0;
  /** Authoritative chest state travels with each frame, independently of entity revisions. */
  chests: ChestState[] | null = null;

  constructor(public clock: () => number = () => performance.now()) {}

  apply(d: EntityDelta): void {
    // Between stream frames a cache-backed source has nothing new: re-applying the same positions would restart every glide
    // and count a short interval, so motion would ease out and stall between frames.
    if (d.frame !== undefined) {
      if (d.frame === this.lastFrame) return;
      this.lastFrame = d.frame;
    }
    const now = this.clock();
    const gap = now - this.lastUpdate;
    const sourceGap = typeof d.at === 'number' && Number.isFinite(d.at) && this.sourceAt !== undefined ? d.at - this.sourceAt : gap;
    // A frame that took much less time to get here than the one the clock is anchored on: that one was old (the cache of
    // a tab that was away) or held up, and every body is drawn that much too late. Right after the anchor was taken,
    // start over from this frame. Later the bodies are in mid-stream, and starting over would make them jump: the clock
    // moves under them instead (`rebase`).
    const early =
      typeof d.at === 'number' &&
      Number.isFinite(d.at) &&
      this.sourceOffset !== undefined &&
      now - d.at < this.sourceOffset - ANCHOR_SLACK_MS;
    this.resetMotion =
      this.lastUpdate > 0 &&
      (gap > 1000 || sourceGap > 1000 || sourceGap < 0 || (early && now - this.anchoredAt <= ANCHOR_FRESH_MS));
    if (this.resetMotion) this.intervalMs = 200;
    else if (this.lastUpdate > 0 && (sourceGap > 0 || d.at === undefined)) this.intervalMs = Math.max(50, Math.min(1000, this.intervalMs * 0.7 + sourceGap * 0.3));
    if (typeof d.at === 'number' && Number.isFinite(d.at)) {
      this.resetMotion ||= this.sourceAt !== undefined && (d.at - this.sourceAt > 1000 || d.at < this.sourceAt);
      if (this.sourceOffset === undefined || this.resetMotion) {
        this.sourceOffset = now - d.at;
        this.anchoredAt = now;
      } else if (early) this.rebase(now - d.at, now);
      this.sampled = d.at + this.sourceOffset;
      this.sourceAt = d.at;
    } else this.sampled = undefined;
    this.measure(now, gap, sourceGap);
    this.lastUpdate = now;
    this.applyEntities(d, now);
    if (this.applyBots(d.bots, now)) {
      this.pauses++;
      this.recent.add(now, PAUSES);
    }
    this.chests = d.chests ?? null;
    for (const ev of d.events ?? []) this.pending.push(ev);
    if (this.pending.length > 512) this.pending.splice(0, this.pending.length - 512);
    this.rev = d.rev;
    this.version++;
  }

  /** One applied frame for the ten-second figures: its step at the source and on arrival, when it followed another. */
  private measure(now: number, gap: number, sourceGap: number): void {
    this.recent.add(now, FRAMES);
    if (this.lastUpdate <= 0 || !(sourceGap > 0 && sourceGap < STEP_LIMIT_MS)) return;
    this.recent.max(now, SRC, sourceGap);
    this.recent.max(now, ARR, gap);
  }

  /**
   * The stream over the last ten seconds, for the frame-rate overlay. Reading it: `arr` high with `src` near the frame
   * period is the relays or the network; `src` high is the bot or the bridge's world socket.
   */
  motionFigures(now: number = this.clock()): MotionFigures {
    return {
      rate: this.recent.rate(now, FRAMES),
      src: this.recent.most(now, SRC),
      arr: this.recent.most(now, ARR),
      buf: this.playoutDelay,
      pauses: this.recent.sum(now, PAUSES),
    };
  }

  /** Moves the stream clock to `offset` under every timestamped track: nothing drawn moves (`SnapshotMotion.rebase`). */
  private rebase(offset: number, now: number): void {
    const shift = offset - this.sourceOffset!;
    for (const t of this.tracks.values()) t.motion?.rebase(shift, now);
    for (const t of this.botTracks.values()) t.motion?.rebase(shift, now);
    this.sourceOffset = offset;
  }

  /** Entities glide to their new positions; a full reply drops those it doesn't list, a delta the ones it names removed. */
  private applyEntities(d: EntityDelta, now: number): void {
    const seen = d.full ? new Set<string>() : null;
    for (const e of d.entities) {
      if (e.id == null) continue;
      const k = `${e.dim}:${e.id}`;
      seen?.add(k);
      this.applyEntity(k, e, now);
    }
    if (seen) for (const k of [...this.tracks.keys()]) if (!seen.has(k)) this.tracks.delete(k);
    for (const [dim, id] of d.removed ?? []) this.tracks.delete(`${dim}:${id}`);
  }

  private applyEntity(k: string, e: EntityDto, now: number): void {
    const yaw = e.yaw ?? 0;
    const t = this.tracks.get(k);
    if (!t) {
      this.tracks.set(k, { e, ...track(e.x, e.y, e.z, yaw, now, this.intervalMs, this.sampled), ageAt: (e.age ?? 0) * 20, ageMs: now });
      return;
    }
    retarget(t, e.x, e.y, e.z, yaw, now, this.intervalMs, this.sampled, this.resetMotion);
    if (e.age != null && Math.abs(e.age * 20 - this.ticks(t, now)) > 20) {
      t.ageAt = e.age * 20;
      t.ageMs = now;
    }
    t.e = e;
  }

  /** Bots: always the full list (a bot that changed dimension starts over where it is). True when one had to wait for this frame. */
  private applyBots(bots: BotDto[], now: number): boolean {
    const names = new Set<string>();
    let waited = false;
    for (const b of bots) {
      names.add(b.name);
      const yaw = b.yaw ?? 0;
      const t = this.botTracks.get(b.name);
      const sparse = b.live === false;
      if (!t || t.b.dim !== b.dim) {
        this.botTracks.set(b.name, {
          b,
          ...track(b.x, b.y, b.z, yaw, now, this.intervalMs, sparse ? undefined : this.sampledAt(b)),
          ...(sparse ? { changed: now } : {}),
        });
        continue;
      }
      if (sparse) this.glide(t, b, yaw, now);
      else if (retarget(t, b.x, b.y, b.z, yaw, now, this.intervalMs, this.sampledAt(b), this.resetMotion) > 0)
        waited = true;
      t.b = b;
    }
    for (const k of [...this.botTracks.keys()]) if (!names.has(k)) this.botTracks.delete(k);
    return waited;
  }

  /**
   * When a live body's pose was read at its bot: the frame's time less the pose's age. Positions change every game tick
   * (50 ms) but are read once per frame, so stamping each with the frame's time puts some a tick off and the body shakes.
   */
  private sampledAt(b: BotDto): number | undefined {
    const age = b.poseAge;
    if (
      this.sampled === undefined ||
      b.live !== true ||
      typeof age !== 'number' ||
      !Number.isFinite(age) ||
      age < 0 ||
      age > MAX_POSE_AGE
    )
      return this.sampled;
    return this.sampled - age * 1000;
  }

  /**
   * An AI hive body (`live: false`) is published every frame, but its place changes only every 1 to 2.5 s. Played out
   * by the frame's time it stood, then dashed. It glides to each new place instead, over the time since its last
   * change: continuous, one update behind. After a second away, a body that went far appears where it is.
   */
  private glide(t: BotTrack, b: BotDto, yaw: number, now: number): void {
    if (b.x === t.b.x && b.y === t.b.y && b.z === t.b.z) {
      // Standing: a turn of the head shows once the last glide is over.
      if (!t.motion && yaw !== t.toYaw && now >= t.start + t.dur) retarget(t, b.x, b.y, b.z, yaw, now, this.intervalMs);
      return;
    }
    const [x, y, z] = at(t, now);
    const far = this.resetMotion && Math.abs(b.x - x) + Math.abs(b.y - y) + Math.abs(b.z - z) > RESET_SNAP;
    retarget(
      t,
      b.x,
      b.y,
      b.z,
      yaw,
      now,
      far ? 0 : Math.max(SPARSE_MIN_MS, Math.min(SPARSE_MAX_MS, now - (t.changed ?? now))),
    );
    t.changed = now;
  }

  private pending: EntityEvent[] = [];

  /** Events (swing, hurt, death, crit, eat, teleport, lightning…) received since the last call, oldest first. */
  drainEvents(): EntityEvent[] {
    const out = this.pending;
    this.pending = [];
    return out;
  }

  private ticks(t: EntityTrack, now: number): number {
    return t.ageAt + (now - t.ageMs) / 50;
  }

  /**
   * Fixes the instant that `bots()`, `bot()` and `entities()` sample until the next call, so everything drawn in one frame
   * (models, outlines, overlay marks) sees a body at the same place. Each reader used to take the clock again, and a mark
   * led its body by the work done in between. Never called: the readers follow the clock.
   */
  beginFrame(now: number = this.clock()): void {
    this.frameAt = now;
  }

  /** Every entity of `dim` (null = every dimension) at its interpolated position now (into `out`, cleared first). */
  entities(dim: Dim | null, out: EntityView[] = []): EntityView[] {
    out.length = 0;
    const now = this.frameAt ?? this.clock();
    for (const [key, t] of this.tracks) if (dim === null || t.e.dim === dim) out.push(this.viewOf(key, t, now));
    return out;
  }

  private viewOf(key: string, t: EntityTrack, now: number): EntityView {
    const e = t.e;
    const [x, y, z, yaw] = at(t, now);
    return { key, id: e.id ?? -1, dim: e.dim, kind: e.kind, name: e.name ?? null, type: e.type ?? -1, item: e.item ?? 0, count: e.count ?? 1, x, y, z, yaw, ageTicks: this.ticks(t, now), flags: e.flags ?? 0, dto: e };
  }

  bots(out: BotView[] = []): BotView[] {
    out.length = 0;
    const now = this.frameAt ?? this.clock();
    for (const t of this.botTracks.values()) {
      const [x, y, z, yaw] = at(t, now);
      out.push({ name: t.b.name, dim: t.b.dim, x, y, z, yaw, dto: t.b });
    }
    return out;
  }

  bot(nameOrShort: string): BotView | null {
    const now = this.frameAt ?? this.clock();
    for (const t of this.botTracks.values()) {
      if (t.b.name !== nameOrShort && t.b.short !== nameOrShort) continue;
      const [x, y, z, yaw] = at(t, now);
      return { name: t.b.name, dim: t.b.dim, x, y, z, yaw, dto: t.b };
    }
    return null;
  }

  get count(): number {
    return this.tracks.size;
  }

  /** How far behind the stream the bots are drawn now: the largest playout delay among them (ms; 0 without sender timestamps). */
  get playoutDelay(): number {
    let most = 0;
    for (const t of this.botTracks.values()) if (t.motion) most = Math.max(most, t.motion.behind);
    return most;
  }

  clear(): void {
    this.tracks.clear();
    this.botTracks.clear();
    this.lastUpdate = 0;
    this.lastFrame = undefined;
    this.sourceOffset = this.sourceAt = this.sampled = undefined;
    this.resetMotion = false;
    this.intervalMs = 200;
    this.rev = 0;
    this.chests = null;
    this.version++;
  }
}
