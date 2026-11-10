/**
 * The Design Studio's own 3D layer, added to the (private) world runtime's scene: the plot border and grid, the selection box,
 * the drag / brush preview, the move gizmo, the anchor marker and instanced ghost cubes (the paste ghost, and the brief flash of
 * the AI's ops as they land). Cheap on software WebGL: a handful of line objects and two instanced meshes, rebuilt only when
 * their data changes; nothing here touches the section meshes. Mine excavation and discoverable veins use separate exterior
 * wireframes drawn through terrain, with reusable buffers and no per-block objects.
 */
import * as THREE from 'three';
import { plotMode, type Cell, type V3 } from './canvas';
import { MINE_PREVIEW_COLORS, previewOutline, volumeOutline, type MinePreview } from './mine/minePreview';

const MAX_GHOSTS = 4096;
/** X-ray cubes at most (the rest of a crowded window is left out). */
export const MAX_XRAY = 6000;
/** A mine piece's outline where it cannot be placed or moved to. */
export const REFUSED_COLOR = 0xef4444;
/** The openings other pieces can join while one is placed or moved, and the ones it joins there. */
export const PORT_COLOR = 0xfbbf24;
export const JOINED_PORT_COLOR = 0x22c55e;

/** A square on the floor of each cell (canvas coordinates), `inset` in from its edges: 4 segments per cell. */
export function portMarks(cells: readonly V3[], inset = 0.2): Float32Array {
  const out = new Float32Array(cells.length * 24);
  let i = 0;
  for (const [x, y, z] of cells) {
    const a = x + inset,
      b = x + 1 - inset,
      c = z + inset,
      d = z + 1 - inset,
      h = y + 0.03;
    for (const v of [a, h, c, b, h, c, b, h, c, b, h, d, b, h, d, a, h, d, a, h, d, a, h, c]) out[i++] = v;
  }
  return out;
}

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

function mineLines(name: string, color: number, order: number): THREE.LineSegments {
  const lines = new THREE.LineSegments(
    new THREE.BufferGeometry(),
    new THREE.LineBasicMaterial({
      color,
      depthTest: false,
      depthWrite: false,
      toneMapped: false,
    }),
  );
  lines.name = name;
  lines.renderOrder = order;
  lines.visible = false;
  lines.frustumCulled = false;
  return lines;
}

/** Reuse GPU capacity across edits; when it grows, dispose the old geometry and its buffer together. */
function setMineLines(lines: THREE.LineSegments, cells: V3[] | null): void {
  const positions = cells ? previewOutline(cells) : new Float32Array();
  setMinePositions(lines, positions);
}

function setMinePositions(lines: THREE.LineSegments, positions: Float32Array): void {
  lines.visible = positions.length > 0;
  lines.position.set(0, base(), 0);
  if (positions.length === 0) {
    lines.geometry.setDrawRange(0, 0);
    return;
  }
  let attribute = lines.geometry.getAttribute('position') as THREE.BufferAttribute | undefined;
  if (!attribute || attribute.array.length < positions.length) {
    lines.geometry.dispose();
    lines.geometry = new THREE.BufferGeometry();
    const capacity = 3 * 2 ** Math.ceil(Math.log2(positions.length / 3));
    attribute = new THREE.BufferAttribute(new Float32Array(capacity), 3).setUsage(THREE.DynamicDrawUsage);
    lines.geometry.setAttribute('position', attribute);
  }
  (attribute.array as Float32Array).set(positions);
  attribute.needsUpdate = true;
  lines.geometry.setDrawRange(0, positions.length / 3);
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
  private readonly planned = mineLines('mine-planned', MINE_PREVIEW_COLORS.planned, 12);
  private readonly veins = mineLines('mine-veins', MINE_PREVIEW_COLORS.veins, 13);
  private readonly placement = mineLines('mine-placement', MINE_PREVIEW_COLORS.placement, 14);
  private readonly ports = mineLines('mine-ports', PORT_COLOR, 15);
  private readonly joinedPorts = mineLines('mine-ports-joined', JOINED_PORT_COLOR, 16);
  private placementPositions: Float32Array | null = null;
  private flashUntil = 0;
  private flashStart = 0;
  readonly gizmo = new THREE.Group();
  private readonly handles: THREE.Mesh[] = [];
  private readonly paddedHandles: THREE.Mesh[] = [];
  private screenSizedGizmo = false;
  private readonly gizmoDepth = new THREE.Vector3();
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
      new THREE.MeshBasicMaterial({
        color: 0xffffff,
        transparent: true,
        opacity: 0.85,
        depthTest: false,
        depthWrite: false,
      }),
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
      // Only cursor-first editors use these forgiving targets. They never draw, and do not enlarge the visible arrows.
      const pickMaterial = new THREE.MeshBasicMaterial({ visible: false });
      const pickShaft = new THREE.Mesh(new THREE.CylinderGeometry(0.16, 0.16, 1.5, 8), pickMaterial);
      const pickHead = new THREE.Mesh(new THREE.ConeGeometry(0.28, 0.7, 12), pickMaterial);
      pickShaft.position.y = 0.85;
      pickHead.position.y = 1.85;
      for (const target of [pickShaft, pickHead]) {
        target.userData.axis = axis;
        this.paddedHandles.push(target);
        g.add(target);
      }
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
      this.planned,
      this.veins,
      this.placement,
      this.ports,
      this.joinedPorts,
      this.gizmo,
    );
  }

  /** Keep editor handles, selection, grid and temporary ghosts out of a saved camera picture. */
  async withoutGuides<T>(capture: () => Promise<T>, keepMine = false): Promise<T> {
    const children = this.group.children.map((child) => ({ child, visible: child.visible }));
    for (const { child } of children) if (!keepMine || (child !== this.planned && child !== this.veins)) child.visible = false;
    try {
      return await capture();
    } finally {
      for (const { child, visible } of children) child.visible = visible;
    }
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

  /** Cursor editors keep an axis about 80 px long at its origin, with a padded hit target. Builder arrows stay unchanged. */
  updateGizmoScale(camera: THREE.PerspectiveCamera, viewHeightPx: number): void {
    this.screenSizedGizmo = true;
    if (!this.gizmo.visible || viewHeightPx <= 0) return;
    this.gizmo.getWorldPosition(this.gizmoDepth).applyMatrix4(camera.matrixWorldInverse);
    const depth = Math.max(camera.near, -this.gizmoDepth.z);
    const perPixel = 2 * depth * Math.tan(THREE.MathUtils.degToRad(camera.getEffectiveFOV()) / 2) / viewHeightPx;
    this.gizmo.scale.setScalar(Math.max(0.02, Math.min(128, perPixel * 80 / 2.1)));
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

  /** Authored excavation and discoverable veins, visible through terrain (call only when the canvas changes). */
  setMinePreview(preview: MinePreview | null): void {
    setMineLines(this.planned, preview?.planned ?? null);
    setMineLines(this.veins, preview?.veins ?? null);
  }

  /** A violet module footprint at its proposed placement, distinct from committed cyan digs and amber veins. */
  setMinePlacement(cells: Iterable<readonly [number, number, number]> | null): void {
    this.placementPositions = null;
    this.placementTone(false);
    setMinePositions(this.placement, cells ? volumeOutline(cells) : new Float32Array());
    // The openings belong to the outline in view: they go with it.
    if (!cells) this.setMinePorts(null);
  }

  /** Amber squares where another piece can be joined, green where the outline in view joins one (null hides both). */
  setMinePorts(open: readonly V3[] | null, joined: readonly V3[] = []): void {
    setMinePositions(this.ports, open?.length ? portMarks(open) : new Float32Array());
    setMinePositions(this.joinedPorts, open && joined.length ? portMarks(joined, 0.05) : new Float32Array());
  }

  /** Openings marked now (amber, then green), for tests and the e2e. */
  get portCounts(): [number, number] {
    const count = (l: THREE.LineSegments) => (l.visible ? l.geometry.drawRange.count / 8 : 0);
    return [count(this.ports), count(this.joinedPorts)];
  }

  /** Violet, or red where the piece cannot stand (`refused`). */
  private placementTone(refused: boolean): void {
    (this.placement.material as THREE.LineBasicMaterial).color.setHex(
      refused ? REFUSED_COLOR : MINE_PREVIEW_COLORS.placement,
    );
  }

  /** A prepared local outline uploads once; moving it updates only the object's translation. `refused` draws it red. */
  setMinePlacementGeometry(positions: Float32Array | null, at: V3 = [0, 0, 0], refused = false): void {
    this.placementTone(refused);
    if (!positions) { this.placement.visible = false; return; }
    if (positions !== this.placementPositions) {
      setMinePositions(this.placement, positions);
      this.placementPositions = positions;
    }
    this.placement.position.set(at[0], at[1] + base(), at[2]);
    this.placement.visible = positions.length > 0;
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
    if (!this.group.visible || !this.gizmo.visible) return null;
    this.gizmo.updateWorldMatrix(true, true);
    const hit = ray.intersectObjects(this.screenSizedGizmo ? this.paddedHandles : this.handles, false)[0];
    return (hit?.object.userData.axis as Axis | undefined) ?? null;
  }

  dispose(): void {
    this.group.removeFromParent();
    const geometries = new Set<THREE.BufferGeometry>();
    const materials = new Set<THREE.Material>();
    this.group.traverse((o) => {
      const m = o as THREE.Mesh;
      if (m.geometry) geometries.add(m.geometry);
      const mat = m.material as THREE.Material | THREE.Material[] | undefined;
      if (Array.isArray(mat)) mat.forEach((x) => materials.add(x));
      else if (mat) materials.add(mat);
    });
    geometries.forEach((g) => g.dispose());
    materials.forEach((m) => m.dispose());
  }
}
