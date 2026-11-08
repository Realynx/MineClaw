'use client';

import { GripHorizontal, RotateCcw } from 'lucide-react';
import { useEffect, useRef, useState, type ReactNode } from 'react';

type Point = { x: number; y: number };
type Layout = Record<string, Point>;
type WindowSpec = { id: string; title: string; content: ReactNode };

export function clampWindow(
  point: Point,
  width: number,
  height: number,
  windowWidth: number,
  windowHeight: number,
): Point {
  return {
    x: Math.max(0, Math.min(point.x, width - Math.min(width, windowWidth))),
    y: Math.max(0, Math.min(point.y, height - Math.min(height, windowHeight))),
  };
}

export function loadWindowLayout(key: string): Layout {
  try {
    const value: unknown = JSON.parse(localStorage.getItem(key) ?? '{}');
    if (!value || typeof value !== 'object' || Array.isArray(value)) return {};
    const result: Layout = {};
    for (const [id, p] of Object.entries(value as Record<string, unknown>)) {
      if (!p || typeof p !== 'object') continue;
      const point = p as Partial<Point>;
      if (
        typeof point.x === 'number' &&
        Number.isFinite(point.x) &&
        typeof point.y === 'number' &&
        Number.isFinite(point.y)
      )
        result[id] = { x: point.x, y: point.y };
    }
    return result;
  } catch {
    return {};
  }
}

/** Positions are local to this controller viewport and saved per account and colony. */
export function ColonyWorkspace({ storageKey, windows }: { storageKey: string; windows: WindowSpec[] }) {
  const root = useRef<HTMLDivElement>(null);
  const [layout, setLayout] = useState<Layout>(() => loadWindowLayout(storageKey));
  const [front, setFront] = useState('');
  const [size, setSize] = useState({ width: 1000, height: 600 });
  useEffect(() => {
    const el = root.current;
    if (!el) return;
    const resize = () => setSize({ width: el.clientWidth, height: el.clientHeight });
    resize();
    const observer = new ResizeObserver(resize);
    observer.observe(el);
    return () => observer.disconnect();
  }, []);
  const save = (id: string, point: Point) =>
    setLayout((old) => {
      const next = { ...old, [id]: point };
      try {
        localStorage.setItem(storageKey, JSON.stringify(next));
      } catch {
        /* unavailable */
      }
      return next;
    });
  return (
    <div ref={root} className="pointer-events-none absolute inset-x-2 top-24 bottom-12" data-testid="colony-workspace">
      <div
        className="pointer-events-auto absolute right-0 -top-8 z-10 flex max-w-full items-center gap-1 overflow-x-auto rounded border border-border bg-surface p-1 text-[10px]"
        aria-label="Colony windows"
      >
        {windows.map((w) => (
          <button
            key={w.id}
            type="button"
            aria-label={`Show ${w.title} window`}
            aria-pressed={front === w.id}
            className="shrink-0 rounded px-1.5 py-0.5 hover:bg-surface-2 aria-pressed:bg-accent-soft"
            onClick={() => setFront(w.id)}
          >
            {w.title}
          </button>
        ))}
        <button
          type="button"
          className="flex shrink-0 items-center gap-1 border-l border-border px-1.5 py-0.5 text-subtle hover:text-fg"
          onClick={() => {
            setLayout({});
            try {
              localStorage.removeItem(storageKey);
            } catch {
              /* unavailable */
            }
          }}
        >
          <RotateCcw className="size-3" />
          Reset windows
        </button>
      </div>
      {windows.map((w, i) => (
        <MovableWindow
          key={w.id}
          window={w}
          point={
            layout[w.id] ?? {
              x: (i % Math.max(1, Math.floor(size.width / 350))) * 350,
              y: Math.floor(i / Math.max(1, Math.floor(size.width / 350))) * 48,
            }
          }
          bounds={size}
          active={front === w.id}
          onFront={() => setFront(w.id)}
          onMove={(p) => save(w.id, p)}
        />
      ))}
    </div>
  );
}

function MovableWindow({
  window: w,
  point,
  bounds,
  active,
  onFront,
  onMove,
}: {
  window: WindowSpec;
  point: Point;
  bounds: { width: number; height: number };
  active: boolean;
  onFront: () => void;
  onMove: (p: Point) => void;
}) {
  const ref = useRef<HTMLDivElement>(null);
  const drag = useRef<{ x: number; y: number; point: Point } | null>(null);
  const [moving, setMoving] = useState<Point | null>(null);
  const [extent, setExtent] = useState({ width: 340, height: 360 });
  useEffect(() => {
    const el = ref.current;
    if (!el) return;
    const observer = new ResizeObserver(() => setExtent({ width: el.offsetWidth, height: el.offsetHeight }));
    observer.observe(el);
    return () => observer.disconnect();
  }, []);
  const at = clampWindow(moving ?? point, bounds.width, bounds.height, extent.width, extent.height);
  return (
    <div
      ref={ref}
      className="pointer-events-auto absolute flex max-w-full flex-col overflow-hidden rounded-lg border border-border bg-surface shadow-pop [&>div>section]:max-w-full [&>div>section]:border-0 [&>div>section]:shadow-none"
      style={{ left: at.x, top: at.y, maxHeight: bounds.height, zIndex: active ? 2 : 1 }}
      onPointerDownCapture={onFront}
      data-window={w.id}
    >
      <button
        type="button"
        aria-label={`Move ${w.title} window`}
        title="Drag to move; arrow keys to move"
        className="flex shrink-0 touch-none cursor-grab items-center gap-2 border-b border-border bg-surface-2 px-2 py-1 text-xs font-semibold active:cursor-grabbing"
        onPointerDown={(e) => {
          if (e.button !== 0) return;
          e.preventDefault();
          drag.current = { x: e.clientX, y: e.clientY, point: at };
          e.currentTarget.setPointerCapture(e.pointerId);
        }}
        onPointerMove={(e) => {
          const d = drag.current;
          if (d)
            setMoving(
              clampWindow(
                { x: d.point.x + e.clientX - d.x, y: d.point.y + e.clientY - d.y },
                bounds.width,
                bounds.height,
                extent.width,
                extent.height,
              ),
            );
        }}
        onPointerUp={(e) => {
          if (!drag.current) return;
          const d = drag.current;
          onMove(
            clampWindow(
              { x: d.point.x + e.clientX - d.x, y: d.point.y + e.clientY - d.y },
              bounds.width,
              bounds.height,
              extent.width,
              extent.height,
            ),
          );
          drag.current = null;
          setMoving(null);
          e.currentTarget.releasePointerCapture(e.pointerId);
        }}
        onPointerCancel={() => {
          drag.current = null;
          setMoving(null);
        }}
        onKeyDown={(e) => {
          const delta: Record<string, Point> = {
            ArrowLeft: { x: -16, y: 0 },
            ArrowRight: { x: 16, y: 0 },
            ArrowUp: { x: 0, y: -16 },
            ArrowDown: { x: 0, y: 16 },
          };
          const d = delta[e.key];
          if (d) {
            e.preventDefault();
            e.stopPropagation();
            onMove(
              clampWindow({ x: at.x + d.x, y: at.y + d.y }, bounds.width, bounds.height, extent.width, extent.height),
            );
          }
        }}
      >
        <GripHorizontal className="size-3 text-muted" />
        {w.title}
      </button>
      <div className="min-h-0 overflow-auto">{w.content}</div>
    </div>
  );
}
