'use client';

/**
 * The bottom tab bar's windows: the Architect (orders, zones, structure, furniture, storage), the Work grid (click cycles
 * 1–4 and off, skill as the tint, passion mark, sort by a column), the Schedule grid (paint hours with a brush by dragging;
 * copy and paste between pawns), the build queue (drag to reorder, pause, cancel), the AI directives and the colony.
 */
import {
  STORY_HOURS,
  storyClock,
  type StoryAiBody,
  type StoryArchitectEntry,
  type StoryArchitectResponse,
  type StoryBuild,
  type StoryBuildMaterial,
  type StoryColonyState,
  type StoryDirective,
  type StoryOnlineBlocked,
  type StoryPawn,
  type StoryPlant,
  type StoryIdleMode,
  type StoryScheduleBlock,
  type StoryZone,
} from '@mineclaw/contracts';
import { ZonePreserve } from './zones/PreservePanel';
import { buildTitle, isRepairBuild } from './zones/preserve';
import { zoneCellCount } from './zones/zonePaint';
import { ZoneToolbar } from './zones/ZoneToolbar';
import {
  ClipboardPaste,
  Copy,
  GripVertical,
  Home,
  Lock,
  MapPin,
  Pause,
  Pencil,
  Play,
  Plus,
  ShieldCheck,
  Trash2,
} from 'lucide-react';
import { useRef, useState, type ReactNode } from 'react';
import { Button, Input } from '@/components/ui/primitives';
import { cn } from '@/lib/utils';
import {
  applyPaint,
  directiveStatusTone,
  paintRequests,
  paintSpan,
  pasteRequest,
  SCHEDULE_BRUSHES,
} from './model';
import { useWorldClock, type WorldClock } from './clock';
import { panel } from './StoryHud';
import { BringOnlineBox } from './StoryInspector';
import { DESIGNATE_TOOLS, FURNITURE_TOOLS, STRUCTURE_TOOLS, WATER_TOOLS } from './tools';
import type { MapTool } from './StoryMap';
import { AiMark, ItemIcon, ToolIcon } from './visuals';

export type TabId = 'architect' | 'work' | 'schedule' | 'assign' | 'queue' | 'directives' | 'colony';

export const TABS: ReadonlyArray<{ id: TabId; label: string; key: string }> = [
  { id: 'architect', label: 'Architect', key: 'B' },
  { id: 'work', label: 'Work', key: 'P' },
  { id: 'schedule', label: 'Schedule', key: 'Y' },
  { id: 'assign', label: 'Assign', key: '' },
  { id: 'queue', label: 'Build queue', key: 'U' },
  { id: 'directives', label: 'Directives', key: 'O' },
  { id: 'colony', label: 'Colony', key: 'I' },
];

export function TabBar({
  open,
  onOpen,
  badge,
}: {
  open: TabId | null;
  onOpen: (t: TabId | null) => void;
  badge: Partial<Record<TabId, number>>;
}) {
  return (
    <nav
      className={cn(
        panel,
        'pointer-events-auto flex items-stretch overflow-hidden rounded-b-none border-b-0 text-[12.5px]',
      )}
      aria-label="Colony tabs"
      data-testid="tab-bar"
    >
      {TABS.map((t) => (
        <button
          key={t.id}
          type="button"
          onClick={() => onOpen(open === t.id ? null : t.id)}
          aria-pressed={open === t.id}
          className={cn(
            'relative flex h-9 items-center gap-1.5 border-r border-border px-3.5 font-semibold transition-colors last:border-r-0',
            open === t.id ? 'bg-accent-soft text-fg' : 'text-muted hover:bg-surface-2 hover:text-fg',
          )}
        >
          {t.label}
          <span className="font-mono text-[10px] text-subtle">{t.key}</span>
          {badge[t.id] ? (
            <span className="rounded bg-accent px-1 text-[10px] leading-4 text-accent-fg">{badge[t.id]}</span>
          ) : null}
        </button>
      ))}
    </nav>
  );
}

// ------------------------------------------------------------------ architect

type ArchCat = 'orders' | 'zones' | 'structure' | 'furniture' | 'production' | 'storage' | 'lighting' | 'water' | 'misc';

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
    <section
      className={cn(panel, 'pointer-events-auto max-h-[46vh] w-[22rem] overflow-auto p-2')}
      aria-label="Placeables"
      data-testid="architect-palette"
    >
      <div className="grid grid-cols-2 gap-1">
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
  ['misc', 'Misc'],
];

/** The category the Architect opens on: the one holding the tool in hand. */
function categoryOf(tool: MapTool): ArchCat {
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
      {btn({ kind: 'storage' }, 'storage', 'Register chests', 'Drag over chests: every chest in the area becomes storage')}
      <p className="w-56 text-[11px] leading-snug text-muted">
        Loot lives in chests. Drag over chests to register them, then click one (or box-select several) to choose what it
        keeps and how much it matters.
      </p>
    </>
  ),
  production: loadingPalette,
  lighting: loadingPalette,
  misc: loadingPalette,
};

export function ArchitectPanel({
  tool,
  onTool,
  palette,
  onPick,
  plants,
  zones,
}: {
  tool: MapTool;
  onTool: (t: MapTool) => void;
  palette?: StoryArchitectResponse;
  onPick?: (e: StoryArchitectEntry) => void;
  /** What a growing zone can grow here (the plant picker under the Zones tools). */
  plants?: readonly StoryPlant[] | null;
  /** The colony's zones (the Zones tools name the zone being extended). */
  zones?: readonly StoryZone[];
}) {
  const [cat, setCat] = useState<ArchCat>(categoryOf(tool));
  const btn = toolButton(tool, onTool);
  return (
    <div className="flex items-end gap-2">
      <section
        className={cn(panel, 'pointer-events-auto flex w-[34rem] gap-2 p-2')}
        aria-label="Architect"
        data-testid="architect"
      >
        <div className="flex w-24 flex-col gap-0.5">
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
        <div className="flex flex-1 flex-wrap content-start gap-1.5">
          {CATEGORY_TOOLS[cat]({ tool, onTool, palette, plants, zones, btn })}
        </div>
      </section>
      {palette && onPick && cat !== 'orders' && cat !== 'zones' ? (
        <ArchitectPalette entries={palette.entries} tab={cat} onPick={onPick} />
      ) : null}
    </div>
  );
}

// ------------------------------------------------------------------ work

// The Work grid with roles lives in roles/WorkPanel.tsx (docs/web/story-mode.md "Roles").
export { WorkPanel } from './roles/WorkPanel';

// ------------------------------------------------------------------ schedule

const BLOCK_COLOR: Record<string, string> = { work: '#d9a441', sleep: '#4d6fb8', anything: '#6f7b8a', free: '#5bb07a' };

export function SchedulePanel({
  clock: given,
  pawns,
  ai,
  canWrite,
  onPaint,
}: {
  /** The page's world clock (else the context's). */
  clock?: WorldClock | null;
  pawns: StoryPawn[];
  ai: StoryAiBody[];
  canWrite: boolean;
  onPaint: (req: Array<{ pawns: string[]; hours: Record<string, StoryScheduleBlock> }>) => void;
}) {
  const [brush, setBrush] = useState<StoryScheduleBlock>('work');
  // The column in force: the clock hour of the page's world clock (the server's time; the bot indexes schedules by it).
  const clock = useWorldClock(given);
  const hour = clock.known ? clock.hour : null;
  const [drag, setDrag] = useState<{ from: { row: number; hour: number }; to: { row: number; hour: number } } | null>(
    null,
  );
  const [local, setLocal] = useState<Record<string, string[]>>({});
  const [clip, setClip] = useState<string[] | null>(null);
  const names = pawns.map((p) => p.name);
  const sched = (p: StoryPawn) => local[p.name] ?? p.schedule;
  const span = drag ? paintSpan(names, drag.from, drag.to) : null;
  const commit = () => {
    if (!drag || !canWrite) return setDrag(null);
    const cells = paintSpan(names, drag.from, drag.to);
    setLocal((l) => {
      const n = { ...l };
      for (const [name, hours] of cells) n[name] = applyPaint(sched(pawns.find((p) => p.name === name)!), hours, brush);
      return n;
    });
    onPaint(paintRequests(cells, brush));
    setDrag(null);
  };
  return (
    <section
      className={cn(panel, 'pointer-events-auto max-h-[46vh] overflow-auto p-2 select-none')}
      aria-label="Schedule"
      data-testid="schedule-panel"
      onPointerUp={commit}
      onPointerLeave={() => drag && commit()}
    >
      <div className="mb-1.5 flex items-center gap-1.5 text-xs">
        <span className="font-semibold">Brush</span>
        {SCHEDULE_BRUSHES.map((b) => (
          <button
            key={b.id}
            type="button"
            onClick={() => setBrush(b.id)}
            aria-pressed={brush === b.id}
            className={cn(
              'flex h-6 items-center gap-1 rounded px-2 font-medium',
              brush === b.id ? 'ring-2 ring-[#f5c542]' : 'opacity-80 hover:opacity-100',
            )}
            style={{ background: `${BLOCK_COLOR[b.id]}55` }}
          >
            <span className="size-2.5 rounded-sm" style={{ background: BLOCK_COLOR[b.id] }} />
            {b.label}
          </button>
        ))}
        <span className="ml-2 text-[10.5px] text-subtle">Drag across hours (and rows) to paint</span>
      </div>
      <table className="border-separate border-spacing-0 text-[10px]">
        <thead>
          <tr>
            <th />
            {STORY_HOURS.map((h) => (
              <th
                key={h}
                className={cn('w-[22px] pb-0.5 font-medium text-subtle', Number(h) === hour && 'text-accent')}
                data-now={Number(h) === hour ? 'true' : undefined}
              >
                {storyClock(Number(h)).slice(0, 2)}
              </th>
            ))}
            <th />
          </tr>
        </thead>
        <tbody>
          {pawns.map((p, row) => (
            <tr key={p.name}>
              <td className="pr-2 text-xs font-semibold whitespace-nowrap">{p.display}</td>
              {STORY_HOURS.map((h, i) => {
                const painting = span?.get(p.name)?.includes(i);
                const v = painting ? brush : (sched(p)[i] ?? 'anything');
                return (
                  <td key={h} className="p-[1px]">
                    <div
                      data-hour={`${p.name}:${i}`}
                      role="gridcell"
                      aria-label={`${p.display} ${storyClock(i)}: ${v}`}
                      onPointerDown={(e) => {
                        if (!canWrite || e.button !== 0) return;
                        setDrag({ from: { row, hour: i }, to: { row, hour: i } });
                      }}
                      onPointerEnter={() => drag && setDrag({ ...drag, to: { row, hour: i } })}
                      className={cn('h-5 w-[21px] rounded-[2px]', Number(h) === hour && 'ring-1 ring-accent')}
                      style={{ background: BLOCK_COLOR[v] ?? '#555', opacity: painting ? 1 : 0.85 }}
                    />
                  </td>
                );
              })}
              <td className="pl-1 whitespace-nowrap">
                <button
                  type="button"
                  title="Copy this schedule"
                  aria-label={`Copy ${p.display}'s schedule`}
                  onClick={() => setClip([...sched(p)])}
                  className="rounded p-0.5 hover:bg-surface-2"
                >
                  <Copy className="size-3" />
                </button>
                <button
                  type="button"
                  title="Paste the copied schedule"
                  aria-label={`Paste onto ${p.display}`}
                  disabled={!clip || !canWrite}
                  onClick={() => {
                    if (!clip) return;
                    setLocal((l) => ({ ...l, [p.name]: [...clip] }));
                    onPaint([pasteRequest(clip, [p.name])]);
                  }}
                  className="rounded p-0.5 hover:bg-surface-2 disabled:opacity-30"
                >
                  <ClipboardPaste className="size-3" />
                </button>
              </td>
            </tr>
          ))}
          {ai.length ? (
            <tr>
              <td colSpan={26} className="pt-1 text-[11px] text-muted italic">
                <AiMark className="mr-1 inline size-3 text-[#a78bfa]" />
                {ai.length} AI bod{ai.length === 1 ? 'y keeps its' : 'ies keep their'} own hours.
              </td>
            </tr>
          ) : null}
        </tbody>
      </table>
    </section>
  );
}

// ------------------------------------------------------------------ build queue

/** One item a build still needs: have / crafting / short, the recipe chain and the raw shortfall on hover. */
export function MaterialRow({ m }: { m: StoryBuildMaterial }) {
  const tip = [
    m.chain.length
      ? `Recipe: ${m.chain.join(' → ')}`
      : m.via === 'none'
        ? 'No recipe (or its crafting was cancelled)'
        : null,
    m.raw.length ? `Short of: ${m.raw.join(', ')}` : null,
  ]
    .filter(Boolean)
    .join('\n');
  return (
    <div
      className="flex items-center gap-1.5 text-[10.5px] tabular-nums"
      data-material={m.item}
      data-short={m.short}
      title={tip || undefined}
    >
      <span className="min-w-0 flex-1 truncate text-muted">{m.label ?? m.item.replace(/_/g, ' ')}</span>
      <span className="text-subtle">
        {m.have}/{m.need}
      </span>
      {m.crafting > 0 ? (
        <span className="rounded bg-accent-soft px-1 text-fg" data-crafting={m.crafting}>
          {m.via === 'smelt' ? 'smelting' : 'crafting'} {m.crafting}
        </span>
      ) : null}
      {m.short > 0 ? <span className="rounded bg-danger-soft px-1 text-danger">short {m.short}</span> : null}
    </div>
  );
}

type QueueOp = (op: 'remove' | 'pause' | 'resume' | 'reorder', ids: Array<string | number>) => void;

export function QueuePanel({
  builds,
  canWrite,
  onOp,
  failed,
  onRetry,
}: {
  builds: StoryBuild[];
  canWrite: boolean;
  onOp: QueueOp;
  /** Failed cells per build id: "Retry failed cells (N)". */
  failed?: ReadonlyMap<string, number>;
  onRetry?: (id: string | number) => void;
}) {
  const list = [...builds].filter((b) => b.status !== 'done').sort((a, b) => a.order - b.order);
  // A preserved zone's repairs rest as done while the zone is intact: not a finished build.
  const done = builds.filter((b) => b.status === 'done' && !isRepairBuild(b)).length;
  const dragId = useRef<string | number | null>(null);
  const [over, setOver] = useState<string | number | null>(null);
  /** Dropping the dragged build on `onto`: it goes before it. */
  const drop = (onto: string | number) => {
    setOver(null);
    const from = dragId.current;
    dragId.current = null;
    if (from === null || from === undefined || from === onto) return;
    const ids = list.map((x) => x.id).filter((x) => x !== from);
    ids.splice(ids.indexOf(onto), 0, from);
    onOp('reorder', ids);
  };
  return (
    <section
      className={cn(panel, 'pointer-events-auto max-h-[46vh] w-[30rem] overflow-auto p-2')}
      aria-label="Build queue"
      data-testid="queue-panel"
    >
      {list.length === 0 ? (
        <p className="p-2 text-xs text-muted">Nothing queued. Pick a structure in the Architect and drag on the map.</p>
      ) : null}
      <ol className="space-y-1">
        {list.map((b, i) => (
          <li
            key={b.id}
            data-build={b.id}
            draggable={canWrite}
            onDragStart={() => (dragId.current = b.id)}
            onDragOver={(e) => {
              e.preventDefault();
              setOver(b.id);
            }}
            onDragLeave={() => setOver((o) => (o === b.id ? null : o))}
            onDrop={() => drop(b.id)}
            className={cn(
              'flex items-center gap-2 rounded-md border border-border bg-surface-2 px-2 py-1.5 text-xs',
              over === b.id && 'border-t-2 border-t-[#f5c542]',
            )}
          >
            <QueueRow
              b={b}
              i={i}
              canWrite={canWrite}
              onOp={onOp}
              failedCells={failed?.get(String(b.id)) ?? 0}
              onRetry={onRetry}
              others={list}
            />
          </li>
        ))}
      </ol>
      {done ? <p className="mt-1 px-1 text-[10.5px] text-subtle">{done} finished</p> : null}
    </section>
  );
}

const STATUS_TONE: Record<string, string> = {
  active: 'bg-ok-soft text-ok',
  paused: 'bg-warn-soft text-warn',
  blocked: 'bg-danger-soft text-danger',
};

/** One queued build: its progress, why it waits, its retry, its station and materials, and its buttons. */
function QueueRow({
  b,
  i,
  canWrite,
  onOp,
  failedCells,
  onRetry,
  others,
}: {
  b: StoryBuild;
  i: number;
  canWrite: boolean;
  onOp: QueueOp;
  failedCells: number;
  onRetry?: (id: string | number) => void;
  others: readonly StoryBuild[];
}) {
  const paused = b.status === 'paused';
  const title = buildTitle(b);
  return (
    <>
      <GripVertical className="size-3.5 cursor-grab text-subtle" aria-hidden />
      <span className="w-4 text-right text-subtle tabular-nums">{i + 1}</span>
      <ToolIcon id={b.kind === 'furniture' ? 'block' : b.kind} className="size-3.5 text-muted" />
      <div className="min-w-0 flex-1">
        <div className="truncate font-semibold">{title}</div>
        <BuildProgress b={b} />
        <BuildNotes b={b} canWrite={canWrite} failedCells={failedCells} onRetry={onRetry} />
        {b.station ? (
          <div className="text-[10.5px] text-muted">
            making a {b.station.kind.replace(/_/g, ' ')} at {b.station.pos.join(', ')}
          </div>
        ) : null}
        {(b.materials ?? []).map((m) => (
          <MaterialRow key={m.item} m={m} />
        ))}
      </div>
      <span
        className={cn('rounded px-1 text-[10px] font-semibold uppercase', STATUS_TONE[b.status] ?? 'bg-surface-3 text-muted')}
      >
        {b.status}
      </span>
      <button
        type="button"
        disabled={!canWrite || i === 0}
        onClick={() => onOp('reorder', [b.id, ...others.filter((x) => x.id !== b.id).map((x) => x.id)])}
        className="rounded px-1 text-[10px] font-semibold text-muted hover:bg-surface-3 disabled:opacity-30"
        title="Build first"
        aria-label={`Build ${title} first`}
      >
        Top
      </button>
      <button
        type="button"
        disabled={!canWrite}
        onClick={() => onOp(paused ? 'resume' : 'pause', [b.id])}
        className="rounded p-0.5 hover:bg-surface-3"
        aria-label={paused ? `Resume ${title}` : `Pause ${title}`}
      >
        {paused ? <Play className="size-3.5" /> : <Pause className="size-3.5" />}
      </button>
      <CancelBuild b={b} title={title} canWrite={canWrite} onOp={onOp} />
    </>
  );
}

/** A build's Cancel button; a preserved zone's repairs have none (Preserve goes off in the zone's settings). */
function CancelBuild({ b, title, canWrite, onOp }: { b: StoryBuild; title: string; canWrite: boolean; onOp: QueueOp }) {
  if (isRepairBuild(b))
    return (
      <span
        className="p-0.5 text-subtle"
        title="Repairs a preserved zone: switch Preserve off in that zone's settings to stop them"
        data-testid="repair-build"
      >
        <ShieldCheck className="size-3.5" aria-hidden />
      </span>
    );
  return (
    <button
      type="button"
      disabled={!canWrite}
      onClick={() => onOp('remove', [b.id])}
      className="rounded p-0.5 text-danger hover:bg-danger-soft"
      aria-label={`Cancel ${title}`}
    >
      <Trash2 className="size-3.5" />
    </button>
  );
}

/** Why a build waits or is blocked, and its "Retry failed cells" button. */
function BuildNotes({
  b,
  canWrite,
  failedCells,
  onRetry,
}: {
  b: StoryBuild;
  canWrite: boolean;
  failedCells: number;
  onRetry?: (id: string | number) => void;
}) {
  return (
    <>
      {b.reason || b.status === 'blocked' ? (
        <div className={cn('text-[10.5px]', b.status === 'waiting' ? 'text-muted' : 'text-danger')}>
          {b.reason ?? 'blocked'}
        </div>
      ) : null}
      {failedCells > 0 && onRetry ? (
        <button
          type="button"
          disabled={!canWrite}
          onClick={() => onRetry(b.id)}
          data-testid="build-retry"
          className="mt-0.5 rounded border border-border px-1.5 py-0.5 text-[10.5px] font-semibold hover:bg-surface-3 disabled:opacity-40"
          title="These cells failed (or rest after failing): try them again now"
        >
          Retry failed cells ({failedCells})
        </button>
      ) : null}
    </>
  );
}

function BuildProgress({ b }: { b: StoryBuild }) {
  return (
    <div className="mt-0.5 flex items-center gap-1.5">
      <span className="block h-1.5 flex-1 overflow-hidden rounded-full bg-surface-3">
        <span
          className="block h-full bg-accent"
          style={{ width: `${b.blocksTotal ? (b.blocksDone / b.blocksTotal) * 100 : 0}%` }}
        />
      </span>
      <span className="tabular-nums text-subtle">
        {b.blocksDone}/{b.blocksTotal}
      </span>
    </div>
  );
}

// ------------------------------------------------------------------ directives

export function DirectiveList({ directives, bot }: { directives: readonly StoryDirective[]; bot?: string }) {
  const list = [...directives].filter((d) => !bot || d.bots.includes(bot)).reverse();
  if (!list.length)
    return (
      <p className="text-xs text-muted">
        No directives yet. Select an AI body and use a tool, an order or a build: it gets the same task.
      </p>
    );
  const tone: Record<string, string> = {
    info: 'bg-info-soft text-info',
    warn: 'bg-warn-soft text-warn',
    ok: 'bg-ok-soft text-ok',
    danger: 'bg-danger-soft text-danger',
    muted: 'bg-surface-3 text-muted',
  };
  return (
    <ul className="space-y-1" data-testid="directives">
      {list.map((d) => (
        <li key={d.id} className="rounded-md border border-border bg-surface-2 px-2 py-1.5 text-xs">
          <div className="flex items-center gap-1.5">
            <ToolIcon id={d.kind === 'text' ? 'block' : d.kind} className="size-3.5 text-muted" />
            <span className="min-w-0 flex-1 truncate font-semibold">{d.summary}</span>
            <span
              className={cn('rounded px-1 text-[10px] font-semibold', tone[directiveStatusTone(d.status)])}
              data-status={d.status}
            >
              {d.status}
            </span>
          </div>
          <div className="mt-0.5 text-[10.5px] text-subtle">to {d.bots.join(', ')}</div>
          {d.reply ? <div className="mt-0.5 text-[11px] text-fg/90 italic">“{d.reply}”</div> : null}
        </li>
      ))}
    </ul>
  );
}

export function DirectivesPanel({ state }: { state: StoryColonyState | undefined }) {
  return (
    <section
      className={cn(panel, 'pointer-events-auto max-h-[46vh] w-[26rem] overflow-auto p-2')}
      aria-label="Directives"
    >
      <DirectiveList directives={state?.directives ?? []} />
    </section>
  );
}

// ------------------------------------------------------------------ colony

type ColonyOptionsChange = {
  autoPlaceBenches?: boolean;
  detectBenches?: boolean;
  idle?: StoryIdleMode;
  rescueTeleport?: boolean;
  unfilteredStorageTakesAnything?: boolean;
  keepDisallowed?: boolean;
};

type ColonyEntry = { id: string; name: string; pawns: string[]; paused?: boolean | null };

export function ColonyPanel({
  state,
  colonies,
  current,
  canWrite,
  onSwitch,
  onCreate,
  centre,
  migrate,
  onMoveHome,
  onConfirmMove,
  onCancelMove,
  onPickHome,
  onDrawHouse,
  onShowZone,
  onRenameZone,
  onDeleteZone,
  onPause,
  onRelease,
  onResume,
  blocked = [],
  onBringAll,
  displayOf,
  onOptions,
}: {
  state: StoryColonyState | undefined;
  colonies: ColonyEntry[];
  current: string | null;
  canWrite: boolean;
  onSwitch: (id: string) => void;
  onCreate: (name: string, pawns: number) => void;
  /** The map centre's column: where a new colony settles and where "Move home here" moves this one. */
  centre: readonly number[] | null;
  /** A migration waiting for the confirm: the new home column. */
  migrate: readonly number[] | null;
  onMoveHome: () => void;
  onConfirmMove: () => void;
  onCancelMove: () => void;
  /** The "Set home" map tool: click the spot. */
  onPickHome: () => void;
  onDrawHouse: () => void;
  onShowZone: (z: StoryZone) => void;
  onRenameZone: (z: StoryZone, label: string) => void;
  onDeleteZone: (z: StoryZone) => void;
  onPause: () => void;
  onRelease: () => void;
  onResume: () => void;
  /** The last "Bring online" refusals. */
  blocked?: readonly StoryOnlineBlocked[];
  /** Bring every offline pawn in (overCap: the regulars' seats too). */
  onBringAll?: (overCap: boolean) => void;
  displayOf?: (account: string) => string;
  /** Colony options (auto-place benches builds need, detect benches near home). */
  onOptions?: (o: ColonyOptionsChange) => void;
}) {
  return (
    <section
      className={cn(panel, 'pointer-events-auto w-[26rem] space-y-3 p-3 text-xs')}
      aria-label="Colony"
      data-testid="colony-panel"
    >
      <ColonyList colonies={colonies} current={current} onSwitch={onSwitch} />
      {state ? (
        <>
          <ColonyActions state={state} canWrite={canWrite} onPause={onPause} onRelease={onRelease} onResume={onResume} />
          {state.options && onOptions ? (
            <ColonyOptions opts={state.options} canWrite={canWrite} onOptions={onOptions} />
          ) : null}
          {state.seats ? <SeatsLine seats={state.seats} /> : null}
          {onBringAll ? (
            <BringAllBox
              state={state}
              blocked={blocked}
              canWrite={canWrite}
              onBringAll={onBringAll}
              displayOf={displayOf}
            />
          ) : null}
          <HomeBox
            home={state.home}
            canWrite={canWrite}
            centre={centre}
            migrate={migrate}
            onPickHome={onPickHome}
            onMoveHome={onMoveHome}
            onConfirmMove={onConfirmMove}
            onCancelMove={onCancelMove}
          />
          <Houses
            zones={state.zones.filter((z) => z.kind === 'home')}
            canWrite={canWrite}
            onDraw={onDrawHouse}
            onShow={onShowZone}
            onRename={onRenameZone}
            onDelete={onDeleteZone}
          />
        </>
      ) : null}
      <NewColony canWrite={canWrite} at={centre} onCreate={onCreate} />
    </section>
  );
}

function ColonyList({
  colonies,
  current,
  onSwitch,
}: {
  colonies: ColonyEntry[];
  current: string | null;
  onSwitch: (id: string) => void;
}) {
  return (
    <div>
      <div className="mb-1 font-semibold">Colonies</div>
      <ul className="space-y-1">
        {colonies.map((c) => (
          <li key={c.id}>
            <button
              type="button"
              onClick={() => onSwitch(c.id)}
              aria-pressed={c.id === current}
              className={cn(
                'flex w-full items-center gap-2 rounded border px-2 py-1 text-left',
                c.id === current ? 'border-accent bg-accent-soft' : 'border-border hover:bg-surface-2',
              )}
            >
              <span className="font-semibold">{c.name}</span>
              <span className="text-muted">{c.pawns.length ? `${c.pawns.length} pawns` : 'released'}</span>
              {c.paused ? <span className="ml-auto text-warn">paused</span> : null}
            </button>
          </li>
        ))}
      </ul>
    </div>
  );
}

function ColonyActions({
  state,
  canWrite,
  onPause,
  onRelease,
  onResume,
}: {
  state: StoryColonyState;
  canWrite: boolean;
  onPause: () => void;
  onRelease: () => void;
  onResume: () => void;
}) {
  return (
    <div className="flex gap-2">
      <Button size="sm" disabled={!canWrite} onClick={onPause}>
        {state.paused ? <Play className="size-3.5" /> : <Pause className="size-3.5" />}
        {state.paused ? 'Resume colony' : 'Pause colony'}
      </Button>
      <Button
        size="sm"
        variant="ghost"
        disabled={!canWrite || !state.pawns.some((p) => p.online !== false)}
        onClick={onRelease}
      >
        Release pawns
      </Button>
      {state.active === false ? (
        <Button size="sm" variant="primary" disabled={!canWrite} onClick={onResume}>
          Resume colony
        </Button>
      ) : null}
    </div>
  );
}

type ColonyFlag = 'autoPlaceBenches' | 'detectBenches' | 'unfilteredStorageTakesAnything' | 'keepDisallowed' | 'rescueTeleport';

/** The colony's on / off options, in the panel's order. */
const COLONY_FLAGS: ReadonlyArray<{ key: ColonyFlag; label: string; title: string }> = [
  {
    key: 'autoPlaceBenches',
    label: 'Builds may place the benches they need',
    title:
      "A build that needs a crafting table or furnace the colony hasn't got: a pawn crafts one and places it near home",
  },
  {
    key: 'detectBenches',
    label: 'Use any bench near home (no designation)',
    title: "Off: only benches you designate (inspector: Use as colony workbench) are used, never another player's",
  },
  {
    key: 'unfilteredStorageTakesAnything',
    label: 'Unfiltered storage takes anything',
    title: "Loot no chest's filter takes goes into any chest with room that doesn't deny it",
  },
  {
    key: 'keepDisallowed',
    label: 'Keep disallowed items until a chest takes them',
    title: "Off (like the genre): what a chest's filter no longer allows and no chest takes is dropped beside it",
  },
  {
    key: 'rescueTeleport',
    label: 'Teleport stuck pawns home',
    title: 'Last resort for a pawn stuck in a pit or a cave: teleport it home (op)',
  },
];

function ColonyOptions({
  opts,
  canWrite,
  onOptions,
}: {
  opts: NonNullable<StoryColonyState['options']>;
  canWrite: boolean;
  onOptions: (o: ColonyOptionsChange) => void;
}) {
  return (
    <div className="space-y-1" data-testid="colony-options">
      {COLONY_FLAGS.map((f) => (
        <label key={f.key} className="flex items-center gap-2" title={f.title}>
          <input
            type="checkbox"
            disabled={!canWrite}
            checked={opts[f.key]}
            onChange={(e) => onOptions({ [f.key]: e.target.checked })}
          />
          {f.label}
        </label>
      ))}
      <label
        className="flex items-center gap-2"
        title="With nothing to do, in free hours and while resting after failures: pastimes (wander, socialise, stargaze, sit, the garden, the sunset), strolls only, or stay put"
      >
        Idle pawns
        <select
          aria-label="Idle pawns"
          data-testid="colony-idle"
          disabled={!canWrite}
          value={opts.idle ?? 'recreation'}
          onChange={(e) => onOptions({ idle: e.target.value as StoryIdleMode })}
          className="rounded border border-border bg-surface px-1 py-0.5"
        >
          <option value="recreation">recreation</option>
          <option value="wander">wander</option>
          <option value="stay">stay put</option>
        </select>
      </label>
    </div>
  );
}

/** " · server: N seats free, M kept for regulars" when the server's cap is known. */
function capText(seats: NonNullable<StoryColonyState['seats']>): string {
  if (seats.capFree === null || seats.capFree === undefined) return '';
  return ` · server: ${seats.capFree} seat${seats.capFree === 1 ? '' : 's'} free, ${seats.keptForRegulars ?? 0} kept for regulars`;
}

/**
 * "8 of 20 pawns in game — server full (20 slots, 2 kept for poofimafox and kiwi) · memory: room for 9 more": a colony has no
 * upper limit, so the line says which real limit keeps the rest out (or the free seats when nothing does).
 */
function SeatsLine({ seats }: { seats: NonNullable<StoryColonyState['seats']> }) {
  const memory = seats.memoryRoom === null || seats.memoryRoom === undefined ? '' : ` · memory: room for ${seats.memoryRoom} more`;
  return (
    <div className="text-muted" data-testid="colony-seats">
      {seats.online} of {seats.wanted} pawns in game
      {seats.waitingReason ? (
        <>
          {' — '}
          <span className="text-warn" data-testid="colony-seats-reason">
            {seats.waitingReason}
          </span>
        </>
      ) : (
        capText(seats)
      )}
      {memory}
    </div>
  );
}

/** "Bring all online" while an active colony has pawns offline. */
function BringAllBox({
  state,
  blocked,
  canWrite,
  onBringAll,
  displayOf,
}: {
  state: StoryColonyState;
  blocked: readonly StoryOnlineBlocked[];
  canWrite: boolean;
  onBringAll: (overCap: boolean) => void;
  displayOf?: (account: string) => string;
}) {
  const offline = state.pawns.filter((p) => p.online === false || p.state === 'offline').length;
  if (state.active === false || offline <= 0) return null;
  return (
    <BringOnlineBox
      blocked={blocked}
      canWrite={canWrite}
      label={`Bring all online (${offline} offline)`}
      onBring={onBringAll}
      testid="colony-bring-online"
      names={displayOf}
    />
  );
}

function HomeBox({
  home,
  canWrite,
  centre,
  migrate,
  onPickHome,
  onMoveHome,
  onConfirmMove,
  onCancelMove,
}: {
  home: StoryColonyState['home'];
  canWrite: boolean;
  centre: readonly number[] | null;
  migrate: readonly number[] | null;
  onPickHome: () => void;
  onMoveHome: () => void;
  onConfirmMove: () => void;
  onCancelMove: () => void;
}) {
  return (
    <div className="space-y-1.5 rounded-md border border-border p-2" data-testid="colony-home">
      <div className="flex items-center gap-2">
        <span className="font-semibold">Home</span>
        <span className="text-muted tabular-nums">{home ? `${home[0]}, ${home[2]}` : 'not set'}</span>
        <Button
          size="sm"
          variant="ghost"
          className="ml-auto"
          disabled={!canWrite}
          onClick={onPickHome}
          title="Click the spot on the map"
        >
          <MapPin className="size-3.5" />
          Set home on the map
        </Button>
        <Button size="sm" disabled={!canWrite || !centre} onClick={onMoveHome}>
          <Home className="size-3.5" />
          Move home here (migrate)
        </Button>
      </div>
      {migrate ? (
        <MigrateConfirm migrate={migrate} onConfirm={onConfirmMove} onCancel={onCancelMove} />
      ) : (
        <p className="text-[10.5px] text-subtle">
          {centre ? `Here is ${centre[0]}, ${centre[2]}: the map centre.` : 'Here is the map centre.'} Pawns far from the
          new home are brought to it.
        </p>
      )}
    </div>
  );
}

function MigrateConfirm({
  migrate,
  onConfirm,
  onCancel,
}: {
  migrate: readonly number[];
  onConfirm: () => void;
  onCancel: () => void;
}) {
  return (
    <div
      className="space-y-1.5 rounded border border-accent bg-accent-soft p-2"
      role="alertdialog"
      aria-label="Migrate home"
      data-testid="migrate-confirm"
    >
      <p>
        Migrate home to{' '}
        <span className="font-semibold tabular-nums">
          {migrate[0]}, {migrate[2]}
        </span>
        ? Pawns far from it are brought over; zones, storage and builds stay where they are.
      </p>
      <div className="flex gap-2">
        <Button size="sm" variant="primary" onClick={onConfirm}>
          Migrate
        </Button>
        <Button size="sm" variant="ghost" onClick={onCancel}>
          Cancel
        </Button>
      </div>
    </div>
  );
}

/** The houses: home zones, any number, each named; the pawns sleep, eat and idle in them. */
function Houses({
  zones,
  canWrite,
  onDraw,
  onShow,
  onRename,
  onDelete,
}: {
  zones: StoryZone[];
  canWrite: boolean;
  onDraw: () => void;
  onShow: (z: StoryZone) => void;
  onRename: (z: StoryZone, label: string) => void;
  onDelete: (z: StoryZone) => void;
}) {
  const [editing, setEditing] = useState<{ id: string; text: string } | null>(null);
  return (
    <div className="space-y-1.5 rounded-md border border-border p-2" data-testid="houses">
      <div className="flex items-center gap-2">
        <span className="font-semibold">Houses</span>
        <span className="text-muted">home areas</span>
        <Button size="sm" variant="ghost" className="ml-auto" disabled={!canWrite} onClick={onDraw}>
          <Plus className="size-3.5" />
          Draw a house
        </Button>
      </div>
      {zones.length ? (
        <ul className="space-y-1">
          {zones.map((z, i) => {
            const name = z.name || z.label || `House ${i + 1}`;
            const id = String(z.id);
            return (
              <li key={id} className="flex flex-wrap items-center gap-1.5" data-zone={id}>
                {editing?.id === id ? (
                  <form
                    className="flex min-w-0 flex-1 gap-1"
                    onSubmit={(e) => {
                      e.preventDefault();
                      onRename(z, editing.text.trim());
                      setEditing(null);
                    }}
                  >
                    <Input
                      aria-label={`Name of ${name}`}
                      autoFocus
                      value={editing.text}
                      maxLength={40}
                      onChange={(e) => setEditing({ id, text: e.target.value })}
                      className="h-6 text-xs"
                    />
                    <Button size="sm" type="submit" className="h-6">
                      Save
                    </Button>
                  </form>
                ) : (
                  <>
                    <button
                      type="button"
                      className="min-w-0 flex-1 truncate text-left font-medium hover:underline"
                      onClick={() => onShow(z)}
                      title="Show on the map"
                    >
                      {name}
                    </button>
                    <span className="text-subtle tabular-nums" data-testid="zone-cells">
                      {zoneCellCount(z)} cells
                    </span>
                    <Button
                      size="sm"
                      variant="ghost"
                      className="h-6 px-1.5"
                      disabled={!canWrite}
                      onClick={() => setEditing({ id, text: z.name ?? z.label ?? '' })}
                      aria-label={`Rename ${name}`}
                    >
                      <Pencil className="size-3.5" />
                    </Button>
                    <Button
                      size="sm"
                      variant="ghost"
                      className="h-6 px-1.5 text-danger"
                      disabled={!canWrite}
                      onClick={() => onDelete(z)}
                      aria-label={`Delete ${name}`}
                    >
                      <Trash2 className="size-3.5" />
                    </Button>
                  </>
                )}
                <ZonePreserve z={z} />
              </li>
            );
          })}
        </ul>
      ) : (
        <p className="text-[10.5px] text-subtle">
          No houses yet: draw home areas where the pawns should sleep and rest.
        </p>
      )}
    </div>
  );
}

export function NewColony({
  canWrite,
  at,
  onCreate,
}: {
  canWrite: boolean;
  at: readonly number[] | null;
  onCreate: (name: string, pawns: number) => void;
}) {
  const [name, setName] = useState('Hollow');
  const [n, setN] = useState(4);
  return (
    <form
      className="space-y-2 rounded-md border border-border p-2"
      onSubmit={(e) => {
        e.preventDefault();
        if (name.trim()) onCreate(name.trim(), n);
      }}
      aria-label="Found a colony"
    >
      <div className="font-semibold">Found a colony</div>
      <div className="flex items-center gap-2">
        <Input
          aria-label="Colony name"
          value={name}
          maxLength={40}
          onChange={(e) => setName(e.target.value)}
          className="h-7 text-xs"
        />
        <div className="flex items-center gap-1 whitespace-nowrap" role="group" aria-label="Pawns">
          <span>Pawns</span>
          <Button
            type="button"
            size="sm"
            variant="ghost"
            className="h-7 w-7 px-0"
            aria-label="Fewer pawns"
            disabled={n <= 1}
            onClick={(e) => setN((v) => Math.max(1, v - (e.shiftKey ? 10 : 1)))}
          >
            −
          </Button>
          <output
            aria-label="Number of pawns"
            aria-live="polite"
            className="min-w-[2ch] text-center text-sm font-semibold tabular-nums text-fg"
          >
            {n}
          </output>
          {/* No upper limit (Fox 11-05): the server's seats and memory decide how many come now; the rest wait. Shift: ±10. */}
          <Button
            type="button"
            size="sm"
            variant="ghost"
            className="h-7 w-7 px-0"
            aria-label="More pawns"
            title="More pawns (Shift: 10 more)"
            onClick={(e) => setN((v) => v + (e.shiftKey ? 10 : 1))}
          >
            +
          </Button>
        </div>
      </div>
      <p className="text-[10.5px] text-muted" data-testid="settle-at">
        {at ? (
          <>
            Settles at{' '}
            <span className="font-semibold tabular-nums text-fg">
              {at[0]}, {at[2]}
            </span>
            : the map centre. Pan to settle elsewhere.
          </>
        ) : (
          'Settles at the map centre.'
        )}
      </p>
      <p className="text-[10.5px] text-subtle">
        Pawns are script-only bodies running the colony script. Any number: as many as the server&apos;s seats and memory allow come
        now, the rest join as room frees up.
      </p>
      <Button type="submit" size="sm" variant="primary" disabled={!canWrite}>
        Found colony
      </Button>
    </form>
  );
}
