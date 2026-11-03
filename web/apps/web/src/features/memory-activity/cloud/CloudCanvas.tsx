'use client';

/**
 * The memory cloud renderer: one WebGL2 context draws every point (and the faded link overlay) from GPU buffers, so pan and zoom
 * cost one draw call each frame however many memories there are. Two 2-D canvases sit around it: cluster density blobs below
 * (they carry the picture when zoomed out) and labels, rings, pulses and the lasso above. It draws on demand only (a change,
 * an interaction, or a pulse still fading), never in a free-running loop.
 */
import type { MemoryCloudResponse } from '@mineclaw/contracts';
import { forwardRef, useCallback, useEffect, useImperativeHandle, useMemo, useRef, useState } from 'react';
import { cn } from '@/lib/utils';
import {
  buildGrid,
  clusterColor,
  hexRgb,
  lassoSelect,
  legendKey,
  nearest,
  pointLook,
  visibleMask,
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

interface View {
  k: number;
  tx: number;
  ty: number;
}

const PULSE_MS = 1600;
const MAX_K = 60;
/** Width the legend card takes, left clear in the fitted view on canvases at least LEGEND_ROOM_MIN_W wide. */
export const LEGEND_W = 290;
export const LEGEND_ROOM_MIN_W = 720;

const VS = `#version 300 es
in vec2 a_pos;
in vec4 a_col;
in float a_size;
uniform vec2 u_scale;
uniform vec2 u_off;
uniform vec2 u_res;
uniform float u_dpr;
out vec4 v_col;
void main() {
  vec2 p = a_pos * u_scale + u_off;
  gl_Position = vec4(p.x / u_res.x * 2.0 - 1.0, 1.0 - p.y / u_res.y * 2.0, 0.0, 1.0);
  gl_PointSize = a_size * u_dpr;
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
uniform vec2 u_scale;
uniform vec2 u_off;
uniform vec2 u_res;
void main() {
  vec2 p = a_pos * u_scale + u_off;
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

interface Gl {
  gl: WebGL2RenderingContext;
  point: WebGLProgram;
  halo: WebGLProgram;
  line: WebGLProgram;
  pos: WebGLBuffer;
  style: WebGLBuffer;
  haloBuf: WebGLBuffer;
  lineBuf: WebGLBuffer;
  vaoPoint: WebGLVertexArrayObject;
  vaoHalo: WebGLVertexArrayObject;
  vaoLine: WebGLVertexArrayObject;
  haloCount: number;
  lineCount: number;
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
  renderer?: string;
}

export const CloudCanvas = forwardRef<
  CloudCanvasHandle,
  {
    cloud: MemoryCloudResponse;
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
    onHover: (index: number | null, x: number, y: number) => void;
    onSelect: (id: number | null) => void;
    onLasso: (indexes: number[]) => void;
    className?: string;
    label?: string;
  }
>(function CloudCanvas(
  {
    cloud,
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
  const view = useRef<View>({ k: 1, tx: 0, ty: 0 });
  const raf = useRef(0);
  const hoverIx = useRef<number | null>(null);
  const lassoPath = useRef<Array<[number, number]> | null>(null);
  const drag = useRef<{ x: number; y: number; tx: number; ty: number; moved: boolean } | null>(null);
  const stats = useRef<Stats>({ frames: 0, drawMs: 0, points: 0 });
  // Bumped when the context comes back so buffers are re-uploaded.
  const [gen, setGen] = useState(0);
  const drawRef = useRef<() => void>(() => undefined);
  const schedule = useCallback(() => {
    if (!raf.current && typeof window !== 'undefined') raf.current = requestAnimationFrame(() => drawRef.current());
  }, []);
  const xy = cloud.points.xy;
  const n = cloud.points.id.length;
  const grid = useMemo(() => buildGrid(xy), [xy]);
  const mask = useMemo(() => visibleMask(cloud, hidden), [cloud, hidden]);
  const indexOf = useMemo(() => new Map(cloud.points.id.map((id, i) => [id, i])), [cloud.points.id]);
  const summaryIndexOf = useMemo(() => new Map(cloud.summaries.id.map((id, i) => [id, i])), [cloud.summaries.id]);
  const clusterOfSummary = cloud.summaries.cluster;
  // Latest props for the draw callback (it runs from rAF, outside React's render).
  const props = useRef({ cloud, brightness, theme, hidden, highlight, selectedId, showLinks, pulses, lasso, mask });
  props.current = { cloud, brightness, theme, hidden, highlight, selectedId, showLinks, pulses, lasso, mask };

  // ---- frame geometry ----
  const frame = useCallback((w: number, h: number) => {
    // The fitted view keeps clear of the legend card (top left) when there is room for it.
    const left = w >= LEGEND_ROOM_MIN_W ? LEGEND_W : 0;
    const base = Math.min(w - left, h) * 0.94;
    return { base, ox: left + (w - left - base) / 2, oy: (h - base) / 2 };
  }, []);
  const toData = useCallback(
    (sx: number, sy: number): [number, number] => {
      const f = frame(size.w, size.h);
      const v = view.current;
      return [((sx - v.tx) / v.k - f.ox) / f.base, ((sy - v.ty) / v.k - f.oy) / f.base];
    },
    [frame, size],
  );

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
        const mkVao = (prog: WebGLProgram, posBuf: WebGLBuffer, styleBuf: WebGLBuffer | null, interleaved: boolean) => {
          const vao = gl.createVertexArray();
          gl.bindVertexArray(vao);
          const aPos = gl.getAttribLocation(prog, 'a_pos');
          gl.bindBuffer(gl.ARRAY_BUFFER, posBuf);
          gl.enableVertexAttribArray(aPos);
          gl.vertexAttribPointer(aPos, 2, gl.FLOAT, false, interleaved ? 28 : 0, 0);
          const aCol = gl.getAttribLocation(prog, 'a_col');
          const aSize = gl.getAttribLocation(prog, 'a_size');
          if (aCol >= 0 && aSize >= 0) {
            gl.bindBuffer(gl.ARRAY_BUFFER, interleaved ? posBuf : styleBuf);
            gl.enableVertexAttribArray(aCol);
            gl.vertexAttribPointer(aCol, 4, gl.FLOAT, false, interleaved ? 28 : 20, interleaved ? 8 : 0);
            gl.enableVertexAttribArray(aSize);
            gl.vertexAttribPointer(aSize, 1, gl.FLOAT, false, interleaved ? 28 : 20, interleaved ? 24 : 16);
          }
          gl.bindVertexArray(null);
          return vao;
        };
        const pos = gl.createBuffer();
        const style = gl.createBuffer();
        const haloBuf = gl.createBuffer();
        const lineBuf = gl.createBuffer();
        glr.current = {
          gl,
          point,
          halo,
          line,
          pos,
          style,
          haloBuf,
          lineBuf,
          vaoPoint: mkVao(point, pos, style, false),
          vaoHalo: mkVao(halo, haloBuf, null, true),
          vaoLine: mkVao(line, lineBuf, null, false),
          haloCount: 0,
          lineCount: 0,
        };
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
  // ---- static positions ----
  useEffect(() => {
    const g = glr.current;
    if (!g) return;
    g.gl.bindBuffer(g.gl.ARRAY_BUFFER, g.pos);
    g.gl.bufferData(g.gl.ARRAY_BUFFER, new Float32Array(xy), g.gl.STATIC_DRAW);
    stats.current.points = n;
  }, [xy, n, gen]);

  // ---- link overlay: leaf → each parent summary, summary → each parent (faded); hidden clusters' links are left out ----
  useEffect(() => {
    const g = glr.current;
    if (!g) return;
    const seg: number[] = [];
    const sxy = cloud.summaries.xy;
    const at = (id: number): [number, number] | null => {
      const s = summaryIndexOf.get(id);
      return s === undefined ? null : [sxy[2 * s]!, sxy[2 * s + 1]!];
    };
    if (showLinks) {
      for (let i = 0; i < n; i++) {
        if (!mask[i]) continue;
        for (const p of cloud.points.parents[i] ?? []) {
          const q = at(p);
          if (q) seg.push(xy[2 * i]!, xy[2 * i + 1]!, q[0], q[1]);
        }
      }
      cloud.summaries.id.forEach((_, s) => {
        const c = clusterOfSummary[s]!;
        if (c >= 0 && hidden.has(legendKey(c))) return;
        for (const p of cloud.summaries.parents[s] ?? []) {
          const q = at(p);
          if (q) seg.push(sxy[2 * s]!, sxy[2 * s + 1]!, q[0], q[1]);
        }
      });
    }
    g.gl.bindBuffer(g.gl.ARRAY_BUFFER, g.lineBuf);
    g.gl.bufferData(g.gl.ARRAY_BUFFER, new Float32Array(seg), g.gl.STATIC_DRAW);
    g.lineCount = seg.length / 2;
    schedule();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [cloud, showLinks, mask, hidden, summaryIndexOf, gen]);

  // ---- per-point style (colour, alpha, size) and the halo list; rebuilt when heat, filters, theme or zoom band change ----
  const kBand = useRef(1);
  const uploadStyle = useCallback(() => {
    const g = glr.current;
    if (!g) return;
    const { cloud: c, brightness: b, theme: th, highlight: hl, selectedId: sel, mask: m } = props.current;
    const k = view.current.k;
    const data = new Float32Array(n * 5);
    const halo: number[] = [];
    const rgb = new Map<number, [number, number, number]>();
    for (let i = 0; i < n; i++) {
      if (!m[i]) continue; // size 0: not drawn
      const cl = c.points.cluster[i]!;
      let col = rgb.get(cl);
      if (!col) rgb.set(cl, (col = hexRgb(clusterColor(cl, th))));
      const look = pointLook(b[i] ?? 0, k, {
        dimmed: hl ? !hl[i] : false,
        highlighted: hl ? !!hl[i] : false,
        selected: c.points.id[i] === sel,
      });
      data.set([col[0], col[1], col[2], look.alpha, look.size], i * 5);
      if (look.halo > 0)
        halo.push(xy[2 * i]!, xy[2 * i + 1]!, col[0], col[1], col[2], 0.55 * look.halo, look.size * 4.5);
    }
    g.gl.bindBuffer(g.gl.ARRAY_BUFFER, g.style);
    g.gl.bufferData(g.gl.ARRAY_BUFFER, data, g.gl.DYNAMIC_DRAW);
    g.gl.bindBuffer(g.gl.ARRAY_BUFFER, g.haloBuf);
    g.gl.bufferData(g.gl.ARRAY_BUFFER, new Float32Array(halo), g.gl.DYNAMIC_DRAW);
    g.haloCount = halo.length / 7;
    kBand.current = k;
  }, [n, xy]);

  useEffect(() => {
    uploadStyle();
    schedule();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [brightness, theme, highlight, selectedId, mask, gen, uploadStyle]);

  // ---- draw ----
  const draw = useCallback(() => {
    raf.current = 0;
    const t0 = performance.now();
    const w = size.w,
      h = size.h;
    if (!w || !h) return;
    const dpr = window.devicePixelRatio || 1;
    const v = view.current;
    // Point sizes follow the zoom band (rebuilt when the zoom moved by more than ~25 %).
    if (Math.abs(Math.log(v.k / kBand.current)) > 0.22) uploadStyle();
    const f = frame(w, h);
    const scale = f.base * v.k;
    const offX = f.ox * v.k + v.tx,
      offY = f.oy * v.k + v.ty;
    const { cloud: c, theme: th, hidden: hid, pulses: ps, lasso: ls, selectedId: sel } = props.current;

    for (const el of [under.current, glCanvas.current, over.current]) {
      if (!el) continue;
      if (el.width !== Math.round(w * dpr)) el.width = Math.round(w * dpr);
      if (el.height !== Math.round(h * dpr)) el.height = Math.round(h * dpr);
    }

    // below: cluster density blobs, strongest when zoomed out (the level of detail for a far view)
    const u = under.current?.getContext('2d');
    if (u) {
      u.setTransform(dpr, 0, 0, dpr, 0, 0);
      u.clearRect(0, 0, w, h);
      const blob = Math.max(0.03, Math.min(0.2, 0.22 / Math.sqrt(v.k)));
      for (const cl of c.clusters) {
        if (hid.has(legendKey(cl.id))) continue;
        const cx = cl.x * scale + offX,
          cy = cl.y * scale + offY;
        const r = Math.max(24, cl.spread * 2.4 * scale);
        if (cx + r < 0 || cy + r < 0 || cx - r > w || cy - r > h) continue;
        const grad = u.createRadialGradient(cx, cy, 0, cx, cy, r);
        const [cr, cg, cb] = hexRgb(clusterColor(cl.id, th)).map((x) => Math.round(x * 255));
        grad.addColorStop(0, `rgba(${cr},${cg},${cb},${blob})`);
        grad.addColorStop(1, `rgba(${cr},${cg},${cb},0)`);
        u.fillStyle = grad;
        u.fillRect(cx - r, cy - r, 2 * r, 2 * r);
      }
    }

    // the points
    const g = glr.current;
    if (g) {
      const gl = g.gl;
      gl.viewport(0, 0, Math.round(w * dpr), Math.round(h * dpr));
      gl.clearColor(0, 0, 0, 0);
      gl.clear(gl.COLOR_BUFFER_BIT);
      gl.enable(gl.BLEND);
      gl.blendFuncSeparate(gl.SRC_ALPHA, gl.ONE_MINUS_SRC_ALPHA, gl.ONE, gl.ONE_MINUS_SRC_ALPHA);
      const uni = (p: WebGLProgram) => {
        gl.useProgram(p);
        gl.uniform2f(gl.getUniformLocation(p, 'u_scale'), scale, scale);
        gl.uniform2f(gl.getUniformLocation(p, 'u_off'), offX, offY);
        gl.uniform2f(gl.getUniformLocation(p, 'u_res'), w, h);
        const d = gl.getUniformLocation(p, 'u_dpr');
        if (d) gl.uniform1f(d, dpr);
      };
      if (g.lineCount) {
        uni(g.line);
        const ink = th === 'dark' ? [0.85, 0.88, 0.92, 0.07] : [0.1, 0.13, 0.18, 0.08];
        gl.uniform4f(gl.getUniformLocation(g.line, 'u_col'), ink[0]!, ink[1]!, ink[2]!, ink[3]!);
        gl.bindVertexArray(g.vaoLine);
        gl.drawArrays(gl.LINES, 0, g.lineCount);
      }
      uni(g.point);
      gl.bindVertexArray(g.vaoPoint);
      gl.drawArrays(gl.POINTS, 0, n);
      if (g.haloCount) {
        uni(g.halo);
        gl.bindVertexArray(g.vaoHalo);
        gl.drawArrays(gl.POINTS, 0, g.haloCount);
      }
      gl.bindVertexArray(null);
    }

    // above: cluster labels, selection / hover / lasso rings, live pulses, the lasso path
    const o = over.current?.getContext('2d');
    let pulsing = false;
    if (o) {
      o.setTransform(dpr, 0, 0, dpr, 0, 0);
      o.clearRect(0, 0, w, h);
      const text = cssVar('--text', th === 'dark' ? '#e7e9ec' : '#14171c');
      const bg = cssVar('--canvas-bg', th === 'dark' ? '#0a0c0f' : '#eef1f5');
      o.font = '600 12px ui-sans-serif, system-ui, sans-serif';
      o.textBaseline = 'middle';
      const placed: Array<[number, number, number, number]> = [];
      for (const cl of c.clusters) {
        if (hid.has(legendKey(cl.id))) continue;
        const x = cl.x * scale + offX,
          y = cl.y * scale + offY - Math.min(60, cl.spread * scale * 1.1) - 8;
        if (x < -100 || y < -20 || x > w + 100 || y > h + 20) continue;
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
      const ring = (i: number, r: number, color: string, width: number, alpha = 1) => {
        const sx = xy[2 * i]! * scale + offX,
          sy = xy[2 * i + 1]! * scale + offY;
        o.globalAlpha = alpha;
        o.beginPath();
        o.arc(sx, sy, r, 0, Math.PI * 2);
        o.strokeStyle = color;
        o.lineWidth = width;
        o.stroke();
        o.globalAlpha = 1;
      };
      for (const i of ls) if (props.current.mask[i]) ring(i, 5, text, 1, 0.55);
      const now = performance.now();
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
          o.arc(c.summaries.xy[2 * s]! * scale + offX, c.summaries.xy[2 * s + 1]! * scale + offY, 10, 0, Math.PI * 2);
          o.strokeStyle = text;
          o.lineWidth = 2;
          o.setLineDash([3, 3]);
          o.stroke();
          o.setLineDash([]);
        }
      }
      if (hoverIx.current !== null) ring(hoverIx.current, 7, text, 1.5, 0.8);
      const lp = lassoPath.current;
      if (lp && lp.length > 1) {
        o.beginPath();
        o.moveTo(lp[0]![0], lp[0]![1]);
        for (const [x, y] of lp) o.lineTo(x, y);
        o.closePath();
        o.fillStyle = th === 'dark' ? 'rgba(255,255,255,0.06)' : 'rgba(0,0,0,0.05)';
        o.fill();
        o.setLineDash([4, 3]);
        o.strokeStyle = text;
        o.lineWidth = 1;
        o.stroke();
        o.setLineDash([]);
      }
    }
    const s = stats.current;
    s.frames++;
    s.drawMs += performance.now() - t0;
    if (pulsing) raf.current = requestAnimationFrame(draw);
  }, [size, n, xy, frame, uploadStyle, indexOf, summaryIndexOf]);

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
    (v: View) => {
      view.current = v;
      schedule();
    },
    [schedule],
  );

  const zoomAt = useCallback(
    (sx: number, sy: number, factor: number) => {
      const v = view.current;
      const k = Math.min(MAX_K, Math.max(0.5, v.k * factor));
      const f = k / v.k;
      setView({ k, tx: sx - (sx - v.tx) * f, ty: sy - (sy - v.ty) * f });
    },
    [setView],
  );

  useImperativeHandle(
    ref,
    () => ({
      fit: () => setView({ k: 1, tx: 0, ty: 0 }),
      reveal: (id: number) => {
        const i = indexOf.get(id);
        const s = summaryIndexOf.get(id);
        const p =
          i !== undefined
            ? [xy[2 * i]!, xy[2 * i + 1]!]
            : s !== undefined
              ? [cloud.summaries.xy[2 * s]!, cloud.summaries.xy[2 * s + 1]!]
              : null;
        if (!p || !size.w) return;
        const f = frame(size.w, size.h);
        const k = Math.max(view.current.k, 4);
        setView({ k, tx: size.w / 2 - (f.ox + p[0]! * f.base) * k, ty: size.h / 2 - (f.oy + p[1]! * f.base) * k });
      },
    }),
    [indexOf, summaryIndexOf, xy, cloud.summaries.xy, size, frame, setView],
  );

  // wheel must be non-passive to stop the page scrolling
  useEffect(() => {
    const el = wrap.current;
    if (!el) return;
    const onWheel = (e: WheelEvent) => {
      e.preventDefault();
      const r = el.getBoundingClientRect();
      zoomAt(e.clientX - r.left, e.clientY - r.top, Math.exp(-e.deltaY * (e.deltaMode === 1 ? 0.05 : 0.0016)));
    };
    el.addEventListener('wheel', onWheel, { passive: false });
    return () => el.removeEventListener('wheel', onWheel);
  }, [zoomAt]);

  const hitAt = (sx: number, sy: number): number => {
    const [x, y] = toData(sx, sy);
    const f = frame(size.w, size.h);
    return nearest(grid, xy, x, y, 9 / (f.base * view.current.k), mask);
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
      className={cn(
        'touch-none overflow-hidden outline-none select-none focus-visible:ring-2 focus-visible:ring-[var(--ring)]',
        className,
      )}
      onPointerDown={(e) => {
        if (e.button !== 0) return;
        wrap.current?.focus({ preventScroll: true });
        (e.currentTarget as HTMLElement).setPointerCapture(e.pointerId);
        const [x, y] = local(e);
        if (e.shiftKey) lassoPath.current = [[x, y]];
        else drag.current = { x, y, tx: view.current.tx, ty: view.current.ty, moved: false };
      }}
      onPointerMove={(e) => {
        const [x, y] = local(e);
        if (lassoPath.current) {
          lassoPath.current.push([x, y]);
          schedule();
          return;
        }
        const d = drag.current;
        if (d) {
          if (Math.abs(x - d.x) + Math.abs(y - d.y) > 3) d.moved = true;
          if (d.moved) {
            setView({ k: view.current.k, tx: d.tx + x - d.x, ty: d.ty + y - d.y });
            if (hoverIx.current !== null) {
              hoverIx.current = null;
              onHover(null, 0, 0);
            }
            return;
          }
        }
        const i = hitAt(x, y);
        const next = i >= 0 ? i : null;
        if (next !== hoverIx.current) {
          hoverIx.current = next;
          schedule();
        }
        onHover(next, x, y);
      }}
      onPointerUp={(e) => {
        const [x, y] = local(e);
        const lp = lassoPath.current;
        if (lp) {
          lassoPath.current = null;
          onLasso(
            lp.length > 2
              ? lassoSelect(
                  grid,
                  xy,
                  lp.map(([px, py]) => toData(px, py)),
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
          const i = hitAt(x, y);
          onSelect(i >= 0 ? cloud.points.id[i]! : null);
        }
      }}
      onPointerLeave={() => {
        if (hoverIx.current !== null) {
          hoverIx.current = null;
          schedule();
        }
        onHover(null, 0, 0);
      }}
      onDoubleClick={(e) => {
        const [x, y] = local(e);
        zoomAt(x, y, 2);
      }}
      onKeyDown={(e) => {
        const v = view.current;
        const step = 60;
        if (e.key === 'f' || e.key === 'F' || e.key === 'Home') setView({ k: 1, tx: 0, ty: 0 });
        else if (e.key === '+' || e.key === '=') zoomAt(size.w / 2, size.h / 2, 1.4);
        else if (e.key === '-' || e.key === '_') zoomAt(size.w / 2, size.h / 2, 1 / 1.4);
        else if (e.key === 'ArrowLeft') setView({ ...v, tx: v.tx + step });
        else if (e.key === 'ArrowRight') setView({ ...v, tx: v.tx - step });
        else if (e.key === 'ArrowUp') setView({ ...v, ty: v.ty + step });
        else if (e.key === 'ArrowDown') setView({ ...v, ty: v.ty - step });
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
      {glError ? (
        <div className="absolute inset-x-6 top-6 rounded-lg border border-border bg-surface p-3 text-sm text-muted">
          {glError}
        </div>
      ) : null}
    </div>
  );
});
