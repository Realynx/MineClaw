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

/** A voxel walk along a unit direction (Amanatides & Woo): the cell it is in, and the face it last entered by. */
class VoxelRay {
  vx = 0;
  vy = 0;
  vz = 0;
  /** The face of the current cell the ray came in through (Minecraft side order; −1 = the start cell). */
  face = -1;
  private sx = 0;
  private sy = 0;
  private sz = 0;
  private tdx = 0;
  private tdy = 0;
  private tdz = 0;
  private tmx = 0;
  private tmy = 0;
  private tmz = 0;

  start(o: V3, dx: number, dy: number, dz: number): this {
    this.vx = Math.floor(o[0]);
    this.vy = Math.floor(o[1]);
    this.vz = Math.floor(o[2]);
    this.sx = dx > 0 ? 1 : -1;
    this.sy = dy > 0 ? 1 : -1;
    this.sz = dz > 0 ? 1 : -1;
    this.tdx = axisDelta(dx);
    this.tdy = axisDelta(dy);
    this.tdz = axisDelta(dz);
    this.tmx = axisStart(dx, this.vx, o[0], this.tdx);
    this.tmy = axisStart(dy, this.vy, o[1], this.tdy);
    this.tmz = axisStart(dz, this.vz, o[2], this.tdz);
    this.face = -1;
    return this;
  }

  /** Into the next cell; returns the distance travelled to its boundary. */
  step(): number {
    let tt: number;
    if (this.tmx < this.tmy && this.tmx < this.tmz) {
      tt = this.tmx;
      this.vx += this.sx;
      this.tmx += this.tdx;
      this.face = this.sx > 0 ? 4 : 5;
    } else if (this.tmy < this.tmz) {
      tt = this.tmy;
      this.vy += this.sy;
      this.tmy += this.tdy;
      this.face = this.sy > 0 ? 0 : 1;
    } else {
      tt = this.tmz;
      this.vz += this.sz;
      this.tmz += this.tdz;
      this.face = this.sz > 0 ? 2 : 3;
    }
    return tt;
  }
}

/** Distance along the ray between two boundaries of one axis. */
function axisDelta(d: number): number {
  return d === 0 ? Infinity : Math.abs(1 / d);
}

/** Distance along the ray to the first boundary of one axis. */
function axisStart(d: number, v: number, o: number, td: number): number {
  return d === 0 ? Infinity : (d > 0 ? v + 1 - o : o - v) * td;
}

/** One walk at a time (the worker is single-threaded and the queries don't nest). */
const ray = new VoxelRay();

/** First drawn block along the ray (fluids skipped), within maxDist; null when nothing is hit. */
export function pick(store: ColumnStore, t: StateTable, dim: number, o: V3, d: V3, maxDist: number, cut?: CutParams | null): PickHit | null {
  const len = Math.hypot(d[0], d[1], d[2]);
  if (len < 1e-9) return null;
  ray.start(o, d[0] / len, d[1] / len, d[2] / len);
  for (let i = 0; i < 4096; i++) {
    const hit = pickHere(store, t, dim, cut);
    if (hit) return hit;
    const tt = ray.step();
    if (tt > maxDist || ray.vy < -64 || ray.vy > 320) return null;
  }
  return null;
}

/** The ray's current cell when it holds a drawn, uncut block that isn't a fluid. */
function pickHere(store: ColumnStore, t: StateTable, dim: number, cut: CutParams | null | undefined): PickHit | null {
  const { vx, vy, vz } = ray;
  const b = store.blockAt(dim, vx, vy, vz);
  if (!b || b.id === 0 || cutHides(cut, vx, vy, vz)) return null;
  const s = (b.id << 4) | b.meta;
  if (t.shape[s] === Shape.None || t.flags[s] & (F_WATER | F_LAVA)) return null;
  return { x: vx, y: vy, z: vz, id: b.id, meta: b.meta, face: ray.face, live: b.col.live, seen: b.col.seen, by: b.col.by };
}

/** ViewCut.Blocked: does an opaque cube or leaf block (not already cut) hide point p from the camera? */
export function blocked(store: ColumnStore, t: StateTable, dim: number, p: V3, cam: V3, cut?: CutParams | null): boolean {
  let dx = cam[0] - p[0], dy = cam[1] - p[1], dz = cam[2] - p[2];
  const len = Math.hypot(dx, dy, dz);
  if (len < 1e-6) return false;
  dx /= len; dy /= len; dz /= len;
  ray.start(p, dx, dy, dz);
  for (let i = 0; i < 512; i++) {
    const tt = ray.step();
    if (tt >= len - 0.3) return false;
    const b = store.blockAt(dim, ray.vx, ray.vy, ray.vz);
    if (!b) {
      if (ray.vy >= 256 && dy > 0) return false;
      continue;
    }
    if (hidesView(t, b.id, b.meta, cut)) return true;
  }
  return false;
}

/** An opaque cube or leaves, not cut away, in the ray's current cell. */
function hidesView(t: StateTable, id: number, meta: number, cut: CutParams | null | undefined): boolean {
  if (id === 0 || !(t.flags[(id << 4) | meta] & (F_OPAQUE | F_LEAVES))) return false;
  return !cutHides(cut, ray.vx, ray.vy, ray.vz);
}

/** For each body (x, y, z of its feet, 3 numbers each): is any of its feet / waist / head points hidden from the camera by an
 * opaque block or leaves? (The occluded-body silhouettes: plants, glass and water never count, a body standing in wheat
 * is not hidden.) */
export function occluded(store: ColumnStore, t: StateTable, dim: number, cam: V3, bodies: number[], heights: number[] = [0.3, 1.0, 1.55]): boolean[] {
  const out: boolean[] = [];
  for (let i = 0; i + 2 < bodies.length; i += 3) {
    let hid = false;
    for (const h of heights) if (blocked(store, t, dim, [bodies[i], bodies[i + 1] + h, bodies[i + 2]], cam)) { hid = true; break; }
    out.push(hid);
  }
  return out;
}

export interface ViewInfo {
  /** Two of the body's feet / waist / head points are hidden from the camera (ViewCut.For). */
  hidden: boolean;
  /** Opaque blocks above the body's head (indoors, a tunnel, a cave): the roof cut-away is useful. A tree's canopy is not a
   * roof (cutting it away would slice the hillside around an outdoor bot); bodies under leaves show as silhouettes. */
  covered: boolean;
  /** The lowest covering block above the head (the roof cut's y), or -1. */
  roofY: number;
  /** The camera sits inside an opaque block. */
  cameraInside: boolean;
  /** 1.12.2 biome id at the body (else the camera) column, −1 = unknown (b1.8.1, remembered columns). */
  biome: number;
}

export function viewInfo(store: ColumnStore, t: StateTable, dim: number, cam: V3, body: V3 | null, cut?: CutParams | null): ViewInfo {
  const cb = store.blockAt(dim, Math.floor(cam[0]), Math.floor(cam[1]), Math.floor(cam[2]));
  const cameraInside = !!cb && cb.id !== 0 && (t.flags[(cb.id << 4) | cb.meta] & F_OPAQUE) !== 0;
  const biomeAt = (x: number, z: number) => {
    const c = store.get(dim, Math.floor(x) >> 4, Math.floor(z) >> 4);
    return c?.light?.biomes ? c.light.biomes[((Math.floor(z) & 15) << 4) | (Math.floor(x) & 15)] : -1;
  };
  if (!body) return { hidden: false, covered: false, roofY: -1, cameraInside, biome: biomeAt(cam[0], cam[2]) };
  let n = 0;
  for (const h of [0.3, 1.0, 1.55]) if (blocked(store, t, dim, [body[0], body[1] + h, body[2]], cam, cut)) n++;
  let roofY = -1;
  const bx = Math.floor(body[0]), bz = Math.floor(body[2]);
  for (let y = Math.floor(body[1]) + 2; y < Math.floor(body[1]) + 40; y++) {
    const b = store.blockAt(dim, bx, y, bz);
    if (!b) break;
    if (b.id !== 0 && t.flags[(b.id << 4) | b.meta] & F_OPAQUE) {
      roofY = y;
      break;
    }
  }
  return { hidden: n >= 2, covered: roofY >= 0, roofY, cameraInside, biome: biomeAt(body[0], body[2]) };
}
