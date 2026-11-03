// Section meshes of one dimension on the GPU, shared by every view: what to (re)mesh next (dirty and in range of any view,
// nearest first), the meshes themselves (ONE BufferGeometry per pass per section; the cut cap reuses the solid geometry with
// the cap material, no copy), per-view visibility by draw distance, and eviction (everything far beyond every view, then
// least recently rendered). The worker keeps the blocks, so an evicted section is simply meshed again when it comes back.

import * as THREE from 'three';
import type { MeshHost } from '../mesh/host';
import type { SectionMesh } from '../mesh/mesher';
import type { MeshArrays } from '../mesh/sink';
import type { WorldCache } from '../world/cache';
import { ALL_VISIBLE, visibleSections } from '../mesh/visibility';

interface Entry {
  cx: number;
  sy: number;
  cz: number;
  solid: THREE.Mesh | null;
  cap: THREE.Mesh | null;
  trans: THREE.Mesh | null;
  bytes: number;
  /** Cave-culling face connectivity. */
  vis: number;
  lastRendered: number;
  /** Bumped when the section is dirtied; a result for an older generation is re-requested. */
  gen: number;
  meshedGen: number;
}

export interface Centre {
  x: number;
  y: number;
  z: number;
  /** Draw distance in blocks. */
  distance: number;
}

const skey = (cx: number, sy: number, cz: number) => `${cx},${sy},${cz}`;
const SPHERE = new THREE.Sphere(new THREE.Vector3(8, 8, 8), 14);

export class SectionManager {
  readonly group = new THREE.Group();
  private readonly entries = new Map<string, Entry>();
  private readonly dirty = new Set<string>();
  private inFlight = false;
  private frame = 0;
  private scanAt = -Infinity;
  maxSections = 6000;
  batch = 12;
  dim = 0;
  quads = 0;
  /** Bytes of every section buffer held (what the GPU holds for this dimension's world). */
  gpuBytes = 0;
  /** How many times each section was meshed and uploaded (diagnostics and tests). */
  readonly builds = new Map<string, number>();

  constructor(
    private readonly host: MeshHost,
    private cache: WorldCache,
    private readonly mats: { solid: THREE.Material; cap: THREE.Material; translucent: THREE.Material },
  ) {
    this.group.name = 'sections';
  }

  setCache(c: WorldCache): void {
    this.clear();
    this.cache = c;
  }

  /** Sections with an entry (meshed, empty or pending). */
  get count(): number {
    return this.entries.size;
  }

  /** Section geometries held (solid + translucent). */
  get geometries(): number {
    let n = 0;
    for (const e of this.entries.values()) n += (e.solid ? 1 : 0) + (e.trans ? 1 : 0);
    return n;
  }

  get queued(): number {
    return this.dirty.size;
  }

  get busy(): boolean {
    return this.inFlight;
  }

  markDirty(list: Array<[number, number, number]>): void {
    for (const [cx, sy, cz] of list) {
      if (!this.cache.get(this.dim, cx, cz)) continue;
      const k = skey(cx, sy, cz);
      const e = this.entries.get(k);
      if (e) e.gen++;
      this.dirty.add(k);
    }
  }

  /** Columns are gone: free their meshes. */
  dropColumns(cols: Array<[number, number]>): void {
    const set = new Set(cols.map(([x, z]) => `${x},${z}`));
    for (const [k, e] of this.entries) if (set.has(`${e.cx},${e.cz}`)) this.remove(k, e);
    for (const k of [...this.dirty]) {
      const [x, , z] = k.split(',');
      if (set.has(`${x},${z}`)) this.dirty.delete(k);
    }
  }

  clear(): void {
    for (const [k, e] of this.entries) this.remove(k, e);
    this.dirty.clear();
  }

  private remove(k: string, e: Entry): void {
    this.release(e);
    this.entries.delete(k);
  }

  private release(e: Entry): void {
    for (const m of [e.solid, e.cap, e.trans]) if (m) this.group.remove(m);
    e.solid?.geometry.dispose();
    e.trans?.geometry.dispose();
    this.gpuBytes -= e.bytes;
    e.bytes = 0;
    e.solid = e.cap = e.trans = null;
  }

  /**
   * Before rendering one view: only the sections within its draw distance of (x, z), and (with `cull`) only those the
   * camera can see through the cave-culling graph. `viewKey` caches the walk per view while its camera stays in one section
   * and keeps looking the same way.
   */
  showAround(x: number, z: number, drawDistance: number, opts?: { cull?: { viewKey: number; cam: THREE.Vector3; dir: THREE.Vector3; ymax: number } | null; translucentDistance?: number }): void {
    const ccx = x / 16, ccz = z / 16, maxD = drawDistance / 16 + 1;
    const cull = opts?.cull;
    const seen = cull ? this.visible(cull, Math.ceil(maxD)) : null;
    const transD = (opts?.translucentDistance ?? Infinity) / 16 + 1;
    let quads = 0;
    this.culled = 0;
    for (const e of this.entries.values()) {
      let vis = Math.hypot(e.cx + 0.5 - ccx, e.cz + 0.5 - ccz) <= maxD;
      if (vis && seen && !seen.has(`${e.cx},${e.sy},${e.cz}`)) {
        vis = false;
        if (e.solid || e.trans) this.culled++;
      }
      if (e.solid) {
        e.solid.visible = vis;
        if (e.cap) e.cap.visible = vis;
        quads += vis ? (e.solid.userData.quads as number) : 0;
      }
      if (e.trans) {
        // Distant water and glass are the first thing a slow renderer drops (blended overdraw).
        const tv = vis && Math.hypot(e.cx + 0.5 - ccx, e.cz + 0.5 - ccz) <= transD;
        e.trans.visible = tv;
        quads += tv ? (e.trans.userData.quads as number) : 0;
      }
    }
    this.quads = quads;
  }

  /** Sections hidden by cave culling in the last showAround. */
  culled = 0;
  private visRev = 0;
  private readonly visCache = new Map<number, { s: string; dir: THREE.Vector3; rev: number; set: Set<string> | null }>();

  private visible(c: { viewKey: number; cam: THREE.Vector3; dir: THREE.Vector3; ymax: number }, range: number): Set<string> | null {
    const sx = Math.floor(c.cam.x / 16), sy = Math.floor(c.cam.y / 16), sz = Math.floor(c.cam.z / 16);
    const s = `${sx},${sy},${sz}`;
    const hit = this.visCache.get(c.viewKey);
    if (hit && hit.s === s && hit.dir.dot(c.dir) > 0.97 && this.visRev - hit.rev < 8) return hit.set;
    // A camera buried in a solid section sees nothing through the graph: draw everything (the x-ray handles it).
    const start = this.entries.get(s);
    const set = start && start.vis === 0 ? null : visibleSections(sx, sy, sz, range, c.ymax, (cx, cy, cz) => this.entries.get(`${cx},${cy},${cz}`)?.vis ?? ALL_VISIBLE, c.dir);
    this.visCache.set(c.viewKey, { s, dir: c.dir.clone(), rev: this.visRev, set });
    return set;
  }

  /** Once per frame for all views in this dimension: a section is wanted when it is in range of any of them. */
  maintain(centres: Centre[], now: number): void {
    this.frame++;
    if (!centres.length) return;
    const cs = centres.map((c) => ({ x: c.x / 16, y: c.y / 16, z: c.z / 16, d: c.distance / 16 + 1 }));
    const inRange = (cx: number, cz: number, slack = 1, extra = 0) => cs.some((c) => Math.hypot(cx + 0.5 - c.x, cz + 0.5 - c.z) <= c.d * slack + extra);
    // Columns in range of some view that have no entries yet (first sight, or evicted earlier and back in range).
    if (now - this.scanAt > 400) {
      this.scanAt = now;
      for (const c of this.cache.columns(this.dim)) {
        if (!inRange(c.cx, c.cz)) continue;
        for (let sy = 0; sy < c.height >> 4; sy++) {
          const k = skey(c.cx, sy, c.cz);
          if (!this.entries.has(k)) this.dirty.add(k);
        }
      }
    }
    for (const [k, e] of this.entries) if (!inRange(e.cx, e.cz, 1.3, 2)) this.remove(k, e);
    if (this.entries.size > this.maxSections) {
      const lru = [...this.entries].sort((a, b) => a[1].lastRendered - b[1].lastRendered);
      for (const [k, e] of lru.slice(0, this.entries.size - this.maxSections)) this.remove(k, e);
    }
    if (!this.inFlight && this.dirty.size) this.schedule(cs);
  }

  private schedule(cs: Array<{ x: number; y: number; z: number; d: number }>): void {
    const scored: Array<[number, string]> = [];
    for (const k of this.dirty) {
      const [cx, sy, cz] = k.split(',').map(Number);
      let best = Infinity;
      for (const c of cs) {
        const d = Math.hypot(cx + 0.5 - c.x, cz + 0.5 - c.z);
        if (d <= c.d) best = Math.min(best, d * d + (sy + 0.5 - c.y) ** 2 * 0.25);
      }
      if (best < Infinity) scored.push([best, k]);
    }
    if (!scored.length) return;
    scored.sort((a, b) => a[0] - b[0]);
    const pick: Array<[number, number, number]> = [];
    const gens = new Map<string, number>();
    for (const [, k] of scored.slice(0, this.batch)) {
      this.dirty.delete(k);
      const [cx, sy, cz] = k.split(',').map(Number);
      pick.push([cx, sy, cz]);
      let e = this.entries.get(k);
      if (!e) {
        e = { cx, sy, cz, solid: null, cap: null, trans: null, bytes: 0, vis: ALL_VISIBLE, lastRendered: this.frame, gen: 1, meshedGen: 0 };
        this.entries.set(k, e);
      }
      gens.set(k, e.gen);
    }
    this.inFlight = true;
    const dim = this.dim;
    this.host
      .mesh(dim, pick, null)
      .then((res) => {
        if (dim !== this.dim) return;
        for (const m of res) this.install(m, gens.get(skey(m.cx, m.sy, m.cz)) ?? 0);
      })
      .catch(() => {
        for (const [cx, sy, cz] of pick) this.dirty.add(skey(cx, sy, cz));
      })
      .finally(() => {
        this.inFlight = false;
      });
  }

  private install(m: SectionMesh, gen: number): void {
    const k = skey(m.cx, m.sy, m.cz);
    const e = this.entries.get(k);
    if (!e) return; // evicted or dropped meanwhile
    if (e.gen !== gen) {
      this.dirty.add(k);
      if (e.meshedGen > 0) return; // keep the old mesh until the fresh one arrives
    }
    e.meshedGen = gen;
    e.vis = m.vis ?? ALL_VISIBLE;
    this.visRev++;
    this.builds.set(k, (this.builds.get(k) ?? 0) + 1);
    this.release(e);
    if (m.solid) {
      const g = this.geometry(m.solid, e);
      e.solid = this.mesh(g, this.mats.solid, e, 0, m.solid.quads);
      e.cap = this.mesh(g, this.mats.cap, e, 1, 0); // the same geometry, back faces only
    }
    if (m.translucent) e.trans = this.mesh(this.geometry(m.translucent, e), this.mats.translucent, e, 2, m.translucent.quads);
  }

  private geometry(data: MeshArrays, e: Entry): THREE.BufferGeometry {
    const g = new THREE.BufferGeometry();
    g.setAttribute('position', new THREE.BufferAttribute(data.pos, 3));
    g.setAttribute('uv', new THREE.BufferAttribute(data.uv, 2));
    g.setAttribute('aTile', new THREE.BufferAttribute(data.tile, 4, false));
    g.setAttribute('aColor', new THREE.BufferAttribute(data.color, 4, true));
    g.setAttribute('aLight', new THREE.BufferAttribute(data.light, 4, true));
    g.setAttribute('aCell', new THREE.BufferAttribute(data.cell, 4, false));
    g.setIndex(new THREE.BufferAttribute(data.index, 1));
    g.boundingSphere = SPHERE.clone();
    g.boundingBox = new THREE.Box3(new THREE.Vector3(-1, -1, -1), new THREE.Vector3(17, 17, 17));
    const bytes = data.pos.byteLength + data.uv.byteLength + data.tile.byteLength + data.color.byteLength + data.light.byteLength + data.cell.byteLength + data.index.byteLength;
    e.bytes += bytes;
    this.gpuBytes += bytes;
    return g;
  }

  private mesh(g: THREE.BufferGeometry, mat: THREE.Material, e: Entry, order: number, quads: number): THREE.Mesh {
    const mesh = new THREE.Mesh(g, mat);
    mesh.position.set(e.cx * 16, e.sy * 16, e.cz * 16);
    mesh.matrixAutoUpdate = false;
    mesh.updateMatrix();
    mesh.renderOrder = order;
    mesh.userData.quads = quads;
    mesh.onBeforeRender = () => {
      e.lastRendered = this.frame;
    };
    this.group.add(mesh);
    return mesh;
  }

  dispose(): void {
    this.clear();
  }
}
