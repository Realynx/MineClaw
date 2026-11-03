// Live block updates (`GET api/world/blocks?since=REV` or the bridge's SSE `blocks` event). Changes queue up and are applied
// once per animation frame (`flush`): the worker patches its block arrays in place and reports the sections to re-mesh (the
// changed block's own plus the neighbours across any section boundary it sits on); the section manager's dirty set coalesces
// them, so a burst (an explosion, a bot digging a tunnel) re-meshes each section once. A `full` reply, a reconnect or a change
// in a column we do not hold falls back to the index hash-diff: those columns are invalidated and the index re-run.

import type { BlockDeltas, Dim, WorldSource } from '../types';
import type { MeshHost } from '../mesh/host';
import type { WorldCache } from './cache';
import type { WorldSync } from './sync';

export interface DeltaEvents {
  /** Sections to re-mesh in `dim`. */
  onDirty(dim: Dim, dirty: Array<[number, number, number]>): void;
  /** Blocks that changed (for the highlight). */
  onChanged?(dim: Dim, blocks: Array<[number, number, number]>): void;
  onError?(e: unknown): void;
}

type Change5 = [number, number, number, number, number];

export class BlockDeltaFeed {
  /** The revision to ask from next (0 = not started: the first reply only sets it). */
  rev = 0;
  private pending = new Map<Dim, Change5[]>();
  private stale: Array<{ dim: Dim; cx: number; cz: number; kind: 'loaded' | 'unloaded' }> = [];
  private fallbackDue = false;
  private timer: ReturnType<typeof setTimeout> | null = null;
  private unsubscribe: (() => void) | null = null;
  private running = false;
  private flushing = false;
  pollMs = 250;
  /** Changes applied since start (stats). */
  applied = 0;
  fallbacks = 0;

  constructor(
    private readonly source: WorldSource,
    private readonly host: MeshHost,
    private readonly cache: WorldCache,
    private readonly sync: WorldSync,
    private readonly ev: DeltaEvents,
  ) {}

  start(): void {
    if (this.running) return;
    this.running = true;
    if (this.source.subscribe) {
      this.unsubscribe = this.source.subscribe(
        (d) => this.push(d),
        () => this.fallback(),
      );
    } else if (this.source.blocks) this.poll(0);
  }

  stop(): void {
    this.running = false;
    this.unsubscribe?.();
    this.unsubscribe = null;
    if (this.timer) clearTimeout(this.timer);
    this.timer = null;
  }

  private poll(ms: number): void {
    this.timer = setTimeout(async () => {
      this.timer = null;
      if (!this.running || !this.source.blocks) return;
      let next = this.pollMs;
      try {
        const d = await this.source.blocks(this.rev || undefined);
        this.push(d);
        if (d.more) next = 0;
      } catch (e) {
        next = Math.min(15_000, Math.max(1000, next * 4));
        this.ev.onError?.(e);
      }
      if (this.running) this.poll(next);
    }, ms);
  }

  /** Queues one reply (poll or push). Applied by the next `flush`. */
  push(d: BlockDeltas): void {
    const first = this.rev === 0;
    this.rev = Math.max(this.rev, d.rev);
    if (d.full && !first) this.fallbackDue = true;
    const groups: Array<[Dim, Change5[]]> = Array.isArray(d.blocks) ? [[d.dim ?? 0, d.blocks]] : Object.entries(d.blocks ?? {}).map(([k, v]) => [Number(k), v]);
    for (const [dim, blocks] of groups) {
      if (!blocks?.length || !Number.isFinite(dim)) continue;
      let list = this.pending.get(dim);
      if (!list) this.pending.set(dim, (list = []));
      // In rev order: applying them in order makes the later entry for a cell win.
      for (const b of blocks) list.push([b[0], b[1], b[2], b[3], b[4]]);
    }
    for (const c of d.columns ?? []) this.stale.push(c);
  }

  /** The stream reconnected or the server lost our revision: refetch through the index hash-diff. */
  fallback(): void {
    this.fallbackDue = true;
  }

  get queued(): number {
    let n = 0;
    for (const l of this.pending.values()) n += l.length;
    return n + this.stale.length + (this.fallbackDue ? 1 : 0);
  }

  /** Applies everything queued (call once per animation frame). */
  async flush(): Promise<void> {
    if (this.flushing || !this.queued) return;
    this.flushing = true;
    try {
      const batches = this.pending;
      this.pending = new Map();
      const cols = this.stale;
      this.stale = [];
      let reindex = false;
      if (this.fallbackDue) {
        this.fallbackDue = false;
        this.fallbacks++;
        reindex = true;
      }
      for (const [dim, blocks] of batches) {
        const r = await this.host.patch(dim, blocks);
        this.applied += r.applied;
        if (r.dirty.length) this.ev.onDirty(dim, r.dirty);
        if (r.applied) this.ev.onChanged?.(dim, blocks.map(([x, y, z]) => [x, y, z]));
        if (r.missing.length) {
          // Not held (yet): the index fetches them when they are in someone's interest.
          this.cache.invalidate(dim, r.missing);
          reindex = true;
        }
      }
      for (const c of cols) {
        const have = this.cache.get(c.dim, c.cx, c.cz);
        if (c.kind === 'loaded') {
          this.cache.invalidate(c.dim, [[c.cx, c.cz]]);
          reindex = true;
        } else if (have && have.live) {
          // Out of every bot's sight: keep it, drawn as remembered (dimmed); the cache's LRU trims it later.
          have.live = false;
          this.ev.onDirty(c.dim, await this.host.touch(c.dim, [{ cx: c.cx, cz: c.cz, seen: have.seen, by: have.by, live: false }]));
        }
      }
      if (reindex) this.sync.reindexNow();
    } catch (e) {
      this.ev.onError?.(e);
    } finally {
      this.flushing = false;
    }
  }
}
