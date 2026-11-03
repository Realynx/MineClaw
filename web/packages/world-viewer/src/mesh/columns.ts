// The decoded world the mesher reads (lives in the worker, or in-thread for tests): columns by (dim, cx, cz), each with its
// blocks, light, biomes and an opaque-top height map (remembered columns carry no light: sky is estimated from it).

import { decodeMcwc, type McwcEntry } from '../wire/mcwc';
import { cellIndex, lightPacked, parseLight, unpackSnapshot, type ColumnLight } from '../wire/chunk';
import { inflateRaw } from '../wire/inflate';
import { F_OPAQUE, tableFor, type GameVersion } from '../world/blocks';
import { relight } from './light';
import type { BlockEntityDto } from '../types';

export interface StoredColumn {
  dim: number;
  cx: number;
  cz: number;
  hash: bigint;
  seen: number;
  by: string | null;
  live: boolean;
  ids: Uint8Array;
  meta: Uint8Array;
  height: number;
  light: ColumnLight | null;
  /** Highest opaque y per (lx*16+lz), -1 = none. */
  top: Int16Array;
}

/** What the main thread learns about a stored (or dropped) column. */
export interface ColumnSummary {
  cx: number;
  cz: number;
  /** Decimal string of the exact i64. */
  hash: string;
  seen: number;
  by: string | null;
  live: boolean;
  height: number;
  /** The payload said the column is unknown: dropped. */
  missing: boolean;
}

export interface ApplyResult {
  dim: number;
  columns: ColumnSummary[];
  /** Sections whose mesh is stale now: [cx, sy, cz]. */
  dirty: Array<[number, number, number]>;
  bytes: number;
}

export const colKey = (dim: number, cx: number, cz: number) => `${dim},${cx},${cz}`;

export class ColumnStore {
  readonly cols = new Map<string, StoredColumn>();
  version: GameVersion = 'b1.8.1';

  get(dim: number, cx: number, cz: number): StoredColumn | undefined {
    return this.cols.get(colKey(dim, cx, cz));
  }

  /** Decodes an MCWC payload (inflating every blob) and stores its columns. Throws on a malformed payload, applying nothing. */
  async applyPayload(buf: ArrayBuffer | Uint8Array): Promise<ApplyResult> {
    const p = decodeMcwc(buf);
    const decoded = await Promise.all(p.entries.map((e) => decodeEntry(p.dim, e)));
    const res: ApplyResult = { dim: p.dim, columns: [], dirty: [], bytes: buf.byteLength };
    const dirty = new Set<string>();
    for (let i = 0; i < decoded.length; i++) {
      const e = p.entries[i];
      const col = decoded[i];
      const key = colKey(p.dim, e.cx, e.cz);
      const old = this.cols.get(key);
      if (!col) {
        if (old) {
          this.cols.delete(key);
          markAll(dirty, e.cx, e.cz, old.height >> 4);
        }
        res.columns.push({ cx: e.cx, cz: e.cz, hash: e.hash.toString(), seen: e.seenMs, by: e.by, live: e.live, height: 0, missing: true });
        continue;
      }
      col.top = topMap(col, this.version);
      this.cols.set(key, col);
      const sections = col.height >> 4;
      if (!old || old.live !== col.live || old.height !== col.height) markAll(dirty, e.cx, e.cz, sections);
      else for (let s = 0; s < sections; s++) if (sectionChanged(old, col, s)) markAround(dirty, e.cx, s, e.cz);
      res.columns.push({ cx: e.cx, cz: e.cz, hash: e.hash.toString(), seen: e.seenMs, by: e.by, live: e.live, height: col.height, missing: false });
    }
    for (const k of dirty) {
      const [x, s, z] = k.split(',').map(Number);
      res.dirty.push([x, s, z]);
    }
    return res;
  }

  /** Same hash, new bookkeeping: a live flip changes the remembered shading, so it dirties the column. */
  touch(dim: number, cx: number, cz: number, seen: number, by: string | null, live: boolean): Array<[number, number, number]> {
    const c = this.get(dim, cx, cz);
    if (!c) return [];
    c.seen = seen;
    c.by = by;
    if (c.live === live) return [];
    c.live = live;
    const out: Array<[number, number, number]> = [];
    for (let s = 0; s < c.height >> 4; s++) out.push([cx, s, cz]);
    return out;
  }

  /** Drops columns; returns the neighbours' sections that need remeshing (their edge faces now face unknown space). */
  drop(dim: number, cols: Array<[number, number]>): Array<[number, number, number]> {
    const dirty = new Set<string>();
    for (const [cx, cz] of cols) {
      const c = this.cols.get(colKey(dim, cx, cz));
      if (!c) continue;
      this.cols.delete(colKey(dim, cx, cz));
      for (let dx = -1; dx <= 1; dx++)
        for (let dz = -1; dz <= 1; dz++) if ((dx || dz) && this.get(dim, cx + dx, cz + dz)) for (let s = 0; s < c.height >> 4; s++) dirty.add(`${cx + dx},${s},${cz + dz}`);
    }
    return [...dirty].map((k) => k.split(',').map(Number) as [number, number, number]);
  }

  clear(): void {
    this.cols.clear();
    this.blockEntities.clear();
    this.blockEntityAtCell.clear();
  }

  /** Block entities by column key "dim,cx,cz" (signs, beds, banners, skulls, flower pots, spawners…). */
  readonly blockEntities = new Map<string, BlockEntityDto[]>();
  private readonly blockEntityAtCell = new Map<string, BlockEntityDto>();

  /** The block entity at a world cell, if its column sent one (the mesher reads bed colours, skull types, pot contents). */
  blockEntityAt(dim: number, x: number, y: number, z: number): BlockEntityDto | undefined {
    return this.blockEntityAtCell.get(`${dim},${x},${y},${z}`);
  }

  /** Replaces a column's block entities; returns the sections to re-mesh (those holding an old or a new one). */
  setBlockEntities(dim: number, cx: number, cz: number, list: BlockEntityDto[]): Array<[number, number, number]> {
    const k = colKey(dim, cx, cz);
    const dirty = new Set<string>();
    for (const b of this.blockEntities.get(k) ?? []) {
      this.blockEntityAtCell.delete(`${dim},${b.x},${b.y},${b.z}`);
      dirty.add(`${cx},${b.y >> 4},${cz}`);
    }
    if (list.length) this.blockEntities.set(k, list);
    else this.blockEntities.delete(k);
    for (const b of list) {
      this.blockEntityAtCell.set(`${dim},${b.x},${b.y},${b.z}`, b);
      dirty.add(`${cx},${b.y >> 4},${cz}`);
    }
    return [...dirty].map((d) => d.split(',').map(Number) as [number, number, number]);
  }

  /**
   * Applies block changes in place ([x, y, z, id, meta] in `dim`). Returns the sections to re-mesh: the changed block's own
   * section, plus the neighbouring section across every boundary the block sits on (their faces, AO and chest pairs read it).
   * Changes in columns not held are returned in `missing` (the caller refetches them through the index). Light is not
   * recomputed here: the column's light hash changes too, so the index refetches the column's light shortly after.
   */
  patch(dim: number, blocks: Array<[number, number, number, number, number]>): { dirty: Array<[number, number, number]>; missing: Array<[number, number]>; applied: number } {
    const dirty = new Set<string>();
    const missing = new Set<string>();
    const tops = new Set<StoredColumn>();
    let applied = 0;
    for (const [x, y, z, id, meta] of blocks) {
      const cx = x >> 4, cz = z >> 4;
      const c = this.get(dim, cx, cz);
      if (!c) {
        missing.add(`${cx},${cz}`);
        continue;
      }
      if (y < 0 || y >= c.height) continue;
      const i = cellIndex(x & 15, y, z & 15);
      if (c.ids[i] === (id & 255) && c.meta[i] === (meta & 15)) continue;
      c.ids[i] = id & 255;
      c.meta[i] = meta & 15;
      tops.add(c);
      applied++;
      const sy = y >> 4, lx = x & 15, lz = z & 15, ly = y & 15;
      const xs = [0, ...(lx === 0 ? [-1] : lx === 15 ? [1] : [])];
      const zs = [0, ...(lz === 0 ? [-1] : lz === 15 ? [1] : [])];
      const ys = [0, ...(ly === 0 && sy > 0 ? [-1] : ly === 15 && (sy + 1) * 16 < c.height ? [1] : [])];
      for (const dx of xs) for (const dz of zs) for (const dy of ys) dirty.add(`${cx + dx},${sy + dy},${cz + dz}`);
    }
    for (const c of tops) c.top = topMap(c, this.version);
    // Relight around the changes (a dug hole lets the sky in, a removed torch goes dark) and re-mesh what that touched.
    const changed = blocks.filter(([x, , z]) => this.get(dim, x >> 4, z >> 4)?.light).map(([x, y, z]) => [x, y, z] as [number, number, number]);
    for (const d of relight(this, dim, changed)) dirty.add(d.join(','));
    return {
      dirty: [...dirty].map((k) => k.split(',').map(Number) as [number, number, number]),
      missing: [...missing].map((k) => k.split(',').map(Number) as [number, number]),
      applied,
    };
  }

  /** Block id / meta / light at world coordinates (0 / unknown outside the store). */
  blockAt(dim: number, x: number, y: number, z: number): { id: number; meta: number; col: StoredColumn } | null {
    const c = this.get(dim, x >> 4, z >> 4);
    if (!c || y < 0 || y >= c.height) return null;
    const i = cellIndex(x & 15, y, z & 15);
    return { id: c.ids[i], meta: c.meta[i], col: c };
  }
}

async function decodeEntry(dim: number, e: McwcEntry): Promise<StoredColumn | null> {
  if (e.data.length === 0) return null;
  const blocks = unpackSnapshot(await inflateRaw(e.data));
  let light: ColumnLight | null = null;
  if (e.light && e.light.length > 0) {
    const l = parseLight(await inflateRaw(e.light));
    if (l.sections * 16 === blocks.height) light = l; // a mismatched height is ignored, as the C# cache does
  }
  return { dim, cx: e.cx, cz: e.cz, hash: e.hash, seen: e.seenMs, by: e.by, live: e.live, ...blocks, light, top: new Int16Array(0) };
}

function topMap(c: StoredColumn, version: GameVersion): Int16Array {
  const t = tableFor(version);
  const top = new Int16Array(256).fill(-1);
  for (let x = 0; x < 16; x++)
    for (let z = 0; z < 16; z++) {
      for (let y = c.height - 1; y >= 0; y--) {
        const d = t[c.ids[cellIndex(x, y, z)]];
        if (d && d.flags & F_OPAQUE) {
          top[x * 16 + z] = y;
          break;
        }
      }
    }
  return top;
}

function sectionChanged(a: StoredColumn, b: StoredColumn, s: number): boolean {
  for (let x = 0; x < 16; x++)
    for (let z = 0; z < 16; z++) {
      const i = cellIndex(x, s * 16, z);
      for (let y = 0; y < 16; y++) if (a.ids[i + y] !== b.ids[i + y] || a.meta[i + y] !== b.meta[i + y]) return true;
    }
  const la = a.light, lb = b.light;
  if (!la || !lb) return la !== lb;
  for (let x = 0; x < 16; x++) for (let z = 0; z < 16; z++) for (let y = 0; y < 16; y++) if (lightPacked(la, x, s * 16 + y, z) !== lightPacked(lb, x, s * 16 + y, z)) return true;
  return false;
}

function markAround(set: Set<string>, cx: number, s: number, cz: number): void {
  for (let dx = -1; dx <= 1; dx++) for (let dz = -1; dz <= 1; dz++) for (let ds = -1; ds <= 1; ds++) if (s + ds >= 0) set.add(`${cx + dx},${s + ds},${cz + dz}`);
}

function markAll(set: Set<string>, cx: number, cz: number, sections: number): void {
  for (let dx = -1; dx <= 1; dx++) for (let dz = -1; dz <= 1; dz++) for (let s = 0; s < sections; s++) set.add(`${cx + dx},${s},${cz + dz}`);
}
