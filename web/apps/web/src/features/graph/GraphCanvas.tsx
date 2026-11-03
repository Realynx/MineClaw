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

function cssVar(name: string, fallback: string): string {
  if (typeof window === 'undefined') return fallback;
  return getComputedStyle(document.documentElement).getPropertyValue(name).trim() || fallback;
}

function traceShape(ctx: CanvasRenderingContext2D, n: GraphNode, layout: GraphLayout, f: Frame, v: View, inset = 0): { w: number; h: number } {
  if (layout === 'icicle') {
    const [x0, y0] = toScreen(f, v, n.x - (n.w ?? 0) / 2, n.y - (n.h ?? 0) / 2);
    const [x1, y1] = toScreen(f, v, n.x + (n.w ?? 0) / 2, n.y + (n.h ?? 0) / 2);
    const gap = Math.min(1, (x1 - x0) * 0.15);
    const w = Math.max(0, x1 - x0 - gap - inset * 2);
    const h = Math.max(0, y1 - y0 - 1.5 - inset * 2);
    ctx.beginPath();
    ctx.rect(x0 + inset, y0 + inset, w, h);
    return { w, h };
  }
  if (layout === 'radial') {
    const [cx, cy] = toScreen(f, v, 0.5, 0.5);
    const s = f.bw * v.k;
    const r0 = (n.r0 ?? 0) * s + inset;
    const r1 = Math.max(r0, (n.r1 ?? 0) * s - 0.75 - inset);
    const a0 = (n.a0 ?? 0) - Math.PI / 2;
    const a1 = Math.max(a0, (n.a1 ?? 0) - Math.PI / 2 - Math.min(0.004, ((n.a1 ?? 0) - (n.a0 ?? 0)) * 0.1));
    ctx.beginPath();
    ctx.arc(cx, cy, r1, a0, a1);
    ctx.arc(cx, cy, r0, a1, a0, true);
    ctx.closePath();
    return { w: (a1 - a0) * r1, h: r1 - r0 };
  }
  const [x, y] = toScreen(f, v, n.x, n.y);
  const r = Math.max(1.5, (n.r ?? 0.005) * f.bw * v.k - inset);
  ctx.beginPath();
  ctx.arc(x, y, r, 0, TAU);
  return { w: r * 2, h: r * 2 };
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

  useEffect(() => setView({ k: 1, tx: 0, ty: 0 }), [graph.layout, graph.focus]);

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

  // ---- draw ----
  useEffect(() => {
    const el = canvas.current;
    if (!el || !size.w || !size.h) return;
    const dpr = window.devicePixelRatio || 1;
    if (el.width !== Math.round(size.w * dpr)) el.width = Math.round(size.w * dpr);
    if (el.height !== Math.round(size.h * dpr)) el.height = Math.round(size.h * dpr);
    const ctx = el.getContext('2d');
    if (!ctx) return;
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    const bg = cssVar('--canvas-bg', '#0a0c0f');
    const text = cssVar('--text', '#e7e9ec');
    const subtle = cssVar('--text-subtle', '#6f7884');
    ctx.fillStyle = bg;
    ctx.fillRect(0, 0, size.w, size.h);
    const layout = graph.layout;

    // edges (node-link only)
    if (layout === 'nodelink') {
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

    const glowing: Array<[GraphNode, NodeStyle]> = [];
    for (const n of ordered) {
      const st = styleFor(n);
      // level of detail: skip shapes smaller than a pixel
      if (layout === 'icicle' && (n.w ?? 0) * frame.bw * view.k < 0.7) continue;
      if (layout === 'radial' && ((n.a1 ?? 0) - (n.a0 ?? 0)) * (n.r1 ?? 0) * frame.bw * view.k < 0.7) continue;
      const { w, h } = traceShape(ctx, n, layout, frame, view);
      if (w <= 0 || h <= 0) continue;
      if (st.ring?.hollow) {
        ctx.globalAlpha = 1;
        ctx.fillStyle = bg;
        ctx.fill();
      } else {
        ctx.globalAlpha = st.alpha;
        ctx.fillStyle = st.fill;
        ctx.fill();
      }
      ctx.globalAlpha = 1;
      if (st.glow && st.glow.strength > 0.02) glowing.push([n, st]);
      if (st.ring && Math.min(w, h) > 3) {
        traceShape(ctx, n, layout, frame, view, 0.5);
        ctx.strokeStyle = st.ring.color;
        ctx.lineWidth = Math.max(1, Math.min(4, st.ring.width * Math.min(w, h) * 0.3));
        ctx.setLineDash(st.ring.hollow ? [3, 2] : []);
        ctx.stroke();
        ctx.setLineDash([]);
      }
      if (st.outline) {
        traceShape(ctx, n, layout, frame, view);
        ctx.strokeStyle = st.outline;
        ctx.lineWidth = 1.5;
        ctx.stroke();
      }
    }
    // glow pass on top, additive so lit regions read as light
    if (glowing.length) {
      ctx.save();
      ctx.globalCompositeOperation = document.documentElement.classList.contains('dark') ? 'screen' : 'source-over';
      for (const [n, st] of glowing) {
        const g = st.glow!;
        traceShape(ctx, n, layout, frame, view);
        ctx.shadowColor = g.color;
        ctx.shadowBlur = 4 + 18 * g.strength;
        ctx.globalAlpha = 0.15 + 0.6 * g.strength;
        ctx.fillStyle = g.color;
        ctx.fill();
      }
      ctx.restore();
    }
    // selection
    if (selected !== null) {
      const n = idx.byId.get(selected);
      if (n) {
        traceShape(ctx, n, layout, frame, view, -1.5);
        ctx.strokeStyle = text;
        ctx.lineWidth = 2;
        ctx.stroke();
      }
    }
    // labels where they fit
    ctx.font = '500 11px Inter, "Segoe UI", system-ui, sans-serif';
    ctx.textBaseline = 'middle';
    ctx.fillStyle = text;
    for (const n of ordered) {
      if (layout === 'icicle') {
        const pw = (n.w ?? 0) * frame.bw * view.k;
        const ph = (n.h ?? 0) * frame.bh * view.k;
        if (pw < 54 || ph < 16) continue;
        const [x0, y] = toScreen(frame, view, n.x - (n.w ?? 0) / 2, n.y);
        if (x0 + pw < 0 || x0 > size.w) continue;
        const maxChars = Math.floor((pw - 10) / 6.2);
        const label = n.label.length > maxChars ? `${n.label.slice(0, Math.max(1, maxChars - 1))}…` : n.label;
        ctx.globalAlpha = 0.92;
        ctx.fillText(label, Math.max(4, x0 + 5), y);
      } else if (layout === 'nodelink' && n.layer >= 2) {
        const [x, y] = toScreen(frame, view, n.x, n.y);
        const r = (n.r ?? 0) * frame.bw * view.k;
        ctx.globalAlpha = 0.85;
        ctx.fillText(n.label, x + r + 4, y);
      } else if (layout === 'radial' && n.layer >= 2) {
        const [x, y] = toScreen(frame, view, n.x, n.y);
        ctx.globalAlpha = 0.9;
        ctx.textAlign = 'center';
        ctx.fillText(n.label.length > 18 ? `${n.label.slice(0, 17)}…` : n.label, x, y);
        ctx.textAlign = 'left';
      }
    }
    ctx.globalAlpha = 1;
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
    const dirs: Record<string, 'up' | 'down' | 'left' | 'right'> = { ArrowUp: 'up', ArrowDown: 'down', ArrowLeft: 'left', ArrowRight: 'right' };
    const d = dirs[e.key];
    if (d) {
      e.preventDefault();
      // icicle: up/down are parent/child, left/right siblings; same for the others
      onSelect?.(walk(idx, selected, d));
    } else if (e.key === 'Enter' && selected !== null) {
      onFocus?.(selected);
    } else if (e.key === 'f' || e.key === 'F') {
      setView({ k: 1, tx: 0, ty: 0 });
    } else if (e.key === 'Home') {
      setView({ k: 1, tx: 0, ty: 0 });
      onFocus?.(null);
    } else if (e.key === 'Escape') {
      if (view.k > 1.01) setView({ k: 1, tx: 0, ty: 0 });
      else onFocus?.(null);
    } else if (e.key === '+' || e.key === '=') {
      setView((v) => zoomAt(v, 1.25, size.w / 2, size.h / 2));
    } else if (e.key === '-') {
      setView((v) => zoomAt(v, 0.8, size.w / 2, size.h / 2));
    }
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
