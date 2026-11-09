'use client';

/**
 * The bottom-left inspector for the selection: a pawn (state, job and queue, needs, skills, traits), several bodies, an AI
 * body (task, brain, its directives), chests (the Storage tab: priority, the loot tree with tri-state checks, search, allow
 * all / clear, copy / paste, label, contents) or a workbench (the Bills tab: add from the recipe picker, reorder, pause,
 * delete, and the bill dialog).
 */
import {
  regularsSeatsWouldHelp,
  STORY_STORAGE_PRIORITIES,
  type StoryAiBody,
  type StoryDesignation,
  type StoryOnlineBlocked,
  targetPos,
  type StoryBed,
  type StoryBill,
  type StoryColonyState,
  type StoryOrder,
  type StoryFilter,
  type StoryPawn,
  type StoryRecipe,
  type StoryReservation,
  type StoryStorage,
  type StoryWorkbench,
} from '@mineclaw/contracts';
import {
  BedDouble,
  ChevronDown,
  ChevronRight,
  ClipboardPaste,
  Copy,
  Flame,
  MessageSquare,
  Minus,
  Plus,
  Power,
  RotateCcw,
  Search,
  Shield,
  ShieldOff,
  Trash2,
  WifiOff,
  X,
} from 'lucide-react';
import { useMemo, useRef, useState, type ReactNode } from 'react';
import { Dialog } from '@/components/ui/overlays';
import { Button, Input } from '@/components/ui/primitives';
import { errorText } from '@/lib/api/client';
import { cn } from '@/lib/utils';
import { ContainerContents } from '@/features/world/ContainerPanel';
import { InventorySection } from '@/features/live/inventory/InventoryPanel';
import { benchTaskLine } from './CraftNeeds';
import { retryIn, shortReason } from './failedLabels';
import { BillRow } from './inspector/BillRow';
import { BillPrioritySelect } from './inspector/BillPrioritySelect';
import { StorageEntryActions, StorageSigns } from './inspector/StorageSigns';
import { WORK_LABELS, WORK_TYPES } from './model';
import { panel } from './StoryHud';
import { DirectiveList } from './StoryPanels';
import {
  addBillBody,
  allowAll,
  billSummary,
  FUEL_CHOICES,
  fuelBody,
  fuelLabel,
  fuelTable,
  reservationLine,
  reservationRows,
  smeltingLine,
  enchantLabel,
  recipeModes,
  requiresLine,
  stationLines,
  buildTree,
  CLEAR_FILTER,
  draftOf,
  filterSummary,
  itemAllowed,
  newDraft,
  nodeState,
  PRIORITY_LABELS,
  recipeLine,
  reorderBills,
  searchRecipes,
  searchTree,
  storageSetBody,
  storageEntriesBody,
  toggleItem,
  toggleNode,
  updateBillBody,
  type BillDraft,
  type StorageClip,
  type TreeNode,
} from './storage';
import { useRecipes, type StoryWrite } from './useStory';
import { AiMark, Avatar, ItemIcon } from './visuals';

type V3 = [number, number, number];

function orderWhere(t: StoryOrder['target']): string {
  const p = targetPos(t);
  if (p) return p.map((n) => Math.floor(n)).join(', ');
  return t && typeof t === 'object' && !Array.isArray(t) && t.entity !== null && t.entity !== undefined
    ? `#${t.entity}`
    : '';
}
const k3 = (p: readonly number[]) => `${p[0]},${p[1]},${p[2]}`;

function Bar({ value, color, label }: { value: number; color: string; label: string }) {
  return (
    <div className="flex items-center gap-1.5 text-[11px]">
      <span className="w-10 text-muted">{label}</span>
      <span className="block h-2 flex-1 overflow-hidden rounded-full bg-surface-3">
        <span
          className="block h-full"
          style={{ width: `${Math.max(0, Math.min(1, value / 20)) * 100}%`, background: color }}
        />
      </span>
      <span className="w-8 text-right tabular-nums">{Math.round(value)}/20</span>
    </div>
  );
}

function Shell({
  title,
  sub,
  onClose,
  children,
  avatar,
  testid,
  wide,
}: {
  title: string;
  sub?: string;
  onClose: () => void;
  children: React.ReactNode;
  avatar?: React.ReactNode;
  testid?: string;
  /** Chests and stations: wide and tall enough for a whole 9-column container grid (a double chest's 54 slots) without
   * scroll bars; the grid's slots shrink to fit a narrow window instead. */
  wide?: boolean;
}) {
  return (
    <section
      className={cn(
        panel,
        'pointer-events-auto flex flex-col',
        wide ? 'max-h-[calc(100vh-9rem)] w-[24rem] max-w-[calc(100vw-1rem)]' : 'max-h-[52vh] w-[21rem]',
      )}
      aria-label={`Inspect ${title}`}
      data-testid={testid ?? 'inspector'}
    >
      <header className="flex items-center gap-2 border-b border-border px-2.5 py-2">
        {avatar}
        <div className="min-w-0 flex-1">
          <div className="truncate text-sm font-semibold">{title}</div>
          {sub ? <div className="truncate text-[11px] text-muted">{sub}</div> : null}
        </div>
        <button
          type="button"
          onClick={onClose}
          aria-label="Close the inspector"
          className="rounded p-0.5 hover:bg-surface-2"
        >
          <X className="size-4" />
        </button>
      </header>
      <div
        className="min-h-0 flex-1 overflow-auto p-2.5"
        style={
          wide
            ? ({ containerType: 'inline-size', '--slot': 'min(36px, calc((100cqi - 16px) / 9))' } as React.CSSProperties)
            : undefined
        }
      >
        {children}
      </div>
    </section>
  );
}

function TabRow<T extends string>({
  tabs,
  value,
  onChange,
}: {
  tabs: Array<[T, string]>;
  value: T;
  onChange: (t: T) => void;
}) {
  return (
    <div className="mb-2 flex gap-0.5 border-b border-border text-xs" role="tablist">
      {tabs.map(([id, label]) => (
        <button
          key={id}
          type="button"
          role="tab"
          aria-selected={value === id}
          onClick={() => onChange(id)}
          className={cn(
            '-mb-px border-b-2 px-2 py-1 font-semibold',
            value === id ? 'border-accent text-fg' : 'border-transparent text-muted hover:text-fg',
          )}
        >
          {label}
        </button>
      ))}
    </div>
  );
}

// ------------------------------------------------------------------ offline pawns

/**
 * "Bring online" for one offline pawn or all of them: why they're out, the button, and when the last try was refused by the
 * player cap, the reason and an explicit secondary "Use regulars' seats" (it can lock a regular out until a pawn leaves).
 */
export function BringOnlineBox({
  reason,
  blocked,
  canWrite,
  label,
  onBring,
  testid = 'bring-online',
  names,
}: {
  /** Why it is offline (one pawn), or null for the colony's box. */
  reason?: string | null;
  /** The refusals of the last try (for these pawns). */
  blocked: readonly StoryOnlineBlocked[];
  canWrite: boolean;
  label: string;
  onBring: (overCap: boolean) => void;
  testid?: string;
  /** Account → display name for the refusals. */
  names?: (account: string) => string;
}) {
  const overHelps = blocked.some((b) => regularsSeatsWouldHelp(b.reason));
  return (
    <div className="space-y-1.5 rounded-md border border-border bg-surface-2 px-2 py-1.5 text-xs" data-testid={testid}>
      {reason ? (
        <div className="flex items-start gap-1.5">
          <WifiOff className="mt-0.5 size-3.5 shrink-0 text-muted" aria-hidden />
          <div className="min-w-0">
            <span className="font-semibold">Offline:</span> <span data-testid="pawn-offline-reason">{reason}</span>
          </div>
        </div>
      ) : null}
      {canWrite ? (
        <Button size="sm" onClick={() => onBring(false)}>
          <Power className="size-3.5" />
          {label}
        </Button>
      ) : (
        <p className="text-muted">Bringing pawns online needs the story role.</p>
      )}
      {blocked.length ? (
        <ul className="space-y-0.5 text-warn" data-testid="online-blocked">
          {blocked.map((b) => (
            <li key={b.pawn}>
              {blocked.length > 1 || !reason ? `${names?.(b.pawn) ?? b.pawn}: ` : 'Still out: '}
              {b.reason}
            </li>
          ))}
        </ul>
      ) : null}
      {canWrite && overHelps ? (
        <div className="space-y-0.5">
          <Button size="sm" variant="ghost" onClick={() => onBring(true)} data-testid="use-regulars-seats">
            Use regulars&apos; seats
          </Button>
          <p className="text-[10.5px] text-muted">A regular may then find the server full until a pawn leaves.</p>
        </div>
      ) : null}
    </div>
  );
}

// ------------------------------------------------------------------ a failed designation

/** A designation that keeps failing: why (in full), how often, when it is tried again; Retry now / Cancel. */
export function DesignationInspector({
  designation: d,
  cluster,
  canWrite,
  onRetry,
  onCancel,
  onClose,
}: {
  designation: StoryDesignation;
  /** The other designations its map label stands for (same reason, adjacent), this one included. */
  cluster: ReadonlyArray<string | number>;
  canWrite: boolean;
  onRetry: (ids: Array<string | number>) => void;
  onCancel: (ids: Array<string | number>) => void;
  onClose: () => void;
}) {
  const many = cluster.length > 1;
  return (
    <Shell
      title={designationTitle(d)}
      sub={designationSub(d)}
      onClose={onClose}
      testid="designation-inspector"
    >
      <div className="space-y-2 text-xs">
        {d.wait ? (
          <div className="rounded-md border border-border bg-surface-2 px-2 py-1.5" data-testid="designation-wait">
            <div className="text-[10.5px] font-semibold tracking-wide text-subtle uppercase">Waiting (not a failure)</div>
            <div className="break-words">{d.wait}</div>
          </div>
        ) : null}
        <div className="rounded-md bg-surface-2 px-2 py-1.5">
          <div className="text-[10.5px] font-semibold tracking-wide text-subtle uppercase">Why it failed</div>
          <div className="break-words" data-testid="designation-reason">
            {d.reason ?? 'No reason given'}
          </div>
        </div>
        <dl className="grid grid-cols-[auto_1fr] gap-x-3 gap-y-0.5">
          <dt className="text-muted">Tries</dt>
          <dd className="tabular-nums" data-testid="designation-fails">
            {d.fails ?? 0} failed
          </dd>
          <dt className="text-muted">Next try</dt>
          <dd data-testid="designation-retry">{retryIn(d.retryAt)}</dd>
          {many ? (
            <>
              <dt className="text-muted">Label</dt>
              <dd>{cluster.length} like it nearby</dd>
            </>
          ) : null}
        </dl>
        <div className="flex flex-wrap gap-2">
          <Button size="sm" disabled={!canWrite} onClick={() => onRetry([d.id])}>
            <RotateCcw className="size-3.5" />
            Retry now
          </Button>
          <Button size="sm" variant="ghost" disabled={!canWrite} onClick={() => onCancel([d.id])}>
            <Trash2 className="size-3.5" />
            Cancel
          </Button>
          {many ? (
            <>
              <Button size="sm" variant="ghost" disabled={!canWrite} onClick={() => onRetry([...cluster])}>
                Retry all {cluster.length}
              </Button>
              <Button size="sm" variant="ghost" disabled={!canWrite} onClick={() => onCancel([...cluster])}>
                Cancel all {cluster.length}
              </Button>
            </>
          ) : null}
        </div>
      </div>
    </Shell>
  );
}

/** "Chop at 4, 64, 2". */
function designationTitle(d: StoryDesignation): string {
  const at = designationAt(d);
  return `${d.label ?? d.kind}${at ? ` at ${at.join(', ')}` : ''}`;
}

/** Failed / Waiting (a wait with no failure) / Failing, with the short reason. */
function designationSub(d: StoryDesignation): string {
  if (d.status === 'failed') return `Failed: ${shortReason(d.reason).title}`;
  if (d.wait && !d.reason) return `Waiting: ${shortReason(d.wait).title}`;
  return `Failing: ${shortReason(d.reason).title}`;
}

function designationAt(d: StoryDesignation): number[] | null {
  const t = d.target;
  if (Array.isArray(t)) return t.map((n) => Math.floor(n));
  const b = d.block ?? d.blocks?.[0] ?? d.targets?.[0];
  return b ? b.map((n) => Math.floor(n)) : null;
}

// ------------------------------------------------------------------ pawn

type PawnTab = 'overview' | 'inventory' | 'skills' | 'orders';

/** What the pawn is doing (its job's progress), or drafted / nothing. */
function PawnDoing({ pawn }: { pawn: StoryPawn }) {
  const progress = pawn.job?.progress;
  return (
    <div className="rounded-md bg-surface-2 px-2 py-1.5">
      <div className="text-[10.5px] font-semibold tracking-wide text-subtle uppercase">Doing</div>
      <div className="font-medium">{pawn.job?.label ?? (pawn.drafted ? 'Drafted: awaiting orders' : 'Nothing')}</div>
      {progress !== null && progress !== undefined ? (
        <span className="mt-1 block h-1 overflow-hidden rounded-full bg-surface-3">
          <span className="block h-full bg-accent" style={{ width: `${progress * 100}%` }} />
        </span>
      ) : null}
    </div>
  );
}

/** The pawn's own bed (Show centres the map on it). */
function PawnBedRow({ bed, onShowBed }: { bed?: StoryBed | null; onShowBed?: () => void }) {
  return (
    <div className="flex items-center gap-2 rounded-md bg-surface-2 px-2 py-1.5" data-testid="pawn-bed">
      <BedDouble className="size-3.5 text-muted" aria-hidden />
      <span className="min-w-0 flex-1 truncate">{bed ? `Bed at ${bed.pos[0]}, ${bed.pos[2]}` : 'No bed of its own'}</span>
      {bed && onShowBed ? (
        <Button size="sm" variant="ghost" onClick={onShowBed}>
          Show
        </Button>
      ) : null}
    </div>
  );
}

/** Overview: doing, health, food, traits, held item, bed, draft, and the equipment / gear actions (`extra`). */
function PawnOverview({
  pawn,
  bed,
  canWrite,
  onDraft,
  onShowBed,
  extra,
}: {
  pawn: StoryPawn;
  bed?: StoryBed | null;
  canWrite: boolean;
  onDraft: (d: boolean) => void;
  onShowBed?: () => void;
  extra?: ReactNode;
}) {
  return (
    <div className="space-y-2 text-xs">
      <PawnDoing pawn={pawn} />
      <Bar value={pawn.hp ?? 20} color="#e5484d" label="Health" />
      <Bar value={pawn.food ?? 20} color="#e2a336" label="Food" />
      <div>
        <div className="mb-1 text-[10.5px] font-semibold tracking-wide text-subtle uppercase">Traits</div>
        <ul className="flex flex-wrap gap-1">
          {pawn.traits.map((t) => (
            <li key={t.id} className="rounded bg-surface-2 px-1.5 py-0.5" title={t.effect ?? undefined}>
              {t.label}
            </li>
          ))}
        </ul>
      </div>
      {pawn.held ? <div className="text-muted">Holding {pawn.held}</div> : null}
      <PawnBedRow bed={bed} onShowBed={onShowBed} />
      <Button
        size="sm"
        variant={pawn.drafted ? 'subtle' : 'secondary'}
        disabled={!canWrite}
        onClick={() => onDraft(!pawn.drafted)}
      >
        {pawn.drafted ? <ShieldOff className="size-3.5" /> : <Shield className="size-3.5" />}
        {pawn.drafted ? 'Undraft (R)' : 'Draft (R)'}
      </Button>
      {extra}
    </div>
  );
}

/** One skill row: its level bar and passion. */
function SkillRow({ t, pawn }: { t: (typeof WORK_TYPES)[number]; pawn: StoryPawn }) {
  const s = pawn.skills[t];
  const level = s?.level ?? 0;
  return (
    <li className="flex items-center gap-1.5">
      <span className="w-14 text-muted">{WORK_LABELS[t]}</span>
      <span className="block h-2 flex-1 overflow-hidden rounded-full bg-surface-3">
        <span className="block h-full bg-accent" style={{ width: `${(level / 20) * 100}%` }} />
      </span>
      <span className="w-5 text-right tabular-nums">{level}</span>
      {s?.passion ? <Flame className="size-3 text-[#ff9f43]" aria-label="passion" /> : <span className="w-3" />}
    </li>
  );
}

/** Skills: every work type's level and passion, and the XP level. */
function PawnSkills({ pawn }: { pawn: StoryPawn }) {
  return (
    <ul className="space-y-1 text-xs">
      {WORK_TYPES.map((t) => (
        <SkillRow key={t} t={t} pawn={pawn} />
      ))}
      <li
        className="flex items-center gap-1.5 pt-1"
        data-testid="pawn-xp"
        title="Experience level: earned mining ore, smelting and fighting; enchanting and anvil bills spend it"
      >
        <span className="w-14 text-muted">XP level</span>
        <span className="tabular-nums">{pawn.xpLevel ?? 0}</span>
      </li>
    </ul>
  );
}

/** Orders: the queued orders, and clearing them. */
function PawnOrders({ pawn, canWrite, onClearQueue }: { pawn: StoryPawn; canWrite: boolean; onClearQueue: () => void }) {
  return (
    <div className="space-y-1 text-xs">
      {pawn.queue.length === 0 ? (
        <p className="text-muted">No queued orders. Right-click the map with this pawn selected (Shift queues).</p>
      ) : null}
      <ol className="space-y-0.5">
        {pawn.queue.map((o, i) => (
          <li key={String(o.id ?? i)} className="flex items-center gap-1.5 rounded bg-surface-2 px-1.5 py-1">
            <span className="w-4 text-subtle">{i + 1}</span>
            <span className="font-medium capitalize">{o.kind}</span>
            <span className="text-muted">{orderWhere(o.target)}</span>
            <span className="ml-auto text-subtle">{o.state}</span>
          </li>
        ))}
      </ol>
      {pawn.queue.length ? (
        <Button size="sm" variant="ghost" disabled={!canWrite} onClick={onClearQueue}>
          Clear orders
        </Button>
      ) : null}
    </div>
  );
}

const isOffline = (p: StoryPawn) => p.online === false || p.state === 'offline';

/** An offline pawn: why, and Bring online. */
function PawnOffline({
  pawn,
  blocked,
  canWrite,
  onBringOnline,
}: {
  pawn: StoryPawn;
  blocked: readonly StoryOnlineBlocked[];
  canWrite: boolean;
  onBringOnline: (overCap: boolean) => void;
}) {
  return (
    <div className="mb-2">
      <BringOnlineBox
        reason={pawn.offlineReason ?? 'offline'}
        blocked={blocked}
        canWrite={canWrite}
        label="Bring online"
        onBring={onBringOnline}
        testid="pawn-offline"
      />
    </div>
  );
}

export function PawnInspector({
  pawn,
  bed,
  canWrite,
  onDraft,
  onClearQueue,
  onShowBed,
  onClose,
  blocked = [],
  onBringOnline,
  extra,
}: {
  /** The pawn's equipment and gear actions (assign/GearActions), under its overview. */
  extra?: ReactNode;
  pawn: StoryPawn;
  bed?: StoryBed | null;
  canWrite: boolean;
  onDraft: (d: boolean) => void;
  onClearQueue: () => void;
  onShowBed?: () => void;
  onClose: () => void;
  /** The last "Bring online" refusals for this pawn. */
  blocked?: readonly StoryOnlineBlocked[];
  onBringOnline?: (overCap: boolean) => void;
}) {
  const [tab, setTab] = useState<PawnTab>('overview');
  const offline = isOffline(pawn);
  return (
    <Shell
      title={pawn.display}
      sub={offline ? 'offline' : (pawn.state ?? 'idle')}
      onClose={onClose}
      avatar={<Avatar name={pawn.name} size={30} />}
      wide={tab === 'inventory'}
    >
      {offline && onBringOnline ? (
        <PawnOffline pawn={pawn} blocked={blocked} canWrite={canWrite} onBringOnline={onBringOnline} />
      ) : null}
      <TabRow<PawnTab>
        tabs={[
          ['overview', 'Overview'],
          ['inventory', 'Inventory'],
          ['skills', 'Skills'],
          ['orders', 'Orders'],
        ]}
        value={tab}
        onChange={setTab}
      />
      {tab === 'overview' ? (
        <PawnOverview pawn={pawn} bed={bed} canWrite={canWrite} onDraft={onDraft} onShowBed={onShowBed} extra={extra} />
      ) : null}
      {tab === 'skills' ? <PawnSkills pawn={pawn} /> : null}
      {tab === 'inventory' ? (
        // The pawn's own window (armour, offhand, 27 main slots, hotbar with the held slot), polled while open: the same
        // panel the Live hive shows for any body (bots.inventory answers for colony pawns by login).
        <InventorySection bot={pawn.name} compact />
      ) : null}
      {tab === 'orders' ? <PawnOrders pawn={pawn} canWrite={canWrite} onClearQueue={onClearQueue} /> : null}
    </Shell>
  );
}

// ------------------------------------------------------------------ several bodies, AI

/** A selected bed: who sleeps in it, and a picker to give it to a pawn (their other bed is freed) or clear it. */
/** The house zone a bed is in (when it is in one). */
function bedZone(state: StoryColonyState | undefined, bed: StoryBed) {
  if (bed.zone === null || bed.zone === undefined) return undefined;
  return state?.zones.find((z) => String(z.id) === String(bed.zone));
}

/** "x, y, z · Main house". */
function bedSub(state: StoryColonyState | undefined, bed: StoryBed): string {
  const zone = bedZone(state, bed);
  return `${bed.pos[0]}, ${bed.pos[1]}, ${bed.pos[2]}${zone ? ` · ${zone.name || zone.label || 'a house'}` : ''}`;
}

/** Who sleeps in the bed (nobody). */
function BedOwner({ bed, state }: { bed: StoryBed; state: StoryColonyState | undefined }) {
  const owner = bed.pawn ? state?.pawns.find((p) => p.name === bed.pawn) : undefined;
  return (
    <div className="flex items-center gap-2">
      <span className="text-muted">Owner</span>
      {owner || bed.pawn ? (
        <span className="flex items-center gap-1.5 font-medium">
          <Avatar name={bed.pawn!} size={18} />
          {owner?.display ?? bed.pawn}
        </span>
      ) : (
        <span className="text-subtle">nobody</span>
      )}
    </div>
  );
}

/** A pawn as a bed owner choice ("Ann (moves from their bed)" when she has another bed). */
function bedOwnerLabel(p: StoryPawn, bed: StoryBed, beds: readonly StoryBed[]): string {
  return `${p.display}${beds.some((b) => b.pawn === p.name && b !== bed) ? ' (moves from their bed)' : ''}`;
}

export function BedInspector({
  bed,
  state,
  canWrite,
  write,
  onClose,
}: {
  bed: StoryBed;
  state: StoryColonyState | undefined;
  canWrite: boolean;
  write: StoryWrite;
  onClose: () => void;
}) {
  const [busy, setBusy] = useState(false);
  const pos = { x: bed.pos[0], y: bed.pos[1], z: bed.pos[2] };
  const run = (body: Parameters<StoryWrite>[1]) => {
    setBusy(true);
    void write('story.beds', body as never).finally(() => setBusy(false));
  };
  const beds = state?.beds ?? [];
  return (
    <Shell
      title="Bed"
      sub={bedSub(state, bed)}
      onClose={onClose}
      testid="bed-inspector"
      avatar={<BedDouble className="size-6 text-muted" />}
    >
      <div className="space-y-2 text-xs">
        <BedOwner bed={bed} state={state} />
        <label className="flex items-center gap-2">
          <span className="text-muted">Set owner</span>
          <select
            aria-label="Set owner"
            className="h-7 min-w-0 flex-1 rounded border border-border bg-surface px-1"
            disabled={!canWrite || busy}
            value={bed.pawn ?? ''}
            onChange={(e) =>
              e.target.value ? run({ op: 'assign', bed: pos, pawn: e.target.value }) : run({ op: 'unassign', bed: pos })
            }
          >
            <option value="">nobody</option>
            {(state?.pawns ?? []).map((p) => (
              <option key={p.name} value={p.name}>
                {bedOwnerLabel(p, bed, beds)}
              </option>
            ))}
          </select>
        </label>
        <Button
          size="sm"
          variant="ghost"
          disabled={!canWrite || busy || !bed.pawn}
          onClick={() => run({ op: 'unassign', bed: pos })}
        >
          Clear
        </Button>
      </div>
    </Shell>
  );
}

export function GroupInspector({
  names,
  state,
  canWrite,
  onDraft,
  onClose,
  extra,
}: {
  /** Bulk gear actions for the selected pawns (assign/GearActions). */
  extra?: ReactNode;
  names: string[];
  state: StoryColonyState | undefined;
  canWrite: boolean;
  onDraft: (d: boolean) => void;
  onClose: () => void;
}) {
  const pawns = (state?.pawns ?? []).filter((p) => names.includes(p.name));
  const ai = (state?.ai ?? []).filter((a) => names.includes(a.name) && !pawns.some((p) => p.name === a.name));
  return (
    <Shell
      title={`${names.length} selected`}
      sub={[pawns.length && `${pawns.length} pawn${pawns.length === 1 ? '' : 's'}`, ai.length && `${ai.length} AI`]
        .filter(Boolean)
        .join(' · ')}
      onClose={onClose}
    >
      <ul className="space-y-1 text-xs">
        {pawns.map((p) => (
          <li key={p.name} className="flex items-center gap-1.5">
            <Avatar name={p.name} size={18} />
            <span className="font-semibold">{p.display}</span>
            <span className="truncate text-muted">{p.job?.label ?? p.state}</span>
          </li>
        ))}
        {ai.map((a) => (
          <li key={a.name} className="flex items-center gap-1.5">
            <Avatar name={a.name} size={18} ai />
            <span className="font-semibold">{a.name}</span>
            <AiMark className="size-3 text-[#a78bfa]" />
            <span className="truncate text-muted">{a.task}</span>
          </li>
        ))}
      </ul>
      {pawns.length ? (
        <div className="mt-2 flex gap-1.5">
          <Button size="sm" disabled={!canWrite} onClick={() => onDraft(true)}>
            <Shield className="size-3.5" /> Draft all
          </Button>
          <Button size="sm" variant="ghost" disabled={!canWrite} onClick={() => onDraft(false)}>
            Undraft
          </Button>
        </div>
      ) : null}
      {pawns.length ? extra : null}
    </Shell>
  );
}

export function AiInspector({
  body,
  state,
  onChat,
  onClose,
}: {
  body: StoryAiBody;
  state: StoryColonyState | undefined;
  onChat: () => void;
  onClose: () => void;
}) {
  return (
    <Shell
      title={body.name}
      sub={`${body.kind}${body.owner ? ` · driven by ${body.owner}` : ''}`}
      onClose={onClose}
      avatar={<Avatar name={body.name} size={30} ai />}
    >
      <div className="space-y-2 text-xs">
        <div className="rounded-md bg-surface-2 px-2 py-1.5">
          <div className="text-[10.5px] font-semibold tracking-wide text-subtle uppercase">Task</div>
          <div className="font-medium">{body.task ?? '–'}</div>
          {body.brainState ? <div className="text-muted">{body.brainState}</div> : null}
          {body.script ? <div className="text-muted">script {body.script}</div> : null}
        </div>
        <Bar value={body.hp ?? 20} color="#e5484d" label="Health" />
        <Bar value={body.food ?? 20} color="#e2a336" label="Food" />
        <Button size="sm" onClick={onChat}>
          <MessageSquare className="size-3.5" /> Chat (T)
        </Button>
        <div>
          <div className="mb-1 text-[10.5px] font-semibold tracking-wide text-subtle uppercase">Directives</div>
          <DirectiveList directives={state?.directives ?? []} bot={body.name} />
        </div>
      </div>
    </Shell>
  );
}

// ------------------------------------------------------------------ storage

function Check({ state, onClick, label }: { state: 'on' | 'off' | 'partial'; onClick: () => void; label: string }) {
  return (
    <button
      type="button"
      role="checkbox"
      aria-checked={state === 'partial' ? 'mixed' : state === 'on'}
      aria-label={label}
      onClick={onClick}
      className={cn(
        'grid size-3.5 shrink-0 place-items-center rounded-[3px] border',
        state === 'off' ? 'border-border-strong bg-surface' : 'border-accent bg-accent text-accent-fg',
      )}
    >
      {state === 'on' ? (
        <svg viewBox="0 0 10 10" className="size-2.5">
          <path d="M2 5.2 4.2 7.4 8.2 2.8" stroke="currentColor" strokeWidth="1.6" fill="none" />
        </svg>
      ) : state === 'partial' ? (
        <Minus className="size-2.5" />
      ) : null}
    </button>
  );
}

export function FilterTree({
  tree,
  filter,
  onChange,
  compact,
}: {
  tree: TreeNode[];
  filter: StoryFilter;
  onChange: (f: StoryFilter) => void;
  compact?: boolean;
}) {
  const [open, setOpen] = useState<Set<string>>(() => new Set(compact ? [] : tree.map((n) => n.id)));
  const [q, setQ] = useState('');
  const shown = useMemo(() => searchTree(tree, q), [tree, q]);
  const node = (n: TreeNode, depth: number) => {
    const isOpen = open.has(n.id) || !!q.trim();
    return (
      <li key={n.id}>
        <div className="flex items-center gap-1 py-0.5" style={{ paddingLeft: depth * 12 }}>
          <button
            type="button"
            onClick={() =>
              setOpen((s) => (s.has(n.id) ? new Set([...s].filter((x) => x !== n.id)) : new Set([...s, n.id])))
            }
            className="text-subtle"
            aria-label={isOpen ? `Collapse ${n.label}` : `Expand ${n.label}`}
          >
            {isOpen ? <ChevronDown className="size-3" /> : <ChevronRight className="size-3" />}
          </button>
          <Check state={nodeState(filter, n)} onClick={() => onChange(toggleNode(filter, n))} label={n.label} />
          <span className="font-medium">{n.label}</span>
        </div>
        {isOpen ? (
          <ul>
            {n.children.map((c) => node(c, depth + 1))}
            {n.items.map((it) => (
              <li key={it.key} className="flex items-center gap-1 py-0.5" style={{ paddingLeft: depth * 12 + 16 }}>
                <Check
                  state={itemAllowed(filter, n, it.key) ? 'on' : 'off'}
                  onClick={() => onChange(toggleItem(filter, n, it.key))}
                  label={it.label}
                />
                <ItemIcon id={it.id} meta={it.meta} size={16} />
                <span>{it.label}</span>
              </li>
            ))}
          </ul>
        ) : null}
      </li>
    );
  };
  return (
    <div className="text-[11.5px]">
      <div className="mb-1 flex items-center gap-1">
        <div className="relative flex-1">
          <Search className="pointer-events-none absolute top-1.5 left-1.5 size-3 text-subtle" />
          <Input
            aria-label="Search items"
            value={q}
            onChange={(e) => setQ(e.target.value)}
            placeholder="Search"
            className="h-6 pl-5 text-[11px]"
          />
        </div>
        <button
          type="button"
          onClick={() => onChange(allowAll(tree))}
          className="rounded bg-surface-2 px-1.5 py-0.5 font-medium hover:bg-surface-3"
        >
          Allow all
        </button>
        <button
          type="button"
          onClick={() => onChange(CLEAR_FILTER)}
          className="rounded bg-surface-2 px-1.5 py-0.5 font-medium hover:bg-surface-3"
        >
          Clear
        </button>
      </div>
      <ul className="max-h-48 overflow-auto rounded border border-border p-1" data-testid="filter-tree">
        {shown.map((n) => node(n, 0))}
      </ul>
    </div>
  );
}

type StoragePatch = { filter?: StoryFilter; priority?: string; label?: string };

/** The storage a chest belongs to (either half of a double chest). */
const storageOf = (storages: readonly StoryStorage[], c: V3) =>
  storages.find((s) => k3(s.chest) === k3(c) || (s.doubleWith && k3(s.doubleWith) === k3(c)));

/** "3 chests", the chest's label, "Double chest" or "Chest". */
function storageTitle(chests: readonly V3[], first: StoryStorage | undefined): string {
  if (chests.length > 1) return `${chests.length} chests`;
  return first?.label || (first?.doubleWith ? 'Double chest' : 'Chest');
}

/** "Storage · High · Everything" (or "Not storage yet"). */
function storageSub(registered: boolean, first: StoryStorage | undefined, tree: TreeNode[], filter: StoryFilter): string {
  if (!registered) return 'Not storage yet';
  const priority = PRIORITY_LABELS[(first?.priority ?? 'normal') as keyof typeof PRIORITY_LABELS] ?? first?.priority;
  return `Storage · ${priority} · ${filterSummary(tree, filter)}`;
}

/** A registered storage's settings: priority, copy / paste, label, the item filter, stop using it. */
function StorageSettings({
  first,
  single,
  keys,
  tree,
  filter,
  canWrite,
  clip,
  onClip,
  set,
  entries,
  write,
}: {
  first: StoryStorage | undefined;
  single: boolean;
  keys: V3[];
  tree: TreeNode[];
  filter: StoryFilter;
  canWrite: boolean;
  clip: StorageClip | null;
  onClip: (c: StorageClip) => void;
  set: (patch: StoragePatch) => void;
  entries: (op: 'add' | 'remove_entries', entries: string) => void;
  write: StoryWrite;
}) {
  return (
    <>
      <div className="flex items-center gap-1.5">
        <label className="flex items-center gap-1">
          {first?.pendingPriority ? 'Requested priority' : 'Priority'}
          <select
            aria-label="Storage priority"
            value={first?.pendingPriority ?? first?.priority ?? 'normal'}
            disabled={!canWrite}
            onChange={(e) => set({ priority: e.target.value })}
            className="h-6 rounded border border-border bg-surface-2 px-1"
          >
            {STORY_STORAGE_PRIORITIES.map((p) => (
              <option key={p} value={p}>
                {PRIORITY_LABELS[p]}
              </option>
            ))}
          </select>
        </label>
        <button
          type="button"
          onClick={() => onClip({ filter, priority: first?.pendingPriority ?? first?.priority ?? 'normal' })}
          className="ml-auto inline-flex items-center gap-1 rounded bg-surface-2 px-1.5 py-0.5 hover:bg-surface-3"
          title="Copy these settings"
        >
          <Copy className="size-3" /> Copy
        </button>
        <button
          type="button"
          disabled={!clip || !canWrite}
          onClick={() => clip && set({ filter: clip.filter, priority: clip.priority })}
          className="inline-flex items-center gap-1 rounded bg-surface-2 px-1.5 py-0.5 hover:bg-surface-3 disabled:opacity-40"
          title="Paste copied settings onto the selected chests"
        >
          <ClipboardPaste className="size-3" /> Paste
        </button>
      </div>
      {single ? (
        <Input
          aria-label="Chest label"
          defaultValue={first?.label ?? ''}
          placeholder="Label (optional)"
          maxLength={40}
          className="h-6 text-[11px]"
          onBlur={(e) => e.target.value !== (first?.label ?? '') && set({ label: e.target.value })}
        />
      ) : null}
      <FilterTree tree={tree} filter={filter} onChange={(f) => canWrite && set({ filter: f })} />
      <StorageEntryActions tree={tree} canWrite={canWrite} apply={entries} />
      <Button
        size="sm"
        variant="ghost"
        disabled={!canWrite}
        onClick={() => void write('story.storage', { op: 'remove', chests: keys } as never)}
      >
        Stop using as storage
      </Button>
    </>
  );
}

export function StorageInspector({
  chests,
  state,
  canWrite,
  write,
  clip,
  onClip,
  onClose,
}: {
  chests: V3[];
  state: StoryColonyState | undefined;
  canWrite: boolean;
  write: StoryWrite;
  clip: StorageClip | null;
  onClip: (c: StorageClip) => void;
  onClose: () => void;
}) {
  const categories = state?.categories;
  const tree = useMemo(() => buildTree(categories), [categories]);
  const storages = state?.storages ?? [];
  const list = chests.map((c) => storageOf(storages, c));
  const first = list.find((s): s is StoryStorage => !!s);
  const [pending, setPending] = useState<StoryFilter | null>(null);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const filter = pending ?? first?.pendingFilter ?? first?.filter ?? CLEAR_FILTER;
  const registered = list.every(Boolean);
  const keys = [...new Map(chests.map((c, i) => { const at = list[i]?.chest ?? c; return [k3(at), at] as const; })).values()];
  const selected = [...new Map(list.filter((s): s is StoryStorage => !!s).map((s) => [k3(s.chest), s])).values()];
  const send = (body: ReturnType<typeof storageSetBody> | ReturnType<typeof storageEntriesBody>) => {
    setSaving(true);
    setError(null);
    void write('story.storage', body as never).catch((e: unknown) => setError(errorText(e)))
      .finally(() => { setPending(null); setSaving(false); });
  };
  const set = (patch: StoragePatch) => {
    if (patch.filter) setPending(patch.filter);
    send(storageSetBody(keys, patch));
  };
  return (
    <Shell
      title={storageTitle(chests, first)}
      sub={storageSub(registered, first, tree, first?.filter ?? CLEAR_FILTER)}
      onClose={onClose}
      testid="storage-inspector"
      wide
    >
      <div className="space-y-2 text-xs">
        {chests.length === 1 ? (
          <ContainerContents target={{ dim: 0, x: keys[0]![0], y: keys[0]![1], z: keys[0]![2] }} heading />
        ) : null}
        <ReservationList
          reservations={state?.reservations}
          items={new Set(list.flatMap((st) => (st?.contents ?? []).map(([i, m]) => `${i}:${m}`)))}
          canWrite={canWrite}
          write={write}
        />
        <StorageSigns storages={selected} tree={tree} />
        {saving ? <p role="status">Saving requested settings…</p> : null}
        {error ? <p role="alert" className="text-red-300">{error}</p> : null}
        {pending || first?.pendingFilter ? <p className="font-medium">Requested filter (not active yet)</p> : null}
        {registered ? (
          <StorageSettings
            first={first}
            single={chests.length === 1}
            keys={keys}
            tree={tree}
            filter={filter}
            canWrite={canWrite && !saving}
            clip={clip}
            onClip={onClip}
            set={set}
            entries={(op, text) => send(storageEntriesBody(keys, op, text))}
            write={write}
          />
        ) : (
          <Button
            size="sm"
            variant="primary"
            disabled={!canWrite}
            onClick={() =>
              void write('story.storage', {
                op: 'set',
                chests: keys,
                priority: 'normal',
                filter: allowAll(tree),
              } as never)
            }
          >
            Use as storage
          </Button>
        )}
      </div>
    </Shell>
  );
}

// ------------------------------------------------------------------ bills

/** A build's auto bill: tagged with its build, progress only; the one thing the owner can do is cancel it (the build then waits). */
function AutoBillRow({
  bill,
  label,
  output,
  state,
  canWrite,
  write,
}: {
  bill: StoryBill;
  label: string;
  output: readonly number[] | undefined;
  state: StoryColonyState | undefined;
  canWrite: boolean;
  write: StoryWrite;
}) {
  const build = (state?.builds ?? []).find((b) => String(b.id) === String(bill.forBuild));
  return (
    <li
      data-bill={bill.id}
      data-auto-bill={String(bill.forBuild)}
      className="flex items-center gap-1.5 rounded-md border border-dashed border-border bg-surface-2 px-1.5 py-1"
      title="Made automatically for a queued build: it follows the build's shortfall and goes when the build no longer needs it"
    >
      <span className="w-3" aria-hidden />
      {output ? <ItemIcon id={output[0]!} meta={output[1] ?? 0} size={18} /> : null}
      <div className="min-w-0 flex-1">
        <div className="flex items-center gap-1 truncate font-semibold">
          {label}
          <span className="rounded bg-accent-soft px-1 text-[10px] font-semibold text-fg">
            {String(bill.forBuild) === 'tools' ? 'for tools' : `for build #${String(bill.forBuild)}`}
          </span>
        </div>
        <div className="text-[10.5px] text-muted">
          {bill.done}/{bill.target}
          {build ? ` · ${build.label}` : ''}
        </div>
        {bill.suspendedReason ? (
          <div className="text-[10.5px] text-danger">Suspended: {bill.suspendedReason}</div>
        ) : null}
      </div>
      <button
        type="button"
        disabled={!canWrite}
        onClick={() => void write('story.bills', { op: 'remove', id: bill.id } as never)}
        aria-label={
          String(bill.forBuild) === 'tools' ? `Cancel ${label} for tools` : `Cancel ${label} for build ${String(bill.forBuild)}`
        }
        title="Cancel: the build waits for this item instead of crafting it"
        className="rounded p-0.5 text-danger hover:bg-danger-soft"
      >
        <Trash2 className="size-3.5" />
      </button>
    </li>
  );
}

/** The colony's craft tasks at this bench, as one line (they are never bills: docs "Craft tasks"). */
function BenchTasks({ tasks, pos }: { tasks: StoryColonyState['craftTasks']; pos: readonly number[] }) {
  const line = benchTaskLine(tasks, pos);
  if (!line) return null;
  return (
    <div className="rounded-md bg-surface-2 px-1.5 py-1 text-[10.5px] text-muted" data-testid="bench-tasks" title="The colony crafts these itself, before or after your bills: immediate needs first, build ingredients last">
      {line}
    </div>
  );
}

/** A crafting table / furnace the colony doesn't use yet (another player's, or not designated): designate it here. */
export function UndesignatedBench({
  pos,
  canWrite,
  write,
  onClose,
}: {
  pos: V3;
  canWrite: boolean;
  write: StoryWrite;
  onClose: () => void;
}) {
  const [err, setErr] = useState<string | null>(null);
  return (
    <Shell
      title="Workbench"
      sub={`${pos[0]}, ${pos[1]}, ${pos[2]} · not the colony's`}
      onClose={onClose}
      testid="bench-designate"
    >
      <div className="space-y-2 text-xs">
        <p className="text-muted">
          The colony only uses benches it was given: designated ones take bills, receive the builds' crafts and unlock
          the Architect&apos;s tiers. Other players&apos; benches are left alone.
        </p>
        <Button
          size="sm"
          variant="primary"
          disabled={!canWrite}
          onClick={() =>
            void write('story.workbenches', { op: 'designate', pos } as never).then(
              () => setErr(null),
              (e: unknown) => setErr(e instanceof Error ? e.message : 'failed'),
            )
          }
        >
          Use as colony workbench
        </Button>
        {err ? <p className="text-danger">{err}</p> : null}
      </div>
    </Shell>
  );
}

/** A furnace's fuel choice: one icon button per fuel the version burns (the pressed one is the setting). */
/** What builds hold of these items (the colony's reservations), each with a Release that frees that build's claim. */
export function ReservationList({
  reservations,
  items,
  canWrite,
  write,
}: {
  reservations: readonly StoryReservation[] | null | undefined;
  items: ReadonlySet<string>;
  canWrite: boolean;
  write: StoryWrite;
}) {
  const rows = reservationRows(reservations, items);
  if (rows.length === 0) return null;
  return (
    <div className="space-y-0.5" data-testid="reservations">
      <div className="text-[11px] font-semibold text-muted">Reserved for builds</div>
      <ul className="space-y-0.5">
        {rows.map((r) => (
          <li key={`${r.build}|${r.forItem}|${r.label}`} className="flex items-center gap-1.5 rounded bg-surface-2 px-1.5 py-0.5 text-[11px]">
            <ItemIcon id={r.item[0]} meta={Math.max(0, r.item[1])} size={14} />
            <span className="min-w-0 flex-1 truncate" title={r.chain ? 'an input of the chain that crafts it' : undefined}>
              {reservationLine(r)}
            </span>
            <button
              type="button"
              disabled={!canWrite}
              onClick={() => void write('story.bills', { op: 'release_claim', build: r.build, item: r.forItem } as never)}
              className="rounded px-1 text-[10.5px] text-accent hover:bg-surface-3"
              title={r.chain ? 'Stop crafting it for the build (the build waits for it)' : 'Pause the build: it lets go of what it holds'}
            >
              Release
            </button>
          </li>
        ))}
      </ul>
    </div>
  );
}

export function FuelPicker({
  value,
  choices,
  disabled,
  onPick,
}: {
  value: string;
  choices: readonly string[];
  disabled?: boolean;
  onPick: (fuel: string) => void;
}) {
  return (
    <div className="flex flex-wrap items-center gap-1" data-testid="fuel-picker" role="group" aria-label="Furnace fuel">
      <span className="mr-1 text-[11px] font-semibold text-muted">Fuel</span>
      {choices.map((f) => {
        const c = FUEL_CHOICES[f];
        return (
          <button
            key={f}
            type="button"
            disabled={disabled}
            aria-pressed={f === value}
            aria-label={`Fuel: ${fuelLabel(f)}`}
            title={fuelLabel(f)}
            onClick={() => f !== value && onPick(f)}
            className={cn(
              'flex items-center gap-0.5 rounded border px-1 py-0.5 text-[10.5px]',
              f === value ? 'border-accent bg-accent-soft' : 'border-border hover:bg-surface-3',
            )}
          >
            {c ? <ItemIcon id={c.icon[0]} meta={c.icon[1]} size={14} /> : null}
            {f === 'auto' ? 'Auto' : f === 'any' ? 'Any' : f === 'coal_or_charcoal' ? 'Coal/charcoal' : null}
          </button>
        );
      })}
    </div>
  );
}

/** What a bench says before its bills: an unsupported kind, a furnace's fuel and smelting, station facts, its contents. */
function BenchFacts({ bench, canWrite, write }: { bench: StoryWorkbench; canWrite: boolean; write: StoryWrite }) {
  const pos = bench.pos;
  return (
    <>
      {bench.supported === false ? (
        <p
          className="rounded-md bg-surface-2 px-2 py-1.5 text-muted"
          title="b1.8.1 has no brewing stands, anvils or enchanting tables"
        >
          This version has no {bench.kind.replace(/_/g, ' ')} bills.
        </p>
      ) : null}
      {bench.kind === 'furnace' && bench.fuelChoices?.length ? (
        <FuelPicker
          value={bench.fuel ?? 'auto'}
          choices={bench.fuelChoices}
          disabled={!canWrite}
          onPick={(f) => void write('story.workbenches', fuelBody(pos, f) as never)}
        />
      ) : null}
      {smeltingLine(bench) ? (
        <p className="rounded-md bg-surface-2 px-2 py-1 text-[11px] text-muted" data-testid="furnace-smelting">
          {smeltingLine(bench)}
        </p>
      ) : null}
      {stationLines(bench).length ? (
        <ul className="space-y-0.5 rounded-md bg-surface-2 px-2 py-1.5 text-[11px] text-muted" data-testid="station-facts">
          {stationLines(bench).map((l) => (
            <li key={l} className={l.includes('gone') || l.startsWith('No water') ? 'text-danger' : undefined}>
              {l}
            </li>
          ))}
        </ul>
      ) : null}
      {bench.kind === 'furnace' || bench.kind === 'brewing_stand' ? (
        <ContainerContents target={{ dim: 0, x: pos[0], y: pos[1], z: pos[2] }} heading />
      ) : null}
    </>
  );
}

/** The items the bench's bills take (`id:meta`), for the reservations they hold. */
function billInputs(recipes: readonly StoryRecipe[], bills: readonly StoryBill[]): Set<string> {
  return new Set(
    recipes
      .filter((r) => bills.some((b) => b.recipe === r.id))
      .flatMap((r) => r.inputs.flatMap((i) => (i.options ?? []).map(([a, m]) => `${a}:${m}`))),
  );
}

export function BillsInspector({
  bench,
  state,
  canWrite,
  write,
  onPickChest,
  onClose,
}: {
  bench: StoryWorkbench;
  state: StoryColonyState | undefined;
  canWrite: boolean;
  write: StoryWrite;
  onPickChest: () => Promise<V3 | null>;
  onClose: () => void;
}) {
  const recipes = useRecipes(bench.kind);
  const [picker, setPicker] = useState(false);
  const [editing, setEditing] = useState<{ bill: StoryBill | null; draft: BillDraft; key: number } | null>(null);
  const [picking, setPicking] = useState(false);
  const dragFrom = useRef<number | null>(null);
  // "Pick a chest" closes the dialog while the next chest clicked on the map is chosen, then reopens it with that chest.
  const pickChest = async (cur: BillDraft) => {
    setEditing((e) => e && { ...e, draft: cur });
    setPicking(true);
    const c = await onPickChest();
    setPicking(false);
    setEditing((e) => e && { ...e, key: e.key + 1, draft: c ? { ...e.draft, store: { chest: c } } : e.draft });
  };
  const pos = bench.pos;
  const recipeOf = (id: string) => recipes.data?.find((r) => r.id === id);
  /** A bill dropped on row `i`: the owner's bills reorder (auto bills, listed first, follow their builds' order). */
  const dropBill = (i: number) => {
    const from = dragFrom.current;
    dragFrom.current = null;
    if (from === null || from === i) return;
    const auto = bench.bills.filter((x) => (x.forBuild ?? null) !== null).length;
    if (from < auto || i < auto) return;
    void write(
      'story.bills',
      reorderBills(
        pos,
        bench.bills.filter((x) => (x.forBuild ?? null) === null).map((x) => x.id),
        from - auto,
        i - auto,
      ) as never,
    );
  };
  const label = bench.label ?? bench.kind.replace(/_/g, ' ');
  return (
    <Shell
      title={label[0]!.toUpperCase() + label.slice(1)}
      sub={`${bench.bills.length} bill${bench.bills.length === 1 ? '' : 's'} · priority first, then list order`}
      onClose={onClose}
      testid="bills-inspector"
      wide
    >
      <div className="space-y-2 text-xs">
        <BenchFacts bench={bench} canWrite={canWrite} write={write} />
        <div className="flex items-center gap-1.5">
          <Button
            size="sm"
            variant="primary"
            disabled={!canWrite || bench.supported === false}
            onClick={() => setPicker(true)}
            title={bench.supported === false ? 'This version has no such bills' : undefined}
          >
            <Plus className="size-3.5" /> Add bill
          </Button>
          <Button
            size="sm"
            variant="ghost"
            disabled={!canWrite}
            onClick={() => void write('story.workbenches', { op: 'release', pos } as never).then(onClose)}
            title="Stop using this bench: its bills go with it, the builds' crafts move to another bench or wait"
          >
            Release
          </Button>
        </div>
        <BenchTasks tasks={state?.craftTasks} pos={pos} />
        <ReservationList
          reservations={state?.reservations}
          items={billInputs(recipes.data ?? [], bench.bills)}
          canWrite={canWrite}
          write={write}
        />
        <ol className="space-y-1" data-testid="bills">
          {bench.bills.map((b, i) => {
            const r = recipeOf(b.recipe);
            if ((b.forBuild ?? null) !== null)
              return (
                <AutoBillRow
                  key={b.id}
                  bill={b}
                  label={r?.label ?? b.recipe}
                  output={r?.output}
                  state={state}
                  canWrite={canWrite}
                  write={write}
                />
              );
            return (
              <BillRow
                key={b.id}
                bill={b}
                recipe={r}
                bench={bench}
                state={state}
                canWrite={canWrite}
                write={write}
                onDragStart={() => (dragFrom.current = i)}
                onDrop={() => dropBill(i)}
                onEdit={() => setEditing({ bill: b, draft: draftOf(b), key: 0 })}
              />
            );
          })}
        </ol>
      </div>
      <RecipePicker
        open={picker}
        recipes={recipes.data ?? []}
        state={state}
        onClose={() => setPicker(false)}
        onPick={(r) => {
          setPicker(false);
          setEditing({ bill: null, draft: newDraft(r), key: 0 });
        }}
      />
      {editing && !picking ? (
        <BillDialog
          recipe={recipeOf(editing.draft.recipe)}
          initial={editing.draft}
          isNew={!editing.bill}
          state={state}
          key={editing.key}
          onPickChest={pickChest}
          onClose={() => setEditing(null)}
          onSave={(d) => {
            const body = editing.bill ? updateBillBody(editing.bill, d) : addBillBody(pos, d);
            setEditing(null);
            if (body) void write('story.bills', body as never);
          }}
        />
      ) : null}
    </Shell>
  );
}

export function RecipePicker({
  open,
  recipes,
  state,
  onPick,
  onClose,
}: {
  open: boolean;
  recipes: StoryRecipe[];
  state: StoryColonyState | undefined;
  onPick: (r: StoryRecipe) => void;
  onClose: () => void;
}) {
  const [q, setQ] = useState('');
  const tree = useMemo(() => buildTree(state?.categories), [state?.categories]);
  const groups = useMemo(() => searchRecipes(recipes, q, tree), [recipes, q, tree]);
  return (
    <Dialog
      open={open}
      onOpenChange={(o) => !o && onClose()}
      title="Add a bill"
      description="What should be made here?"
      size="md"
    >
      <Input
        aria-label="Search recipes"
        value={q}
        onChange={(e) => setQ(e.target.value)}
        placeholder="Search recipes or ingredients"
        className="mb-2 h-8 text-sm"
        autoFocus
      />
      <div className="max-h-[50vh] space-y-2 overflow-auto" data-testid="recipe-picker">
        {groups.map((g) => (
          <div key={g.group}>
            <div className="mb-0.5 text-[10.5px] font-semibold tracking-wide text-subtle uppercase">{g.label}</div>
            <ul className="space-y-0.5">
              {g.recipes.map((r) => (
                <li key={r.id}>
                  <button
                    type="button"
                    onClick={() => onPick(r)}
                    className="flex w-full items-center gap-2 rounded px-1.5 py-1 text-left text-sm hover:bg-accent-soft"
                    aria-label={`Recipe ${r.label}`}
                  >
                    <ItemIcon id={r.output[0]} meta={r.output[1]} size={22} />
                    <span className="font-medium">{r.label}</span>
                    {r.any ? (
                      <span className="rounded bg-accent-soft px-1 text-[10px] font-semibold uppercase" title={`any ${r.family ?? 'variant'}`}>
                        any
                      </span>
                    ) : null}
                    <span className="ml-auto text-xs text-muted">{recipeLine(r)}</span>
                  </button>
                </li>
              ))}
            </ul>
          </div>
        ))}
        {groups.length === 0 ? <p className="text-sm text-muted">No recipe matches.</p> : null}
      </div>
    </Dialog>
  );
}

function Stepper({
  value,
  onChange,
  min,
  max,
  label,
  step = 1,
}: {
  value: number;
  onChange: (v: number) => void;
  min: number;
  max: number;
  label: string;
  step?: number;
}) {
  return (
    <span className="inline-flex items-center gap-1">
      <button
        type="button"
        aria-label={`${label} −`}
        onClick={(e) => onChange(Math.max(min, value - (e.shiftKey ? step * 10 : step)))}
        className="grid size-6 place-items-center rounded bg-surface-2 hover:bg-surface-3"
      >
        <Minus className="size-3" />
      </button>
      <input
        aria-label={label}
        value={value}
        onChange={(e) => onChange(Math.max(min, Math.min(max, Number(e.target.value) || min)))}
        className="h-6 w-12 rounded border border-border bg-surface text-center text-xs tabular-nums"
      />
      <button
        type="button"
        aria-label={`${label} +`}
        onClick={(e) => onChange(Math.min(max, value + (e.shiftKey ? step * 10 : step)))}
        className="grid size-6 place-items-center rounded bg-surface-2 hover:bg-surface-3"
      >
        <Plus className="size-3" />
      </button>
    </span>
  );
}

type DraftSet = (p: Partial<BillDraft>) => void;
type DraftProps = { d: BillDraft; set: DraftSet; recipe: StoryRecipe | undefined };

const MODE_LABEL: Record<string, string> = { count: 'Do X times', until: 'Do until you have X' };

/** The ingredient tree: only the categories and items the recipe can use. */
function useIngredientTree(recipe: StoryRecipe | undefined, categories: StoryColonyState['categories'] | undefined) {
  return useMemo(() => {
    const full = buildTree(categories);
    const allowed = new Set(
      (recipe?.inputs ?? []).flatMap((i) => [
        ...(i.category ? [i.category] : []),
        ...(i.options ?? []).map(([a, m]) => `${a}:${m}`),
      ]),
    );
    const keep = (n: TreeNode): TreeNode | null => {
      if (allowed.has(n.id)) return n;
      const items = n.items.filter((it) => allowed.has(it.key));
      const children = n.children.map(keep).filter((c): c is TreeNode => !!c);
      return items.length || children.length ? { ...n, items, children } : null;
    };
    return full.map(keep).filter((n): n is TreeNode => !!n);
  }, [recipe, categories]);
}

/** Repeat: the mode (Do X times / until you have X / forever) and its target. */
function BillRepeat({ d, set, recipe }: DraftProps) {
  return (
    <fieldset className="space-y-1.5">
      <legend className="text-xs font-semibold">Repeat</legend>
      <div className="flex flex-wrap items-center gap-2">
        <select
          aria-label="Repeat mode"
          value={d.mode}
          onChange={(e) =>
            set({
              mode: e.target.value as BillDraft['mode'],
              target: e.target.value === 'until' && d.target < 2 ? 64 : d.target,
            })
          }
          className="h-7 rounded border border-border bg-surface-2 px-1 text-xs"
        >
          {recipeModes(recipe).map((m) => (
            <option key={m} value={m}>
              {MODE_LABEL[m] ?? 'Do forever'}
            </option>
          ))}
        </select>
        {d.mode !== 'forever' ? (
          <Stepper label="Target" value={d.target} min={1} max={9999} onChange={(v) => set({ target: v })} />
        ) : null}
        <span className="text-xs text-muted">{billSummary(d)}</span>
      </div>
    </fieldset>
  );
}

/** What the recipe needs: a station's requirements, its fuels, a note, a brewing chain. */
function BillRequires({ recipe, option }: { recipe: StoryRecipe | undefined; option: BillDraft['option'] }) {
  if (!recipe?.requires && !recipe?.fuels?.length) return null;
  return (
    <div className="space-y-1 rounded-md bg-surface-2 px-2 py-1.5 text-xs" data-testid="bill-requires">
      {recipe.requires ? (
        <div>
          <span className="font-semibold">Needs:</span> {requiresLine(recipe, option)}
        </div>
      ) : null}
      {recipe.fuels?.length ? (
        <div data-testid="bill-fuels">
          <span className="font-semibold">Fuel:</span> {fuelTable(recipe)}
        </div>
      ) : null}
      <RecipeNotes recipe={recipe} />
    </div>
  );
}

/** The recipe's note and its brewing steps. */
function RecipeNotes({ recipe }: { recipe: StoryRecipe }) {
  return (
    <>
      {recipe.requires?.note ? <div className="text-muted">{recipe.requires.note}</div> : null}
      {recipe.chain?.length ? (
        <ol className="list-decimal pl-4 text-muted" aria-label="Brewing steps">
          {recipe.chain.map((c) => (
            <li key={c}>{c}</li>
          ))}
        </ol>
      ) : null}
    </>
  );
}

/** A furnace bill's fuel (the furnace's unless set). */
function BillFuel({ d, set, recipe }: DraftProps) {
  if (recipe?.workbench !== 'furnace') return null;
  return (
    <label className="flex items-center gap-2 text-xs">
      <span className="w-28 font-semibold">Fuel</span>
      <select
        aria-label="Bill fuel"
        value={d.fuel ?? ''}
        onChange={(e) => set({ fuel: e.target.value || null })}
        className="h-7 rounded border border-border bg-surface-2 px-1"
      >
        <option value="">The furnace's</option>
        {Object.keys(FUEL_CHOICES).map((f) => (
          <option key={f} value={f}>
            {fuelLabel(f)}
          </option>
        ))}
      </select>
    </label>
  );
}

/** Enchanting: the offer to take up to, a preferred enchantment (only that one). */
function BillEnchanting({ d, set, recipe }: DraftProps) {
  if (recipe?.workbench !== 'enchanting_table') return null;
  return (
    <fieldset className="space-y-1.5 text-xs">
      <legend className="text-xs font-semibold">Enchanting</legend>
      <div className="flex flex-wrap items-center gap-2">
        <label className="flex items-center gap-1">
          Up to offer
          <select
            aria-label="Enchant option"
            value={d.option ?? 3}
            onChange={(e) => set({ option: Number(e.target.value) })}
            className="h-7 rounded border border-border bg-surface-2 px-1"
          >
            <option value={1}>1 (1 lapis, 1 level)</option>
            <option value={2}>2 (2 lapis, 2 levels)</option>
            <option value={3}>3 (3 lapis, 3 levels)</option>
          </select>
        </label>
        <label className="flex items-center gap-1">
          Prefer
          <select
            aria-label="Preferred enchantment"
            value={d.prefer ?? ''}
            onChange={(e) => set({ prefer: e.target.value || null, preferOnly: e.target.value ? d.preferOnly : false })}
            className="h-7 rounded border border-border bg-surface-2 px-1"
          >
            <option value="">The highest offer</option>
            {(recipe.enchantments ?? []).map((e) => (
              <option key={e} value={e}>
                {enchantLabel(e)}
              </option>
            ))}
          </select>
        </label>
        <label className="flex items-center gap-1">
          <input
            type="checkbox"
            checked={d.preferOnly}
            disabled={!d.prefer}
            onChange={(e) => set({ preferOnly: e.target.checked })}
          />{' '}
          Only that one
        </label>
      </div>
      <p className="text-[10.5px] text-muted">
        The table shows its offers once the item is in; a pawn's offers only change after it enchants something.
      </p>
    </fieldset>
  );
}

/** A worker choice: the pawn's skill for the recipe's work (and XP level when it takes levels). */
function workerLabel(p: StoryPawn, recipe: StoryRecipe | undefined): string {
  const work = recipe?.workType ?? 'craft';
  return `${p.display} (${work} ${p.skills[work]?.level ?? 0}${recipe?.requires?.xpLevels ? `, XP ${p.xpLevel ?? 0}` : ''})`;
}

/** Worker: anyone or one pawn, and the minimum skill. */
function BillWorker({ d, set, recipe, pawns }: DraftProps & { pawns: StoryPawn[] }) {
  return (
    <div className="flex flex-wrap items-center gap-2 text-xs">
      <span className="w-28 font-semibold">Worker</span>
      <select
        aria-label="Worker"
        value={d.pawn}
        onChange={(e) => set({ pawn: e.target.value })}
        className="h-7 rounded border border-border bg-surface-2 px-1"
      >
        <option value="any">Anyone</option>
        {pawns.map((p) => (
          <option key={p.name} value={p.name}>
            {workerLabel(p, recipe)}
          </option>
        ))}
      </select>
      <label className="flex items-center gap-1">
        Min skill
        <Stepper
          label="Minimum skill"
          value={d.minSkill ?? 0}
          min={0}
          max={20}
          onChange={(v) => set({ minSkill: v || null })}
        />
      </label>
    </div>
  );
}

/** Store in: the best storage, or a chest picked on the map. */
function BillStore({ d, set, onPick }: { d: BillDraft; set: DraftSet; onPick: () => void }) {
  const best = d.store === 'best';
  return (
    <div className="flex flex-wrap items-center gap-2 text-xs">
      <span className="w-28 font-semibold">Store in</span>
      <select
        aria-label="Store mode"
        value={best ? 'best' : 'chest'}
        onChange={(e) => e.target.value === 'best' && set({ store: 'best' })}
        className="h-7 rounded border border-border bg-surface-2 px-1"
      >
        <option value="best">Best storage</option>
        <option value="chest" disabled={best}>
          {d.store === 'best' ? 'A chest (pick on the map)' : `Chest ${d.store.chest.join(', ')}`}
        </option>
      </select>
      <Button size="sm" variant="ghost" onClick={onPick}>
        Pick a chest…
      </Button>
    </div>
  );
}

export function BillDialog({
  recipe,
  initial,
  isNew,
  state,
  onPickChest,
  onSave,
  onClose,
}: {
  recipe: StoryRecipe | undefined;
  initial: BillDraft;
  isNew: boolean;
  state: StoryColonyState | undefined;
  onPickChest: (current: BillDraft) => void;
  onSave: (d: BillDraft) => void;
  onClose: () => void;
}) {
  const [d, setD] = useState<BillDraft>(initial);
  const set = (p: Partial<BillDraft>) => setD((x) => ({ ...x, ...p }));
  const tree = useIngredientTree(recipe, state?.categories);
  return (
    <Dialog
      open
      onOpenChange={(o) => !o && onClose()}
      title={`${isNew ? 'New bill' : 'Bill'}: ${recipe?.label ?? d.recipe}`}
      description={recipe ? recipeLine(recipe) : undefined}
      size="md"
      footer={
        <>
          <Button variant="ghost" onClick={onClose}>
            Cancel
          </Button>
          <Button variant="primary" onClick={() => onSave(d)}>
            {isNew ? 'Add bill' : 'Save'}
          </Button>
        </>
      }
    >
      <div className="space-y-3 text-sm" data-testid="bill-dialog">
        <BillRepeat d={d} set={set} recipe={recipe} />
        <div className="space-y-1">
          <label className="flex items-center gap-2 text-xs">
            <span className="w-28 font-semibold">Priority</span>
            <BillPrioritySelect value={d.priority} onChange={(priority) => set({ priority })} />
          </label>
          <p className="text-xs text-muted">1 is highest. Applies across workbenches and fuel allocation.</p>
        </div>
        <BillRequires recipe={recipe} option={d.option} />
        <BillFuel d={d} set={set} recipe={recipe} />
        <BillEnchanting d={d} set={set} recipe={recipe} />
        <fieldset>
          <legend className="mb-1 text-xs font-semibold">Ingredients</legend>
          <FilterTree tree={tree} filter={d.ingredients} onChange={(f) => set({ ingredients: f })} />
        </fieldset>
        <label className="flex items-center gap-2 text-xs">
          <span className="w-28 font-semibold">Search radius</span>
          <input
            type="range"
            min={4}
            max={128}
            value={d.radius}
            onChange={(e) => set({ radius: Number(e.target.value) })}
            aria-label="Ingredient search radius"
            className="flex-1 accent-[var(--accent)]"
          />
          <span className="w-16 text-right tabular-nums">{d.radius} blocks</span>
        </label>
        <BillWorker d={d} set={set} recipe={recipe} pawns={state?.pawns ?? []} />
        <BillStore d={d} set={set} onPick={() => onPickChest(d)} />
        <label className="flex items-center gap-2 text-xs">
          <input type="checkbox" checked={d.paused} onChange={(e) => set({ paused: e.target.checked })} /> Paused
        </label>
      </div>
    </Dialog>
  );
}
