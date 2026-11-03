// Block entities (`GET api/world/blockentities?dim&cols=`): sign text, bed colours, banner patterns, skull types, flower pot
// contents, spawner mobs. Fetched per column when the column arrives or changes (the block hash moved) and when the delta
// feed reports `block_entity` for it; kept here for the main-thread layers (sign text, banners, spawner mobs) and handed to
// the worker, whose mesher reads beds, skulls and pots. Batched (48 columns a request) and never more than one in flight.

import type { BlockEntityDto, Dim, WorldSource } from '../types';
import type { MeshHost } from '../mesh/host';
import { hashKey } from '../wire/hash';
import { normalizeBlockEntities } from '../wire/normalize';

const key = (dim: Dim, cx: number, cz: number) => `${dim},${cx},${cz}`;

export class BlockEntityStore {
  /** "dim,cx,cz" → that column's block entities. */
  readonly columns = new Map<string, BlockEntityDto[]>();
  private readonly hashes = new Map<string, number>();
  private readonly queue = new Set<string>();
  private busy = false;
  private last = 0;
  /** Bumped whenever any column's block entities changed (the main-thread layers rebuild on it). */
  revision = 0;
  batch = 48;

  constructor(
    private source: WorldSource,
    private readonly host: MeshHost,
    private readonly onDirty: (dim: Dim, dirty: Array<[number, number, number]>) => void,
    private readonly onError?: (e: unknown) => void,
  ) {}

  setSource(s: WorldSource): void {
    this.source = s;
  }

  /** Ask for these columns' block entities again (they arrived, changed, or a `block_entity` event named them). */
  want(dim: Dim, cols: Array<[number, number]>): void {
    if (!this.source.blockEntities) return;
    for (const [cx, cz] of cols) this.queue.add(key(dim, cx, cz));
  }

  /** Columns gone: forget their block entities. */
  drop(dim: Dim, cols: Array<[number, number]>): void {
    for (const [cx, cz] of cols) {
      const k = key(dim, cx, cz);
      this.queue.delete(k);
      if (this.columns.delete(k)) this.revision++;
      this.hashes.delete(k);
    }
  }

  /** Block entities of `dim` (every held column). */
  *all(dim: Dim): Generator<BlockEntityDto> {
    const p = `${dim},`;
    for (const [k, list] of this.columns) if (k.startsWith(p)) yield* list;
  }

  get queued(): number {
    return this.queue.size;
  }

  /** Called every frame; fetches at most one batch at a time, a few times a second. */
  tick(now: number): void {
    if (this.busy || !this.queue.size || !this.source.blockEntities || now - this.last < 250) return;
    this.last = now;
    const first = this.queue.values().next().value as string;
    const dim = Number(first.split(',')[0]);
    const cols: Array<[number, number]> = [];
    for (const k of this.queue) {
      const [d, cx, cz] = k.split(',').map(Number);
      if (d !== dim) continue;
      cols.push([cx, cz]);
      this.queue.delete(k);
      if (cols.length >= this.batch) break;
    }
    this.busy = true;
    const src = this.source;
    void (async () => {
      try {
        const reply = normalizeBlockEntities(await src.blockEntities!(dim, cols));
        const changed: Array<{ cx: number; cz: number; entities: BlockEntityDto[] }> = [];
        const answered = new Set<string>();
        for (const c of reply.columns) {
          const k = key(dim, c.cx, c.cz);
          answered.add(k);
          const h = hashKey(c.hash);
          if (this.hashes.get(k) === h && this.columns.has(k)) continue;
          this.hashes.set(k, h);
          changed.push(c);
          if (c.entities.length) this.columns.set(k, c.entities);
          else this.columns.delete(k);
        }
        // Asked for but not in the reply: the column has none.
        for (const [cx, cz] of cols) {
          const k = key(dim, cx, cz);
          if (!answered.has(k) && this.columns.has(k)) {
            this.columns.delete(k);
            this.hashes.delete(k);
            changed.push({ cx, cz, entities: [] });
          }
        }
        if (changed.length) {
          this.revision++;
          this.onDirty(dim, await this.host.setBlockEntities(dim, changed));
        }
      } catch (e) {
        for (const [cx, cz] of cols) this.queue.add(key(dim, cx, cz)); // retried on a later tick
        this.last = now + 2000;
        this.onError?.(e);
      } finally {
        this.busy = false;
      }
    })();
  }

  clear(): void {
    this.columns.clear();
    this.hashes.clear();
    this.queue.clear();
    this.revision++;
  }
}
