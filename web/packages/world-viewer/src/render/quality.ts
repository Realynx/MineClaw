// Quality profiles and the frame-time controller. The user's everyday browser runs WebGL in software (hardware acceleration
// off: SwiftShader / ANGLE-on-SwiftShader / llvmpipe / Microsoft Basic Render Driver), so the viewer detects that and picks a
// "software" profile aiming at 22 ms of measured work (40 fps once the compositor adds its share); with a GPU 16 ms (60 fps). The controller times one primary
// frame every second with asynchronous GPU queries (CPU time when unavailable) and turns the levers in
// order: render resolution, then draw distance, then (software) translucent and entity detail distances; it gives them back
// in reverse order when there is headroom.

export type QualityName = 'gpu' | 'software';

export interface QualityProfile {
  name: QualityName;
  /** Frame-time target (ms). */
  targetMs: number;
  antialias: boolean;
  /** Device-pixel-ratio cap for the primary view. */
  pixelRatioCap: number;
  /** Lowest render scale the controller may use. */
  minScale: number;
  /** Draw distance cap (blocks) and the lowest the controller may go. */
  maxDraw: number;
  minDraw: number;
  /** Translucent sections (water, glass) beyond this many blocks are skipped. */
  translucentDistance: number;
  /** Entities beyond this distance are drawn as plain boxes. */
  entityDetailDistance: number;
  /** Animated texture updates per second (game ticks). */
  animHz: number;
  /** Most animated atlas cells updated per tick (0 = all). */
  animCellsPerTick: number;
  /** Thumbnail rate multiplier and render slots per frame. */
  thumbFpsScale: number;
  thumbSlots: number;
  /** Section cave culling (visibility graph). */
  caveCulling: boolean;
  /** Extra sky passes (clouds, stars). */
  clouds: boolean;
}

export const PROFILES: Record<QualityName, QualityProfile> = {
  gpu: {
    name: 'gpu', targetMs: 16, antialias: true, pixelRatioCap: 2, minScale: 0.6, maxDraw: 256, minDraw: 64, translucentDistance: 256,
    entityDetailDistance: 64, animHz: 20, animCellsPerTick: 0, thumbFpsScale: 1, thumbSlots: 2, caveCulling: true, clouds: true,
  },
  software: {
    // 22 ms of measured work leaves room for the compositor's own (unmeasured) present, so the page holds 40 fps.
    name: 'software', targetMs: 22, antialias: false, pixelRatioCap: 1, minScale: 0.5, maxDraw: 128, minDraw: 48, translucentDistance: 64,
    entityDetailDistance: 24, animHz: 10, animCellsPerTick: 8, thumbFpsScale: 0.5, thumbSlots: 1, caveCulling: true, clouds: false,
  },
};

const SOFTWARE = /swiftshader|llvmpipe|softpipe|basic render driver|software|mesa offscreen|lavapipe/i;

export interface RendererProbe {
  software: boolean;
  renderer: string;
  /** Why it was judged software (or not). */
  reason: string;
}

/** Is WebGL running on a software rasteriser here? (No context is kept: the probe's canvas is dropped at once.) */
export function probeRenderer(): RendererProbe {
  if (typeof document === 'undefined') return { software: true, renderer: 'none', reason: 'no DOM' };
  try {
    const c = document.createElement('canvas');
    const strict = c.getContext('webgl2', { failIfMajorPerformanceCaveat: true });
    const ctx = strict ?? document.createElement('canvas').getContext('webgl2');
    if (!ctx) return { software: true, renderer: 'none', reason: 'no WebGL2' };
    const ext = ctx.getExtension('WEBGL_debug_renderer_info');
    const renderer = String(ext ? ctx.getParameter(ext.UNMASKED_RENDERER_WEBGL) : ctx.getParameter(ctx.RENDERER));
    ctx.getExtension('WEBGL_lose_context')?.loseContext();
    if (!strict) return { software: true, renderer, reason: 'failIfMajorPerformanceCaveat refused a context' };
    if (SOFTWARE.test(renderer)) return { software: true, renderer, reason: `renderer "${renderer}"` };
    return { software: false, renderer, reason: `renderer "${renderer}"` };
  } catch (e) {
    return { software: true, renderer: 'error', reason: String(e) };
  }
}

let probed: RendererProbe | null = null;

/** `probeRenderer`, run once per page (a settings panel and every runtime share the answer). */
export function probeRendererOnce(): RendererProbe {
  return (probed ??= probeRenderer());
}

/** The preset a settings panel suggests for this renderer: the tuned Auto profile in software, High with a GPU. */
export function recommendedPreset(p: Pick<RendererProbe, 'software'>): 'auto' | 'high' {
  return p.software ? 'auto' : 'high';
}

/** The levers the controller moves (read by the runtime each frame). */
export interface QualityState {
  profile: QualityProfile;
  /** Render resolution factor (× pixel ratio). */
  scale: number;
  /** Draw distance factor (× the view's own distance, capped by the profile). */
  drawFactor: number;
  /** Last timed frame (ms) and its smoothed value. */
  frameMs: number;
  smoothMs: number;
}

export class QualityController {
  readonly state: QualityState;
  private lastSample = 0;
  enabled = true;

  constructor(profile: QualityProfile) {
    this.state = { profile, scale: 1, drawFactor: 1, frameMs: 0, smoothMs: 0 };
  }

  setProfile(p: QualityProfile): void {
    this.state.profile = p;
    this.state.scale = Math.max(this.state.scale, p.minScale);
  }

  /** Should this frame be timed to completion? (once a second) */
  due(now: number): boolean {
    return this.enabled && now - this.lastSample >= 1000;
  }

  /** Feeds one timed frame; moves at most one lever one step. */
  sample(now: number, ms: number): void {
    this.lastSample = now;
    const s = this.state, p = s.profile;
    s.frameMs = ms;
    s.smoothMs = s.smoothMs ? s.smoothMs * 0.6 + ms * 0.4 : ms;
    const t = p.targetMs;
    if (s.smoothMs > t * 1.08) {
      if (s.scale > p.minScale + 1e-3) s.scale = Math.max(p.minScale, s.scale * 0.88);
      else if (s.drawFactor > p.minDraw / p.maxDraw + 1e-3) s.drawFactor = Math.max(p.minDraw / p.maxDraw, s.drawFactor * 0.85);
    } else if (s.smoothMs < t * 0.6) {
      if (s.drawFactor < 1) s.drawFactor = Math.min(1, s.drawFactor * 1.12);
      else if (s.scale < 1) s.scale = Math.min(1, s.scale * 1.1);
    }
  }

  /** The draw distance a view asking for `want` blocks gets now. */
  drawDistance(want: number): number {
    const p = this.state.profile;
    return Math.max(Math.min(p.minDraw, want), Math.min(want, p.maxDraw) * this.state.drawFactor);
  }
}
