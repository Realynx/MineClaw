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
  type StoryScheduleBlock,
  type StoryZone,
} from '@mineclaw/contracts';
import {
  ClipboardPaste,
  Copy,
  Flame,
  GripVertical,
  Home,
  Lock,
  MapPin,
  Pause,
  Pencil,
  Play,
  Plus,
  Trash2,
} from 'lucide-react';
import { useMemo, useRef, useState } from 'react';
import { Button, Input } from '@/components/ui/primitives';
import { cn } from '@/lib/utils';
import {
  applyPaint,
  cyclePriority,
  directiveStatusTone,
  nextSort,
  paintRequests,
  paintSpan,
  pasteRequest,
  SCHEDULE_BRUSHES,
  skillTint,
  sortPawns,
  WORK_LABELS,
  WORK_TYPES,
  type SortKey,
} from './model';
import { panel } from './StoryHud';
import { BringOnlineBox } from './StoryInspector';
import { DESIGNATE_TOOLS, FURNITURE_TOOLS, STRUCTURE_TOOLS, ZONE_TOOLS } from './tools';
import type { MapTool } from './StoryMap';
import { AiMark, Avatar, ItemIcon, ToolIcon } from './visuals';

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

type ArchCat = 'orders' | 'zones' | 'structure' | 'furniture' | 'production' | 'storage' | 'lighting' | 'misc';

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

export function ArchitectPanel({
  tool,
  onTool,
  palette,
  onPick,
}: {
  tool: MapTool;
  onTool: (t: MapTool) => void;
  palette?: StoryArchitectResponse;
  onPick?: (e: StoryArchitectEntry) => void;
}) {
  const [cat, setCat] = useState<ArchCat>(
    tool.kind === 'zone'
      ? 'zones'
      : tool.kind === 'build'
        ? tool.id.startsWith('furniture:')
          ? 'furniture'
          : 'structure'
        : tool.kind === 'storage'
          ? 'storage'
          : 'orders',
  );
  const cats: Array<[ArchCat, string]> = [
    ['orders', 'Orders'],
    ['zones', 'Zones'],
    ['structure', 'Structure'],
    ['furniture', 'Furniture'],
    ['production', 'Production'],
    ['storage', 'Storage'],
    ['lighting', 'Lighting'],
    ['misc', 'Misc'],
  ];
  const active = (t: MapTool) => JSON.stringify(t) === JSON.stringify(tool);
  const btn = (t: MapTool, icon: string, label: string, hint: string, key?: string) => (
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
  return (
    <div className="flex items-end gap-2">
      <section
        className={cn(panel, 'pointer-events-auto flex w-[34rem] gap-2 p-2')}
        aria-label="Architect"
        data-testid="architect"
      >
        <div className="flex w-24 flex-col gap-0.5">
          {cats.map(([id, label]) => (
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
          {cat === 'orders'
            ? DESIGNATE_TOOLS.map((t) =>
                btn({ kind: 'designate', id: t.id }, t.id, t.label, t.hint, t.key.replace('Key', '')),
              )
            : null}
          {cat === 'zones'
            ? [
                ...ZONE_TOOLS.map((z) => btn({ kind: 'zone', id: z.id, op: 'add' }, 'zone', z.label, z.hint)),
                btn(
                  { kind: 'zone', id: 'home', op: 'remove' },
                  'cancel',
                  'Remove zone',
                  'Drag over a zone to remove it',
                ),
              ]
            : null}
          {cat === 'structure'
            ? STRUCTURE_TOOLS.map((s) => btn({ kind: 'build', id: s.id }, s.id, s.label, s.hint))
            : null}
          {cat === 'furniture'
            ? FURNITURE_TOOLS.map((f) =>
                btn(
                  { kind: 'build', id: `furniture:${f.id}` },
                  f.id,
                  f.label,
                  `Place a ${f.label.toLowerCase()} (Q/E turns)`,
                ),
              )
            : null}
          {cat === 'storage' ? (
            <>
              {btn(
                { kind: 'storage' },
                'storage',
                'Register chests',
                'Drag over chests: every chest in the area becomes storage',
              )}
              <p className="w-56 text-[11px] leading-snug text-muted">
                Loot lives in chests. Drag over chests to register them, then click one (or box-select several) to
                choose what it keeps and how much it matters.
              </p>
            </>
          ) : null}
          {(cat === 'production' || cat === 'lighting' || cat === 'misc') && !palette ? (
            <p className="w-56 text-[11px] leading-snug text-muted">Loading what the colony can make…</p>
          ) : null}
        </div>
      </section>
      {palette && onPick && cat !== 'orders' && cat !== 'zones' ? (
        <ArchitectPalette entries={palette.entries} tab={cat} onPick={onPick} />
      ) : null}
    </div>
  );
}

// ------------------------------------------------------------------ work

export function WorkPanel({
  pawns,
  ai,
  canWrite,
  onSet,
}: {
  pawns: StoryPawn[];
  ai: StoryAiBody[];
  canWrite: boolean;
  onSet: (pawns: string[], work: Record<string, number>) => void;
}) {
  const [sort, setSort] = useState<SortKey>(null);
  const [local, setLocal] = useState<Record<string, Record<string, number>>>({});
  const rows = useMemo(() => sortPawns(pawns, sort), [pawns, sort]);
  const value = (p: StoryPawn, t: string) => local[p.name]?.[t] ?? p.priorities[t] ?? 0;
  const set = (p: StoryPawn, t: string, back: boolean) => {
    if (!canWrite) return;
    const v = cyclePriority(value(p, t), back);
    setLocal((l) => ({ ...l, [p.name]: { ...l[p.name], [t]: v } }));
    onSet([p.name], { [t]: v });
  };
  return (
    <section
      className={cn(panel, 'pointer-events-auto max-h-[46vh] overflow-auto p-2')}
      aria-label="Work priorities"
      data-testid="work-panel"
    >
      <table className="border-separate border-spacing-0 text-xs">
        <thead>
          <tr>
            <th className="sticky top-0 bg-surface px-2 text-left">
              <button type="button" onClick={() => setSort(nextSort(sort, 'name'))} className="font-semibold">
                Pawn
              </button>
            </th>
            {WORK_TYPES.map((t) => (
              <th key={t} className="sticky top-0 bg-surface px-0.5 pb-1">
                <button
                  type="button"
                  onClick={() => setSort(nextSort(sort, t))}
                  className={cn(
                    'w-11 rounded py-0.5 text-[11px] font-semibold',
                    sort?.col === t ? 'bg-accent-soft' : 'text-muted hover:text-fg',
                  )}
                  title={`Sort by ${WORK_LABELS[t]} skill`}
                >
                  {WORK_LABELS[t]}
                  {sort?.col === t ? (sort.dir === 1 ? ' ▾' : ' ▴') : ''}
                </button>
              </th>
            ))}
          </tr>
        </thead>
        <tbody>
          {rows.map((p) => (
            <tr key={p.name} data-row={p.name}>
              <td className="pr-2">
                <span className="flex items-center gap-1.5">
                  <Avatar name={p.name} size={18} />
                  <span className="font-semibold">{p.display}</span>
                </span>
              </td>
              {WORK_TYPES.map((t) => {
                const v = value(p, t);
                const sk = p.skills[t];
                return (
                  <td key={t} className="p-0.5">
                    <button
                      type="button"
                      data-cell={`${p.name}:${t}`}
                      aria-label={`${p.display} ${WORK_LABELS[t]}: ${v || 'off'} (skill ${sk?.level ?? 0}${sk?.passion ? ', passion' : ''})`}
                      onClick={(e) => set(p, t, e.shiftKey)}
                      onContextMenu={(e) => {
                        e.preventDefault();
                        set(p, t, true);
                      }}
                      disabled={!canWrite}
                      className="relative h-7 w-11 rounded border border-border text-[13px] font-bold tabular-nums"
                      style={{
                        background: `color-mix(in oklab, var(--accent) ${Math.round(skillTint(sk?.level) * 42)}%, var(--surface-2))`,
                      }}
                      title={`Skill ${sk?.level ?? 0}/20${sk?.passion ? ' · loves this work' : ''} · click: 1 → 4 → off`}
                    >
                      <span className={cn(v === 1 ? 'text-fg' : v === 0 ? 'text-subtle' : 'text-muted')}>
                        {v || '·'}
                      </span>
                      {sk?.passion ? (
                        <Flame className="absolute top-0.5 right-0.5 size-2.5 text-[#ff9f43]" aria-hidden />
                      ) : null}
                      <span className="absolute bottom-0.5 left-1 text-[8px] font-medium text-subtle">
                        {sk?.level ?? 0}
                      </span>
                    </button>
                  </td>
                );
              })}
            </tr>
          ))}
          {ai.map((a) => (
            <tr key={a.name} className="opacity-60" data-ai-row={a.name}>
              <td className="pr-2">
                <span className="flex items-center gap-1.5">
                  <Avatar name={a.name} size={18} ai />
                  <span className="font-semibold">{a.name}</span>
                </span>
              </td>
              <td colSpan={WORK_TYPES.length} className="px-2 text-[11px] text-muted italic">
                <AiMark className="mr-1 inline size-3 text-[#a78bfa]" />
                AI-managed: send it directives instead
              </td>
            </tr>
          ))}
        </tbody>
      </table>
      <p className="mt-1 px-1 text-[10.5px] text-subtle">
        1 is done first; ties go left to right, then to skill. Right-click or Shift-click counts down.
      </p>
    </section>
  );
}

// ------------------------------------------------------------------ schedule

const BLOCK_COLOR: Record<string, string> = { work: '#d9a441', sleep: '#4d6fb8', anything: '#6f7b8a', free: '#5bb07a' };

export function SchedulePanel({
  pawns,
  ai,
  hour,
  canWrite,
  onPaint,
}: {
  pawns: StoryPawn[];
  ai: StoryAiBody[];
  hour: number | null | undefined;
  canWrite: boolean;
  onPaint: (req: Array<{ pawns: string[]; hours: Record<string, StoryScheduleBlock> }>) => void;
}) {
  const [brush, setBrush] = useState<StoryScheduleBlock>('work');
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

export function QueuePanel({
  builds,
  canWrite,
  onOp,
}: {
  builds: StoryBuild[];
  canWrite: boolean;
  onOp: (op: 'remove' | 'pause' | 'resume' | 'reorder', ids: Array<string | number>) => void;
}) {
  const list = [...builds].filter((b) => b.status !== 'done').sort((a, b) => a.order - b.order);
  const done = builds.filter((b) => b.status === 'done').length;
  const dragId = useRef<string | number | null>(null);
  const [over, setOver] = useState<string | number | null>(null);
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
            onDrop={() => {
              setOver(null);
              const from = dragId.current;
              dragId.current = null;
              if (from === null || from === undefined || from === b.id) return;
              const ids = list.map((x) => x.id).filter((x) => x !== from);
              ids.splice(ids.indexOf(b.id), 0, from);
              onOp('reorder', ids);
            }}
            className={cn(
              'flex items-center gap-2 rounded-md border border-border bg-surface-2 px-2 py-1.5 text-xs',
              over === b.id && 'border-t-2 border-t-[#f5c542]',
            )}
          >
            <GripVertical className="size-3.5 cursor-grab text-subtle" aria-hidden />
            <span className="w-4 text-right text-subtle tabular-nums">{i + 1}</span>
            <ToolIcon id={b.kind === 'furniture' ? 'block' : b.kind} className="size-3.5 text-muted" />
            <div className="min-w-0 flex-1">
              <div className="truncate font-semibold">{b.label}</div>
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
              {b.reason || b.status === 'blocked' ? (
                <div className={cn('text-[10.5px]', b.status === 'waiting' ? 'text-muted' : 'text-danger')}>
                  {b.reason ?? 'blocked'}
                </div>
              ) : null}
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
              className={cn(
                'rounded px-1 text-[10px] font-semibold uppercase',
                b.status === 'active'
                  ? 'bg-ok-soft text-ok'
                  : b.status === 'paused'
                    ? 'bg-warn-soft text-warn'
                    : b.status === 'blocked'
                      ? 'bg-danger-soft text-danger'
                      : 'bg-surface-3 text-muted',
              )}
            >
              {b.status}
            </span>
            <button
              type="button"
              disabled={!canWrite || i === 0}
              onClick={() => onOp('reorder', [b.id, ...list.filter((x) => x.id !== b.id).map((x) => x.id)])}
              className="rounded px-1 text-[10px] font-semibold text-muted hover:bg-surface-3 disabled:opacity-30"
              title="Build first"
              aria-label={`Build ${b.label} first`}
            >
              Top
            </button>
            <button
              type="button"
              disabled={!canWrite}
              onClick={() => onOp(b.status === 'paused' ? 'resume' : 'pause', [b.id])}
              className="rounded p-0.5 hover:bg-surface-3"
              aria-label={b.status === 'paused' ? `Resume ${b.label}` : `Pause ${b.label}`}
            >
              {b.status === 'paused' ? <Play className="size-3.5" /> : <Pause className="size-3.5" />}
            </button>
            <button
              type="button"
              disabled={!canWrite}
              onClick={() => onOp('remove', [b.id])}
              className="rounded p-0.5 text-danger hover:bg-danger-soft"
              aria-label={`Cancel ${b.label}`}
            >
              <Trash2 className="size-3.5" />
            </button>
          </li>
        ))}
      </ol>
      {done ? <p className="mt-1 px-1 text-[10.5px] text-subtle">{done} finished</p> : null}
    </section>
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
  colonies: Array<{ id: string; name: string; pawns: string[]; paused?: boolean | null }>;
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
  onOptions?: (o: { autoPlaceBenches?: boolean; detectBenches?: boolean }) => void;
}) {
  const offline = (state?.pawns ?? []).filter((p) => p.online === false || p.state === 'offline').length;
  const opts = state?.options;
  const seats = state?.seats;
  return (
    <section
      className={cn(panel, 'pointer-events-auto w-[26rem] space-y-3 p-3 text-xs')}
      aria-label="Colony"
      data-testid="colony-panel"
    >
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
      {state ? (
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
      ) : null}
      {state && opts && onOptions ? (
        <div className="space-y-1" data-testid="colony-options">
          <label
            className="flex items-center gap-2"
            title="A build that needs a crafting table or furnace the colony hasn't got: a pawn crafts one and places it near home"
          >
            <input
              type="checkbox"
              disabled={!canWrite}
              checked={opts.autoPlaceBenches}
              onChange={(e) => onOptions({ autoPlaceBenches: e.target.checked })}
            />
            Builds may place the benches they need
          </label>
          <label
            className="flex items-center gap-2"
            title="Off: only benches you designate (inspector: Use as colony workbench) are used, never another player's"
          >
            <input
              type="checkbox"
              disabled={!canWrite}
              checked={opts.detectBenches}
              onChange={(e) => onOptions({ detectBenches: e.target.checked })}
            />
            Use any bench near home (no designation)
          </label>
        </div>
      ) : null}
      {state && seats ? (
        <div className="text-muted" data-testid="colony-seats">
          {seats.online} of {seats.wanted} pawns in game
          {seats.capFree !== null && seats.capFree !== undefined
            ? ` · server: ${seats.capFree} seat${seats.capFree === 1 ? '' : 's'} free, ${seats.keptForRegulars ?? 0} kept for regulars`
            : ''}
        </div>
      ) : null}
      {state && state.active !== false && offline > 0 && onBringAll ? (
        <BringOnlineBox
          blocked={blocked}
          canWrite={canWrite}
          label={`Bring all online (${offline} offline)`}
          onBring={onBringAll}
          testid="colony-bring-online"
          names={displayOf}
        />
      ) : null}
      {state ? (
        <div className="space-y-1.5 rounded-md border border-border p-2" data-testid="colony-home">
          <div className="flex items-center gap-2">
            <span className="font-semibold">Home</span>
            <span className="text-muted tabular-nums">
              {state.home ? `${state.home[0]}, ${state.home[2]}` : 'not set'}
            </span>
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
                <Button size="sm" variant="primary" onClick={onConfirmMove}>
                  Migrate
                </Button>
                <Button size="sm" variant="ghost" onClick={onCancelMove}>
                  Cancel
                </Button>
              </div>
            </div>
          ) : (
            <p className="text-[10.5px] text-subtle">
              {centre ? `Here is ${centre[0]}, ${centre[2]}: the map centre.` : 'Here is the map centre.'} Pawns far
              from the new home are brought to it.
            </p>
          )}
        </div>
      ) : null}
      {state ? (
        <Houses
          zones={state.zones.filter((z) => z.kind === 'home')}
          canWrite={canWrite}
          onDraw={onDrawHouse}
          onShow={onShowZone}
          onRename={onRenameZone}
          onDelete={onDeleteZone}
        />
      ) : null}
      <NewColony canWrite={canWrite} at={centre} onCreate={onCreate} />
    </section>
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
              <li key={id} className="flex items-center gap-1.5" data-zone={id}>
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
                    <span className="text-subtle tabular-nums">
                      {Math.abs(z.area[3] - z.area[0]) + 1} × {Math.abs(z.area[5] - z.area[2]) + 1}
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
            onClick={() => setN((v) => Math.max(1, v - 1))}
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
          <Button
            type="button"
            size="sm"
            variant="ghost"
            className="h-7 w-7 px-0"
            aria-label="More pawns"
            disabled={n >= 12}
            onClick={() => setN((v) => Math.min(12, v + 1))}
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
      <p className="text-[10.5px] text-subtle">Pawns are script-only bodies running the colony script.</p>
      <Button type="submit" size="sm" variant="primary" disabled={!canWrite}>
        Found colony
      </Button>
    </form>
  );
}
