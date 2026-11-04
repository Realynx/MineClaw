// Cave culling, Minecraft's chunk visibility graph: for each 16³ section, which pairs of its six faces are connected through
// cells that don't block sight (computed in the worker with the mesh); per view, a breadth-first walk from the camera's
// section only enters a neighbour when the section it leaves connects the face it came in by to the face it leaves by, and
// never turns back toward the camera. Sections the walk never reaches (caves behind solid rock, the far side of a hill seen
// from a tunnel) are not drawn.

import { cellIndex } from '../wire/chunk';
import { F_OPAQUE } from '../world/blocks';
import type { ColumnStore, StoredColumn } from './columns';
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
let sp = 0;

/** The face-connectivity mask of section (cx, sy, cz); all-visible when the section is empty or not held. */
export function sectionVisibility(store: ColumnStore, dim: number, cx: number, sy: number, cz: number, t: StateTable): number {
  const c = store.get(dim, cx, cz);
  if (!c || sy * 16 >= c.height) return ALL_VISIBLE;
  const opaque = markOpaque(c, sy, t);
  if (opaque === 0) return ALL_VISIBLE;
  if (opaque === 4096) return 0;
  // Every open region (flood-filled from each unseen cell) connects the faces it touches.
  let mask = 0;
  for (let start = 0; start < 4096; start++) {
    if (seen[start]) continue;
    mask |= facePairs(floodFaces(start));
    if (mask === ALL_VISIBLE) break;
  }
  return mask;
}

/** Marks the section's opaque cells seen (cell x << 8 | z << 4 | y); returns how many there are. */
function markOpaque(c: StoredColumn, sy: number, t: StateTable): number {
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
  return opaque;
}

/** Flood-fills the open region holding `start`; returns the faces it touches (bit per face, Minecraft side order). */
function floodFaces(start: number): number {
  let faces = 0;
  sp = 0;
  stack[sp++] = start;
  seen[start] = 1;
  while (sp) {
    const i = stack[--sp];
    const x = i >> 8, z = (i >> 4) & 15, y = i & 15;
    faces |= edgeFaces(x, y, z);
    if (y > 0) visit(i - 1);
    if (y < 15) visit(i + 1);
    if (z > 0) visit(i - 16);
    if (z < 15) visit(i + 16);
    if (x > 0) visit(i - 256);
    if (x < 15) visit(i + 256);
  }
  return faces;
}

function visit(j: number): void {
  if (seen[j]) return;
  seen[j] = 1;
  stack[sp++] = j;
}

/** The section faces a cell lies on. */
function edgeFaces(x: number, y: number, z: number): number {
  let faces = 0;
  if (y === 0) faces |= 1;
  if (y === 15) faces |= 2;
  if (z === 0) faces |= 4;
  if (z === 15) faces |= 8;
  if (x === 0) faces |= 16;
  if (x === 15) faces |= 32;
  return faces;
}

/** Every pair of the faces in `faces` as the mask's pair bits. */
function facePairs(faces: number): number {
  let mask = 0;
  for (let a = 0; a < 6; a++) for (let b = a + 1; b < 6; b++) if (faces & (1 << a) && faces & (1 << b)) mask |= pairBit(a, b);
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
  const q: WalkStep[] = [];
  for (let d = 0; d < 6; d++) q.push([sx + DIRS[d][0], sy + DIRS[d][1], sz + DIRS[d][2], OPPOSITE[d], 1 << d]);
  for (let h = 0; h < q.length; h++) {
    const [cx, cy, cz, entered, used] = q[h];
    if (!inReach(cx - sx, cz - sz, cy, range, ymax)) continue;
    const k = `${cx},${cy},${cz}`;
    if (out.has(k)) continue;
    // Behind the camera's view direction: skip (Minecraft's frustum-aware traversal; three still frustum-culls meshes).
    if (view && behind(view, cx + 0.5 - sx, cy + 0.5 - sy, cz + 0.5 - sz)) continue;
    out.add(k);
    stepOut(q, q[h], cy < 0 || cy >= ymax ? ALL_VISIBLE : vis(cx, cy, cz));
  }
  return out;
}

/** Within `range` sections horizontally of the camera's section, and from one layer under the world to `ymax`. */
function inReach(dx: number, dz: number, cy: number, range: number, ymax: number): boolean {
  return Math.abs(dx) <= range && Math.abs(dz) <= range && cy >= -1 && cy <= ymax;
}

/** A section centre offset (dx, dy, dz) from the camera's section lies well behind the view direction. */
function behind(view: { x: number; y: number; z: number }, dx: number, dy: number, dz: number): boolean {
  return dx * view.x + dy * view.y + dz * view.z < -1.5;
}

type WalkStep = [cx: number, cy: number, cz: number, entered: number, used: number];

/** Queues the neighbours a section's mask lets the walk reach from the face it entered by (never back toward the camera). */
function stepOut(q: WalkStep[], [cx, cy, cz, entered, used]: WalkStep, m: number): void {
  for (let d = 0; d < 6; d++) {
    if (used & (1 << OPPOSITE[d])) continue;
    if (!connects(m, entered, d)) continue;
    q.push([cx + DIRS[d][0], cy + DIRS[d][1], cz + DIRS[d][2], OPPOSITE[d], used | (1 << d)]);
  }
}

/**
 * Where the cave-culling walk starts for a camera at `cam` looking along `dir` (unit), the walk's top layer `ymax`, and the
 * drawn area's centre (in chunks). Normally the camera itself. A camera far above the world (the top-down and iso views
 * zoomed out sit hundreds of blocks up) would start in a layer the walk never leaves and cull everything, so it enters
 * along the view ray just above the world instead, with `extra` chunks of range to still reach the drawn area. Null: the
 * camera is above the world looking up or level, so nothing is culled.
 */
export function walkStart(cam: { x: number; y: number; z: number }, dir: { x: number; y: number; z: number }, ymax: number, ccx: number, ccz: number): { x: number; y: number; z: number; extra: number } | null {
  const ceil = (ymax + 1) * 16;
  if (cam.y < ceil + 16) return { x: cam.x, y: cam.y, z: cam.z, extra: 0 };
  if (dir.y > -0.05) return null;
  const t = (cam.y - (ceil + 8)) / -dir.y;
  const x = cam.x + dir.x * t, z = cam.z + dir.z * t;
  return { x, y: ceil + 8, z, extra: Math.ceil(Math.hypot(x / 16 - ccx, z / 16 - ccz)) };
}
