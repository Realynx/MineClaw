/**
 * Story Mode's map math, kept free of three.js so it is cheap and unit-testable: projecting world points with the view's
 * camera matrices (column-major 4×4, as three.js stores them), casting a ray through a screen point, intersecting the ground
 * plane, screen and grid rectangles, zoom-to-cursor and edge panning.
 */

export type V3 = [number, number, number];
export type Pt = { x: number; y: number };

/** What the overlay needs from the camera: its matrices and the canvas size in CSS pixels. */
export interface ViewMats {
  /** camera.matrixWorldInverse.elements */
  view: ArrayLike<number>;
  /** camera.projectionMatrix.elements */
  proj: ArrayLike<number>;
  /** camera.matrixWorld.elements */
  world: ArrayLike<number>;
  /** camera.projectionMatrixInverse.elements */
  projInv: ArrayLike<number>;
  w: number;
  h: number;
}

function mul4(m: ArrayLike<number>, x: number, y: number, z: number, w: number): [number, number, number, number] {
  return [
    m[0]! * x + m[4]! * y + m[8]! * z + m[12]! * w,
    m[1]! * x + m[5]! * y + m[9]! * z + m[13]! * w,
    m[2]! * x + m[6]! * y + m[10]! * z + m[14]! * w,
    m[3]! * x + m[7]! * y + m[11]! * z + m[15]! * w,
  ];
}

/** World → screen (CSS px). Null when the point is behind the camera. `z` is the NDC depth (−1 near … 1 far). */
export function project(m: ViewMats, x: number, y: number, z: number): { x: number; y: number; z: number } | null {
  const v = mul4(m.view, x, y, z, 1);
  const c = mul4(m.proj, v[0], v[1], v[2], v[3]);
  if (c[3] <= 1e-6) return null;
  return { x: ((c[0] / c[3] + 1) / 2) * m.w, y: ((1 - c[1] / c[3]) / 2) * m.h, z: c[2] / c[3] };
}

/** The world ray through a screen point (origin on the near plane, unit direction). */
export function rayAt(m: ViewMats, sx: number, sy: number): { o: V3; d: V3 } {
  const nx = (sx / m.w) * 2 - 1,
    ny = 1 - (sy / m.h) * 2;
  const unproject = (nz: number): V3 => {
    const e = mul4(m.projInv, nx, ny, nz, 1);
    const w = mul4(m.world, e[0] / e[3], e[1] / e[3], e[2] / e[3], 1);
    return [w[0], w[1], w[2]];
  };
  const a = unproject(-1),
    b = unproject(1);
  const d: V3 = [b[0] - a[0], b[1] - a[1], b[2] - a[2]];
  const l = Math.hypot(d[0], d[1], d[2]) || 1;
  return { o: a, d: [d[0] / l, d[1] / l, d[2] / l] };
}

/** Where a ray crosses the horizontal plane y = `y` (null when parallel or behind). */
export function rayPlaneY(o: V3, d: V3, y: number): V3 | null {
  if (Math.abs(d[1]) < 1e-9) return null;
  const t = (y - o[1]) / d[1];
  if (t < 0) return null;
  return [o[0] + d[0] * t, y, o[2] + d[2] * t];
}

/** The block cell under a screen point on the plane y = `planeY` (the top of the level's floor). */
export function cellAt(m: ViewMats, sx: number, sy: number, planeY: number): V3 | null {
  const r = rayAt(m, sx, sy);
  const p = rayPlaneY(r.o, r.d, planeY);
  return p ? [Math.floor(p[0]), Math.floor(planeY) - 1, Math.floor(p[2])] : null;
}

export interface Rect {
  x: number;
  y: number;
  w: number;
  h: number;
}

/** The screen rectangle between two drag points. */
export function rectOf(a: Pt, b: Pt): Rect {
  return { x: Math.min(a.x, b.x), y: Math.min(a.y, b.y), w: Math.abs(a.x - b.x), h: Math.abs(a.y - b.y) };
}

export function inRect(r: Rect, p: Pt, pad = 0): boolean {
  return p.x >= r.x - pad && p.x <= r.x + r.w + pad && p.y >= r.y - pad && p.y <= r.y + r.h + pad;
}

/** A drag is a box (not a click) once it moved this many pixels. */
export const DRAG_PX = 5;

export function isDrag(a: Pt, b: Pt): boolean {
  return Math.abs(a.x - b.x) + Math.abs(a.y - b.y) >= DRAG_PX;
}

/** The grid rectangle [x1, z1, x2, z2] (inclusive, min first) between two cells. */
export function cellRect(a: readonly number[], b: readonly number[]): [number, number, number, number] {
  return [Math.min(a[0]!, b[0]!), Math.min(a[2]!, b[2]!), Math.max(a[0]!, b[0]!), Math.max(a[2]!, b[2]!)];
}

/** Names whose screen points fall inside a rectangle. */
export function boxPick<T extends { key: string; at: Pt | null }>(items: readonly T[], r: Rect): string[] {
  return items.filter((i) => i.at && inRect(r, i.at, 6)).map((i) => i.key);
}

/** The nearest item within `radius` px of a point. */
export function nearest<T extends { key: string; at: Pt | null }>(items: readonly T[], p: Pt, radius = 16): T | null {
  let best: T | null = null;
  let bd = radius;
  for (const i of items) {
    if (!i.at) continue;
    const d = Math.hypot(i.at.x - p.x, i.at.y - p.y);
    if (d <= bd) {
      bd = d;
      best = i;
    }
  }
  return best;
}

export const ZOOM_MIN = 10;
/**
 * The farthest zoom (blocks of view height). The viewer draws a top-down / iso picture's whole ground (up to its 192-block
 * cover cap), so the ground never ends at the edges; the cost grows with the area. Measured on software WebGL over the
 * dense fixture world (scripts/perf.mjs --camera top): 33 fps at 112, 27 fps at 144.
 */
export const ZOOM_MAX = 112;

/**
 * Zoom around the cursor: the world point under the cursor stays put. `zoom` is the world height the view shows; one wheel
 * notch scales it by 1.15. Returns the new zoom and the new camera target (x, z).
 */
export function zoomAt(
  target: { x: number; z: number },
  zoom: number,
  notches: number,
  cursor: { x: number; z: number },
) {
  const next = Math.max(ZOOM_MIN, Math.min(ZOOM_MAX, zoom * 1.15 ** notches));
  const k = next / zoom;
  return { zoom: next, x: cursor.x + (target.x - cursor.x) * k, z: cursor.z + (target.z - cursor.z) * k };
}

/** Screen-edge pan direction (−1..1 per axis) when the pointer is within `margin` px of an edge. */
export function edgePan(p: Pt, w: number, h: number, margin = 10): [number, number] {
  const ax = p.x < margin ? -1 : p.x > w - margin ? 1 : 0;
  const ay = p.y < margin ? -1 : p.y > h - margin ? 1 : 0;
  return [ax, ay];
}

/** World units per CSS pixel at the top-down zoom (the view shows `zoom` world units over its height). */
export function unitsPerPx(zoom: number, h: number): number {
  return zoom / Math.max(1, h);
}

// ------------------------------------------------------------------ formations (a right-drag line for drafted pawns)

export type P2 = { x: number; z: number };

const unit = (a: P2, b: P2): P2 => {
  const dx = b.x - a.x,
    dz = b.z - a.z,
    len = Math.hypot(dx, dz);
  return len > 1e-6 ? { x: dx / len, z: dz / len } : { x: 1, z: 0 };
};

/**
 * `n` formation slots spread evenly along the line a → b (world points), in line order. Slots are at least one cell apart
 * along the line's own grid step (a diagonal needs √2 per pawn), so a short drag fans the pawns out around its middle.
 */
export function lineSlots(a: P2, b: P2, n: number): P2[] {
  if (n <= 0) return [];
  const u = unit(a, b),
    len = Math.hypot(b.x - a.x, b.z - a.z);
  const step = 1 / Math.max(Math.abs(u.x), Math.abs(u.z));
  const span = Math.max(len, (n - 1) * step);
  const mx = (a.x + b.x) / 2,
    mz = (a.z + b.z) / 2;
  return Array.from({ length: n }, (_, i) => {
    const t = n === 1 ? 0 : -span / 2 + (span * i) / (n - 1);
    return { x: mx + u.x * t, z: mz + u.z * t };
  });
}

/**
 * Snaps slots to distinct cells: each slot's own cell when it is standable and free, else the nearest standable free cell
 * within `radius`, else the nearest free one. Returns [x, z] cells in slot order.
 */
export function snapCells(
  slots: readonly P2[],
  standable: (x: number, z: number) => boolean,
  radius = 3,
): Array<[number, number]> {
  const taken = new Set<string>();
  const ring: Array<[number, number]> = [];
  for (let dx = -radius; dx <= radius; dx++) for (let dz = -radius; dz <= radius; dz++) ring.push([dx, dz]);
  return slots.map((s) => {
    const cx = Math.floor(s.x),
      cz = Math.floor(s.z);
    const d = (c: [number, number]) => Math.hypot(c[0] + 0.5 - s.x, c[1] + 0.5 - s.z);
    const cands = ring.map(([dx, dz]) => [cx + dx, cz + dz] as [number, number]).sort((p, q) => d(p) - d(q));
    const free = (c: [number, number]) => !taken.has(`${c[0]},${c[1]}`);
    const pick = cands.find((c) => free(c) && standable(c[0], c[1])) ?? cands.find(free)!;
    taken.add(`${pick[0]},${pick[1]}`);
    return pick;
  });
}

/**
 * Who takes which slot: bodies and slots are both ordered along the line (a → b) and paired in that order, so the paths
 * into a straight formation do not cross. Returns `[body key, slot index]` pairs (extra bodies get none).
 */
export function assignAlong<T extends { key: string; x: number; z: number }>(
  bodies: readonly T[],
  slots: readonly P2[],
  a: P2,
  b: P2,
): Array<[string, number]> {
  const u = unit(a, b);
  const along = (p: P2) => (p.x - a.x) * u.x + (p.z - a.z) * u.z;
  const across = (p: P2) => -(p.x - a.x) * u.z + (p.z - a.z) * u.x;
  const bs = [...bodies].sort((p, q) => along(p) - along(q) || across(p) - across(q) || p.key.localeCompare(q.key));
  const ss = slots.map((s, i) => ({ s, i })).sort((p, q) => along(p.s) - along(q.s));
  return bs.slice(0, ss.length).map((body, k) => [body.key, ss[k]!.i]);
}

/** Feet and head may be in these: air, plants, torches, rails, snow layers, signs, plates, buttons (both versions' ids). */
const PASSABLE = new Set([
  0, 6, 27, 28, 31, 32, 37, 38, 39, 40, 50, 55, 59, 63, 66, 68, 69, 70, 72, 75, 76, 77, 78, 104, 105, 106, 141, 142,
  143, 147, 148, 157, 171, 175,
]);
/** Never a floor: liquids, fire, cactus. */
const NOT_FLOOR = new Set([8, 9, 10, 11, 51, 81]);

/**
 * Where a pawn stands in one column: block ids from `y0` upward and the working `level` (a floor height). The feet y of the
 * standable spot (a solid floor with two passable blocks above) nearest the level; null when the column has none;
 * undefined when the column is not loaded (an id of -1).
 */
export function standFeet(ids: readonly number[], y0: number, level: number): number | null | undefined {
  if (ids.some((id) => id < 0)) return undefined;
  let best: number | null = null;
  for (let j = 0; j + 2 < ids.length; j++) {
    const floor = ids[j]!;
    if (PASSABLE.has(floor) || NOT_FLOOR.has(floor) || !PASSABLE.has(ids[j + 1]!) || !PASSABLE.has(ids[j + 2]!))
      continue;
    const y = y0 + j;
    if (best === null || Math.abs(y - level) < Math.abs(best - level)) best = y;
  }
  return best === null ? null : best + 1;
}
