'use client';

/**
 * The memory cloud renderer: one WebGL2 context draws every point (and the faded link overlay) from GPU buffers, so pan and zoom
 * cost one draw call each frame however many memories there are. Compare mode draws 2–4 arrangements side by side from the same
 * context (one viewport, a scissor rectangle and a buffer set per pane), with hover, selection, search, lasso and heat linked
 * across panes (they share the per-point style buffer). Two 2-D canvases sit around it: cluster density blobs below (they carry
 * the picture when zoomed out) and labels, rings, pulses, scale bars and the lasso above. Switching a pane's arrangement animates
 * every point from where it was (the vertex shader mixes old and new positions). It draws on demand only (a change, an
 * interaction, an animation or a pulse still fading), never in a free-running loop.
 */
import type { CloudCluster, MemoryCloudResponse } from '@mineclaw/contracts';
import { forwardRef, useCallback, useEffect, useImperativeHandle, useMemo, useRef, useState, type ReactNode } from 'react';
import { cn } from '@/lib/utils';
import {
  buildGrid,
  clusterColor,
  easeInOut,
  hexRgb,
  lassoSelect,
  legendKey,
  linkAlpha,
  nearest,
  niceScale,
  paneFrame,
  paneRects,
  pointLook,
  visibleMask,
  zoomSizeFactor,
  type Rect,
  type Theme,
} from './cloudStyle';

export interface CloudCanvasHandle {
  fit: () => void;
  reveal: (id: number) => void;
}

export interface CloudPulse {
  index: number;
  at: number;
}

/** One arrangement drawn in one pane: positions in the primary cloud's point / summary order. */
export interface CloudPane {
  key: string;
  xy: readonly number[];
  sxy: readonly number[];
  clusters: readonly CloudCluster[];
  /** Drawn distance is meant to be read as embedding distance: a scale bar is drawn. */
  metric: boolean;
  /** Cosine distance per unit of the unit square (the arrangement's best-fit scale). */
  unitDistance: number | null;
}

interface View {
  k: number;
  tx: number;
  ty: number;
}

const PULSE_MS = 1600;
const ANIM_MS = 650;
const MAX_K = 60;
/** Width the legend card takes, left clear in the fitted view on canvases at least LEGEND_ROOM_MIN_W wide. */
export const LEGEND_W = 290;
export const LEGEND_ROOM_MIN_W = 720;

const VS = `#version 300 es
in vec2 a_pos;
in vec2 a_prev;
in vec4 a_col;
in float a_size;
uniform vec2 u_scale;
uniform vec2 u_off;
uniform vec2 u_res;
uniform float u_dpr;
uniform float u_mix;
uniform float u_ksize;
out vec4 v_col;
void main() {
  vec2 p = mix(a_prev, a_pos, u_mix) * u_scale + u_off;
  gl_Position = vec4(p.x / u_res.x * 2.0 - 1.0, 1.0 - p.y / u_res.y * 2.0, 0.0, 1.0);
  gl_PointSize = a_size * u_ksize * u_dpr;
  v_col = a_col;
}`;

const FS_POINT = `#version 300 es
precision mediump float;
in vec4 v_col;
out vec4 o;
void main() {
  float d = length(gl_PointCoord - 0.5) * 2.0;
  float a = v_col.a * (1.0 - smoothstep(0.62, 1.0, d));
  if (a < 0.004) discard;
  o = vec4(v_col.rgb, a);
}`;

const FS_HALO = `#version 300 es
precision mediump float;
in vec4 v_col;
out vec4 o;
void main() {
  vec2 c = gl_PointCoord - 0.5;
  float a = v_col.a * exp(-dot(c, c) * 14.0);
  if (a < 0.004) discard;
  o = vec4(v_col.rgb, a);
}`;

const VS_LINE = `#version 300 es
in vec2 a_pos;
in vec2 a_prev;
uniform vec2 u_scale;
uniform vec2 u_off;
uniform vec2 u_res;
uniform float u_mix;
void main() {
  vec2 p = mix(a_prev, a_pos, u_mix) * u_scale + u_off;
  gl_Position = vec4(p.x / u_res.x * 2.0 - 1.0, 1.0 - p.y / u_res.y * 2.0, 0.0, 1.0);
}`;

const FS_LINE = `#version 300 es
precision mediump float;
uniform vec4 u_col;
out vec4 o;
void main() { o = u_col; }`;

function compile(gl: WebGL2RenderingContext, vs: string, fs: string): WebGLProgram {
  const p = gl.createProgram();
  for (const [type, src] of [
    [gl.VERTEX_SHADER, vs],
    [gl.FRAGMENT_SHADER, fs],
  ] as const) {
    const s = gl.createShader(type)!;
    gl.shaderSource(s, src);
    gl.compileShader(s);
    if (!gl.getShaderParameter(s, gl.COMPILE_STATUS)) throw new Error(gl.getShaderInfoLog(s) ?? 'shader');
    gl.attachShader(p, s);
  }
  gl.linkProgram(p);
  if (!gl.getProgramParameter(p, gl.LINK_STATUS)) throw new Error(gl.getProgramInfoLog(p) ?? 'link');
  return p;
}

/** One pane's GPU buffers: its positions (now and before the last switch), its links and its halos. */
interface PaneGl {
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

interface Gl {
  gl: WebGL2RenderingContext;
  point: WebGLProgram;
  halo: WebGLProgram;
  line: WebGLProgram;
  style: WebGLBuffer;
  panes: PaneGl[];
}

/** A pane's switch animation: where its points (summaries, clusters) were drawn when its arrangement changed. */
interface Anim {
  xy: readonly number[];
  sxy: readonly number[];
  clusters: readonly CloudCluster[];
  start: number;
}

function cssVar(name: string, fallback: string): string {
  if (typeof window === 'undefined') return fallback;
  return getComputedStyle(document.documentElement).getPropertyValue(name).trim() || fallback;
}

/** Frame stats for the perf harness (`window.__mineclawCloudStats`); written in place, no DOM churn. */
interface Stats {
  frames: number;
  drawMs: number;
  points: number;
  panes: number;
  renderer?: string;
}

function mixArr(a: readonly number[], b: readonly number[], t: number): number[] {
  const out = new Array<number>(b.length);
  for (let i = 0; i < b.length; i++) {
    const av = a[i];
    out[i] = av === undefined ? b[i]! : av + (b[i]! - av) * t;
  }
  return out;
}

export const CloudCanvas = forwardRef<
  CloudCanvasHandle,
  {
    /** The primary arrangement: point ids, clusters, parents and summaries (every pane shares its point order). */
    cloud: MemoryCloudResponse;
    panes: readonly CloudPane[];
    /** Per point (same order as `cloud.points`), 0..1. */
    brightness: Float32Array;
    theme: Theme;
    hidden: ReadonlySet<number>;
    /** Per point: 1 = search/hit match. Null = no search (nothing dimmed). */
    highlight: Uint8Array | null;
    selectedId: number | null;
    showLinks: boolean;
    pulses: readonly CloudPulse[];
    lasso: readonly number[];
    onHover: (index: number | null, x: number, y: number, pane: number) => void;
    onSelect: (id: number | null) => void;
    onLasso: (indexes: number[]) => void;
    /** Rendered over each pane (compare mode: its title), positioned at the pane's top left. */
    paneOverlay?: (pane: number, rect: Rect) => ReactNode;
    className?: string;
    label?: string;
  }
>(function CloudCanvas(
  {
    cloud,
    panes,
    brightness,
    theme,
    hidden,
    highlight,
    selectedId,
    showLinks,
    pulses,
    lasso,
    onHover,
    onSelect,
    onLasso,
    paneOverlay,
    className,
    label = 'Memory cloud',
  },
  ref,
) {
  const wrap = useRef<HTMLDivElement>(null);
  const under = useRef<HTMLCanvasElement>(null);
  const glCanvas = useRef<HTMLCanvasElement>(null);
  const over = useRef<HTMLCanvasElement>(null);
  const glr = useRef<Gl | null>(null);
  const [glError, setGlError] = useState<string | null>(null);
  const [size, setSize] = useState({ w: 0, h: 0 });
  const views = useRef<View[]>([{ k: 1, tx: 0, ty: 0 }]);
  const active = useRef(0);
  const raf = useRef(0);
  const hoverIx = useRef<number | null>(null);
  const lassoPath = useRef<{ pane: number; path: Array<[number, number]> } | null>(null);
  const drag = useRef<{ pane: number; x: number; y: number; tx: number; ty: number; moved: boolean } | null>(null);
  const stats = useRef<Stats>({ frames: 0, drawMs: 0, points: 0, panes: 0 });
  // Bumped when the context comes back so buffers are re-uploaded.
  const [gen, setGen] = useState(0);
  const drawRef = useRef<() => void>(() => undefined);
  const schedule = useCallback(() => {
    if (!raf.current && typeof window !== 'undefined') raf.current = requestAnimationFrame(() => drawRef.current());
  }, []);
  const n = cloud.points.id.length;
  const count = panes.length;
  const grids = useMemo(() => panes.map((p) => buildGrid(p.xy)), [panes]);
  const mask = useMemo(() => visibleMask(cloud, hidden), [cloud, hidden]);
  const indexOf = useMemo(() => new Map(cloud.points.id.map((id, i) => [id, i])), [cloud.points.id]);
  const summaryIndexOf = useMemo(() => new Map(cloud.summaries.id.map((id, i) => [id, i])), [cloud.summaries.id]);
  const rects = useMemo(() => paneRects(size.w, size.h, count, LEGEND_W, LEGEND_ROOM_MIN_W), [size, count]);
  // Latest props for the draw callback (it runs from rAF, outside React's render).
  const props = useRef({ cloud, panes, brightness, theme, hidden, highlight, selectedId, showLinks, pulses, lasso, mask });
  props.current = { cloud, panes, brightness, theme, hidden, highlight, selectedId, showLinks, pulses, lasso, mask };

  // Pane switch animations: a slot whose positions change (same points) animates from where it was drawn.
  const shown = useRef<Array<{ key: string; xy: readonly number[]; sxy: readonly number[]; clusters: readonly CloudCluster[] }>>([]);
  const anims = useRef<Array<Anim | null>>([]);
  const mixOf = (slot: number, now = performance.now()): number => {
    const a = anims.current[slot];
    if (!a) return 1;
    const t = (now - a.start) / ANIM_MS;
    if (t >= 1) {
      anims.current[slot] = null;
      return 1;
    }
    return easeInOut(t);
  };

  if (views.current.length !== count) {
    views.current = Array.from({ length: count }, (_, i) => views.current[i] ?? { k: 1, tx: 0, ty: 0 });
    active.current = Math.min(active.current, count - 1);
  }

  // ---- GL setup (once per canvas) ----
  useEffect(() => {
    const el = glCanvas.current;
    if (!el) return;
    const init = () => {
      const gl = el.getContext('webgl2', {
        antialias: false,
        premultipliedAlpha: true,
        alpha: true,
        preserveDrawingBuffer: false,
      });
      if (!gl) {
        setGlError('This browser has WebGL 2 turned off, so the cloud cannot be drawn.');
        return;
      }
      try {
        const point = compile(gl, VS, FS_POINT);
        const halo = compile(gl, VS, FS_HALO);
        const line = compile(gl, VS_LINE, FS_LINE);
        glr.current = { gl, point, halo, line, style: gl.createBuffer(), panes: [] };
        const info = gl.getExtension('WEBGL_debug_renderer_info');
        stats.current.renderer = String(gl.getParameter(info ? info.UNMASKED_RENDERER_WEBGL : gl.RENDERER));
        setGlError(null);
      } catch (e) {
        setGlError(e instanceof Error ? e.message : 'WebGL setup failed');
      }
    };
    const lost = (e: Event) => {
      e.preventDefault();
      glr.current = null;
    };
    const restored = () => {
      init();
      setGen((g) => g + 1);
    };
    el.addEventListener('webglcontextlost', lost);
    el.addEventListener('webglcontextrestored', restored);
    init();
    return () => {
      el.removeEventListener('webglcontextlost', lost);
      el.removeEventListener('webglcontextrestored', restored);
      glr.current = null;
    };
  }, []);

  /** The pane's buffers and vertex arrays (created on first use). */
  const paneGl = useCallback((g: Gl, slot: number): PaneGl => {
    const existing = g.panes[slot];
    if (existing) return existing;
    const gl = g.gl;
    const pos = gl.createBuffer();
    const prev = gl.createBuffer();
    const line = gl.createBuffer();
    const halo = gl.createBuffer();
    const attr = (prog: WebGLProgram, name: string, buf: WebGLBuffer, size: number, stride: number, offset: number) => {
      const loc = gl.getAttribLocation(prog, name);
      if (loc < 0) return;
      gl.bindBuffer(gl.ARRAY_BUFFER, buf);
      gl.enableVertexAttribArray(loc);
      gl.vertexAttribPointer(loc, size, gl.FLOAT, false, stride, offset);
    };
    const vaoPoint = gl.createVertexArray();
    gl.bindVertexArray(vaoPoint);
    attr(g.point, 'a_pos', pos, 2, 0, 0);
    attr(g.point, 'a_prev', prev, 2, 0, 0);
    attr(g.point, 'a_col', g.style, 4, 20, 0);
    attr(g.point, 'a_size', g.style, 1, 20, 16);
    const vaoHalo = gl.createVertexArray();
    gl.bindVertexArray(vaoHalo);
    attr(g.halo, 'a_pos', halo, 2, 36, 0);
    attr(g.halo, 'a_prev', halo, 2, 36, 8);
    attr(g.halo, 'a_col', halo, 4, 36, 16);
    attr(g.halo, 'a_size', halo, 1, 36, 32);
    const vaoLine = gl.createVertexArray();
    gl.bindVertexArray(vaoLine);
    attr(g.line, 'a_pos', line, 2, 16, 0);
    attr(g.line, 'a_prev', line, 2, 16, 8);
    gl.bindVertexArray(null);
    const p: PaneGl = { pos, prev, line, halo, vaoPoint, vaoLine, vaoHalo, lineCount: 0, haloCount: 0 };
    g.panes[slot] = p;
    return p;
  }, []);

  /** Where a slot's points are drawn right now (mid-animation: between the two arrangements). */
  const drawnNow = (slot: number) => {
    const s = shown.current[slot];
    const a = anims.current[slot];
    if (!s) return null;
    if (!a) return { xy: s.xy, sxy: s.sxy, clusters: s.clusters };
    const t = mixOf(slot);
    return {
      xy: mixArr(a.xy, s.xy, t),
      sxy: mixArr(a.sxy, s.sxy, t),
      clusters: s.clusters.map((c, i) => {
        const o = a.clusters[i];
        return o ? { ...c, x: o.x + (c.x - o.x) * t, y: o.y + (c.y - o.y) * t, spread: o.spread + (c.spread - o.spread) * t } : c;
      }),
    };
  };

  // ---- positions (and the switch animation) per pane ----
  useEffect(() => {
    const now = performance.now();
    for (let slot = 0; slot < panes.length; slot++) {
      const p = panes[slot]!;
      const old = shown.current[slot];
      if (old && old.xy === p.xy && old.sxy === p.sxy) continue;
      if (old && old.xy.length === p.xy.length) {
        const from = drawnNow(slot)!;
        anims.current[slot] = { xy: from.xy, sxy: from.sxy, clusters: from.clusters, start: now };
      } else anims.current[slot] = null;
      shown.current[slot] = { key: p.key, xy: p.xy, sxy: p.sxy, clusters: p.clusters };
    }
    shown.current.length = panes.length;
    anims.current.length = panes.length;
    const g = glr.current;
    if (g) {
      for (let slot = 0; slot < panes.length; slot++) {
        const pg = paneGl(g, slot);
        const s = shown.current[slot]!;
        const a = anims.current[slot];
        g.gl.bindBuffer(g.gl.ARRAY_BUFFER, pg.pos);
        g.gl.bufferData(g.gl.ARRAY_BUFFER, new Float32Array(s.xy), g.gl.STATIC_DRAW);
        g.gl.bindBuffer(g.gl.ARRAY_BUFFER, pg.prev);
        g.gl.bufferData(g.gl.ARRAY_BUFFER, new Float32Array(a ? a.xy : s.xy), g.gl.STATIC_DRAW);
      }
    }
    stats.current.points = n;
    stats.current.panes = panes.length;
    schedule();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [panes, n, gen, paneGl, schedule]);

  // ---- link overlay per pane: leaf → each parent summary, summary → each parent (faded); hidden clusters' links left out ----
  useEffect(() => {
    const g = glr.current;
    if (!g) return;
    for (let slot = 0; slot < panes.length; slot++) {
      const pg = paneGl(g, slot);
      const s = shown.current[slot];
      const a = anims.current[slot];
      const seg: number[] = [];
      if (showLinks && s) {
        const xy = s.xy;
        const sxy = s.sxy;
        const pxy = a ? a.xy : xy;
        const psxy = a ? a.sxy : sxy;
        const push = (x: number, y: number, px: number, py: number) => seg.push(x, y, px, py);
        for (let i = 0; i < n; i++) {
          if (!mask[i]) continue;
          for (const p of cloud.points.parents[i] ?? []) {
            const q = summaryIndexOf.get(p);
            if (q === undefined) continue;
            push(xy[2 * i]!, xy[2 * i + 1]!, pxy[2 * i]!, pxy[2 * i + 1]!);
            push(sxy[2 * q]!, sxy[2 * q + 1]!, psxy[2 * q]!, psxy[2 * q + 1]!);
          }
        }
        cloud.summaries.id.forEach((_, si) => {
          const c = cloud.summaries.cluster[si]!;
          if (c >= 0 && hidden.has(legendKey(c))) return;
          for (const p of cloud.summaries.parents[si] ?? []) {
            const q = summaryIndexOf.get(p);
            if (q === undefined) continue;
            push(sxy[2 * si]!, sxy[2 * si + 1]!, psxy[2 * si]!, psxy[2 * si + 1]!);
            push(sxy[2 * q]!, sxy[2 * q + 1]!, psxy[2 * q]!, psxy[2 * q + 1]!);
          }
        });
      }
      g.gl.bindBuffer(g.gl.ARRAY_BUFFER, pg.line);
      g.gl.bufferData(g.gl.ARRAY_BUFFER, new Float32Array(seg), g.gl.STATIC_DRAW);
      pg.lineCount = seg.length / 4;
    }
    schedule();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [cloud, panes, showLinks, mask, hidden, summaryIndexOf, gen, paneGl]);

  // ---- per-point style (colour, alpha, size at the fitted zoom) shared by every pane, and each pane's halos ----
  const uploadStyle = useCallback(() => {
    const g = glr.current;
    if (!g) return;
    const { cloud: c, brightness: b, theme: th, highlight: hl, selectedId: sel, mask: m } = props.current;
    const data = new Float32Array(n * 5);
    const halos: Array<[number, number, number, number, number, number]> = [];
    const rgb = new Map<number, [number, number, number]>();
    for (let i = 0; i < n; i++) {
      if (!m[i]) continue; // size 0: not drawn
      const cl = c.points.cluster[i]!;
      let col = rgb.get(cl);
      if (!col) rgb.set(cl, (col = hexRgb(clusterColor(cl, th))));
      const look = pointLook(b[i] ?? 0, 1, {
        dimmed: hl ? !hl[i] : false,
        highlighted: hl ? !!hl[i] : false,
        selected: c.points.id[i] === sel,
      });
      data.set([col[0], col[1], col[2], look.alpha, look.size], i * 5);
      if (look.halo > 0) halos.push([i, col[0], col[1], col[2], 0.55 * look.halo, look.size * 4.5]);
    }
    g.gl.bindBuffer(g.gl.ARRAY_BUFFER, g.style);
    g.gl.bufferData(g.gl.ARRAY_BUFFER, data, g.gl.DYNAMIC_DRAW);
    for (let slot = 0; slot < shown.current.length; slot++) {
      const s = shown.current[slot];
      if (!s) continue;
      const pg = paneGl(g, slot);
      const a = anims.current[slot];
      const pxy = a ? a.xy : s.xy;
      const buf = new Float32Array(halos.length * 9);
      halos.forEach(([i, r, gg, bb, al, sz], k) =>
        buf.set([s.xy[2 * i]!, s.xy[2 * i + 1]!, pxy[2 * i]!, pxy[2 * i + 1]!, r, gg, bb, al, sz], k * 9),
      );
      g.gl.bindBuffer(g.gl.ARRAY_BUFFER, pg.halo);
      g.gl.bufferData(g.gl.ARRAY_BUFFER, buf, g.gl.DYNAMIC_DRAW);
      pg.haloCount = halos.length;
    }
  }, [n, paneGl]);

  useEffect(() => {
    uploadStyle();
    schedule();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [brightness, theme, highlight, selectedId, mask, gen, panes, uploadStyle]);

  // ---- draw ----
  const draw = useCallback(() => {
    raf.current = 0;
    const t0 = performance.now();
    const w = size.w,
      h = size.h;
    if (!w || !h) return;
    const dpr = window.devicePixelRatio || 1;
    const { cloud: c, theme: th, hidden: hid, pulses: ps, lasso: ls, selectedId: sel, panes: pn } = props.current;
    let animating = false;

    for (const el of [under.current, glCanvas.current, over.current]) {
      if (!el) continue;
      if (el.width !== Math.round(w * dpr)) el.width = Math.round(w * dpr);
      if (el.height !== Math.round(h * dpr)) el.height = Math.round(h * dpr);
    }
    const u = under.current?.getContext('2d');
    if (u) {
      u.setTransform(dpr, 0, 0, dpr, 0, 0);
      u.clearRect(0, 0, w, h);
    }
    const o = over.current?.getContext('2d');
    if (o) {
      o.setTransform(dpr, 0, 0, dpr, 0, 0);
      o.clearRect(0, 0, w, h);
    }
    const g = glr.current;
    if (g) {
      const gl = g.gl;
      gl.viewport(0, 0, Math.round(w * dpr), Math.round(h * dpr));
      gl.disable(gl.SCISSOR_TEST);
      gl.clearColor(0, 0, 0, 0);
      gl.clear(gl.COLOR_BUFFER_BIT);
      gl.enable(gl.BLEND);
      gl.blendFuncSeparate(gl.SRC_ALPHA, gl.ONE_MINUS_SRC_ALPHA, gl.ONE, gl.ONE_MINUS_SRC_ALPHA);
      if (pn.length > 1) gl.enable(gl.SCISSOR_TEST);
    }
    const text = cssVar('--text', th === 'dark' ? '#e7e9ec' : '#14171c');
    const bg = cssVar('--canvas-bg', th === 'dark' ? '#0a0c0f' : '#eef1f5');
    const now = performance.now();
    let pulsing = false;

    for (let slot = 0; slot < pn.length; slot++) {
      const rect = rects[slot];
      const cur = drawnNow(slot);
      if (!rect || !cur) continue;
      const mix = mixOf(slot, now);
      if (mix < 1) animating = true;
      const v = views.current[slot] ?? { k: 1, tx: 0, ty: 0 };
      const f = paneFrame(rect);
      const scale = f.base * v.k;
      const offX = f.ox * v.k + v.tx,
        offY = f.oy * v.k + v.ty;
      const sx = (x: number) => x * scale + offX;
      const sy = (y: number) => y * scale + offY;
      const clip = (ctx: CanvasRenderingContext2D) => {
        ctx.save();
        ctx.beginPath();
        ctx.rect(rect.x, rect.y, rect.w, rect.h);
        ctx.clip();
      };

      // below: cluster density blobs, strongest when zoomed out (the level of detail for a far view)
      if (u) {
        clip(u);
        const blob = Math.max(0.03, Math.min(0.2, 0.22 / Math.sqrt(v.k)));
        for (const cl of cur.clusters) {
          if (hid.has(legendKey(cl.id))) continue;
          const cx = sx(cl.x),
            cy = sy(cl.y);
          const r = Math.max(24, cl.spread * 2.4 * scale);
          if (cx + r < rect.x || cy + r < rect.y || cx - r > rect.x + rect.w || cy - r > rect.y + rect.h) continue;
          const grad = u.createRadialGradient(cx, cy, 0, cx, cy, r);
          const [cr, cg, cb] = hexRgb(clusterColor(cl.id, th)).map((x) => Math.round(x * 255));
          grad.addColorStop(0, `rgba(${cr},${cg},${cb},${blob})`);
          grad.addColorStop(1, `rgba(${cr},${cg},${cb},0)`);
          u.fillStyle = grad;
          u.fillRect(cx - r, cy - r, 2 * r, 2 * r);
        }
        u.restore();
      }

      // the points
      if (g) {
        const gl = g.gl;
        const pg = paneGl(g, slot);
        if (pn.length > 1)
          gl.scissor(
            Math.round(rect.x * dpr),
            Math.round((h - rect.y - rect.h) * dpr),
            Math.round(rect.w * dpr),
            Math.round(rect.h * dpr),
          );
        const uni = (p: WebGLProgram) => {
          gl.useProgram(p);
          gl.uniform2f(gl.getUniformLocation(p, 'u_scale'), scale, scale);
          gl.uniform2f(gl.getUniformLocation(p, 'u_off'), offX, offY);
          gl.uniform2f(gl.getUniformLocation(p, 'u_res'), w, h);
          gl.uniform1f(gl.getUniformLocation(p, 'u_mix'), mix);
          const d = gl.getUniformLocation(p, 'u_dpr');
          if (d) gl.uniform1f(d, dpr);
          const ks = gl.getUniformLocation(p, 'u_ksize');
          if (ks) gl.uniform1f(ks, zoomSizeFactor(v.k));
        };
        if (pg.lineCount) {
          uni(g.line);
          const ink = th === 'dark' ? [0.85, 0.88, 0.92] : [0.1, 0.13, 0.18];
          gl.uniform4f(gl.getUniformLocation(g.line, 'u_col'), ink[0]!, ink[1]!, ink[2]!, linkAlpha(v.k, pg.lineCount / 2, th));
          gl.bindVertexArray(pg.vaoLine);
          gl.drawArrays(gl.LINES, 0, pg.lineCount);
        }
        uni(g.point);
        gl.bindVertexArray(pg.vaoPoint);
        gl.drawArrays(gl.POINTS, 0, n);
        if (pg.haloCount) {
          uni(g.halo);
          gl.bindVertexArray(pg.vaoHalo);
          gl.drawArrays(gl.POINTS, 0, pg.haloCount);
        }
        gl.bindVertexArray(null);
      }

      // above: cluster labels, selection / hover / lasso rings, live pulses, the scale bar, the lasso path
      if (o) {
        clip(o);
        o.font = '600 12px ui-sans-serif, system-ui, sans-serif';
        o.textBaseline = 'middle';
        const placed: Array<[number, number, number, number]> = [];
        for (const cl of cur.clusters) {
          if (hid.has(legendKey(cl.id))) continue;
          const x = sx(cl.x),
            y = sy(cl.y) - Math.min(60, cl.spread * scale * 1.1) - 8;
          if (x < rect.x - 100 || y < rect.y - 20 || x > rect.x + rect.w + 100 || y > rect.y + rect.h + 20) continue;
          const tw = o.measureText(cl.label).width + 16;
          const box: [number, number, number, number] = [x - tw / 2, y - 9, tw, 18];
          if (
            placed.some(
              (b) => box[0] < b[0] + b[2] && b[0] < box[0] + box[2] && box[1] < b[1] + b[3] && b[1] < box[1] + box[3],
            )
          )
            continue;
          placed.push(box);
          o.lineWidth = 4;
          o.strokeStyle = bg;
          o.lineJoin = 'round';
          o.strokeText(cl.label, x - tw / 2 + 14, y);
          o.fillStyle = text;
          o.fillText(cl.label, x - tw / 2 + 14, y);
          o.beginPath();
          o.arc(x - tw / 2 + 6, y, 4, 0, Math.PI * 2);
          o.fillStyle = clusterColor(cl.id, th);
          o.fill();
        }
        const xy = cur.xy;
        const ring = (i: number, r: number, color: string, width: number, alpha = 1) => {
          o.globalAlpha = alpha;
          o.beginPath();
          o.arc(sx(xy[2 * i]!), sy(xy[2 * i + 1]!), r, 0, Math.PI * 2);
          o.strokeStyle = color;
          o.lineWidth = width;
          o.stroke();
          o.globalAlpha = 1;
        };
        for (const i of ls) if (props.current.mask[i]) ring(i, 5, text, 1, 0.55);
        for (const p of ps) {
          const age = (now - p.at) / PULSE_MS;
          if (age >= 1 || !props.current.mask[p.index]) continue;
          pulsing = true;
          if (age < 0) continue; // staggered: starts a little later
          ring(p.index, 4 + 16 * age, clusterColor(c.points.cluster[p.index]!, th), 1.5, (1 - age) * 0.8);
        }
        const si = sel === null ? undefined : indexOf.get(sel);
        if (si !== undefined) ring(si, 8, text, 2);
        else if (sel !== null) {
          const s = summaryIndexOf.get(sel);
          if (s !== undefined) {
            o.beginPath();
            o.arc(sx(cur.sxy[2 * s]!), sy(cur.sxy[2 * s + 1]!), 10, 0, Math.PI * 2);
            o.strokeStyle = text;
            o.lineWidth = 2;
            o.setLineDash([3, 3]);
            o.stroke();
            o.setLineDash([]);
          }
        }
        if (hoverIx.current !== null) ring(hoverIx.current, 7, text, 1.5, 0.8);

        // Scale bar (metric arrangements): a round cosine distance at this zoom, bottom left of the pane.
        const pane = pn[slot]!;
        const bar = pane.metric && pane.unitDistance ? niceScale(pane.unitDistance, scale) : null;
        if (bar) {
          const bx = rect.x + 14,
            by = rect.y + rect.h - 16;
          o.strokeStyle = text;
          o.globalAlpha = 0.75;
          o.lineWidth = 1.5;
          o.beginPath();
          o.moveTo(bx, by - 4);
          o.lineTo(bx, by);
          o.lineTo(bx + bar.px, by);
          o.lineTo(bx + bar.px, by - 4);
          o.stroke();
          o.globalAlpha = 1;
          o.font = '500 11px ui-sans-serif, system-ui, sans-serif';
          o.textBaseline = 'bottom';
          o.lineWidth = 3;
          o.strokeStyle = bg;
          o.strokeText(bar.label, bx, by - 6);
          o.fillStyle = text;
          o.fillText(bar.label, bx, by - 6);
        }
        if (wrap.current) {
          const key = `scale${slot}`;
          const val = bar ? `${bar.label}|${bar.px.toFixed(1)}` : '';
          if (wrap.current.dataset[key] !== val) wrap.current.dataset[key] = val;
        }

        const lp = lassoPath.current;
        if (lp && lp.pane === slot && lp.path.length > 1) {
          o.beginPath();
          o.moveTo(lp.path[0]![0], lp.path[0]![1]);
          for (const [x, y] of lp.path) o.lineTo(x, y);
          o.closePath();
          o.fillStyle = th === 'dark' ? 'rgba(255,255,255,0.06)' : 'rgba(0,0,0,0.05)';
          o.fill();
          o.setLineDash([4, 3]);
          o.strokeStyle = text;
          o.lineWidth = 1;
          o.stroke();
          o.setLineDash([]);
        }
        o.restore();
        if (pn.length > 1) {
          // a hairline frame per pane
          o.strokeStyle = th === 'dark' ? 'rgba(255,255,255,0.10)' : 'rgba(0,0,0,0.10)';
          o.lineWidth = 1;
          o.strokeRect(rect.x + 0.5, rect.y + 0.5, rect.w - 1, rect.h - 1);
        }
      }
    }
    const s = stats.current;
    s.frames++;
    s.drawMs += performance.now() - t0;
    if (pulsing || animating) raf.current = requestAnimationFrame(draw);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [size, n, rects, indexOf, summaryIndexOf, paneGl]);

  drawRef.current = draw;

  useEffect(() => {
    (window as unknown as { __mineclawCloudStats?: Stats }).__mineclawCloudStats = stats.current;
    return () => {
      if (raf.current) cancelAnimationFrame(raf.current);
      raf.current = 0;
    };
  }, []);

  // redraw on any prop that only affects the frame
  useEffect(() => {
    if (raf.current) cancelAnimationFrame(raf.current);
    raf.current = 0;
    schedule();
  }, [draw, pulses, lasso, hidden, selectedId, theme, schedule]);

  // size
  useEffect(() => {
    const el = wrap.current;
    if (!el) return;
    const ro = new ResizeObserver(() => setSize({ w: el.clientWidth, h: el.clientHeight }));
    ro.observe(el);
    setSize({ w: el.clientWidth, h: el.clientHeight });
    return () => ro.disconnect();
  }, []);

  const setView = useCallback(
    (slot: number, v: View) => {
      views.current[slot] = v;
      schedule();
    },
    [schedule],
  );
  const fitAll = useCallback(() => {
    views.current = views.current.map(() => ({ k: 1, tx: 0, ty: 0 }));
    schedule();
  }, [schedule]);

  const zoomAt = useCallback(
    (slot: number, sx: number, sy: number, factor: number) => {
      const v = views.current[slot] ?? { k: 1, tx: 0, ty: 0 };
      const k = Math.min(MAX_K, Math.max(0.5, v.k * factor));
      const f = k / v.k;
      setView(slot, { k, tx: sx - (sx - v.tx) * f, ty: sy - (sy - v.ty) * f });
    },
    [setView],
  );

  const paneAt = useCallback(
    (x: number, y: number): number => {
      const i = rects.findIndex((r) => x >= r.x && x <= r.x + r.w && y >= r.y && y <= r.y + r.h);
      return i >= 0 ? i : rects.length === 1 ? 0 : active.current;
    },
    [rects],
  );

  const toData = useCallback(
    (slot: number, sx: number, sy: number): [number, number] => {
      const r = rects[slot];
      if (!r) return [NaN, NaN];
      const f = paneFrame(r);
      const v = views.current[slot] ?? { k: 1, tx: 0, ty: 0 };
      return [((sx - v.tx) / v.k - f.ox) / f.base, ((sy - v.ty) / v.k - f.oy) / f.base];
    },
    [rects],
  );

  useImperativeHandle(
    ref,
    () => ({
      fit: fitAll,
      reveal: (id: number) => {
        const i = indexOf.get(id);
        const s = summaryIndexOf.get(id);
        panes.forEach((pane, slot) => {
          const p =
            i !== undefined
              ? [pane.xy[2 * i]!, pane.xy[2 * i + 1]!]
              : s !== undefined
                ? [pane.sxy[2 * s]!, pane.sxy[2 * s + 1]!]
                : null;
          const r = rects[slot];
          if (!p || !r) return;
          const f = paneFrame(r);
          const k = Math.max(views.current[slot]?.k ?? 1, 4);
          setView(slot, {
            k,
            tx: r.x + r.w / 2 - (f.ox + p[0]! * f.base) * k,
            ty: r.y + r.h / 2 - (f.oy + p[1]! * f.base) * k,
          });
        });
      },
    }),
    [indexOf, summaryIndexOf, panes, rects, setView, fitAll],
  );

  // wheel must be non-passive to stop the page scrolling
  useEffect(() => {
    const el = wrap.current;
    if (!el) return;
    const onWheel = (e: WheelEvent) => {
      e.preventDefault();
      const r = el.getBoundingClientRect();
      const x = e.clientX - r.left,
        y = e.clientY - r.top;
      zoomAt(paneAt(x, y), x, y, Math.exp(-e.deltaY * (e.deltaMode === 1 ? 0.05 : 0.0016)));
    };
    el.addEventListener('wheel', onWheel, { passive: false });
    return () => el.removeEventListener('wheel', onWheel);
  }, [zoomAt, paneAt]);

  const hitAt = (slot: number, sx: number, sy: number): number => {
    const pane = panes[slot];
    const grid = grids[slot];
    const r = rects[slot];
    if (!pane || !grid || !r) return -1;
    const [x, y] = toData(slot, sx, sy);
    const f = paneFrame(r);
    return nearest(grid, pane.xy, x, y, 9 / (f.base * (views.current[slot]?.k ?? 1)), mask);
  };

  const local = (e: { clientX: number; clientY: number }): [number, number] => {
    const r = wrap.current!.getBoundingClientRect();
    return [e.clientX - r.left, e.clientY - r.top];
  };

  return (
    <div
      ref={wrap}
      role="application"
      aria-label={label}
      aria-roledescription="memory cloud"
      tabIndex={0}
      data-points={n}
      data-panes={count}
      className={cn(
        'touch-none overflow-hidden outline-none select-none focus-visible:ring-2 focus-visible:ring-[var(--ring)]',
        className,
      )}
      onPointerDown={(e) => {
        if (e.button !== 0) return;
        wrap.current?.focus({ preventScroll: true });
        (e.currentTarget as HTMLElement).setPointerCapture(e.pointerId);
        const [x, y] = local(e);
        const slot = paneAt(x, y);
        active.current = slot;
        const v = views.current[slot] ?? { k: 1, tx: 0, ty: 0 };
        if (e.shiftKey) lassoPath.current = { pane: slot, path: [[x, y]] };
        else drag.current = { pane: slot, x, y, tx: v.tx, ty: v.ty, moved: false };
      }}
      onPointerMove={(e) => {
        const [x, y] = local(e);
        if (lassoPath.current) {
          lassoPath.current.path.push([x, y]);
          schedule();
          return;
        }
        const d = drag.current;
        if (d) {
          if (Math.abs(x - d.x) + Math.abs(y - d.y) > 3) d.moved = true;
          if (d.moved) {
            setView(d.pane, { k: views.current[d.pane]?.k ?? 1, tx: d.tx + x - d.x, ty: d.ty + y - d.y });
            if (hoverIx.current !== null) {
              hoverIx.current = null;
              onHover(null, 0, 0, d.pane);
            }
            return;
          }
        }
        const slot = paneAt(x, y);
        active.current = slot;
        const i = hitAt(slot, x, y);
        const next = i >= 0 ? i : null;
        if (next !== hoverIx.current) {
          hoverIx.current = next;
          schedule();
        }
        onHover(next, x, y, slot);
      }}
      onPointerUp={(e) => {
        const [x, y] = local(e);
        const lp = lassoPath.current;
        if (lp) {
          lassoPath.current = null;
          const pane = panes[lp.pane];
          const grid = grids[lp.pane];
          onLasso(
            lp.path.length > 2 && pane && grid
              ? lassoSelect(
                  grid,
                  pane.xy,
                  lp.path.map(([px, py]) => toData(lp.pane, px, py)),
                  mask,
                )
              : [],
          );
          schedule();
          return;
        }
        const d = drag.current;
        drag.current = null;
        if (d && !d.moved) {
          const i = hitAt(d.pane, x, y);
          onSelect(i >= 0 ? cloud.points.id[i]! : null);
        }
      }}
      onPointerLeave={() => {
        if (hoverIx.current !== null) {
          hoverIx.current = null;
          schedule();
        }
        onHover(null, 0, 0, active.current);
      }}
      onDoubleClick={(e) => {
        const [x, y] = local(e);
        zoomAt(paneAt(x, y), x, y, 2);
      }}
      onKeyDown={(e) => {
        const slot = active.current;
        const v = views.current[slot] ?? { k: 1, tx: 0, ty: 0 };
        const r = rects[slot] ?? { x: 0, y: 0, w: size.w, h: size.h };
        const step = 60;
        if (e.key === 'f' || e.key === 'F' || e.key === 'Home') fitAll();
        else if (e.key === '+' || e.key === '=') zoomAt(slot, r.x + r.w / 2, r.y + r.h / 2, 1.4);
        else if (e.key === '-' || e.key === '_') zoomAt(slot, r.x + r.w / 2, r.y + r.h / 2, 1 / 1.4);
        else if (e.key === 'ArrowLeft') setView(slot, { ...v, tx: v.tx + step });
        else if (e.key === 'ArrowRight') setView(slot, { ...v, tx: v.tx - step });
        else if (e.key === 'ArrowUp') setView(slot, { ...v, ty: v.ty + step });
        else if (e.key === 'ArrowDown') setView(slot, { ...v, ty: v.ty - step });
        else if (e.key === 'Escape') {
          if (lasso.length) onLasso([]);
          else onSelect(null);
        } else return;
        e.preventDefault();
      }}
    >
      <canvas ref={under} className="pointer-events-none absolute inset-0 size-full" aria-hidden />
      <canvas ref={glCanvas} className="pointer-events-none absolute inset-0 size-full" aria-hidden />
      <canvas ref={over} className="pointer-events-none absolute inset-0 size-full" aria-hidden />
      {paneOverlay
        ? rects.map((r, i) => (
            <div key={i} className="pointer-events-none absolute" style={{ left: r.x, top: r.y, width: r.w }}>
              {paneOverlay(i, r)}
            </div>
          ))
        : null}
      {glError ? (
        <div className="absolute inset-x-6 top-6 rounded-lg border border-border bg-surface p-3 text-sm text-muted">
          {glError}
        </div>
      ) : null}
    </div>
  );
});
