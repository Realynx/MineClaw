// The user's viewer quality settings, one module-level store shared by every WorldRuntime of the page (the hive runtime, Story
// Mode's, the Design Studio's private one): the app pushes a resolved `ViewerQuality` with `setViewerQuality` and every
// runtime reads it at the start of its next frame (no reload, no remount). Fields left null take the detected quality
// profile's own value (the "Auto" preset). The runtime also reports what it measured (`viewerStatus`), so a settings panel can
// show the live fps and whether WebGL runs in software. No three.js here: the app imports this through the light
// `@mineclaw/world-viewer/settings` entry without pulling in the renderer.

import type { QualityProfile } from './quality';

/** How far mobs, players and drops are drawn: as far as the ground ('ground'), or this many blocks. */
export type EntityDistance = 'ground' | number;

export interface ViewerQuality {
  /** Draw distance of interactive perspective views (blocks, 32..256). A view's own `drawDistance` prop can only lower it. */
  drawDistance: number;
  /** Top-down / iso views: the most ground drawn around the look-at point (blocks, 64..192). */
  coverCap: number;
  /** Mobs, players and drops: as far as the ground ('ground': top-down / iso views cover their whole picture), or blocks. */
  entityDistance: EntityDistance;
  /** Dropped items and experience orbs. */
  drops: boolean;
  /** Render pixels per CSS pixel (0.25..2, never above the screen's own ratio); null = the profile's (1 software, 2 GPU). */
  renderScale: number | null;
  /** Frames per second at most; null = every animation frame. */
  fpsCap: number | null;
  /** The frame-time controller may lower resolution and draw distance to hold the profile's target. */
  adaptive: boolean;
  /** Water, lava, fire and portal texture animation. */
  animatedTextures: boolean;
  /** Clouds; null = the profile's (off in software). */
  clouds: boolean | null;
  /** Rain and snow (and the darker rainy sky). */
  weather: boolean;
  /** Corner shading where blocks meet (the mesher's smooth-light occlusion term). */
  ambientOcclusion: boolean;
  /** Distance fog at the edge of the drawn world. */
  fog: boolean;
  /** Full animated models within this distance, simple boxes beyond (blocks of apparent distance); null = the profile's. */
  entityModelDistance: number | null;
  /** Name tags over bots, players, mobs and drops. */
  labels: boolean;
  /** Chat bubbles over speakers. */
  bubbles: boolean;
  /** Silhouettes of bots and players hidden behind blocks. */
  xray: boolean;
  /** A small fps / frame-time readout over every interactive view. */
  perfOverlay: boolean;
  /** Story Mode's queued builds as translucent block holograms in the world (off: flat outlines on the map). */
  holograms: boolean;
}

/** Today's behaviour, except that entities reach as far as the ground (top-down / iso views zoomed out keep their mobs). */
export const DEFAULT_VIEWER_QUALITY: Readonly<ViewerQuality> = Object.freeze({
  drawDistance: 128,
  coverCap: 192,
  entityDistance: 'ground',
  drops: true,
  renderScale: null,
  fpsCap: null,
  adaptive: true,
  animatedTextures: true,
  clouds: null,
  weather: true,
  ambientOcclusion: true,
  fog: true,
  entityModelDistance: null,
  labels: true,
  bubbles: true,
  xray: true,
  perfOverlay: false,
  holograms: true,
});

/** The bounds the renderer honours (the app's schema clamps to these too). */
export const QUALITY_LIMITS = {
  drawDistance: [32, 256],
  coverCap: [64, 192],
  entityDistance: [16, 256],
  renderScale: [0.25, 2],
  entityModelDistance: [0, 128],
} as const;

const clamp = (v: number, [lo, hi]: readonly [number, number]) => Math.max(lo, Math.min(hi, v));

let current: ViewerQuality = { ...DEFAULT_VIEWER_QUALITY };
let rev = 0;
const listeners = new Set<() => void>();

/** Replaces (fields given) the settings every runtime applies from its next frame. */
export function setViewerQuality(patch: Partial<ViewerQuality>): void {
  const next = { ...current, ...patch };
  next.drawDistance = clamp(Math.round(next.drawDistance), QUALITY_LIMITS.drawDistance);
  next.coverCap = clamp(Math.round(next.coverCap), QUALITY_LIMITS.coverCap);
  if (next.entityDistance !== 'ground') next.entityDistance = clamp(next.entityDistance, QUALITY_LIMITS.entityDistance);
  if (next.renderScale !== null) next.renderScale = clamp(next.renderScale, QUALITY_LIMITS.renderScale);
  if (next.fpsCap !== null) next.fpsCap = next.fpsCap > 0 ? Math.max(10, next.fpsCap) : null;
  if (next.entityModelDistance !== null) next.entityModelDistance = clamp(next.entityModelDistance, QUALITY_LIMITS.entityModelDistance);
  if ((Object.keys(next) as Array<keyof ViewerQuality>).every((k) => next[k] === current[k])) return;
  current = next;
  rev++;
  for (const l of listeners) l();
}

export function resetViewerQuality(): void {
  setViewerQuality(DEFAULT_VIEWER_QUALITY);
}

export function getViewerQuality(): Readonly<ViewerQuality> {
  return current;
}

/** Bumped on every change (runtimes re-apply when it moves). */
export function viewerQualityRev(): number {
  return rev;
}

export function subscribeViewerQuality(fn: () => void): () => void {
  listeners.add(fn);
  return () => listeners.delete(fn);
}

/** The quality profile a runtime runs with: its detected (or forced) profile with the user's settings laid over it. */
export function effectiveProfile(base: QualityProfile, q: Readonly<ViewerQuality>): QualityProfile {
  return {
    ...base,
    maxDraw: q.drawDistance,
    minDraw: Math.min(base.minDraw, q.drawDistance),
    pixelRatioCap: q.renderScale ?? base.pixelRatioCap,
    entityDetailDistance: q.entityModelDistance ?? base.entityDetailDistance,
    clouds: q.clouds ?? base.clouds,
  };
}

/**
 * How far one view draws entities and from where. Top-down / iso views (cover > 0) measure across the ground from the point
 * they look at (their camera hangs hundreds of blocks above it); the others measure from the camera. "Same as ground" is the
 * picture's ground (the cover) or the drawn distance; a number never reaches past the ground either.
 */
export function entityReach(q: Readonly<ViewerQuality>, cover: number, groundDistance: number): { radius: number; fromCentre: boolean } {
  const ground = cover > 0 ? cover : groundDistance;
  const radius = q.entityDistance === 'ground' ? ground : Math.min(q.entityDistance, ground);
  return { radius, fromCentre: cover > 0 };
}

/**
 * Frame limiter for the fps cap: true when this animation frame should draw. Keeps the average at the cap (a 40 fps cap on a
 * 60 Hz display draws two frames of three) and never bursts to catch up after a stall.
 */
export class FrameLimiter {
  private next = 0;

  due(now: number, cap: number | null): boolean {
    if (!cap) {
      this.next = 0;
      return true;
    }
    const iv = 1000 / cap;
    if (now < this.next - 1) return false;
    // More than a whole interval late (a stall, a hidden tab): start the schedule again from now.
    this.next = now - this.next > iv ? now + iv : this.next + iv;
    return true;
  }
}

// ------------------------------------------------------------------------------------------------------------ status

export interface ViewerStatus {
  /** Frames per second the primary (interactive) view drew lately. */
  fps: number;
  /** Smoothed timed frame (CPU + GPU), ms. */
  frameMs: number;
  /** The controller's resolution scale and draw-distance factor. */
  scale: number;
  drawFactor: number;
  /** Profile in use ('software' or 'gpu') and the renderer string. */
  profile: string;
  software: boolean;
  renderer: string;
  /** Entities the primary view drew last frame and how far it reached (blocks; measured from its look-at point in top / iso). */
  entities: number;
  entityRadius: number;
  /** Draw distance the primary view used last frame (blocks; top / iso: its ground). */
  drawDistance: number;
  /** performance.now() of the report. */
  at: number;
}

let status: ViewerStatus | null = null;

/** Runtimes report here after each primary render. */
export function reportViewerStatus(s: ViewerStatus): void {
  status = s;
}

/** The latest report of any runtime's interactive view, or null when none drew in the last two seconds. */
export function viewerStatus(now = typeof performance !== 'undefined' ? performance.now() : 0): ViewerStatus | null {
  return status && now - status.at < 2000 ? status : null;
}
