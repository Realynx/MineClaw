// ModelDef → geometry arrays (no three.js, so tests run in node): every cuboid's six faces in part space with the vanilla
// box UV layout, normalised by the model's texture size, each vertex tagged with its part index (+ 256 × layer). Winding is
// fixed per face so the outward side is the front face (mirrored boxes included), so the renderer can cull back faces.
// The part tree is flattened depth-first: parents come before their children.

import type { CubeDef, FaceKey, ModelDef, PartDef, V3 } from './types';

/** Bone slots per entity row: parts use 0..MAX_PARTS−1; the last slot holds the LOD box / hitbox matrix. */
export const BONE_SLOTS = 64;
export const MAX_PARTS = BONE_SLOTS - 1;
export const BOX_SLOT = BONE_SLOTS - 1;

export interface FlatPart {
  name: string;
  parent: number;
  pivot: V3;
  rot: V3;
  /** 0 = base, k = ModelDef.layers[k − 1]. */
  layer: number;
}

export interface BuiltModel {
  def: ModelDef;
  parts: FlatPart[];
  index: Record<string, number>;
  positions: Float32Array;
  normals: Float32Array;
  uvs: Float32Array;
  /** part + 256 × layer per vertex. */
  partIds: Float32Array;
  indices: Uint16Array;
  /** Layer name → layer number (1-based). */
  layers: Record<string, number>;
  /** Pixel-space UV rectangles of every face drawn (for tests: they must lie inside the texture). */
  uvRects: Array<[number, number, number, number]>;
}

const FACE_KEYS: FaceKey[] = ['east', 'west', 'up', 'down', 'north', 'south'];
const NOMINAL: V3[] = [
  [1, 0, 0],
  [-1, 0, 0],
  [0, -1, 0],
  [0, 1, 0],
  [0, 0, -1],
  [0, 0, 1],
];
// Corner indices of each face (into the 8 box corners) as vanilla ModelBox builds its quads.
const CORNERS = [
  [5, 1, 2, 6],
  [0, 4, 7, 3],
  [5, 4, 0, 1],
  [2, 3, 7, 6],
  [1, 0, 3, 2],
  [4, 5, 6, 7],
];
/** Texels the face UVs are pulled in by (keeps nearest sampling off the neighbouring texture in the sheet). */
const INSET = 1 / 64;

/** The vanilla box UV rectangle [u1, v1, u2, v2] of face k. */
export function boxFaceUv(c: CubeDef, k: number): [number, number, number, number] {
  const [u, v] = c.uv, w = c.s[0], h = c.s[1], d = c.s[2];
  switch (k) {
    case 0:
      return [u + d + w, v + d, u + d + w + d, v + d + h];
    case 1:
      return [u, v + d, u + d, v + d + h];
    case 2:
      return [u + d, v, u + d + w, v + d];
    case 3:
      return [u + d + w, v, u + d + w + w, v + d];
    case 4:
      return [u + d, v + d, u + d + w, v + d + h];
    default:
      return [u + d + w + d, v + d, u + d + w + d + w, v + d + h];
  }
}

export function flattenParts(def: ModelDef): FlatPart[] {
  const layers = def.layers ?? [];
  const out: FlatPart[] = [];
  const walk = (p: PartDef, parent: number, inherited: number) => {
    const layer = p.layer ? layers.indexOf(p.layer) + 1 : inherited;
    if (p.layer && layer === 0) throw new Error(`${def.id}: part ${p.name} uses unknown layer ${p.layer}`);
    const i = out.length;
    out.push({ name: p.name, parent, pivot: p.pivot, rot: p.rot ?? [0, 0, 0], layer });
    for (const c of p.children ?? []) walk(c, i, layer);
  };
  for (const p of def.parts) walk(p, -1, 0);
  if (out.length > MAX_PARTS) throw new Error(`${def.id}: ${out.length} parts (max ${MAX_PARTS})`);
  return out;
}

export function buildModel(def: ModelDef): BuiltModel {
  const parts = flattenParts(def);
  const index: Record<string, number> = {};
  parts.forEach((p, i) => {
    if (index[p.name] != null) throw new Error(`${def.id}: duplicate part ${p.name}`);
    index[p.name] = i;
  });
  const sink = new CubeSink(def.tex);
  let pi = 0;
  const visit = (p: PartDef) => {
    const me = pi++;
    const tag = me + 256 * parts[me].layer;
    for (const c of p.cubes) sink.cube(c, tag);
    for (const ch of p.children ?? []) visit(ch);
  };
  for (const p of def.parts) visit(p);
  const layerNames: Record<string, number> = {};
  (def.layers ?? []).forEach((l, i) => (layerNames[l] = i + 1));
  return { def, parts, index, layers: layerNames, ...sink.finish(def.id) };
}

/** Collects cuboid faces (vanilla box UVs normalised by `tex`) tagged with a bone slot + 256 × layer. */
export class CubeSink {
  private pos: number[] = [];
  private nor: number[] = [];
  private uv: number[] = [];
  private pid: number[] = [];
  private idx: number[] = [];
  readonly uvRects: Array<[number, number, number, number]> = [];
  constructor(private readonly tex: readonly [number, number]) {}

  cube(c: CubeDef, tag: number): void {
    const [tw, th] = this.tex;
    const { pos, nor, uv, pid, idx } = this;
    const g = c.inflate ?? 0;
    let x0 = c.o[0] - g, x1 = c.o[0] + c.s[0] + g;
    const y0 = c.o[1] - g, y1 = c.o[1] + c.s[1] + g, z0 = c.o[2] - g, z1 = c.o[2] + c.s[2] + g;
    if (c.mirror) [x0, x1] = [x1, x0];
    const v: V3[] = [
      [x0, y0, z0],
      [x1, y0, z0],
      [x1, y1, z0],
      [x0, y1, z0],
      [x0, y0, z1],
      [x1, y0, z1],
      [x1, y1, z1],
      [x0, y1, z1],
    ];
    for (let k = 0; k < 6; k++) {
      let rect: readonly [number, number, number, number];
      if (c.faces) {
        const r = c.faces[FACE_KEYS[k]];
        if (!r) continue;
        rect = r;
      } else rect = boxFaceUv(c, k);
      const q = CORNERS[k].map((i) => v[i]);
      // Skip faces with no area (the edges of a zero-thickness box).
      let n = cross(sub(q[1], q[0]), sub(q[2], q[0]));
      if (Math.hypot(n[0], n[1], n[2]) < 1e-9) {
        n = cross(sub(q[2], q[0]), sub(q[3], q[0]));
        if (Math.hypot(n[0], n[1], n[2]) < 1e-9) continue;
      }
      const nom = NOMINAL[k];
      const out: V3 = c.mirror && k < 2 ? [-nom[0], nom[1], nom[2]] : nom;
      const [u1, v1, u2, v2] = rect;
      this.uvRects.push([Math.min(u1, u2), Math.min(v1, v2), Math.max(u1, u2), Math.max(v1, v2)]);
      // Vertex 0 (u2, v1), 1 (u1, v1), 2 (u1, v2), 3 (u2, v2), pulled in by INSET.
      const su = Math.sign(u2 - u1) * INSET, sv = Math.sign(v2 - v1) * INSET;
      let corners: Array<[V3, number, number]> = [
        [q[0], u2 - su, v1 + sv],
        [q[1], u1 + su, v1 + sv],
        [q[2], u1 + su, v2 - sv],
        [q[3], u2 - su, v2 - sv],
      ];
      if (dot(n, out) < 0) corners = [corners[0], corners[3], corners[2], corners[1]];
      const base = pos.length / 3;
      for (const [p, cu, cv] of corners) {
        pos.push(p[0], p[1], p[2]);
        nor.push(out[0], out[1], out[2]);
        uv.push(cu / tw, cv / th);
        pid.push(tag);
      }
      // Counter-clockwise seen from outside in a right-handed frame: (0, 1, 2), (0, 2, 3).
      idx.push(base, base + 1, base + 2, base, base + 2, base + 3);
    }
  }

  finish(id: string): Pick<BuiltModel, 'positions' | 'normals' | 'uvs' | 'partIds' | 'indices' | 'uvRects'> {
    if (this.pos.length / 3 > 65535) throw new Error(`${id}: too many vertices`);
    return {
      positions: new Float32Array(this.pos),
      normals: new Float32Array(this.nor),
      uvs: new Float32Array(this.uv),
      partIds: new Float32Array(this.pid),
      indices: new Uint16Array(this.idx),
      uvRects: this.uvRects,
    };
  }
}

const sub = (a: V3, b: V3): V3 => [a[0] - b[0], a[1] - b[1], a[2] - b[2]];
const cross = (a: V3, b: V3): V3 => [a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0]];
const dot = (a: V3, b: V3) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
