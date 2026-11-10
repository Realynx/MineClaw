/**
 * The redstone studio's own 3D layer over the circuit, added to the viewer's scene like `Overlay3D`: the power level
 * written on every powered dust cell, a box around each probe and breakpoint, and a bar across each repeater a lock holds
 * (a studio-only mark: the viewer does not draw locks). Three draw calls at most and no object per block: each kind is one
 * buffer, written again only when its data changes.
 */
import * as THREE from 'three';
import { plotMode } from '../canvas';
import type { Dir } from '../facing';
import { MAX_POWER_LABELS, type SimMarker, type SimMarks } from './RedstoneSimController';

export const MAX_MARKERS = 256;
export const MAX_LOCKS = 256;

/** The atlas: the numbers 0..15, four to a row. */
const GRID = 4;
const GLYPH_PX = 32;
/** A number's size on its cell, and how far above the cell's floor it lies (over the dust, which is nearly flat). */
const LABEL_SIZE = 0.5;
const LABEL_LIFT = 0.07;
/** How far a marker's box stands out from its cell: a breakpoint's goes around a probe's on the same cell. */
const PAD = { probe: 0.05, breakpoint: 0.11 } as const;
/** A lock's bar: its length across the repeater, its thickness, and its middle above the cell's floor. */
const BAR = { length: 0.8, thick: 0.14, lift: 0.195 } as const;

/** The twelve edges of a unit box as corner pairs. */
const EDGES: ReadonlyArray<readonly [number, number, number, number, number, number]> = [
  [0, 0, 0, 1, 0, 0],
  [0, 0, 0, 0, 1, 0],
  [0, 0, 0, 0, 0, 1],
  [1, 0, 0, 1, 1, 0],
  [1, 0, 0, 1, 0, 1],
  [0, 1, 0, 1, 1, 0],
  [0, 1, 0, 0, 1, 1],
  [0, 0, 1, 1, 0, 1],
  [0, 0, 1, 0, 1, 1],
  [1, 1, 0, 1, 1, 1],
  [1, 0, 1, 1, 1, 1],
  [0, 1, 1, 1, 1, 1],
];
const EDGE_VERTICES = EDGES.length * 2;

/** For each way the viewer looks: the text's right and its up on the ground, as `[rx, rz, ux, uz]`. */
const TEXT_AXES: Record<Dir, readonly [number, number, number, number]> = {
  north: [1, 0, 0, -1],
  east: [0, 1, 1, 0],
  south: [-1, 0, 0, 1],
  west: [0, -1, -1, 0],
};

/** World y of canvas y 0. */
const base = (): number => plotMode().baseY;

/** The numbers 0..15 drawn on one texture; null where there is no 2D canvas to draw on. */
function levelAtlas(): THREE.CanvasTexture | null {
  try {
    const canvas = document.createElement('canvas');
    canvas.width = canvas.height = GLYPH_PX * GRID;
    const g = canvas.getContext('2d');
    if (!g) return null;
    g.font = `bold ${Math.round(GLYPH_PX * 0.62)}px ui-monospace, Menlo, Consolas, monospace`;
    g.textAlign = 'center';
    g.textBaseline = 'middle';
    g.lineJoin = 'round';
    g.lineWidth = GLYPH_PX * 0.16;
    g.strokeStyle = '#1b0503';
    g.fillStyle = '#ffffff';
    for (let n = 0; n < GRID * GRID; n++) {
      const x = ((n % GRID) + 0.5) * GLYPH_PX;
      const y = (Math.floor(n / GRID) + 0.54) * GLYPH_PX;
      g.strokeText(String(n), x, y);
      g.fillText(String(n), x, y);
    }
    const texture = new THREE.CanvasTexture(canvas);
    texture.colorSpace = THREE.SRGBColorSpace;
    return texture;
  } catch {
    return null;
  }
}

const dynamic = (size: number, items: number) =>
  new THREE.BufferAttribute(new Float32Array(size * items), items).setUsage(THREE.DynamicDrawUsage);

function sameLocks(a: SimMarks['locks'], b: SimMarks['locks']): boolean {
  if (a === b) return true;
  if (a.length !== b.length) return false;
  return a.every((l, i) => l[0] === b[i]![0] && l[1] === b[i]![1] && l[2] === b[i]![2] && l[3] === b[i]![3]);
}

export class PowerOverlay {
  readonly group = new THREE.Group();
  private readonly atlas: THREE.CanvasTexture | null;
  private readonly labels: THREE.Mesh;
  private readonly markers: THREE.LineSegments;
  private readonly locks: THREE.InstancedMesh;
  private readonly levels = new Int16Array(MAX_POWER_LABELS * 4);
  private levelCount = 0;
  private facing: Dir = 'north';
  private shownMarkers: readonly SimMarker[] = [];
  private shownLocks: SimMarks['locks'] = [];
  private readonly tmp = new THREE.Object3D();
  private readonly colour = new THREE.Color();

  constructor() {
    this.atlas = levelAtlas();
    const quads = new THREE.BufferGeometry();
    quads.setAttribute('position', dynamic(MAX_POWER_LABELS * 4, 3));
    quads.setAttribute('uv', dynamic(MAX_POWER_LABELS * 4, 2));
    const index = new Uint16Array(MAX_POWER_LABELS * 6);
    for (let i = 0; i < MAX_POWER_LABELS; i++) {
      const v = i * 4;
      index.set([v, v + 1, v + 2, v, v + 2, v + 3], i * 6);
    }
    quads.setIndex(new THREE.BufferAttribute(index, 1));
    quads.setDrawRange(0, 0);
    this.labels = new THREE.Mesh(
      quads,
      new THREE.MeshBasicMaterial({
        map: this.atlas,
        transparent: true,
        alphaTest: 0.3,
        depthWrite: false,
        side: THREE.DoubleSide,
        toneMapped: false,
      }),
    );
    this.labels.name = 'power-levels';

    const boxes = new THREE.BufferGeometry();
    boxes.setAttribute('position', dynamic(MAX_MARKERS * EDGE_VERTICES, 3));
    boxes.setAttribute('color', dynamic(MAX_MARKERS * EDGE_VERTICES, 3));
    boxes.setDrawRange(0, 0);
    this.markers = new THREE.LineSegments(
      boxes,
      new THREE.LineBasicMaterial({ vertexColors: true, depthTest: false, transparent: true, toneMapped: false }),
    );
    this.markers.name = 'sim-markers';
    this.markers.renderOrder = 10;

    this.locks = new THREE.InstancedMesh(
      new THREE.BoxGeometry(1, 1, 1),
      new THREE.MeshBasicMaterial({ color: 0x2b2b2b }),
      MAX_LOCKS,
    );
    this.locks.name = 'repeater-locks';
    this.locks.count = 0;
    this.locks.instanceMatrix.setUsage(THREE.DynamicDrawUsage);

    for (const o of [this.labels, this.markers, this.locks]) {
      o.visible = false;
      o.frustumCulled = false;
    }
    this.group.add(this.labels, this.markers, this.locks);
  }

  /** Draws these marks. A list that is what is already shown costs nothing. */
  set(marks: SimMarks): void {
    this.setLevels(marks.levels, marks.levelCount);
    if (marks.markers !== this.shownMarkers) this.setMarkers(marks.markers);
    if (!sameLocks(marks.locks, this.shownLocks)) this.setLocks(marks.locks);
  }

  /** The way the viewer looks: the numbers lie on the ground, turned to read upright from there. */
  setFacing(dir: Dir): void {
    if (dir === this.facing) return;
    this.facing = dir;
    if (this.levelCount > 0) this.writeLabels();
  }

  private setLevels(levels: Int16Array, count: number): void {
    const n = Math.min(count, MAX_POWER_LABELS);
    let same = n === this.levelCount;
    for (let i = 0; same && i < n * 4; i++) same = levels[i] === this.levels[i];
    if (same) return;
    this.levels.set(levels.subarray(0, n * 4));
    this.levelCount = n;
    this.writeLabels();
  }

  /** One quad per number: its four corners on the ground and the number's square of the atlas. */
  private writeLabels(): void {
    const geometry = this.labels.geometry;
    const position = geometry.getAttribute('position') as THREE.BufferAttribute;
    const uv = geometry.getAttribute('uv') as THREE.BufferAttribute;
    const p = position.array as Float32Array;
    const t = uv.array as Float32Array;
    const [rx, rz, ux, uz] = TEXT_AXES[this.facing];
    const half = LABEL_SIZE / 2;
    const lift = base() + LABEL_LIFT;
    for (let i = 0; i < this.levelCount; i++) {
      const cx = this.levels[i * 4]! + 0.5;
      const cy = this.levels[i * 4 + 1]! + lift;
      const cz = this.levels[i * 4 + 2]! + 0.5;
      const level = this.levels[i * 4 + 3]! & 15;
      // Corners in the text's own order: bottom left, bottom right, top right, top left.
      for (let c = 0; c < 4; c++) {
        const right = c === 1 || c === 2 ? half : -half;
        const up = c >= 2 ? half : -half;
        const v = (i * 4 + c) * 3;
        p[v] = cx + rx * right + ux * up;
        p[v + 1] = cy;
        p[v + 2] = cz + rz * right + uz * up;
      }
      // The atlas has the numbers in rows from its top; a texture's v runs up from its bottom.
      const u0 = (level % GRID) / GRID;
      const u1 = u0 + 1 / GRID;
      const v1 = 1 - Math.floor(level / GRID) / GRID;
      const v0 = v1 - 1 / GRID;
      const w = i * 8;
      t[w] = u0;
      t[w + 1] = v0;
      t[w + 2] = u1;
      t[w + 3] = v0;
      t[w + 4] = u1;
      t[w + 5] = v1;
      t[w + 6] = u0;
      t[w + 7] = v1;
    }
    position.needsUpdate = true;
    uv.needsUpdate = true;
    geometry.setDrawRange(0, this.levelCount * 6);
    this.labels.visible = this.levelCount > 0 && this.atlas !== null;
  }

  private setMarkers(markers: readonly SimMarker[]): void {
    this.shownMarkers = markers;
    const geometry = this.markers.geometry;
    const position = geometry.getAttribute('position') as THREE.BufferAttribute;
    const color = geometry.getAttribute('color') as THREE.BufferAttribute;
    const p = position.array as Float32Array;
    const c = color.array as Float32Array;
    const n = Math.min(markers.length, MAX_MARKERS);
    const y0 = base();
    for (let i = 0; i < n; i++) {
      const m = markers[i]!;
      const pad = PAD[m.kind];
      this.colour.setHex(m.color);
      for (let e = 0; e < EDGES.length; e++) {
        const edge = EDGES[e]!;
        for (let end = 0; end < 2; end++) {
          const v = (i * EDGE_VERTICES + e * 2 + end) * 3;
          p[v] = m.at[0] + (edge[end * 3] ? 1 + pad : -pad);
          p[v + 1] = m.at[1] + y0 + (edge[end * 3 + 1] ? 1 + pad : -pad);
          p[v + 2] = m.at[2] + (edge[end * 3 + 2] ? 1 + pad : -pad);
          c[v] = this.colour.r;
          c[v + 1] = this.colour.g;
          c[v + 2] = this.colour.b;
        }
      }
    }
    position.needsUpdate = true;
    color.needsUpdate = true;
    geometry.setDrawRange(0, n * EDGE_VERTICES);
    this.markers.visible = n > 0;
  }

  /** A dark bar across each held repeater, at right angles to the way its signal runs. */
  private setLocks(locks: SimMarks['locks']): void {
    this.shownLocks = locks;
    const n = Math.min(locks.length, MAX_LOCKS);
    const y0 = base() + BAR.lift;
    for (let i = 0; i < n; i++) {
      const [x, y, z, meta] = locks[i]!;
      // A repeater with an even facing (meta & 1 = 0) runs north-south, so its bar lies east-west.
      const acrossX = (meta & 1) === 0;
      this.tmp.position.set(x + 0.5, y + y0, z + 0.5);
      this.tmp.scale.set(acrossX ? BAR.length : BAR.thick, BAR.thick, acrossX ? BAR.thick : BAR.length);
      this.tmp.updateMatrix();
      this.locks.setMatrixAt(i, this.tmp.matrix);
    }
    this.locks.count = n;
    this.locks.instanceMatrix.needsUpdate = true;
    this.locks.visible = n > 0;
  }

  dispose(): void {
    this.group.removeFromParent();
    for (const o of [this.labels, this.markers, this.locks]) {
      o.geometry.dispose();
      (o.material as THREE.Material).dispose();
    }
    this.atlas?.dispose();
  }
}
