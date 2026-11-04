/**
 * The Design Studio's own 3D layer, added to the (private) world runtime's scene: the plot border and grid, the selection box,
 * the drag / brush preview, the move gizmo, the anchor marker and instanced ghost cubes (the paste ghost, and the brief flash of
 * the AI's ops as they land). Cheap on software WebGL: a handful of line objects and two instanced meshes, rebuilt only when
 * their data changes; nothing here touches the section meshes. A mine plot adds the X-ray layer: its terrain's ores as
 * coloured cubes drawn through the stone (one instanced mesh, depth test off).
 */
import * as THREE from 'three';
import { plotMode, type Cell, type V3 } from './canvas';

const MAX_GHOSTS = 4096;
/** X-ray cubes at most (the rest of a crowded window is left out). */
export const MAX_XRAY = 6000;

/** World y of canvas y 0 in the plot's mode (64 on a build plot, 0 on a mine). */
const base = (): number => plotMode().baseY;

function boxLines(color: number, opacity = 1): THREE.LineSegments {
  const g = new THREE.EdgesGeometry(new THREE.BoxGeometry(1, 1, 1));
  const m = new THREE.LineBasicMaterial({ color, transparent: opacity < 1, opacity, depthTest: false });
  const l = new THREE.LineSegments(g, m);
  l.renderOrder = 10;
  l.visible = false;
  l.frustumCulled = false;
  return l;
}

function placeBox(o: THREE.Object3D, min: V3, max: V3, pad = 0.02): void {
  o.position.set((min[0] + max[0] + 1) / 2, (min[1] + max[1] + 1) / 2 + base(), (min[2] + max[2] + 1) / 2);
  o.scale.set(max[0] - min[0] + 1 + pad * 2, max[1] - min[1] + 1 + pad * 2, max[2] - min[2] + 1 + pad * 2);
  o.visible = true;
}

export type Axis = 'x' | 'y' | 'z';

export class Overlay3D {
  readonly group = new THREE.Group();
  private readonly selection = boxLines(0x38bdf8);
  private readonly drag = boxLines(0xfacc15);
  private readonly brush = boxLines(0xffffff, 0.8);
  private readonly anchor = boxLines(0xf472b6);
  private readonly ghosts: THREE.InstancedMesh;
  private readonly flash: THREE.InstancedMesh;
  private readonly xray: THREE.InstancedMesh;
  private flashUntil = 0;
  private flashStart = 0;
  readonly gizmo = new THREE.Group();
  private readonly handles: THREE.Mesh[] = [];
  private readonly tmp = new THREE.Object3D();
  private readonly colour = new THREE.Color();

  constructor(plot: number, gridY?: number) {
    const cube = new THREE.BoxGeometry(1.02, 1.02, 1.02);
    this.ghosts = new THREE.InstancedMesh(
      cube,
      new THREE.MeshBasicMaterial({ color: 0xffffff, transparent: true, opacity: 0.38, depthWrite: false }),
      MAX_GHOSTS,
    );
    this.flash = new THREE.InstancedMesh(
      cube,
      new THREE.MeshBasicMaterial({ color: 0x5ee08a, transparent: true, opacity: 0.55, depthWrite: false }),
      MAX_GHOSTS,
    );
    this.xray = new THREE.InstancedMesh(
      new THREE.BoxGeometry(0.7, 0.7, 0.7),
      new THREE.MeshBasicMaterial({ color: 0xffffff, transparent: true, opacity: 0.85, depthTest: false, depthWrite: false }),
      MAX_XRAY,
    );
    this.xray.name = 'xray';
    for (const m of [this.ghosts, this.flash, this.xray]) {
      m.count = 0;
      m.frustumCulled = false;
      m.renderOrder = 9;
      m.instanceMatrix.setUsage(THREE.DynamicDrawUsage);
    }
    // The plot: its border at grass level and a faint 1-block grid (one LineSegments).
    const pts: number[] = [];
    // A build's grid lies on its grass; a mine's over its highest ground (`gridY`, canvas y).
    const y = (gridY ?? 0) + base() + 0.01;
    for (let i = 0; i <= plot; i += 1) {
      pts.push(i, y, 0, i, y, plot, 0, y, i, plot, y, i);
    }
    const grid = new THREE.LineSegments(
      new THREE.BufferGeometry().setAttribute('position', new THREE.Float32BufferAttribute(pts, 3)),
      new THREE.LineBasicMaterial({ color: 0x000000, transparent: true, opacity: 0.12 }),
    );
    grid.name = 'grid';
    const border = new THREE.LineLoop(
      new THREE.BufferGeometry().setAttribute(
        'position',
        new THREE.Float32BufferAttribute([0, y, 0, plot, y, 0, plot, y, plot, 0, y, plot], 3),
      ),
      new THREE.LineBasicMaterial({ color: 0xffffff, transparent: true, opacity: 0.6 }),
    );
    // The move gizmo: three arrows (x red, y green, z blue) from the selection's centre.
    const arrow = (axis: Axis, color: number) => {
      const mat = new THREE.MeshBasicMaterial({ color, depthTest: false });
      const shaft = new THREE.Mesh(new THREE.CylinderGeometry(0.06, 0.06, 1.6, 8), mat);
      const head = new THREE.Mesh(new THREE.ConeGeometry(0.2, 0.5, 12), mat);
      shaft.position.y = 0.8;
      head.position.y = 1.85;
      const g = new THREE.Group();
      g.add(shaft, head);
      if (axis === 'x') g.rotation.z = -Math.PI / 2;
      if (axis === 'z') g.rotation.x = Math.PI / 2;
      for (const m of [shaft, head]) {
        m.userData.axis = axis;
        m.renderOrder = 11;
        this.handles.push(m);
      }
      return g;
    };
    this.gizmo.add(arrow('x', 0xef4444), arrow('y', 0x22c55e), arrow('z', 0x3b82f6));
    this.gizmo.visible = false;
    this.group.add(
      grid,
      border,
      this.selection,
      this.drag,
      this.brush,
      this.anchor,
      this.ghosts,
      this.flash,
      this.xray,
      this.gizmo,
    );
  }

  setGrid(on: boolean): void {
    const g = this.group.getObjectByName('grid');
    if (g) g.visible = on;
  }

  setSelection(s: { min: V3; max: V3 } | null): void {
    if (!s) {
      this.selection.visible = false;
      this.gizmo.visible = false;
      return;
    }
    placeBox(this.selection, s.min, s.max, 0.03);
    this.gizmo.position.set((s.min[0] + s.max[0] + 1) / 2, s.max[1] + 1 + base() + 0.2, (s.min[2] + s.max[2] + 1) / 2);
    this.gizmo.visible = true;
  }

  setDrag(b: { min: V3; max: V3 } | null): void {
    if (b) placeBox(this.drag, b.min, b.max, 0.05);
    else this.drag.visible = false;
  }

  setBrush(b: { min: V3; max: V3 } | null): void {
    if (b) placeBox(this.brush, b.min, b.max, 0.04);
    else this.brush.visible = false;
  }

  setAnchor(p: V3 | null): void {
    if (p) placeBox(this.anchor, p, p, 0.12);
    else this.anchor.visible = false;
  }

  private fill(mesh: THREE.InstancedMesh, cells: readonly Cell[] | readonly V3[], tint?: (i: number) => number): void {
    const n = Math.min(cells.length, MAX_GHOSTS);
    for (let i = 0; i < n; i++) {
      const c = cells[i]!;
      this.tmp.position.set(c[0] + 0.5, c[1] + base() + 0.5, c[2] + 0.5);
      this.tmp.updateMatrix();
      mesh.setMatrixAt(i, this.tmp.matrix);
      if (tint) mesh.setColorAt(i, this.colour.setHex(tint(i)));
    }
    mesh.count = n;
    mesh.instanceMatrix.needsUpdate = true;
    if (mesh.instanceColor) mesh.instanceColor.needsUpdate = true;
  }

  /** The X-ray layer: ore cells (canvas coordinates) with their colours; null or empty hides it. */
  setXray(cells: ReadonlyArray<readonly [number, number, number, number]> | null): void {
    const list = cells ?? [];
    const n = Math.min(list.length, MAX_XRAY);
    for (let i = 0; i < n; i++) {
      const c = list[i]!;
      this.tmp.position.set(c[0] + 0.5, c[1] + base() + 0.5, c[2] + 0.5);
      this.tmp.updateMatrix();
      this.xray.setMatrixAt(i, this.tmp.matrix);
      this.xray.setColorAt(i, this.colour.setHex(c[3]));
    }
    this.xray.count = n;
    this.xray.instanceMatrix.needsUpdate = true;
    if (this.xray.instanceColor) this.xray.instanceColor.needsUpdate = true;
  }

  /** X-ray cubes shown now. */
  get xrayCount(): number {
    return this.xray.count;
  }

  /** The paste / placement ghost (null hides it). */
  setGhost(cells: readonly Cell[] | null, colour?: (c: Cell) => number): void {
    if (!cells || cells.length === 0) {
      this.ghosts.count = 0;
      return;
    }
    const solid = cells.filter((c) => c[3] !== 0);
    this.fill(this.ghosts, solid, colour ? (i) => colour(solid[i]!) : undefined);
  }

  /** The AI's cells flash green and fade over `ms`. */
  flashCells(cells: readonly Cell[], now: number, ms = 1400): void {
    const shown = cells.filter((c) => c[3] !== 0);
    const list = shown.length ? shown : cells;
    this.fill(this.flash, list);
    (this.flash.material as THREE.MeshBasicMaterial).color.setHex(shown.length ? 0x5ee08a : 0xef4444);
    this.flashStart = now;
    this.flashUntil = now + ms;
  }

  get flashing(): boolean {
    return this.flash.count > 0;
  }

  /** Per frame: fades the flash out. */
  tick(now: number): void {
    if (this.flash.count === 0) return;
    const m = this.flash.material as THREE.MeshBasicMaterial;
    if (now >= this.flashUntil) {
      this.flash.count = 0;
      return;
    }
    m.opacity = 0.6 * (1 - (now - this.flashStart) / (this.flashUntil - this.flashStart));
  }

  /** The gizmo axis under a ray (unlocked mouse), or null. */
  pickHandle(ray: THREE.Raycaster): Axis | null {
    if (!this.gizmo.visible) return null;
    const hit = ray.intersectObjects(this.handles, false)[0];
    return (hit?.object.userData.axis as Axis | undefined) ?? null;
  }

  dispose(): void {
    this.group.removeFromParent();
    this.group.traverse((o) => {
      const m = o as THREE.Mesh;
      m.geometry?.dispose();
      const mat = m.material as THREE.Material | THREE.Material[] | undefined;
      if (Array.isArray(mat)) mat.forEach((x) => x.dispose());
      else mat?.dispose();
    });
  }
}
