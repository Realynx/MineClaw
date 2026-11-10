// The wheat field bug (live 11-05: "different sized rectangles across the wheat field"): the bots' harvests and replants
// each relit a ±15-block region around them with farmland letting the sky through, while the server's light (vanilla:
// farmland, slabs and stairs take all light) keeps it dark, so every relit rectangle lit the crops' lower halves
// differently from the field around it. These pin that a field relit delta by delta equals the field lit from scratch
// (light and section meshes), that a crop on farmland takes its own cell's light, flat (vanilla's renderModelFlat for the
// crop model, ambientocclusion false; b1.8.1's crops render type likewise), and that farmland, holding no light of its own,
// is lit from around it (vanilla's neighbour brightness), so its sides between farmland cells don't draw dark seams.

import { describe, expect, it } from 'vitest';
import { meshSection, type SectionMesh } from '../src/mesh/mesher';
import type { ColumnStore } from '../src/mesh/columns';
import type { StateTable } from '../src/mesh/states';
import type { GameVersion } from '../src/world/blocks';
import { CX0, CX1, FieldWorld, GROUND, farmDeltas, inField } from './fieldWorld';
import { Digest } from './digest';
import { noPacks, table1122, tableBeta } from './pack';

const VERSIONS: Array<[GameVersion, () => StateTable]> = [['b1.8.1', tableBeta], ['1.12.2', table1122]];

/** Digests of every section around the field's surface (sections 3 and 4 hold y 48..79). */
function meshDigests(store: ColumnStore, table: StateTable): string[] {
  const out: string[] = [];
  for (let cx = CX0; cx <= CX1; cx++)
    for (let cz = CX0; cz <= CX1; cz++)
      for (const sy of [3, 4]) {
        const d = new Digest();
        const m = meshSection(store, 0, cx, sy, cz, table);
        for (const a of [m.solid, m.translucent]) for (const b of a ? [a.pos, a.uv, a.tile, a.color, a.light, a.cell, a.index] : []) d.bytes(b);
        out.push(`${cx},${sy},${cz} ${d.hex()}`);
      }
  return out;
}

/** Every vertex of the blocks of `id` in a section mesh: its cell (world) and its light bytes. */
function vertices(m: SectionMesh, w: FieldWorld, id: number): Array<{ x: number; y: number; z: number; light: number[] }> {
  const out: Array<{ x: number; y: number; z: number; light: number[] }> = [];
  for (const a of [m.solid, m.translucent]) {
    if (!a) continue;
    for (let v = 0; v < a.cell.length / 4; v++) {
      const x = m.cx * 16 + a.cell[v * 4], y = m.sy * 16 + a.cell[v * 4 + 1], z = m.cz * 16 + a.cell[v * 4 + 2];
      if (w.id(x, y, z) === id) out.push({ x, y, z, light: [...a.light.subarray(v * 4, v * 4 + 3)] });
    }
  }
  return out;
}

describe.skipIf(noPacks)('wheat field light', () => {
  for (const [version, table] of VERSIONS)
    it(`a field relit after 200 harvest / replant deltas equals the field lit from scratch (${version})`, async () => {
      const w = new FieldWorld();
      const store = await w.store(version);
      expect(w.diff(w.lightOf(store), w.light())).toEqual([]);
      for (const d of farmDeltas(w, 200)) store.patch(0, [d]);
      const full = await w.store(version);
      expect(w.diff(w.lightOf(store), w.lightOf(full))).toEqual([]);
      expect(meshDigests(store, table())).toEqual(meshDigests(full, table()));
    });

  for (const [version, table] of VERSIONS)
    it(`a crop on farmland takes its own cell's light on every vertex, without occlusion (${version})`, async () => {
      const w = new FieldWorld();
      for (const d of farmDeltas(w, 60)) void d;
      const store = await w.store(version);
      const light = w.light();
      let crops = 0, nearTorch = 0;
      for (let cx = 0; cx <= 1; cx++)
        for (let cz = 0; cz <= 1; cz++)
          for (const v of vertices(meshSection(store, 0, cx, (GROUND + 1) >> 4, cz, table()), w, 59)) {
            const own = light[w.index(v.x, v.y, v.z)];
            expect(inField(v.x, v.z)).toBe(true);
            expect(v.light).toEqual([(own >> 4) * 17, (own & 15) * 17, 255]);
            crops++;
            if (own & 15) nearTorch++;
          }
      expect(crops).toBeGreaterThan(1000);
      expect(nearTorch).toBeGreaterThan(0);
    });

  for (const [version, table] of VERSIONS)
    it(`farmland between farmland is lit from its neighbours: full sky on every vertex (${version})`, async () => {
      const w = new FieldWorld();
      const store = await w.store(version);
      // Farmland all round (the eight cells about it): water or the field's edge would rightly take a level or two off a corner.
      const inner = (x: number, z: number) => [-1, 0, 1].every((dx) => [-1, 0, 1].every((dz) => w.id(x + dx, GROUND, z + dz) === 60));
      let n = 0;
      for (let cx = 0; cx <= 1; cx++)
        for (let cz = 0; cz <= 1; cz++)
          for (const v of vertices(meshSection(store, 0, cx, GROUND >> 4, cz, table()), w, 60)) {
            if (!inner(v.x, v.z)) continue;
            expect(v.light[0]).toBe(255);
            n++;
          }
      expect(n).toBeGreaterThan(3000);
    });
});
