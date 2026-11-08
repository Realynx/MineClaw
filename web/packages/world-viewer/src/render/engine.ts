// The interactive view: cameras (follow / fly / iso), input, picking, x-ray decisions and its layer toggles, drawn through a
// WorldRuntime (its own, or the one a WorldViewerProvider shares with the hive-card thumbnails). It owns no world data and
// no GPU resources: only a camera, a viewport and per-view uniforms.

import * as THREE from 'three';
import type { V3 } from '../mesh/query';
import {
  DEFAULT_LAYERS,
  type CameraMode,
  type Dim,
  type LayerToggles,
  type Overlays,
  type PickedBlock,
  type ViewerStats,
  type WorldSource,
  type XRayMode,
} from '../types';
import { blockName } from '../world/blocks';
import type { BotView } from '../world/entities';
import type { Interest } from '../world/sync';
import { aimAt, outlineOf, type AimTarget, type AimRay } from './aim';
import type { BubbleKind, ShownBubble } from './bubbles';
import type { BotBadge } from './botBadges';
import { CameraRig, lookDir, type RigInput } from './cameraRig';
import { newViewId, WorldRuntime, type RuntimeOptions, type RuntimeView, type ViewSetup } from './runtime';
import type { HoloCell, HologramStats } from './holograms';
import type { Centre } from './sections';

export interface EngineOptions extends RuntimeOptions {
  source: WorldSource;
  /** Draw through this shared runtime instead of creating one (WorldViewerProvider passes it). */
  runtime?: WorldRuntime;
  dim?: Dim;
  follow?: string | null;
  layers?: Partial<LayerToggles>;
  cameraMode?: CameraMode;
  /** Editor: cursor-first orbit (MMB), pan (Shift+MMB), and wheel dolly. Never captures the mouse with pointer lock. */
  controls?: 'default' | 'editor' | 'controller';
  xray?: XRayMode;
  /** Blocks (32..256): the most this view ever draws; the viewer settings' draw distance applies below it. Absent = the setting. */
  drawDistance?: number;
  onSelectBot?: (name: string) => void;
  onPick?: (block: PickedBlock) => void;
  onStats?: (s: ViewerStats) => void;
  onNotes?: (notes: string[]) => void;
  onCameraModeChange?: (m: CameraMode) => void;
  /** What the locked freecam crosshair or editor cursor points at, whenever it changes (null = nothing or inactive). */
  onAim?: (t: AimTarget | null) => void;
  /** A left click with the mouse locked, on what the crosshair points at. Without it the click picks as before. */
  onAimClick?: (t: AimTarget | null, mods: { shift: boolean; ctrl: boolean; button?: number }) => void;
  /** Adds application plan cells to the center-ray pick, without changing the physical world. */
  resolveAim?: (physical: AimTarget | null, ray: AimRay) => AimTarget | null;
  /** Application camera input, applied before this frame's camera matrices are computed. */
  onBeforeFrame?: (engine: WorldViewerEngine, now: number, dt: number) => void;
  /** Application overlays, using the exact camera matrices just rendered (including viewport fitting). */
  onAfterFrame?: (engine: WorldViewerEngine, now: number, dt: number) => void;
}

const XRAY_RANGE = 48; // FreecamXRay.Range

export class WorldViewerEngine implements RuntimeView {
  readonly id = newViewId();
  readonly runtime: WorldRuntime;
  readonly camera = new THREE.PerspectiveCamera(70, 1, 0.05, 2000);
  readonly rig = new CameraRig();
  /** The element input binds to: the runtime canvas (primary) or this view's own 2D canvas (copy mode). */
  readonly surface: HTMLCanvasElement;
  private readonly ctx2d: CanvasRenderingContext2D | null = null;
  private readonly ownsRuntime: boolean;
  private opts: EngineOptions;
  private dim: Dim;
  private followName: string | null;
  private following = true;
  private acquired = false;
  private layers: LayerToggles;
  private xray: XRayMode;
  /** The view's own cap (the drawDistance prop), null = the viewer settings' distance alone. */
  private drawCap: number | null;
  private keys = new Set<string>();
  private inputPaused = false;
  private drag: { x: number; y: number; t: number; button: number; moved: boolean } | null = null;
  private editorDrag: { x: number; y: number; pointerId: number } | null = null;
  private frames = 0;
  private fpsAt = 0;
  private fps = 0;
  private frameMs = 0;
  private xrayAt = 0;
  private xrayBusy = false;
  private coneOn = false;
  private roof: ViewSetup['roof'] = null;
  private precip: 'rain' | 'snow' | 'none' = 'rain';
  private overlaysSeen: Overlays | null = null;
  private disposed = false;
  private resize: ResizeObserver | null = null;
  private readonly cleanups: Array<() => void> = [];
  private size: [number, number] = [1, 1];
  private aimed: AimTarget | null = null;
  private aimAtMs = 0;
  private aimBusy = false;
  private aimRequest = 0;
  private pointerAt: [number, number] | null = null;
  private marker: [number, number, number] | null = null;
  private highlighted: string[] = [];
  private levelCut: { y: number; r: number; dimBelow: number | null } | null = null;
  private botLabels = true;
  private frameNow = 0;
  private frameDt = 0;
  private readonly afterRender = () => this.opts.onAfterFrame?.(this, this.frameNow, this.frameDt);

  constructor(
    private readonly container: HTMLElement,
    opts: EngineOptions,
  ) {
    this.opts = opts;
    this.dim = opts.dim ?? 0;
    this.followName = opts.follow ?? null;
    this.layers = { ...DEFAULT_LAYERS, ...opts.layers };
    this.xray = opts.xray ?? 'auto';
    this.drawCap = opts.drawDistance ?? null;
    if (opts.cameraMode) this.rig.mode = opts.cameraMode;
    if (opts.controls === 'editor') this.rig.mode = 'follow';
    this.ownsRuntime = !opts.runtime;
    this.runtime = (opts.runtime ?? new WorldRuntime(opts.source, opts)).retain();
    if (this.runtime.attachPrimary(this, container)) this.surface = this.runtime.canvas;
    else {
      // Another interactive view owns the shared canvas: draw into our own 2D canvas (same world, same meshes).
      this.surface = document.createElement('canvas');
      this.surface.tabIndex = 0;
      Object.assign(this.surface.style, {
        display: 'block',
        width: '100%',
        height: '100%',
        outline: 'none',
        touchAction: 'none',
      });
      container.appendChild(this.surface);
      this.ctx2d = this.surface.getContext('2d');
    }
    this.runtime.addView(this);
    this.bindInput();
    this.resize = new ResizeObserver(() => this.onResize());
    this.resize.observe(container);
    this.onResize();
  }

  // ------------------------------------------------------------------------------------------------------------ public

  /** A bounded PNG of this camera's next rendered frame, without its DOM controls. */
  capturePng(): Promise<string> { return this.runtime.capturePng(this.id); }

  setSource(src: WorldSource): void {
    if (this.ownsRuntime) this.runtime.setSource(src);
  }

  setDim(d: Dim): void {
    if (d === this.dim) return;
    this.dim = d;
    this.overlaysSeen = null;
  }

  setFollow(name: string | null): void {
    this.followName = name;
    this.following = true;
    this.acquired = false;
  }

  setLayers(l: Partial<LayerToggles>): void {
    this.layers = { ...DEFAULT_LAYERS, ...l };
    if (!this.layers.notes) this.opts.onNotes?.([]);
  }

  setCameraMode(m: CameraMode): void {
    if (this.opts.controls === 'editor') m = 'follow';
    this.keys.clear();
    this.rig.setMode(m, this.camera, performance.now());
    if (m !== 'fly' && document.pointerLockElement === this.surface) document.exitPointerLock();
    this.opts.onCameraModeChange?.(m);
  }

  getCameraMode(): CameraMode {
    return this.rig.mode;
  }

  setControls(controls: EngineOptions['controls']): void {
    if (this.opts.controls === controls) return;
    this.opts.controls = controls;
    this.keys.clear();
    this.drag = null;
    this.editorDrag = null;
    if (controls === 'editor') {
      if (document.pointerLockElement === this.surface) document.exitPointerLock();
      this.rig.collideDist = Infinity;
      this.setCameraMode('follow');
    }
  }

  /** Request immersive mouse look only for the normal viewer controls. */
  lock(): void {
    if (this.opts.controls === 'editor') return;
    this.surface.focus();
    void Promise.resolve(this.surface.requestPointerLock?.()).catch(() => undefined);
  }

  /** Temporarily lets an immersive tool menu own input without releasing pointer lock. */
  setInputPaused(paused: boolean): void {
    this.inputPaused = paused;
    this.keys.clear();
    this.drag = null;
    this.aimRequest++;
  }

  setXRay(m: XRayMode): void {
    this.xray = m;
  }

  /** The most this view draws (blocks); null = whatever the viewer settings say. */
  setDrawDistance(n: number | null): void {
    this.drawCap = n;
  }

  /** The draw distance in force: the viewer settings' distance, lowered by this view's own cap. */
  get drawDistance(): number {
    const q = this.runtime.viewerQuality.drawDistance;
    return clampDraw(this.drawCap === null ? q : Math.min(this.drawCap, q));
  }

  /** A target position marker (a block), drawn in this view; null clears it. */
  setMarker(p: [number, number, number] | null): void {
    this.marker = p;
  }

  /**
   * [level cut] Cuts every block at y >= `y` away within `r` blocks of the view's centre (the roof cut's uniforms, so nothing is
   * re-meshed): what was cut shows as the hatched cap. Story Mode's top-down levels x-ray roofs this way; `dimBelow` dims the
   * world under that y (the storeys below the working one). null = off.
   */
  setLevelCut(cut: { y: number; r?: number; dimBelow?: number | null } | null): void {
    this.levelCut = cut ? { y: cut.y, r: cut.r ?? 512, dimBelow: cut.dimBelow ?? null } : null;
  }

  /** This view's world time and sky light subtraction (0 noon … 11 midnight) as of its last frame (null before one). */
  sky(): { time: number | null; skySub: number } | null {
    return this.runtime.skyOf(this.id);
  }

  /** Name labels over bots in this view (default on); off where the app draws its own (Story Mode's overlay). */
  setBotLabels(on: boolean): void {
    this.botLabels = on;
  }

  /** Activity badges shown only in this view; an empty list removes them. */
  setBotBadges(badges: readonly BotBadge[]): void {
    this.runtime.setBotBadges(this.id, badges);
  }

  /**
   * [holograms] Queued build cells drawn in this view as translucent block holograms (render/holograms.ts). Rebuilt only when
   * `rev` changes (or a block change lands on one of the cells: a built cell disappears); cheap to call every frame.
   */
  setHolograms(cells: readonly HoloCell[], rev: string): void {
    const h = this.runtime.holograms(this.id, this.dim);
    h.setDim(this.dim);
    h.set(cells, rev);
  }

  /** [holograms] A placement preview (flat [x, y, z, id, meta, …]; id 0 = dig): green where it fits, red where a block is in
   * the way; null clears it. The same `key` keeps the current one. */
  setHologramPreview(blocks: number[] | null, key?: string): void {
    if (!blocks && !this.runtime.hologramsOf(this.id)) return;
    this.runtime.holograms(this.id, this.dim).setPreview(blocks, key);
  }

  /** [holograms] What this view's holograms show (cells, digs, boxes, preview, LOD), or null when it has none. */
  hologramStats(): HologramStats | null {
    return this.runtime.hologramsOf(this.id)?.stats() ?? null;
  }

  /** [holograms] Whether the queued cell at (x, y, z) is drawn as a hologram now (a built one is not): as a block, as a dig
   * outline, or (no kind) either. */
  hologramAt(x: number, y: number, z: number, kind?: 'block' | 'dig'): boolean {
    return this.runtime.hologramsOf(this.id)?.has(x, y, z, kind) ?? false;
  }

  /** The cut a pick should honour (the level cut, else the roof cut), centred where this view looks. */
  pickCut(): { x: number; y: number; z: number; r: number } | null {
    if (this.levelCut) return { x: this.rig.target.x, y: this.levelCut.y, z: this.rig.target.z, r: this.levelCut.r };
    return this.roof ? { ...this.roof } : null;
  }

  /** Bots outlined in this view (the app's selection). */
  setHighlighted(names: string[]): void {
    this.highlighted = names;
  }

  /** What the locked freecam crosshair or editor cursor points at, as last computed. */
  aim(): AimTarget | null {
    return this.aimed;
  }

  /** Whether the mouse is locked to this view (free cam look mode). */
  locked(): boolean {
    return document.pointerLockElement === this.surface;
  }

  /** The bots of this view's dimension, nearest the camera first (number keys pick from these). */
  botsInView(): BotView[] {
    const p = this.camera.position;
    return this.runtime.store
      .bots()
      .filter((b) => b.dim === this.dim)
      .sort((a, b) => Math.hypot(a.x - p.x, a.y - p.y, a.z - p.z) - Math.hypot(b.x - p.x, b.y - p.y, b.z - p.z));
  }

  /** Recomputes the target: the editor cursor, or the freecam's centre ray. A level cut is looked through. */
  async aimNow(): Promise<AimTarget | null> {
    if (this.opts.controls === 'editor') return this.pointerAt ? this.aimAtPointer(...this.pointerAt) : null;
    const o = this.camera.position;
    const d = this.camera.getWorldDirection(new THREE.Vector3());
    return this.aimAlong(o, d);
  }

  /** Pick the exact cursor ray, including application plan cells; usable from capture-phase editor handlers. */
  async aimAtPointer(clientX: number, clientY: number): Promise<AimTarget | null> {
    const rect = this.surface.getBoundingClientRect();
    if (!rect.width || !rect.height) return null;
    const ray = new THREE.Raycaster();
    ray.setFromCamera(new THREE.Vector2((clientX - rect.left) / rect.width * 2 - 1, 1 - (clientY - rect.top) / rect.height * 2), this.camera);
    return this.aimAlong(ray.ray.origin, ray.ray.direction);
  }

  private async aimAlong(o: THREE.Vector3, d: THREE.Vector3): Promise<AimTarget | null> {
    const request = ++this.aimRequest;
    const ray: AimRay = { origin: [o.x, o.y, o.z], direction: [d.x, d.y, d.z], max: this.drawDistance + 32 };
    const bots = this.layers.bots ? this.runtime.store.bots().filter((b) => b.dim === this.dim) : [];
    const physical = await aimAt(
      [o.x, o.y, o.z],
      [d.x, d.y, d.z],
      bots,
      (oo, dd, max) => this.runtime.host.pick(this.dim, oo, dd, max, this.levelCut ? this.pickCut() : null),
      (id) => blockName(this.runtime.version ?? 'b1.8.1', id),
      this.drawDistance + 32,
    );
    const t = this.opts.resolveAim ? this.opts.resolveAim(physical, ray) : physical;
    if (!this.disposed && request === this.aimRequest) this.setAim(t);
    return t;
  }

  private setAim(t: AimTarget | null): void {
    const a = this.aimed;
    const same =
      a === t ||
      (!!a &&
        !!t &&
        a.kind === t.kind &&
        (a.kind === 'bot' ? a.name === (t as typeof a).name : t.kind === 'block' && a.x === t.x && a.y === t.y && a.z === t.z && a.face === t.face && a.id === t.id && a.meta === t.meta && a.plan === t.plan));
    this.aimed = t;
    if (!same) this.opts.onAim?.(t);
  }

  setCallbacks(
    c: Pick<
      EngineOptions,
      'onSelectBot' | 'onPick' | 'onStats' | 'onNotes' | 'onCameraModeChange' | 'onError' | 'onAim' | 'onAimClick' | 'resolveAim'
    >,
  ): void {
    this.opts = { ...this.opts, ...c };
  }

  /** Centres the camera on a block position (stops following until `setFollow`). */
  focus(x: number, y: number, z: number, view?: { distance?: number; pitch?: number }): void {
    this.following = false;
    if (this.opts.controls === 'editor') {
      if (view?.pitch !== undefined) this.rig.orbitPitch = Math.max(-89.9, Math.min(89.9, view.pitch));
      if (view?.distance !== undefined) this.rig.orbitDist = Math.max(1, Math.min(256, view.distance));
    }
    this.rig.focus(x + 0.5, y + 0.5, z + 0.5, this.camera, performance.now(), view);
  }

  bots(): BotView[] {
    return this.runtime.store.bots();
  }

  /** A chat bubble over a speaker's head (in every view of the runtime; see `WorldRuntime.say`). */
  say(name: string, text: string, opts?: { kind?: BubbleKind }): boolean {
    return this.runtime.say(name, text, opts);
  }

  /** The chat bubbles this view drew in its last frame (speaker, anchor in CSS pixels of the view, lines). */
  bubbles(): readonly ShownBubble[] {
    return this.runtime.bubblesShown(this.id);
  }

  stats(): ViewerStats {
    const m = this.runtime.memory();
    return {
      fps: this.fps,
      frameMs: this.frameMs,
      drawCalls: m.drawCalls,
      triangles: m.triangles,
      sections: m.sections,
      sectionsQueued: m.sectionsQueued,
      columns: m.columns,
      bytesReceived: m.bytesReceived,
      version: this.runtime.version ?? 'unknown',
      textured: this.runtime.textured,
      gpu: this.runtime.gpu,
      memory: m,
      resolutionScale: this.runtime.resolutionScale,
      blockChanges: this.runtime.deltas.applied,
    };
  }

  dispose(): void {
    if (this.disposed) return;
    this.disposed = true;
    for (const c of this.cleanups) c();
    this.resize?.disconnect();
    if (document.pointerLockElement === this.surface) document.exitPointerLock();
    this.runtime.removeView(this);
    if (this.ctx2d) this.surface.remove();
    this.runtime.release();
  }

  // ------------------------------------------------------------------------------------------------------------ RuntimeView

  private lookCentre(): THREE.Vector3 {
    return this.rig.mode === 'fly' ? this.camera.position : this.rig.target;
  }

  interest(): Interest {
    const c = this.lookCentre();
    return { dim: this.dim, x: c.x, z: c.z, r: Math.max(this.drawDistance, this.cover()) + 24,
      priority: this.runtime.isPrimary(this) ? 2 : 1,
    };
  }

  centre(): Centre & { dim: Dim } {
    const c = this.lookCentre();
    return { dim: this.dim, x: c.x, y: c.y, z: c.z, distance: this.drawDistance, cover: this.cover() };
  }

  /** Top-down and iso views: the ground the picture shows is always drawn (and unfogged) this far; 0 otherwise. */
  cover(): number {
    return this.rig.groundCover(this.camera.aspect, this.runtime.viewerQuality.coverCap);
  }

  overlayDim(): Dim | null {
    const l = this.layers;
    return l.claims || l.places || l.points || l.plans || l.dangers || l.notes ? this.dim : null;
  }

  selectedBots(): string[] {
    return this.followName ? [this.followName] : [];
  }

  private followed(): BotView | null {
    if (!this.followName) return null;
    const b = this.runtime.store.bot(this.followName);
    return b && b.dim === this.dim ? b : null;
  }

  frame(rt: WorldRuntime, now: number, dt: number): ViewSetup | null {
    if (this.disposed) return null;
    this.frameNow = now;
    this.frameDt = dt;
    this.opts.onBeforeFrame?.(this, now, dt);
    const t0 = performance.now();
    const bot = this.followed();
    this.followBot(bot, dt);
    this.rigInput.controller = this.opts.controls === 'controller';
    if (this.inputPaused || this.opts.controls === 'controller' && this.rig.mode === 'fly' && !this.locked()) this.keys.clear();
    this.rig.apply(this.camera, dt, this.rigInput, now);
    const iso = this.rig.mode === 'iso' || this.rig.mode === 'top';
    const cover = this.cover();
    this.fitClipPlanes(iso, cover);
    this.syncOverlays(rt);
    const subs = this.xray === 'off' ? [] : this.subjects(bot);
    this.updateXRay(now, bot, subs);
    this.updateAim(now);
    const setup = this.setupFor(rt, bot, subs, iso, cover);
    this.countFrame(now, t0);
    return setup;
  }

  private readonly rigInput: RigInput = { keys: this.keys };
  private readonly followAt = new THREE.Vector3();

  /** The look-at point follows the bot's eyes (jumps there when first acquired, then glides); not while flying. */
  private followBot(bot: BotView | null, dt: number): void {
    if (!bot || !this.following || this.rig.mode === 'fly' || this.opts.controls === 'editor') return;
    const want = this.followAt.set(bot.x, bot.y + 1.2, bot.z);
    if (!this.acquired) {
      this.rig.target.copy(want);
      this.acquired = true;
    } else this.rig.target.lerp(want, Math.min(1, dt * 10));
  }

  /** Near and far planes for the drawn distance (iso: from far away). */
  private fitClipPlanes(iso: boolean, cover: number): void {
    const isoDist = iso ? this.rig.isoDistance() : 0;
    this.camera.far = Math.max(this.drawDistance, cover) * 1.6 + isoDist + 32;
    this.camera.near = iso ? Math.max(0.5, this.rig.isoDistance() * 0.2) : 0.05;
    // A cut-away shows solids from the inside: the cap pass needs their far side (down to the world floor) in range.
    if (this.roof || this.coneOn || this.levelCut) this.camera.far += 256;
  }

  /** Overlays: this view's dimension, its toggles; notes go to the app. */
  private syncOverlays(rt: WorldRuntime): void {
    const ov = rt.overlaysFor(this.dim);
    if (ov && ov !== this.overlaysSeen) {
      this.overlaysSeen = ov;
      this.opts.onNotes?.(this.layers.notes ? ov.notes : []);
    }
    rt.overlays.setVisible(this.layers);
  }

  /** The active crosshair/cursor target, ten times a second (also updates when only the camera moves). */
  private updateAim(now: number): void {
    if (!(this.opts.controls === 'editor' ? this.pointerAt && !this.editorDrag : this.locked() && this.rig.mode === 'fly')) {
      if (this.aimed) this.setAim(null);
      return;
    }
    if (this.aimBusy || now - this.aimAtMs < 100) return;
    this.aimAtMs = now;
    this.aimBusy = true;
    void this.aimNow().finally(() => {
      this.aimBusy = false;
    });
  }

  /** What the runtime draws for this view this frame. */
  private setupFor(rt: WorldRuntime, bot: BotView | null, subs: BotView[], iso: boolean, cover: number): ViewSetup {
    const outlines = this.outlines();
    const primary = rt.isPrimary(this);
    const [w, h] = primary ? rt.primarySize(this.container) : this.size;
    return {
      dim: this.dim,
      camera: this.camera,
      ...(this.opts.onAfterFrame ? { afterRender: this.afterRender } : {}),
      centre: iso || this.rig.mode === 'follow' ? this.rig.target : this.camera.position,
      drawDistance: this.drawDistance,
      cover,
      depthFloor: this.depthFloor(cover),
      width: w,
      height: h,
      layers: this.layers,
      labels: true,
      overlays: true,
      selected: this.followName,
      cones: this.cones(subs),
      floorY: floorUnder(subs),
      roof: this.roofCut(),
      dimBelow: this.levelCut?.dimBelow ?? null,
      ghosts: this.xray !== 'off',
      viewId: this.id,
      precip: this.precip,
      aids: this.aidsAt(bot),
      target: primary ? 'primary' : (this.ctx2d ?? 'primary'),
      outlines,
      bubbles: this.layers.bubbles === false ? 'off' : 'full',
      pxScale: h / Math.max(1, this.container.clientHeight),
      botLabels: this.botLabels,
      entityCut: this.entityCut(),
      holoPx: iso ? this.container.clientHeight / Math.max(1, this.rig.isoZoom) : null,
    };
  }

  /** Zoomed out past the draw distance, a top-down / iso picture skips what lies deep below the ground it looks at. */
  private depthFloor(cover: number): number | null {
    return cover > this.drawDistance ? this.rig.target.y - FLOOR_DEPTH : null;
  }

  /** X-ray cones toward the subjects: always with x-ray on, in auto mode only around a buried camera. */
  private cones(subs: BotView[]): ViewSetup['cones'] {
    if (!subs.length || !(this.xray === 'on' || this.coneOn)) return [];
    return subs.map((s) => ({ x: s.x, y: s.y + 0.9, z: s.z }));
  }

  /** The level cut as a roof cut around where the view looks, else the automatic roof cut-away. */
  private roofCut(): ViewSetup['roof'] {
    const cut = this.levelCut;
    return cut ? { x: this.rig.target.x, y: cut.y, z: this.rig.target.z, r: cut.r } : this.roof;
  }

  /** [level cut] Bodies above the cut are hidden, those below its dimmed storeys dimmed. */
  private entityCut(): ViewSetup['entityCut'] {
    const cut = this.levelCut;
    return cut ? { x: this.rig.target.x, y: cut.y, z: this.rig.target.z, r: cut.r, dimBelow: cut.dimBelow } : null;
  }

  private aidsAt(bot: BotView | null): ViewSetup['aids'] {
    return this.layers.aids && bot ? { x: bot.x, y: bot.y, z: bot.z } : null;
  }

  /** Wireframe boxes: the highlighted bots, the target marker and what the crosshair points at. */
  private outlines(): NonNullable<ViewSetup['outlines']> {
    const outlines: Array<{ min: [number, number, number]; max: [number, number, number]; color: number }> = [];
    for (const name of this.highlighted) {
      const b = this.runtime.store.bot(name);
      if (b && b.dim === this.dim) outlines.push({ ...outlineOf({ kind: 'bot', name, x: b.x, y: b.y, z: b.z, distance: 0 }), color: 0x5ee08a });
    }
    const m = this.marker;
    if (m) outlines.push({ min: [m[0] - 0.01, m[1] - 0.01, m[2] - 0.01], max: [m[0] + 1.01, m[1] + 1.01, m[2] + 1.01], color: 0x38bdf8 });
    if (this.aimed) outlines.push(this.aimOutline(this.aimed));
    return outlines;
  }

  /** The crosshair's box: a bot's where it stands now (white), a block's (black). */
  private aimOutline(aimed: AimTarget): { min: [number, number, number]; max: [number, number, number]; color: number } {
    const b = aimed.kind === 'bot' ? this.runtime.store.bot(aimed.name) : null;
    const t = b ? { ...aimed, x: b.x, y: b.y, z: b.z } : aimed;
    return { ...outlineOf(t), color: t.kind === 'bot' ? 0xffffff : 0x000000 };
  }

  /** Frame time and fps (over half-second windows), reported to the app with the stats. */
  private countFrame(now: number, t0: number): void {
    this.frames++;
    this.frameMs = this.frameMs * 0.9 + (performance.now() - t0) * 0.1;
    if (!this.fpsAt) this.fpsAt = now;
    if (now - this.fpsAt < 500) return;
    this.fps = (this.frames * 1000) / (now - this.fpsAt);
    this.frames = 0;
    this.fpsAt = now;
    this.opts.onStats?.(this.stats());
  }

  /** The bodies the x-ray keeps in sight: the followed bot, or (flying) up to two bots in front of the camera. */
  private subjects(bot: BotView | null): BotView[] {
    if (this.rig.mode !== 'fly') return bot ? [bot] : [];
    const fwd = this.camera.getWorldDirection(new THREE.Vector3());
    const p = this.camera.position;
    return this.runtime.store
      .bots()
      .filter((b) => b.dim === this.dim)
      .map((b) => ({
        b,
        d: Math.hypot(b.x - p.x, b.y + 0.9 - p.y, b.z - p.z),
        f: (b.x - p.x) * fwd.x + (b.y + 0.9 - p.y) * fwd.y + (b.z - p.z) * fwd.z,
      }))
      .filter((t) => t.d > 1.5 && t.d <= XRAY_RANGE && t.f > 1)
      .sort((a, b) => a.d - b.d)
      .slice(0, 2)
      .map((t) => t.b);
  }

  /** Visibility tests (auto mode) and the roof cut, against the worker's blocks a few times a second. */
  private updateXRay(now: number, bot: BotView | null, subs: BotView[]): void {
    if (this.xrayBusy || now - this.xrayAt < 250) return;
    this.xrayAt = now;
    const main = subs[0] ?? bot;
    if (!main) {
      this.coneOn = false;
      this.roof = null;
      return;
    }
    this.xrayBusy = true;
    const cam = this.camera.position;
    const camV: V3 = [cam.x, cam.y, cam.z];
    this.runtime.host
      .viewInfo(this.dim, camV, [main.x, main.y, main.z], null)
      .then((info) => {
        // Auto: a hidden body shows as a silhouette (ViewSetup.ghosts); the cone only opens the rock around a buried camera.
        this.coneOn = this.xray === 'auto' ? info.cameraInside : this.xray === 'on';
        this.precip = this.runtime.precipAt(info.biome, main.y);
        // Roof cut-away: the followed bot is under cover and the camera looks down on it (not when flying).
        this.roof =
          this.layers.cutaway && bot && this.rig.mode !== 'fly' && info.covered && cam.y > bot.y + 1.5
            ? { x: bot.x, y: Math.floor(bot.y) + 2, z: bot.z, r: 9 }
            : null;
      })
      .then(() => this.collide())
      .catch(() => undefined)
      .finally(() => {
        this.xrayBusy = false;
      });
  }

  /** Follow camera collision (vanilla third person): pull the camera in front of the first solid block between the bot
   * and where the camera wants to be, so it never sits inside rock in a tunnel; the x-ray handles walls seen from outside. */
  private async collide(): Promise<void> {
    if (this.rig.mode !== 'follow' || !this.cameraCollision || this.opts.controls === 'editor') {
      this.rig.collideDist = Infinity;
      return;
    }
    const t = this.rig.target;
    const d = lookDir(this.rig.orbitYaw, this.rig.orbitPitch).negate(); // from the bot back toward the camera
    // Blocks the roof cut removes don't push the camera in: with the bot under cover the camera stays up and looks down
    // into the opened room or tunnel (the C# orbit panels' framing).
    const roof = this.roof ? { x: this.roof.x, y: this.roof.y, z: this.roof.z, r: this.roof.r } : null;
    const hit = await this.runtime.host
      .pick(this.dim, [t.x, t.y, t.z], [d.x, d.y, d.z], this.rig.orbitDist + 1, roof)
      .catch(() => null);
    const want = hit
      ? Math.max(1, Math.hypot(hit.x + 0.5 - t.x, hit.y + 0.5 - t.y, hit.z + 0.5 - t.z) - 0.9)
      : Infinity;
    // Pull in at once, ease back out.
    this.rig.collideDist = want < this.rig.collideDist ? want : Math.min(want, this.rig.collideDist + 0.8);
  }

  /** Keep the follow camera out of solid blocks (default on). */
  cameraCollision = true;

  // ------------------------------------------------------------------------------------------------------------ input

  private onResize(): void {
    const dpr = this.opts.pixelRatio ?? Math.min(2, globalThis.devicePixelRatio || 1);
    const w = Math.max(1, Math.round(this.container.clientWidth * dpr)),
      h = Math.max(1, Math.round(this.container.clientHeight * dpr));
    this.size = [w, h];
    if (this.ctx2d && (this.surface.width !== w || this.surface.height !== h)) {
      this.surface.width = w;
      this.surface.height = h;
    }
  }

  private listen(el: HTMLElement | Window, type: string, fn: (e: never) => void, opts?: AddEventListenerOptions): void {
    el.addEventListener(type, fn as EventListener, opts);
    this.cleanups.push(() => el.removeEventListener(type, fn as EventListener, opts));
  }

  private active(): boolean {
    return document.activeElement === this.surface || document.pointerLockElement === this.surface;
  }

  private bindInput(): void {
    const c = this.surface;
    const rememberPointer = (e: PointerEvent) => { this.pointerAt = [e.clientX, e.clientY]; };
    this.listen(c, 'pointermove', rememberPointer, { capture: true });
    this.listen(c, 'pointerdown', rememberPointer, { capture: true });
    this.listen(c, 'pointerleave', () => {
      this.pointerAt = null;
      if (this.opts.controls === 'editor') { this.aimRequest++; this.setAim(null); }
    });
    this.bindEditorNavigation();
    this.listen(c, 'contextmenu', (e: MouseEvent) => e.preventDefault());
    this.listen(c, 'pointerdown', (e: PointerEvent) => {
      if (this.inputPaused) return;
      c.focus();
      // Free cam plays like the game: the first click locks and hides the cursor (Esc releases it); that click picks nothing.
      if (this.opts.controls !== 'editor' && this.rig.mode === 'fly' && e.button === 0 && document.pointerLockElement !== c) {
        this.drag = null;
        this.lock();
        return;
      }
      this.drag = { x: e.clientX, y: e.clientY, t: performance.now(), button: e.button, moved: false };
      // A locked pointer cannot also be captured (InvalidStateError); it already goes nowhere else.
      if (document.pointerLockElement !== c) {
        try {
          c.setPointerCapture?.(e.pointerId);
        } catch {
          // the pointer went away meanwhile
        }
      }
    });
    this.listen(c, 'pointermove', (e: PointerEvent) => {
      if (this.inputPaused) return;
      if (document.pointerLockElement === c) {
        this.rig.look(e.movementX, e.movementY);
        return;
      }
      const d = this.drag;
      if (!d) return;
      const dx = e.clientX - d.x,
        dy = e.clientY - d.y;
      if (!d.moved && Math.abs(dx) + Math.abs(dy) < 4) return;
      d.moved = true;
      d.x = e.clientX;
      d.y = e.clientY;
      if (this.opts.controls === 'editor') return;
      if (
        (this.rig.mode === 'iso' || this.rig.mode === 'top') &&
        (d.button === 2 || e.shiftKey || this.rig.mode === 'top')
      )
        this.rig.pan(dx, dy, c.clientHeight);
      else this.rig.look(dx, dy);
    });
    this.listen(c, 'pointerup', (e: PointerEvent) => {
      if (this.inputPaused) return;
      const d = this.drag;
      this.drag = null;
      if (c.hasPointerCapture?.(e.pointerId)) c.releasePointerCapture(e.pointerId);
      const locked = document.pointerLockElement === c;
      if (!isClick(d, locked && !!this.opts.onAimClick)) return;
      if (locked && this.opts.onAimClick) {
        const mods = { shift: e.shiftKey, ctrl: e.ctrlKey || e.metaKey, button: e.button };
        void this.aimNow().then((t) => {
          if (!this.disposed && !this.inputPaused && document.pointerLockElement === c && this.rig.mode === 'fly') this.opts.onAimClick?.(t, mods);
        });
        return;
      }
      const rect = c.getBoundingClientRect();
      const ndc = locked
        ? new THREE.Vector2(0, 0)
        : new THREE.Vector2(
            ((e.clientX - rect.left) / rect.width) * 2 - 1,
            -((e.clientY - rect.top) / rect.height) * 2 + 1,
          );
      void this.pickAt(ndc);
    });
    this.listen(
      c,
      'wheel',
      (e: WheelEvent) => {
        if (this.inputPaused) return;
        e.preventDefault();
        if (this.opts.controls === 'editor') this.rig.dolly(e.deltaY);
        else this.rig.wheel(e.deltaY);
      },
      { passive: false },
    );
    this.listen(window, 'keydown', (e: KeyboardEvent) => {
      if (!this.active() || this.inputPaused || this.opts.controls === 'editor') return;
      if (this.opts.controls === 'controller' && this.rig.mode === 'fly') {
        if (!this.locked() || (e.target as HTMLElement | null)?.closest?.('input,textarea,select,[contenteditable="true"]')) return;
        // Ctrl is descent, so Ctrl+W/S/D must not close/save/bookmark the page while flying.
        if (e.ctrlKey) e.preventDefault();
        if (e.code === 'KeyQ' || e.code === 'KeyE') return;
      }
      if (e.code === 'KeyQ' && this.rig.mode === 'iso') this.rig.rotateIso(-1);
      else if (e.code === 'KeyE' && this.rig.mode === 'iso') this.rig.rotateIso(1);
      this.keys.add(e.code);
      if (e.code === 'Space' || e.code.startsWith('Arrow') || e.code.startsWith('Page')) e.preventDefault();
    });
    this.listen(window, 'keyup', (e: KeyboardEvent) => this.keys.delete(e.code));
    this.listen(window, 'blur', () => this.keys.clear());
    this.listen(c, 'blur', () => this.keys.clear());
  }

  /** Own navigation gestures in capture phase so MMB/Alt+LMB never reach an editor's block tools or gizmos. */
  private bindEditorNavigation(): void {
    const c = this.surface;
    const consume = (e: PointerEvent) => { e.preventDefault(); e.stopImmediatePropagation(); };
    this.listen(c, 'pointerdown', (e: PointerEvent) => {
      if (this.opts.controls !== 'editor' || !(e.button === 1 || (e.button === 0 && e.altKey))) return;
      consume(e);
      c.focus();
      this.drag = null;
      this.editorDrag = { x: e.clientX, y: e.clientY, pointerId: e.pointerId };
      try { c.setPointerCapture?.(e.pointerId); } catch { /* The pointer may have already left. */ }
    }, { capture: true });
    this.listen(c, 'pointermove', (e: PointerEvent) => {
      const d = this.editorDrag;
      if (!d || d.pointerId !== e.pointerId) return;
      consume(e);
      const dx = e.clientX - d.x, dy = e.clientY - d.y;
      d.x = e.clientX; d.y = e.clientY;
      this.following = false;
      if (e.shiftKey) this.rig.panOrbit(dx, dy, c.clientHeight, this.camera);
      else this.rig.orbit(dx, dy);
    }, { capture: true });
    const end = (e: PointerEvent) => {
      if (this.editorDrag?.pointerId !== e.pointerId) return;
      consume(e);
      this.editorDrag = null;
      if (c.hasPointerCapture?.(e.pointerId)) c.releasePointerCapture(e.pointerId);
    };
    this.listen(c, 'pointerup', end, { capture: true });
    this.listen(c, 'pointercancel', end, { capture: true });
    this.listen(c, 'lostpointercapture', () => { this.editorDrag = null; });
    this.listen(window, 'blur', () => { this.editorDrag = null; this.drag = null; });
  }

  private async pickAt(ndc: THREE.Vector2): Promise<void> {
    const ray = new THREE.Raycaster();
    ray.setFromCamera(ndc, this.camera);
    const ents = this.runtime.entities;
    ents.showFor(this.dim, this.camera.position, { bots: this.layers.bots, entities: false, labels: false });
    const name = ents.botAt(ray);
    if (name) {
      this.opts.onSelectBot?.(name);
      return;
    }
    if (!this.opts.onPick) return;
    const o = ray.ray.origin,
      d = ray.ray.direction;
    const roof = this.roof ? { x: this.roof.x, y: this.roof.y, z: this.roof.z, r: this.roof.r } : null;
    const hit = await this.runtime.host
      .pick(this.dim, [o.x, o.y, o.z], [d.x, d.y, d.z], this.drawDistance + 32, roof)
      .catch(() => null);
    if (!hit || this.disposed) return;
    this.opts.onPick({ ...hit, name: blockName(this.runtime.version ?? 'b1.8.1', hit.id), dim: this.dim });
  }
}

/** How deep below the look-at point a zoomed-out top-down / iso view still draws (ocean floors stay; caves do not). */
const FLOOR_DEPTH = 32;

/** A press that counts as a click: the left button, not dragged, released within half a second. */
function isClick(d: { t: number; button: number; moved: boolean } | null, right = false): boolean {
  return !!d && !d.moved && performance.now() - d.t <= 500 && (d.button === 0 || (right && d.button === 2));
}

/** The x-ray's floor: the cut never goes below the lowest subject's feet (0 without subjects). */
function floorUnder(subs: BotView[]): number {
  return subs.length ? Math.floor(Math.min(...subs.map((s) => s.y)) + 0.01) : 0;
}

function clampDraw(n: number): number {
  return Math.max(32, Math.min(256, Math.round(n)));
}
