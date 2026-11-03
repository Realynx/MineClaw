// The 1.12.2 block model format (models/block/*.json), ported in our own words from the C# ModelBaker (itself from the
// client's ModelBlock, BlockPart, BlockPartFace, BlockFaceUV, FaceBakery and ModelRotation):
// - parent inheritance: textures merge child-first, the nearest model with elements supplies them, `ambientocclusion` is the
//   root model's (ModelBlock.isAmbientOcclusion asks the parent chain);
// - `#variable` texture references;
// - elements: from/to in 0..16, an optional rotation about an origin on one axis (±22.5/45°) with `rescale`;
// - faces: a uv rectangle (default: the element's projection, BlockPart.getFaceUvs), a uv rotation (0/90/180/270),
//   `cullface`, `tintindex`, `shade`;
// - the variant's x/y rotation about the block centre (x first, then y), and `uvlock` (the texture re-projected from the
//   rotated face, which is what uvlock does for the default uvs every vanilla uvlock model uses).

import { FACE_N, FACE_SHADE, dirOf, nearestDir, onBoundary, type Quad, TINT_NONE } from '../quad';
import { Layer } from '../../world/blocks';

interface FaceDef {
  uv: number[] | null;
  texture: string;
  cull: number;
  rotation: number;
  tintindex: number;
}

interface ElementDef {
  from: number[];
  to: number[];
  origin: number[];
  axis: 'x' | 'y' | 'z';
  angle: number;
  rescale: boolean;
  shade: boolean;
  faces: Array<FaceDef | null>;
}

interface ModelDef {
  parent: string | null;
  textures: Record<string, string>;
  elements: ElementDef[] | null;
  ao: boolean | null;
}

/** A baked face plus the facts the block layer resolves (texture name for coverage, tint index). */
export interface BakedFace extends Quad {
  texture: string;
  tintindex: number;
}

/** The atlas lookups the baker needs: a texture path ("blocks/stone") → its cell, and its coverage/average. */
export interface TileLookup {
  cell(name: string): [number, number] | null;
  /** [coverage 0..1, semi-transparent 0/1, average 0xRRGGBB]. */
  info(name: string): readonly [number, number, number] | null;
}

export const faceOf = (n: string): number => ({ down: 0, bottom: 0, up: 1, north: 2, south: 3, west: 4, east: 5 })[n] ?? -1;

/** "oak_stairs" / "block/oak_stairs" / "minecraft:block/oak_stairs" → "block/oak_stairs". */
export function normalizeModel(name: string): string {
  if (name.startsWith('minecraft:')) name = name.slice(10);
  return name.includes('/') ? name : 'block/' + name;
}

const CORNERS: ReadonlyArray<ReadonlyArray<readonly [number, number, number]>> = [
  [[0, 0, 1], [0, 0, 0], [1, 0, 0], [1, 0, 1]],
  [[0, 1, 0], [0, 1, 1], [1, 1, 1], [1, 1, 0]],
  [[1, 1, 0], [1, 0, 0], [0, 0, 0], [0, 1, 0]],
  [[0, 1, 1], [0, 0, 1], [1, 0, 1], [1, 1, 1]],
  [[0, 1, 0], [0, 0, 0], [0, 0, 1], [0, 1, 1]],
  [[1, 1, 1], [1, 0, 1], [1, 0, 0], [1, 1, 0]],
];

function defaultUv(face: number, f: number[], t: number[]): number[] {
  switch (face) {
    case 0: return [f[0], 16 - t[2], t[0], 16 - f[2]];
    case 1: return [f[0], f[2], t[0], t[2]];
    case 2: return [16 - t[0], 16 - t[1], 16 - f[0], 16 - f[1]];
    case 3: return [f[0], 16 - t[1], t[0], 16 - f[1]];
    case 4: return [f[2], 16 - t[1], t[2], 16 - f[1]];
    default: return [16 - t[2], 16 - t[1], 16 - f[2], 16 - f[1]];
  }
}

function project(face: number, p: number[]): [number, number] {
  switch (face) {
    case 0: return [p[0], 16 - p[2]];
    case 1: return [p[0], p[2]];
    case 2: return [16 - p[0], 16 - p[1]];
    case 3: return [p[0], 16 - p[1]];
    case 4: return [p[2], 16 - p[1]];
    default: return [16 - p[2], 16 - p[1]];
  }
}

/** Right-handed rotation of v about an axis by `deg` degrees. */
function rot(v: number[], axis: 'x' | 'y' | 'z', deg: number): number[] {
  const a = (deg * Math.PI) / 180, c = Math.cos(a), s = Math.sin(a);
  if (axis === 'x') return [v[0], v[1] * c - v[2] * s, v[1] * s + v[2] * c];
  if (axis === 'y') return [v[0] * c + v[2] * s, v[1], -v[0] * s + v[2] * c];
  return [v[0] * c - v[1] * s, v[0] * s + v[1] * c, v[2]];
}

const snap = (v: number) => (Math.abs(v - Math.round(v)) < 1e-4 ? Math.round(v) : v);

/** ModelRotation on a 0..16 position about the centre: x first, then y (each by the negated angle, right-handed). */
function variantRotate(p: number[], x: number, y: number): number[] {
  let v = [p[0] - 8, p[1] - 8, p[2] - 8];
  if (x) v = rot(v, 'x', -x);
  if (y) v = rot(v, 'y', -y);
  return [snap(v[0] + 8), snap(v[1] + 8), snap(v[2] + 8)];
}

function variantRotateDir(d: number, x: number, y: number): number {
  let v = [...FACE_N[d]];
  if (x) v = rot(v, 'x', -x);
  if (y) v = rot(v, 'y', -y);
  return dirOf(v);
}

export class ModelBaker {
  private readonly defs = new Map<string, ModelDef>();

  constructor(models: Record<string, unknown>, private readonly tiles: TileLookup) {
    for (const [name, json] of Object.entries(models)) this.add(name, json);
  }

  get count(): number {
    return this.defs.size;
  }

  has(name: string): boolean {
    return this.defs.has(normalizeModel(name));
  }

  /** Adds (or replaces) a model ("block/stairs" → its parsed JSON). */
  add(name: string, json: unknown): void {
    const r = json as { parent?: string; textures?: Record<string, string>; elements?: unknown[]; ambientocclusion?: boolean };
    const elements = Array.isArray(r.elements) ? r.elements.map((e) => element(e as RawElement)) : null;
    this.defs.set(normalizeModel(name), {
      parent: typeof r.parent === 'string' ? normalizeModel(r.parent) : null,
      textures: r.textures ?? {},
      elements,
      ao: typeof r.ambientocclusion === 'boolean' ? r.ambientocclusion : null,
    });
  }

  /** Merged textures, elements and the ambient-occlusion flag of a model, following the parent chain (cycles stop). */
  resolve(name: string): { textures: Record<string, string>; elements: ElementDef[]; ao: boolean } {
    const textures: Record<string, string> = {};
    let elements: ElementDef[] | null = null;
    let ao = true;
    const seen = new Set<string>();
    let cur: string | null = normalizeModel(name);
    while (cur && !seen.has(cur)) {
      seen.add(cur);
      const d = this.defs.get(cur);
      if (!d) break;
      for (const [k, v] of Object.entries(d.textures)) if (!(k in textures)) textures[k] = v;
      elements ??= d.elements;
      ao = d.ao ?? true; // ModelBlock.isAmbientOcclusion: the root model's own flag (a child's is ignored)
      cur = d.parent;
    }
    return { textures, elements: elements ?? [], ao };
  }

  /** Bakes one model under a variant rotation, appending block-local quads (0..1). */
  bake(model: string, xRot: number, yRot: number, uvlock: boolean, out: BakedFace[]): void {
    const { textures, elements, ao } = this.resolve(model);
    const rotated = xRot % 360 !== 0 || yRot % 360 !== 0;
    for (const el of elements) {
      for (let face = 0; face < 6; face++) {
        const fd = el.faces[face];
        if (!fd) continue;
        const r = fd.uv ?? defaultUv(face, el.from, el.to);
        const pos: number[][] = [];
        const uv: number[][] = [];
        for (let i = 0; i < 4; i++) {
          const c = CORNERS[face][i];
          let p = [c[0] ? el.to[0] : el.from[0], c[1] ? el.to[1] : el.from[1], c[2] ? el.to[2] : el.from[2]];
          if (el.angle !== 0) p = rotatePart(p, el);
          pos.push(rotated ? variantRotate(p, xRot, yRot) : p);
          const k = (i + ((fd.rotation / 90) | 0)) % 4;
          uv.push([k === 0 || k === 1 ? r[0] : r[2], k === 0 || k === 3 ? r[1] : r[3]]);
        }
        const flatP = pos.flat().map((v) => v / 16);
        const n = normal(pos);
        const dir = dirOf(n);
        if (uvlock && rotated && dir >= 0 && el.angle === 0) for (let i = 0; i < 4; i++) uv[i] = project(dir, pos[i]);
        let cull = fd.cull;
        if (cull >= 0 && rotated) cull = variantRotateDir(cull, xRot, yRot);
        const texName = resolveTexture(textures, fd.texture);
        const cell = this.tiles.cell(texName);
        const info = this.tiles.info(texName);
        out.push({
          p: flatP,
          uv: uv.flat().map((v) => v / 16),
          c: cell ? cell[0] : 0,
          r: cell ? cell[1] : 0,
          span: 0,
          wrap: false,
          tint: TINT_NONE,
          ovC: -1,
          ovR: -1,
          ovTint: TINT_NONE,
          shade: el.shade ? FACE_SHADE[dir >= 0 ? dir : nearestDir(n)] : 1,
          dir,
          cull,
          flush: dir >= 0 && onBoundary(dir, flatP),
          layer: Layer.Opaque,
          twoSided: false,
          ao,
          emissive: false,
          flat: cell ? (info?.[2] ?? 0x808080) : 0xff00ff,
          alpha: 1,
          textured: !!cell,
          texture: texName,
          tintindex: fd.tintindex,
        });
      }
    }
  }
}

function normal(p: number[][]): number[] {
  const a = [p[1][0] - p[0][0], p[1][1] - p[0][1], p[1][2] - p[0][2]];
  const b = [p[3][0] - p[0][0], p[3][1] - p[0][1], p[3][2] - p[0][2]];
  const n = [a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0]];
  const l = Math.hypot(n[0], n[1], n[2]);
  return l < 1e-12 ? [0, 1, 0] : [n[0] / l, n[1] / l, n[2] / l];
}

function resolveTexture(textures: Record<string, string>, ref: string): string {
  let r = ref;
  for (let i = 0; i < 8 && r.startsWith('#'); i++) r = textures[r.slice(1)] ?? 'missingno';
  if (r.startsWith('#')) return 'missingno';
  return r.startsWith('minecraft:') ? r.slice(10) : r;
}

/** FaceBakery.rotatePart: the element rotation about its origin, with the rescale (1/cos on the two other axes). */
function rotatePart(p: number[], el: ElementDef): number[] {
  const o = el.origin;
  const v = rot([p[0] - o[0], p[1] - o[1], p[2] - o[2]], el.axis, el.angle);
  if (el.rescale) {
    const k = Math.abs(el.angle) === 22.5 ? 1 / Math.cos(Math.PI / 8) - 1 : 1 / Math.cos(Math.PI / 4) - 1;
    const s = el.axis === 'x' ? [0, 1, 1] : el.axis === 'y' ? [1, 0, 1] : [1, 1, 0];
    for (let a = 0; a < 3; a++) v[a] *= s[a] * k + 1;
  }
  return [v[0] + o[0], v[1] + o[1], v[2] + o[2]];
}

interface RawElement {
  from: number[];
  to: number[];
  rotation?: { origin?: number[]; axis?: string; angle?: number; rescale?: boolean };
  shade?: boolean;
  faces?: Record<string, { uv?: number[]; texture?: string; cullface?: string; rotation?: number; tintindex?: number }>;
}

function element(e: RawElement): ElementDef {
  const faces: Array<FaceDef | null> = [null, null, null, null, null, null];
  for (const [name, f] of Object.entries(e.faces ?? {})) {
    const d = faceOf(name);
    if (d < 0) continue;
    faces[d] = {
      uv: f.uv && f.uv.length === 4 ? f.uv : null,
      texture: f.texture ?? '',
      cull: f.cullface ? faceOf(f.cullface) : -1,
      rotation: f.rotation ?? 0,
      tintindex: f.tintindex ?? -1,
    };
  }
  const rt = e.rotation;
  return {
    from: e.from,
    to: e.to,
    origin: rt?.origin ?? [8, 8, 8],
    axis: (rt?.axis as 'x' | 'y' | 'z') ?? 'y',
    angle: rt?.angle ?? 0,
    rescale: rt?.rescale === true,
    shade: e.shade !== false,
    faces,
  };
}
