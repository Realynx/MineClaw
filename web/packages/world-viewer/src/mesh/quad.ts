// The one block-geometry currency both versions bake into and the section mesher emits: a textured quad in block-local
// coordinates (0..1, may reach into a neighbour for wide tile-entity models) with its atlas cell, render pass, tint kind,
// face shade, facing, cull face and lighting rules. 1.12.2 models (models/baker.ts), the 1.12.2 tile-entity blocks
// (models/builtin.ts), the b1.8.1 render types (beta/geometry.ts) and the fluids (fluids.ts) all produce these; the mesher
// (mesher.ts) culls, lights, tints and writes them into the vertex sink. Small matrix helpers for the model ports live here.

import { Layer } from '../world/blocks';

/** Tint kinds resolved per position by the mesher (biome colours); anything ≥ TINT_FIXED is a fixed 0xRRGGBB. */
export const TINT_NONE = 0;
export const TINT_GRASS = 1;
export const TINT_FOLIAGE = 2;
export const TINT_WATER = 3;
export const TINT_FIXED = 0x1000000;
export const fixedTint = (rgb: number) => TINT_FIXED | (rgb & 0xffffff);

export interface Quad {
  /** 12 numbers: P0..P3 (block-local), counter-clockwise seen from the visible side. */
  p: number[];
  /** 8 numbers: per-corner UV in cell units relative to (c, r) (0..1 for a tile; up to the sheet size for spans). */
  uv: number[];
  c: number;
  r: number;
  /** 0 = one cell; else (widthCells << 8) | heightCells of the sheet the UVs address (tile-entity textures). */
  span: number;
  /** UVs wrap inside the cell (procedural b1.8.1 liquids). */
  wrap: boolean;
  /** TINT_* kind or TINT_FIXED | rgb. */
  tint: number;
  /** Overlay cell (grass side fringe) or -1, with its tint. */
  ovC: number;
  ovR: number;
  ovTint: number;
  /** Face shade multiplier (bottom 0.5, top 1, N/S 0.8, E/W 0.6; 1 for unshaded). */
  shade: number;
  /** Axis-aligned facing (0 down … 5 east), or -1 for a rotated quad. */
  dir: number;
  /** Cull face: skipped when the neighbour on that side is an opaque cube (-1 = never). */
  cull: number;
  /** Lies on the block boundary in `dir`: lit from the neighbour cell. */
  flush: boolean;
  layer: Layer;
  /** Emit both windings (planes without a back face of their own). */
  twoSided: boolean;
  /** Smooth light with ambient occlusion (model `ambientocclusion`; off for light-emitting blocks). */
  ao: boolean;
  emissive: boolean;
  /** Untextured colour (the tile's average) before tint and shade. */
  flat: number;
  /** Vertex alpha (1 = the texel's own). */
  alpha: number;
  textured: boolean;
}

export const FACE_N: ReadonlyArray<readonly [number, number, number]> = [
  [0, -1, 0], [0, 1, 0], [0, 0, -1], [0, 0, 1], [-1, 0, 0], [1, 0, 0],
];

/** RenderBlocks / FaceBakery face shade: bottom 0.5, top 1, N/S 0.8, E/W 0.6. */
export const FACE_SHADE = [0.5, 1, 0.8, 0.8, 0.6, 0.6];

/** Canonical cube corners per face (EnumFaceDirection vertex order), counter-clockwise seen from outside. */
export const CUBE_CORNERS: ReadonlyArray<ReadonlyArray<readonly [number, number, number]>> = [
  [[0, 0, 1], [0, 0, 0], [1, 0, 0], [1, 0, 1]],
  [[0, 1, 0], [0, 1, 1], [1, 1, 1], [1, 1, 0]],
  [[1, 1, 0], [1, 0, 0], [0, 0, 0], [0, 1, 0]],
  [[0, 1, 1], [0, 0, 1], [1, 0, 1], [1, 1, 1]],
  [[0, 1, 0], [0, 0, 0], [0, 0, 1], [0, 1, 1]],
  [[1, 1, 1], [1, 0, 1], [1, 0, 0], [1, 1, 0]],
];

/** Block-local (u, v) of a point on a box face, the vanilla mapping (top/bottom: x, z; sides: across, 1 − y). */
export function faceUv(f: number, x: number, y: number, z: number): [number, number] {
  switch (f) {
    case 0:
    case 1:
      return [x, z];
    case 2:
      return [1 - x, 1 - y];
    case 3:
      return [x, 1 - y];
    case 4:
      return [z, 1 - y];
    default:
      return [1 - z, 1 - y];
  }
}

/** Unit normal of P0..P3 ((P1 − P0) × (P3 − P0)). */
export function normalOf(p: number[]): [number, number, number] {
  const ax = p[3] - p[0], ay = p[4] - p[1], az = p[5] - p[2];
  const bx = p[9] - p[0], by = p[10] - p[1], bz = p[11] - p[2];
  let nx = ay * bz - az * by, ny = az * bx - ax * bz, nz = ax * by - ay * bx;
  const l = Math.hypot(nx, ny, nz);
  if (l < 1e-12) return [0, 1, 0];
  nx /= l;
  ny /= l;
  nz /= l;
  return [nx, ny, nz];
}

/** The axis-aligned facing of a normal, -1 when it is not axis-aligned. */
export function dirOf(n: readonly number[]): number {
  for (let i = 0; i < 6; i++) {
    const f = FACE_N[i];
    if (n[0] * f[0] + n[1] * f[1] + n[2] * f[2] > 0.999) return i;
  }
  return -1;
}

export function nearestDir(n: readonly number[]): number {
  let best = 1, bd = -Infinity;
  for (let i = 0; i < 6; i++) {
    const f = FACE_N[i];
    const d = n[0] * f[0] + n[1] * f[1] + n[2] * f[2];
    if (d > bd) {
      bd = d;
      best = i;
    }
  }
  return best;
}

/** Whether every corner lies on the block boundary plane of facing `dir`. */
export function onBoundary(dir: number, p: number[]): boolean {
  const e = 1e-4;
  for (let i = 0; i < 4; i++) {
    const x = p[i * 3], y = p[i * 3 + 1], z = p[i * 3 + 2];
    const c = dir === 0 ? y : dir === 1 ? 1 - y : dir === 2 ? z : dir === 3 ? 1 - z : dir === 4 ? x : 1 - x;
    if (Math.abs(c) > e) return false;
  }
  return true;
}

/** Shade for an arbitrary normal (a blend of the axis shades), as BlockGeometry.ShadeOf. */
export function shadeOf(n: readonly number[]): number {
  const ax = Math.abs(n[0]), az = Math.abs(n[2]);
  const side = ax + az < 1e-6 ? 1 : (ax * 0.6 + az * 0.8) / (ax + az);
  return n[1] > 0 ? n[1] + (1 - n[1]) * side : -n[1] * 0.5 + (1 + n[1]) * side;
}

export interface QuadInit {
  c?: number;
  r?: number;
  span?: number;
  wrap?: boolean;
  tint?: number;
  ovC?: number;
  ovR?: number;
  ovTint?: number;
  shade?: number;
  cull?: number;
  layer?: Layer;
  twoSided?: boolean;
  ao?: boolean;
  emissive?: boolean;
  flat?: number;
  alpha?: number;
  textured?: boolean;
  /** Override the computed facing (-1 forces a rotated/flat-lit quad). */
  dir?: number;
}

/** A quad from corners + UVs; facing and flushness from the geometry. */
export function makeQuad(p: number[], uv: number[], o: QuadInit = {}): Quad {
  const n = normalOf(p);
  const dir = o.dir ?? dirOf(n);
  return {
    p,
    uv,
    c: o.c ?? 0,
    r: o.r ?? 0,
    span: o.span ?? 0,
    wrap: o.wrap ?? false,
    tint: o.tint ?? TINT_NONE,
    ovC: o.ovC ?? -1,
    ovR: o.ovR ?? -1,
    ovTint: o.ovTint ?? TINT_NONE,
    shade: o.shade ?? (dir >= 0 ? FACE_SHADE[dir] : 1),
    dir,
    cull: o.cull ?? -1,
    flush: dir >= 0 && onBoundary(dir, p),
    layer: o.layer ?? Layer.Opaque,
    twoSided: o.twoSided ?? false,
    ao: o.ao ?? true,
    emissive: o.emissive ?? false,
    flat: o.flat ?? 0x808080,
    alpha: o.alpha ?? 1,
    textured: o.textured ?? true,
  };
}

/** Recomputes facing/flushness after the corners moved. */
export function refresh(q: Quad, keepShade = false): Quad {
  const n = normalOf(q.p);
  q.dir = dirOf(n);
  q.flush = q.dir >= 0 && onBoundary(q.dir, q.p);
  if (!keepShade) q.shade = q.dir >= 0 ? FACE_SHADE[q.dir] : shadeOf(n);
  return q;
}

// ------------------------------------------------------------------------------------------------------------ matrices
// Column-major-free 4×4 as a flat row-major array: m[r*4+c]; points are column vectors (p' = M p).

export type M4 = number[];

export const ident = (): M4 => [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1];

export function mul(a: M4, b: M4): M4 {
  const o = new Array<number>(16);
  for (let r = 0; r < 4; r++)
    for (let c = 0; c < 4; c++) o[r * 4 + c] = a[r * 4] * b[c] + a[r * 4 + 1] * b[4 + c] + a[r * 4 + 2] * b[8 + c] + a[r * 4 + 3] * b[12 + c];
  return o;
}

export const translate = (x: number, y: number, z: number): M4 => [1, 0, 0, x, 0, 1, 0, y, 0, 0, 1, z, 0, 0, 0, 1];
export const scale = (x: number, y: number, z: number): M4 => [x, 0, 0, 0, 0, y, 0, 0, 0, 0, z, 0, 0, 0, 0, 1];

/** Right-handed rotation by `deg` degrees about an axis (GL glRotate semantics). */
export function rotate(deg: number, axis: 'x' | 'y' | 'z'): M4 {
  const a = (deg * Math.PI) / 180, c = Math.cos(a), s = Math.sin(a);
  const r = (v: number) => (Math.abs(v) < 1e-12 ? 0 : v);
  if (axis === 'x') return [1, 0, 0, 0, 0, r(c), r(-s), 0, 0, r(s), r(c), 0, 0, 0, 0, 1];
  if (axis === 'y') return [r(c), 0, r(s), 0, 0, 1, 0, 0, r(-s), 0, r(c), 0, 0, 0, 0, 1];
  return [r(c), r(-s), 0, 0, r(s), r(c), 0, 0, 0, 0, 1, 0, 0, 0, 0, 1];
}

/** M1 · M2 · … (the rightmost applies first). */
export function chain(...ms: M4[]): M4 {
  return ms.reduce((a, b) => mul(a, b), ident());
}

export function apply(m: M4, x: number, y: number, z: number): [number, number, number] {
  return [m[0] * x + m[1] * y + m[2] * z + m[3], m[4] * x + m[5] * y + m[6] * z + m[7], m[8] * x + m[9] * y + m[10] * z + m[11]];
}

/** Transforms a quad's corners in place (orientation kept: rigid transforms only, or mirror pairs). */
export function transformQuad(q: Quad, m: M4, keepShade = false): Quad {
  for (let i = 0; i < 4; i++) {
    const [x, y, z] = apply(m, q.p[i * 3], q.p[i * 3 + 1], q.p[i * 3 + 2]);
    q.p[i * 3] = x;
    q.p[i * 3 + 1] = y;
    q.p[i * 3 + 2] = z;
  }
  return refresh(q, keepShade);
}

/** Reverses the winding (P0 P3 P2 P1) when the quad faces against `n`. */
export function orient(q: Quad, n: readonly number[]): Quad {
  const qn = normalOf(q.p);
  if (qn[0] * n[0] + qn[1] * n[1] + qn[2] * n[2] >= 0) return q;
  const order = [0, 3, 2, 1];
  const p = q.p.slice(), uv = q.uv.slice();
  for (let k = 0; k < 4; k++) {
    for (let a = 0; a < 3; a++) q.p[k * 3 + a] = p[order[k] * 3 + a];
    q.uv[k * 2] = uv[order[k] * 2];
    q.uv[k * 2 + 1] = uv[order[k] * 2 + 1];
  }
  return refresh(q, true);
}

/**
 * Clips an axis-aligned rectangle with affine UVs to `min ≤ p[axis] ≤ max` (a large chest's half per cell); null when
 * nothing is left. Faces perpendicular to the axis are kept or dropped whole.
 */
export function clipAxis(q: Quad, axis: number, min: number, max: number): Quad | null {
  const e = 1e-5;
  const vals = [0, 1, 2, 3].map((k) => q.p[k * 3 + axis]);
  const lo = Math.min(...vals), hi = Math.max(...vals);
  if (hi <= min + e || lo >= max - e) return hi - lo < e && lo >= min - e && hi <= max + e ? q : null;
  if (lo >= min - e && hi <= max + e) return q;
  const out = { ...q, p: q.p.slice(), uv: q.uv.slice() };
  // P = P0 + s·(P1 − P0) + t·(P3 − P0), UV likewise; corners are (s, t) = (0,0) (1,0) (1,1) (0,1).
  const st = [[0, 0], [1, 0], [1, 1], [0, 1]];
  const d1 = q.p[3 + axis] - q.p[axis], d3 = q.p[9 + axis] - q.p[axis];
  for (let k = 0; k < 4; k++) {
    let [s, t] = st[k];
    const v = q.p[axis] + s * d1 + t * d3;
    const target = Math.max(min, Math.min(max, v));
    if (Math.abs(target - v) < e) continue;
    if (Math.abs(d1) > e) s = (target - q.p[axis] - t * d3) / d1;
    else t = (target - q.p[axis] - s * d1) / d3;
    for (let a = 0; a < 3; a++) out.p[k * 3 + a] = q.p[a] + s * (q.p[3 + a] - q.p[a]) + t * (q.p[9 + a] - q.p[a]);
    for (let a = 0; a < 2; a++) out.uv[k * 2 + a] = q.uv[a] + s * (q.uv[2 + a] - q.uv[a]) + t * (q.uv[6 + a] - q.uv[a]);
  }
  return refresh(out, true);
}

/** Moves every corner. */
export function offsetQuad(q: Quad, dx: number, dy: number, dz: number): Quad {
  for (let i = 0; i < 4; i++) {
    q.p[i * 3] += dx;
    q.p[i * 3 + 1] += dy;
    q.p[i * 3 + 2] += dz;
  }
  q.flush = q.dir >= 0 && onBoundary(q.dir, q.p);
  return q;
}

export const cloneQuad = (q: Quad): Quad => ({ ...q, p: q.p.slice(), uv: q.uv.slice() });
