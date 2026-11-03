// The worker-side world: decoded columns + the state table + the mesher and queries. Runs inside the Web Worker, or on the
// calling thread (InlineMeshHost) where workers are unavailable (tests, SSR-less fallbacks).

import type { AtlasMap } from '../atlas/types';
import type { GameVersion } from '../world/blocks';
import { ColumnStore, type ApplyResult } from './columns';
import { meshSection, type CutParams, type SectionMesh } from './mesher';
import { pick, viewInfo, type PickHit, type V3, type ViewInfo } from './query';
import { buildStateTable, type StateTable } from './states';
import { sectionVisibility } from './visibility';

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

  configure(version: GameVersion, atlas: AtlasMap | null): void {
    this.store.version = version;
    this.table = buildStateTable(version, atlas && atlas.format === 'mineclaw-atlas' ? atlas : null);
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

  viewInfo(dim: number, cam: V3, body: V3 | null, cut: CutParams | null): ViewInfo {
    return viewInfo(this.store, this.table, dim, cam, body, cut);
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
