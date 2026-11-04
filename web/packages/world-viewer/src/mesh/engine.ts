// The worker-side world: decoded columns + the state table + the mesher and queries. Runs inside the Web Worker, or on the
// calling thread (InlineMeshHost) where workers are unavailable (tests, SSR-less fallbacks).

import type { GameVersion } from '../world/blocks';
import { ColumnStore, type ApplyResult } from './columns';
import { meshSection, type CutParams, type SectionMesh } from './mesher';
import { occluded, pick, viewInfo, type PickHit, type V3, type ViewInfo } from './query';
import { buildStateTable, type StateTable } from './states';
import { sectionVisibility } from './visibility';
import { lightPacked } from '../wire/chunk';
import type { BlockEntityDto } from '../types';
import type { MeshAssets } from './source';
import { meshHolograms, type HoloRequest, type HoloResult } from './holo';

export interface PatchResult {
  dirty: Array<[number, number, number]>;
  missing: Array<[number, number]>;
  applied: number;
}

export interface TouchCol {
  cx: number;
  cz: number;
  seen: number;
  by: string | null;
  live: boolean;
}

/** A query box's whole-cell bounds (y from 0). */
interface Box {
  x1: number;
  x2: number;
  y1: number;
  y2: number;
  z1: number;
  z2: number;
}

function boxOf(min: V3, max: V3): Box {
  return {
    x1: Math.floor(Math.min(min[0], max[0])),
    x2: Math.floor(Math.max(min[0], max[0])),
    y1: Math.max(0, Math.floor(Math.min(min[1], max[1]))),
    y2: Math.floor(Math.max(min[1], max[1])),
    z1: Math.floor(Math.min(min[2], max[2])),
    z2: Math.floor(Math.max(min[2], max[2])),
  };
}

/** One box query in progress: its hits (flat, six numbers each) and the most numbers it may hold. */
interface BoxQuery {
  dim: number;
  box: Box;
  out: number[];
  cap: number;
}

export class MeshEngine {
  readonly store = new ColumnStore();
  table: StateTable = buildStateTable('b1.8.1', null);

  /** The version and its bundled block assets (null = flat palette shapes); `textured` = quads sample the block atlas. */
  configure(version: GameVersion, assets: MeshAssets | null, textured = assets != null, fastLeaves = false): void {
    this.store.version = version;
    this.table = buildStateTable(version, assets && (assets.kind === version ? assets : null), textured, fastLeaves);
  }

  apply(buf: ArrayBuffer): Promise<ApplyResult> {
    return this.store.applyPayload(buf);
  }

  touch(dim: number, cols: TouchCol[]): Array<[number, number, number]> {
    const out: Array<[number, number, number]> = [];
    for (const c of cols) out.push(...this.store.touch(dim, c.cx, c.cz, c.seen, c.by, c.live));
    return out;
  }

  patch(dim: number, blocks: Array<[number, number, number, number, number]>): PatchResult {
    return this.store.patch(dim, blocks);
  }

  drop(dim: number, cols: Array<[number, number]>): Array<[number, number, number]> {
    return this.store.drop(dim, cols);
  }

  mesh(dim: number, sections: Array<[number, number, number]>, cut: CutParams | null): SectionMesh[] {
    return sections.map(([cx, sy, cz]) => {
      const m = meshSection(this.store, dim, cx, sy, cz, this.table, cut && sectionTouchesCut(cx, sy, cz, cut) ? cut : null);
      m.vis = sectionVisibility(this.store, dim, cx, sy, cz, this.table); // cave culling (orchestrator hunk)
      return m;
    });
  }

  /** [holograms] Queued build cells / a placement as merged block meshes, built cells left out (mesh/holo.ts). */
  meshHolo(dim: number, reqs: HoloRequest[]): HoloResult[] {
    return meshHolograms(this.store, dim, this.table, reqs);
  }

  pick(dim: number, o: V3, d: V3, maxDist: number, cut: CutParams | null): PickHit | null {
    return pick(this.store, this.table, dim, o, d, maxDist, cut);
  }

  /** [blocks] id, meta and packed light (or -1) at each integer point; id -1 where the column is not held. */
  blocksAt(dim: number, pts: number[]): number[] {
    const out: number[] = [];
    for (let i = 0; i + 2 < pts.length; i += 3) {
      const b = this.store.blockAt(dim, Math.floor(pts[i]), Math.floor(pts[i + 1]), Math.floor(pts[i + 2]));
      if (!b) out.push(-1, 0, -1);
      else {
        const l = b.col.light;
        out.push(b.id, b.meta, l ? lightPacked(l, Math.floor(pts[i]) & 15, Math.floor(pts[i + 1]), Math.floor(pts[i + 2]) & 15) : -1);
      }
    }
    return out;
  }

  /**
   * [scan] Every block of a box whose id is in `ids` (all non-air when `ids` is empty), as flat [x, y, z, id, meta, below-id, …]
   * (below-id = the block under it: crops on farmland, cane on cane). At most `max` hits; columns not held are skipped.
   */
  scan(dim: number, min: V3, max: V3, ids: number[], maxHits = 4096): number[] {
    const q: BoxQuery = { dim, box: boxOf(min, max), out: [], cap: maxHits * 6 };
    const want = ids.length ? new Set(ids) : null;
    for (let x = q.box.x1; x <= q.box.x2; x++)
      for (let z = q.box.z1; z <= q.box.z2; z++) {
        const col = this.store.get(dim, x >> 4, z >> 4);
        if (col && this.scanColumn(q, x, z, col.height, want)) return q.out;
      }
    return q.out;
  }

  /** One column of a scan; true when the hit cap is reached. */
  private scanColumn(q: BoxQuery, x: number, z: number, height: number, want: Set<number> | null): boolean {
    const { y1, y2 } = q.box;
    let below = y1 > 0 ? this.idAt(q.dim, x, y1 - 1, z) : 0;
    for (let y = y1; y <= Math.min(y2, height - 1); y++) {
      const b = this.store.blockAt(q.dim, x, y, z);
      const id = b?.id ?? 0;
      if (id !== 0 && (!want || want.has(id))) {
        q.out.push(x, y, z, id, b!.meta, below);
        if (q.out.length >= q.cap) return true;
      }
      below = id;
    }
    return false;
  }

  /** The block id at a cell (0 for air or not held). */
  private idAt(dim: number, x: number, y: number, z: number): number {
    return this.store.blockAt(dim, x, y, z)?.id ?? 0;
  }

  /**
   * Per column of the box's x/z rect, the column's top: the highest block in [min y, max y] that is neither air nor in `skip`
   * (trees, plants, snow…), then the blocks down from it as [x, y, z, id, meta, below, …] (air left out): first whatever
   * non-air stands on the top (up to `above` cells over it, never over max y: plants), then the top and up to `depth − 1` cells under it, never
   * below min y. Columns with no top in the range, or not held, give nothing. Stops after `maxHits` blocks.
   */
  stacks(dim: number, min: V3, max: V3, skip: number[], depth: number, above = 2, maxHits = 8192): number[] {
    const q: BoxQuery = { dim, box: boxOf(min, max), out: [], cap: maxHits * 6 };
    const skipSet = new Set(skip);
    for (let x = q.box.x1; x <= q.box.x2; x++)
      for (let z = q.box.z1; z <= q.box.z2; z++) {
        const col = this.store.get(dim, x >> 4, z >> 4);
        if (!col) continue;
        const top = this.columnTop(q, x, z, col.height, skipSet);
        if (top >= 0 && this.stackColumn(q, x, z, col.height, top, depth, above)) return q.out;
      }
    return q.out;
  }

  /** The highest cell of the column in the box's height range that is neither air nor skipped (−1: none). */
  private columnTop(q: BoxQuery, x: number, z: number, height: number, skip: Set<number>): number {
    for (let y = Math.min(q.box.y2, height - 1); y >= q.box.y1; y--) {
      const id = this.idAt(q.dim, x, y, z);
      if (id !== 0 && !skip.has(id)) return y;
    }
    return -1;
  }

  /** The column's stack from `above` cells over its top down `depth` cells; true when the hit cap is reached. */
  private stackColumn(q: BoxQuery, x: number, z: number, height: number, top: number, depth: number, above: number): boolean {
    const lo = Math.max(q.box.y1, top - Math.max(1, depth) + 1);
    for (let y = Math.min(height - 1, q.box.y2, top + above); y >= lo; y--) {
      const b = this.store.blockAt(q.dim, x, y, z);
      if (!b || b.id === 0) continue;
      q.out.push(x, y, z, b.id, b.meta, y > 0 ? this.idAt(q.dim, x, y - 1, z) : 0);
      if (q.out.length >= q.cap) return true;
    }
    return false;
  }

  /** Packed light (sky << 4 | block) at each point (entities take the light of their cell); -1 = unknown. */
  lights(dim: number, pts: number[]): number[] {
    const out: number[] = [];
    for (let i = 0; i + 2 < pts.length; i += 3) {
      const x = Math.floor(pts[i]), y = Math.floor(pts[i + 1]), z = Math.floor(pts[i + 2]);
      const c = this.store.get(dim, x >> 4, z >> 4);
      if (!c || !c.light) out.push(-1);
      else if (y >= c.height) out.push(0xf0);
      else out.push(lightPacked(c.light, x & 15, Math.max(0, y), z & 15));
    }
    return out;
  }

  viewInfo(dim: number, cam: V3, body: V3 | null, cut: CutParams | null): ViewInfo {
    return viewInfo(this.store, this.table, dim, cam, body, cut);
  }

  occluded(dim: number, cam: V3, bodies: number[]): boolean[] {
    return occluded(this.store, this.table, dim, cam, bodies);
  }

  /** Stores block entities per column; the sections they sit in are returned for re-meshing. */
  setBlockEntities(dim: number, cols: Array<{ cx: number; cz: number; entities: BlockEntityDto[] }>): Array<[number, number, number]> {
    const out: Array<[number, number, number]> = [];
    for (const c of cols) out.push(...this.store.setBlockEntities(dim, c.cx, c.cz, c.entities));
    return out;
  }

  clear(): void {
    this.store.clear();
  }
}

/** Does the roof cut reach into section (cx, sy, cz)? */
export function sectionTouchesCut(cx: number, sy: number, cz: number, cut: CutParams): boolean {
  // A section whose top layer sits right under the cut still needs it: its top faces become the cut's cap.
  if ((sy + 1) * 16 < cut.y) return false;
  const nx = Math.max(cx * 16, Math.min(cut.x, cx * 16 + 16));
  const nz = Math.max(cz * 16, Math.min(cut.z, cz * 16 + 16));
  return (nx - cut.x) ** 2 + (nz - cut.z) ** 2 <= (cut.r + 1) ** 2;
}
