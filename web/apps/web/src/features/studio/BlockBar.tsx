'use client';

/**
 * The creative hotbar (1–9, the scroll of the selected slot) and the block palette that fills it (B): every block of the
 * session's version, searched by name, spec or id, by category, with recent and favourite rows. Icons come from one sheet
 * the world viewer draws on a 2D canvas (`blockIconAtlas`): one image for ~400 entries, CSS positions, no WebGL per entry.
 */
import { Search, Star, X } from 'lucide-react';
import { useMemo, useState, type CSSProperties } from 'react';
import { cn } from '@/lib/utils';
import {
  categoryCounts,
  entriesOf,
  entryKey,
  favouriteKeys,
  inCategory,
  type PaletteEntry,
  recentKeys,
  sameEntry,
  searchPalette,
  toggleFavourite,
} from './palette';

export type IconOf = (id: number, meta: number, icon?: string | null) => string | null;

/** A palette drawn into one sheet (the world viewer's `blockIconAtlas`): "id:meta" → slot. */
export interface IconSheet {
  url: string;
  size: number;
  cols: number;
  rows: number;
  index: Map<string, number>;
}

function Icon({ entry, icon, size = 32 }: { entry: PaletteEntry; icon: IconOf; size?: number }) {
  const url = icon(entry.id, entry.meta, entry.icon);
  if (!url) return <span className="block size-6 rounded bg-surface-3" />;
  // eslint-disable-next-line @next/next/no-img-element -- generated data URLs from the bundled textures
  return <img src={url} alt="" width={size} height={size} style={{ imageRendering: 'pixelated' }} draggable={false} />;
}

/** One entry's icon cut from the sheet (a background position), or a placeholder while the sheet draws. */
function SheetIcon({ entry, sheet, size = 32 }: { entry: PaletteEntry; sheet: IconSheet | null; size?: number }) {
  const i = sheet?.index.get(entryKey(entry));
  if (!sheet || i === undefined) return <span className="block size-6 rounded bg-surface-3" />;
  const k = size / sheet.size;
  const style: CSSProperties = {
    width: size,
    height: size,
    backgroundImage: `url(${sheet.url})`,
    backgroundSize: `${sheet.cols * sheet.size * k}px ${sheet.rows * sheet.size * k}px`,
    backgroundPosition: `-${(i % sheet.cols) * size}px -${Math.floor(i / sheet.cols) * size}px`,
    imageRendering: 'pixelated',
  };
  return <span aria-hidden className="block" style={style} />;
}

export function Hotbar({
  slots,
  active,
  onPick,
  icon,
}: {
  slots: Array<PaletteEntry | null>;
  active: number;
  onPick: (i: number) => void;
  icon: IconOf;
}) {
  const cur = slots[active];
  return (
    <div
      className="pointer-events-auto absolute bottom-3 left-1/2 flex -translate-x-1/2 flex-col items-center gap-1"
      data-testid="hotbar"
    >
      {cur ? (
        <span className="rounded bg-black/60 px-2 py-0.5 font-mono text-[11px] text-white">{cur.name}</span>
      ) : null}
      <div className="flex gap-0.5 rounded bg-black/55 p-0.5">
        {slots.map((s, i) => (
          <button
            key={i}
            type="button"
            onClick={() => onPick(i)}
            aria-label={`Slot ${i + 1}${s ? `: ${s.name}` : ''}`}
            aria-pressed={i === active}
            className={cn(
              'relative grid size-11 place-items-center border-2',
              i === active ? 'border-white' : 'border-white/15 hover:border-white/40',
            )}
          >
            {s ? <Icon entry={s} icon={icon} /> : null}
            <span className="absolute top-0 left-0.5 font-mono text-[9px] text-white/70">{i + 1}</span>
          </button>
        ))}
      </div>
    </div>
  );
}

function Cell({
  e,
  current,
  sheet,
  fav,
  onChoose,
  onStar,
}: {
  e: PaletteEntry;
  current: PaletteEntry | null;
  sheet: IconSheet | null;
  fav: boolean;
  onChoose: (e: PaletteEntry) => void;
  onStar: (e: PaletteEntry) => void;
}) {
  return (
    <li className="group relative">
      <button
        type="button"
        title={`${e.name} — ${e.spec ?? e.id} (${e.id}${e.meta ? `:${e.meta}` : ''})`}
        aria-label={e.name}
        data-spec={e.spec ?? undefined}
        onClick={() => onChoose(e)}
        onContextMenu={(ev) => {
          ev.preventDefault();
          onStar(e);
        }}
        className={cn(
          'grid size-11 place-items-center rounded border',
          sameEntry(e, current)
            ? 'border-accent bg-accent-soft'
            : 'border-transparent hover:border-border hover:bg-surface-2',
        )}
      >
        <SheetIcon entry={e} sheet={sheet} />
      </button>
      <button
        type="button"
        aria-label={fav ? `Unstar ${e.name}` : `Star ${e.name}`}
        aria-pressed={fav}
        onClick={() => onStar(e)}
        className={cn(
          'absolute top-0 right-0 hidden rounded-sm p-px text-amber-400 group-hover:block',
          fav && 'block',
        )}
      >
        <Star className="size-2.5" fill={fav ? 'currentColor' : 'none'} />
      </button>
    </li>
  );
}

export function PaletteDrawer({
  palette,
  version,
  current,
  onChoose,
  onClose,
  sheet,
}: {
  palette: readonly PaletteEntry[];
  version: string;
  current: PaletteEntry | null;
  onChoose: (e: PaletteEntry) => void;
  onClose: () => void;
  sheet: IconSheet | null;
}) {
  const [q, setQ] = useState('');
  const [cat, setCat] = useState<string | null>(null);
  const [favs, setFavs] = useState<string[]>(() => favouriteKeys(version));
  const recent = useMemo(() => entriesOf(palette, recentKeys(version)), [palette, version]);
  const favourites = useMemo(() => entriesOf(palette, favs), [palette, favs]);
  const counts = useMemo(() => categoryCounts(palette), [palette]);
  const shown = useMemo(() => searchPalette(inCategory(palette, q ? null : cat), q), [palette, q, cat]);
  const star = (e: PaletteEntry) => setFavs(toggleFavourite(version, e));
  const cell = (e: PaletteEntry, row = '') => (
    <Cell
      key={`${row}${entryKey(e)}`}
      e={e}
      current={current}
      sheet={sheet}
      fav={favs.includes(entryKey(e))}
      onChoose={onChoose}
      onStar={star}
    />
  );
  return (
    <div
      className="pointer-events-auto absolute top-12 left-1/2 z-10 flex max-h-[76%] w-[min(640px,94%)] -translate-x-1/2 flex-col rounded-lg border border-border bg-surface/95 shadow-card"
      role="dialog"
      aria-label="Block palette"
      onKeyDown={(e) => {
        e.stopPropagation();
        if (e.key === 'Escape' || (e.key === 'Enter' && shown[0])) {
          if (e.key === 'Enter') onChoose(shown[0]!);
          onClose();
        }
      }}
    >
      <div className="flex items-center gap-2 border-b border-border p-2">
        <Search className="size-4 text-subtle" />
        <input
          autoFocus
          value={q}
          onChange={(e) => setQ(e.target.value)}
          placeholder="Search name, spec or id (Enter takes the first)…"
          aria-label="Search blocks"
          className="min-w-0 flex-1 bg-transparent text-sm text-fg outline-none"
        />
        <span className="text-xs text-subtle" data-testid="palette-count">
          {shown.length}
        </span>
        <span className="rounded bg-surface-2 px-1.5 py-0.5 font-mono text-[10px] text-muted">{version}</span>
        <button type="button" onClick={onClose} aria-label="Close the palette" className="text-muted hover:text-fg">
          <X className="size-4" />
        </button>
      </div>
      <div className="flex flex-wrap gap-1 border-b border-border px-2 py-1.5" role="tablist" aria-label="Categories">
        {[['All', palette.length] as [string, number], ...counts].map(([c, n]) => {
          const on = (c === 'All' ? null : c) === cat && !q;
          return (
            <button
              key={c}
              type="button"
              role="tab"
              aria-selected={on}
              onClick={() => {
                setCat(c === 'All' ? null : c);
                setQ('');
              }}
              className={cn(
                'rounded px-2 py-0.5 text-xs',
                on ? 'bg-accent-soft text-fg' : 'text-muted hover:bg-surface-2 hover:text-fg',
              )}
            >
              {c} <span className="text-subtle">{n}</span>
            </button>
          );
        })}
      </div>
      {!q && (favourites.length > 0 || recent.length > 0) ? (
        <div className="flex flex-col gap-1 border-b border-border px-2 py-1.5">
          {favourites.length > 0 ? (
            <div className="flex items-center gap-2">
              <span className="w-16 shrink-0 text-[11px] text-subtle">Favourites</span>
              <ul className="flex flex-wrap gap-1" aria-label="Favourite blocks">
                {favourites.map((e) => cell(e, 'f'))}
              </ul>
            </div>
          ) : null}
          {recent.length > 0 ? (
            <div className="flex items-center gap-2">
              <span className="w-16 shrink-0 text-[11px] text-subtle">Recent</span>
              <ul className="flex flex-wrap gap-1" aria-label="Recent blocks">
                {recent.map((e) => cell(e, 'r'))}
              </ul>
            </div>
          ) : null}
        </div>
      ) : null}
      <ul className="grid grid-cols-[repeat(auto-fill,minmax(44px,1fr))] gap-1 overflow-y-auto p-2" aria-label="Blocks">
        {shown.map((e) => cell(e))}
      </ul>
      {shown.length === 0 ? <p className="px-3 pb-3 text-xs text-muted">No {version} block matches “{q}”.</p> : null}
    </div>
  );
}
