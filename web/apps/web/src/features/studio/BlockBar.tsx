'use client';

/** The creative hotbar (1–9, the scroll of the selected slot) and the searchable block palette that fills it (B). */
import { Search, X } from 'lucide-react';
import { useMemo, useState } from 'react';
import { cn } from '@/lib/utils';
import { type PaletteEntry, sameEntry, searchPalette } from './palette';

export type IconOf = (id: number, meta: number) => string | null;

function Icon({ entry, icon, size = 32 }: { entry: PaletteEntry; icon: IconOf; size?: number }) {
  const url = icon(entry.id, entry.meta);
  // eslint-disable-next-line @next/next/no-img-element -- generated data URLs from the bundled textures
  return url ? (
    <img src={url} alt="" width={size} height={size} style={{ imageRendering: 'pixelated' }} draggable={false} />
  ) : (
    <span className="block size-6 rounded bg-surface-3" />
  );
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

export function PaletteDrawer({
  palette,
  current,
  onChoose,
  onClose,
  icon,
}: {
  palette: readonly PaletteEntry[];
  current: PaletteEntry | null;
  onChoose: (e: PaletteEntry) => void;
  onClose: () => void;
  icon: IconOf;
}) {
  const [q, setQ] = useState('');
  const shown = useMemo(() => searchPalette(palette, q), [palette, q]);
  return (
    <div
      className="pointer-events-auto absolute top-12 left-1/2 z-10 flex max-h-[70%] w-[min(560px,92%)] -translate-x-1/2 flex-col rounded-lg border border-border bg-surface/95 shadow-card"
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
          placeholder="Search blocks (Enter takes the first)…"
          aria-label="Search blocks"
          className="min-w-0 flex-1 bg-transparent text-sm text-fg outline-none"
        />
        <span className="text-xs text-subtle">{shown.length}</span>
        <button type="button" onClick={onClose} aria-label="Close the palette" className="text-muted hover:text-fg">
          <X className="size-4" />
        </button>
      </div>
      <ul className="grid grid-cols-[repeat(auto-fill,minmax(44px,1fr))] gap-1 overflow-y-auto p-2" aria-label="Blocks">
        {shown.map((e) => (
          <li key={`${e.id}:${e.meta}`}>
            <button
              type="button"
              title={`${e.name} (${e.id}${e.meta ? `:${e.meta}` : ''})`}
              aria-label={e.name}
              onClick={() => onChoose(e)}
              className={cn(
                'grid size-11 place-items-center rounded border',
                sameEntry(e, current)
                  ? 'border-accent bg-accent-soft'
                  : 'border-transparent hover:border-border hover:bg-surface-2',
              )}
            >
              <Icon entry={e} icon={icon} />
            </button>
          </li>
        ))}
      </ul>
    </div>
  );
}
