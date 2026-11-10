// The crack drawn over a block being dug (vanilla's destroy stages 0 to 9). The art is the block atlas already on the GPU
// (1.12.2 `blocks/destroy_stage_N`, b1.8.1 terrain.png tiles 240 + N); without one, ten original crack masks are drawn
// procedurally once. A pool of 16 small meshes shares one material: each covers one box of the dug cell (its collision
// boxes when known, else the whole cell), with UVs cropped to that box as the game does.

import * as THREE from 'three';
import type { AssetBundle } from '../assets/bundle';
import type { PlayerCollisionBox } from '../world/playerCollisionTypes';

export const BREAK_STAGES = 10;
/** The most crack boxes drawn at once. */
export const BREAK_POOL = 16;
/** How far the crack stands off the block's faces (as the aim outline). */
const GROW = 0.002;
/** Half a texel of a 16 px cell: nearest sampling never bleeds into the next cell. */
const INSET = 0.5 / 16;

/** The crack stage shown for a dig's progress 0..1: nothing below 10 %, then 0 to 9 (vanilla). */
export function breakStage(progress: number): number {
  if (!Number.isFinite(progress)) return -1;
  return Math.max(-1, Math.min(BREAK_STAGES - 1, Math.floor(progress * 10) - 1));
}

/** Where the ten stages sit in an atlas grid: [column, row] per stage. */
export interface BreakCells {
  columns: number;
  rows: number;
  cells: Array<[number, number]>;
}

/** The destroy-stage cells of a bundle's block atlas; null for the placeholder bundle or a pack without all ten. */
export function breakCells(bundle: AssetBundle | null): BreakCells | null {
  if (!bundle || bundle.fallback) return null;
  if (bundle.pack.version === 'b1.8.1') {
    return { columns: 16, rows: 16, cells: Array.from({ length: BREAK_STAGES }, (_, n): [number, number] => [n, 15]) };
  }
  const blocks = bundle.pack.blocks;
  const cells: Array<[number, number]> = [];
  for (let n = 0; n < BREAK_STAGES; n++) {
    const c = blocks.tiles[`blocks/destroy_stage_${n}`];
    if (!c) return null;
    cells.push([c[0], c[1]]);
  }
  return { columns: blocks.columns, rows: blocks.rows, cells };
}

const MASK = 16;

/** Ten original crack masks side by side (160 × 16 RGBA): dark cracks growing from the middle, clear elsewhere. */
export function crackMasks(): Uint8Array {
  const first = new Int8Array(MASK * MASK).fill(BREAK_STAGES);
  let seed = 0x5eed;
  const rnd = () => ((seed = (Math.imul(seed, 1103515245) + 12345) | 0) >>> 0) / 4294967296;
  const branches = 7;
  for (let b = 0; b < branches; b++) {
    let a = (b / branches) * Math.PI * 2 + (rnd() - 0.5) * 0.6;
    let x = 7.5,
      y = 7.5;
    for (let s = 0; s < 14; s++) {
      a += (rnd() - 0.5) * 0.9;
      x += Math.cos(a);
      y += Math.sin(a);
      const px = Math.round(x),
        py = Math.round(y);
      if (px < 0 || py < 0 || px >= MASK || py >= MASK) break;
      const i = py * MASK + px;
      first[i] = Math.min(first[i], Math.min(BREAK_STAGES - 1, Math.floor(s / 1.5)));
    }
  }
  const width = MASK * BREAK_STAGES;
  const out = new Uint8Array(width * MASK * 4);
  for (let n = 0; n < BREAK_STAGES; n++)
    for (let py = 0; py < MASK; py++)
      for (let px = 0; px < MASK; px++) {
        if (first[py * MASK + px] > n) continue;
        const o = (py * width + n * MASK + px) * 4;
        // Under the crack blend (2 × texel × screen) 60 darkens the block to about half.
        out[o] = out[o + 1] = out[o + 2] = 60;
        out[o + 3] = 255;
      }
  return out;
}

// Writes the texel unchanged: the renderer's sRGB output step must not touch it, or the multiply blend would brighten the
// block instead of cracking it (the atlas is NoColorSpace and every world material writes raw values).
const VERTEX = `varying vec2 vUv;
void main() {
  vUv = uv;
  gl_Position = projectionMatrix * modelViewMatrix * vec4(position, 1.0);
}`;
const FRAGMENT = `uniform sampler2D uMap;
varying vec2 vUv;
void main() {
  vec4 t = texture2D(uMap, vUv);
  if (t.a < 0.1) discard;
  gl_FragColor = t;
}`;

/** One crack: a cell of a dimension at a stage 0..9, optionally its boxes (world coordinates) and the view it is for. */
export interface BreakEntry {
  dim: number;
  x: number;
  y: number;
  z: number;
  stage: number;
  boxes?: readonly PlayerCollisionBox[] | null;
  /** Only this view shows it; absent = every view of the dimension. */
  view?: number;
}

/** The atlas the sections sample (render/blockAtlas.ts), or null. */
export interface BreakAtlas {
  readonly texture: THREE.Texture;
  readonly bundle: AssetBundle;
}

/** Each face: its axis, its side, and the two in-plane axes (u × v points out of the face). */
const FACES: ReadonlyArray<readonly [number, number, number, number]> = [
  [0, 1, 1, 2],
  [0, -1, 2, 1],
  [1, 1, 2, 0],
  [1, -1, 0, 2],
  [2, 1, 0, 1],
  [2, -1, 1, 0],
];
const CORNERS: ReadonlyArray<readonly [number, number]> = [
  [0, 0],
  [1, 0],
  [1, 1],
  [0, 1],
];
const SIG = 10;
const UNIT: readonly number[] = [-GROW, -GROW, -GROW, 1 + GROW, 1 + GROW, 1 + GROW];

function quadIndex(): THREE.BufferAttribute {
  const idx = new Uint16Array(36);
  for (let f = 0; f < 6; f++) idx.set([f * 4, f * 4 + 1, f * 4 + 2, f * 4, f * 4 + 2, f * 4 + 3], f * 6);
  return new THREE.BufferAttribute(idx, 1);
}

export class BreakOverlay {
  readonly group = new THREE.Group();
  readonly material: THREE.ShaderMaterial;
  private readonly meshes: THREE.Mesh[] = [];
  /** Per mesh what its geometry holds now: cell x, y, z, the box (cell-local min and max) and the stage. */
  private readonly sigs: number[][] = [];
  private readonly local = new Map<string, BreakEntry>();
  private world: readonly BreakEntry[] = [];
  private snapshot: readonly BreakEntry[] | null = null;
  private list: BreakEntry[] = [];
  private cells: BreakCells;
  private masks: THREE.DataTexture | null = null;
  private atlas: BreakAtlas | null = null;
  private readonly lo = [0, 0, 0];
  private readonly hi = [0, 0, 0];

  constructor() {
    this.group.name = 'breaks';
    this.cells = this.maskCells();
    this.material = new THREE.ShaderMaterial({
      uniforms: { uMap: { value: this.maskTexture() } },
      vertexShader: VERTEX,
      fragmentShader: FRAGMENT,
      transparent: true,
      depthWrite: false,
      polygonOffset: true,
      polygonOffsetFactor: -1,
      polygonOffsetUnits: -4,
      blending: THREE.CustomBlending,
      blendEquation: THREE.AddEquation,
      blendSrc: THREE.DstColorFactor,
      blendDst: THREE.SrcColorFactor,
      blendSrcAlpha: THREE.ZeroFactor,
      blendDstAlpha: THREE.OneFactor,
    });
    const index = quadIndex();
    const sphere = new THREE.Sphere(new THREE.Vector3(0.5, 0.5, 0.5), 0.9);
    for (let i = 0; i < BREAK_POOL; i++) {
      const g = new THREE.BufferGeometry();
      g.setAttribute('position', new THREE.BufferAttribute(new Float32Array(24 * 3), 3));
      g.setAttribute('uv', new THREE.BufferAttribute(new Float32Array(24 * 2), 2));
      g.setIndex(index);
      g.boundingSphere = sphere;
      const mesh = new THREE.Mesh(g, this.material);
      mesh.visible = false;
      mesh.renderOrder = 4;
      mesh.matrixAutoUpdate = false;
      this.meshes.push(mesh);
      this.sigs.push(new Array<number>(SIG).fill(NaN));
      this.group.add(mesh);
    }
  }

  /** The texture the cracks sample now (the block atlas, or the procedural masks). */
  get texture(): THREE.Texture {
    return this.material.uniforms.uMap.value as THREE.Texture;
  }

  /** Meshes drawn by the last `showFor` (tests, stats). */
  get shown(): number {
    let n = 0;
    for (const m of this.meshes) if (m.visible) n++;
    return n;
  }

  /** The pool (tests). */
  get pool(): readonly THREE.Mesh[] {
    return this.meshes;
  }

  /** Samples the block atlas when it has the ten destroy stages, else the procedural masks. */
  setAtlas(atlas: BreakAtlas | null): void {
    if (atlas === this.atlas) return;
    this.atlas = atlas;
    const cells = atlas ? breakCells(atlas.bundle) : null;
    this.cells = cells ?? this.maskCells();
    this.material.uniforms.uMap.value = cells ? atlas!.texture : this.maskTexture();
    for (const s of this.sigs) s.fill(NaN); // the UVs follow the grid
  }

  /** A crack of this runtime's own (a view's dig); null removes it. */
  set(key: string, entry: BreakEntry | null): void {
    if (entry && entry.stage >= 0 && entry.stage < BREAK_STAGES) this.local.set(key, entry);
    else if (!this.local.delete(key)) return;
    this.rebuild();
  }

  /** The world-wide list of cracks (every pawn's digs); entries absent from it are gone. */
  reconcile(snapshot: readonly BreakEntry[] | null): void {
    if (snapshot === this.snapshot) return;
    this.snapshot = snapshot;
    this.world = snapshot ?? [];
    this.rebuild();
  }

  private rebuild(): void {
    this.list = [...this.local.values(), ...this.world.filter((e) => e.stage >= 0 && e.stage < BREAK_STAGES)];
  }

  /** Before rendering a view of `dim`: its cracks into the pool (geometry is rewritten only when a crack changed); the
   * number of boxes shown. */
  showFor(dim: number, view = 0): number {
    let used = 0;
    for (const e of this.list) {
      if (used >= BREAK_POOL) break;
      if (e.dim !== dim || (e.view !== undefined && e.view !== view)) continue;
      used = this.place(e, used);
    }
    for (let i = used; i < BREAK_POOL; i++) this.meshes[i].visible = false;
    this.group.visible = used > 0;
    return used;
  }

  /** One crack's boxes from pool slot `used` on; the next free slot. */
  private place(e: BreakEntry, used: number): number {
    let any = false;
    const boxes = e.boxes;
    for (let i = 0; boxes && i < boxes.length; i++) {
      if (used >= BREAK_POOL) return used;
      if (!this.clip(boxes[i], e)) continue;
      this.draw(this.meshes[used], this.sigs[used], e);
      used++;
      any = true;
    }
    if (!any && used < BREAK_POOL) {
      for (let i = 0; i < 3; i++) {
        this.lo[i] = UNIT[i];
        this.hi[i] = UNIT[i + 3];
      }
      this.draw(this.meshes[used], this.sigs[used], e);
      used++;
    }
    return used;
  }

  /** A box cut to its cell, in cell coordinates and grown off the faces, into lo / hi; false when nothing is left. */
  private clip(b: PlayerCollisionBox, e: BreakEntry): boolean {
    return (
      this.span(0, b.minX - e.x, b.maxX - e.x) &&
      this.span(1, b.minY - e.y, b.maxY - e.y) &&
      this.span(2, b.minZ - e.z, b.maxZ - e.z)
    );
  }

  private span(axis: number, min: number, max: number): boolean {
    const lo = Math.max(0, min),
      hi = Math.min(1, max);
    if (!(hi > lo)) return false;
    this.lo[axis] = lo - GROW;
    this.hi[axis] = hi + GROW;
    return true;
  }

  /** Records what a slot will hold; false when it holds that already. */
  private changed(sig: number[], e: BreakEntry): boolean {
    const lo = this.lo,
      hi = this.hi;
    if (
      sig[0] === e.x &&
      sig[1] === e.y &&
      sig[2] === e.z &&
      sig[3] === lo[0] &&
      sig[4] === lo[1] &&
      sig[5] === lo[2] &&
      sig[6] === hi[0] &&
      sig[7] === hi[1] &&
      sig[8] === hi[2] &&
      sig[9] === e.stage
    )
      return false;
    sig[0] = e.x;
    sig[1] = e.y;
    sig[2] = e.z;
    for (let i = 0; i < 3; i++) {
      sig[3 + i] = lo[i];
      sig[6 + i] = hi[i];
    }
    sig[9] = e.stage;
    return true;
  }

  private draw(mesh: THREE.Mesh, sig: number[], e: BreakEntry): void {
    mesh.visible = true;
    if (!this.changed(sig, e)) return;
    mesh.position.set(e.x, e.y, e.z);
    mesh.updateMatrix();
    this.writeBox(mesh.geometry, e.stage);
  }

  /** The box's 24 corners (cell coordinates) and their UVs: the stage's cell, cropped to where the face lies in the cell. */
  private writeBox(g: THREE.BufferGeometry, stage: number): void {
    const pos = g.getAttribute('position') as THREE.BufferAttribute,
      uv = g.getAttribute('uv') as THREE.BufferAttribute;
    const p = pos.array as Float32Array,
      t = uv.array as Float32Array;
    const { columns, rows, cells } = this.cells;
    const [col, row] = cells[stage];
    const c = [0, 0, 0];
    let v = 0;
    for (const [axis, side, ua, va] of FACES) {
      for (const [cu, cv] of CORNERS) {
        c[axis] = side > 0 ? this.hi[axis] : this.lo[axis];
        c[ua] = cu ? this.hi[ua] : this.lo[ua];
        c[va] = cv ? this.hi[va] : this.lo[va];
        p.set(c, v * 3);
        const lu = axis === 0 ? c[2] : c[0],
          lv = axis === 1 ? c[2] : 1 - c[1];
        t[v * 2] = (col + Math.min(1 - INSET, Math.max(INSET, lu))) / columns;
        t[v * 2 + 1] = (row + Math.min(1 - INSET, Math.max(INSET, lv))) / rows;
        v++;
      }
    }
    pos.needsUpdate = true;
    uv.needsUpdate = true;
  }

  private maskCells(): BreakCells {
    return {
      columns: BREAK_STAGES,
      rows: 1,
      cells: Array.from({ length: BREAK_STAGES }, (_, n): [number, number] => [n, 0]),
    };
  }

  /** The procedural masks, made on first use. */
  private maskTexture(): THREE.DataTexture {
    if (!this.masks) {
      const tex = new THREE.DataTexture(
        crackMasks(),
        MASK * BREAK_STAGES,
        MASK,
        THREE.RGBAFormat,
        THREE.UnsignedByteType,
      );
      tex.magFilter = THREE.NearestFilter;
      tex.minFilter = THREE.NearestFilter;
      tex.generateMipmaps = false;
      tex.flipY = false;
      tex.colorSpace = THREE.NoColorSpace;
      tex.needsUpdate = true;
      this.masks = tex;
    }
    return this.masks;
  }

  dispose(): void {
    this.local.clear();
    this.world = [];
    this.list = [];
    for (const m of this.meshes) m.geometry.dispose();
    this.material.dispose();
    this.masks?.dispose();
  }
}
