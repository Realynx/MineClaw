// GPU side of the entity layer: one instanced draw per model kind (opaque, plus a translucent twin when used), one for item
// sprites, one for item/block cubes, one for shadows. Vertex attributes are uploaded once per model; per view only the
// small instance arrays (which entity row, which skin rectangle, tint, light, flashes) change.

import * as THREE from 'three';
import type { BuiltModel } from './builder';

const INSTANCE_ATTRS: Array<[string, number]> = [
  ['aBone', 3],
  ['aTex', 4],
  ['aColor', 4],
  ['aFx', 4],
];
const TILE_ATTRS: Array<[string, number]> = [
  ['aFaceA', 3],
  ['aFaceB', 4],
];

export interface InstanceData {
  row: number;
  slot: number;
  layer: number;
  tex: ArrayLike<number>;
  r: number;
  g: number;
  b: number;
  flags: number;
  hurt: number;
  sky: number;
  block: number;
  flash: number;
  faces?: ArrayLike<number>;
  tintMask?: number;
}

/** One draw call: a base geometry drawn once per instance pushed this view. */
export class InstancedDraw {
  readonly geometry = new THREE.InstancedBufferGeometry();
  readonly mesh: THREE.Mesh;
  private cap = 0;
  private arrays: Record<string, Float32Array> = {};
  private attrs: Record<string, THREE.InstancedBufferAttribute> = {};
  count = 0;

  constructor(
    base: THREE.BufferGeometry,
    material: THREE.Material,
    private readonly tile: boolean,
    name: string,
  ) {
    for (const [k, a] of Object.entries(base.attributes)) this.geometry.setAttribute(k, a);
    this.geometry.setIndex(base.index);
    this.mesh = new THREE.Mesh(this.geometry, material);
    this.mesh.name = name;
    this.mesh.frustumCulled = false;
    this.mesh.visible = false;
    this.grow(8);
  }

  private grow(n: number): void {
    const cap = Math.max(n, this.cap * 2);
    for (const [k, size] of this.tile ? [...INSTANCE_ATTRS, ...TILE_ATTRS] : INSTANCE_ATTRS) {
      const arr = new Float32Array(cap * size);
      if (this.arrays[k]) arr.set(this.arrays[k]);
      this.arrays[k] = arr;
      const attr = new THREE.InstancedBufferAttribute(arr, size);
      attr.setUsage(THREE.DynamicDrawUsage);
      this.attrs[k] = attr;
      this.geometry.setAttribute(k, attr);
    }
    this.cap = cap;
  }

  reset(): void {
    this.count = 0;
  }

  push(d: InstanceData): void {
    if (this.count >= this.cap) this.grow(this.count + 1);
    const i = this.count++;
    const a = this.arrays;
    a.aBone.set([d.row, d.slot, d.layer], i * 3);
    const t = a.aTex;
    t[i * 4] = d.tex[0];
    t[i * 4 + 1] = d.tex[1];
    t[i * 4 + 2] = d.tex[2];
    t[i * 4 + 3] = d.tex[3];
    const c = a.aColor;
    c[i * 4] = d.r;
    c[i * 4 + 1] = d.g;
    c[i * 4 + 2] = d.b;
    c[i * 4 + 3] = d.flags;
    const f = a.aFx;
    f[i * 4] = d.hurt;
    f[i * 4 + 1] = d.sky;
    f[i * 4 + 2] = d.block;
    f[i * 4 + 3] = d.flash;
    if (this.tile) {
      const fa = a.aFaceA, fb = a.aFaceB, fc = d.faces;
      fa[i * 3] = fc?.[0] ?? 0;
      fa[i * 3 + 1] = fc?.[1] ?? 0;
      fa[i * 3 + 2] = fc?.[2] ?? 0;
      fb[i * 4] = fc?.[3] ?? 0;
      fb[i * 4 + 1] = fc?.[4] ?? 0;
      fb[i * 4 + 2] = fc?.[5] ?? 0;
      fb[i * 4 + 3] = d.tintMask ?? 0;
    }
  }

  commit(): void {
    this.geometry.instanceCount = this.count;
    this.mesh.visible = this.count > 0;
    if (!this.count) return;
    for (const [k, size] of this.tile ? [...INSTANCE_ATTRS, ...TILE_ATTRS] : INSTANCE_ATTRS) {
      const attr = this.attrs[k];
      attr.clearUpdateRanges();
      attr.addUpdateRange(0, this.count * size);
      attr.needsUpdate = true;
    }
  }

  dispose(): void {
    this.geometry.dispose();
  }
}

/** A built model's vertex data as a BufferGeometry (positions in part pixels; the bones place them). */
export function modelGeometry(m: BuiltModel): THREE.BufferGeometry {
  const g = new THREE.BufferGeometry();
  g.setAttribute('position', new THREE.BufferAttribute(m.positions, 3));
  g.setAttribute('normal', new THREE.BufferAttribute(m.normals, 3));
  g.setAttribute('uv', new THREE.BufferAttribute(m.uvs, 2));
  g.setAttribute('aPart', new THREE.BufferAttribute(m.partIds, 1));
  g.setIndex(new THREE.BufferAttribute(m.indices, 1));
  return g;
}

/** A two-sided unit sprite (x −0.5..0.5, y 0..1; the back mirrored so icons never read backwards). */
export function spriteGeometry(): THREE.BufferGeometry {
  const pos = [-0.5, 0, 0, 0.5, 0, 0, 0.5, 1, 0, -0.5, 1, 0, 0.5, 0, 0, -0.5, 0, 0, -0.5, 1, 0, 0.5, 1, 0];
  const nor = [0, 0, 1, 0, 0, 1, 0, 0, 1, 0, 0, 1, 0, 0, -1, 0, 0, -1, 0, 0, -1, 0, 0, -1];
  const uv = [0, 1, 1, 1, 1, 0, 0, 0, 1, 1, 0, 1, 0, 0, 1, 0];
  const g = new THREE.BufferGeometry();
  g.setAttribute('position', new THREE.Float32BufferAttribute(pos, 3));
  g.setAttribute('normal', new THREE.Float32BufferAttribute(nor, 3));
  g.setAttribute('uv', new THREE.Float32BufferAttribute(uv, 2));
  g.setAttribute('aPart', new THREE.Float32BufferAttribute(new Array(8).fill(0), 1));
  g.setAttribute('aFace', new THREE.Float32BufferAttribute(new Array(8).fill(-1), 1));
  g.setIndex([0, 1, 2, 0, 2, 3, 4, 5, 6, 4, 6, 7]);
  return g;
}

/** A unit block cube (x, z −0.5..0.5, y 0..1), faces tagged 0..5 = down, up, north, south, west, east. */
export function cubeGeometry(): THREE.BufferGeometry {
  const pos: number[] = [], nor: number[] = [], uv: number[] = [], face: number[] = [], idx: number[] = [];
  // Corners per face, counter-clockwise from outside, with the texture's top edge up (sides) / north (top, bottom).
  const faces: Array<{ n: [number, number, number]; c: Array<[number, number, number, number, number]> }> = [
    { n: [0, -1, 0], c: [[-0.5, 0, 0.5, 0, 1], [-0.5, 0, -0.5, 0, 0], [0.5, 0, -0.5, 1, 0], [0.5, 0, 0.5, 1, 1]] },
    { n: [0, 1, 0], c: [[-0.5, 1, -0.5, 0, 0], [-0.5, 1, 0.5, 0, 1], [0.5, 1, 0.5, 1, 1], [0.5, 1, -0.5, 1, 0]] },
    { n: [0, 0, -1], c: [[0.5, 0, -0.5, 0, 1], [-0.5, 0, -0.5, 1, 1], [-0.5, 1, -0.5, 1, 0], [0.5, 1, -0.5, 0, 0]] },
    { n: [0, 0, 1], c: [[-0.5, 0, 0.5, 0, 1], [0.5, 0, 0.5, 1, 1], [0.5, 1, 0.5, 1, 0], [-0.5, 1, 0.5, 0, 0]] },
    { n: [-1, 0, 0], c: [[-0.5, 0, -0.5, 0, 1], [-0.5, 0, 0.5, 1, 1], [-0.5, 1, 0.5, 1, 0], [-0.5, 1, -0.5, 0, 0]] },
    { n: [1, 0, 0], c: [[0.5, 0, 0.5, 0, 1], [0.5, 0, -0.5, 1, 1], [0.5, 1, -0.5, 1, 0], [0.5, 1, 0.5, 0, 0]] },
  ];
  faces.forEach((f, k) => {
    const base = pos.length / 3;
    for (const [x, y, z, u, v] of f.c) {
      pos.push(x, y, z);
      nor.push(...f.n);
      uv.push(u, v);
      face.push(k);
    }
    idx.push(base, base + 1, base + 2, base, base + 2, base + 3);
  });
  const g = new THREE.BufferGeometry();
  g.setAttribute('position', new THREE.Float32BufferAttribute(pos, 3));
  g.setAttribute('normal', new THREE.Float32BufferAttribute(nor, 3));
  g.setAttribute('uv', new THREE.Float32BufferAttribute(uv, 2));
  g.setAttribute('aPart', new THREE.Float32BufferAttribute(new Array(24).fill(0), 1));
  g.setAttribute('aFace', new THREE.Float32BufferAttribute(face, 1));
  g.setIndex(idx);
  return g;
}

/** Instanced ground blobs (x, z −0.5..0.5 quad). */
export class ShadowDraw {
  readonly geometry = new THREE.InstancedBufferGeometry();
  readonly mesh: THREE.Mesh;
  private cap = 0;
  private shadow = new Float32Array(0);
  private alpha = new Float32Array(0);
  count = 0;

  constructor(material: THREE.Material) {
    this.geometry.setAttribute('position', new THREE.Float32BufferAttribute([-0.5, 0, -0.5, -0.5, 0, 0.5, 0.5, 0, 0.5, 0.5, 0, -0.5], 3));
    this.geometry.setIndex([0, 1, 2, 0, 2, 3]);
    this.mesh = new THREE.Mesh(this.geometry, material);
    this.mesh.name = 'entity shadows';
    this.mesh.frustumCulled = false;
    this.mesh.visible = false;
    this.mesh.renderOrder = 1;
    this.grow(16);
  }

  private grow(n: number): void {
    const cap = Math.max(n, this.cap * 2);
    const s = new Float32Array(cap * 4), a = new Float32Array(cap);
    s.set(this.shadow);
    a.set(this.alpha);
    this.shadow = s;
    this.alpha = a;
    this.geometry.setAttribute('aShadow', new THREE.InstancedBufferAttribute(s, 4).setUsage(THREE.DynamicDrawUsage));
    this.geometry.setAttribute('aAlpha', new THREE.InstancedBufferAttribute(a, 1).setUsage(THREE.DynamicDrawUsage));
    this.cap = cap;
  }

  reset(): void {
    this.count = 0;
  }

  push(x: number, y: number, z: number, r: number, alpha: number): void {
    if (this.count >= this.cap) this.grow(this.count + 1);
    const i = this.count++;
    this.shadow[i * 4] = x;
    this.shadow[i * 4 + 1] = y;
    this.shadow[i * 4 + 2] = z;
    this.shadow[i * 4 + 3] = r;
    this.alpha[i] = alpha;
  }

  commit(): void {
    this.geometry.instanceCount = this.count;
    this.mesh.visible = this.count > 0;
    if (!this.count) return;
    this.geometry.attributes.aShadow.needsUpdate = true;
    this.geometry.attributes.aAlpha.needsUpdate = true;
  }

  dispose(): void {
    this.geometry.dispose();
  }
}
