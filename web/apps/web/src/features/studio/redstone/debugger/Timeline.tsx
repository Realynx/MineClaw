'use client';

/**
 * The timeline: the probes' history as a logic analyser on a 2D canvas (./timelineDraw), drawn again only when the tick,
 * the zoom, the size or the probes change. A click on an earlier tick still in history steps back to it.
 */
import { ZoomIn, ZoomOut } from 'lucide-react';
import { useEffect, useRef, useState, type MouseEvent } from 'react';
import type { SimControllerApi, SimSnapshot } from '../simController.types';
import {
  DEFAULT_ZOOM,
  firstTick,
  stepBackTarget,
  tickAtX,
  timelineHeight,
  TimelinePainter,
  ZOOMS,
} from './timelineDraw';

/** The width drawn before the canvas has been measured (the panel's own). */
const DEFAULT_WIDTH = 264;

export function Timeline({ api, snap }: { api: SimControllerApi; snap: SimSnapshot }) {
  const canvas = useRef<HTMLCanvasElement>(null);
  const box = useRef<HTMLDivElement>(null);
  const [painter] = useState(() => new TimelinePainter());
  const [zoom, setZoom] = useState(DEFAULT_ZOOM);
  const [width, setWidth] = useState(DEFAULT_WIDTH);
  const [hover, setHover] = useState<number | null>(null);
  const { tick, canStepBack, probes, mode } = snap;
  const height = timelineHeight(probes.length);
  const historyFrom = tick - canStepBack;

  useEffect(() => {
    const el = box.current;
    if (!el) return;
    const measure = () => setWidth(Math.max(40, Math.floor(el.clientWidth) || DEFAULT_WIDTH));
    measure();
    if (typeof ResizeObserver === 'undefined') return;
    const ro = new ResizeObserver(measure);
    ro.observe(el);
    return () => ro.disconnect();
  }, []);

  useEffect(() => {
    const el = canvas.current;
    const ctx = el?.getContext('2d');
    if (!el || !ctx) return;
    const dpr = typeof window === 'undefined' ? 1 : window.devicePixelRatio || 1;
    const w = Math.round(width * dpr);
    const h = Math.round(height * dpr);
    // A new backing size clears the canvas, so the next paint must draw whatever it is given.
    if (el.width !== w || el.height !== h) {
      el.width = w;
      el.height = h;
      painter.invalidate();
    }
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    painter.paint(ctx, { width, pxPerTick: zoom, tick, historyFrom, mode, probes }, (id, f, t) =>
      api.timeline(id, f, t),
    );
  }, [api, painter, width, height, zoom, tick, historyFrom, mode, probes]);

  const xOf = (e: MouseEvent<HTMLCanvasElement>) => e.clientX - e.currentTarget.getBoundingClientRect().left;
  const onClick = (e: MouseEvent<HTMLCanvasElement>) => {
    const t = stepBackTarget(xOf(e), { width, pxPerTick: zoom, tick, historyFrom });
    if (t !== null) api.stepBackTo(t);
  };
  const zoomBy = (by: -1 | 1) => {
    const i = ZOOMS.indexOf(zoom) + by;
    if (i >= 0 && i < ZOOMS.length) setZoom(ZOOMS[i]!);
  };

  return (
    <section className="px-3 py-2" aria-label="Timeline" data-testid="timeline">
      <div className="flex items-center gap-1">
        <h3 className="text-[11px] font-semibold text-fg">Timeline</h3>
        <span className="font-mono text-[10px] text-muted" data-testid="timeline-hover">
          {hover !== null ? `tick ${hover}` : ''}
        </span>
        <span className="ml-auto" />
        <button
          type="button"
          aria-label="Zoom out"
          onClick={() => zoomBy(-1)}
          disabled={zoom === ZOOMS[0]}
          className="rounded p-0.5 text-muted hover:bg-surface-2 hover:text-fg disabled:opacity-40"
        >
          <ZoomOut className="size-3.5" />
        </button>
        <span className="w-12 text-center font-mono text-[10px] text-muted" data-testid="timeline-zoom">
          {zoom} px/gt
        </span>
        <button
          type="button"
          aria-label="Zoom in"
          onClick={() => zoomBy(1)}
          disabled={zoom === ZOOMS[ZOOMS.length - 1]}
          className="rounded p-0.5 text-muted hover:bg-surface-2 hover:text-fg disabled:opacity-40"
        >
          <ZoomIn className="size-3.5" />
        </button>
      </div>
      <div ref={box} className="mt-1 w-full">
        {probes.length === 0 ? (
          <p className="text-[11px] text-muted">Add a probe to see its trace here.</p>
        ) : (
          <canvas
            ref={canvas}
            aria-label="Probe timeline: click an earlier tick to step back to it"
            data-testid="timeline-canvas"
            style={{ width, height }}
            className="block cursor-crosshair rounded border border-border"
            onClick={onClick}
            onMouseMove={(e) => {
              const t = tickAtX(xOf(e), firstTick(tick, width, zoom), zoom);
              setHover(t >= 0 && t <= tick ? t : null);
            }}
            onMouseLeave={() => setHover(null)}
          />
        )}
      </div>
    </section>
  );
}
