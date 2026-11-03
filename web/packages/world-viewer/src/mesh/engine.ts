// The worker-side world: decoded columns + the state table + the mesher and queries. Runs inside the Web Worker, or on the
// calling thread (InlineMeshHost) where workers are unavailable (tests, SSR-less fallbacks).

import type { GameVersion } from '../world/blocks';
import { ColumnStore, type ApplyResult } from './columns';
import { meshSection, type CutParams, type SectionMesh } from './mesher';
import { pick, viewInfo, type PickHit, type V3, type ViewInfo } from './query';
import { buildStateTable, type StateTable } from './states';
import { sectionVisibility } from './visibility';
import { lightPacked } from '../wire/chunk';
import type { BlockEntityDto } from '../types';
import type { MeshAssets } from './source';

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

export class MeshEngine {
  readonly store = new ColumnStore();
  table: StateTable = buildStateTable('b1.8.1', null);

  /** The version and its bundled block assets (null = flat palette shapes); `textured` = quads sample the block atlas. */
  configure(version: GameVersion, assets: MeshAssets | null, textured = assets != null): void {
    this.store.version = version;
    this.table = buildStateTable(version, assets && (assets.kind === version ? assets : null), textured);
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

  pick(dim: number, o: V3, d: V3, maxDist: number, cut: CutParams | null): PickHit | null {
    return pick(this.store, this.table, dim, o, d, maxDist, cut);
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
