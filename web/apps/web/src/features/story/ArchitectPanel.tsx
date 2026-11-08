'use client';

import type { StoryArchitectEntry, StoryArchitectResponse, StoryPlant, StoryZone } from '@mineclaw/contracts';
import { Lock } from 'lucide-react';
import { useState, type ReactNode } from 'react';
import { cn } from '@/lib/utils';
import { panel } from './StoryHud';
import type { MapTool } from './StoryMap';
import { DESIGNATE_TOOLS, FURNITURE_TOOLS, STRUCTURE_TOOLS, WATER_TOOLS } from './tools';
import { ItemIcon, ToolIcon } from './visuals';
import { ZoneToolbar } from './zones/ZoneToolbar';
import { DesignLibrary } from './DesignLibrary';

// ------------------------------------------------------------------ architect

type ArchCat =
  'orders' | 'zones' | 'structure' | 'furniture' | 'production' | 'storage' | 'lighting' | 'water' | 'designs' | 'misc';

const TIER_LABEL: Record<string, string> = {
  gathered: 'Gathered',
  hand: 'Hand',
  workbench: 'Workbench',
  furnace: 'Furnace',
};

/** What the colony has of a placeable, can make, or is short of (one line under its name). */
export function stockLine(e: StoryArchitectEntry): string {
  if (e.have > 0) return `have ${e.have}`;
  if (e.craftable > 0)
    return `craftable ${e.craftable}${e.from.length ? ` from ${e.from.join(', ').toLowerCase()}` : ''}`;
  if (e.short.length) return `short: ${e.short.join(', ').toLowerCase()}`;
  return e.tier === 'gathered' ? 'none stored' : '';
}

/**
 * The Architect's placeables for one tab (the bot's palette: every block and piece of furniture the version has), each with
 * its tier and whether the colony unlocked it. Locked ones are greyed with a lock and the reason; picking an unlocked one
 * arms its build tool with it as the material.
 */
export function ArchitectPalette({
  entries,
  tab,
  onPick,
}: {
  entries: readonly StoryArchitectEntry[];
  tab: string;
  onPick: (e: StoryArchitectEntry) => void;
}) {
  const list = entries.filter((e) => e.tab === tab);
  if (!list.length) return null;
  return (
    <section className="min-w-0" aria-label="Placeables" data-testid="architect-palette">
      <div className="grid grid-cols-2 gap-1.5">
        {list.map((e) => (
          <button
            key={e.id}
            type="button"
            data-entry={e.id}
            data-tier={e.tier}
            data-unlocked={e.unlocked ? 'true' : 'false'}
            disabled={!e.unlocked}
            onClick={() => onPick(e)}
            title={
              [e.reason, e.chain.length ? `Made: ${e.chain.join(' → ')}` : null].filter(Boolean).join('\n') || e.label
            }
            className={cn(
              'flex min-w-0 items-center gap-1.5 rounded-md border px-1.5 py-1 text-left text-[11px]',
              e.unlocked
                ? 'border-border bg-surface-2 hover:border-border-strong'
                : 'cursor-not-allowed border-dashed border-border bg-surface-1 opacity-50',
            )}
          >
            <ItemIcon id={e.item[0]} meta={e.item[1]} size={20} />
            <span className="min-w-0 flex-1">
              <span className="flex items-center gap-1 truncate font-semibold">
                {e.unlocked ? null : <Lock className="size-3 shrink-0" aria-label="locked" />}
                <span className="truncate">{e.label}</span>
              </span>
              <span className="block truncate text-[10px] text-muted">
                {TIER_LABEL[e.tier] ?? e.tier}
                {e.unlocked ? (stockLine(e) ? ` · ${stockLine(e)}` : '') : ` · ${e.reason ?? 'locked'}`}
              </span>
            </span>
          </button>
        ))}
      </div>
    </section>
  );
}

const ARCH_CATS: ReadonlyArray<[ArchCat, string]> = [
  ['orders', 'Orders'],
  ['zones', 'Zones'],
  ['structure', 'Structure'],
  ['furniture', 'Furniture'],
  ['production', 'Production'],
  ['storage', 'Storage'],
  ['lighting', 'Lighting'],
  ['water', 'Water'],
  ['designs', 'Designs'],
  ['misc', 'Misc'],
];

/** The category the Architect opens on: the one holding the tool in hand. */
function categoryOf(tool: MapTool): ArchCat {
  if (tool.kind === 'design') return 'designs';
  if (tool.kind === 'zone') return 'zones';
  if (tool.kind === 'storage') return 'storage';
  if (tool.kind !== 'build') return 'orders';
  if (tool.id.startsWith('furniture:')) return 'furniture';
  return WATER_TOOLS.some((w) => w.id === tool.id) ? 'water' : 'structure';
}

type ToolBtn = (t: MapTool, icon: string, label: string, hint: string, key?: string) => ReactNode;

/** One tool button (pressed while in hand; clicking it again puts it down). */
function toolButton(tool: MapTool, onTool: (t: MapTool) => void): ToolBtn {
  const active = (t: MapTool) => JSON.stringify(t) === JSON.stringify(tool);
  return (t, icon, label, hint, key) => (
    <button
      key={label}
      type="button"
      onClick={() => onTool(active(t) ? { kind: 'select' } : t)}
      aria-pressed={active(t)}
      title={hint}
      className={cn(
        'relative flex h-[62px] w-[74px] flex-col items-center justify-center gap-1 rounded-md border text-[11px] leading-tight font-medium',
        active(t)
          ? 'border-accent bg-accent-soft text-fg'
          : 'border-border bg-surface-2 text-muted hover:border-border-strong hover:text-fg',
      )}
    >
      <ToolIcon id={icon} className="size-5" />
      <span className="text-center">{label}</span>
      {key ? <span className="absolute top-0.5 right-1 font-mono text-[9px] text-subtle">{key}</span> : null}
    </button>
  );
}

type CatProps = {
  tool: MapTool;
  onTool: (t: MapTool) => void;
  palette?: StoryArchitectResponse;
  plants?: readonly StoryPlant[] | null;
  zones?: readonly StoryZone[];
  btn: ToolBtn;
};

const loadingPalette = ({ palette }: CatProps) =>
  palette ? null : <p className="w-56 text-[11px] leading-snug text-muted">Loading what the colony can make…</p>;

/** Each category's tools (the palette's own entries show beside the panel). */
const CATEGORY_TOOLS: Record<ArchCat, (p: CatProps) => ReactNode> = {
  orders: ({ btn }) =>
    DESIGNATE_TOOLS.map((t) => btn({ kind: 'designate', id: t.id }, t.id, t.label, t.hint, t.key.replace('Key', ''))),
  zones: ({ tool, onTool, plants, zones, btn }) => (
    <ZoneToolbar tool={tool} onTool={onTool} plants={plants} zones={zones ?? []} btn={btn} />
  ),
  structure: ({ btn }) => STRUCTURE_TOOLS.map((s) => btn({ kind: 'build', id: s.id }, s.id, s.label, s.hint)),
  furniture: ({ btn }) =>
    FURNITURE_TOOLS.map((f) =>
      btn({ kind: 'build', id: `furniture:${f.id}` }, f.id, f.label, `Place a ${f.label.toLowerCase()} (Q/E turns)`),
    ),
  water: ({ palette, btn }) => (
    <>
      {WATER_TOOLS.filter(
        // Lava only while the colony allows it (the palette lists it then).
        (w) => w.id !== 'lava' || palette?.entries.some((e) => e.kind === 'lava'),
      ).map((w) => btn({ kind: 'build', id: w.id }, w.id, w.label, w.hint))}
      <p className="w-56 text-[11px] leading-snug text-muted" data-testid="water-note">
        Pawns bring a bucket (3 iron ingots at a crafting table), fill it at still water and pour each cell. Dig the
        trench first for a canal; a 2×2 infinite source refills.
      </p>
    </>
  ),
  storage: ({ btn }) => (
    <>
      {btn(
        { kind: 'storage' },
        'storage',
        'Register chests',
        'Drag over chests: every chest in the area becomes storage',
      )}
      <p className="w-56 text-[11px] leading-snug text-muted">
        Loot lives in chests. Drag over chests to register them, then click one (or box-select several) to choose what
        it keeps and how much it matters.
      </p>
    </>
  ),
  production: loadingPalette,
  lighting: loadingPalette,
  misc: loadingPalette,
  designs: () => null,
};

export function ArchitectPanel({
  tool,
  onTool,
  palette,
  onPick,
  plants,
  zones,
  onDesignPick,
}: {
  tool: MapTool;
  onTool: (t: MapTool) => void;
  palette?: StoryArchitectResponse;
  onPick?: (e: StoryArchitectEntry) => void;
  /** What a growing zone can grow here (the plant picker under the Zones tools). */
  plants?: readonly StoryPlant[] | null;
  /** The colony's zones (the Zones tools name the zone being extended). */
  zones?: readonly StoryZone[];
  onDesignPick?: (id: number) => void;
}) {
  const [cat, setCat] = useState<ArchCat>(categoryOf(tool));
  const btn = toolButton(tool, onTool);
  return (
    <section
      className={cn(panel, 'pointer-events-auto flex w-[40rem] max-w-[calc(100vw-2rem)] gap-3 p-2')}
      aria-label="Architect"
      data-testid="architect"
    >
      <div className="flex w-24 shrink-0 flex-col gap-0.5">
        {ARCH_CATS.map(([id, label]) => (
          <button
            key={id}
            type="button"
            onClick={() => setCat(id)}
            aria-pressed={cat === id}
            className={cn(
              'rounded px-2 py-1.5 text-left text-xs font-semibold',
              cat === id ? 'bg-accent-soft text-fg' : 'text-muted hover:bg-surface-2',
            )}
          >
            {label}
          </button>
        ))}
      </div>
      <div className="max-h-[min(56vh,36rem)] min-w-0 flex-1 overflow-y-auto pr-1">
        <h3 className="mb-2 text-sm font-semibold">{ARCH_CATS.find(([id]) => id === cat)?.[1]}</h3>
        {cat === 'designs' ? (
          <DesignLibrary onPick={onDesignPick} />
        ) : (
          <>
            <div className="mb-2 flex flex-wrap content-start gap-1.5">
              {CATEGORY_TOOLS[cat]({ tool, onTool, palette, plants, zones, btn })}
            </div>
            {palette && onPick && cat !== 'orders' && cat !== 'zones' ? (
              <ArchitectPalette entries={palette.entries} tab={cat} onPick={onPick} />
            ) : null}
          </>
        )}
      </div>
    </section>
  );
}
