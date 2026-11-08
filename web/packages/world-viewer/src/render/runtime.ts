// The shared world runtime: ONE WebGL context, ONE scene, ONE mesh worker, ONE world store (per dimension, keyed (dim, cx, cz)
// with its hash, fetched once whatever number of views or bots need it), ONE BufferGeometry per section pass, ONE set of
// materials and one atlas texture, one entity store and layer, one overlay layer. Views (the interactive viewer, hive-card
// thumbnails) own only a camera, a viewport and per-view uniforms (sky/time, fog centre, x-ray cone, roof cut); everything
// else is shared. Section upkeep and world sync follow the union of every view's interest region, so bots near each other
// share sections. The interactive "primary" view renders straight into the runtime's canvas (placed in its container);
// every other view is rendered into a corner of it and copied into its own 2D canvas in the same frame.

import * as THREE from 'three';
import { createMeshHost, InlineMeshHost, type MeshHost } from '../mesh/host';
import type { ChatSay, Dim, LayerToggles, Overlays, WorldSource } from '../types';
import { normalizeEntities, normalizeOverlays } from '../wire/normalize';
import type { GameVersion } from '../world/blocks';
import { WorldCache, type WorldTimeAnchor } from '../world/cache';
import { EntityStore, type BotView, type EntityView } from '../world/entities';
import { fogColour, precipitation, skyColour, skylightSubtractedSmooth } from '../world/sky';
import { Poller, WorldSync, type Interest } from '../world/sync';
// [blocks] the bundled block atlas + its animation pass
import { meshAssets } from '../atlas/assets';
import { BlockAtlas } from './blockAtlas';
import { BlockEntityLayer } from './blockEntityLayer'; // [blocks] sign text, banners, spawner mobs
import { capBackstop, createChunkMaterials, createChunkUniforms, type ChunkMaterials, type ChunkUniforms } from './chunkMaterial';
import { EntityLayer } from './entityLayer';
import { OverlayLayer } from './overlayLayer';
import { SectionManager, type Centre } from './sections';
import { WeatherLayer } from './weather';
import { SkyLayer } from './sky';
import { AidsLayer } from './aids';
import { BubbleLayer, type BubbleKind, type BubbleMode, type ShownBubble } from './bubbles';
import { loadBundle, type AssetBundle } from '../assets/bundle';
import { PROFILES, QualityController, probeRendererOnce, type QualityName, type QualityProfile, type RendererProbe } from './quality';
import { effectiveProfile, entityReach, FrameLimiter, getViewerQuality, reportViewerStatus, viewerQualityRev, type ViewerQuality } from './viewerQuality';
import { ChangeFlash } from './changeFlash';
import { BlockDeltaFeed } from '../world/deltas';
import { BlockEntityStore } from '../world/blockEntities';
import { HologramLayer } from './holograms';
import { BotBadgeLayer, type BotBadge } from './botBadges';

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
  /** Top-down / iso: the radius of ground the picture shows (drawn at least this far, unfogged; see Centre.cover). */
  cover?: number;
  /** Sections wholly below this height are skipped (a zoomed-out top-down / iso view's cheaper picture); null = none. */
  depthFloor?: number | null;
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
  /** Dim the world below this y (a level cut's lower storeys); null/absent = off. */
  dimBelow?: number | null;
  /** Silhouettes of the bots and players that blocks (leaves included) hide from this view's camera. */
  ghosts?: boolean;
  /** Reading aids around this point (the followed bot's feet), or none. */
  aids?: { x: number; y: number; z: number } | null;
  /** What falls here (biome under the view); default rain. */
  precip?: 'rain' | 'snow' | 'none';
  /** The view's id (per-view caches such as the cave-culling walk). */
  viewId?: number;
  /** Where the picture goes: the runtime canvas itself (primary) or a 2D canvas it is copied into. */
  target: 'primary' | CanvasRenderingContext2D;
  /** Wireframe boxes drawn in this view only (the crosshair's selection box, selected bots, a target marker). */
  outlines?: ReadonlyArray<{ min: [number, number, number]; max: [number, number, number]; color: number }>;
  /** Chat bubbles over speakers: full stacks, one small bubble (thumbnails), or none. Default: full with labels, else compact. */
  bubbles?: BubbleMode;
  /** Render pixels per CSS pixel (bubble sizes are chosen in CSS pixels); default 1. */
  pxScale?: number;
  /** Name labels over bots (default: with `labels`); off when the app draws its own (Story Mode). */
  botLabels?: boolean;
  /** [level cut] Entities whose feet are at or above `y` within `r` of (x, z) are not drawn; those below `dimBelow` are dimmed
   * (Story Mode's storeys: bodies follow the blocks). */
  entityCut?: { x: number; y: number; z: number; r: number; dimBelow: number | null } | null;
  /** [holograms] Screen pixels per block of this view (its holograms switch to outline boxes when tiny); null = near enough. */
  holoPx?: number | null;
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
  /** How far the last primary view drew entities (blocks; top-down / iso: across the ground from its look-at point). */
  entityRadius: number;
}

let nextViewId = 1;
export const newViewId = () => nextViewId++;

/** Silhouettes: how often a view re-tests which bodies are hidden, how far it looks, and the fade (seconds). */
const GHOST_TEST_MS = 200, GHOST_RANGE = 96, GHOST_FADE_S = 0.18;
const NO_OUTLINES: NonNullable<ViewSetup['outlines']> = [];

/** Per view: which bodies the last occlusion test found hidden, and each one's silhouette strength (faded in and out). */
interface GhostView {
  busy: boolean;
  at: number;
  t: number;
  hidden: Set<string>;
  alpha: Map<string, number>;
}

/** Fades each silhouette toward its last test's answer (in over GHOST_FADE_S, out likewise, then forgotten). */
function fadeGhosts(st: GhostView, now: number): void {
  const step = Math.min(0.25, Math.max(0, (now - st.t) / 1000)) / GHOST_FADE_S;
  st.t = now;
  for (const k of st.hidden) if (!st.alpha.has(k)) st.alpha.set(k, 0);
  for (const [k, a] of st.alpha) {
    const want = st.hidden.has(k);
    const v = want ? Math.min(1, a + step) : Math.max(0, a - step);
    if (!want && v <= 0) st.alpha.delete(k);
    else st.alpha.set(k, v);
  }
}

/** Software WebGL (hardware acceleration off) gets its own profile; a forced quality skips the probe. */
function probeFor(opts: RuntimeOptions): RendererProbe {
  return opts.quality && opts.quality !== 'auto' ? { software: opts.quality === 'software', renderer: 'forced', reason: `quality: ${opts.quality}` } : probeRendererOnce();
}

function profileFor(opts: RuntimeOptions, probe: RendererProbe): QualityProfile {
  if (opts.quality && opts.quality !== 'auto') return PROFILES[opts.quality];
  return PROFILES[probe.software ? 'software' : 'gpu'];
}

/** The GPU's name as WebGL reports it (unmasked when the browser allows), 'webgl2' when it can't be read. */
function gpuName(renderer: THREE.WebGLRenderer): string {
  try {
    const gl = renderer.getContext();
    const ext = gl.getExtension('WEBGL_debug_renderer_info');
    return ext ? String(gl.getParameter(ext.UNMASKED_RENDERER_WEBGL)) : String(gl.getParameter(gl.RENDERER));
  } catch {
    return 'webgl2';
  }
}

/** The world time and weather of the view being drawn. */
interface ViewSky {
  nether: boolean;
  time: number | null;
  /** Weather shown (the view's layer and the viewer setting). */
  weather: boolean;
  rain: number;
  thunder: number;
}

export class WorldRuntime {
  readonly renderer: THREE.WebGLRenderer;
  readonly canvas: HTMLCanvasElement;
  readonly scene = new THREE.Scene();
  readonly uniforms: ChunkUniforms;
  readonly materials: ChunkMaterials;
  /** The cut cap's world-floor backstop (follows the drawn view's camera). */
  private readonly backstop: THREE.Mesh;
  readonly host: MeshHost;
  cache = new WorldCache();
  sync: WorldSync;
  /** Per view: the world time and sky light subtraction of its last frame. */
  private readonly lastSky = new Map<number, { time: number | null; skySub: number }>();
  readonly store = new EntityStore();
  readonly entities = new EntityLayer();
  readonly overlays = new OverlayLayer();
  readonly weather = new WeatherLayer();
  readonly flashes = new ChangeFlash();
  readonly sky = new SkyLayer();
  readonly aids = new AidsLayer();
  /** Chat bubbles over the heads of bots and players that speak (`say`, `WorldSource.chat`), in every view. */
  readonly bubbles = new BubbleLayer();
  private chatOff: (() => void) | null = null;
  /** Block entities of the held columns (sign text, banners, spawner mobs… for the main-thread layers). */
  blockEntities: BlockEntityStore;
  /** The bundled assets of the current version (textures, models); null until loaded. */
  bundle: AssetBundle | null = null;
  deltas: BlockDeltaFeed;
  private readonly sections = new Map<Dim, SectionManager>();
  private readonly views = new Map<number, RuntimeView>();
  /** [holograms] Each view's build holograms (only that view draws them). */
  private readonly holoViews = new Map<number, HologramLayer>();
  private readonly badgeViews = new Map<number, BotBadgeLayer>();
  /** Block changes as they are applied (dim, the changed cells). */
  readonly blockListeners = new Set<(dim: Dim, blocks: Array<[number, number, number]>) => void>();
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
  /** A bot or player within `r` of a point this frame (spawners spin only then). */
  private readonly playerNear = (dim: number, x: number, y: number, z: number, r: number): boolean => {
    const r2 = r * r;
    for (const b of this.botsBuf) if (b.dim === dim && (b.x - x) ** 2 + (b.y - y) ** 2 + (b.z - z) ** 2 < r2) return true;
    for (const e of this.entsBuf) if (e.kind === 'player' && e.dim === dim && (e.x - x) ** 2 + (e.y - y) ** 2 + (e.z - z) ** 2 < r2) return true;
    return false;
  };
  readonly quality: QualityController;
  readonly probe: RendererProbe;
  /** The detected (or forced) profile before the user's viewer settings are laid over it. */
  private baseProfile: QualityProfile;
  /** The viewer settings applied (re-read when the store's revision moves). */
  private q: Readonly<ViewerQuality> = getViewerQuality();
  private qRev = -1;
  private readonly limiter = new FrameLimiter();
  private readonly frustum = new THREE.Frustum();
  private readonly projView = new THREE.Matrix4();
  /** Primary renders counted for the reported fps. */
  private statusFrames = 0;
  private statusAt = 0;
  private statusFps = 0;
  private entityRadius = 0;
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
    this.probe = probeFor(opts);
    const profile = profileFor(opts, this.probe);
    this.baseProfile = profile;
    this.quality = new QualityController(profile);
    this.renderer = new THREE.WebGLRenderer({ antialias: (opts.antialias ?? true) && profile.antialias, powerPreference: 'high-performance', preserveDrawingBuffer: false });
    this.renderer.setPixelRatio(1);
    this.renderer.setScissorTest(true);
    this.canvas = this.renderer.domElement;
    this.canvas.tabIndex = 0;
    Object.assign(this.canvas.style, { display: 'block', width: '100%', height: '100%', outline: 'none', touchAction: 'none' });
    this.gpu = gpuName(this.renderer);
    this.scene.background = new THREE.Color(0x78a7ff);
    this.scene.add(new THREE.HemisphereLight(0xffffff, 0x555566, 2.2));
    const sun = new THREE.DirectionalLight(0xffffff, 1.2);
    sun.position.set(0.4, 1, 0.25);
    this.scene.add(sun);
    this.uniforms = createChunkUniforms();
    this.materials = createChunkMaterials(this.uniforms);
    this.applyQuality(); // the user's viewer settings over the profile
    this.backstop = capBackstop(this.materials.cap);
    this.scene.add(this.backstop);
    this.entities.setWorldUniforms(this.uniforms); // [entity models] entities light and fog like the sections
    this.host = opts.inlineMesher ? new InlineMeshHost() : createMeshHost(opts.createWorker);
    this.scene.add(this.aids.group, this.sky.group, this.entities.group, this.overlays.group, this.weather.object, this.flashes.mesh, this.bubbles.group);
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
    this.listenChat();
    void this.configure();
    // The network loops start with the first view (and pause when the last one leaves).
    this.paused = true;
    this.raf = requestAnimationFrame(this.tick);
    // A hidden tab draws nothing, so it fetches nothing either: over plain HTTP/1.1 every background tab's polling would hold
    // one of the browser's six connections to the host that the visible tab needs (page navigations included).
    if (typeof document !== 'undefined') {
      this.hidden = document.visibilityState === 'hidden';
      document.addEventListener('visibilitychange', this.onVisibility);
    }
  }

  private hidden = false;

  private readonly onVisibility = (): void => {
    this.hidden = document.visibilityState === 'hidden';
    if (this.hidden) this.pause();
    else if (this.views.size) this.resume();
  };

  // ------------------------------------------------------------------------------------------------------------ chat

  /**
   * A speech bubble over the bot or player called `name` (bot name or short key, or a username; any case) in every view:
   * stacked with that speaker's last few lines, fading after ~6 s. Nothing shows while nobody of that name is in the world.
   * False when the line was ignored (empty, or the speaker's newest line again within 2 s, e.g. heard by several bots).
   */
  say(name: string, text: string, opts: { kind?: BubbleKind } = {}): boolean {
    return this.bubbles.say(name, text, opts.kind ?? 'chat', performance.now());
  }

  /** The chat bubbles a view drew in its last render (speaker, anchor in CSS pixels, lines). */
  bubblesShown(view: RuntimeView | number): readonly ShownBubble[] {
    return this.bubbles.shownIn(typeof view === 'number' ? view : view.id);
  }

  private listenChat(): void {
    this.chatOff?.();
    this.chatOff = null;
    const chat = this.source.chat?.bind(this.source);
    if (chat) this.chatOff = chat((l: ChatSay) => void this.say(l.from, l.text, { kind: l.kind }));
    this.listenEntities();
  }

  private entitiesOff: (() => void) | null = null;

  /** A push source (the shared stream's `entities` frames): each frame is read at once, so bodies glide at the stream's pace
   * instead of the beat of two 200 ms loops (the bridge's poll and ours); the poll slows to a backstop. */
  private listenEntities(): void {
    this.entitiesOff?.();
    this.entitiesOff = null;
    const on = this.source.onEntities?.bind(this.source);
    this.entityPoller.periodMs = on ? (this.opts.entityPollMs ?? 1000) : (this.opts.entityPollMs ?? 200);
    if (on) this.entitiesOff = on(() => this.entityPoller.kick());
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
    if (this.paused && !this.hidden) this.resume();
    this.overlayPoller.kick();
  }

  /** [holograms] The view's hologram layer (made and added to the scene on first use). */
  holograms(viewId: number, dim: Dim = 0): HologramLayer {
    let h = this.holoViews.get(viewId);
    if (!h) {
      h = new HologramLayer(this.host, this.uniforms, dim);
      h.group.visible = false;
      this.scene.add(h.group);
      this.holoViews.set(viewId, h);
    }
    return h;
  }

  /** [holograms] The view's layer, if it has one. */
  hologramsOf(viewId: number): HologramLayer | null {
    return this.holoViews.get(viewId) ?? null;
  }

  /** Per-view activity badges, sharing bot positions and the terrain depth buffer. */
  setBotBadges(viewId: number, badges: readonly BotBadge[]): void {
    let layer = this.badgeViews.get(viewId);
    if (!layer && !badges.length) return;
    if (!layer) {
      layer = new BotBadgeLayer();
      layer.group.visible = false;
      this.badgeViews.set(viewId, layer);
      this.scene.add(layer.group);
    }
    layer.set(badges);
  }

  removeView(v: RuntimeView): void {
    this.views.delete(v.id);
    this.holoViews.get(v.id)?.dispose();
    this.holoViews.delete(v.id);
    this.badgeViews.get(v.id)?.dispose();
    this.badgeViews.delete(v.id);
    this.ghostViews.delete(v.id);
    // No views left (e.g. between pages, persistent runtime): stop the network loops, keep the world.
    if (!this.views.size) this.pause();
    this.renderedAt.delete(v.id);
    this.bubbles.forgetView(v.id);
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
    this.listenChat();
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
    this.listenChat();
    this.bubbles.clear();
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
    if (typeof document !== 'undefined') document.removeEventListener('visibilitychange', this.onVisibility);
    cancelAnimationFrame(this.raf);
    this.sync.stop();
    this.deltas.stop();
    this.entityPoller.stop();
    this.overlayPoller.stop();
    this.views.clear();
    for (const h of this.holoViews.values()) h.dispose();
    this.holoViews.clear();
    for (const b of this.badgeViews.values()) b.dispose();
    this.badgeViews.clear();
    this.chatOff?.();
    this.chatOff = null;
    this.entitiesOff?.();
    this.entitiesOff = null;
    this.bubbles.dispose();
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
    this.backstop.geometry.dispose();
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

  /**
   * Anchors the world time of `dim` from another feed of the server's clock (Story Mode's colony state): `ticks` received at
   * `atMs` (Date.now() ms). The newer of this and the index replies' time wins; `frozen` = the daylight cycle is stopped.
   */
  setWorldTime(dim: Dim, ticks: number, atMs: number, frozen = false): void {
    this.cache.setTime(dim, ticks, atMs, frozen);
  }

  /** The world time anchor of `dim` the sky and light follow (null = unknown). */
  worldTimeAnchor(dim: Dim): WorldTimeAnchor | null {
    return this.cache.timeAnchor(dim);
  }

  /** The sky light taken off (0 noon … 11 midnight) and the world time of a view's last frame (tests, debug hooks). */
  skyOf(viewId: number): { time: number | null; skySub: number } | null {
    return this.lastSky.get(viewId) ?? null;
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
      entityRadius: this.entityRadius,
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
      onChanged: (dim, blocks) => {
        this.flashes.add(dim, blocks, performance.now());
        for (const h of this.holoViews.values()) h.blocksChanged(dim, blocks);
        for (const l of this.blockListeners) l(dim, blocks);
      },
      onBlockEntities: (dim, cx, cz) => this.blockEntities.want(dim, [[cx, cz]]),
      onError: (e) => this.opts.onError?.('blocks', e),
    });
  }

  // [blocks] -------------------------------------------------------------------------------------------------------
  /** (Re)configures the mesher when the version changed: flat at once, then textured when the version's bundle has loaded
   * (the bundled atlas replaces the bridge's; `source.atlas` is not read). Every section is meshed again. */
  private async configure(): Promise<void> {
    const c = this.meshConfig();
    const { v, ready, fastLeaves } = c;
    // While the version's textures load, sections wait instead of meshing flat first and textured again.
    this.setMeshHold(c.want && !ready);
    if (c.key === this.configKey) return;
    this.configKey = c.key;
    this.version = v;
    // The version's bundled assets: sky and entities here, the block atlas in loadBlockAtlas (one fetch, cached).
    this.loadSkyBundle(v);
    this.loadEntityBundle(v); // [entity models]
    this.uniforms.uTextured.value = ready ? 1 : 0;
    await this.host.configure(v, ready ? meshAssets(this.blockAtlas!.bundle.pack) : null, ready, fastLeaves);
    for (const s of this.sections.values()) s.clear();
    for (const h of this.holoViews.values()) h.refresh(); // [holograms] the new atlas cells
    if (c.want && !ready && this.atlasFor !== v) void this.loadBlockAtlas(v);
  }

  /** The mesher configuration the world calls for: its version, textures wanted and ready, leaves, and their key. */
  private meshConfig(): { v: GameVersion; want: boolean; ready: boolean; fastLeaves: boolean; key: string } {
    const v = this.cache.detectedVersion() ?? 'b1.8.1';
    const want = this.opts.textures !== false;
    const ready = want && this.blockAtlas?.bundle.version === v;
    // [blocks] Fast (opaque, mutually culled) leaves on request only: `npm run perf` showed no measurable gain under
    // SwiftShader on the dense fixture (within run-to-run noise), so the software profile keeps fancy leaves.
    const fastLeaves = ready && this.opts.fastLeaves === true;
    const key = `${v}|${ready ? 'atlas' : 'flat'}|${fastLeaves ? 'fast' : 'fancy'}`;
    return { v, want, ready, fastLeaves, key };
  }

  private loadSkyBundle(v: GameVersion): void {
    loadBundle(v)
      .then((b) => {
        if (this.disposed || this.version !== v) return;
        this.bundle = b;
        this.sky.setBundle(b);
      })
      .catch((e) => this.opts.onError?.('assets', e));
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
      if (d !== null) dims.add(d);
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
    this.applyQuality();
    // The fps cap skips whole frames (upkeep included); the next drawn frame's dt spans the skipped ones.
    if (!this.limiter.due(now, this.q.fpsCap)) return;
    const dt = Math.min(0.1, this.last ? (now - this.last) / 1000 : 0);
    this.last = now;
    this.step(now, dt);
  };

  /** One frame: shared upkeep, then every due view (the primary last, so its picture is what the canvas shows). */
  step(now: number, dt: number): void {
    this.applyQuality();
    this.updateBodies(now);
    this.blockEntities.tick(now);
    this.updateBlockEntityLayer(now);
    void this.deltas.flush(); // a frame's worth of block changes, applied together
    this.animateAtlas(now);
    this.maintainSections(now);
    this.renderViews(now, dt);
  }

  /** Entities, overlays, bubbles and entity light; entity events animate (swings, hurt flashes, deaths…), lightning also
   * flashes the world. */
  private updateBodies(now: number): void {
    const selected = new Set<string>();
    for (const v of this.views.values()) for (const s of v.selectedBots()) selected.add(s);
    this.entities.update(this.store.bots(this.botsBuf), this.store.entities(null, this.entsBuf), now, selected);
    this.overlays.tick(now);
    this.bubbles.update(now, this.botsBuf, this.entsBuf);
    this.refreshEntityLight(now);
    const events = this.store.drainEvents();
    if (!events.length) return;
    for (const ev of events) if (ev.kind === 'lightning') this.lightningFlash = 0.7;
    this.entities.onEvents?.(events, now);
  }

  /** [blocks] The block-entity layer around the primary view (else the first view): sign text, banners, spawner mobs. */
  private updateBlockEntityLayer(now: number): void {
    const v = this.primary?.view ?? this.views.values().next().value;
    const c = v?.centre();
    if (!c) return;
    this.beCentre.set(c.x, c.y, c.z);
    this.blockEntityLayer.update(this.blockEntities, {
      dim: c.dim,
      centre: this.beCentre,
      software: this.quality.state.profile.name === 'software',
      now,
      ticks: this.cache.timeNow(c.dim, Date.now()) ?? now / 50,
      displayMobs: this.entities,
      playerNear: this.playerNear,
    });
  }

  /** [blocks] Atlas animation: the profile's tick rate and cell budget, only the cells the drawn sections sample. Animated
   * textures off (viewer settings): the atlas keeps the frame it shows. */
  private animateAtlas(now: number): void {
    if (!(this.uniforms.uTextured.value > 0 && this.blockAtlas && this.q.animatedTextures)) return;
    const p = this.quality.state.profile;
    this.blockAtlas.animHz = p.animHz;
    this.blockAtlas.animCellsPerTick = p.animCellsPerTick;
    this.blockAtlas.starLayers = 17; // vanilla's own distance-based layer count on every profile
    const cells = new Set<number>();
    for (const s of this.sections.values()) s.visibleAnimCells(cells);
    this.blockAtlas.setVisible(cells);
    this.blockAtlas.update(this.renderer, now);
  }

  /** World sync follows every view's interest; sections are kept around every view's centre (per dimension). */
  private maintainSections(now: number): void {
    const interests: Record<string, Interest> = {};
    const centres = new Map<Dim, Centre[]>();
    for (const v of this.views.values()) {
      const i = v.interest();
      if (i) interests[`v${v.id}`] = i;
      const c0 = v.centre();
      if (!c0) continue;
      const c = { ...c0, distance: Math.max(c0.cover ?? 0, this.quality.drawDistance(c0.distance)) };
      const list = centres.get(c.dim) ?? [];
      list.push(c);
      centres.set(c.dim, list);
    }
    this.sync.setInterests(interests);
    for (const [dim, cs] of centres) this.sectionsFor(dim).maintain(cs, now);
  }

  /** Every view that wants a picture this frame, most overdue first and the primary last. */
  private renderViews(now: number, dt: number): void {
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
    if (!primarySetup) return;
    this.render(primarySetup, now);
    if (timed) this.sampleFrame(now, t0);
  }

  /** Once a second the whole frame is timed to completion (gl.finish): CPU and GPU (or the software rasteriser) together,
   * so a throttled background tab (slow rAF, cheap frames) never lowers the quality. */
  private sampleFrame(now: number, t0: number): void {
    // gl.finish returns early on ANGLE/SwiftShader; reading one pixel back really waits for the frame to be drawn.
    const gl = this.renderer.getContext();
    gl.readPixels(0, 0, 1, 1, gl.RGBA, gl.UNSIGNED_BYTE, this.pixel);
    this.quality.sample(now, performance.now() - t0);
  }

  /** After a primary render: the page-wide viewer status (fps over half-second windows, the levers, the entity reach). */
  private report(now: number, s: ViewSetup, entityRadius: number): void {
    this.entityRadius = entityRadius;
    this.statusFrames++;
    if (!this.statusAt) this.statusAt = now;
    if (now - this.statusAt >= 500) {
      this.statusFps = (this.statusFrames * 1000) / (now - this.statusAt);
      this.statusFrames = 0;
      this.statusAt = now;
    }
    const st = this.quality.state;
    reportViewerStatus({
      fps: this.statusFps,
      frameMs: st.smoothMs,
      scale: st.scale,
      drawFactor: st.drawFactor,
      profile: st.profile.name,
      software: this.probe.software,
      renderer: this.gpu,
      entities: this.entities.instances,
      entityRadius,
      drawDistance: s.drawDistance,
      at: now,
    });
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
    this.baseProfile = PROFILES[q === 'auto' ? (this.probe.software ? 'software' : 'gpu') : q];
    this.qRev = -1;
    this.applyQuality();
  }

  /** The viewer settings in force (what the last frame applied). */
  get viewerQuality(): Readonly<ViewerQuality> {
    return this.q;
  }

  /**
   * Lays the user's viewer settings (`setViewerQuality`) over the base profile when they changed: draw distance cap, render
   * scale, entity model distance, clouds, and whether the controller may move its levers (off: full resolution and distance).
   * Runs at the start of every frame; a no-op unless the store's revision moved.
   */
  applyQuality(): void {
    const rev = viewerQualityRev();
    if (rev === this.qRev) return;
    this.qRev = rev;
    this.q = getViewerQuality();
    const c = this.quality;
    c.setProfile(effectiveProfile(this.baseProfile, this.q));
    c.enabled = this.opts.adaptiveResolution !== false && this.q.adaptive;
    if (!c.enabled) {
      c.state.scale = 1;
      c.state.drawFactor = 1;
    }
    this.uniforms.uAo.value = this.q.ambientOcclusion ? 1 : 0;
  }

  private readonly outlineBoxes: THREE.LineSegments[] = [];

  /** Positions this view's wireframe boxes (a small pool of unit-cube edges, scaled; hidden when unused). */
  private showOutlines(list: NonNullable<ViewSetup['outlines']>): void {
    for (let i = 0; i < Math.max(list.length, this.outlineBoxes.length); i++) {
      const o = list[i];
      let box = this.outlineBoxes[i];
      if (!o) {
        if (box) box.visible = false;
        continue;
      }
      if (!box) {
        const geo = new THREE.EdgesGeometry(new THREE.BoxGeometry(1, 1, 1).translate(0.5, 0.5, 0.5));
        box = new THREE.LineSegments(geo, new THREE.LineBasicMaterial({ color: 0x000000, transparent: true, opacity: 0.8, depthTest: true }));
        box.renderOrder = 10;
        box.frustumCulled = false;
        this.outlineBoxes.push(box);
        this.scene.add(box);
      }
      box.visible = true;
      box.position.set(o.min[0], o.min[1], o.min[2]);
      box.scale.set(o.max[0] - o.min[0], o.max[1] - o.min[1], o.max[2] - o.min[2]);
      (box.material as THREE.LineBasicMaterial).color.setHex(o.color);
    }
  }

  /** Per view: which bodies the last occlusion test found hidden, and each one's silhouette strength (faded in and out). */
  private readonly ghostViews = new Map<number, GhostView>();

  /** The view's silhouette strengths. The worker ray-tests every bot and player near the camera a few times a second
   * (opaque blocks and leaves hide, plants and glass don't); the ghost pass itself is per pixel (depth GREATER), so a stale
   * answer only decides who may get one, never where it shows. */
  private ghostsFor(s: ViewSetup, now: number): Map<string, number> {
    const st = this.ghostView(s.viewId ?? 0, now);
    if (!st.busy && now - st.at >= GHOST_TEST_MS) this.testGhosts(st, s, now);
    fadeGhosts(st, now);
    return st.alpha;
  }

  private ghostView(id: number, now: number): GhostView {
    let g = this.ghostViews.get(id);
    if (!g) {
      g = { busy: false, at: -Infinity, t: now, hidden: new Set(), alpha: new Map() };
      this.ghostViews.set(id, g);
    }
    return g;
  }

  /** Asks the worker which bots and players near the camera blocks hide (answered a frame or more later). */
  private testGhosts(st: GhostView, s: ViewSetup, now: number): void {
    st.at = now;
    const cam = s.camera.position;
    const cands = s.layers.bots || s.layers.entities ? this.entities.ghostCandidates(s.dim, cam, Math.min(s.drawDistance, GHOST_RANGE)) : [];
    if (!cands.length) {
      st.hidden.clear();
      return;
    }
    st.busy = true;
    this.host
      .occluded(s.dim, [cam.x, cam.y, cam.z], cands.flatMap((c) => [c.x, c.y, c.z]))
      .then((r) => {
        st.hidden = new Set(cands.filter((_, i) => r[i]).map((c) => c.key));
      })
      .catch(() => undefined)
      .finally(() => {
        st.busy = false;
      });
  }

  /** Draws one view: the shared scene sized, lit, cut and filtered for it, then copied out when it is not the primary. */
  private render(setup: ViewSetup, now: number): void {
    const primary = setup.target === 'primary';
    // The controller's draw distance (software rendering, slow frames) applies to the interactive view.
    // A top-down / iso view's visible ground is drawn whatever the lever says (the render scale gives way instead).
    const cover = setup.cover ?? 0;
    const plainDistance = primary ? this.quality.drawDistance(setup.drawDistance) : setup.drawDistance;
    const s = { ...setup, drawDistance: Math.max(cover, plainDistance) };
    // Entities (viewer settings): as far as the ground by default, measured across it from the look-at point in top-down /
    // iso views (far ones are cheap instanced boxes, frustum-culled and capped in number); or the user's own distance.
    const reach = entityReach(this.q, cover, s.drawDistance);
    this.fitViewport(s, primary);
    const sky = this.skyState(s);
    this.applySky(s, cover, sky);
    this.applyWeather(s, primary, now, sky);
    const cones = this.applyCutaways(s);
    this.showSections(s, cover, cones);
    this.showBodies(s, now, reach.radius, reach.fromCentre);
    this.showViewLayers(s, now);
    this.showBubbles(s, this.vpH);
    this.draw(s, primary, now, reach.radius);
  }

  /** The drawn size of the view in the canvas (set by fitViewport). */
  private vpW = 1;
  private vpH = 1;

  /** Sizes the canvas for a view: the primary owns it; other views use its bottom-left corner (the canvas grows when nobody
   * owns it). Sets the camera's aspect and the starfield's viewport to the drawn size (`vpW` × `vpH`). */
  private fitViewport(s: ViewSetup, primary: boolean): void {
    const r = this.renderer;
    const cw = this.canvas.width, ch = this.canvas.height;
    if (primary) {
      if (cw !== s.width || ch !== s.height) r.setSize(s.width, s.height, false);
    } else if (!this.primary && (cw < s.width || ch < s.height)) r.setSize(Math.max(cw, s.width), Math.max(ch, s.height), false);
    const w = Math.min(s.width, this.canvas.width), h = Math.min(s.height, this.canvas.height);
    this.vpW = w;
    this.vpH = h;
    s.camera.aspect = w / h;
    s.camera.updateProjectionMatrix();
    this.uniforms.uViewport.value.set(w, h); // [blocks] the starfield's screen-space projection
  }

  /** The world time and weather of the view being drawn (one record, reused every view). */
  private readonly viewSky: ViewSky = { nether: false, time: null, weather: false, rain: 0, thunder: 0 };

  private skyState(s: ViewSetup): ViewSky {
    const st = this.viewSky;
    st.nether = s.dim === -1;
    st.time = this.cache.timeNow(s.dim, Date.now());
    const wx = this.cache.weatherIn(s.dim);
    st.weather = s.layers.weather && this.q.weather;
    st.rain = st.weather ? wx.rain : 0;
    st.thunder = st.weather ? wx.thunder : 0;
    return st;
  }

  /** World uniforms for this view: sky light, sky and fog colours, the fog range, the staleness tint. */
  private applySky(s: ViewSetup, cover: number, st: ViewSky): void {
    const u = this.uniforms;
    const { nether, time, rain, thunder } = st;
    u.uSkySub.value = nether || time === null ? 0 : skylightSubtractedSmooth(time, rain, thunder);
    this.lastSky.set(s.viewId ?? -1, { time, skySub: u.uSkySub.value });
    u.uBase.value = nether ? 0.1 : 0;
    const sky = skyColour(time, rain, thunder, nether), fog = fogColour(time, rain, thunder, nether);
    (this.scene.background as THREE.Color).setRGB(sky[0], sky[1], sky[2], THREE.SRGBColorSpace);
    u.uFogColor.value.setRGB(fog[0], fog[1], fog[2]);
    u.uFogCentre.value.copy(s.centre);
    // Fog starts past the picture's own ground in top-down / iso views (in the others cover is 0: the usual 72 %).
    // Fog off (viewer settings): pushed out of reach, so the drawn world simply ends.
    const fogStart = this.q.fog ? Math.max(s.drawDistance * 0.72, cover) : 1e7;
    u.uFogRange.value.set(fogStart, Math.max(s.drawDistance, fogStart + 16));
    u.uStale.value = s.layers.staleness ? 1 : 0;
  }

  /** Clouds and stars, rain or snow around the primary view's camera, and the lightning flash. */
  private applyWeather(s: ViewSetup, primary: boolean, now: number, st: ViewSky): void {
    const { nether, time, weather, rain, thunder } = st;
    this.sky.update(s.camera.position, time, rain, nether, s.layers.weather && this.quality.state.profile.clouds, now, s.camera.far * 0.8);
    const falls = weather && !nether;
    if (primary) this.flash = this.weather.update(s.camera.position, now / 1000, rain, thunder, falls, s.precip ?? 'rain');
    this.weather.object.visible = primary && falls && rain > 0.01;
    this.lightningFlash = Math.max(0, this.lightningFlash - 0.03);
    this.uniforms.uFlash.value = (primary ? this.flash : 0) + this.lightningFlash;
  }

  /** Per-view cut-aways (uniforms only; the cap pass shows what they open). Returns the number of x-ray cones in use. */
  private applyCutaways(s: ViewSetup): number {
    const u = this.uniforms;
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
    u.uDimBelow.value = s.dimBelow ?? -1e9;
    this.materials.cap.visible = n > 0 || !!s.roof;
    this.backstop.position.set(Math.round(cam.x), 0, Math.round(cam.z));
    this.backstop.updateMatrixWorld();
    return n;
  }

  /** Shared scene, this view's slice of it: the sections of the view's dimension around its centre. */
  private showSections(s: ViewSetup, cover: number, cones: number): void {
    const profile = this.quality.state.profile;
    for (const [dim, sec] of this.sections) {
      sec.group.visible = dim === s.dim;
      if (dim !== s.dim) continue;
      sec.showAround(s.centre.x, s.centre.z, s.drawDistance, {
        // Cave culling, unless a cut-away is opening the rock for this view.
        cull: profile.caveCulling && cones === 0 && !s.roof ? { viewKey: s.viewId ?? 0, cam: s.camera.position, dir: s.camera.getWorldDirection(this.tmpDir), ymax: 16 } : null,
        translucentDistance: Math.max(cover, profile.translucentDistance),
        floorY: s.depthFloor ?? null,
      });
    }
  }

  /** Bots, players, mobs and drops this view shows (their labels and silhouettes too), culled to its frustum. */
  private showBodies(s: ViewSetup, now: number, radius: number, fromCentre: boolean): void {
    const q = this.q;
    this.entities.detailDistance = this.quality.state.profile.entityDetailDistance; // posed models near, boxes beyond
    const ghosts = s.ghosts && q.xray ? this.ghostsFor(s, now) : null;
    const labels = s.labels && q.labels;
    s.camera.updateMatrixWorld();
    this.frustum.setFromProjectionMatrix(this.projView.multiplyMatrices(s.camera.projectionMatrix, s.camera.matrixWorldInverse));
    this.entities.showFor(s.dim, s.camera.position, {
      bots: s.layers.bots,
      entities: s.layers.entities,
      labels,
      botLabels: labels && s.botLabels !== false,
      cut: s.entityCut ?? null,
      selected: s.selected,
      drawDistance: radius,
      centre: fromCentre ? s.centre : null,
      drops: q.drops,
      frustum: this.frustum,
      fov: s.camera.fov,
      ghosts,
    });
  }

  /** After entities and per-view badges: the visible labels decide where speech bubbles sit. */
  private showBubbles(s: ViewSetup, h: number): void {
    const fallback = s.labels ? 'full' : 'compact';
    const mode = this.q.bubbles ? (s.bubbles ?? fallback) : 'off';
    const badges = this.badgeViews.get(s.viewId ?? 0);
    const labelLift = (key: string, bot: boolean) => Math.max(this.entities.labelLift(key, bot), bot ? badges?.labelLift(key) ?? 0 : 0);
    this.bubbles.showFor(
      { dim: s.dim, camera: s.camera, height: h, pxScale: s.pxScale ?? 1, mode, bots: s.layers.bots, players: s.layers.entities, labelLift },
      s.viewId ?? 0,
    );
  }

  /** The view's overlays, change flashes, block entities, its own holograms, reading aids and outline boxes. */
  private showViewLayers(s: ViewSetup, now: number): void {
    this.overlays.group.visible = s.overlays;
    if (s.overlays) this.showOverlays(s.dim);
    this.flashes.showFor(s.dim, now, s.layers.changes);
    this.blockEntityLayer.showFor(s.dim); // [blocks]
    for (const [id, h] of this.holoViews) {
      h.group.visible = this.q.holograms && id === s.viewId;
      if (h.group.visible) h.show(s.holoPx ?? null);
    }
    for (const [id, badges] of this.badgeViews) {
      badges.group.visible = id === s.viewId && s.layers.bots && s.labels && this.q.labels;
      if (badges.group.visible) badges.show(s.camera, s.dim, this.vpH / Math.max(0.01, s.pxScale ?? 1), s.drawDistance, (name) => this.store.bot(name));
    }
    this.aids.showAt(s.aids ?? null);
    this.showOutlines(s.outlines ?? NO_OUTLINES);
  }

  /** Renders the scene into the view's corner of the canvas; a secondary view's picture is copied into its own canvas. */
  private draw(s: ViewSetup, primary: boolean, now: number, radius: number): void {
    const r = this.renderer;
    const w = this.vpW, h = this.vpH;
    r.setViewport(0, 0, w, h);
    r.setScissor(0, 0, w, h);
    r.render(this.scene, s.camera);
    if (primary) this.report(now, s, radius);
    if (s.target !== 'primary') {
      const ctx = s.target;
      ctx.drawImage(this.canvas, 0, this.canvas.height - h, w, h, 0, 0, ctx.canvas.width, ctx.canvas.height);
    }
  }
}
