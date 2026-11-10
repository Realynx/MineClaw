'use client';

import { Focus, Group, Layers3 } from 'lucide-react';
import { useEffect, useRef, useState, type KeyboardEvent, type MouseEvent, type ReactNode } from 'react';
import { cn } from '@/lib/utils';

/** One placed component as the outliner lists it. */
export interface OutlinerItem {
  id: string;
  /** Its name; the row reads "name n", n its place in the list. */
  name: string;
  /** A small line under the name (where it stands). */
  detail?: string;
  /** The row's tooltip, before "Double-click to frame." */
  title?: string;
  glyph?: ReactNode;
  /** Drawn faded (a hidden wireframe). */
  dim?: boolean;
}

export interface ComponentOutlinerProps {
  items: readonly OutlinerItem[];
  selectedId?: string | null;
  onSelect?: (id: string) => void;
  onFrame?: (id: string) => void;
  onCopy?: (id: string) => void;
  onPaste?: () => void;
  onDuplicate?: (id: string) => void;
  onCombine?: (ids: string[]) => void;
  canPaste?: boolean;
  /** The Combine checklist's ticked rows (null: closed). Given with `onCheck`, the outliner does not keep its own. */
  checked?: string[] | null;
  onCheck?: (ids: string[] | null) => void;
  disabled?: boolean;
  /** Buttons at the end of a row; `label` is the row's name ("name n"). */
  rowActions?: (item: OutlinerItem, label: string) => ReactNode;
  labels: {
    /** The list's accessible name. */
    list: string;
    /** The Combine checklist's accessible name. */
    combine: string;
    testId?: string;
  };
  /** Under the list. */
  hint?: ReactNode;
  /** Shown instead of the list when nothing is placed. */
  empty?: ReactNode;
  /** At the top of the Combine checklist. */
  combineHint?: ReactNode;
}

/** A small icon button at the end of an outliner row. */
export function OutlinerRowAction(p: {
  label: string;
  title: string;
  onClick: () => void;
  disabled?: boolean;
  danger?: boolean;
  pressed?: boolean;
  children: ReactNode;
}) {
  return (
    <button
      type="button"
      aria-label={p.label}
      title={p.title}
      aria-pressed={p.pressed}
      disabled={p.disabled}
      onClick={p.onClick}
      className={cn(
        'shrink-0 rounded p-1 hover:bg-surface-2 disabled:opacity-40',
        p.danger ? 'text-danger' : 'text-muted',
      )}
    >
      {p.children}
    </button>
  );
}

/**
 * A persistent list of placed components: select, frame, copy / paste / duplicate from a focused row, and Ctrl- or
 * Shift-click ticks for Combine. Selection is independent of edit permissions.
 */
export function ComponentOutliner(p: ComponentOutlinerProps) {
  const items = p.items;
  const selected = items.find((m) => m.id === p.selectedId);
  const selectedId = selected?.id;
  const list = useRef<HTMLDivElement>(null);
  const rows = useRef(new Map<string, HTMLButtonElement>());
  // The page keeps the checklist when it is given (Ctrl+G combines what is ticked); alone, the outliner keeps its own.
  const [own, setOwn] = useState<string[] | null>(null);
  const checklist = p.onCheck ? (p.checked ?? null) : own;
  const setChecklist = p.onCheck ?? setOwn;
  const combining = checklist !== null,
    checked = checklist ?? [];
  const combineIds = checked.filter((id) => items.some((item) => item.id === id));
  const label = (item: OutlinerItem, index: number) => `${item.name} ${index + 1}`;
  /** Where a Shift-click range starts: the row clicked last, else the selected one. */
  const rangeFrom = useRef<string | null>(null);
  /** Ctrl-click ticks or unticks a row for Combine, Shift-click ticks the rows from the last one clicked; a plain click selects. */
  const pick = (event: MouseEvent<HTMLButtonElement>, index: number) => {
    const item = items[index];
    if (!item) return;
    const toggle = event.ctrlKey || event.metaKey;
    if ((toggle || event.shiftKey) && p.onCombine && !p.disabled && items.length >= 2) {
      event.preventDefault();
      // The checklist opens with the selected row ticked, as the Combine button opens it.
      const base = combining ? checked : selectedId ? [selectedId] : [];
      let next: string[];
      if (event.shiftKey) {
        const at = items.findIndex((m) => m.id === (rangeFrom.current ?? selectedId));
        const from = at < 0 ? index : at;
        const range = items.slice(Math.min(from, index), Math.max(from, index) + 1).map((m) => m.id);
        next = [...new Set([...base, ...range])];
      } else next = base.includes(item.id) ? base.filter((id) => id !== item.id) : [...base, item.id];
      rangeFrom.current = item.id;
      setChecklist(next);
      return;
    }
    rangeFrom.current = item.id;
    p.onSelect?.(item.id);
  };
  useEffect(() => {
    const row = selectedId && rows.current.get(selectedId),
      viewport = list.current;
    if (!row || !viewport) return;
    // Scroll only this list, never the full sidebar or page when a canvas pick changes selection.
    const top = row.offsetTop,
      bottom = top + row.offsetHeight;
    if (top < viewport.scrollTop) viewport.scrollTop = top;
    else if (bottom > viewport.scrollTop + viewport.clientHeight) viewport.scrollTop = bottom - viewport.clientHeight;
  }, [selectedId]);

  const key = (event: KeyboardEvent<HTMLButtonElement>, index: number) => {
    const item = items[index];
    if ((event.ctrlKey || event.metaKey) && !event.altKey && item) {
      const name = event.key.toLowerCase();
      if (name === 'c' || name === 'v' || name === 'd') {
        event.preventDefault();
        event.stopPropagation();
        if (name === 'c') p.onCopy?.(item.id);
        else if (!p.disabled && name === 'd') p.onDuplicate?.(item.id);
        else if (!p.disabled && p.canPaste) p.onPaste?.();
        return;
      }
    }
    if (
      !event.ctrlKey &&
      !event.metaKey &&
      !event.altKey &&
      (event.code === 'KeyF' || event.code === 'NumpadDecimal')
    ) {
      event.preventDefault();
      event.stopPropagation();
      if (item) {
        p.onSelect?.(item.id);
        p.onFrame?.(item.id);
      }
      return;
    }
    let next = index;
    if (event.key === 'ArrowDown') next = Math.min(items.length - 1, index + 1);
    else if (event.key === 'ArrowUp') next = Math.max(0, index - 1);
    else if (event.key === 'Home') next = 0;
    else if (event.key === 'End') next = items.length - 1;
    else if (event.key !== 'Enter') return;
    event.preventDefault();
    event.stopPropagation();
    const nextItem = items[next];
    if (nextItem) {
      p.onSelect?.(nextItem.id);
      rows.current.get(nextItem.id)?.focus();
    }
  };

  return (
    <section
      aria-label="Components"
      className="shrink-0 border-b border-border bg-surface p-2.5"
      data-testid={p.labels.testId}
    >
      <div className="mb-2 flex items-center gap-1.5">
        <Layers3 className="size-3.5 text-muted" />
        <h3 className="flex-1 text-xs font-semibold">
          Components <span className="ml-1 font-mono text-[10px] font-normal text-subtle">{items.length}</span>
        </h3>
        <button
          type="button"
          disabled={p.disabled || items.length < 2 || !p.onCombine}
          aria-label="Combine components"
          title="Choose components to combine"
          onClick={() => setChecklist(combining ? null : selected ? [selected.id] : [])}
          className="rounded border border-border p-1 text-muted hover:bg-surface-2 hover:text-fg disabled:opacity-40"
        >
          <Group className="size-3.5" />
        </button>
        <button
          type="button"
          disabled={!selected || !p.onFrame}
          aria-label="Frame selected component"
          title="Frame selected component (F / Numpad .)"
          onClick={() => {
            if (selected) p.onFrame?.(selected.id);
          }}
          className="rounded border border-border p-1 text-muted hover:bg-surface-2 hover:text-fg disabled:opacity-40"
        >
          <Focus className="size-3.5" />
        </button>
      </div>
      {items.length ? (
        <>
          <div
            ref={list}
            role="listbox"
            aria-label={p.labels.list}
            className="relative max-h-48 space-y-0.5 overflow-y-auto rounded border border-border bg-surface-2/40 p-1"
          >
            {items.map((item, index) => (
              <div key={item.id} className="flex items-center gap-1">
                <button
                  type="button"
                  role="option"
                  aria-selected={selected?.id === item.id}
                  aria-label={label(item, index)}
                  title={`${item.title ?? item.name}. Double-click to frame.`}
                  tabIndex={selected ? (selected.id === item.id ? 0 : -1) : index === 0 ? 0 : -1}
                  ref={(node) => {
                    if (node) rows.current.set(item.id, node);
                    else rows.current.delete(item.id);
                  }}
                  onClick={(event) => pick(event, index)}
                  onDoubleClick={() => {
                    p.onSelect?.(item.id);
                    p.onFrame?.(item.id);
                  }}
                  onKeyDown={(event) => key(event, index)}
                  className={cn(
                    'flex min-w-0 flex-1 items-center gap-1.5 rounded px-1.5 py-1.5 text-left focus-visible:outline focus-visible:outline-1 focus-visible:outline-accent',
                    selected?.id === item.id ? 'bg-accent/20 text-fg' : 'text-muted hover:bg-surface-2',
                    item.dim && 'opacity-50',
                    combining && checked.includes(item.id) && 'ring-1 ring-accent/60',
                  )}
                >
                  {item.glyph}
                  <span className="min-w-0 flex-1">
                    <span className="block truncate text-[11px]">
                      {item.name} <span className="text-subtle">{index + 1}</span>
                    </span>
                    {item.detail !== undefined ? (
                      <span className="block font-mono text-[9px] text-subtle">{item.detail}</span>
                    ) : null}
                  </span>
                </button>
                {p.rowActions?.(item, label(item, index))}
              </div>
            ))}
          </div>
          {p.hint ? <p className="mt-1.5 text-[10px] text-subtle">{p.hint}</p> : null}
        </>
      ) : p.empty ? (
        <div className="rounded border border-dashed border-border px-2 py-3 text-[10px] text-subtle">{p.empty}</div>
      ) : null}
      {combining ? (
        <section aria-label={p.labels.combine} className="mt-2 rounded border border-accent/40 bg-surface-2 p-2">
          {p.combineHint ? <p className="mb-1.5 text-[10px] text-muted">{p.combineHint}</p> : null}
          <div className="max-h-32 space-y-1 overflow-y-auto">
            {items.map((item, index) => (
              <label key={item.id} className="flex items-center gap-1.5 text-[11px]">
                <input
                  type="checkbox"
                  aria-label={`Combine ${label(item, index)}`}
                  checked={checked.includes(item.id)}
                  disabled={p.disabled}
                  onChange={(event) =>
                    setChecklist(event.target.checked ? [...checked, item.id] : checked.filter((id) => id !== item.id))
                  }
                />
                {item.name} {index + 1}
              </label>
            ))}
          </div>
          <div className="mt-2 flex gap-1.5">
            <button
              type="button"
              disabled={p.disabled || combineIds.length < 2}
              onClick={() => {
                p.onCombine?.(combineIds);
                setChecklist(null);
              }}
              className="flex-1 rounded border border-accent/50 bg-accent/15 py-1 text-[11px] disabled:opacity-40"
            >
              Combine {combineIds.length} selected
            </button>
            <button
              type="button"
              onClick={() => setChecklist(null)}
              className="rounded border border-border px-2 text-[11px]"
            >
              Cancel
            </button>
          </div>
        </section>
      ) : null}
    </section>
  );
}
