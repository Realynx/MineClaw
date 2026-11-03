// The shared world runtime: ONE WebGL context, ONE scene, ONE mesh worker, ONE world store (per dimension, keyed (dim, cx, cz)
// with its hash, fetched once whatever number of views or bots need it), ONE BufferGeometry per section pass, ONE set of
// materials and one atlas texture, one entity store and layer, one overlay layer. Views (the interactive viewer, hive-card
// thumbnails) own only a camera, a viewport and per-view uniforms (sky/time, fog centre, x-ray cone, roof cut); everything
// else is shared. Section upkeep and world sync follow the union of every view's interest region, so bots near each other
// share sections. The interactive "primary" view renders straight into the runtime's canvas (placed in its container);
// every other view is rendered into a corner of it and copied into its own 2D canvas in the same frame.

import * as THREE from 'three';
import type { AtlasMap } from '../atlas/types';
import { createMeshHost, InlineMeshHost, type MeshHost } from '../mesh/host';
import type { Dim, LayerToggles, Overlays, WorldSource } from '../types';
import { normalizeEntities, normalizeOverlays } from '../wire/normalize';
import type { GameVersion } from '../world/blocks';
import { WorldCache } from '../world/cache';
import { EntityStore, type BotView, type EntityView } from '../world/entities';
import { fogColour, skyColour, skylightSubtractedSmooth } from '../world/sky';
import { Poller, WorldSync, type Interest } from '../world/sync';
import { atlasTexture, bindAtlas } from './atlasTexture';
import { createChunkMaterials, createChunkUniforms, type ChunkMaterials, type ChunkUniforms } from './chunkMaterial';
import { EntityLayer } from './entityLayer';
import { OverlayLayer } from './overlayLayer';
import { SectionManager, type Centre } from './sections';
import { WeatherLayer } from './weather';
import { ChangeFlash } from './changeFlash';
import { BlockDeltaFeed } from '../world/deltas';

export interface RuntimeOptions {
  /** Device pixel ratio cap for the primary view (default min(2, devicePixelRatio)). */
  pixelRatio?: number;
  antialias?: boolean;
  /** Lower the primary view's resolution while it runs under ~50 fps (back up when it has headroom). Default on. */
  adaptiveResolution?: boolean;
  /** Most section entries kept per dimension (LRU beyond). */
  maxSections?: number;
  entityPollMs?: number;
  overlayPollMs?: number;
  indexPeriodMs?: number;
  /** Most columns kept per dimension. */
  maxColumns?: number;
  createWorker?: () => Worker;
  /** Mesh on the main thread (tests, environments without module workers). */
  inlineMesher?: boolean;
  /** Secondary (thumbnail) renders per animation frame at most (default 2); the most overdue go first. */
  viewsPerFrame?: number;
  /** Largest secondary render in device pixels (default 480×300; bigger cards are upscaled). */
  thumbMaxWidth?: number;
  thumbMaxHeight?: number;
  onError?: (route: string, e: unknown) => void;
}

/** What one view draws this frame. */
export interface ViewSetup {
  dim: Dim;
  camera: THREE.PerspectiveCamera;
  /** Fog and section-visibility centre (the camera, or what it looks at). */
  centre: THREE.Vector3;
  drawDistance: number;
  /** Device pixels. */
  width: number;
  height: number;
  layers: Pick<LayerToggles, 'bots' | 'entities' | 'weather' | 'staleness' | 'changes'>;
  /** Labels on (the interactive view) or off (thumbnails). */
  labels: boolean;
  overlays: boolean;
  selected: string | null;
  /** X-ray cone subjects (points the cone aims at) and the floor it never cuts below. */
  cones: Array<{ x: number; y: number; z: number }>;
  floorY: number;
  /** Roof cut: cells at y ≥ roof.y within roof.r of (x, z). */
  roof: { x: number; y: number; z: number; r: number } | null;
  /** Where the picture goes: the runtime canvas itself (primary) or a 2D canvas it is copied into. */
  target: 'primary' | CanvasRenderingContext2D;
}

export interface RuntimeView {
  readonly id: number;
  /** The world this view needs synced (null = nothing yet). */
  interest(): Interest | null;
  /** The centre and draw distance its sections are kept around (null = none). */
  centre(): (Centre & { dim: Dim }) | null;
  /** Called every animation frame; returns what to render now, or null to skip this frame. */
  frame(rt: WorldRuntime, now: number, dt: number): ViewSetup | null;
  /** The dimension whose overlays it shows (null = none). */
  overlayDim(): Dim | null;
  selectedBots(): string[];
}

export interface RuntimeMemory {
  views: number;
  columns: number;
  /** Section entries (meshed, empty or pending) across dimensions. */
  sections: number;
  /** BufferGeometries held for sections (one per pass per non-empty section, shared by every view). */
  sectionGeometries: number;
  sectionsQueued: number;
  /** Bytes of section vertex/index buffers (each uploaded once to the one context). */
  gpuBytes: number;
  /** Approximate bytes of decoded blocks/light the worker holds. */
  workerBytes: number;
  bytesReceived: number;
  entities: number;
  bots: number;
  atlas: string | null;
  drawCalls: number;
  triangles: number;
}

let nextViewId = 1;
export const newViewId = () => nextViewId++;

export class WorldRuntime {
  readonly renderer: THREE.WebGLRenderer;
  readonly canvas: HTMLCanvasElement;
  readonly scene = new THREE.Scene();
  readonly uniforms: ChunkUniforms;
  readonly materials: ChunkMaterials;
  readonly host: MeshHost;
  cache = new WorldCache();
  sync: WorldSync;
  readonly store = new EntityStore();
  readonly entities = new EntityLayer();
  readonly overlays = new OverlayLayer();
  readonly weather = new WeatherLayer();
  readonly flashes = new ChangeFlash();
  deltas: BlockDeltaFeed;
  private readonly sections = new Map<Dim, SectionManager>();
  private readonly views = new Map<number, RuntimeView>();
  private primary: { view: RuntimeView; container: HTMLElement } | null = null;
  private entityPoller: Poller;
  private overlayPoller: Poller;
  private source: WorldSource;
  private readonly opts: RuntimeOptions;
  private raf = 0;
  private last = 0;
  private refs = 0;
  private disposed = false;
  version: GameVersion | null = null;
  private atlasMap: AtlasMap | null = null;
  private atlasTex: THREE.Texture | null = null;
  private configKey = '';
  private overlayData = new Map<Dim, Overlays>();
  private readonly botsBuf: BotView[] = [];
  private readonly entsBuf: EntityView[] = [];
  private scale = 1;
  private scaleAt = 0;
  private flash = 0;
  private readonly renderedAt = new Map<number, number>();
  private slots = 0;
  /** Called (on the runtime's frame) after the overlays of a dimension changed. */
  onOverlays: ((dim: Dim, ov: Overlays) => void) | null = null;
  readonly gpu: string;

  constructor(source: WorldSource, opts: RuntimeOptions = {}) {
    this.source = source;
    this.opts = opts;
    this.renderer = new THREE.WebGLRenderer({ antialias: opts.antialias ?? true, powerPreference: 'high-performance', preserveDrawingBuffer: false });
    this.renderer.setPixelRatio(1);
    this.renderer.setScissorTest(true);
    this.canvas = this.renderer.domElement;
    this.canvas.tabIndex = 0;
    Object.assign(this.canvas.style, { display: 'block', width: '100%', height: '100%', outline: 'none', touchAction: 'none' });
    let gpu = 'webgl2';
    try {
      const gl = this.renderer.getContext();
      const ext = gl.getExtension('WEBGL_debug_renderer_info');
      gpu = ext ? String(gl.getParameter(ext.UNMASKED_RENDERER_WEBGL)) : String(gl.getParameter(gl.RENDERER));
    } catch {
      /* keep the default */
    }
    this.gpu = gpu;
    this.scene.background = new THREE.Color(0x78a7ff);
    this.scene.add(new THREE.HemisphereLight(0xffffff, 0x555566, 2.2));
    const sun = new THREE.DirectionalLight(0xffffff, 1.2);
    sun.position.set(0.4, 1, 0.25);
    this.scene.add(sun);
    this.uniforms = createChunkUniforms();
    this.materials = createChunkMaterials(this.uniforms);
    this.host = opts.inlineMesher ? new InlineMeshHost() : createMeshHost(opts.createWorker);
    this.scene.add(this.entities.group, this.overlays.group, this.weather.object, this.flashes.mesh);
    this.sync = this.makeSync();
    this.deltas = this.makeDeltas();
    this.entityPoller = new Poller(
      async () => {
        const raw = await this.source.entities(this.store.rev > 0 ? this.store.rev : undefined);
        this.store.apply(normalizeEntities(raw));
      },
      opts.entityPollMs ?? 200,
      (e) => this.opts.onError?.('entities', e),
    );
    this.overlayPoller = new Poller(() => this.pollOverlays(), opts.overlayPollMs ?? 4000, (e) => this.opts.onError?.('overlays', e));
    void this.configure();
    void this.loadAtlas();
    this.sync.start();
    this.deltas.start();
    this.entityPoller.start();
    this.overlayPoller.start();
    this.raf = requestAnimationFrame(this.tick);
  }

  // ------------------------------------------------------------------------------------------------------------ lifetime

  /** Shared owners (a provider, its views) hold a reference; the last release disposes. */
  retain(): this {
    this.refs++;
    return this;
  }

  release(): void {
    if (--this.refs <= 0) this.dispose();
  }

  addView(v: RuntimeView): void {
    this.views.set(v.id, v);
    this.overlayPoller.kick();
  }

  removeView(v: RuntimeView): void {
    this.views.delete(v.id);
    this.renderedAt.delete(v.id);
    if (this.primary?.view === v) {
      this.canvas.remove();
      this.primary = null;
    }
  }

  /** Makes `v` the view drawn straight into the runtime canvas, placed in `container`. False when another view has it. */
  attachPrimary(v: RuntimeView, container: HTMLElement): boolean {
    if (this.primary && this.primary.view !== v) return false;
    this.primary = { view: v, container };
    container.appendChild(this.canvas);
    return true;
  }

  isPrimary(v: RuntimeView): boolean {
    return this.primary?.view === v;
  }

  get viewCount(): number {
    return this.views.size;
  }

  setSource(src: WorldSource): void {
    if (src === this.source) return;
    this.sync.stop();
    this.deltas.stop();
    this.entityPoller.stop();
    this.overlayPoller.stop();
    this.source = src;
    this.store.clear();
    this.cache = new WorldCache();
    for (const s of this.sections.values()) s.setCache(this.cache);
    void this.host.clear();
    this.sync = this.makeSync();
    this.deltas = this.makeDeltas();
    this.overlayData.clear();
    this.overlays.set(null, this.version ?? 'b1.8.1');
    this.configKey = '';
    void this.configure();
    void this.loadAtlas();
    this.sync.start();
    this.deltas.start();
    this.entityPoller.start();
    this.overlayPoller.start();
  }

  dispose(): void {
    if (this.disposed) return;
    this.disposed = true;
    cancelAnimationFrame(this.raf);
    this.sync.stop();
    this.deltas.stop();
    this.entityPoller.stop();
    this.overlayPoller.stop();
    this.views.clear();
    for (const s of this.sections.values()) s.dispose();
    this.sections.clear();
    this.entities.dispose();
    this.overlays.dispose();
    this.weather.dispose();
    this.flashes.dispose();
    for (const m of [this.materials.solid, this.materials.translucent, this.materials.cap]) m.dispose();
    this.atlasTex?.dispose();
    this.host.dispose();
    this.renderer.renderLists.dispose();
    this.renderer.dispose();
    this.renderer.forceContextLoss();
    this.canvas.remove();
  }

  get isDisposed(): boolean {
    return this.disposed;
  }

  // ------------------------------------------------------------------------------------------------------------ world

  sectionsFor(dim: Dim): SectionManager {
    let s = this.sections.get(dim);
    if (!s) {
      s = new SectionManager(this.host, this.cache, this.materials);
      s.dim = dim;
      if (this.opts.maxSections) s.maxSections = this.opts.maxSections;
      this.sections.set(dim, s);
      this.scene.add(s.group);
    }
    return s;
  }

  get textured(): boolean {
    return !!this.atlasTex && this.uniforms.uTextured.value > 0;
  }

  timeNow(dim: Dim): number | null {
    return this.cache.timeNow(dim, Date.now());
  }

  overlaysFor(dim: Dim): Overlays | null {
    return this.overlayData.get(dim) ?? null;
  }

  memory(): RuntimeMemory {
    let sections = 0, geometries = 0, queued = 0, gpuBytes = 0;
    for (const s of this.sections.values()) {
      sections += s.count;
      geometries += s.geometries;
      queued += s.queued;
      gpuBytes += s.gpuBytes;
    }
    let workerBytes = 0;
    if (this.host instanceof InlineMeshHost) for (const c of this.host.engine.store.cols.values()) workerBytes += c.ids.byteLength + c.meta.byteLength + (c.light?.full.reduce((a, f) => a + (f?.byteLength ?? 0), 0) ?? 0);
    else for (const d of [0, -1, 1]) for (const c of this.cache.columns(d)) workerBytes += 512 * c.height + 2048;
    const info = this.renderer.info.render;
    return {
      views: this.views.size,
      columns: this.cache.count(),
      sections,
      sectionGeometries: geometries,
      sectionsQueued: queued,
      gpuBytes,
      workerBytes,
      bytesReceived: this.cache.bytesReceived,
      entities: this.store.count,
      bots: this.store.bots().length,
      atlas: this.atlasMap ? `${this.atlasMap.version} ${this.atlasMap.columns}×${this.atlasMap.rows}` : null,
      drawCalls: info.calls,
      triangles: info.triangles,
    };
  }

  private makeSync(): WorldSync {
    const s = new WorldSync(this.source, this.host, this.cache, {
      onApplied: (res) => this.sections.get(res.dim)?.markDirty(res.dirty),
      onDropped: (dim, cols, dirty) => {
        const m = this.sections.get(dim);
        m?.dropColumns(cols);
        m?.markDirty(dirty);
      },
      onTouched: (dim, dirty) => this.sections.get(dim)?.markDirty(dirty),
      onInfo: () => void this.configure(),
      onError: (r, e) => this.opts.onError?.(r, e),
    });
    if (this.opts.indexPeriodMs) s.periodMs = this.opts.indexPeriodMs;
    if (this.opts.maxColumns) s.maxColumns = this.opts.maxColumns;
    return s;
  }

  private makeDeltas(): BlockDeltaFeed {
    return new BlockDeltaFeed(this.source, this.host, this.cache, this.sync, {
      onDirty: (dim, dirty) => this.sections.get(dim)?.markDirty(dirty),
      onChanged: (dim, blocks) => this.flashes.add(dim, blocks, performance.now()),
      onError: (e) => this.opts.onError?.('blocks', e),
    });
  }

  /** (Re)configures the mesher when the version or atlas changed; every section is meshed again. */
  private async configure(): Promise<void> {
    const v = this.cache.detectedVersion() ?? (this.atlasMap?.version === '1.12.2' ? '1.12.2' : 'b1.8.1');
    const atlas = this.atlasMap && (this.atlasMap.version === v || !this.atlasMap.version) ? this.atlasMap : null;
    const key = `${v}|${atlas ? 'atlas' : 'flat'}`;
    if (key === this.configKey) return;
    this.configKey = key;
    this.version = v;
    this.uniforms.uTextured.value = atlas && this.atlasTex ? 1 : 0;
    await this.host.configure(v, atlas);
    for (const s of this.sections.values()) s.clear();
  }

  private async loadAtlas(): Promise<void> {
    try {
      const res = await this.source.atlas();
      if (!res || this.disposed || res.map?.format !== 'mineclaw-atlas') return;
      this.atlasTex?.dispose();
      this.atlasTex = atlasTexture(res);
      this.atlasMap = res.map;
      bindAtlas(this.uniforms, this.atlasTex, res);
      this.configKey = '';
      await this.configure();
    } catch (e) {
      this.opts.onError?.('atlas', e);
    }
  }

  private async pollOverlays(): Promise<void> {
    const dims = new Set<Dim>();
    for (const v of this.views.values()) {
      const d = v.overlayDim();
      if (d != null) dims.add(d);
    }
    for (const d of dims) {
      const ov = normalizeOverlays(await this.source.overlays(d));
      this.overlayData.set(d, ov);
      this.onOverlays?.(d, ov);
    }
  }

  // ------------------------------------------------------------------------------------------------------------ frame

  private readonly tick = (now: number): void => {
    if (this.disposed) return;
    this.raf = requestAnimationFrame(this.tick);
    const dt = Math.min(0.1, this.last ? (now - this.last) / 1000 : 0);
    this.last = now;
    this.step(now, dt);
  };

  /** One frame: shared upkeep, then every due view (the primary last, so its picture is what the canvas shows). */
  step(now: number, dt: number): void {
    const selected = new Set<string>();
    for (const v of this.views.values()) for (const s of v.selectedBots()) selected.add(s);
    this.entities.update(this.store.bots(this.botsBuf), this.store.entities(null, this.entsBuf), now, selected);
    this.overlays.tick(now);
    void this.deltas.flush(); // a frame's worth of block changes, applied together
    const interests: Record<string, Interest> = {};
    const centres = new Map<Dim, Centre[]>();
    for (const v of this.views.values()) {
      const i = v.interest();
      if (i) interests[`v${v.id}`] = i;
      const c = v.centre();
      if (!c) continue;
      const list = centres.get(c.dim) ?? [];
      list.push(c);
      centres.set(c.dim, list);
    }
    this.sync.setInterests(interests);
    for (const [dim, cs] of centres) this.sectionsFor(dim).maintain(cs, now);
    let primarySetup: ViewSetup | null = null;
    this.slots = this.opts.viewsPerFrame ?? 2;
    const order = [...this.views.values()].sort((a, b) => (this.renderedAt.get(a.id) ?? 0) - (this.renderedAt.get(b.id) ?? 0));
    for (const v of order) {
      const s = v.frame(this, now, dt);
      if (!s) continue;
      this.renderedAt.set(v.id, now);
      if (this.isPrimary(v)) primarySetup = s;
      else this.render(s, now);
    }
    if (primarySetup) {
      // Every 1.5 s one primary frame is timed to completion (gl.finish): that measures the GPU cost itself, so a throttled
      // background tab (slow rAF, cheap frames) never lowers the resolution.
      const sample = this.opts.adaptiveResolution !== false && now - this.scaleAt >= 1500;
      const t0 = performance.now();
      this.render(primarySetup, now);
      if (sample) {
        this.renderer.getContext().finish();
        this.adapt(now, performance.now() - t0);
      }
    }
  }

  /** A secondary view asks for one of this frame's render slots (round-robin, most overdue first). */
  takeSlot(): boolean {
    if (this.slots <= 0) return false;
    this.slots--;
    return true;
  }

  /** Render size of a secondary view of `cw`×`ch` device pixels (capped; the copy upscales). */
  thumbSize(cw: number, ch: number): [number, number] {
    const k = Math.min(1, (this.opts.thumbMaxWidth ?? 480) / cw, (this.opts.thumbMaxHeight ?? 300) / ch);
    return [Math.max(1, Math.floor(cw * k)), Math.max(1, Math.floor(ch * k))];
  }

  /** The canvas size the primary view should use (its container × pixel ratio × the adaptive scale). */
  primarySize(container: HTMLElement): [number, number] {
    const pr = (this.opts.pixelRatio ?? Math.min(2, globalThis.devicePixelRatio || 1)) * this.scale;
    return [Math.max(1, Math.round(container.clientWidth * pr)), Math.max(1, Math.round(container.clientHeight * pr))];
  }

  /** Adaptive resolution from a timed frame: over ~14 ms (no room for 60 fps) step down, under ~7 ms step back up. */
  private adapt(now: number, frameMs: number): void {
    this.scaleAt = now;
    this.lastFrameMs = frameMs;
    if (frameMs > 14 && this.scale > 0.5) this.scale = Math.max(0.5, this.scale * 0.85);
    else if (frameMs < 7 && this.scale < 1) this.scale = Math.min(1, this.scale * 1.15);
  }

  /** Duration of the last timed primary frame (CPU + GPU), ms. */
  lastFrameMs = 0;

  get resolutionScale(): number {
    return this.scale;
  }

  private render(s: ViewSetup, now: number): void {
    const u = this.uniforms;
    const r = this.renderer;
    const primary = s.target === 'primary';
    // Size: the primary owns the canvas; other views use its bottom-left corner (the canvas grows when nobody owns it).
    let w = s.width, h = s.height;
    const cw = this.canvas.width, ch = this.canvas.height;
    if (primary) {
      if (cw !== w || ch !== h) r.setSize(w, h, false);
    } else if (!this.primary && (cw < w || ch < h)) r.setSize(Math.max(cw, w), Math.max(ch, h), false);
    const fw = this.canvas.width, fh = this.canvas.height;
    w = Math.min(w, fw);
    h = Math.min(h, fh);
    s.camera.aspect = w / h;
    s.camera.updateProjectionMatrix();
    // World uniforms for this view.
    const nether = s.dim === -1;
    const time = this.cache.timeNow(s.dim, Date.now());
    const wx = this.cache.weatherIn(s.dim);
    const rain = s.layers.weather ? wx.rain : 0, thunder = s.layers.weather ? wx.thunder : 0;
    u.uSkySub.value = nether || time == null ? 0 : skylightSubtractedSmooth(time, rain, thunder);
    u.uBase.value = nether ? 0.1 : 0;
    const sky = skyColour(time, rain, thunder, nether), fog = fogColour(time, rain, thunder, nether);
    (this.scene.background as THREE.Color).setRGB(sky[0], sky[1], sky[2], THREE.SRGBColorSpace);
    u.uFogColor.value.setRGB(fog[0], fog[1], fog[2]);
    u.uFogCentre.value.copy(s.centre);
    u.uFogRange.value.set(s.drawDistance * 0.72, s.drawDistance);
    u.uStale.value = s.layers.staleness ? 1 : 0;
    if (primary) this.flash = this.weather.update(s.camera.position, now / 1000, rain, thunder, s.layers.weather && !nether);
    this.weather.object.visible = primary && s.layers.weather && !nether && rain > 0.01;
    u.uFlash.value = primary ? this.flash : 0;
    // Per-view cut-aways (uniforms only; the cap pass shows what they open).
    const cam = s.camera.position;
    let n = 0;
    for (const c of s.cones) {
      if (n >= 2) break;
      const ax = c.x - cam.x, ay = c.y - cam.y, az = c.z - cam.z, d = Math.hypot(ax, ay, az);
      if (d < 1.5) continue;
      u.uConeU.value[n++].set(ax / d, ay / d, az / d, d);
    }
    u.uCones.value = n;
    u.uFloorY.value = s.floorY;
    if (s.roof) u.uRoof.value.set(s.roof.x, s.roof.y, s.roof.z, s.roof.r);
    else u.uRoof.value.set(0, 0, 0, 0);
    this.materials.cap.visible = n > 0 || !!s.roof;
    // Shared scene, this view's slice of it.
    for (const [dim, sec] of this.sections) {
      sec.group.visible = dim === s.dim;
      if (dim === s.dim) sec.showAround(s.centre.x, s.centre.z, s.drawDistance);
    }
    this.entities.showFor(s.dim, cam, { bots: s.layers.bots, entities: s.layers.entities, labels: s.labels, selected: s.selected });
    this.overlays.group.visible = s.overlays;
    this.flashes.showFor(s.dim, now, s.layers.changes);
    r.setViewport(0, 0, w, h);
    r.setScissor(0, 0, w, h);
    r.render(this.scene, s.camera);
    if (s.target !== 'primary') {
      const ctx = s.target;
      const tw = ctx.canvas.width, th = ctx.canvas.height;
      ctx.drawImage(this.canvas, 0, fh - h, w, h, 0, 0, tw, th);
    }
  }
}
