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
  type StoryBuild,
  type StoryBuildMaterial,
  type StoryColonyState,
  type StoryDirective,
  type StoryOnlineBlocked,
  type StoryPawn,
  type StoryIdleMode,
  type StoryScheduleBlock,
  type StoryZone,
} from '@mineclaw/contracts';
import { ZonePreserve } from './zones/PreservePanel';
import { buildTitle, isRepairBuild } from './zones/preserve';
import { buildGroups, reorderBuildGroup, type BuildGroup } from './buildGroups';
import { zoneCellCount } from './zones/zonePaint';
import {
  ClipboardPaste,
  Copy,
  GripVertical,
  Home,
  MapPin,
  Pause,
  Pencil,
  Play,
  Plus,
  ShieldCheck,
  Trash2,
} from 'lucide-react';
import { useRef, useState } from 'react';
import { Button, Input } from '@/components/ui/primitives';
import { cn } from '@/lib/utils';
import { applyPaint, directiveStatusTone, paintRequests, paintSpan, pasteRequest, SCHEDULE_BRUSHES } from './model';
import { useWorldClock, type WorldClock } from './clock';
import { panel } from './StoryHud';
import { BringOnlineBox } from './StoryInspector';
import { AiMark, ToolIcon } from './visuals';

export type TabId = 'architect' | 'work' | 'schedule' | 'assign' | 'queue' | 'directives' | 'colony' | 'inventory';

export const TABS: ReadonlyArray<{ id: TabId; label: string; key: string }> = [
  { id: 'architect', label: 'Architect', key: 'B' },
  { id: 'work', label: 'Work', key: 'P' },
  { id: 'schedule', label: 'Schedule', key: 'Y' },
  { id: 'assign', label: 'Assign', key: '' },
  { id: 'queue', label: 'Build queue', key: 'U' },
  { id: 'directives', label: 'Directives', key: 'O' },
  { id: 'colony', label: 'Colony', key: 'I' },
  { id: 'inventory', label: 'Inventory', key: '' },
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

export { ArchitectPanel, ArchitectPalette, stockLine } from './ArchitectPanel';

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
  const groups = buildGroups(builds);
  const list = groups.filter((g) => g.active.length);
  const active = list.flatMap((g) => g.active);
  const done = groups.filter((g) => !g.active.length && !isRepairBuild(g.builds[0])).length;
  const dragId = useRef<string | null>(null);
  const [over, setOver] = useState<string | null>(null);
  const drop = (onto: string) => {
    setOver(null);
    const from = dragId.current;
    dragId.current = null;
    if (!canWrite || from === null) return;
    const ids = reorderBuildGroup(list, from, onto);
    if (ids.length) onOp('reorder', ids);
  };
  return (
    <section
      className={cn(panel, 'pointer-events-auto max-h-[55vh] w-[34rem] max-w-full overflow-auto p-2')}
      aria-label="Build queue"
      data-testid="queue-panel"
    >
      {list.length === 0 ? (
        <p className="p-2 text-xs text-muted">Nothing queued. Pick a structure in the Architect and drag on the map.</p>
      ) : null}
      <ol className="space-y-1">
        {list.map((g, i) => (
          <li
            key={g.key}
            data-build={g.grouped ? undefined : g.builds[0]!.id}
            data-build-group={g.grouped ? g.key : undefined}
            draggable={canWrite}
            onDragStart={() => (dragId.current = g.key)}
            onDragEnd={() => {
              dragId.current = null;
              setOver(null);
            }}
            onDragOver={(e) => {
              e.preventDefault();
              setOver(g.key);
            }}
            onDragLeave={() => setOver((o) => (o === g.key ? null : o))}
            onDrop={(e) => {
              e.preventDefault();
              drop(g.key);
            }}
            className={cn(
              'rounded-md border border-border bg-surface-2 px-2 py-1.5 text-xs',
              over === g.key && 'border-t-2 border-t-[#f5c542]',
            )}
          >
            {g.grouped ? (
              <QueueGroup
                group={g}
                index={i}
                canWrite={canWrite}
                onOp={onOp}
                failed={failed}
                onRetry={onRetry}
                others={active}
              />
            ) : (
              <div>
                <div className="mb-1 text-[9px] tracking-wide text-subtle uppercase">
                  {isRepairBuild(g.builds[0]) ? 'Zone maintenance' : 'Manual order'}
                </div>
                <div className="flex items-center gap-2">
                  <QueueRow
                    b={g.active[0]!}
                    i={i}
                    canWrite={canWrite}
                    onOp={onOp}
                    failedCells={failed?.get(String(g.active[0]!.id)) ?? 0}
                    onRetry={onRetry}
                    others={active}
                  />
                </div>
              </div>
            )}
          </li>
        ))}
      </ol>
      {done ? <p className="mt-1 px-1 text-[10.5px] text-subtle">{done} finished</p> : null}
    </section>
  );
}

function QueueGroup({
  group: g,
  index,
  canWrite,
  onOp,
  failed,
  onRetry,
  others,
}: {
  group: BuildGroup;
  index: number;
  canWrite: boolean;
  onOp: QueueOp;
  failed?: ReadonlyMap<string, number>;
  onRetry?: (id: string | number) => void;
  others: readonly StoryBuild[];
}) {
  const [expanded, setExpanded] = useState(false);
  const ids = g.active.map((b) => b.id);
  const paused = g.active.every((b) => b.status === 'paused');
  const done = g.builds.reduce((n, b) => n + b.blocksDone, 0);
  const total = g.builds.reduce((n, b) => n + b.blocksTotal, 0);
  const failures = g.active.reduce((n, b) => n + (failed?.get(String(b.id)) ?? 0), 0);
  const problems = [...new Set(g.active.map((b) => b.reason).filter(Boolean))];
  const label = `${g.title} · ${g.builds[0]!.id}`;
  return (
    <>
      <div className="flex items-center gap-2">
        <GripVertical className="size-3.5 shrink-0 cursor-grab text-subtle" />
        <button
          type="button"
          className="min-w-0 flex-1 text-left"
          aria-expanded={expanded}
          onClick={() => setExpanded(!expanded)}
        >
          <span className="text-[9px] tracking-wide text-subtle uppercase">Design / building instance</span>
          <span className="block truncate font-semibold">
            {expanded ? '▾' : '▸'} {label}
          </span>
          <span className="block text-[10.5px] text-muted">
            {done}/{total} blocks · {g.active.length} remaining parts
          </span>
        </button>
        <button
          type="button"
          disabled={!canWrite || index === 0}
          className="rounded px-1 text-[10px] text-muted disabled:opacity-30"
          aria-label={`Build ${label} first`}
          onClick={() => onOp('reorder', [...ids, ...others.filter((b) => !ids.includes(b.id)).map((b) => b.id)])}
        >
          Top
        </button>
        <button
          type="button"
          disabled={!canWrite}
          aria-label={`${paused ? 'Resume' : 'Pause'} ${label}`}
          onClick={() => onOp(paused ? 'resume' : 'pause', ids)}
        >
          {paused ? <Play className="size-3.5" /> : <Pause className="size-3.5" />}
        </button>
        <button
          type="button"
          disabled={!canWrite}
          className="text-danger"
          aria-label={`Cancel ${label}`}
          onClick={() =>
            onOp(
              'remove',
              g.builds.map((b) => b.id),
            )
          }
        >
          <Trash2 className="size-3.5" />
        </button>
      </div>
      <div className="mt-1 h-1.5 overflow-hidden rounded-full bg-surface-3">
        <div className="h-full bg-accent" style={{ width: `${total ? Math.min(100, (done / total) * 100) : 0}%` }} />
      </div>
      {paused ? <p className="mt-1 text-[10px] text-warn">Paused</p> : null}
      {problems.slice(0, expanded ? undefined : 2).map((reason) => (
        <p key={reason} className="mt-1 text-[10px] text-danger">
          {reason}
        </p>
      ))}
      {failures && onRetry ? (
        <button
          type="button"
          disabled={!canWrite}
          className="mt-1 text-[10px] text-accent"
          onClick={() => g.active.filter((b) => failed?.get(String(b.id))).forEach((b) => onRetry(b.id))}
        >
          Retry failed cells ({failures})
        </button>
      ) : null}
      {expanded ? (
        <ol className="mt-2 space-y-1 border-l border-border pl-2">
          {g.builds.map((b, i) => (
            <li key={b.id} data-build={b.id} className="flex items-center gap-2 rounded border border-border p-1.5">
              <QueueRow
                b={b}
                i={i}
                canWrite={canWrite}
                onOp={onOp}
                failedCells={failed?.get(String(b.id)) ?? 0}
                onRetry={onRetry}
                others={others}
                grouped
              />
            </li>
          ))}
        </ol>
      ) : null}
    </>
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
  grouped = false,
}: {
  b: StoryBuild;
  i: number;
  canWrite: boolean;
  onOp: QueueOp;
  failedCells: number;
  onRetry?: (id: string | number) => void;
  others: readonly StoryBuild[];
  grouped?: boolean;
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
        className={cn(
          'rounded px-1 text-[10px] font-semibold uppercase',
          STATUS_TONE[b.status] ?? 'bg-surface-3 text-muted',
        )}
      >
        {b.status}
      </span>
      {!grouped ? (
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
      ) : null}
      <button
        type="button"
        disabled={!canWrite || b.status === 'done'}
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
          <ColonyActions
            state={state}
            canWrite={canWrite}
            onPause={onPause}
            onRelease={onRelease}
            onResume={onResume}
          />
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

type ColonyFlag =
  'autoPlaceBenches' | 'detectBenches' | 'unfilteredStorageTakesAnything' | 'keepDisallowed' | 'rescueTeleport';

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
  const memory =
    seats.memoryRoom === null || seats.memoryRoom === undefined ? '' : ` · memory: room for ${seats.memoryRoom} more`;
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
          {centre ? `Here is ${centre[0]}, ${centre[2]}: the map centre.` : 'Here is the map centre.'} Pawns far from
          the new home are brought to it.
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
        Pawns are script-only bodies running the colony script. Any number: as many as the server&apos;s seats and
        memory allow come now, the rest join as room frees up.
      </p>
      <Button type="submit" size="sm" variant="primary" disabled={!canWrite}>
        Found colony
      </Button>
    </form>
  );
}
