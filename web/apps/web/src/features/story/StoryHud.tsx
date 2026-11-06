'use client';

/**
 * The HUD over the map: the colonist bar (one portrait per pawn, then the AI bodies; click, Shift/Ctrl, drag across, double
 * click to jump, long-press and drag to reorder), the alerts stack, the toast, the right-click order menu, the clock / pause /
 * view / level cluster and the active tool's options bar. Panels are plain DOM over the canvas: no blur, cheap to draw.
 */
import {
  tickClock,
  type StoryAiBody,
  type StoryAlert,
  type StoryPawn,
  type StoryTimeControl,
  type StorySpeed,
} from '@mineclaw/contracts';
import {
  AlertTriangle,
  ArrowDown,
  ArrowUp,
  Box,
  Eye,
  EyeOff,
  Info,
  Layers,
  Moon,
  Pause,
  Play,
  Power,
  RotateCcw,
  RotateCw,
  Snowflake,
  Sun,
  WifiOff,
  X,
} from 'lucide-react';
import { useEffect, useRef, useState, type ReactNode } from 'react';
import { Kbd } from '@/components/ui/primitives';
import { ViewerQualityButton } from '@/features/settings/QuickSettings';
import { cn } from '@/lib/utils';
import { useWorldClock, type WorldClock } from './clock';
import { pawnMood, reorderNames, type PawnMood } from './model';
import {
  DESIGNATE_TOOLS,
  MINE_FILTERS,
  HEIGHT_MODES,
  hasHeight,
  type HeightMode,
  ZONE_TOOLS,
  storeyOf,
  type BuildOpts,
  type DesignateId,
  type MenuItem,
} from './tools';
import type { MapTool } from './StoryMap';
import { AiMark, Avatar, ToolIcon } from './visuals';

export const panel = 'rounded-lg border border-border bg-surface/95 text-fg shadow-pop';

// ------------------------------------------------------------------ colonist bar

export interface BarBody {
  name: string;
  label: string;
  kind: 'pawn' | 'ai';
  hp: number;
  food: number;
  job: string;
  drafted: boolean;
  mood: PawnMood;
  pos: readonly number[] | null;
  /** Not in game (a pawn the server kicked, one waiting for a seat, a released colony's). */
  offline: boolean;
  /** Why (the director's `offlineReason`), when offline. */
  offlineReason: string | null;
}

/** The offline pawns of the bar (not the AI bodies). */
export const offlinePawns = (bodies: readonly BarBody[]) => bodies.filter((b) => b.kind === 'pawn' && b.offline);

/** What a pawn's portrait says it is doing. */
function pawnJob(p: StoryPawn, offline: boolean): string {
  if (offline) return 'Offline';
  if (p.job?.label) return p.job.label;
  if (p.drafted) return 'Drafted, awaiting orders';
  return p.state === 'sleeping' ? 'Sleeping' : 'Idle';
}

function pawnBody(p: StoryPawn): BarBody {
  const offline = p.online === false || p.state === 'offline';
  return {
    name: p.name,
    label: p.display,
    kind: 'pawn' as const,
    hp: p.hp ?? 20,
    food: p.food ?? 20,
    job: pawnJob(p, offline),
    drafted: !!p.drafted,
    mood: pawnMood(p),
    pos: p.pos ?? null,
    offline,
    offlineReason: offline ? (p.offlineReason ?? 'offline') : null,
  };
}

function aiBody(a: StoryAiBody): BarBody {
  return {
    name: a.name,
    label: a.kind === 'main' ? 'main' : a.name.replace(/^maid_chan_?/, '') || a.name,
    kind: 'ai' as const,
    hp: a.hp ?? 20,
    food: a.food ?? 20,
    job: a.task ?? a.brainState ?? 'AI',
    drafted: false,
    mood: pawnMood({
      hp: a.hp,
      food: a.food,
      state: a.online === false ? 'offline' : 'working',
      drafted: false,
      online: a.online,
    }),
    pos: a.pos ?? null,
    offline: a.online === false,
    offlineReason: null,
  };
}

export function barBodies(pawns: readonly StoryPawn[], ai: readonly StoryAiBody[]): BarBody[] {
  return [...pawns.map(pawnBody), ...ai.map(aiBody)];
}

const MOOD_COLOR: Record<PawnMood, string> = {
  danger: 'bg-danger',
  idle: 'bg-warn',
  busy: 'bg-ok',
  drafted: 'bg-[#ff9f43]',
  offline: 'bg-subtle',
};

/** A pointer held on a portrait: a click, a double click, a box-select drag, or (held still) a lift to reorder. */
type Press = {
  name: string;
  x: number;
  y: number;
  t: number;
  timer: ReturnType<typeof setTimeout> | null;
  lifted: boolean;
  moved: boolean;
  shift: boolean;
  ctrl: boolean;
  id: number;
};
type Lift = { name: string; x: number; top: number; height: number };
type BarBox = { x0: number; x1: number; top: number };

type GestureProps = {
  bodies: BarBody[];
  pawns: BarBody[];
  order: readonly string[];
  onOrder: (next: string[]) => void;
  onClick: (name: string, mods: { shift: boolean; ctrl: boolean }) => void;
  onBox: (names: string[], add: boolean) => void;
  onJump: (name: string) => void;
};

/** The pawn order a lifted portrait dropped at `x` makes: before the pawn portrait under the pointer, else last. */
function droppedOrder(row: HTMLElement | null, p: Press, x: number, order: readonly string[], pawns: BarBody[]): string[] {
  const target = [...(row?.querySelectorAll<HTMLElement>('[data-portrait][data-kind="pawn"]') ?? [])].find((el) => {
    const r = el.getBoundingClientRect();
    return x < r.left + r.width / 2;
  });
  const names = order.length ? order : pawns.map((b) => b.name);
  const full = [...names, ...pawns.map((b) => b.name).filter((n) => !names.includes(n))];
  const before = target?.dataset.portrait ?? null;
  return reorderNames(full, p.name, before === p.name ? null : before);
}

/** The portraits between two x positions (a box-select drag). */
function coveredNames(row: HTMLElement | null, x0: number, x1: number): string[] {
  const lo = Math.min(x0, x1),
    hi = Math.max(x0, x1);
  return [...(row?.querySelectorAll<HTMLElement>('[data-portrait]') ?? [])]
    .filter((el) => {
      const r = el.getBoundingClientRect();
      return r.right >= lo && r.left <= hi;
    })
    .map((el) => el.dataset.portrait!);
}

/** The colonist bar's pointer gestures: click, Shift/Ctrl, double click to jump, drag across, long-press and drag to reorder. */
function useBarGestures({ bodies, pawns, order, onOrder, onClick, onBox, onJump }: GestureProps) {
  const row = useRef<HTMLDivElement>(null);
  const [box, setBox] = useState<BarBox | null>(null);
  const [lift, setLift] = useState<Lift | null>(null);
  const [pressing, setPressing] = useState(false);
  const press = useRef<Press | null>(null);
  const last = useRef<{ name: string; t: number } | null>(null);

  const liftAt = (name: string, x: number) => {
    const r = row.current?.getBoundingClientRect();
    setLift({ name, x, top: r?.top ?? 0, height: r?.height ?? 60 });
  };

  const down = (e: React.PointerEvent, name: string) => {
    if (e.button !== 0) return;
    (e.currentTarget as HTMLElement).setPointerCapture(e.pointerId);
    const p: Press = {
      name,
      x: e.clientX,
      y: e.clientY,
      t: e.timeStamp,
      timer: null,
      lifted: false,
      moved: false,
      shift: e.shiftKey,
      ctrl: e.ctrlKey || e.metaKey,
      id: e.pointerId,
    };
    // Hold still for a moment to lift the portrait (then drag to reorder); a quick drag box-selects.
    p.timer = setTimeout(() => {
      if (press.current !== p || p.moved || bodies.find((b) => b.name === name)?.kind !== 'pawn') return;
      p.lifted = true;
      liftAt(name, e.clientX);
    }, 320);
    press.current = p;
    setPressing(true);
  };

  const move = (e: React.PointerEvent) => {
    const p = press.current;
    if (!p) return;
    if (!p.moved && Math.abs(e.clientX - p.x) + Math.abs(e.clientY - p.y) > 5) {
      p.moved = true;
      if (p.timer) clearTimeout(p.timer);
    }
    if (p.lifted) liftAt(p.name, e.clientX);
    else if (p.moved) setBox({ x0: p.x, x1: e.clientX, top: (row.current?.getBoundingClientRect().bottom ?? 2) - 2 });
  };

  /** A press that didn't move: a click, or the second click of a double click (jump). */
  const tap = (p: Press, now: number) => {
    if (last.current && last.current.name === p.name && now - last.current.t < 350) {
      last.current = null;
      onJump(p.name);
      return;
    }
    last.current = { name: p.name, t: now };
    onClick(p.name, { shift: p.shift, ctrl: p.ctrl });
  };

  const up = (e: React.PointerEvent) => {
    const p = press.current;
    press.current = null;
    setPressing(false);
    if (!p) return;
    if (p.timer) clearTimeout(p.timer);
    setBox(null);
    setLift(null);
    if (p.lifted) {
      if (p.moved) onOrder(droppedOrder(row.current, p, e.clientX, order, pawns));
      return;
    }
    if (p.moved) onBox(coveredNames(row.current, p.x, e.clientX), p.shift);
    else tap(p, e.timeStamp);
  };

  return { row, box, lift, pressing, down, move, up };
}

type PortraitHandlers = {
  onDown: (e: React.PointerEvent, name: string) => void;
  onMove: (e: React.PointerEvent) => void;
  onUp: (e: React.PointerEvent) => void;
  onEnter: (name: string) => void;
  onLeave: (name: string) => void;
};

function portraitClass(b: BarBody, selected: boolean, lifted: boolean): string {
  return cn(
    'relative flex cursor-pointer flex-col items-center gap-0.5 rounded-md px-1 pt-1 pb-1 transition-colors select-none',
    b.kind === 'ai' ? 'w-[48px]' : 'w-[58px]',
    selected ? 'bg-[#f5c542]/20 ring-2 ring-[#f5c542]' : 'hover:bg-surface-2',
    lifted && 'opacity-40',
    b.offline && !lifted && 'opacity-60',
  );
}

/** One body's portrait: avatar and badges, mood dot, name, health and food, and the tooltip while hovered. */
function Portrait({
  b,
  selected,
  lifted,
  tip,
  h,
}: {
  b: BarBody;
  selected: boolean;
  lifted: boolean;
  tip: boolean;
  h: PortraitHandlers;
}) {
  return (
    <div
      data-portrait={b.name}
      data-kind={b.kind}
      data-drafted={b.drafted}
      role="button"
      tabIndex={-1}
      aria-pressed={selected}
      aria-label={b.offline ? `${b.label}: offline, ${b.offlineReason}` : `${b.label}: ${b.job}`}
      data-offline={b.offline || undefined}
      onPointerDown={(e) => h.onDown(e, b.name)}
      onPointerMove={h.onMove}
      onPointerUp={h.onUp}
      onPointerEnter={() => h.onEnter(b.name)}
      onPointerLeave={() => h.onLeave(b.name)}
      className={portraitClass(b, selected, lifted)}
    >
      <PortraitFace b={b} />
      <span
        className={cn('absolute top-1 right-1 size-2 rounded-full ring-1 ring-black/40', MOOD_COLOR[b.mood])}
        aria-hidden
      />
      <span
        className={cn(
          'max-w-full truncate text-[10px] leading-tight font-semibold',
          b.mood === 'danger' ? 'text-danger' : 'text-fg',
        )}
      >
        {b.label}
      </span>
      <StatBar title={`Health ${b.hp}/20`} colour="bg-[#e5484d]" value={b.hp} />
      <StatBar title={`Food ${Math.round(b.food)}/20`} colour="bg-[#e2a336]" value={b.food} />
      {tip ? <PortraitTip b={b} /> : null}
    </div>
  );
}

function PortraitFace({ b }: { b: BarBody }) {
  const ai = b.kind === 'ai';
  return (
    <div
      className={cn(
        'relative overflow-hidden rounded-[5px] ring-1',
        ai ? 'ring-[#a78bfa]' : 'ring-black/30',
        b.mood === 'offline' && 'grayscale',
      )}
    >
      <Avatar name={b.name} size={ai ? 28 : 34} ai={ai} />
      {ai ? (
        <span
          className="absolute -right-0.5 -bottom-0.5 grid size-3.5 place-items-center rounded-sm bg-[#a78bfa] text-[#1b1530]"
          title="AI-driven"
        >
          <AiMark className="size-2.5" />
        </span>
      ) : null}
      {b.offline ? (
        <span
          className="absolute -bottom-0.5 -left-0.5 grid size-3.5 place-items-center rounded-sm bg-surface-3 text-muted ring-1 ring-border"
          title={`Offline: ${b.offlineReason}`}
          data-testid="offline-badge"
        >
          <WifiOff className="size-2.5" aria-hidden />
        </span>
      ) : null}
      {b.drafted ? (
        <span className="absolute top-0 left-0 rounded-br bg-[#ff9f43] px-0.5 text-[8px] leading-3 font-bold text-[#2a1600]">
          D
        </span>
      ) : null}
    </div>
  );
}

/** Health or food out of 20 as a thin bar. */
function StatBar({ title, colour, value }: { title: string; colour: string; value: number }) {
  return (
    <span className="block h-1 w-full overflow-hidden rounded-full bg-black/25" title={title}>
      <span className={cn('block h-full', colour)} style={{ width: `${Math.max(0, Math.min(1, value / 20)) * 100}%` }} />
    </span>
  );
}

function PortraitTip({ b }: { b: BarBody }) {
  return (
    <div
      className="pointer-events-none absolute top-full left-1/2 z-30 mt-1 w-max max-w-56 -translate-x-1/2 rounded-md border border-border bg-surface px-2 py-1 text-left text-[11px] shadow-pop"
      role="tooltip"
    >
      <div className="font-semibold">
        {b.label} {b.kind === 'ai' ? <span className="text-[#a78bfa]">· AI</span> : null}
      </div>
      {b.offline ? (
        <div className="text-muted" data-testid="offline-reason">
          Offline: {b.offlineReason}
        </div>
      ) : (
        <div className="text-muted">{b.job}</div>
      )}
    </div>
  );
}

export function ColonistBar({
  bodies,
  selected,
  order,
  onOrder,
  onClick,
  onBox,
  onJump,
  onBringAll,
  pending = 0,
  pendingReason = null,
  onAdd,
  onRemove,
}: {
  bodies: BarBody[];
  selected: readonly string[];
  order: readonly string[];
  onOrder: (next: string[]) => void;
  onClick: (name: string, mods: { shift: boolean; ctrl: boolean }) => void;
  onBox: (names: string[], add: boolean) => void;
  onJump: (name: string) => void;
  /** Shown while any pawn is offline: bring every offline pawn in (null = no button: viewers can't). */
  onBringAll?: (() => void) | null;
  /** Pawns added that aren't here yet: greyed "joining…" tiles (one "+N waiting" tile when many: there is no upper limit). */
  pending?: number;
  /** Why they wait (the seats line's reason: "server full (20 slots, 2 kept for …)"), shown on hover. */
  pendingReason?: string | null;
  /** + : add pawns (shift-click asks how many; null = no button: viewers can't). */
  onAdd?: ((n: number) => void) | null;
  /** − : remove the one selected pawn, after a confirm (null = no button). */
  onRemove?: ((name: string) => void) | null;
}) {
  const [addMany, setAddMany] = useState<number | null>(null);
  const [confirmRemove, setConfirmRemove] = useState<string | null>(null);
  const [hover, setHover] = useState<string | null>(null);
  const sel = new Set(selected);
  const pawns = bodies.filter((b) => b.kind === 'pawn');
  const ai = bodies.filter((b) => b.kind === 'ai');
  const { row, box, lift, pressing, down, move, up } = useBarGestures({
    bodies,
    pawns,
    order,
    onOrder,
    onClick,
    onBox,
    onJump,
  });
  const h: PortraitHandlers = {
    onDown: down,
    onMove: move,
    onUp: up,
    onEnter: setHover,
    onLeave: (name) => setHover((x) => (x === name ? null : x)),
  };
  const portrait = (b: BarBody) => (
    <Portrait
      key={b.name}
      b={b}
      selected={sel.has(b.name)}
      lifted={lift?.name === b.name}
      tip={hover === b.name && !pressing}
      h={h}
    />
  );

  return (
    <div
      className={cn(
        panel,
        'pointer-events-auto relative flex max-w-full flex-wrap items-start justify-center gap-1 px-1.5 py-1',
      )}
      ref={row}
      data-testid="colonist-bar"
      aria-label="Colonists"
    >
      {pawns.map(portrait)}
      <JoiningTiles pending={pending} reason={pendingReason} />
      {onAdd || onRemove ? (
        <BarControls
          pawns={pawns}
          sel={sel}
          onAdd={onAdd}
          onRemove={onRemove}
          addMany={addMany}
          setAddMany={setAddMany}
          confirmRemove={confirmRemove}
          setConfirmRemove={setConfirmRemove}
        />
      ) : null}
      {onBringAll ? <BringAllTile bodies={bodies} onBringAll={onBringAll} /> : null}
      {ai.length ? <div className="mx-1 w-px self-stretch bg-border" aria-hidden /> : null}
      {ai.map(portrait)}
      <DragMarks box={box} lift={lift} />
    </div>
  );
}

/** Up to this many pawns waiting show a tile each; more show as one "+N waiting" tile. */
export const JOINING_TILES_MAX = 4;

const joiningTile =
  'flex h-[58px] w-[58px] flex-col items-center justify-center gap-0.5 rounded-md border border-dashed border-border text-[10px] text-subtle opacity-70 grayscale';

/** Greyed "joining…" tiles for pawns added that aren't here yet; many collapse into one "+N waiting" tile. */
function JoiningTiles({ pending, reason }: { pending: number; reason?: string | null }) {
  const why = reason ? `: ${reason}` : '';
  if (pending > JOINING_TILES_MAX)
    return (
      <div
        className={cn(joiningTile, 'leading-tight')}
        title={`${pending} pawns waiting to join: they come as room frees up${why}`}
        data-testid="bar-joining"
        data-count={pending}
      >
        <span className="text-sm font-semibold tabular-nums">+{pending}</span>
        waiting
      </div>
    );
  return Array.from({ length: Math.max(0, pending) }, (_, i) => (
    <div
      key={`joining-${i}`}
      className={joiningTile}
      title={`A new pawn joining: it comes as soon as a seat is free${why}`}
      data-testid="bar-joining"
    >
      <Avatar name={`joining-${i}`} size={24} />
      joining…
    </div>
  ));
}

function BringAllTile({ bodies, onBringAll }: { bodies: readonly BarBody[]; onBringAll: () => void }) {
  const n = offlinePawns(bodies).length;
  if (!n) return null;
  return (
    <button
      type="button"
      onClick={onBringAll}
      className="flex h-[58px] w-[58px] flex-col items-center justify-center gap-0.5 rounded-md border border-dashed border-border-strong text-[10px] leading-tight font-semibold text-muted hover:bg-surface-2 hover:text-fg"
      title="Bring every offline pawn online (main whitelists them again; the player cap permitting)"
      data-testid="bar-bring-online"
    >
      <Power className="size-3.5" aria-hidden />
      Bring {n} online
    </button>
  );
}

/** The box-select line and the reorder drop marker while dragging. */
function DragMarks({ box, lift }: { box: BarBox | null; lift: Lift | null }) {
  return (
    <>
      {box ? (
        <div
          className="pointer-events-none fixed top-0 h-0 border-t-2 border-[#f5c542]"
          style={{ left: Math.min(box.x0, box.x1), width: Math.abs(box.x1 - box.x0), top: box.top }}
        />
      ) : null}
      {lift ? (
        <div
          className="pointer-events-none fixed z-40 w-0.5 bg-[#f5c542]"
          style={{ left: lift.x, top: lift.top, height: lift.height }}
        />
      ) : null}
    </>
  );
}

/** + (Shift: how many) and − (the one selected pawn, after a confirm). */
function BarControls({
  pawns,
  sel,
  onAdd,
  onRemove,
  addMany,
  setAddMany,
  confirmRemove,
  setConfirmRemove,
}: {
  pawns: BarBody[];
  sel: ReadonlySet<string>;
  onAdd?: ((n: number) => void) | null;
  onRemove?: ((name: string) => void) | null;
  addMany: number | null;
  setAddMany: (n: number | null) => void;
  confirmRemove: string | null;
  setConfirmRemove: (name: string | null) => void;
}) {
  const chosen = pawns.filter((p) => sel.has(p.name));
  return (
    <div className="relative flex h-[58px] flex-col justify-center gap-1">
      {onAdd ? (
        <button
          type="button"
          onClick={(e) => (e.shiftKey ? setAddMany(2) : onAdd(1))}
          className="grid size-6 place-items-center rounded-md border border-border text-sm font-bold text-muted hover:bg-surface-2 hover:text-fg"
          title="Add a pawn (Shift+click: add several)"
          aria-label="Add a pawn"
          data-testid="bar-add-pawn"
        >
          +
        </button>
      ) : null}
      {onRemove ? (
        <button
          type="button"
          disabled={chosen.length !== 1 || pawns.length <= 1}
          onClick={() => setConfirmRemove(chosen[0]?.name ?? null)}
          className="grid size-6 place-items-center rounded-md border border-border text-sm font-bold text-muted hover:bg-surface-2 hover:text-fg disabled:opacity-40"
          title="Remove the selected pawn (its items go to storage)"
          aria-label="Remove the selected pawn"
          data-testid="bar-remove-pawn"
        >
          −
        </button>
      ) : null}
      {addMany !== null && onAdd ? <AddManyBox n={addMany} setN={setAddMany} onAdd={onAdd} /> : null}
      {confirmRemove && onRemove ? (
        <RemoveConfirm
          name={confirmRemove}
          label={pawns.find((p) => p.name === confirmRemove)?.label ?? confirmRemove}
          onRemove={onRemove}
          onDone={() => setConfirmRemove(null)}
        />
      ) : null}
    </div>
  );
}

function AddManyBox({ n, setN, onAdd }: { n: number; setN: (n: number | null) => void; onAdd: (n: number) => void }) {
  return (
    <div className="absolute top-full left-0 z-40 mt-1 flex items-center gap-1 rounded-md border border-border bg-surface p-1.5 text-[11px] shadow-pop">
      Add
      <input
        type="number"
        min={1}
        value={n}
        aria-label="How many pawns"
        onChange={(e) => setN(Math.max(1, Math.floor(Number(e.target.value)) || 1))}
        className="w-12 rounded border border-border bg-surface-2 px-1"
      />
      <button
        type="button"
        className="rounded bg-accent px-1.5 text-accent-fg"
        onClick={() => {
          onAdd(n);
          setN(null);
        }}
      >
        Add
      </button>
      <button type="button" className="text-muted" onClick={() => setN(null)}>
        Cancel
      </button>
    </div>
  );
}

function RemoveConfirm({
  name,
  label,
  onRemove,
  onDone,
}: {
  name: string;
  label: string;
  onRemove: (name: string) => void;
  onDone: () => void;
}) {
  return (
    <div
      className="absolute top-full left-0 z-40 mt-1 w-max max-w-64 space-y-1 rounded-md border border-border bg-surface p-2 text-[11px] shadow-pop"
      role="dialog"
      aria-label="Remove a pawn"
      data-testid="remove-pawn-confirm"
    >
      <div>Remove {label}? Their items go to storage.</div>
      <div className="flex justify-end gap-2">
        <button type="button" className="text-muted" onClick={onDone}>
          Cancel
        </button>
        <button
          type="button"
          className="rounded bg-danger px-1.5 text-white"
          onClick={() => {
            onRemove(name);
            onDone();
          }}
        >
          Remove
        </button>
      </div>
    </div>
  );
}

// ------------------------------------------------------------------ clock, pause, view, level

/** The speed buttons (RimWorld's ▶ / ▶▶ / ▶▶▶) and what each one does. */
type SpeedButton = { id: StorySpeed; glyph: string; label: string; key: string; hint: string };
export const SPEED_BUTTONS: readonly SpeedButton[] = [
  { id: 'normal', glyph: '▶', label: 'Normal speed', key: '1', hint: 'Pawns walk and sprint at normal speed' },
  {
    id: 'fast',
    glyph: '▶▶',
    label: 'Fast',
    key: '2',
    hint: "Pawns move about twice as fast: a movement-speed boost for the colony's pawns only (not time: the server owns the clock)",
  },
  {
    id: 'faster',
    glyph: '▶▶▶',
    label: 'Faster',
    key: '3',
    hint: "Pawns move about three times as fast: a movement-speed boost for the colony's pawns only; it steps down by itself if the server rejects it",
  },
];

/**
 * The pawns' movement speed as a segmented control beside Pause: ▶ normal, ▶▶ fast, ▶▶▶ faster (keys 1 / 2 / 3 with no tool in
 * hand). A pawn the server pushed back runs slower than asked: the control shows the level the pawns actually run at as a dim mark.
 */
export function SpeedControl({
  speed,
  effective,
  canWrite,
  onSpeed,
}: {
  speed: StorySpeed;
  /** The slowest level a pawn runs at (a stepped-down body), when below `speed`. */
  effective?: StorySpeed | null;
  canWrite: boolean;
  onSpeed: (s: StorySpeed) => void;
}) {
  return (
    <div
      role="radiogroup"
      aria-label="Pawn speed"
      className="flex items-center rounded bg-surface-2"
      data-testid="speed-control"
    >
      {SPEED_BUTTONS.map((b) => {
        const on = b.id === speed;
        const held = effective && effective !== speed && b.id === effective;
        return (
          <button
            key={b.id}
            type="button"
            role="radio"
            aria-checked={on}
            aria-label={b.label}
            disabled={!canWrite}
            onClick={() => onSpeed(b.id)}
            title={`${b.label} (${b.key}): ${b.hint}${held ? ' — some pawns run at this speed: the server rejected faster movement' : ''}`}
            data-speed={b.id}
            className={cn(
              'inline-flex h-6 min-w-6 items-center justify-center rounded px-1 text-[10px] leading-none tracking-[-0.15em] tabular-nums',
              on ? 'bg-accent-soft text-accent' : 'hover:bg-surface-3',
              held && 'ring-1 ring-warn ring-inset',
            )}
          >
            {b.glyph}
          </button>
        );
      })}
    </div>
  );
}

export function ControlCluster({
  clock,
  name,
  paused,
  canWrite,
  onPause,
  speed,
  speedEffective,
  onSpeed,
  view,
  onView,
  level,
  ground,
  height,
  xray,
  onLevel,
  onXray,
  onHelp,
  children,
}: {
  /** The page's world clock (else the context's). */
  clock?: WorldClock | null;
  name: string;
  paused: boolean;
  canWrite: boolean;
  onPause: () => void;
  /** The pawns' movement speed (absent = no speed control). */
  speed?: StorySpeed;
  speedEffective?: StorySpeed | null;
  onSpeed?: (s: StorySpeed) => void;
  view: 'top' | 'angled';
  onView: () => void;
  level: number;
  ground: number;
  height: number;
  xray: boolean;
  onLevel: (dir: 1 | -1) => void;
  onXray: () => void;
  onHelp: () => void;
  /** Extra rows (the time of day control). */
  children?: ReactNode;
}) {
  return (
    <div className={cn(panel, 'pointer-events-auto flex flex-col gap-1.5 p-2 text-xs')} data-testid="story-controls">
      <div className="flex items-center gap-2">
        <span className="max-w-36 truncate text-[13px] font-semibold">{name}</span>
        <ColonyClock clock={clock} />
        <PauseButton paused={paused} canWrite={canWrite} onPause={onPause} />
        {speed && onSpeed ? (
          <SpeedControl speed={speed} effective={speedEffective} canWrite={canWrite} onSpeed={onSpeed} />
        ) : null}
      </div>
      <div className="flex items-center gap-1.5">
        <ViewButton view={view} onView={onView} />
        <div className="flex items-center rounded bg-surface-2" data-testid="level-widget">
          <button
            type="button"
            onClick={() => onLevel(-1)}
            className="grid size-6 place-items-center rounded hover:bg-surface-3"
            aria-label="Level down (PgDn)"
            title="Level down (PgDn; Shift: one block)"
          >
            <ArrowDown className="size-3.5" />
          </button>
          <span className="px-1 tabular-nums" aria-live="polite">
            Storey {storeyOf(level, ground, height)} · Y {level}
          </span>
          <button
            type="button"
            onClick={() => onLevel(1)}
            className="grid size-6 place-items-center rounded hover:bg-surface-3"
            aria-label="Level up (PgUp)"
            title="Level up (PgUp; Shift: one block)"
          >
            <ArrowUp className="size-3.5" />
          </button>
        </div>
        <XrayButton xray={xray} onXray={onXray} />
        <button
          type="button"
          onClick={onHelp}
          className="grid size-6 place-items-center rounded hover:bg-surface-2"
          title="Controls (?)"
          aria-label="Controls"
        >
          <Info className="size-3.5" />
        </button>
        <ViewerQualityButton compact />
      </div>
      {children}
    </div>
  );
}

function PauseButton({ paused, canWrite, onPause }: { paused: boolean; canWrite: boolean; onPause: () => void }) {
  return (
    <button
      type="button"
      onClick={onPause}
      disabled={!canWrite}
      aria-pressed={paused}
      className={cn(
        'ml-auto inline-flex h-6 items-center gap-1 rounded px-1.5 font-medium',
        paused ? 'bg-warn-soft text-warn' : 'hover:bg-surface-2',
      )}
      title="Pause the colony: every pawn holds (Space)"
    >
      {paused ? <Play className="size-3.5" /> : <Pause className="size-3.5" />}
      {paused ? 'Paused' : 'Pause'}
    </button>
  );
}

function ViewButton({ view, onView }: { view: 'top' | 'angled'; onView: () => void }) {
  const top = view === 'top';
  return (
    <button
      type="button"
      onClick={onView}
      className="inline-flex h-6 items-center gap-1 rounded bg-surface-2 px-1.5 font-medium hover:bg-surface-3"
      title="Top-down ↔ angled view (V)"
      aria-label={top ? 'Switch to the angled view' : 'Switch to the top-down view'}
    >
      {top ? <Box className="size-3.5" /> : <Layers className="size-3.5" />}
      {top ? 'Top-down' : 'Angled'}
    </button>
  );
}

function XrayButton({ xray, onXray }: { xray: boolean; onXray: () => void }) {
  return (
    <button
      type="button"
      onClick={onXray}
      aria-pressed={xray}
      className={cn('grid size-6 place-items-center rounded', xray ? 'bg-accent-soft text-accent' : 'hover:bg-surface-2')}
      title={xray ? 'Roofs x-rayed (X)' : 'Show everything above this level (X)'}
      aria-label="X-ray above the level"
    >
      {xray ? <EyeOff className="size-3.5" /> : <Eye className="size-3.5" />}
    </button>
  );
}

/**
 * The colony clock (the page's one world clock: the server's time, advanced between samples): HH:MM, the sun or the moon
 * (night 19:00–05:59, as the bot's schedule sees it), a snowflake while the server's daylight cycle is stopped, dimmed when
 * no sample came for a minute.
 */
function ColonyClock({ clock }: { clock?: WorldClock | null }) {
  const c = useWorldClock(clock);
  const title = !c.known
    ? "Waiting for the server's time"
    : [
        'World time (the server owns the clock)',
        c.frozen ? "Time is frozen: the server's daylight cycle is stopped" : null,
        c.stale ? 'No time from the server for over a minute: this is the last one heard' : null,
      ]
        .filter(Boolean)
        .join('\n');
  return (
    <span
      className={cn('flex items-center gap-1 tabular-nums text-muted', c.stale && 'opacity-50')}
      title={title}
      data-testid="colony-clock"
      data-night={c.known ? String(c.night) : undefined}
      data-frozen={c.frozen ? 'true' : undefined}
      data-stale={c.stale ? 'true' : undefined}
    >
      {c.night ? <Moon className="size-3.5" aria-label="night" /> : <Sun className="size-3.5 text-warn" aria-label="day" />}
      <span data-testid="colony-time">{c.text}</span>
      {c.frozen ? <Snowflake className="size-3.5 text-accent" aria-label="time is frozen" /> : null}
    </span>
  );
}

/** Day marks on the time slider (ticks; 0 = 06:00, the Minecraft convention). */
const DAY_MARKS: Array<[number, string]> = [
  [0, 'dawn'],
  [6000, 'noon'],
  [12000, 'dusk'],
  [18000, 'midnight'],
];

/**
 * The world's time of day as a slider over one day (0..23999 ticks). It follows the colony clock; a drag shows the time it
 * would set and commits once on release (debounced), through the bot's op account. Without one it stays disabled with the
 * reason.
 */
export function TimeSlider({
  clock: given,
  control,
  canWrite,
  onSet,
}: {
  clock?: WorldClock | null;
  control: StoryTimeControl | null | undefined;
  canWrite: boolean;
  onSet: (tick: number) => Promise<unknown>;
}) {
  const [held, setHeld] = useState<number | null>(null);
  const timer = useRef<ReturnType<typeof setTimeout> | null>(null);
  const live = useRef<number | null>(null);
  useEffect(() => () => void (timer.current && clearTimeout(timer.current)), []);
  const available = !!control?.available;
  // It follows the page's world clock (the server's time); a drag holds what it would set until the change is through.
  const clock = useWorldClock(given);
  const value = held ?? Math.max(0, Math.min(23999, Math.round(clock.ticks)));
  const commit = () => {
    if (live.current === null) return;
    if (timer.current) clearTimeout(timer.current);
    timer.current = setTimeout(() => {
      const t = live.current;
      live.current = null;
      if (t === null) return;
      void onSet(t).finally(() => setHeld((h) => (h === t ? null : h)));
    }, 300);
  };
  const hint = !available
    ? 'No op account — op the main bot to change time'
    : !canWrite
      ? 'Changing the time needs the story role'
      : `Set the time of day via ${control?.by ?? 'the main bot'} (op)`;
  return (
    <div className="space-y-0.5" data-testid="time-control" title={hint}>
      <div className="flex items-center gap-2">
        <span className="w-10 shrink-0 tabular-nums font-medium">{tickClock(value)}</span>
        <input
          type="range"
          min={0}
          max={23999}
          step={50}
          value={value}
          disabled={!available || !canWrite}
          aria-label="Time of day"
          aria-valuetext={tickClock(value)}
          onChange={(e) => {
            const t = Number(e.target.value);
            live.current = t;
            setHeld(t);
          }}
          onPointerUp={commit}
          onKeyUp={commit}
          className="h-1.5 flex-1 cursor-pointer accent-[#f5c542] disabled:cursor-not-allowed disabled:opacity-50"
        />
      </div>
      <div className="relative ml-12 h-3 text-[9.5px] text-subtle" aria-hidden>
        {DAY_MARKS.map(([t, name]) => (
          <span
            key={name}
            className="absolute -translate-x-1/2 whitespace-nowrap first:translate-x-0"
            style={{ left: `${(t / 24000) * 100}%` }}
          >
            {name}
          </span>
        ))}
      </div>
      {!available ? <div className="text-[10.5px] text-warn">{hint}</div> : null}
      {clock.frozen ? (
        <div className="text-[10.5px] text-muted" data-testid="time-frozen">
          Time is frozen: the server&apos;s daylight cycle is stopped
        </div>
      ) : null}
    </div>
  );
}

// ------------------------------------------------------------------ the active tool's options

/**
 * The height sub-bar of Mine and Deconstruct: Surface · Level · Down to level · Flatten here · Layers (1–5 while the tool is
 * active), the Layers stepper (+ / −) and the working level the level modes go to.
 */
function HeightModes({
  mode,
  onMode,
  layers,
  onLayers,
  level,
}: {
  mode: HeightMode;
  onMode?: (m: HeightMode) => void;
  layers: number;
  onLayers?: (n: number) => void;
  level?: number;
}) {
  const levelled = mode === 'level' || mode === 'down' || mode === 'flatten';
  return (
    <span className="flex flex-wrap items-center gap-1" role="radiogroup" aria-label="How deep" data-testid="height-modes">
      {HEIGHT_MODES.map((h, i) => (
        <button
          key={h.id}
          type="button"
          role="radio"
          aria-checked={mode === h.id}
          title={`${h.hint} (${i + 1})`}
          onClick={() => onMode?.(h.id)}
          className={cn(
            'h-6 rounded border px-1.5',
            mode === h.id
              ? 'border-accent bg-accent/15 font-semibold text-fg'
              : 'border-border bg-surface-2 text-muted hover:bg-surface-3',
          )}
        >
          <span className="mr-1 text-[10px] text-muted">{i + 1}</span>
          {h.label}
        </button>
      ))}
      {mode === 'layers' ? (
        <span className="flex items-center gap-1" title="Layers deep (+ / −)">
          <button
            type="button"
            aria-label="Fewer layers"
            className="grid size-5 place-items-center rounded bg-surface-2 hover:bg-surface-3"
            onClick={() => onLayers?.(layers - 1)}
          >
            −
          </button>
          <span className="w-4 text-center font-semibold tabular-nums" aria-label="Layers">
            {layers}
          </span>
          <button
            type="button"
            aria-label="More layers"
            className="grid size-5 place-items-center rounded bg-surface-2 hover:bg-surface-3"
            onClick={() => onLayers?.(layers + 1)}
          >
            +
          </button>
        </span>
      ) : null}
      {levelled && level !== undefined ? (
        <span className="text-muted" data-testid="height-level">
          level y {level} (PgUp / PgDn)
        </span>
      ) : null}
    </span>
  );
}

type OptionsTool = Exclude<MapTool, { kind: 'select' } | { kind: 'build' }>;
type ToolHeading = { name: string | undefined; hint: string; icon: string };

/** The options bar's title, hint and icon for each tool kind. */
const HEADINGS: { [K in OptionsTool['kind']]: (t: Extract<OptionsTool, { kind: K }>) => ToolHeading } = {
  designate: (t) => {
    const d = DESIGNATE_TOOLS.find((x) => x.id === t.id);
    return { name: d?.label, hint: `${d?.hint}. Drag a box; Shift keeps the tool.`, icon: t.id };
  },
  zone: (t) => ({
    name: `${t.op === 'remove' ? 'Remove ' : ''}${ZONE_TOOLS.find((z) => z.id === t.id)?.label ?? 'Zone'}`,
    hint: 'Drag to paint the zone',
    icon: 'zone',
  }),
  storage: () => ({ name: 'Register chests', hint: 'Drag over chests to make them storage', icon: 'storage' }),
  home: () => ({
    name: 'Set home',
    hint: 'Click where home should be: the colony migrates there (you confirm first)',
    icon: 'home',
  }),
  design: (t) => ({
    name: `Design: ${t.name}`,
    hint: `${t.blocks.length} blocks: click to queue it here (Q/E turns, PgUp/PgDn changes the level)`,
    icon: 'block',
  }),
};

function toolHeadingOf(tool: OptionsTool): ToolHeading {
  return (HEADINGS[tool.kind] as (t: OptionsTool) => ToolHeading)(tool);
}

export function OptionsBar({
  tool,
  build,
  mineFilter,
  onMineFilter,
  heightMode = 'surface',
  onHeightMode,
  layers = 2,
  onLayers,
  level,
  stalled = 0,
  onRetryAll,
  onClose,
}: {
  tool: MapTool;
  build: BuildOpts;
  mineFilter: string;
  onMineFilter: (f: 'all' | 'ores' | 'stone' | 'soil') => void;
  /** Mine / Deconstruct: how deep a drag goes (1–5 while the tool is active), and the Layers count (+ / −). */
  heightMode?: HeightMode;
  onHeightMode?: (m: HeightMode) => void;
  layers?: number;
  onLayers?: (n: number) => void;
  /** The working level (PgUp / PgDn): what Level, Down to level and Flatten here go to. */
  level?: number;
  /** Retry tool: how many designations are failed or resting colony-wide, and the "retry them all" action. */
  stalled?: number;
  onRetryAll?: () => void;
  onClose: () => void;
}) {
  // Structure and furniture tools have the docked material selector instead (MaterialBar.tsx): one place, no second bar.
  if (tool.kind === 'select' || tool.kind === 'build') return null;
  const { name, hint, icon } = toolHeadingOf(tool);
  return (
    <div
      className={cn(panel, 'pointer-events-auto flex flex-wrap items-center gap-2 px-2.5 py-1.5 text-xs')}
      data-testid="tool-options"
      role="toolbar"
      aria-label={`${name} options`}
    >
      <ToolIcon id={icon} className="size-4 text-accent" />
      <span className="font-semibold">{name}</span>
      <span className="text-muted">{hint}</span>
      {tool.kind === 'designate' ? (
        <DesignateOptions
          id={tool.id}
          height={{ mode: heightMode, onMode: onHeightMode, layers, onLayers, level }}
          mineFilter={mineFilter}
          onMineFilter={onMineFilter}
          stalled={stalled}
          onRetryAll={onRetryAll}
        />
      ) : null}
      {tool.kind === 'design' ? (
        <span className="flex items-center gap-1" title="Facing (Q / E)">
          <RotateCcw className="size-3.5 text-muted" />
          <span className="w-4 text-center font-semibold uppercase" data-testid="facing">
            {build.facing}
          </span>
          <RotateCw className="size-3.5 text-muted" />
        </span>
      ) : null}
      <button
        type="button"
        onClick={onClose}
        className="ml-1 grid size-5 place-items-center rounded hover:bg-surface-2"
        aria-label="Put the tool down (Esc or right-click)"
      >
        <X className="size-3.5" />
      </button>
    </div>
  );
}

/** A designate tool's own options: the height modes (Mine, Deconstruct), the mine filter, "Retry all failed". */
function DesignateOptions({
  id,
  height,
  mineFilter,
  onMineFilter,
  stalled,
  onRetryAll,
}: {
  id: DesignateId;
  height: Parameters<typeof HeightModes>[0];
  mineFilter: string;
  onMineFilter: (f: 'all' | 'ores' | 'stone' | 'soil') => void;
  stalled: number;
  onRetryAll?: () => void;
}) {
  return (
    <>
      {hasHeight(id) ? <HeightModes {...height} /> : null}
      {id === 'mine' ? (
        <select
          aria-label="Mine filter"
          value={mineFilter}
          onChange={(e) => onMineFilter(e.target.value as never)}
          className="h-6 rounded border border-border bg-surface-2 px-1"
        >
          {MINE_FILTERS.map((f) => (
            <option key={f.id} value={f.id}>
              {f.label}
            </option>
          ))}
        </select>
      ) : null}
      {id === 'retry' ? (
        <button
          type="button"
          onClick={onRetryAll}
          disabled={!stalled}
          data-testid="retry-all"
          className="rounded border border-border bg-surface-2 px-2 py-0.5 font-medium hover:bg-surface-3 disabled:opacity-50"
        >
          Retry all failed ({stalled})
        </button>
      ) : null}
    </>
  );
}

// ------------------------------------------------------------------ alerts, toast, menu, help

export function AlertsStack({
  alerts,
  onPick,
  onDismiss,
}: {
  alerts: readonly StoryAlert[];
  onPick: (a: StoryAlert) => void;
  onDismiss: (id: string) => void;
}) {
  const list = [...alerts].reverse().slice(0, 7);
  if (!list.length) return null;
  return (
    <ul className="pointer-events-auto flex w-64 flex-col gap-1" aria-label="Alerts" data-testid="alerts">
      {list.map((a) => {
        const id = String(a.id ?? `${a.at}|${a.text}`);
        const tone = a.level === 'danger' ? 'border-l-danger' : a.level === 'warn' ? 'border-l-warn' : 'border-l-info';
        return (
          <li
            key={id}
            className={cn(
              panel,
              'group flex items-start gap-1.5 border-l-4 px-2 py-1.5 text-[11.5px] leading-snug',
              tone,
            )}
          >
            <button
              type="button"
              onClick={() => onPick(a)}
              className="flex min-w-0 flex-1 items-start gap-1.5 text-left"
            >
              {a.level === 'info' ? (
                <Info className="mt-0.5 size-3.5 shrink-0 text-info" />
              ) : (
                <AlertTriangle
                  className={cn('mt-0.5 size-3.5 shrink-0', a.level === 'danger' ? 'text-danger' : 'text-warn')}
                />
              )}
              <span className="min-w-0">{a.text}</span>
            </button>
            <button
              type="button"
              onClick={() => onDismiss(id)}
              className="opacity-0 group-hover:opacity-100"
              aria-label="Dismiss"
            >
              <X className="size-3 text-muted" />
            </button>
          </li>
        );
      })}
    </ul>
  );
}

export function Toast({ text, tone = 'ok' }: { text: string | null; tone?: 'ok' | 'warn' }) {
  if (!text) return null;
  return (
    <div
      role="status"
      data-testid="story-toast"
      className={cn(panel, 'pointer-events-none px-3 py-1.5 text-xs font-medium', tone === 'warn' && 'text-warn')}
    >
      {text}
    </div>
  );
}

export function ContextMenu({
  at,
  title,
  items,
  onPick,
  onClose,
}: {
  at: { x: number; y: number };
  title: string;
  items: MenuItem[];
  onPick: (item: MenuItem, queue: boolean) => void;
  onClose: () => void;
}) {
  const ref = useRef<HTMLDivElement>(null);
  useEffect(() => {
    const away = (e: PointerEvent) => {
      if (!ref.current?.contains(e.target as Node)) onClose();
    };
    window.addEventListener('pointerdown', away, true);
    return () => window.removeEventListener('pointerdown', away, true);
  }, [onClose]);
  return (
    <div
      ref={ref}
      className={cn(panel, 'pointer-events-auto fixed z-40 min-w-48 py-1 text-[13px]')}
      style={{ left: at.x + 2, top: at.y + 2 }}
      role="menu"
      aria-label={title}
      data-testid="order-menu"
    >
      <div className="px-3 pt-0.5 pb-1 text-[11px] font-semibold tracking-wide text-subtle uppercase">{title}</div>
      {items.length === 0 ? <div className="px-3 py-1 text-muted">Select pawns first</div> : null}
      {items.map((it) => (
        <button
          key={it.id}
          type="button"
          role="menuitem"
          onClick={(e) => onPick(it, e.shiftKey)}
          className="flex w-full items-center gap-2 px-3 py-1 text-left hover:bg-accent-soft"
        >
          <ToolIcon
            id={it.order?.kind === 'attack' ? 'hunt' : (it.order?.kind ?? it.action ?? 'block')}
            className="size-3.5 text-muted"
          />
          {it.label}
        </button>
      ))}
      <div className="border-t border-border px-3 pt-1 text-[10.5px] text-subtle">Shift-click queues</div>
    </div>
  );
}

export function HelpOverlay({ onClose }: { onClose: () => void }) {
  const rows: Array<[ReactNode, string]> = [
    [<Kbd key="1">Left drag</Kbd>, 'Box-select pawns (or chests)'],
    [<Kbd key="2">Shift</Kbd>, 'Add to the selection · queue orders · keep a tool'],
    [<Kbd key="3">Double-click</Kbd>, 'Select every pawn of that kind on screen'],
    [<Kbd key="4">Right-click</Kbd>, 'Orders for the thing under the cursor · put a tool down'],
    [<Kbd key="5">R</Kbd>, 'Draft / undraft the selected pawns'],
    [<Kbd key="6">Space</Kbd>, 'Pause the colony'],
    [<Kbd key="6s">1 / 2 / 3</Kbd>, 'Pawn speed: normal, fast, faster (with no tool in hand)'],
    [<Kbd key="7">WASD · arrows · edge · middle-drag</Kbd>, 'Pan'],
    [<Kbd key="8">Wheel</Kbd>, 'Zoom to the cursor'],
    [
      <Kbd key="9">C G H M L N K Z F J</Kbd>,
      'Chop, cut, harvest, mine, haul, hunt, deconstruct, cancel, forbid, allow',
    ],
    [<Kbd key="10">B P Y U O I</Kbd>, 'Architect, work, schedule, queue, directives, colony'],
    [<Kbd key="11">PgUp / PgDn</Kbd>, 'Change storey (Shift: one block) · X x-ray above it'],
    [<Kbd key="12">Q / E · + / −</Kbd>, 'Turn a door or stairs · wall height'],
    [
      <Kbd key="12m">1–5 · + / −</Kbd>,
      'Mine / deconstruct depth: surface, level, down to level, flatten here, layers (+ / − layers)',
    ],
    [<Kbd key="13">V</Kbd>, 'Top-down ↔ angled view'],
    [<Kbd key="13f">Shift+F</Kbd>, 'Full screen: the map and every panel (Esc leaves)'],
    [<Kbd key="14">T / Enter</Kbd>, 'Chat to the selected AI bodies'],
    [<Kbd key="15">Esc</Kbd>, 'Cancel a drag · close a menu · drop the tool · clear the selection'],
    [<Kbd key="16">Right-click while dragging</Kbd>, 'Cancel the drag (nothing is designated)'],
  ];
  return (
    <div className={cn(panel, 'pointer-events-auto w-[30rem] p-3 text-xs')} role="dialog" aria-label="Controls">
      <div className="mb-2 flex items-center">
        <span className="text-sm font-semibold">Controls</span>
        <button type="button" onClick={onClose} className="ml-auto" aria-label="Close">
          <X className="size-4" />
        </button>
      </div>
      <dl className="grid grid-cols-[auto_1fr] gap-x-3 gap-y-1">
        {rows.map(([k, v], i) => (
          <div key={i} className="contents">
            <dt className="text-right">{k}</dt>
            <dd className="text-muted">{v}</dd>
          </div>
        ))}
      </dl>
    </div>
  );
}
