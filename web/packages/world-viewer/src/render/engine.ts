// The interactive view: cameras (follow / fly / iso), input, picking, x-ray decisions and its layer toggles, drawn through a
// WorldRuntime (its own, or the one a WorldViewerProvider shares with the hive-card thumbnails). It owns no world data and
// no GPU resources: only a camera, a viewport and per-view uniforms.

import * as THREE from 'three';
import type { V3 } from '../mesh/query';
import { DEFAULT_LAYERS, type CameraMode, type Dim, type LayerToggles, type Overlays, type PickedBlock, type ViewerStats, type WorldSource, type XRayMode } from '../types';
import { blockName } from '../world/blocks';
import type { BotView } from '../world/entities';
import type { Interest } from '../world/sync';
import { CameraRig } from './cameraRig';
import { newViewId, WorldRuntime, type RuntimeOptions, type RuntimeView, type ViewSetup } from './runtime';
import type { Centre } from './sections';

export interface EngineOptions extends RuntimeOptions {
  source: WorldSource;
  /** Draw through this shared runtime instead of creating one (WorldViewerProvider passes it). */
  runtime?: WorldRuntime;
  dim?: Dim;
  follow?: string | null;
  layers?: Partial<LayerToggles>;
  cameraMode?: CameraMode;
  xray?: XRayMode;
  /** Blocks (32..256, default 128). */
  drawDistance?: number;
  onSelectBot?: (name: string) => void;
  onPick?: (block: PickedBlock) => void;
  onStats?: (s: ViewerStats) => void;
  onNotes?: (notes: string[]) => void;
  onCameraModeChange?: (m: CameraMode) => void;
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
  private drawDistance: number;
  private keys = new Set<string>();
  private drag: { x: number; y: number; t: number; button: number; moved: boolean } | null = null;
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

  constructor(private readonly container: HTMLElement, opts: EngineOptions) {
    this.opts = opts;
    this.dim = opts.dim ?? 0;
    this.followName = opts.follow ?? null;
    this.layers = { ...DEFAULT_LAYERS, ...opts.layers };
    this.xray = opts.xray ?? 'auto';
    this.drawDistance = clampDraw(opts.drawDistance ?? 128);
    if (opts.cameraMode) this.rig.mode = opts.cameraMode;
    this.ownsRuntime = !opts.runtime;
    this.runtime = (opts.runtime ?? new WorldRuntime(opts.source, opts)).retain();
    if (this.runtime.attachPrimary(this, container)) this.surface = this.runtime.canvas;
    else {
      // Another interactive view owns the shared canvas: draw into our own 2D canvas (same world, same meshes).
      this.surface = document.createElement('canvas');
      this.surface.tabIndex = 0;
      Object.assign(this.surface.style, { display: 'block', width: '100%', height: '100%', outline: 'none', touchAction: 'none' });
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
    this.rig.setMode(m, this.camera, performance.now());
    if (m !== 'fly' && document.pointerLockElement === this.surface) document.exitPointerLock();
    this.opts.onCameraModeChange?.(m);
  }

  getCameraMode(): CameraMode {
    return this.rig.mode;
  }

  setXRay(m: XRayMode): void {
    this.xray = m;
  }

  setDrawDistance(n: number): void {
    this.drawDistance = clampDraw(n);
  }

  setCallbacks(c: Pick<EngineOptions, 'onSelectBot' | 'onPick' | 'onStats' | 'onNotes' | 'onCameraModeChange' | 'onError'>): void {
    this.opts = { ...this.opts, ...c };
  }

  /** Centres the camera on a block position (stops following until `setFollow`). */
  focus(x: number, y: number, z: number): void {
    this.following = false;
    this.rig.focus(x + 0.5, y + 0.5, z + 0.5, this.camera, performance.now());
  }

  bots(): BotView[] {
    return this.runtime.store.bots();
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
    return { dim: this.dim, x: c.x, z: c.z, r: this.drawDistance + 24 };
  }

  centre(): Centre & { dim: Dim } {
    const c = this.lookCentre();
    return { dim: this.dim, x: c.x, y: c.y, z: c.z, distance: this.drawDistance };
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
    const t0 = performance.now();
    const bot = this.followed();
    if (bot && this.following && this.rig.mode !== 'fly') {
      const want = new THREE.Vector3(bot.x, bot.y + 1.2, bot.z);
      if (!this.acquired) {
        this.rig.target.copy(want);
        this.acquired = true;
      } else this.rig.target.lerp(want, Math.min(1, dt * 10));
    }
    this.rig.apply(this.camera, dt, { keys: this.keys }, now);
    const iso = this.rig.mode === 'iso' || this.rig.mode === 'top';
    this.camera.far = this.drawDistance * 1.6 + (iso ? this.rig.isoDistance() : 0) + 32;
    this.camera.near = iso ? Math.max(0.5, this.rig.isoDistance() * 0.2) : 0.05;
    // Overlays: this view's dimension, its toggles; notes go to the app.
    const ov = rt.overlaysFor(this.dim);
    if (ov && ov !== this.overlaysSeen) {
      this.overlaysSeen = ov;
      this.opts.onNotes?.(this.layers.notes ? ov.notes : []);
    }
    rt.overlays.setVisible(this.layers);
    const subs = this.xray === 'off' ? [] : this.subjects(bot);
    this.updateXRay(now, bot, subs);
    const primary = rt.isPrimary(this);
    const [w, h] = primary ? rt.primarySize(this.container) : this.size;
    const setup: ViewSetup = {
      dim: this.dim,
      camera: this.camera,
      centre: iso || this.rig.mode === 'follow' ? this.rig.target : this.camera.position,
      drawDistance: this.drawDistance,
      width: w,
      height: h,
      layers: this.layers,
      labels: true,
      overlays: true,
      selected: this.followName,
      cones: subs.length && (this.xray === 'on' || this.coneOn) ? subs.map((s) => ({ x: s.x, y: s.y + 0.9, z: s.z })) : [],
      floorY: subs.length ? Math.floor(Math.min(...subs.map((s) => s.y)) + 0.01) : 0,
      roof: this.roof,
      viewId: this.id,
      precip: this.precip,
      aids: this.layers.aids && bot ? { x: bot.x, y: bot.y, z: bot.z } : null,
      target: primary ? 'primary' : (this.ctx2d ?? 'primary'),
    };
    this.frames++;
    this.frameMs = this.frameMs * 0.9 + (performance.now() - t0) * 0.1;
    if (!this.fpsAt) this.fpsAt = now;
    if (now - this.fpsAt >= 500) {
      this.fps = (this.frames * 1000) / (now - this.fpsAt);
      this.frames = 0;
      this.fpsAt = now;
      this.opts.onStats?.(this.stats());
    }
    return setup;
  }

  /** The bodies the x-ray keeps in sight: the followed bot, or (flying) up to two bots in front of the camera. */
  private subjects(bot: BotView | null): BotView[] {
    if (this.rig.mode !== 'fly') return bot ? [bot] : [];
    const fwd = this.camera.getWorldDirection(new THREE.Vector3());
    const p = this.camera.position;
    return this.runtime.store
      .bots()
      .filter((b) => b.dim === this.dim)
      .map((b) => ({ b, d: Math.hypot(b.x - p.x, b.y + 0.9 - p.y, b.z - p.z), f: (b.x - p.x) * fwd.x + (b.y + 0.9 - p.y) * fwd.y + (b.z - p.z) * fwd.z }))
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
        this.coneOn = this.xray === 'auto' ? info.hidden || info.cameraInside : this.xray === 'on';
        this.precip = this.runtime.precipAt(info.biome, main.y);
        // Roof cut-away: the followed bot is under cover and the camera looks down on it (not when flying).
        this.roof = this.layers.cutaway && bot && this.rig.mode !== 'fly' && info.covered && cam.y > bot.y + 1.5 ? { x: bot.x, y: Math.floor(bot.y) + 2, z: bot.z, r: 9 } : null;
      })
      .catch(() => undefined)
      .finally(() => {
        this.xrayBusy = false;
      });
  }

  // ------------------------------------------------------------------------------------------------------------ input

  private onResize(): void {
    const dpr = this.opts.pixelRatio ?? Math.min(2, globalThis.devicePixelRatio || 1);
    const w = Math.max(1, Math.round(this.container.clientWidth * dpr)), h = Math.max(1, Math.round(this.container.clientHeight * dpr));
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
    this.listen(c, 'contextmenu', (e: MouseEvent) => e.preventDefault());
    this.listen(c, 'pointerdown', (e: PointerEvent) => {
      c.focus();
      this.drag = { x: e.clientX, y: e.clientY, t: performance.now(), button: e.button, moved: false };
      c.setPointerCapture?.(e.pointerId);
    });
    this.listen(c, 'pointermove', (e: PointerEvent) => {
      if (document.pointerLockElement === c) {
        this.rig.look(e.movementX, e.movementY);
        return;
      }
      const d = this.drag;
      if (!d) return;
      const dx = e.clientX - d.x, dy = e.clientY - d.y;
      if (!d.moved && Math.abs(dx) + Math.abs(dy) < 4) return;
      d.moved = true;
      d.x = e.clientX;
      d.y = e.clientY;
      if ((this.rig.mode === 'iso' || this.rig.mode === 'top') && (d.button === 2 || e.shiftKey || this.rig.mode === 'top')) this.rig.pan(dx, dy, c.clientHeight);
      else if (this.rig.mode === 'fly') this.rig.look(-dx, -dy);
      else this.rig.look(dx, dy);
    });
    this.listen(c, 'pointerup', (e: PointerEvent) => {
      const d = this.drag;
      this.drag = null;
      if (c.hasPointerCapture?.(e.pointerId)) c.releasePointerCapture(e.pointerId);
      if (!d || d.moved || performance.now() - d.t > 500 || d.button !== 0) return;
      if (this.rig.mode === 'fly' && document.pointerLockElement !== c) {
        c.requestPointerLock?.();
        return;
      }
      const locked = document.pointerLockElement === c;
      const rect = c.getBoundingClientRect();
      const ndc = locked ? new THREE.Vector2(0, 0) : new THREE.Vector2(((e.clientX - rect.left) / rect.width) * 2 - 1, -((e.clientY - rect.top) / rect.height) * 2 + 1);
      void this.pickAt(ndc);
    });
    this.listen(c, 'wheel', (e: WheelEvent) => {
      e.preventDefault();
      this.rig.wheel(e.deltaY);
    }, { passive: false });
    this.listen(window, 'keydown', (e: KeyboardEvent) => {
      if (!this.active()) return;
      if (e.code === 'KeyQ' && this.rig.mode === 'iso') this.rig.rotateIso(-1);
      else if (e.code === 'KeyE' && this.rig.mode === 'iso') this.rig.rotateIso(1);
      this.keys.add(e.code);
      if (e.code === 'Space' || e.code.startsWith('Arrow') || e.code.startsWith('Page')) e.preventDefault();
    });
    this.listen(window, 'keyup', (e: KeyboardEvent) => this.keys.delete(e.code));
    this.listen(window, 'blur', () => this.keys.clear());
    this.listen(c, 'blur', () => this.keys.clear());
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
    const o = ray.ray.origin, d = ray.ray.direction;
    const roof = this.roof ? { x: this.roof.x, y: this.roof.y, z: this.roof.z, r: this.roof.r } : null;
    const hit = await this.runtime.host.pick(this.dim, [o.x, o.y, o.z], [d.x, d.y, d.z], this.drawDistance + 32, roof).catch(() => null);
    if (!hit || this.disposed) return;
    this.opts.onPick({ ...hit, name: blockName(this.runtime.version ?? 'b1.8.1', hit.id), dim: this.dim });
  }
}

function clampDraw(n: number): number {
  return Math.max(32, Math.min(256, Math.round(n)));
}
