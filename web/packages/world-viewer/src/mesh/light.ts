// Local light recomputation after block deltas (worker side): a block change can open a cave to the sky, remove a torch or
// shade the ground, and the payload's light only comes back with the next column refetch. Instead the region around the
// changes (±15 blocks horizontally, the full height) is relit the vanilla way: sky light falls straight down through
// cells that block nothing and spreads sideways losing one level per step (more through water, ice and leaves); block light
// floods out from emitters. Cells just outside the region keep their light and seed it, so the patch joins seamlessly.
// Columns without light (remembered) are left alone: they keep the estimate the mesher derives from the opaque top.

import { cellIndex, lightPacked, SECTION_CELLS, type ColumnLight } from '../wire/chunk';
import { F_OPAQUE, tableFor, type GameVersion } from '../world/blocks';
import type { ColumnStore, StoredColumn } from './columns';

/** Light emitted by each block id (vanilla light values, b1.8.1 and 1.12.2 numbering). */
const EMISSION: Uint8Array = (() => {
  const t = new Uint8Array(256);
  const set = (level: number, ids: number[]) => ids.forEach((id) => (t[id] = level));
  set(15, [10, 11, 51, 89, 91, 119, 124, 138, 169, 209]);
  set(14, [50, 198]);
  set(13, [62]);
  set(11, [90]);
  set(9, [74, 94]);
  set(7, [76, 130]);
  set(3, [213]);
  set(1, [39, 117, 120, 122]);
  return t;
})();

/** Light emitted by a block id (vanilla light values, b1.8.1 and 1.12.2 numbering). */
export function emission(id: number): number {
  return EMISSION[id] ?? 0;
}

/** What the see-through blocks take from light passing through them: water, ice and frosted ice 3; leaves and cobweb 1. */
const SEE_THROUGH: Record<number, number> = { 8: 3, 9: 3, 79: 3, 212: 3, 18: 1, 161: 1, 30: 1 };

/** How much light a block takes away when light passes through it (15 = stops it). */
export function opacity(version: GameVersion, id: number): number {
  if (id === 0) return 0;
  const d = tableFor(version)[id];
  if (d && d.flags & F_OPAQUE) return 15;
  return SEE_THROUGH[id] ?? 0;
}

/** Writable light of one column: full sections allocated on demand from the uniform ones. */
function sectionArray(l: ColumnLight, s: number): Uint8Array {
  let f = l.full[s];
  if (!f) {
    f = new Uint8Array(SECTION_CELLS).fill(l.uniform[s]);
    l.full[s] = f;
  }
  return f;
}

/**
 * Relights the region around `changes` ([x, y, z] world cells) in `dim`. Returns the sections whose light changed (to
 * re-mesh). Light lives in each StoredColumn's ColumnLight, which is edited in place.
 */
export function relight(store: ColumnStore, dim: number, changes: Array<[number, number, number]>): Array<[number, number, number]> {
  if (!changes.length) return [];
  const r = new RelightRegion(store, dim, changes);
  if (!r.H) return [];
  r.load();
  // Sky light: straight down from the top, then spread (the cells just outside the region seed it).
  r.skyDown();
  r.seed(r.sky, 4);
  r.spread(r.sky);
  // Block light: from the emitters, likewise.
  r.queueEmitters();
  r.seed(r.blk, 0);
  r.spread(r.blk);
  return r.writeBack();
}

/** The cells within 15 blocks (horizontally) of the changes, the full height: their opacity, old and new light, and the
 * flood-fill queue. */
class RelightRegion {
  readonly version: GameVersion;
  readonly x0: number;
  readonly x1: number;
  readonly z0: number;
  readonly z1: number;
  readonly W: number;
  readonly D: number;
  /** The tallest lit column of the region (0 = nothing to relight). */
  readonly H: number;
  sky = EMPTY;
  blk = EMPTY;
  private op = EMPTY;
  private inside = EMPTY;
  private before = EMPTY;
  private queue = new Int32Array(0);
  private qh = 0;
  private qt = 0;

  constructor(
    private readonly store: ColumnStore,
    private readonly dim: number,
    changes: Array<[number, number, number]>,
  ) {
    this.version = store.version;
    let x0 = Infinity, x1 = -Infinity, z0 = Infinity, z1 = -Infinity;
    for (const [x, , z] of changes) {
      x0 = Math.min(x0, x - 15); x1 = Math.max(x1, x + 15);
      z0 = Math.min(z0, z - 15); z1 = Math.max(z1, z + 15);
    }
    this.x0 = x0; this.x1 = x1; this.z0 = z0; this.z1 = z1;
    this.W = x1 - x0 + 1;
    this.D = z1 - z0 + 1;
    let H = 0;
    for (let cx = x0 >> 4; cx <= x1 >> 4; cx++) for (let cz = z0 >> 4; cz <= z1 >> 4; cz++) H = Math.max(H, store.get(dim, cx, cz)?.light ? store.get(dim, cx, cz)!.height : 0);
    this.H = H;
  }

  private idx(x: number, y: number, z: number): number {
    return (y * this.D + (z - this.z0)) * this.W + (x - this.x0);
  }

  /** The region column holding block column (x, z), when it has light. */
  private colAt(x: number, z: number): StoredColumn | null {
    const c = this.store.get(this.dim, x >> 4, z >> 4);
    return c && c.light ? c : null;
  }

  /** Opacity, emitters, and the old light (kept for the boundary seeds and the change detection). */
  load(): void {
    const N = this.W * this.D * this.H;
    this.sky = new Uint8Array(N);
    this.blk = new Uint8Array(N);
    this.op = new Uint8Array(N);
    this.inside = new Uint8Array(N);
    this.before = new Uint8Array(N);
    this.queue = new Int32Array(N * 3);
    for (let x = this.x0; x <= this.x1; x++)
      for (let z = this.z0; z <= this.z1; z++) {
        const c = this.colAt(x, z);
        if (c) this.loadColumn(c, x, z);
      }
  }

  private loadColumn(c: StoredColumn, x: number, z: number): void {
    for (let y = 0; y < c.height; y++) {
      const i = this.idx(x, y, z);
      const id = c.ids[cellIndex(x & 15, y, z & 15)];
      this.op[i] = opacity(this.version, id);
      this.before[i] = lightPacked(c.light!, x & 15, y, z & 15);
      this.inside[i] = 1;
      this.blk[i] = emission(id);
    }
  }

  /** Sky light falls straight down from the top until something takes light away; lit cells start the spread. */
  skyDown(): void {
    this.qh = this.qt = 0;
    for (let x = this.x0; x <= this.x1; x++)
      for (let z = this.z0; z <= this.z1; z++) {
        const c = this.colAt(x, z);
        if (c) this.skyColumn(c, x, z);
      }
  }

  private skyColumn(c: StoredColumn, x: number, z: number): void {
    let level = 15;
    for (let y = c.height - 1; y >= 0; y--) {
      const i = this.idx(x, y, z);
      const o = this.op[i];
      if (o >= 15) level = 0;
      else if (o > 0) level = Math.max(0, level - o);
      this.sky[i] = level;
      if (level > 0) this.queue[this.qt++] = i;
    }
  }

  /** Every emitter starts the block-light spread. */
  queueEmitters(): void {
    this.qh = this.qt = 0;
    const blk = this.blk;
    for (let i = 0; i < blk.length; i++) if (blk[i] > 0) this.queue[this.qt++] = i;
  }

  /** Boundary seeds: the cells just outside the region keep their light (`shift` picks sky or block) and light the edge. */
  seed(arr: Uint8Array, shift: number): void {
    for (let x = this.x0 - 1; x <= this.x1 + 1; x++)
      for (let z = this.z0 - 1; z <= this.z1 + 1; z++) {
        if (this.inRegion(x, z)) continue;
        const c = this.store.get(this.dim, x >> 4, z >> 4);
        if (c && c.light) this.seedColumn(arr, shift, c, x, z);
      }
  }

  private inRegion(x: number, z: number): boolean {
    return x >= this.x0 && x <= this.x1 && z >= this.z0 && z <= this.z1;
  }

  private seedColumn(arr: Uint8Array, shift: number, c: StoredColumn, x: number, z: number): void {
    const nx = Math.min(this.x1, Math.max(this.x0, x)), nz = Math.min(this.z1, Math.max(this.z0, z));
    for (let y = 0; y < Math.min(this.H, c.height); y++) {
      const v = (lightPacked(c.light!, x & 15, y, z & 15) >> shift) & 15;
      const i = this.idx(nx, y, nz);
      if (!this.inside[i] || v <= 1) continue;
      const want = v - Math.max(1, this.op[i]);
      if (want > arr[i] && this.op[i] < 15) {
        arr[i] = want;
        this.push(i);
      }
    }
  }

  private push(i: number): void {
    if (this.qt < this.queue.length) this.queue[this.qt++] = i;
  }

  /** Floods the queued cells' light out, one level per step (more through see-through blocks). */
  spread(arr: Uint8Array): void {
    while (this.qh < this.qt) {
      const i = this.queue[this.qh++];
      const v = arr[i];
      if (v > 1) this.spreadFrom(arr, i, v);
    }
  }

  /** The six neighbours of cell i (−x, +x, −z, +z, −y, +y) within the region. */
  private spreadFrom(arr: Uint8Array, i: number, v: number): void {
    const W = this.W, D = this.D;
    const x = i % W, rest = (i - x) / W, z = rest % D, y = (rest - z) / D;
    if (x > 0) this.reach(arr, i - 1, v);
    if (x < W - 1) this.reach(arr, i + 1, v);
    if (z > 0) this.reach(arr, i - W, v);
    if (z < D - 1) this.reach(arr, i + W, v);
    if (y > 0) this.reach(arr, i - W * D, v);
    if (y < this.H - 1) this.reach(arr, i + W * D, v);
  }

  private reach(arr: Uint8Array, j: number, v: number): void {
    if (!this.inside[j] || this.op[j] >= 15) return;
    const nv = v - Math.max(1, this.op[j]);
    if (nv > arr[j]) {
      arr[j] = nv;
      this.push(j);
    }
  }

  /** Writes the new light back; returns the sections whose light changed (their mesh and the neighbours' faces read it). */
  writeBack(): Array<[number, number, number]> {
    const dirty = new Set<string>();
    for (let x = this.x0; x <= this.x1; x++)
      for (let z = this.z0; z <= this.z1; z++) {
        const c = this.colAt(x, z);
        if (c) this.writeColumn(c, x, z, dirty);
      }
    return [...dirty].map((k) => k.split(',').map(Number) as [number, number, number]);
  }

  private writeColumn(c: StoredColumn, x: number, z: number, dirty: Set<string>): void {
    for (let y = 0; y < c.height; y++) {
      const i = this.idx(x, y, z);
      const v = (this.sky[i] << 4) | this.blk[i];
      if (v === this.before[i]) continue;
      sectionArray(c.light!, y >> 4)[((x & 15) << 8) | ((z & 15) << 4) | (y & 15)] = v;
      markDirty(dirty, x, y, z);
    }
  }
}

const EMPTY = new Uint8Array(0);

/** The section of cell (x, y, z), and each neighbouring section one of whose faces can read this cell's light. */
function markDirty(dirty: Set<string>, x: number, y: number, z: number): void {
  const s = y >> 4, cx = x >> 4, cz = z >> 4;
  dirty.add(`${cx},${s},${cz}`);
  if ((x & 15) === 0) dirty.add(`${cx - 1},${s},${cz}`);
  if ((x & 15) === 15) dirty.add(`${cx + 1},${s},${cz}`);
  if ((z & 15) === 0) dirty.add(`${cx},${s},${cz - 1}`);
  if ((z & 15) === 15) dirty.add(`${cx},${s},${cz + 1}`);
  if ((y & 15) === 0 && s > 0) dirty.add(`${cx},${s - 1},${cz}`);
  if ((y & 15) === 15) dirty.add(`${cx},${s + 1},${cz}`);
}
