'use client';

/**
 * Canvas renderer for the server-laid-out RAPTOR graph (icicle / radial / node-link). The layout comes from the server; this
 * only maps unit space to pixels, draws, and handles interaction: wheel zoom about the pointer, drag to pan, click to select,
 * double-click to focus, arrows to walk the tree, Enter to select, F to fit, Home to reset, Esc to step out.
 */
import type { GraphLayout, GraphNode, RaptorGraph } from '@mineclaw/contracts';
import { forwardRef, useCallback, useEffect, useImperativeHandle, useMemo, useRef, useState, type KeyboardEvent } from 'react';
import { cn } from '@/lib/utils';
import { baseFrame, hitTest, indexGraph, toScreen, toUnit, walk, zoomAt, type Frame, type View } from './geometry';

export interface NodeStyle {
  fill: string;
  alpha: number;
  /** Glow (memory heat): colour and 0..1 strength. */
  glow?: { color: string; strength: number } | null;
  /** Ring (staleness): colour, 0..1 thickness share, hollow = evicted. */
  ring?: { color: string; width: number; hollow?: boolean } | null;
  outline?: string | null;
}

export interface GraphCanvasHandle {
  fit(): void;
  reveal(id: number): void;
}

const TAU = Math.PI * 2;

/** Draw timings (ms) for the perf check in scripts/screenshots.mjs (window.__mcxGraphDraws, last 240 draws). */
function recordDraw(ms: number): void {
  const w = window as unknown as { __mcxGraphDraws?: number[] };
  const list = (w.__mcxGraphDraws ??= []);
  list.push(ms);
  if (list.length > 240) list.shift();
}

function cssVar(name: string, fallback: string): string {
  if (typeof window === 'undefined') return fallback;
  return getComputedStyle(document.documentElement).getPropertyValue(name).trim() || fallback;
}

type Traced = { w: number; h: number };

/** An icicle cell: a rectangle with a hairline gap. */
function traceIcicle(ctx: CanvasRenderingContext2D, n: GraphNode, f: Frame, v: View, inset: number): Traced {
  const nw = n.w ?? 0,
    nh = n.h ?? 0;
  const [x0, y0] = toScreen(f, v, n.x - nw / 2, n.y - nh / 2);
  const [x1, y1] = toScreen(f, v, n.x + nw / 2, n.y + nh / 2);
  const gap = Math.min(1, (x1 - x0) * 0.15);
  const w = Math.max(0, x1 - x0 - gap - inset * 2);
  const h = Math.max(0, y1 - y0 - 1.5 - inset * 2);
  ctx.beginPath();
  ctx.rect(x0 + inset, y0 + inset, w, h);
  return { w, h };
}

/** A radial cell: an annular sector from 12 o'clock, a sliver short of its end angle. */
function traceRadial(ctx: CanvasRenderingContext2D, n: GraphNode, f: Frame, v: View, inset: number): Traced {
  const [cx, cy] = toScreen(f, v, 0.5, 0.5);
  const s = f.bw * v.k;
  const na0 = n.a0 ?? 0,
    na1 = n.a1 ?? 0;
  const r0 = (n.r0 ?? 0) * s + inset;
  const r1 = Math.max(r0, (n.r1 ?? 0) * s - 0.75 - inset);
  const a0 = na0 - Math.PI / 2;
  const a1 = Math.max(a0, na1 - Math.PI / 2 - Math.min(0.004, (na1 - na0) * 0.1));
  ctx.beginPath();
  ctx.arc(cx, cy, r1, a0, a1);
  ctx.arc(cx, cy, r0, a1, a0, true);
  ctx.closePath();
  return { w: (a1 - a0) * r1, h: r1 - r0 };
}

/** A node-link node: a circle (at least 1.5 px). */
function traceCircle(ctx: CanvasRenderingContext2D, n: GraphNode, f: Frame, v: View, inset: number): Traced {
  const [x, y] = toScreen(f, v, n.x, n.y);
  const r = Math.max(1.5, (n.r ?? 0.005) * f.bw * v.k - inset);
  ctx.beginPath();
  ctx.arc(x, y, r, 0, TAU);
  return { w: r * 2, h: r * 2 };
}

function traceShape(ctx: CanvasRenderingContext2D, n: GraphNode, layout: GraphLayout, f: Frame, v: View, inset = 0): Traced {
  if (layout === 'icicle') return traceIcicle(ctx, n, f, v, inset);
  if (layout === 'radial') return traceRadial(ctx, n, f, v, inset);
  return traceCircle(ctx, n, f, v, inset);
}

/** One frame's inputs. */
interface DrawCtx {
  ctx: CanvasRenderingContext2D;
  graph: RaptorGraph;
  ordered: readonly GraphNode[];
  idx: ReturnType<typeof indexGraph>;
  frame: Frame;
  view: View;
  size: { w: number; h: number };
  selected: number | null;
  styleFor: (n: GraphNode) => NodeStyle;
}

/** The theme colours a frame draws with. */
interface Ink {
  bg: string;
  text: string;
  subtle: string;
}

/** Edges (node-link only), shapes, the glow pass, the selection and the labels that fit. */
function drawGraph(d: DrawCtx): void {
  const { ctx, size } = d;
  const ink: Ink = {
    bg: cssVar('--canvas-bg', '#0a0c0f'),
    text: cssVar('--text', '#e7e9ec'),
    subtle: cssVar('--text-subtle', '#6f7884'),
  };
  ctx.fillStyle = ink.bg;
  ctx.fillRect(0, 0, size.w, size.h);
  if (d.graph.layout === 'nodelink') drawEdges(d, ink.subtle);
  const glowing: Array<[GraphNode, NodeStyle]> = [];
  for (const n of d.ordered) drawNode(d, n, ink.bg, glowing);
  if (glowing.length) drawGlow(d, glowing);
  drawSelection(d, ink.text);
  drawLabels(d, ink.text);
}

function drawEdges({ ctx, graph, idx, frame, view }: DrawCtx, subtle: string): void {
  ctx.lineWidth = 1;
  for (const e of graph.edges) {
    const a = idx.byId.get(e.source);
    const b = idx.byId.get(e.target);
    if (!a || !b) continue;
    const [x0, y0] = toScreen(frame, view, a.x, a.y);
    const [x1, y1] = toScreen(frame, view, b.x, b.y);
    ctx.strokeStyle = subtle;
    ctx.globalAlpha = e.primary ? 0.35 : 0.18;
    ctx.setLineDash(e.primary ? [] : [3, 3]);
    ctx.beginPath();
    ctx.moveTo(x0, y0);
    ctx.lineTo(x1, y1);
    ctx.stroke();
  }
  ctx.setLineDash([]);
  ctx.globalAlpha = 1;
}

/** Level of detail: icicle and radial shapes smaller than a pixel are skipped. */
function tooSmall(n: GraphNode, layout: GraphLayout, f: Frame, v: View): boolean {
  if (layout === 'icicle') return (n.w ?? 0) * f.bw * v.k < 0.7;
  if (layout === 'radial') return ((n.a1 ?? 0) - (n.a0 ?? 0)) * (n.r1 ?? 0) * f.bw * v.k < 0.7;
  return false;
}

/** Traces the node's shape when it is big enough to see; its size, or null (nothing to draw). */
function traceVisible({ ctx, graph, frame, view }: DrawCtx, n: GraphNode): { w: number; h: number } | null {
  if (tooSmall(n, graph.layout, frame, view)) return null;
  const size = traceShape(ctx, n, graph.layout, frame, view);
  return size.w <= 0 || size.h <= 0 ? null : size;
}

/** A node's shape, its staleness ring and outline; a glowing node is kept for the glow pass. */
function drawNode(d: DrawCtx, n: GraphNode, bg: string, glowing: Array<[GraphNode, NodeStyle]>): void {
  const { ctx, frame, view } = d;
  const layout = d.graph.layout;
  const st = d.styleFor(n);
  const shape = traceVisible(d, n);
  if (!shape) return;
  const { w, h } = shape;
  const hollow = !!st.ring?.hollow;
  ctx.globalAlpha = hollow ? 1 : st.alpha;
  ctx.fillStyle = hollow ? bg : st.fill;
  ctx.fill();
  ctx.globalAlpha = 1;
  if (st.glow && st.glow.strength > 0.02) glowing.push([n, st]);
  if (st.ring && Math.min(w, h) > 3) strokeRing(d, n, st.ring, Math.min(w, h));
  if (st.outline) {
    traceShape(ctx, n, layout, frame, view);
    ctx.strokeStyle = st.outline;
    ctx.lineWidth = 1.5;
    ctx.stroke();
  }
}

function strokeRing({ ctx, graph, frame, view }: DrawCtx, n: GraphNode, ring: NonNullable<NodeStyle['ring']>, side: number): void {
  traceShape(ctx, n, graph.layout, frame, view, 0.5);
  ctx.strokeStyle = ring.color;
  ctx.lineWidth = Math.max(1, Math.min(4, ring.width * side * 0.3));
  ctx.setLineDash(ring.hollow ? [3, 2] : []);
  ctx.stroke();
  ctx.setLineDash([]);
}

/** The glow pass on top, additive so lit regions read as light. */
function drawGlow({ ctx, graph, frame, view }: DrawCtx, glowing: Array<[GraphNode, NodeStyle]>): void {
  ctx.save();
  ctx.globalCompositeOperation = document.documentElement.classList.contains('dark') ? 'screen' : 'source-over';
  for (const [n, st] of glowing) {
    const g = st.glow!;
    // Cheap on software rendering: per-node alpha plus a thin halo stroke, never shadowBlur or filters.
    const { w, h } = traceShape(ctx, n, graph.layout, frame, view);
    ctx.globalAlpha = 0.12 + 0.7 * g.strength;
    ctx.fillStyle = g.color;
    ctx.fill();
    if (g.strength > 0.35 && Math.min(w, h) > 2) {
      ctx.globalAlpha = g.strength * 0.9;
      ctx.strokeStyle = g.color;
      ctx.lineWidth = 1.5;
      ctx.stroke();
    }
  }
  ctx.restore();
}

function drawSelection({ ctx, graph, idx, frame, view, selected }: DrawCtx, text: string): void {
  if (selected === null) return;
  const n = idx.byId.get(selected);
  if (!n) return;
  traceShape(ctx, n, graph.layout, frame, view, -1.5);
  ctx.strokeStyle = text;
  ctx.lineWidth = 2;
  ctx.stroke();
}

/** An icicle cell's label, cut to its width; nothing when the cell is too small or off screen. */
function icicleLabel({ ctx, frame, view, size }: DrawCtx, n: GraphNode): void {
  const pw = (n.w ?? 0) * frame.bw * view.k;
  const ph = (n.h ?? 0) * frame.bh * view.k;
  if (pw < 54 || ph < 16) return;
  const [x0, y] = toScreen(frame, view, n.x - (n.w ?? 0) / 2, n.y);
  if (x0 + pw < 0 || x0 > size.w) return;
  const maxChars = Math.floor((pw - 10) / 6.2);
  const label = n.label.length > maxChars ? `${n.label.slice(0, Math.max(1, maxChars - 1))}…` : n.label;
  ctx.globalAlpha = 0.92;
  ctx.fillText(label, Math.max(4, x0 + 5), y);
}

/** A node-link summary's label right of its circle. */
function nodeLinkLabel({ ctx, frame, view }: DrawCtx, n: GraphNode): void {
  if (n.layer < 2) return;
  const [x, y] = toScreen(frame, view, n.x, n.y);
  const r = (n.r ?? 0) * frame.bw * view.k;
  ctx.globalAlpha = 0.85;
  ctx.fillText(n.label, x + r + 4, y);
}

/** A radial summary's label centred on its arc (18 characters at most). */
function radialLabel({ ctx, frame, view }: DrawCtx, n: GraphNode): void {
  if (n.layer < 2) return;
  const [x, y] = toScreen(frame, view, n.x, n.y);
  ctx.globalAlpha = 0.9;
  ctx.textAlign = 'center';
  ctx.fillText(n.label.length > 18 ? `${n.label.slice(0, 17)}…` : n.label, x, y);
  ctx.textAlign = 'left';
}

const LABELS: Record<GraphLayout, (d: DrawCtx, n: GraphNode) => void> = {
  icicle: icicleLabel,
  nodelink: nodeLinkLabel,
  radial: radialLabel,
};

/** Labels where they fit. */
function drawLabels(d: DrawCtx, text: string): void {
  const { ctx } = d;
  ctx.font = '500 11px Inter, "Segoe UI", system-ui, sans-serif';
  ctx.textBaseline = 'middle';
  ctx.fillStyle = text;
  const label = LABELS[d.graph.layout];
  if (label) for (const n of d.ordered) label(d, n);
  ctx.globalAlpha = 1;
}

const ARROW_DIRS: Record<string, 'up' | 'down' | 'left' | 'right'> = {
  ArrowUp: 'up',
  ArrowDown: 'down',
  ArrowLeft: 'left',
  ArrowRight: 'right',
};
const ZOOM_KEYS: Record<string, number> = { '+': 1.25, '=': 1.25, '-': 0.8 };
const FIT: View = { k: 1, tx: 0, ty: 0 };

/** F fits, Home fits and steps out of the focus, Esc fits when zoomed in, else steps out. */
function viewKey(key: string, k: number, setView: (v: View) => void, focus: (id: null) => void): void {
  if (key === 'f' || key === 'F') setView({ ...FIT });
  else if (key === 'Home') {
    setView({ ...FIT });
    focus(null);
  } else if (key === 'Escape') {
    if (k > 1.01) setView({ ...FIT });
    else focus(null);
  }
}

export const GraphCanvas = forwardRef<
  GraphCanvasHandle,
  {
    graph: RaptorGraph;
    styleFor: (n: GraphNode) => NodeStyle;
    selected?: number | null;
    onSelect?: (id: number | null) => void;
    onFocus?: (id: number | null) => void;
    onHover?: (n: GraphNode | null, x: number, y: number) => void;
    /** Changes force a redraw (e.g. heat ticks). */
    frameKey?: unknown;
    className?: string;
    label?: string;
  }
>(function GraphCanvas({ graph, styleFor, selected = null, onSelect, onFocus, onHover, frameKey, className, label = 'Memory graph' }, ref) {
  const canvas = useRef<HTMLCanvasElement>(null);
  const [size, setSize] = useState({ w: 0, h: 0 });
  const [view, setView] = useState<View>({ k: 1, tx: 0, ty: 0 });
  const drag = useRef<{ x: number; y: number; tx: number; ty: number; moved: boolean } | null>(null);
  const idx = useMemo(() => indexGraph(graph), [graph]);
  const frame = useMemo(() => baseFrame(graph.layout, size.w, size.h), [graph.layout, size]);
  // draw order: shallow first, leaves last (hit testing relies on it)
  const ordered = useMemo(() => [...graph.nodes].sort((a, b) => b.layer - a.layer), [graph.nodes]);

  useEffect(() => {
    setView({ k: 1, tx: 0, ty: 0 });
  }, [graph.layout, graph.focus]);

  useEffect(() => {
    const el = canvas.current;
    if (!el) return;
    const ro = new ResizeObserver(() => setSize({ w: el.clientWidth, h: el.clientHeight }));
    ro.observe(el);
    setSize({ w: el.clientWidth, h: el.clientHeight });
    return () => ro.disconnect();
  }, []);

  useImperativeHandle(
    ref,
    () => ({
      fit: () => setView({ k: 1, tx: 0, ty: 0 }),
      reveal: (id: number) => {
        const n = idx.byId.get(id);
        if (!n || !size.w) return;
        const k = Math.max(view.k, graph.layout === 'icicle' ? Math.min(40, 0.25 / Math.max(0.0005, n.w ?? 0.01)) : 3);
        const [sx, sy] = toScreen(frame, { k, tx: 0, ty: 0 }, n.x, n.y);
        setView({ k, tx: size.w / 2 - sx, ty: size.h / 2 - sy });
      },
    }),
    [idx, size, view.k, frame, graph.layout],
  );

  // ---- draw (once per animation frame, however many state changes arrived) ----
  useEffect(() => {
    const raf = requestAnimationFrame(() => {
      const t0 = performance.now();
      const el = canvas.current;
      if (!el || !size.w || !size.h) return;
      const dpr = window.devicePixelRatio || 1;
      if (el.width !== Math.round(size.w * dpr)) el.width = Math.round(size.w * dpr);
      if (el.height !== Math.round(size.h * dpr)) el.height = Math.round(size.h * dpr);
      const ctx = el.getContext('2d');
      if (!ctx) return;
      ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
      drawGraph({ ctx, graph, ordered, idx, frame, view, size, selected, styleFor });
      recordDraw(performance.now() - t0);
    });
    return () => cancelAnimationFrame(raf);
  }, [graph, ordered, idx, frame, view, size, selected, styleFor, frameKey]);

  // ---- interaction ----
  const pointToNode = useCallback(
    (clientX: number, clientY: number): GraphNode | null => {
      const el = canvas.current;
      if (!el) return null;
      const rect = el.getBoundingClientRect();
      const [ux, uy] = toUnit(frame, view, clientX - rect.left, clientY - rect.top);
      return hitTest(ordered, graph.layout, ux, uy, 1 / (frame.bw * view.k));
    },
    [frame, view, ordered, graph.layout],
  );

  useEffect(() => {
    const el = canvas.current;
    if (!el) return;
    const onWheel = (e: WheelEvent) => {
      e.preventDefault();
      const rect = el.getBoundingClientRect();
      setView((v) => zoomAt(v, Math.exp(-e.deltaY * 0.0015), e.clientX - rect.left, e.clientY - rect.top));
    };
    el.addEventListener('wheel', onWheel, { passive: false });
    return () => el.removeEventListener('wheel', onWheel);
  }, []);

  const onKey = (e: KeyboardEvent<HTMLCanvasElement>) => {
    const d = ARROW_DIRS[e.key];
    if (d) {
      e.preventDefault();
      // icicle: up/down are parent/child, left/right siblings; same for the others
      onSelect?.(walk(idx, selected, d));
      return;
    }
    if (e.key === 'Enter') {
      if (selected !== null) onFocus?.(selected);
      return;
    }
    const zoom = ZOOM_KEYS[e.key];
    if (zoom) return setView((v) => zoomAt(v, zoom, size.w / 2, size.h / 2));
    viewKey(e.key, view.k, setView, (id) => onFocus?.(id));
  };

  return (
    <canvas
      ref={canvas}
      tabIndex={0}
      role="application"
      aria-label={`${label}. Arrow keys walk the tree, Enter focuses, F fits, Escape steps out.`}
      className={cn('block size-full cursor-grab touch-none outline-none focus-visible:ring-2 focus-visible:ring-[var(--ring)] active:cursor-grabbing', className)}
      onKeyDown={onKey}
      onPointerDown={(e) => {
        (e.target as HTMLCanvasElement).setPointerCapture(e.pointerId);
        drag.current = { x: e.clientX, y: e.clientY, tx: view.tx, ty: view.ty, moved: false };
      }}
      onPointerMove={(e) => {
        const d = drag.current;
        if (d) {
          const dx = e.clientX - d.x;
          const dy = e.clientY - d.y;
          if (Math.abs(dx) + Math.abs(dy) > 3) d.moved = true;
          if (d.moved) setView((v) => ({ ...v, tx: d.tx + dx, ty: d.ty + dy }));
          return;
        }
        const rect = canvas.current!.getBoundingClientRect();
        onHover?.(pointToNode(e.clientX, e.clientY), e.clientX - rect.left, e.clientY - rect.top);
      }}
      onPointerUp={(e) => {
        const d = drag.current;
        drag.current = null;
        if (d && !d.moved) onSelect?.(pointToNode(e.clientX, e.clientY)?.id ?? null);
      }}
      onPointerLeave={() => onHover?.(null, 0, 0)}
      onDoubleClick={(e) => {
        const n = pointToNode(e.clientX, e.clientY);
        if (n && !n.isLeaf) onFocus?.(n.id);
      }}
    />
  );
});
