// World queries the main thread asks the worker (it owns the blocks): block picking and the x-ray visibility tests
// (ViewCut.Blocked / ViewCut.For's "is the body hidden" rule, ported from src/MineClaw.Render/EyeRenderer.cs).

import { F_LEAVES, F_OPAQUE, F_WATER, F_LAVA, Shape } from '../world/blocks';
import type { ColumnStore } from './columns';
import type { CutParams } from './mesher';
import type { StateTable } from './states';

export type V3 = [number, number, number];

export interface PickHit {
  x: number;
  y: number;
  z: number;
  id: number;
  meta: number;
  face: number;
  live: boolean;
  seen: number;
  by: string | null;
}

function cutHides(cut: CutParams | null | undefined, x: number, y: number, z: number): boolean {
  return !!cut && y >= cut.y && (x + 0.5 - cut.x) ** 2 + (z + 0.5 - cut.z) ** 2 <= cut.r * cut.r;
}

/** First drawn block along the ray (fluids skipped), within maxDist; null when nothing is hit. */
export function pick(store: ColumnStore, t: StateTable, dim: number, o: V3, d: V3, maxDist: number, cut?: CutParams | null): PickHit | null {
  const len = Math.hypot(d[0], d[1], d[2]);
  if (len < 1e-9) return null;
  const dx = d[0] / len, dy = d[1] / len, dz = d[2] / len;
  let vx = Math.floor(o[0]), vy = Math.floor(o[1]), vz = Math.floor(o[2]);
  const sx = dx > 0 ? 1 : -1, sy = dy > 0 ? 1 : -1, sz = dz > 0 ? 1 : -1;
  const tdx = dx === 0 ? Infinity : Math.abs(1 / dx), tdy = dy === 0 ? Infinity : Math.abs(1 / dy), tdz = dz === 0 ? Infinity : Math.abs(1 / dz);
  let tmx = dx === 0 ? Infinity : (dx > 0 ? vx + 1 - o[0] : o[0] - vx) * tdx;
  let tmy = dy === 0 ? Infinity : (dy > 0 ? vy + 1 - o[1] : o[1] - vy) * tdy;
  let tmz = dz === 0 ? Infinity : (dz > 0 ? vz + 1 - o[2] : o[2] - vz) * tdz;
  let face = -1;
  for (let i = 0; i < 4096; i++) {
    const b = store.blockAt(dim, vx, vy, vz);
    if (b && b.id !== 0 && !cutHides(cut, vx, vy, vz)) {
      const s = (b.id << 4) | b.meta;
      if (t.shape[s] !== Shape.None && !(t.flags[s] & (F_WATER | F_LAVA))) {
        return { x: vx, y: vy, z: vz, id: b.id, meta: b.meta, face, live: b.col.live, seen: b.col.seen, by: b.col.by };
      }
    }
    let tt: number;
    if (tmx < tmy && tmx < tmz) { tt = tmx; vx += sx; tmx += tdx; face = sx > 0 ? 4 : 5; }
    else if (tmy < tmz) { tt = tmy; vy += sy; tmy += tdy; face = sy > 0 ? 0 : 1; }
    else { tt = tmz; vz += sz; tmz += tdz; face = sz > 0 ? 2 : 3; }
    if (tt > maxDist) return null;
    if (vy < -64 || vy > 320) return null;
  }
  return null;
}

/** ViewCut.Blocked: does an opaque cube or leaf block (not already cut) hide point p from the camera? */
export function blocked(store: ColumnStore, t: StateTable, dim: number, p: V3, cam: V3, cut?: CutParams | null): boolean {
  let dx = cam[0] - p[0], dy = cam[1] - p[1], dz = cam[2] - p[2];
  const len = Math.hypot(dx, dy, dz);
  if (len < 1e-6) return false;
  dx /= len; dy /= len; dz /= len;
  let vx = Math.floor(p[0]), vy = Math.floor(p[1]), vz = Math.floor(p[2]);
  const sx = dx > 0 ? 1 : -1, sy = dy > 0 ? 1 : -1, sz = dz > 0 ? 1 : -1;
  const tdx = dx === 0 ? Infinity : Math.abs(1 / dx), tdy = dy === 0 ? Infinity : Math.abs(1 / dy), tdz = dz === 0 ? Infinity : Math.abs(1 / dz);
  let tmx = dx === 0 ? Infinity : (dx > 0 ? vx + 1 - p[0] : p[0] - vx) * tdx;
  let tmy = dy === 0 ? Infinity : (dy > 0 ? vy + 1 - p[1] : p[1] - vy) * tdy;
  let tmz = dz === 0 ? Infinity : (dz > 0 ? vz + 1 - p[2] : p[2] - vz) * tdz;
  for (let i = 0; i < 512; i++) {
    let tt: number;
    if (tmx < tmy && tmx < tmz) { tt = tmx; vx += sx; tmx += tdx; }
    else if (tmy < tmz) { tt = tmy; vy += sy; tmy += tdy; }
    else { tt = tmz; vz += sz; tmz += tdz; }
    if (tt >= len - 0.3) return false;
    const b = store.blockAt(dim, vx, vy, vz);
    if (!b) {
      if (vy >= 256 && dy > 0) return false;
      continue;
    }
    if (b.id === 0) continue;
    const s = (b.id << 4) | b.meta;
    if (!(t.flags[s] & (F_OPAQUE | F_LEAVES))) continue;
    if (cutHides(cut, vx, vy, vz)) continue;
    return true;
  }
  return false;
}

export interface ViewInfo {
  /** Two of the body's feet / waist / head points are hidden from the camera (ViewCut.For). */
  hidden: boolean;
  /** Opaque blocks above the body's head (indoors, a tunnel, a cave): the roof cut-away is useful. */
  covered: boolean;
  /** The lowest covering block above the head (the roof cut's y), or -1. */
  roofY: number;
  /** The camera sits inside an opaque block. */
  cameraInside: boolean;
}

export function viewInfo(store: ColumnStore, t: StateTable, dim: number, cam: V3, body: V3 | null, cut?: CutParams | null): ViewInfo {
  const cb = store.blockAt(dim, Math.floor(cam[0]), Math.floor(cam[1]), Math.floor(cam[2]));
  const cameraInside = !!cb && cb.id !== 0 && (t.flags[(cb.id << 4) | cb.meta] & F_OPAQUE) !== 0;
  if (!body) return { hidden: false, covered: false, roofY: -1, cameraInside };
  let n = 0;
  for (const h of [0.3, 1.0, 1.55]) if (blocked(store, t, dim, [body[0], body[1] + h, body[2]], cam, cut)) n++;
  let roofY = -1;
  const bx = Math.floor(body[0]), bz = Math.floor(body[2]);
  for (let y = Math.floor(body[1]) + 2; y < Math.floor(body[1]) + 40; y++) {
    const b = store.blockAt(dim, bx, y, bz);
    if (!b) break;
    if (b.id !== 0 && t.flags[(b.id << 4) | b.meta] & (F_OPAQUE | F_LEAVES)) {
      roofY = y;
      break;
    }
  }
  return { hidden: n >= 2, covered: roofY >= 0, roofY, cameraInside };
}
