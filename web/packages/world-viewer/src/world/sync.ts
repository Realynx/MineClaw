// Polling loops (explorer-spec §12.2): the world index → chunk deltas loop, plus a generic sequential poller with the
// per-route back-off the Explorer uses (exponential 0.5 → 15 s, ±25 % jitter, reset on success).

import type { Dim, WorldSource } from '../types';
import type { MeshHost } from '../mesh/host';
import type { ApplyResult } from '../mesh/columns';
import { normalizeIndex } from '../wire/normalize';
import { WorldCache, indexRect, planBatches } from './cache';

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
  readonly backoff = new Backoff();
  constructor(private readonly fn: () => Promise<void>, public periodMs: number, private readonly onError?: (e: unknown) => void) {}
  start(): void {
    if (this.running) return;
    this.running = true;
    this.schedule(0);
  }
  stop(): void {
    this.running = false;
    if (this.timer) clearTimeout(this.timer);
    this.timer = null;
  }
  /** Run now (e.g. the dimension changed). */
  kick(): void {
    if (!this.running) return;
    if (this.timer) clearTimeout(this.timer);
    this.schedule(0);
  }
  private schedule(ms: number): void {
    this.timer = setTimeout(async () => {
      this.timer = null;
      if (!this.running) return;
      let next = this.periodMs;
      try {
        await this.fn();
        this.backoff.ok();
      } catch (e) {
        next = Math.max(next, this.backoff.fail(Date.now()));
        this.onError?.(e);
      }
      if (this.running && !this.timer) this.schedule(next);
    }, ms);
  }
}

export interface Interest {
  dim: Dim;
  x: number;
  z: number;
  /** Blocks; clamped 16..256 like the server. */
  r: number;
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
 * changed columns nearest first in batches of 48 (at most 8 per pass), hand each payload to the mesh host, and keep at most
 * `maxColumns` per dimension (those nearest any interest). Several interests (one per viewed bot) are indexed round-robin.
 */
export class WorldSync {
  private interests = new Map<string, Interest>();
  private indexedAt = new Map<string, { at: Interest; t: number }>();
  private timer: ReturnType<typeof setTimeout> | null = null;
  private running = false;
  private busy = false;
  private rr = 0;
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

  /** The single interest of one view. */
  setInterest(i: Interest): void {
    this.setInterests({ main: i });
  }

  /** Every view's interest, by a stable key. */
  setInterests(list: Record<string, Interest>): void {
    const next = new Map<string, Interest>();
    for (const [k, i] of Object.entries(list)) next.set(k, { ...i, r: Math.max(16, Math.min(256, Math.round(i.r))) });
    this.interests = next;
    for (const k of [...this.indexedAt.keys()]) if (!next.has(k)) this.indexedAt.delete(k);
  }

  start(): void {
    if (this.running) return;
    this.running = true;
    this.loop();
  }

  stop(): void {
    this.running = false;
    if (this.timer) clearTimeout(this.timer);
    this.timer = null;
  }

  private loop(): void {
    this.timer = setTimeout(async () => {
      this.timer = null;
      if (!this.running) return;
      try {
        await this.tick(Date.now());
      } finally {
        if (this.running) this.loop();
      }
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
    const keys = [...this.interests.keys()];
    let key: string | null = null;
    for (let n = 0; n < keys.length; n++) {
      const k = keys[(this.rr + n) % keys.length];
      if (this.due(k, this.interests.get(k)!, now)) {
        key = k;
        this.rr = (this.rr + n + 1) % keys.length;
        break;
      }
    }
    if (key == null) return;
    const it = this.interests.get(key)!;
    this.busy = true;
    try {
      let raw;
      try {
        raw = await this.source.index(it.dim, Math.floor(it.x), Math.floor(it.z), it.r);
        this.indexBackoff.ok();
      } catch (e) {
        this.indexBackoff.fail(Date.now());
        this.ev.onError('index', e);
        return;
      }
      const index = normalizeIndex(raw);
      this.indexedAt.set(key, { at: it, t: now });
      this.cache.setWorldInfo(index, now);
      this.ev.onInfo();
      const [cx0, cz0, cx1, cz1] = indexRect(it.x, it.z, it.r);
      const rec = this.cache.reconcile(it.dim, cx0, cz0, cx1, cz1, index.columns);
      if (rec.dropped.length) this.ev.onDropped(it.dim, rec.dropped, await this.host.drop(it.dim, rec.dropped));
      if (rec.touched.length) this.ev.onTouched(it.dim, await this.host.touch(it.dim, rec.touched));
      const ccx = Math.floor(it.x) >> 4, ccz = Math.floor(it.z) >> 4;
      for (const batch of planBatches(rec.want, ccx, ccz, this.batchColumns, this.maxBatches)) {
        if (!this.running || this.chunkBackoff.waiting(Date.now())) break;
        let buf: ArrayBuffer;
        try {
          buf = await this.source.chunks(it.dim, batch);
          this.chunkBackoff.ok();
        } catch (e) {
          this.chunkBackoff.fail(Date.now());
          this.ev.onError('chunks', e);
          this.indexedAt.delete(key); // retried on the next pass
          break;
        }
        let res: ApplyResult;
        try {
          res = await this.host.apply(buf);
        } catch (e) {
          this.ev.onError('chunks', e);
          this.indexedAt.delete(key);
          break;
        }
        this.cache.applyStored(res.dim, res.columns, res.bytes);
        this.ev.onApplied(res);
      }
      const centres = [...this.interests.values()].filter((i) => i.dim === it.dim).map((i) => [Math.floor(i.x) >> 4, Math.floor(i.z) >> 4] as [number, number]);
      const trimmed = this.cache.trim(it.dim, centres.length ? centres : [[ccx, ccz]], 0, this.maxColumns);
      if (trimmed.length) this.ev.onDropped(it.dim, trimmed, await this.host.drop(it.dim, trimmed));
      // Columns of dimensions no view looks at are not drawn; free them.
      for (const [d, cols] of this.cache.otherDims([...this.interests.values()].map((i) => i.dim))) {
        this.cache.clearDim(d);
        this.ev.onDropped(d, cols, await this.host.drop(d, cols));
      }
    } finally {
      this.busy = false;
    }
  }
}
