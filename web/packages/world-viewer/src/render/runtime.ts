// The shared world runtime: ONE WebGL context, ONE scene, ONE mesh worker, ONE world store (per dimension, keyed (dim, cx, cz)
// with its hash, fetched once whatever number of views or bots need it), ONE BufferGeometry per section pass, ONE set of
// materials and one atlas texture, one entity store and layer, one overlay layer. Views (the interactive viewer, hive-card
// thumbnails) own only a camera, a viewport and per-view uniforms (sky/time, fog centre, x-ray cone, roof cut); everything
// else is shared. Section upkeep and world sync follow the union of every view's interest region, so bots near each other
// share sections. The interactive "primary" view renders straight into the runtime's canvas (placed in its container);
// every other view is rendered into a corner of it and copied into its own 2D canvas in the same frame.

import * as THREE from 'three';
import { createMeshHost, InlineMeshHost, type MeshHost } from '../mesh/host';
import type { Dim, LayerToggles, Overlays, WorldSource } from '../types';
import { normalizeEntities, normalizeOverlays } from '../wire/normalize';
import type { GameVersion } from '../world/blocks';
import { WorldCache } from '../world/cache';
import { EntityStore, type BotView, type EntityView } from '../world/entities';
import { fogColour, precipitation, skyColour, skylightSubtractedSmooth } from '../world/sky';
import { Poller, WorldSync, type Interest } from '../world/sync';
// [blocks] the bundled block atlas + its animation pass
import { meshAssets } from '../atlas/assets';
import { BlockAtlas } from './blockAtlas';
import { BlockEntityLayer, type BlockEntityLayerUpdate } from './blockEntityLayer'; // [blocks] sign text, banners, spawner mobs
import { createChunkMaterials, createChunkUniforms, type ChunkMaterials, type ChunkUniforms } from './chunkMaterial';
import { EntityLayer } from './entityLayer';
import { OverlayLayer } from './overlayLayer';
import { SectionManager, type Centre } from './sections';
import { WeatherLayer } from './weather';
import { SkyLayer } from './sky';
import { AidsLayer } from './aids';
import { loadBundle, type AssetBundle } from '../assets/bundle';
import { PROFILES, QualityController, probeRenderer, type QualityName, type RendererProbe } from './quality';
import { ChangeFlash } from './changeFlash';
import { BlockDeltaFeed } from '../world/deltas';
import { BlockEntityStore } from '../world/blockEntities';

export interface RuntimeOptions {
  /** Device pixel ratio cap for the primary view (default min(2, devicePixelRatio)). */
  pixelRatio?: number;
  antialias?: boolean;
  /** Let the frame-time controller move resolution and draw distance (default on). */
  adaptiveResolution?: boolean;
  /** Quality profile: auto-detect software rendering (default), or force one. */
  quality?: 'auto' | QualityName;
  /** Most section entries kept per dimension (LRU beyond). */
  maxSections?: number;
  entityPollMs?: number;
  overlayPollMs?: number;
  indexPeriodMs?: number;
  /** Most columns kept per dimension. */
  maxColumns?: number;
  createWorker?: () => Worker;
  /** [blocks] Textured blocks from the bundled atlas (default true); false = the flat palette (no bundle download). */
  textures?: boolean;
  /** [blocks] Opaque "fast graphics" leaves (default false). */
  fastLeaves?: boolean;
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
  /** Reading aids around this point (the followed bot's feet), or none. */
  aids?: { x: number; y: number; z: number } | null;
  /** What falls here (biome under the view); default rain. */
  precip?: 'rain' | 'snow' | 'none';
  /** The view's id (per-view caches such as the cave-culling walk). */
  viewId?: number;
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
  /** Active quality profile, the renderer string and why it was picked. */
  quality: QualityName;
  renderer: string;
  qualityReason: string;
  /** Smoothed timed frame (CPU + GPU), ms. */
  frameMs: number;
  /** Draw-distance factor the controller applies. */
  drawFactor: number;
  /** Sections hidden by cave culling in the last view drawn. */
  culledSections: number;
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
  readonly sky = new SkyLayer();
  readonly aids = new AidsLayer();
  /** Block entities of the held columns (sign text, banners, spawner mobs… for the main-thread layers). */
  blockEntities: BlockEntityStore;
  /** The bundled assets of the current version (textures, models); null until loaded. */
  bundle: AssetBundle | null = null;
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
  // [blocks] the bundled block atlas of the configured version (null = flat palette or still loading).
  blockAtlas: BlockAtlas | null = null;
  /** [blocks] Sign text, patterned swaying banners and spawner mobs (main thread, shared by every view). */
  readonly blockEntityLayer: BlockEntityLayer;
  private readonly beCentre = new THREE.Vector3();
  private atlasFor: GameVersion | null = null;
  private configKey = '';
  private overlayData = new Map<Dim, Overlays>();
  private readonly botsBuf: BotView[] = [];
  private readonly entsBuf: EntityView[] = [];
  readonly quality: QualityController;
  readonly probe: RendererProbe;
  private flash = 0;
  private readonly renderedAt = new Map<number, number>();
  private readonly tmpDir = new THREE.Vector3();
  /** World brightening from a lightning strike (decays over ~25 frames). */
  private lightningFlash = 0;
  private readonly pixel = new Uint8Array(4);
  private slots = 0;
  /** Called (on the runtime's frame) after the overlays of a dimension changed. */
  onOverlays: ((dim: Dim, ov: Overlays) => void) | null = null;
  readonly gpu: string;

  constructor(source: WorldSource, opts: RuntimeOptions = {}) {
    this.source = source;
    this.opts = opts;
    // Software WebGL (hardware acceleration off) gets its own profile: no MSAA, 1× pixels, shorter draw distance, 25 ms target.
    this.probe = opts.quality && opts.quality !== 'auto' ? { software: opts.quality === 'software', renderer: 'forced', reason: `quality: ${opts.quality}` } : probeRenderer();
    const profile = PROFILES[opts.quality && opts.quality !== 'auto' ? opts.quality : this.probe.software ? 'software' : 'gpu'];
    this.quality = new QualityController(profile);
    this.quality.enabled = opts.adaptiveResolution !== false;
    this.renderer = new THREE.WebGLRenderer({ antialias: (opts.antialias ?? true) && profile.antialias, powerPreference: 'high-performance', preserveDrawingBuffer: false });
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
    this.entities.setWorldUniforms(this.uniforms); // [entity models] entities light and fog like the sections
    this.host = opts.inlineMesher ? new InlineMeshHost() : createMeshHost(opts.createWorker);
    this.scene.add(this.aids.group, this.sky.group, this.entities.group, this.overlays.group, this.weather.object, this.flashes.mesh);
    this.blockEntityLayer = new BlockEntityLayer(this.host, this.uniforms); // [blocks]
    this.scene.add(this.blockEntityLayer.group);
    this.blockEntities = new BlockEntityStore(source, this.host, (dim, dirty) => this.sections.get(dim)?.markDirty(dirty), (e) => this.opts.onError?.('blockentities', e));
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
    // The network loops start with the first view (and pause when the last one leaves).
    this.paused = true;
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
    if (this.paused) this.resume();
    this.overlayPoller.kick();
  }

  removeView(v: RuntimeView): void {
    this.views.delete(v.id);
    // No views left (e.g. between pages, persistent runtime): stop the network loops, keep the world.
    if (!this.views.size) this.pause();
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

  private paused = false;

  private pause(): void {
    if (this.paused || this.disposed) return;
    this.paused = true;
    this.sync.stop();
    this.deltas.stop();
    this.entityPoller.stop();
    this.overlayPoller.stop();
  }

  private resume(): void {
    if (!this.paused || this.disposed) return;
    this.paused = false;
    this.sync.start();
    this.deltas.fallback(); // changes may have been missed while paused: the index hash-diff catches up
    this.deltas.start();
    this.entityPoller.start();
    this.overlayPoller.start();
  }

  /**
   * Swaps in a new source object for the same world (a page recreated its fetch wrapper) without dropping the store, the
   * meshes or the entities: the loops are rebuilt around the existing cache. Use `setSource` for a different world.
   */
  replaceSource(src: WorldSource): void {
    if (src === this.source || this.disposed) return;
    this.sync.stop();
    this.deltas.stop();
    this.source = src;
    this.blockEntities.setSource(src);
    const rev = this.deltas.rev;
    this.sync = this.makeSync();
    this.deltas = this.makeDeltas();
    this.deltas.rev = rev;
    if (!this.paused) {
      this.sync.start();
      this.deltas.start();
    }
  }

  setSource(src: WorldSource): void {
    if (src === this.source) return;
    this.sync.stop();
    this.deltas.stop();
    this.entityPoller.stop();
    this.overlayPoller.stop();
    this.source = src;
    this.blockEntities.setSource(src);
    this.blockEntities.clear();
    this.blockEntityLayer.clear(); // [blocks]
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
    if (!this.paused) {
      this.sync.start();
      this.deltas.start();
      this.entityPoller.start();
      this.overlayPoller.start();
    }
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
    this.blockEntityLayer.dispose(); // [blocks]
    this.sky.dispose();
    this.aids.dispose();
    for (const m of [this.materials.solid, this.materials.translucent, this.materials.cap]) m.dispose();
    this.blockAtlas?.dispose();
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

  private meshHold = false;
  private readonly entityLight = new Map<string, readonly [number, number]>();
  private lightAt = 0;
  private lightBusy = false;

  /** Entity models take the light of their cell: asked from the worker a few times a second, cached per cell. */
  private refreshEntityLight(now: number): void {
    if (!this.entities.lightAt)
      this.entities.lightAt = (dim, x, y, z) => {
        // The cells sampled are near the body's middle; take the closest one sampled.
        const bx = Math.floor(x), by = Math.floor(y), bz = Math.floor(z);
        for (const dy of [0, -1, 1, -2, 2]) {
          const v = this.entityLight.get(`${dim},${bx},${by + dy},${bz}`);
          if (v) return v;
        }
        return null;
      };
    if (this.lightBusy || now - this.lightAt < 300) return;
    this.lightAt = now;
    const byDim = new Map<Dim, number[]>();
    const add = (dim: Dim, x: number, y: number, z: number) => {
      let l = byDim.get(dim);
      if (!l) byDim.set(dim, (l = []));
      l.push(Math.floor(x), Math.floor(y), Math.floor(z));
    };
    for (const b of this.botsBuf) add(b.dim, b.x, b.y + 0.9, b.z);
    for (const e of this.entsBuf) add(e.dim, e.x, e.y + 0.4, e.z);
    if (!byDim.size) return;
    this.lightBusy = true;
    Promise.all(
      [...byDim].map(([dim, pts]) =>
        this.host.lights(dim, pts).then((v) => {
          for (let i = 0; i < v.length; i++) if (v[i] >= 0) this.entityLight.set(`${dim},${pts[i * 3]},${pts[i * 3 + 1]},${pts[i * 3 + 2]}`, [v[i] >> 4, v[i] & 15]);
        }),
      ),
    )
      .catch(() => undefined)
      .finally(() => {
        this.lightBusy = false;
        if (this.entityLight.size > 4096) this.entityLight.clear();
      });
  }

  private setMeshHold(h: boolean): void {
    this.meshHold = h;
    for (const s of this.sections.values()) s.hold = h;
  }

  sectionsFor(dim: Dim): SectionManager {
    let s = this.sections.get(dim);
    if (!s) {
      s = new SectionManager(this.host, this.cache, this.materials);
      s.dim = dim;
      s.hold = this.meshHold;
      if (this.opts.maxSections) s.maxSections = this.opts.maxSections;
      this.sections.set(dim, s);
      this.scene.add(s.group);
    }
    return s;
  }

  get textured(): boolean {
    return !!this.blockAtlas && this.uniforms.uTextured.value > 0;
  }

  timeNow(dim: Dim): number | null {
    return this.cache.timeNow(dim, Date.now());
  }

  /** Precipitation for a 1.12.2 biome id at height y (rain when unknown). */
  precipAt(biome: number, y: number): 'rain' | 'snow' | 'none' {
    const p = this.bundle?.pack;
    const b = p && p.version === '1.12.2' && biome >= 0 ? p.biomes[String(biome)] : null;
    return precipitation(b, y);
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
      atlas: this.blockAtlas ? `${this.blockAtlas.bundle.version} ${this.blockAtlas.columns}×${this.blockAtlas.rows}` : null,
      drawCalls: info.calls,
      triangles: info.triangles,
      quality: this.quality.state.profile.name,
      renderer: this.probe.renderer,
      qualityReason: this.probe.reason,
      frameMs: this.quality.state.smoothMs,
      drawFactor: this.quality.state.drawFactor,
      culledSections: [...this.sections.values()].reduce((a, s) => a + s.culled, 0),
    };
  }

  private makeSync(): WorldSync {
    const s = new WorldSync(this.source, this.host, this.cache, {
      onApplied: (res) => {
        this.sections.get(res.dim)?.markDirty(res.dirty);
        // A column that arrived or changed: its block entities may have too.
        this.blockEntities.want(res.dim, res.columns.filter((c) => !c.missing).map((c) => [c.cx, c.cz] as [number, number]));
        this.blockEntities.drop(res.dim, res.columns.filter((c) => c.missing).map((c) => [c.cx, c.cz] as [number, number]));
      },
      onDropped: (dim, cols, dirty) => {
        const m = this.sections.get(dim);
        m?.dropColumns(cols);
        m?.markDirty(dirty);
        this.blockEntities.drop(dim, cols);
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
      onBlockEntities: (dim, cx, cz) => this.blockEntities.want(dim, [[cx, cz]]),
      onError: (e) => this.opts.onError?.('blocks', e),
    });
  }

  // [blocks] -------------------------------------------------------------------------------------------------------
  /** (Re)configures the mesher when the version changed: flat at once, then textured when the version's bundle has loaded
   * (the bundled atlas replaces the bridge's; `source.atlas` is not read). Every section is meshed again. */
  private async configure(): Promise<void> {
    const v = this.cache.detectedVersion() ?? 'b1.8.1';
    const want = this.opts.textures !== false;
    const ready = want && this.blockAtlas?.bundle.version === v;
    // [blocks] Fast (opaque, mutually culled) leaves on request only: `npm run perf` showed no measurable gain under
    // SwiftShader on the dense fixture (within run-to-run noise), so the software profile keeps fancy leaves.
    const fastLeaves = ready && this.opts.fastLeaves === true;
    const key = `${v}|${ready ? 'atlas' : 'flat'}|${fastLeaves ? 'fast' : 'fancy'}`;
    // While the version's textures load, sections wait instead of meshing flat first and textured again.
    this.setMeshHold(want && !ready);
    if (key === this.configKey) return;
    this.configKey = key;
    this.version = v;
    // The version's bundled assets: sky and entities here, the block atlas in loadBlockAtlas (one fetch, cached).
    loadBundle(v)
      .then((b) => {
        if (this.disposed || this.version !== v) return;
        this.bundle = b;
        this.sky.setBundle(b);
      })
      .catch((e) => this.opts.onError?.('assets', e));
    this.loadEntityBundle(v); // [entity models]
    this.uniforms.uTextured.value = ready ? 1 : 0;
    await this.host.configure(v, ready ? meshAssets(this.blockAtlas!.bundle.pack) : null, ready, fastLeaves);
    for (const s of this.sections.values()) s.clear();
    if (want && !ready && this.atlasFor !== v) void this.loadBlockAtlas(v);
  }

  // [entity models] The entity layer textures its models from the bundled sheets of the world's version.
  private entityBundle: GameVersion | null = null;
  private loadEntityBundle(v: GameVersion): void {
    this.entities.setVersion(v);
    if (this.entityBundle === v) return;
    this.entityBundle = v;
    loadBundle(v)
      .then((b) => {
        if (!this.disposed && this.entityBundle === v) this.entities.setBundle(b);
      })
      .catch((e) => this.opts.onError?.('bundle', e));
  }

  private async loadBlockAtlas(v: GameVersion): Promise<void> {
    this.atlasFor = v;
    try {
      const bundle = await loadBundle(v);
      if (this.disposed || this.atlasFor !== v) return;
      this.blockAtlas?.dispose();
      this.blockAtlas = new BlockAtlas(bundle);
      this.blockAtlas.bind(this.uniforms);
      this.blockEntityLayer.setBundle(bundle); // [blocks] banner pattern textures
      await this.configure();
    } catch (e) {
      this.atlasFor = null;
      this.setMeshHold(false); // no textures: mesh flat rather than nothing
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
    this.refreshEntityLight(now);
    // Entity events: the entity layer animates them (swings, hurt flashes, deaths…); lightning also flashes the world.
    const events = this.store.drainEvents();
    if (events.length) {
      for (const ev of events) if (ev.kind === 'lightning') this.lightningFlash = 0.7;
      this.entities.onEvents?.(events, now);
    }
    this.blockEntities.tick(now);
    {
      // [blocks] the block-entity layer around the primary view (else the first view): sign text, banners, spawner mobs.
      const v = this.primary?.view ?? this.views.values().next().value;
      const c = v?.centre();
      if (c) {
        this.beCentre.set(c.x, c.y, c.z);
        this.blockEntityLayer.update(this.blockEntities, {
          dim: c.dim,
          centre: this.beCentre,
          software: this.quality.state.profile.name === 'software',
          now,
          ticks: this.cache.timeNow(c.dim, Date.now()) ?? now / 50,
          displayMobs: this.entities as unknown as BlockEntityLayerUpdate['displayMobs'],
        });
      }
    }
    void this.deltas.flush(); // a frame's worth of block changes, applied together
    if (this.uniforms.uTextured.value > 0 && this.blockAtlas) {
      // [blocks] atlas animation: the profile's tick rate and cell budget, only the cells the drawn sections sample.
      const p = this.quality.state.profile;
      this.blockAtlas.animHz = p.animHz;
      this.blockAtlas.animCellsPerTick = p.animCellsPerTick;
      this.blockAtlas.starLayers = p.name === 'software' ? 8 : 16;
      const cells = new Set<number>();
      for (const s of this.sections.values()) s.visibleAnimCells(cells);
      this.blockAtlas.setVisible(cells);
      this.blockAtlas.update(this.renderer, now);
    }
    const interests: Record<string, Interest> = {};
    const centres = new Map<Dim, Centre[]>();
    for (const v of this.views.values()) {
      const i = v.interest();
      if (i) interests[`v${v.id}`] = i;
      const c0 = v.centre();
      if (!c0) continue;
      const c = { ...c0, distance: this.quality.drawDistance(c0.distance) };
      const list = centres.get(c.dim) ?? [];
      list.push(c);
      centres.set(c.dim, list);
    }
    this.sync.setInterests(interests);
    for (const [dim, cs] of centres) this.sectionsFor(dim).maintain(cs, now);
    let primarySetup: ViewSetup | null = null;
    const timed = this.quality.due(now);
    const t0 = performance.now();
    this.slots = Math.min(this.opts.viewsPerFrame ?? 2, this.quality.state.profile.thumbSlots);
    const order = [...this.views.values()].sort((a, b) => (this.renderedAt.get(a.id) ?? 0) - (this.renderedAt.get(b.id) ?? 0));
    for (const v of order) {
      const s = v.frame(this, now, dt);
      if (!s) continue;
      this.renderedAt.set(v.id, now);
      if (this.isPrimary(v)) primarySetup = s;
      else this.render(s, now);
    }
    if (primarySetup) this.render(primarySetup, now);
    // Once a second the whole frame is timed to completion (gl.finish): CPU and GPU (or the software rasteriser) together,
    // so a throttled background tab (slow rAF, cheap frames) never lowers the quality.
    if (timed && primarySetup) {
      // gl.finish returns early on ANGLE/SwiftShader; reading one pixel back really waits for the frame to be drawn.
      const gl = this.renderer.getContext();
      gl.readPixels(0, 0, 1, 1, gl.RGBA, gl.UNSIGNED_BYTE, this.pixel);
      this.quality.sample(now, performance.now() - t0);
    }
  }

  private overlaysShown: Overlays | null = null;

  /** Puts `dim`'s overlays in the shared overlay layer (rebuilt only when the data or the dimension changed). */
  private showOverlays(dim: Dim): void {
    const ov = this.overlayData.get(dim) ?? null;
    if (ov === this.overlaysShown) return;
    this.overlaysShown = ov;
    this.overlays.set(ov, this.version ?? 'b1.8.1');
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

  /** The canvas size the primary view should use (its container × pixel ratio × the controller's scale). */
  primarySize(container: HTMLElement): [number, number] {
    const q = this.quality.state;
    const pr = Math.min(this.opts.pixelRatio ?? (globalThis.devicePixelRatio || 1), q.profile.pixelRatioCap) * q.scale;
    return [Math.max(1, Math.round(container.clientWidth * pr)), Math.max(1, Math.round(container.clientHeight * pr))];
  }

  /** Duration of the last timed frame (CPU + GPU), ms. */
  get lastFrameMs(): number {
    return this.quality.state.frameMs;
  }

  get resolutionScale(): number {
    return this.quality.state.scale;
  }

  /** Forces a quality profile (developer details) or returns to the detected one. */
  setQuality(q: 'auto' | QualityName): void {
    this.quality.setProfile(PROFILES[q === 'auto' ? (this.probe.software ? 'software' : 'gpu') : q]);
  }

  private render(s: ViewSetup, now: number): void {
    const u = this.uniforms;
    const r = this.renderer;
    const primary = s.target === 'primary';
    // The controller's draw distance (software rendering, slow frames) applies to the interactive view.
    if (primary) s = { ...s, drawDistance: this.quality.drawDistance(s.drawDistance) };
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
    this.uniforms.uProj.value.copy(s.camera.projectionMatrix);
    this.uniforms.uViewport.value.set(w, h); // [blocks] the starfield's screen-space projection
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
    this.sky.update(s.camera.position, time, rain, nether, s.layers.weather && this.quality.state.profile.clouds, now, s.camera.far * 0.8);
    if (primary) this.flash = this.weather.update(s.camera.position, now / 1000, rain, thunder, s.layers.weather && !nether, s.precip ?? 'rain');
    this.weather.object.visible = primary && s.layers.weather && !nether && rain > 0.01;
    this.lightningFlash = Math.max(0, this.lightningFlash - 0.03);
    u.uFlash.value = (primary ? this.flash : 0) + this.lightningFlash;
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
      if (dim === s.dim)
        sec.showAround(s.centre.x, s.centre.z, s.drawDistance, {
          // Cave culling, unless a cut-away is opening the rock for this view.
          cull: this.quality.state.profile.caveCulling && n === 0 && !s.roof ? { viewKey: s.viewId ?? 0, cam: s.camera.position, dir: s.camera.getWorldDirection(this.tmpDir), ymax: 16 } : null,
          translucentDistance: this.quality.state.profile.translucentDistance,
        });
    }
    this.entities.detailDistance = this.quality.state.profile.entityDetailDistance; // posed models near, boxes beyond
    this.entities.showFor(s.dim, cam, { bots: s.layers.bots, entities: s.layers.entities, labels: s.labels, selected: s.selected, drawDistance: s.drawDistance, fov: s.camera.fov });
    this.overlays.group.visible = s.overlays;
    if (s.overlays) this.showOverlays(s.dim);
    this.flashes.showFor(s.dim, now, s.layers.changes);
    this.blockEntityLayer.showFor(s.dim); // [blocks]
    this.aids.showAt(s.aids ?? null);
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
