// Local light recomputation after block deltas (worker side): a block change can open a cave to the sky, remove a torch or
// shade the ground, and the payload's light only comes back with the next column refetch. Instead the region around the
// changes (±15 blocks horizontally, the full height) is relit the vanilla way: sky light falls straight down through
// cells that block nothing and spreads sideways losing one level per step (more through water, ice and leaves); block light
// floods out from emitters. Cells just outside the region keep their light and seed it, so the patch joins seamlessly.
// Columns without light (remembered) are left alone: they keep the estimate the mesher derives from the opaque top.

import { cellIndex, lightPacked, SECTION_CELLS, type ColumnLight } from '../wire/chunk';
import { F_OPAQUE, tableFor, type GameVersion } from '../world/blocks';
import type { ColumnStore, StoredColumn } from './columns';

/** Light emitted by a block id (vanilla light values, b1.8.1 and 1.12.2 numbering). */
export function emission(id: number): number {
  switch (id) {
    case 10: case 11: case 51: case 89: case 91: case 119: case 124: case 138: case 169: case 209: return 15;
    case 50: case 198: return 14;
    case 62: return 13;
    case 90: return 11;
    case 74: case 94: return 9;
    case 76: case 130: return 7;
    case 213: return 3;
    case 39: case 117: case 120: case 122: return 1;
    default: return 0;
  }
}

/** How much light a block takes away when light passes through it (15 = stops it). */
export function opacity(version: GameVersion, id: number): number {
  if (id === 0) return 0;
  const d = tableFor(version)[id];
  if (d && d.flags & F_OPAQUE) return 15;
  if (id === 8 || id === 9 || id === 79 || id === 212) return 3; // water, ice, frosted ice
  if (id === 18 || id === 161 || id === 30) return 1; // leaves, cobweb
  return 0;
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
  const version = store.version;
  let x0 = Infinity, x1 = -Infinity, z0 = Infinity, z1 = -Infinity;
  for (const [x, , z] of changes) {
    x0 = Math.min(x0, x - 15); x1 = Math.max(x1, x + 15);
    z0 = Math.min(z0, z - 15); z1 = Math.max(z1, z + 15);
  }
  const W = x1 - x0 + 1, D = z1 - z0 + 1;
  // Region columns (those with light only), by block column.
  const colAt = (x: number, z: number): StoredColumn | null => {
    const c = store.get(dim, x >> 4, z >> 4);
    return c && c.light ? c : null;
  };
  let H = 0;
  for (let cx = x0 >> 4; cx <= x1 >> 4; cx++) for (let cz = z0 >> 4; cz <= z1 >> 4; cz++) H = Math.max(H, store.get(dim, cx, cz)?.light ? store.get(dim, cx, cz)!.height : 0);
  if (!H) return [];
  const N = W * D * H;
  const sky = new Uint8Array(N), blk = new Uint8Array(N), op = new Uint8Array(N), inside = new Uint8Array(N);
  const idx = (x: number, y: number, z: number) => ((y * D + (z - z0)) * W + (x - x0));
  const before = new Uint8Array(N);
  // Load: opacity, and the old light (kept for the boundary seeds and the change detection).
  for (let x = x0; x <= x1; x++)
    for (let z = z0; z <= z1; z++) {
      const c = colAt(x, z);
      if (!c) continue;
      for (let y = 0; y < c.height; y++) {
        const i = idx(x, y, z);
        const id = c.ids[cellIndex(x & 15, y, z & 15)];
        op[i] = opacity(version, id);
        before[i] = lightPacked(c.light!, x & 15, y, z & 15);
        inside[i] = 1;
        blk[i] = emission(id);
      }
    }
  const queue = new Int32Array(N * 3);
  // Sky light: straight down from the top until something takes light away.
  let qh = 0, qt = 0;
  for (let x = x0; x <= x1; x++)
    for (let z = z0; z <= z1; z++) {
      const c = colAt(x, z);
      if (!c) continue;
      let level = 15;
      for (let y = c.height - 1; y >= 0; y--) {
        const i = idx(x, y, z);
        const o = op[i];
        if (o >= 15) level = 0;
        else if (o > 0) level = Math.max(0, level - o);
        sky[i] = level;
        if (level > 0) queue[qt++] = i;
      }
    }
  // Boundary seeds: the cells just outside the region keep their light.
  const seed = (arr: Uint8Array, shift: number) => {
    for (let x = x0 - 1; x <= x1 + 1; x++)
      for (let z = z0 - 1; z <= z1 + 1; z++) {
        if (x >= x0 && x <= x1 && z >= z0 && z <= z1) continue;
        const c = store.get(dim, x >> 4, z >> 4);
        if (!c || !c.light) continue;
        const nx = Math.min(x1, Math.max(x0, x)), nz = Math.min(z1, Math.max(z0, z));
        for (let y = 0; y < Math.min(H, c.height); y++) {
          const v = (lightPacked(c.light, x & 15, y, z & 15) >> shift) & 15;
          const i = idx(nx, y, nz);
          if (!inside[i] || v <= 1) continue;
          const want = v - Math.max(1, op[i]);
          if (want > arr[i] && op[i] < 15) {
            arr[i] = want;
            if (qt < queue.length) queue[qt++] = i;
          }
        }
      }
  };
  const spread = (arr: Uint8Array) => {
    while (qh < qt) {
      const i = queue[qh++];
      const v = arr[i];
      if (v <= 1) continue;
      const x = i % W, rest = (i - x) / W, z = rest % D, y = (rest - z) / D;
      const nb = [x > 0 ? i - 1 : -1, x < W - 1 ? i + 1 : -1, z > 0 ? i - W : -1, z < D - 1 ? i + W : -1, y > 0 ? i - W * D : -1, y < H - 1 ? i + W * D : -1];
      for (const j of nb) {
        if (j < 0 || !inside[j] || op[j] >= 15) continue;
        const nv = v - Math.max(1, op[j]);
        if (nv > arr[j]) {
          arr[j] = nv;
          if (qt < queue.length) queue[qt++] = j;
        }
      }
    }
  };
  seed(sky, 4);
  spread(sky);
  qh = qt = 0;
  for (let i = 0; i < N; i++) if (blk[i] > 0) queue[qt++] = i;
  seed(blk, 0);
  spread(blk);
  // Write back; collect the sections whose light changed (their mesh and the neighbours' faces read it).
  const dirty = new Set<string>();
  for (let x = x0; x <= x1; x++)
    for (let z = z0; z <= z1; z++) {
      const c = colAt(x, z);
      if (!c) continue;
      for (let y = 0; y < c.height; y++) {
        const i = idx(x, y, z);
        const v = (sky[i] << 4) | blk[i];
        if (v === before[i]) continue;
        sectionArray(c.light!, y >> 4)[((x & 15) << 8) | ((z & 15) << 4) | (y & 15)] = v;
        const s = y >> 4, cx = x >> 4, cz = z >> 4;
        dirty.add(`${cx},${s},${cz}`);
        // A face of the neighbouring section can read this cell's light.
        if ((x & 15) === 0) dirty.add(`${cx - 1},${s},${cz}`);
        if ((x & 15) === 15) dirty.add(`${cx + 1},${s},${cz}`);
        if ((z & 15) === 0) dirty.add(`${cx},${s},${cz - 1}`);
        if ((z & 15) === 15) dirty.add(`${cx},${s},${cz + 1}`);
        if ((y & 15) === 0 && s > 0) dirty.add(`${cx},${s - 1},${cz}`);
        if ((y & 15) === 15) dirty.add(`${cx},${s + 1},${cz}`);
      }
    }
  return [...dirty].map((k) => k.split(',').map(Number) as [number, number, number]);
}
