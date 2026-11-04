/**
 * The memory cloud's draw passes (CloudCanvas.tsx draws one frame by aiming the painter at the frame, then at each pane, and
 * calling these passes in order): cluster density blobs below, the WebGL points / links / halos, then labels, rings, the scale
 * bar and the lasso above. One {@link CloudPainter} lives as long as the canvas and is re-aimed in place each frame and pane, so
 * a frame allocates nothing the single draw callback did not (the 40 fps software-WebGL budget).
 */
import type { CloudCluster } from '@mineclaw/contracts';
import { clusterColor, hexRgb, legendKey, linkAlpha, niceScale, paneFrame, zoomSizeFactor, type Rect, type Theme } from './cloudStyle';

/** One pane's GPU buffers: its positions (now and before the last switch), its links and its halos. */
export interface PaneGl {
  pos: WebGLBuffer;
  prev: WebGLBuffer;
  line: WebGLBuffer;
  halo: WebGLBuffer;
  vaoPoint: WebGLVertexArrayObject;
  vaoLine: WebGLVertexArrayObject;
  vaoHalo: WebGLVertexArrayObject;
  lineCount: number;
  haloCount: number;
}

export interface Gl {
  gl: WebGL2RenderingContext;
  point: WebGLProgram;
  halo: WebGLProgram;
  line: WebGLProgram;
  style: WebGLBuffer;
  panes: PaneGl[];
}

export interface View {
  k: number;
  tx: number;
  ty: number;
}

/** Where a pane's points, summaries and clusters are drawn this frame. */
export interface Drawn {
  xy: readonly number[];
  sxy: readonly number[];
  clusters: readonly CloudCluster[];
}

export interface CloudPulse {
  index: number;
  at: number;
}

export const PULSE_MS = 1600;
const LINK_INK = { dark: [0.85, 0.88, 0.92], light: [0.1, 0.13, 0.18] } as const;
const LABEL_FONT = '600 12px ui-sans-serif, system-ui, sans-serif';
const SCALE_FONT = '500 11px ui-sans-serif, system-ui, sans-serif';

export function cssVar(name: string, fallback: string): string {
  if (typeof window === 'undefined') return fallback;
  return getComputedStyle(document.documentElement).getPropertyValue(name).trim() || fallback;
}

function sizeCanvas(el: HTMLCanvasElement | null, w: number, h: number) {
  if (!el) return;
  if (el.width !== Math.round(w)) el.width = Math.round(w);
  if (el.height !== Math.round(h)) el.height = Math.round(h);
}

function clear2d(el: HTMLCanvasElement | null, dpr: number, w: number, h: number): CanvasRenderingContext2D | null {
  const ctx = el?.getContext('2d') ?? null;
  if (ctx) {
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    ctx.clearRect(0, 0, w, h);
  }
  return ctx;
}

function overlaps(a: readonly number[], b: readonly number[]): boolean {
  return a[0]! < b[0]! + b[2]! && b[0]! < a[0]! + a[2]! && a[1]! < b[1]! + b[3]! && b[1]! < a[1]! + a[3]!;
}

export class CloudPainter {
  /** The frame: canvas size (CSS px), pixel ratio, theme and its ink, the frame clock. */
  w = 0;
  h = 0;
  dpr = 1;
  theme: Theme = 'dark';
  text = '';
  bg = '';
  now = 0;
  /** Compare mode: each pane is scissored and framed. */
  multi = false;
  under: CanvasRenderingContext2D | null = null;
  over: CanvasRenderingContext2D | null = null;
  g: Gl | null = null;
  /** The pane: its rectangle, zoom, data → screen transform and switch-animation mix. */
  rect: Rect = { x: 0, y: 0, w: 0, h: 0 };
  k = 1;
  scale = 1;
  offX = 0;
  offY = 0;
  mix = 1;
  private readonly placed: Array<[number, number, number, number]> = [];

  sx(x: number): number {
    return x * this.scale + this.offX;
  }

  sy(y: number): number {
    return y * this.scale + this.offY;
  }

  /** Sizes and clears the three canvases and sets the frame's ink. */
  begin(
    under: HTMLCanvasElement | null,
    glCanvas: HTMLCanvasElement | null,
    over: HTMLCanvasElement | null,
    g: Gl | null,
    w: number,
    h: number,
    dpr: number,
    theme: Theme,
    multi: boolean,
  ) {
    this.w = w;
    this.h = h;
    this.dpr = dpr;
    this.theme = theme;
    this.multi = multi;
    this.g = g;
    sizeCanvas(under, w * dpr, h * dpr);
    sizeCanvas(glCanvas, w * dpr, h * dpr);
    sizeCanvas(over, w * dpr, h * dpr);
    this.under = clear2d(under, dpr, w, h);
    this.over = clear2d(over, dpr, w, h);
    if (g) this.clearGl(g.gl);
    this.text = cssVar('--text', theme === 'dark' ? '#e7e9ec' : '#14171c');
    this.bg = cssVar('--canvas-bg', theme === 'dark' ? '#0a0c0f' : '#eef1f5');
    this.now = performance.now();
  }

  private clearGl(gl: WebGL2RenderingContext) {
    gl.viewport(0, 0, Math.round(this.w * this.dpr), Math.round(this.h * this.dpr));
    gl.disable(gl.SCISSOR_TEST);
    gl.clearColor(0, 0, 0, 0);
    gl.clear(gl.COLOR_BUFFER_BIT);
    gl.enable(gl.BLEND);
    gl.blendFuncSeparate(gl.SRC_ALPHA, gl.ONE_MINUS_SRC_ALPHA, gl.ONE, gl.ONE_MINUS_SRC_ALPHA);
    if (this.multi) gl.enable(gl.SCISSOR_TEST);
  }

  /** Aims the painter at one pane: its rectangle, view and animation mix. */
  pane(rect: Rect, v: View, mix: number) {
    const f = paneFrame(rect);
    this.rect = rect;
    this.k = v.k;
    this.mix = mix;
    this.scale = f.base * v.k;
    this.offX = f.ox * v.k + v.tx;
    this.offY = f.oy * v.k + v.ty;
  }

  private clip(ctx: CanvasRenderingContext2D) {
    const r = this.rect;
    ctx.save();
    ctx.beginPath();
    ctx.rect(r.x, r.y, r.w, r.h);
    ctx.clip();
  }

  /** Below: cluster density blobs, strongest when zoomed out (the level of detail for a far view). */
  blobs(clusters: readonly CloudCluster[], hidden: ReadonlySet<number>) {
    const u = this.under;
    if (!u) return;
    this.clip(u);
    const blob = Math.max(0.03, Math.min(0.2, 0.22 / Math.sqrt(this.k)));
    for (const cl of clusters) {
      if (!hidden.has(legendKey(cl.id))) this.blob(u, cl, blob);
    }
    u.restore();
  }

  private blob(u: CanvasRenderingContext2D, cl: CloudCluster, alpha: number) {
    const rect = this.rect;
    const cx = this.sx(cl.x),
      cy = this.sy(cl.y);
    const r = Math.max(24, cl.spread * 2.4 * this.scale);
    if (cx + r < rect.x || cy + r < rect.y || cx - r > rect.x + rect.w || cy - r > rect.y + rect.h) return;
    const grad = u.createRadialGradient(cx, cy, 0, cx, cy, r);
    const [cr, cg, cb] = hexRgb(clusterColor(cl.id, this.theme)).map((x) => Math.round(x * 255));
    grad.addColorStop(0, `rgba(${cr},${cg},${cb},${alpha})`);
    grad.addColorStop(1, `rgba(${cr},${cg},${cb},0)`);
    u.fillStyle = grad;
    u.fillRect(cx - r, cy - r, 2 * r, 2 * r);
  }

  /** The points (WebGL): the faded links, every point, then the halos, scissored to the pane in compare mode. */
  points(g: Gl, pg: PaneGl, n: number) {
    const gl = g.gl;
    const rect = this.rect;
    const dpr = this.dpr;
    if (this.multi)
      gl.scissor(
        Math.round(rect.x * dpr),
        Math.round((this.h - rect.y - rect.h) * dpr),
        Math.round(rect.w * dpr),
        Math.round(rect.h * dpr),
      );
    if (pg.lineCount) {
      this.uniforms(gl, g.line);
      const ink = LINK_INK[this.theme];
      gl.uniform4f(gl.getUniformLocation(g.line, 'u_col'), ink[0], ink[1], ink[2], linkAlpha(this.k, pg.lineCount / 2, this.theme));
      gl.bindVertexArray(pg.vaoLine);
      gl.drawArrays(gl.LINES, 0, pg.lineCount);
    }
    this.uniforms(gl, g.point);
    gl.bindVertexArray(pg.vaoPoint);
    gl.drawArrays(gl.POINTS, 0, n);
    if (pg.haloCount) {
      this.uniforms(gl, g.halo);
      gl.bindVertexArray(pg.vaoHalo);
      gl.drawArrays(gl.POINTS, 0, pg.haloCount);
    }
    gl.bindVertexArray(null);
  }

  private uniforms(gl: WebGL2RenderingContext, p: WebGLProgram) {
    gl.useProgram(p);
    gl.uniform2f(gl.getUniformLocation(p, 'u_scale'), this.scale, this.scale);
    gl.uniform2f(gl.getUniformLocation(p, 'u_off'), this.offX, this.offY);
    gl.uniform2f(gl.getUniformLocation(p, 'u_res'), this.w, this.h);
    gl.uniform1f(gl.getUniformLocation(p, 'u_mix'), this.mix);
    const d = gl.getUniformLocation(p, 'u_dpr');
    if (d) gl.uniform1f(d, this.dpr);
    const ks = gl.getUniformLocation(p, 'u_ksize');
    if (ks) gl.uniform1f(ks, zoomSizeFactor(this.k));
  }

  /** Above: starts the pane's overlay (clipped to it) with the cluster labels, skipping any that would overlap a placed one. */
  beginOverlay(o: CanvasRenderingContext2D, clusters: readonly CloudCluster[], hidden: ReadonlySet<number>) {
    this.clip(o);
    o.font = LABEL_FONT;
    o.textBaseline = 'middle';
    this.placed.length = 0;
    for (const cl of clusters) {
      if (!hidden.has(legendKey(cl.id))) this.label(o, cl);
    }
  }

  private label(o: CanvasRenderingContext2D, cl: CloudCluster) {
    const rect = this.rect;
    const x = this.sx(cl.x),
      y = this.sy(cl.y) - Math.min(60, cl.spread * this.scale * 1.1) - 8;
    if (x < rect.x - 100 || y < rect.y - 20 || x > rect.x + rect.w + 100 || y > rect.y + rect.h + 20) return;
    const tw = o.measureText(cl.label).width + 16;
    const box: [number, number, number, number] = [x - tw / 2, y - 9, tw, 18];
    if (this.placed.some((b) => overlaps(box, b))) return;
    this.placed.push(box);
    o.lineWidth = 4;
    o.strokeStyle = this.bg;
    o.lineJoin = 'round';
    o.strokeText(cl.label, x - tw / 2 + 14, y);
    o.fillStyle = this.text;
    o.fillText(cl.label, x - tw / 2 + 14, y);
    o.beginPath();
    o.arc(x - tw / 2 + 6, y, 4, 0, Math.PI * 2);
    o.fillStyle = clusterColor(cl.id, this.theme);
    o.fill();
  }

  /** A ring around point `i` (positions `xy`). */
  ring(o: CanvasRenderingContext2D, xy: readonly number[], i: number, r: number, color: string, width: number, alpha = 1) {
    o.globalAlpha = alpha;
    o.beginPath();
    o.arc(this.sx(xy[2 * i]!), this.sy(xy[2 * i + 1]!), r, 0, Math.PI * 2);
    o.strokeStyle = color;
    o.lineWidth = width;
    o.stroke();
    o.globalAlpha = 1;
  }

  /** Faint rings on the lassoed points that are shown. */
  lassoRings(o: CanvasRenderingContext2D, xy: readonly number[], lasso: readonly number[], mask: ArrayLike<number | boolean>) {
    for (const i of lasso) if (mask[i]) this.ring(o, xy, i, 5, this.text, 1, 0.55);
  }

  /** Live pulses growing out of their points; true while any is still fading (another frame is wanted). */
  pulses(
    o: CanvasRenderingContext2D,
    xy: readonly number[],
    pulses: readonly CloudPulse[],
    mask: ArrayLike<number | boolean>,
    clusterOf: readonly number[],
  ): boolean {
    let pulsing = false;
    for (const p of pulses) {
      const age = (this.now - p.at) / PULSE_MS;
      if (age >= 1 || !mask[p.index]) continue;
      pulsing = true;
      if (age < 0) continue; // staggered: starts a little later
      this.ring(o, xy, p.index, 4 + 16 * age, clusterColor(clusterOf[p.index]!, this.theme), 1.5, (1 - age) * 0.8);
    }
    return pulsing;
  }

  /** The dashed ring around a selected summary. */
  summaryRing(o: CanvasRenderingContext2D, sxy: readonly number[], s: number) {
    o.beginPath();
    o.arc(this.sx(sxy[2 * s]!), this.sy(sxy[2 * s + 1]!), 10, 0, Math.PI * 2);
    o.strokeStyle = this.text;
    o.lineWidth = 2;
    o.setLineDash([3, 3]);
    o.stroke();
    o.setLineDash([]);
  }

  /** Scale bar (metric arrangements): a round cosine distance at this zoom, bottom left of the pane. Returns what it drew. */
  scaleBar(o: CanvasRenderingContext2D, metric: boolean, unitDistance: number | null) {
    const bar = metric && unitDistance ? niceScale(unitDistance, this.scale) : null;
    if (!bar) return null;
    const bx = this.rect.x + 14,
      by = this.rect.y + this.rect.h - 16;
    o.strokeStyle = this.text;
    o.globalAlpha = 0.75;
    o.lineWidth = 1.5;
    o.beginPath();
    o.moveTo(bx, by - 4);
    o.lineTo(bx, by);
    o.lineTo(bx + bar.px, by);
    o.lineTo(bx + bar.px, by - 4);
    o.stroke();
    o.globalAlpha = 1;
    o.font = SCALE_FONT;
    o.textBaseline = 'bottom';
    o.lineWidth = 3;
    o.strokeStyle = this.bg;
    o.strokeText(bar.label, bx, by - 6);
    o.fillStyle = this.text;
    o.fillText(bar.label, bx, by - 6);
    return bar;
  }

  /** The lasso being drawn (screen points). */
  lassoPath(o: CanvasRenderingContext2D, path: ReadonlyArray<readonly [number, number]>) {
    o.beginPath();
    o.moveTo(path[0]![0], path[0]![1]);
    for (const [x, y] of path) o.lineTo(x, y);
    o.closePath();
    o.fillStyle = this.theme === 'dark' ? 'rgba(255,255,255,0.06)' : 'rgba(0,0,0,0.05)';
    o.fill();
    o.setLineDash([4, 3]);
    o.strokeStyle = this.text;
    o.lineWidth = 1;
    o.stroke();
    o.setLineDash([]);
  }

  /** Ends the pane's overlay (unclips) and, in compare mode, draws its hairline frame. */
  endOverlay(o: CanvasRenderingContext2D) {
    o.restore();
    if (!this.multi) return;
    const rect = this.rect;
    o.strokeStyle = this.theme === 'dark' ? 'rgba(255,255,255,0.10)' : 'rgba(0,0,0,0.10)';
    o.lineWidth = 1;
    o.strokeRect(rect.x + 0.5, rect.y + 0.5, rect.w - 1, rect.h - 1);
  }
}
