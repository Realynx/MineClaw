'use client';

/**
 * The universal material selector: one bar docked bottom-left, just above the tab bar, whenever a structure or furniture tool
 * is active (never a popup over the map). It holds the tool's name, its materials (the Architect's palette for the colony's
 * version: icon, name, tier, lock state with the reason, stock), a search box and the recent row, and the tool's own options
 * (wall height, roof style and overhang, facing) as small segmented controls to the right: one place for everything the
 * placement takes. Hovering a material previews it on the map at once; Tab / Shift+Tab or ] / [ cycle, 1–5 pick a recent one
 * (StoryMode's keys). Narrow maps get a single material chip that opens the same bar.
 */
import { ChevronUp, Lock, RotateCcw, RotateCw, Search, X } from 'lucide-react';
import { useEffect, useRef, useState } from 'react';
import { cn } from '@/lib/utils';
import { stockText, type MaterialOption } from './materials';
import { panel } from './StoryHud';
import { FURNITURE_TOOLS, rotateFacing, STRUCTURE_TOOLS, type BuildOpts, type BuildToolId } from './tools';
import { ItemIcon, ToolIcon } from './visuals';

const TIER_LABEL: Record<string, string> = {
  gathered: 'Gathered',
  hand: 'Hand',
  workbench: 'Workbench',
  furnace: 'Furnace',
};

const ROOF_STYLES: Array<[BuildOpts['roof'], string]> = [
  ['flat', 'Flat'],
  ['slab', 'Slab'],
  ['gable', 'Gable'],
  ['none', 'None'],
];

/** Below this width (the Story area's) the bar folds into one material chip. */
export const NARROW_PX = 760;

export function MaterialBar({
  toolId,
  options,
  current,
  recent,
  query,
  onQuery,
  onPick,
  onHover,
  build,
  onBuild,
  onClose,
}: {
  toolId: BuildToolId;
  /** The tool's materials (null: it takes none, as furniture). */
  options: MaterialOption[] | null;
  /** The chosen material's key. */
  current: string | null;
  recent: MaterialOption[];
  query: string;
  onQuery: (q: string) => void;
  onPick: (o: MaterialOption) => void;
  onHover: (o: MaterialOption | null) => void;
  build: BuildOpts;
  onBuild: (b: Partial<BuildOpts>) => void;
  onClose: () => void;
}) {
  const host = useRef<HTMLDivElement | null>(null);
  const list = useRef<HTMLDivElement | null>(null);
  const [narrow, setNarrow] = useState(false);
  const [open, setOpen] = useState(false);
  const furniture = toolId.startsWith('furniture:');
  const spec = furniture ? null : STRUCTURE_TOOLS.find((s) => s.id === toolId);
  const name = furniture
    ? (FURNITURE_TOOLS.find((f) => `furniture:${f.id}` === toolId)?.label ?? 'Furniture')
    : spec?.label;
  const hint = furniture ? 'Click to place; Q/E turns' : (spec?.hint ?? '');
  const chosen = options?.find((o) => o.key === current) ?? null;

  // Narrow when the Story area is: the chip instead of the bar.
  useEffect(() => {
    const root = host.current?.closest('[data-testid="story-mode"]') as HTMLElement | null;
    if (!root || typeof ResizeObserver === 'undefined') return;
    const ro = new ResizeObserver(() => setNarrow(root.clientWidth < NARROW_PX));
    ro.observe(root);
    setNarrow(root.clientWidth < NARROW_PX);
    return () => ro.disconnect();
  }, []);

  // The chosen material stays in view in the scrolled list (keys cycle through it).
  useEffect(() => {
    const el = list.current?.querySelector<HTMLElement>(`[data-material="${current}"]`);
    el?.scrollIntoView?.({ block: 'nearest', inline: 'nearest' });
  }, [current]);

  const chip = (o: MaterialOption, n?: number) => (
    <button
      key={`${n ?? 'm'}-${o.key}`}
      type="button"
      data-material={n === undefined ? o.key : undefined}
      data-recent={n !== undefined ? o.key : undefined}
      data-unlocked={o.unlocked ? 'true' : 'false'}
      aria-pressed={o.key === current}
      disabled={!o.unlocked}
      onClick={() => onPick(o)}
      onPointerEnter={() => o.unlocked && onHover(o)}
      onFocus={() => o.unlocked && onHover(o)}
      onBlur={() => onHover(null)}
      title={[
        o.label,
        o.tier ? (TIER_LABEL[o.tier] ?? o.tier) : null,
        stockText(o),
        n !== undefined ? `key ${n}` : null,
      ]
        .filter(Boolean)
        .join(' · ')}
      className={cn(
        'relative flex shrink-0 items-center gap-1.5 rounded-md border text-left',
        n !== undefined ? 'h-7 px-1' : 'h-9 max-w-40 px-1.5',
        o.key === current
          ? 'border-accent bg-accent-soft'
          : o.unlocked
            ? 'border-border bg-surface-2 hover:border-border-strong'
            : 'cursor-not-allowed border-dashed border-border bg-surface-1 opacity-50',
      )}
    >
      <ItemIcon id={o.item[0]} meta={o.item[1]} size={n !== undefined ? 18 : 20} />
      {n !== undefined ? (
        <span className="font-mono text-[9.5px] text-subtle">{n}</span>
      ) : (
        <span className="min-w-0">
          <span className="flex items-center gap-1 truncate text-[11px] font-semibold leading-tight">
            {o.unlocked ? null : <Lock className="size-3 shrink-0" aria-label="locked" />}
            <span className="truncate">{o.label}</span>
          </span>
          <span className="block truncate text-[10px] leading-tight text-muted">{stockText(o)}</span>
        </span>
      )}
    </button>
  );

  const seg = (on: boolean) =>
    cn('h-6 rounded px-1.5 text-[11px] font-medium', on ? 'bg-accent-soft text-fg' : 'text-muted hover:bg-surface-3');

  const toolOptions = (
    <>
      {toolId === 'wall' || toolId === 'roof' ? (
        <span className="flex items-center gap-1 whitespace-nowrap" title="Wall height (+ / −)">
          Height
          <button
            type="button"
            className="grid size-5 place-items-center rounded bg-surface-2 hover:bg-surface-3"
            aria-label="Lower"
            onClick={() => onBuild({ height: Math.max(1, build.height - 1) })}
          >
            −
          </button>
          <span className="w-4 text-center tabular-nums" data-testid="wall-height">
            {build.height}
          </span>
          <button
            type="button"
            className="grid size-5 place-items-center rounded bg-surface-2 hover:bg-surface-3"
            aria-label="Higher"
            onClick={() => onBuild({ height: Math.min(12, build.height + 1) })}
          >
            +
          </button>
        </span>
      ) : null}
      {toolId === 'roof' ? (
        <>
          <span className="flex items-center rounded bg-surface-2 p-0.5" role="radiogroup" aria-label="Roof style">
            {ROOF_STYLES.map(([id, label]) => (
              <button
                key={id}
                type="button"
                role="radio"
                aria-checked={build.roof === id}
                onClick={() => onBuild({ roof: id })}
                className={seg(build.roof === id)}
              >
                {label}
              </button>
            ))}
          </span>
          <button
            type="button"
            aria-pressed={build.overhang}
            onClick={() => onBuild({ overhang: !build.overhang })}
            className={cn(seg(build.overhang), 'bg-surface-2')}
            title="The roof reaches one block past the walls"
          >
            Overhang
          </button>
        </>
      ) : null}
      {toolId === 'door' || toolId === 'stairs' || furniture ? (
        <span className="flex items-center gap-0.5 rounded bg-surface-2 p-0.5" title="Facing (Q / E)">
          <button
            type="button"
            aria-label="Turn left (Q)"
            className="grid size-5 place-items-center rounded hover:bg-surface-3"
            onClick={() => onBuild({ facing: rotateFacing(build.facing, -1) })}
          >
            <RotateCcw className="size-3.5 text-muted" />
          </button>
          <span className="w-4 text-center font-semibold uppercase" data-testid="facing">
            {build.facing}
          </span>
          <button
            type="button"
            aria-label="Turn right (E)"
            className="grid size-5 place-items-center rounded hover:bg-surface-3"
            onClick={() => onBuild({ facing: rotateFacing(build.facing, 1) })}
          >
            <RotateCw className="size-3.5 text-muted" />
          </button>
        </span>
      ) : null}
    </>
  );

  const materials = options ? (
    <>
      <label className="flex h-7 shrink-0 items-center gap-1 rounded border border-border bg-surface-2 px-1.5">
        <Search className="size-3 text-muted" aria-hidden />
        <input
          value={query}
          onChange={(e) => onQuery(e.target.value)}
          onKeyDown={(e) => {
            if (e.key === 'Escape') {
              e.stopPropagation();
              onQuery('');
              (e.target as HTMLInputElement).blur();
            }
          }}
          placeholder="Material"
          aria-label="Search materials"
          className="w-20 bg-transparent text-[11px] outline-none placeholder:text-subtle"
        />
      </label>
      {recent.length ? (
        <span className="flex shrink-0 items-center gap-1" aria-label="Recent materials" data-testid="recent-materials">
          {recent.map((o, i) => chip(o, i + 1))}
        </span>
      ) : null}
      <div
        ref={list}
        className={cn('flex min-w-0 gap-1', narrow ? 'flex-wrap' : 'overflow-x-auto')}
        role="listbox"
        aria-label={`${name} materials`}
        data-testid="material-list"
        onPointerLeave={() => onHover(null)}
      >
        {options.length ? (
          options.map((o) => chip(o))
        ) : (
          <span className="self-center px-1 text-[11px] text-muted">No material matches</span>
        )}
      </div>
    </>
  ) : null;

  const body = (
    <>
      <span className="flex shrink-0 items-center gap-1.5" title={hint}>
        <ToolIcon id={toolId.replace('furniture:', '')} className="size-4 text-accent" />
        <span className="font-semibold whitespace-nowrap">{name}</span>
      </span>
      {materials ? <span className="h-6 w-px shrink-0 bg-border" aria-hidden /> : null}
      {materials}
      {options ? <span className="h-6 w-px shrink-0 bg-border" aria-hidden /> : null}
      <span className="flex shrink-0 items-center gap-1.5">{toolOptions}</span>
      {!options ? <span className="min-w-0 truncate text-muted">{hint}</span> : null}
      <button
        type="button"
        onClick={onClose}
        className="ml-auto grid size-5 shrink-0 place-items-center rounded hover:bg-surface-2"
        aria-label="Put the tool down (Esc or right-click)"
      >
        <X className="size-3.5" />
      </button>
    </>
  );

  return (
    <div ref={host} className="pointer-events-none relative max-w-full">
      {narrow && options ? (
        <>
          {open ? (
            <section
              className={cn(
                panel,
                'pointer-events-auto absolute bottom-full left-0 mb-1 flex w-[min(30rem,calc(100vw-2rem))] flex-wrap items-center gap-2 p-2 text-xs',
              )}
              role="toolbar"
              aria-label={`${name}: material and options`}
              data-testid="material-bar"
              data-docked="bottom-left"
            >
              {body}
            </section>
          ) : null}
          <button
            type="button"
            onClick={() => setOpen((o) => !o)}
            aria-expanded={open}
            className={cn(panel, 'pointer-events-auto flex h-9 items-center gap-1.5 px-2 text-xs')}
            data-testid="material-chip"
            title={`${name}: ${chosen?.label ?? 'choose a material'}`}
          >
            <ToolIcon id={toolId} className="size-4 text-accent" />
            {chosen ? <ItemIcon id={chosen.item[0]} meta={chosen.item[1]} size={18} /> : null}
            <span className="max-w-28 truncate font-semibold">{chosen?.label ?? name}</span>
            <ChevronUp className={cn('size-3.5 text-muted transition-transform', !open && 'rotate-180')} />
          </button>
        </>
      ) : (
        <section
          className={cn(panel, 'pointer-events-auto flex max-w-full items-center gap-2 px-2 py-1.5 text-xs')}
          role="toolbar"
          aria-label={`${name}: material and options`}
          data-testid="material-bar"
          data-docked="bottom-left"
        >
          {body}
        </section>
      )}
    </div>
  );
}
