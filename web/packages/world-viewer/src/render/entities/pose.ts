// Poses a built model and writes its bone matrices: each part's world transform = root · (parent chain) · translate(pivot)
// · rotZ · rotY · rotX, as 3×4 row-major affine matrices (12 floats per part) — the layout of the bone texture rows.
// No allocation per call: one Skeleton per model kind is reused for every entity of that kind.

import { BONE_SLOTS, type BuiltModel } from './builder';
import type { PartState, PoseCtx } from './types';

export type Affine = Float32Array; // 12 floats: rows (m00 m01 m02 m03), (m10 …), (m20 …)

export function identity(out: Affine = new Float32Array(12)): Affine {
  out.fill(0);
  out[0] = out[5] = out[10] = 1;
  return out;
}

/** out = a · b (affine). `out` may alias neither. */
export function mul(out: Affine, a: ArrayLike<number>, b: ArrayLike<number>): Affine {
  for (let r = 0; r < 3; r++) {
    const a0 = a[r * 4], a1 = a[r * 4 + 1], a2 = a[r * 4 + 2], a3 = a[r * 4 + 3];
    out[r * 4] = a0 * b[0] + a1 * b[4] + a2 * b[8];
    out[r * 4 + 1] = a0 * b[1] + a1 * b[5] + a2 * b[9];
    out[r * 4 + 2] = a0 * b[2] + a1 * b[6] + a2 * b[10];
    out[r * 4 + 3] = a0 * b[3] + a1 * b[7] + a2 * b[11] + a3;
  }
  return out;
}

/** translate(p) · rotZ(rz) · rotY(ry) · rotX(rx) (vanilla ModelRenderer order). */
export function partLocal(out: Affine, px: number, py: number, pz: number, rx: number, ry: number, rz: number): Affine {
  const cx = Math.cos(rx), sx = Math.sin(rx), cy = Math.cos(ry), sy = Math.sin(ry), cz = Math.cos(rz), sz = Math.sin(rz);
  // Rz·Ry
  const a00 = cz * cy, a01 = -sz, a02 = cz * sy;
  const a10 = sz * cy, a11 = cz, a12 = sz * sy;
  const a20 = -sy, a21 = 0, a22 = cy;
  // ·Rx
  out[0] = a00;
  out[1] = a01 * cx + a02 * sx;
  out[2] = -a01 * sx + a02 * cx;
  out[3] = px;
  out[4] = a10;
  out[5] = a11 * cx + a12 * sx;
  out[6] = -a11 * sx + a12 * cx;
  out[7] = py;
  out[8] = a20;
  out[9] = a21 * cx + a22 * sx;
  out[10] = -a21 * sx + a22 * cx;
  out[11] = pz;
  return out;
}

export interface RootParams {
  x: number;
  y: number;
  z: number;
  /** Body yaw, degrees (vanilla: 0 south, 90 west). */
  yaw: number;
  /** Death roll, degrees (0..90). */
  death: number;
  scale: readonly [number, number, number];
  /** Model-space translation after the scale, pixels. */
  offset: readonly [number, number, number];
  /** World-space y offset after everything (sneaking sinks players 0.125, bob…). */
  lift?: number;
}

/** RenderLivingBase: translate(pos) · rotY(180 − yaw) · rotZ(death) · scale(−1, −1, 1) · scale · translate(0, −1.501, 0) ·
 * scale(1/16) · translate(offset). */
export function rootMatrix(out: Affine, r: RootParams): Affine {
  const a = ((180 - r.yaw) * Math.PI) / 180, d = (r.death * Math.PI) / 180;
  const ca = Math.cos(a), sa = Math.sin(a), cd = Math.cos(d), sd = Math.sin(d);
  // R = Ry(a) · Rz(d)
  const r00 = ca * cd, r01 = -ca * sd, r02 = sa;
  const r10 = sd, r11 = cd, r12 = 0;
  const r20 = -sa * cd, r21 = sa * sd, r22 = ca;
  // · S(-1,-1,1) · S(scale) · S(1/16): column scales.
  const kx = (-r.scale[0]) / 16, ky = (-r.scale[1]) / 16, kz = r.scale[2] / 16;
  // translation inside: T(0, -1.501, 0) in scaled space, then offset (pixels) in model space.
  const tx = r.offset[0] / 16, ty = -1.501 + r.offset[1] / 16, tz = r.offset[2] / 16;
  // M = T(pos) · R · S' · T(t'), with S' = diag(-sx, -sy, sz) and t' = (tx, ty, tz) in units before the 1/16 (blocks).
  const sx = -r.scale[0], sy = -r.scale[1], sz = r.scale[2];
  out[0] = r00 * kx;
  out[1] = r01 * ky;
  out[2] = r02 * kz;
  out[3] = r.x + r00 * sx * tx + r01 * sy * ty + r02 * sz * tz;
  out[4] = r10 * kx;
  out[5] = r11 * ky;
  out[6] = r12 * kz;
  out[7] = r.y + (r.lift ?? 0) + r10 * sx * tx + r11 * sy * ty + r12 * sz * tz;
  out[8] = r20 * kx;
  out[9] = r21 * ky;
  out[10] = r22 * kz;
  out[11] = r.z + r20 * sx * tx + r21 * sy * ty + r22 * sz * tz;
  return out;
}

export class Skeleton {
  readonly states: PartState[];
  readonly byName: Record<string, PartState>;
  private readonly world: Float32Array;
  private readonly local = new Float32Array(12);
  private readonly hidden: Uint8Array;

  constructor(readonly model: BuiltModel) {
    this.states = model.parts.map((p) => ({ rx: p.rot[0], ry: p.rot[1], rz: p.rot[2], px: p.pivot[0], py: p.pivot[1], pz: p.pivot[2], visible: true }));
    this.byName = {};
    model.parts.forEach((p, i) => (this.byName[p.name] = this.states[i]));
    this.world = new Float32Array(model.parts.length * 12);
    this.hidden = new Uint8Array(model.parts.length);
  }

  reset(): void {
    const parts = this.model.parts;
    for (let i = 0; i < parts.length; i++) {
      const s = this.states[i], p = parts[i];
      s.rx = p.rot[0];
      s.ry = p.rot[1];
      s.rz = p.rot[2];
      s.px = p.pivot[0];
      s.py = p.pivot[1];
      s.pz = p.pivot[2];
      s.visible = true;
    }
  }

  /** Rest pose → pose function → bones (root · chain) into `out` at `offset` (floats), 12 per part. */
  pose(ctx: PoseCtx | null, root: ArrayLike<number>, out: Float32Array, offset: number): void {
    this.reset();
    if (ctx) this.model.def.pose?.(this.byName, ctx);
    this.write(root, out, offset);
  }

  write(root: ArrayLike<number>, out: Float32Array, offset: number): void {
    const parts = this.model.parts, w = this.world, l = this.local;
    for (let i = 0; i < parts.length; i++) {
      const s = this.states[i], par = parts[i].parent;
      const hide = !s.visible || (par >= 0 && this.hidden[par] === 1);
      this.hidden[i] = hide ? 1 : 0;
      partLocal(l, s.px, s.py, s.pz, s.rx, s.ry, s.rz);
      const dst = w.subarray(i * 12, i * 12 + 12);
      if (par < 0) mul(dst, root, l);
      else mul(dst, w.subarray(par * 12, par * 12 + 12), l);
      const o = offset + i * 12;
      if (hide) out.fill(0, o, o + 12);
      else out.set(dst, o);
    }
  }

  /** The world matrix of part `i` from the last pose (e.g. the right arm, for the held item). */
  partMatrix(i: number): Float32Array {
    return this.world.subarray(i * 12, i * 12 + 12);
  }
}

export const ROW_FLOATS = BONE_SLOTS * 12;

/** Transforms a model-space point (pixels) by an affine matrix. */
export function apply(m: ArrayLike<number>, x: number, y: number, z: number): [number, number, number] {
  return [m[0] * x + m[1] * y + m[2] * z + m[3], m[4] * x + m[5] * y + m[6] * z + m[7], m[8] * x + m[9] * y + m[10] * z + m[11]];
}
