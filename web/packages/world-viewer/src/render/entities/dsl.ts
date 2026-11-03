// Small constructors that keep the model data files terse (types.ts describes the format).

import type { CubeDef, PartDef, PartState, V3 } from './types';

/** addBox(x, y, z, dx, dy, dz, inflate) at texture offset (u, v). */
export function box(x: number, y: number, z: number, dx: number, dy: number, dz: number, u: number, v: number, inflate = 0, mirror = false): CubeDef {
  const c: { -readonly [K in keyof CubeDef]: CubeDef[K] } = { o: [x, y, z], s: [dx, dy, dz], uv: [u, v] };
  if (inflate) c.inflate = inflate;
  if (mirror) c.mirror = true;
  return c;
}

/** A mirrored box (left limbs). */
export const mbox = (x: number, y: number, z: number, dx: number, dy: number, dz: number, u: number, v: number, inflate = 0): CubeDef =>
  box(x, y, z, dx, dy, dz, u, v, inflate, true);

export interface PartOpts {
  rot?: V3;
  children?: PartDef[];
  layer?: string;
}

export function part(name: string, pivot: V3, cubes: CubeDef[], opts: PartOpts = {}): PartDef {
  const p: { -readonly [K in keyof PartDef]: PartDef[K] } = { name, pivot, cubes };
  if (opts.rot) p.rot = opts.rot;
  if (opts.children?.length) p.children = opts.children;
  if (opts.layer) p.layer = opts.layer;
  return p;
}

export const PI = Math.PI;
export const D2R = Math.PI / 180;

/** Vanilla quadruped legs: legs 1..4 = back right, back left, front right, front left (ModelQuadruped order). */
export function walkLegs(p: Record<string, PartState>, swing: number, amount: number, names = ['leg1', 'leg2', 'leg3', 'leg4']): void {
  const a = Math.cos(swing * 0.6662) * 1.4 * amount, b = Math.cos(swing * 0.6662 + PI) * 1.4 * amount;
  if (p[names[0]]) p[names[0]].rx = a;
  if (p[names[1]]) p[names[1]].rx = b;
  if (p[names[2]]) p[names[2]].rx = b;
  if (p[names[3]]) p[names[3]].rx = a;
}

/** head.ry/rx from the net head yaw and pitch (degrees). */
export function look(head: PartState | undefined, yawDeg: number, pitchDeg: number): void {
  if (!head) return;
  head.ry = yawDeg * D2R;
  head.rx = pitchDeg * D2R;
}
