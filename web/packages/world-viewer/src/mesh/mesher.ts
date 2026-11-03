// Meshes one 16³ section into a solid pass (opaque + alpha-tested cut-outs) and a translucent pass, the way the C# GPU
// freecam's SectionStore does: full cubes take a fast path with face culling against opaque neighbours (and same-id culling
// for glass, ice and fluids), per-vertex smooth light from the payload's sky/block light (averaged over the four cells in
// front of each corner) with ambient occlusion, and every other block is a box/plane shape (or the atlas's own quads). Light
// is stored per vertex as raw sky/block levels, so day and night are a shader uniform and never a remesh.

import type { AtlasQuad } from '../atlas/types';
import { cellIndex, lightPacked } from '../wire/chunk';
import { F_EMISSIVE, F_OPAQUE, F_SELF_CULL, F_WATER, F_LAVA, F_BIOME_GRASS, F_BIOME_FOLIAGE, Layer, Shape, pairsAsChest } from '../world/blocks';
import type { ColumnStore, StoredColumn } from './columns';
import type { ResolvedFace, StateTable } from './states';
import { LF_CUT, LF_EMISSIVE, LF_REMEMBERED, LF_UNKNOWN, TEX_TEXTURED, VertexSink, type MeshArrays } from './sink';

/** The roof cut-away: cells with y ≥ `y` whose centre lies within `r` of (x, z) are treated as air (and their exposed faces
 * are flagged so the shader hatches them). */
export interface CutParams {
  y: number;
  x: number;
  z: number;
  r: number;
}

export interface SectionMesh {
  cx: number;
  sy: number;
  cz: number;
  solid: MeshArrays | null;
  translucent: MeshArrays | null;
  /** Quads in both passes. */
  quads: number;
  /** Cave-culling face connectivity (mesh/visibility.ts); set by the engine. */
  vis?: number;
}

// Padded neighbourhood: x, z from -2..17 (chest pairing looks two cells out), y from -1..16.
const PX = 20, PY = 18, PZ = 20;
const PN = PX * PY * PZ;
const P = (x: number, y: number, z: number) => ((y + 1) * PZ + (z + 2)) * PX + (x + 2);

/** Minecraft side order: down, up, north (-z), south (+z), west (-x), east (+x). */
export const FACE_N: ReadonlyArray<readonly [number, number, number]> = [
  [0, -1, 0], [0, 1, 0], [0, 0, -1], [0, 0, 1], [-1, 0, 0], [1, 0, 0],
];

/** Canonical cube corners per face (BlockMesh.CubeFace), counter-clockwise seen from outside. */
export const CUBE_CORNERS: ReadonlyArray<ReadonlyArray<readonly [number, number, number]>> = [
  [[0, 0, 1], [0, 0, 0], [1, 0, 0], [1, 0, 1]],
  [[0, 1, 0], [0, 1, 1], [1, 1, 1], [1, 1, 0]],
  [[1, 1, 0], [1, 0, 0], [0, 0, 0], [0, 1, 0]],
  [[0, 1, 1], [0, 0, 1], [1, 0, 1], [1, 1, 1]],
  [[0, 1, 0], [0, 0, 0], [0, 0, 1], [0, 1, 1]],
  [[1, 1, 1], [1, 0, 1], [1, 0, 0], [1, 1, 0]],
];

/** RenderBlocks face shade: bottom 0.5, top 1, N/S 0.8, E/W 0.6. */
export const FACE_SHADE = [0.5, 1, 0.8, 0.8, 0.6, 0.6];

/** Tangent axes (0 x, 1 y, 2 z) of each face, for the smooth-light corner samples. */
const TANGENTS: ReadonlyArray<readonly [number, number]> = [[0, 2], [0, 2], [0, 1], [0, 1], [2, 1], [2, 1]];

const AO = [1, 0.8, 0.64, 0.5];

/** BlockGeometry.FaceUv: block-local (u, v) of a point on a face (top/bottom: x, z; sides: across, 1 - y). */
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

const ids = new Uint8Array(PN);
const metas = new Uint8Array(PN);
const sky = new Int8Array(PN);
const blk = new Int8Array(PN);
const cutMark = new Uint8Array(PN);
const solid = new VertexSink();
const trans = new VertexSink();

let T: StateTable;
let remembered = 0;
let biomes: Uint8Array | null = null;

function mul(rgb: number, k: number): number {
  const r = Math.min(255, ((rgb >> 16) & 255) * k) | 0, g = Math.min(255, ((rgb >> 8) & 255) * k) | 0, b = Math.min(255, (rgb & 255) * k) | 0;
  return (r << 16) | (g << 8) | b;
}

function mulRgb(a: number, b: number): number {
  const r = (((a >> 16) & 255) * ((b >> 16) & 255)) / 255, g = (((a >> 8) & 255) * ((b >> 8) & 255)) / 255, bl = ((a & 255) * (b & 255)) / 255;
  return ((r | 0) << 16) | ((g | 0) << 8) | (bl | 0);
}

function fill(store: ColumnStore, dim: number, cx: number, sy: number, cz: number, cut: CutParams | null | undefined): void {
  ids.fill(0);
  metas.fill(0);
  sky.fill(15);
  blk.fill(0);
  cutMark.fill(0);
  const baseX = cx * 16, baseZ = cz * 16, baseY = sy * 16;
  const cols: Array<StoredColumn | undefined> = [];
  for (let dz = -1; dz <= 1; dz++) for (let dx = -1; dx <= 1; dx++) cols.push(store.get(dim, cx + dx, cz + dz));
  for (let pz = -2; pz < 18; pz++) {
    const cDz = pz < 0 ? -1 : pz > 15 ? 1 : 0;
    for (let px = -2; px < 18; px++) {
      const cDx = px < 0 ? -1 : px > 15 ? 1 : 0;
      const c = cols[(cDz + 1) * 3 + (cDx + 1)];
      const lx = px & 15, lz = pz & 15;
      const wx = baseX + px, wz = baseZ + pz;
      const cutCol = cut ? (wx + 0.5 - cut.x) ** 2 + (wz + 0.5 - cut.z) ** 2 <= cut.r * cut.r : false;
      for (let py = -1; py < 17; py++) {
        const y = baseY + py;
        const p = P(px, py, pz);
        if (!c) {
          sky[p] = 15;
          continue;
        }
        if (y < 0) {
          ids[p] = 7; // under the world: treat as bedrock so bottom faces are not drawn
          sky[p] = 0;
          continue;
        }
        if (y >= c.height) continue;
        const i = cellIndex(lx, y, lz);
        let id = c.ids[i];
        if (cutCol && cut && y >= cut.y && id !== 0) {
          cutMark[p] = 1;
          id = 0;
        }
        ids[p] = id;
        metas[p] = c.meta[i];
        if (c.light) {
          const v = lightPacked(c.light, lx, y, lz);
          sky[p] = v >> 4;
          blk[p] = v & 15;
        } else {
          const top = c.top[lx * 16 + lz];
          if (y > top) {
            sky[p] = 15;
            blk[p] = 0;
          } else {
            sky[p] = -1;
            blk[p] = -1;
          }
        }
        if (cutMark[p]) {
          sky[p] = 15;
          if (blk[p] < 0) blk[p] = 0;
        }
      }
    }
  }
}

const stateAt = (p: number) => (ids[p] << 4) | metas[p];
const opaqueAt = (p: number) => ids[p] !== 0 && (T.flags[stateAt(p)] & F_OPAQUE) !== 0;

/** Biome-adjusted tint of a textured face. */
function faceTint(f: ResolvedFace, x: number, z: number): number {
  if (!f.biome || !biomes) return f.tint;
  const b = T.biomes.get(biomes[(z << 4) | x]);
  if (!b) return f.tint;
  const k = f.biome - 1;
  const def = T.defaultTints[k];
  if (k < 2 && f.tint !== def) return f.tint; // spruce/birch keep their fixed colours
  return b[k] >= 0 ? b[k] : f.tint;
}

interface TexChoice {
  col: number;
  row: number;
  tex: number;
  rgb: number;
  face: ResolvedFace | null;
}

const texOut: TexChoice = { col: 0, row: 0, tex: 0, rgb: 0, face: null };

function texFor(s: number, f: number, x: number, z: number): TexChoice {
  const rf = T.faces[s]?.[f] ?? null;
  if (rf) {
    texOut.col = rf.c;
    texOut.row = rf.r;
    texOut.tex = TEX_TEXTURED | (rf.flags & 6);
    texOut.rgb = faceTint(rf, x, z);
    texOut.face = rf;
  } else {
    texOut.col = texOut.row = texOut.tex = 0;
    let rgb = f <= 1 ? T.top[s] : T.side[s];
    const fl = T.flags[s];
    if (biomes && (fl & (F_BIOME_GRASS | F_BIOME_FOLIAGE)) && (f === 1 || fl & F_BIOME_FOLIAGE)) {
      const b = T.biomes.get(biomes[(z << 4) | x]);
      const c = b ? b[fl & F_BIOME_GRASS ? 0 : 1] : -1;
      if (c >= 0) rgb = mul(c, 0.85);
    }
    texOut.rgb = rgb;
    texOut.face = null;
  }
  return texOut;
}

function sinkFor(layer: number): VertexSink {
  return layer === Layer.Translucent ? trans : solid;
}

function lightFlags(s: number): number {
  return remembered | (T.flags[s] & F_EMISSIVE ? LF_EMISSIVE : 0);
}

// ---------------------------------------------------------------------------------------------------------------- cubes

function cube(x: number, y: number, z: number, s: number): void {
  const id = s >> 4;
  const fl = T.flags[s];
  const sink = sinkFor(T.layer[s]);
  const p = P(x, y, z);
  for (let f = 0; f < 6; f++) {
    const n = FACE_N[f];
    const q = p + n[0] + n[1] * PX * PZ + n[2] * PX;
    const nid = ids[q];
    if (nid !== 0) {
      if (T.flags[(nid << 4) | metas[q]] & F_OPAQUE) continue;
      if (fl & F_SELF_CULL && nid === id) continue;
    }
    cubeFace(sink, x, y, z, s, f, q);
  }
}

const cl = new Float32Array(4 * 3);

/** Smooth light + AO of the four corners of face f of cell (x, y, z), whose front cell is q. Returns the face's light flags. */
function cornerLights(x: number, y: number, z: number, f: number, q: number): number {
  const [ta, tb] = TANGENTS[f];
  const step = [1, PX * PZ, PX];
  const corners = CUBE_CORNERS[f];
  const unknown = sky[q] < 0;
  for (let k = 0; k < 4; k++) {
    const c = corners[k];
    const sa = c[ta] * 2 - 1, sb = c[tb] * 2 - 1;
    const A = q + sa * step[ta], B = q + sb * step[tb], C = A + sb * step[tb];
    const oa = opaqueAt(A), ob = opaqueAt(B), oc = opaqueAt(C);
    const ao = oa && ob ? 3 : +oa + +ob + +oc;
    let ss = 0, bs = 0, n = 0;
    if (sky[q] >= 0) { ss += sky[q]; bs += blk[q]; n++; }
    if (!oa && sky[A] >= 0) { ss += sky[A]; bs += blk[A]; n++; }
    if (!ob && sky[B] >= 0) { ss += sky[B]; bs += blk[B]; n++; }
    if (!oc && !(oa && ob) && sky[C] >= 0) { ss += sky[C]; bs += blk[C]; n++; }
    cl[k * 3] = n ? ss / n : 15;
    cl[k * 3 + 1] = n ? bs / n : 0;
    cl[k * 3 + 2] = AO[ao];
  }
  return unknown ? LF_UNKNOWN : 0;
}

function cubeFace(sink: VertexSink, x: number, y: number, z: number, s: number, f: number, q: number): void {
  const lf = cornerLights(x, y, z, f, q) | lightFlags(s) | (cutMark[q] ? LF_CUT : 0);
  const t = texFor(s, f, x, z);
  const corners = CUBE_CORNERS[f];
  const flip = cl[2] + cl[8] < cl[5] + cl[11];
  const alpha = t.tex ? 1 : T.alpha[s];
  const rgb = mul(t.rgb, FACE_SHADE[f]);
  const face = t.face;
  emitCorners(sink, x, y, z, f, corners, t.col, t.row, t.tex, rgb, alpha, lf, flip, face?.uv);
  if (face && face.ovC >= 0) emitCorners(solid, x, y, z, f, corners, face.ovC, face.ovR, TEX_TEXTURED, mul(face.ovTint, FACE_SHADE[f]), 1, lf, flip, face.uv);
}

function emitCorners(sink: VertexSink, x: number, y: number, z: number, f: number, corners: ReadonlyArray<readonly [number, number, number]>, col: number, row: number, tex: number, rgb: number, alpha: number, lf: number, flip: boolean, uv: number[] | undefined): void {
  const v = sink.begin(flip);
  for (let k = 0; k < 4; k++) {
    const c = corners[k];
    const [u, w] = uv ? [uv[k * 2], uv[k * 2 + 1]] : faceUv(f, c[0], c[1], c[2]);
    sink.vertex(v + k, x + c[0], y + c[1], z + c[2], u, w);
    sink.lightAt(v + k, cl[k * 3], cl[k * 3 + 1], cl[k * 3 + 2], lf);
  }
  sink.quadAttrs(v, col, row, tex, rgb, alpha, x, y, z);
}

// ---------------------------------------------------------------------------------------------------------------- boxes

/** A box in block-local coordinates with vanilla UVs; faces on the cell boundary are culled against opaque neighbours. */
function box(x: number, y: number, z: number, s: number, x0: number, y0: number, z0: number, x1: number, y1: number, z1: number, skip = 0, layer = T.layer[s]): void {
  const sink = sinkFor(layer === Layer.Translucent ? Layer.Translucent : Layer.Opaque);
  const p = P(x, y, z);
  const own = p;
  const lo = [x0, y0, z0], hi = [x1, y1, z1];
  for (let f = 0; f < 6; f++) {
    if ((skip >> f) & 1) continue;
    const n = FACE_N[f];
    const onEdge = (f === 0 && y0 <= 0) || (f === 1 && y1 >= 1) || (f === 2 && z0 <= 0) || (f === 3 && z1 >= 1) || (f === 4 && x0 <= 0) || (f === 5 && x1 >= 1);
    const q = p + n[0] + n[1] * PX * PZ + n[2] * PX;
    if (onEdge && opaqueAt(q)) continue;
    // Slabs, stairs and other partial blocks often hold no light of their own (vanilla lights them from their
    // neighbours): take the brighter of the cell and the non-opaque cell the face looks at.
    let lp = own;
    if (!opaqueAt(q) && sky[q] >= 0 && (sky[lp] < 0 || Math.max(sky[q], blk[q]) > Math.max(sky[lp], blk[lp]))) lp = q;
    if (sky[lp] < 0 && !opaqueAt(p + PX * PZ) && sky[p + PX * PZ] >= 0) lp = p + PX * PZ;
    const known = sky[lp] >= 0;
    const ls = known ? sky[lp] : 15, lb = known ? blk[lp] : 0;
    const lf = lightFlags(s) | (known ? 0 : LF_UNKNOWN);
    const t = texFor(s, f, x, z);
    const rgb = mul(t.rgb, FACE_SHADE[f]);
    const alpha = t.tex ? 1 : T.alpha[s];
    const corners = CUBE_CORNERS[f];
    const v = sink.begin(false);
    for (let k = 0; k < 4; k++) {
      const c = corners[k];
      const px = c[0] ? hi[0] : lo[0], py = c[1] ? hi[1] : lo[1], pz = c[2] ? hi[2] : lo[2];
      const [u, w] = faceUv(f, px, py, pz);
      sink.vertex(v + k, x + px, y + py, z + pz, u, w);
      sink.lightAt(v + k, ls, lb, 1, lf);
    }
    sink.quadAttrs(v, t.col, t.row, t.tex, rgb, alpha, x, y, z);
    const face = t.face;
    if (face && face.ovC >= 0) {
      const v2 = solid.begin(false);
      for (let k = 0; k < 4; k++) {
        const c = corners[k];
        const px = c[0] ? hi[0] : lo[0], py = c[1] ? hi[1] : lo[1], pz = c[2] ? hi[2] : lo[2];
        const [u, w] = faceUv(f, px, py, pz);
        solid.vertex(v2 + k, x + px, y + py, z + pz, u, w);
        solid.lightAt(v2 + k, ls, lb, 1, lf);
      }
      solid.quadAttrs(v2, face.ovC, face.ovR, TEX_TEXTURED, mul(face.ovTint, FACE_SHADE[f]), 1, x, y, z);
    }
  }
}

/** A free quad (block-local corners P0..P3, uv per corner) lit by the block's own cell; optional mirror for two-sided. */
function plane(x: number, y: number, z: number, s: number, pts: number[], uvs: number[], texFace: number, twoSided: boolean, shade = 1, layer = T.layer[s]): void {
  const sink = sinkFor(layer === Layer.Translucent ? Layer.Translucent : Layer.Opaque);
  const p = P(x, y, z);
  const known = sky[p] >= 0;
  const lf = lightFlags(s) | (known ? 0 : LF_UNKNOWN);
  const t = texFor(s, texFace, x, z);
  const v = sink.begin(false);
  for (let k = 0; k < 4; k++) {
    sink.vertex(v + k, x + pts[k * 3], y + pts[k * 3 + 1], z + pts[k * 3 + 2], uvs[k * 2], uvs[k * 2 + 1]);
    sink.lightAt(v + k, known ? sky[p] : 15, known ? blk[p] : 0, 1, lf);
  }
  sink.quadAttrs(v, t.col, t.row, t.tex, mul(t.rgb, shade), t.tex ? 1 : T.alpha[s], x, y, z);
  if (twoSided) sink.mirror(v);
}

const UV_FULL = [0, 0, 0, 1, 1, 1, 1, 0];

function cross(x: number, y: number, z: number, s: number): void {
  const a = 0.05, b = 0.95;
  plane(x, y, z, s, [a, 1, a, a, 0, a, b, 0, b, b, 1, b], UV_FULL, 2, true);
  plane(x, y, z, s, [b, 1, a, b, 0, a, a, 0, b, a, 1, b], UV_FULL, 2, true);
}

/** A wall plane at distance o from side `side` (2 north, 3 south, 4 west, 5 east, 1 = ceiling). */
function wallPlane(x: number, y: number, z: number, s: number, side: number, o = 1 / 16, y0 = 0, y1 = 1): void {
  let pts: number[];
  switch (side) {
    case 2: pts = [1, y1, o, 1, y0, o, 0, y0, o, 0, y1, o]; break;
    case 3: pts = [0, y1, 1 - o, 0, y0, 1 - o, 1, y0, 1 - o, 1, y1, 1 - o]; break;
    case 4: pts = [o, y1, 0, o, y0, 0, o, y0, 1, o, y1, 1]; break;
    case 5: pts = [1 - o, y1, 1, 1 - o, y0, 1, 1 - o, y0, 0, 1 - o, y1, 0]; break;
    default: pts = [0, 1 - o, 0, 0, 1 - o, 1, 1, 1 - o, 1, 1, 1 - o, 0];
  }
  plane(x, y, z, s, pts, UV_FULL, side === 1 ? 1 : side, true, side >= 4 ? 0.6 : 0.8);
}

// ---------------------------------------------------------------------------------------------------------------- shapes

const E = 1 / 16;

function sameFluid(a: number, b: number): boolean {
  return a === b || (a === 8 && b === 9) || (a === 9 && b === 8) || (a === 10 && b === 11) || (a === 11 && b === 10);
}

function fluidHeight(meta: number): number {
  return 1 - ((meta >= 8 ? 0 : meta) + 1) / 9;
}

function fluid(x: number, y: number, z: number, s: number): void {
  const id = s >> 4;
  const p = P(x, y, z);
  const above = ids[p + PX * PZ];
  const h = sameFluid(above, id) ? 1 : fluidHeight(s & 15);
  const water = (T.flags[s] & F_WATER) !== 0;
  const sink = sinkFor(T.layer[s]);
  const known = sky[p] >= 0;
  const lf = lightFlags(s) | (known ? 0 : LF_UNKNOWN) | (T.flags[s] & F_LAVA ? LF_EMISSIVE : 0);
  for (let f = 0; f < 6; f++) {
    const n = FACE_N[f];
    const q = p + n[0] + n[1] * PX * PZ + n[2] * PX;
    const nid = ids[q];
    if (f === 1) {
      if (sameFluid(nid, id)) continue;
    } else {
      if (sameFluid(nid, id) || opaqueAt(q)) continue;
    }
    const t = texFor(s, f === 1 ? 1 : f === 0 ? 0 : 2, x, z);
    const corners = CUBE_CORNERS[f];
    const v = sink.begin(false);
    const lp = f === 1 && h < 1 ? p : q;
    const ls = sky[lp] >= 0 ? sky[lp] : known ? sky[p] : 15, lb = blk[lp] >= 0 ? blk[lp] : 0;
    for (let k = 0; k < 4; k++) {
      const c = corners[k];
      const py = c[1] ? h : 0;
      const [u, w] = faceUv(f, c[0], py, c[2]);
      sink.vertex(v + k, x + c[0], y + py, z + c[2], u, f >= 2 ? w - (1 - h) : w);
      sink.lightAt(v + k, ls, lb, 1, lf);
    }
    sink.quadAttrs(v, t.col, t.row, t.tex, mul(t.rgb, FACE_SHADE[f]), t.tex ? (water ? 0.8 : 1) : T.alpha[s], x, y, z);
    if (f === 1 && water) sink.mirror(v); // the surface seen from below
  }
}

function chestPartner(x: number, y: number, z: number, id: number): [number, number] | null {
  if (!pairsAsChest(T.version, id)) return null;
  const only = (px: number, pz: number): [number, number] | null => {
    let found: [number, number] | null = null;
    for (const [dx, dz] of [[-1, 0], [1, 0], [0, -1], [0, 1]] as const) {
      if (ids[P(px + dx, y, pz + dz)] !== id) continue;
      if (found) return null;
      found = [dx, dz];
    }
    return found;
  };
  const o = only(x, z);
  if (!o) return null;
  const back = only(x + o[0], z + o[1]);
  return back && back[0] === -o[0] && back[1] === -o[1] ? o : null;
}

function chest(x: number, y: number, z: number, s: number): void {
  const pr = chestPartner(x, y, z, s >> 4);
  const x0 = pr && pr[0] < 0 ? 0 : E, x1 = pr && pr[0] > 0 ? 1 : 1 - E;
  const z0 = pr && pr[1] < 0 ? 0 : E, z1 = pr && pr[1] > 0 ? 1 : 1 - E;
  // The seam faces between the halves are skipped so the pair reads as one large chest.
  let skip = 0;
  if (pr) skip |= 1 << (pr[0] < 0 ? 4 : pr[0] > 0 ? 5 : pr[1] < 0 ? 2 : 3);
  box(x, y, z, s, x0, 0, z0, x1, 14 / 16, z1, skip);
}

function connects(q: number, kind: 'fence' | 'pane' | 'wall'): boolean {
  const id = ids[q];
  if (id === 0) return false;
  const ns = (id << 4) | metas[q];
  if (T.flags[ns] & F_OPAQUE) return true;
  const sh = T.shape[ns];
  if (kind === 'fence') return sh === Shape.Fence || sh === Shape.Gate;
  if (kind === 'wall') return sh === Shape.Wall || sh === Shape.Gate;
  return sh === Shape.Pane || id === 20 || (T.version === '1.12.2' && id === 95);
}

const SIDES: ReadonlyArray<readonly [number, number]> = [[0, -1], [0, 1], [-1, 0], [1, 0]]; // north, south, west, east

function fence(x: number, y: number, z: number, s: number): void {
  box(x, y, z, s, 6 * E, 0, 6 * E, 10 * E, 1, 10 * E);
  for (const [dx, dz] of SIDES) {
    if (!connects(P(x + dx, y, z + dz), 'fence')) continue;
    for (const [b0, b1] of [[6 * E, 9 * E], [12 * E, 15 * E]]) {
      if (dx) box(x, y, z, s, dx < 0 ? 0 : 10 * E, b0, 7 * E, dx < 0 ? 6 * E : 1, b1, 9 * E);
      else box(x, y, z, s, 7 * E, b0, dz < 0 ? 0 : 10 * E, 9 * E, b1, dz < 0 ? 6 * E : 1);
    }
  }
}

function wall(x: number, y: number, z: number, s: number): void {
  box(x, y, z, s, 4 * E, 0, 4 * E, 12 * E, 1, 12 * E);
  for (const [dx, dz] of SIDES) {
    if (!connects(P(x + dx, y, z + dz), 'wall')) continue;
    if (dx) box(x, y, z, s, dx < 0 ? 0 : 12 * E, 0, 5 * E, dx < 0 ? 4 * E : 1, 13 * E, 11 * E);
    else box(x, y, z, s, 5 * E, 0, dz < 0 ? 0 : 12 * E, 11 * E, 13 * E, dz < 0 ? 4 * E : 1);
  }
}

function pane(x: number, y: number, z: number, s: number): void {
  const c = SIDES.map(([dx, dz]) => connects(P(x + dx, y, z + dz), 'pane'));
  const any = c.some(Boolean);
  box(x, y, z, s, 7 * E, 0, 7 * E, 9 * E, 1, 9 * E);
  SIDES.forEach(([dx, dz], i) => {
    if (any && !c[i]) return;
    if (dx) box(x, y, z, s, dx < 0 ? 0 : 9 * E, 0, 7 * E, dx < 0 ? 7 * E : 1, 1, 9 * E);
    else box(x, y, z, s, 7 * E, 0, dz < 0 ? 0 : 9 * E, 9 * E, 1, dz < 0 ? 7 * E : 1);
  });
}

function gate(x: number, y: number, z: number, s: number): void {
  const meta = s & 15;
  const alongX = (meta & 1) === 0;
  const open = (meta & 4) !== 0;
  if (alongX) {
    box(x, y, z, s, 0, 5 * E, 7 * E, 2 * E, 1, 9 * E);
    box(x, y, z, s, 14 * E, 5 * E, 7 * E, 1, 1, 9 * E);
    if (!open) for (const [b0, b1] of [[6 * E, 9 * E], [12 * E, 15 * E]]) box(x, y, z, s, 2 * E, b0, 7 * E, 14 * E, b1, 9 * E);
  } else {
    box(x, y, z, s, 7 * E, 5 * E, 0, 9 * E, 1, 2 * E);
    box(x, y, z, s, 7 * E, 5 * E, 14 * E, 9 * E, 1, 1);
    if (!open) for (const [b0, b1] of [[6 * E, 9 * E], [12 * E, 15 * E]]) box(x, y, z, s, 7 * E, b0, 2 * E, 9 * E, b1, 14 * E);
  }
}

function door(x: number, y: number, z: number, s: number): void {
  let meta = s & 15;
  if (meta & 8) {
    // Upper half: the facing lives in the lower half's metadata.
    const below = P(x, y - 1, z);
    if (ids[below] === s >> 4) meta = metas[below];
  }
  let dir = meta & 3;
  if (meta & 4) dir = (dir + 1) & 3;
  const t = 3 * E;
  switch (dir) {
    case 0: box(x, y, z, s, 0, 0, 0, t, 1, 1); break;
    case 1: box(x, y, z, s, 0, 0, 0, 1, 1, t); break;
    case 2: box(x, y, z, s, 1 - t, 0, 0, 1, 1, 1); break;
    default: box(x, y, z, s, 0, 0, 1 - t, 1, 1, 1);
  }
}

function stairs(x: number, y: number, z: number, s: number): void {
  const meta = s & 15;
  const upside = T.version === '1.12.2' && (meta & 4) !== 0;
  const [h0, h1] = upside ? [0.5, 1] : [0, 0.5];
  const [s0, s1] = upside ? [0, 0.5] : [0.5, 1];
  box(x, y, z, s, 0, h0, 0, 1, h1, 1);
  switch (meta & 3) {
    case 0: box(x, y, z, s, 0.5, s0, 0, 1, s1, 1); break;
    case 1: box(x, y, z, s, 0, s0, 0, 0.5, s1, 1); break;
    case 2: box(x, y, z, s, 0, s0, 0.5, 1, s1, 1); break;
    default: box(x, y, z, s, 0, s0, 0, 1, s1, 0.5);
  }
}

function slab(x: number, y: number, z: number, s: number): void {
  const top = T.version === '1.12.2' && (s & 8) !== 0;
  if (top) box(x, y, z, s, 0, 0.5, 0, 1, 1, 1);
  else box(x, y, z, s, 0, 0, 0, 1, 0.5, 1);
}

function partial(x: number, y: number, z: number, s: number): void {
  const id = s >> 4, meta = s & 15;
  let h = 1, inset = 0;
  switch (id) {
    case 26: h = 9 * E; break;
    case 27: case 28: case 66: case 157: h = E; break;
    case 55: h = E / 2; break;
    case 60: case 208: h = 15 * E; break;
    case 70: case 72: case 147: case 148: h = E; inset = E; break;
    case 78: h = (((meta & 7) + 1) * 2) / 16; break;
    case 92: h = 0.5; inset = E; break;
    case 93: case 94: case 149: case 150: h = 2 * E; break;
    case 111: h = E / 4; break;
    case 116: h = 12 * E; break;
    case 119: h = 12 * E; break;
    case 120: h = 13 * E; break;
    case 132: h = E; break;
    case 151: case 178: h = 6 * E; break;
    case 171: h = E; break;
    default: h = 0.5;
  }
  box(x, y, z, s, inset, 0, inset, 1 - inset, h, 1 - inset);
}

function trapdoor(x: number, y: number, z: number, s: number): void {
  const meta = s & 15, t = 3 * E;
  if (meta & 4) {
    switch (meta & 3) {
      case 0: box(x, y, z, s, 0, 0, 1 - t, 1, 1, 1); break;
      case 1: box(x, y, z, s, 0, 0, 0, 1, 1, t); break;
      case 2: box(x, y, z, s, 1 - t, 0, 0, 1, 1, 1); break;
      default: box(x, y, z, s, 0, 0, 0, t, 1, 1);
    }
  } else if (T.version === '1.12.2' && meta & 8) box(x, y, z, s, 0, 1 - t, 0, 1, 1, 1);
  else box(x, y, z, s, 0, 0, 0, 1, t, 1);
}

function torch(x: number, y: number, z: number, s: number): void {
  const meta = s & 15;
  let dx = 0, dz = 0, dy = 0;
  if (meta === 1) { dx = -5 * E; dy = 3 * E; }
  else if (meta === 2) { dx = 5 * E; dy = 3 * E; }
  else if (meta === 3) { dz = -5 * E; dy = 3 * E; }
  else if (meta === 4) { dz = 5 * E; dy = 3 * E; }
  box(x, y, z, s, 7 * E + dx, dy, 7 * E + dz, 9 * E + dx, 10 * E + dy, 9 * E + dz, 0, Layer.Opaque);
}

function small(x: number, y: number, z: number, s: number): void {
  const id = s >> 4, m = s & 7;
  const wallMount = id === 69 || id === 77 || id === 143 || id === 131;
  if (wallMount && m >= 1 && m <= 4) {
    const [a, b] = [5 * E, 11 * E];
    switch (m) {
      case 1: box(x, y, z, s, 0, a, a, 2 * E, b, b); return;
      case 2: box(x, y, z, s, 14 * E, a, a, 1, b, b); return;
      case 3: box(x, y, z, s, a, a, 0, b, b, 2 * E); return;
      default: box(x, y, z, s, a, a, 14 * E, b, b, 1); return;
    }
  }
  box(x, y, z, s, 5 * E, 0, 5 * E, 11 * E, 6 * E, 11 * E);
}

function post(x: number, y: number, z: number, s: number): void {
  box(x, y, z, s, 7 * E, 0, 7 * E, 9 * E, 9 * E, 9 * E);
  box(x, y, z, s, 0, 9 * E, 7 * E, 1, 1, 9 * E);
}

function wallMounted(x: number, y: number, z: number, s: number): void {
  const id = s >> 4, meta = s & 15;
  if (id === 106) {
    // Vines: a plane per attached side (1 south, 2 west, 4 north, 8 east), the ceiling when none.
    if (meta & 1) wallPlane(x, y, z, s, 3);
    if (meta & 2) wallPlane(x, y, z, s, 4);
    if (meta & 4) wallPlane(x, y, z, s, 2);
    if (meta & 8) wallPlane(x, y, z, s, 5);
    if (!meta) wallPlane(x, y, z, s, 1);
    return;
  }
  const side = meta === 2 ? 3 : meta === 3 ? 2 : meta === 4 ? 5 : meta === 5 ? 4 : 3;
  if (id === 68 || id === 177) {
    // Wall sign / banner: a board against the wall.
    const t = 2 * E;
    switch (side) {
      case 2: box(x, y, z, s, 0, 4.5 * E, 0, 1, 12.5 * E, t); return;
      case 3: box(x, y, z, s, 0, 4.5 * E, 1 - t, 1, 12.5 * E, 1); return;
      case 4: box(x, y, z, s, 0, 4.5 * E, 0, t, 12.5 * E, 1); return;
      default: box(x, y, z, s, 1 - t, 4.5 * E, 0, 1, 12.5 * E, 1); return;
    }
  }
  wallPlane(x, y, z, s, side);
}

function atlasQuads(x: number, y: number, z: number, s: number, quads: AtlasQuad[]): void {
  const p = P(x, y, z);
  for (const q of quads) {
    const layer = q.pass === 'translucent' ? Layer.Translucent : q.pass ? Layer.Opaque : T.layer[s];
    const sink = sinkFor(layer);
    let lp = p;
    if (q.face != null && q.face >= 0 && q.face < 6) {
      const n = FACE_N[q.face];
      const nq = p + n[0] + n[1] * PX * PZ + n[2] * PX;
      if (!opaqueAt(nq)) lp = nq;
    }
    const known = sky[lp] >= 0;
    const lf = lightFlags(s) | (known ? 0 : LF_UNKNOWN);
    const textured = !q.untextured;
    let rgb = textured ? (q.tint ?? 0xffffff) : (q.flat ?? T.side[s]);
    if (textured && q.biome && biomes) rgb = faceTint({ c: q.c, r: q.r, tint: rgb, ovC: -1, ovR: -1, ovTint: 0, flags: 0, biome: q.biome === 'grass' ? 1 : q.biome === 'foliage' ? 2 : 3 }, x, z);
    rgb = mul(rgb, q.shade ?? 1);
    const alpha = q.alpha ?? (layer === Layer.Translucent && !textured ? 0.62 : 1);
    const emit = (col: number, row: number, tex: number, colour: number, sk: VertexSink) => {
      const v = sk.begin(false);
      for (let k = 0; k < 4; k++) {
        sk.vertex(v + k, x + q.p[k * 3], y + q.p[k * 3 + 1], z + q.p[k * 3 + 2], q.uv[k * 2], q.uv[k * 2 + 1]);
        sk.lightAt(v + k, known ? sky[lp] : 15, known ? blk[lp] : 0, 1, lf);
      }
      sk.quadAttrs(v, col, row, tex, colour, alpha, x, y, z);
      if (q.twoSided) sk.mirror(v);
    };
    emit(q.c, q.r, textured ? TEX_TEXTURED | ((q.flags ?? 0) & 6) : 0, rgb, sink);
    if (textured && q.ov) emit(q.ov.c, q.ov.r, TEX_TEXTURED, mul(q.ov.tint ?? 0xffffff, q.shade ?? 1), layer === Layer.Translucent ? sink : solid);
  }
}

// ---------------------------------------------------------------------------------------------------------------- entry

export function emptySection(cx: number, sy: number, cz: number): SectionMesh {
  return { cx, sy, cz, solid: null, translucent: null, quads: 0 };
}

/** Meshes section (cx, sy, cz) of `dim` from the store. `cut` = the roof cut-away (null = none). */
export function meshSection(store: ColumnStore, dim: number, cx: number, sy: number, cz: number, table: StateTable, cut?: CutParams | null): SectionMesh {
  const centre = store.get(dim, cx, cz);
  if (!centre || sy < 0 || sy * 16 >= centre.height) return emptySection(cx, sy, cz);
  // Fast empty check before building the neighbourhood.
  let any = false;
  for (let x = 0; x < 16 && !any; x++)
    for (let z = 0; z < 16 && !any; z++) {
      const i = cellIndex(x, sy * 16, z);
      for (let y = 0; y < 16; y++) if (centre.ids[i + y] !== 0) { any = true; break; }
    }
  if (!any) return emptySection(cx, sy, cz);
  T = table;
  remembered = centre.live ? 0 : LF_REMEMBERED;
  biomes = centre.light?.biomes ?? null;
  fill(store, dim, cx, sy, cz, cut);
  solid.reset();
  trans.reset();
  for (let y = 0; y < 16; y++)
    for (let z = 0; z < 16; z++)
      for (let x = 0; x < 16; x++) {
        const p = P(x, y, z);
        const id = ids[p];
        if (id === 0) continue;
        const s = (id << 4) | metas[p];
        const quads = T.quads[s];
        switch (T.shape[s] as Shape) {
          case Shape.None: break;
          case Shape.Cube: cube(x, y, z, s); break;
          case Shape.Quads: if (quads) atlasQuads(x, y, z, s, quads); else cube(x, y, z, s); break;
          case Shape.Cross: cross(x, y, z, s); break;
          case Shape.Torch: torch(x, y, z, s); break;
          case Shape.Fluid: fluid(x, y, z, s); break;
          case Shape.Slab: slab(x, y, z, s); break;
          case Shape.Partial: partial(x, y, z, s); break;
          case Shape.Fence: fence(x, y, z, s); break;
          case Shape.Pane: pane(x, y, z, s); break;
          case Shape.Wall: wall(x, y, z, s); break;
          case Shape.Door: door(x, y, z, s); break;
          case Shape.WallPlane: wallMounted(x, y, z, s); break;
          case Shape.Stairs: stairs(x, y, z, s); break;
          case Shape.Chest: chest(x, y, z, s); break;
          case Shape.Inset: box(x, y, z, s, E, 0, E, 1 - E, 1, 1 - E); break;
          case Shape.Small: small(x, y, z, s); break;
          case Shape.Post: post(x, y, z, s); break;
          case Shape.Trapdoor: trapdoor(x, y, z, s); break;
          case Shape.Gate: gate(x, y, z, s); break;
          default: cube(x, y, z, s);
        }
      }
  if (sy === 0 && centre.ids[cellIndex(0, 0, 0)] !== 0) {
    // Close the world's floor with one downward quad per column (the per-cell bottoms are culled against the void): the
    // cut-away cap pass needs a back face under solid rock to paint, or a cut would show the sky through the ground.
    const v = solid.begin(false);
    const pts = [[0, 0, 16], [0, 0, 0], [16, 0, 0], [16, 0, 16]];
    for (let k = 0; k < 4; k++) {
      solid.vertex(v + k, pts[k][0], 0, pts[k][2], pts[k][0] / 16, pts[k][2] / 16);
      solid.lightAt(v + k, 0, 0, 1, remembered);
    }
    solid.quadAttrs(v, 0, 0, 0, 0x151515, 1, 0, 0, 0);
  }
  const sm = solid.finish();
  const tm = trans.finish();
  return { cx, sy, cz, solid: sm, translucent: tm, quads: (sm?.quads ?? 0) + (tm?.quads ?? 0) };
}

/** Exposed for tests: the opaque flag lookup the culling uses. */
export const _internals = { P, PX, PY, PZ, mulRgb };
