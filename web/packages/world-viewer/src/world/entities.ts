// The merged entity set fed by the delta stream (tools/MemoryExplorer/LiveHive/EntityStore.cs): every update starts a glide
// from where the entity is drawn now to the new position over the measured update interval (EWMA, 50..1000 ms), so 5 Hz
// polls read as smooth motion; a jump of more than 8 blocks snaps. Bots glide the same way. Ages advance locally.

import type { BotDto, Dim, EntityDelta, EntityDto } from '../types';

interface Track {
  fromX: number; fromY: number; fromZ: number;
  toX: number; toY: number; toZ: number;
  fromYaw: number; toYaw: number;
  start: number;
  dur: number;
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
  const k = t.dur <= 0 ? 1 : Math.max(0, Math.min(1, (now - t.start) / t.dur));
  return [t.fromX + (t.toX - t.fromX) * k, t.fromY + (t.toY - t.fromY) * k, t.fromZ + (t.toZ - t.fromZ) * k, lerpAngle(t.fromYaw, t.toYaw, k)];
}

function retarget(t: Track, x: number, y: number, z: number, yaw: number, now: number, dur: number): void {
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
}

export class EntityStore {
  private readonly tracks = new Map<string, EntityTrack>();
  private readonly botTracks = new Map<string, BotTrack>();
  private lastUpdate = 0;
  intervalMs = 200;
  /** The revision to send as `since` next. */
  rev = 0;
  /** Bumped on every applied reply. */
  version = 0;

  constructor(public clock: () => number = () => performance.now()) {}

  apply(d: EntityDelta): void {
    const now = this.clock();
    if (this.lastUpdate > 0) this.intervalMs = Math.max(50, Math.min(1000, this.intervalMs * 0.7 + (now - this.lastUpdate) * 0.3));
    this.lastUpdate = now;
    const seen = d.full ? new Set<string>() : null;
    for (const e of d.entities) {
      if (e.id == null) continue;
      const k = `${e.dim}:${e.id}`;
      seen?.add(k);
      const yaw = e.yaw ?? 0;
      const t = this.tracks.get(k);
      if (!t) {
        this.tracks.set(k, { e, fromX: e.x, fromY: e.y, fromZ: e.z, toX: e.x, toY: e.y, toZ: e.z, fromYaw: yaw, toYaw: yaw, start: now, dur: this.intervalMs, ageAt: (e.age ?? 0) * 20, ageMs: now });
        continue;
      }
      retarget(t, e.x, e.y, e.z, yaw, now, this.intervalMs);
      if (e.age != null && Math.abs(e.age * 20 - this.ticks(t, now)) > 20) {
        t.ageAt = e.age * 20;
        t.ageMs = now;
      }
      t.e = e;
    }
    if (seen) for (const k of [...this.tracks.keys()]) if (!seen.has(k)) this.tracks.delete(k);
    for (const [dim, id] of d.removed ?? []) this.tracks.delete(`${dim}:${id}`);
    // Bots: always the full list.
    const names = new Set<string>();
    for (const b of d.bots) {
      names.add(b.name);
      const yaw = b.yaw ?? 0;
      const t = this.botTracks.get(b.name);
      if (!t || t.b.dim !== b.dim) {
        this.botTracks.set(b.name, { b, fromX: b.x, fromY: b.y, fromZ: b.z, toX: b.x, toY: b.y, toZ: b.z, fromYaw: yaw, toYaw: yaw, start: now, dur: this.intervalMs });
        continue;
      }
      retarget(t, b.x, b.y, b.z, yaw, now, this.intervalMs);
      t.b = b;
    }
    for (const k of [...this.botTracks.keys()]) if (!names.has(k)) this.botTracks.delete(k);
    this.rev = d.rev;
    this.version++;
  }

  private ticks(t: EntityTrack, now: number): number {
    return t.ageAt + (now - t.ageMs) / 50;
  }

  /** Every entity of `dim` (null = every dimension) at its interpolated position now (into `out`, cleared first). */
  entities(dim: Dim | null, out: EntityView[] = []): EntityView[] {
    out.length = 0;
    const now = this.clock();
    for (const [key, t] of this.tracks) {
      const e = t.e;
      if (dim !== null && e.dim !== dim) continue;
      const [x, y, z, yaw] = at(t, now);
      out.push({ key, id: e.id ?? -1, dim: e.dim, kind: e.kind, name: e.name ?? null, type: e.type ?? -1, item: e.item ?? 0, count: e.count ?? 1, x, y, z, yaw, ageTicks: this.ticks(t, now), flags: e.flags ?? 0, dto: e });
    }
    return out;
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
    this.rev = 0;
    this.version++;
  }
}
