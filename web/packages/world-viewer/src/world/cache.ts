// The main thread's index of the synced world (HiveWorldCache's bookkeeping; the blocks themselves live in the mesh worker):
// per dimension, which columns are held with which hash. Index-then-fetch: `reconcile` turns an index reply into the columns
// to download (new or changed hash), the ones to just touch (same hash, new seen/by/live) and the ones to drop.

import type { Dim, WorldIndex, WorldIndexColumn } from '../types';
import { hashKey } from '../wire/hash';
import type { ColumnSummary } from '../mesh/columns';
import type { GameVersion } from './blocks';

export interface CachedColumn {
  cx: number;
  cz: number;
  hash: number;
  seen: number;
  by: string | null;
  live: boolean;
  height: number;
}

export interface ReconcileResult {
  want: Array<[number, number]>;
  touched: Array<{ cx: number; cz: number; seen: number; by: string | null; live: boolean }>;
  dropped: Array<[number, number]>;
}

const key = (cx: number, cz: number) => `${cx},${cz}`;

export class WorldCache {
  private readonly dims = new Map<Dim, Map<string, CachedColumn>>();
  private gameVersion: string | null = null;
  private maxHeight = 128;
  private time: number | null = null;
  private timeAt = 0;
  private timeDim = 0;
  private weather = new Map<Dim, { rain: number; thunder: number }>();
  bytesReceived = 0;
  /** Bumped on every change. */
  revision = 0;

  private dim(d: Dim): Map<string, CachedColumn> {
    let m = this.dims.get(d);
    if (!m) this.dims.set(d, (m = new Map()));
    return m;
  }

  get(d: Dim, cx: number, cz: number): CachedColumn | undefined {
    return this.dims.get(d)?.get(key(cx, cz));
  }

  count(d?: Dim): number {
    if (d != null) return this.dims.get(d)?.size ?? 0;
    let n = 0;
    for (const m of this.dims.values()) n += m.size;
    return n;
  }

  columns(d: Dim): IterableIterator<CachedColumn> {
    return this.dim(d).values();
  }

  /** The hive's version (index replies), else 1.12.2 when 256-high columns were seen, else null. */
  detectedVersion(): GameVersion | null {
    const v = this.gameVersion;
    if (v === 'b1.8.1' || v === '1.12.2') return v;
    if (v && v.startsWith('1.')) return '1.12.2';
    if (v && v.startsWith('b')) return 'b1.8.1';
    return this.maxHeight > 128 ? '1.12.2' : null;
  }

  /** Records what an index said about the world: version, time (advanced locally at 20 ticks/s), weather. */
  setWorldInfo(idx: WorldIndex, nowMs: number): void {
    if (idx.version) this.gameVersion = idx.version;
    if (idx.time != null) {
      this.time = idx.time;
      this.timeAt = nowMs;
      this.timeDim = idx.dim;
    }
    if (idx.rain != null) this.weather.set(idx.dim, { rain: clamp01(idx.rain), thunder: clamp01(idx.thunder ?? 0) });
  }

  /** World ticks now in `d`, or null when unknown. */
  timeNow(d: Dim, nowMs: number): number | null {
    if (this.time == null || d !== this.timeDim) return null;
    return this.time + Math.min(60_000, Math.max(0, nowMs - this.timeAt)) / 50;
  }

  weatherIn(d: Dim): { rain: number; thunder: number } {
    return d === -1 ? { rain: 0, thunder: 0 } : (this.weather.get(d) ?? { rain: 0, thunder: 0 });
  }

  /** HiveWorldCache.Reconcile over the rectangle (cx0..cx1, cz0..cz1) the index covered. */
  reconcile(d: Dim, cx0: number, cz0: number, cx1: number, cz1: number, index: WorldIndexColumn[]): ReconcileResult {
    const m = this.dim(d);
    const res: ReconcileResult = { want: [], touched: [], dropped: [] };
    const listed = new Set<string>();
    for (const c of index) {
      const k = key(c.cx, c.cz);
      listed.add(k);
      const have = m.get(k);
      if (have && have.hash === hashKey(c.hash)) {
        const by = c.by ?? null;
        if (have.seen !== c.seen || have.live !== c.live || have.by !== by) {
          have.seen = c.seen;
          have.by = by;
          have.live = c.live;
          res.touched.push({ cx: c.cx, cz: c.cz, seen: c.seen, by, live: c.live });
        }
        continue;
      }
      res.want.push([c.cx, c.cz]);
    }
    for (const [k, c] of m)
      if (c.cx >= cx0 && c.cx <= cx1 && c.cz >= cz0 && c.cz <= cz1 && !listed.has(k)) {
        m.delete(k);
        res.dropped.push([c.cx, c.cz]);
      }
    if (res.touched.length || res.dropped.length) this.revision++;
    return res;
  }

  /** Stores what a chunks reply delivered (missing = dropped). */
  applyStored(d: Dim, cols: ColumnSummary[], bytes: number): void {
    const m = this.dim(d);
    for (const c of cols) {
      if (c.missing) {
        m.delete(key(c.cx, c.cz));
        continue;
      }
      m.set(key(c.cx, c.cz), { cx: c.cx, cz: c.cz, hash: hashKey(c.hash), seen: c.seen, by: c.by, live: c.live, height: c.height });
      if (c.height > this.maxHeight) this.maxHeight = c.height;
    }
    this.bytesReceived += bytes;
    this.revision++;
  }

  /** Drops the columns furthest from the nearest of the centres (chunk coords) until at most `max` remain in `d`; returns them. */
  trim(d: Dim, cx: number | Array<[number, number]>, cz: number, max: number): Array<[number, number]> {
    const m = this.dim(d);
    if (m.size <= max) return [];
    const cs: Array<[number, number]> = typeof cx === 'number' ? [[cx, cz]] : cx;
    const far = (c: CachedColumn) => Math.min(...cs.map(([x, z]) => (c.cx - x) ** 2 + (c.cz - z) ** 2));
    const all = [...m.values()].sort((a, b) => far(b) - far(a));
    const out: Array<[number, number]> = [];
    for (const c of all.slice(0, m.size - max)) {
      m.delete(key(c.cx, c.cz));
      out.push([c.cx, c.cz]);
    }
    this.revision++;
    return out;
  }

  /** Every column of the dimensions not in `keep` (no view looks at them any more). */
  otherDims(keep: Dim | Dim[]): Array<[Dim, Array<[number, number]>]> {
    const ks = Array.isArray(keep) ? keep : [keep];
    const out: Array<[Dim, Array<[number, number]>]> = [];
    for (const [d, m] of this.dims) if (!ks.includes(d) && m.size) out.push([d, [...m.values()].map((c) => [c.cx, c.cz] as [number, number])]);
    return out;
  }

  /** Forgets the hash of these columns (the blocks stay drawn) so the next index refetches them. */
  invalidate(d: Dim, cols: Array<[number, number]>): void {
    const m = this.dim(d);
    for (const [cx, cz] of cols) {
      const c = m.get(key(cx, cz));
      if (c) c.hash = Number.NaN;
    }
    this.revision++;
  }

  clearDim(d: Dim): void {
    this.dims.delete(d);
    this.revision++;
  }
}

/** Index rectangle in chunk coordinates for a block centre and radius (HiveSync: (x ± r) >> 4). */
export function indexRect(x: number, z: number, r: number): [number, number, number, number] {
  const bx = Math.floor(x), bz = Math.floor(z);
  return [(bx - r) >> 4, (bz - r) >> 4, (bx + r) >> 4, (bz + r) >> 4];
}

/** Nearest first, `size` columns per request, at most `maxBatches` requests per pass. */
export function planBatches(want: Array<[number, number]>, ccx: number, ccz: number, size = 48, maxBatches = 8): Array<Array<[number, number]>> {
  const sorted = [...want].sort((a, b) => (a[0] - ccx) ** 2 + (a[1] - ccz) ** 2 - ((b[0] - ccx) ** 2 + (b[1] - ccz) ** 2));
  const out: Array<Array<[number, number]>> = [];
  for (let i = 0; i < sorted.length && out.length < maxBatches; i += size) out.push(sorted.slice(i, i + size));
  return out;
}

function clamp01(v: number): number {
  return Math.max(0, Math.min(1, v));
}
