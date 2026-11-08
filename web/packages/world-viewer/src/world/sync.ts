// Polling loops (explorer-spec §12.2): the world index → chunk deltas loop, plus a generic sequential poller with the
// per-route back-off the Explorer uses (exponential 0.5 → 15 s, ±25 % jitter, reset on success).

import type { Dim, WorldIndex, WorldSource } from '../types';
import type { MeshHost } from '../mesh/host';
import type { ApplyResult } from '../mesh/columns';
import { normalizeIndex } from '../wire/normalize';
import { type WorldCache, indexRect, planBatches } from './cache';
import { FairQueue } from './fairQueue';

/** Leaving a view cancels its reads; this is not a server failure and must not delay its next visit. */
export const isAbortError = (e: unknown): boolean => e instanceof Error && e.name === 'AbortError';

export class Backoff {
  private delay = 0;
  until = 0;
  constructor(private readonly min = 500, private readonly max = 15_000, private readonly random = Math.random) {}
  fail(now: number): number {
    this.delay = Math.min(this.max, Math.max(this.min, this.delay * 2));
    const d = this.delay * (0.75 + this.random() * 0.5);
    this.until = now + d;
    return d;
  }
  ok(): void {
    this.delay = 0;
    this.until = 0;
  }
  waiting(now: number): boolean {
    return now < this.until;
  }
}

/** Runs `fn` every `periodMs` (never overlapping), backing off after failures. */
export class Poller {
  private timer: ReturnType<typeof setTimeout> | null = null;
  private running = false;
  private inFlight = false;
  private again = false;
  private generation = 0;
  readonly backoff = new Backoff();
  constructor(private readonly fn: () => Promise<void>, public periodMs: number, private readonly onError?: (e: unknown) => void) {}
  start(): void {
    if (this.running) return;
    this.running = true;
    this.generation++;
    if (this.inFlight) this.again = true;
    else this.schedule(0);
  }
  stop(): void {
    this.running = false;
    this.generation++;
    this.again = false;
    if (this.timer) clearTimeout(this.timer);
    this.timer = null;
  }
  /** Run now (e.g. the dimension changed, a push said new data is ready); a run in flight runs once more when it ends. */
  kick(): void {
    if (!this.running) return;
    if (this.inFlight) {
      this.again = true;
      return;
    }
    if (this.timer) clearTimeout(this.timer);
    this.schedule(0);
  }
  private schedule(ms: number): void {
    this.timer = setTimeout(() => {
      void (async () => {
        this.timer = null;
        if (!this.running) return;
        let next = this.periodMs;
        const generation = this.generation;
        this.inFlight = true;
        try {
          await this.fn();
          if (generation === this.generation) this.backoff.ok();
        } catch (e) {
          if (generation === this.generation && !isAbortError(e)) {
            next = Math.max(next, this.backoff.fail(Date.now()));
            this.again = false;
            this.onError?.(e);
          }
        } finally {
          this.inFlight = false;
        }
        if (this.again) {
          this.again = false;
          next = 0;
        }
        if (this.running && !this.timer) this.schedule(next);
      })();
    }, ms);
  }
}

export interface Interest {
  dim: Dim;
  x: number;
  z: number;
  /** Blocks; clamped 16..256 like the server. */
  r: number;
  /** Interactive view before thumbnails; round-robin still gives background views a turn. */
  priority?: number;
}

/** A view wholly covered by another shares its index/download work, including negative chunk coordinates. */
export function sharedInterests(list: Record<string, Interest>): Map<string, Interest> {
  const sorted = Object.entries(list)
    .map(([k, i]) => [k, { ...i, r: Math.max(16, Math.min(256, Math.round(i.r))) }] as const)
    .sort((a, b) => (b[1].priority ?? 0) - (a[1].priority ?? 0) || b[1].r - a[1].r);
  const kept = new Map<string, Interest>();
  for (const [k, i] of sorted) {
    const r = indexRect(i.x, i.z, i.r);
    const covered = [...kept.values()].some((other) => {
      const q = indexRect(other.x, other.z, other.r);
      return other.dim === i.dim && q[0] <= r[0] && q[1] <= r[1] && q[2] >= r[2] && q[3] >= r[3];
    });
    if (!covered) kept.set(k, i);
  }
  return kept;
}

export interface WorldSyncEvents {
  onApplied(res: ApplyResult): void;
  onDropped(dim: Dim, cols: Array<[number, number]>, dirty: Array<[number, number, number]>): void;
  onTouched(dim: Dim, dirty: Array<[number, number, number]>): void;
  onInfo(): void;
  onError(route: 'index' | 'chunks', e: unknown): void;
}

/**
 * The world loop: re-index each interest every `periodMs` (or at once when it moves 16+ blocks / changes dimension), fetch the
 * changed columns with nearby/oldest-waiting fairness in batches of 48 (at most 8 per pass), hand each payload to the mesh host, and keep at most
 * `maxColumns` per dimension (those nearest any interest). Several interests (one per viewed bot) are indexed round-robin.
 */
export class WorldSync {
  private interests = new Map<string, Interest>();
  private interestShape = '';
  private indexedAt = new Map<string, { at: Interest; t: number }>();
  private readonly pendingColumns = new Map<string, FairQueue>();
  private timer: ReturnType<typeof setTimeout> | null = null;
  private running = false;
  private busy = false;
  private generation = 0;
  private rr = 0;
  private priorityTurn = false;
  readonly indexBackoff = new Backoff();
  readonly chunkBackoff = new Backoff();
  periodMs = 900;
  maxColumns = 1600;
  batchColumns = 48;
  maxBatches = 8;

  constructor(private readonly source: WorldSource, private readonly host: MeshHost, readonly cache: WorldCache, private readonly ev: WorldSyncEvents) {}

  /** Re-index every interest on the next pass (block-delta fallback, reconnect). */
  reindexNow(): void {
    this.indexedAt.clear();
  }

  /** A delta outside every view must not restart their indexing/download turns. */
  reindexColumns(dim: Dim, columns: Array<[number, number]>): void {
    for (const [key, interest] of this.interests) {
      if (interest.dim !== dim) continue;
      const [x0, z0, x1, z1] = indexRect(interest.x, interest.z, interest.r);
      if (columns.some(([x, z]) => x >= x0 && x <= x1 && z >= z0 && z <= z1)) this.indexedAt.delete(key);
    }
  }

  /** The single interest of one view. */
  setInterest(i: Interest): void {
    this.setInterests({ main: i });
  }

  /** Every view's interest, by a stable key. */
  setInterests(list: Record<string, Interest>): void {
    // Called every rendered frame. Recompute rectangle coverage only when a view crosses a chunk boundary, resizes,
    // appears or disappears; retain current centres for nearest-first downloads without an O(views²) frame cost.
    const shape = Object.entries(list)
      .map(
        ([k, i]) =>
          `${k}:${i.dim}:${indexRect(i.x, i.z, Math.max(16, Math.min(256, Math.round(i.r)))).join(',')}:${i.priority ?? 0}`,
      )
      .join('|');
    if (shape === this.interestShape) {
      for (const k of this.interests.keys()) {
        const i = list[k];
        this.interests.set(k, { ...i, r: Math.max(16, Math.min(256, Math.round(i.r))) });
      }
      return;
    }
    this.interestShape = shape;
    const next = sharedInterests(list);
    this.interests = next;
    for (const k of [...this.indexedAt.keys()]) if (!next.has(k)) this.indexedAt.delete(k);
    for (const k of this.pendingColumns.keys()) if (!next.has(k)) this.pendingColumns.delete(k);
  }

  start(): void {
    if (this.running) return;
    this.running = true;
    this.loop(++this.generation);
  }

  stop(): void {
    this.running = false;
    this.generation++;
    if (this.timer) clearTimeout(this.timer);
    this.timer = null;
  }

  private loop(generation: number): void {
    this.timer = setTimeout(() => {
      void (async () => {
        this.timer = null;
        if (!this.running || generation !== this.generation) return;
        try {
          await this.tick(Date.now());
        } finally {
          if (this.running && generation === this.generation) this.loop(generation);
        }
      })();
    }, 150);
  }

  private due(key: string, i: Interest, now: number): boolean {
    const a = this.indexedAt.get(key);
    if (!a || a.at.dim !== i.dim || Math.abs(a.at.x - i.x) + Math.abs(a.at.z - i.z) >= 16 || a.at.r !== i.r) return true;
    return now - a.t >= this.periodMs;
  }

  /** One pass (exposed for tests): index the next due interest, then fetch what changed. */
  async tick(now: number): Promise<void> {
    if (this.busy || !this.interests.size || this.indexBackoff.waiting(now)) return;
    const key = this.nextDue(now);
    if (key === null) return;
    const it = this.interests.get(key)!;
    this.busy = true;
    try {
      const index = await this.fetchIndex(it);
      if (!index) return;
      this.indexedAt.set(key, { at: it, t: now });
      this.cache.setWorldInfo(index, now);
      this.ev.onInfo();
      const [cx0, cz0, cx1, cz1] = indexRect(it.x, it.z, it.r);
      const rec = this.cache.reconcile(it.dim, cx0, cz0, cx1, cz1, index.columns);
      if (rec.dropped.length) this.ev.onDropped(it.dim, rec.dropped, await this.host.drop(it.dim, rec.dropped));
      if (rec.touched.length) this.ev.onTouched(it.dim, await this.host.touch(it.dim, rec.touched));
      await this.fetchColumns(key, it, rec.want);
      await this.trimAround(it);
    } finally {
      this.busy = false;
    }
  }

  /** The next interest due for indexing (round robin), or null. */
  private nextDue(now: number): string | null {
    const keys = [...this.interests.keys()];
    if (!this.priorityTurn) {
      const primary = keys.find(
        (k) => (this.interests.get(k)!.priority ?? 0) > 0 && this.due(k, this.interests.get(k)!, now),
      );
      if (primary) {
        this.priorityTurn = true;
        // Keep the background cursor advancing even when slow downloads make the primary due every turn.
        // Only skip the primary itself; resetting to its neighbour would starve all later background views.
        if (keys[this.rr % keys.length] === primary) this.rr = (this.rr + 1) % keys.length;
        return primary;
      }
    }
    for (let n = 0; n < keys.length; n++) {
      const k = keys[(this.rr + n) % keys.length];
      if (this.due(k, this.interests.get(k)!, now)) {
        this.rr = (this.rr + n + 1) % keys.length;
        this.priorityTurn = false;
        return k;
      }
    }
    return null;
  }

  private async fetchIndex(it: Interest): Promise<WorldIndex | null> {
    let raw;
    try {
      raw = await this.source.index(it.dim, Math.floor(it.x), Math.floor(it.z), it.r);
      this.indexBackoff.ok();
    } catch (e) {
      if (!isAbortError(e)) {
        this.indexBackoff.fail(Date.now());
        this.ev.onError('index', e);
      }
      return null;
    }
    return normalizeIndex(raw);
  }

  /** Nearby columns interleaved with oldest waiting columns; a failed batch is retried on the next pass. */
  private async fetchColumns(key: string, it: Interest, want: Array<[number, number]>): Promise<void> {
    if (!this.currentInterest(key, it) || this.chunkBackoff.waiting(Date.now())) return;
    const ccx = Math.floor(it.x) >> 4, ccz = Math.floor(it.z) >> 4;
    // A scattered thumbnail grid cannot hold the interactive view behind eight whole downloads. One view keeps full
    // throughput; several views share short turns. Overlap the next download with the worker applying this one.
    const limit = this.interests.size > 1 ? Math.min(2, this.maxBatches) : this.maxBatches;
    const ordered = planBatches(want, ccx, ccz, this.batchColumns, Infinity).flat();
    let queue = this.pendingColumns.get(key);
    if (!queue) this.pendingColumns.set(key, (queue = new FairQueue()));
    const selected = queue.pick(ordered.map((c) => c.join(',')), this.batchColumns * limit);
    const batches: Array<Array<[number, number]>> = [];
    for (let n = 0; n < selected.length; n += this.batchColumns)
      batches.push(selected.slice(n, n + this.batchColumns).map((c) => c.split(',').map(Number) as [number, number]));
    if (!batches.length) return;
    let pending = this.downloadBatch(it.dim, batches[0]);
    for (let n = 0; n < batches.length; n++) {
      const buf = await pending;
      if (!this.currentInterest(key, it)) break;
      if (!buf) {
        this.indexedAt.delete(key);
        break;
      }
      if (n + 1 < batches.length && !this.chunkBackoff.waiting(Date.now()))
        pending = this.downloadBatch(it.dim, batches[n + 1]);
      const res = await this.applyBatch(buf);
      if (!res) {
        this.indexedAt.delete(key); // retried on the next pass
        break;
      }
      this.cache.applyStored(res.dim, res.columns, res.bytes);
      this.ev.onApplied(res);
      if (this.chunkBackoff.waiting(Date.now())) break;
    }
  }

  /** Stop a stale download sweep as soon as its view closes, changes dimension, or pans into another index rectangle. */
  private currentInterest(key: string, it: Interest): boolean {
    const current = this.interests.get(key);
    return (
      this.running &&
      !!current &&
      current.dim === it.dim &&
      indexRect(current.x, current.z, current.r).join(',') === indexRect(it.x, it.z, it.r).join(',')
    );
  }

  private async downloadBatch(dim: Dim, batch: Array<[number, number]>): Promise<ArrayBuffer | null> {
    try {
      const buf = await this.source.chunks(dim, batch);
      this.chunkBackoff.ok();
      return buf;
    } catch (e) {
      if (!isAbortError(e)) {
        this.chunkBackoff.fail(Date.now());
        this.ev.onError('chunks', e);
      }
      return null;
    }
  }

  private async applyBatch(buf: ArrayBuffer): Promise<ApplyResult | null> {
    try {
      return await this.host.apply(buf);
    } catch (e) {
      this.ev.onError('chunks', e);
      return null;
    }
  }

  /** Columns beyond the most kept around the interests of this dimension, and those of dimensions no view looks at. */
  private async trimAround(it: Interest): Promise<void> {
    const ccx = Math.floor(it.x) >> 4, ccz = Math.floor(it.z) >> 4;
    const centres = [...this.interests.values()].filter((i) => i.dim === it.dim).map((i) => [Math.floor(i.x) >> 4, Math.floor(i.z) >> 4] as [number, number]);
    const trimmed = this.cache.trim(it.dim, centres.length ? centres : [[ccx, ccz]], 0, this.maxColumns);
    if (trimmed.length) this.ev.onDropped(it.dim, trimmed, await this.host.drop(it.dim, trimmed));
    // Columns of dimensions no view looks at are not drawn; free them.
    for (const [d, cols] of this.cache.otherDims([...this.interests.values()].map((i) => i.dim))) {
      this.cache.clearDim(d);
      this.ev.onDropped(d, cols, await this.host.drop(d, cols));
    }
  }
}
