// [holograms] Queued (not yet built) build cells drawn in the world as translucent "holograms" of the block they will become:
// the block's own model and texture (its variant and facing), recoloured in a calm tint by state, ~40 % opaque, with faint
// static scanlines and a soft glow along the block edges; cells to dig are red outlines just outside the cell with a faint red
// fill over their top (above the ground they are in). A design being placed gets the same look in green where it fits and red
// where a block is in the way; the cells it digs first show as dig cells and are no obstacle to what goes into them.
//
// Cost: the cells are meshed in the mesh worker (mesh/holo.ts), one merged mesh per state, so a whole queue is a handful of
// draw calls; they are rebuilt only when the caller's revision moves or a block change lands on one of their cells (a pawn
// placed it: it disappears), never per frame. A big queue (a strip mine of tens of thousands of cells) is meshed per region
// of `HOLO_REGION` blocks, and only the regions whose cells changed are meshed again. At most `HOLO_CAP` cells are drawn;
// past it, and when the view is zoomed out so far that a block is a few pixels, each build is one outline box instead. Drawn
// after the opaque world with the depth test on and depth writes off, so they sit in the world (hidden behind hills, visible through each other). The level cut
// (Story Mode's storeys) hides cells above it and dims the ones below, like the blocks.

import * as THREE from 'three';
import type { HoloRequest, HoloResult } from '../mesh/holo';
import type { MeshHost } from '../mesh/host';
import type { ChunkUniforms } from './chunkMaterial';
import {
  BOX_RGB,
  DIG_RGB,
  HOLO_LOD_PX,
  HOLO_REGION_MIN,
  HOLO_STATES,
  HOLO_STYLES,
  holoRegionId,
  planHolograms,
  planRegions,
  type HoloCell,
  type HoloPlan,
  type HoloRegionPlan,
  type HoloState,
  type HoloStyle,
} from './holoPlan';
export * from './holoPlan';

// ------------------------------------------------------------------------------------------------------------ shaders

const blockVertex = /* glsl */ `
attribute vec4 aTile;
attribute vec4 aColor;
attribute vec3 aCellW;
varying vec2 vUv;
flat varying vec4 vTile;
varying vec4 vColor;
varying vec3 vWorld;
flat varying vec3 vCell;
void main() {
  vec4 w = modelMatrix * vec4(position, 1.0);
  vWorld = w.xyz;
  vCell = (modelMatrix * vec4(aCellW, 1.0)).xyz;
  vUv = uv;
  vTile = aTile;
  vColor = aColor;
  gl_Position = projectionMatrix * viewMatrix * w;
}
`;

const blockFragment = /* glsl */ `
uniform sampler2D uAtlas;
uniform vec2 uGrid;
uniform float uInset;
uniform float uTextured;
uniform vec4 uRoof;
uniform float uDimBelow;
uniform vec3 uFogColor;
uniform vec3 uFogCentre;
uniform vec2 uFogRange;
uniform vec3 uTint;
uniform float uOpacity;
uniform float uSat;
uniform float uStripes;
uniform float uBoost;
varying vec2 vUv;
flat varying vec4 vTile;
varying vec4 vColor;
varying vec3 vWorld;
flat varying vec3 vCell;

bool flag(float f, float bit) { return mod(floor(f / bit), 2.0) >= 1.0; }

vec4 texel() {
  float tf = vTile.z;
  if (uTextured < 0.5 || !flag(tf, 1.0)) return vec4(1.0);
  vec2 span = vTile.w > 0.5 ? vec2(floor(vTile.w / 256.0), mod(vTile.w, 256.0)) : vec2(1.0);
  vec2 luv = flag(tf, 2.0) ? fract(vUv) : vUv;
  luv = clamp(luv, vec2(uInset), span - vec2(uInset));
  return texture2D(uAtlas, (vTile.xy + luv) / uGrid);
}

void main() {
  vec3 cell = floor(vCell + 0.001);
  // The level cut: cells at or above it are x-rayed away, like the blocks.
  if (uRoof.w > 0.0 && cell.y >= uRoof.y && length(cell.xz + 0.5 - uRoof.xz) <= uRoof.w) discard;
  vec4 t = vColor * texel();
  if (t.a < 0.1) discard;
  float lum = dot(t.rgb, vec3(0.299, 0.587, 0.114));
  vec3 rgb = uTint * (0.4 + 0.9 * lum);
  rgb = mix(vec3(dot(rgb, vec3(0.299, 0.587, 0.114))), rgb, uSat);
  // A soft glow along the block's edges: where the fragment is near two of the cell's faces at once.
  vec3 p = clamp(vWorld - cell, 0.0, 1.0);
  vec3 d = min(p, 1.0 - p);
  float mid = max(min(d.x, d.y), min(max(d.x, d.y), d.z));
  // A thin line (about a pixel at any zoom) plus a faint halo inside it.
  float w = max(fwidth(mid), 1e-4);
  float edge = (1.0 - smoothstep(w * 0.5, w * 1.6, mid)) * 0.75 + (1.0 - smoothstep(0.0, 0.08, mid)) * 0.2;
  // Static scanlines (no animation: calm).
  float scan = step(0.5, fract(vWorld.y * 5.0));
  float a = uOpacity * (0.86 + 0.14 * scan) + edge * 0.3;
  rgb = rgb * (0.92 + 0.08 * scan) + uTint * edge * 0.35;
  if (uStripes > 0.5) {
    float s = step(0.5, fract((vWorld.x + vWorld.y + vWorld.z) * 1.5));
    rgb = mix(rgb, vec3(0.95, 0.22, 0.2), 0.35 * s);
    a += 0.08 * s;
  }
  rgb *= uBoost;
  if (vWorld.y < uDimBelow) {
    rgb *= 0.55;
    a *= 0.7;
  }
  float fog = smoothstep(uFogRange.x, uFogRange.y, length(vWorld.xz - uFogCentre.xz));
  gl_FragColor = vec4(mix(rgb, uFogColor, fog), clamp(a, 0.0, 0.9) * (1.0 - fog));
}
`;

const lineVertex = /* glsl */ `
attribute vec3 aRgb;
attribute float aCy;
varying vec3 vRgb;
varying vec3 vWorld;
varying float vCy;
void main() {
  vec4 w = modelMatrix * vec4(position, 1.0);
  vWorld = w.xyz;
  vRgb = aRgb;
  vCy = aCy;
  gl_Position = projectionMatrix * viewMatrix * w;
}
`;

const lineFragment = /* glsl */ `
uniform vec4 uRoof;
uniform float uDimBelow;
uniform float uOpacity;
varying vec3 vRgb;
varying vec3 vWorld;
varying float vCy;
void main() {
  if (uRoof.w > 0.0 && vCy >= uRoof.y && length(vWorld.xz - uRoof.xz) <= uRoof.w) discard;
  float a = uOpacity * (vWorld.y < uDimBelow ? 0.6 : 1.0);
  gl_FragColor = vec4(vRgb, a);
}
`;

function blockMaterial(shared: ChunkUniforms, style: HoloStyle): THREE.ShaderMaterial {
  return new THREE.ShaderMaterial({
    vertexShader: blockVertex,
    fragmentShader: blockFragment,
    uniforms: {
      uAtlas: shared.uAtlas,
      uGrid: shared.uGrid,
      uInset: shared.uInset,
      uTextured: shared.uTextured,
      uRoof: shared.uRoof,
      uDimBelow: shared.uDimBelow,
      uFogColor: shared.uFogColor,
      uFogCentre: shared.uFogCentre,
      uFogRange: shared.uFogRange,
      uTint: { value: new THREE.Vector3(...style.tint) },
      uOpacity: { value: style.opacity },
      uSat: { value: style.saturation },
      uStripes: { value: style.stripes },
      uBoost: { value: style.boost },
    },
    transparent: true,
    depthWrite: false,
    depthTest: true,
    side: THREE.FrontSide,
  });
}

function lineMaterial(shared: ChunkUniforms, opacity: number): THREE.ShaderMaterial {
  return new THREE.ShaderMaterial({
    vertexShader: lineVertex,
    fragmentShader: lineFragment,
    uniforms: { uRoof: shared.uRoof, uDimBelow: shared.uDimBelow, uOpacity: { value: opacity } },
    transparent: true,
    depthWrite: false,
    depthTest: true,
  });
}

// The 12 edges of a unit cube as 24 corner indices into CORNERS.
const CORNERS: ReadonlyArray<readonly [number, number, number]> = [
  [0, 0, 0], [1, 0, 0], [1, 0, 1], [0, 0, 1], [0, 1, 0], [1, 1, 0], [1, 1, 1], [0, 1, 1],
];
const EDGES = [0, 1, 1, 2, 2, 3, 3, 0, 4, 5, 5, 6, 6, 7, 7, 4, 0, 4, 1, 5, 2, 6, 3, 7];

/** Outline boxes as one LineSegments geometry (positions relative to `origin`): each box [min, max, rgb, cut y]. */
export function boxLines(
  boxes: ReadonlyArray<{ min: readonly number[]; max: readonly number[]; rgb: readonly number[]; cy: number }>,
  origin: readonly number[],
  inset = 0,
): THREE.BufferGeometry {
  const pos = new Float32Array(boxes.length * 24 * 3), rgb = new Float32Array(boxes.length * 24 * 3), cy = new Float32Array(boxes.length * 24);
  boxes.forEach((b, i) => {
    for (let e = 0; e < 24; e++) {
      const c = CORNERS[EDGES[e]];
      const v = i * 24 + e;
      for (let k = 0; k < 3; k++) {
        pos[v * 3 + k] = (c[k] ? b.max[k] - inset : b.min[k] + inset) - origin[k];
        rgb[v * 3 + k] = b.rgb[k];
      }
      cy[v] = b.cy;
    }
  });
  const g = new THREE.BufferGeometry();
  g.setAttribute('position', new THREE.BufferAttribute(pos, 3));
  g.setAttribute('aRgb', new THREE.BufferAttribute(rgb, 3));
  g.setAttribute('aCy', new THREE.BufferAttribute(cy, 1));
  return g;
}

// ------------------------------------------------------------------------------------------------------------ layer

/** How far a dig cell's outline and top fill stand out of the cell (over the ground surface around it). */
const DIG_LIFT = 0.03;

/** Adds the "x,y,z" key of every cell in a flat [x, y, z, id, meta, …] list. */
function addCellKeys(into: Set<string>, cells: readonly number[]): void {
  for (let i = 0; i + 4 < cells.length + 1; i += 5) into.add(`${cells[i]},${cells[i + 1]},${cells[i + 2]}`);
}

/** A placement's cells split into the blocks it places and the cells it digs (id 0), both flat [x, y, z, id, meta, …]. */
function splitDigCells(blocks: readonly number[]): { solid: number[]; dig: number[] } {
  const solid: number[] = [], dig: number[] = [];
  for (let i = 0; i + 4 < blocks.length; i += 5) (blocks[i + 3] === 0 ? dig : solid).push(...blocks.slice(i, i + 5));
  return { solid, dig };
}

/** The preview's worker request: the cells it digs first are no obstacle to what goes into them (a trench, then its water). */
function placeRequest(solid: number[], dig: number[]): HoloRequest {
  const clear = dig.flatMap((_, i) => (i % 5 < 3 ? [dig[i]] : []));
  return { key: 'p', blocks: solid, mode: 'place', clear };
}

export interface HologramStats {
  /** Cells drawn as blocks (after the world left out the built ones). */
  cells: number;
  /** Cells still to dig (outlines). */
  digs: number;
  /** Outline boxes (builds). */
  boxes: number;
  /** Placement preview cells: fitting, blocked, and the cells it digs out first (outlines). */
  preview: { ok: number; blocked: number; dig: number };
  /** The far-zoom outline boxes are showing instead of the cells. */
  lod: boolean;
  /** Rebuilds done (worker round trips). */
  builds: number;
  rev: string | null;
  /** Drawn in the view's last frame (the "Build holograms" setting is on and this is the view showing them). */
  visible: boolean;
}

type Group = HoloState | 'place' | 'placeBlocked';

/** The one region of a small queue (meshed whole, under its plain state keys). */
const WHOLE = -1;

/** One region's holograms: its share of the plan, the cells whose block changes rebuild it, and what it draws now. */
interface Region {
  /** `holoRegionId`, or `WHOLE`. */
  id: number;
  plan: HoloRegionPlan;
  /** Its objects: its own group, or (a small queue) the cells group itself. */
  group: THREE.Group;
  /** Its drawn cells, by `localKey` (a big queue's regions; the whole queue's cells are the layer's `keys`). */
  keys: Set<number>;
  shown: Set<string>;
  shownDig: Set<string>;
  cells: number;
  digs: number;
  dirty: boolean;
  /** Moves with every new share of the plan: a worker answer for an older one is dropped. */
  gen: number;
}

/** A block's key inside its region. */
const localKey = (x: number, y: number, z: number) => ((y + 1024) * 32 + (z & 31)) * 32 + (x & 31);

function sameNumbers(a: readonly number[], b: readonly number[]): boolean {
  if (a.length !== b.length) return false;
  for (let i = 0; i < a.length; i++) if (a[i] !== b[i]) return false;
  return true;
}

function samePlan(a: HoloRegionPlan, b: HoloRegionPlan): boolean {
  return sameNumbers(a.dig, b.dig) && HOLO_STATES.every((s) => sameNumbers(a.groups[s], b.groups[s]));
}

/** One view's holograms (the queue and a placement preview). Add `group` to the scene; `show` before each render. */
export class HologramLayer {
  readonly group = new THREE.Group();
  private readonly cellsGroup = new THREE.Group();
  private readonly boxGroup = new THREE.Group();
  private readonly previewGroup = new THREE.Group();
  private readonly mats: Record<Group, THREE.ShaderMaterial>;
  private readonly digMat: THREE.ShaderMaterial;
  private readonly digFillMat: THREE.ShaderMaterial;
  private readonly boxMat: THREE.ShaderMaterial;
  private rev: string | null = null;
  private plan: HoloPlan | null = null;
  private keys = new Set<string>();
  /** The queue's regions (a small queue: the one `WHOLE`). Each knows the cells it draws now: as blocks, and as dig outlines
   * (one cell can be both kinds over time: dug, then the water poured). */
  private regions = new Map<number, Region>();
  /** Whether the queue is small enough to be meshed whole. */
  private whole = true;
  private busy = false;
  private dirty = false;
  private previewKey = '';
  private previewBlocks: number[] | null = null;
  private previewBusy = false;
  private previewDirty = false;
  private lod = false;
  private disposed = false;
  private readonly st: HologramStats = { cells: 0, digs: 0, boxes: 0, preview: { ok: 0, blocked: 0, dig: 0 }, lod: false, builds: 0, rev: null, visible: false };
  /** Resolves when no rebuild is pending (tests). */
  idle: Promise<void> = Promise.resolve();

  constructor(
    private readonly host: MeshHost,
    shared: ChunkUniforms,
    private dim = 0,
  ) {
    this.group.name = 'holograms';
    this.group.add(this.cellsGroup, this.boxGroup, this.previewGroup);
    this.mats = {
      todo: blockMaterial(shared, HOLO_STYLES.todo),
      active: blockMaterial(shared, HOLO_STYLES.active),
      waiting: blockMaterial(shared, HOLO_STYLES.waiting),
      blocked: blockMaterial(shared, HOLO_STYLES.blocked),
      place: blockMaterial(shared, HOLO_STYLES.place),
      placeBlocked: blockMaterial(shared, HOLO_STYLES.placeBlocked),
    };
    this.digMat = lineMaterial(shared, 0.8);
    this.digFillMat = lineMaterial(shared, 0.2);
    this.digFillMat.side = THREE.DoubleSide;
    this.boxMat = lineMaterial(shared, 0.85);
  }

  setDim(d: number): void {
    if (d === this.dim) return;
    this.dim = d;
    this.rev = null;
    if (this.whole) this.clearGroup(this.cellsGroup);
    else this.dropRegions();
    this.clearGroup(this.boxGroup);
  }

  /**
   * The queued cells, in queue order. Nothing happens unless `rev` differs from the last one (the caller's revision of its
   * build list: rebuilding is a worker round trip, never per frame). A small queue is meshed again whole; a big one only in
   * the regions whose cells changed. Returns whether a rebuild was started.
   */
  set(cells: readonly HoloCell[], rev: string): boolean {
    if (rev === this.rev) return false;
    this.rev = rev;
    this.st.rev = rev;
    const plan = (this.plan = planHolograms(cells));
    const whole = cells.length <= HOLO_REGION_MIN;
    if (whole !== this.whole) {
      this.dropRegions();
      this.whole = whole;
    }
    if (whole) {
      const keys = new Set<string>();
      for (const c of cells) keys.add(`${c.x},${c.y},${c.z}`);
      this.keys = keys;
      this.regionPlan(WHOLE, { key: '', groups: plan.groups, dig: plan.dig });
    } else {
      this.keys = new Set();
      const next = planRegions(plan);
      for (const [id, rp] of next) {
        const r = this.regions.get(id);
        if (!r || !samePlan(r.plan, rp)) this.regionPlan(id, rp);
      }
      for (const [id, r] of this.regions)
        if (!next.has(id)) {
          this.clearGroup(r.group);
          r.group.removeFromParent();
          this.regions.delete(id);
        }
    }
    this.schedule();
    return true;
  }

  /** Block changes in the world: a change on a queued cell (it was built or dug) rebuilds its region, so the hologram
   * disappears. */
  blocksChanged(dim: number, blocks: ReadonlyArray<readonly [number, number, number]>): void {
    if (dim !== this.dim || !this.plan) return;
    if (this.whole) {
      for (const b of blocks)
        if (this.keys.has(`${b[0]},${b[1]},${b[2]}`)) {
          const w = this.regions.get(WHOLE);
          if (w) w.dirty = true;
          this.schedule();
          return;
        }
      return;
    }
    let hit = false;
    for (const b of blocks) {
      const r = this.regions.get(holoRegionId(b[0], b[2]));
      if (r?.keys.has(localKey(b[0], b[1], b[2]))) hit = r.dirty = true;
    }
    if (hit) this.schedule();
  }

  /** A placement preview (flat [x, y, z, id, meta, …]; id 0 = dig), green where it fits and red where blocked; null clears.
   * `key` names the placement (the same key keeps the current preview). */
  setPreview(blocks: number[] | null, key = ''): void {
    const k = blocks ? key || `${blocks.length}:${blocks.slice(0, 15).join(',')}` : '';
    if (k === this.previewKey) return;
    this.previewKey = k;
    this.previewBlocks = blocks;
    if (!blocks) {
      this.clearGroup(this.previewGroup);
      this.st.preview = { ok: 0, blocked: 0, dig: 0 };
      return;
    }
    this.previewDirty = true;
    void this.runPreview();
  }

  /** Meshes everything again (the block atlas changed). */
  refresh(): void {
    if (this.plan) {
      for (const r of this.regions.values()) r.dirty = true;
      this.schedule();
    }
    if (this.previewBlocks) {
      this.previewDirty = true;
      void this.runPreview();
    }
  }

  /** Before rendering the view: `pxPerBlock` = how big a block is on screen (null = near enough). */
  show(pxPerBlock: number | null): void {
    const plan = this.plan;
    this.lod = pxPerBlock !== null && pxPerBlock < HOLO_LOD_PX;
    this.st.lod = this.lod;
    this.cellsGroup.visible = !this.lod;
    // Boxes: every build when zoomed out; else only the builds whose cells were capped.
    for (const o of this.boxGroup.children) o.visible = this.lod || (!!plan && plan.capped.size > 0 && o.name === 'capped');
  }

  stats(): HologramStats {
    return { ...this.st, preview: { ...this.st.preview }, visible: this.group.visible };
  }

  /** Whether a queued cell is drawn as a hologram now (built cells are not): as a block, as a dig outline, or either. */
  has(x: number, y: number, z: number, kind?: 'block' | 'dig'): boolean {
    const r = this.regions.get(this.whole ? WHOLE : holoRegionId(x, z));
    if (!r) return false;
    const k = `${x},${y},${z}`;
    return kind === 'dig' ? r.shownDig.has(k) : kind === 'block' ? r.shown.has(k) : r.shown.has(k) || r.shownDig.has(k);
  }

  dispose(): void {
    this.disposed = true;
    this.clearGroup(this.cellsGroup);
    this.clearGroup(this.boxGroup);
    this.clearGroup(this.previewGroup);
    for (const m of Object.values(this.mats)) m.dispose();
    this.digMat.dispose();
    this.digFillMat.dispose();
    this.boxMat.dispose();
    this.group.removeFromParent();
  }

  // ---------------------------------------------------------------------------------------------------------- internals

  private schedule(): void {
    this.dirty = true;
    if (!this.busy) this.idle = this.run();
  }

  /** A region's new share of the plan (made on first use): it is meshed again, and an answer for its old share is dropped. */
  private regionPlan(id: number, plan: HoloRegionPlan): void {
    let r = this.regions.get(id);
    if (!r) {
      const group = id === WHOLE ? this.cellsGroup : new THREE.Group();
      if (id !== WHOLE) {
        group.name = `holo region ${plan.key}`;
        this.cellsGroup.add(group);
      }
      r = {
        id,
        plan,
        group,
        keys: new Set(),
        shown: new Set(),
        shownDig: new Set(),
        cells: 0,
        digs: 0,
        dirty: true,
        gen: 0,
      };
      this.regions.set(id, r);
    }
    r.plan = plan;
    r.dirty = true;
    r.gen++;
    if (id === WHOLE) return;
    const keys = new Set<number>();
    for (const flat of [...HOLO_STATES.map((s) => plan.groups[s]), plan.dig])
      for (let i = 0; i + 4 < flat.length; i += 5) keys.add(localKey(flat[i], flat[i + 1], flat[i + 2]));
    r.keys = keys;
  }

  private dropRegions(): void {
    this.clearGroup(this.cellsGroup);
    this.regions.clear();
  }

  /** A region's worker requests: per state, then its digs (a region of a big queue answers under "<state>@<region>"). */
  private requests(r: Region): HoloRequest[] {
    const at = r.plan.key ? `@${r.plan.key}` : '';
    const reqs: HoloRequest[] = HOLO_STATES.filter((s) => r.plan.groups[s].length).map((s) => ({
      key: s + at,
      blocks: r.plan.groups[s],
      mode: 'build',
    }));
    if (r.plan.dig.length) reqs.push({ key: 'dig' + at, blocks: r.plan.dig, mode: 'dig' });
    return reqs;
  }

  private async run(): Promise<void> {
    while (this.dirty && !this.disposed) {
      this.dirty = false;
      this.busy = true;
      const plan = this.plan;
      if (!plan) break;
      const batch = [...this.regions.values()].filter((r) => r.dirty);
      const gens = batch.map((r) => r.gen);
      for (const r of batch) r.dirty = false;
      const reqs = batch.flatMap((r) => this.requests(r));
      let rs: HoloResult[];
      try {
        rs = reqs.length ? await this.host.holo(this.dim, reqs) : [];
      } catch {
        rs = [];
      }
      if (this.disposed) break;
      // A region whose cells changed meanwhile (or that went) waits for the next round: its answer is for the old ones.
      const live = batch.filter((r, i) => r.gen === gens[i] && this.regions.get(r.id) === r);
      if (live.length < batch.length) this.dirty = true;
      if (batch.length && !live.length) continue;
      if (live.length) this.st.builds++;
      this.apply(plan, live, rs);
    }
    this.busy = false;
  }

  /** The worker's answers into their regions; then the outline boxes and the totals. */
  private apply(plan: HoloPlan, live: readonly Region[], rs: HoloResult[]): void {
    const byKey = new Map(live.map((r) => [r.plan.key, [] as HoloResult[]]));
    for (const res of rs) {
      const at = res.key.indexOf('@');
      byKey.get(at < 0 ? '' : res.key.slice(at + 1))?.push(res);
    }
    for (const r of live) this.applyRegion(r, byKey.get(r.plan.key)!);
    this.clearGroup(this.boxGroup);
    this.addBoxes(plan);
    let cells = 0, digs = 0;
    for (const r of this.regions.values()) {
      cells += r.cells;
      digs += r.digs;
    }
    this.st.cells = cells;
    this.st.digs = digs;
    this.st.boxes = plan.boxes.length;
    this.show(this.lod ? 0 : null);
  }

  private applyRegion(r: Region, rs: readonly HoloResult[]): void {
    this.clearGroup(r.group);
    const shown = new Set<string>(),
      shownDig = new Set<string>();
    let cells = 0,
      digs = 0;
    for (const res of rs) {
      const at = res.key.indexOf('@');
      const state = at < 0 ? res.key : res.key.slice(0, at);
      const dig = state === 'dig';
      addCellKeys(dig ? shownDig : shown, res.cells);
      if (dig) {
        digs = res.cells.length / 5;
        if (digs) r.group.add(...this.digMarks(res.cells));
        continue;
      }
      cells += res.cells.length / 5;
      if (res.mesh) r.group.add(this.mesh(res, this.mats[state as HoloState]));
    }
    r.shown = shown;
    r.shownDig = shownDig;
    r.cells = cells;
    r.digs = digs;
  }

  /** Outline boxes: every build (far zoom), the capped ones marked to show always. */
  private addBoxes(plan: HoloPlan): void {
    const all = plan.boxes.map((b) => ({ min: b.min, max: b.max, rgb: BOX_RGB[b.state], cy: b.min[1], capped: plan.capped.has(b.build) }));
    if (!all.length) return;
    const origin = all[0].min;
    const capped = all.filter((b) => b.capped), rest = all.filter((b) => !b.capped);
    for (const [list, name] of [[capped, 'capped'], [rest, 'all']] as const) {
      if (!list.length) continue;
      const l = new THREE.LineSegments(boxLines(list, origin, -0.02), this.boxMat);
      l.name = name;
      l.position.set(origin[0], origin[1], origin[2]);
      l.frustumCulled = false;
      l.renderOrder = 6;
      this.boxGroup.add(l);
    }
  }

  private async runPreview(): Promise<void> {
    if (this.previewBusy) return;
    this.previewBusy = true;
    try {
      while (this.previewDirty && !this.disposed) {
        this.previewDirty = false;
        const blocks = this.previewBlocks;
        const key = this.previewKey;
        if (!blocks) break;
        const { solid, dig } = splitDigCells(blocks);
        let rs: HoloResult[];
        try {
          rs = await this.host.holo(this.dim, [placeRequest(solid, dig)]);
        } catch {
          rs = [];
        }
        if (this.disposed || key !== this.previewKey) continue;
        this.showPreview(rs, dig);
      }
    } finally {
      this.previewBusy = false;
    }
  }

  /** Draws a preview's answer: fitting and blocked cells in their two looks, and outlines for the cells it digs. */
  private showPreview(rs: HoloResult[], dig: number[]): void {
    this.clearGroup(this.previewGroup);
    let ok = 0, blocked = 0;
    for (const r of rs) {
      const bad = r.key.endsWith(':blocked');
      if (bad) blocked += r.cells.length / 5;
      else ok += r.cells.length / 5;
      if (r.mesh) this.previewGroup.add(this.mesh(r, bad ? this.mats.placeBlocked : this.mats.place));
    }
    if (dig.length) this.previewGroup.add(...this.digMarks(dig));
    this.st.preview = { ok, blocked, dig: dig.length / 5 };
  }

  private mesh(r: HoloResult, mat: THREE.ShaderMaterial): THREE.Mesh {
    const m = r.mesh!;
    const g = new THREE.BufferGeometry();
    g.setAttribute('position', new THREE.BufferAttribute(m.pos, 3));
    g.setAttribute('uv', new THREE.BufferAttribute(m.uv, 2));
    g.setAttribute('aTile', new THREE.BufferAttribute(m.tile, 4, false));
    g.setAttribute('aColor', new THREE.BufferAttribute(m.color, 4, true));
    g.setAttribute('aCellW', new THREE.BufferAttribute(m.cell, 3));
    g.setIndex(new THREE.BufferAttribute(m.index, 1));
    g.computeBoundingSphere();
    const mesh = new THREE.Mesh(g, mat);
    mesh.name = `holo ${r.key}`;
    mesh.position.set(m.origin[0], m.origin[1], m.origin[2]);
    mesh.renderOrder = 5;
    return mesh;
  }

  /**
   * Cells to dig: a red outline just outside each cell (a cell in solid ground is inside the ground, so an outline on its own
   * edges or inside them would be buried) and a faint red fill over each exposed top (no dig cell above it), lifted a little
   * over the ground surface so a trench in flat ground reads from above and from the angled view alike.
   */
  private digMarks(cells: readonly number[]): [THREE.LineSegments, THREE.Mesh] {
    const boxes: Array<{ min: number[]; max: number[]; rgb: readonly number[]; cy: number }> = [];
    const at = new Set<string>();
    for (let i = 0; i + 4 < cells.length + 1; i += 5) at.add(`${cells[i]},${cells[i + 1]},${cells[i + 2]}`);
    const tops: number[][] = [];
    for (let i = 0; i + 4 < cells.length + 1; i += 5) {
      const x = cells[i], y = cells[i + 1], z = cells[i + 2];
      boxes.push({ min: [x, y, z], max: [x + 1, y + 1, z + 1], rgb: DIG_RGB, cy: y });
      if (!at.has(`${x},${y + 1},${z}`)) tops.push([x, y, z]);
    }
    const origin = boxes[0].min;
    const l = new THREE.LineSegments(boxLines(boxes, origin, -DIG_LIFT), this.digMat);
    l.name = 'holo dig';
    l.position.set(origin[0], origin[1], origin[2]);
    l.geometry.computeBoundingSphere();
    l.renderOrder = 5;
    const pos = new Float32Array(tops.length * 18), rgb = new Float32Array(tops.length * 18), cy = new Float32Array(tops.length * 6);
    tops.forEach(([x, y, z], i) => {
      const x0 = x - origin[0] - DIG_LIFT, x1 = x0 + 1 + 2 * DIG_LIFT, z0 = z - origin[2] - DIG_LIFT, z1 = z0 + 1 + 2 * DIG_LIFT;
      const yy = y + 1 + DIG_LIFT - origin[1];
      pos.set([x0, yy, z0, x0, yy, z1, x1, yy, z1, x0, yy, z0, x1, yy, z1, x1, yy, z0], i * 18);
      for (let k = 0; k < 6; k++) {
        rgb.set(DIG_RGB, i * 18 + k * 3);
        cy[i * 6 + k] = y;
      }
    });
    const g = new THREE.BufferGeometry();
    g.setAttribute('position', new THREE.BufferAttribute(pos, 3));
    g.setAttribute('aRgb', new THREE.BufferAttribute(rgb, 3));
    g.setAttribute('aCy', new THREE.BufferAttribute(cy, 1));
    g.computeBoundingSphere();
    const f = new THREE.Mesh(g, this.digFillMat);
    f.name = 'holo dig fill';
    f.position.set(origin[0], origin[1], origin[2]);
    f.renderOrder = 5;
    return [l, f];
  }

  private clearGroup(g: THREE.Group): void {
    for (const o of [...g.children]) {
      g.remove(o);
      if (o instanceof THREE.Group) this.clearGroup(o); // a region's group
      (o as THREE.Mesh).geometry?.dispose();
    }
  }
}
