'use client';

import { CopyPlus, Eye, EyeOff, Focus, Group, Layers3, Trash2 } from 'lucide-react';
import { useEffect, useRef, useState, type KeyboardEvent, type MouseEvent } from 'react';
import { cn } from '@/lib/utils';
import { ModuleGlyph, moduleLabel, type ModulePaletteProps } from './ModulePalette';

/** A persistent outliner: component selection is independent of terrain visibility and edit permissions. */
export function MineComponents(p: Pick<ModulePaletteProps, 'modules' | 'selectedModule' | 'onSelectModule' | 'onFrameModule' | 'onCopyModule' | 'onPasteModule' | 'onDuplicateModule' | 'onRemoveModule' | 'onCombineModules' | 'checkedModules' | 'onCheckModules' | 'canPasteModule' | 'onVisibilityModule'> & { disabled?: boolean }) {
  const modules = p.modules ?? [];
  const selected = modules.find((m) => m.id === p.selectedModule);
  const selectedId = selected?.id;
  const list = useRef<HTMLDivElement>(null);
  const rows = useRef(new Map<string, HTMLButtonElement>());
  // The page keeps the checklist when it is given (Ctrl+G combines what is ticked); alone, the outliner keeps its own.
  const [own, setOwn] = useState<string[] | null>(null);
  const checklist = p.onCheckModules ? p.checkedModules ?? null : own;
  const setChecklist = p.onCheckModules ?? setOwn;
  const combining = checklist !== null, checked = checklist ?? [];
  const combineIds = checked.filter((id) => modules.some((item) => item.id === id));
  /** Where a Shift-click range starts: the row clicked last, else the selected one. */
  const rangeFrom = useRef<string | null>(null);
  /** Ctrl-click ticks or unticks a row for Combine, Shift-click ticks the rows from the last one clicked; a plain click selects. */
  const pick = (event: MouseEvent<HTMLButtonElement>, index: number) => {
    const item = modules[index];
    if (!item) return;
    const toggle = event.ctrlKey || event.metaKey;
    if ((toggle || event.shiftKey) && p.onCombineModules && !p.disabled && modules.length >= 2) {
      event.preventDefault();
      // The checklist opens with the selected piece ticked, as the Combine button opens it.
      const base = combining ? checked : selectedId ? [selectedId] : [];
      let next: string[];
      if (event.shiftKey) {
        const at = modules.findIndex((m) => m.id === (rangeFrom.current ?? selectedId));
        const from = at < 0 ? index : at;
        const range = modules.slice(Math.min(from, index), Math.max(from, index) + 1).map((m) => m.id);
        next = [...new Set([...base, ...range])];
      } else next = base.includes(item.id) ? base.filter((id) => id !== item.id) : [...base, item.id];
      rangeFrom.current = item.id;
      setChecklist(next);
      return;
    }
    rangeFrom.current = item.id;
    p.onSelectModule?.(item.id);
  };
  useEffect(() => {
    const row = selectedId && rows.current.get(selectedId), viewport = list.current;
    if (!row || !viewport) return;
    // Scroll only this list, never the full sidebar or page when a canvas pick changes selection.
    const top = row.offsetTop, bottom = top + row.offsetHeight;
    if (top < viewport.scrollTop) viewport.scrollTop = top;
    else if (bottom > viewport.scrollTop + viewport.clientHeight) viewport.scrollTop = bottom - viewport.clientHeight;
  }, [selectedId]);

  const key = (event: KeyboardEvent<HTMLButtonElement>, index: number) => {
    const item = modules[index];
    if ((event.ctrlKey || event.metaKey) && !event.altKey && item) {
      const name = event.key.toLowerCase();
      if (name === 'c' || name === 'v' || name === 'd') {
        event.preventDefault(); event.stopPropagation();
        if (name === 'c') p.onCopyModule?.(item.id);
        else if (!p.disabled && name === 'd') p.onDuplicateModule?.(item.id);
        else if (!p.disabled && p.canPasteModule) p.onPasteModule?.();
        return;
      }
    }
    if (!event.ctrlKey && !event.metaKey && !event.altKey && (event.code === 'KeyF' || event.code === 'NumpadDecimal')) {
      event.preventDefault();
      event.stopPropagation();
      if (item) { p.onSelectModule?.(item.id); p.onFrameModule?.(item.id); }
      return;
    }
    let next = index;
    if (event.key === 'ArrowDown') next = Math.min(modules.length - 1, index + 1);
    else if (event.key === 'ArrowUp') next = Math.max(0, index - 1);
    else if (event.key === 'Home') next = 0;
    else if (event.key === 'End') next = modules.length - 1;
    else if (event.key !== 'Enter') return;
    event.preventDefault();
    event.stopPropagation();
    const nextItem = modules[next];
    if (nextItem) { p.onSelectModule?.(nextItem.id); rows.current.get(nextItem.id)?.focus(); }
  };

  return <section aria-label="Components" className="shrink-0 border-b border-border bg-surface p-2.5" data-testid="mine-components">
    <div className="mb-2 flex items-center gap-1.5">
      <Layers3 className="size-3.5 text-muted" />
      <h3 className="flex-1 text-xs font-semibold">Components <span className="ml-1 font-mono text-[10px] font-normal text-subtle">{modules.length}</span></h3>
      <button type="button" disabled={p.disabled || modules.length < 2 || !p.onCombineModules} aria-label="Combine components" title="Choose components to combine"
        onClick={() => setChecklist(combining ? null : selected ? [selected.id] : [])} className="rounded border border-border p-1 text-muted hover:bg-surface-2 hover:text-fg disabled:opacity-40"><Group className="size-3.5" /></button>
      <button type="button" disabled={!selected || !p.onFrameModule} aria-label="Frame selected component" title="Frame selected component (F / Numpad .)"
        onClick={() => { if (selected) p.onFrameModule?.(selected.id); }} className="rounded border border-border p-1 text-muted hover:bg-surface-2 hover:text-fg disabled:opacity-40"><Focus className="size-3.5" /></button>
    </div>
    {modules.length ? <>
      <div ref={list} role="listbox" aria-label="Mine components" className="relative max-h-48 space-y-0.5 overflow-y-auto rounded border border-border bg-surface-2/40 p-1">
        {modules.map((item, index) => <div key={item.id} className="flex items-center gap-1"><button type="button" role="option" aria-selected={selected?.id === item.id}
          aria-label={`${moduleLabel(item.spec)} ${index + 1}`} title={`${moduleLabel(item.spec)} · ${item.at.join(', ')}. Double-click to frame.`}
          tabIndex={selected ? (selected.id === item.id ? 0 : -1) : index === 0 ? 0 : -1}
          ref={(node) => { if (node) rows.current.set(item.id, node); else rows.current.delete(item.id); }}
          onClick={(event) => pick(event, index)} onDoubleClick={() => { p.onSelectModule?.(item.id); p.onFrameModule?.(item.id); }} onKeyDown={(event) => key(event, index)}
          className={cn('flex min-w-0 flex-1 items-center gap-1.5 rounded px-1.5 py-1.5 text-left focus-visible:outline focus-visible:outline-1 focus-visible:outline-accent', selected?.id === item.id ? 'bg-accent/20 text-fg' : 'text-muted hover:bg-surface-2', item.visible === false && 'opacity-50', combining && checked.includes(item.id) && 'ring-1 ring-accent/60')}>
          <ModuleGlyph kind={item.spec.kind} compact />
          <span className="min-w-0 flex-1"><span className="block truncate text-[11px]">{moduleLabel(item.spec)} <span className="text-subtle">{index + 1}</span></span>
            <span className="block font-mono text-[9px] text-subtle">{item.at[0]}, {item.at[1]}, {item.at[2]}</span></span>
        </button><button type="button" aria-label={`${item.visible === false ? 'Show' : 'Hide'} ${moduleLabel(item.spec)} ${index + 1}`} aria-pressed={item.visible !== false}
          title="Show or hide this component's wireframe. The saved excavation and mining tasks stay unchanged."
          onClick={() => p.onVisibilityModule?.(item.id, item.visible === false)} className="shrink-0 rounded p-1 text-muted hover:bg-surface-2">
          {item.visible === false ? <EyeOff className="size-3.5" /> : <Eye className="size-3.5" />}
        </button>
        <button type="button" aria-label={`Duplicate ${moduleLabel(item.spec)} ${index + 1}`} title="Duplicate: place a copy at the same height (Ctrl/Cmd+D)"
          disabled={p.disabled || !p.onDuplicateModule} onClick={() => p.onDuplicateModule?.(item.id)} className="shrink-0 rounded p-1 text-muted hover:bg-surface-2 disabled:opacity-40">
          <CopyPlus className="size-3.5" />
        </button>
        <button type="button" aria-label={`Delete ${moduleLabel(item.spec)} ${index + 1}`} title="Delete this component and the excavation only it plans (Ctrl+Z undoes)"
          disabled={p.disabled || !p.onRemoveModule} onClick={() => p.onRemoveModule?.(item.id)} className="shrink-0 rounded p-1 text-danger hover:bg-surface-2 disabled:opacity-40">
          <Trash2 className="size-3.5" />
        </button></div>)}
      </div>
      <p className="mt-1.5 text-[10px] text-subtle">Select here or in the mine. Double-click to frame. Ctrl- or Shift-click ticks pieces to combine.</p>
    </> : <div className="rounded border border-dashed border-border px-2 py-3 text-[10px] text-subtle">
      <p>Add a layout below to create a movable component.</p>
      <p className="mt-1">Manual digs and older presets remain editable cells, without component groups.</p>
    </div>}
    {combining ? <section aria-label="Combine mine components" className="mt-2 rounded border border-accent/40 bg-surface-2 p-2">
      <p className="mb-1.5 text-[10px] text-muted">Choose pieces to move, rotate and copy as one custom component (Ctrl+G).</p>
      <div className="max-h-32 space-y-1 overflow-y-auto">{modules.map((item, index) => <label key={item.id} className="flex items-center gap-1.5 text-[11px]">
        <input type="checkbox" aria-label={`Combine ${moduleLabel(item.spec)} ${index + 1}`} checked={checked.includes(item.id)} disabled={p.disabled}
          onChange={(event) => setChecklist(event.target.checked ? [...checked, item.id] : checked.filter((id) => id !== item.id))} />{moduleLabel(item.spec)} {index + 1}
      </label>)}</div>
      <div className="mt-2 flex gap-1.5"><button type="button" disabled={p.disabled || combineIds.length < 2} onClick={() => { p.onCombineModules?.(combineIds); setChecklist(null); }} className="flex-1 rounded border border-accent/50 bg-accent/15 py-1 text-[11px] disabled:opacity-40">Combine {combineIds.length} selected</button>
        <button type="button" onClick={() => setChecklist(null)} className="rounded border border-border px-2 text-[11px]">Cancel</button></div>
    </section> : null}
  </section>;
}
