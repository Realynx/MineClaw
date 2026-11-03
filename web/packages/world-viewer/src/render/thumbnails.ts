// Hive-card thumbnails: small live third-person views of one bot each, drawn by a shared WorldRuntime (one WebGL context, one
// world, one mesh per section for every card) and copied into each card's own 2D canvas. Each renders at a low rate (default
// 3 fps), takes one of the runtime's few render slots per frame (most overdue first), pauses while offscreen
// (IntersectionObserver) or while the page is hidden, and uses a short draw distance. Browsers cap live WebGL contexts (~16),
// so a grid of cards must never create one context per card.

import * as THREE from 'three';
import type { Dim } from '../types';
import type { Interest } from '../world/sync';
import { lookDir } from './cameraRig';
import { newViewId, type RuntimeView, type ViewSetup, type WorldRuntime } from './runtime';
import type { Centre } from './sections';

export interface ThumbnailViewOptions {
  /** Camera behind the bot (default) or at the Explorer's fixed angle (yaw 315). */
  angle?: 'behind' | 'fixed';
  /** Camera distance in blocks (default 6). */
  distance?: number;
  /** Camera pitch in degrees (default 28). */
  pitch?: number;
  /** Renders per second (default 3; 1..10). */
  fps?: number;
  /** Blocks drawn around the bot (default 40). */
  drawDistance?: number;
  /** X-ray toward the bot when it is hidden (default on). */
  xray?: boolean;
}

export interface ThumbnailHandle {
  setFollow(bot: string): void;
  setOptions(o: ThumbnailViewOptions): void;
  /** Render again at the next opportunity (e.g. after a resize). */
  invalidate(): void;
  dispose(): void;
}

export class ThumbnailView implements RuntimeView {
  readonly id = newViewId();
  private readonly camera = new THREE.PerspectiveCamera(70, 1.6, 0.05, 400);
  private readonly ctx: CanvasRenderingContext2D | null;
  private readonly observer: IntersectionObserver | null = null;
  private onScreen = true;
  private lastAt = -Infinity;
  private yaw = Number.NaN;
  private cone = false;
  private busy = false;
  private readonly target = new THREE.Vector3();

  constructor(private readonly rt: WorldRuntime, private readonly canvas: HTMLCanvasElement, public follow: string, public opts: ThumbnailViewOptions = {}) {
    this.ctx = canvas.getContext('2d');
    if (typeof IntersectionObserver !== 'undefined') {
      this.observer = new IntersectionObserver((es) => {
        for (const e of es) this.onScreen = e.isIntersecting;
      });
      this.observer.observe(canvas);
    }
  }

  private get drawDistance(): number {
    return this.opts.drawDistance ?? 40;
  }

  private bot() {
    return this.rt.store.bot(this.follow);
  }

  interest(): Interest | null {
    const b = this.bot();
    return b ? { dim: b.dim, x: b.x, z: b.z, r: this.drawDistance + 16 } : null;
  }

  centre(): (Centre & { dim: Dim }) | null {
    const b = this.bot();
    if (!b || !this.onScreen) return null;
    return { dim: b.dim, x: b.x, y: b.y, z: b.z, distance: this.drawDistance };
  }

  overlayDim(): Dim | null {
    return null;
  }

  selectedBots(): string[] {
    return [];
  }

  invalidate(): void {
    this.lastAt = -Infinity;
  }

  frame(rt: WorldRuntime, now: number): ViewSetup | null {
    const b = this.bot();
    if (!b || !this.ctx || !this.onScreen) return null;
    if (typeof document !== 'undefined' && document.visibilityState === 'hidden') return null;
    const fps = Math.max(1, Math.min(10, this.opts.fps ?? 3));
    if (now - this.lastAt < 1000 / fps || !rt.takeSlot()) return null;
    this.lastAt = now;
    const dpr = Math.min(2, globalThis.devicePixelRatio || 1);
    const cw = Math.max(1, Math.round(this.canvas.clientWidth * dpr)), ch = Math.max(1, Math.round(this.canvas.clientHeight * dpr));
    if (this.canvas.width !== cw || this.canvas.height !== ch) {
      this.canvas.width = cw;
      this.canvas.height = ch;
    }
    const [w, h] = rt.thumbSize(cw, ch);
    // Behind the bot, smoothed between renders, or the fixed Explorer angle.
    this.target.set(b.x, b.y + 1.3, b.z);
    const wantYaw = (this.opts.angle ?? 'behind') === 'behind' ? b.yaw : 315;
    if (Number.isNaN(this.yaw)) this.yaw = wantYaw;
    this.yaw += ((((wantYaw - this.yaw) % 360) + 540) % 360 - 180) * 0.35;
    this.camera.position.copy(this.target).addScaledVector(lookDir(this.yaw, this.opts.pitch ?? 28), -(this.opts.distance ?? 6));
    this.camera.lookAt(this.target);
    this.camera.far = this.drawDistance * 1.6 + 16;
    this.camera.updateMatrixWorld();
    // The cone toward the bot when the last test found it hidden (tested at the view's own rate, never blocking).
    const xray = this.opts.xray ?? true;
    if (xray && !this.busy) {
      this.busy = true;
      const c = this.camera.position;
      rt.host
        .viewInfo(b.dim, [c.x, c.y, c.z], [b.x, b.y, b.z], null)
        .then((i) => (this.cone = i.hidden || i.cameraInside))
        .catch(() => undefined)
        .finally(() => (this.busy = false));
    }
    return {
      dim: b.dim,
      camera: this.camera,
      centre: this.target,
      drawDistance: this.drawDistance,
      width: w,
      height: h,
      layers: { bots: true, entities: true, weather: false, staleness: true, changes: true },
      labels: false,
      overlays: false,
      selected: null,
      cones: xray && this.cone ? [{ x: b.x, y: b.y + 0.9, z: b.z }] : [],
      floorY: Math.floor(b.y + 0.01),
      roof: null,
      target: this.ctx,
    };
  }

  dispose(): void {
    this.observer?.disconnect();
  }
}

/** Registers `canvas` as a thumbnail of `follow` on the shared runtime. */
export function registerThumbnail(rt: WorldRuntime, canvas: HTMLCanvasElement, follow: string, opts: ThumbnailViewOptions = {}): ThumbnailHandle {
  const v = new ThumbnailView(rt, canvas, follow, opts);
  rt.addView(v);
  return {
    setFollow: (bot) => {
      v.follow = bot;
      v.invalidate();
    },
    setOptions: (o) => {
      v.opts = o;
      v.invalidate();
    },
    invalidate: () => v.invalidate(),
    dispose: () => {
      rt.removeView(v);
      v.dispose();
    },
  };
}
