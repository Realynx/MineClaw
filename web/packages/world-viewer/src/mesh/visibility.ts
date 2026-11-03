// Cave culling, Minecraft's chunk visibility graph: for each 16³ section, which pairs of its six faces are connected through
// cells that don't block sight (computed in the worker with the mesh); per view, a breadth-first walk from the camera's
// section only enters a neighbour when the section it leaves connects the face it came in by to the face it leaves by, and
// never turns back toward the camera. Sections the walk never reaches (caves behind solid rock, the far side of a hill seen
// from a tunnel) are not drawn.

import { cellIndex } from '../wire/chunk';
import { F_OPAQUE } from '../world/blocks';
import type { ColumnStore } from './columns';
import type { StateTable } from './states';

/** Faces in Minecraft side order: 0 down, 1 up, 2 north, 3 south, 4 west, 5 east. */
export const DIRS: ReadonlyArray<readonly [number, number, number]> = [
  [0, -1, 0], [0, 1, 0], [0, 0, -1], [0, 0, 1], [-1, 0, 0], [1, 0, 0],
];
export const OPPOSITE = [1, 0, 3, 2, 5, 4];

/** Bit of the face pair (a, b) in the 15-bit mask (a ≠ b, order-free). */
export function pairBit(a: number, b: number): number {
  if (a > b) [a, b] = [b, a];
  // Pairs in order (0,1)(0,2)…(0,5)(1,2)…(4,5).
  const base = [0, 5, 9, 12, 14][a];
  return 1 << (base + (b - a - 1));
}

export const ALL_VISIBLE = 0x7fff;

export function connects(mask: number, a: number, b: number): boolean {
  return a === b || (mask & pairBit(a, b)) !== 0;
}

const seen = new Uint8Array(4096);
const stack = new Int32Array(4096);

/** The face-connectivity mask of section (cx, sy, cz); all-visible when the section is empty or not held. */
export function sectionVisibility(store: ColumnStore, dim: number, cx: number, sy: number, cz: number, t: StateTable): number {
  const c = store.get(dim, cx, cz);
  if (!c || sy * 16 >= c.height) return ALL_VISIBLE;
  seen.fill(0);
  let opaque = 0;
  for (let x = 0; x < 16; x++)
    for (let z = 0; z < 16; z++) {
      const base = cellIndex(x, sy * 16, z);
      for (let y = 0; y < 16; y++) {
        const id = c.ids[base + y];
        if (id && t.flags[(id << 4) | c.meta[base + y]] & F_OPAQUE) {
          seen[(x << 8) | (z << 4) | y] = 1;
          opaque++;
        }
      }
    }
  if (opaque === 0) return ALL_VISIBLE;
  if (opaque === 4096) return 0;
  let mask = 0;
  for (let start = 0; start < 4096; start++) {
    if (seen[start]) continue;
    let faces = 0, sp = 0;
    stack[sp++] = start;
    seen[start] = 1;
    while (sp) {
      const i = stack[--sp];
      const x = i >> 8, z = (i >> 4) & 15, y = i & 15;
      if (y === 0) faces |= 1;
      if (y === 15) faces |= 2;
      if (z === 0) faces |= 4;
      if (z === 15) faces |= 8;
      if (x === 0) faces |= 16;
      if (x === 15) faces |= 32;
      const push = (j: number) => {
        if (!seen[j]) {
          seen[j] = 1;
          stack[sp++] = j;
        }
      };
      if (y > 0) push(i - 1);
      if (y < 15) push(i + 1);
      if (z > 0) push(i - 16);
      if (z < 15) push(i + 16);
      if (x > 0) push(i - 256);
      if (x < 15) push(i + 256);
    }
    for (let a = 0; a < 6; a++) for (let b = a + 1; b < 6; b++) if (faces & (1 << a) && faces & (1 << b)) mask |= pairBit(a, b);
    if (mask === ALL_VISIBLE) break;
  }
  return mask;
}

/**
 * The sections a camera at section (sx, sy, sz) can see through the visibility graph, within `range` sections horizontally
 * and `ymax` sections vertically. `vis(cx, sy, cz)` gives a section's mask (ALL_VISIBLE when unknown). Returns "cx,sy,cz" keys.
 */
export function visibleSections(sx: number, sy: number, sz: number, range: number, ymax: number, vis: (cx: number, sy: number, cz: number) => number, view?: { x: number; y: number; z: number }): Set<string> {
  const out = new Set<string>();
  const startKey = `${sx},${sy},${sz}`;
  out.add(startKey);
  // Queue entries: section, the face it was entered by, the directions used so far (bitmask: never step back).
  const q: Array<[number, number, number, number, number]> = [];
  for (let d = 0; d < 6; d++) q.push([sx + DIRS[d][0], sy + DIRS[d][1], sz + DIRS[d][2], OPPOSITE[d], 1 << d]);
  for (let h = 0; h < q.length; h++) {
    const [cx, cy, cz, entered, used] = q[h];
    if (Math.abs(cx - sx) > range || Math.abs(cz - sz) > range || cy < -1 || cy > ymax) continue;
    const k = `${cx},${cy},${cz}`;
    if (out.has(k)) continue;
    // Behind the camera's view direction: skip (Minecraft's frustum-aware traversal; three still frustum-culls meshes).
    if (view && (cx + 0.5 - sx) * view.x + (cy + 0.5 - sy) * view.y + (cz + 0.5 - sz) * view.z < -1.5) continue;
    out.add(k);
    const m = cy < 0 || cy >= ymax ? ALL_VISIBLE : vis(cx, cy, cz);
    for (let d = 0; d < 6; d++) {
      if (used & (1 << OPPOSITE[d])) continue;
      if (!connects(m, entered, d)) continue;
      q.push([cx + DIRS[d][0], cy + DIRS[d][1], cz + DIRS[d][2], OPPOSITE[d], used | (1 << d)]);
    }
  }
  return out;
}
