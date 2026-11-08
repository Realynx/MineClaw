'use client';

import { useLayoutEffect, useRef, useState, type CSSProperties, type KeyboardEvent, type ReactNode } from 'react';
import type { PaletteEntry } from './palette';

const CELL = 44,
  STEP = 48,
  PAD = 8,
  DEFAULT_WIDTH = 624,
  MAX_HEIGHT = 384;

export interface PaletteCellLayout {
  style: CSSProperties;
  index: number;
  size: number;
  tabIndex: 0 | -1;
}

/** Only the visible rows and one spare row on either side are mounted, regardless of palette size. */
export function PaletteGrid({
  entries,
  cell,
}: {
  entries: readonly PaletteEntry[];
  cell: (entry: PaletteEntry, layout: PaletteCellLayout) => ReactNode;
}) {
  const scroll = useRef<HTMLDivElement>(null);
  const focusWanted = useRef(false);
  const [focused, setFocused] = useState(0);
  const [view, setView] = useState({ width: DEFAULT_WIDTH, height: MAX_HEIGHT, top: 0 });
  const cols = Math.max(1, Math.floor((view.width - PAD * 2 + STEP - CELL) / STEP));
  const rows = Math.ceil(entries.length / cols);
  const fullHeight = rows * STEP + PAD * 2;
  const firstRow = Math.max(0, Math.min(rows - 1, Math.floor((view.top - PAD) / STEP) - 1));
  const endRow = Math.min(rows, Math.ceil((view.top + view.height - PAD) / STEP) + 1);
  const start = firstRow * cols,
    end = endRow * cols;
  const columnWidth = Math.max(CELL, (view.width - PAD * 2 + STEP - CELL) / cols);

  useLayoutEffect(() => {
    const el = scroll.current;
    if (!el) return;
    const measure = () =>
      setView((old) => {
        const next = {
          width: el.clientWidth || DEFAULT_WIDTH,
          height: el.clientHeight || Math.min(MAX_HEIGHT, fullHeight),
          top: el.scrollTop,
        };
        return old.width === next.width && old.height === next.height && old.top === next.top ? old : next;
      });
    measure();
    const observer = typeof ResizeObserver === 'undefined' ? null : new ResizeObserver(measure);
    observer?.observe(el);
    return () => observer?.disconnect();
  }, [fullHeight]);

  useLayoutEffect(() => {
    if (scroll.current) scroll.current.scrollTop = 0;
    setView((old) => (old.top === 0 ? old : { ...old, top: 0 }));
    setFocused(0);
    focusWanted.current = false;
  }, [entries]);

  useLayoutEffect(() => {
    if (!focusWanted.current) return;
    const button = scroll.current?.querySelector<HTMLButtonElement>(`button[data-palette-index="${focused}"]`);
    if (button) {
      button.focus({ preventScroll: true });
      focusWanted.current = false;
    }
  }, [focused, start, end]);

  const moveFocus = (event: KeyboardEvent<HTMLDivElement>) => {
    const button = event.target;
    if (!(button instanceof HTMLButtonElement) || button.dataset.paletteIndex === undefined) return;
    const index = Number(button.dataset.paletteIndex);
    const page = Math.max(1, Math.floor(view.height / STEP)) * cols;
    const target = {
      ArrowLeft: index - 1,
      ArrowRight: index + 1,
      ArrowUp: index - cols,
      ArrowDown: index + cols,
      Home: 0,
      End: entries.length - 1,
      PageUp: index - page,
      PageDown: index + page,
    }[event.key];
    if (target === undefined || !entries.length) return;
    event.preventDefault();
    event.stopPropagation();
    const next = Math.max(0, Math.min(entries.length - 1, target));
    const top = PAD + Math.floor(next / cols) * STEP;
    const el = scroll.current;
    if (el) {
      if (top < el.scrollTop) el.scrollTop = top;
      else if (top + CELL > el.scrollTop + view.height) el.scrollTop = top + CELL - view.height;
      setView((old) => ({ ...old, top: el.scrollTop }));
    }
    focusWanted.current = true;
    setFocused(next);
  };

  return (
    <div
      ref={scroll}
      className="min-h-0 overflow-y-auto overscroll-contain"
      style={{ height: Math.min(MAX_HEIGHT, fullHeight) }}
      onScroll={(event) => {
        const top = event.currentTarget.scrollTop;
        setView((old) => (old.top === top ? old : { ...old, top }));
      }}
      data-testid="palette-scroll"
      onKeyDown={moveFocus}
    >
      <ul aria-label="Blocks" className="relative" style={{ height: fullHeight }}>
        {entries.slice(start, end).map((entry, offset) => {
          const index = start + offset;
          return cell(entry, {
            index,
            size: entries.length,
            tabIndex: index === focused ? 0 : -1,
            style: {
              position: 'absolute',
              top: PAD + Math.floor(index / cols) * STEP,
              left: PAD + (index % cols) * columnWidth,
              width: CELL,
              height: CELL,
            },
          });
        })}
      </ul>
    </div>
  );
}
