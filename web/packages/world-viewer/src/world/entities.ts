// Timestamped stream frames use a short snapshot history; legacy sources glide over their measured update interval.
// Both paths snap teleports and reset after long silence. Bots and entities share the same clock; ages advance locally.

import type { BotDto, ChestState, Dim, EntityDelta, EntityDto, EntityEvent } from '../types';
import { SnapshotMotion } from './motion';

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

function lerpAngle(a: number, b: number, k: number): number {
  let d = ((b - a) % 360 + 540) % 360 - 180;
  return a + d * k;
}

function at(t: Track, now: number): [number, number, number, number] {
  if (t.motion) return t.motion.at(now);
  const k = t.dur <= 0 ? 1 : Math.max(0, Math.min(1, (now - t.start) / t.dur));
  return [t.fromX + (t.toX - t.fromX) * k, t.fromY + (t.toY - t.fromY) * k, t.fromZ + (t.toZ - t.fromZ) * k, lerpAngle(t.fromYaw, t.toYaw, k)];
}

function retarget(t: Track, x: number, y: number, z: number, yaw: number, now: number, dur: number, sampled?: number, reset = false): void {
  if (sampled !== undefined) {
    const point = { x, y, z, yaw, at: sampled };
    const delay = Math.max(100, Math.min(250, dur + 50));
    if (t.motion) t.motion.push(point, now, reset, delay);
    else t.motion = new SnapshotMotion(point, now, delay);
    return;
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
}

function track(x: number, y: number, z: number, yaw: number, now: number, dur: number, sampled?: number): Track {
  return { fromX: x, fromY: y, fromZ: z, toX: x, toY: y, toZ: z, fromYaw: yaw, toYaw: yaw, start: now, dur,
    ...(sampled !== undefined ? { motion: new SnapshotMotion({ x, y, z, yaw, at: sampled }, now, Math.max(100, Math.min(250, dur + 50))) } : {}) };
}

export class EntityStore {
  private readonly tracks = new Map<string, EntityTrack>();
  private readonly botTracks = new Map<string, BotTrack>();
  private lastUpdate = 0;
  private lastFrame: number | undefined;
  private sourceOffset: number | undefined;
  private sourceAt: number | undefined;
  private sampled: number | undefined;
  private resetMotion = false;
  intervalMs = 200;
  /** The revision to send as `since` next. */
  rev = 0;
  /** Bumped on every applied reply. */
  version = 0;
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
    this.resetMotion = this.lastUpdate > 0 && (gap > 1000 || sourceGap > 1000 || sourceGap < 0);
    if (this.resetMotion) this.intervalMs = 200;
    else if (this.lastUpdate > 0 && (sourceGap > 0 || d.at === undefined)) this.intervalMs = Math.max(50, Math.min(1000, this.intervalMs * 0.7 + sourceGap * 0.3));
    if (typeof d.at === 'number' && Number.isFinite(d.at)) {
      this.resetMotion ||= this.sourceAt !== undefined && (d.at - this.sourceAt > 1000 || d.at < this.sourceAt);
      if (this.sourceOffset === undefined || this.resetMotion) this.sourceOffset = now - d.at;
      this.sampled = d.at + this.sourceOffset;
      this.sourceAt = d.at;
    } else this.sampled = undefined;
    this.lastUpdate = now;
    this.applyEntities(d, now);
    this.applyBots(d.bots, now);
    this.chests = d.chests ?? null;
    for (const ev of d.events ?? []) this.pending.push(ev);
    if (this.pending.length > 512) this.pending.splice(0, this.pending.length - 512);
    this.rev = d.rev;
    this.version++;
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

  /** Bots: always the full list (a bot that changed dimension starts over where it is). */
  private applyBots(bots: BotDto[], now: number): void {
    const names = new Set<string>();
    for (const b of bots) {
      names.add(b.name);
      const yaw = b.yaw ?? 0;
      const t = this.botTracks.get(b.name);
      if (!t || t.b.dim !== b.dim) {
        this.botTracks.set(b.name, { b, ...track(b.x, b.y, b.z, yaw, now, this.intervalMs, this.sampled) });
        continue;
      }
      retarget(t, b.x, b.y, b.z, yaw, now, this.intervalMs, this.sampled, this.resetMotion);
      t.b = b;
    }
    for (const k of [...this.botTracks.keys()]) if (!names.has(k)) this.botTracks.delete(k);
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

  /** Every entity of `dim` (null = every dimension) at its interpolated position now (into `out`, cleared first). */
  entities(dim: Dim | null, out: EntityView[] = []): EntityView[] {
    out.length = 0;
    const now = this.clock();
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
    const now = this.clock();
    for (const t of this.botTracks.values()) {
      const [x, y, z, yaw] = at(t, now);
      out.push({ name: t.b.name, dim: t.b.dim, x, y, z, yaw, dto: t.b });
    }
    return out;
  }

  bot(nameOrShort: string): BotView | null {
    const now = this.clock();
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
