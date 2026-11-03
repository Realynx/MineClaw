// Meshes one 16³ section into a solid pass (opaque + alpha-tested cut-outs) and a translucent pass, the way the C# GPU
// freecam's SectionStore does. Every block becomes the quads its version's BlockSource gives (1.12.2 baked models,
// tile-entity geometry and fluids; b1.8.1 render types), and each quad is:
// - culled by its cull face against an opaque cube neighbour (and same-id glass, ice and slime);
// - lit per vertex from the payload's sky/block light: axis-aligned quads take the smooth light + ambient occlusion of the
//   face plane they lie in (the four cells in front of each corner, bilinearly interpolated to the vertex), flush faces from
//   the neighbour cell, inner faces from the brighter of their own cell and the cell they face; rotated quads (plants) take
//   their cell's light flat. Model `ambientocclusion: false` and light-emitting blocks skip the occlusion;
// - tinted (biome grass/foliage/water blended over 3×3 columns, or a fixed colour), shaded by its face, and written with its
//   atlas cell (or untextured, the tile's average colour).
// Light is stored per vertex as raw sky/block levels, so day and night are a shader uniform and never a remesh.

import { cellIndex, lightPacked } from '../wire/chunk';
import { F_EMISSIVE, F_OPAQUE, F_SELF_CULL, Layer } from '../world/blocks';
import type { ColumnStore, StoredColumn } from './columns';
import type { Nb } from './models/states';
import { CUBE_CORNERS, FACE_N, FACE_SHADE, TINT_FIXED, faceUv, type Quad } from './quad';
import type { StateTable } from './states';
import { LF_CUT, LF_EMISSIVE, LF_REMEMBERED, LF_UNKNOWN, TEX_TEXTURED, TEX_WRAP, VertexSink, type MeshArrays } from './sink';

export { CUBE_CORNERS, FACE_N, FACE_SHADE, faceUv };

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
  /** Animated atlas cells (row * columns + column) this section samples: the atlas animation's visible-only hook. */
  animCells?: number[];
}

// Padded neighbourhood: x, z from -2..17 (chest pairing looks two cells out), y from -1..16.
const PX = 20, PY = 18, PZ = 20;
const PN = PX * PY * PZ;
const P = (x: number, y: number, z: number) => ((y + 1) * PZ + (z + 2)) * PX + (x + 2);
const STEP = FACE_N.map(([dx, dy, dz]) => dx + dy * PX * PZ + dz * PX);

/** Tangent axes (0 x, 1 y, 2 z) of each face, for the smooth-light corner samples. */
const TANGENTS: ReadonlyArray<readonly [number, number]> = [[0, 2], [0, 2], [0, 1], [0, 1], [2, 1], [2, 1]];

const AO = [1, 0.8, 0.64, 0.5];

const ids = new Uint8Array(PN);
const metas = new Uint8Array(PN);
const sky = new Int8Array(PN);
const blk = new Int8Array(PN);
const cutMark = new Uint8Array(PN);
const bio = new Int16Array(18 * 18);
const tintCache = new Int32Array(3 * 256);
const solid = new VertexSink();
const trans = new VertexSink();
const scratch: Quad[] = [];
const anim = new Set<number>();

let T: StateTable;
let remembered = 0;
let baseX = 0, baseY = 0, baseZ = 0;
let hasBiomes = false;

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
  bio.fill(-1);
  hasBiomes = false;
  baseX = cx * 16;
  baseZ = cz * 16;
  baseY = sy * 16;
  const cols: Array<StoredColumn | undefined> = [];
  for (let dz = -1; dz <= 1; dz++) for (let dx = -1; dx <= 1; dx++) cols.push(store.get(dim, cx + dx, cz + dz));
  for (let pz = -2; pz < 18; pz++) {
    const cDz = pz < 0 ? -1 : pz > 15 ? 1 : 0;
    for (let px = -2; px < 18; px++) {
      const cDx = px < 0 ? -1 : px > 15 ? 1 : 0;
      const c = cols[(cDz + 1) * 3 + (cDx + 1)];
      const lx = px & 15, lz = pz & 15;
      const wx = baseX + px, wz = baseZ + pz;
      if (c?.light?.biomes && px >= -1 && px <= 16 && pz >= -1 && pz <= 16) {
        bio[(pz + 1) * 18 + px + 1] = c.light.biomes[lz * 16 + lx];
        hasBiomes = true;
      }
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
        metas[p] = id === 0 ? 0 : c.meta[i];
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

const opaqueAt = (p: number) => ids[p] !== 0 && (T.flags[(ids[p] << 4) | metas[p]] & F_OPAQUE) !== 0;

/** The neighbourhood accessor sources read (relative to the block being meshed). */
const nb = {
  p: 0,
  wx: 0,
  wy: 0,
  wz: 0,
  id(dx: number, dy: number, dz: number): number {
    return ids[this.p + dx + dy * PX * PZ + dz * PX];
  },
  meta(dx: number, dy: number, dz: number): number {
    return metas[this.p + dx + dy * PX * PZ + dz * PX];
  },
} satisfies Nb & { p: number };

/** Biome colour (kind 1 grass, 2 foliage, 3 water) at section column (x, z), averaged over the 3×3 columns around it
 * (BiomeColorHelper). Unknown biomes count as plains. */
function biomeTint(kind: number, x: number, z: number): number {
  const k = (kind - 1) * 256 + z * 16 + x;
  const c = tintCache[k];
  if (c >= 0) return c;
  let r = 0, g = 0, b = 0;
  for (let dz = -1; dz <= 1; dz++)
    for (let dx = -1; dx <= 1; dx++) {
      const id = hasBiomes ? bio[(z + dz + 1) * 18 + x + dx + 1] : -1;
      const col = T.source.biomeColour(kind, id < 0 ? 1 : id);
      r += (col >> 16) & 255;
      g += (col >> 8) & 255;
      b += col & 255;
    }
  const out = (Math.round(r / 9) << 16) | (Math.round(g / 9) << 8) | Math.round(b / 9);
  tintCache[k] = out;
  return out;
}

function resolveTint(t: number, x: number, z: number): number {
  if (t >= TINT_FIXED) return t & 0xffffff;
  if (t === 0) return 0xffffff;
  return biomeTint(t, x, z);
}

const cl = new Float32Array(4 * 3);
const vl = new Float32Array(4 * 3);

/** Smooth light + AO of the four corners (CUBE_CORNERS order) of a face of direction f whose front cell is q. */
function cornerLights(f: number, q: number): boolean {
  const [ta, tb] = TANGENTS[f];
  const step = [1, PX * PZ, PX];
  const corners = CUBE_CORNERS[f];
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
  return sky[q] < 0;
}

/** The cell an inner face takes its light from: its own, or the brighter non-opaque cell it faces (slabs, stairs and other
 * partial blocks hold little light of their own; vanilla lights them from their neighbours). */
function lightCell(p: number, dir: number): number {
  let lp = p;
  const n = p + STEP[dir];
  if (!opaqueAt(n) && sky[n] >= 0 && (sky[lp] < 0 || Math.max(sky[n], blk[n]) > Math.max(sky[lp], blk[lp]))) lp = n;
  const up = p + PX * PZ;
  if (sky[lp] < 0 && !opaqueAt(up) && sky[up] >= 0) lp = up;
  return lp;
}

function emit(q: Quad, x: number, y: number, z: number, p: number, id: number, sflags: number): void {
  if (q.cull >= 0) {
    const n = p + STEP[q.cull];
    const nid = ids[n];
    if (nid !== 0 && (T.flags[(nid << 4) | metas[n]] & F_OPAQUE || (sflags & F_SELF_CULL && nid === id))) return;
  }
  const sink = q.layer === Layer.Translucent ? trans : solid;
  const emissive = q.emissive || (sflags & F_EMISSIVE) !== 0;
  let lf = remembered | (emissive ? LF_EMISSIVE : 0);
  const pts = q.p;
  if (q.dir >= 0) {
    const d = q.dir;
    const lc = q.flush ? p + STEP[d] : lightCell(p, d);
    if (cornerLights(d, lc)) lf |= LF_UNKNOWN;
    if (q.flush && cutMark[lc]) lf |= LF_CUT;
    const [ta, tb] = TANGENTS[d];
    const corners = CUBE_CORNERS[d];
    const noAo = !q.ao || emissive;
    for (let k = 0; k < 4; k++) {
      const a = Math.min(1, Math.max(0, pts[k * 3 + ta])), b = Math.min(1, Math.max(0, pts[k * 3 + tb]));
      let s = 0, bl = 0, ao = 0;
      for (let j = 0; j < 4; j++) {
        const c = corners[j];
        const w = (c[ta] ? a : 1 - a) * (c[tb] ? b : 1 - b);
        if (w === 0) continue;
        s += w * cl[j * 3];
        bl += w * cl[j * 3 + 1];
        ao += w * cl[j * 3 + 2];
      }
      vl[k * 3] = s;
      vl[k * 3 + 1] = bl;
      vl[k * 3 + 2] = noAo ? 1 : ao;
    }
  } else {
    const lc = lightCell(p, 1);
    const known = sky[lc] >= 0;
    if (!known) lf |= LF_UNKNOWN;
    for (let k = 0; k < 4; k++) {
      vl[k * 3] = known ? sky[lc] : 15;
      vl[k * 3 + 1] = known ? blk[lc] : 0;
      vl[k * 3 + 2] = 1;
    }
  }
  const tint = resolveTint(q.tint, x, z);
  const rgb = mul(q.textured ? tint : mulRgb(q.flat, tint), q.shade);
  const tex = q.textured ? TEX_TEXTURED | (q.wrap ? TEX_WRAP : 0) : 0;
  const flip = vl[2] + vl[8] < vl[5] + vl[11];
  const v = sink.begin(flip);
  for (let k = 0; k < 4; k++) {
    sink.vertex(v + k, x + pts[k * 3], y + pts[k * 3 + 1], z + pts[k * 3 + 2], q.uv[k * 2], q.uv[k * 2 + 1]);
    sink.lightAt(v + k, vl[k * 3], vl[k * 3 + 1], vl[k * 3 + 2], lf);
  }
  sink.quadAttrs(v, q.c, q.r, tex, rgb, q.alpha, x, y, z, q.span);
  if (q.twoSided) sink.mirror(v);
  if (q.textured) {
    const cell = q.r * T.source.columns + q.c;
    if (T.source.animated.has(cell)) anim.add(cell);
  }
  if (q.ovC >= 0 && q.textured) {
    const os = q.layer === Layer.Translucent ? trans : solid;
    const w = os.begin(flip);
    for (let k = 0; k < 4; k++) {
      os.vertex(w + k, x + pts[k * 3], y + pts[k * 3 + 1], z + pts[k * 3 + 2], q.uv[k * 2], q.uv[k * 2 + 1]);
      os.lightAt(w + k, vl[k * 3], vl[k * 3 + 1], vl[k * 3 + 2], lf);
    }
    os.quadAttrs(w, q.ovC, q.ovR, TEX_TEXTURED, mul(resolveTint(q.ovTint, x, z), q.shade), 1, x, y, z, 0);
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
  fill(store, dim, cx, sy, cz, cut);
  tintCache.fill(-1);
  anim.clear();
  solid.reset();
  trans.reset();
  const src = T.source;
  for (let y = 0; y < 16; y++)
    for (let z = 0; z < 16; z++)
      for (let x = 0; x < 16; x++) {
        const p = P(x, y, z);
        const id = ids[p];
        if (id === 0) continue;
        const s = (id << 4) | metas[p];
        if (T.shape[s] === 0) continue;
        let quads: Quad[];
        if (src.dynamic[s]) {
          nb.p = p;
          nb.wx = baseX + x;
          nb.wy = baseY + y;
          nb.wz = baseZ + z;
          scratch.length = 0;
          src.build(id, s & 15, nb, scratch);
          quads = scratch;
        } else quads = src.quads(s);
        const sflags = T.flags[s];
        for (let i = 0; i < quads.length; i++) emit(quads[i], x, y, z, p, id, sflags);
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
    solid.quadAttrs(v, 0, 0, 0, 0x151515, 1, 0, 0, 0, 0);
  }
  const sm = solid.finish();
  const tm = trans.finish();
  const out: SectionMesh = { cx, sy, cz, solid: sm, translucent: tm, quads: (sm?.quads ?? 0) + (tm?.quads ?? 0) };
  if (anim.size) out.animCells = [...anim];
  return out;
}

/** Exposed for tests. */
export const _internals = { P, PX, PY, PZ, mulRgb };
