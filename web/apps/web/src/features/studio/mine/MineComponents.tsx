'use client';

import { Focus, Layers3 } from 'lucide-react';
import { useEffect, useRef, type KeyboardEvent } from 'react';
import { cn } from '@/lib/utils';
import { ModuleGlyph, type ModulePaletteProps } from './ModulePalette';
import { moduleName } from './modules';

/** A persistent outliner: component selection is independent of terrain visibility and edit permissions. */
export function MineComponents(p: Pick<ModulePaletteProps, 'modules' | 'selectedModule' | 'onSelectModule' | 'onFrameModule'>) {
  const modules = p.modules ?? [];
  const selected = modules.find((m) => m.id === p.selectedModule);
  const selectedId = selected?.id;
  const list = useRef<HTMLDivElement>(null);
  const rows = useRef(new Map<string, HTMLButtonElement>());
  useEffect(() => {
    const row = selectedId && rows.current.get(selectedId), viewport = list.current;
    if (!row || !viewport) return;
    // Scroll only this list, never the full sidebar or page when a canvas pick changes selection.
    const top = row.offsetTop, bottom = top + row.offsetHeight;
    if (top < viewport.scrollTop) viewport.scrollTop = top;
    else if (bottom > viewport.scrollTop + viewport.clientHeight) viewport.scrollTop = bottom - viewport.clientHeight;
  }, [selectedId]);

  const key = (event: KeyboardEvent<HTMLButtonElement>, index: number) => {
    if (!event.ctrlKey && !event.metaKey && !event.altKey && (event.code === 'KeyF' || event.code === 'NumpadDecimal')) {
      event.preventDefault();
      event.stopPropagation();
      const item = modules[index];
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
    const item = modules[next];
    if (item) { p.onSelectModule?.(item.id); rows.current.get(item.id)?.focus(); }
  };

  return <section aria-label="Components" className="shrink-0 border-b border-border bg-surface p-2.5" data-testid="mine-components">
    <div className="mb-2 flex items-center gap-1.5">
      <Layers3 className="size-3.5 text-muted" />
      <h3 className="flex-1 text-xs font-semibold">Components <span className="ml-1 font-mono text-[10px] font-normal text-subtle">{modules.length}</span></h3>
      <button type="button" disabled={!selected || !p.onFrameModule} aria-label="Frame selected component" title="Frame selected component (F / Numpad .)"
        onClick={() => { if (selected) p.onFrameModule?.(selected.id); }} className="rounded border border-border p-1 text-muted hover:bg-surface-2 hover:text-fg disabled:opacity-40"><Focus className="size-3.5" /></button>
    </div>
    {modules.length ? <>
      <div ref={list} role="listbox" aria-label="Mine components" className="relative max-h-48 space-y-0.5 overflow-y-auto rounded border border-border bg-surface-2/40 p-1">
        {modules.map((item, index) => <button key={item.id} type="button" role="option" aria-selected={selected?.id === item.id}
          aria-label={`${moduleName(item.spec.kind)} ${index + 1}`} title={`${moduleName(item.spec.kind)} · ${item.at.join(', ')}. Double-click to frame.`}
          tabIndex={selected ? (selected.id === item.id ? 0 : -1) : index === 0 ? 0 : -1}
          ref={(node) => { if (node) rows.current.set(item.id, node); else rows.current.delete(item.id); }}
          onClick={() => p.onSelectModule?.(item.id)} onDoubleClick={() => { p.onSelectModule?.(item.id); p.onFrameModule?.(item.id); }} onKeyDown={(event) => key(event, index)}
          className={cn('flex w-full items-center gap-1.5 rounded px-1.5 py-1.5 text-left focus-visible:outline focus-visible:outline-1 focus-visible:outline-accent', selected?.id === item.id ? 'bg-accent/20 text-fg' : 'text-muted hover:bg-surface-2')}>
          <ModuleGlyph kind={item.spec.kind} compact />
          <span className="min-w-0 flex-1"><span className="block truncate text-[11px]">{moduleName(item.spec.kind)} <span className="text-subtle">{index + 1}</span></span>
            <span className="block font-mono text-[9px] text-subtle">{item.at[0]}, {item.at[1]}, {item.at[2]}</span></span>
        </button>)}
      </div>
      <p className="mt-1.5 text-[10px] text-subtle">Select here or in the mine. Double-click to frame.</p>
    </> : <div className="rounded border border-dashed border-border px-2 py-3 text-[10px] text-subtle">
      <p>Add a layout below to create a movable component.</p>
      <p className="mt-1">Manual digs and older presets remain editable cells, without component groups.</p>
    </div>}
  </section>;
}
